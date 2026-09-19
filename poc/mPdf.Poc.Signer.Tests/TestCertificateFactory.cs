using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace mPdf.Poc.Signer.Tests;

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

    /// Gera um certificado folha assinado por uma AC intermediária que NÃO é retornada nem instalada
    /// em lugar algum — simula uma cadeia real cujo emissor é inalcançável para o X509Chain.Build.
    public static X509Certificate2 CreateLeafWithUnresolvableChain(string subjectCn = "Assinante Cadeia Incompleta")
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=AC Teste Intermediaria mPDF", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        caRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, critical: true));
        using var caCert = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        // caCert (a AC) deliberadamente não é retornada, instalada no store ou exposta de nenhuma forma:
        // o teste precisa que o emissor seja inalcançável para X509Chain.Build.

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            $"CN={subjectCn}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

        var serial = Guid.NewGuid().ToByteArray();
        using var leafCertPublicOnly = leafRequest.Create(
            caCert.SubjectName,
            X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1),
            serial);
        using var leafCert = leafCertPublicOnly.CopyWithPrivateKey(leafKey);

        // Reimporta como PFX para a chave privada ficar utilizável via GetRSAPrivateKey em qualquer provider
        return X509CertificateLoader.LoadPkcs12(
            leafCert.Export(X509ContentType.Pfx), password: null,
            X509KeyStorageFlags.Exportable);
    }
}
