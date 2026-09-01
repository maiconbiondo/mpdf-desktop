using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace mPdf.Poc.Signer.Tests;

public class TestCertificateFactoryTests
{
    [Fact] // certificado gerado precisa ter chave privada (é ela que assina)
    public void CreateSelfSigned_HasPrivateKey()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        Assert.True(cert.HasPrivateKey);
    }

    [Fact] // KeyUsage precisa permitir assinatura digital
    public void CreateSelfSigned_HasDigitalSignatureKeyUsage()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var ku = cert.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(ku.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
    }

    [Fact] // CN configurável aparece no Subject
    public void CreateSelfSigned_SubjectContainsCn()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned("Fulano de Tal");
        Assert.Contains("CN=Fulano de Tal", cert.Subject);
    }
}
