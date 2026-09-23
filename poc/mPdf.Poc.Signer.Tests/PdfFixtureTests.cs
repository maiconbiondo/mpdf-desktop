using iText.Kernel.Pdf;
using Xunit;

namespace mPdf.Poc.Signer.Tests;

public class PdfFixtureTests
{
    [Fact] // bytes gerados são um PDF (cabeçalho %PDF)
    public void CreateSimplePdf_StartsWithPdfHeader()
    {
        var bytes = PdfFixture.CreateSimplePdf();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact] // abre no iText com exatamente 1 página
    public void CreateSimplePdf_HasOnePage()
    {
        var bytes = PdfFixture.CreateSimplePdf();
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        Assert.Equal(1, doc.GetNumberOfPages());
    }
}
