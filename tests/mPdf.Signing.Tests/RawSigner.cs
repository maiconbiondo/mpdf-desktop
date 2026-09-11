using System.Security.Cryptography.X509Certificates;
using iText.Bouncycastleconnector;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Pdf;
using iText.Signatures;

namespace mPdf.Signing.Tests;

/// Sonda-nível: assina bytes CRUS com um `AccessPermissions` arbitrário (inclusive P=1/P=3, que
/// `DocMdpLevel`/`ISigningEngine.Sign` não têm como pedir por design — este app nunca EMITE esses
/// níveis). Extraído de
/// `FormFillIncrementalEngineTests.SignFormularioWithRawCertificationLevel`/`BuildChain` — revisão
/// final (I1 do `Sign` precisou do MESMO mecanismo pra fabricar um documento P=1 de TERCEIRO; 1
/// implementação compartilhada entre os 2 arquivos de teste em vez de duplicar de novo).
internal static class RawSigner
{
    public static byte[] SignWithCertificationLevel(
        byte[] pdf, X509Certificate2 cert, AccessPermissions level, string fieldName = "AssinaturaBruta1")
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var padesSigner = new PdfPadesSigner(new PdfReader(input), output);
        padesSigner.SetStampingProperties(new StampingProperties().UseAppendMode());
        var props = new SignerProperties().SetFieldName(fieldName).SetCertificationLevel(level);
        var chain = BuildChain(cert);
        var signature = new X509Certificate2Signature(cert);
        padesSigner.SignWithBaselineBProfile(props, chain, signature);
        return output.ToArray();
    }

    private static IX509Certificate[] BuildChain(X509Certificate2 certificate)
    {
        var factory = BouncyCastleFactoryCreator.GetFactory();
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.Build(certificate);
        return chain.ChainElements
            .Select(e => factory.CreateX509Certificate(new MemoryStream(e.Certificate.RawData)))
            .ToArray();
    }
}
