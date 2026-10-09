using System.Text;
using mPdf.Documents;

namespace mPdf.Documents.Tests;

public class PdfEnvelopeTests
{
    private static byte[] Pdf => Fixtures.A4(); // tests/mPdf.Documents.Tests/Fixtures.cs
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static string B64 => Convert.ToBase64String(Pdf);

    [Fact] public void PlainPdf_NotWrapped() => Assert.False(PdfEnvelope.TryUnwrap(Pdf, out _));
    [Fact] public void QuotedBase64_Unwraps() { Assert.True(PdfEnvelope.TryUnwrap(Ascii("\"" + B64 + "\""), out var p)); Assert.Equal(Pdf, p); }
    [Fact] public void BareBase64_Unwraps() { Assert.True(PdfEnvelope.TryUnwrap(Ascii(B64), out var p)); Assert.Equal(Pdf, p); }
    [Fact] public void DataUri_Unwraps() { Assert.True(PdfEnvelope.TryUnwrap(Ascii("data:application/pdf;base64," + B64), out var p)); Assert.Equal(Pdf, p); }
    [Fact] public void JsonEscapedSlashes_Unwraps() { Assert.True(PdfEnvelope.TryUnwrap(Ascii("\"" + B64.Replace("/", "\\/") + "\""), out var p)); Assert.Equal(Pdf, p); }
    [Fact] public void LineBreaksAndBom_Unwraps()
    {
        var withBreaks = string.Join("\r\n", Enumerable.Range(0, (B64.Length + 75) / 76).Select(i => B64.Substring(i * 76, Math.Min(76, B64.Length - i * 76))));
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Ascii(withBreaks + "\n")).ToArray();
        Assert.True(PdfEnvelope.TryUnwrap(bytes, out var p)); Assert.Equal(Pdf, p);
    }
    [Fact] public void JsonLiteralNewlines_Unwraps() { Assert.True(PdfEnvelope.TryUnwrap(Ascii("\"" + B64.Insert(76, "\\n") + "\""), out var p)); Assert.Equal(Pdf, p); }
    [Fact] public void Base64OfNonPdf_NotUnwrapped() => Assert.False(PdfEnvelope.TryUnwrap(Ascii(Convert.ToBase64String(Encoding.ASCII.GetBytes("hello world, not a pdf"))), out _));
    [Fact] public void RandomText_NotUnwrapped() => Assert.False(PdfEnvelope.TryUnwrap(Ascii("isto nao e base64 nem pdf!"), out _));
    [Fact] public void Empty_NotUnwrapped() => Assert.False(PdfEnvelope.TryUnwrap(Array.Empty<byte>(), out _));

    [Theory]
    [InlineData("\\t")]
    public void JsonTab_Unwraps(string esc) { Assert.True(PdfEnvelope.TryUnwrap(Ascii("\"" + B64.Insert(76, esc) + "\""), out var p)); Assert.Equal(Pdf, p); }

    [Theory]
    [InlineData("\\u002F", "\\u002B")]
    [InlineData("\\u002f", "\\u002b")]
    public void JsonUnicodeEscapes_SlashAndPlus_Unwrap(string slash, string plus)
    {
        string e = B64.Replace("/", slash).Replace("+", plus);
        Assert.True(PdfEnvelope.TryUnwrap(Ascii("\"" + e + "\""), out var p)); Assert.Equal(Pdf, p);
    }

    [Fact] // CommitSigned grava o assinado em FilePath: o conserto pendente morre e nao sobrescreve
    public async Task CommitSigned_ClearsEnvelopeFix_AndFixIsNoOp()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-b64-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Ascii("\"" + B64 + "\""));
        try
        {
            using var s = await DocumentSession.OpenAsync(path);
            var signed = Fixtures.ThirtyPages(); // PDF valido distinto, faz o papel de "assinado"
            Assert.True(s.TryBeginEdit());
            try { s.CommitSigned(signed); } finally { s.EndEdit(); }
            Assert.False(s.NeedsEnvelopeFix);
            s.FixEnvelopeOnDisk();
            Assert.Equal(signed, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact] // abrir um arquivo embrulhado: sessão trabalha com o PDF real e sinaliza o conserto pendente
    public async Task OpenAsync_WrappedFile_OpensDecodedAndNeedsFix()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-b64-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Ascii("\"" + B64 + "\""));
        try
        {
            using var s = await DocumentSession.OpenAsync(path);
            Assert.Equal(Pdf, s.Snapshot);
            Assert.True(s.NeedsEnvelopeFix);
        }
        finally { File.Delete(path); }
    }

    [Fact] // consertar regrava o PDF real no disco e limpa o sinal
    public async Task FixEnvelopeOnDisk_RewritesFileAsPdf()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-b64-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Ascii("\"" + B64 + "\""));
        try
        {
            using var s = await DocumentSession.OpenAsync(path);
            int eventos = 0; s.NeedsEnvelopeFixChanged += (_, _) => eventos++;
            s.FixEnvelopeOnDisk();
            Assert.Equal(Pdf, File.ReadAllBytes(path));
            Assert.False(s.NeedsEnvelopeFix);
            Assert.Equal(1, eventos);
        }
        finally { File.Delete(path); }
    }

    [Fact] // PDF normal nunca pede conserto
    public void Open_PlainPdf_DoesNotNeedFix()
    {
        using var s = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        Assert.False(s.NeedsEnvelopeFix);
    }
}
