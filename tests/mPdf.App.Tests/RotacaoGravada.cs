using System.IO;
using iText.Kernel.Pdf;

namespace mPdf.App.Tests;

/// /Rotate gravado em cada página (o `% 360` do iText — igual à rotação exibida para os múltiplos de 90 que estes
/// testes usam). O contrato `IPdfEditor` não lê mais rotação (o antigo `GetPageRotations` saiu sem consumidor de
/// produção); os testes do App que conferem "a página é mesmo girada" leem o PDF direto.
internal static class RotacaoGravada
{
    public static int[] Ler(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Enumerable.Range(1, doc.GetNumberOfPages()).Select(i => doc.GetPage(i).GetRotation()).ToArray();
    }
}
