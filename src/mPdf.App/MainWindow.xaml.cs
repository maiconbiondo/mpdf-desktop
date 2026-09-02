using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents; // PendingDisposals mora aqui desde a Task 3 do Plano 3a (ver doc XML da classe)

namespace mPdf.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        // Plano 14 (Task 2): sincroniza o ícone do botão maximizar/restaurar (e o padding do estado
        // maximizado) com o WindowState real — inclui o estado INICIAL (WindowStartupLocation).
        StateChanged += (_, _) => AtualizarChromeMaximizado();
        Loaded += (_, _) => AtualizarChromeMaximizado();
        // Rider (revisão pós-Task 4, Plano 3a): varredura best-effort de pastas ÓRFÃS de undo/redo
        // (%TEMP%\mPDF\undo-*, mais de 24h) deixadas por sessões que crasharam/perderam energia antes
        // do Dispose rodar — ver doc XML de DocumentSession.SweepOrphanUndoSpillDirectories. try/catch
        // aqui é redundante com o try/catch INTERNO do método (que já nunca lança), mas documenta a
        // garantia no PRÓPRIO call site: uma falha de limpeza JAMAIS pode impedir a janela de abrir.
        try { DocumentSession.SweepOrphanUndoSpillDirectories(); }
        catch { /* melhor esforço — nunca pode impedir a janela de abrir */ }
        // Task 2 (Plano 7, fix Important 1 pós-revisão): MESMO raciocínio/MESMO try-catch acima, agora
        // também pros PDFs temporários de imagem convertida (%TEMP%\mPDF\open-*, mais de 24h) deixados
        // por MainViewModel.OpenImageAsNewDocument quando o usuário nunca dá "Salvar como" (ou o app
        // crasha/fecha antes) — ver doc XML de DocumentSession.SweepOrphanConvertedImageDirectories.
        try { DocumentSession.SweepOrphanConvertedImageDirectories(); }
        catch { /* melhor esforço — nunca pode impedir a janela de abrir */ }
        ViewModel = new MainViewModel(new FileDialogService());
        DataContext = ViewModel;
        InputBindings.Add(new KeyBinding(ViewModel.OpenFileCommand, Key.O, ModifierKeys.Control));
        // Ctrl+S salva a aba ATIVA (Task 3, Plano 3a) — SaveCommand já tem CanExecute (documento
        // limpo/nenhum documento, o KeyBinding simplesmente não dispara nada, mesmo comportamento do
        // botão desabilitado).
        InputBindings.Add(new KeyBinding(ViewModel.SaveCommand, Key.S, ModifierKeys.Control));
        // Ctrl+P imprime a aba ATIVA (Task 8) — PrintCommand já tem CanExecute (sem documento, o
        // KeyBinding simplesmente não dispara nada, mesmo comportamento do botão desabilitado).
        InputBindings.Add(new KeyBinding(ViewModel.PrintCommand, Key.P, ModifierKeys.Control));
        // Ctrl+C copia o texto selecionado (Task 3). Cada DocumentViewModel guarda sua PRÓPRIA
        // seleção (_pageWithSelection); o atalho só lê a do documento da aba ATIVA
        // (ViewModel.SelectedDocument) — é assim que trocar de aba naturalmente copia a seleção
        // daquela aba, sem precisar limpar a seleção das abas inativas.
        // Nota (Task 5): com foco no TextBox da busca e NADA selecionado nele, o TextBox não marca o
        // evento como tratado (Ctrl+C sem seleção não copia nada) — o KeyBinding da Window abaixo
        // ainda recebe o evento na borbulha e copia a seleção do PDF normalmente; comportamento aceito.
        InputBindings.Add(new KeyBinding(new RelayCommand(CopySelectedText), Key.C, ModifierKeys.Control));
        // Ctrl+F abre/foca a barra de busca (Task 5) da aba ATIVA — mesma lógica de "sempre a aba
        // corrente" do Ctrl+C acima.
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenSearch), Key.F, ModifierKeys.Control));
        // Ctrl+Z/Ctrl+Y desfazem/refazem na aba ATIVA (Task 4, Plano 3a) — UndoCommand/RedoCommand
        // vivem no DocumentViewModel (pilha de undo/redo é POR DOCUMENTO), então não dá pra fazer
        // `new KeyBinding(ViewModel.SelectedDocument.UndoCommand, ...)` uma vez só no construtor
        // (SelectedDocument muda ao trocar de aba, e pode ser null ao abrir a janela) — mesma técnica
        // de wrapper já usada por Ctrl+C/Ctrl+F acima: lê ViewModel.SelectedDocument NA HORA do atalho.
        InputBindings.Add(new KeyBinding(new RelayCommand(Undo), Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(Redo), Key.Y, ModifierKeys.Control));
        // Clique na miniatura (Task 6) rola o viewer da aba ATIVA até a página — ThumbnailsRail.DataContext
        // é o MESMO SelectedDocument da aba corrente (ver MainWindow.xaml), então não há ambiguidade de
        // "qual aba" aqui, diferente de CopySelectedText/OpenSearch que leem o VM antes de agir.
        ThumbnailsRail.ThumbnailClicked += pageIndex => CurrentViewer()?.ScrollToPage(pageIndex);
    }

    private void OpenSearch()
    {
        if (ViewModel.SelectedDocument is not { } doc) return;
        doc.Search.IsOpen = true;
        CurrentViewer()?.FocusSearchBar();
    }

    private void CopySelectedText()
    {
        if (ViewModel.SelectedDocument?.SelectedText is not { Length: > 0 } text) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard preso por outro app — não derrubar o app por causa de uma cópia */ }
    }

    // Task 4 (Plano 3a): CanExecute checado explicitamente aqui — o RelayCommand do KeyBinding em si
    // (`new RelayCommand(Undo)`) não tem predicado próprio, então SEMPRE "dispara" ao apertar Ctrl+Z;
    // é este guard que faz o atalho respeitar o mesmo CanUndo do botão ↶ da toolbar (sem documento, ou
    // documento sem histórico de undo, o atalho simplesmente não faz nada — mesmo comportamento de um
    // botão desabilitado).
    private void Undo()
    {
        if (ViewModel.SelectedDocument?.UndoCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    private void Redo()
    {
        if (ViewModel.SelectedDocument?.RedoCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    // I3 (revisão pós-Task 3, Plano 3a): fechar a JANELA inteira (✕ da barra de título, Alt+F4) tinha
    // o MESMO risco de perda de dados que fechar uma aba (CloseDocument já perguntava; a janela não).
    // ConfirmCloseAll pergunta (via o MESMO IConfirmCloseService) por CADA documento sujo; qualquer
    // recusa (Cancelar, ou Salvar que falha) cancela o fechamento da janela inteira — e.Cancel=true
    // PÁRA aqui, ANTES de OnClosed rodar, então nenhum documento é descartado nesse caso.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ViewModel.ConfirmCloseAll())
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var doc in ViewModel.Documents.ToArray())
            doc.Dispose();
        // Drena os descartes de sessão descarregados p/ thread-pool (ver PendingDisposals) antes
        // de deixar o processo morrer — teardown nativo do PDFium correndo durante o exit do
        // processo causa access violation. Limitado no tempo: nunca trava o encerramento.
        try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
        base.OnClosed(e);
    }

    private Views.PdfViewerControl? CurrentViewer()
    {
        // HIPÓTESE de implementação: achar o PdfViewerControl do item selecionado no visual tree.
        // Alternativa mais simples se falhar: guardar referência no Loaded do controle.
        if (Tabs.SelectedItem is null) return null;
        return FindDescendant<Views.PdfViewerControl>(Tabs);
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

    private void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentViewer() is { } v && ViewModel.SelectedDocument is { } d) d.FitWidth(v.ViewportWidth);
    }

    private void FitPage_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentViewer() is { } v && ViewModel.SelectedDocument is { } d) d.FitPage(v.ViewportWidth, v.ViewportHeight);
    }

    // ═══════════════════ Plano 14 (Task 2): gestão de janela (title bar custom) ═══════════════════

    private void SearchButton_Click(object sender, RoutedEventArgs e) => OpenSearch();

    // Plano 16 (Task 3): fechar o popup de "Exportar" ao escolher Word/Excel (o Command do item já
    // disparou — Click e Command ambos ocorrem no clique). Sem isto o popup ficaria aberto atrás do
    // diálogo modal de exportação.
    private void ExportMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ExportDocToggle is not null) ExportDocToggle.IsChecked = false;
    }

    // Clicar um ícone do activity rail revela o painel esquerdo (a seleção do painel em si é feita pelo
    // binding TwoWay IsChecked -> PanelTabs.SelectedIndex). ThumbnailsVisible continua sendo a flag de
    // visibilidade do painel do Plano 12 — mantida meaningful aqui.
    private void RailItem_Checked(object sender, RoutedEventArgs e) => ViewModel.ThumbnailsVisible = true;

    // Fix painel recolhível (padrão "activity bar" do VS Code): intercepta o clique ANTES do
    // RadioButton processar a marcação. Cada item do rail carrega Tag = índice do painel (ver
    // MainWindow.xaml). Dois casos do MESMO índice do painel ATIVO precisam ser tratados aqui porque
    // `RailItem_Checked` só dispara quando IsChecked MUDA de false->true — clicar um RadioButton já
    // marcado não gera Checked:
    //   1) painel ativo E visível -> recolhe (e marca Handled pra não reprocessar o clique à toa);
    //   2) painel ativo E recolhido -> reabre (IsChecked não muda, então é só aqui que isso acontece).
    // Clicar um ícone de OUTRO painel não entra em nenhum dos dois ramos: o fluxo normal segue
    // (IsChecked passa a true -> Checked troca o painel e mostra via RailItem_Checked acima).
    private void RailItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        if (rb.Tag is not string tagTexto || !int.TryParse(tagTexto, out int indice)) return;
        if (indice != PanelTabs.SelectedIndex) return; // outro painel: fluxo normal (Checked cuida)

        ViewModel.ThumbnailsVisible = !ViewModel.ThumbnailsVisible;
        e.Handled = true;
    }

    // Fix painel recolhível: botão discreto do cabeçalho do painel esquerdo — recolhe direto, sem
    // depender do rail (útil quando o usuário só quer mais espaço pro leitor).
    private void CollapsePanelButton_Click(object sender, RoutedEventArgs e) => ViewModel.ThumbnailsVisible = false;

    // Configurações (Task 2, Plano 17) — o ⚙ do rail é um RadioButton (reusa o Style mPdf.RailItem, que
    // exige TargetType RadioButton) mas fora do GroupName "rail" (não deve competir pela seleção de
    // PanelTabs) — sem grupo, um RadioButton clicado FICA marcado, então este handler desmarca na hora
    // pra ele nunca parecer "o painel ativo" (é uma AÇÃO que abre um diálogo, não um painel selecionável).
    private void ConfiguracoesButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb) rb.IsChecked = false;
        ViewModel.ConfiguracoesCommand.Execute(null);
    }

    // Plano 14 (Task 3): "Ver assinaturas" na faixa de validação — revela o painel esquerdo e seleciona
    // a aba "Assinaturas" (índice 3, mesma ordem do PanelTabs/activity rail). O binding TwoWay do rail
    // (IsChecked <-> SelectedIndex) acompanha e marca o ícone de assinaturas.
    private void VerAssinaturas_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ThumbnailsVisible = true;
        PanelTabs.SelectedIndex = 3;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// Troca o ícone maximizar<->restaurar e o padding do RootBorder conforme o WindowState. No estado
    /// MAXIMIZADO o WindowChrome (WindowStyle=None) desenha a janela ~7px além de cada borda do monitor,
    /// cortando o conteúdo; o padding compensa isso pra o conteúdo não ficar cortado (o hook
    /// WM_GETMINMAXINFO já garante que a área maximizada respeita a taskbar/work area).
    private void AtualizarChromeMaximizado()
    {
        bool max = WindowState == WindowState.Maximized;
        if (MaximizeGlyph is not null) MaximizeGlyph.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        if (RestoreGlyph is not null) RestoreGlyph.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
        if (MaximizeButton is not null) MaximizeButton.ToolTip = max ? "Restaurar" : "Maximizar";
        if (RootBorder is not null)
        {
            // Espessura do frame de redimensionamento do SO (varia por DPI) — só no maximizado.
            double b = SystemParameters.WindowResizeBorderThickness.Left + SystemParameters.WindowNonClientFrameThickness.Left;
            RootBorder.Padding = max ? new Thickness(b) : new Thickness(0);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Hook WM_GETMINMAXINFO: janela sem moldura (WindowStyle=None) + WindowChrome, ao maximizar,
        // por padrão cobre a tela INTEIRA (inclusive a taskbar). Este hook limita a área maximizada ao
        // "work area" do monitor onde a janela está — resolvendo o bug clássico do WindowChrome. Falha
        // aberta: se por algum motivo o HwndSource não existir, a janela ainda maximiza (só sem o ajuste).
        var origem = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        origem?.AddHook(HookJanela);
    }

    private static IntPtr HookJanela(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            AjustarMaxInfoParaWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void AjustarMaxInfoParaWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        RECT trabalho = info.rcWork;   // área útil (exclui taskbar)
        RECT monitorR = info.rcMonitor; // monitor inteiro
        // Posição/tamanho máximos RELATIVOS ao canto do monitor — respeitando a taskbar.
        mmi.ptMaxPosition.x = trabalho.left - monitorR.left;
        mmi.ptMaxPosition.y = trabalho.top - monitorR.top;
        mmi.ptMaxSize.x = trabalho.right - trabalho.left;
        mmi.ptMaxSize.y = trabalho.bottom - trabalho.top;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
