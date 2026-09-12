using mPdf.Rendering;

namespace mPdf.App.Services;

/// Task 2, Plano 15: render de OCR (~300 DPI) e detecção de página-com-texto, SEPARADO do pipeline de
/// tela (não toca RenderScheduler/PageViewModel/PdfViewerControl/overlay/seleção). Renderer dedicado
/// PRÓPRIO, no mesmo padrão de `PdfPrintPaginator`/`ExportImageViewModel.RenderPageForExport`:
/// `PdfDocumentRenderer` cacheia o reader de render por UMA escala só (ver doc XML da classe), então a
/// escala de OCR (300dpi) não pode compartilhar reader com o viewer (zoom variável) nem com as
/// miniaturas (0.2 fixo) sem invalidar o cache de um a cada troca do outro.
///
/// Consumido pela T4 (comando "Reconhecer texto (OCR)"): para cada página SEM texto (`PaginaTemTexto`
/// falso), `RasterizeForOcr` entrega o bitmap pro `IOcrEngine` (mPdf.Ocr, T1); nenhum tipo de
/// Tesseract/iText cruza esta classe (só mPdf.Rendering).
public sealed class OcrPageRasterizer : IOcrPageRasterizer
{
    // `PdfDocumentRenderer.RenderPage(pageIndex, scale)`: "scale 1.0 = 72dpi (1px por ponto)" (doc XML
    // do método, confirmado por leitura direta do código-fonte de mPdf.Rendering — o mesmo raciocínio
    // que `PdfPrintPaginator` já usa para DPI de impressora: `_scale = dpi / 72.0`). Para 300 DPI:
    // scale = 300/72 ≈ 4,1667. Isso NÃO embute o `PtToPx` (96/72) da tela — aquele fator é específico
    // do mapeamento zoom-de-tela->pixel-de-tela em `PageViewModel` (`DisplayWidth = WidthPt * zoom *
    // PtToPx`) e não tem relação com a escala do renderer nativo, que é puramente pt->px na escala
    // pedida. Confundir os dois daria 300 * 96/72 ≈ 400 DPI reais — por isso o cálculo aqui usa só
    // dpi/72, igual ao paginator de impressão (já provado certo em produção).
    public const double OcrDpi = 300.0;
    private const double OcrScale = OcrDpi / 72.0;

    // Limiar pequeno (plano: "≥ 1–3 chars não-espaço"): 2 caracteres extraíveis não-espaço já bastam
    // pra considerar a página "tem texto" (evita falso-positivo de 1 caractere-fantasma isolado, ex.
    // um glifo de marca d'água ou um caractere de controle mal-extraído, sem exigir um limiar alto que
    // arriscaria tratar uma página quase-vazia como imagem).
    private const int MinNonWhitespaceCharsWithText = 2;

    private readonly PdfDocumentRenderer _renderer;

    public int PageCount => _renderer.PageCount;

    public OcrPageRasterizer(byte[] pdf) => _renderer = new PdfDocumentRenderer(pdf);

    /// Renderiza a página `pageIndex` a ~300 DPI para o motor de OCR (T1). Uma página por vez: o
    /// chamador (T4) processa e descarta (`RenderedPage.Bgra`) antes de pedir a próxima -- este
    /// serviço nunca mantém mais de um bitmap de OCR vivo por vez.
    public RenderedPage RasterizeForOcr(int pageIndex) => _renderer.RenderPage(pageIndex, OcrScale);

    /// Reusa a MESMA extração de texto que o Ctrl+F já usa (`PdfDocumentRenderer.GetTextPage`, consumida
    /// hoje por `PageViewModel`/`PdfTextSearch` em mPdf.Rendering) -- nenhuma extração nova é criada.
    /// Critério: a página TEM texto se a contagem de caracteres extraíveis NÃO-espaço for >= um limiar
    /// pequeno. Páginas com texto são PULADAS pelo comando de OCR (T4) -- evita gravar uma camada
    /// invisível redundante sobre texto que já existe e já é buscável/copiável.
    public bool PaginaTemTexto(int pageIndex)
    {
        string text = _renderer.GetTextPage(pageIndex).Text;
        int nonWhitespace = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (++nonWhitespace >= MinNonWhitespaceCharsWithText) return true;
        }
        return false;
    }

    public void Dispose() => _renderer.Dispose();
}
