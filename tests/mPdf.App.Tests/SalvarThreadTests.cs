using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

file sealed class DialogoSalvarComo(string? saveAsResult) : IFileDialogService
{
    public string? PickPdfToOpen() => null;
    public string? PickPdfToSaveAs(string currentPath) => saveAsResult;
    public string? PickImageToImport() => null;
    public string? PickPdfToSave(string suggestedName) => null;
}

file sealed class FecharCancelar : IConfirmCloseService
{
    public CloseConfirmation Confirm(string documentTitle) => CloseConfirmation.Cancel;
}

/// Bug do usuário (v2.16.0): "Salvar" mostrava "Não foi possível abrir o arquivo: O thread de chamada não pode
/// acessar este objeto porque ele pertence a um thread diferente." `Save`/`SaveAs` rodam
/// `DocumentSession.Save`/`SaveAs` dentro de `Task.Run`; ali `MarkSaved` dispara `DirtyChanged` NA THREAD DO POOL →
/// `DocumentViewModel.IsDirty` PropertyChanged → `MainViewModel.SaveCommand.NotifyCanExecuteChanged()` → o
/// `Button` "Salvar" da MainWindow (um DispatcherObject) reavalia `CanExecute` fora da UI thread e o WPF lança.
/// O arquivo era gravado, mas a exceção voltava pelo `catch` do Save e virava o MessageBox.
///
/// Os testes de VM antigos não pegavam isto: rodam sem Dispatcher nem botão ligado ao comando. Aqui é o setup REAL:
/// thread STA com Dispatcher bombeando, `DispatcherSynchronizationContext` (o contexto do app), a MainWindow real
/// com o DataContext trocado pelo VM de teste (diálogos/notificação injetados — nenhum diálogo do SO).
public class SalvarThreadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-salvar-thread-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void RunSta(Action scenario)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                scenario();
            }
            catch (Exception ex) { threadEx = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "thread STA não terminou em 60s");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    /// Bombeia o Dispatcher até a Task terminar (o await da VM volta pela fila do Dispatcher, como no app).
    private static void Aguardar(Task t)
    {
        var frame = new DispatcherFrame();
        t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        t.GetAwaiter().GetResult();
    }

    private string Copia(string fixture, string nome)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, nome);
        File.Copy(Path.Combine(Fixtures.Root, fixture), p);
        return p;
    }

    private (mPdf.App.MainWindow W, MainViewModel Vm) Janela(List<string> erros, string? saveAsResult = null)
    {
        var vm = new MainViewModel(new DialogoSalvarComo(saveAsResult), new RecentFilesStore(_dir), erros.Add,
            new AppConfig(Path.Combine(_dir, "config")), new FecharCancelar(),
            stampGallery: new StampGallery(Path.Combine(_dir, "carimbos")), notifyInfo: _ => { },
            listSigningCertificates: () => Array.Empty<SigningCertificateInfo>());
        var w = new mPdf.App.MainWindow { Width = 1000, Height = 700, DataContext = vm };
        w.Show();
        w.UpdateLayout();
        return (w, vm);
    }

    private static void Fechar(mPdf.App.MainWindow w, MainViewModel vm)
    {
        foreach (var d in vm.Documents.ToList()) d.Dispose();
        vm.Documents.Clear();
        w.Close();
        try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }

    [Fact] // Salvar (Ctrl+S / botão) de um documento sujo: grava e NÃO notifica erro de thread
    public void Salvar_DocumentoSujo_NaoNotificaErroDeThread()
    {
        var erros = new List<string>();
        var caminho = Copia("fixture-a4.pdf", "doc.pdf");
        RunSta(() =>
        {
            var (w, vm) = Janela(erros);
            try
            {
                Aguardar(vm.OpenPath(caminho));
                var doc = Assert.Single(vm.Documents);
                doc.Session.Apply(Fixtures.ThirtyPages());
                Assert.True(vm.SaveCommand.CanExecute(null));

                Aguardar(vm.SaveCommand.ExecuteAsync(null));

                Assert.Empty(erros);
                Assert.False(doc.IsDirty);
                Assert.False(vm.SaveCommand.CanExecute(null));
                Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(caminho));
            }
            finally { Fechar(w, vm); }
        });
    }

    [Fact] // Salvar como: mesma cadeia (MarkSaved + FilePathChanged) fora da UI thread
    public void SalvarComo_NaoNotificaErroDeThread()
    {
        var erros = new List<string>();
        var caminho = Copia("fixture-a4.pdf", "doc.pdf");
        var destino = Path.Combine(_dir, "destino.pdf");
        RunSta(() =>
        {
            var (w, vm) = Janela(erros, saveAsResult: destino);
            try
            {
                Aguardar(vm.OpenPath(caminho));
                var doc = Assert.Single(vm.Documents);
                doc.Session.Apply(Fixtures.ThirtyPages());

                Aguardar(vm.SaveAsCommand.ExecuteAsync(null));

                Assert.Empty(erros);
                Assert.False(doc.IsDirty);
                Assert.Equal(destino, doc.Session.FilePath);
                Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(destino));
            }
            finally { Fechar(w, vm); }
        });
    }

    [Fact] // O fluxo do relato: PDF assinado → "Editar uma cópia" → edita → Salvar
    public void EditarUmaCopia_DepoisSalvar_NaoNotificaErroDeThread()
    {
        var erros = new List<string>();
        var assinado = Copia("fixture-carimbo.pdf", "Anderson_assinado.pdf");
        RunSta(() =>
        {
            var (w, vm) = Janela(erros);
            try
            {
                Aguardar(vm.OpenPath(assinado));
                Assert.True(vm.SelectedDocument!.IsSignedDocument);
                Aguardar(vm.EditCopyCommand.ExecuteAsync(null));
                Assert.Equal(Array.Empty<string>(), erros);
                Assert.Equal(2, vm.Documents.Count);
                var copia = vm.SelectedDocument!;
                Assert.Contains("(cópia editável)", copia.Session.FilePath);

                copia.Session.Apply(Fixtures.ThirtyPages());
                Aguardar(vm.SaveCommand.ExecuteAsync(null));

                Assert.Empty(erros);
                Assert.False(copia.IsDirty);
                Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(copia.Session.FilePath));
            }
            finally { Fechar(w, vm); }
        });
    }
}
