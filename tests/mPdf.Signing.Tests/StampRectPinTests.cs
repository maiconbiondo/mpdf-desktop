using System.IO;
using System.Linq;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using mPdf.Editing;
using Xunit;

namespace mPdf.Signing.Tests;

/// Páginas giradas, Task 1 — PIN TEST do `/Rect` do carimbo da assinatura visível. Valores-ouro capturados
/// ANTES de o `/Rect` passar à transformação única (`DisplayFrame`, em
/// mPdf.Editing): o `/Rect` gravado tem de continuar idêntico nas 4 rotações, com e sem CropBox deslocada.
public class StampRectPinTests
{
    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();

    private static byte[] Blank(int rot, bool crop)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            page.SetRotation(rot);
            if (crop) page.SetCropBox(new Rectangle(30, 40, 500, 720));
        }
        return ms.ToArray();
    }

    private static string WidgetRect(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var w = doc.GetPage(1).GetAnnotations().OfType<PdfWidgetAnnotation>().Single();
        var r = w.GetRectangle().ToDoubleArray();
        return string.Join(" ", r.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(0, false, "100.25 60.5 330.75 170")]
    [InlineData(90, false, "425 100.25 534.5 330.75")]
    [InlineData(180, false, "264.25 672 494.75 781.5")]
    [InlineData(270, false, "60.5 511.25 170 741.75")]
    [InlineData(0, true, "130.25 100.5 360.75 210")]
    [InlineData(90, true, "360 140.25 469.5 370.75")]
    [InlineData(180, true, "199.25 590 429.75 699.5")]
    [InlineData(270, true, "90.5 429.25 200 659.75")]
    public void StampRect_IsPinned(int rot, bool crop, string golden)
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        var stamp = new VisibleStampSpec(0, new PdfQuad(100.25, 60.5, 330.75, 170));
        var signed = Engine.Sign(new SignRequest(Blank(rot, crop), cert, "Aprovacao", "Local", stamp, null));

        Assert.Equal(golden, WidgetRect(signed));
    }
}
