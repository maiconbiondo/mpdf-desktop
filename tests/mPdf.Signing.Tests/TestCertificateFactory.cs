using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace mPdf.Signing.Tests;

/// Certificados EFÊMEROS gerados em memória — NUNCA toca o repositório real do Windows, NUNCA usa
/// certificado de usuário real. `CreateSelfSigned` ADAPTADO literalmente de
/// poc/mPdf.Poc.Signer.Tests/TestCertificateFactory.cs (exemplar aprovado no Marco 0).
public static class TestCertificateFactory
{
    public static X509Certificate2 CreateSelfSigned(string subjectCn = "Assinante Teste mPDF")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        // Reimporta como PFX para a chave privada ficar utilizável via GetRSAPrivateKey em qualquer provider
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), password: null,
            X509KeyStorageFlags.Exportable);
    }

    /// Certificado ECC (curva P-256) self-signed — usado SÓ para provar a recusa RSA-only
    /// (`GuardAgainstNonRsaCertificate`, `PadesSigningEngine.Sign`). Mesma mecânica de
    /// `CreateSelfSigned` (CertificateRequest + reimport PFX), trocando RSA por ECDsa.
    public static X509Certificate2 CreateEccSelfSigned(string subjectCn = "Assinante ECC Teste mPDF")
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN={subjectCn}", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), password: null,
            X509KeyStorageFlags.Exportable);
    }

    /// Certificado RSA cujo CN segue a convenção ICP-Brasil "NOME:CPF|CNPJ" (Leiaute RFB v4.1,
    /// §2.1.12/3.1.12) — usado só para provar `SignatureReader`/`SplitNameAndDocument` (ver HIPÓTESE
    /// em SignatureReader.cs). NÃO é um certificado ICP-Brasil real (self-signed, sem cadeia AC-RFB),
    /// só imita a CONVENÇÃO de nomenclatura do Subject/CN pra testar o parser isoladamente.
    public static X509Certificate2 CreateSelfSignedWithDocumentSuffix(string cn) => CreateSelfSigned(cn);

    /// Certificado cuja chave PÚBLICA é RSA, mas cuja chave PRIVADA não está acessível — usado só para
    /// provar a 2ª metade da recusa em `GuardAgainstNonRsaCertificate` (revisão I1): reimporta um
    /// certificado RSA normal SEM a parte privada (`X509ContentType.Cert`, não `.Pfx`), reproduzindo o
    /// caminho mais comum na prática (token A3 removido, PIN recusado, certificado importado sem
    /// `Exportable`) sem precisar de hardware nem tocar o repositório real do Windows.
    /// `GetRSAPublicKey()` continua não-nulo (é RSA); `GetRSAPrivateKey()` é `null` (sem chave privada).
    public static X509Certificate2 CreateRsaPublicKeyOnlyCertificate(
        string subjectCn = "Assinante RSA Sem Chave Privada mPDF")
    {
        using var full = CreateSelfSigned(subjectCn);
        return X509CertificateLoader.LoadCertificate(full.Export(X509ContentType.Cert));
    }

    /// Certificado RSA EXPIRADO (`NotAfter` no passado) — usado só para provar o filtro de expiração de
    /// `CertificateCatalog.ListSigningCertificates` (Task 2, Plano 4). Mesma mecânica de
    /// `CreateSelfSigned`, só com a janela de validade inteira no passado (nunca `NotBefore` no futuro,
    /// que produziria "ainda não válido" em vez de "expirado" — casos diferentes, este método cobre só
    /// o segundo).
    public static X509Certificate2 CreateExpiredRsaSelfSigned(string subjectCn = "Assinante RSA Expirado mPDF")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(-1));
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), password: null,
            X509KeyStorageFlags.Exportable);
    }

    /// Certificado RSA válido cuja extensão KeyUsage está PRESENTE mas sem o bit `DigitalSignature`
    /// marcado (só `KeyEncipherment`) — usado só para provar o filtro de uso de chave de
    /// `CertificateCatalog.ListSigningCertificates` (Task 2, Plano 4): a extensão AUSENTE não exclui
    /// (RFC 5280 só restringe quando presente), então este fixture precisa da extensão presente e
    /// deliberadamente sem o bit certo, não apenas omiti-la.
    public static X509Certificate2 CreateRsaSelfSignedWithoutDigitalSignatureUsage(
        string subjectCn = "Assinante RSA Sem Uso DigitalSignature mPDF")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), password: null,
            X509KeyStorageFlags.Exportable);
    }

    /// Certificado RSA válido cuja extensão KeyUsage marca SÓ `NonRepudiation` (sem `DigitalSignature`)
    /// — usado para provar que `CertificateCatalog.HasSigningKeyUsage` (revisão do coordenador, Task 2)
    /// também aceita esse bit: algumas ACs ICP-Brasil marcam o uso de assinatura como `nonRepudiation`,
    /// às vezes SEM `digitalSignature` — sem aceitar os dois bits, um certificado assim sumia
    /// silenciosamente do catálogo.
    public static X509Certificate2 CreateRsaSelfSignedWithNonRepudiationOnlyUsage(
        string subjectCn = "Assinante RSA NonRepudiation mPDF")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.NonRepudiation, critical: true));
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), password: null,
            X509KeyStorageFlags.Exportable);
    }

    /// Certificado RSA válido SEM a extensão KeyUsage (omitida por completo — não só sem o bit certo)
    /// — usado para provar o comportamento de "ausência passa" documentado em
    /// `CertificateCatalog.HasSigningKeyUsage` (RFC 5280 §4.2.1.3: a extensão só restringe o uso
    /// quando presente). Mesma mecânica de `CreateSelfSigned`, sem adicionar a extensão nenhuma.
    public static X509Certificate2 CreateRsaSelfSignedWithoutKeyUsageExtension(
        string subjectCn = "Assinante RSA Sem Extensao KeyUsage mPDF")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), password: null,
            X509KeyStorageFlags.Exportable);
    }

    /// Cadeia efêmera de 2 níveis (AC intermediária -> folha), NENHUM dos dois certificados instalado
    /// em lugar algum — usada só pra provar o MECANISMO de resolução de cadeia via `ChainPolicy.
    /// ExtraStore` (revisão I5, `SignatureReaderTests.
    /// X509Chain_WithExtraStore_ResolvesIntermediateInsteadOfPartialChain`). Nenhum dos 2 certificados
    /// precisa de chave privada utilizável no retorno — `X509Chain.Build` só inspeciona campos
    /// PÚBLICOS do certificado, então os dois saem reimportados só com a parte pública
    /// (`X509ContentType.Cert`), mesmo espírito de `CreateRsaPublicKeyOnlyCertificate` acima. Mesma
    /// mecânica de `poc/mPdf.Poc.Signer.Tests/TestCertificateFactory.CreateLeafWithUnresolvableChain`
    /// (folha assinada por uma AC que nunca é instalada/exposta em lugar nenhum), estendida pra também
    /// devolver a AC (que o PoC deliberadamente descartava) — aqui ela é o material que o teste entrega
    /// como `ExtraStore`, não algo que precisa estar no repositório do Windows.
    public static (X509Certificate2 Leaf, X509Certificate2 Ca) CreateTwoLevelEphemeralChain()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=AC Teste Intermediaria mPDF (nunca instalada)", caKey, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0,
                critical: true));
        caRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, critical: true));
        using var caWithKey = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var ca = X509CertificateLoader.LoadCertificate(caWithKey.Export(X509ContentType.Cert));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            "CN=Assinante Cadeia Dois Niveis mPDF", leafKey, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var serial = Guid.NewGuid().ToByteArray();
        using var leafPublicOnly = leafRequest.Create(
            caWithKey.SubjectName,
            X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1),
            serial);
        var leaf = X509CertificateLoader.LoadCertificate(leafPublicOnly.Export(X509ContentType.Cert));

        return (leaf, ca);
    }
}
