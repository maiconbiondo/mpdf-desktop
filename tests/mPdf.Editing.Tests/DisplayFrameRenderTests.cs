using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using mPdf.Rendering;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 1 (fix round 1): `DisplayFrame` == "o que o PDFium mostra" também para `/Rotate`
/// fora de múltiplo de 90 (o PDFium faz `r = (raw / 90) % 4; if (r < 0) r += 4` — 135 -> 90, −135 -> 270,
/// 45 -> 0, 315 -> 270) e para MediaBox paisagem. Oráculo: render PDFium — tamanho exibido e texto posto via
/// `TextMatrix` saindo EM PÉ (avanço em +X, glifos acima da baseline) exatamente onde foi pedido.
public class DisplayFrameRenderTests
{
    private const string Word = "UPRIGHT";
    private static readonly PdfPoint Baseline = new(60, 200); // exibido; cabe em retrato e paisagem

    /// Página com `/Rotate` gravado CRU (sem a normalização do iText) — na página ou herdado do nó /Pages.
    private static byte[] Page(int rawRotate, PageSize size, bool crop = false, bool inherited = false)
    {
        byte[] blank;
        using (var ms0 = new MemoryStream())
        {
            using (var doc0 = new PdfDocument(new PdfWriter(ms0))) doc0.AddNewPage(size);
            blank = ms0.ToArray();
        }
        // 2ª passada (o nó /Pages só existe depois do 1º save): /Rotate cru na página ou no pai, e o texto.
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(blank)), new PdfWriter(ms)))
        {
            var page = doc.GetPage(1);
            var target = inherited ? page.GetPdfObject().GetAsDictionary(PdfName.Parent) : page.GetPdfObject();
            target.Put(PdfName.Rotate, new PdfNumber(rawRotate));
            if (crop) page.SetCropBox(new Rectangle(30, 40, size.GetWidth() - 80, size.GetHeight() - 100));
            var f = PageFrame.Frame(page);
            var m = f.TextMatrix(Baseline);
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            new PdfCanvas(page).BeginText().SetFontAndSize(font, 24)
                .SetTextMatrix((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F)
                .ShowText(Word).EndText();
        }
        var pdf = ms.ToArray();
        using (var check = new PdfDocument(new PdfReader(new MemoryStream(pdf))))
        {
            var pd = check.GetPage(1).GetPdfObject();
            Assert.Equal(inherited, pd.GetAsNumber(PdfName.Rotate) is null); // sanity: herdado de verdade
        }
        return pdf;
    }

    private static DisplayFrame FrameOf(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return PageFrame.Frame(doc.GetPage(1));
    }

    /// Caixa da tinta (pixels escuros) em pt do frame exibido, y-para-cima.
    private static (double L, double B, double R, double T) InkBox(RenderedPage p)
    {
        int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1;
        for (int y = 0; y < p.HeightPx; y++)
            for (int x = 0; x < p.WidthPx; x++)
            {
                int i = (y * p.WidthPx + x) * 4;
                if (p.Bgra[i] + p.Bgra[i + 1] + p.Bgra[i + 2] < 384)
                { minx = Math.Min(minx, x); miny = Math.Min(miny, y); maxx = Math.Max(maxx, x); maxy = Math.Max(maxy, y); }
            }
        Assert.True(maxx >= 0, "nenhuma tinta renderizada");
        return (minx, p.HeightPx - 1 - maxy, maxx + 1, p.HeightPx - miny);
    }

    /// "Em pé onde pedido": tinta começa na origem pedida e avança para +X (largo e baixo), assentada SOBRE a
    /// baseline (Helvetica 24: maiúsculas ~17 pt acima, nada abaixo); caracteres do PDFium em ordem crescente de X.
    private static void AssertUprightAtBaseline(byte[] pdf, string ctx)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var ink = InkBox(r.RenderPage(0, 1.0));
        ctx += $" ink L={ink.L} B={ink.B} R={ink.R} T={ink.T}";
        Assert.InRange(ink.L, Baseline.XPt, Baseline.XPt + 5);          // começa na origem, cresce para a direita
        Assert.InRange(ink.R, Baseline.XPt + 90, Baseline.XPt + 110);   // largura de "UPRIGHT" @24pt ≈ 100
        Assert.InRange(ink.B, Baseline.YPt - 1.5, Baseline.YPt + 1.5);  // assenta na baseline (sem descendentes)
        Assert.InRange(ink.T, Baseline.YPt + 15, Baseline.YPt + 20);    // altura de maiúscula acima dela
        Assert.True(ink.R - ink.L > 4 * (ink.T - ink.B), "não está horizontal: " + ctx);

        var chars = r.GetTextPage(0).Characters.Where(c => char.IsLetter(c.Char)).ToList();
        Assert.Equal(Word, new string(chars.Select(c => c.Char).ToArray()));
        for (int i = 1; i < chars.Count; i++)
            Assert.True(chars[i].LeftPt > chars[i - 1].LeftPt, "avanço não é +X: " + ctx);
    }

    [Theory] // regra do PDFium (CPDF_Page::GetPageRotation)
    [InlineData(0, 0)] [InlineData(45, 0)] [InlineData(89, 0)] [InlineData(90, 90)] [InlineData(135, 90)]
    [InlineData(180, 180)] [InlineData(270, 270)] [InlineData(315, 270)] [InlineData(360, 0)] [InlineData(450, 90)]
    [InlineData(-45, 0)] [InlineData(-90, 270)] [InlineData(-135, 270)] [InlineData(-180, 180)] [InlineData(-270, 90)]
    public void NormalizeRotation_MatchesPdfium(int raw, int expected)
    {
        Assert.Equal(expected, PageFrame.NormalizeRotation(raw));
        Assert.Equal(expected, new DisplayFrame(raw, 595, 842).Rotation);
    }

    [Theory] // /Rotate fora de múltiplo de 90: tamanho exibido e texto em pé == PDFium
    [InlineData(45, false)] [InlineData(135, false)] [InlineData(-135, false)] [InlineData(315, false)]
    [InlineData(-135, true)] [InlineData(135, true)]
    public void OddRotate_FrameMatchesPdfium(int raw, bool inherited)
    {
        var pdf = Page(raw, PageSize.A4, inherited: inherited);
        var f = FrameOf(pdf);
        Assert.Equal(PageFrame.NormalizeRotation(raw), f.Rotation);
        using (var r = new PdfDocumentRenderer(pdf))
        {
            var size = r.GetPageSize(0);
            Assert.Equal(f.DisplayedWidth, size.WidthPt, 0);
            Assert.Equal(f.DisplayedHeight, size.HeightPt, 0);
        }
        AssertUprightAtBaseline(pdf, $"raw={raw} inherited={inherited}");
    }

    [Theory] // retrato e PAISAGEM × 0/90/180/270 (× CropBox): texto via TextMatrix sai em pé onde pedido
    [InlineData(0, false, false)] [InlineData(90, false, false)] [InlineData(180, false, false)] [InlineData(270, false, false)]
    [InlineData(0, true, false)] [InlineData(90, true, false)] [InlineData(180, true, false)] [InlineData(270, true, false)]
    [InlineData(90, true, true)] [InlineData(270, false, true)]
    public void TextMatrix_RendersUpright_PortraitAndLandscape(int rot, bool landscape, bool crop)
    {
        var size = landscape ? PageSize.A4.Rotate() : PageSize.A4;
        var pdf = Page(rot, size, crop);
        var f = FrameOf(pdf);
        using (var r = new PdfDocumentRenderer(pdf))
        {
            var s = r.GetPageSize(0);
            Assert.Equal(f.DisplayedWidth, s.WidthPt, 0);
            Assert.Equal(f.DisplayedHeight, s.HeightPt, 0);
        }
        AssertUprightAtBaseline(pdf, $"rot={rot} landscape={landscape} crop={crop}");
    }

    [Theory] // paisagem (MediaBox 842×595): tabela da análise com W=842, H=595
    [InlineData(0, 100, 200, 350, 300)]
    [InlineData(90, 542, 100, 642, 350)]
    [InlineData(180, 492, 295, 742, 395)]
    [InlineData(270, 200, 245, 300, 495)]
    public void Landscape_RectToUser_AndRoundTrip(int rot, double l, double b, double r, double t)
    {
        var f = new DisplayFrame(rot, 842, 595, 30, 40);
        Assert.Equal(rot is 90 or 270 ? (595.0, 842.0) : (842.0, 595.0), (f.DisplayedWidth, f.DisplayedHeight));
        var d = new PdfQuad(100, 200, 350, 300);
        var u = f.ToUser(d);
        Assert.Equal(new PdfQuad(l + 30, b + 40, r + 30, t + 40), u);
        Assert.Equal(d, f.ToDisplayed(u));
        var p = new PdfPoint(123.5, 456.25);
        Assert.Equal(p, f.ToDisplayed(f.ToUser(p)));
    }

    [Theory] // `PageFrame.Rotation` (a rotação usada no motor inteiro) concorda com o PDFium
    [InlineData(135, 90)] [InlineData(-135, 270)] [InlineData(45, 0)] [InlineData(315, 270)] [InlineData(-90, 270)]
    public void PageFrameRotation_MatchesPdfium(int raw, int expected)
    {
        Assert.Equal(new[] { expected }, RotacaoDasPaginas.Ler(Page(raw, PageSize.A4)));
    }

    [Theory] // girar parte do que o PDFium MOSTRA: −135 (exibe 270) + 90 -> 0; 135 (exibe 90) + 90 -> 180
    [InlineData(-135, 90, 0)] [InlineData(135, 90, 180)] [InlineData(45, 180, 180)] [InlineData(315, 90, 0)]
    public void RotatePages_StartsFromDisplayedRotation(int raw, int delta, int expected)
    {
        var rotated = PdfEditorFactory.Create().RotatePages(Page(raw, PageSize.A4), new[] { 0 }, delta);
        Assert.Equal(expected, FrameOf(rotated).Rotation);
        using var r = new PdfDocumentRenderer(rotated);
        var s = r.GetPageSize(0);
        Assert.Equal(expected is 90 or 270 ? (842.0, 595.0) : (595.0, 842.0), (s.WidthPt, s.HeightPt));
    }
}
