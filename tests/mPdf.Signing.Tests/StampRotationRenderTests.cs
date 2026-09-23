using System.IO;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using mPdf.Editing;
using mPdf.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace mPdf.Signing.Tests;

// Oráculo de render da costura de rotação: o carimbo desenhado no frame EXIBIDO tem que aparecer NA
// região exibida pedida (não girado, não espremido) em todas as rotações de /Rotate.
public class StampRotationRenderTests
{
    private readonly ITestOutputHelper _out;
    public StampRotationRenderTests(ITestOutputHelper o) => _out = o;

    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();

    // Retângulo EXIBIDO (y-para-cima), cabe em retrato E paisagem (dentro de 595x595)
    private const double L = 100, B = 100, R = 350, T = 250; // 250 largo x 150 alto

    private static byte[] BlankRotated(int rot)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
            doc.AddNewPage(PageSize.A4).SetRotation(rot);
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
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Stamp_LandsInRequestedDisplayedRect_PerRotation(int rot)
    {
        const double Scale = 2.0;
        var pdf = BlankRotated(rot);
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        var stamp = new VisibleStampSpec(0, new PdfQuad(L, B, R, T));
        var signed = Engine.Sign(new SignRequest(pdf, cert, "Aprovacao", "Local", stamp, null));

        using var r0 = new PdfDocumentRenderer(pdf);
        using var r1 = new PdfDocumentRenderer(signed);
        var pa = r0.RenderPage(0, Scale);
        var pb = r1.RenderPage(0, Scale);
        var bb = DiffBBox(pa, pb);

        // Região EXIBIDA pedida, em px de tela (y invertido: topo-down). Hd = altura EXIBIDA.
        double hd = pa.HeightPx / Scale;
        int exMinX = (int)(L * Scale), exMaxX = (int)(R * Scale);
        int exMinY = (int)((hd - T) * Scale), exMaxY = (int)((hd - B) * Scale);
        _out.WriteLine($"rot={rot}: ink x[{bb.minx}..{bb.maxx}] y[{bb.miny}..{bb.maxy}] | esperado x[{exMinX}..{exMaxX}] y[{exMinY}..{exMaxY}] count={bb.count}");

        const int Tol = 8; // px @2x (~4pt): borda/antialias
        Assert.InRange(bb.minx, exMinX - Tol, exMinX + Tol);
        Assert.InRange(bb.maxx, exMaxX - Tol, exMaxX + Tol);
        Assert.InRange(bb.miny, exMinY - Tol, exMinY + Tol);
        Assert.InRange(bb.maxy, exMaxY - Tol, exMaxY + Tol);
    }
}
