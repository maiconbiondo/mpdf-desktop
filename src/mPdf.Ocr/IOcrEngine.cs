namespace mPdf.Ocr;

/// Fronteira NEUTRA do reconhecimento óptico (OCR). Recebe um bitmap BGRA já pronto (origem
/// topo-esquerda, 4 bytes por pixel na ordem B,G,R,A) e devolve texto + caixas de palavra em PIXELS
/// na resolução do próprio bitmap. NENHUM tipo de Tesseract, iText, Docnet ou de rede cruza esta
/// interface — o motor é um detalhe de implementação (ver TesseractOcrEngine). A raiz de composição
/// (mPdf.App) é quem renderiza a página (mPdf.Rendering) e mapeia o resultado para a camada de texto
/// invisível (mPdf.Editing); este módulo não conhece PDF nenhum.
public interface IOcrEngine
{
    /// <param name="bgra">Bitmap da página em BGRA (B,G,R,A por pixel), origem topo-esquerda,
    /// comprimento esperado = widthPx * heightPx * 4.</param>
    /// <param name="widthPx">Largura do bitmap em pixels.</param>
    /// <param name="heightPx">Altura do bitmap em pixels.</param>
    /// <param name="languages">Idiomas do Tesseract, ex.: "por+eng" (o default do produto).</param>
    OcrEngineResult Recognize(ReadOnlySpan<byte> bgra, int widthPx, int heightPx, string languages);
}
