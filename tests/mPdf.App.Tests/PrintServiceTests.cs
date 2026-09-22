using System.IO;
using System.Printing;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using mPdf.App.Services;
using mPdf.Documents;

namespace mPdf.App.Tests;

/// Task 8 (Impressão): PrintService.BuildPaginator é testável SEM impressora — PageCount correto
/// (documento inteiro e range parcial), GetPage devolve um Visual não-nulo com conteúdo real
/// (estrutural: Canvas contendo 1 Image), e a matemática de posicionamento (ComputePlacement) é pura
/// e testável fora de qualquer paginator.
public class PrintServiceTests
{
    [Fact]
    public void BuildPaginator_FullDocument_PageCountEqualsTotal()
    {
        using var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        var paginator = (PdfPrintPaginator)PrintService.BuildPaginator(session, 300, null);
        try
        {
            Assert.Equal(30, paginator.PageCount);
            Assert.True(paginator.IsPageCountValid);
        }
        finally { paginator.Dispose(); }
    }

    [Fact]
    public void BuildPaginator_PartialRange_PageCountEqualsRangeSize()
    {
        using var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        // range 5..10 (1-based, contrato do PageRange do WPF) -> 6 páginas (5,6,7,8,9,10)
        var paginator = (PdfPrintPaginator)PrintService.BuildPaginator(session, 300, new PageRange(5, 10));
        try { Assert.Equal(6, paginator.PageCount); }
        finally { paginator.Dispose(); }
    }

