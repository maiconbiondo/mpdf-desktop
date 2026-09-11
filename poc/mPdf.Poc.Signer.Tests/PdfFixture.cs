using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;

namespace mPdf.Poc.Signer.Tests;

public static class PdfFixture
{
    public static byte[] CreateSimplePdf(string text = "Documento de teste mPDF")
    {
        using var ms = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(ms)))
        using (var doc = new Document(pdf))
        {
            doc.Add(new Paragraph(text));
        }
        return ms.ToArray();
    }
}
