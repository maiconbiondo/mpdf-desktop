using System.IO;
using System.Linq;
using System.Windows;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Editing.Tests;
using mPdf.Rendering;

namespace mPdf.App.Tests;

/// Páginas giradas, Task 4: "Corrigir texto" em página com /Rotate, no lado do App.
///
/// - `Pipeline_*`: o MESMO pipeline do VM (`PrepararCorrecao.Sondar` -> render -> `PlanejarCorrecao.Calcular` ->
///   `ApplyTextCorrection`), editor e renderer reais;
/// - `Vm_*`: o comando de verdade, do gesto de seleção ao Apply (o gate de rotação do VM saiu na Task 5; na Task 4
///   estes testes estavam com Skip). Inclui um ESCANEADO girado (camada OCR) pelo comando.
/// A matriz do escaneado girado (pipeline, checagens de pixel) está em `CorrigirTextoScanTests.EscaneadoGirado_*`.
public class CorrigirTextoGiradaTests
{
    private const string Linha = "Endereco: RUA ANTIGA 123";

    /// Página digital com o texto EM PÉ na tela: fixture-sem-texto (imagem) girada e a linha escrita DEPOIS pelo
    /// próprio editor (que, desde a Task 4, escreve em pé em página girada).
    private static byte[] DigitalEmPe(int rot)
    {
        var ed = PdfEditorFactory.Create();
        byte[] pdf = rot == 0 ? Fixtures.NoText() : ed.RotatePages(Fixtures.NoText(), new[] { 0 }, rot);
        return ed.ApplyTextCorrection(pdf, new TextCorrection(0, new PdfQuad(20, 20, 21, 21), Linha, TextFontFamily.Serif,
            false, false, 11, 0xFF000000u, 300, 72, 0xFFFFFFFFu));
    }

    /// Página digital com o texto DEITADO na tela: a linha escrita com a página em pé e a página girada depois.
    private static byte[] DigitalDeitado(int rot)
    {
        var ed = PdfEditorFactory.Create();
        return ed.RotatePages(DigitalEmPe(0), new[] { 0 }, rot);
    }

