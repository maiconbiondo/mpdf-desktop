using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using mPdf.Ocr;

namespace mPdf.Ocr.Tests;

/// TDD do motor Tesseract embarcado (Task 1, Plano 15). Gera um bitmap SINTÉTICO com texto conhecido
/// (uma frase pt + um trecho en) via System.Drawing e prova que o OCR:
///  - devolve PlainText contendo as palavras esperadas (asserção TOLERANTE a ruído: normalizada,
///    "contém", maioria — NUNCA igualdade exata, pois OCR tem ruído);
///  - devolve Words não-vazio, com caixas dentro do bitmap, em ordem de leitura (cima→baixo,
///    esq→dir);
///  - funciona a partir do OUTPUT (nativos x64 + tessdata resolvidos de AppContext.BaseDirectory) —
///    este teste roda do bin do projeto de teste, NÃO de `dotnet run` no módulo de produto, o que já
///    é a prova de empacotamento (o risco nº 1). Ver PROVA DE EMPACOTAMENTO abaixo, que afirma isso
///    explicitamente.
public sealed class TesseractOcrEngineTests
{
    // Duas linhas, palavras comuns e curtas (reconhecimento robusto do LSTM). Linha 1 pt, linha 2 en.
    private const string Linha1Pt = "Documento escaneado";
    private const string Linha2En = "Hello World";

    private static readonly string[] PalavrasEsperadas = { "documento", "escaneado", "hello", "world" };

    private static (byte[] bgra, int w, int h) RenderTextBitmap()
    {
        const int w = 1000, h = 320;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        bmp.SetResolution(300, 300);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(FontFamily.GenericSansSerif, 48f, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(Linha1Pt, font, Brushes.Black, new PointF(30f, 30f));
            g.DrawString(Linha2En, font, Brushes.Black, new PointF(30f, 150f));
        }

        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[w * h * 4];
            int stride = data.Stride;
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * stride, bgra, y * w * 4, w * 4);
            return (bgra, w, h);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue; // remove acentos
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else sb.Append(' ');
        }
        return sb.ToString();
    }

    [Fact]
    public void Recognize_SyntheticBitmap_PlainTextContainsExpectedWords_ToleranteARuido()
    {
        var (bgra, w, h) = RenderTextBitmap();
        using var engine = new TesseractOcrEngine();

        OcrEngineResult result = engine.Recognize(bgra, w, h, TesseractOcrEngine.DefaultLanguages);

        string norm = Normalize(result.PlainText);
        int achadas = PalavrasEsperadas.Count(p => norm.Contains(p, StringComparison.Ordinal));
        // Tolerância a ruído de OCR: exige a MAIORIA das palavras (≥3 de 4), nunca igualdade exata.
        Assert.True(achadas >= 3,
            $"OCR deveria conter a maioria das palavras esperadas. Achadas {achadas}/4. " +
            $"PlainText normalizado: '{norm.Trim()}'");
    }

    [Fact]
    public void Recognize_SyntheticBitmap_WordsNonEmpty_BoxesDentroDoBitmap()
    {
        var (bgra, w, h) = RenderTextBitmap();
        using var engine = new TesseractOcrEngine();

        OcrEngineResult result = engine.Recognize(bgra, w, h, TesseractOcrEngine.DefaultLanguages);

        Assert.NotEmpty(result.Words);
        foreach (var word in result.Words)
        {
            Assert.InRange(word.LeftPx, 0, w);
            Assert.InRange(word.TopPx, 0, h);
            Assert.InRange(word.LeftPx + word.WidthPx, 0, w);
            Assert.InRange(word.TopPx + word.HeightPx, 0, h);
            Assert.True(word.WidthPx > 0 && word.HeightPx > 0, "caixa de palavra deve ter área positiva");
            Assert.InRange(word.Confidence, 0f, 100f);
        }
    }

    [Fact]
    public void Recognize_SyntheticBitmap_WordsEmOrdemDeLeitura()
    {
        var (bgra, w, h) = RenderTextBitmap();
        using var engine = new TesseractOcrEngine();

        OcrEngineResult result = engine.Recognize(bgra, w, h, TesseractOcrEngine.DefaultLanguages);

        var norm = result.Words.Select(word => Normalize(word.Text).Trim()).ToList();
        int iDocumento = norm.IndexOf("documento");
        int iEscaneado = norm.IndexOf("escaneado");
        int iHello = norm.IndexOf("hello");

        // Âncoras robustas presentes (palavras fáceis); se faltarem, o motor/empacotamento falhou.
        Assert.True(iDocumento >= 0, $"'documento' não reconhecido. Palavras: {string.Join(",", norm)}");
        Assert.True(iHello >= 0, $"'hello' não reconhecido. Palavras: {string.Join(",", norm)}");

        // Ordem de leitura: linha 1 (esq→dir) antes da linha 2.
        Assert.True(iDocumento < iHello, "linha 1 (Documento) deve vir antes da linha 2 (Hello)");
        if (iEscaneado >= 0)
            Assert.True(iDocumento < iEscaneado, "'Documento' (esq) deve vir antes de 'escaneado' (dir) na linha 1");

        // Verificação geométrica da ordem: a caixa de 'hello' está ABAIXO da de 'documento'.
        var docBox = result.Words[iDocumento];
        var helloBox = result.Words[iHello];
        Assert.True(helloBox.TopPx > docBox.TopPx, "a linha 'Hello' deve estar abaixo de 'Documento' no bitmap");
    }

    /// PROVA DE EMPACOTAMENTO (risco nº 1: "funciona no dev, quebra no instalado"). Este teste roda a
    /// partir do OUTPUT do projeto de teste (bin/.../win-x64), não de `dotnet run` no módulo de
    /// produto. Afirma explicitamente que os nativos (x64/leptonica*.dll, x64/tesseract50.dll) E os
    /// dados (tessdata/*.traineddata) existem ao lado do assembly em execução — exatamente o layout
    /// que o publish self-contained e o instalador produzem — e são resolvidos de
    /// AppContext.BaseDirectory. Se o empacotamento quebrar (nativos/tessdata não copiados
    /// transitivamente), este teste falha ANTES de qualquer OCR.
    [Fact]
    public void Empacotamento_NativosETessdata_ResolvidosDeBaseDirectory()
    {
        string bas = AppContext.BaseDirectory;

        string tessdata = Path.Combine(bas, "tessdata");
        Assert.True(Directory.Exists(tessdata), $"pasta tessdata ausente no output: {tessdata}");
        Assert.True(File.Exists(Path.Combine(tessdata, "por.traineddata")), "por.traineddata ausente");
        Assert.True(File.Exists(Path.Combine(tessdata, "eng.traineddata")), "eng.traineddata ausente");

        string x64 = Path.Combine(bas, "x64");
        Assert.True(Directory.Exists(x64), $"pasta de nativos x64 ausente no output: {x64}");
        Assert.True(File.Exists(Path.Combine(x64, "tesseract50.dll")), "tesseract50.dll (nativo) ausente");
        Assert.True(
            Directory.EnumerateFiles(x64, "leptonica*.dll").Any(),
            "leptonica (nativo) ausente em x64/");

        // E de fato instancia o motor a partir desse layout (ctor resolve tessdata de BaseDirectory).
        using var engine = new TesseractOcrEngine();
        Assert.NotNull(engine);
    }
}
