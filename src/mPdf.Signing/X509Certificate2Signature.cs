using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Kernel.Crypto;
using iText.Signatures;

namespace mPdf.Signing;

/// Ponte X509Certificate2 -> iText (ADAPTADO literalmente de
/// poc/mPdf.Poc.Signer/Signing/X509Certificate2Signature.cs, aprovado pelo ITI no Marco 0 — mecânica
/// criptográfica NÃO alterada, só namespace). GetRSAPrivateKey() usa CNG: funciona para A1
/// instalado/arquivo, token A3 (CSP/KSP do fabricante pede o PIN) e conectores de nuvem.
internal sealed class X509Certificate2Signature : IExternalSignature
{
    private readonly X509Certificate2 _cert;
    public X509Certificate2Signature(X509Certificate2 cert) => _cert = cert;

    public string GetDigestAlgorithmName() => DigestAlgorithms.SHA256;
    public string GetSignatureAlgorithmName() => "RSA";
    public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;

    public byte[] Sign(byte[] message)
    {
        // Revisão M5 (Plano 4 Task 1, review 1): `PadesSigningEngine.GuardAgainstNonRsaCertificate`
        // (revisão I1) já garante, ANTES de `PdfPadesSigner.SignWithBaselineBProfile` sequer chamar
        // este método, que `GetRSAPrivateKey()` não é nulo naquele instante. Revisão 2 (item 4):
        // `GetRSAPrivateKey()!` (null-forgiving) era otimista demais — entre o guard rodar e este
        // método ser chamado (a assinatura de fato, potencialmente após o driver do token pedir PIN
        // ao usuário) o token pode ser removido, expirar o acesso, ou qualquer outra janela de corrida
        // real com hardware — um `!` indevido viraria `NullReferenceException` NÃO tipada bem no meio
        // da operação. Restaurado um `?? throw` de verdade, agora com o tipo/mensagem NEUTROS do
        // módulo (`PdfSigningException`, não o `InvalidOperationException` genérico que o PoC usava) —
        // 2ª linha de defesa, não duplica a mensagem da 1ª checagem (`GuardAgainstNonRsaCertificate`
        // nomeia a política; esta nomeia o sintoma de acesso, mesma mensagem que a 2ª checagem de lá
        // já usa, reafirmada aqui pro caso desta janela de corrida).
        using var rsa = _cert.GetRSAPrivateKey()
            ?? throw new PdfSigningException(
                "Não foi possível acessar a chave privada do certificado (token removido, PIN " +
                "recusado ou permissão negada?).");
        return rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
