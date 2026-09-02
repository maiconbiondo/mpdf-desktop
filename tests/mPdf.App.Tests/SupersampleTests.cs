using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Documents;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

/// Task 1 (Plano 13): nitidez do texto por SUPERSAMPLING — mesmo mecanismo do dpiFactor (Task 2, Plano
/// 9, ver `PageDpiTests.cs`), com um MULTIPLICADOR extra (`DocumentViewModel.SupersampleFactor`)
/// aplicado à escala de RENDER só quando a escala efetiva (zoom×PtToPx×dpiFactor) está ABAIXO do
/// limiar `PageViewModel.SupersampleThreshold` (≈100% zoom em monitor 100%, onde o 1:1 do PDFium sofre
/// mais) — acima do limiar o bitmap já nasce denso o bastante, SS vira no-op (fator efetivo 1.0). A
/// fronteira lógico×device é IDÊNTICA à do P9 T2: só o BITMAP nasce mais denso; `DisplayWidth`/
/// `DisplayHeight`/overlays/seleção/caixa do carimbo continuam usando só `zoom * PtToPx`, sem o fator
/// de SS nem de DPI (prova de regressão: toda a suíte de overlay/seleção/caixa/scroll já existente
/// continua verde SEM ser tocada por esta task).
public class SupersampleTests
{
    // ---- Testes PUROS (Item 1-3, 8): exercitam `PageViewModel.ComputeRenderScale` diretamente — a
    // MESMA função usada por `RequestRender` em produção (reuso exato, não uma reimplementação
    // paralela testada isoladamente) — nenhum WPF/STA necessário, então rodam instantâneos.

    [Fact] // fator 1.0 (comportamento de hoje) é IDENTIDADE IEEE: x*1.0 nunca arredonda — comparado
    // contra a fórmula de ANTES da task (zoom*PtToPx*dpiFactor, sem fator nenhum na conta) byte a byte.
    public void ComputeRenderScale_FactorOne_IsByteIdenticalToPreTaskFormula()
    {
        double zoom = 1.0, dpiFactor = 1.0;
        double expectedScale = zoom * PageViewModel.PtToPx * dpiFactor; // fórmula de ANTES da task
        double expectedDpi = 96.0 * dpiFactor;

        var (scale, dpi) = PageViewModel.ComputeRenderScale(zoom, dpiFactor, supersampleFactor: 1.0);

        Assert.Equal(expectedScale, scale); // sem tolerância — identidade exata
        Assert.Equal(expectedDpi, dpi);
    }

    [Fact] // 100% zoom, monitor 100% (dpiFactor 1.0): escala efetiva ~1.333 < limiar 1.4 -> SS 1.5x
    // aplica nos DOIS lados (escala de render E tag de DPI) — tamanho LÓGICO fica igual (nenhum dos
    // dois entra em DisplayWidth/DisplayHeight).
    public void ComputeRenderScale_BelowThreshold_MultipliesScaleAndDpiBySupersampleFactor()
    {
        double zoom = 1.0, dpiFactor = 1.0, ss = 1.5;
        double baseScale = zoom * PageViewModel.PtToPx * dpiFactor;
        Assert.True(baseScale < PageViewModel.SupersampleThreshold, "pré-condição do teste: escala efetiva devia estar abaixo do limiar");

        var (scale, dpi) = PageViewModel.ComputeRenderScale(zoom, dpiFactor, ss);

        Assert.Equal(baseScale * ss, scale);
        Assert.Equal(96.0 * dpiFactor * ss, dpi);
    }

