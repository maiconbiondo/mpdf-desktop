using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using mPdf.App.Rendering;
using mPdf.App.Services;
using mPdf.Rendering;

namespace mPdf.App.ViewModels;

public sealed partial class PageViewModel : ObservableObject
{
    internal const double PtToPx = 96.0 / 72.0;
    private readonly RenderScheduler _scheduler;
    private readonly DocumentViewModel _owner;
    // Capturado no construtor (não Application.Current?.Dispatcher): funciona tanto no app real quanto
    // numa thread STA de teste que bombeia frames manualmente (Application.Current é null em testes).
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public int Index { get; }
    public double WidthPt { get; }
    public double HeightPt { get; }

    [ObservableProperty] private double displayWidth;
    [ObservableProperty] private double displayHeight;
    [ObservableProperty] private ImageSource? imageSource;

    private bool _realized;

    // Cache lazy da TextPage (Task 2): GetTextPage foi medido em ~0,5ms/página (ver task-3-report.md) —
    // rápido o bastante pra chamar de forma SÍNCRONA no primeiro gesto de seleção, sem precisar do
    // RenderScheduler em background como os bitmaps. Fica null até a primeira seleção nesta página.
    private TextPage? _textPage;
    // Guarda do prefetch (Task 1, Plano 3a, item b): true assim que OnRealized DISPARA o Task.Run —
    // não assim que ele TERMINA — pra evitar reagendar um segundo Task.Run se a página for
    // realizada/derealizada várias vezes (virtualização da ListBox) antes do primeiro terminar. 1x
    // por PageViewModel, pra sempre (nunca reseta em OnDerealized — ver comentário lá).
    private bool _textPagePrefetchStarted;
    private Point _anchorPt;   // âncora do arrasto atual, em PONTOS de página (origem PDF)

    /// Retângulos de seleção em PX DE TELA (já com zoom aplicado e Y invertido), um por linha —
    /// consumido pelo ItemsControl/Canvas overlay dentro do Border do ItemTemplate.
    public ObservableCollection<Rect> SelectionRects { get; } = [];

    /// Retângulos de destaque de busca (Task 5) — TODOS os hits desta página, em PX DE TELA. Vive no
    /// VM (não no container visual) de propósito: uma página derealizada pela virtualização mantém
    /// os dados; quando um container é reciclado de volta pra esta página, o binding do overlay
    /// (ItemsControl) pega os retângulos já prontos sem lógica extra — mesmo padrão de SelectionRects.
    public ObservableCollection<Rect> HighlightRects { get; } = [];

    /// Retângulos do hit de busca CORRENTE nesta página (cor distinta, desenhado por cima de
    /// HighlightRects) — vazio em toda página que não seja a do hit corrente.
    public ObservableCollection<Rect> CurrentHighlightRects { get; } = [];

    // Fonte-da-verdade em PONTOS de página (independente de zoom) por trás de SelectionRects/
    // HighlightRects/CurrentHighlightRects acima — revisão da Task 5 (I4): sem guardar isso, um zoom
    // depois de selecionar texto ou buscar deixava os retângulos (já convertidos pra px no zoom
    // ANTIGO) flutuando na posição errada, porque ApplyZoom não tinha de onde reconverter.
    private List<Rect> _selectionPtRects = [];
    private List<Rect> _highlightPtRects = [];
    private List<Rect> _currentHighlightPtRects = [];

    /// Texto selecionado nesta página (ordem de texto), ou null se não há seleção ativa aqui.
    public string? SelectedText { get; private set; }

    // ---- Task 7 (Plano 3a): overlay de seleção de ANOTAÇÃO (nota adesiva/caixa de texto) -----------
    // Exemplar: SelectionRects (seleção de TEXTO, Task 3) — mas aqui só existe NO MÁXIMO 1 anotação
    // selecionada em todo o documento (DocumentViewModel.SelectedAnnotation), então não precisa de uma
    // ObservableCollection de retângulos: 1 Rect + 1 bool bastam. DocumentViewModel é quem escreve os 2
    // (UpdateAnnotationSelectionOverlay, ao trocar SelectedAnnotation) — este VM só expõe pro binding.
    [ObservableProperty] private bool hasAnnotationSelection;
    [ObservableProperty] private Rect annotationSelectionRect;

