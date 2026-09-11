using System.Security.Cryptography.X509Certificates;
using iText.Bouncycastleconnector;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Fields.Properties;
using iText.Forms.Form.Element;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Signatures;

namespace mPdf.Poc.Signer.Signing;

public static class PadesSigner
{
    public static byte[] Sign(byte[] pdf, X509Certificate2 certificate, SignatureOptions options)
    {
        int existing = CountSignatures(pdf);
        if (options.Certify && existing > 0)
            throw new InvalidOperationException(
                "Não é possível certificar (DocMDP) um documento que já contém assinaturas.");

        var chain = BuildChain(certificate);
        var signature = new X509Certificate2Signature(certificate);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var reader = new PdfReader(input);
        var padesSigner = new PdfPadesSigner(reader, output);
        padesSigner.SetStampingProperties(new StampingProperties().UseAppendMode());

        var props = new SignerProperties().SetFieldName($"Assinatura{existing + 1}");
        if (options.Reason is not null) props.SetReason(options.Reason);
        if (options.Location is not null) props.SetLocation(options.Location);
        if (options.Certify)
            // Nível exigido pelo spec §5.3: formulários + novas assinaturas.
            props.SetCertificationLevel(AccessPermissions.FORM_FIELDS_MODIFICATION);
        if (options.Stamp is { } s)
        {
            // HIPÓTESE do brief: SignedAppearanceText em iText.Signatures — real: iText.Forms.Fields.Properties
            // (verificado via reflexão contra itext.forms.dll 9.7.0). Demais membros batem com o hipotetizado.
            var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            var appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID)
                .SetContent(new SignedAppearanceText()
                    .SetSignedBy(cn)
                    .SetReasonLine(options.Reason is null ? "" : $"Motivo: {options.Reason}")
                    .SetSignDate(DateTime.Now));
            props.SetPageNumber(s.PageNumber)
                 .SetPageRect(new Rectangle(s.X, s.Y, s.Width, s.Height))
                 .SetSignatureAppearance(appearance);
        }

        padesSigner.SignWithBaselineBProfile(props, chain, signature);
        return output.ToArray();
    }

    private static int CountSignatures(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return new SignatureUtil(doc).GetSignatureNames().Count;
    }

    /// Constrói a cadeia completa (folha -> raiz) — o ITI precisa da cadeia embutida na assinatura.
    private static IX509Certificate[] BuildChain(X509Certificate2 certificate)
    {
        var factory = BouncyCastleFactoryCreator.GetFactory();
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // cadeia só para embutir
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.Build(certificate);
        if (chain.ChainStatus.Any(s => s.Status.HasFlag(X509ChainStatusFlags.PartialChain)))
            throw new InvalidOperationException(
                "Não foi possível montar a cadeia completa do certificado (cadeia parcial). " +
                "Instale a cadeia da AC (ICP-Brasil) na máquina e tente novamente.");
        return chain.ChainElements
            .Select(e => factory.CreateX509Certificate(new MemoryStream(e.Certificate.RawData)))
            .ToArray();
    }
}
