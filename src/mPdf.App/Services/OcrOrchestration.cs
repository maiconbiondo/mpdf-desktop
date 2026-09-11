using mPdf.Rendering;

namespace mPdf.App.Services;

/// Task 4 (Plano 15): seams NEUTROS que a orquestração de OCR (`DocumentViewModel.RecognizeText`)
/// consome — extraídos como interfaces/records aqui para que os testes possam injetar fakes
/// determinísticos (rasterizer sem render nativo; progresso observável; cancelamento controlado) sem
/// depender de bitmaps reais nem de uma janela WPF real.

/// Fronteira do rasterizador de OCR (implementado por `OcrPageRasterizer`, T2). Extraída como interface
/// SÓ para testabilidade — a lógica (render 300dpi + detecção de página-com-texto) fica intocada em
/// `OcrPageRasterizer`; o App só CONSOME. Um fake de teste devolve páginas conhecidas sem tocar o
/// renderer nativo.
public interface IOcrPageRasterizer : IDisposable
{
    int PageCount { get; }
    bool PaginaTemTexto(int pageIndex);
    RenderedPage RasterizeForOcr(int pageIndex);
}

/// Progresso do OCR reportado à UI: página `PaginaAtual` de `TotalPaginas` (1-based) — vira a faixa
/// "Reconhecendo página N de M…". Struct imutável para atravessar o `IProgress<T>` sem alocação.
public readonly record struct OcrProgress(int PaginaAtual, int TotalPaginas);

/// Seam do diálogo/faixa de progresso do OCR (produção: `OcrProgressDialogService` abre a janela escura
/// com "Reconhecendo página N de M…", botão Cancelar e a nota honesta). Roteado por `UiPrompts.
/// CreateOcrProgress` — um teste headless que alcança o default de produção FALHA nomeado (via
/// `UiPromptsTestGuard`) em vez de tentar abrir uma `Window` real fora de uma thread STA.
public interface IOcrProgressService
{
    /// Abre a UI de progresso e devolve a sessão viva. `Dispose` da sessão fecha a UI.
    IOcrProgressSession Start();
}

/// Sessão de progresso viva — carrega o `CancellationToken` do botão Cancelar (cancelar interrompe o
/// laço de OCR de forma limpa, nada é gravado) e o `IProgress<OcrProgress>` que atualiza a faixa. Em
/// produção o `Progress<OcrProgress>` é criado na thread de UI (captura o `SynchronizationContext`), de
/// modo que os reports vindos do `Task.Run` do OCR voltam marshalados para a UI thread. `Dispose` fecha
/// a janela.
public interface IOcrProgressSession : IDisposable
{
    CancellationToken Token { get; }
    IProgress<OcrProgress> Progress { get; }
}
