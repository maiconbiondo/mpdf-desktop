using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using mPdf.App.Services;
using mPdf.App.ViewModels;
// Task 7 (Plano 3a): só o contrato NEUTRO (AnnotationData/AnnotationTool) — mesma fronteira AGPL já
// atravessada por DocumentViewModel/MainViewModel (Tasks 5-6), nunca um tipo iText.
using mPdf.Editing;

namespace mPdf.App.Views;

public partial class PdfViewerControl : UserControl
{
    private ScrollViewer? _scrollViewer;
    // Zoom conhecido no momento da última troca — precisamos do valor ANTIGO (antes da troca) para
    // calcular a proporção do offset; um binding não guarda isso, por isso assinamos PropertyChanged.
    private double _lastZoom = 1.0;

    // ScrollToPage (Task 5, I1) reaplica o offset em vários ScrollChanged até convergir (ver
    // comentário do método) — se o usuário navegar de novo (Próximo/Anterior rápido) ANTES da
    // convergência anterior terminar, o loop velho continua assinado e briga com o novo alvo. Guarda
    // o handler ATIVO pra desassinar o anterior antes de começar um novo — só um loop de convergência
    // por vez, mesmo espírito dos outros "staleness guards" do arquivo (IsCurrentDocument etc.).
    private ScrollChangedEventHandler? _activeScrollReapply;

    // Estado do gesto de seleção de texto em curso (Task 3) — um por controle, já que só um dedo/
    // botão pode estar arrastando por vez. _dragging só vira true depois do limiar de distância:
    // isso é o que distingue "clique simples" (limpa seleção, não arrasta) de um arrasto de verdade.
    private const double DragThresholdPx = 2.0;
    private PageViewModel? _selectingPage;
    private FrameworkElement? _selectingElement;
    private Point _mouseDownPx;
    private bool _dragging;

    // Estado do gesto de ARRASTAR UMA ANOTAÇÃO selecionada (Task 7, Plano 3a) — espelho do estado de
    // seleção de texto acima, só que o objeto arrastado é um AnnotationData (não texto). Mesmo limiar
    // de distância (_annotationDragMoved só vira true depois de DragThresholdPx) distingue "clique
    // simples" (só seleciona) de "arrastar pra mover".
    private AnnotationData? _draggingAnnotation;
    private PageViewModel? _draggingAnnotationPage;
    private Point _annotationDragAnchorPx;
    private bool _annotationDragMoved;

    // Estado do gesto de DESENHAR (Task 8, Plano 3a) — Ink/Rectangle/Line/Arrow. 3º grupo de estado de
    // arrasto neste arquivo (espelha os 2 acima); `_drawingPointsPx` acumula TODOS os pontos do gesto,
    // em px de tela — pra Ink é a polilinha inteira (throttle de InkThrottlePx entre amostras); pra
    // Rectangle/Line/Arrow só o [0]=âncora e o ÚLTIMO elemento importam (cada MouseMove SUBSTITUI o
    // último em vez de acumular, ver Page_MouseMove) — mas reusar a MESMA lista pros 4 tipos evita um
    // 4º campo/tipo de estado só pra essa distinção. O reset de TODOS os 3 grupos, unificado num só
    // método (`ResetGestureState`), é o "shared helper" que evita a 3ª cópia quase-idêntica de
    // LostMouseCapture (ver doc XML de Page_LostMouseCapture).
    private PageViewModel? _drawingPage;
    private AnnotationTool _drawingTool;
    private readonly List<Point> _drawingPointsPx = new();
    private const double InkThrottlePx = 4.0; // brief: "throttle to every N px moved"

    // Estado dos gestos de mouse da caixa ajustável do carimbo de assinatura (Task 2, Plano 8) — 3
    // grupos distintos, mutuamente exclusivos (só 1 pode estar em curso por vez, mesmo espírito dos 3
    // grupos acima): arrastar-para-DESENHAR (Drawing, mouse-down na página via
    // Page_MouseLeftButtonDown), MOVER o corpo inteiro (Adjusting, mouse-down no Grid do adorner via
    // StampBox_MouseLeftButtonDown) e REDIMENSIONAR por uma alça (Adjusting, mouse-down num Rectangle
    // de alça via StampBoxHandle_MouseLeftButtonDown). Os 3 capturam o mouse no BORDER da página
    // (achado via FindPageBorder abaixo, não no elemento fisicamente clicado) de propósito: assim
    // Page_MouseMove/Page_MouseLeftButtonUp (registrados no Border) continuam sendo o ÚNICO lugar que
    // trata Move/Up pra qualquer gesto deste arquivo — o evento SOBE (bubbling) do elemento capturado
    // até o Border independente de QUEM chamou CaptureMouse, mas só o Border tem os handlers ligados.
    private PageViewModel? _stampBoxDrawPage;
    private PageViewModel? _stampBoxMovePage;
    private PageViewModel? _stampBoxResizePage;
    private StampBoxHandle _stampBoxResizeHandle;
    // Ponto de PÁGINA (pt) do último MouseMove processado pro gesto de mover/redimensionar em curso —
    // MoveBoxBy/ResizeBoxByHandle esperam um DELTA desde a ÚLTIMA chamada (não desde o mouse-down, ver
    // doc XML de ResizeBoxByHandle em DocumentViewModel), por isso este ponto é atualizado a CADA
    // MouseMove processado, não só uma vez no mouse-down.
    private Point _stampBoxLastPagePt;

    public PdfViewerControl()
    {
        InitializeComponent();
        PreviewMouseWheel += OnPreviewMouseWheel;
        // I1-A (revisão 2 da Task 5): QUALQUER entrada do usuário aborta o loop de convergência do
        // scroll-to-hit em curso — o scroll automático da busca nunca pode "brigar" com o usuário
        // rolando, clicando ou navegando por teclado por conta própria.
        PreviewMouseDown += (_, _) => CancelScrollReapply();
        PreviewKeyDown += OnPreviewKeyDown;
        // Task 2 (Plano 9): fixa o fator de DPI do monitor ATUAL assim que o controle entra na árvore
        // visual de verdade (ANTES do Loaded, VisualTreeHelper.GetDpi ainda não reflete o monitor real —
        // ver doc XML de PushCurrentDpiFactor) — mesmo adiamento de PageList.Focus() logo abaixo.
        Loaded += (_, _) => { PageList.Focus(); PushCurrentDpiFactor(); };   // foco para PageUp/PageDown funcionarem ao trocar de aba
        PageList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        DataContextChanged += OnDataContextChanged;
    }

    // largura/altura do viewport para os modos de ajuste (chamado pela MainWindow)
    public double ViewportWidth => PageList.ActualWidth;
    public double ViewportHeight => PageList.ActualHeight;

    // ---- Task 2 (Plano 9): nitidez -- fator de DPI do monitor propagado pro DocumentViewModel --------