    // ---- Task 2 (Plano 3c): destaque do CAMPO DE FORMULÁRIO selecionado (painel Campos) -------------
    // Exemplar EXATO de HasAnnotationSelection/AnnotationSelectionRect acima — mesma mecânica (1 Rect +
    // 1 bool, reconvertido em ApplyZoom), só que a fonte é DocumentViewModel.SelectedFormField
    // (FormFieldData.WidgetRect) em vez de AnnotationData. GATE DE ROTAÇÃO (brief): decidido em
    // DocumentViewModel.UpdateFormFieldHighlightOverlay — este VM só expõe pro binding, mesma separação
    // de responsabilidade do overlay de anotação.
    [ObservableProperty] private bool hasFormFieldHighlight;
    [ObservableProperty] private Rect formFieldHighlightRect;

    // ---- Task 4 (Plano 4): destaque do CARIMBO DE ASSINATURA selecionado (painel Assinaturas) -------
    // Exemplar EXATO de HasFormFieldHighlight/FormFieldHighlightRect acima — mesma mecânica (1 Rect + 1
    // bool, reconvertido em ApplyZoom), só que a fonte é DocumentViewModel.SelectedSignature
    // (SignatureInfo.StampRect) em vez de FormFieldData.WidgetRect. GATE DE ROTAÇÃO: decidido em
    // DocumentViewModel.SelectSignature ANTES de chegar até aqui (recusa a ação inteira numa página
    // girada, diferente do gate silencioso-só-no-destaque de FormField) — este VM só expõe pro binding,
    // mesma separação de responsabilidade do overlay de campo/anotação.
    [ObservableProperty] private bool hasSignatureStampHighlight;
    [ObservableProperty] private Rect signatureStampHighlightRect;

    // ---- Task 8 (Plano 3a): overlay de PRÉVIA ao vivo do gesto de desenho (Ink/Rectangle/Line/Arrow) --
    // Exemplar: HasAnnotationSelection/AnnotationSelectionRect acima — só que aqui são 3 FORMAS
    // distintas (Polyline/Rectangle/Line), cada uma com seu próprio flag de visibilidade; só 1 ativa
    // por vez (as 4 ferramentas de desenho são mutuamente exclusivas, mesma garantia de ActiveTool). A
    // View (PdfViewerControl) escreve os 3 grupos abaixo a cada MouseDown/MouseMove/MouseUp — este VM
    // só expõe pro binding. TUDO em PX DE TELA (não pt de página): diferente de SelectionRects/
    // AnnotationSelectionRect, esta prévia é puramente TRANSIENTE (dura só o gesto em curso) e nunca
    // precisa sobreviver a um zoom mid-drag nem a uma reciclagem de container — por isso não guarda
    // uma fonte em pontos nem reage a ApplyZoom (a View recalcula do zero a cada MouseMove).
    [ObservableProperty] private bool hasInkPreview;
    /// Pontos acumulados do traço de Ink em curso, em px de tela — Polyline.Points aceita um
    /// PointCollection diretamente (Freezable: mutar a MESMA instância já invalida o binding, sem
    /// precisar trocar a coleção inteira a cada ponto).
    public PointCollection InkPreviewPoints { get; } = new();

    [ObservableProperty] private bool hasRectPreview;
    [ObservableProperty] private Rect rectPreviewRect; // rubber-band do Rectangle, em px de tela

    [ObservableProperty] private bool hasLinePreview; // cobre Line E Arrow — mesma prévia "rubber-band"
    [ObservableProperty] private Point linePreviewStart;
    [ObservableProperty] private Point linePreviewEnd;

