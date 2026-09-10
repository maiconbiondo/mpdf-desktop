using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using mPdf.App.Converters;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

/// Plano 14 (Task 3) — painéis escuros, faixa de validação e estado vazio/boas-vindas. Sondas STA
/// (mesmo padrão de ShellTests/ViewerIntegrationTests) + testes puros dos conversores novos. Nada toca
/// o caminho de render/overlay (fronteira SAGRADA): só chrome/painéis.
public class Task3PaineisTests
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

    private static void Fechar(mPdf.App.MainWindow? w)
    {
        if (w is not null) w.ViewModel.Documents.Clear();
        w?.Close();
        try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* já desligando */ }
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    // ───────────────────────── FIDELIDADE (renderiza pra inspeção a olho) ─────────────────────────

    public static readonly string PngLeitorAssinado = Path.Combine(Path.GetTempPath(), "mpdf-t3-leitor-assinado.png");
    public static readonly string PngMiniaturas = Path.Combine(Path.GetTempPath(), "mpdf-t3-miniaturas.png");
    public static readonly string PngWelcome = Path.Combine(Path.GetTempPath(), "mpdf-t3-welcome.png");

    [Fact]
    public void Fidelidade_LeitorAssinado_FaixaEPainelAssinaturas()
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

                var engine = new FakeSigningEngine
                {
                    ReadSignaturesResult = new[]
                    {
                        new SignatureInfo("Assinatura1", "MARIA S. OLIVEIRA", "01672780838", "ETSI.CAdES.detached",
                            DateTimeOffset.UtcNow, true, true, true, "Aprovação contratual", DocMdpLevel.None, 0,
                            new PdfQuad(50, 50, 150, 100))
                    }
                };
                var doc = new DocumentViewModel(
                    DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")), signingEngine: engine);
                doc.IsSignedDocument = true; // faz a FAIXA de validação aparecer
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                w.UpdateLayout();
                Pump(() => doc.Pages.Count > 0, TimeSpan.FromSeconds(10));

                // Dispara o refresh REAL de assinaturas (mesmo caminho da integração) e abre a aba.
                doc.Session.Apply(Fixtures.ThirtyPages());
                Pump(() => doc.HasSignatures, TimeSpan.FromSeconds(10));
                var painel = Descendentes<TabControl>(w).First(); // PanelTabs (primeiro na árvore)
                painel.SelectedIndex = 3;
                w.ViewModel.ThumbnailsVisible = true;
                w.UpdateLayout();
                Pump(() => Descendentes<SignaturePanel>(w).Any(), TimeSpan.FromSeconds(5));
                w.UpdateLayout();

                SalvarPng(w, PngLeitorAssinado);
                Assert.True(new FileInfo(PngLeitorAssinado).Length > 0);

                // Também renderiza a aba Miniaturas (cartões de página) pra inspeção de fidelidade —
                // espera as miniaturas renderizarem via o scheduler assíncrono.
                painel.SelectedIndex = 0;
                w.UpdateLayout();
                Pump(() => doc.Thumbnails.Count > 0 && doc.Thumbnails[0].ImageSource is not null, TimeSpan.FromSeconds(10));
                w.UpdateLayout();
                Pump(() => doc.Thumbnails.Take(3).All(t => t.ImageSource is not null), TimeSpan.FromSeconds(5));
                w.UpdateLayout();
                SalvarPng(w, PngMiniaturas);
                Assert.True(new FileInfo(PngMiniaturas).Length > 0);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void Fidelidade_Welcome_EstadoVazioRenderiza()
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
                // WelcomeView presente e visível quando não há documento.
                var welcome = Descendentes<WelcomeView>(w).FirstOrDefault();
                Assert.NotNull(welcome);
                Assert.Equal(Visibility.Visible, welcome!.Visibility);
                SalvarPng(w, PngWelcome);
                Assert.True(new FileInfo(PngWelcome).Length > 0);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void Welcome_ColapsaQuandoDocumentoAberto()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                w.UpdateLayout();
                var welcome = Descendentes<WelcomeView>(w).First();
                Assert.Equal(Visibility.Collapsed, welcome.Visibility);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void Welcome_BotaoAbrir_LigadoAoOpenFileCommand()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Show();
                w.UpdateLayout();
                var welcome = Descendentes<WelcomeView>(w).First();
                var botao = Descendentes<Button>(welcome).First(b => b.Command == w.ViewModel.OpenFileCommand);
                Assert.NotNull(botao); // "Abrir arquivo…" religado ao MESMO comando existente
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void FaixaValidacao_VerAssinaturas_SelecionaPainelDeAssinaturas()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Show();
                var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                doc.IsSignedDocument = true;
                w.ViewModel.Documents.Add(doc);
                w.ViewModel.SelectedDocument = doc;
                w.UpdateLayout();

                // Botão "Ver assinaturas" na faixa (Content string).
                var ver = Descendentes<Button>(w).First(b => (b.Content as string) == "Ver assinaturas");
                ver.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, ver));
                w.UpdateLayout();

                var painel = Descendentes<TabControl>(w).First(); // PanelTabs
                Assert.Equal(3, painel.SelectedIndex); // aba "Assinaturas"
            }
            finally { Fechar(w); }
        });
    }

    // ───────────────────────── Conversores novos (puros, sem WPF) ─────────────────────────

    [Theory]
    [InlineData(0, "0 assinaturas válidas")]
    [InlineData(1, "1 assinatura válida")]
    [InlineData(3, "3 assinaturas válidas")]
    public void ContagemAssinaturas_Pluraliza(int n, string esperado)
    {
        var c = new ContagemAssinaturasParaRotuloConverter();
        Assert.Equal(esperado, c.Convert(n, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CaminhoParaNomeEPasta_SeparamNomeEDiretorio()
    {
        var nome = new CaminhoParaNomeConverter();
        var pasta = new CaminhoParaPastaConverter();
        var caminho = Path.Combine("C:", "docs", "contrato.pdf");
        Assert.Equal("contrato.pdf", nome.Convert(caminho, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(Path.GetDirectoryName(caminho), pasta.Convert(caminho, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PaginaMaisUm_SomaUmOuVazio()
    {
        var c = new PaginaMaisUmConverter();
        Assert.Equal("1", c.Convert(0, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("5", c.Convert(4, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("", c.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NuloParaVisibilidadeInverso_VisivelQuandoNulo()
    {
        var c = new NuloParaVisibilidadeInversoConverter();
        Assert.Equal(Visibility.Visible, c.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, c.Convert(new object(), typeof(Visibility), null, CultureInfo.InvariantCulture));
    }
}
