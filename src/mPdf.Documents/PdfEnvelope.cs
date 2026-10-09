using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace mPdf.Documents;

/// Arquivos ".pdf" que na verdade são o PDF codificado em Base64 (ex.: download do "Alvará Online" da
/// Prefeitura de Ponta Grossa: começa com "JVBERi0 e termina com "). Desembrulha sem perda.
public static class PdfEnvelope
{
    public const long MaxWrappedBytes = 512L * 1024 * 1024;
    private static readonly byte[] Header = "%PDF-"u8.ToArray();

    public static bool TryUnwrap(byte[] raw, [NotNullWhen(true)] out byte[]? pdf)
    {
        pdf = null;
        if (raw is null || raw.Length == 0 || raw.Length > MaxWrappedBytes) return false;
        if (HasPdfHeader(raw)) return false;

        var sb = new StringBuilder(raw.Length);
        int start = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
        for (int i = start; i < raw.Length; i++)
        {
            byte b = raw[i];
            if (b > 0x7E) return false;           // não-ASCII: não é Base64
            sb.Append((char)b);
        }
        string s = sb.ToString().Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        const string dataUri = "data:application/pdf;base64,";
        if (s.StartsWith(dataUri, StringComparison.OrdinalIgnoreCase)) s = s[dataUri.Length..];
        s = s.Replace("\\/", "/").Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
             .Replace("\\u002B", "+", StringComparison.OrdinalIgnoreCase)
             .Replace("\\n", "").Replace("\\r", "").Replace("\\t", "");
        var clean = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '\r' or '\n' or ' ' or '\t') continue;
            char n = c switch { '-' => '+', '_' => '/', _ => c };
            if (!(char.IsAsciiLetterOrDigit(n) || n is '+' or '/' or '=')) return false;
            clean.Append(n);
        }
        while (clean.Length % 4 != 0) clean.Append('=');
        var buffer = new byte[clean.Length * 3 / 4];
        if (!Convert.TryFromBase64String(clean.ToString(), buffer, out int written)) return false;
        var decoded = buffer.AsSpan(0, written).ToArray();
        if (!HasPdfHeader(decoded)) return false;
        pdf = decoded;
        return true;
    }

    private static bool HasPdfHeader(byte[] bytes) =>
        bytes.AsSpan(0, Math.Min(bytes.Length, 1024)).IndexOf(Header) >= 0;
}