    /// Limpa TODA a prévia de desenho desta página — chamado pela View no fim de um gesto (commit OU
    /// abortado por LostMouseCapture), mesmo espírito de ClearSelection abaixo.
    public void ClearDrawingPreview()
    {
        HasInkPreview = false;
        InkPreviewPoints.Clear();
        HasRectPreview = false;
        RectPreviewRect = default;
        HasLinePreview = false;
        LinePreviewStart = default;
        LinePreviewEnd = default;
    }

    /// Retângulos da seleção ativa, em PONTOS de página (mesma fonte de `SelectionRects`, ANTES da
    /// conversão pra px de tela) — consumido por `DocumentViewModel.ApplyMarkupCommand` (Task 6, Plano
    /// 3a) pra montar os `Quads` da anotação: a seleção JÁ produz os retângulos por linha certos
    /// (`TextSelection.BuildLineRects`), nenhuma síntese nova precisa acontecer. `internal` (mesmo
    /// padrão de `TextPageCache` acima): só o próprio VM/DocumentViewModel e os testes
    /// (`InternalsVisibleTo`) precisam disto — a UI (overlay de seleção) continua consumindo só
    /// `SelectionRects` (px de tela).
    internal IReadOnlyList<Rect> SelectionPointRects => _selectionPtRects;

    /// Exposto só pra teste (Task 1, Plano 3a, item b — prova que OnRealized populou o cache da
    /// TextPage em background, ANTES de qualquer gesto de seleção) — internal via InternalsVisibleTo,
    /// mesmo padrão de DocumentViewModel.ThumbnailRenderer.
    internal TextPage? TextPageCache => _textPage;

    public PageViewModel(int index, PdfPageSize size, RenderScheduler scheduler, DocumentViewModel owner)
    {
        Index = index; WidthPt = size.WidthPt; HeightPt = size.HeightPt;
        _scheduler = scheduler; _owner = owner;
        ApplyZoom(owner.Zoom);
    }

    public void ApplyZoom(double zoom)
    {
        DisplayWidth = WidthPt * zoom * PtToPx;
        DisplayHeight = HeightPt * zoom * PtToPx;
        // mantém o bitmap antigo (o Image usa Stretch="Fill", então ele estica para o novo
        // tamanho) até a nova renderização chegar — evita tela em branco durante o zoom.
        // O guard de escala em RequestRender descarta entregas obsoletas; OnDerealized
        // continua sendo o único lugar que libera a memória do bitmap.
        if (_realized) RequestRender(zoom);

        // I4 (revisão Task 5): reconverte os retângulos (seleção/destaques de busca) a partir da
        // fonte em PONTOS no zoom NOVO — só quando há algo pra reconverter (evita Clear+no-op à toa
        // na maioria das páginas, que não têm seleção nem hit).
        if (_selectionPtRects.Count > 0) FillScreenRects(SelectionRects, _selectionPtRects);
        if (_highlightPtRects.Count > 0) FillScreenRects(HighlightRects, _highlightPtRects);
        if (_currentHighlightPtRects.Count > 0) FillScreenRects(CurrentHighlightRects, _currentHighlightPtRects);

        // Task 7 (Plano 3a): o overlay de seleção de ANOTAÇÃO não guarda sua própria fonte em pontos
        // (ao contrário dos 3 acima) — a fonte É o AnnotationData em DocumentViewModel.SelectedAnnotation,
        // já em pontos por natureza (LeftPt/BottomPt/RightPt/TopPt). Só reconverte se ESTA página for a
        // dona da seleção corrente (mesmo guard de "há algo pra reconverter" dos 3 acima).
        if (HasAnnotationSelection && _owner.SelectedAnnotation is { } sel && sel.PageIndex == Index)
            AnnotationSelectionRect = PointRectToScreenRect(sel.LeftPt, sel.BottomPt, sel.RightPt, sel.TopPt, zoom, HeightPt);

        // Task 2 (Plano 3c): mesma reconversão, fonte = DocumentViewModel.SelectedFormField.WidgetRect.
        if (HasFormFieldHighlight && _owner.SelectedFormField is { Data.WidgetRect: { } rect } selField && selField.Data.PageIndex == Index)
            FormFieldHighlightRect = PointRectToScreenRect(rect.LeftPt, rect.BottomPt, rect.RightPt, rect.TopPt, zoom, HeightPt);

        // Task 4 (Plano 4): mesma reconversão, fonte = DocumentViewModel.SelectedSignature.Data.StampRect.
        if (HasSignatureStampHighlight && _owner.SelectedSignature?.Data is { StampPageIndex: int sigPage, StampRect: { } sigRect } && sigPage == Index)
            SignatureStampHighlightRect = PointRectToScreenRect(sigRect.LeftPt, sigRect.BottomPt, sigRect.RightPt, sigRect.TopPt, zoom, HeightPt);
    }

