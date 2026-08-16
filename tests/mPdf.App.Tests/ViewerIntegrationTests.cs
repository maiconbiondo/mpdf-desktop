using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Signing;
using Xunit;
// Rectangle (não System.Windows.Shapes inteiro, que colide com System.IO.Path via Shapes.Path) -- usado
// só pelo Task 2 (Plano 8) STA de mouse real, pra achar a alça de redimensionar por DataContext.
using Rectangle = System.Windows.Shapes.Rectangle;

namespace mPdf.App.Tests;

/// Smoke de aceitação ponta a ponta: scheduler -> PDFium -> bitmap -> dispatcher -> tela,
/// exercitando a virtualização real da ListBox (não só os view models isolados).
public class ViewerIntegrationTests
{
    [Fact]
    public void Viewer_RendersFirstPageAndLastPageAfterScroll()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunScenario()
    {
        DocumentViewModel? doc = null;
        PdfViewerControl? control = null;
        Window? window = null;
        try
        {
            doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
            control = new PdfViewerControl { DataContext = doc };
            window = new Window { Width = 1000, Height = 800, Content = control };
            window.Show();

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.True(doc.Pages[0].ImageSource is not null,
                "primeira página não renderizou a tempo (scheduler->PDFium->bitmap->dispatcher)");

            // FindPageListScrollViewer (não um FindVisualChild genérico varrendo a raiz do controle):
            // desde a Task 5 a raiz também contém a SearchBar, cujo TextBox tem seu PRÓPRIO
            // ScrollViewer interno (padrão de qualquer TextBox do WPF) — uma varredura ingênua a
            // partir da raiz acharia esse primeiro (SearchBar é o filho ANTERIOR ao PageList no
            // DockPanel) e rolaria o alvo errado, nunca realizando a última página.
            var scrollViewer = control.FindPageListScrollViewer();
            Assert.NotNull(scrollViewer);
            scrollViewer!.ScrollToEnd();

            Pump(() => doc.Pages[29].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.True(doc.Pages[29].ImageSource is not null,
                "última página não realizou/renderizou após rolar até o fim (virtualização)");

            scrollViewer.ScrollToHome();

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.True(doc.Pages[0].ImageSource is not null,
                "primeira página não rerenderizou após rolar de volta ao topo (reciclagem de container)");
        }
        finally
        {
            window?.Close();
            doc?.Dispose();
            // Drena o descarte da sessão (descarregado p/ thread-pool em DocumentViewModel.Dispose)
            // antes de encerrar — evita que o teardown nativo do PDFium ainda esteja em curso
            // quando o processo de teste morre (0xC0000005).
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Task 8 (Plano 2b): PageDown com foco no PageList precisa rolar de verdade — antes desta task a
    /// ListBox engolia a tecla sem repassar pro ScrollViewer (containers não-focáveis, template de
    /// seleção zerado). Simula a tecla via RaiseEvent (PreviewKeyDown tuneliza da raiz até o PageList,
    /// passando pelo PdfViewerControl no meio do caminho — mesma árvore visual do smoke acima) e
    /// verifica que o offset REALMENTE aumentou, não só que nenhuma exceção foi lançada.
    [Fact]
    public void Viewer_PageDownKey_ScrollsScrollViewer()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunPageDownKeyScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunPageDownKeyScenario()
    {
        DocumentViewModel? doc = null;
        PdfViewerControl? control = null;
        Window? window = null;
        try
        {
            doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
            control = new PdfViewerControl { DataContext = doc };
            window = new Window { Width = 1000, Height = 800, Content = control };
            window.Show();

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.True(doc.Pages[0].ImageSource is not null,
                "primeira página não renderizou a tempo (scheduler->PDFium->bitmap->dispatcher)");

            var scrollViewer = control.FindPageListScrollViewer();
            Assert.NotNull(scrollViewer);

            // Loaded já chama PageList.Focus() (ver construtor de PdfViewerControl), mas garantimos
            // aqui pra não depender de timing do próprio Loaded já ter corrido.
            control.PageList.Focus();
            Pump(() => control.PageList.IsKeyboardFocusWithin, TimeSpan.FromSeconds(5));
            Assert.True(control.PageList.IsKeyboardFocusWithin, "PageList não obteve foco de teclado");

            double before = scrollViewer!.VerticalOffset;

            var args = new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(control.PageList), 0, Key.PageDown)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            control.PageList.RaiseEvent(args);

            Pump(() => scrollViewer.VerticalOffset > before, TimeSpan.FromSeconds(5));

            Assert.True(scrollViewer.VerticalOffset > before,
                $"PageDown não rolou o ScrollViewer (antes={before}, depois={scrollViewer.VerticalOffset}) " +
                "— regressão da Task 8 (ListBox engolindo a tecla sem delegar pro ScrollViewer)");
        }
        finally
        {
            window?.Close();
            doc?.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Task 1 (Plano 8): a caixa ajustável do carimbo em Adjusting mostra o adorner (retângulo + alças +
    /// botões flutuantes) na árvore visual DE VERDADE, e o estado (HasStampBox/StampBoxScreenRect, no
    /// PageViewModel — não no container reciclável) sobrevive a rolar a página dona pra fora do
    /// viewport (desrealização, CacheLength="1,1" Page) e voltar (reciclagem) — mesma prova ponta a
    /// ponta que `Viewer_RendersFirstPageAndLastPageAfterScroll` já faz pro bitmap, aplicada ao overlay
    /// desta task. Também prova que Cancelar (botão) limpa o adorner na hora. NADA disto é alcançável
    /// hoje pelo clique único de produção (Task 2 troca o gatilho) — os métodos da máquina são chamados
    /// DIRETO, exatamente como a View fará depois de Task 2.
    [Fact]
    public void Viewer_StampBoxAdorner_AppearsWhileAdjusting_SurvivesScrollAndVirtualization_ClearsOnCancel()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunStampBoxAdornerScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunStampBoxAdornerScenario()
    {
        DocumentViewModel? doc = null;
        PdfViewerControl? control = null;
        Window? window = null;
        try
        {
            doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
            control = new PdfViewerControl { DataContext = doc };
            window = new Window { Width = 1000, Height = 800, Content = control };
            window.Show();

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.True(doc.Pages[0].ImageSource is not null, "primeira página não renderizou a tempo");

            // Aciona a máquina DIRETO (mesma nota de doc XML acima) — chega a Adjusting numa caixa
            // válida (200x50pt, acima do mínimo 60x20pt).
            doc.ActiveTool = AnnotationTool.SignatureStamp;
            doc.BeginStampBoxPlacement(0, new PdfPoint(100, 700), "CN=Assinante STA");
            doc.UpdateDrawTo(new PdfPoint(300, 750));
            doc.EndStampDraw();
            Assert.Equal(StampPlacementPhase.Adjusting, doc.StampPlacementPhase);

            Pump(() => FindVisualChildren<Button>(window).Any(IsConfirmButtonVisible), TimeSpan.FromSeconds(5));
            var buttons = FindVisualChildren<Button>(window).ToList();
            var confirmButton = buttons.FirstOrDefault(b => Equals(b.Content, "✔ Assinar aqui"));
            var cancelButton = buttons.FirstOrDefault(b => Equals(b.Content, "✖ Cancelar"));
            Assert.True(confirmButton is { IsVisible: true }, "botão Confirmar não apareceu (Adjusting)");
            Assert.True(cancelButton is { IsVisible: true }, "botão Cancelar não apareceu (Adjusting)");

            Assert.True(doc.Pages[0].HasStampBox);
            var rectBefore = doc.Pages[0].StampBoxScreenRect;

            // Rola até o fim (desrealiza a página 0, fora do CacheLength="1,1") e volta (recicla o
            // container) — mesma mecânica do smoke de virtualização acima.
            var scrollViewer = control.FindPageListScrollViewer();
            Assert.NotNull(scrollViewer);
            scrollViewer!.ScrollToEnd();
            Pump(() => doc.Pages[29].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.True(doc.Pages[29].ImageSource is not null, "última página não realizou após rolar");

            scrollViewer.ScrollToHome();
            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.True(doc.Pages[0].ImageSource is not null, "primeira página não re-realizou ao voltar ao topo");

            // O estado sobreviveu à reciclagem (vive no PageViewModel, não no container visual descartado)
            Assert.True(doc.Pages[0].HasStampBox, "HasStampBox não sobreviveu à desrealização+reciclagem");
            Assert.Equal(rectBefore, doc.Pages[0].StampBoxScreenRect);

            Pump(() => FindVisualChildren<Button>(window).Any(IsConfirmButtonVisible), TimeSpan.FromSeconds(5));
            Assert.Contains(FindVisualChildren<Button>(window), IsConfirmButtonVisible);

            // Cancelar limpa o adorner na hora (binding reage à mudança de HasStampBox/IsStampBoxAdjusting)
            doc.CancelStampBox();
            Pump(() => !doc.Pages[0].HasStampBox, TimeSpan.FromSeconds(5));
            Assert.False(doc.Pages[0].HasStampBox);
            Assert.DoesNotContain(FindVisualChildren<Button>(window), IsConfirmButtonVisible);
        }
        finally
        {
            window?.Close();
            doc?.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    // IsVisible (efetiva, considerando ancestrais — inclui o StackPanel do adorner que carrega o
    // binding de verdade), NÃO Visibility (propriedade LOCAL do próprio Button, nunca setada aqui —
    // fica sempre no default Visible independente do ancestral estar Collapsed; achado ao vivo: a 1ª
    // versão deste teste usava Visibility e falhava com falso-positivo em containers reciclados fora
    // de tela, cujo StackPanel pai já estava Collapsed corretamente).
    private static bool IsConfirmButtonVisible(Button b) => Equals(b.Content, "✔ Assinar aqui") && b.IsVisible;

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    /// Task 1 (Plano 8): Esc cancela a caixa em curso pelo pipeline de teclado REAL (PreviewKeyDown do
    /// PdfViewerControl, mesmo caminho já provado pra PageDown acima) — não só a chamada direta de
    /// CancelStampBox (já coberta headless em StampBoxPlacementTests).
    [Fact]
    public void Viewer_EscapeKey_CancelsStampBoxPlacement()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunEscapeCancelsStampBoxScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunEscapeCancelsStampBoxScenario()
    {
        DocumentViewModel? doc = null;
        PdfViewerControl? control = null;
        Window? window = null;
        try
        {
            doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
            control = new PdfViewerControl { DataContext = doc };
            window = new Window { Width = 1000, Height = 800, Content = control };
            window.Show();

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));

            control.PageList.Focus();
            Pump(() => control.PageList.IsKeyboardFocusWithin, TimeSpan.FromSeconds(5));
            Assert.True(control.PageList.IsKeyboardFocusWithin, "PageList não obteve foco de teclado");

            doc.ActiveTool = AnnotationTool.SignatureStamp;
            doc.BeginStampBoxPlacement(0, new PdfPoint(100, 700), "CN=Assinante STA");
            doc.UpdateDrawTo(new PdfPoint(300, 750));
            doc.EndStampDraw();
            Assert.Equal(StampPlacementPhase.Adjusting, doc.StampPlacementPhase);

            var args = new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(control.PageList), 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            control.PageList.RaiseEvent(args);

            Pump(() => doc.StampPlacementPhase == StampPlacementPhase.None, TimeSpan.FromSeconds(5));

            Assert.Equal(StampPlacementPhase.None, doc.StampPlacementPhase);
            Assert.Equal(AnnotationTool.None, doc.ActiveTool);
            Assert.True(args.Handled, "Esc não marcou e.Handled=true");
        }
        finally
        {
            window?.Close();
            doc?.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Task 2 (Plano 8): fiação REAL de mouse da caixa ajustável, numa `MainWindow` REAL (não um
    /// `Window`/`PdfViewerControl` avulso como os testes acima) — prova que: (a) mouse-down REAL na
    /// página, com `ActiveTool == SignatureStamp`, entra em Drawing e CAPTURA o mouse no Border da
    /// página (mesmo exemplar de mouse-capture da ferramenta Retângulo, Task 8/Plano 3a); (b) mouse-down
    /// REAL numa ALÇA (Adjusting) e no CORPO da caixa capturam o mouse no Border ANCESTRAL — não no
    /// elemento fisicamente clicado (Rectangle da alça / Grid do corpo) — e marcam `e.Handled`
    /// (`StampBoxHandle_MouseLeftButtonDown`/`StampBox_MouseLeftButtonDown`, ver doc XML de
    /// `PdfViewerControl`); (c) o botão "✔ Assinar aqui" REAL, clicado via `RaiseEvent` no
    /// `ButtonBase.ClickEvent` (não um clique de mouse posicionado), dispara `ConfirmSignatureStampAsync`
    /// ponta a ponta até o motor (fake), recebendo o rect da caixa corrente.
    ///
    /// POSIÇÃO exata do mouse NÃO é controlável em teste sem mover o cursor real do SO — medido ao vivo
    /// escrevendo este teste: `MouseButtonEventArgs.GetPosition` lê o `MouseDevice` de verdade (a
    /// posição REAL do cursor no momento do teste), nunca um valor injetável via `RaiseEvent`; por isso
    /// o rect em si (pra provar o motor recebe o valor CORRETO) é estabelecido por chamada DIRETA à
    /// máquina (mesmo exemplar/justificativa de `Viewer_StampBoxAdorner_...`, Task 1) — a precisão em
    /// PIXELS do rect final é responsabilidade da aceitação por pixel
    /// (`SignCommandTests.Sign_Integration_StampBoxDrawAdjustConfirm_RendersExactlyInsideFinalRect`);
    /// este teste foca no que É observável deterministicamente com mouse REAL: `Mouse.Captured` (QUEM
    /// capturou) e `e.Handled`.
    [Fact]
    public void Viewer_StampBoxRealMouseWiring_CapturesOnPageBorder_ConfirmButtonSignsWithCurrentRect()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunStampBoxRealMouseWiringScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunStampBoxRealMouseWiringScenario()
    {
        // Exemplar: RunCamposTabScenario (doc XML lá) — ConfirmSignatureStampAsync é um AsyncRelayCommand
        // acionado por um Button REAL; sem este SynchronizationContext explícito o `await` retomaria numa
        // thread do POOL (não a STA desta thread), derrubando o processo ao tentar notificar
        // CanExecuteChanged/mexer num DependencyObject fora da thread dona.
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        mPdf.App.MainWindow? window = null;
        DocumentViewModel? doc = null;
        string? tmp = null;
        try
        {
            // CRÍTICO (exemplar: SignCommandTests.CopyFixtureToTemp): este teste alcança um Confirmar
            // BEM-SUCEDIDO -> Session.CommitSigned grava em disco de verdade. NUNCA abrir a fixture
            // COMPARTILHADA direto aqui — copiar pra um arquivo descartável primeiro.
            tmp = Path.Combine(Path.GetTempPath(), $"mpdf-stampbox-mouse-{Guid.NewGuid():N}.pdf");
            File.Copy(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), tmp);

            var engine = new FakeSigningEngine();
            using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
            var dialog = new FakeSignDialogService(new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true));
            doc = new DocumentViewModel(
                DocumentSession.Open(tmp),
                notifyError: _ => { }, notifyInfo: _ => { },
                signDialog: dialog, signingEngine: engine,
                confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
                listSigningCertificates: () => Array.Empty<SigningCertificateInfo>());

            window = new mPdf.App.MainWindow();
            window.Show();
            window.ViewModel.Documents.Add(doc);
            window.ViewModel.SelectedDocument = doc;

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));

            // Re-achado a cada uso (não cacheado uma única vez) -- o container do ListBox por trás da
            // página PODE ser reciclado/regenerado por baixo (virtualização) entre passos; comparar por
            // IDENTIDADE de referência a uma captura ANTIGA seria frágil. A prova que IMPORTA aqui (quem
            // capturou o mouse é o Border ANCESTRAL da página, não o sub-elemento clicado) é feita
            // comparando o DataContext do capturado contra `doc.Pages[0]` — estável mesmo que a
            // INSTÂNCIA do Border mude por baixo.
            Border PageBorder() => FindDescendantByDataContext<Border>(window!, doc!.Pages[0])
                ?? throw new InvalidOperationException("Border da página 0 não encontrado na árvore visual.");
            Assert.NotNull(PageBorder()); // exemplar: RunAssinaturasTabScenario -- Border é a raiz do DataTemplate por página

            // ---- (a) Sign() com PlaceStamp -> ativa o modo de colocação ------------------------------
            _ = doc.SignCommand.ExecuteAsync(null);
            Pump(() => doc.ActiveTool == AnnotationTool.SignatureStamp, TimeSpan.FromSeconds(5));
            Assert.Equal(AnnotationTool.SignatureStamp, doc.ActiveTool);

            // ---- (b) Mouse-down REAL na página -- entra em Drawing e CAPTURA o mouse no Border -------
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            PageBorder().RaiseEvent(down);

            Assert.True(down.Handled, "mouse-down na página não foi marcado Handled");
            Assert.Same(doc.Pages[0], (Mouse.Captured as FrameworkElement)?.DataContext);
            Assert.Equal(StampPlacementPhase.Drawing, doc.StampPlacementPhase);

            // Mouse-up REAL -- solta a captura (EndStampDraw decide sozinho Drawing-permanece vs Adjusting).
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            PageBorder().RaiseEvent(up);
            Assert.Null(Mouse.Captured);

            // ---- (c) Estabelece um rect CONHECIDO por chamada direta (posição de mouse real não é
            // controlável, ver doc XML acima) -- reseta o que quer que (b) tenha desenhado e recomeça. --
            doc.CancelStampBox();
            _ = doc.SignCommand.ExecuteAsync(null); // CancelStampBox desligou ActiveTool -- reativa o modo
            Pump(() => doc.ActiveTool == AnnotationTool.SignatureStamp, TimeSpan.FromSeconds(5));
            doc.BeginStampBoxPlacementAsync(0, new PdfPoint(100, 100)).GetAwaiter().GetResult();
            doc.UpdateDrawTo(new PdfPoint(300, 200));
            doc.EndStampDraw();
            Assert.Equal(StampPlacementPhase.Adjusting, doc.StampPlacementPhase); // sanity

            // ---- (d) Mouse-down REAL numa ALÇA -- captura no Border ANCESTRAL (não no Rectangle da
            // alça), marca Handled (StampBoxHandle_MouseLeftButtonDown) ------------------------------
            var handlePoint = doc.Pages[0].StampBoxHandlePoints[3]; // índice 3 = Right (FillStampBoxHandlePoints)
            Assert.Equal(StampBoxHandle.Right, handlePoint.Handle);
            // O ItemsControl precisa de um passe de LAYOUT (Dispatcher) pra gerar o container do item
            // recém-adicionado à ObservableCollection -- mesmo cuidado de RunStampBoxAdornerScenario
            // (Pump antes de achar os botões flutuantes pela 1ª vez).
            Pump(() => FindDescendantByDataContext<Rectangle>(window, handlePoint) is not null, TimeSpan.FromSeconds(5));
            var handleRect = FindDescendantByDataContext<Rectangle>(window, handlePoint);
            Assert.NotNull(handleRect); // ItemsControl/DataTemplate da alça resolveram de verdade

            var handleDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            handleRect!.RaiseEvent(handleDown);

            Assert.True(handleDown.Handled, "mouse-down na alça não foi marcado Handled");
            // capturado no BORDER ancestral (a PÁGINA), não no Rectangle da alça fisicamente clicado.
            Assert.IsType<Border>(Mouse.Captured);
            Assert.Same(doc.Pages[0], (Mouse.Captured as FrameworkElement)?.DataContext);

            var handleUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            ((Border)Mouse.Captured!).RaiseEvent(handleUp);
            Assert.Null(Mouse.Captured);
            Assert.Equal(StampPlacementPhase.Adjusting, doc.StampPlacementPhase); // redimensionar não muda a fase

            // ---- (e) Mouse-down REAL no CORPO da caixa -- mesma disciplina (StampBox_MouseLeftButtonDown) --
            var bodyGrid = FindDescendants<Grid>(window).FirstOrDefault(g => g.Name == "StampBoxAdornerGrid");
            Assert.NotNull(bodyGrid); // Grid do adorner (x:Name pra teste) resolveu de verdade

            var bodyDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            bodyGrid!.RaiseEvent(bodyDown);

            Assert.True(bodyDown.Handled, "mouse-down no corpo da caixa não foi marcado Handled");
            // capturado no BORDER ancestral (a PÁGINA), não no Grid do corpo fisicamente clicado.
            Assert.IsType<Border>(Mouse.Captured);
            Assert.Same(doc.Pages[0], (Mouse.Captured as FrameworkElement)?.DataContext);

            var bodyUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            ((Border)Mouse.Captured!).RaiseEvent(bodyUp);
            Assert.Null(Mouse.Captured);

            var rectBeforeConfirm = doc.StampBoxRect;

            // ---- (f) Botão "✔ Assinar aqui" REAL -- clique via RaiseEvent no ButtonBase.ClickEvent
            // (posição-independente, mesmo padrão de clique já usado por outros testes desta classe) --
            // dispara ConfirmSignatureStampAsync ponta a ponta até o motor (fake). ---------------------
            var confirmButton = FindVisualChildren<Button>(window).FirstOrDefault(b => Equals(b.Content, "✔ Assinar aqui"));
            Assert.NotNull(confirmButton); // botão REAL do adorner, dentro da MainWindow REAL

            confirmButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, confirmButton));

            Pump(() => engine.SignCallCount > 0, TimeSpan.FromSeconds(10));
            Assert.Equal(1, engine.SignCallCount);
            var stamp = engine.LastRequest!.Stamp;
            Assert.NotNull(stamp);
            Assert.Equal(0, stamp!.PageIndex);
            Assert.Equal(rectBeforeConfirm.LeftPt, stamp.Rect.LeftPt, 0.01);
            Assert.Equal(rectBeforeConfirm.BottomPt, stamp.Rect.BottomPt, 0.01);
            Assert.Equal(rectBeforeConfirm.RightPt, stamp.Rect.RightPt, 0.01);
            Assert.Equal(rectBeforeConfirm.TopPt, stamp.Rect.TopPt, 0.01);
            Pump(() => doc.IsSignedDocument, TimeSpan.FromSeconds(10));
            Assert.True(doc.IsSignedDocument);
            Assert.Equal(StampPlacementPhase.None, doc.StampPlacementPhase);
            Assert.Equal(AnnotationTool.None, doc.ActiveTool);
        }
        finally
        {
            if (window is not null) window.ViewModel.Documents.Clear(); // doc fica sujo/assinado -- mesmo cinto de RunAssinaturasTabScenario
            window?.Close();
            doc?.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            if (tmp is not null)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                try { if (File.Exists(tmp + ".bak")) File.Delete(tmp + ".bak"); } catch { }
            }
        }
    }

    /// I3 (revisão final da branch, achado real do revisor): as 8 alças só podem existir na árvore
    /// visual em Adjusting — em Drawing (incl. o estado "pequeno demais, permanece Drawing com aviso
    /// sutil") elas precisam ficar em ZERO, senão um clique que caia sobre a posição de uma alça
    /// "fantasma" intercepta o mouse-down (StampBoxHandle_MouseLeftButtonDown marca `e.Handled`) que
    /// deveria CONTINUAR o gesto de desenho em Page_MouseLeftButtonDown. Conta Rectangles pelo
    /// DataContext (`StampBoxHandlePoint`, mesma técnica de RunStampBoxRealMouseWiringScenario). Prova o
    /// RESULTADO OBSERVÁVEL final (0 containers renderizados) — os 2 fixes (VM: RefreshStampBoxOverlay/
    /// ApplyZoom só preenchem em Adjusting; XAML: Visibility no ItemsControl) atuam JUNTOS aqui: achado
    /// ao vivo escrevendo este teste — com o `Visibility` do ItemsControl Collapsed (o cinto XAML),
    /// o WPF nem chega a gerar os containers do item mesmo que a COLEÇÃO tenha 8 itens (verificado
    /// injetando uma mutação só no VM: a coleção tinha 8, a árvore visual continuava com 0) — então este
    /// teste sozinho NÃO isola qual dos 2 fixes está segurando a barra. `StampBoxPlacementTests.
    /// HandlePoints_PopulatedOnlyInAdjusting_EmptyDuringDrawing` (headless, sem XAML) prova o fix do VM
    /// isoladamente, contando a COLEÇÃO direto.
    [Fact]
    public void Viewer_StampBoxHandles_HiddenDuringDrawing_ExactlyEightDuringAdjusting()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunStampBoxHandlesVisibilityScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunStampBoxHandlesVisibilityScenario()
    {
        DocumentViewModel? doc = null;
        PdfViewerControl? control = null;
        Window? window = null;
        try
        {
            doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
            control = new PdfViewerControl { DataContext = doc };
            window = new Window { Width = 1000, Height = 800, Content = control };
            window.Show();

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));

            int HandleCount() => FindDescendants<Rectangle>(window!).Count(r => r.DataContext is StampBoxHandlePoint);
            // ACHADO ao vivo (escrevendo este teste): Pump(condition, timeout) só bombeia o dispatcher
            // ENQUANTO a condição for falsa -- pedir uma condição já VERDADEIRA (ex.: HasStampBox, que
            // já é true de imediato) devolve na hora, SEM processar a fila de layout nenhuma vez. Sem
            // isto, uma alça "fantasma" adicionada à ObservableCollection (o bug) NUNCA teria a chance
            // de virar um Rectangle de verdade na árvore visual antes da asserção rodar -- o teste
            // passaria por engano tanto no código certo quanto no código com bug (verificado: o mutante
            // que preenche as alças incondicionalmente ainda passava neste teste até este fix). Força
            // um número FIXO de ciclos do dispatcher, sempre, antes de cada checagem de HandleCount.
            void FlushDispatcher()
            {
                for (int i = 0; i < 10; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }

            doc.ActiveTool = AnnotationTool.SignatureStamp;
            doc.BeginStampBoxPlacement(0, new PdfPoint(100, 700), "CN=Teste I3");
            doc.UpdateDrawTo(new PdfPoint(130, 710)); // 30x10pt -- abaixo do mínimo 60x20pt
            doc.EndStampDraw();
            Assert.Equal(StampPlacementPhase.Drawing, doc.StampPlacementPhase); // ficou em Drawing (aviso sutil)
            FlushDispatcher();
            Assert.True(doc.Pages[0].HasStampBox);
            Assert.Equal(0, HandleCount()); // achado EXATO do revisor: nem no estado "pequeno demais"

            doc.UpdateDrawTo(new PdfPoint(300, 750)); // continua o MESMO gesto até um tamanho válido
            Assert.Equal(StampPlacementPhase.Drawing, doc.StampPlacementPhase); // ainda Drawing (não soltou)
            FlushDispatcher();
            Assert.Equal(0, HandleCount());

            doc.EndStampDraw();
            Assert.Equal(StampPlacementPhase.Adjusting, doc.StampPlacementPhase);
            Pump(() => HandleCount() == 8, TimeSpan.FromSeconds(5));
            Assert.Equal(8, HandleCount());

            doc.CancelStampBox();
            Pump(() => HandleCount() == 0, TimeSpan.FromSeconds(5));
            Assert.Equal(0, HandleCount());
        }
        finally
        {
            window?.Close();
            doc?.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    // Bombeia a fila do dispatcher da thread ATUAL até a condição ficar verdadeira ou o timeout vencer.
    // Invoke(_, Background) força o processamento de tudo com prioridade maior (Loaded/Render/Normal/
    // Send) antes de devolver o controle — equivalente ao "DoEvents" do WPF.
    private static void Pump(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(50);
        }
    }

    /// Task 3 (Plano 3a) — SEAM crítica citada no brief: `DocumentViewModel` construía o
    /// RenderScheduler principal como `new RenderScheduler(session.Renderer.RenderPage)`, uma
    /// conversão de GRUPO DE MÉTODO que fica ligada PARA SEMPRE à instância de `PdfDocumentRenderer`
    /// que `session.Renderer` apontava NAQUELE INSTANTE. `DocumentSession.Apply` troca `Renderer` por
    /// uma instância NOVA (manda a antiga pro `PendingDisposals`) toda vez que uma edição é aplicada —
    /// sem o fix de late-binding, todo render pedido DEPOIS de um Apply continuaria silenciosamente
    /// preso ao documento ANTIGO (ou lançaria `ObjectDisposedException`, engolida pelo `catch{}` do
    /// RenderScheduler — a página ficaria em branco pra sempre, SEM NENHUM ERRO VISÍVEL). Ponta a
    /// ponta com o WORKER de verdade (scheduler -> PDFium -> dispatcher), sem PdfViewerControl/Window
    /// (não precisa da virtualização da ListBox aqui, só do pipeline de render em si).
    [Fact]
    public void Viewer_Apply_SwapsRenderer_SubsequentRendersUseNewDocument()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunApplySeamScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunApplySeamScenario()
    {
        DocumentViewModel? doc = null;
        try
        {
            // fixture-a4: 1 página só — abre o documento ORIGINAL e prova que ele renderiza normalmente
            // ANTES de qualquer Apply (baseline: o pipeline funciona sem a seam entrar em jogo ainda).
            doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
            Assert.Single(doc.Pages);

            doc.Pages[0].OnRealized();
            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.True(doc.Pages[0].ImageSource is not null,
                "página 0 do documento ORIGINAL não renderizou a tempo (baseline antes do Apply)");

            // Aplica um documento NOVO (30 páginas) por cima da sessão já aberta — simula uma edição
            // real chegando via mPdf.Editing. Session.Renderer agora é uma instância DIFERENTE; a
            // antiga foi enfileirada pro PendingDisposals.
            doc.Session.Apply(Fixtures.ThirtyPages());
            Assert.Equal(30, doc.Pages.Count); // Pages reconstruído SINCRONAMENTE pelo handler de Applied

            // Página 29 SÓ EXISTE no documento NOVO — o teste mais forte possível: se o scheduler
            // ainda estivesse preso ao renderer ANTIGO (1 página, e já descartado), pedir a página 29
            // faria RenderPage(29, ...) falhar (índice fora do range OU ObjectDisposedException); o
            // RenderScheduler ENGOLE essa exceção por design (página fica em placeholder) — ou seja,
            // sem o fix, ImageSource NUNCA seria populado aqui, e o Pump abaixo estouraria o timeout.
            doc.Pages[29].OnRealized();
            Pump(() => doc.Pages[29].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.True(doc.Pages[29].ImageSource is not null,
                "SEAM do late-binding NÃO corrigida: página 29 (só existe no documento NOVO pós-Apply) " +
                "nunca renderizou — o scheduler continuou preso ao renderer ANTIGO");
        }
        finally
        {
            doc?.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Fix pós-revisão (Task 5, Plano 3a — C1): reproduz empiricamente a classe de bug que uma suíte
    /// de VM headless não enxerga — o MOTOR de binding do WPF em si. Sem `FallbackValue=Collapsed` no
    /// Binding do banner (`SelectedDocument.IsSignedDocument`), com `SelectedDocument == null` (app
    /// recém-aberto/última aba fechada) o PropertyPath não resolve (referência nula no meio do
    /// caminho) — o Binding NUNCA chega a invocar o Converter, e cai no valor PADRÃO do DP alvo
    /// (`UIElement.Visibility` = `Visible`), não em `Collapsed`. Um teste que só olha o
    /// `DocumentViewModel`/`MainViewModel` (VM puro, sem `Window`/`Binding` de verdade) não teria como
    /// pegar isso — só existe no comportamento real do WPF binding engine sobre o XAML compilado, daí
    /// precisar de uma `MainWindow` de VERDADE (não um fake). Constrói a janela de produção tal como o
    /// app faz (`new MainWindow()`), SEM `Show()` (a inspeção de `Visibility` já reflete a avaliação do
    /// Binding assim que `DataContext` propaga pela árvore lógica/visual — não depende de layout/
    /// renderização de tela) e SEM abrir nenhum documento — exatamente o estado de app "recém-aberto"
    /// que expôs o bug.
    [Fact]
    public void MainWindow_NoDocumentOpen_SignedDocumentBannerIsCollapsed()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunMainWindowNoDocumentScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunMainWindowNoDocumentScenario()
    {
        mPdf.App.MainWindow? window = null;
        try
        {
            // Ctor de produção de verdade (mesmo `new MainViewModel(new FileDialogService())` que o
            // app usa) — sem isso o teste provaria só um fake, não o bug real. Não chama nenhum método
            // de diálogo (PickPdfToOpen/PickPdfToSaveAs), então FileDialogService nunca toca a UI
            // nativa do SO aqui; RecentFilesStore/AppConfig criam `%AppData%\mPDF` se ainda não
            // existir (mesmo efeito colateral idempotente que o app real já produz ao abrir).
            window = new mPdf.App.MainWindow();

            Assert.Null(window.ViewModel.SelectedDocument); // estado "recém-aberto" que expôs o bug
            Assert.Equal(Visibility.Collapsed, window.SignedDocumentBanner.Visibility);
        }
        finally
        {
            window?.Close();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Task 3 (Plano 3b): prova que o XAML REAL de `MainWindow` (Grid com `PdfViewerControl`/
    /// `PageOrganizerView` sobrepostos, `Style`/`DataTrigger` de Visibility, `{StaticResource BoolToVis}`
    /// referenciado de dentro do `DataTemplate`) resolve e renderiza sem lançar — o mesmo tipo de bug
    /// que `MainWindow_NoDocumentOpen_SignedDocumentBannerIsCollapsed` já provou não dar pra pegar só
    /// com testes de VM (o MOTOR de binding/template do WPF em si). Precisa de `Show()` (ao contrário
    /// daquele teste): o `ContentTemplate` do `TabControl` só materializa o `Grid`/`PageOrganizerView`
    /// depois de um passe de layout — a inspeção de `Visibility` sozinha não bastaria aqui.
    [Fact]
    public void MainWindow_ToggleOrganizer_RendersPageOrganizerViewAndReturnsToReaderPage()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunToggleOrganizerScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunToggleOrganizerScenario()
    {
        mPdf.App.MainWindow? window = null;
        try
        {
            window = new mPdf.App.MainWindow();
            window.Show();

            // DocumentSession.Open SÍNCRONO (não MainViewModel.OpenPath/OpenAsync) — mesmo padrão de
            // RunScenario acima: este arquivo testa o MOTOR de binding/render de verdade, não o fluxo
            // assíncrono de abertura de arquivo (já coberto em MainViewModelTests, sem thread STA/
            // Dispatcher manual no meio do caminho). Adicionado DIRETO à coleção real de MainViewModel
            // pra exercitar o MESMO caminho que o TabControl/DataTemplate de produção usa.
            var doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
            window.ViewModel.Documents.Add(doc);
            window.ViewModel.SelectedDocument = doc;

            // Deixa a 1ª renderização/ScrollChanged inicial do leitor assentar (mesmo "settle" que
            // RunScenario acima espera) ANTES de forçar CurrentPage — sem isso, um ScrollChanged
            // pendente da abertura (offset 0, ainda na fila do Dispatcher) dispara DEPOIS (durante um
            // Pump mais adiante) e sobrescreve o valor que este teste está tentando fixar.
            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));

            doc.CurrentPage = 12; // "página equivalente" ao sair precisa bater com o que já estava aberto

            doc.IsOrganizerOpen = true;
            Pump(() => FindDescendant<PageOrganizerView>(window) is not null, TimeSpan.FromSeconds(10));
            var organizerView = FindDescendant<PageOrganizerView>(window);
            Assert.NotNull(organizerView); // Grid/Style/DataTrigger/{StaticResource BoolToVis} resolveram sem exceção
            Assert.Equal(30, doc.Organizer!.Pages.Count);

            // Pipeline de render PRÓPRIO do organizador (renderer/scheduler dedicados a 0.35) funciona
            // ponta a ponta — mesma prova que Viewer_RendersFirstPageAndLastPageAfterScroll já faz pro
            // pipeline principal, aplicada aqui ao renderer NOVO desta task.
            Pump(() => doc.Organizer.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            Assert.NotNull(doc.Organizer.Pages[0].ImageSource);

            // Sair do organizador SEM seleção -> volta pra página que já estava CORRENTE (brief:
            // "página equivalente").
            doc.IsOrganizerOpen = false;
            Assert.Null(doc.Organizer);
            Assert.Equal(12, doc.CurrentPage);
        }
        finally
        {
            window?.Close();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Task 5 (Plano 3b): prova que o XAML REAL da aba "Sumário" (TabControl novo dentro do painel
    /// esquerdo, `HierarchicalDataTemplate DataType="{x:Type editing:OutlineNode}"`, `xmlns:editing`,
    /// `{StaticResource BoolToVis}`-equivalente via Style/DataTrigger em `OutlineView.xaml`) resolve,
    /// popula via o refresh assíncrono de verdade (`_dispatcher.BeginInvoke` bombeado pelo `Pump`, ao
    /// contrário dos testes de VM puro que chamam `RefreshOutlineAsync` direto) e that clicar um nó
    /// (via `TreeViewItem.IsSelected`, já que `TreeView.SelectedItem` não é setável direto) dispara
    /// `ScrollToPageRequested` com o índice certo — mesmo espírito de
    /// `MainWindow_ToggleOrganizer_RendersPageOrganizerViewAndReturnsToReaderPage` (o MOTOR de binding/
    /// template do WPF em si, que um teste de VM isolado não alcança).
    [Fact]
    public void MainWindow_SumarioTab_PopulatesTreeAndNavigatesOnClick()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunSumarioTabScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunSumarioTabScenario()
    {
        mPdf.App.MainWindow? window = null;
        try
        {
            window = new mPdf.App.MainWindow();
            window.Show();

            var doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-sumario.pdf")));
            window.ViewModel.Documents.Add(doc);
            window.ViewModel.SelectedDocument = doc;
            window.ViewModel.ThumbnailsVisible = true; // mostra o painel esquerdo (TabControl novo dentro)

            Pump(() => doc.HasOutline, TimeSpan.FromSeconds(10));
            Assert.True(doc.HasOutline, "Outline não populou via refresh assíncrono real (Task.Run + _dispatcher)");
            Assert.Equal(4, doc.Outline.Count); // Capítulo 1/2/3 + Anexos, ver fixture-sumario.pdf

            var tabControl = FindDescendant<TabControl>(window);
            Assert.NotNull(tabControl);
            tabControl!.SelectedIndex = 1; // aba "Sumário" (0 = Miniaturas)
            Pump(() => FindDescendant<TreeView>(window) is not null, TimeSpan.FromSeconds(10));

            var tree = FindDescendant<TreeView>(window);
            Assert.NotNull(tree);
            Pump(() => tree!.Items.Count == 4, TimeSpan.FromSeconds(10));
            Assert.Equal(4, tree!.Items.Count); // ItemsSource="{Binding Outline}" resolveu de verdade

            // "Capítulo 2" (PageIndex=10 em fixture-sumario.pdf) — realiza o container e seleciona,
            // mesmo caminho que um clique real do usuário produz (TreeViewItem.IsSelected = true).
            Pump(() => tree.ItemContainerGenerator.ContainerFromIndex(1) is TreeViewItem, TimeSpan.FromSeconds(10));
            var cap2Item = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(1)!;

            int? scrolledTo = null;
            doc.ScrollToPageRequested += idx => scrolledTo = idx;
            cap2Item.IsSelected = true;
            Pump(() => scrolledTo is not null, TimeSpan.FromSeconds(5));

            Assert.Equal(10, scrolledTo); // 0-based — mesma convenção do resto do App

            // I2 (revisão final pré-merge): 2º "clique" no MESMO nó JÁ selecionado — `IsSelected = true`
            // de novo seria um NO-OP pro WPF (já está true, `SelectedItemChanged` NUNCA dispara de novo,
            // exatamente o bug real: usuário reclica "Capítulo 2" depois de rolar pra outro lugar e nada
            // acontece). Prova o FIX de verdade (não só o contrato do VM, já coberto por
            // `NavigateToOutlineNodeCommand_SameNodeActivatedTwice_...` em DocumentViewModelTests):
            // dispara `MouseLeftButtonUp` DIRETO na TextBlock do item template (mesmo mecanismo de
            // `Viewer_PageDownKey_ScrollsScrollViewer` acima pra teclado) e confere que `ScrollToPageRequested`
            // dispara de NOVO — só pode ter vindo do handler NOVO (`Node_MouseLeftButtonUp`), já que a
            // seleção não mudou.
            var node = doc.Outline[1]; // "Capítulo 2" — MESMA instância que o item template bindou
            var textBlock = FindDescendantByDataContext<TextBlock>(window, node);
            Assert.NotNull(textBlock);

            scrolledTo = null;
            var mouseArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            };
            textBlock!.RaiseEvent(mouseArgs);
            Pump(() => scrolledTo is not null, TimeSpan.FromSeconds(5));

            Assert.Equal(10, scrolledTo); // navegou de NOVO — reclique no nó já selecionado funciona (I2)
        }
        finally
        {
            window?.Close();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Task 2 (Plano 3c): prova que o XAML REAL da aba "Campos" (3ª aba, `FormPanel.xaml` —
    /// `DataTemplate DataType="{x:Type vm:FormFieldViewModel}"` com 5 editores por Style/DataTrigger em
    /// `Type`, `xmlns:editing`, `RadioButton` via EVENTO em vez de binding puro — ver doc XML de
    /// `FormPanel.xaml.cs`) resolve, popula via o refresh assíncrono de VERDADE (Task.Run + `_dispatcher`
    /// pumpados pelo `Pump`, tanto na carga inicial quanto DEPOIS de um Apply real — nenhum teste de VM
    /// puro alcança o 2º caso, já que xUnit sem `Dispatcher.Run()` nunca deixa o fire-and-forget de
    /// `OnSessionApplied` disparar de verdade), edita Text/Radio via os controles REAIS e aplica pelo
    /// funil (`ApplyFormValuesCommand` do botão) — mesmo espírito de
    /// `MainWindow_SumarioTab_PopulatesTreeAndNavigatesOnClick`.
    [Fact]
    public void MainWindow_CamposTab_PopulatesEditsAppliesAndRefreshesAfterApply()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunCamposTabScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunCamposTabScenario()
    {
        mPdf.App.MainWindow? window = null;
        try
        {
            // ApplyFormValuesCommand é um AsyncRelayCommand com `await Task.Run(...)` no meio — em
            // PRODUÇÃO, `Application.Run()` já instala um `DispatcherSynchronizationContext` (é assim
            // que o `await` sempre retoma na UI thread depois de um Task.Run, em todo VM deste app).
            // Este teste (como os demais desta classe) constrói a Window/pump MANUALMENTE, sem nunca
            // chamar `Application.Run()`/`Dispatcher.Run()` — sem este `SetSynchronizationContext`
            // explícito, o `await` retomava numa thread do POOL (não a STA desta thread), e o
            // `AsyncRelayCommand` tentando notificar `CanExecuteChanged` pro `Button` real derrubava o
            // processo com `InvalidOperationException` (acesso cross-thread a um DependencyObject) —
            // reproduzido ao vivo escrevendo este teste. Nenhum OUTRO teste desta classe precisou disto
            // porque nenhum ainda exercitava um comando ASYNC com `Task.Run` através de um `Button`
            // REAL vinculado — mesma causa raiz, só não alcançada antes.
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            window = new mPdf.App.MainWindow();
            window.Show();

            var fake = new FakePdfEditor();
            // notifyError capturado (não o default de produção — UiPromptsTestGuard LANÇA nesse caso):
            // o refresh pós-Apply abaixo dispara uma notificação REAL de "edição perdida" (nome/genero
            // continuam DIRTY quando o campo "outro" substitui a leitura — ver Important 1 da revisão),
            // e essa notificação corre DENTRO do continuation assíncrono de RefreshFormFieldsAsync — sem
            // um fake aqui, o guard abortava o método ANTES de FormFieldEditors ser reatribuído, e o Pump
            // mais abaixo nunca convergia (sintoma observado ao vivo: timeout de 10s, lista NUNCA vira
            // ["outro"]).
            var errors = new List<string>();
            var doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")), editor: fake, notifyError: errors.Add);
            window.ViewModel.Documents.Add(doc);
            window.ViewModel.SelectedDocument = doc;
            window.ViewModel.ThumbnailsVisible = true; // mostra o painel esquerdo (TabControl novo dentro)

            var nome = new mPdf.Editing.FormFieldData(
                "nome", mPdf.Editing.FormFieldType.Text, "Original", Array.Empty<string>(), 0,
                new mPdf.Editing.PdfQuad(10, 10, 30, 30), IsReadOnly: false);
            var genero = new mPdf.Editing.FormFieldData(
                "genero", mPdf.Editing.FormFieldType.Radio, "M", new[] { "M", "F" }, 0, null, IsReadOnly: false);
            doc.SeedFormFieldsCache(false, new[] { nome, genero });

            var tabControl = FindDescendant<TabControl>(window);
            Assert.NotNull(tabControl);
            tabControl!.SelectedIndex = 2; // aba "Campos" (0 = Miniaturas, 1 = Sumário)
            Pump(() => FindDescendant<FormPanel>(window) is not null, TimeSpan.FromSeconds(10));
            var panel = FindDescendant<FormPanel>(window);
            Assert.NotNull(panel); // Grid/Style/MultiDataTrigger/xmlns:editing resolveram sem exceção

            var itemsControl = FindDescendantByDataContext<ItemsControl>(window, doc);
            // ItemsControl.DataContext é herdado do painel (DocumentViewModel) — mesmo objeto do doc.
            Pump(() => itemsControl?.Items.Count == 2, TimeSpan.FromSeconds(10));
            Assert.Equal(2, itemsControl!.Items.Count); // ItemsSource="{Binding FormFieldEditors}" resolveu de verdade

            var nomeField = doc.FormFieldEditors.First(f => f.Name == "nome");
            var generoField = doc.FormFieldEditors.First(f => f.Name == "genero");

            // Seleção: clicar no NOME do campo -> ScrollToPageRequested + destaque do widget (overlay real).
            int? scrolledTo = null;
            doc.ScrollToPageRequested += idx => scrolledTo = idx;
            var nomeBorder = FindDescendantByDataContext<Border>(window, nomeField);
            Assert.NotNull(nomeBorder);
            var nomeLabel = FindDescendant<TextBlock>(nomeBorder!);
            Assert.NotNull(nomeLabel);
            nomeLabel!.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.Equal(0, scrolledTo);
            Assert.Same(nomeField, doc.SelectedFormField);
            Assert.True(doc.Pages[0].HasFormFieldHighlight);

            // Editor de texto REAL: achar o TextBox pelo DataContext (campo "nome") e digitar.
            var nomeBox = FindDescendantByDataContext<TextBox>(window, nomeField);
            Assert.NotNull(nomeBox);
            nomeBox!.Text = "Novo Nome";
            Assert.Equal("Novo Nome", nomeField.EditedValue); // UpdateSourceTrigger=PropertyChanged propagou

            // Radio REAL: achar os 2 RadioButtons (DataContext = a OPÇÃO, "M"/"F") dentro do bloco do
            // campo "genero" — prova RadioOption_Loaded (estado inicial "M" já marcado) e
            // RadioOption_Checked (marcar "F" escreve de volta em EditedValue).
            var generoBorder = FindDescendantByDataContext<Border>(window, generoField);
            Assert.NotNull(generoBorder);
            Pump(() => FindDescendants<RadioButton>(generoBorder!).Count() == 2, TimeSpan.FromSeconds(5));
            var radios = FindDescendants<RadioButton>(generoBorder!).ToList();
            Assert.Equal(2, radios.Count);
            var radioM = radios.Single(r => (string)r.DataContext == "M");
            var radioF = radios.Single(r => (string)r.DataContext == "F");
            Assert.True(radioM.IsChecked, "RadioOption_Loaded deveria ter marcado a opção ATUAL (M)");
            radioF.IsChecked = true; // simula o clique real — dispara RadioOption_Checked
            Assert.Equal("F", generoField.EditedValue);

            // Aplicar alterações: botão REAL, Command bindado — junta só os campos ALTERADOS.
            var applyButton = FindDescendants<Button>(window).Single(b => Equals(b.Content, "Aplicar alterações"));
            Assert.True(applyButton.IsEnabled);
            // Configurado ANTES do clique: o refresh pós-Apply (fire-and-forget via _dispatcher, real
            // aqui — ver doc XML da classe) vai reler ReadFormFieldsResult quando rodar.
            fake.ReadFormFieldsResult = new[]
            {
                new mPdf.Editing.FormFieldData("outro", mPdf.Editing.FormFieldType.Text, "X", Array.Empty<string>(), 0, null, false),
            };
            fake.SetFormFieldsResult = Fixtures.ThirtyPages();
            applyButton.Command!.Execute(applyButton.CommandParameter);
            // Pino solto (finally { Session.EndEdit(); }) é o sinal de que o método INTEIRO terminou —
            // SetFormFieldsCallCount incrementa ainda DENTRO do Task.Run (background), antes do
            // continuation retomar na UI thread e aplicar de verdade (TryApplyEdit/Pages rebuild).
            Pump(() => !doc.Session.IsEditInFlight, TimeSpan.FromSeconds(5));

            Assert.Equal(1, fake.SetFormFieldsCallCount);
            Assert.Equal(2, fake.LastSetFormFieldsValues!.Count); // só os 2 ALTERADOS (nome+genero)
            Assert.Equal("Novo Nome", fake.LastSetFormFieldsValues["nome"]);
            Assert.Equal("F", fake.LastSetFormFieldsValues["genero"]);
            Assert.Equal(30, doc.Pages.Count); // ApplyEdit foi de verdade (Session.Snapshot trocou)

            // Refresh pós-Apply REAL: só um teste de INTEGRAÇÃO alcança isto — xUnit puro (sem
            // Dispatcher.Run()) nunca deixa o BeginInvoke de OnSessionApplied disparar (ver doc XML de
            // DocumentViewModel._dispatcher); aqui o Dispatcher real ESTÁ rodando (Pump o bombeia).
            Pump(() => doc.FormFieldEditors.Count == 1 && doc.FormFieldEditors[0].Name == "outro", TimeSpan.FromSeconds(10));
            Assert.Single(doc.FormFieldEditors);
            Assert.Equal("outro", doc.FormFieldEditors[0].Name);

            // Important 1 (revisão): FormFieldViewModel.MarkApplied já tinha desligado o dirty de
            // "nome"/"genero" no INSTANTE em que ApplyFormValues confirmou sucesso (antes mesmo deste
            // refresh rodar) — o refresh REAL que acabou de rodar não encontra NENHUM dirty pendente
            // pra preservar/perder, então nenhuma notificação de "edição descartada" dispara aqui (o
            // valor não foi descartado, foi APLICADO). Prova, através do Dispatcher de verdade, que os
            // dois efeitos colaterais (MarkApplied + o refresh assíncrono) não brigam entre si.
            Assert.Empty(errors);
        }
        finally
        {
            // O Apply real (ApplyFormValuesCommand) deixa `doc` SUJO (IsDirty) — MainWindow.OnClosing
            // -> MainViewModel.ConfirmCloseAll perguntaria "salvar antes de fechar?" pro documento sujo,
            // e o VM de PRODUÇÃO (new MainWindow(), sem fake de IConfirmCloseService) usa o default
            // guardado (UiPromptsTestGuard, Task 0) — LANÇA em vez de abrir um diálogo real (o
            // comportamento CERTO fora deste teardown, mas indesejado aqui). Esvazia Documents ANTES do
            // Close pra ConfirmCloseAll não achar nada sujo pra perguntar — mesmo espírito de descartar
            // o VM sem persistir (o teste não quer testar "salvar ao fechar", só a aba Campos).
            if (window is not null) window.ViewModel.Documents.Clear();
            window?.Close();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Task 4 (Plano 4): prova que o XAML REAL da aba "Assinaturas" (4ª aba, `SignaturePanel.xaml` —
    /// `DataTemplate DataType="{x:Type vm:SignatureRowViewModel}"`, estado vazio via Style/DataTrigger em
    /// `HasSignatures`, rodapé fixo "Validação oficial") resolve, popula via o refresh assíncrono de
    /// VERDADE (`_dispatcher.BeginInvoke` bombeado pelo `Pump`, disparado por um `Session.Applied` REAL —
    /// nenhum teste de VM puro alcança esse caminho, ver doc XML de `DocumentViewModel._dispatcher`) e
    /// que clicar no signatário dispara `ScrollToPageRequested` + destaque do carimbo (overlay real, cor
    /// laranja em `PdfViewerControl.xaml`) — mesmo espírito de
    /// `MainWindow_SumarioTab_PopulatesTreeAndNavigatesOnClick`/
    /// `MainWindow_CamposTab_PopulatesEditsAppliesAndRefreshesAfterApply`. `FakeSigningEngine` (não o
    /// motor real): este teste prova a MECÂNICA de binding/template/evento da View, não a criptografia —
    /// essa cobertura já existe em `mPdf.Signing.Tests`/`SignaturePanelTests.
    /// Sign_Integration_RealEngineWithEphemeralCertificate_...`.
    [Fact]
    public void MainWindow_AssinaturasTab_PopulatesRowsShowsEmptyStateAndNavigatesOnClick()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunAssinaturasTabScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunAssinaturasTabScenario()
    {
        mPdf.App.MainWindow? window = null;
        try
        {
            window = new mPdf.App.MainWindow();
            window.Show();

            var engine = new FakeSigningEngine(); // sem ReadSignaturesResult ainda -- documento "sem assinatura"
            var doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")), signingEngine: engine);
            window.ViewModel.Documents.Add(doc);
            window.ViewModel.SelectedDocument = doc;
            window.ViewModel.ThumbnailsVisible = true; // mostra o painel esquerdo (TabControl novo dentro)

            var tabControl = FindDescendant<TabControl>(window);
            Assert.NotNull(tabControl);
            tabControl!.SelectedIndex = 3; // aba "Assinaturas" (0=Miniaturas,1=Sumário,2=Campos)
            Pump(() => FindDescendant<SignaturePanel>(window) is not null, TimeSpan.FromSeconds(10));
            var panel = FindDescendant<SignaturePanel>(window);
            Assert.NotNull(panel); // DockPanel/Style/DataTrigger/xmlns:vm resolveram sem exceção

            // Estado vazio (brief, texto EXATO) — o fake devolve lista vazia até ReadSignaturesResult ser
            // configurado (ver FakeSigningEngine.ReadSignatures), então isto vale mesmo ANTES do refresh
            // assíncrono do construtor terminar (sem corrida pra esperar aqui).
            var emptyText = FindDescendants<TextBlock>(panel!)
                .FirstOrDefault(t => t.Text == "Este documento não tem assinaturas.");
            Assert.NotNull(emptyText);
            Assert.Equal(Visibility.Visible, emptyText!.Visibility);
            // Rodapé (brief, texto EXATO) — SEMPRE visível, inclusive no estado vazio.
            var footer = FindDescendants<TextBlock>(panel!)
                .FirstOrDefault(t => t.Text == "Validação oficial: validar.iti.gov.br");
            Assert.NotNull(footer);
            Assert.Equal(Visibility.Visible, footer!.Visibility);

            // 1 assinatura COM carimbo — reconfigura o fake e dispara um `Session.Apply` REAL (mesmo
            // caminho que `Session.CommitSigned` usa pra disparar `Applied`) — refresh assíncrono de
            // VERDADE (`_dispatcher`, bombeado pelo `Pump`), caminho que nenhum teste de VM puro alcança.
            var stampRect = new PdfQuad(50, 50, 150, 100);
            engine.ReadSignaturesResult = new[]
            {
                new SignatureInfo("Assinatura1", "Fulano de Tal", "01672780838", "ETSI.CAdES.detached",
                    DateTimeOffset.UtcNow, true, true, false, "Aprovado", DocMdpLevel.None, 0, stampRect)
            };
            doc.Session.Apply(Fixtures.ThirtyPages());

            Pump(() => doc.HasSignatures, TimeSpan.FromSeconds(10));
            Assert.True(doc.HasSignatures, "SignatureRows não populou via refresh assíncrono real (Task.Run + _dispatcher)");
            Assert.Single(doc.SignatureRows);

            var row = doc.SignatureRows[0];
            var rowTextBlock = FindDescendantByDataContext<TextBlock>(window, row);
            Assert.NotNull(rowTextBlock); // ItemsControl/DataTemplate resolveram de verdade

            int? scrolledTo = null;
            doc.ScrollToPageRequested += idx => scrolledTo = idx;
            rowTextBlock!.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

            Assert.Equal(0, scrolledTo);
            Assert.Same(row, doc.SelectedSignature);
            Assert.True(doc.Pages[0].HasSignatureStampHighlight);
        }
        finally
        {
            // Session.Apply acima deixa `doc` SUJO (IsDirty) — mesmo cinto de
            // RunCamposTabScenario (ver doc XML lá): esvazia Documents ANTES do Close pra
            // ConfirmCloseAll não achar nada sujo pra perguntar (o VM de PRODUÇÃO usado aqui, `new
            // MainWindow()`, não tem fake de IConfirmCloseService — o default guardado LANÇA em teste
            // headless em vez de abrir um diálogo real).
            if (window is not null) window.ViewModel.Documents.Clear();
            window?.Close();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Task 3 (Plano 5): critério de aceitação do brief — "TODOS os botões visíveis a 1200×800, MEDIDO
    /// por sonda STA", não uma inspeção visual. A medida é `ToolBar.HasOverflowItems`, propriedade que o
    /// próprio WPF calcula durante o passe de layout: `true` quando o `ToolBarPanel` interno não consegue
    /// caber todos os itens na largura disponível e empurra o resto pro popup de overflow — inacessível/
    /// invisível pra quem não sabe que o popup existe (achado do smoke do Plano 4 que motivou esta task:
    /// "Assinar em lote" ficou preso lá, sem ninguém perceber, com a ToolBar ÚNICA de ~30 botões).
    /// PERMANENTE por design (brief): não assume QUANTAS ToolBar existem nem QUAIS botões — qualquer
    /// botão futuro adicionado a QUALQUER linha da ToolBarTray que estoure 1200px reprova este teste
    /// imediatamente, sem depender de alguém notar visualmente de novo. `new MainWindow()` de PRODUÇÃO
    /// (mesmo ctor de `MainWindow_NoDocumentOpen_SignedDocumentBannerIsCollapsed`), SEM documento aberto —
    /// a largura da toolbar não depende de nenhum estado de documento, só da coleção fixa de botões no
    /// XAML. `UpdateLayout()` força um passe de Measure/Arrange SÍNCRONO (o cálculo de overflow do
    /// `ToolBarPanel` acontece dentro dele, não num tick futuro do Dispatcher — diferente do resto desta
    /// classe, este teste não precisa de `Pump`).
    [Fact]
    public void MainWindow_At1200x800_ToolbarsHaveNoOverflowItems()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunToolbarOverflowScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunToolbarOverflowScenario()
    {
        mPdf.App.MainWindow? window = null;
        try
        {
            window = new mPdf.App.MainWindow();
            // Width/Height já são 1200x800 por padrão no XAML (WindowStartupLocation=CenterScreen) —
            // explícito aqui pra este teste documentar o critério do brief por si só, sem depender de
            // ninguém não mexer no default do XAML sem perceber que isto quebra a premissa.
            window.Width = 1200;
            window.Height = 800;
            window.Show();
            window.UpdateLayout();

            var toolBars = FindDescendants<ToolBar>(window).ToList();
            // 2 linhas — ver o comentário da 2ª ToolBar em MainWindow.xaml (Task 3, Plano 5). Se este
            // número mudar, o design mudou e merece revisão consciente, não passar batido.
            Assert.Equal(2, toolBars.Count);

            for (int i = 0; i < toolBars.Count; i++)
            {
                var toolBar = toolBars[i];
                Assert.False(toolBar.HasOverflowItems,
                    $"ToolBar #{i} (Items={toolBar.Items.Count}, ActualWidth={toolBar.ActualWidth}) tem " +
                    "itens no popup de OVERFLOW a 1200×800 — inacessíveis pra um usuário leigo sem saber " +
                    "que o popup existe (motivo desta task: 'Assinar em lote' estava caindo aqui antes da " +
                    "reorganização em 2 linhas — ver comentário da 2ª ToolBar em MainWindow.xaml).");
            }
        }
        finally
        {
            window?.Close();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var found in FindDescendants<T>(child)) yield return found;
        }
    }

    private static T? FindDescendantByDataContext<T>(DependencyObject root, object dataContext) where T : FrameworkElement
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t && ReferenceEquals(t.DataContext, dataContext)) return t;
            if (FindDescendantByDataContext<T>(child, dataContext) is { } found) return found;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