    [Fact] // FIX (revisão pós-Task 8, C1): reescrito pra passar pelo caminho REAL de produção
    // (PreparePaginator, não BuildPaginator + PageSize setado à mão pelo teste) — a versão anterior
    // deste teste mascarava o bug de PageSize nunca setado em Print(), porque ela mesma setava.
    public void GetPage_ViaPreparePaginator_ReturnsNonDegenerateVisualWithRenderedContent()
    {
        // DocumentPage/Visual (Canvas+Image) construídos aqui podem exigir STA (aviso do brief) —
        // mesmo padrão de thread STA manual usado por ViewerIntegrationTests (exemplar).
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunGetPageScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(20));
        Assert.True(joined, "thread STA não terminou dentro de 20s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunGetPageScenario()
    {
        using var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        // PreparePaginator é EXATAMENTE o que Print() chama (BuildPaginator + PageSize) — nenhum
        // passo extra do teste "completando" a fiação que o código de produção deveria fazer sozinho.
        // Papel A4 em DIPs (1/96"), mesma unidade que PrintDialog.PrintableAreaWidth/Height devolve.
        var paginator = PrintService.PreparePaginator(session, 300, null, new Size(794, 1123));
        try
        {
            var page = paginator.GetPage(0);

            Assert.NotNull(page.Visual);
            Assert.Equal(794, page.Size.Width, 0.01);
            Assert.Equal(1123, page.Size.Height, 0.01);

            var canvas = Assert.IsType<Canvas>(page.Visual);
            var image = Assert.Single(canvas.Children.OfType<Image>());
            Assert.NotNull(image.Source);

            // NÃO-DEGENERADA: prova que o placement real (via PreparePaginator, papel de verdade)
            // produziu uma imagem com área > 0 — é exatamente o que C1 quebrava (paperW/H=0 ->
            // ComputePlacement cai na guarda -> Width=Height=0 -> "página" invisível na impressão real).
            Assert.True(image.Width > 0 && image.Height > 0, "placement degenerado: PageSize não chegou até GetPage");
            // proporção A4 preservada (nunca esticada pro papel de teste): CONTIDA, nunca maior que o papel.
            Assert.True(image.Width <= 794 + 0.01 && image.Height <= 1123 + 0.01);
            // proporcional ao aspect ratio real da página A4 renderizada (595x842pt -> ~0.7067)
            double expectedAspect = 595.0 / 842.0;
            Assert.Equal(expectedAspect, image.Width / image.Height, 0.01);
        }
        finally { paginator.Dispose(); }
    }

    [Fact] // FIX (revisão pós-Task 8, C1) — CONTROLE NEGATIVO: prova (não só documenta) POR QUE a
    // fiação de PreparePaginator importa. Um paginator montado só via BuildPaginator (sem passar por
    // PreparePaginator, ou seja, sem ninguém setar PageSize) precisa produzir um placement DEGENERADO
    // — se este teste um dia passar a falhar (imagem com área > 0 sem PageSize setado), a guarda de
    // ComputePlacement parou de disparar e C1 pode ter voltado silenciosamente.
    public void GetPage_WithoutPreparePaginator_PageSizeUnset_ProducesDegenerateVisual()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunDegenerateScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(20));
        Assert.True(joined, "thread STA não terminou dentro de 20s (BLOCKED: possível deadlock/hang do WPF)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunDegenerateScenario()
    {
        using var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        // BuildPaginator "cru" — o caminho que Print() tinha ANTES do fix C1, sem PageSize nenhum.
        var paginator = (PdfPrintPaginator)PrintService.BuildPaginator(session, 300, null);
        try
        {
            var page = paginator.GetPage(0);

            var canvas = Assert.IsType<Canvas>(page.Visual);
            var image = Assert.Single(canvas.Children.OfType<Image>());
            // é exatamente o bug real: PageSize=(0,0) (nunca setado) -> ComputePlacement cai na
            // guarda `paperW <= 0` -> Rect(0,0,0,0) -> imagem de área ZERO, invisível na impressão.
            Assert.Equal(0, image.Width);
            Assert.Equal(0, image.Height);
        }
        finally { paginator.Dispose(); }
    }

    [Fact] // C-1 (revisão final): PROVA da premissa do bug — PageResolution é uma CLASSE anulável,
    // não uma struct. Um PrintTicket "cru" (nenhuma propriedade setada, o estado de um driver que não
    // relata resolução) tem PageResolution == null; ANTES do fix, `dlg.PrintTicket?.PageResolution.X`
    // (sem o `?.` extra) lançaria NullReferenceException nesse caminho.
    public void ResolveDpi_NullPageResolution_ReturnsFallback300WithoutThrowing()
    {
        var ticket = new PrintTicket();
        Assert.Null(ticket.PageResolution); // sanity: confirma a premissa (classe anulável) antes do fix
        Assert.Equal(300, PrintService.ResolveDpi(ticket));
    }

    [Fact] // ShowDialog pode devolver um PrintTicket nulo (driver sem suporte) — mesmo fallback, sem lançar.
    public void ResolveDpi_NullTicket_ReturnsFallback300()
    {
        Assert.Equal(300, PrintService.ResolveDpi(null));
    }

    [Fact] // I-2: teto de 600dpi — driver relatando um valor exagerado (1200dpi -> ~560MB/página A4)
    // não deve estourar a memória do processo de impressão.
    public void ResolveDpi_AboveCap_ClampsTo600()
    {
        var ticket = new PrintTicket { PageResolution = new PageResolution(1200, 1200) };
        Assert.Equal(600, PrintService.ResolveDpi(ticket));
    }

    [Fact] // valor normal (dentro do teto) passa direto, sem cair no fallback nem no teto.
    public void ResolveDpi_NormalValue_PassesThroughUnclamped()
    {
        var ticket = new PrintTicket { PageResolution = new PageResolution(300, 300) };
        Assert.Equal(300, PrintService.ResolveDpi(ticket));
    }

    [Fact] // F-1 (Task 1, Plano 3a): PageResolution NÃO-nula (driver relatou ALGO) mas só com
    // resolução QUALITATIVA (X/Y numéricos nulos) — comum em drivers que só sabem dizer
    // "Default"/"High" sem um DPI numérico. `ticket?.PageResolution?.X is int x` (ResolveDpi) já
    // cobre isso pela cadeia de `?.`/`is` (X nulo -> padrão `int x` não casa -> cai no fallback), mas
    // só uma prova ao vivo confirma que `new PageResolution(PageQualitativeResolution.Default)`
    // produz exatamente esse estado (não lança, PageResolution não-nula, X nulo) — verificado por
    // sonda compilada contra a API real do WPF em net10.0-windows antes deste teste.
    public void ResolveDpi_NonNullPageResolutionWithNullX_ReturnsFallback300WithoutThrowing()
    {
        var ticket = new PrintTicket { PageResolution = new PageResolution(PageQualitativeResolution.Default) };
        Assert.NotNull(ticket.PageResolution);   // sanity: PageResolution é NÃO-nula aqui
        Assert.Null(ticket.PageResolution.X);    // sanity: mas X (o que ResolveDpi lê) é nulo
        Assert.Equal(300, PrintService.ResolveDpi(ticket));
    }

    [Fact]
    public void ComputePlacement_CentersContainsAndNeverStretches()
    {
        // portrait-on-portrait: página 100x200 (retrato) num papel 200x300 (retrato, aspecto diferente)
        // -> limitada pela ALTURA (scaleY=1.5 < scaleX=2.0), sobra espaço HORIZONTAL, centrado.
        var portrait = PrintService.ComputePlacement(100, 200, 200, 300);
        Assert.Equal(150, portrait.Width, 0.01);   // 100 * 1.5
        Assert.Equal(300, portrait.Height, 0.01);  // 200 * 1.5 -> preenche a altura inteira
        Assert.Equal(25, portrait.X, 0.01);        // (200-150)/2 -> centrado horizontalmente
        Assert.Equal(0, portrait.Y, 0.01);         // preenche a altura -> sem sobra vertical
        Assert.True(portrait.Width <= 200 + 0.01 && portrait.Height <= 300 + 0.01, "contida no papel");

        // landscape-on-portrait: página 200x100 (paisagem) no MESMO papel 200x300 (retrato)
        // -> limitada pela LARGURA (scaleX=1.0 < scaleY=3.0), sobra espaço VERTICAL, centrado.
        var landscape = PrintService.ComputePlacement(200, 100, 200, 300);
        Assert.Equal(200, landscape.Width, 0.01);  // preenche a largura inteira
        Assert.Equal(100, landscape.Height, 0.01); // 100 * 1.0
        Assert.Equal(0, landscape.X, 0.01);        // preenche a largura -> sem sobra horizontal
        Assert.Equal(100, landscape.Y, 0.01);      // (300-100)/2 -> centrado verticalmente
        Assert.True(landscape.Width <= 200 + 0.01 && landscape.Height <= 300 + 0.01, "contida no papel");
    }

    [Fact] // caso de DOWNSCALE: página MAIOR que o papel (comum na impressão real — página A3/A4 num
    // papel menor, ou um bitmap em DPI alto cujas dimensões em pontos excedem o papel escolhido) —
    // os dois testes acima só cobrem o caso de página menor/igual (scale >= 1); aqui o fator de
    // escala precisa ser < 1 (reduzir, nunca ampliar além do necessário) e ainda assim CONTIDA e CENTRADA.
    public void ComputePlacement_PageLargerThanPaper_ScalesDownContainedAndCentered()
    {
        // página 2000x1000 (bem maior que o papel 200x300 nas duas dimensões)
        // -> limitada pela ALTURA (scaleY=300/1000=0.3 < scaleX=200/2000=0.1)... na verdade scaleX é
        // MENOR aqui (0.1 < 0.3), então a largura é quem limita.
        var placement = PrintService.ComputePlacement(2000, 1000, 200, 300);
        double expectedScale = 200.0 / 2000.0; // 0.1 -- menor dos dois fatores
        double expectedW = 2000 * expectedScale, expectedH = 1000 * expectedScale; // 200, 100

        Assert.True(expectedScale < 1, "sanity: este cenário só testa algo novo se for downscale de verdade");
        Assert.Equal(expectedW, placement.Width, 0.01);
        Assert.Equal(expectedH, placement.Height, 0.01);
        Assert.Equal(0, placement.X, 0.01);                          // preenche a largura -> sem sobra horizontal
        Assert.Equal((300 - expectedH) / 2.0, placement.Y, 0.01);    // sobra vertical centrada
        Assert.True(placement.Width <= 200 + 0.01 && placement.Height <= 300 + 0.01, "contida no papel (nunca maior)");
    }
}
