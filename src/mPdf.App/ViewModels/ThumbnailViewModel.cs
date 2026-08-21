using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using mPdf.App.Rendering;
using mPdf.App.Services;
using mPdf.Rendering;

namespace mPdf.App.ViewModels;

/// Miniatura de UMA página (Task 6) — mesmo trio realize/derealize (Loaded/Unloaded/DataContextChanged,
/// hospedado no code-behind de ThumbnailsPanel) e mesmo staleness-guard em RequestRender que
/// PageViewModel (exemplar de todo o pipeline), mas SIMPLIFICADO: escala FIXA (sem zoom, sem
/// seleção, sem overlays de busca) — por isso não há um guard de "escala mudou entre pedido e
/// entrega" como em PageViewModel.RequestRender, só o guard de "ainda realizada".
///
/// Renderiza através do SEGUNDO RenderScheduler/PdfDocumentRenderer de miniaturas do
/// DocumentViewModel dono — NUNCA do renderer/scheduler principal (cache de render-reader do
/// PdfDocumentRenderer é de escala ÚNICA; ver doc XML da classe).
public sealed partial class ThumbnailViewModel : ObservableObject
{
    /// Escala fixa de miniatura, na MESMA unidade de PdfDocumentRenderer.RenderPage(scale) — 1.0 =
    /// 72dpi (1px por ponto de página), então 0.2 dá ~119px de largura pra uma página A4 (595pt),
    /// batendo com o alvo do brief de "~120px de largura A4".
    public const double Scale = 0.2;

    private readonly RenderScheduler _scheduler;
    // Capturado no construtor (não Application.Current?.Dispatcher): mesmo motivo de PageViewModel —
    // funciona tanto no app real quanto numa thread STA de teste que bombeia frames manualmente.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _realized;

    public int Index { get; }
    public int PageNumber => Index + 1;
    public double DisplayWidth { get; }
    public double DisplayHeight { get; }

    [ObservableProperty] private ImageSource? imageSource;
    [ObservableProperty] private bool isCurrent;

    public ThumbnailViewModel(int index, PdfPageSize size, RenderScheduler scheduler)
    {
        Index = index;
        DisplayWidth = size.WidthPt * Scale;
        DisplayHeight = size.HeightPt * Scale;
        _scheduler = scheduler;
    }

    public void OnRealized()
    {
        _realized = true;
        if (ImageSource is null) RequestRender();
    }

    public void OnDerealized()
    {
        _realized = false;
        ImageSource = null;               // libera memória do bitmap
        _scheduler.Cancel(Index);         // não precisa mais do render pendente desta miniatura
    }

    private void RequestRender()
    {
        _scheduler.Request(Index, Scale, (i, sc, page) =>
        {
            // Task 2 (Plano 9): 96 fixo, sempre -- miniatura de escala própria (0.2, INALTERADA pela
            // nitidez do viewer), pequena o bastante pra o custo de renderizar mais denso não valer o
            // ganho visual (brief).
            var bmp = BitmapConverter.ToBitmapSource(page, 96, 96);   // Freeze() -> pode nascer no worker
            _dispatcher.BeginInvoke(() =>
            {
                // escala é FIXA (sem zoom) — o único jeito de a entrega ficar obsoleta é a miniatura
                // já ter sido desrealizada enquanto o render estava em voo.
                if (_realized) ImageSource = bmp;
            });
        });
    }
}
