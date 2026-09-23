using mPdf.Rendering;
using Xunit;

namespace mPdf.Rendering.Tests;

public class RenderPageTests
{
    [Fact] // buffer tem exatamente W*H*4 bytes
    public void RenderPage_BufferMatchesDimensions()
    {
        using var r = new PdfDocumentRenderer(Fixtures.A4());
        var page = r.RenderPage(0, 1.0);
        Assert.Equal(page.WidthPx * page.HeightPx * 4, page.Bgra.Length);
    }

    [Fact] // dobrar a escala dobra as dimensões em pixels (tolerância 2px de arredondamento)
    public void RenderPage_DoubleScale_DoublesPixelSize()
    {
        using var r = new PdfDocumentRenderer(Fixtures.A4());
        var s1 = r.RenderPage(0, 1.0);
        var s2 = r.RenderPage(0, 2.0);
        Assert.InRange(s2.WidthPx, s1.WidthPx * 2 - 2, s1.WidthPx * 2 + 2);
        Assert.InRange(s2.HeightPx, s1.HeightPx * 2 - 2, s1.HeightPx * 2 + 2);
    }

    [Fact] // a página da fixture tem texto: nem todos os pixels são brancos
    public void RenderPage_FixtureWithText_IsNotBlank()
    {
        using var r = new PdfDocumentRenderer(Fixtures.A4());
        var page = r.RenderPage(0, 1.0);
        bool hasInk = false;
        for (int i = 0; i < page.Bgra.Length; i += 4)
            if (page.Bgra[i] < 250) { hasInk = true; break; }  // canal B abaixo de branco
        Assert.True(hasInk, "página renderizada saiu toda branca");
    }

    [Fact] // renderizações em escalas alternadas não corrompem (exercita o cache de reader)
    public void RenderPage_AlternatingScales_AllBuffersConsistent()
    {
        using var r = new PdfDocumentRenderer(Fixtures.ThirtyPages());
        foreach (var scale in new[] { 1.0, 2.0, 1.0 })
        {
            var p = r.RenderPage(5, scale);
            Assert.Equal(p.WidthPx * p.HeightPx * 4, p.Bgra.Length);
        }
    }

    [Fact] // regressão Plano 10/Task 2: PDFium precisa continuar com anti-aliasing de texto em tons de
    // cinza LIGADO (comportamento padrão do PDFium sem nenhuma RenderFlags extra — investigação A/B
    // mostrou que OptimizeTextForLcd/Grayscale/ForceHalftone/RenderForPrinting são NO-OPs byte-a-byte
    // nesta build nativa, e DisableTextAntialiasing piora visivelmente, ver task-2-report.md). Prova
    // de disparo: fixture-a4 com AA ligado tem 994 pixels de cinza intermediário; com
    // DisableTextAntialiasing esse número cai pra 0 (medido ao vivo no harness de investigação) — a
    // guarda abaixo pegaria qualquer regressão futura que ligasse essa flag por engano.
    public void RenderPage_SmallText_HasIntermediateGrayAntialiasing()
    {
        using var r = new PdfDocumentRenderer(Fixtures.A4());
        var page = r.RenderPage(0, 1.0);
        bool hasIntermediateGray = false;
        for (int i = 0; i < page.Bgra.Length; i += 4)
        {
            byte b = page.Bgra[i];
            if (b > 10 && b < 245) { hasIntermediateGray = true; break; } // nem preto nem branco puros
        }
        Assert.True(hasIntermediateGray,
            "nenhum pixel de cinza intermediário — texto pode estar sendo rasterizado sem anti-aliasing");
    }

    [Fact] // anotações (carimbo de assinatura) DEVEM ser renderizadas (RenderFlags.RenderAnnotations)
    public void RenderPage_SignatureStampAnnotation_IsPainted()
    {
        using var r = new PdfDocumentRenderer(
            File.ReadAllBytes(Path.Combine(Fixtures.Root, "fixture-carimbo.pdf")));
        var page = r.RenderPage(0, 1.0);
        // região do carimbo: retângulo (300,50)-(550,130) em pontos, origem PDF = canto inferior esquerdo;
        // em pixels de imagem (origem topo): y_img = alturaPt - y_pdf. Conte pixels não-brancos na região.
        int painted = 0;
        int h = page.HeightPx, w = page.WidthPx;
        for (int y = h - 130; y < h - 50; y++)
            for (int x = 300; x < 550; x++)
            {
                int i = (y * w + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) painted++;
            }
        Assert.True(painted > 100, $"carimbo não renderizado: só {painted} pixels pintados na região");
    }
}
