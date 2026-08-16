namespace mPdf.Rendering;

/// PDFium não é thread-safe: TODA chamada Docnet no processo passa por este lock.
public static class PdfRenderLock
{
    public static readonly object Gate = new();
}
