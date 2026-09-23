using System.Security.Cryptography.X509Certificates;
using mPdf.Editing;

namespace mPdf.Signing.Tests;

public class SignatureReaderTests
{
    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();

    [Fact]
    public void ReadSignatures_UnsignedPdf_ReturnsEmpty()
    {
        Assert.Empty(Engine.ReadSignatures(Fixtures.A4()));
    }

    [Fact] // bytes corrompidos -> PdfSigningException neutra, nunca um tipo iText cru
    public void ReadSignatures_CorruptBytes_ThrowsPdfSigningException()
    {
        var corrupt = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        Assert.Throws<PdfSigningException>(() => Engine.ReadSignatures(corrupt));
    }

    [Fact] // M4 (review 1) + revisão 2 item 1: PDF protegido por senha -> PdfPasswordRequiredException
    // tipada também no caminho de LEITURA (não só em Sign).
    public void ReadSignatures_PasswordProtectedPdf_ThrowsPdfPasswordRequiredException()
    {
        var encrypted = Fixtures.PasswordProtected();
        Assert.Throws<PdfPasswordRequiredException>(() => Engine.ReadSignatures(encrypted));
    }

    // --- Invariante central 6: compat com fixture-carimbo.pdf (self-signed antigo do PoC/Marco 0) ---

    [Fact]
    public void ReadSignatures_FixtureCarimbo_ReadsExistingPadesSignature()
    {
        var infos = Engine.ReadSignatures(Fixtures.Carimbo());
        var info = Assert.Single(infos);
        Assert.True(info.IntegrityValid);
        Assert.True(info.CoversWholeDocument);
        Assert.NotEmpty(info.SignerName);
    }

    // --- ChainTrustedWindows: sinal LOCAL auxiliar, nunca a validação oficial (ver Contract.cs) ------

