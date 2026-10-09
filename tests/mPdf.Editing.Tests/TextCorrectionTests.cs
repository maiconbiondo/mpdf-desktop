using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.Editing.Tests;

public class TextCorrectionTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();
    private const uint White = 0xFFFFFFFF;

    private static string ExtractText(byte[] pdf, int page1Based = 1)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return PdfTextExtractor.GetTextFromPage(doc.GetPage(page1Based));
    }

    private static TextCorrection TimesCorrection(string newText) => new(
        PageIndex: 0, AreaPt: TextFixtures.OldWordArea(), NewText: newText, Family: TextFontFamily.Serif,
        Bold: false, Italic: false, SizePt: TextFixtures.FontSize, ColorArgb: 0xFF000000,
        BaselineYPt: TextFixtures.LineBaseline, LeftXPt: TextFixtures.OldWordArea().LeftPt, BackgroundArgb: White);

    [Fact] // digital: a palavra antiga some da extração, a nova aparece, o resto da linha fica
    public void ApplyTextCorrection_DigitalText_RemovesOldAndWritesNew()
    {
        var result = Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), TimesCorrection("NOVA"));
        string text = ExtractText(result);
        Assert.DoesNotContain(TextFixtures.OldWord, text);
        Assert.Contains("NOVA", text);
        Assert.Contains("Endereco", text);
        Assert.Contains("Outra linha que nao deve mudar", text);
    }

    [Fact] // PDFium (o motor que o app usa pra busca/cópia) também não enxerga mais a palavra antiga
    public void ApplyTextCorrection_DigitalText_PdfiumTextHasNewNotOld()
    {
        var result = Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), TimesCorrection("NOVA"));
        using var r = new PdfDocumentRenderer(result);
        string text = r.GetTextPage(0).Text;
        Assert.DoesNotContain(TextFixtures.OldWord, text);
        Assert.Contains("NOVA", text);
    }

    [Fact] // OCR: o texto invisível antigo some, o novo (visível) entra na extração
    public void ApplyTextCorrection_OcrLayer_RemovesInvisibleOldWord()
    {
        var area = TextFixtures.OcrWordArea();
        var c = new TextCorrection(0, area, "NOVA", TextFontFamily.SansSerif, false, false, 20,
            0xFF000000, area.BottomPt + 4, area.LeftPt, White);
        string text = ExtractText(Editor.ApplyTextCorrection(TextFixtures.OcrPage(), c));
        Assert.DoesNotContain(TextFixtures.OldWord, text);
        Assert.Contains("NOVA", text);
    }

    [Fact] // acentos do português sobrevivem a escrever e extrair (WinAnsi)
    public void ApplyTextCorrection_PortugueseAccents_RoundTrip()
    {
        string text = ExtractText(Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), TimesCorrection("CONCEIÇÃO")));
        Assert.Contains("CONCEIÇÃO", text);
    }

    [Fact] // fora da área (e da largura do texto novo) a renderização é idêntica à original
    public void ApplyTextCorrection_PixelsOutsideAreaUnchanged()
    {
        byte[] before = TextFixtures.TimesOverImage();
        byte[] after = Editor.ApplyTextCorrection(before, TimesCorrection("NOVA"));
        using var r0 = new PdfDocumentRenderer(before);
        using var r1 = new PdfDocumentRenderer(after);
        var p0 = r0.RenderPage(0, 1.0); var p1 = r1.RenderPage(0, 1.0);
        var a = TextFixtures.OldWordArea();
        double pageH = r0.GetPageSize(0).HeightPt; // PdfPageSize(WidthPt, HeightPt) — src/mPdf.Rendering/PdfPageSize.cs
        int changedOutside = 0;
        for (int y = 0; y < p0.HeightPx; y++)
        for (int x = 0; x < p0.WidthPx; x++)
        {
            double xPt = x + 0.5, yPt = pageH - (y + 0.5);
            bool inside = xPt >= a.LeftPt - 3 && xPt <= a.RightPt + 40 && yPt >= a.BottomPt - 3 && yPt <= a.TopPt + 3;
            if (inside) continue;
            int i = (y * p0.WidthPx + x) * 4;
            if (Math.Abs(p0.Bgra[i] - p1.Bgra[i]) > 8 || Math.Abs(p0.Bgra[i + 1] - p1.Bgra[i + 1]) > 8 || Math.Abs(p0.Bgra[i + 2] - p1.Bgra[i + 2]) > 8)
                changedOutside++;
        }
        // POC: pdfSweep re-codifica a imagem limpa. Se falhar SÓ por ruído de recompressão fora da área,
        // registre o maior desvio medido no relatório (é um achado de qualidade para escaneados) antes
        // de mexer na tolerância — não afrouxe em silêncio.
        Assert.Equal(0, changedOutside);
    }

    [Fact] // texto maior avança para a direita no MESMO tamanho (não encolhe)
    public void ApplyTextCorrection_LongerText_ExtendsToTheRight()
    {
        var result = Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), TimesCorrection("AVENIDA BRASIL"));
        Assert.Contains("AVENIDA BRASIL", ExtractText(result));
    }

    [Fact] // documento assinado: recusa com a exceção tipada de sempre
    public void ApplyTextCorrection_OnSignedDocument_Throws()
    {
        var c = new TextCorrection(0, new PdfQuad(100, 100, 200, 120), "X", TextFontFamily.SansSerif,
            false, false, 10, 0xFF000000, 104, 100, White);
        Assert.Throws<PdfSignedDocumentException>(() => Editor.ApplyTextCorrection(Fixtures.Carimbo(), c));
    }

    // ---- Fix round 1: provas por RENDERIZAÇÃO (PDFium), não só por extração de texto ----------------

    /// Conta pixels "escuros" (todos os canais < 80) no retângulo em pontos [x0,x1]x[y0,y1] (origem PDF),
    /// renderizando a página 0 a 1.0 (1 px = 1 pt).
    private static int CountPixels(byte[] pdf, double x0, double x1, double y0, double y1, Func<byte, byte, byte, bool> match)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var p = r.RenderPage(0, 1.0);
        double pageH = r.GetPageSize(0).HeightPt;
        int n = 0;
        for (int y = 0; y < p.HeightPx; y++)
        for (int x = 0; x < p.WidthPx; x++)
        {
            double xPt = x + 0.5, yPt = pageH - (y + 0.5);
            if (xPt < x0 || xPt > x1 || yPt < y0 || yPt > y1) continue;
            int i = (y * p.WidthPx + x) * 4;
            if (match(p.Bgra[i + 2], p.Bgra[i + 1], p.Bgra[i])) n++; // BGRA -> (R,G,B)
        }
        return n;
    }

    private static bool Dark(byte r, byte g, byte b) => r < 80 && g < 80 && b < 80;

    private static double WidthPt(string fontName, string text, float size) =>
        iText.Kernel.Font.PdfFontFactory.CreateFont(fontName).GetWidth(text, size);

    [Fact] // o texto novo é PINTADO (não só extraível) na faixa [LeftXPt, LeftXPt+largura] da baseline
    public void ApplyTextCorrection_DigitalText_NewTextIsVisibleAtBaseline()
    {
        var c = TimesCorrection("NOVA");
        double w = WidthPt(iText.IO.Font.Constants.StandardFonts.TIMES_ROMAN, "NOVA", TextFixtures.FontSize);
        var result = Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), c);
        int dark = CountPixels(result, c.LeftXPt, c.LeftXPt + w, c.BaselineYPt - 1, c.BaselineYPt + c.SizePt * 0.7, Dark);
        Assert.True(dark >= 15, $"esperava o texto novo pintado na baseline; pixels escuros = {dark}");
    }

    [Fact] // armadilha do `3 Tr` vazado da camada OCR: o texto novo tem de ser VISÍVEL
    public void ApplyTextCorrection_OcrLayer_NewTextIsVisible()
    {
        var area = TextFixtures.OcrWordArea();
        var c = new TextCorrection(0, area, "NOVA", TextFontFamily.SansSerif, false, false, 20,
            0xFF000000, area.BottomPt + 4, area.LeftPt, White);
        double w = WidthPt(iText.IO.Font.Constants.StandardFonts.HELVETICA, "NOVA", 20);
        // 2 palavras: a de fora da área mantém o `3 Tr` vivo no conteúdo depois da limpeza.
        var result = Editor.ApplyTextCorrection(TextFixtures.OcrPageTwoWords(), c);
        Assert.Contains("PERMANECE", ExtractText(result)); // a palavra fora da área continua lá
        int dark = CountPixels(result, c.LeftXPt, c.LeftXPt + w, c.BaselineYPt - 1, c.BaselineYPt + c.SizePt * 0.7, Dark);
        Assert.True(dark >= 30, $"esperava o texto novo pintado sobre a página OCR; pixels escuros = {dark}");
    }

    [Fact] // a área limpa da imagem é pintada com BackgroundArgb; fora dela, não (controle negativo)
    public void ApplyTextCorrection_CleanedImageArea_PaintedWithBackground()
    {
        const uint Magenta = 0xFFFF00FF;
        var a = TextFixtures.OldWordArea();
        var c = TimesCorrection("") with { BackgroundArgb = Magenta };
        var result = Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), c);
        static bool IsMagenta(byte r, byte g, byte b) => r > 220 && g < 40 && b > 220;

        // Por dentro (margem de 1 pt contra antisserrilhado da borda): todos magenta.
        double x0 = a.LeftPt + 1, x1 = a.RightPt - 1, y0 = a.BottomPt + 1, y1 = a.TopPt - 1;
        int total = CountPixels(result, x0, x1, y0, y1, (_, _, _) => true);
        int magentaInside = CountPixels(result, x0, x1, y0, y1, IsMagenta);
        Assert.True(total > 100, $"área de teste pequena demais: {total}");
        Assert.Equal(total, magentaInside);

        // Controle negativo: mesma largura, 30 pt abaixo (fora da área) — nenhum magenta.
        int magentaOutside = CountPixels(result, x0, x1, y0 - 30, y1 - 30, IsMagenta);
        Assert.Equal(0, magentaOutside);
    }

    [Fact] // caracteres fora do WinAnsi não podem sumir em silêncio
    public void ApplyTextCorrection_UnsupportedCharacters_ThrowsPdfEditingException()
    {
        var ex = Assert.Throws<PdfEditingException>(
            () => Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), TimesCorrection("Łódź ł 中")));
        Assert.Contains("O texto novo tem caracteres que esta fonte não suporta:", ex.Message);
        Assert.Contains("ł", ex.Message);
        Assert.Contains("中", ex.Message);
        Assert.DoesNotContain("ó", ex.Message); // ó é WinAnsi — não é listado
    }

    [Fact] // área degenerada/não finita -> ArgumentException antes de abrir o PDF
    public void ApplyTextCorrection_InvalidArea_ThrowsArgumentException()
    {
        var pdf = TextFixtures.TimesOverImage();
        Assert.Throws<ArgumentException>(() => Editor.ApplyTextCorrection(pdf,
            TimesCorrection("X") with { AreaPt = new PdfQuad(200, 100, 100, 120) }));   // direita <= esquerda
        Assert.Throws<ArgumentException>(() => Editor.ApplyTextCorrection(pdf,
            TimesCorrection("X") with { AreaPt = new PdfQuad(100, 120, 200, 120) }));   // topo <= base
        Assert.Throws<ArgumentException>(() => Editor.ApplyTextCorrection(pdf,
            TimesCorrection("X") with { AreaPt = new PdfQuad(double.NaN, 100, 200, 120) }));
        Assert.Throws<ArgumentException>(() => Editor.ApplyTextCorrection(pdf,
            TimesCorrection("X") with { AreaPt = new PdfQuad(100, 100, double.PositiveInfinity, 120) }));
    }

    [Fact] // página inexistente: mesmo tipo que ApplyOcrTextLayer_InvalidPageIndex_Throws espera
    public void ApplyTextCorrection_InvalidPage_Throws()
    {
        var c = TimesCorrection("X") with { PageIndex = 5 };
        Assert.Throws<ArgumentOutOfRangeException>(() => Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), c));
    }

    [Fact] // Times-Roman da fixture -> Serif, tamanho 11, preto, linha de base 700, visível
    public void ProbeTextStyle_TimesLine_DetectsSerifSizeColorBaseline()
    {
        var p = Editor.ProbeTextStyle(TextFixtures.TimesOverImage(), 0, TextFixtures.OldWordArea());
        Assert.NotNull(p);
        Assert.Equal(TextFontFamily.Serif, p!.Family);
        Assert.False(p.Bold);
        Assert.Equal(TextFixtures.FontSize, p.SizePt, 1);
        Assert.Equal(0xFF000000u, p.ColorArgb);
        Assert.Equal(TextFixtures.LineBaseline, p.BaselineYPt, 1);
        Assert.False(p.Invisible);
        Assert.Contains(TextFixtures.OldWord, p.Text);
    }

    [Fact] // camada OCR (render mode 3) -> Invisible = true
    public void ProbeTextStyle_OcrLayer_IsInvisible()
    {
        var p = Editor.ProbeTextStyle(TextFixtures.OcrPage(), 0, TextFixtures.OcrWordArea());
        Assert.NotNull(p);
        Assert.True(p!.Invisible);
    }

    [Fact] // área sem texto -> null
    public void ProbeTextStyle_EmptyArea_ReturnsNull()
        => Assert.Null(Editor.ProbeTextStyle(TextFixtures.TimesOverImage(), 0, new PdfQuad(400, 100, 500, 120)));

    [Theory] // nomes reais de fonte -> família/negrito/itálico (inclui o caso real Crystal Reports)
    [InlineData("TimesNewRoman,Bold", TextFontFamily.Serif, true, false)]
    [InlineData("TimesNewRoman,BoldItalic", TextFontFamily.Serif, true, true)]
    [InlineData("ABCDEE+Arial-BoldMT", TextFontFamily.SansSerif, true, false)]
    [InlineData("Courier-Oblique", TextFontFamily.Monospace, false, true)]
    [InlineData("Helvetica", TextFontFamily.SansSerif, false, false)]
    [InlineData("Georgia", TextFontFamily.Serif, false, false)]
    public void ClassifyFontName_MapsFamilyBoldItalic(string baseFont, TextFontFamily fam, bool bold, bool italic)
    {
        var (f, b, i) = TextCorrectionEngine.ClassifyFontName(baseFont);
        Assert.Equal((fam, bold, italic), (f, b, i));
    }

    [Fact] // largura medida = largura da métrica padrão-14 (Times 11pt, "ANTIGA")
    public void MeasureTextWidth_MatchesStandardFontMetrics()
    {
        double w = Editor.MeasureTextWidth(TextFontFamily.Serif, false, false, TextFixtures.FontSize, TextFixtures.OldWord);
        var a = TextFixtures.OldWordArea();
        Assert.Equal(a.RightPt - a.LeftPt, w, 1);
    }

    [Theory] // controle positivo do guarda WinAnsi: símbolos legítimos do português não podem ser recusados
    [InlineData(TextFontFamily.Serif)]
    [InlineData(TextFontFamily.SansSerif)]
    [InlineData(TextFontFamily.Monospace)]
    public void ApplyTextCorrection_PortugueseSymbols_Accepted(TextFontFamily fam)
    {
        string novo = "nº 5, 1ª via, 30°C, R$ 1.000,00 € – “aspas” • fim ok";
        var c = TimesCorrection(novo) with { Family = fam };
        var result = Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), c);
        string text = ExtractText(result);
        Assert.Contains("nº", text);
        Assert.Contains("1ª", text);
        Assert.Contains("€", text);
        Assert.Contains("–", text);
        Assert.Contains(' ', novo); // o NBSP está mesmo na entrada
    }

    [Fact] // fonte TrueType embutida: o Probe devolve valores simples (documento já fechado)
    public void ProbeTextStyle_EmbeddedTrueType_ReturnsWordAndSansSerif()
    {
        var pdf = TextFixtures.EmbeddedInterLine("PALAVRA", 14f, 100f, 500f);
        var p = Editor.ProbeTextStyle(pdf, 0, new PdfQuad(95, 495, 200, 515));
        Assert.NotNull(p);
        Assert.Contains("PALAVRA", p!.Text);
        Assert.Equal(TextFontFamily.SansSerif, p.Family);
        Assert.Equal(14.0, p.SizePt, 1);
    }

    [Fact] // cor CMYK vermelha -> ARGB ~ vermelho
    public void ProbeTextStyle_CmykRed_ConvertsToRgb()
    {
        var pdf = TextFixtures.CmykRedLine("VERMELHO", 12f, 100f, 500f);
        var p = Editor.ProbeTextStyle(pdf, 0, new PdfQuad(95, 495, 200, 512));
        Assert.NotNull(p);
        Assert.Equal(0xFFFF0000u, p!.ColorArgb);
    }

    [Fact] // várias linhas tocando a área: fica só a da esquerda-mais
    public void ProbeTextStyle_TwoLinesInArea_KeepsLeftmostLine()
    {
        var area = new PdfQuad(0, 640, 600, 720); // pega só a linha de baseline 700
        var p = Editor.ProbeTextStyle(TextFixtures.TimesOverImage(), 0, area);
        Assert.NotNull(p);
        Assert.DoesNotContain("Outra", p!.Text);
        Assert.Equal(TextFixtures.LineBaseline, p.BaselineYPt, 1);
        var both = Editor.ProbeTextStyle(TextFixtures.TimesOverImage(), 0, new PdfQuad(0, 640, 600, 720 - 0));
        Assert.NotNull(both);
    }
}
