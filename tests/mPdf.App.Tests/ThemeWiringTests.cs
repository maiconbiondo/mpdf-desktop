using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using mPdf.App.Views;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

/// Plano 12 (Task 2) — Task 1 só DISPONIBILIZOU os estilos KEYED (`mPdf.Button.Toolbar`/
/// `mPdf.Button.Dialog`/`mPdf.Border.Painel`), mesclados só em `App.xaml` (nunca em nenhum
/// `Window.Resources`/`UserControl.Resources` individual) — nenhum controle de chrome os
/// REFERENCIAVA ainda (ver task-1-report.md §3, "nenhum arquivo de chrome foi editado nesta task").
/// Esta suíte prova que a FIAÇÃO desta task realmente aconteceu (Style="{StaticResource ...}" nos
/// pontos nomeados pelo brief) e continua acontecendo no futuro — sem ela, alguém poderia remover um
/// atributo `Style=` por engano (ou reverter o merge local de `Themes/mPdfTheme.xaml` num dos
/// arquivos) e nenhum teste perceberia (o app ainda compila e roda, só volta a ficar com o chrome
/// padrão do Windows silenciosamente).
///
/// ACHADO estrutural que motivou esta suíte existir: `Application.Current` fica `null` o processo de
/// teste INTEIRO (nenhuma sonda STA deste projeto constrói `mPdf.App.App`, achado já documentado no
/// report do Task 1) — `{StaticResource mPdf.Button.Toolbar}` referenciado direto em `MainWindow.xaml`/
/// diálogos SEM uma mescla LOCAL do tema em cada arquivo (`Window.Resources`/`UserControl.Resources`)
/// lançaria `XamlParseException` na hora do `InitializeComponent()` de QUALQUER teste que construísse
/// essas janelas — não só as sondas de tema, TODA a suíte (SignDialogTests/SobreDialogTests/
/// ViewerIntegrationTests/etc.). Por isso esta task mesclou `Themes/mPdfTheme.xaml` localmente em
/// CADA arquivo tocado (MainWindow.xaml, SobreDialog/SignDialog/ExportImageDialog/MergeFilesDialog/
/// SplitDialog/FormPanel), redundante mas inofensivo com o merge de `App.xaml` em produção (o
/// dicionário mais próximo na cadeia de busca vence, mesmo conteúdo) — os testes abaixo, ao
/// construírem essas janelas com sucesso e resolverem os Styles via `FindResource`, também são a
/// prova de regressão de que essa mescla local continua presente.
public class ThemeWiringTests
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

        bool joined = thread.Join(TimeSpan.FromSeconds(20));
        Assert.True(joined, "thread STA não terminou dentro de 20s (BLOCKED: possível deadlock/hang do WPF)");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var found in FindDescendants<T>(child)) yield return found;
        }
    }

    [Fact]
    public void MainWindow_ToolbarButtons_ResolveToolbarStyle()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? window = null;
            try
            {
                window = new mPdf.App.MainWindow();
                window.Width = 1200;
                window.Height = 800;
                window.Show();
                window.UpdateLayout();

                // Plano 14 (Task 2): a ToolBarTray virou a COMMAND BAR redesenhada; os botões de comando
                // usam o estilo novo `mPdf.Button.Command` (não mais `mPdf.Button.Toolbar`). Amostra dos
                // botões da command bar (esquerda + direita) — o "Assinar" destacado usa outro estilo
                // (mPdf.Button.Assinar) e o zoom/fit usam mPdf.Button.Status, cobertos à parte.
                var expected = (Style)window.FindResource("mPdf.Button.Command");
                string[] toolTipsEsperados =
                [
                    "Abrir PDF (Ctrl+O)", "Salvar (Ctrl+S)", "Imprimir (Ctrl+P)", // grupo esquerdo
                    "Assinar vários PDFs em lote", "Sobre o mPDF", // esquerdo/direito
                ];
                var buttons = FindDescendants<Button>(window).ToList();
                foreach (var toolTip in toolTipsEsperados)
                {
                    var button = buttons.FirstOrDefault(b => (b.ToolTip as string) == toolTip);
                    Assert.True(button is not null, $"botão com ToolTip '{toolTip}' não encontrado na árvore visual da MainWindow");
                    Assert.Same(expected, button!.Style);
                }
            }
            finally
            {
                window?.Close();
                try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
                catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento real — já estamos desligando */ }
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void MainWindow_PanelContainerBorder_ResolvesPainelStyle()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? window = null;
            try
            {
                window = new mPdf.App.MainWindow();
                window.Width = 1200;
                window.Height = 800;
                // Revela o painel esquerdo (Border container das abas Miniaturas/Sumário/Campos/
                // Assinaturas) — colapsado por padrão (ThumbnailsVisible nasce false), mesma técnica
                // de ThemeVisualProbeTests.
                window.ViewModel.ThumbnailsVisible = true;
                window.Show();
                window.UpdateLayout();

                // Plano 14 (Task 2): o painel esquerdo passou de 150px para 238px (largura do redesenho)
                // — o Style continua sendo mPdf.Border.Painel (a fiação do Plano 12 que este teste guarda).
                var expected = (Style)window.FindResource("mPdf.Border.Painel");
                var panelBorder = FindDescendants<Border>(window).FirstOrDefault(b => b.Width == 238);
                Assert.True(panelBorder is not null, "Border container do painel esquerdo (Width=238) não encontrado");
                Assert.Same(expected, panelBorder!.Style);
            }
            finally
            {
                window?.Close();
                try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
                catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento real — já estamos desligando */ }
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void SobreDialog_Buttons_ResolveDialogStyle()
    {
        RunSta(() =>
        {
            var vm = new mPdf.App.ViewModels.SobreViewModel(
                confirmCloseAllDocuments: () => true,
                startInstaller: _ => { },
                shutdown: () => { },
                createSource: () => new NullUpdateSource());
            var dialog = new SobreDialog(vm);
            try
            {
                dialog.Show();
                dialog.UpdateLayout();

                // Plano 14 (Task 4): os diálogos usam os estilos escuros por PAPEL (cancelar contornado,
                // primário azul, neutro) — não mais o único mPdf.Button.Dialog do Plano 12. A guarda
                // segue provando que cada botão resolve o estilo KEYED pretendido (nunca o default do WPF).
                var cancelar = (Style)dialog.FindResource("mPdf.Button.DialogCancelar");
                var neutro = (Style)dialog.FindResource("mPdf.Button.DialogNeutro");
                var fechar = FindDescendants<Button>(dialog).First(b => (string)b.Content == "Fechar");
                Assert.Same(cancelar, fechar.Style);
                var verificar = (Button)dialog.FindName("VerificarButton")!;
                Assert.Same(neutro, verificar.Style);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void SignDialog_Buttons_ResolveDialogStyle()
    {
        RunSta(() =>
        {
            var dialog = new SignDialog(Array.Empty<mPdf.Signing.SigningCertificateInfo>(), allowDocMdp: true);
            try
            {
                dialog.Show();
                dialog.UpdateLayout();

                // Plano 14 (Task 4): Cancelar contornado + Assinar primário azul (ver mPdf.Button.Dialog*).
                var cancel = (Button)dialog.FindName("CancelButton")!;
                var ok = (Button)dialog.FindName("OkButton")!;
                Assert.Same((Style)dialog.FindResource("mPdf.Button.DialogCancelar"), cancel.Style);
                Assert.Same((Style)dialog.FindResource("mPdf.Button.DialogPrimario"), ok.Style);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void SplitDialog_Buttons_ResolveDialogStyle()
    {
        RunSta(() =>
        {
            var dialog = new SplitDialog();
            try
            {
                dialog.Show();
                dialog.UpdateLayout();

                // Plano 14 (Task 4): Cancelar (contornado), OK (primário azul), Escolher… (neutro) —
                // cada botão resolve um estilo KEYED de diálogo (nunca o default do WPF).
                var esperados = new[]
                {
                    (Style)dialog.FindResource("mPdf.Button.DialogCancelar"),
                    (Style)dialog.FindResource("mPdf.Button.DialogPrimario"),
                    (Style)dialog.FindResource("mPdf.Button.DialogNeutro"),
                };
                foreach (var button in FindDescendants<Button>(dialog))
                    Assert.Contains(button.Style, esperados);
            }
            finally { dialog.Close(); }
        });
    }

    /// Guarda de regressão do achado ao vivo desta task: o BorderThickness=1 OCIOSO de
    /// `mPdf.Button.Toolbar` era invisível (BorderBrush=Transparent) mas custava layout — somado nos
    /// ~19-25 botões de cada banda da ToolBarTray, isso derrubou a sonda de overflow
    /// (`ViewerIntegrationTests.MainWindow_At1200x800_ToolbarsHaveNoOverflowItems`) na 1ª tentativa de
    /// ligar os estilos. Fix: BorderThickness=0 ocioso em `mPdf.Button.Toolbar` (pixel-idêntico — nada
    /// pintava ali mesmo), preservando o anel de foco (trigger `IsKeyboardFocused` ainda usa 2) e
    /// preservando a borda VISÍVEL de `mPdf.Button.Dialog` (BorderBrush=Cor.Borda, não Transparente —
    /// sobrescreve de volta pra 1). Sem este teste, alguém "limpando duplicação" poderia remover o
    /// Setter de BorderThickness=1 de `mPdf.Button.Dialog` (parece redundante lido isolado — BasedOn
    /// já dá 0 do Toolbar) e reintroduzir OUTRO jeito de estourar a densidade sem perceber a ligação
    /// entre as duas coisas.
    [Fact]
    public void ButtonToolbarStyle_IdleBorderThickness_IsZero_DialogStyleOverridesToVisibleBorder()
    {
        RunSta(() =>
        {
            var window = new Window();
            try
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/mPdf.App;component/Themes/mPdfTheme.xaml", UriKind.Absolute)
                });

                var toolbarStyle = (Style)window.FindResource("mPdf.Button.Toolbar");
                var dialogStyle = (Style)window.FindResource("mPdf.Button.Dialog");

                var toolbarBorderThickness = toolbarStyle.Setters
                    .OfType<Setter>()
                    .Single(s => s.Property == Control.BorderThicknessProperty);
                Assert.Equal(new Thickness(0), toolbarBorderThickness.Value);

                var dialogBorderThickness = dialogStyle.Setters
                    .OfType<Setter>()
                    .Single(s => s.Property == Control.BorderThicknessProperty);
                Assert.Equal(new Thickness(1), dialogBorderThickness.Value);
            }
            finally { window.Close(); }
        });
    }

    // Fake mínimo (não usa nenhum arquivo de produção específico de rede) — só pra construir o
    // SobreViewModel sem tocar a internet, mesmo espírito de FakeUpdateSource em SobreDialogTests
    // (arquivo separado pra não depender de um `internal`/`file` de outra classe de teste).
    private sealed class NullUpdateSource : mPdf.App.Services.IUpdateSource
    {
        public Task<mPdf.App.Services.LatestRelease?> GetLatestAsync(CancellationToken ct) => Task.FromResult<mPdf.App.Services.LatestRelease?>(null);
    }
}