    [Fact] // certificado efêmero self-signed NUNCA está no repositório de raízes confiáveis do
    // Windows — `false` é o valor HONESTO esperado aqui, não um bug; a validação oficial de uma
    // assinatura ICP-Brasil continua sendo a do ITI (validar.iti.gov.br), fora do escopo deste sinal.
    public void ReadSignatures_EphemeralSelfSignedCertificate_ChainTrustedWindowsIsFalse()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = Engine.Sign(new SignRequest(Fixtures.A4(), cert, null, null, null, null));

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.False(info.ChainTrustedWindows);
    }

    [Fact] // I5 (review) — MECANISMO isolado, BCL puro, SEM instalar nada em lugar nenhum: prova POR
    // QUE embutir os intermediários da assinatura como `ChainPolicy.ExtraStore` importa (ver
    // reconciliação completa em SignatureReader.IsChainTrustedByWindows). SEM ExtraStore, o elo
    // folha->AC intermediária (nunca instalada) fica PartialChain — a checagem nem chega a avaliar a
    // raiz. COM ExtraStore contendo a AC (o mesmo material que uma assinatura real embutiria via
    // PdfPKCS7.GetSignCertificateChain()), o elo resolve e a ÚNICA pendência que sobra é a raiz não
    // instalada (UntrustedRoot) — dependência que continua fora do escopo deste sinal por design. O
    // valor PÚBLICO `ChainTrustedWindows` continua `false` nos dois casos (ver teste acima); este teste
    // documenta e prova a distinção INTERNA que a correção resolve.
    public void X509Chain_WithExtraStore_ResolvesIntermediateInsteadOfPartialChain()
    {
        var (leaf, ca) = TestCertificateFactory.CreateTwoLevelEphemeralChain();
        using (leaf)
        using (ca)
        {
            using var withoutExtraStore = new X509Chain();
            withoutExtraStore.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            withoutExtraStore.Build(leaf);
            Assert.Contains(withoutExtraStore.ChainStatus,
                s => s.Status.HasFlag(X509ChainStatusFlags.PartialChain));

            using var withExtraStore = new X509Chain();
            withExtraStore.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            withExtraStore.ChainPolicy.ExtraStore.Add(ca);
            withExtraStore.Build(leaf);
            Assert.DoesNotContain(withExtraStore.ChainStatus,
                s => s.Status.HasFlag(X509ChainStatusFlags.PartialChain));
            // elo resolvido; ainda assim NÃO confiável (a AC nunca foi instalada) — dependência
            // documentada, não resolvida por este fix (nem deveria: instalar raízes/ACs é decisão do
            // gerente de TI, fora do escopo de um sinal local auxiliar).
            Assert.Contains(withExtraStore.ChainStatus,
                s => s.Status.HasFlag(X509ChainStatusFlags.UntrustedRoot));
        }
    }

    // --- Document (CPF/CNPJ): convenção CN "NOME:CPF|CNPJ" (Leiaute RFB v4.1 §2.1.12/3.1.12) --------

    [Fact]
    public void ReadSignatures_CnWithCpfSuffix_ExtractsDocumentAndStripsSuffixFromSignerName()
    {
        using var cert = TestCertificateFactory.CreateSelfSignedWithDocumentSuffix("JOAO DA SILVA:01672780838");
        var signed = Engine.Sign(new SignRequest(Fixtures.A4(), cert, null, null, null, null));

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Equal("JOAO DA SILVA", info.SignerName);
        Assert.Equal("01672780838", info.Document);
    }

    [Fact]
    public void ReadSignatures_CnWithCnpjSuffix_ExtractsDocumentAndStripsSuffixFromSignerName()
    {
        using var cert = TestCertificateFactory.CreateSelfSignedWithDocumentSuffix(
            "EMPRESA TESTE LTDA:12345678000199");
        var signed = Engine.Sign(new SignRequest(Fixtures.A4(), cert, null, null, null, null));

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Equal("EMPRESA TESTE LTDA", info.SignerName);
        Assert.Equal("12345678000199", info.Document);
    }

    [Fact] // certificado sem a convenção (ex.: os efêmeros "padrão" usados no resto da suíte) ->
    // Document nulo, SignerName é o CN cru — nunca quebra por causa da ausência do padrão.
    public void ReadSignatures_CnWithoutDocumentSuffix_DocumentIsNullSignerNameIsRawCn()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Assinante Sem Documento");
        var signed = Engine.Sign(new SignRequest(Fixtures.A4(), cert, null, null, null, null));

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Equal("Assinante Sem Documento", info.SignerName);
        Assert.Null(info.Document);
    }

    // --- Reason: escrito no pedido, lido de volta -----------------------------------------------

    [Fact]
    public void ReadSignatures_WithReason_RoundTrips()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = Engine.Sign(new SignRequest(Fixtures.A4(), cert, "Concordo com os termos", null, null, null));

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Equal("Concordo com os termos", info.Reason);
    }

    [Fact]
    public void ReadSignatures_WithoutReason_IsNull()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = Engine.Sign(new SignRequest(Fixtures.A4(), cert, null, null, null, null));

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Null(info.Reason);
    }

    // --- StampPageIndex/StampRect (Task 4, Plano 4): geometria do widget do carimbo visível, pro
    // clique-pra-navegar do painel de Assinaturas (DocumentViewModel.SelectSignature) -------------

    [Fact]
    public void ReadSignatures_WithVisibleStamp_ExposesStampPageIndexAndRect()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var stamp = new VisibleStampSpec(0, new PdfQuad(100, 100, 280, 160));
        var signed = Engine.Sign(new SignRequest(Fixtures.A4(), cert, null, null, stamp, null));

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Equal(0, info.StampPageIndex);
        Assert.Equal(new PdfQuad(100, 100, 280, 160), info.StampRect);
    }

    [Fact] // assinatura SEM carimbo visível (aprovação "invisível", caso mais comum na prática) -> os
    // 2 campos ficam null — sem widget geométrico pra apontar, o painel não oferece
    // clique-pra-navegar pra esta assinatura (ver DocumentViewModel.SelectSignature).
    public void ReadSignatures_WithoutStamp_StampPageIndexAndRectAreNull()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = Engine.Sign(new SignRequest(Fixtures.A4(), cert, null, null, null, null));

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Null(info.StampPageIndex);
        Assert.Null(info.StampRect);
    }

    [Fact] // compat: fixture-carimbo.pdf (PoC antigo, Marco 0) também tem carimbo visível — mesma
    // invariante central (ReadSignatures_FixtureCarimbo_ReadsExistingPadesSignature acima) cobrindo
    // os 2 campos novos também, não só os já existentes.
    public void ReadSignatures_FixtureCarimbo_ExposesStampGeometry()
    {
        var info = Assert.Single(Engine.ReadSignatures(Fixtures.Carimbo()));
        Assert.NotNull(info.StampPageIndex);
        Assert.NotNull(info.StampRect);
    }
}
