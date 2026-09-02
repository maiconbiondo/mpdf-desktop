using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.App.Views;

namespace mPdf.App.Tests;

file sealed class FakeUpdateSource(LatestRelease? result) : IUpdateSource
{
    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct) => Task.FromResult(result);
}

/// Task 2 (Plano 17) — STA do diálogo "Configurações", MIGRADO de `SobreDialogTests` (Task 2, Plano 11)
/// junto com os controles (Tema/Nitidez/Atualização). Mesma disciplina: construção de `Window` real numa
/// thread STA dedicada; muta `Estado` diretamente no VM real (não um mock da View) e observa o binding
/// refletir.
public class ConfiguracoesDialogTests
{
    private static ConfiguracoesViewModel BuildVm() => new(
        confirmCloseAllDocuments: () => true,
        startInstaller: _ => { },
        shutdown: () => { },
        createSource: () => new FakeUpdateSource(null));

    [Fact]
    public void ConfiguracoesDialog_Constructed_VerificarButtonVisibleInEstadoOcioso()
    {
        RunSta(() =>
        {
            var dialog = new ConfiguracoesDialog(BuildVm());
            try
            {
                dialog.Show();
                dialog.UpdateLayout();

                var button = (Button)dialog.FindName("VerificarButton")!;
                Assert.Equal(Visibility.Visible, button.Visibility);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void ConfiguracoesDialog_EstadoDisponivel_ShowsBaixarButton_HidesVerificarButton()
    {
        RunSta(() =>
        {
            var vm = BuildVm();
            var dialog = new ConfiguracoesDialog(vm);
            try
            {
                dialog.Show();
                vm.Estado = ConfiguracoesEstado.Disponivel;
                vm.AtualizacaoDisponivel = new UpdateInfo("v9.9.9", "notas", "https://x.invalid/x.exe", "x.exe", 1, "0".PadLeft(64, '0'));
                dialog.UpdateLayout();

                var verificar = (Button)dialog.FindName("VerificarButton")!;
                var baixar = (Button)dialog.FindName("BaixarButton")!;
                Assert.Equal(Visibility.Collapsed, verificar.Visibility);
                Assert.Equal(Visibility.Visible, baixar.Visibility);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void ConfiguracoesDialog_EstadoErro_ShowsErrorMessage()
    {
        RunSta(() =>
        {
            var vm = BuildVm();
            var dialog = new ConfiguracoesDialog(vm);
            try
            {
                dialog.Show();
                vm.MensagemErro = "Não foi possível verificar a atualização. Confira a conexão com a internet.";
                vm.Estado = ConfiguracoesEstado.Erro;
                dialog.UpdateLayout();

                var erro = (TextBlock)dialog.FindName("ErroText")!;
                Assert.Equal(Visibility.Visible, erro.Visibility);
                Assert.Equal(vm.MensagemErro, erro.Text);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact] // Task 2 (Plano 17): os 2 toggles migrados do Sobre existem e refletem o estado do VM.
    public void ConfiguracoesDialog_Constructed_TemaENitidezCheckBoxesReflectVm()
    {
        RunSta(() =>
        {
            var vm = BuildVm();
            var dialog = new ConfiguracoesDialog(vm);
            try
            {
                dialog.Show();
                dialog.UpdateLayout();

                var tema = (CheckBox)dialog.FindName("TemaEscuroCheckBox")!;
                var nitidez = (CheckBox)dialog.FindName("NitidezExtraCheckBox")!;
                Assert.Equal(vm.TemaEscuro, tema.IsChecked);
                Assert.Equal(vm.NitidezExtra, nitidez.IsChecked);
            }
            finally { dialog.Close(); }
        });
    }

    private static void RunSta(Action scenario)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { scenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(15));
        Assert.True(joined, "thread STA não terminou dentro de 15s (BLOCKED: possível deadlock/hang do WPF)");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }
}