    /// Chamado pelo SISTEMA (WPF) quando o DPI efetivo deste Visual muda — o caso real é a janela sendo
    /// arrastada entre monitores com escalas diferentes (125% -> 150%, etc.). `Visual.OnDpiChanged` é o
    /// hook per-monitor-DPI-v2 do WPF (desde .NET Core 3.0), disparado pelo próprio framework — nenhum
    /// polling/timer nosso. Re-renderiza as páginas REALIZADAS na densidade nova (ver
    /// DocumentViewModel.OnDpiFactorChanged/PageViewModel.RefreshDpi) — "barato de ouvir" (brief): só um
    /// re-render dos poucos containers hoje na tela, mesmo custo de um zoom.
    protected override void OnDpiChanged(DpiScale oldDpiScaleInfo, DpiScale newDpiScaleInfo)
    {
        base.OnDpiChanged(oldDpiScaleInfo, newDpiScaleInfo);
        ApplyDpiFactor(newDpiScaleInfo.DpiScaleX);
    }

    // Lê o DPI do monitor onde este controle está DE VERDADE (precisa estar conectado a um
    // PresentationSource — Loaded/troca de aba, nunca o construtor) e propaga pro doc CORRENTE.
    private void PushCurrentDpiFactor() => ApplyDpiFactor(VisualTreeHelper.GetDpi(this).DpiScaleX);

