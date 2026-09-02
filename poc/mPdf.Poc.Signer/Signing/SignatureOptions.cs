namespace mPdf.Poc.Signer.Signing;

public sealed record VisibleStamp(int PageNumber, float X, float Y, float Width, float Height);

public sealed record SignatureOptions
{
    public bool Certify { get; init; } = true;
    public VisibleStamp? Stamp { get; init; }
    public string? Reason { get; init; }
    public string? Location { get; init; }
}
