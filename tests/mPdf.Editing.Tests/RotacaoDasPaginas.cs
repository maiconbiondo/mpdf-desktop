using iText.Kernel.Pdf;
using mPdf.Editing;

namespace mPdf.Editing.Tests;

/// Rotação de cada página como o PDFium a EXIBE (`PageFrame.Rotation`, 0/90/180/270), na ordem do documento — o que
/// o antigo `IPdfEditor.GetPageRotations` devolvia. O contrato não lê mais rotação (sem consumidor de produção desde o
/// plano páginas-giradas); os testes que precisam conferir o /Rotate gravado usam isto.
public static class RotacaoDasPaginas
{
    public static IReadOnlyList<int> Ler(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Enumerable.Range(1, doc.GetNumberOfPages()).Select(i => PageFrame.Rotation(doc.GetPage(i))).ToArray();
    }
}
