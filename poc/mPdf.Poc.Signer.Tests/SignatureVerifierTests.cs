using mPdf.Poc.Signer.Signing;
using Xunit;

namespace mPdf.Poc.Signer.Tests;

public class SignatureVerifierTests
{
    [Fact] // PDF sem assinatura -> lista vazia
    public void Verify_UnsignedPdf_ReturnsEmpty()
    {
        Assert.Empty(SignatureVerifier.Verify(PdfFixture.CreateSimplePdf()));
    }

    [Fact] // PDF com 2 assinaturas -> 2 relatórios íntegros com nomes dos signatários
    public void Verify_TwoSignatures_ReportsBoth()
    {
        using var cert1 = TestCertificateFactory.CreateSelfSigned("Signatario Um");
        using var cert2 = TestCertificateFactory.CreateSelfSigned("Signatario Dois");
        var signed = PadesSigner.Sign(
            PadesSigner.Sign(PdfFixture.CreateSimplePdf(), cert1, new SignatureOptions()),
            cert2, new SignatureOptions { Certify = false });

        var infos = SignatureVerifier.Verify(signed);
        Assert.Equal(2, infos.Count);
        Assert.All(infos, i => Assert.True(i.IntegrityOk));
        Assert.All(infos, i => Assert.Equal("ETSI.CAdES.detached", i.SubFilter));
        Assert.Contains(infos, i => i.SignerName.Contains("Signatario Um"));
        Assert.Contains(infos, i => i.SignerName.Contains("Signatario Dois"));
    }

    [Fact] // adulteração após assinar -> IntegrityOk=false (o verificador morde)
    public void Verify_TamperedPdf_ReportsBrokenIntegrity()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = PadesSigner.Sign(PdfFixture.CreateSimplePdf(), cert, new SignatureOptions());
        // corrompe 1 byte no meio do conteúdo assinado (fora do próprio blob da assinatura):
        var tampered = (byte[])signed.Clone();
        // offset 200 cai no conteúdo da 1ª revisão (dentro do ByteRange assinado, fora do /Contents) — assinatura em append preserva o prefixo
        tampered[200] ^= 0xFF;
        var infos = SignatureVerifier.Verify(tampered);
        // ou a leitura falha (PDF inválido -> lista vazia é INACEITÁVEL: deve lançar) ou reporta quebra
        Assert.Contains(infos, i => !i.IntegrityOk);
    }
}
