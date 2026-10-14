using System.Text;
using mPdf.Rendering;

namespace mPdf.Rendering.Tests;

/// Páginas giradas, Task 1: em página com /Rotate o Docnet devolve as caixas de caractere com os cantos
/// TROCADOS (sonda: 90° troca L/R — `'U' L=75 R=62`; 180° troca L/R e B/T; 270° troca B/T). O contrato de
/// `PdfCharacter` é Left≤Right, Bottom≤Top no frame exibido — `GetTextPage` normaliza (min/max). A prova
/// cruzada com os glifos do iText (posição exata por rotação) está em mPdf.Editing.Tests
/// (`DocnetRotationFrameTests`); aqui, sem iText, um PDF escrito à mão.
public class TextPageRotationTests
{
    /// PDF mínimo A4 com /Rotate `rot` e "HELLO" (Helvetica 24) horizontal no espaço do usuário em (60,100).
    internal static byte[] MinimalPdf(int rot)
    {
        const string content = "BT /F1 24 Tf 1 0 0 1 60 100 Tm (HELLO) Tj ET";
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Rotate {rot} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void GetTextPage_RotatedPage_CharBoxesAreNormalized(int rot)
    {
        using var r = new PdfDocumentRenderer(MinimalPdf(rot));
        var chars = r.GetTextPage(0).Characters.Where(c => !char.IsWhiteSpace(c.Char) && c.Char != '\0').ToList();

        Assert.Equal("HELLO", new string(chars.Select(c => c.Char).ToArray()));
        foreach (var c in chars)
        {
            Assert.True(c.LeftPt <= c.RightPt, $"rot={rot} '{c.Char}' L={c.LeftPt} > R={c.RightPt}");
            Assert.True(c.BottomPt <= c.TopPt, $"rot={rot} '{c.Char}' B={c.BottomPt} > T={c.TopPt}");
            Assert.True(c.RightPt - c.LeftPt > 0 && c.TopPt - c.BottomPt > 0, $"rot={rot} '{c.Char}' caixa vazia");
        }
    }

    [Fact] // posição em 90°: usuário (60,100) -> exibido (100, 595-60=535): o 'H' começa em X≈100, topo ≈535
    public void GetTextPage_Rot90_FirstGlyphAtDisplayedPosition()
    {
        using var r = new PdfDocumentRenderer(MinimalPdf(90));
        var h = r.GetTextPage(0).Characters.First(c => c.Char == 'H');
        Assert.InRange(h.LeftPt, 99, 103);   // baseline do glifo deitado = borda esquerda exibida
        Assert.InRange(h.TopPt, 530, 536);   // borda esquerda do 'H' no usuário (x≈60+bearing) -> topo exibido
    }
}
