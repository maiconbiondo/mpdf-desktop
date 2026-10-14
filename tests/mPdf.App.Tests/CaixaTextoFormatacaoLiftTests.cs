using System.IO;
using System.Linq;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Editing.Tests;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

/// Fix round 2 da revisão final (defeito ANTIGO, já na v2.16): a caixa de texto criada com formatação (tamanho,
/// negrito, itálico, família, cor) perdia tudo no primeiro lift — `ReadAnnotations` não devolvia a formatação, e
/// mover/colar/replicar reescreviam com os defaults (12 pt Helvetica normal). Caminho REAL do App: diálogo
/// formatado → colocar → mover 2× → editar (diálogo pré-preenchido) → copiar/colar → replicar, em /Rotate 0 e 90.
public class CaixaTextoFormatacaoLiftTests
{
    private const uint Vermelho = 0xFFD32F2F; // cor "Vermelho" da paleta do diálogo
    private static readonly AnnotationTextResult Formato = new("Formatado", 14, true, true, "Times", Vermelho);

    private static VmTemporaria Abrir(byte[] pdf) =>
        VmTemporaria.Abrir(pdf, "ft-fmt", annotationDialog: new FakeAnnotationTextDialogService { FormatResult = Formato });

    private static void MesmaFormatacao(AnnotationData a, string ctx)
    {
        Assert.Equal(AnnotationKind.FreeText, a.Kind);
        Assert.True(a.FontSizePt == 14, $"{ctx}: tamanho {a.FontSizePt}");
        Assert.True(a.Bold, $"{ctx}: perdeu o negrito");
        Assert.True(a.Italic, $"{ctx}: perdeu o itálico");
        Assert.Equal("Times", a.FontFamily);
        Assert.Equal(Vermelho, a.ColorArgb);
    }

    /// Altura (pt) da tinta vermelha na página — 14 pt negrito ≠ 12 pt normal.
    private static double AlturaTinta(byte[] pdf, int pagina)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var p = r.RenderPage(pagina, 1.0);
        int top = int.MaxValue, bottom = -1;
        for (int y = 0; y < p.HeightPx; y++)
            for (int x = 0; x < p.WidthPx; x++)
            {
                int i = (y * p.WidthPx + x) * 4;
                if (p.Bgra[i + 2] > 150 && p.Bgra[i + 2] - p.Bgra[i + 1] > 80) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            }
        Assert.True(bottom >= 0, "texto vermelho não renderizado");
        return bottom - top + 1;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task CaixaFormatada_MoverEditarColarReplicar_PreservaAFormatacao(int rot)
    {
        var ed = PdfEditorFactory.Create();
        byte[] dois = ed.ExtractPages(Fixtures.ThirtyPages(), new[] { 0, 1 });
        if (rot != 0) dois = ed.RotatePages(dois, new[] { 0, 1 }, rot);
        using var vm = Abrir(dois);
        var d = vm.D;
        d.ToggleFreeTextToolCommand.Execute(null);
        await d.PlaceAnnotationAtAsync(0, 100, d.Pages[0].HeightPt - 150);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var a0 = Assert.Single(d.AnnotationsByPage[0]);
        MesmaFormatacao(a0, $"rot {rot}: colocada");
        double altura0 = AlturaTinta(d.Session.Snapshot, 0);

        // mover 2×
        for (int k = 1; k <= 2; k++)
        {
            d.SelectedAnnotation = Assert.Single(d.AnnotationsByPage[0]);
            await d.MoveSelectedAnnotationAsync(d.SelectedAnnotation!.LeftPt + 30, d.SelectedAnnotation.BottomPt - 20);
            Assert.Empty(vm.Errors);
            await d.RefreshAnnotationsByPageAsync();
            MesmaFormatacao(Assert.Single(d.AnnotationsByPage[0]), $"rot {rot}: movida {k}×");
            Assert.Equal(altura0, AlturaTinta(d.Session.Snapshot, 0), 1);
        }

        // editar: o diálogo abre com a formatação ATUAL (o fake devolve o que recebeu, só troca o texto)
        vm.Dialog.FormatResult = null;
        vm.Dialog.EchoInitialFormat = "Editado";
        d.SelectedAnnotation = Assert.Single(d.AnnotationsByPage[0]);
        await d.EditSelectedAnnotationCommand.ExecuteAsync(null);
        Assert.Empty(vm.Errors);
        Assert.Equal(Formato with { Text = "Formatado" }, vm.Dialog.LastInitialFormat);
        await d.RefreshAnnotationsByPageAsync();
        var editada = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal("Editado", editada.Content);
        MesmaFormatacao(editada, $"rot {rot}: editada");

        // copiar/colar (mesma página)
        d.SelectedAnnotation = editada;
        d.CopyAnnotationCommand.Execute(null);
        await d.PasteAnnotationCommand.ExecuteAsync(null);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        Assert.Equal(2, d.AnnotationsByPage[0].Count);
        foreach (var a in d.AnnotationsByPage[0]) MesmaFormatacao(a, $"rot {rot}: colada");

        // replicar na próxima página
        d.SelectedAnnotation = d.AnnotationsByPage[0].First(a => a.Id == editada.Id);
        await d.ReplicarProximaPaginaCommand.ExecuteAsync(null);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        MesmaFormatacao(Assert.Single(d.AnnotationsByPage[1]), $"rot {rot}: replicada");
        Assert.Equal(altura0, AlturaTinta(d.Session.Snapshot, 1), 1);
    }

    [Fact] // diálogo real (STA): a edição abre na formatação recebida e a devolve intacta ao confirmar
    public void Dialogo_EdicaoComecaNaFormatacaoAtual()
    {
        Exception? erro = null;
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                var dlg = new mPdf.App.Views.AnnotationTextDialog("Editar", "Formatado", showFormatting: true, Formato);
                Assert.True(dlg.BoldToggle.IsChecked == true && dlg.ItalicToggle.IsChecked == true);
                Assert.Equal("14", dlg.SizeCombo.Text);
                Assert.Equal("Times", (dlg.FamilyCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag);
                try
                {
                    typeof(mPdf.App.Views.AnnotationTextDialog).GetMethod("Ok_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .Invoke(dlg, new object?[] { null, null });
                }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { } // DialogResult sem ShowDialog
                Assert.Equal(Formato, dlg.ResultFormat);
            }
            catch (Exception ex) { erro = ex; }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(25)));
        if (erro is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(erro).Throw();
    }
}
