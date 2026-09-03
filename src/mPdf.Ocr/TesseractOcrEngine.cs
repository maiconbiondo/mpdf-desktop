using Tesseract;

namespace mPdf.Ocr;

/// Implementação de <see cref="IOcrEngine"/> sobre o wrapper Tesseract (charlesw, Apache 2.0). Os
/// binários NATIVOS win-x64 (leptonica/tesseract) vêm do pacote NuGet (pasta x64/ ao lado do
/// assembly) e os dados de idioma (por+eng, tessdata_fast) da pasta `tessdata/` — ambos resolvidos a
/// partir de <see cref="AppContext.BaseDirectory"/>, portanto funcionam igualmente no dev e no app
/// PUBLICADO/instalado (nunca de um caminho de dev). SEM rede, SEM iText/Docnet.
///
/// Thread-safety: o <see cref="TesseractEngine"/> nativo NÃO é reentrante. Esta classe mantém um
/// cache de engines por idioma e serializa cada reconhecimento sob um lock — seguro para o uso
/// sequencial página-a-página da orquestração (uma página por vez).
public sealed class TesseractOcrEngine : IOcrEngine, IDisposable
{
    /// Idioma default do produto: português + inglês.
    public const string DefaultLanguages = "por+eng";

    private readonly string _tessdataPath;
    private readonly Dictionary<string, TesseractEngine> _engines = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    /// <param name="tessdataPath">Pasta que contém os arquivos `*.traineddata`. Se omitido, resolve
    /// `tessdata/` ao lado do assembly (AppContext.BaseDirectory) — o caminho válido tanto no build
    /// de dev quanto no publish self-contained.</param>
    public TesseractOcrEngine(string? tessdataPath = null)
    {
        _tessdataPath = tessdataPath ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!Directory.Exists(_tessdataPath))
            throw new DirectoryNotFoundException(
                $"Pasta tessdata não encontrada em '{_tessdataPath}'. Os dados de idioma do OCR " +
                "(por+eng) precisam ser copiados para o output/publish ao lado do executável.");
    }

    public OcrEngineResult Recognize(ReadOnlySpan<byte> bgra, int widthPx, int heightPx, string languages)
    {
        if (widthPx <= 0 || heightPx <= 0)
            throw new ArgumentException($"Dimensões inválidas: {widthPx}x{heightPx}.");
        long expected = (long)widthPx * heightPx * 4;
        if (bgra.Length < expected)
            throw new ArgumentException(
                $"Buffer BGRA curto: {bgra.Length} bytes para {widthPx}x{heightPx} (esperado {expected}).");
        if (string.IsNullOrWhiteSpace(languages)) languages = DefaultLanguages;

        // BGRA → BMP 24-bit em memória → Pix (Leptonica). Encapsular em BMP evita depender de
        // System.Drawing no módulo de produto e passa pelo carregador de imagem robusto do Leptonica.
        byte[] bmp = BgraToBmp24(bgra, widthPx, heightPx);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            TesseractEngine engine = GetOrCreateEngine(languages);
            using Pix pix = Pix.LoadFromMemory(bmp);
            using Page page = engine.Process(pix);

            var words = new List<OcrWord>();
            using (ResultIterator iter = page.GetIterator())
            {
                iter.Begin();
                do
                {
                    if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out Rect r)) continue;
                    string? text = iter.GetText(PageIteratorLevel.Word);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    float conf = iter.GetConfidence(PageIteratorLevel.Word);
                    words.Add(new OcrWord(text.Trim(), r.X1, r.Y1, r.Width, r.Height, conf));
                }
                while (iter.Next(PageIteratorLevel.Word));
            }

            string plain = page.GetText() ?? string.Empty;
            return new OcrEngineResult(words, plain);
        }
    }

    private TesseractEngine GetOrCreateEngine(string languages)
    {
        if (_engines.TryGetValue(languages, out var existing)) return existing;
        // LstmOnly: os dados tessdata_fast só trazem o modelo neural (LSTM); o modo legado
        // (TesseractOnly/Default) exigiria os dados do motor de padrões, ausentes na variante fast.
        var engine = new TesseractEngine(_tessdataPath, languages, EngineMode.LstmOnly);
        _engines[languages] = engine;
        return engine;
    }

    /// BGRA (top-down, B,G,R,A) → BMP 24-bit (BGR, bottom-up, linhas alinhadas a 4 bytes), com 300 DPI
    /// gravado no cabeçalho (o Tesseract usa a resolução para heurísticas de escala).
    private static byte[] BgraToBmp24(ReadOnlySpan<byte> bgra, int width, int height)
    {
        int rowSize = ((width * 3) + 3) & ~3;          // linha alinhada a 4 bytes
        int pixelDataSize = rowSize * height;
        const int fileHeader = 14, infoHeader = 40;
        int offset = fileHeader + infoHeader;
        byte[] bmp = new byte[offset + pixelDataSize];

        // BITMAPFILEHEADER
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        WriteI32(bmp, 2, bmp.Length);                   // tamanho do arquivo
        WriteI32(bmp, 10, offset);                      // offset dos pixels

        // BITMAPINFOHEADER
        WriteI32(bmp, 14, infoHeader);
        WriteI32(bmp, 18, width);
        WriteI32(bmp, 22, height);                      // positivo = bottom-up
        bmp[26] = 1;                                    // planes = 1
        bmp[28] = 24;                                   // bits por pixel
        // compression=0, já zerado
        WriteI32(bmp, 34, pixelDataSize);
        const int ppm300 = 11811;                       // 300 dpi ≈ 11811 pixels/metro
        WriteI32(bmp, 38, ppm300);
        WriteI32(bmp, 42, ppm300);

        // Pixels: BMP bottom-up — a última linha do bitmap-fonte vai primeiro no arquivo.
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * width * 4;                 // fonte top-down
            int dstRow = offset + (height - 1 - y) * rowSize;
            for (int x = 0; x < width; x++)
            {
                int s = srcRow + x * 4;
                int d = dstRow + x * 3;
                bmp[d] = bgra[s];                        // B
                bmp[d + 1] = bgra[s + 1];               // G
                bmp[d + 2] = bgra[s + 2];               // R (alfa descartado)
            }
        }
        return bmp;
    }

    private static void WriteI32(byte[] buf, int pos, int value)
    {
        buf[pos] = (byte)value;
        buf[pos + 1] = (byte)(value >> 8);
        buf[pos + 2] = (byte)(value >> 16);
        buf[pos + 3] = (byte)(value >> 24);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var engine in _engines.Values) engine.Dispose();
            _engines.Clear();
            _disposed = true;
        }
    }
}
