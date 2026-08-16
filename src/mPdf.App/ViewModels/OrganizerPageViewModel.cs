using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using mPdf.App.Rendering;
using mPdf.App.Services;
using mPdf.Rendering;

namespace mPdf.App.ViewModels;

/// Miniatura GRANDE de UMA página no organizador (Task 3, Plano 3b) — exemplar: `ThumbnailViewModel`
/// (Task 6, Plano 3a), MESMO trio realize/derealize (Loaded/Unloaded/DataContextChanged, hospedado no
/// code-behind de `PageOrganizerView`) e escala FIXA (sem zoom/seleção de texto/overlays de busca).
/// Diferenças de propósito: escala MAIOR (0.35, "grade de miniaturas grandes" do brief — miniaturas do
/// painel lateral são 0.2, um renderer DIFERENTE — ver `OrganizerViewModel`, contrato "escala única por
/// renderer" já em vigor pro par ThumbnailViewModel/DocumentViewModel._thumbnailRenderer) e `IsSelected`
/// no lugar de `IsCurrent` (multi-seleção do organizador, não "página atual" do leitor).
public sealed partial class OrganizerPageViewModel : ObservableObject
{
    /// Escala fixa das miniaturas do organizador — brief pede "~0.35" (miniaturas GRANDES, contraste
    /// deliberado com as 0.2 do painel lateral). Mesma unidade de `PdfDocumentRenderer.RenderPage(scale)`
    /// (1.0 = 72dpi) que `ThumbnailViewModel.Scale` já usa.
    public const double Scale = 0.35;

    private readonly RenderScheduler _scheduler;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _realized;

    public int Index { get; }
    public int PageNumber => Index + 1;
    public double DisplayWidth { get; }
    public double DisplayHeight { get; }

    [ObservableProperty] private ImageSource? imageSource;
    [ObservableProperty] private bool isSelected;

    public OrganizerPageViewModel(int index, PdfPageSize size, RenderScheduler scheduler)
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
        _scheduler.Cancel(Index);         // não precisa mais do render pendente desta página
    }

    private void RequestRender()
    {
        _scheduler.Request(Index, Scale, (i, sc, page) =>
        {
            var bmp = BitmapConverter.ToBitmapSource(page);   // Freeze() -> pode nascer no worker
            _dispatcher.BeginInvoke(() =>
            {
                if (_realized) ImageSource = bmp;
            });
        });
    }
}
