namespace mPdf.Ocr;

/// Resultado NEUTRO do OCR de UM bitmap. Tipo próprio do módulo mPdf.Ocr — não vaza Tesseract nem
/// conhece PDF. `PlainText` é o texto corrido reconhecido (para busca/diagnóstico); `Words` são as
/// palavras com caixa em pixels (para a raiz de composição mapear px→pt na camada invisível).
public sealed record OcrEngineResult(IReadOnlyList<OcrWord> Words, string PlainText);

/// Uma palavra reconhecida com sua caixa em PIXELS (origem topo-esquerda, na resolução do bitmap
/// dado ao motor) e a confiança do Tesseract (0–100).
public sealed record OcrWord(string Text, int LeftPx, int TopPx, int WidthPx, int HeightPx, float Confidence);
