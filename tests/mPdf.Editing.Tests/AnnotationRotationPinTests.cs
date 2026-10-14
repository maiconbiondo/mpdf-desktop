using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using iText.Kernel.Pdf;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 2 — PIN TESTS (valores-ouro) da escrita de anotações em página NÃO girada, capturados
/// no código ANTIGO (antes de AddAnnotation/ReadAnnotations passarem a usar `DisplayFrame`). A rotação 0 tem de
/// continuar BYTE A BYTE igual: /Rect, /QuadPoints, /InkList, /L, /Border, /IC, /C e o conteúdo (+BBox/Matrix)
/// da appearance stream /AP /N. Exceção deliberada e documentada: Line/Arrow (ganham /AP próprio — eram
/// invisíveis no PDFium; ver `AnnotationRotationRenderTests.Line_*`/`Arrow_*`) — deles só /L é fixado aqui.
public class AnnotationRotationPinTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    internal static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    internal static IEnumerable<AnnotationData> Kinds() => new[]
    {
        new AnnotationData { Id = "hl", Kind = AnnotationKind.Highlight, PageIndex = 0, LeftPt = 100, BottomPt = 700, RightPt = 200.5, TopPt = 712.25,
            Quads = new[] { new PdfQuad(100, 700, 200.5, 712.25), new PdfQuad(100, 686, 160, 698) }, ColorArgb = 0xFFFFFF00 },
        new AnnotationData { Id = "ul", Kind = AnnotationKind.Underline, PageIndex = 0, LeftPt = 100, BottomPt = 650, RightPt = 200, TopPt = 662,
            Quads = new[] { new PdfQuad(100, 650, 200, 662) }, ColorArgb = 0xFF00FF00 },
        new AnnotationData { Id = "so", Kind = AnnotationKind.Strikeout, PageIndex = 0, LeftPt = 100, BottomPt = 600, RightPt = 200, TopPt = 612,
            Quads = new[] { new PdfQuad(100, 600, 200, 612) }, ColorArgb = 0xFFFF0000 },
        new AnnotationData { Id = "nt", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 300, BottomPt = 700, RightPt = 320, TopPt = 720, Content = "nota", ColorArgb = 0xFFFFD000 },
        new AnnotationData { Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, LeftPt = 300, BottomPt = 600, RightPt = 450, TopPt = 650,
            Content = "caixa de texto com bastante conteudo para crescer a altura da caixa", FontSizePt = 14, Bold = true, FontFamily = "Times", ColorArgb = 0xFF0000FF },
        new AnnotationData { Id = "rc", Kind = AnnotationKind.Rectangle, PageIndex = 0, LeftPt = 100, BottomPt = 400, RightPt = 250, TopPt = 500,
            ColorArgb = 0xFFFF0000, FillArgb = 0xFF00FFFF, StrokeWidthPt = 3 },
        new AnnotationData { Id = "ln", Kind = AnnotationKind.Line, PageIndex = 0, LeftPt = 100, BottomPt = 300, RightPt = 250, TopPt = 350, ColorArgb = 0xFF0000FF,
            LineStartPt = new PdfPoint(100, 300), LineEndPt = new PdfPoint(250, 350) },
        new AnnotationData { Id = "ar", Kind = AnnotationKind.Arrow, PageIndex = 0, LeftPt = 300, BottomPt = 300, RightPt = 450, TopPt = 350, ColorArgb = 0xFF0000FF,
            LineStartPt = new PdfPoint(300, 350), LineEndPt = new PdfPoint(450, 300) },
        new AnnotationData { Id = "ik", Kind = AnnotationKind.Ink, PageIndex = 0, LeftPt = 100, BottomPt = 200, RightPt = 200, TopPt = 250, ColorArgb = 0xFF000000, StrokeWidthPt = 1.5,
            InkStrokes = new[] { (IReadOnlyList<PdfPoint>)new[] { new PdfPoint(100, 200), new PdfPoint(150, 250), new PdfPoint(200, 210) } } },
        new AnnotationData { Id = "st", Kind = AnnotationKind.ImageStamp, PageIndex = 0, LeftPt = 300, BottomPt = 150, RightPt = 400, TopPt = 250, ImageBytes = TinyPng },
    };

    private static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
    private static string Arr(PdfArray? a) => a is null ? "-" : "[" + string.Join(" ", a.ToFloatArray().Select(F)) + "]";

    /// Descrição canônica do dicionário cru de uma anotação + SHA-256 do conteúdo do /AP /N.
    internal static string Describe(byte[] pdf, string id, bool lineOnly = false)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var a = doc.GetPage(1).GetAnnotations().Single(x => x.GetName()?.ToUnicodeString() == id).GetPdfObject();
        if (lineOnly) return "L=" + Arr(a.GetAsArray(PdfName.L));
        var sb = new StringBuilder();
        sb.Append("Rect=").Append(Arr(a.GetAsArray(PdfName.Rect)));
        sb.Append(";QP=").Append(Arr(a.GetAsArray(PdfName.QuadPoints)));
        sb.Append(";L=").Append(Arr(a.GetAsArray(PdfName.L)));
        var ink = a.GetAsArray(PdfName.InkList);
        sb.Append(";Ink=").Append(ink is null ? "-" : string.Join(",", Enumerable.Range(0, ink.Size()).Select(i => Arr(ink.GetAsArray(i)))));
        sb.Append(";Border=").Append(Arr(a.GetAsArray(PdfName.Border)));
        sb.Append(";C=").Append(Arr(a.GetAsArray(PdfName.C)));
        sb.Append(";IC=").Append(Arr(a.GetAsArray(PdfName.IC)));
        sb.Append(";F=").Append(a.GetAsNumber(PdfName.F)?.IntValue().ToString(CultureInfo.InvariantCulture) ?? "-");
        var n = a.GetAsDictionary(PdfName.AP)?.GetAsStream(PdfName.N);
        if (n is null) sb.Append(";AP=-");
        else
        {
            sb.Append(";BBox=").Append(Arr(n.GetAsArray(PdfName.BBox)));
            sb.Append(";Matrix=").Append(Arr(n.GetAsArray(PdfName.Matrix)));
            sb.Append(";AP=").Append(Convert.ToHexString(SHA256.HashData(n.GetBytes()))[..16]);
        }
        return sb.ToString();
    }

    public static IEnumerable<object[]> Cases() =>
        from off in new[] { (0f, 0f), (30f, 50f) }
        from a in Kinds()
        select new object[] { a.Id!, off.Item1, off.Item2 };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rotation0_AnnotationDictionary_IsUnchanged(string id, float ox, float oy)
    {
        var a = Kinds().Single(k => k.Id == id);
        var pdf = Editor.AddAnnotation(AnnotationCropBoxFrameTests.CropOffsetPage(ox, oy), a);
        bool lineOnly = a.Kind is AnnotationKind.Line or AnnotationKind.Arrow;
        Assert.Equal(Golden[$"{id}@{ox},{oy}"], Describe(pdf, id, lineOnly));
    }

    private static readonly Dictionary<string, string> Golden = new()
    {
        // capturado no código ANTIGO (e719889), 2 execuções idênticas
        ["hl@0,0"] = "Rect=[100 700 200.5 712.25];QP=[100 712.25 200.5 712.25 100 700 200.5 700 100 698 160 698 100 686 160 686];L=-;Ink=-;Border=-;C=[1 1 0];IC=-;F=-;AP=-",
        ["ul@0,0"] = "Rect=[100 650 200 662];QP=[100 662 200 662 100 650 200 650];L=-;Ink=-;Border=-;C=[0 1 0];IC=-;F=-;AP=-",
        ["so@0,0"] = "Rect=[100 600 200 612];QP=[100 612 200 612 100 600 200 600];L=-;Ink=-;Border=-;C=[1 0 0];IC=-;F=-;AP=-",
        ["nt@0,0"] = "Rect=[300 700 320 720];QP=-;L=-;Ink=-;Border=-;C=[1 0.81569 0];IC=-;F=-;AP=-",
        ["ft@0,0"] = "Rect=[300 582.5 450 650];QP=-;L=-;Ink=-;Border=-;C=[0 0 1];IC=-;F=-;BBox=[300 582.5 450 650];Matrix=-;AP=79E6E7F8B0D91A60",
        ["rc@0,0"] = "Rect=[100 400 250 500];QP=-;L=-;Ink=-;Border=[0 0 3];C=[1 0 0];IC=[0 1 1];F=-;AP=-",
        ["ln@0,0"] = "L=[100 300 250 350]",
        ["ar@0,0"] = "L=[300 350 450 300]",
        ["ik@0,0"] = "Rect=[100 200 200 250];QP=-;L=-;Ink=[100 200 150 250 200 210];Border=[0 0 1.5];C=[0 0 0];IC=-;F=-;AP=-",
        ["st@0,0"] = "Rect=[300 150 400 250];QP=-;L=-;Ink=-;Border=-;C=-;IC=-;F=-;BBox=[300 150 400 250];Matrix=-;AP=EA9F4498AB29B5B5",
        ["hl@30,50"] = "Rect=[130 750 230.5 762.25];QP=[130 762.25 230.5 762.25 130 750 230.5 750 130 748 190 748 130 736 190 736];L=-;Ink=-;Border=-;C=[1 1 0];IC=-;F=-;AP=-",
        ["ul@30,50"] = "Rect=[130 700 230 712];QP=[130 712 230 712 130 700 230 700];L=-;Ink=-;Border=-;C=[0 1 0];IC=-;F=-;AP=-",
        ["so@30,50"] = "Rect=[130 650 230 662];QP=[130 662 230 662 130 650 230 650];L=-;Ink=-;Border=-;C=[1 0 0];IC=-;F=-;AP=-",
        ["nt@30,50"] = "Rect=[330 750 350 770];QP=-;L=-;Ink=-;Border=-;C=[1 0.81569 0];IC=-;F=-;AP=-",
        ["ft@30,50"] = "Rect=[330 632.5 480 700];QP=-;L=-;Ink=-;Border=-;C=[0 0 1];IC=-;F=-;BBox=[330 632.5 480 700];Matrix=-;AP=1EFDC6EBE995496B",
        ["rc@30,50"] = "Rect=[130 450 280 550];QP=-;L=-;Ink=-;Border=[0 0 3];C=[1 0 0];IC=[0 1 1];F=-;AP=-",
        ["ln@30,50"] = "L=[130 350 280 400]",
        ["ar@30,50"] = "L=[330 400 480 350]",
        ["ik@30,50"] = "Rect=[130 250 230 300];QP=-;L=-;Ink=[130 250 180 300 230 260];Border=[0 0 1.5];C=[0 0 0];IC=-;F=-;AP=-",
        ["st@30,50"] = "Rect=[330 200 430 300];QP=-;L=-;Ink=-;Border=-;C=-;IC=-;F=-;BBox=[330 200 430 300];Matrix=-;AP=1EACD86B767EF4C9",
    };

    [SkippableFact] // captura dos valores-ouro: só age com PIN_OUT definido (rodado no código antigo); sem ele, PULADO
    public void Capture()
    {
        string? outPath = Environment.GetEnvironmentVariable("PIN_OUT");
        Skip.If(string.IsNullOrEmpty(outPath), "PIN_OUT não definida — captura dos valores-ouro não executada.");
        var sb = new StringBuilder();
        foreach (var c in Cases())
        {
            var (id, ox, oy) = ((string)c[0], (float)c[1], (float)c[2]);
            var a = Kinds().Single(k => k.Id == id);
            var pdf = Editor.AddAnnotation(AnnotationCropBoxFrameTests.CropOffsetPage(ox, oy), a);
            bool lineOnly = a.Kind is AnnotationKind.Line or AnnotationKind.Arrow;
            sb.AppendLine($"        [\"{id}@{ox.ToString(CultureInfo.InvariantCulture)},{oy.ToString(CultureInfo.InvariantCulture)}\"] = \"{Describe(pdf, id, lineOnly)}\",");
        }
        File.WriteAllText(outPath, sb.ToString());
    }
}
