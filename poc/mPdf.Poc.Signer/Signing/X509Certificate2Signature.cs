using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Kernel.Crypto;
using iText.Signatures;

namespace mPdf.Poc.Signer.Signing;

/// Ponte X509Certificate2 -> iText. GetRSAPrivateKey() usa CNG: funciona para
/// A1 instalado/arquivo, token A3 (CSP/KSP do fabricante pede o PIN) e conectores de nuvem.
internal sealed class X509Certificate2Signature : IExternalSignature
{
    private readonly X509Certificate2 _cert;
    public X509Certificate2Signature(X509Certificate2 cert) => _cert = cert;

    public string GetDigestAlgorithmName() => DigestAlgorithms.SHA256;
    public string GetSignatureAlgorithmName() => "RSA";
    public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;

    public byte[] Sign(byte[] message)
    {
        using var rsa = _cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException(
                "O certificado selecionado não tem chave privada RSA acessível.");
        return rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
