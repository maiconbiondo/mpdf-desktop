using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using mPdf.Rendering;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 1: as caixas de caractere do PDFium/Docnet (`PdfDocumentRenderer.GetTextPage`, frame
/// EXIBIDO) batem com os glifos do iText (espaço do usuário) levados ao frame exibido por `DisplayFrame` —
/// dois motores independentes concordando nas 4 rotações, com e sem CropBox deslocada. Também prova a
/// normalização (Left≤Right, Bottom≤Top): o Docnet troca os cantos em página girada (sonda:
/// `'U' L=75 R=62` em 90°, B/T trocados em 180/270).
public class DocnetRotationFrameTests
{
    /// Molde da sonda (.superpowers/sdd/rotacao-sonda): "UPRIGHT" desenhado EM PÉ para o leitor (Tm = matriz
    /// de conteúdo do frame) na posição exibida (60, Hd-100); "SIDEWAYS" horizontal no espaço do usuário.
    private static byte[] MakePage(int rot, bool crop)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            page.SetRotation(rot);
            if (crop) page.SetCropBox(new Rectangle(30, 40, 500, 720));
            var f = PageFrame.Frame(page);
            var m = f.TextMatrix(new PdfPoint(60, f.DisplayedHeight - 100));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var cv = new PdfCanvas(page);
            cv.BeginText().SetFontAndSize(font, 24)
              .SetTextMatrix((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F)
              .ShowText("UPRIGHT").EndText();
            var (ox, oy) = PageFrame.DisplayedOrigin(page);
            cv.BeginText().SetFontAndSize(font, 24).SetTextMatrix(1, 0, 0, 1, (float)(ox + 60), (float)(oy + 100))
              .ShowText("SIDEWAYS").EndText();
        }
        return ms.ToArray();
    }

    private sealed class Glyphs : IEventListener
    {
        public readonly List<(string Text, PdfQuad UserBox)> Items = new();
        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo tri) return;
            foreach (var c in tri.GetCharacterRenderInfos())
            {
                var pts = new[] { c.GetAscentLine().GetStartPoint(), c.GetAscentLine().GetEndPoint(),
                                  c.GetDescentLine().GetStartPoint(), c.GetDescentLine().GetEndPoint() };
                Items.Add((c.GetText(), new PdfQuad(pts.Min(p => p.Get(0)), pts.Min(p => p.Get(1)),
                                                    pts.Max(p => p.Get(0)), pts.Max(p => p.Get(1)))));
            }
        }
        public ICollection<EventType> GetSupportedEvents() => new HashSet<EventType> { EventType.RENDER_TEXT };
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public void DocnetCharBoxes_MatchITextGlyphsThroughDisplayFrame(int rot, bool crop)
    {
        var pdf = MakePage(rot, crop);

        // iText: glifos no espaço do usuário -> frame exibido (via DisplayFrame, a transformação sob teste).
        DisplayFrame frame;
        var listener = new Glyphs();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf))))
        {
            frame = PageFrame.Frame(doc.GetPage(1));
            new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(1));
        }
        var expected = listener.Items.Select(g => (g.Text, Box: frame.ToDisplayed(g.UserBox))).ToList();

        // PDFium: caixas de tinta no frame exibido.
        using var renderer = new PdfDocumentRenderer(pdf);
        var size = renderer.GetPageSize(0);
        Assert.Equal(frame.DisplayedWidth, size.WidthPt, 0);
        Assert.Equal(frame.DisplayedHeight, size.HeightPt, 0);
        var chars = renderer.GetTextPage(0).Characters.Where(c => !char.IsWhiteSpace(c.Char) && c.Char != '\0').ToList();

        Assert.Equal("UPRIGHTSIDEWAYS", string.Concat(expected.Select(e => e.Text)));
        Assert.Equal("UPRIGHTSIDEWAYS", new string(chars.Select(c => c.Char).ToArray()));

        const double Tol = 1.5; // Docnet arredonda para inteiro; caixa de tinta ⊂ caixa ascent/descent do iText
        for (int i = 0; i < chars.Count; i++)
        {
            var c = chars[i];
            var e = expected[i].Box;
            string ctx = $"rot={rot} crop={crop} '{c.Char}' pdfium L={c.LeftPt} B={c.BottomPt} R={c.RightPt} T={c.TopPt} | itext->exibido {e}";
            Assert.True(c.LeftPt <= c.RightPt && c.BottomPt <= c.TopPt, "não normalizado: " + ctx);
            Assert.True(c.LeftPt >= e.LeftPt - Tol && c.RightPt <= e.RightPt + Tol, "X fora: " + ctx);
            Assert.True(c.BottomPt >= e.BottomPt - Tol && c.TopPt <= e.TopPt + Tol, "Y fora: " + ctx);
            // a tinta ocupa boa parte da caixa do glifo no eixo do avanço (não é um ponto perdido noutro lugar)
            bool upright = i < "UPRIGHT".Length || frame.Rotation is 0 or 180;
            double inkAlong = upright ? c.RightPt - c.LeftPt : c.TopPt - c.BottomPt;
            double boxAlong = upright ? e.RightPt - e.LeftPt : e.TopPt - e.BottomPt;
            Assert.True(inkAlong >= 0.2 * boxAlong, "tinta pequena demais: " + ctx);
        }
    }
}
