using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.Editing.Tests;

/// Frame de coordenadas das ANOTAÇÕES (fix/markup-cropbox, 2026-10-09). O App monta a geometria das
/// anotações a partir das caixas de caractere do PDFium e dos cliques na página exibida — frame relativo
/// à origem da CropBox. O iText grava /Rect, /QuadPoints, /InkList e /L no espaço do usuário. Numa página
/// com CropBox de origem != (0,0) (página recortada no Acrobat/Foxit) a anotação caía deslocada pela
/// origem. Dados sintéticos (mesma página de `TextCorrectionFrameTests`).
public class AnnotationCropBoxFrameTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();
    private const string Line1 = "Endereco: RUA XV DE NOVEMBRO, 2023";
    private const string Line2 = "Responsavel: JOSE MARIA DA SILVA";
    private const float Base1 = 689f, Base2 = 678f;

    /// MediaBox maior, CropBox em (ox, oy, 595, 842). Texto desenhado em (110+ox, base+oy) do espaço do
    /// usuário — no frame EXIBIDO (CropBox) ele aparece em (110, base).
    internal static byte[] CropOffsetPage(float ox, float oy, float extraMediaRight = 0)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(595 + 2 * ox + extraMediaRight, 842 + 2 * oy));
            page.SetCropBox(new Rectangle(ox, oy, 595, 842));
            var times = PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN);
            var canvas = new PdfCanvas(page);
            canvas.BeginText().SetFontAndSize(times, 9).MoveText(110 + ox, Base1 + oy).ShowText(Line1).EndText();
            canvas.BeginText().SetFontAndSize(times, 9).MoveText(110 + ox, Base2 + oy).ShowText(Line2).EndText();
        }
        return output.ToArray();
    }

    /// Área da palavra como o App a obtém (SelectionPointRects): união das caixas do PDFium.
    private static PdfQuad PdfiumWordArea(byte[] pdf, string word)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var tp = r.GetTextPage(0);
        int i = tp.Text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{word}' não está no texto do PDFium: {tp.Text}");
        var cs = tp.Characters.Skip(i).Take(word.Length).ToList();
        return new PdfQuad(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt), cs.Max(c => c.RightPt), cs.Max(c => c.TopPt));
    }

    /// Onde a palavra está no ESPAÇO DO USUÁRIO segundo o iText (glifo a glifo): x do 1º/último glifo e baseline.
    private static (double Left, double Right, double Baseline) ITextWordLocation(byte[] pdf, string word)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var glyphs = new List<(string T, double L, double R, double Y)>();
        new PdfCanvasProcessor(new GlyphListener(glyphs)).ProcessPageContent(doc.GetPage(1));
        string all = string.Concat(glyphs.Select(g => g.T));
        int i = all.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{word}' não está no texto do iText: {all}");
        var w = glyphs.Skip(i).Take(word.Length).ToList();
        return (w.Min(g => g.L), w.Max(g => g.R), w[0].Y);
    }

    private sealed class GlyphListener(List<(string T, double L, double R, double Y)> sink) : IEventListener
    {
        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo tri) return;
            foreach (var g in tri.GetCharacterRenderInfos())
            {
                var b = g.GetBaseline();
                sink.Add((g.GetText(), b.GetStartPoint().Get(0), b.GetEndPoint().Get(0), b.GetStartPoint().Get(1)));
            }
        }
        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
    }

    private static PdfDictionary RawAnnot(byte[] pdf)
    {
        // Lê o dicionário cru; o PdfDocument é descartado, então copia os valores relevantes antes.
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var a = Assert.Single(doc.GetPage(1).GetAnnotations());
        var copy = new PdfDictionary();
        foreach (var key in new[] { PdfName.Rect, PdfName.QuadPoints, PdfName.L })
            if (a.GetPdfObject().GetAsArray(key) is { } arr) copy.Put(key, new PdfArray(arr.ToFloatArray()));
        if (a.GetPdfObject().GetAsArray(PdfName.InkList) is { } ink)
        {
            var inkCopy = new PdfArray();
            for (int i = 0; i < ink.Size(); i++) inkCopy.Add(new PdfArray(ink.GetAsArray(i).ToFloatArray()));
            copy.Put(PdfName.InkList, inkCopy);
        }
        return copy;
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, 11f)]
    [InlineData(30f, 50f)]
    public void AddAnnotation_Highlight_CropBoxOffset_QuadPointsLandOnTheGlyphs(float ox, float oy)
    {
        var pdf = CropOffsetPage(ox, oy);
        var area = PdfiumWordArea(pdf, "NOVEMBRO"); // frame da CropBox, como o App produz
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Highlight, PageIndex = 0, ColorArgb = 0xFFFFFF00,
            LeftPt = area.LeftPt, BottomPt = area.BottomPt, RightPt = area.RightPt, TopPt = area.TopPt,
            Quads = new[] { area },
        };

        var after = Editor.AddAnnotation(pdf, data);

        var word = ITextWordLocation(after, "NOVEMBRO"); // espaço do usuário
        var raw = RawAnnot(after);
        var qp = raw.GetAsArray(PdfName.QuadPoints).ToFloatArray();
        double qL = new[] { qp[0], qp[2], qp[4], qp[6] }.Min(), qR = new[] { qp[0], qp[2], qp[4], qp[6] }.Max();
        double qB = new[] { qp[1], qp[3], qp[5], qp[7] }.Min(), qT = new[] { qp[1], qp[3], qp[5], qp[7] }.Max();
        Assert.InRange(qL, word.Left - 1.5, word.Left + 1.5);
        Assert.InRange(qR, word.Right - 1.5, word.Right + 1.5);
        Assert.InRange(word.Baseline, qB - 1, qT); // a faixa cobre a linha de NOVEMBRO...
        Assert.True(qB > Base2 + oy + 2, $"quad desceu para a linha de baixo: qB={qB}");   // ...e não a de baixo
        var rect = raw.GetAsArray(PdfName.Rect).ToFloatArray();
        Assert.InRange(rect[0], word.Left - 1.5, word.Left + 1.5);
        Assert.InRange(word.Baseline, Math.Min(rect[1], rect[3]) - 1, Math.Max(rect[1], rect[3]));
    }

    [Theory] // o PDFium pinta o realce sobre a palavra (prova visual ponta a ponta, mesmo motor do App)
    [InlineData(0f, 0f)]
    [InlineData(0f, 11f)]
    [InlineData(30f, 50f)]
    public void AddAnnotation_Highlight_CropBoxOffset_PdfiumPaintsOverTheWord(float ox, float oy)
    {
        var pdf = CropOffsetPage(ox, oy);
        var area = PdfiumWordArea(pdf, "NOVEMBRO");
        var after = Editor.AddAnnotation(pdf, new AnnotationData
        {
            Kind = AnnotationKind.Highlight, PageIndex = 0, ColorArgb = 0xFFFFFF00,
            LeftPt = area.LeftPt, BottomPt = area.BottomPt, RightPt = area.RightPt, TopPt = area.TopPt,
            Quads = new[] { area },
        });

        Assert.True(YellowFraction(after, area) > 0.4, $"realce fora da palavra: amarelo={YellowFraction(after, area):P0}");
    }

    /// Fração de pixels "amarelos" (R,G altos, B baixo) dentro da área (frame da CropBox, escala 1).
    internal static double YellowFraction(byte[] pdf, PdfQuad area)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var img = r.RenderPage(0, 1.0);
        int x0 = (int)Math.Ceiling(area.LeftPt), x1 = (int)Math.Floor(area.RightPt);
        int y0 = (int)Math.Ceiling(img.HeightPx - area.TopPt), y1 = (int)Math.Floor(img.HeightPx - area.BottomPt);
        int total = 0, yellow = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = (y * img.WidthPx + x) * 4;
                byte b = img.Bgra[o], g = img.Bgra[o + 1], rr = img.Bgra[o + 2];
                total++;
                if (rr > 180 && g > 180 && b < 120) yellow++;
            }
        return total == 0 ? 0 : (double)yellow / total;
    }

    [Theory] // anotação gravada por OUTRO programa (espaço do usuário) volta no frame da CropBox
    [InlineData(0f, 0f)]
    [InlineData(0f, 11f)]
    [InlineData(30f, 50f)]
    public void ReadAnnotations_CropBoxOffset_ReturnsDisplayedFrame(float ox, float oy)
    {
        var src = CropOffsetPage(ox, oy);
        var area = PdfiumWordArea(src, "NOVEMBRO");
        byte[] pdf;
        using (var o = new MemoryStream())
        {
            using (var d = new PdfDocument(new PdfReader(new MemoryStream(src)), new PdfWriter(o)))
            {
                float l = (float)area.LeftPt + ox, b = (float)area.BottomPt + oy, r = (float)area.RightPt + ox, t = (float)area.TopPt + oy;
                var hl = PdfTextMarkupAnnotation.CreateHighLight(new Rectangle(l, b, r - l, t - b),
                    new float[] { l, t, r, t, l, b, r, b });
                hl.SetName(new PdfString("externa"));
                d.GetPage(1).AddAnnotation(hl);
            }
            pdf = o.ToArray();
        }

        var a = Assert.Single(Editor.ReadAnnotations(pdf));

        Assert.Equal(area.LeftPt, a.LeftPt, 0.01);
        Assert.Equal(area.BottomPt, a.BottomPt, 0.01);
        var q = Assert.Single(a.Quads!);
        Assert.Equal(area.LeftPt, q.LeftPt, 0.01);
        Assert.Equal(area.TopPt, q.TopPt, 0.01);
    }

    public static IEnumerable<object[]> Offsets() => new[] { new object[] { 0f, 0f }, new object[] { 0f, 11f }, new object[] { 30f, 50f } };

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    /// Um AnnotationData de cada kind, no frame da CropBox (o que o App manda).
    private static IEnumerable<AnnotationData> OneOfEachKind() => new[]
    {
        new AnnotationData { Id = "hl", Kind = AnnotationKind.Highlight, PageIndex = 0, LeftPt = 100, BottomPt = 700, RightPt = 200, TopPt = 712,
            Quads = new[] { new PdfQuad(100, 700, 200, 712) }, ColorArgb = 0xFFFFFF00 },
        new AnnotationData { Id = "ul", Kind = AnnotationKind.Underline, PageIndex = 0, LeftPt = 100, BottomPt = 650, RightPt = 200, TopPt = 662,
            Quads = new[] { new PdfQuad(100, 650, 200, 662) }, ColorArgb = 0xFF00FF00 },
        new AnnotationData { Id = "so", Kind = AnnotationKind.Strikeout, PageIndex = 0, LeftPt = 100, BottomPt = 600, RightPt = 200, TopPt = 612,
            Quads = new[] { new PdfQuad(100, 600, 200, 612) }, ColorArgb = 0xFFFF0000 },
        new AnnotationData { Id = "nt", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 300, BottomPt = 700, RightPt = 320, TopPt = 720, Content = "nota" },
        new AnnotationData { Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, LeftPt = 300, BottomPt = 600, RightPt = 450, TopPt = 650, Content = "caixa" },
        new AnnotationData { Id = "rc", Kind = AnnotationKind.Rectangle, PageIndex = 0, LeftPt = 100, BottomPt = 400, RightPt = 250, TopPt = 500, ColorArgb = 0xFFFF0000 },
        new AnnotationData { Id = "ln", Kind = AnnotationKind.Line, PageIndex = 0, LeftPt = 100, BottomPt = 300, RightPt = 250, TopPt = 350, ColorArgb = 0xFF0000FF,
            LineStartPt = new PdfPoint(100, 300), LineEndPt = new PdfPoint(250, 350) },
        new AnnotationData { Id = "ar", Kind = AnnotationKind.Arrow, PageIndex = 0, LeftPt = 300, BottomPt = 300, RightPt = 450, TopPt = 350, ColorArgb = 0xFF0000FF,
            LineStartPt = new PdfPoint(300, 350), LineEndPt = new PdfPoint(450, 300) },
        new AnnotationData { Id = "ik", Kind = AnnotationKind.Ink, PageIndex = 0, LeftPt = 100, BottomPt = 200, RightPt = 200, TopPt = 250, ColorArgb = 0xFF000000,
            InkStrokes = new[] { (IReadOnlyList<PdfPoint>)new[] { new PdfPoint(100, 200), new PdfPoint(150, 250), new PdfPoint(200, 210) } } },
        new AnnotationData { Id = "st", Kind = AnnotationKind.ImageStamp, PageIndex = 0, LeftPt = 300, BottomPt = 150, RightPt = 400, TopPt = 250, ImageBytes = TinyPng },
    };

    private static void AssertSameGeometry(AnnotationData expected, AnnotationData actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.LeftPt, actual.LeftPt, 0.01);
        Assert.Equal(expected.RightPt, actual.RightPt, 0.01);
        Assert.Equal(expected.TopPt, actual.TopPt, 0.01);
        if (expected.Kind != AnnotationKind.FreeText) Assert.Equal(expected.BottomPt, actual.BottomPt, 0.01);
        if (expected.Quads is { } eq)
            for (int i = 0; i < eq.Count; i++)
            {
                Assert.Equal(eq[i].LeftPt, actual.Quads![i].LeftPt, 0.01);
                Assert.Equal(eq[i].BottomPt, actual.Quads![i].BottomPt, 0.01);
                Assert.Equal(eq[i].RightPt, actual.Quads![i].RightPt, 0.01);
                Assert.Equal(eq[i].TopPt, actual.Quads![i].TopPt, 0.01);
            }
        if (expected.LineStartPt is { } s)
        {
            Assert.Equal(s.XPt, actual.LineStartPt!.Value.XPt, 0.01); Assert.Equal(s.YPt, actual.LineStartPt!.Value.YPt, 0.01);
            Assert.Equal(expected.LineEndPt!.Value.XPt, actual.LineEndPt!.Value.XPt, 0.01);
            Assert.Equal(expected.LineEndPt!.Value.YPt, actual.LineEndPt!.Value.YPt, 0.01);
        }
        if (expected.InkStrokes is { } ink)
            for (int i = 0; i < ink[0].Count; i++)
            {
                Assert.Equal(ink[0][i].XPt, actual.InkStrokes![0][i].XPt, 0.01);
                Assert.Equal(ink[0][i].YPt, actual.InkStrokes![0][i].YPt, 0.01);
            }
    }

    [Theory] // AddAnnotation -> ReadAnnotations devolve a MESMA geometria (frame da CropBox), todos os kinds
    [MemberData(nameof(Offsets))]
    public void AddThenRead_AllKinds_CropBoxOffset_RoundTripsInDisplayedFrame(float ox, float oy)
    {
        var pdf = CropOffsetPage(ox, oy);
        foreach (var a in OneOfEachKind()) pdf = Editor.AddAnnotation(pdf, a);

        var read = Editor.ReadAnnotations(pdf).ToDictionary(a => a.Id!);

        foreach (var a in OneOfEachKind()) AssertSameGeometry(a, read[a.Id!]);
    }

    [Theory] // todos os kinds ficam no ESPAÇO DO USUÁRIO = frame da CropBox + origem (o que o PDFium pinta)
    [MemberData(nameof(Offsets))]
    public void AddAnnotation_AllKinds_CropBoxOffset_WritesUserSpace(float ox, float oy)
    {
        foreach (var a in OneOfEachKind())
        {
            var raw = RawAnnot(Editor.AddAnnotation(CropOffsetPage(ox, oy), a));
            var rect = raw.GetAsArray(PdfName.Rect).ToFloatArray();
            if (a.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
            {
                // Páginas giradas, Task 2: o /Rect de Line/Arrow ganhou margem para a espessura/ponta do /AP
                // (antes eram invisíveis) — contém o bbox; a geometria exata é o /L (conferido abaixo).
                Assert.True(Math.Min(rect[0], rect[2]) < a.LeftPt + ox && Math.Max(rect[1], rect[3]) > a.TopPt + oy);
                Assert.True(Math.Max(rect[0], rect[2]) > a.RightPt + ox && Math.Min(rect[1], rect[3]) < a.BottomPt + oy);
            }
            else
            {
                Assert.Equal(a.LeftPt + ox, Math.Min(rect[0], rect[2]), 0.01);
                Assert.Equal(a.TopPt + oy, Math.Max(rect[1], rect[3]), 0.01);
            }
            if (raw.GetAsArray(PdfName.QuadPoints) is { } qp)
            {
                var f = qp.ToFloatArray();
                Assert.Equal(a.Quads![0].LeftPt + ox, new[] { f[0], f[2], f[4], f[6] }.Min(), 0.01);
                Assert.Equal(a.Quads![0].BottomPt + oy, new[] { f[1], f[3], f[5], f[7] }.Min(), 0.01);
            }
            if (raw.GetAsArray(PdfName.L) is { } l)
            {
                var f = l.ToFloatArray();
                Assert.Equal(a.LineStartPt!.Value.XPt + ox, f[0], 0.01);
                Assert.Equal(a.LineStartPt!.Value.YPt + oy, f[1], 0.01);
            }
            if (raw.GetAsArray(PdfName.InkList) is { } ink)
            {
                var f = ink.GetAsArray(0).ToFloatArray();
                Assert.Equal(a.InkStrokes![0][0].XPt + ox, f[0], 0.01);
                Assert.Equal(a.InkStrokes![0][0].YPt + oy, f[1], 0.01);
            }
        }
    }

    [Theory] // o "lift" do App (Read -> Remove -> Add com o mesmo Id) não deriva pela origem a cada movimento
    [MemberData(nameof(Offsets))]
    public void Lift_RepeatedReadRemoveAdd_DoesNotDrift(float ox, float oy)
    {
        var pdf = CropOffsetPage(ox, oy);
        foreach (var a in OneOfEachKind()) pdf = Editor.AddAnnotation(pdf, a);

        for (int round = 0; round < 3; round++)
            foreach (var a in Editor.ReadAnnotations(pdf).ToList())
            {
                var original = OneOfEachKind().Single(x => x.Id == a.Id);
                pdf = Editor.RemoveAnnotation(pdf, a.Id!);
                pdf = Editor.AddAnnotation(pdf, a with { ImageBytes = original.ImageBytes });
            }

        var read = Editor.ReadAnnotations(pdf).ToDictionary(a => a.Id!);
        foreach (var a in OneOfEachKind()) AssertSameGeometry(a, read[a.Id!]);
    }

    [Fact] // CropBox maior que a MediaBox: o PDFium recorta a CropBox pela MediaBox (origem = interseção)
    public void AddAnnotation_CropBoxBeyondMediaBox_UsesIntersectionOrigin()
    {
        byte[] pdf;
        using (var output = new MemoryStream())
        {
            using (var doc = new PdfDocument(new PdfWriter(output)))
            {
                var page = doc.AddNewPage(new PageSize(new Rectangle(20, 30, 595, 842))); // MediaBox [20 30 615 872]
                page.SetCropBox(new Rectangle(0, 0, 700, 900));                             // CropBox [0 0 700 900]
                var times = PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN);
                new PdfCanvas(page).BeginText().SetFontAndSize(times, 9).MoveText(130, Base1 + 30).ShowText(Line1).EndText();
            }
            pdf = output.ToArray();
        }
        var area = PdfiumWordArea(pdf, "NOVEMBRO");
        var after = Editor.AddAnnotation(pdf, new AnnotationData
        {
            Kind = AnnotationKind.Highlight, PageIndex = 0, ColorArgb = 0xFFFFFF00,
            LeftPt = area.LeftPt, BottomPt = area.BottomPt, RightPt = area.RightPt, TopPt = area.TopPt,
            Quads = new[] { area },
        });

        var word = ITextWordLocation(after, "NOVEMBRO");
        var qp = RawAnnot(after).GetAsArray(PdfName.QuadPoints).ToFloatArray();
        Assert.InRange(new[] { qp[0], qp[2], qp[4], qp[6] }.Min(), word.Left - 1.5, word.Left + 1.5);
        Assert.True(YellowFraction(after, area) > 0.4);
    }

    [Theory] // WidgetRect (destaque do campo no viewer) também no frame exibido
    [MemberData(nameof(Offsets))]
    public void ReadFormFields_CropBoxOffset_WidgetRectInDisplayedFrame(float ox, float oy)
    {
        byte[] pdf;
        using (var o = new MemoryStream())
        {
            using (var d = new PdfDocument(new PdfReader(new MemoryStream(CropOffsetPage(ox, oy))), new PdfWriter(o)))
            {
                var form = iText.Forms.PdfAcroForm.GetAcroForm(d, true);
                var field = new iText.Forms.Fields.TextFormFieldBuilder(d, "nome")
                    .SetWidgetRectangle(new Rectangle(110 + ox, 600 + oy, 100, 20)).SetPage(1).CreateText();
                form.AddField(field, d.GetPage(1));
            }
            pdf = o.ToArray();
        }

        var f = Assert.Single(Editor.ReadFormFields(pdf));

        var r = f.WidgetRect!.Value;
        Assert.Equal(110, r.LeftPt, 0.01);
        Assert.Equal(600, r.BottomPt, 0.01);
        Assert.Equal(210, r.RightPt, 0.01);
        Assert.Equal(620, r.TopPt, 0.01);
    }

    [Theory] // FreeText que cresce para baixo para de crescer no rodapé da página EXIBIDA (não em y=0 do usuário)
    [MemberData(nameof(Offsets))]
    public void AddAnnotation_FreeTextGrowth_ClampsAtDisplayedBottom(float ox, float oy)
    {
        var pdf = CropOffsetPage(ox, oy);
        var after = Editor.AddAnnotation(pdf, new AnnotationData
        {
            Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, LeftPt = 100, BottomPt = 20, RightPt = 140, TopPt = 40,
            Content = string.Join(" ", Enumerable.Repeat("palavra", 60)),
        });

        var a = Assert.Single(Editor.ReadAnnotations(after));
        Assert.Equal(0, a.BottomPt, 0.01);
        Assert.Equal(40, a.TopPt, 0.01);
    }
}

