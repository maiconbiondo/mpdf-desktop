using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 1: `DisplayFrame` — a transformação ÚNICA frame exibido (PDFium, já girado, relativo
/// à origem de CropBox∩MediaBox) ↔ espaço do usuário ABSOLUTO (iText). Tabela da análise
/// (docs/superpowers/specs/2026-10-13-paginas-giradas-analise.md §3), conferida pela sonda.
public class DisplayFrameTests
{
    private const double W = 595, H = 842;
    private const double Ox = 30, Oy = 40;

    // Referência INDEPENDENTE (exibido -> usuário RELATIVO), escrita a partir da tabela da análise.
    private static (double x, double y) RefToUserRel(int rot, double X, double Y, double w, double h) => rot switch
    {
        90 => (w - Y, X),
        180 => (w - X, h - Y),
        270 => (Y, h - X),
        _ => (X, Y),
    };

    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    [Theory]
    [MemberData(nameof(Rotations))]
    public void ToUser_MatchesAnalysisTable_WithOrigin(int rot)
    {
        var f = new DisplayFrame(rot, W, H, Ox, Oy);
        var (x, y) = RefToUserRel(rot, 100, 200, W, H);
        Assert.Equal(new PdfPoint(x + Ox, y + Oy), f.ToUser(new PdfPoint(100, 200)));
    }

    [Theory]
    [MemberData(nameof(Rotations))]
    public void Points_RoundTrip_IsIdentity(int rot)
    {
        var f = new DisplayFrame(rot, W, H, Ox, Oy);
        foreach (var p in new[] { new PdfPoint(0, 0), new PdfPoint(12.5, 700.25), new PdfPoint(500, 3) })
        {
            Assert.Equal(p, f.ToDisplayed(f.ToUser(p)));
            Assert.Equal(p, f.ToUser(f.ToDisplayed(p)));
        }
    }

    [Fact] // sonda /Rotate 90 A4: 'S' de "SIDEWAYS" em usuário (60,100) -> exibido (100,535)
    public void ToDisplayed_MatchesProbe_Rot90()
    {
        var f = new DisplayFrame(90, W, H);
        Assert.Equal(new PdfPoint(100, 535), f.ToDisplayed(new PdfPoint(60, 100)));
    }

    [Theory] // sonda: retângulo exibido (100,200,350,300) -> usuário (295,100,395,350) em /Rotate 90
    [InlineData(0, 100, 200, 350, 300)]
    [InlineData(90, 295, 100, 395, 350)]
    [InlineData(180, 245, 542, 495, 642)]
    [InlineData(270, 200, 492, 300, 742)]
    public void RectToUser_Normalized(int rot, double l, double b, double r, double t)
    {
        var f = new DisplayFrame(rot, W, H);
        var u = f.ToUser(new PdfQuad(100, 200, 350, 300));
        Assert.Equal(new PdfQuad(l, b, r, t), u);
        Assert.Equal(new PdfQuad(100, 200, 350, 300), f.ToDisplayed(u));
    }

    [Theory]
    [MemberData(nameof(Rotations))]
    public void RectToUser_WithOrigin_IsRelativePlusOrigin(int rot)
    {
        var rel = new DisplayFrame(rot, W, H).ToUser(new PdfQuad(10, 20, 30, 50));
        var abs = new DisplayFrame(rot, W, H, Ox, Oy).ToUser(new PdfQuad(10, 20, 30, 50));
        Assert.Equal(new PdfQuad(rel.LeftPt + Ox, rel.BottomPt + Oy, rel.RightPt + Ox, rel.TopPt + Oy), abs);
    }

    [Theory] // ordem por vértice: TL, TR, BL, BR EXIBIDOS levados ao usuário (8 floats)
    [MemberData(nameof(Rotations))]
    public void QuadPointsToUser_PerVertex(int rot)
    {
        var f = new DisplayFrame(rot, W, H, Ox, Oy);
        var d = new PdfQuad(100, 200, 350, 300);
        var qp = f.QuadPointsToUser(d);
        var expected = new[] { (100.0, 300.0), (350.0, 300.0), (100.0, 200.0), (350.0, 200.0) }
            .SelectMany(v => { var u = f.ToUser(new PdfPoint(v.Item1, v.Item2)); return new[] { (float)u.XPt, (float)u.YPt }; })
            .ToArray();
        Assert.Equal(expected, qp);
    }

    [Theory] // rot 0: QuadPoints idênticos ao BuildQuadPoints atual (TL,TR,BL,BR do retângulo + origem)
    [InlineData(0.0, 0.0)]
    [InlineData(Ox, Oy)]
    public void QuadPointsToUser_Rot0_IsTranslatedRectangle(double ox, double oy)
    {
        var qp = new DisplayFrame(0, W, H, ox, oy).QuadPointsToUser(new PdfQuad(1, 2, 3, 4));
        Assert.Equal(new[] { (float)(1 + ox), (float)(4 + oy), (float)(3 + ox), (float)(4 + oy),
                             (float)(1 + ox), (float)(2 + oy), (float)(3 + ox), (float)(2 + oy) }, qp);
    }

    [Theory] // matriz linear "em pé" (/Matrix do AP, Tm do texto) — tabela da análise
    [InlineData(0, 1, 0, 0, 1)]
    [InlineData(90, 0, 1, -1, 0)]
    [InlineData(180, -1, 0, 0, -1)]
    [InlineData(270, 0, -1, 1, 0)]
    public void ContentMatrix_PerRotation(int rot, double a, double b, double c, double d)
    {
        Assert.Equal((a, b, c, d), new DisplayFrame(rot, W, H).ContentMatrix);
    }

    [Theory] // a matriz linear leva os eixos EXIBIDOS (+X avanço, +Y subida) aos vetores no usuário
    [MemberData(nameof(Rotations))]
    public void ContentMatrix_IsLinearPartOfToUser(int rot)
    {
        var f = new DisplayFrame(rot, W, H, Ox, Oy);
        var (a, b, c, d) = f.ContentMatrix;
        var o = f.ToUser(new PdfPoint(0, 0));
        var ex = f.ToUser(new PdfPoint(1, 0));
        var ey = f.ToUser(new PdfPoint(0, 1));
        Assert.Equal((a, b), (ex.XPt - o.XPt, ex.YPt - o.YPt));
        Assert.Equal((c, d), (ey.XPt - o.XPt, ey.YPt - o.YPt));
    }

    [Theory]
    [MemberData(nameof(Rotations))]
    public void TextMatrix_IsLinearPlusToUserOfOrigin(int rot)
    {
        var f = new DisplayFrame(rot, W, H, Ox, Oy);
        var m = f.TextMatrix(new PdfPoint(50, 515));
        var p = f.ToUser(new PdfPoint(50, 515));
        Assert.Equal((f.ContentMatrix.A, f.ContentMatrix.B, f.ContentMatrix.C, f.ContentMatrix.D, p.XPt, p.YPt), m);
    }

    [Theory]
    [InlineData(0, false, 595, 842)]
    [InlineData(90, true, 842, 595)]
    [InlineData(180, false, 595, 842)]
    [InlineData(270, true, 842, 595)]
    [InlineData(-90, true, 842, 595)]
    [InlineData(450, true, 842, 595)]
    public void DisplayedSize_AndNormalization(int rot, bool swaps, double dw, double dh)
    {
        var f = new DisplayFrame(rot, W, H);
        Assert.Equal(swaps, f.SwapsAxes);
        Assert.Equal(dw, f.DisplayedWidth);
        Assert.Equal(dh, f.DisplayedHeight);
        Assert.Contains(f.Rotation, new[] { 0, 90, 180, 270 });
    }

    [Fact]
    public void Of_ReadsRotationAndDisplayedBox()
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            page.SetRotation(270);
            page.SetCropBox(new Rectangle(30, 40, 500, 720));
            var f = PageFrame.Frame(page);
            Assert.Equal(new DisplayFrame(270, 500, 720, 30, 40), f);
        }
    }

    // --- AnnotationData (insumo da Task 2; rot 0 = Translate exato) ---------------------------------------

    private static AnnotationData Sample() => new()
    {
        Kind = AnnotationKind.Ink, PageIndex = 0,
        LeftPt = 100, BottomPt = 200, RightPt = 350, TopPt = 300,
        Quads = new[] { new PdfQuad(100, 200, 150, 220) },
        InkStrokes = new[] { (IReadOnlyList<PdfPoint>)new[] { new PdfPoint(100, 200), new PdfPoint(350, 300) } },
        LineStartPt = new PdfPoint(110, 210), LineEndPt = new PdfPoint(340, 290),
    };

    [Fact]
    public void Annotation_Rot0_EqualsTranslate()
    {
        var a = Sample();
        var u = new DisplayFrame(0, W, H, Ox, Oy).ToUser(a);
        var t = PageFrame.Translate(a, Ox, Oy);
        Assert.Equal(t.LeftPt, u.LeftPt); Assert.Equal(t.BottomPt, u.BottomPt);
        Assert.Equal(t.RightPt, u.RightPt); Assert.Equal(t.TopPt, u.TopPt);
        Assert.Equal(t.Quads, u.Quads);
        Assert.Equal(t.InkStrokes![0], u.InkStrokes![0]);
        Assert.Equal(t.LineStartPt, u.LineStartPt); Assert.Equal(t.LineEndPt, u.LineEndPt);
        Assert.Same(a, new DisplayFrame(0, W, H).ToUser(a)); // sem origem: o mesmo objeto (caminho comum intocado)
    }

    [Theory]
    [MemberData(nameof(Rotations))]
    public void Annotation_RoundTrip_AndGeometry(int rot)
    {
        var f = new DisplayFrame(rot, W, H, Ox, Oy);
        var a = Sample();
        var u = f.ToUser(a);
        Assert.Equal(f.ToUser(new PdfQuad(100, 200, 350, 300)), new PdfQuad(u.LeftPt, u.BottomPt, u.RightPt, u.TopPt));
        Assert.Equal(f.ToUser(new PdfQuad(100, 200, 150, 220)), u.Quads![0]);
        Assert.Equal(f.ToUser(new PdfPoint(350, 300)), u.InkStrokes![0][1]);
        Assert.Equal(f.ToUser(new PdfPoint(110, 210)), u.LineStartPt);

        var back = f.ToDisplayed(u);
        Assert.Equal((a.LeftPt, a.BottomPt, a.RightPt, a.TopPt), (back.LeftPt, back.BottomPt, back.RightPt, back.TopPt));
        Assert.Equal(a.Quads, back.Quads);
        Assert.Equal(a.InkStrokes![0], back.InkStrokes![0]);
        Assert.Equal(a.LineStartPt, back.LineStartPt); Assert.Equal(a.LineEndPt, back.LineEndPt);
    }

    // ---------------------------------------------------------------- revisão final (M1): cópias da rotação
    // que moravam em TextCorrectionEngine.Horizontal e PdfEditor.MatrizImposicaoGirada passam a viver aqui.

    [Theory] // vetor (sem origem): a diferença de dois pontos levados ao frame exibido
    [MemberData(nameof(Rotations))]
    public void VectorToDisplayed_IsDifferenceOfMappedPoints(int rot)
    {
        var f = new DisplayFrame(rot, W, H, Ox, Oy);
        foreach (var (vx, vy) in new[] { (1.0, 0.0), (0.0, 1.0), (3.5, -2.25) })
        {
            var p0 = f.ToDisplayed(new PdfPoint(100, 200));
            var p1 = f.ToDisplayed(new PdfPoint(100 + vx, 200 + vy));
            var v = f.VectorToDisplayed(vx, vy);
            Assert.Equal(p1.XPt - p0.XPt, v.X, 9);
            Assert.Equal(p1.YPt - p0.YPt, v.Y, 9);
        }
    }

    [Theory] // pin: a tabela que TextCorrectionEngine.Horizontal usava
    [InlineData(0, 2, 3, 2, 3)]
    [InlineData(90, 2, 3, 3, -2)]
    [InlineData(180, 2, 3, -2, -3)]
    [InlineData(270, 2, 3, -3, 2)]
    public void VectorToDisplayed_Pin(int rot, double vx, double vy, double ex, double ey)
    {
        var v = new DisplayFrame(rot, W, H).VectorToDisplayed(vx, vy);
        Assert.Equal((ex, ey), (v.X, v.Y));
    }

    [Theory] // [a b c d e f] (X = a·x + c·y + e, Y = b·x + d·y + f) == ToDisplayed ponto a ponto
    [MemberData(nameof(Rotations))]
    public void UserToDisplayedMatrix_EqualsToDisplayed(int rot)
    {
        var f = new DisplayFrame(rot, W, H, Ox, Oy);
        var m = f.UserToDisplayedMatrix();
        foreach (var u in new[] { new PdfPoint(30, 40), new PdfPoint(100, 200), new PdfPoint(612.5, 3.25) })
        {
            var d = f.ToDisplayed(u);
            Assert.Equal(d.XPt, m[0] * u.XPt + m[2] * u.YPt + m[4], 9);
            Assert.Equal(d.YPt, m[1] * u.XPt + m[3] * u.YPt + m[5], 9);
        }
    }

    [Theory] // pin: os valores que PdfEditor.MatrizImposicaoGirada usava
    [InlineData(90, 0, -1, 1, 0, -Oy, W + Ox)]
    [InlineData(180, -1, 0, 0, -1, W + Ox, H + Oy)]
    [InlineData(270, 0, 1, -1, 0, H + Oy, -Ox)]
    public void UserToDisplayedMatrix_Pin(int rot, double a, double b, double c, double d, double e, double ff)
    {
        Assert.Equal(new[] { a, b, c, d, e, ff }, new DisplayFrame(rot, W, H, Ox, Oy).UserToDisplayedMatrix());
    }
}