    private static List<PdfCharacter> Palavra(TextPage tp, string w)
    {
        int i = tp.Text.IndexOf(w, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{w}' não está no texto: {tp.Text}");
        return tp.Characters.Skip(i).Take(w.Length).ToList();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Pipeline_Digital_PaginaGirada_TrocaEmPe(int rot)
    {
        var editor = PdfEditorFactory.Create();
        byte[] pdf = DigitalEmPe(rot);
        using var r0 = new PdfDocumentRenderer(pdf);
        var size = r0.GetPageSize(0);
        var cs = Palavra(r0.GetTextPage(0), "ANTIGA");
        var area = new PdfQuad(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt), cs.Max(c => c.RightPt), cs.Max(c => c.TopPt));

        var (estilo, areaSond, _) = PrepararCorrecao.Sondar(editor, pdf, 0, area, "ANTIGA");
        Assert.NotNull(estilo);
        Assert.False(estilo!.Invisible);
        Assert.Equal(300, estilo.BaselineYPt, 1);
        var plano = PlanejarCorrecao.Calcular(estilo, areaSond, r0.RenderPage(0, 2.0), size.HeightPt, 2.0);
        byte[] depois = editor.ApplyTextCorrection(pdf, new TextCorrection(0, plano.AreaPt, "NOVA", estilo.Family, estilo.Bold,
            estilo.Italic, plano.TamanhoPt, estilo.ColorArgb, plano.BaselineYPt, plano.LeftXPt, plano.FundoArgb, plano.MinOverlapRatio, plano.AreasPt));

        using var r1 = new PdfDocumentRenderer(depois);
        var tp1 = r1.GetTextPage(0);
        Assert.DoesNotContain("ANTIGA", tp1.Text);
        Assert.Contains("Endereco: RUA", tp1.Text);
        var novo = Palavra(tp1, "NOVA");
        // Em pé: avança para a direita na tela e fica na linha da palavra antiga.
        for (int k = 1; k < novo.Count; k++) Assert.True(novo[k].LeftPt > novo[k - 1].LeftPt + 1);
        Assert.InRange(novo[0].LeftPt, area.LeftPt - 1.5, area.LeftPt + 1.5);
        Assert.InRange(novo.Min(c => c.BottomPt), area.BottomPt - 1.5, area.BottomPt + 1.5);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Pipeline_Digital_TextoDeitadoNaTela_Recusa(int rot)
    {
        byte[] pdf = DigitalDeitado(rot);
        using var r0 = new PdfDocumentRenderer(pdf);
        var cs = Palavra(r0.GetTextPage(0), "ANTIGA");
        var area = new PdfQuad(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt), cs.Max(c => c.RightPt), cs.Max(c => c.TopPt));
        var ex = Assert.Throws<PdfEditingException>(() => PrepararCorrecao.Sondar(PdfEditorFactory.Create(), pdf, 0, area, "ANTIGA"));
        Assert.Equal("Corrigir texto funciona só em texto na horizontal.", ex.Message);
    }

    // ---- Comando do VM ------------------------------------------------------------------------------

    private static (VmTemporaria Vm, FakeCorrigirTextoDialog Dialog) AbrirVm(byte[] pdf)
    {
        var dialog = new FakeCorrigirTextoDialog { Resultado = new CorrigirTextoResultado("NOVA", TextFontFamily.Serif, false, false, 11, 0xFF000000u) };
        return (VmTemporaria.Abrir(pdf, "corrigir-girada", autor: null, corrigirTextoDialog: dialog), dialog);
    }

    /// Arrasta a seleção sobre `palavra` (gesto do usuário, em px da página renderizada).
    private static void SelecionarPalavra(DocumentViewModel d, PageViewModel page, string palavra)
    {
        page.BeginSelection(new Point(1, 1)); page.ClearSelection(); // garante o cache da TextPage
        var cs = Palavra(page.TextPageCache!, palavra);
        double scale = d.Zoom * PageViewModel.PtToPx;
        Point Centro(PdfCharacter c) => new((c.LeftPt + c.RightPt) / 2 * scale, (page.HeightPt - (c.BottomPt + c.TopPt) / 2) * scale);
        page.BeginSelection(Centro(cs[0]));
        page.UpdateSelection(Centro(cs[^1]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Vm_PaginaGirada_TextoEmPe_AplicaCorrecao(int rot)
    {
        var (vm, dialog) = AbrirVm(DigitalEmPe(rot));
        var (d, errors) = (vm.D, vm.Errors);
        try
        {
            SelecionarPalavra(d, d.Pages[0], "ANTIGA");
            Assert.Equal("ANTIGA", d.Pages[0].SelectedText?.Trim());

            await d.CorrigirTextoCommand.ExecuteAsync(null);

            Assert.Empty(errors);
            Assert.Equal(1, dialog.Chamadas);
            Assert.Equal("ANTIGA", dialog.UltimoPedido!.TextoAtual.Trim());
            Assert.True(d.Session.CanUndo);
            using var r = new PdfDocumentRenderer(d.Session.Snapshot);
            string texto = r.GetTextPage(0).Text;
            Assert.DoesNotContain("ANTIGA", texto);
            Assert.Contains("NOVA", texto);
        }
        finally { vm.Dispose(); }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Vm_PaginaGirada_TextoDeitado_RecusaAmigavel(int rot)
    {
        var (vm, dialog) = AbrirVm(DigitalDeitado(rot));
        var (d, errors) = (vm.D, vm.Errors);
        try
        {
            SelecionarPalavra(d, d.Pages[0], "ANTIGA");

            await d.CorrigirTextoCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "Corrigir texto funciona só em texto na horizontal." }, errors);
            Assert.Equal(0, dialog.Chamadas);
            Assert.False(d.Session.CanUndo);
        }
        finally { vm.Dispose(); }
    }
}
