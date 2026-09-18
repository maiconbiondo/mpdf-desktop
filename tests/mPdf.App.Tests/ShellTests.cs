using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

/// Plano 14 (Task 2) — shell escuro: title bar custom (WindowChrome), command bar, activity rail e
/// status bar. Foco de RISCO: gestão de janela (min/max/restaurar) não pode quebrar pros 38 usuários.
/// Todas as sondas constroem a `MainWindow` de PRODUÇÃO numa thread STA (mesmo padrão de
/// ThemeWiringTests/ViewerIntegrationTests) e mesclam o tema LOCALMENTE (Application.Current == null no
/// processo de teste). Nada aqui toca o caminho de render/overlay (fronteira SAGRADA).
public class ShellTests
{
    private static void RunSta(Action scenario)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { scenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        bool joined = thread.Join(TimeSpan.FromSeconds(40));
        Assert.True(joined, "thread STA não terminou dentro de 40s (BLOCKED: possível deadlock/hang do WPF)");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    // Bare Window (sem o merge próprio da MainWindow): precisa de tokens + estrutura.
    private static void MesclarTema(Window w)
    {
        AdicionarTokens(w);
        w.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/mPdf.App;component/Themes/mPdfTheme.xaml", UriKind.Absolute) });
    }

    // MainWindow JÁ mescla mPdfTheme (estrutura) no próprio Window.Resources — mesclar de novo criaria
    // uma 2ª cópia (não a que os botões usam), quebrando Assert.Same de Style. Aqui só adiciono os
    // TOKENS (Application.Current == null no teste), pros {DynamicResource Cor.*} pintarem no PNG.
    private static void AdicionarTokens(Window w) =>
        w.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/mPdf.App;component/Themes/Tokens.Escuro.xaml", UriKind.Absolute) });

    private static IEnumerable<T> Descendentes<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var f in Descendentes<T>(child)) yield return f;
        }
    }

    private static void Pump(Func<bool> cond, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!cond() && DateTime.UtcNow < end)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static Button? BotaoPorTooltip(DependencyObject root, string tip) =>
        Descendentes<Button>(root).FirstOrDefault(b => (b.ToolTip as string) == tip);

    private static RadioButton? RadioPorTooltip(DependencyObject root, string tip) =>
        Descendentes<RadioButton>(root).FirstOrDefault(b => (b.ToolTip as string) == tip);

    // ───────────────────────── Gestão de janela (WindowChrome) ─────────────────────────

    [Fact]
    public void Window_UsaWindowChrome_ComCaption44_SemMolduraNativa()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                w.UpdateLayout();

                var chrome = WindowChrome.GetWindowChrome(w);
                Assert.NotNull(chrome);
                Assert.Equal(44, chrome!.CaptionHeight);
                Assert.False(chrome.UseAeroCaptionButtons);
                Assert.Equal(WindowStyle.None, w.WindowStyle);
                Assert.Equal(ResizeMode.CanResize, w.ResizeMode); // 8 bordas/cantos redimensionáveis
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void TitleBar_Maximizar_AlternaEstadoEIconeRestaurar()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                // Plano 18 (Task 1): a MainWindow agora abre Maximized por padrão (ver
                // MainWindow_AbreMaximizadaPorPadrao) — este teste cobre o CICLO de alternância do botão
                // (Normal <-> Maximized), não o estado inicial; restaura pra Normal primeiro pra isolar o
                // que este teste realmente afirma.
                w.WindowState = WindowState.Normal;
                w.UpdateLayout();

                var maxBtn = (Button)w.FindName("MaximizeButton")!;
                var maxGlyph = (TextBlock)w.FindName("MaximizeGlyph")!;
                var restoreGlyph = (Grid)w.FindName("RestoreGlyph")!;
                Assert.Equal(WindowState.Normal, w.WindowState);
                Assert.Equal(Visibility.Visible, maxGlyph.Visibility);
                Assert.Equal(Visibility.Collapsed, restoreGlyph.Visibility);

                Clicar(maxBtn);
                w.UpdateLayout();
                Assert.Equal(WindowState.Maximized, w.WindowState);
                Assert.Equal(Visibility.Collapsed, maxGlyph.Visibility); // vira ícone de restaurar
                Assert.Equal(Visibility.Visible, restoreGlyph.Visibility);

                Clicar(maxBtn);
                w.UpdateLayout();
                Assert.Equal(WindowState.Normal, w.WindowState);
                Assert.Equal(Visibility.Visible, maxGlyph.Visibility);
            }
            finally { Fechar(w); }
        });
    }

    // Plano 18 (Task 1): abrir maximizado por padrão. O estado inicial (ANTES de qualquer interação do
    // usuário) tem que ser Maximized — mas min/max/restore/hit-test (os testes acima e abaixo) continuam
    // funcionando normalmente (o hook WM_GETMINMAXINFO de OnSourceInitialized já roda independente do
    // WindowState inicial, ver comentário em MainWindow.xaml.cs).
    [Fact]
    public void MainWindow_AbreMaximizadaPorPadrao()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                // Já Maximized ANTES do Show() -- é o valor do WindowState declarado no XAML (não algo
                // que só se manifesta depois de exibir a janela).
                Assert.Equal(WindowState.Maximized, w.WindowState);
                w.Show();
                w.UpdateLayout();
                Assert.Equal(WindowState.Maximized, w.WindowState);
                // O ícone da title bar já reflete "maximizado" (RestoreGlyph, não MaximizeGlyph) desde o
                // primeiro layout -- AtualizarChromeMaximizado() roda no Loaded (ver construtor).
                var maxGlyph = (TextBlock)w.FindName("MaximizeGlyph")!;
                var restoreGlyph = (Grid)w.FindName("RestoreGlyph")!;
                Assert.Equal(Visibility.Collapsed, maxGlyph.Visibility);
                Assert.Equal(Visibility.Visible, restoreGlyph.Visibility);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void TitleBar_Minimizar_DefineMinimized()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                var minBtn = (Button)w.FindName("MinimizeButton")!;
                Clicar(minBtn);
                Assert.Equal(WindowState.Minimized, w.WindowState);
                w.WindowState = WindowState.Normal; // restaura pra fechar limpo
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void TitleBar_Botoes_SaoHitTestVisibleNoChrome()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                w.UpdateLayout();
                // Sem IsHitTestVisibleInChrome os cliques seriam engolidos pela área de arrastar do caption.
                foreach (var nome in new[] { "MinimizeButton", "MaximizeButton", "CloseButton" })
                {
                    var b = (Button)w.FindName(nome)!;
                    Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(b), $"{nome} não é hit-test-visible no chrome");
                }
            }
            finally { Fechar(w); }
        });
    }

    // ───────────────────────── Command bar: religamento aos comandos existentes ─────────────────────────

    [Fact]
    public void CommandBar_Botoes_LigadosAosComandosDoViewModel()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                w.UpdateLayout();
                var vm = w.ViewModel;

                // Comandos de NÍVEL DE JANELA (não dependem de documento aberto).
                Assert.Same(vm.OpenFileCommand, BotaoPorTooltip(w, "Abrir PDF (Ctrl+O)")!.Command);
                Assert.Same(vm.SaveCommand, BotaoPorTooltip(w, "Salvar (Ctrl+S)")!.Command);
                Assert.Same(vm.PrintCommand, BotaoPorTooltip(w, "Imprimir (Ctrl+P)")!.Command);
                Assert.Same(vm.MergeCommand, BotaoPorTooltip(w, "Juntar vários PDFs em um só")!.Command);
                Assert.Same(vm.SplitCommand, BotaoPorTooltip(w, "Dividir o documento em vários arquivos")!.Command);
                Assert.Same(vm.BatchSignCommand, BotaoPorTooltip(w, "Assinar vários PDFs em lote")!.Command);
                Assert.Same(vm.SobreCommand, BotaoPorTooltip(w, "Sobre o mPDF")!.Command);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void CommandBar_BotaoAssinar_UsaEstiloDestacado()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                w.UpdateLayout();
                var assinar = BotaoPorTooltip(w, "Assinar digitalmente");
                Assert.NotNull(assinar);
                Assert.Same(w.FindResource("mPdf.Button.Assinar"), assinar!.Style);
            }
            finally { Fechar(w); }
        });
    }

    // ───────────────────────── Sonda de overflow da command bar (Plano 16) ─────────────────────────

    /// Plano 16 (Task 3): o menu "Exportar como Word/Excel" adicionou UM botão de 38px à command bar.
    /// Esta sonda prova que os grupos ESQUERDA e DIREITA da barra NÃO se sobrepõem (não estouram) na
    /// largura de projeto do app (1200px) — mede o RENDER real (TransformToAncestor + ActualWidth), não
    /// o markup. Se um botão futuro estourar a barra, a borda direita do grupo esquerdo passaria a borda
    /// esquerda do grupo direito e este teste ficaria vermelho.
    [Fact]
    public void CommandBar_NaoEstoura_NaLarguraDeProjeto()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Width = 1200; w.Height = 800;
                w.Show();
                w.UpdateLayout();

                var left = (FrameworkElement)w.FindName("CommandBarLeft");
                var right = (FrameworkElement)w.FindName("CommandBarRight");
                Assert.NotNull(left);
                Assert.NotNull(right);

                var leftOrigin = left.TransformToAncestor(w).Transform(new Point(0, 0));
                var rightOrigin = right.TransformToAncestor(w).Transform(new Point(0, 0));
                double leftRightEdge = leftOrigin.X + left.ActualWidth;
                double rightLeftEdge = rightOrigin.X;

                Assert.True(leftRightEdge <= rightLeftEdge,
                    $"command bar estourou: borda direita do grupo esquerdo ({leftRightEdge:F0}) " +
                    $"passou a borda esquerda do grupo direito ({rightLeftEdge:F0}).");
            }
            finally { Fechar(w); }
        });
    }

    // ───────────────────────── Activity rail dirige o painel ─────────────────────────

    [Fact]
    public void ActivityRail_SelecionarIcone_TrocaPainelAtivo()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc; // rail/painel só aparecem com documento
                w.UpdateLayout();

                var panelTabs = (TabControl)w.FindName("PanelTabs")!;
                var railAssin = Descendentes<RadioButton>(w).First(r => (r.ToolTip as string) == "Assinaturas");
                railAssin.IsChecked = true; // escreve SelectedIndex=3 via IndiceIgualConverter
                w.UpdateLayout();
                Assert.Equal(3, panelTabs.SelectedIndex);

                var railSum = Descendentes<RadioButton>(w).First(r => (r.ToolTip as string) == "Sumário");
                railSum.IsChecked = true;
                w.UpdateLayout();
                Assert.Equal(1, panelTabs.SelectedIndex);
            }
            finally { Fechar(w); }
        });
    }

    // ───────────────────────── Fix painel recolhível (activity bar) ─────────────────────────

    [Fact]
    public void PainelMiniaturas_ComecaOculto_ComDocumento()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                w.UpdateLayout();

                // Decisão do usuário (2026-09-01): a coluna de miniaturas vem OCULTA por padrão; abrir
                // um documento NÃO a revela. O usuário clica no ícone do rail para vê-la.
                Assert.False(w.ViewModel.ThumbnailsVisible);
                var painel = (Border)w.FindName("ThumbnailsPanelBorder")!;
                Assert.Equal(Visibility.Collapsed, painel.Visibility);

                // O rail de 58px continua visível (para reabrir), independente do painel de 238px.
                var rail = Descendentes<RadioButton>(w).First(r => (r.ToolTip as string) == "Miniaturas");
                Assert.True(IsAncestorVisible(rail));
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void PainelMiniaturas_ThumbnailsVisible_AlternaVisibilidadeReal()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                w.UpdateLayout();

                // Começa oculto por padrão (decisão do usuário); mostrar explicitamente para exercitar o toggle.
                var painel = (Border)w.FindName("ThumbnailsPanelBorder")!;
                Assert.Equal(Visibility.Collapsed, painel.Visibility);

                w.ViewModel.ThumbnailsVisible = true;
                w.UpdateLayout();
                Assert.Equal(Visibility.Visible, painel.Visibility);

                w.ViewModel.ThumbnailsVisible = false;
                w.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, painel.Visibility);

                w.ViewModel.ThumbnailsVisible = true;
                w.UpdateLayout();
                Assert.Equal(Visibility.Visible, painel.Visibility);

                // Rail de 58px SEMPRE visível (só depende de haver documento), mesmo com o painel recolhido.
                w.ViewModel.ThumbnailsVisible = false;
                w.UpdateLayout();
                var rail = Descendentes<RadioButton>(w).First(r => (r.ToolTip as string) == "Assinaturas");
                Assert.True(IsAncestorVisible(rail));
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void ActivityRail_ClicarIconeAtivo_RecolheEReabrePainel()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                w.UpdateLayout();

                var painel = (Border)w.FindName("ThumbnailsPanelBorder")!;
                var panelTabs = (TabControl)w.FindName("PanelTabs")!;
                var railMini = Descendentes<RadioButton>(w).First(r => (r.ToolTip as string) == "Miniaturas");

                // Garante o painel "Miniaturas" como ativo (o SelectedIndex inicial do TabControl sem
                // seleção prévia é -1, não 0 — mesmo padrão de ActivityRail_SelecionarIcone_TrocaPainelAtivo).
                railMini.IsChecked = true;
                w.UpdateLayout();

                Assert.Equal(0, panelTabs.SelectedIndex);
                Assert.True(w.ViewModel.ThumbnailsVisible);
                Assert.Equal(Visibility.Visible, painel.Visibility);

                // Ícone JÁ ativo, painel visível -> clique RECOLHE.
                DispararPreviewMouseDown(railMini);
                w.UpdateLayout();
                Assert.False(w.ViewModel.ThumbnailsVisible);
                Assert.Equal(Visibility.Collapsed, painel.Visibility);

                // Mesmo ícone, painel recolhido -> clique REABRE (mesmo painel selecionado).
                DispararPreviewMouseDown(railMini);
                w.UpdateLayout();
                Assert.True(w.ViewModel.ThumbnailsVisible);
                Assert.Equal(Visibility.Visible, painel.Visibility);
                Assert.Equal(0, panelTabs.SelectedIndex);

                // Ícone de OUTRO painel -> troca o painel ativo E mostra.
                w.ViewModel.ThumbnailsVisible = false;
                w.UpdateLayout();
                var railAssin = Descendentes<RadioButton>(w).First(r => (r.ToolTip as string) == "Assinaturas");
                railAssin.IsChecked = true; // simula clique completo (checked -> mostra + seleciona)
                w.UpdateLayout();
                Assert.True(w.ViewModel.ThumbnailsVisible);
                Assert.Equal(3, panelTabs.SelectedIndex);
                Assert.Equal(Visibility.Visible, painel.Visibility);
            }
            finally { Fechar(w); }
        });
    }

    // Task 2 (Plano 17) — ⚙ Configurações: é uma AÇÃO (abre um diálogo), não um painel selecionável —
    // clicar não pode virar "o painel ativo" (não pertence ao GroupName "rail") nem alterar
    // PanelTabs.SelectedIndex/ThumbnailsVisible. Injeta um fake de ISobreDialogService-like
    // (IConfiguracoesDialogService) via UiPrompts (mesmo seam dos outros diálogos) pra provar que o
    // clique alcança MainViewModel.ConfiguracoesCommand sem abrir janela real nenhuma.
    [Fact]
    public void BotaoConfiguracoes_Clicar_AbreConfiguracoesDialog_SemAlterarSelecaoDoRail()
    {
        var original = mPdf.App.Services.UiPrompts.CreateConfiguracoesDialog;
        try
        {
            var spy = new SpyConfiguracoesDialogServiceShell();
            mPdf.App.Services.UiPrompts.CreateConfiguracoesDialog = () => spy;

            RunSta(() =>
            {
                mPdf.App.MainWindow? w = null;
                try
                {
                    w = new mPdf.App.MainWindow();
                    w.Show();
                    var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                    w.ViewModel.Documents.Add(doc);
                    w.ViewModel.SelectedDocument = doc;
                    w.UpdateLayout();

                    var panelTabs = (TabControl)w.FindName("PanelTabs")!;
                    int selecaoAntes = panelTabs.SelectedIndex;
                    bool thumbsAntes = w.ViewModel.ThumbnailsVisible;

                    // Plano 20: o ⚙ é agora um Button na BARRA SUPERIOR, ao lado do Sobre (não mais um
                    // RadioButton no rail). O rail NÃO tem mais um item "Configurações".
                    Assert.Null(RadioPorTooltip(w, "Configurações"));
                    var sobre = BotaoPorTooltip(w, "Sobre o mPDF");
                    var config = BotaoPorTooltip(w, "Configurações");
                    Assert.NotNull(sobre);
                    Assert.NotNull(config);
                    // Ambos são irmãos no mesmo container (cluster direito da barra superior).
                    Assert.Same(VisualTreeHelper.GetParent(sobre!), VisualTreeHelper.GetParent(config!));

                    // O ⚙ da barra superior é um Button ligado direto a ConfiguracoesCommand (sem handler
                    // de Click de code-behind) — executar o Command do botão abre o diálogo pelo seam.
                    Assert.Same(w.ViewModel.ConfiguracoesCommand, config!.Command);
                    config.Command.Execute(config.CommandParameter);
                    w.UpdateLayout();

                    Assert.Equal(1, spy.CallCount);
                    Assert.Equal(selecaoAntes, panelTabs.SelectedIndex); // seleção do rail preservada
                    Assert.Equal(thumbsAntes, w.ViewModel.ThumbnailsVisible); // painel de miniaturas não mexido
                }
                finally { Fechar(w); }
            });
        }
        finally { mPdf.App.Services.UiPrompts.CreateConfiguracoesDialog = original; }
    }

    [Fact]
    public void BotaoRecolherCabecalho_EscondeOPainel()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                // Painel vem oculto por padrão; mostrar para exercitar o botão de recolher.
                w.ViewModel.ThumbnailsVisible = true;
                w.UpdateLayout();

                var painel = (Border)w.FindName("ThumbnailsPanelBorder")!;
                Assert.Equal(Visibility.Visible, painel.Visibility);

                var botaoRecolher = (Button)w.FindName("CollapsePanelButton")!;
                Assert.Equal("Recolher painel", botaoRecolher.ToolTip as string);
                Clicar(botaoRecolher);
                w.UpdateLayout();

                Assert.False(w.ViewModel.ThumbnailsVisible);
                Assert.Equal(Visibility.Collapsed, painel.Visibility);
            }
            finally { Fechar(w); }
        });
    }

    private static void DispararPreviewMouseDown(UIElement elemento)
    {
        var e = new MouseButtonEventArgs(InputManager.Current.PrimaryMouseDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        elemento.RaiseEvent(e);
    }

    private static bool IsAncestorVisible(DependencyObject d)
    {
        while (d is not null)
        {
            if (d is UIElement ui && ui.Visibility != Visibility.Visible) return false;
            d = VisualTreeHelper.GetParent(d)!;
        }
        return true;
    }

    // ───────────────────────── Scrollbar fina no rail (Plano 18 T1) ─────────────────────────

    /// Prova ESTRUTURAL de duas coisas na MESMA sonda: (1) o ScrollBar vertical resolvido dentro de
    /// `AnotacaoRailStrip` (a tira de anotação no rail, Plano 17 T3) usa o estilo fino
    /// (`mPdf.ScrollViewer.Fino` -> `mPdf.ScrollBar.Fino`): largura <= 8px e NENHUM RepeatButton de
    /// linha/seta (LineUp/LineDown) no template resolvido; (2) o ScrollViewer do CAMINHO DE RENDER
    /// (`PdfViewerControl.FindPageListScrollViewer`, a ListBox virtualizada das páginas) NÃO herdou o
    /// estilo fino — continua com o Style default do WPF (fronteira SAGRADA: nunca o viewer de página).
    [Fact]
    public void AnotacaoRailStrip_ScrollBar_FinoESoNoRail_ViewerDePaginaIntocado()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                // Janela BAIXA (o cenário do usuário/screenshot): a tira de anotação, com todas as
                // ferramentas, estoura a altura disponível no rail e precisa rolar.
                w.Width = 1200; w.Height = 420;
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                w.ViewModel.MenuAnotacaoNaBarraLateral = true; // ativa a tira vertical no rail (Plano 17 T3)
                w.UpdateLayout();

                var tira = (ScrollViewer)w.FindName("AnotacaoRailStrip")!;
                Assert.Equal(Visibility.Visible, tira.Visibility);
                Assert.Same(w.FindResource("mPdf.ScrollViewer.Fino"), tira.Style);

                var scrollBarDoRail = Descendentes<ScrollBar>(tira).FirstOrDefault(sb => sb.Name == "PART_VerticalScrollBar");
                Assert.NotNull(scrollBarDoRail);
                Assert.Same(w.FindResource("mPdf.ScrollBar.Fino"), scrollBarDoRail!.Style);
                Assert.True(scrollBarDoRail.Width <= 8, $"ScrollBar do rail não é fino: Width={scrollBarDoRail.Width}");

                // Nenhum RepeatButton com comando de LINHA (a setinha do topo/base do ScrollBar padrão do
                // Windows) no template resolvido -- só o Track+Thumb (ver mPdf.ScrollBar.Fino).
                var setasDeLinha = Descendentes<RepeatButton>(scrollBarDoRail)
                    .Where(rb => rb.Command == ScrollBar.LineUpCommand || rb.Command == ScrollBar.LineDownCommand
                              || rb.Command == ScrollBar.LineLeftCommand || rb.Command == ScrollBar.LineRightCommand)
                    .ToList();
                Assert.Empty(setasDeLinha);

                // Fronteira: o ScrollViewer do viewer de página (dentro do PdfViewerControl ativo) NUNCA
                // usa o estilo fino -- Style default (null, herdado do tema do sistema), não o KEYED daqui.
                var pdfViewerControl = Descendentes<PdfViewerControl>(w).FirstOrDefault();
                Assert.NotNull(pdfViewerControl);
                var scrollViewerDaPagina = pdfViewerControl!.FindPageListScrollViewer();
                Assert.NotNull(scrollViewerDaPagina);
                Assert.NotSame(w.FindResource("mPdf.ScrollViewer.Fino"), scrollViewerDaPagina!.Style);

                // Sem filtro por Name aqui de propósito: o ScrollBar do template DEFAULT do WPF (não é o
                // nosso, não controlamos o x:Name interno dele) -- só precisa existir e não ser o fino.
                var scrollBarDaPagina = Descendentes<ScrollBar>(scrollViewerDaPagina).FirstOrDefault();
                Assert.NotNull(scrollBarDaPagina);
                Assert.NotSame(w.FindResource("mPdf.ScrollBar.Fino"), scrollBarDaPagina!.Style);
                // ScrollBar de sistema (não estilizado) tem largura BEM maior que 8px (tipicamente ~17px,
                // varia por DPI/tema do Windows) -- prova por MEDIDA, não só por Style != KEYED.
                Assert.True(scrollBarDaPagina.ActualWidth > 8 || double.IsNaN(scrollBarDaPagina.Width),
                    $"ScrollBar do viewer de página parece ter encolhido: ActualWidth={scrollBarDaPagina.ActualWidth}");
            }
            finally { Fechar(w); }
        });
    }

    // ───────────────────────── Status bar: zoom/fit ligados ─────────────────────────

    [Fact]
    public void StatusBar_ZoomEFit_LigadosAoDocumento()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                w.UpdateLayout();

                Assert.Same(doc.ZoomOutCommand, BotaoPorTooltip(w, "Reduzir zoom")!.Command);
                Assert.Same(doc.ZoomInCommand, BotaoPorTooltip(w, "Ampliar zoom")!.Command);
                // Largura/Página existem e são clicáveis (fiados por Click no code-behind pra FitWidth/FitPage).
                Assert.NotNull(BotaoPorTooltip(w, "Ajustar à largura"));
                Assert.NotNull(BotaoPorTooltip(w, "Página inteira"));
            }
            finally { Fechar(w); }
        });
    }

    // ───────────────────────── Correção do bug herdado do "White" na aba selecionada ─────────────────────────

    [Fact]
    public void DocTabAtiva_NaoEBranca_NoTemaEscuro()
    {
        RunSta(() =>
        {
            var w = new Window();
            try
            {
                MesclarTema(w);
                var brush = (SolidColorBrush)w.FindResource("Cor.DocTabAtiva");
                // #161826 (fundo da janela do redesenho) — NÃO branco (o débito do Plano 12).
                Assert.NotEqual(Colors.White, brush.Color);
                Assert.Equal(Color.FromRgb(0x16, 0x18, 0x26), brush.Color);
            }
            finally { w.Close(); }
        });
    }

    // ───────────────────────── FIDELIDADE: renderiza o shell (leitor + vazio) pra inspeção ─────────────────────────

    public static readonly string PngLeitor = Path.Combine(Path.GetTempPath(), "mpdf-shell-leitor.png");
    public static readonly string PngVazio = Path.Combine(Path.GetTempPath(), "mpdf-shell-vazio.png");

    [Fact]
    public void Fidelidade_EstadoVazio_RenderizaShell()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Width = 1200; w.Height = 800;
                w.Show();
                w.UpdateLayout();
                SalvarPng(w, PngVazio);
                Assert.True(new FileInfo(PngVazio).Length > 0);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void Fidelidade_EstadoLeitor_RenderizaShell()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Width = 1200; w.Height = 800;
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                w.UpdateLayout();
                Pump(() => doc.Pages.Count > 0, TimeSpan.FromSeconds(10));
                w.UpdateLayout();
                SalvarPng(w, PngLeitor);
                Assert.True(new FileInfo(PngLeitor).Length > 0);
            }
            finally { Fechar(w); }
        });
    }

    private static void SalvarPng(Window w, string caminho)
    {
        int cw = Math.Min(1200, (int)Math.Ceiling(w.ActualWidth));
        int ch = Math.Min(800, (int)Math.Ceiling(w.ActualHeight));
        var rtb = new RenderTargetBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = new FileStream(caminho, FileMode.Create);
        enc.Save(fs);
    }

    private static void Clicar(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
    private static void Clicar(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));

    // Fake local (não usa nenhum arquivo de produção) — só pra provar que o ⚙ do rail alcança o comando
    // via o seam UiPrompts, sem abrir janela real nenhuma (mesmo padrão de UiPromptsGuardTests).
    private sealed class SpyConfiguracoesDialogServiceShell : mPdf.App.Services.IConfiguracoesDialogService
    {
        public int CallCount { get; private set; }
        public void ShowConfiguracoesDialog(ConfiguracoesViewModel viewModel) => CallCount++;
    }

    private static void Fechar(Window? w)
    {
        w?.Close();
        try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* já desligando */ }
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }
}
