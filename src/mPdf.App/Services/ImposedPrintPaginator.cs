using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using mPdf.App.Rendering;
using mPdf.Documents;
using mPdf.Rendering;

namespace mPdf.App.Services;

/// Plano 25 (Task 2): paginador de impressão AVANÇADA — consome as colocações do motor de imposição
/// (`Imposicao`, Task 1) e desenha cada página-fonte no seu retângulo dentro da FOLHA, com rotação e
/// borda opcional. Uma `DocumentPage` = uma FOLHA física (que pode conter várias páginas do PDF: N-up,
/// livreto). Com `LayoutImpressao { NUp = 1 }` reproduz o 1-up centralizado do `PdfPrintPaginator`.
///
/// Render SOB DEMANDA (dentro de `GetPage`): cada página é rasterizada na resolução necessária pro seu
/// tamanho FINAL na folha (não a página inteira em 600dpi quando ela ocupa 1/16 da folha) — economiza
/// memória em N-up de documentos grandes. Segundo `PdfDocumentRenderer` dedicado sobre `Session.Snapshot`
/// (mesmo contrato das miniaturas/impressão simples; `PdfRenderLock.Gate` torna concorrência segura).
///
/// `internal`: só `PrintService`/o diálogo criam instâncias; exposto aos testes via InternalsVisibleTo.
internal sealed class ImposedPrintPaginator : DocumentPaginator, IDisposable
{
    private readonly PdfDocumentRenderer _renderer;
    private readonly LayoutImpressao _layout;
    private readonly double _dpi;
    private readonly int[] _pageIndices;      // páginas 0-based do PDF, na ordem de impressão (após range)
    private readonly TamanhoPt[] _tamanhosPt; // tamanho (pt) de cada página em _pageIndices
    private readonly int _folhas;             // total de folhas (independe do tamanho do papel)

    private IReadOnlyList<Colocacao>? _colocacoes; // calculado quando PageSize é conhecido (lazy)
    private Size _pageSize;

    public ImposedPrintPaginator(DocumentSession session, LayoutImpressao layout, double dpi, PageRange? range)
    {
        _renderer = new PdfDocumentRenderer(session.Snapshot);
        _layout = layout;
        _dpi = dpi;

        int total = _renderer.PageCount;
        _pageIndices = range is { } r ? ResolveRange(r, total) : Enumerable.Range(0, total).ToArray();
        _tamanhosPt = _pageIndices.Select(i =>
        {
            var s = _renderer.GetPageSize(i);
            return new TamanhoPt(s.WidthPt, s.HeightPt);
        }).ToArray();

        _folhas = ContarFolhas(_pageIndices.Length, layout);
    }

    private static int[] ResolveRange(PageRange r, int total)
    {
        int from = Math.Max(1, r.PageFrom);
        int to = Math.Min(total, r.PageTo);
        return to >= from ? Enumerable.Range(from - 1, to - from + 1).ToArray() : [];
    }

    /// Folhas físicas (independe do tamanho do papel): livreto = padded(múltiplo de 4)/2 faces; N-up =
    /// teto(páginas / NUp).
    private static int ContarFolhas(int nPaginas, LayoutImpressao layout)
    {
        if (nPaginas == 0) return 0;
        if (layout.Livreto) { int padded = ((nPaginas + 3) / 4) * 4; return padded / 2; }
        int porFolha = Math.Max(1, layout.NUp);
        return (nPaginas + porFolha - 1) / porFolha;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _folhas;
    public override IDocumentPaginatorSource? Source => null;

    public override Size PageSize
    {
        get => _pageSize;
        set { if (_pageSize != value) { _pageSize = value; _colocacoes = null; } } // muda o papel -> recalcula
    }

    private IReadOnlyList<Colocacao> Colocacoes()
    {
        if (_colocacoes is not null) return _colocacoes;
        if (_pageSize.Width <= 0 || _pageSize.Height <= 0) return _colocacoes = [];
        // Imposição em DIPs (1/96"): folha = PageSize (DIPs); páginas = pt -> DIPs (×96/72). Assim os
        // retângulos já saem em DIPs, prontos pro DrawingContext do WPF.
        const double PtParaDip = 96.0 / 72.0;
        var folha = new TamanhoPt(_pageSize.Width, _pageSize.Height);
        var paginasDip = _tamanhosPt.Select(t => new TamanhoPt(t.Largura * PtParaDip, t.Altura * PtParaDip)).ToList();
        _colocacoes = Imposicao.Calcular(folha, paginasDip, _layout);
        return _colocacoes;
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        var colocacoes = Colocacoes();
        var daFolha = colocacoes.Where(c => c.Folha == pageNumber).ToList();

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var borda = _layout.Borda ? new Pen(Brushes.Gray, 0.5) : null;
            foreach (var c in daFolha)
            {
                if (c.PaginaFonte < 0 || c.Largura <= 0 || c.Altura <= 0) continue; // célula em branco
                DesenharPagina(dc, c, borda);
            }
        }
        var caixa = new Rect(new Point(0, 0), _pageSize);
        return new DocumentPage(dv, _pageSize, caixa, caixa);
    }

    private void DesenharPagina(DrawingContext dc, Colocacao c, Pen? borda)
    {
        int pdfIndex = _pageIndices[c.PaginaFonte];
        var tam = _tamanhosPt[c.PaginaFonte];

        // Resolução de render casada com o tamanho FINAL na folha (pela aresta MAIOR, proporção já
        // preservada pela imposição): px_maior = destMaiorDip/96 * dpi -> escala PDFium = px/pt.
        double destMaior = Math.Max(c.Largura, c.Altura);
        double pagMaiorPt = Math.Max(tam.Largura, tam.Altura);
        double escala = pagMaiorPt > 0 ? destMaior * _dpi / (96.0 * pagMaiorPt) : 1.0;
        escala = Math.Clamp(escala, 0.05, _dpi / 72.0);

        var rendered = _renderer.RenderPage(pdfIndex, escala);
        var bmp = BitmapConverter.ToBitmapSource(rendered, 96, 96);

        double cx = c.X + c.Largura / 2, cy = c.Y + c.Altura / 2;
        var destino = new Rect(c.X, c.Y, c.Largura, c.Altura);

        if (c.Rotacao == 0)
        {
            dc.DrawImage(bmp, destino);
        }
        else
        {
            dc.PushTransform(new RotateTransform(c.Rotacao, cx, cy));
            // Ao girar 90/270 em torno do centro, um retângulo (Altura×Largura) vira a "pegada"
            // (Largura×Altura) desejada — desenhamos o bitmap nesse retângulo pré-rotação.
            var r = c.Rotacao is 90 or 270
                ? new Rect(cx - c.Altura / 2, cy - c.Largura / 2, c.Altura, c.Largura)
                : destino;
            dc.DrawImage(bmp, r);
            dc.Pop();
        }

        if (borda is not null) dc.DrawRectangle(null, borda, destino);
    }

    public void Dispose() => _renderer.Dispose();
}
