using System.IO;
using mPdf.App.ViewModels;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

public class ZoomTests
{
    private static DocumentViewModel Doc() =>
        new(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));

    [Fact] // passos de 10% e limites 25%-400%
    public void ZoomCommands_StepAndClamp()
    {
        using var doc = Doc();
        doc.ZoomInCommand.Execute(null);
        Assert.Equal(1.1, doc.Zoom, 3);
        doc.Zoom = 4.0;
        doc.ZoomInCommand.Execute(null);
        Assert.Equal(4.0, doc.Zoom, 3);   // não passa do teto
        doc.Zoom = 0.25;
        doc.ZoomOutCommand.Execute(null);
        Assert.Equal(0.25, doc.Zoom, 3);  // não passa do piso
    }

    [Fact] // ajustar à largura: viewport de 1190px (2x A4 em px de tela) -> zoom ~2.0 (menos margem)
    public void FitWidth_ComputesZoomFromViewport()
    {
        using var doc = Doc();
        double pageWidthPx = 595 * 96.0 / 72.0;      // ~793px no zoom 1.0
        doc.FitWidth(pageWidthPx * 2);
        Assert.InRange(doc.Zoom, 1.85, 2.0);          // 2.0 menos a folga de margem
    }

    [Fact] // página inteira: limita pelo lado mais restritivo (altura, no A4 retrato)
    public void FitPage_UsesMostRestrictiveDimension()
    {
        using var doc = Doc();
        double pageHeightPx = 842 * 96.0 / 72.0;
        doc.FitPage(10_000, pageHeightPx);            // altura = exatamente 1 página
        Assert.InRange(doc.Zoom, 0.90, 1.0);
    }

    [Fact] // rótulo formatado
    public void ZoomPercent_Formats()
    {
        using var doc = Doc();
        doc.Zoom = 1.5;
        Assert.Equal("150%", doc.ZoomPercent);
    }
}
