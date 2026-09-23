using mPdf.Editing;
using Xunit;

namespace mPdf.Signing.Tests;

public class StampRotationTests
{
    // MediaBox retrato A4-ish (não-rotacionado)
    private const double W = 595, H = 842;
    // Retângulo desenhado pelo usuário no frame EXIBIDO (L,B,R,T), y-para-cima
    private static readonly PdfQuad Displayed = new(100, 50, 300, 150); // 200 largo x 100 alto

    [Fact] // rot 0: documento comum — identidade (comportamento intocado)
    public void Rot0_IsIdentity()
    {
        var mb = StampRotation.DisplayedToMediaBox(Displayed, 0, W, H);
        Assert.Equal(Displayed, mb);
        Assert.False(StampRotation.SwapsAxes(0));
    }

    [Fact] // rot 90 horário: eixos trocam (100x200 no MediaBox)
    public void Rot90_MapsAndSwaps()
    {
        var mb = StampRotation.DisplayedToMediaBox(Displayed, 90, W, H);
        Assert.Equal(new PdfQuad(445, 100, 545, 300), mb);
        Assert.True(StampRotation.SwapsAxes(90));
        Assert.True(mb.RightPt > mb.LeftPt && mb.TopPt > mb.BottomPt); // normalizado
    }

    [Fact] // rot 180: sem troca de eixo, espelhado nos dois eixos
    public void Rot180_Maps()
    {
        var mb = StampRotation.DisplayedToMediaBox(Displayed, 180, W, H);
        Assert.Equal(new PdfQuad(295, 692, 495, 792), mb);
        Assert.False(StampRotation.SwapsAxes(180));
    }

    [Fact] // rot 270: eixos trocam
    public void Rot270_MapsAndSwaps()
    {
        var mb = StampRotation.DisplayedToMediaBox(Displayed, 270, W, H);
        Assert.Equal(new PdfQuad(50, 542, 150, 742), mb);
        Assert.True(StampRotation.SwapsAxes(270));
    }

    [Theory] // round-trip: exibido -> MediaBox -> exibido volta ao original (inversa consistente)
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RoundTrip_ViaForwardReference(int rot)
    {
        var mb = StampRotation.DisplayedToMediaBox(Displayed, rot, W, H);
        var back = MediaBoxToDisplayedReference(mb, rot, W, H);
        AssertClose(Displayed, back);
    }

    // Referência FORWARD (MediaBox -> exibido), independente da implementação sob teste — 90°:
    // (x,y)->(y, W-x); 180°: (W-x, H-y); 270°: (H-y, x). Usada só pro round-trip.
    private static PdfQuad MediaBoxToDisplayedReference(PdfQuad m, int rotation, double w, double h)
    {
        int rot = ((rotation % 360) + 360) % 360;
        (double x, double y) P(double x, double y) => rot switch
        {
            90 => (y, w - x),
            180 => (w - x, h - y),
            270 => (h - y, x),
            _ => (x, y),
        };
        var c1 = P(m.LeftPt, m.BottomPt);
        var c2 = P(m.RightPt, m.TopPt);
        return new PdfQuad(
            System.Math.Min(c1.x, c2.x), System.Math.Min(c1.y, c2.y),
            System.Math.Max(c1.x, c2.x), System.Math.Max(c1.y, c2.y));
    }

    private static void AssertClose(PdfQuad a, PdfQuad b)
    {
        Assert.Equal(a.LeftPt, b.LeftPt, 3);
        Assert.Equal(a.BottomPt, b.BottomPt, 3);
        Assert.Equal(a.RightPt, b.RightPt, 3);
        Assert.Equal(a.TopPt, b.TopPt, 3);
    }
}
