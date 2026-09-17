using iText.Kernel.Pdf;
using iText.Signatures;

namespace mPdf.Poc.Signer.Signing;

public static class SignatureVerifier
{
    public static IReadOnlyList<SignatureInfo> Verify(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var util = new SignatureUtil(doc);
        var result = new List<SignatureInfo>();
        foreach (var name in util.GetSignatureNames())
        {
            var pkcs7 = util.ReadSignatureData(name);
            var subFilter = util.GetSignatureDictionary(name)
                .GetAsName(PdfName.SubFilter).GetValue();
            // Confirmado via reflexão sobre itext.sign.dll 9.7.0 (net461/netstandard2.0):
            // CertificateInfo.GetSubjectFields(IX509Certificate) -> X500Name.GetField(string) -> string?
            // PdfPKCS7.GetSigningCertificate() -> IX509Certificate (bate com o parâmetro acima).
            var signerCn = iText.Signatures.CertificateInfo
                .GetSubjectFields(pkcs7.GetSigningCertificate()).GetField("CN") ?? "";
            bool integrity;
            try { integrity = pkcs7.VerifySignatureIntegrityAndAuthenticity(); }
            catch { integrity = false; } // criptografia ilegível conta como quebrada, nunca como válida
            result.Add(new SignatureInfo(
                name, signerCn, subFilter, integrity,
                util.SignatureCoversWholeDocument(name),
                pkcs7.GetSignDate() == DateTime.MinValue ? null : pkcs7.GetSignDate()));
        }
        return result;
    }
}
