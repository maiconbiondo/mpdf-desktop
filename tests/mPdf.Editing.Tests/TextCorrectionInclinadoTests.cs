using System.Globalization;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using mPdf.Rendering;
using Xunit.Abstractions;

namespace mPdf.Editing.Tests;

/// Revisão crítica F1, item E7: a guarda "texto na horizontal" do Corrigir texto (|dy| ≤ 0,1·dx ≈ 5,7°) passou a
/// valer também na rotação 0 e recusava texto levemente inclinado que a v2.16 corrigia (camada OCR de scan torto
/// sem deskew, 6–12°). Regra: recusa só o que claramente NÃO é horizontal na tela (> 30°); inclinação leve segue o
/// caminho da v2.16 (valores-ouro abaixo capturados num worktree da v2.16.0 com esta mesma sonda).
public class TextCorrectionInclinadoTests
{
    private readonly ITestOutputHelper _out;
    public TextCorrectionInclinadoTests(ITestOutputHelper o) => _out = o;

    private static IPdfEditor Ed => PdfEditorFactory.Create();
    private const string Linha = "Endereco: RUA ANTIGA 123";

    /// A4 sem /Rotate com a linha em Times 11 pt girada `graus` (anti-horário) a partir de (72, 600).
    private static byte[] Inclinado(double graus)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            double r = graus * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            new PdfCanvas(page).BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN), 11)
                .SetTextMatrix((float)c, (float)s, (float)-s, (float)c, 72, 600).ShowText(Linha).EndText();
        }
        return ms.ToArray();
    }

    /// União das caixas de caractere do PDFium da palavra (como o App monta a seleção).
    private static PdfQuad Area(byte[] pdf, string word)
    {
        using var rr = new PdfDocumentRenderer(pdf);
        var tp = rr.GetTextPage(0);
        int i = tp.Text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{word}' não está no texto do PDFium: {tp.Text}");
        var cs = tp.Characters.Skip(i).Take(word.Length).ToList();
        return new PdfQuad(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt), cs.Max(c => c.RightPt), cs.Max(c => c.TopPt));
    }

    private static string Describe(TextStyleProbe? p)
    {
        if (p is null) return "null";
        string D(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        return $"{p.Text}|{p.Family}|{p.Bold}|{p.Italic}|{D(p.SizePt)}|{p.ColorArgb:X8}|{D(p.BaselineYPt)}|{D(p.LeftXPt)}|{D(p.RightXPt)}|{p.Invisible}";
    }

    private (string Probe, string Depois) Sonda(double graus)
    {
        var pdf = Inclinado(graus);
        var area = Area(pdf, "ANTIGA");
        var p = Ed.ProbeTextStyle(pdf, 0, area);
        string depois = "-";
        if (p is not null)
        {
            var res = Ed.ApplyTextCorrection(pdf, new TextCorrection(0, area, "NOVA", p.Family, p.Bold, p.Italic, p.SizePt,
                p.ColorArgb, p.BaselineYPt, p.LeftXPt, 0xFFFFFFFF));
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(res)));
            depois = PdfTextExtractor.GetTextFromPage(doc.GetPage(1)).Replace("\n", " / ");
        }
        _out.WriteLine($"{graus}°: probe={Describe(p)} depois={depois}");
        return (Describe(p), depois);
    }

    /// Valores-ouro da v2.16.0 (worktree em v2.16.0 rodando esta mesma sonda — sem guarda nenhuma).
    public static IEnumerable<object[]> Leves() => new[]
    {
        new object[] { 3.0, V216_3 }, new object[] { 6.0, V216_6 }, new object[] { 12.0, V216_12 }, new object[] { 25.0, V216_25 },
    };

    private const string
        V216_3 = "ANTIGA|Serif|False|False|11|FF000000|603.84|145.21|187.31|False ## NOVA / Endereco: RUA 123",
        V216_6 = " ANTIGA|Serif|False|False|11|FF000000|607.38|142.18|186.83|False ## NOVA / Endereco: RUA 123",
        V216_12 = " ANTI|Serif|False|False|11|FF000000|614.67|141.02|169.41|False ## NOVA / Endereco: RUA 123",
        V216_25 = " AN|Serif|False|False|11|FF000000|629.82|135.95|152.84|False ## NOVA / Endereco: RU 23";

    [Theory, MemberData(nameof(Leves))]
    public void PaginaNormal_InclinacaoLeve_ComoNaV216(double graus, string esperado)
    {
        var (probe, depois) = Sonda(graus);
        Assert.NotEqual("null", probe);
        Assert.Equal(esperado, probe + " ## " + depois);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(-60.0)]
    [InlineData(180.0)]
    public void PaginaNormal_ClaramenteForaDaHorizontal_Recusa(double graus)
    {
        var pdf = Inclinado(graus);
        var ex = Assert.Throws<PdfEditingException>(() => Ed.ProbeTextStyle(pdf, 0, Area(pdf, "ANTIGA")));
        Assert.Contains("horizontal", ex.Message);
    }
}