/// Camada de OCR (ApplyOcrTextLayer) em página escaneada RECORTADA: o bitmap do OCR é a página EXIBIDA
/// (o PDFium rasteriza a CropBox ∩ MediaBox), então a escala px->pt e a origem têm de vir dessa caixa,
/// não da MediaBox. Oráculo: as caixas de caractere do PDFium (o frame que o App usa para selecionar).
public class OcrLayerCropBoxFrameTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    private static byte[] CroppedBlank(float ox, float oy, int rotation)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(595 + 2 * ox + 40, 842 + 2 * oy + 25)); // MediaBox maior que a CropBox
            page.SetCropBox(new Rectangle(ox, oy, 595, 842));
            page.SetRotation(rotation);
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData(0f, 0f, 0)]
    [InlineData(0f, 11f, 0)]
    [InlineData(30f, 50f, 0)]
    [InlineData(30f, 50f, 90)]
    [InlineData(30f, 50f, 180)]
    [InlineData(30f, 50f, 270)]
    public void ApplyOcrTextLayer_CroppedPage_TextLandsWhereTheBitmapShowedIt(float ox, float oy, int rotation)
    {
        var pdf = CroppedBlank(ox, oy, rotation);
        bool swap = rotation is 90 or 270;
        double dispW = swap ? 842 : 595, dispH = swap ? 595 : 842;
        const double Scale = 2.0;
        // Palavra no bitmap (px, topo-esquerda) a 2x: esquerda 100pt, base em y_exibido = dispH - 212pt, 12pt de altura.
        var layer = new OcrTextLayer(0, (int)(dispW * Scale), (int)(dispH * Scale),
            new[] { new OcrTextBox("PALAVRA", 100 * Scale, 200 * Scale, 60 * Scale, 12 * Scale) });

        var after = Editor.ApplyOcrTextLayer(pdf, new[] { layer });

        using var r = new PdfDocumentRenderer(after);
        var size = r.GetPageSize(0);
        Assert.Equal(dispW, size.WidthPt, 0.5);
        var tp = r.GetTextPage(0);
        int i = tp.Text.IndexOf("PALAVRA", StringComparison.Ordinal);
        Assert.True(i >= 0, $"texto do OCR não extraído pelo PDFium: '{tp.Text}'");
        var p = tp.Characters[i];
        // Em página girada o Docnet transforma os 2 cantos da caixa separadamente: Left/Right (e Top/Bottom)
        // podem vir trocados — min/max dá a caixa exibida de verdade.
        Assert.InRange(Math.Min(p.LeftPt, p.RightPt), 100 - 2, 100 + 2);
        Assert.InRange(Math.Min(p.BottomPt, p.TopPt), dispH - 212 - 3, dispH - 212 + 3);
    }
}

