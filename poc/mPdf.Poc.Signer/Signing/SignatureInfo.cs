namespace mPdf.Poc.Signer.Signing;

public sealed record SignatureInfo(
    string FieldName, string SignerName, string SubFilter,
    bool IntegrityOk, bool CoversWholeDocument, DateTime? SigningTime);
