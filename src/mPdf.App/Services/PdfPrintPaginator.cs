using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using mPdf.App.Rendering;
using mPdf.Documents;
using mPdf.Rendering;

namespace mPdf.App.Services;

/// Paginador dedicado à impressão (Task 8). Renderiza cada página SOB DEMANDA (dentro de GetPage),
/// nunca todas de uma vez — pré-renderizar um documento inteiro na resolução da impressora (ex.:
/// 600dpi) explodiria memória em documentos grandes. Usa um SEGUNDO `PdfDocumentRenderer` dedicado
/// sobre o MESMO `Session.Snapshot`, na mesma linha das miniaturas (Task 6): o cache de render-reader
/// de `PdfDocumentRenderer` é de escala ÚNICA (ver doc XML da classe), então impressão não pode
/// compartilhar renderer com o viewer (escala de zoom, variável) nem com as miniaturas (escala fixa
/// 0.2) sem invalidar o cache de um a cada troca do outro. `PdfRenderLock.Gate` (global) torna operar
/// os três ao mesmo tempo seguro.
///
/// `internal` (não `public`): só `PrintService` cria instâncias em produção; exposto aos testes via
/// `InternalsVisibleTo("mPdf.App.Tests")` (mesmo padrão já usado por `DocumentViewModel.ThumbnailRenderer`).
internal sealed class PdfPrintPaginator : DocumentPaginator, IDisposable
{
    private readonly PdfDocumentRenderer _renderer;
    private readonly double _scale; // dpi/72.0 — mesma unidade de PdfDocumentRenderer.RenderPage(scale)
    private readonly int[] _pageIndices; // páginas 0-based incluídas, na ordem de impressão

    public PdfPrintPaginator(DocumentSession session, double dpi, PageRange? range)
    {
        _renderer = new PdfDocumentRenderer(session.Snapshot);
        _scale = dpi / 72.0;

        int total = _renderer.PageCount;
        _pageIndices = range is { } r ? ResolveRange(r, total) : Enumerable.Range(0, total).ToArray();
    }

    // PageRange é 1-based (contrato do WPF: PrintDialog.PageRange/PageFrom/PageTo) — recorta pra
    // dentro de [1, total] ANTES de converter pro índice 0-based, pra um PageFrom/PageTo fora dos
    // limites (o diálogo nativo permite digitar valores maiores que o total de páginas) nunca virar
    // índice negativo nem estourar o array.
    private static int[] ResolveRange(PageRange r, int total)
    {
        int from = Math.Max(1, r.PageFrom);
        int to = Math.Min(total, r.PageTo);
        return to >= from ? Enumerable.Range(from - 1, to - from + 1).ToArray() : [];
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pageIndices.Length;
    public override Size PageSize { get; set; }

    // Confirmado por COMPILAÇÃO real contra a API do WPF (net10.0-windows, não memória de versões
    // antigas do .NET Framework): `DocumentPaginator.Source` é ABSTRATO nesta versão — uma subclasse
    // sem overrid-lo não compila (CS0534: "não implementa membro abstrato herdado"). Não existe um
    // `IDocumentPaginatorSource` de verdade por trás deste paginator (não vem de um `FlowDocument`),
    // então `null` é o valor correto.
    public override IDocumentPaginatorSource? Source => null;

    /// Renderiza a página `pageNumber` (0-based, índice DENTRO do range escolhido — não o índice do
    /// PDF) na resolução da impressora e a devolve como um `Visual` (um `Image` com o bitmap dentro de
    /// um `Canvas` do tamanho do papel) CENTRADO e com proporção preservada, NUNCA esticado — cálculo
    /// puro em `PrintService.ComputePlacement`, testável sem impressora/paginator nenhum.
    public override DocumentPage GetPage(int pageNumber)
    {
        int pdfIndex = _pageIndices[pageNumber];
        var rendered = _renderer.RenderPage(pdfIndex, _scale);
        var bmp = BitmapConverter.ToBitmapSource(rendered);

        double pageWpt = rendered.WidthPx / _scale;
        double pageHpt = rendered.HeightPx / _scale;
        var placement = PrintService.ComputePlacement(pageWpt, pageHpt, PageSize.Width, PageSize.Height);

        var image = new Image
        {
            Source = bmp,
            Stretch = Stretch.Uniform, // proteção redundante: Width/Height abaixo já vêm no aspect ratio certo
            Width = placement.Width,
            Height = placement.Height,
        };
        Canvas.SetLeft(image, placement.X);
        Canvas.SetTop(image, placement.Y);

        var page = new Canvas { Width = PageSize.Width, Height = PageSize.Height };
        page.Children.Add(image);
        // DocumentPage recebe um Visual já MEDIDO e ARRANJADO — diferente do viewer (onde o layout do
        // WPF cuida disso via binding dentro de uma Window real), o pipeline de impressão só
        // serializa o que já está posicionado; sem isso o Canvas nasceria com tamanho (0,0).
        page.Measure(PageSize);
        page.Arrange(new Rect(new Point(0, 0), PageSize));

        return new DocumentPage(page, PageSize, new Rect(new Point(0, 0), PageSize), new Rect(new Point(0, 0), PageSize));
    }

    public void Dispose() => _renderer.Dispose();
}