/// Auditoria fix/markup-cropbox: a imposição (N-up/livreto) compõe a página-fonte como XObject cujo BBox
/// é a CropBox (`CopyAsFormXObject`) e `MatrizImposicao` já desconta a origem do BBox. Guarda de regressão:
/// numa folha 1:1 a palavra aparece onde aparecia na página EXIBIDA da fonte.
public class ImposicaoCropBoxFrameTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, 11f)]
    [InlineData(30f, 50f)]
    public void ImporEmFolhas_CroppedSource_KeepsDisplayedPosition(float ox, float oy)
    {
        var src = AnnotationCropBoxFrameTests.CropOffsetPage(ox, oy, extraMediaRight: 40);
        var before = WordBox(src, "NOVEMBRO");

        var imposed = Editor.ImporEmFolhas(src, new[] { new ColocacaoFolha(0, 0, 0, 0, 595, 842, 0) }, 595, 842);

        var after = WordBox(imposed, "NOVEMBRO");
        Assert.Equal(before.LeftPt, after.LeftPt, 1.0);
        Assert.Equal(before.BottomPt, after.BottomPt, 1.0);
    }

    private static PdfQuad WordBox(byte[] pdf, string word)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var tp = r.GetTextPage(0);
        int i = tp.Text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{word}' não está no texto: {tp.Text}");
        var cs = tp.Characters.Skip(i).Take(word.Length).ToList();
        return new PdfQuad(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt), cs.Max(c => c.RightPt), cs.Max(c => c.TopPt));
    }
}