    /// Converte um retângulo EM PONTOS (origem PDF, Y cresce pra cima) pra PX DE TELA (Y cresce pra
    /// baixo) no zoom dado — mesma matemática de `FillScreenRects` abaixo, extraída como método
    /// estático `internal` (Task 7, Plano 3a) porque agora tem 2 chamadores: `FillScreenRects` (via
    /// coleções de retângulos de seleção de texto/busca) e `DocumentViewModel.
    /// UpdateAnnotationSelectionOverlay`/`PdfViewerControl` (retângulo ÚNICO da anotação selecionada,
    /// incl. a preview ao vivo do arrasto).
    internal static Rect PointRectToScreenRect(double leftPt, double bottomPt, double rightPt, double topPt, double zoom, double pageHeightPt)
    {
        double scale = zoom * PtToPx;
        return new Rect(leftPt * scale, (pageHeightPt - topPt) * scale, (rightPt - leftPt) * scale, (topPt - bottomPt) * scale);
    }

    public void OnRealized()
    {
        _realized = true;
        if (ImageSource is null) RequestRender(_owner.Zoom);

        // Item (b): prefetch da TextPage (cache de seleção) em background, fora do RenderScheduler —
        // GetTextPage é rápido o bastante (~0,5ms/página, ver doc XML de _textPage acima) pra não
        // precisar de fila/worker dedicado; um Task.Run avulso por página já elimina a espera do
        // gate global do PDFium NA THREAD DE UI no primeiro arrasto de seleção (BeginSelection
        // abaixo continua como fallback SÍNCRONO — cobre o caso raro de o usuário arrastar antes do
        // prefetch terminar, ou de ele ter sido pulado por algum motivo).
        if (!_textPagePrefetchStarted)
        {
            _textPagePrefetchStarted = true;
            Task.Run(() =>
            {
                // Mesma disciplina de ODE do resto do app (ver doc XML de PdfDocumentRenderer/
                // RenderScheduler.WorkerLoop): a aba pode fechar (Session.Dispose, offloaded via
                // PendingDisposals) ENQUANTO este prefetch está em voo — GetTextPage lança
                // ObjectDisposedException GERENCIADA nesse caso (nunca AV nativa); sem cache aqui,
                // sem problema, ninguém mais vai olhar pra esta página.
                try { _textPage ??= _owner.Session.Renderer.GetTextPage(Index); }
                catch (ObjectDisposedException) { }
            });
        }
    }

    public void OnDerealized()
    {
        _realized = false;
        ImageSource = null;                    // libera memória do bitmap
        _scheduler.Cancel(Index);              // não precisa mais do render pendente desta página
        // NÃO cancela o prefetch da TextPage em voo (_textPagePrefetchStarted continua true pra
        // sempre): ao contrário do bitmap (MBs, vale a pena descartar), a TextPage é pequena e barata
        // (~0,5ms) — deixar um prefetch órfão terminar em background é inofensivo, e cancelá-lo só
        // pra reagendar de novo numa realização futura seria trabalho extra sem benefício real.
    }

    private void RequestRender(double zoom)
    {
        double scale = zoom * PtToPx;
        _scheduler.Request(Index, scale, (i, sc, page) =>
        {
            var bmp = BitmapConverter.ToBitmapSource(page);   // Freeze() -> pode nascer no worker
            _dispatcher.BeginInvoke(() =>
            {
                // descarta resultado obsoleto se o zoom mudou enquanto renderizava
                if (Math.Abs(_owner.Zoom * PtToPx - sc) < 0.001 && _realized)
                    ImageSource = bmp;
            });
        });
    }