    /// Extraído (mesmo padrão de `ComputeAnchoredOffset`/`IsCurrentDocument` abaixo) pra ser TESTÁVEL
    /// sem depender de uma mudança de DPI REAL do SO (não simulável em teste — só o Windows real dispara
    /// `OnDpiChanged`, arrastando a janela entre monitores físicos de escalas diferentes) nem de estar
    /// conectado a um monitor real (`VisualTreeHelper.GetDpi` exige isso). `internal`: só esta View e os
    /// testes (`InternalsVisibleTo`) precisam chamar isto diretamente — a UI de produção nunca escolhe o
    /// fator, só REPASSA o que o SO informou.
    internal void ApplyDpiFactor(double factor)
    {
        if (DataContext is DocumentViewModel doc) doc.DpiFactor = factor;
    }

    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        CancelScrollReapply(); // I1-A: roda do mouse (com ou sem Ctrl) é entrada do usuário
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
        if (DataContext is not DocumentViewModel doc) return;
        if (e.Delta > 0) doc.ZoomInCommand.Execute(null); else doc.ZoomOutCommand.Execute(null);
        e.Handled = true;
    }

    // Task 8 (Plano 2b): teclas de leitura (PageUp/PageDown/setas/Home/End) não funcionam "de graça"
    // na ListBox — o ItemContainerStyle desativa Focusable nos containers e zera o template de seleção
    // (Task 3/5), então a navegação por teclado NATIVA da ListBox (que tenta selecionar/focar o
    // primeiro/próximo item pra depois de ScrollIntoView nele) não acha nenhum contêiner focável pra
    // mirar: na prática PageDown não fazia NADA, e a seta pra baixo pulava pro TOPO (a ListBox cai de
    // volta pro item 0 quando não acha nada pra navegar a partir do "selecionado" atual). Delegamos
    // direto pro ScrollViewer subjacente e marcamos Handled pra matar o comportamento nativo antes que
    // ele rode. Só intercepta quando o foco de teclado está DENTRO do PageList — com o foco na
    // SearchBar (Ctrl+F), digitar não pode rolar o documento por baixo.
    //
    // I1-A: entrada do usuário cancela o loop de convergência do scroll-to-hit ANTES de qualquer outra
    // coisa (mesma ordem do PreviewMouseDown/PreviewMouseWheel acima) — mesmo quando a tecla não é uma
    // das de rolagem tratadas aqui.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        CancelScrollReapply();

        if (DataContext is not DocumentViewModel doc) return;
        if (!PageList.IsKeyboardFocusWithin) return; // foco na SearchBar (ou fora) -> não intercepta

        // Task 7 (Plano 3a): Del exclui a anotação SELECIONADA — checado ANTES do guard de
        // ScrollViewer abaixo (não depende dele, ao contrário das teclas de leitura) e só faz algo
        // quando HÁ uma anotação selecionada (brief: "Wire keyboard Del in the viewer only when an
        // annotation is selected" — sem seleção, Del cai no comportamento padrão do WPF, que aqui é
        // nenhum, então não quebra nada de existente).
        //
        // Minor rider (revisão Opus): `e.Handled` só vira `true` quando `CanExecute` também é true —
        // dentro do MESMO `if`, não como 2 passos separados. Marcar "tratado" um Del que na verdade não
        // fez NADA (CanExecute falso, ex.: documento virou assinado com a anotação ainda selecionada)
        // reivindicaria o evento sem entregar o comportamento — o teclado ficaria "comendo" o Del sem
        // motivo, em vez de deixar o comportamento padrão do WPF (aqui, nenhum) seguir seu curso normal.
        if (e.Key == Key.Delete && doc.SelectedAnnotation is not null && doc.DeleteSelectedAnnotationCommand.CanExecute(null))
        {
            doc.DeleteSelectedAnnotationCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Task 1/2 (Plano 8): Esc cancela a caixa ajustável do carimbo em curso (Drawing OU Adjusting) —
        // mesmo formato do Del acima (guard por estado antes de marcar Handled; CancelStampBox já é
        // idempotente/seguro fora dessas fases, mas o guard aqui evita "comer" um Esc que não fazia
        // nada, mesmo espírito do "minor rider" documentado no Del). COMENTÁRIO CORRIGIDO (revisão final
        // da branch — a versão anterior estava desatualizada): desde a Task 2, o mouse-down na página com
        // SignatureStamp ativo já chama BeginStampBoxPlacementAsync de verdade (ver
        // Page_MouseLeftButtonDown) — esta fase FICA != None em produção real, este branch está ATIVO no
        // fluxo ao vivo, não mais dormente esperando um gatilho futuro.
        if (e.Key == Key.Escape && doc.StampPlacementPhase != StampPlacementPhase.None)
        {
            doc.CancelStampBox();
            e.Handled = true;
            return;
        }

        var scrollViewer = FindScrollViewer();
        if (scrollViewer is null) return;

        switch (e.Key)
        {
            case Key.PageDown: scrollViewer.PageDown(); break;
            case Key.PageUp: scrollViewer.PageUp(); break;
            case Key.Down: scrollViewer.LineDown(); break;
            case Key.Up: scrollViewer.LineUp(); break;
            case Key.Home: scrollViewer.ScrollToHome(); break;
            case Key.End: scrollViewer.ScrollToEnd(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// Aborta o loop de convergência do scroll-to-hit ativo, se houver (I1-A) — chamado a cada gesto
    /// de entrada do usuário (roda, mouse-down, tecla) E internamente por ScrollToPage antes de
    /// iniciar um novo loop (só um de cada vez, mesmo espírito dos outros "staleness guards" deste
    /// arquivo, ex.: IsCurrentDocument).
    private void CancelScrollReapply()
    {
        if (_activeScrollReapply is not { } handler) return;
        if (FindScrollViewer() is { } sv) sv.ScrollChanged -= handler;
        _activeScrollReapply = null;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is DocumentViewModel doc) doc.UpdateCurrentPageFromScroll(e.VerticalOffset);
    }

    // O DataContext deste controle pode ser TROCADO em vez de recriado (o ContentTemplate da aba
    // reaproveita o mesmo PdfViewerControl ao trocar de aba) — por isso assinamos/desassinamos aqui
    // em vez de assumir uma instância nova por documento; mesmo cuidado dos padrões de reciclagem
    // já usados em Page_DataContextChanged.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DocumentViewModel oldDoc)
        {
            oldDoc.PropertyChanged -= OnDocumentPropertyChanged;
            oldDoc.ScrollToPageRequested -= ScrollToPage;
            oldDoc.FitWidthRecalcRequested -= OnFitWidthRecalcRequested;
            oldDoc.Search.PropertyChanged -= OnSearchPropertyChanged;
        }
        if (e.NewValue is DocumentViewModel newDoc)
        {
            _lastZoom = newDoc.Zoom;
            newDoc.PropertyChanged += OnDocumentPropertyChanged;
            newDoc.ScrollToPageRequested += ScrollToPage;
            newDoc.FitWidthRecalcRequested += OnFitWidthRecalcRequested;
            newDoc.Search.PropertyChanged += OnSearchPropertyChanged;
            // Task 2 (Plano 9): troca de ABA (DataContext trocado num controle RECICLADO, já conectado a
            // uma janela/monitor de verdade — ao contrário da 1ª criação, coberta pelo Loaded do
            // construtor) — o doc NOVO precisa do fator de DPI JÁ CONHECIDO deste monitor na hora, sem
            // esperar um Loaded que não vai disparar de novo (o controle não sai/entra na árvore visual
            // numa troca de aba). DataContext já reflete `newDoc` neste ponto (callback roda DEPOIS da
            // propriedade ser escrita), então PushCurrentDpiFactor resolve pro doc certo.
            PushCurrentDpiFactor();
        }
    }

    /// Deferência (Task 2, Plano 5) — ver doc XML de `DocumentViewModel.FitWidthRecalcRequested`: o VM
    /// não sabe medir pixels, só pede o recálculo; esta View é quem tem `ViewportWidth`
    /// (`PageList.ActualWidth`) E sabe que ele só volta a refletir a largura real DEPOIS que o layout
    /// processar a troca Collapsed->Visible do organizador fechando — mesmo adiamento com
    /// `DispatcherPriority.Loaded` já usado por `FocusSearchBar`/`OnDocumentPropertyChanged` acima, pelo
    /// mesmo motivo exato (layout pendente).
    private void OnFitWidthRecalcRequested()
    {
        if (DataContext is not DocumentViewModel doc) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Staleness guard: mesma disciplina de OnDocumentPropertyChanged acima — uma troca de aba
            // rápida entre o pedido e o layout terminar não pode aplicar o FitWidth calculado pro
            // documento ANTIGO na aba NOVA que ocupou este mesmo controle reciclado.
            if (!IsCurrentDocument(DataContext, doc)) return;
            doc.FitWidth(ViewportWidth);
        });
    }

    // I5 (revisão Task 5): quando a barra fecha (Esc/Fechar -> IsOpen vira false), devolve o foco ao
    // PageList — sem isso, PageUp/PageDown ficavam mortos depois de fechar a busca (o TextBox, agora
    // invisível, ainda segurava o foco de teclado). Mesmo motivo já documentado no construtor
    // (`Loaded += (_, _) => PageList.Focus()`), só que disparado pelo fechamento em vez do Loaded.
    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.IsOpen) && sender is SearchViewModel { IsOpen: false })
            PageList.Focus();
    }

    /// Rola a visão até o hit CORRENTE da busca ficar visível (Task 5, revisado em I1/I1-A/I1-B).
    /// Alvo: topo acumulado da página (DocumentViewModel.PageTopOffsetPx — aproximado, ver ressalva
    /// no XML doc de lá) + Y do retângulo do hit corrente (CurrentHighlightRects, já preenchido pelo
    /// DocumentViewModel ANTES de disparar este evento — ver M-3 em
    /// DocumentViewModelTests.Search_RealFixture_HighlightsOnlyTheHitPage) - 1/3 do viewport,
    /// deixando o hit perto do terço superior da tela em vez de colado na borda ou (o bug original)
    /// fora da tela inteiramente quando o hit cai na metade de baixo de uma página maior que o
    /// viewport (zoom "largura").
    ///
    /// ACHADO (medido via instrumentação temporária, não hipótese): um único ScrollToVerticalOffset
    /// não é suficiente para um salto GRANDE num VirtualizingStackPanel com poucas páginas realizadas
    /// (CacheLength="1,1" Page) — a primeira chamada é normalizada pra um valor BEM diferente do
    /// pedido (a estimativa de altura média do painel só fica exata depois que ele realiza containers
    /// perto do alvo), e o valor efetivo só converge GRADUALMENTE ao longo de VÁRIOS ScrollChanged
    /// subsequentes. Por isso reaplica o alvo a CADA ScrollChanged até o HIT estar DENTRO do viewport
    /// — I1-B: esse é o requisito real, e converge em bem menos passes do que exigir um offset exato
    /// (que a imprecisão de PageTopOffsetPx nunca cravaria de qualquer forma) — ou esgotar um teto de
    /// tentativas; nesse caso (I1-B) cai pro ScrollIntoView antigo, que ao menos garante a PÁGINA
    /// visível — pior caso nunca pior que o comportamento anterior a esta revisão, nunca um offset
    /// arbitrário largado no meio do nada.
    ///
    /// I1-A: qualquer entrada do usuário aborta o loop na hora (ver CancelScrollReapply, assinado no
    /// construtor). E se a PRIMEIRA chamada de ScrollToVerticalOffset abaixo for um NO-OP (alvo já é
    /// o offset atual), nenhum ScrollChanged dispara pra desarmar o handler sozinho — sem tratar
    /// isso, o handler ficava pendurado pra sempre esperando um scroll que não é nosso, e "puxava" de
    /// volta o PRÓXIMO scroll manual do usuário. Por isso o desarme NÃO depende só do evento: uma
    /// checagem adiada em prioridade Input desarma se ainda estiver pendurado pouco depois de iniciar.
    public void ScrollToPage(int pageIndex)
    {
        if (DataContext is not DocumentViewModel doc) return;
        if (pageIndex < 0 || pageIndex >= doc.Pages.Count) return;
        var page = doc.Pages[pageIndex];

        var scrollViewer = FindScrollViewer();
        if (scrollViewer is null) return;

        CancelScrollReapply(); // só 1 loop de convergência por vez

        double Target()
        {
            double t = doc.PageTopOffsetPx(pageIndex);
            if (page.CurrentHighlightRects.Count > 0)
                t += page.CurrentHighlightRects.Min(r => r.Y) - scrollViewer.ViewportHeight / 3;
            double maxOffset = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
            return Math.Clamp(t, 0, maxOffset);
        }

        // I1-B: "visível" = o hit CORRENTE cabe inteiro dentro do viewport atual. SEM hit corrente
        // nesta página — chamador é a navegação por miniatura (Task 6), não a busca — "visível" vira
        // "já estamos no offset que Target() calcula pra essa página (o topo dela)": sem essa
        // checagem, todo clique de miniatura numa página sem highlight caía direto num antigo
        // `return true` aqui e o método virava no-op (nunca chegava a chamar ScrollToVerticalOffset).
        // Comparar contra Target() (em vez de "página inteira cabe no viewport") também evita um
        // segundo bug: uma página mais alta que o viewport (zoom grande) NUNCA caberia inteira,
        // estourando sempre o teto de 25 tentativas antes de cair no fallback ScrollIntoView.
        bool HitVisible()
        {
            if (page.CurrentHighlightRects.Count == 0)
                return Math.Abs(scrollViewer.VerticalOffset - Target()) < 1.0;
            double pageTop = doc.PageTopOffsetPx(pageIndex);
            double hitTop = pageTop + page.CurrentHighlightRects.Min(r => r.Y);
            double hitBottom = pageTop + page.CurrentHighlightRects.Max(r => r.Y + r.Height);
            double viewTop = scrollViewer.VerticalOffset;
            double viewBottom = viewTop + scrollViewer.ViewportHeight;
            return hitTop >= viewTop && hitBottom <= viewBottom;
        }

        if (HitVisible()) return; // já visível (ex.: Próximo dentro da mesma página) — nada a fazer

        int attempts = 0;
        void Reapply(object? s, ScrollChangedEventArgs e)
        {
            attempts++;
            bool stale = !IsCurrentDocument(DataContext, doc);
            bool done = stale || HitVisible();
            bool capped = !done && attempts >= 25;

            if (done || capped)
            {
                scrollViewer.ScrollChanged -= Reapply;
                if (_activeScrollReapply == Reapply) _activeScrollReapply = null;
                // I1-B: esgotou o teto sem convergir -> cai pro comportamento pré-revisão (garante ao
                // menos a página, nunca deixa a view largada num offset arbitrário no meio do nada).
                if (capped) PageList.ScrollIntoView(page);
                return;
            }
            scrollViewer.ScrollToVerticalOffset(Target());
        }
        _activeScrollReapply = Reapply;
        scrollViewer.ScrollChanged += Reapply;
        scrollViewer.ScrollToVerticalOffset(Target());

        // I1-A: teardown garantido mesmo se a chamada acima não tiver disparado NENHUM ScrollChanged.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_activeScrollReapply == Reapply) CancelScrollReapply();
        });
    }

    /// Abre o foco na barra de busca (Ctrl+F, chamado pela MainWindow) — adiado com prioridade
    /// Loaded pro layout terminar de aplicar a Visibility (Collapsed->Visible) antes do Focus, mesmo
    /// exemplar de adiamento pós-layout já usado em OnDocumentPropertyChanged pro âncora de zoom.
    public void FocusSearchBar() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SearchBarControl.FocusQueryBox());

    // Âncora de zoom: reposiciona o offset de rolagem para a posição de leitura não "pular" quando
    // o zoom muda (ex.: Ctrl+scroll no meio da página).
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DocumentViewModel.Zoom) || sender is not DocumentViewModel doc) return;

        // Task 8 (Plano 3a, achado de revisão): um gesto de DESENHO em curso acumula TODOS os pontos em
        // PX DE TELA cru, nunca reconvertidos (ver doc XML das propriedades de prévia em PageViewModel —
        // diferente de SelectionRects/AnnotationSelectionRect, que guardam uma fonte em PONTOS e
        // reconvertem em ApplyZoom). Um zoom mudando NO MEIO do gesto (Ctrl+scroll — não coberto pelo
        // brief, mas alcançável) deixaria a prévia visualmente desalinhada da página (que já
        // redimensionou) e, pior, Page_MouseLeftButtonUp converteria TODOS os pontos usando o zoom do
        // MOMENTO DO COMMIT, não o zoom de quando cada ponto foi capturado — geometria commitada
        // SILENCIOSAMENTE ERRADA, não só uma prévia feia. Aborta o gesto (mesma escolha conservadora de
        // Page_LostMouseCapture: nunca commita um gesto interrompido) em vez de tentar reconciliar.
        if (_drawingPage is not null) ResetGestureState();

        double oldZoom = _lastZoom, newZoom = doc.Zoom;
        _lastZoom = newZoom;
        if (oldZoom <= 0 || oldZoom == newZoom) return;

        var scrollViewer = FindScrollViewer();
        if (scrollViewer is null) return;
        double oldOffset = scrollViewer.VerticalOffset;

        // O layout ainda não rodou (as páginas ainda têm o DisplayWidth/Height antigos) — adia com
        // prioridade Loaded para aplicar o offset só depois que o layout terminar de recalcular os
        // novos tamanhos, exemplar: como OnScrollChanged já lê o offset direto do ScrollViewer.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Staleness guard: entregas de render enfileiram em prioridade Normal (> Loaded), então
            // numa troca de aba rápida durante um re-render pesado este callback adiado pode rodar
            // DEPOIS do DataContext já ter mudado — sem isso, aplicaríamos o offset calculado para
            // o documento/aba ANTIGO no _scrollViewer (cache compartilhado da instância) que agora
            // pertence à aba NOVA, corrompendo visualmente a rolagem da aba errada.
            if (!IsCurrentDocument(DataContext, doc)) return;
            scrollViewer.ScrollToVerticalOffset(ComputeAnchoredOffset(oldOffset, oldZoom, newZoom));
        });
    }

    // Extraído como método estático puro (sem UI) para ser testável diretamente — ver ZoomAnchorTests.
    public static double ComputeAnchoredOffset(double oldOffset, double oldZoom, double newZoom) =>
        oldOffset * (newZoom / oldZoom);

    // Decisão do staleness guard acima, extraída para ser testável sem precisar despachar/pumpar o
    // Dispatcher (ver ZoomAnchorTests.IsCurrentDocument_DetectsDataContextSwap).
    public static bool IsCurrentDocument(object? currentDataContext, DocumentViewModel expected) =>
        ReferenceEquals(currentDataContext, expected);

    private ScrollViewer? FindScrollViewer() => _scrollViewer ??= FindVisualChild<ScrollViewer>(PageList);

    /// Exposto pro assembly de teste (ViewerIntegrationTests) localizar o ScrollViewer real da lista
    /// de páginas. Necessário desde a Task 5: um FindVisualChild&lt;ScrollViewer&gt; varrendo a partir
    /// da RAIZ do controle (que agora inclui a SearchBar, docada acima do PageList) acharia primeiro
    /// o ScrollViewer INTERNO do TextBox da busca (todo TextBox do WPF tem um por padrão, pro próprio
    /// template de rolagem de texto) em vez do ScrollViewer do PageList — scroll no alvo errado.
    public ScrollViewer? FindPageListScrollViewer() => FindScrollViewer();

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PageViewModel p) p.OnRealized();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PageViewModel p) p.OnDerealized();
    }

    // Recycling: Loaded/Unloaded podem AMBOS ser pulados quando um container reciclado troca de
    // DataContext sem sair/entrar na árvore visual. DataContextChanged cobre essa lacuna.
    private void Page_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Só um container NA ÁRVORE troca de página de verdade; container no pool
        // (IsLoaded=false) já derealizou via Unloaded — derealizar de novo aqui
        // atingiria o VM antigo que pode estar visível em OUTRO container.
        if (sender is not FrameworkElement { IsLoaded: true }) return;
        if (e.OldValue is PageViewModel oldPage) oldPage.OnDerealized();
        if (e.NewValue is PageViewModel newPage) newPage.OnRealized();
    }

    // Seleção de texto (Task 3) + ferramenta de anotação/hit-test/arrastar (Task 7, Plano 3a):
    // mouse-down decide entre 4 caminhos, nesta ordem —
    //   1) ferramenta de COLOCAÇÃO ativa (Nota/Texto): o clique COLOCA uma anotação nova (one-shot, sem
    //      arrasto) e consome o gesto inteiro — nunca chega a tocar seleção de texto nem hit-test.
    //   2) ferramenta de DESENHO ativa (Ink/Rectangle/Line/Arrow, Task 8): arma um arrasto que desenha
    //      uma prévia ao vivo no overlay e COMMITA no mouse-up (ver Page_MouseMove/Up abaixo).
    //   3) sem ferramenta, clique DENTRO do retângulo de uma anotação existente (AnnotationsByPage,
    //      via HitTestAnnotation): seleciona e arma um possível arrasto de MOVER.
    //   4) nenhum dos 3: comportamento PRÉ-EXISTENTE (Task 3) — arma o arrasto de seleção de TEXTO.
    // SELEÇÃO ANTERIOR (fix do fix batch — comentário CORRIGIDO: a versão anterior afirmava que os 4
    // caminhos limpavam a seleção "primeiro", o que era FALSO pros caminhos 1-2). Os caminhos 3-4
    // limpam `SelectedAnnotation` explicitamente aqui embaixo (mesmo espírito de "clique simples
    // limpa"). Os caminhos 1-2 SÓ limpavam indiretamente — dependiam de `Session.ApplyEdit` bem-
    // sucedido disparar `OnSessionApplied` (que zera `SelectedAnnotation` como efeito colateral de
    // QUALQUER edição aplicada) — o que deixava uma JANELA com o overlay da seleção ANTIGA ainda
    // visível/obsoleto entre o mouse-down e o commit; se o diálogo fosse cancelado (caminho 1) ou o
    // gesto rejeitado pelo MIN-GESTURE GUARD (caminho 2), o commit nunca acontecia e o overlay ficava
    // preso indefinidamente. Fix: os 2 caminhos agora limpam `SelectedAnnotation` PROATIVAMENTE aqui
    // também — UX mais apertada, fecha a janela por completo (o zero de `OnSessionApplied` continua
    // acontecendo depois, redundante mas inofensivo, mesmo padrão já documentado nos outros métodos).
    private void Page_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PageViewModel page } fe) return;
        if (DataContext is not DocumentViewModel doc) return;

        var downPx = e.GetPosition(fe);
        var pagePt = TextSelection.ScreenToPagePoint(downPx, doc.Zoom, page.HeightPt);

        if (doc.ActiveTool is AnnotationTool.StickyNote or AnnotationTool.FreeText)
        {
            e.Handled = true;
            doc.SelectedAnnotation = null;
            _ = doc.PlaceAnnotationAtAsync(page.Index, pagePt.X, pagePt.Y);
            return;
        }

        // Plano 21 (Task 5): carimbo de imagem — "Inserir imagem" (PendingImageUsesBox) inicia a CAIXA
        // AJUSTÁVEL (desenhar tamanho + reposicionar), igual ao carimbo de assinatura; a GALERIA continua
        // no clique único de tamanho natural (PlaceStampAtAsync). O commit da caixa só acontece no
        // Confirmar ("Inserir aqui").
        if (doc.ActiveTool == AnnotationTool.ImageStamp)
        {
            e.Handled = true;
            doc.SelectedAnnotation = null;
            if (doc.PendingImageUsesBox)
            {
                if (doc.StampPlacementPhase == StampPlacementPhase.Adjusting) return; // clique fora da caixa em ajuste
                fe.CaptureMouse();
                _stampBoxDrawPage = page;
                _ = doc.BeginImageBoxPlacementAsync(page.Index, new PdfPoint(pagePt.X, pagePt.Y));
            }
            else
            {
                _ = doc.PlaceStampAtAsync(page.Index, pagePt.X, pagePt.Y); // galeria: clique único
            }
            return;
        }

        // Task 2 (Plano 8): carimbo visível de assinatura — o mouse-down agora inicia um ARRASTO
        // (desenhar a caixa ajustável), não mais um clique único; o commit final vai pro motor de
        // assinatura (Session.CommitSigned) só no Confirmar — ver
        // DocumentViewModel.BeginStampBoxPlacementAsync/ConfirmSignatureStampAsync. Em Adjusting, o
        // corpo/as alças do adorner já têm handlers PRÓPRIOS (StampBox_MouseLeftButtonDown/
        // StampBoxHandle_MouseLeftButtonDown, ambos marcam e.Handled=true) — um clique que chega ATÉ
        // AQUI com a fase já Adjusting caiu FORA da caixa (o usuário clicou noutro lugar da página); não
        // inicia nada (mesmo "no-op fora do alvo" de qualquer ferramenta deste app).
        if (doc.ActiveTool == AnnotationTool.SignatureStamp)
        {
            e.Handled = true;
            doc.SelectedAnnotation = null;
            if (doc.StampPlacementPhase == StampPlacementPhase.Adjusting) return;
            fe.CaptureMouse();
            _stampBoxDrawPage = page;
            _ = doc.BeginStampBoxPlacementAsync(page.Index, new PdfPoint(pagePt.X, pagePt.Y));
            return;
        }

        if (doc.ActiveTool is AnnotationTool.Ink or AnnotationTool.Rectangle or AnnotationTool.Line or AnnotationTool.Arrow)
        {
            e.Handled = true;
            doc.SelectedAnnotation = null;
            fe.CaptureMouse();
            _drawingPage = page;
            _drawingTool = doc.ActiveTool;
            _drawingPointsPx.Clear();
            _drawingPointsPx.Add(downPx);
            StartDrawingPreview(page, doc.ActiveTool, downPx);
            return;
        }

        if (doc.HitTestAnnotation(page.Index, pagePt.X, pagePt.Y) is { } hit)
        {
            doc.ClearSelection();
            doc.SelectedAnnotation = hit;
            // Plano 21 (Task 5): clicar numa IMAGEM já colocada abre a caixa ajustável sobre ela (mover +
            // redimensionar via alças/corpo), em vez do arrasto genérico (ImageStamp nunca foi liftável
            // pelo caminho genérico — ver MoveSelectedAnnotationAsync). Sem bytes no cache (doc reaberto),
            // BeginImageEditBox avisa e não abre — a imagem fica só selecionada.
            if (hit.Kind == AnnotationKind.ImageStamp)
            {
                e.Handled = true;
                doc.BeginImageEditBox(hit);
                return;
            }
            _draggingAnnotation = hit;
            _draggingAnnotationPage = page;
            _annotationDragAnchorPx = downPx;
            _annotationDragMoved = false;
            fe.CaptureMouse();
            e.Handled = true;
            // duplo-clique (brief: "double-click (or an ✏ button)") edita o Content via o mesmo diálogo
            // — SÓ quando o comando pode mesmo rodar (Task 8: CanEditSelectedAnnotationCommand agora
            // filtra por Kind — StickyNote/FreeText apenas — pra Ink/Rectangle/Line/Arrow não abrirem
            // "Editar caixa de texto" à toa; ExecuteAsync() sozinho NÃO checa CanExecute).
            if (e.ClickCount == 2 && doc.EditSelectedAnnotationCommand.CanExecute(null))
                _ = doc.EditSelectedAnnotationCommand.ExecuteAsync(null);
            return;
        }

        doc.SelectedAnnotation = null;
        doc.ClearSelection();
        fe.CaptureMouse();
        _selectingPage = page;
        _selectingElement = fe;
        _mouseDownPx = downPx;
        _dragging = false;
        e.Handled = true;
    }

    private void Page_MouseMove(object sender, MouseEventArgs e)
    {
        // Task 2 (Plano 8): arrasto de DESENHO da caixa do carimbo (Drawing) -- feed UpdateDrawTo com o
        // ponto de página do mouse-move (o overlay já bindado no VM se atualiza sozinho, nenhuma prévia
        // separada precisa ser mantida aqui, ao contrário do gesto de DESENHO de Ink/Rectangle/etc. logo
        // abaixo -- StampBoxRect JÁ É a fonte de verdade que o adorner desenha).
        if (_stampBoxDrawPage is { } stampDrawPage)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (DataContext is not DocumentViewModel doc) return;
            if (sender is not FrameworkElement fe) return;
            var pos = e.GetPosition(fe);
            var pagePt = TextSelection.ScreenToPagePoint(pos, doc.Zoom, stampDrawPage.HeightPt);
            doc.UpdateDrawTo(new PdfPoint(pagePt.X, pagePt.Y));
            return;
        }

        // Task 2 (Plano 8): arrasto de MOVER a caixa inteira (Adjusting, corpo) -- delta INCREMENTAL
        // desde a ÚLTIMA chamada (MoveBoxBy espera delta desde a ÚLTIMA chamada, não desde o mouse-down
        // -- mesmo contrato de ResizeBoxByHandle abaixo, ver doc XML de lá).
        if (_stampBoxMovePage is { } stampMovePage)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (DataContext is not DocumentViewModel doc) return;
            if (sender is not FrameworkElement fe) return;
            var pos = e.GetPosition(fe);
            var pagePt = TextSelection.ScreenToPagePoint(pos, doc.Zoom, stampMovePage.HeightPt);
            doc.MoveBoxBy(new PdfPoint(pagePt.X - _stampBoxLastPagePt.X, pagePt.Y - _stampBoxLastPagePt.Y));
            _stampBoxLastPagePt = pagePt;
            return;
        }

        // Task 2 (Plano 8): arrasto de REDIMENSIONAR por uma alça (Adjusting) -- mesmo delta
        // incremental do move acima, aplicado à alça capturada no mouse-down.
        if (_stampBoxResizePage is { } stampResizePage)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (DataContext is not DocumentViewModel doc) return;
            if (sender is not FrameworkElement fe) return;
            var pos = e.GetPosition(fe);
            var pagePt = TextSelection.ScreenToPagePoint(pos, doc.Zoom, stampResizePage.HeightPt);
            doc.ResizeBoxByHandle(_stampBoxResizeHandle,
                new PdfPoint(pagePt.X - _stampBoxLastPagePt.X, pagePt.Y - _stampBoxLastPagePt.Y));
            _stampBoxLastPagePt = pagePt;
            return;
        }

        // Gesto de DESENHO em curso (Task 8): atualiza a prévia ao vivo no overlay da própria página
        // (nunca toca Session/ApplyEdit — isso só acontece no commit, no mouse-up).
        if (_drawingPage is { } drawPage)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (sender is not FrameworkElement fe) return;
            var pos = e.GetPosition(fe);

            if (_drawingTool == AnnotationTool.Ink)
            {
                // throttle (brief: "every N px moved") — evita 1 ponto por pixel/evento de MouseMove
                // (WPF dispara MUITOS eventos por segundo); a polilinha final ainda fica suave o
                // bastante pro traçado de um mouse/caneta normal.
                if ((pos - _drawingPointsPx[^1]).Length < InkThrottlePx) return;
                _drawingPointsPx.Add(pos);
                drawPage.InkPreviewPoints.Add(pos);
            }
            else
            {
                // Rectangle/Line/Arrow: só o ponto FINAL importa pro commit (rubber-band) — SUBSTITUI o
                // último elemento em vez de acumular (ao contrário do Ink acima).
                if (_drawingPointsPx.Count > 1) _drawingPointsPx[^1] = pos; else _drawingPointsPx.Add(pos);
                UpdateDrawingPreview(drawPage, _drawingTool, _drawingPointsPx[0], pos);
            }
            return;
        }

        // Arrasto de ANOTAÇÃO em curso (Task 7): live preview — desloca o overlay pelo MESMO delta de
        // tela do arrasto, sem tocar Session/ApplyEdit ainda (isso só acontece no mouse-up).
        if (_draggingAnnotation is { } dragged && _draggingAnnotationPage is { } dragPage)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (DataContext is not DocumentViewModel doc) return;
            if (sender is not FrameworkElement fe) return;

            var pos = e.GetPosition(fe);
            if (!_annotationDragMoved && (pos - _annotationDragAnchorPx).Length < DragThresholdPx) return;
            _annotationDragMoved = true;

            double dx = pos.X - _annotationDragAnchorPx.X, dy = pos.Y - _annotationDragAnchorPx.Y;
            var baseRect = PageViewModel.PointRectToScreenRect(
                dragged.LeftPt, dragged.BottomPt, dragged.RightPt, dragged.TopPt, doc.Zoom, dragPage.HeightPt);
            dragPage.AnnotationSelectionRect = new Rect(baseRect.X + dx, baseRect.Y + dy, baseRect.Width, baseRect.Height);
            return;
        }

        if (_selectingPage is null || _selectingElement is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var selPos = e.GetPosition(_selectingElement);
        if (!_dragging)
        {
            // ainda dentro do limiar: não é arrasto (pode virar um clique simples só) — não faz nada
            // ainda, em particular não carrega a TextPage à toa por um tremor de mouse num clique.
            if ((selPos - _mouseDownPx).Length < DragThresholdPx) return;
            _dragging = true;
            _selectingPage.BeginSelection(_mouseDownPx);   // âncora fixa no ponto de mouse-down
        }
        _selectingPage.UpdateSelection(selPos);
    }

    private void Page_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Task 2 (Plano 8): solta o arrasto de DESENHO -- EndStampDraw decide sozinho se o retângulo já
        // é válido (Adjusting) ou se ficou pequeno demais (permanece Drawing, aviso sutil, ver doc XML
        // de EndStampDraw); a View não precisa saber qual dos 2 aconteceu.
        if (_stampBoxDrawPage is not null)
        {
            (sender as FrameworkElement)?.ReleaseMouseCapture();
            _stampBoxDrawPage = null;
            if (DataContext is DocumentViewModel doc) doc.EndStampDraw();
            return;
        }

        // Task 2 (Plano 8): solta o arrasto de MOVER/REDIMENSIONAR -- StampBoxRect já foi atualizado ao
        // VIVO a cada MouseMove (MoveBoxBy/ResizeBoxByHandle mutam o retângulo bindado diretamente,
        // nenhum "commit" separado precisa acontecer aqui, ao contrário do gesto de anotação abaixo).
        // FIX (revisão final da branch, achado I1/I2 do revisor): EndAdjustGesture() SEMPRE na fronteira
        // do gesto -- ResizeBoxByHandle pode legitimamente deixar os 4 escalares brutos INVERTIDOS (um
        // cruzamento, ver doc XML de lá), e sem canonicalizar aqui o PRÓXIMO gesto (um novo mouse-down,
        // possivelmente MoveBoxBy ou uma alça diferente) herdava essa inversão -- ver doc XML de
        // EndAdjustGesture em DocumentViewModel pros 2 bugs reais que isso causava.
        if (_stampBoxMovePage is not null)
        {
            (sender as FrameworkElement)?.ReleaseMouseCapture();
            _stampBoxMovePage = null;
            if (DataContext is DocumentViewModel doc) doc.EndAdjustGesture();
            return;
        }
        if (_stampBoxResizePage is not null)
        {
            (sender as FrameworkElement)?.ReleaseMouseCapture();
            _stampBoxResizePage = null;
            if (DataContext is DocumentViewModel doc) doc.EndAdjustGesture();
            return;
        }

        if (_drawingPage is { } drawPage)
        {
            // Mesma ordem "captura pra locais ANTES de liberar a captura" do arrasto de anotação abaixo
            // (ReleaseMouseCapture pode disparar Page_LostMouseCapture SINCRONAMENTE).
            var pointsPx = _drawingPointsPx.ToList();
            (sender as FrameworkElement)?.ReleaseMouseCapture();
            drawPage.ClearDrawingPreview();
            _drawingPage = null;

            // O GUARD de gesto mínimo (brief: "drag < 3px -> no commit") vive no VM
            // (DocumentViewModel.CommitDrawingAsync), não aqui — ver doc XML de lá. A View só converte
            // px->pt (ScreenToPagePoint, mesma conversão de sempre) e delega; mesmo 1 ponto só (clique
            // sem nenhum move) chega até CommitDrawingAsync, que descarta um path degenerado sozinho.
            if (DataContext is DocumentViewModel doc)
            {
                var pathPt = pointsPx
                    .Select(p => TextSelection.ScreenToPagePoint(p, doc.Zoom, drawPage.HeightPt))
                    .Select(p => new PdfPoint(p.X, p.Y))
                    .ToList();
                _ = doc.CommitDrawingAsync(drawPage.Index, pathPt);
            }
            return;
        }

        if (_draggingAnnotation is { } dragged && _draggingAnnotationPage is not null)
        {
            // Captura os valores ANTES de ReleaseMouseCapture: liberar a captura pode disparar
            // Page_LostMouseCapture SINCRONAMENTE (mesmo aviso já documentado ali, "Minor rider"), que
            // zeraria _annotationDragMoved/_draggingAnnotation ANTES de eu conseguir lê-los se a ordem
            // fosse "libera primeiro, lê depois" — lendo pra locais primeiro, a ordem não importa mais.
            bool moved = _annotationDragMoved;
            var pos = e.GetPosition(sender as IInputElement);
            var anchor = _annotationDragAnchorPx;
            (sender as FrameworkElement)?.ReleaseMouseCapture();

            if (moved && DataContext is DocumentViewModel doc)
            {
                double dxPx = pos.X - anchor.X, dyPx = pos.Y - anchor.Y;
                double scale = doc.Zoom * PageViewModel.PtToPx;
                double newLeftPt = dragged.LeftPt + dxPx / scale;
                // Y de tela cresce pra baixo; PDF cresce pra cima — mesmo espelhamento de FillScreenRects.
                double newBottomPt = dragged.BottomPt - dyPx / scale;
                _ = doc.MoveSelectedAnnotationAsync(newLeftPt, newBottomPt);
            }
            _draggingAnnotation = null;
            _draggingAnnotationPage = null;
            _annotationDragMoved = false;
            return;
        }

        (sender as FrameworkElement)?.ReleaseMouseCapture();
        _selectingPage = null;
        _selectingElement = null;
        _dragging = false;
    }

    /// Escreve o ponto INICIAL da prévia de desenho (mouse-down) na forma certa pra ferramenta —
    /// exemplar UpdateDrawingPreview abaixo (chamado a cada MouseMove subsequente).
    private static void StartDrawingPreview(PageViewModel page, AnnotationTool tool, Point downPx)
    {
        switch (tool)
        {
            case AnnotationTool.Ink:
                page.InkPreviewPoints.Add(downPx);
                page.HasInkPreview = true;
                break;
            case AnnotationTool.Rectangle:
                page.RectPreviewRect = new Rect(downPx, downPx);
                page.HasRectPreview = true;
                break;
            case AnnotationTool.Line:
            case AnnotationTool.Arrow:
                page.LinePreviewStart = downPx;
                page.LinePreviewEnd = downPx;
                page.HasLinePreview = true;
                break;
        }
    }

    /// Recalcula a prévia (rubber-band) de Rectangle/Line/Arrow a cada MouseMove — Ink não passa por
    /// aqui (sua prévia só ACUMULA pontos, ver Page_MouseMove).
    private static void UpdateDrawingPreview(PageViewModel page, AnnotationTool tool, Point anchorPx, Point currentPx)
    {
        switch (tool)
        {
            case AnnotationTool.Rectangle:
                page.RectPreviewRect = new Rect(anchorPx, currentPx);
                break;
            case AnnotationTool.Line:
            case AnnotationTool.Arrow:
                page.LinePreviewEnd = currentPx;
                break;
        }
    }

    // Botões flutuantes do adorner da caixa ajustável do carimbo — Click (não Command/RelayCommand):
    // mesmo precedente de todo gesto de mouse já tratado em código-behind neste arquivo;
    // ConfirmSignatureStampAsync/CancelStampBox não precisam de CanExecute próprio porque a Visibility
    // do StackPanel (IsStampBoxAdjusting) já é o gate — um botão invisível nunca recebe clique. Task 2
    // (Plano 8): "✔ Assinar aqui" agora dispara o motor de verdade (ConfirmSignatureStampAsync arma o
    // funil e chama SignCoreAsync com o rect AJUSTADO) — fire-and-forget, mesmo padrão de
    // PlaceSignatureStampAtAsync original (o próprio método trata/notifica qualquer falha).
    private void StampBoxConfirm_Click(object sender, RoutedEventArgs e)
    {
        // Plano 21 (Task 5): confirmar UNIFICADO — roteia por propósito (assinatura/rubrica -> motor;
        // imagem nova -> adiciona anotação; edição de imagem -> lift), ver DocumentViewModel.ConfirmStampBoxAsync.
        if (DataContext is DocumentViewModel doc) _ = doc.ConfirmStampBoxAsync();
    }

    private void StampBoxCancel_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DocumentViewModel doc) doc.CancelStampBox();
    }

    /// Mouse-down no CORPO da caixa (Adjusting) — inicia o arrasto de MOVER (Task 2, Plano 8). Captura
    /// no BORDER DA PÁGINA (achado via FindPageBorder, não no Grid clicado) — ver doc XML dos campos
    /// `_stampBoxDrawPage`/`_stampBoxMovePage`/`_stampBoxResizePage` acima pro porquê (Page_MouseMove/Up
    /// centralizados no Border continuam sendo o único lugar que processa o resto do gesto). Marca
    /// Handled ANTES do evento poder bubblear até Page_MouseLeftButtonDown (que trataria um clique
    /// dentro da caixa como "começar a desenhar de novo" se não fosse interceptado aqui).
    private void StampBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (DataContext is not DocumentViewModel doc) return;
        if (doc.StampBoxPageIndex < 0 || doc.StampBoxPageIndex >= doc.Pages.Count) return;
        if (FindPageBorder(fe) is not { } pageBorder) return;

        e.Handled = true;
        var page = doc.Pages[doc.StampBoxPageIndex];
        pageBorder.CaptureMouse();
        _stampBoxMovePage = page;
        _stampBoxLastPagePt = TextSelection.ScreenToPagePoint(e.GetPosition(pageBorder), doc.Zoom, page.HeightPt);
    }

    /// Mouse-down numa ALÇA de redimensionar (Adjusting) — inicia o arrasto de REDIMENSIONAR (Task 2,
    /// Plano 8). `hp.Handle` já vem pronto do DataContext do item (StampBoxHandlePoint, preenchido por
    /// DocumentViewModel.FillStampBoxHandlePoints) — nenhuma dedução por índice/posição na View. Mesmo
    /// padrão de captura-no-Border/Handled de StampBox_MouseLeftButtonDown acima.
    private void StampBoxHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StampBoxHandlePoint hp } fe) return;
        if (DataContext is not DocumentViewModel doc) return;
        if (doc.StampBoxPageIndex < 0 || doc.StampBoxPageIndex >= doc.Pages.Count) return;
        if (FindPageBorder(fe) is not { } pageBorder) return;

        e.Handled = true;
        var page = doc.Pages[doc.StampBoxPageIndex];
        pageBorder.CaptureMouse();
        _stampBoxResizePage = page;
        _stampBoxResizeHandle = hp.Handle;
        _stampBoxLastPagePt = TextSelection.ScreenToPagePoint(e.GetPosition(pageBorder), doc.Zoom, page.HeightPt);
    }

    /// Sobe a árvore visual a partir de `start` até achar o Border DA PÁGINA (`x:Name="PageBorder"`, raiz
    /// do DataTemplate por página — ver PdfViewerControl.xaml) — usado pelos 2 handlers acima pra achar o
    /// Border a partir de um elemento filho do adorner (alça ou corpo da caixa). NÃO um
    /// `FindAncestor&lt;Border&gt;` genérico de propósito (achado ao vivo, revisão pós-implementação): o
    /// ItemsControl das alças usa o TEMA PADRÃO (nenhum Template customizado neste XAML), que embrulha
    /// seu conteúdo num Border PRÓPRIO (chrome do tema) — um Border mais PRÓXIMO na árvore que o da
    /// página, e que HERDA o MESMO DataContext (PageViewModel) por propagação normal do WPF; filtrar só
    /// por tipo (`is Border`) OU por DataContext teria capturado/soltado o mouse no Border ERRADO (o
    /// chrome do ItemsControl, sem `Page_MouseLeftButtonUp` nenhum ligado — a captura nunca soltava).
    private static Border? FindPageBorder(DependencyObject start)
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Border { Name: "PageBorder" } border) return border;
        return null;
    }

    // Minor rider (revisão final): captura pode ser perdida SEM um MouseLeftButtonUp correspondente —
    // ex.: janela desativa (Alt+Tab, outro app ganha foco) no meio de um arrasto de seleção. Sem este
    // handler, o estado de arrasto ficava "preso" até o próximo clique do usuário, que então herdava um
    // estado obsoleto (ex.: um MouseMove gerado antes do próximo MouseLeftButtonDown, com o mouse já
    // sobre outra página, atualizaria a seleção/prévia errada). Mesmo reset de MouseLeftButtonUp, SEM
    // chamar ReleaseMouseCapture — a captura já está perdida quando este evento dispara, chamá-lo de
    // novo não faz sentido.
    //
    // Task 8 (Plano 3a): reset UNIFICADO dos 3 grupos de estado de arrasto deste arquivo (texto/Task 3,
    // anotação/Task 7, desenho/Task 8) num único método (`ResetGestureState`) — evitar uma 3ª cópia
    // quase-idêntica do mesmo bloco de reset é o "shared helper" que a task pediu (a lógica de INÍCIO/
    // ATUALIZAÇÃO de cada gesto continua distinta de propósito — comportamentos realmente diferentes —
    // só o RESET, que é sempre "zera tudo", ganhou 1 método só).
    private void Page_LostMouseCapture(object sender, MouseEventArgs e) => ResetGestureState();

    private void ResetGestureState()
    {
        _selectingPage = null;
        _selectingElement = null;
        _dragging = false;
        // Task 7 (Plano 3a): mesmo reset defensivo pro arrasto de ANOTAÇÃO — captura perdida no meio de
        // um arrasto (Alt+Tab etc.) aborta SEM commitar a posição (mesma escolha conservadora do texto
        // acima: Page_MouseLeftButtonUp é quem decide commitar, nunca este handler).
        _draggingAnnotation = null;
        _draggingAnnotationPage = null;
        _annotationDragMoved = false;
        // Task 8 (Plano 3a): idem pro gesto de DESENHO — aborta SEM commitar, e limpa a prévia visual
        // (senão um retângulo/traço "fantasma" ficaria desenhado na página até o próximo gesto).
        _drawingPage?.ClearDrawingPreview();
        _drawingPage = null;
        _drawingPointsPx.Clear();
        // Task 2 (Plano 8): mesmo reset defensivo pros 3 gestos da caixa ajustável do carimbo -- captura
        // perdida no meio de um arrasto (Alt+Tab etc.) só para de alimentar UpdateDrawTo/MoveBoxBy/
        // ResizeBoxByHandle; a caixa fica exatamente onde estava no último MouseMove processado (nenhum
        // "desfazer" precisa acontecer -- StampBoxRect já É o estado real, não uma prévia separada).
        // FIX (revisão final da branch, achado I1/I2 do revisor): EndAdjustGesture() TAMBÉM aqui -- perder
        // a captura no meio de um MOVER/REDIMENSIONAR (Alt+Tab etc.) é uma fronteira de gesto igual a um
        // MouseLeftButtonUp normal (ver Page_MouseLeftButtonUp) -- sem canonicalizar aqui também, um
        // cruzamento interrompido por Alt+Tab deixaria os 4 escalares brutos invertidos até o PRÓXIMO
        // gesto, o mesmo bug que o fix em Page_MouseLeftButtonUp resolve pro caminho normal.
        bool wasMovingOrResizing = _stampBoxMovePage is not null || _stampBoxResizePage is not null;
        _stampBoxDrawPage = null;
        _stampBoxMovePage = null;
        _stampBoxResizePage = null;
        if (wasMovingOrResizing && DataContext is DocumentViewModel doc) doc.EndAdjustGesture();
    }
}
