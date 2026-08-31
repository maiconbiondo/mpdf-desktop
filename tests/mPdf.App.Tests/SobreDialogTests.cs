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

/// Task 2 (Plano 11) — STA do diálogo "Sobre" (mesmo padrão manual de `SignDialogTests`/
/// `ViewerIntegrationTests`: construção de `Window` real precisa rodar numa thread STA dedicada nesta
/// suíte). Prova que o diálogo ABRE, o botão "Verificar atualização" está visível no estado inicial
/// (Ocioso), e os OUTROS estados (Disponível/Erro/Atualizado) trocam a visibilidade dos painéis
/// corretos — mutar `Estado` diretamente no VM real (não um mock da View) e observar o binding
/// refletir, mesmo espírito de `BatchSignDialog`/`Views.SignDialog` (a View hospeda um VM real,
/// `DataContext`, e o code-behind não tem lógica de estado nenhuma).
public class SobreDialogTests
{
    private static SobreViewModel BuildVm() => new(
        confirmCloseAllDocuments: () => true,
        startInstaller: _ => { },
        shutdown: () => { },
        createSource: () => new FakeUpdateSource(null));

    [Fact]
    public void SobreDialog_Constructed_VerificarButtonVisibleInEstadoOcioso()
    {
        RunSta(() =>
        {
            var dialog = new SobreDialog(BuildVm());
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
    public void SobreDialog_EstadoDisponivel_ShowsBaixarButton_HidesVerificarButton()
    {
        RunSta(() =>
        {
            var vm = BuildVm();
            var dialog = new SobreDialog(vm);
            try
            {
                dialog.Show();
                vm.Estado = SobreEstado.Disponivel;
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
    public void SobreDialog_EstadoErro_ShowsErrorMessage()
    {
        RunSta(() =>
        {
            var vm = BuildVm();
            var dialog = new SobreDialog(vm);
            try
            {
                dialog.Show();
                vm.MensagemErro = "Não foi possível verificar a atualização. Confira a conexão com a internet.";
                vm.Estado = SobreEstado.Erro;
                dialog.UpdateLayout();

                var erro = (TextBlock)dialog.FindName("ErroText")!;
                Assert.Equal(Visibility.Visible, erro.Visibility);
                Assert.Equal(vm.MensagemErro, erro.Text);
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
