using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Signing;

namespace mPdf.App.Tests;

/// Task 3 (Plano 12, fixes finais) — a revisão que achou o crash de `ExportImageDialog` (ver
/// `ExportImageDialogTests`) apontou que NENHUM diálogo deste app tinha um teste de CONSTRUÇÃO real
/// além de `SignDialogTests`/`SobreDialogTests` — o resto só é exercitado via fake de serviço
/// (`IBatchSignDialogService`/etc.), nunca `new XxxDialog(...)` de verdade. Estes são fumaça BARATA
/// (só prova "não lança durante `InitializeComponent`/`Show`", nenhuma asserção de comportamento) pros
/// dois diálogos que a revisão nomeou como próximos candidatos ao MESMO tipo de armadilha ("evento
/// durante InitializeComponent" -- ver doc XML de `ExportImageDialogTests`): `BatchSignDialog` (mesma
/// estrutura de radios com `IsChecked="True"` disparando `Checked` durante o parse, `BatchSignDialog.
/// xaml.cs`) e `AnnotationTextDialog` (mais simples, sem radios, incluído pela mesma varredura).
public class DialogConstructionSmokeTests
{
    [Fact]
    public void BatchSignDialog_Constructed_DoesNotThrow()
    {
        RunSta(() =>
        {
            var vm = new BatchSignViewModel(
                Array.Empty<SigningCertificateInfo>(),
                isPathOpen: _ => false,
                pickFiles: () => null);
            var dialog = new BatchSignDialog(vm);
            try { dialog.Show(); }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void AnnotationTextDialog_Constructed_DoesNotThrow()
    {
        RunSta(() =>
        {
            var dialog = new AnnotationTextDialog("Nota", initialText: null);
            try { dialog.Show(); }
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
