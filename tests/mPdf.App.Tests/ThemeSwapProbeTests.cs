using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using mPdf.App.Services;
using Xunit;
using ThemeMode = mPdf.Documents.ThemeMode;

namespace mPdf.App.Tests;

/// Plano 14 (Task 1) — sonda STA que PROVA a fundação do tema trocável, sem tocar `Application.Current`
/// (estado estático de processo — mesmo motivo de ThemeVisualProbeTests). Monta numa Window os mesmos 2
/// dicionários que App.xaml mescla em produção — [Tokens.Escuro (ativo), mPdfTheme (estrutura + fontes)]
/// — e roda o `ThemeService` sobre a coleção MergedDictionaries da PRÓPRIA janela (o construtor injetável
/// existe pra isto). Prova: (a) os tokens `Cor.*` RESOLVEM no escuro (nenhum DynamicResource órfão);
/// (b) a fonte padrão Inter resolve; (c) trocar pra Claro e voltar re-pinta AO VIVO (o mesmo mecanismo do
/// toggle de Sobre em produção); (d) o fundo renderiza ESCURO (pixel extraído de um PNG).
public class ThemeSwapProbeTests
{
    private const string UriEscuro = "pack://application:,,,/mPdf.App;component/Themes/Tokens.Escuro.xaml";
    private const string UriMpdfTheme = "pack://application:,,,/mPdf.App;component/Themes/mPdfTheme.xaml";

    // Valores esperados de Cor.FundoJanela nos dois temas (dos dicionários de tokens).
    private static readonly Color EscuroFundoJanela = (Color)ColorConverter.ConvertFromString("#FF161826")!;
    private static readonly Color ClaroFundoJanela = (Color)ColorConverter.ConvertFromString("#FFECF0F5")!;

    // Todas as chaves de token que a estrutura referencia — nenhuma pode ficar sem resolver.
    private static readonly string[] TodasAsChavesCor =
    [
        "Cor.Primaria", "Cor.Acento", "Cor.Superficie", "Cor.Borda", "Cor.TextoPrimario",
        "Cor.TextoSecundario", "Cor.TextoDesabilitado", "Cor.SuperficieHover", "Cor.SuperficiePressionada",
        "Cor.Sucesso", "Cor.Erro", "Cor.FundoJanela", "Cor.FundoViewer", "Cor.FundoRail",
        "Cor.FundoPainel", "Cor.FundoTitulo",
    ];

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "thread STA não terminou dentro de 30s");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static Window MontarJanelaComTema(out Border alvo, out TextBlock texto)
    {
        var w = new Window { Width = 300, Height = 200 };
        // Mesma ordem de App.xaml: [Tokens ativo, mPdfTheme]. ThemeService troca o "Tokens.*".
        w.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(UriEscuro, UriKind.Absolute) });
        w.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(UriMpdfTheme, UriKind.Absolute) });

        alvo = new Border();
        alvo.SetResourceReference(Border.BackgroundProperty, "Cor.FundoJanela"); // DynamicResource em código
        texto = new TextBlock { Text = "Ação, coração, José — ç ã é õ" };
        texto.SetResourceReference(TextBlock.FontFamilyProperty, "Fonte.Inter");
        alvo.Child = texto;
        w.Content = alvo;
        return w;
    }

    [Fact]
    public void TemaEscuroDefault_ResolveTokens_ETrocaAoVivoParaClaroEVolta()
    {
        RunSta(() =>
        {
            var w = MontarJanelaComTema(out var alvo, out var texto);
            try
            {
                w.Show();
                w.UpdateLayout();

                // (a) todos os tokens de cor resolvem (nenhum DynamicResource órfão) — FindResource lança
                //     se faltar; e cada um é um SolidColorBrush de verdade.
                foreach (var chave in TodasAsChavesCor)
                    Assert.IsType<SolidColorBrush>(w.FindResource(chave));

                // Fundo inicial = ESCURO (o default v2.0).
                Assert.Equal(EscuroFundoJanela, ((SolidColorBrush)alvo.Background).Color);

                // (b) fonte padrão Inter resolve.
                Assert.Contains("Inter", texto.FontFamily.Source);

                // (c) troca AO VIVO pro Claro via ThemeService (sobre a coleção da PRÓPRIA janela).
                var service = new ThemeService(w.Resources.MergedDictionaries);
                service.Aplicar(ThemeMode.Claro);
                w.UpdateLayout();
                Assert.Equal(ClaroFundoJanela, ((SolidColorBrush)alvo.Background).Color);

                // ... e VOLTA pro Escuro.
                service.Aplicar(ThemeMode.Escuro);
                w.UpdateLayout();
                Assert.Equal(EscuroFundoJanela, ((SolidColorBrush)alvo.Background).Color);

                // Idempotência: aplicar o mesmo tema 2x não acumula dicionários "Tokens.*".
                service.Aplicar(ThemeMode.Escuro);
                int tokensDicts = 0;
                foreach (var d in w.Resources.MergedDictionaries)
                    if (d.Source is not null && d.Source.OriginalString.Contains("Tokens.")) tokensDicts++;
                Assert.Equal(1, tokensDicts);
            }
            finally
            {
                w.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void FundoEscuro_RenderizaEscuro_NoPng()
    {
        RunSta(() =>
        {
            var w = MontarJanelaComTema(out var alvo, out _);
            try
            {
                w.Show();
                w.UpdateLayout();

                int cw = Math.Max(1, (int)Math.Ceiling(alvo.ActualWidth));
                int ch = Math.Max(1, (int)Math.Ceiling(alvo.ActualHeight));
                var rtb = new RenderTargetBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(alvo);

                // Amostra 1 pixel (canto superior-esquerdo do Border) — deve ser escuro (o fundo do tema).
                var px = new byte[4];
                rtb.CopyPixels(new Int32Rect(0, 0, 1, 1), px, 4, 0);
                // Pbgra32: B,G,R,A. "Escuro" = cada canal RGB baixo (o FundoJanela #161826 = 22,24,38).
                Assert.True(px[0] < 80 && px[1] < 80 && px[2] < 80,
                    $"canto do fundo não ficou escuro (BGRA={px[0]},{px[1]},{px[2]},{px[3]}) — o tema escuro não pintou.");

                var outPath = Path.Combine(Path.GetTempPath(), "mpdf-tema-escuro-probe.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = new FileStream(outPath, FileMode.Create);
                encoder.Save(fs);
            }
            finally
            {
                w.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
    }
}