    /// Início de um arrasto de seleção: fixa a âncora (em pt) no ponto de mouse-down (em px de tela,
    /// relativo ao Border da página) e garante a TextPage carregada. Chamado só quando o gesto já foi
    /// reconhecido como arrasto (não em todo mouse-down — clique simples não deve carregar nada).
    public void BeginSelection(Point downPx)
    {
        _textPage ??= _owner.Session.Renderer.GetTextPage(Index);
        _anchorPt = TextSelection.ScreenToPagePoint(downPx, _owner.Zoom, HeightPt);
        _owner.SetSelectionOwner(this);
        UpdateSelection(downPx);
    }

    /// Atualiza a seleção corrente (âncora fixada em BeginSelection) até o ponto de mouse-move/up
    /// atual, em px de tela. Reconverte âncora e cursor pro pt de página a cada chamada (o zoom pode
    /// mudar teoricamente entre chamadas; reler _owner.Zoom sempre em vez de cachear é mais simples
    /// que invalidar um cache).
    public void UpdateSelection(Point currentPx)
    {
        if (_textPage is not { } textPage) return;
        var cursorPt = TextSelection.ScreenToPagePoint(currentPx, _owner.Zoom, HeightPt);
        var result = TextSelection.Select(textPage, _anchorPt, cursorPt);

        SelectedText = result.Text;
        _selectionPtRects = result.LineRects.ToList();
        FillScreenRects(SelectionRects, _selectionPtRects);
    }

    // Preenche `target` com os retângulos de `ptRects` (origem PDF, em PONTOS) convertidos pra PX DE
    // TELA no zoom atual — mesma conversão usada pela seleção (Task 3) e pelos destaques de busca
    // (Task 5): inverte Y usando a borda de TOPO do retângulo (r.Y + r.Height), já que Rect em pt tem
    // origem PDF (Y = borda inferior, cresce pra cima) e a tela tem Y = borda superior, cresce pra baixo.
    private void FillScreenRects(ObservableCollection<Rect> target, IEnumerable<Rect> ptRects)
    {
        target.Clear();
        double scale = _owner.Zoom * PtToPx;
        foreach (var r in ptRects)
        {
            double topPt = r.Y + r.Height;
            target.Add(new Rect(r.X * scale, (HeightPt - topPt) * scale, r.Width * scale, r.Height * scale));
        }
    }

    /// Define os retângulos de destaque de TODOS os hits de busca nesta página (Task 5), em pontos
    /// de página — convertidos aqui pro mesmo espaço de tela de SelectionRects.
    public void SetHighlights(IEnumerable<Rect> ptRects)
    {
        _highlightPtRects = ptRects.ToList();
        FillScreenRects(HighlightRects, _highlightPtRects);
    }

    /// Define o(s) retângulo(s) do hit CORRENTE nesta página (cor distinta) — chamado só na página
    /// que contém o hit corrente; as demais são limpas por ClearHighlights.
    public void SetCurrentHighlight(IEnumerable<Rect> ptRects)
    {
        _currentHighlightPtRects = ptRects.ToList();
        FillScreenRects(CurrentHighlightRects, _currentHighlightPtRects);
    }

    /// Limpa os destaques de busca desta página (todos os hits + o corrente) — chamado a cada nova
    /// busca/navegação antes de redistribuir, e ao fechar a barra (Esc/Fechar).
    public void ClearHighlights()
    {
        _highlightPtRects = [];
        _currentHighlightPtRects = [];
        HighlightRects.Clear();
        CurrentHighlightRects.Clear();
    }

    /// Limpa a seleção desta página (texto + retângulos). Chamado pelo DocumentViewModel ao trocar de
    /// página selecionada ou num clique simples (sem arrasto).
    public void ClearSelection()
    {
        SelectedText = null;
        _selectionPtRects = [];
        SelectionRects.Clear();
    }
}
