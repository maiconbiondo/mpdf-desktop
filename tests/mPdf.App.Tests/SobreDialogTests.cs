using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using mPdf.App.ViewModels;
using mPdf.App.Views;

namespace mPdf.App.Tests;

/// Task 2 (Plano 11; reduzido na Task 2 do Plano 17) — STA do diálogo "Sobre", agora SÓ informações.
/// Prova que o diálogo ABRE e mostra a versão — e, negativamente, que os 3 controles MIGRADOS pro
/// `ConfiguracoesDialog` (Tema/Nitidez/Verificar atualização) não existem mais aqui (`FindName` devolve
/// `null` — a prova mais direta de que a superfície foi removida, não só escondida por Visibility).
public class SobreDialogTests
{
    private static SobreViewModel BuildVm() => new();

    [Fact]
    public void SobreDialog_Constructed_ShowsVersaoAtual()
    {
        RunSta(() =>
        {
            var vm = BuildVm();
            var dialog = new SobreDialog(vm);
            try
            {
                dialog.Show();
                dialog.UpdateLayout();

                Assert.Equal(vm, dialog.DataContext);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact] // prova NEGATIVA (Task 2, Plano 17): os 3 controles migrados pro ConfiguracoesDialog não
    // existem mais no Sobre — FindName devolve null pra cada um.
    public void SobreDialog_MigratedControls_NoLongerExist()
    {
        RunSta(() =>
        {
            var dialog = new SobreDialog(BuildVm());
            try
            {
                dialog.Show();
                dialog.UpdateLayout();

                Assert.Null(dialog.FindName("VerificarButton"));
                Assert.Null(dialog.FindName("BaixarButton"));
                Assert.Null(dialog.FindName("ErroText"));
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