    [Fact] // 300% zoom: escala efetiva ~4.0 >= limiar -> SS PULADO (fator efetivo 1.0), prova que o
    // limiar funciona (SS só onde a densidade é baixa).
    public void ComputeRenderScale_AtOrAboveThreshold_SkipsSupersampling()
    {
        double zoom = 3.0, dpiFactor = 1.0, ss = 1.5;
        double baseScale = zoom * PageViewModel.PtToPx * dpiFactor;
        Assert.True(baseScale >= PageViewModel.SupersampleThreshold, "pré-condição do teste: escala efetiva devia estar no limiar ou acima");

        var (scale, dpi) = PageViewModel.ComputeRenderScale(zoom, dpiFactor, ss);

        Assert.Equal(baseScale, scale); // SS não multiplicou nada
        Assert.Equal(96.0 * dpiFactor, dpi);
    }

    [Fact] // interação com o dpiFactor do P9 (risco declarado no plano): um monitor já a 150% (dpiFactor
    // 1.5) a 100% de zoom já produz escala efetiva 2.0 (>= limiar) — o SS não acrescenta nada ali, só
    // o dpiFactor conta. Prova que os dois mecanismos compõem pela MESMA regra de limiar, sem SS "dobrar"
    // a densidade à toa numa tela que já é nítida por si.
    public void ComputeRenderScale_HighDpiFactorAlreadyAboveThreshold_SupersampleIsNoOp()
    {
        double zoom = 1.0, dpiFactor = 1.5, ss = 1.5;
        double baseScale = zoom * PageViewModel.PtToPx * dpiFactor;
        Assert.True(baseScale >= PageViewModel.SupersampleThreshold);

        var (scale, dpi) = PageViewModel.ComputeRenderScale(zoom, dpiFactor, ss);

        Assert.Equal(baseScale, scale);
        Assert.Equal(96.0 * dpiFactor, dpi);
    }

    [Fact] // default (config ISOLADO, sem NitidezExtra) — comportamento de hoje: fator 1.0. Config
    // isolado de propósito (Plano 17, Task 1): o intento é "config default → 1.0"; ler o config REAL da
    // máquina (que pode ter NitidezExtra=true) daria 2.0 e é exatamente o defeito de isolamento corrigido.
    public void SupersampleFactor_DefaultsToOne()
    {
        using var doc = Doc(TempConfig());
        Assert.Equal(1.0, doc.SupersampleFactor);
    }

    // ---- Task 2 (Plano 13): SupersampleFactor INICIAL lido de AppConfig.NitidezExtra ------------------
    //
    // DocumentViewModel(config: ...) já existia (Task 3, Plano 3a — Autor/CriarBackup); esta task só
    // ACRESCENTA uma leitura a mais no MESMO parâmetro já injetável, sem mudar a assinatura pública.

    [Fact] // config SEM config.json ainda (1º uso do app) -> NitidezExtra=false (default) -> fator 1.0,
    // mesmo comportamento de hoje -- a PROVA central do "default OFF = ninguém paga o custo sem escolher".
    public void SupersampleFactor_ConfigWithoutFile_DefaultsToOne()
    {
        using var doc = Doc(TempConfig());
        Assert.Equal(1.0, doc.SupersampleFactor);
    }

    [Fact] // config com NitidezExtra=false explícito -> fator 1.0.
    public void SupersampleFactor_ConfigNitidezExtraFalse_IsOne()
    {
        var config = TempConfig();
        config.NitidezExtra = false;

        using var doc = Doc(config);

        Assert.Equal(1.0, doc.SupersampleFactor);
    }

    [Fact] // config com NitidezExtra=true -> fator 2.0 (fator de PRODUÇÃO medido na Task 1, NÃO 1.5 --
    // ver DocumentViewModel.NitidezExtraSupersampleFactor).
    public void SupersampleFactor_ConfigNitidezExtraTrue_IsTwo()
    {
        var config = TempConfig();
        config.NitidezExtra = true;

        using var doc = Doc(config);

        Assert.Equal(2.0, doc.SupersampleFactor);
        Assert.Equal(2.0, mPdf.App.ViewModels.DocumentViewModel.NitidezExtraSupersampleFactor);
    }

