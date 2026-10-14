using System.Globalization;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 4: "Corrigir texto" em página com /Rotate 90/180/270 (+ CropBox deslocada).
/// O contrato é o FRAME EXIBIDO (o do PDFium). Oráculo principal = INVARIÂNCIA: uma página girada cuja
/// caixa exibida é A4 retrato (MediaBox paisagem em 90/270) e cujo conteúdo foi desenhado EM PÉ na tela é,
/// para o leitor, a mesma página da rotação 0 — o probe tem de devolver os mesmos valores e a correção tem de
/// renderizar igual (texto novo em pé, no mesmo lugar). Mais: texto que fica DEITADO na tela (PDF retrato
/// comum girado com "Girar") é recusado com mensagem amigável. Dados sintéticos.
public class TextCorrectionRotationTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();
    private const uint White = 0xFFFFFFFF;
    private const float Ox = 30, Oy = 50; // origem da CropBox no caso "crop"
    private const string Line1 = "Endereco: RUA ANTIGA 123", Line2 = "Outra linha que nao deve mudar";
    private const double Base1 = 700, Base2 = 660, LineX = 72, Size = 11;

    public static IEnumerable<object[]> Rotacoes() =>
        from r in new[] { 0, 90, 180, 270 } from crop in new[] { false, true } select new object[] { r, crop };

    public static IEnumerable<object[]> Giradas() =>
        from r in new[] { 90, 180, 270 } from crop in new[] { false, true } select new object[] { r, crop };

    /// Dimensões NÃO-rotacionadas (usuário) para a caixa exibida ser sempre A4 retrato (595 x 842).
    private static (float W, float H) UserDims(int rot) => rot is 90 or 270 ? (842f, 595f) : (595f, 842f);

    /// Página `rot` (com CropBox deslocada se `crop`) com uma imagem sólida cobrindo tudo e duas linhas Times
    /// desenhadas EM PÉ na tela (Tm de `DisplayFrame.TextMatrix`) nas posições EXIBIDAS (72, 700) e (72, 660).
    internal static byte[] UprightPage(int rot, bool crop)
    {
        var (w, h) = UserDims(rot);
        float ox = crop ? Ox : 0, oy = crop ? Oy : 0;
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(w + 2 * ox, h + 2 * oy));
            if (crop) page.SetCropBox(new Rectangle(ox, oy, w, h));
            if (rot != 0) page.SetRotation(rot);
            var frame = PageFrame.Frame(page);
            var canvas = new PdfCanvas(page);
            // Imagem de fundo (o pdfSweep pinta a área NELA) também em pé, cobrindo a caixa exibida: quadrado
            // unitário -> exibido (DW x DH) -> usuário (parte linear = ContentMatrix, origem = ToUser(0,0)).
            var (ca, cb, cc, cd) = frame.ContentMatrix;
            var o = frame.ToUser(new PdfPoint(0, 0));
            double dw = frame.DisplayedWidth, dh = frame.DisplayedHeight;
            canvas.AddXObjectWithTransformationMatrix(new iText.Kernel.Pdf.Xobject.PdfImageXObject(ImageDataFactory.Create(Fixtures.PaginaSolida())),
                (float)(dw * ca), (float)(dw * cb), (float)(dh * cc), (float)(dh * cd), (float)o.XPt, (float)o.YPt);
            var times = PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN);
            foreach (var (text, y) in new[] { (Line1, Base1), (Line2, Base2) })
            {
                var m = frame.TextMatrix(new PdfPoint(LineX, y));
                canvas.BeginText().SetFontAndSize(times, (float)Size)
                      .SetTextMatrix((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F)
                      .ShowText(text).EndText();
            }
        }
        return output.ToArray();
    }

    /// União das caixas de caractere do PDFium da palavra (frame exibido — como o App monta a seleção).
    internal static PdfQuad PdfiumWordArea(byte[] pdf, string word)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var tp = r.GetTextPage(0);
        int i = tp.Text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{word}' não está no texto do PDFium: {tp.Text}");
        var cs = tp.Characters.Skip(i).Take(word.Length).ToList();
        return new PdfQuad(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt), cs.Max(c => c.RightPt), cs.Max(c => c.TopPt));
    }

    private static List<PdfCharacter> PdfiumChars(byte[] pdf, string word)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var tp = r.GetTextPage(0);
        int i = tp.Text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{word}' não está no texto do PDFium: {tp.Text}");
        return tp.Characters.Skip(i).Take(word.Length).ToList();
    }

    private static string PdfiumText(byte[] pdf)
    {
        using var r = new PdfDocumentRenderer(pdf);
        return r.GetTextPage(0).Text;
    }

    private static string ITextText(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return PdfTextExtractor.GetTextFromPage(doc.GetPage(1));
    }

    private static readonly System.Text.RegularExpressions.Regex Numero = new(@"(?<=[|\[ ,])-?\d+(\.\d+)?(?=[\] ,|]|$)");

    /// Mesmos campos (texto, família, token, nº de caixas vizinhas...) e mesmos números com tolerância de 0,02 pt
    /// — o probe em página girada passa pelo `DisplayFrame` (ponto flutuante diferente da rotação 0).
    private static void AssertSameProbe(TextStyleProbe? esperado, TextStyleProbe? atual)
    {
        string e = TextCorrectionRotationPinTests.Describe(esperado), a = TextCorrectionRotationPinTests.Describe(atual);
        Assert.True(Numero.Replace(e, "#") == Numero.Replace(a, "#"), $"campos diferentes:\n esperado {e}\n atual    {a}");
        var ne = Numero.Matches(e).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        var na = Numero.Matches(a).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        for (int k = 0; k < ne.Count; k++)
            Assert.True(Math.Abs(ne[k] - na[k]) <= 0.02, $"número {k} difere: {ne[k]} x {na[k]}\n esperado {e}\n atual    {a}");
    }

    private static void AssertUprightLikeControl(byte[] after, byte[] control, string word)
    {
        // Em pé: as caixas do PDFium avançam para a direita (+X exibido) e batem com as da rotação 0.
        var cs = PdfiumChars(after, word);
        var c0 = PdfiumChars(control, word);
        for (int k = 1; k < cs.Count; k++) Assert.True(cs[k].LeftPt > cs[k - 1].LeftPt + 1, $"'{word}' não avança para a direita: {string.Join(" ", cs.Select(c => c.LeftPt))}");
        for (int k = 0; k < cs.Count; k++)
        {
            Assert.InRange(cs[k].LeftPt, c0[k].LeftPt - 0.6, c0[k].LeftPt + 0.6);
            Assert.InRange(cs[k].BottomPt, c0[k].BottomPt - 0.6, c0[k].BottomPt + 0.6);
            Assert.InRange(cs[k].TopPt, c0[k].TopPt - 0.6, c0[k].TopPt + 0.6);
        }
    }

    /// Pixels (render 1:1 em pontos) cuja luminância difere mais de 64 níveis.
    internal static int DiffPixels(RenderedPage a, RenderedPage b, Func<int, int, bool>? conta = null)
    {
        Assert.Equal(a.WidthPx, b.WidthPx);
        Assert.Equal(a.HeightPx, b.HeightPx);
        int n = 0;
        for (int y = 0; y < a.HeightPx; y++)
        for (int x = 0; x < a.WidthPx; x++)
        {
            if (conta is not null && !conta(x, y)) continue;
            int i = (y * a.WidthPx + x) * 4;
            double la = 0.114 * a.Bgra[i] + 0.587 * a.Bgra[i + 1] + 0.299 * a.Bgra[i + 2];
            double lb = 0.114 * b.Bgra[i] + 0.587 * b.Bgra[i + 1] + 0.299 * b.Bgra[i + 2];
            if (Math.Abs(la - lb) > 64) n++;
        }
        return n;
    }

    // ---- Digital: texto em pé na tela -------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Rotacoes))]
    public void Probe_UprightText_SameAsRotation0(int rot, bool crop)
    {
        var pdf = UprightPage(rot, crop);
        var area0 = PdfiumWordArea(UprightPage(0, crop), "ANTIGA");
        var area = PdfiumWordArea(pdf, "ANTIGA");
        // sanity (Task 1): a seleção do PDFium sai no mesmo lugar exibido em toda rotação
        Assert.InRange(area.LeftPt, area0.LeftPt - 0.6, area0.LeftPt + 0.6);
        Assert.InRange(area.BottomPt, area0.BottomPt - 0.6, area0.BottomPt + 0.6);

        var p = Editor.ProbeTextStyle(pdf, 0, area0);
        var p0 = Editor.ProbeTextStyle(UprightPage(0, crop), 0, area0);

        Assert.NotNull(p);
        AssertSameProbe(p0, p);
        Assert.Equal("ANTIGA", p!.Text.Trim());
        Assert.Equal(Base1, p.BaselineYPt, 2);
        Assert.Equal(Size, p.SizePt, 2);
        Assert.Equal(TextFontFamily.Serif, p.Family);
    }

    [Theory]
    [MemberData(nameof(Rotacoes))]
    public void Apply_UprightText_ReplacesWordUprightAndKeepsTheRest(int rot, bool crop)
    {
        byte[] Corrigir(byte[] pdf)
        {
            var area = PdfiumWordArea(pdf, "ANTIGA");
            var p = Editor.ProbeTextStyle(pdf, 0, area)!;
            return Editor.ApplyTextCorrection(pdf, new TextCorrection(0, area, "NOVA", p.Family, p.Bold, p.Italic,
                p.SizePt, p.ColorArgb, p.BaselineYPt, p.LeftXPt, White));
        }
        var antes = UprightPage(rot, crop);
        var depois = Corrigir(antes);
        var controle = Corrigir(UprightPage(0, crop));

        // PDFium (busca/seleção) e iText (copiar/exportar): palavra antiga sumiu, nova presente, resto intacto.
        string pdfium = PdfiumText(depois), itext = ITextText(depois);
        Assert.DoesNotContain("ANTIGA", pdfium);
        Assert.Contains("NOVA", pdfium);
        Assert.Contains("Endereco: RUA", pdfium);
        Assert.Contains("123", pdfium);
        Assert.Contains(Line2, pdfium);
        Assert.DoesNotContain("ANTIGA", itext);
        Assert.Contains("NOVA", itext);
        Assert.Contains(Line2, itext);
        AssertUprightLikeControl(depois, controle, "NOVA");

        using var rAntes = new PdfDocumentRenderer(antes);
        using var rDepois = new PdfDocumentRenderer(depois);
        using var rControle = new PdfDocumentRenderer(controle);
        var imgDepois = rDepois.RenderPage(0, 1.0);
        // Render: igual ao da rotação 0 corrigida (texto novo em pé, no mesmo lugar; fundo pintado no mesmo lugar).
        int dif = DiffPixels(imgDepois, rControle.RenderPage(0, 1.0));
        Assert.True(dif <= 40, $"render difere do controle (rotação 0) em {dif} px");
        // Fora da área (com 2 pt de folga) nada muda.
        var a = PdfiumWordArea(antes, "ANTIGA");
        double hp = imgDepois.HeightPx;
        bool Fora(int x, int y) => x < a.LeftPt - 2 || x > a.RightPt + 2 || hp - y < a.BottomPt - 2 || hp - y > a.TopPt + 2;
        int fora = DiffPixels(rAntes.RenderPage(0, 1.0), imgDepois, Fora);
        Assert.True(fora == 0, $"{fora} px mudaram fora da área corrigida");
        // Dentro: a tinta mudou (a palavra antiga saiu).
        Assert.True(DiffPixels(rAntes.RenderPage(0, 1.0), imgDepois, (x, y) => !Fora(x, y)) > 20);
    }

    [Theory]
    [MemberData(nameof(Giradas))]
    public void Apply_MultiRectAreas_ConvertedEachToUserSpace(int rot, bool crop)
    {
        // AreasPt (camada OCR, rodada 5): cada retângulo convertido. Duas faixas que cobrem só "ANTIGA" em
        // conjunto: o resultado tem de bater com o da rotação 0.
        byte[] Corrigir(byte[] pdf)
        {
            var a = PdfiumWordArea(pdf, "ANTIGA");
            double mid = (a.BottomPt + a.TopPt) / 2;
            var areas = new[] { new PdfQuad(a.LeftPt, a.BottomPt, a.RightPt, mid), new PdfQuad(a.LeftPt, mid, a.RightPt, a.TopPt) };
            return Editor.ApplyTextCorrection(pdf, new TextCorrection(0, a, "OK", TextFontFamily.SansSerif, false, false,
                Size, 0xFF000000, Base1, a.LeftPt, White, AreasPt: areas));
        }
        var depois = Corrigir(UprightPage(rot, crop));
        var controle = Corrigir(UprightPage(0, crop));
        Assert.DoesNotContain("ANTIGA", PdfiumText(depois));
        Assert.Contains("OK", PdfiumText(depois));
        Assert.Contains(Line2, PdfiumText(depois));
        using var r1 = new PdfDocumentRenderer(depois);
        using var r0 = new PdfDocumentRenderer(controle);
        int dif = DiffPixels(r1.RenderPage(0, 1.0), r0.RenderPage(0, 1.0));
        Assert.True(dif <= 40, $"render difere do controle (rotação 0) em {dif} px");
    }

    // ---- Texto que NÃO está na horizontal na tela: recusa amigável ----------------------------------

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Probe_PageRotatedAfterText_Refused(int rot)
    {
        // PDF retrato comum girado com "Girar": todo o texto fica deitado (90/270) ou de ponta-cabeça (180).
        var pdf = Editor.RotatePages(TextFixtures.TimesOverImage(), new[] { 0 }, rot);
        var area = PdfiumWordArea(pdf, "ANTIGA");

        var ex = Assert.Throws<PdfEditingException>(() => Editor.ProbeTextStyle(pdf, 0, area));
        Assert.Equal("Corrigir texto funciona só em texto na horizontal.", ex.Message);
    }

    [Fact]
    public void Probe_SidewaysTextOnUnrotatedPage_Refused()
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            new PdfCanvas(page).BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 12)
                .SetTextMatrix(0, 1, -1, 0, 300, 300).ShowText("VERTICAL").EndText();
        }
        var pdf = output.ToArray();
        var ex = Assert.Throws<PdfEditingException>(() => Editor.ProbeTextStyle(pdf, 0, PdfiumWordArea(pdf, "VERTICAL")));
        Assert.Equal("Corrigir texto funciona só em texto na horizontal.", ex.Message);
    }

    [Fact]
    public void Probe_OcrLayerThenPageRotated_Refused()
    {
        // Camada OCR gravada com a página em pé e a página girada DEPOIS: os tokens ficam deitados na tela.
        var ocr = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[]
        {
            new OcrTextLayer(0, 800, 1131, new[] { new OcrTextBox("Joaquim", LeftPx: 100, TopPx: 143, WidthPx: 260, HeightPx: 40) }),
        });
        var pdf = Editor.RotatePages(ocr, new[] { 0 }, 90);
        var ex = Assert.Throws<PdfEditingException>(() => Editor.ProbeTextStyle(pdf, 0, PdfiumWordArea(pdf, "Joaquim")));
        Assert.Equal("Corrigir texto funciona só em texto na horizontal.", ex.Message);
    }

    [Fact] // controle: área sem texto nenhum continua devolvendo null (não a recusa)
    public void Probe_EmptyArea_RotatedPage_StillNull()
    {
        Assert.Null(Editor.ProbeTextStyle(UprightPage(90, false), 0, new PdfQuad(300, 100, 400, 150)));
    }

    [Fact] // texto em pé e texto deitado na mesma área: o deitado é ignorado (não recusa, não contamina)
    public void Probe_MixedOrientation_IgnoresSidewaysGlyphs()
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(UprightPage(90, false))), new PdfWriter(output)))
        {
            var page = doc.GetPage(1);
            var frame = PageFrame.Frame(page);
            // "XX" com Tm identidade no usuário = DEITADO na tela (desce em −Y exibido), a partir de (160, 704)
            // exibido: os dois glifos caem dentro da caixa de "ANTIGA" e na faixa da linha (sem o filtro, entrariam no texto).
            var u = frame.ToUser(new PdfPoint(160, 704));
            new PdfCanvas(page).BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 8)
                .SetTextMatrix(1, 0, 0, 1, (float)u.XPt, (float)u.YPt).ShowText("XX").EndText();
        }
        var pdf = output.ToArray();
        var area = PdfiumWordArea(pdf, "ANTIGA");
        var p = Editor.ProbeTextStyle(pdf, 0, area);
        Assert.NotNull(p);
        Assert.Equal("ANTIGA", p!.Text.Trim());
        Assert.Equal(Base1, p.BaselineYPt, 2);
    }

    // ---- Escaneado (camada OCR gravada na página girada) ---------------------------------------------

    /// Página em branco `rot` (caixa exibida A4 retrato) + camada OCR do mPDF (bitmap 800 x 1131 na orientação
    /// EXIBIDA — `ApplyOcrTextLayer` grava em pé desde a Task 1).
    private static byte[] OcrPage(int rot, params OcrTextBox[] boxes)
    {
        var (w, h) = UserDims(rot);
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(w, h));
            if (rot != 0) page.SetRotation(rot);
        }
        return Editor.ApplyOcrTextLayer(output.ToArray(), new[] { new OcrTextLayer(0, 800, 1131, boxes) });
    }

    private const double Sx = 595.0 / 800, Sy = 842.0 / 1131;

    private static readonly OcrTextBox[] Coladas =
    {
        new("gqpjy", LeftPx: 100, TopPx: 100, WidthPx: 200, HeightPx: 40),
        new("Joaquim", LeftPx: 100, TopPx: 143, WidthPx: 260, HeightPx: 40),
        new("Pereira,", LeftPx: 370, TopPx: 143, WidthPx: 200, HeightPx: 40),
        new("lhkbdf", LeftPx: 100, TopPx: 186, WidthPx: 220, HeightPx: 40),
    };
    private static readonly OcrTextBox[] Estreito =
    {
        new("pagamento", LeftPx: 100, TopPx: 100, WidthPx: 160, HeightPx: 40),
        new("jogo", LeftPx: 270, TopPx: 100, WidthPx: 70, HeightPx: 36),
        new("quinta-feira", LeftPx: 350, TopPx: 100, WidthPx: 160, HeightPx: 40),
    };
    private static readonly OcrTextBox[] Acento =
    {
        new("ação", LeftPx: 90, TopPx: 100, WidthPx: 60, HeightPx: 30),
        new("MÚSICA", LeftPx: 100, TopPx: 126, WidthPx: 200, HeightPx: 42),
    };

    public static IEnumerable<object[]> CasosOcr()
    {
        var casos = new (string Nome, PdfQuad Area)[]
        {
            ("coladas-Joaquim", new PdfQuad(100 * Sx, 842 - 183 * Sy - 0.5, 360 * Sx, 842 - 143 * Sy + 0.5)),
            ("coladas-miolo-Pereira", new PdfQuad(370 * Sx + 25, 842 - 183 * Sy - 2, 370 * Sx + 60, 842 - 183 * Sy + 15)),
            ("estreito-jogo", new PdfQuad(270 * Sx, 842 - 136 * Sy - 6, 340 * Sx, 842 - 100 * Sy)),
            ("estreito-duas", new PdfQuad(270 * Sx, 842 - 136 * Sy - 6, 510 * Sx, 842 - 100 * Sy)),
            ("acento-MUSICA", new PdfQuad(100 * Sx, 842 - 168 * Sy, 300 * Sx, 842 - 126 * Sy)),
        };
        foreach (var r in new[] { 90, 180, 270 })
            foreach (var (nome, a) in casos)
                yield return new object[] { r, nome, a.LeftPt, a.BottomPt, a.RightPt, a.TopPt };
    }

    private static OcrTextBox[] BoxesDe(string caso) =>
        caso.StartsWith("coladas") ? Coladas : caso.StartsWith("estreito") ? Estreito : Acento;

    [Theory]
    [MemberData(nameof(CasosOcr))]
    public void Probe_OcrOnRotatedPage_SameAsRotation0(int rot, string caso, double l, double b, double r, double t)
    {
        var area = new PdfQuad(l, b, r, t);
        var p0 = Editor.ProbeTextStyle(OcrPage(0, BoxesDe(caso)), 0, area);
        var p = Editor.ProbeTextStyle(OcrPage(rot, BoxesDe(caso)), 0, area);

        Assert.NotNull(p0);
        Assert.True(p0!.Invisible && p0.TokenText is not null, "sanity: controle é a camada OCR");
        // Token, área do token, próximo token e caixas vizinhas: tudo no frame exibido, igual à rotação 0.
        AssertSameProbe(p0, p);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Apply_OcrOnRotatedPage_RemovesTokenKeepsNeighbours(int rot)
    {
        var box = new PdfQuad(100 * Sx, 842 - 183 * Sy - 0.5, 360 * Sx, 842 - 143 * Sy + 0.5);
        byte[] Corrigir(byte[] pdf)
        {
            var p = Editor.ProbeTextStyle(pdf, 0, box)!;
            var ta = p.TokenAreaPt!.Value;
            var areas = new[] { new PdfQuad(ta.LeftPt, ta.BottomPt, p.NextTokenLeftPt!.Value - 0.5, ta.TopPt) };
            return Editor.ApplyTextCorrection(pdf, new TextCorrection(0, ta, "Antonio", TextFontFamily.SansSerif, false, false,
                p.SizePt * 0.7, 0xFF000000, p.BaselineYPt + 3, p.LeftXPt, White, MinOverlapRatio: 0.4, AreasPt: areas));
        }
        var depois = Corrigir(OcrPage(rot, Coladas));
        var controle = Corrigir(OcrPage(0, Coladas));

        foreach (var texto in new[] { PdfiumText(depois), ITextText(depois) })
        {
            Assert.DoesNotContain("Joaquim", texto);
            Assert.Contains("Antonio", texto);
            Assert.Contains("gqpjy", texto);
            Assert.Contains("lhkbdf", texto);
            Assert.Contains("Pereira,", texto);
        }
        AssertUprightLikeControl(depois, controle, "Antonio");
        using var r1 = new PdfDocumentRenderer(depois);
        using var r0 = new PdfDocumentRenderer(controle);
        int dif = DiffPixels(r1.RenderPage(0, 1.0), r0.RenderPage(0, 1.0));
        Assert.True(dif <= 40, $"render difere do controle (rotação 0) em {dif} px");
    }
}
