using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 2: geometria do contrato (frame EXIBIDO) em página girada — ida e volta Add→Read,
/// lift (Read→Remove→Add repetido) sem deriva, /QuadPoints por vértice, crescimento do FreeText no frame
/// exibido e leitura de anotações gravadas por OUTRO programa (só converte a geometria).
public class AnnotationRotationGeometryTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();
    private const double Tol = 0.01;

    public static IEnumerable<object[]> Frames() => AnnotationRotationRenderTests.Frames();

    private static AnnotationData Original(string id) => AnnotationRotationRenderTests.Kind(id);

    private static void AssertSameGeometry(AnnotationData e, AnnotationData a, string ctx)
    {
        ctx += $" [{e.Id}]";
        Assert.Equal(e.Kind, a.Kind);
        void Eq(double x, double y, string w) => Assert.True(Math.Abs(x - y) <= Tol, $"{ctx} {w}: esperado {x}, obtido {y}");
        Eq(e.LeftPt, a.LeftPt, "L"); Eq(e.RightPt, a.RightPt, "R"); Eq(e.TopPt, a.TopPt, "T"); Eq(e.BottomPt, a.BottomPt, "B");
        if (e.Quads is { } eq)
        {
            Assert.Equal(eq.Count, a.Quads!.Count);
            for (int i = 0; i < eq.Count; i++)
            {
                Eq(eq[i].LeftPt, a.Quads[i].LeftPt, $"q{i}.L"); Eq(eq[i].BottomPt, a.Quads[i].BottomPt, $"q{i}.B");
                Eq(eq[i].RightPt, a.Quads[i].RightPt, $"q{i}.R"); Eq(eq[i].TopPt, a.Quads[i].TopPt, $"q{i}.T");
            }
        }
        if (e.LineStartPt is { } s)
        {
            Eq(s.XPt, a.LineStartPt!.Value.XPt, "L0.x"); Eq(s.YPt, a.LineStartPt!.Value.YPt, "L0.y");
            Eq(e.LineEndPt!.Value.XPt, a.LineEndPt!.Value.XPt, "L1.x"); Eq(e.LineEndPt!.Value.YPt, a.LineEndPt!.Value.YPt, "L1.y");
        }
        if (e.InkStrokes is { } ink)
            for (int i = 0; i < ink[0].Count; i++)
            {
                Eq(ink[0][i].XPt, a.InkStrokes![0][i].XPt, $"ink{i}.x"); Eq(ink[0][i].YPt, a.InkStrokes![0][i].YPt, $"ink{i}.y");
            }
        Assert.Equal(e.ColorArgb, a.ColorArgb);
        Assert.Equal(e.FillArgb, a.FillArgb);
        Assert.Equal(e.Content, a.Content);
    }

    [Theory, MemberData(nameof(Frames))] // Add → Read devolve exatamente a geometria EXIBIDA pedida, todos os kinds
    public void AddThenRead_AllKinds_ReturnsDisplayedGeometry(int rot, bool crop)
    {
        var pdf = AnnotationRotationRenderTests.Page(rot, crop);
        foreach (var a in AnnotationRotationRenderTests.Kinds()) pdf = Editor.AddAnnotation(pdf, a);

        var read = Editor.ReadAnnotations(pdf).ToDictionary(a => a.Id!);

        Assert.Equal(10, read.Count);
        foreach (var a in AnnotationRotationRenderTests.Kinds()) AssertSameGeometry(a, read[a.Id!], $"rot={rot} crop={crop}");
    }

    [Theory, MemberData(nameof(Frames))] // lift do App (Read → Remove → Add com o mesmo Id) 3×: sem deriva
    public void Lift_RepeatedReadRemoveAdd_DoesNotDrift(int rot, bool crop)
    {
        var pdf = AnnotationRotationRenderTests.Page(rot, crop);
        foreach (var a in AnnotationRotationRenderTests.Kinds()) pdf = Editor.AddAnnotation(pdf, a);

        // conferido a CADA rodada: sem isso uma leitura errada pode "voltar" sozinha (R⁴ = identidade)
        for (int round = 0; round < 3; round++)
        {
            foreach (var a in Editor.ReadAnnotations(pdf).ToList())
            {
                pdf = Editor.RemoveAnnotation(pdf, a.Id!);
                pdf = Editor.AddAnnotation(pdf, a with { ImageBytes = Original(a.Id!).ImageBytes });
            }
            var read = Editor.ReadAnnotations(pdf).ToDictionary(a => a.Id!);
            foreach (var a in AnnotationRotationRenderTests.Kinds()) AssertSameGeometry(a, read[a.Id!], $"rot={rot} crop={crop} rodada={round}");
        }
    }

    [Theory, MemberData(nameof(Frames))] // mover (deslocar no frame exibido) 2×: chega exatamente onde foi mandado
    public void Move_Twice_LandsExactlyWhereSent(int rot, bool crop)
    {
        var pdf = AnnotationRotationRenderTests.Page(rot, crop);
        foreach (var a in AnnotationRotationRenderTests.Kinds()) pdf = Editor.AddAnnotation(pdf, a);

        foreach (var (dx, dy) in new[] { (12.5, -7.25), (-12.5, 7.25) })
            foreach (var a in Editor.ReadAnnotations(pdf).ToList())
            {
                pdf = Editor.RemoveAnnotation(pdf, a.Id!);
                pdf = Editor.AddAnnotation(pdf, Shift(a, dx, dy) with { ImageBytes = Original(a.Id!).ImageBytes });
                var moved = Editor.ReadAnnotations(pdf).Single(x => x.Id == a.Id);
                AssertSameGeometry(Shift(a, dx, dy), moved, $"rot={rot} crop={crop} move=({dx},{dy})");
            }

        var read = Editor.ReadAnnotations(pdf).ToDictionary(a => a.Id!);
        foreach (var a in AnnotationRotationRenderTests.Kinds()) AssertSameGeometry(a, read[a.Id!], $"rot={rot} crop={crop} final");
    }

    private static AnnotationData Shift(AnnotationData a, double dx, double dy)
    {
        PdfPoint P(PdfPoint p) => new(p.XPt + dx, p.YPt + dy);
        return a with
        {
            LeftPt = a.LeftPt + dx, RightPt = a.RightPt + dx, BottomPt = a.BottomPt + dy, TopPt = a.TopPt + dy,
            Quads = a.Quads?.Select(q => new PdfQuad(q.LeftPt + dx, q.BottomPt + dy, q.RightPt + dx, q.TopPt + dy)).ToList(),
            InkStrokes = a.InkStrokes?.Select(s => (IReadOnlyList<PdfPoint>)s.Select(P).ToList()).ToList(),
            LineStartPt = a.LineStartPt is { } s0 ? P(s0) : null,
            LineEndPt = a.LineEndPt is { } e0 ? P(e0) : null,
        };
    }

    private static DisplayFrame FrameOf(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return PageFrame.Frame(doc.GetPage(1));
    }

    private static PdfDictionary Raw(byte[] pdf, string id)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var a = doc.GetPage(1).GetAnnotations().Single(x => x.GetName()?.ToUnicodeString() == id).GetPdfObject();
        var copy = new PdfDictionary();
        foreach (var key in new[] { PdfName.Rect, PdfName.QuadPoints, PdfName.L })
            if (a.GetAsArray(key) is { } arr) copy.Put(key, new PdfArray(arr.ToFloatArray()));
        copy.Put(PdfName.F, new PdfNumber(a.GetAsNumber(PdfName.F)?.IntValue() ?? 0));
        return copy;
    }

    [Theory, MemberData(nameof(Frames))] // /QuadPoints por VÉRTICE: TL,TR,BL,BR exibidos levados ao usuário (não o retângulo normalizado)
    public void MarkupQuadPoints_AreDisplayedVerticesInUserSpace(int rot, bool crop)
    {
        foreach (var id in new[] { "hl", "ul", "so" })
        {
            var a = Original(id);
            var pdf = Editor.AddAnnotation(AnnotationRotationRenderTests.Page(rot, crop), a);
            var f = FrameOf(pdf);
            var expected = a.Quads!.SelectMany(f.QuadPointsToUser).ToArray();
            var qp = Raw(pdf, id).GetAsArray(PdfName.QuadPoints).ToFloatArray();
            Assert.Equal(expected.Length, qp.Length);
            for (int i = 0; i < qp.Length; i++) Assert.Equal(expected[i], qp[i], 0.01);
            // /Rect = envelope exibido levado ao usuário; nunca a flag NoRotate
            var rect = Raw(pdf, id).GetAsArray(PdfName.Rect).ToFloatArray();
            var u = f.ToUser(new PdfQuad(a.LeftPt, a.BottomPt, a.RightPt, a.TopPt));
            Assert.Equal(u.LeftPt, Math.Min(rect[0], rect[2]), 0.01);
            Assert.Equal(u.TopPt, Math.Max(rect[1], rect[3]), 0.01);
        }
    }

    [Theory, MemberData(nameof(Frames))] // nenhum kind usa NoRotate (o PDFium ancora errado)
    public void NoKind_UsesNoRotateFlag(int rot, bool crop)
    {
        var pdf = AnnotationRotationRenderTests.Page(rot, crop);
        foreach (var a in AnnotationRotationRenderTests.Kinds()) pdf = Editor.AddAnnotation(pdf, a);
        foreach (var a in AnnotationRotationRenderTests.Kinds())
            Assert.Equal(0, Raw(pdf, a.Id!).GetAsNumber(PdfName.F).IntValue() & PdfAnnotation.NO_ROTATE);
    }

    [Theory, MemberData(nameof(Frames))] // FreeText cresce PARA BAIXO no frame EXIBIDO e para no rodapé exibido (y=0)
    public void FreeTextGrowth_GrowsDownInDisplayedFrame_ClampsAtDisplayedBottom(int rot, bool crop)
    {
        var pdf = AnnotationRotationRenderTests.Page(rot, crop);
        var grow = new AnnotationData
        {
            Id = "g", Kind = AnnotationKind.FreeText, PageIndex = 0, LeftPt = 100, BottomPt = 300, RightPt = 200, TopPt = 320,
            Content = string.Join(" ", Enumerable.Repeat("palavra", 12)),
        };
        var clamp = grow with { Id = "c", LeftPt = 300, BottomPt = 20, RightPt = 340, TopPt = 40, Content = string.Join(" ", Enumerable.Repeat("palavra", 60)) };
        pdf = Editor.AddAnnotation(Editor.AddAnnotation(pdf, grow), clamp);

        var read = Editor.ReadAnnotations(pdf).ToDictionary(a => a.Id!);
        var g = read["g"];
        Assert.Equal(100, g.LeftPt, 0.01); Assert.Equal(200, g.RightPt, 0.01); Assert.Equal(320, g.TopPt, 0.01);
        Assert.True(g.BottomPt < 300 - 20, $"rot={rot}: não cresceu para baixo (B={g.BottomPt})");
        var c = read["c"];
        Assert.Equal(0, c.BottomPt, 0.01); Assert.Equal(40, c.TopPt, 0.01);
        Assert.Equal(300, c.LeftPt, 0.01); Assert.Equal(340, c.RightPt, 0.01);

        // e o texto aparece dentro da caixa crescida (abaixo do fundo original)
        var p = AnnotationRotationRenderTests.Render(pdf);
        Assert.True(AnnotationRotationRenderTests.CountNear(p, new PdfPoint(150, 300 - 10), 20, AnnotationRotationRenderTests.Dark) > 10,
            $"rot={rot}: texto não aparece na parte crescida da caixa");
    }

    /// Anotações gravadas por OUTRO programa numa página girada (/Rect, /QuadPoints, /InkList, /L no espaço do
    /// usuário, sem /NM nosso): ReadAnnotations só converte a geometria para o frame exibido.
    [Theory, MemberData(nameof(Frames))]
    public void Read_ForeignAnnotations_ConvertsGeometryOnly(int rot, bool crop)
    {
        var pdf = AnnotationRotationRenderTests.Page(rot, crop);
        var f = FrameOf(pdf);
        // usuário absoluto de um retângulo/pontos EXIBIDOS conhecidos
        var dispRect = new PdfQuad(100, 200, 180, 230);
        var u = f.ToUser(dispRect);
        var qp = f.QuadPointsToUser(dispRect);
        var p0 = f.ToUser(new PdfPoint(100, 200)); var p1 = f.ToUser(new PdfPoint(180, 230));
        byte[] foreign;
        using (var o = new MemoryStream())
        {
            using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), new PdfWriter(o)))
            {
                var page = doc.GetPage(1);
                var ur = new Rectangle((float)u.LeftPt, (float)u.BottomPt, (float)(u.RightPt - u.LeftPt), (float)(u.TopPt - u.BottomPt));
                var sq = new PdfSquareAnnotation(ur); sq.SetName(new PdfString("sq")); page.AddAnnotation(sq);
                var hl = PdfTextMarkupAnnotation.CreateHighLight(ur, qp); hl.SetName(new PdfString("hl")); page.AddAnnotation(hl);
                // /Rect de outro programa costuma ser MAIOR que a linha (margem para a espessura/pontas)
                var big = new Rectangle(ur.GetLeft() - 5, ur.GetBottom() - 5, ur.GetWidth() + 10, ur.GetHeight() + 10);
                var ln = new PdfLineAnnotation(big, new[] { (float)p0.XPt, (float)p0.YPt, (float)p1.XPt, (float)p1.YPt });
                ln.SetName(new PdfString("ln")); page.AddAnnotation(ln);
            }
            foreign = o.ToArray();
        }

        var read = Editor.ReadAnnotations(foreign).ToDictionary(a => a.Id!);

        foreach (var id in new[] { "sq", "hl", "ln" })
        {
            var a = read[id];
            Assert.Equal(100, a.LeftPt, 0.01); Assert.Equal(200, a.BottomPt, 0.01);
            Assert.Equal(180, a.RightPt, 0.01); Assert.Equal(230, a.TopPt, 0.01);
        }
        var q = Assert.Single(read["hl"].Quads!);
        Assert.Equal(dispRect, new PdfQuad(Math.Round(q.LeftPt, 3), Math.Round(q.BottomPt, 3), Math.Round(q.RightPt, 3), Math.Round(q.TopPt, 3)));
        Assert.Equal(100, read["ln"].LineStartPt!.Value.XPt, 0.01); Assert.Equal(200, read["ln"].LineStartPt!.Value.YPt, 0.01);
        Assert.Equal(180, read["ln"].LineEndPt!.Value.XPt, 0.01); Assert.Equal(230, read["ln"].LineEndPt!.Value.YPt, 0.01);
    }
}
