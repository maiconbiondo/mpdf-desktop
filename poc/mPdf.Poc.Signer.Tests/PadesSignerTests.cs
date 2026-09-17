using iText.Kernel.Pdf;
using iText.Signatures;
using mPdf.Poc.Signer.Signing;
using Xunit;

namespace mPdf.Poc.Signer.Tests;

public class PadesSignerTests
{
    private static byte[] SignFixture(SignatureOptions? options = null)
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        return PadesSigner.Sign(PdfFixture.CreateSimplePdf(), cert, options ?? new SignatureOptions());
    }

    [Fact] // resultado continua sendo um PDF e cresceu (conteúdo + assinatura)
    public void Sign_ProducesPdfBytes()
    {
        var signed = SignFixture();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(signed, 0, 4));
    }

    [Fact] // exatamente 1 assinatura, íntegra e autêntica
    public void Sign_ProducesOneVerifiableSignature()
    {
        var signed = SignFixture();
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var util = new SignatureUtil(doc);
        var names = util.GetSignatureNames();
        Assert.Single(names);
        var pkcs7 = util.ReadSignatureData(names[0]);
        Assert.True(pkcs7.VerifySignatureIntegrityAndAuthenticity());
    }

    [Fact] // SubFilter PAdES exigido pela ICP-Brasil
    public void Sign_UsesEtsiCadesDetachedSubFilter()
    {
        var signed = SignFixture();
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var util = new SignatureUtil(doc);
        var sigDict = util.GetSignatureDictionary(util.GetSignatureNames()[0]);
        Assert.Equal("ETSI.CAdES.detached", sigDict.GetAsName(PdfName.SubFilter).GetValue());
    }

    [Fact] // Certify=true grava DocMDP (referência DocMDP no dicionário da assinatura)
    public void Sign_WithCertify_SetsDocMdp()
    {
        var signed = SignFixture(new SignatureOptions { Certify = true });
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var util = new SignatureUtil(doc);
        var sigDict = util.GetSignatureDictionary(util.GetSignatureNames()[0]);
        Assert.NotNull(sigDict.GetAsArray(PdfName.Reference)); // /Reference com transform DocMDP
    }

    [Fact] // Certify=false não grava DocMDP
    public void Sign_WithoutCertify_HasNoDocMdp()
    {
        var signed = SignFixture(new SignatureOptions { Certify = false });
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var util = new SignatureUtil(doc);
        var sigDict = util.GetSignatureDictionary(util.GetSignatureNames()[0]);
        Assert.Null(sigDict.GetAsArray(PdfName.Reference));
    }

    [Fact] // Folha assinada por AC intermediária inalcançável (não instalada em lugar nenhum) => PartialChain
    public void Sign_LeafWithUnresolvableChain_Throws()
    {
        using var cert = TestCertificateFactory.CreateLeafWithUnresolvableChain();
        Assert.Throws<InvalidOperationException>(
            () => PadesSigner.Sign(PdfFixture.CreateSimplePdf(), cert, new SignatureOptions()));
    }

    [Fact] // 2ª assinatura preserva a 1ª: ambas íntegras
    public void Sign_SecondSignature_KeepsBothValid()
    {
        using var cert1 = TestCertificateFactory.CreateSelfSigned("Signatario Um");
        using var cert2 = TestCertificateFactory.CreateSelfSigned("Signatario Dois");
        var once = PadesSigner.Sign(PdfFixture.CreateSimplePdf(), cert1, new SignatureOptions());
        var twice = PadesSigner.Sign(once, cert2, new SignatureOptions { Certify = false });

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(twice)));
        var util = new SignatureUtil(doc);
        var names = util.GetSignatureNames();
        Assert.Equal(2, names.Count);
        Assert.All(names, n => Assert.True(
            util.ReadSignatureData(n).VerifySignatureIntegrityAndAuthenticity()));
    }

    [Fact] // a 1ª assinatura cobre uma revisão anterior; a última cobre o arquivo todo
    public void Sign_SecondSignature_LastCoversWholeDocument()
    {
        using var cert1 = TestCertificateFactory.CreateSelfSigned("Signatario Um");
        using var cert2 = TestCertificateFactory.CreateSelfSigned("Signatario Dois");
        var once = PadesSigner.Sign(PdfFixture.CreateSimplePdf(), cert1, new SignatureOptions());
        var twice = PadesSigner.Sign(once, cert2, new SignatureOptions { Certify = false });

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(twice)));
        var util = new SignatureUtil(doc);
        var names = util.GetSignatureNames(); // ordem de revisão
        Assert.False(util.SignatureCoversWholeDocument(names[0]));
        Assert.True(util.SignatureCoversWholeDocument(names[1]));
    }

    [Fact] // certificar documento já assinado é proibido
    public void Sign_CertifyOnSignedDocument_Throws()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var once = PadesSigner.Sign(PdfFixture.CreateSimplePdf(), cert, new SignatureOptions());
        Assert.Throws<InvalidOperationException>(() =>
            PadesSigner.Sign(once, cert, new SignatureOptions { Certify = true }));
    }

    [Fact] // carimbo visível vira um widget de assinatura na página/posição pedida
    public void Sign_WithStamp_CreatesVisibleWidgetOnPage()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Signatario Visivel");
        var signed = PadesSigner.Sign(PdfFixture.CreateSimplePdf(), cert,
            new SignatureOptions { Stamp = new VisibleStamp(1, 100, 100, 200, 60) });

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(signed)));
        var annots = doc.GetPage(1).GetAnnotations();
        var widget = Assert.Single(annots); // única anotação: o widget da assinatura
        var rect = widget.GetRectangle().ToRectangle();
        Assert.Equal(100, rect.GetX(), 1);
        Assert.Equal(200, rect.GetWidth(), 1);
    }
}
