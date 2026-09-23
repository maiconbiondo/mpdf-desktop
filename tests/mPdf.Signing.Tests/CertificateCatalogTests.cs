using System.Security.Cryptography.X509Certificates;

namespace mPdf.Signing.Tests;

public class CertificateCatalogTests
{
    // Fake em memória de IX509StoreReader — NUNCA toca o repositório real do Windows. Seam exposto
    // via InternalsVisibleTo (ver src/mPdf.Signing/AssemblyInfo.cs), mesmo padrão já usado por
    // DocumentSession.HandleReplaceFailure (mPdf.Documents).
    private sealed class FakeX509StoreReader(params X509Certificate2[] certs) : IX509StoreReader
    {
        public X509Certificate2Collection Read() => [.. certs];
    }

    [Fact]
    public void ListSigningCertificates_RsaValidCertificate_IsIncludedClassifiedAsRsaWithFormattedDisplayName()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano De Tal");

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        var info = Assert.Single(result);
        Assert.Equal(cert.Thumbprint, info.Certificate.Thumbprint);
        Assert.True(info.IsRsa);
        var expectedValidade = cert.NotAfter.ToString("MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"Fulano De Tal — Fulano De Tal — válido até {expectedValidade}", info.DisplayName);
    }

    [Fact]
    public void ListSigningCertificates_ExpiredCertificate_IsExcluded()
    {
        using var cert = TestCertificateFactory.CreateExpiredRsaSelfSigned();

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        Assert.Empty(result);
    }

    [Fact]
    public void ListSigningCertificates_CertificateWithoutPrivateKey_IsExcluded()
    {
        using var cert = TestCertificateFactory.CreateRsaPublicKeyOnlyCertificate();

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        Assert.Empty(result);
    }

    [Fact]
    public void ListSigningCertificates_CertificateWithoutDigitalSignatureUsage_IsExcluded()
    {
        using var cert = TestCertificateFactory.CreateRsaSelfSignedWithoutDigitalSignatureUsage();

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        Assert.Empty(result);
    }

    [Fact] // MUST FAIL antes da revisão do coordenador: algumas ACs ICP-Brasil marcam o uso de
    // assinatura como nonRepudiation, às vezes SEM digitalSignature — sem aceitar os dois bits, um
    // certificado assim sumia silenciosamente do catálogo.
    public void ListSigningCertificates_NonRepudiationOnlyUsage_IsIncluded()
    {
        using var cert = TestCertificateFactory.CreateRsaSelfSignedWithNonRepudiationOnlyUsage();

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        Assert.Single(result);
    }

    [Fact] // INVARIANTE (pin, não muda com a revisão): extensão KeyUsage AUSENTE por completo (não só
    // sem o bit certo) -> passa (RFC 5280 §4.2.1.3: só restringe quando presente). Até agora só a
    // prosa do código documentava isso — nenhum fixture provava.
    public void ListSigningCertificates_NoKeyUsageExtensionAtAll_IsIncluded()
    {
        using var cert = TestCertificateFactory.CreateRsaSelfSignedWithoutKeyUsageExtension();

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        Assert.Single(result);
    }

    [Fact] // MUST FAIL antes da revisão do coordenador: certificados REJEITADOS pelo filtro (expirado,
    // sem chave privada, sem uso de assinatura) são NOSSOS pra descartar — só o que volta no
    // resultado pertence ao chamador. Sem dispose explícito, cada enumeração vazava 1 handle nativo
    // por certificado rejeitado (comum: repositórios reais acumulam certificados antigos/expirados).
    public void ListSigningCertificates_RejectedCertificates_AreDisposed()
    {
        var expired = TestCertificateFactory.CreateExpiredRsaSelfSigned();
        var noPrivateKey = TestCertificateFactory.CreateRsaPublicKeyOnlyCertificate();
        var wrongUsage = TestCertificateFactory.CreateRsaSelfSignedWithoutDigitalSignatureUsage();
        using var kept = TestCertificateFactory.CreateSelfSigned();

        CertificateCatalog.ListSigningCertificates(
            new FakeX509StoreReader(expired, noPrivateKey, wrongUsage, kept));

        Assert.Equal(IntPtr.Zero, expired.Handle);
        Assert.Equal(IntPtr.Zero, noPrivateKey.Handle);
        Assert.Equal(IntPtr.Zero, wrongUsage.Handle);
        Assert.NotEqual(IntPtr.Zero, kept.Handle); // mantido pertence ao CHAMADOR — não é nosso pra descartar
    }