    /// STA ponta a ponta (exemplar EXATO: `PageDpiTests.Viewer_HighDpiFactor_RendersDenserTaggedBitmap_
    /// WithLogicalLayoutUnchanged`) — injeta `SupersampleFactor` direto no `DocumentViewModel` (seam
    /// testável, nenhum config/UI de Task 2 precisa existir ainda) numa `PdfViewerControl` REAL dentro
    /// de uma `Window`, e prova a fronteira completa: (a) o BITMAP entregue nasce mais denso E com a
    /// tag de DPI multiplicada; (b) o LAYOUT na tela (`DisplayWidth`/`DisplayHeight`, o `Image` real)
    /// não muda 1px.
    [Fact]
    public void Viewer_SupersampleFactor_RendersDenserTaggedBitmap_WithLogicalLayoutUnchanged()
    {
        RunSta(RunSupersampleScenario, TimeSpan.FromSeconds(30));
    }

    private static void RunSupersampleScenario()
    {
        DocumentViewModel? doc = null;
        PdfViewerControl? control = null;
        Window? window = null;
        try
        {
            // Config ISOLADO (Plano 17, Task 1): sem ele o ctor lê o config REAL da máquina, e com
            // NitidezExtra=true o SupersampleFactor inicial vira 2.0 → bmpBefore.DpiX=192 (não 96) e o
            // teste falha. O teste injeta o fator explicitamente adiante; o baseline precisa do 1.0.
            doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")), config: TempConfig());
            control = new PdfViewerControl { DataContext = doc };
            window = new Window { Width = 1000, Height = 800, Content = control };
            window.Show();

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            var bmpBefore = Assert.IsAssignableFrom<BitmapSource>(doc.Pages[0].ImageSource);
            Assert.Equal(96, bmpBefore.DpiX, 3); // 100% zoom, dpiFactor 1.0, SS 1.0 (default) -> hoje

            double logicalWidthBefore = doc.Pages[0].DisplayWidth;
            double logicalHeightBefore = doc.Pages[0].DisplayHeight;

            // ---- injeta SupersampleFactor 1.5 a 100% de zoom (escala efetiva abaixo do limiar) -------
            doc.SupersampleFactor = 1.5;
            Pump(() => doc.Pages[0].ImageSource is BitmapSource b && b.DpiX > 96, TimeSpan.FromSeconds(20));

            var bmpAfter = Assert.IsAssignableFrom<BitmapSource>(doc.Pages[0].ImageSource);
            Assert.Equal(144, bmpAfter.DpiX, 3); // 96 * 1.5
            Assert.Equal(144, bmpAfter.DpiY, 3);

            using (var independent = new PdfDocumentRenderer(Fixtures.A4()))
            {
                double expectedScale = doc.Zoom * PageViewModel.PtToPx * doc.DpiFactor * doc.SupersampleFactor;
                var expected = independent.RenderPage(0, expectedScale);
                Assert.Equal(expected.WidthPx, bmpAfter.PixelWidth);
                Assert.Equal(expected.HeightPx, bmpAfter.PixelHeight);
            }

            // fronteira central: bitmap mais denso, ZERO mudança no layout lógico.
            Assert.Equal(logicalWidthBefore, doc.Pages[0].DisplayWidth, 6);
            Assert.Equal(logicalHeightBefore, doc.Pages[0].DisplayHeight, 6);
        }
        finally
        {
            window?.Close();
            doc?.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Task 2 (Plano 13; migrado pra ConfiguracoesViewModel na Task 2 do Plano 17) — STA ponta a ponta
    /// pela pilha REAL de UI (não injeção direta em `DocumentViewModel.SupersampleFactor` como o teste
    /// acima, herdado da Task 1): abre uma página REALIZADA numa `PdfViewerControl`, liga "Nitidez
    /// extra" através de um `ConfiguracoesViewModel` de verdade (mesmo `applySupersampleFactor` que
    /// `MainViewModel.Configuracoes` injeta em produção), e prova que o BITMAP JÁ NA TELA é
    /// re-renderizado mais denso — sem isto, o toggle só "faria efeito" no próximo evento que já
    /// disparasse render por outro motivo (zoom, DPI), mesma classe de bug real que
    /// `DocumentViewModel.OnSupersampleFactorChanged` (Task 1) foi criado para evitar.
    [Fact]
    public void ConfiguracoesViewModel_ToggleNitidezExtra_RerendersRealizedPageAtFactorTwo()
    {
        RunSta(RunToggleRerenderScenario, TimeSpan.FromSeconds(30));
    }

    private static void RunToggleRerenderScenario()
    {
        DocumentViewModel? doc = null;
        PdfViewerControl? control = null;
        Window? window = null;
        try
        {
            var config = TempConfig(); // NitidezExtra=false (default) -- documento abre no fator de hoje
            doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")), config: config);
            control = new PdfViewerControl { DataContext = doc };
            window = new Window { Width = 1000, Height = 800, Content = control };
            window.Show();

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            var bmpBefore = Assert.IsAssignableFrom<BitmapSource>(doc.Pages[0].ImageSource);
            Assert.Equal(96, bmpBefore.DpiX, 3); // OFF -> comportamento de hoje

            // mesmo callback que MainViewModel.Configuracoes() monta em produção: aplica o fator a TODO
            // DocumentViewModel aberto -- aqui, só este 1.
            var vm = new ConfiguracoesViewModel(
                confirmCloseAllDocuments: () => true,
                startInstaller: _ => { },
                shutdown: () => { },
                config: config,
                applySupersampleFactor: factor => doc!.SupersampleFactor = factor);

            vm.NitidezExtra = true; // liga o toggle -- dispara OnNitidezExtraChanged -> callback -> RefreshDpi

            Pump(() => doc.Pages[0].ImageSource is BitmapSource b && b.DpiX > 96, TimeSpan.FromSeconds(20));

            var bmpAfter = Assert.IsAssignableFrom<BitmapSource>(doc.Pages[0].ImageSource);
            Assert.Equal(192, bmpAfter.DpiX, 3); // 96 * 2.0 (fator de produção, NÃO 1.5)
            Assert.Equal(192, bmpAfter.DpiY, 3);
            Assert.True(config.NitidezExtra); // persistido de verdade, não só em memória

            // desligar de novo -> volta ao fator 1.0, mesmo bitmap de hoje.
            vm.NitidezExtra = false;
            Pump(() => doc.Pages[0].ImageSource is BitmapSource b && b.DpiX < 192, TimeSpan.FromSeconds(20));
            var bmpOff = Assert.IsAssignableFrom<BitmapSource>(doc.Pages[0].ImageSource);
            Assert.Equal(96, bmpOff.DpiX, 3);
            Assert.False(config.NitidezExtra);
        }
        finally
        {
            window?.Close();
            doc?.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// Oráculo de nitidez (Item 4 do brief) — NÃO mede luma médio do crop inteiro (dominado por fundo
    /// branco, erro do plano anterior citado no brief); mede só a REGIÃO DE GLIFOS, delimitada pela
    /// própria tinta do render 1.0x (bbox de qualquer pixel não-branco), e conta pixels ESCUROS
    /// (luma &lt; 128) + a escuridão média deles nos DOIS caminhos:
    ///   (a) render direto a 1.0x (hoje);
    ///   (b) render a 1.5x reduzido pro MESMO grid de 1.0x via o MESMO pipeline WPF de produção
    ///       (`RenderOptions.BitmapScalingMode.HighQuality`/Fant — `PdfViewerControl.xaml` usa
    ///       exatamente este modo no `Image` da página). Fixture: `fixture-nitidez-8-12.pdf`
    ///       (sintética, 5 parágrafos 12/11/10/9/8pt — a MESMA fixture da investigação anterior,
    ///       Plano 10 Task 2, texto real de contrato).
    ///
    /// RESULTADO MEDIDO (ver task-1-report.md pros números completos, 2 fixtures x 2 escalas x 2
    /// filtros de downscale): supersampling+downscale NÃO produz mais pixels escuros nem escuridão
    /// média maior nesta fixture — os números ficam ligeiramente MENORES (ex.: 8887→8575 px escuros,
    /// 174,48→173,55 de escuridão média a 1.5x). Isso CONFIRMA, com um oráculo diferente (contagem/
    /// escuridão de pixel, não Laplaciano/Sobel), o achado independente da investigação anterior
    /// (Plano 10 Task 2, `PdfDocumentRenderer.cs` linhas ~120-157): reamostrar de volta pro grid final
    /// com um filtro que preserva área (Fant/bicúbico) reconverge pra ~a MESMA cobertura de tinta por
    /// pixel final, independente da resolução intermediária — matematicamente esperado, não um bug
    /// desta implementação. Por isso o teste abaixo NÃO afirma "supersampling é mais nítido" (a
    /// evidência não sustenta essa afirmação nesta fixture) — ele prova que o ORÁCULO é SENSÍVEL (os
    /// dois caminhos produzem números DIFERENTES, não um metric degenerado que sempre dá o mesmo
    /// valor) e imprime os números reais pro report. Ver "Concern" no relatório desta task.
    [Fact]
    public void SupersampledThenDownscaled_ProducesMeasurablyDifferentGlyphCoverage()
    {
        RunSta(RunSharpnessOracle, TimeSpan.FromSeconds(30));
    }

    private static void RunSharpnessOracle()
    {
        using var renderer = new PdfDocumentRenderer(File.ReadAllBytes(Path.Combine(Fixtures.Root, "fixture-nitidez-8-12.pdf")));

        var direct = renderer.RenderPage(0, 1.0);
        var oversampled = renderer.RenderPage(0, 1.5);

        var bmpDirect = ToFrozenBitmap(direct, 96, 96);
        var bmpOversampled = ToFrozenBitmap(oversampled, 144, 144);

        // reduz o 1.5x pro MESMO grid de pixels do 1.0x, pelo MESMO pipeline WPF (Fant/HighQuality) que
        // `PdfViewerControl.xaml` usa pra exibir na tela (RenderOptions.BitmapScalingMode="HighQuality").
        var bmpDownscaled = DownscaleHighQuality(bmpOversampled, direct.WidthPx, direct.HeightPx);

        // região de glifos: bbox de qualquer pixel não-branco (luma < 250) no render direto 1.0x —
        // nenhuma coordenada chutada, a própria tinta define a região a medir.
        var region = InkBoundingBox(bmpDirect, threshold: 250);
        Assert.True(region.width > 0 && region.height > 0, "nenhuma tinta encontrada no render 1.0x — fixture sem texto?");
        // 4px de folga (arredondamento do downscale não pode cortar borda do glifo fora da janela).
        region = (Math.Max(0, region.minX - 4), Math.Max(0, region.minY - 4), region.width + 8, region.height + 8);

        var (darkDirect, meanDarknessDirect) = CountDarkPixels(bmpDirect, region, darkThreshold: 128);
        var (darkDownscaled, meanDarknessDownscaled) = CountDarkPixels(bmpDownscaled, region, darkThreshold: 128);

        // impresso pro report (task-1-report.md exige os dois números).
        Console.WriteLine($"[oráculo de nitidez] 1.0x: {darkDirect} px escuros, escuridão média {meanDarknessDirect:F2}");
        Console.WriteLine($"[oráculo de nitidez] 1.5x->1.0x: {darkDownscaled} px escuros, escuridão média {meanDarknessDownscaled:F2}");

        // Assertivo HONESTO (ver doc XML acima): a evidência medida não sustenta "supersampling é mais
        // nítido" nesta fixture — o que este teste TRAVA é que o oráculo é sensível o bastante pra
        // detectar QUALQUER diferença real entre os dois caminhos (não é uma métrica degenerada que
        // sempre devolve o mesmo número) — prova de que, SE um filtro de downscale melhor (ex.: um
        // gama não-linear/stem-darkening) for adotado numa task futura, este mesmo oráculo vai
        // detectar o ganho.
        Assert.True(darkDirect != darkDownscaled || Math.Abs(meanDarknessDirect - meanDarknessDownscaled) > 0.01,
            "oráculo degenerado: os dois caminhos produziram números idênticos — a métrica não está " +
            "medindo nada (bug na captura de pixels, não na hipótese de nitidez)");
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static DocumentViewModel Doc(AppConfig? config = null) =>
        new(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")), config: config);

    // Task 2 (Plano 13): diretório de config TEMPORÁRIO -- mesmo padrão de AppConfigTests/
    // SobreViewModelTests, nunca toca %AppData%\mPDF real durante a suíte.
    private static AppConfig TempConfig() =>
        new(Path.Combine(Path.GetTempPath(), $"mpdf-supersample-cfg-{Guid.NewGuid():N}"));

    private static void RunSta(Action scenario, TimeSpan timeout)
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
        Assert.True(thread.Join(timeout), "thread STA não terminou a tempo");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void Pump(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(50);
        }
    }

    private static BitmapSource ToFrozenBitmap(RenderedPage page, double dpiX, double dpiY)
    {
        var bmp = BitmapSource.Create(page.WidthPx, page.HeightPx, dpiX, dpiY, PixelFormats.Bgra32, null, page.Bgra, page.WidthPx * 4);
        bmp.Freeze();
        return bmp;
    }

    // reduz `source` pra `targetW`x`targetH` usando o MESMO modo de escala do Image de produção
    // (RenderOptions.BitmapScalingMode="HighQuality", filtro Fant) — desenha num DrawingVisual do
    // tamanho alvo e renderiza num RenderTargetBitmap, exatamente como o WPF faria ao apresentar o
    // bitmap denso dentro do Border de DisplayWidth/DisplayHeight menor.
    private static BitmapSource DownscaleHighQuality(BitmapSource source, int targetW, int targetH)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, targetW, targetH));

        var rtb = new RenderTargetBitmap(targetW, targetH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var converted = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static (int minX, int minY, int width, int height) InkBoundingBox(BitmapSource bmp, byte threshold)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var buffer = new byte[w * h * 4];
        bmp.CopyPixels(buffer, w * 4, 0);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (buffer[i] < threshold || buffer[i + 1] < threshold || buffer[i + 2] < threshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        return maxX < minX ? (0, 0, 0, 0) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // conta pixels ESCUROS (luma < darkThreshold) e sua escuridão média, restrito à REGIÃO dada — a
    // fronteira que corrige o erro do oráculo anterior (luma médio do crop INTEIRO, dominado por
    // fundo branco): aqui só a bbox de tinta do render 1.0x entra na conta, nos dois caminhos.
    private static (int darkCount, double meanDarkness) CountDarkPixels(
        BitmapSource bmp, (int minX, int minY, int width, int height) region, byte darkThreshold)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var buffer = new byte[w * h * 4];
        bmp.CopyPixels(buffer, w * 4, 0);

        int darkCount = 0;
        double darknessSum = 0;
        int x0 = Math.Max(0, region.minX), y0 = Math.Max(0, region.minY);
        int x1 = Math.Min(w, region.minX + region.width), y1 = Math.Min(h, region.minY + region.height);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                double luma = 0.114 * buffer[i] + 0.587 * buffer[i + 1] + 0.299 * buffer[i + 2]; // BGR
                if (luma < darkThreshold)
                {
                    darkCount++;
                    darknessSum += 255.0 - luma; // "escuridão" = distância de branco puro
                }
            }
        return (darkCount, darkCount == 0 ? 0.0 : darknessSum / darkCount);
    }
}
