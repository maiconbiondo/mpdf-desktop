using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace mPdf.App.Tests;

/// Plano 12 (Task 1) — sonda visual: renderiza a `MainWindow` REAL (mesmo ctor de produção usado por
/// `ViewerIntegrationTests.MainWindow_At1200x800_ToolbarsHaveNoOverflowItems`) a 1200×800 e grava um
/// PNG do chrome (as 2 bandas da ToolBar + a faixa de abas do painel esquerdo) num caminho FIXO e
/// determinístico em %TEMP% — não um golden-image diff (o brief pede INSPEÇÃO A OLHO do resultado,
/// não uma asserção de pixel-a-pixel; travar pixels aqui reprovaria a suíte a cada ajuste visual
/// futuro do MESMO tema). O teste garante que a sonda RODA sem exceção e produz um arquivo não-vazio
/// — a inspeção em si (Read do PNG, descrição do resultado) é feita fora da suíte, registrada no
/// report desta task.
///
/// `ThumbnailsVisible = true` antes do Show(): sem isto o painel esquerdo (TabControl com as 4 abas
/// Miniaturas/Sumário/Campos/Assinaturas, onde o novo estilo de TabItem fica visível) fica oculto por
/// padrão (`MainViewModel.ThumbnailsVisible` nasce `false`) — nenhum documento precisa estar aberto
/// pra ver a FAIXA de abas em si (Header é texto fixo no XAML, não depende de SelectedDocument).
///
/// ACHADO AO VIVO (escrevendo esta sonda): NENHUM teste STA desta classe/arquivo (nem os já
/// existentes em `ViewerIntegrationTests`, nem este novo) constrói um `mPdf.App.App` — todos vão
/// direto pra `new mPdf.App.MainWindow()`. `Application.Current` fica `null` o processo de teste
/// INTEIRO, e a resolução de `StaticResource`/estilo implícito só sobe até `Application.Resources`
/// QUANDO `Application.Current` existe — sem ela, os estilos globais de `Themes/mPdfTheme.xaml`
/// mesclados em `App.xaml` NUNCA são consultados, e a janela renderiza com o chrome PADRÃO do WPF
/// (comprovado comparando a sonda com o merge presente vs. temporariamente removido — pixel-a-pixel
/// idêntico nos dois casos, até eu perceber a causa). Criar `new mPdf.App.App().InitializeComponent()`
/// dentro do teste RESOLVERIA isso, mas `Application.Current` é estado ESTÁTICO de processo — xUnit
/// paraleliza classes de teste por padrão (sem config de paralelismo neste projeto), e as ~20 sonda
/// STA de `ViewerIntegrationTests` já rodam em suas PRÓPRIAS threads STA dedicadas; setar
/// `Application.Current` a partir da thread desta classe arriscaria contaminar/desestabilizar TODAS
/// as outras (afinidade de thread do Dispatcher é por instância de `Application`, não por processo).
/// Em vez disso: mescla o MESMO dicionário do tema DIRETO em `window.Resources` (via pack URI pro
/// recurso já compilado no assembly `mPdf.App`) — a mesma cadeia de busca de recurso (elemento ->
/// ancestral -> Window.Resources -> Application.Resources) encontra os estilos implícitos a partir
/// daqui igualmente, 100% isolado por instância de Window/thread, sem tocar `Application.Current`.
/// Isto NÃO substitui a prova de que o app de PRODUÇÃO carrega o tema — essa prova já está no
/// `App.g.cs` gerado (`InitializeComponent` chama `Application.LoadComponent` quando
/// `Application.Resources` não está vazio, mesmo mecanismo padrão de QUALQUER app WPF) e no
/// `App.xaml`/merge em si, já cobertos por build bem-sucedido.
public class ThemeVisualProbeTests
{
    public static readonly string OutputPath = Path.Combine(Path.GetTempPath(), "mpdf-theme-chrome-probe.png");

    [Fact]
    public void MainWindow_ChromeAt1200x800_RendersToPngForInspection()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunProbe(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunProbe()
    {
        mPdf.App.MainWindow? window = null;
        try
        {
            window = new mPdf.App.MainWindow();
            // Mescla o tema DIRETO na janela (ver doc XML da classe — por que não dá pra confiar em
            // Application.Current aqui). Mesmo recurso compilado que App.xaml referencia via
            // Source="Themes/mPdfTheme.xaml" (relativo à raiz de mPdf.App) — pack URI absoluto pra
            // funcionar não importa de qual assembly (teste vs. app) o merge é disparado.
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/mPdf.App;component/Themes/mPdfTheme.xaml", UriKind.Absolute)
            });
            window.Width = 1200;
            window.Height = 800;
            // Revela a faixa de abas do painel esquerdo (ver doc XML da classe) — puramente pra a
            // sonda ter algo de "abas" pra fotografar junto da ToolBar, nenhum documento aberto.
            window.ViewModel.ThumbnailsVisible = true;
            window.Show();
            window.UpdateLayout();

            int cropW = Math.Min(1200, (int)Math.Ceiling(window.ActualWidth));
            int cropH = Math.Min(320, (int)Math.Ceiling(window.ActualHeight)); // 2 bandas de ToolBar + topo da faixa de abas
            Assert.True(cropW > 0 && cropH > 0, "janela renderizou com dimensão zero");

            var rtb = new RenderTargetBitmap(cropW, cropH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = new FileStream(OutputPath, FileMode.Create))
            {
                encoder.Save(fs);
            }

            var info = new FileInfo(OutputPath);
            Assert.True(info.Exists && info.Length > 0, $"PNG da sonda visual não foi gravado em {OutputPath}");
        }
        finally
        {
            window?.Close();
            try { mPdf.Documents.PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }
}
