using System.IO;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using mPdf.Editing;
using mPdf.Rendering;
using Xunit;

namespace mPdf.Signing.Tests;

/// Páginas giradas, Task 3: `SignatureInfo.StampRect` no FRAME EXIBIDO em /Rotate 0/90/180/270 (+ CropBox) —
/// o mesmo de `VisibleStampSpec.Rect`, e onde o PDFium de fato desenha o carimbo.
public class StampRectRotationTests
{
    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();

    private const double L = 100, B = 100, R = 350, T = 250; // exibido, cabe em retrato e paisagem (500x500)

    private static byte[] Blank(int rot, bool crop)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            if (crop) page.SetCropBox(new Rectangle(30, 40, 500, 720));
            if (rot != 0) page.SetRotation(rot);
        }
        return ms.ToArray();
    }

    public static IEnumerable<object[]> Frames() => new[]
    {
        new object[] { 0, false }, new object[] { 90, false }, new object[] { 180, false }, new object[] { 270, false },
        new object[] { 0, true }, new object[] { 90, true }, new object[] { 180, true }, new object[] { 270, true },
    };

    [Theory]
    [MemberData(nameof(Frames))]
    public void ReadSignatures_StampRect_EqualsRequestedDisplayedRect(int rot, bool crop)
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        var signed = Engine.Sign(new SignRequest(Blank(rot, crop), cert, "Aprovacao", "Local",
            new VisibleStampSpec(0, new PdfQuad(L, B, R, T)), null));

        var info = Assert.Single(Engine.ReadSignatures(signed));

        Assert.Equal(0, info.StampPageIndex);
        var r = info.StampRect!.Value;
        Assert.Equal(L, r.LeftPt, 0.01); Assert.Equal(B, r.BottomPt, 0.01);
        Assert.Equal(R, r.RightPt, 0.01); Assert.Equal(T, r.TopPt, 0.01);
    }

    [Theory] // oráculo de render: o StampRect lido cai onde o PDFium desenha o carimbo
    [MemberData(nameof(Frames))]
    public void ReadSignatures_StampRect_MatchesWhereTheStampRenders(int rot, bool crop)
    {
        const double Scale = 2.0;
        var pdf = Blank(rot, crop);
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        var signed = Engine.Sign(new SignRequest(pdf, cert, "Aprovacao", "Local",
            new VisibleStampSpec(0, new PdfQuad(L, B, R, T)), null));
        var r = Assert.Single(Engine.ReadSignatures(signed)).StampRect!.Value;

        using var r0 = new PdfDocumentRenderer(pdf);
        using var r1 = new PdfDocumentRenderer(signed);
        var pa = r0.RenderPage(0, Scale);
        var pb = r1.RenderPage(0, Scale);
        int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1;
        for (int y = 0; y < pa.HeightPx; y++)
            for (int x = 0; x < pa.WidthPx; x++)
            {
                int i = (y * pa.WidthPx + x) * 4;
                if (pa.Bgra[i] == pb.Bgra[i] && pa.Bgra[i + 1] == pb.Bgra[i + 1] && pa.Bgra[i + 2] == pb.Bgra[i + 2]) continue;
                minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
            }
        Assert.True(maxx >= 0, "carimbo não apareceu");
        double hd = pa.HeightPx / Scale;
        const int Tol = 8; // px @2x
        Assert.InRange(minx, (int)(r.LeftPt * Scale) - Tol, (int)(r.LeftPt * Scale) + Tol);
        Assert.InRange(maxx, (int)(r.RightPt * Scale) - Tol, (int)(r.RightPt * Scale) + Tol);
        Assert.InRange(miny, (int)((hd - r.TopPt) * Scale) - Tol, (int)((hd - r.TopPt) * Scale) + Tol);
        Assert.InRange(maxy, (int)((hd - r.BottomPt) * Scale) - Tol, (int)((hd - r.BottomPt) * Scale) + Tol);
    }
}
