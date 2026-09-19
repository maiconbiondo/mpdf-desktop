using mPdf.Rendering;

namespace mPdf.PreviewHandler.Aot;

/// Lógica PURA de render (bytes do PDF -> bitmap BGRA fit-width). Igual à 1a abordagem — reusa
/// `PdfDocumentRenderer` (PDFium/Docnet), que já se provou compatível com Native AOT.
internal static class PreviewRenderer
{
    public static (RenderedPage Page, int PageCount) RenderFitWidth(
        byte[] pdf, int pageIndex, int targetWidthPx, double maxScale = 3.0)
    {
        if (targetWidthPx < 1) targetWidthPx = 1;
        using var renderer = new PdfDocumentRenderer(pdf);
        int count = renderer.PageCount;
        if (pageIndex < 0 || pageIndex >= count) pageIndex = 0;

        var tamanho = renderer.GetPageSize(pageIndex);
        double larguraPt = tamanho.WidthPt;
        double scale = larguraPt > 0 ? targetWidthPx / larguraPt : 1.0;
        scale = Math.Clamp(scale, 0.1, maxScale);

        var page = renderer.RenderPage(pageIndex, scale);
        return (page, count);
    }
}