    [Fact] // ECC continua LISTADO (nunca escondido) — só marcado IsRsa=false; desabilitar na UI é
    // decisão da Task 3, não deste catálogo.
    public void ListSigningCertificates_EccCertificate_IsListedButNotRsa()
    {
        using var cert = TestCertificateFactory.CreateEccSelfSigned();

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        var info = Assert.Single(result);
        Assert.False(info.IsRsa);
    }

    [Fact] // Convenção do CN "NOME:CPF" (11 dígitos, Leiaute RFB v4.1 §2.1.12) — mesma regra já usada
    // por SignatureReader.SplitNameAndDocument, reaplicada aqui pra classificar o TIPO do certificado.
    public void ListSigningCertificates_CnWithCpfSuffix_ClassifiedAsIcpBrasilPersonalWithECpfDisplayName()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano De Tal:12345678901");

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        var info = Assert.Single(result);
        Assert.True(info.IsIcpBrasilPersonal);
        Assert.False(info.IsIcpBrasilCompany);
        // Narrow ao PREFIXO que a classificação produz (nome + tipo) — não ao DisplayName inteiro:
        // este fixture é self-signed (emissor == subject), então o CN cru (com o sufixo ":CPF")
        // aparece de novo no trecho "emissor" por artefato do fixture, nunca por vazamento da lógica
        // de classificação em si (que só recorta o sufixo do NOME do assinante). Certificados
        // ICP-Brasil reais nunca têm essa duplicação: o emissor é o nome da AC, sem sufixo de CPF/CNPJ.
        Assert.StartsWith("Fulano De Tal (e-CPF) — ", info.DisplayName);
    }

    [Fact] // Convenção do CN "NOME:CNPJ" (14 dígitos, Leiaute RFB v4.1 §3.1.12).
    public void ListSigningCertificates_CnWithCnpjSuffix_ClassifiedAsIcpBrasilCompanyWithECnpjDisplayName()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Empresa Fulano Ltda:12345678000199");

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        var info = Assert.Single(result);
        Assert.True(info.IsIcpBrasilCompany);
        Assert.False(info.IsIcpBrasilPersonal);
        Assert.Contains("Empresa Fulano Ltda (e-CNPJ)", info.DisplayName);
    }

    [Fact] // certificado sem a convenção ICP-Brasil de CN (efêmero de teste, outra PKI) -> os dois
    // sinalizadores ficam false e o DisplayName não ganha o parêntese de tipo.
    public void ListSigningCertificates_CnWithoutDocumentSuffix_IsNotClassifiedAsIcpBrasil()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Assinante Sem Sufixo mPDF");

        var result = CertificateCatalog.ListSigningCertificates(new FakeX509StoreReader(cert));

        var info = Assert.Single(result);
        Assert.False(info.IsIcpBrasilPersonal);
        Assert.False(info.IsIcpBrasilCompany);
        Assert.DoesNotContain("(e-CPF)", info.DisplayName);
        Assert.DoesNotContain("(e-CNPJ)", info.DisplayName);
    }

    // --- Integração READ-ONLY contra o repositório REAL do Windows ------------------------------

    [Fact] // NUNCA instala nada — só lê CurrentUser\My (ver WindowsX509StoreReader). Asserções
    // ESTRUTURAIS apenas: não depende de QUAIS certificados existem nesta máquina (roda verde mesmo
    // num repositório vazio) e não loga NENHUM dado dos certificados reais do usuário (CN/CPF/CNPJ)
    // — só contagens e predicados booleanos, nunca os valores em si.
    public void ListSigningCertificates_RealWindowsStore_ReturnsOnlyNonExpiredCertificatesWithPrivateKey()
    {
        var result = CertificateCatalog.ListSigningCertificates();

        Assert.All(result, c => Assert.True(c.Certificate.HasPrivateKey));
        Assert.All(result, c => Assert.True(c.Certificate.NotAfter > DateTime.Now));
        Assert.All(result, c => Assert.False(string.IsNullOrWhiteSpace(c.DisplayName)));
    }
}
