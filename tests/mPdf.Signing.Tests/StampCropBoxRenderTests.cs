using System.IO;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using mPdf.Editing;
using mPdf.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace mPdf.Signing.Tests;

// fix/markup-cropbox (2026-10-09): o frame EXIBIDO do PDFium é a CropBox (recortada pela MediaBox), não a
// MediaBox. Numa página recortada (CropBox com origem != 0,0 e/ou menor que a MediaBox) o carimbo
// desenhado pelo usuário tem que aparecer NA região exibida pedida, em todas as rotações.
public class StampCropBoxRenderTests
{
    private readonly ITestOutputHelper _out;
    public StampCropBoxRenderTests(ITestOutputHelper o) => _out = o;

    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();
    private const double L = 100, B = 100, R = 350, T = 250;

    private static byte[] CroppedRotated(int rot, float ox, float oy)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(new PageSize(595 + 2 * ox + 40, 842 + 2 * oy + 25)); // MediaBox maior
            page.SetCropBox(new Rectangle(ox, oy, 595, 842));
            page.SetRotation(rot);
        }
        return ms.ToArray();
    }

    private static (int minx, int miny, int maxx, int maxy, int count) DiffBBox(RenderedPage a, RenderedPage b)
    {
        int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1, count = 0;
        for (int y = 0; y < a.HeightPx; y++)
            for (int x = 0; x < a.WidthPx; x++)
            {
                int i = (y * a.WidthPx + x) * 4;
                if (a.Bgra[i] != b.Bgra[i] || a.Bgra[i + 1] != b.Bgra[i + 1] || a.Bgra[i + 2] != b.Bgra[i + 2])
                { count++; if (x < minx) minx = x; if (y < miny) miny = y; if (x > maxx) maxx = x; if (y > maxy) maxy = y; }
            }
        return (minx, miny, maxx, maxy, count);
    }

    [Theory]
    [InlineData(0, 0f, 11f)]
    [InlineData(0, 30f, 50f)]
    [InlineData(90, 30f, 50f)]
    [InlineData(180, 30f, 50f)]
    [InlineData(270, 30f, 50f)]
    public void Stamp_CroppedPage_LandsInRequestedDisplayedRect(int rot, float ox, float oy)
    {
        const double Scale = 2.0;
        var pdf = CroppedRotated(rot, ox, oy);
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        var signed = Engine.Sign(new SignRequest(pdf, cert, "Aprovacao", "Local", new VisibleStampSpec(0, new PdfQuad(L, B, R, T)), null));

        using var r0 = new PdfDocumentRenderer(pdf);
        using var r1 = new PdfDocumentRenderer(signed);
        var pa = r0.RenderPage(0, Scale);
        var pb = r1.RenderPage(0, Scale);
        var bb = DiffBBox(pa, pb);

        double hd = pa.HeightPx / Scale;
        int exMinX = (int)(L * Scale), exMaxX = (int)(R * Scale);
        int exMinY = (int)((hd - T) * Scale), exMaxY = (int)((hd - B) * Scale);
        _out.WriteLine($"rot={rot} crop=({ox},{oy}): ink x[{bb.minx}..{bb.maxx}] y[{bb.miny}..{bb.maxy}] | esperado x[{exMinX}..{exMaxX}] y[{exMinY}..{exMaxY}] count={bb.count}");

        const int Tol = 8;
        Assert.True(bb.count > 0, "carimbo não apareceu na página exibida");
        Assert.InRange(bb.minx, exMinX - Tol, exMinX + Tol);
        Assert.InRange(bb.maxx, exMaxX - Tol, exMaxX + Tol);
        Assert.InRange(bb.miny, exMinY - Tol, exMinY + Tol);
        Assert.InRange(bb.maxy, exMaxY - Tol, exMaxY + Tol);
    }

    [Theory] // ReadSignatures devolve o StampRect no mesmo frame exibido (o destaque do painel cai no carimbo)
    [InlineData(0f, 0f)]
    [InlineData(0f, 11f)]
    [InlineData(30f, 50f)]
    public void ReadSignatures_CroppedPage_StampRectInDisplayedFrame(float ox, float oy)
    {
        var pdf = CroppedRotated(0, ox, oy);
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        var signed = Engine.Sign(new SignRequest(pdf, cert, "Aprovacao", "Local", new VisibleStampSpec(0, new PdfQuad(L, B, R, T)), null));

        var info = Assert.Single(Engine.ReadSignatures(signed));

        Assert.Equal(0, info.StampPageIndex);
        var r = info.StampRect!.Value;
        Assert.Equal(L, r.LeftPt, 0.01);
        Assert.Equal(B, r.BottomPt, 0.01);
        Assert.Equal(R, r.RightPt, 0.01);
        Assert.Equal(T, r.TopPt, 0.01);
    }
}
