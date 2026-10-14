using System.IO;
using System.Linq;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using mPdf.Editing;
using Xunit;

namespace mPdf.Signing.Tests;

/// Revisão crítica F1, item E6: página com /Rotate fora de múltiplo de 90 (malformada). O /Rect do carimbo usa a
/// regra do PDFium (135 → exibe 90; −135 → 270; 45 → 0; 315 → 270), mas o iText gira a APARÊNCIA do campo com o
/// próprio `GetRotation()` (`% 360`; troca os eixos se `(r/90)` é ímpar e só gira em 90/180/270 exatos) — carimbo
/// deitado (135) ou espremido (−135). O iText não dá ponto de extensão seguro para isso (PdfPadesSigner esconde o
/// campo; mudar o /Rotate da página alteraria o documento), então: carimbo VISÍVEL (padrão ou rubrica) nessas páginas
/// é recusado com mensagem pt-BR; assinatura INVISÍVEL continua aceita e íntegra. Múltiplos de 90: intocados.
public class StampRotacaoForaDoPadraoTests
{
    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();

    private static byte[] Pagina(int rotate)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
            doc.AddNewPage(PageSize.A4).GetPdfObject().Put(PdfName.Rotate, new PdfNumber(rotate));
        return ms.ToArray();
    }

    private static readonly byte[] RubricaPng = System.Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    [Theory]
    [InlineData(135)]
    [InlineData(-135)]
    [InlineData(45)]
    [InlineData(315)]
    public void CarimboVisivel_RotacaoForaDoPadrao_RecusaAmigavel(int rotate)
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        foreach (var img in new[] { null, RubricaPng })
        {
            var stamp = new VisibleStampSpec(0, new PdfQuad(100, 100, 350, 250), img);
            // Revisão F2 (A8): tipada (`PdfStampRotationException : PdfSigningException`) para o lote orientar diferente.
            var ex = Assert.Throws<PdfStampRotationException>(() =>
                Engine.Sign(new SignRequest(Pagina(rotate), cert, "Aprovacao", "Local", stamp, null)));
            Assert.Equal(0, ex.PageIndex);
            Assert.Equal(rotate, ex.RotationDegrees);
            Assert.Contains("rotação", ex.Message);
            Assert.Contains("invisível", ex.Message);
        }
    }

    [Theory]
    [InlineData(135)]
    [InlineData(-135)]
    [InlineData(45)]
    [InlineData(315)]
    public void AssinaturaInvisivel_RotacaoForaDoPadrao_Integra(int rotate)
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        var signed = Engine.Sign(new SignRequest(Pagina(rotate), cert, "Aprovacao", "Local", null, null));
        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);
    }

    [Theory] // controle: múltiplos de 90 (inclusive fora de 0..359) continuam assinando com carimbo
    [InlineData(-90)]
    [InlineData(450)]
    [InlineData(180)]
    public void CarimboVisivel_MultiploDe90_Assina(int rotate)
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        var stamp = new VisibleStampSpec(0, new PdfQuad(100, 100, 350, 250));
        var signed = Engine.Sign(new SignRequest(Pagina(rotate), cert, "Aprovacao", "Local", stamp, null));
        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);
    }

    [Fact] // a recusa olha só a página DO CARIMBO: outra página malformada não impede
    public void CarimboEmOutraPagina_Assina()
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            doc.AddNewPage(PageSize.A4);
            doc.AddNewPage(PageSize.A4).GetPdfObject().Put(PdfName.Rotate, new PdfNumber(135));
        }
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        var signed = Engine.Sign(new SignRequest(ms.ToArray(), cert, null, null,
            new VisibleStampSpec(0, new PdfQuad(100, 100, 350, 250)), null));
        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);
    }
}
