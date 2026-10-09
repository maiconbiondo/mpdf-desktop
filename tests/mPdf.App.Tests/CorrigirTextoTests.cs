using System.IO;
using System.Linq;
using System.Windows;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.App.Tests;

public class CorrigirTextoTests
{
    private static RenderedPage Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return new RenderedPage(w, h, px);
    }

    [Fact] // fundo uniforme amarelado -> a mediana da moldura devolve exatamente essa cor
    public void CorDeFundo_UniformBackground_ReturnsThatColor()
    {
        var page = Solid(200, 200, b: 0xD0, g: 0xF0, r: 0xF8);
        uint c = CorDeFundo.Mediana(page, new PdfQuad(50, 50, 100, 70), pageHeightPt: 200, scale: 1.0);
        Assert.Equal(0xFFF8F0D0u, c);
    }

    [Fact] // glifos pretos DENTRO da área não contaminam a cor (só a moldura em volta é amostrada)
    public void CorDeFundo_IgnoresInkInsideArea()
    {
        var page = Solid(200, 200, 0xFF, 0xFF, 0xFF);
        for (int y = 135; y < 145; y++) for (int x = 55; x < 95; x++) { int i = (y * 200 + x) * 4; page.Bgra[i] = page.Bgra[i + 1] = page.Bgra[i + 2] = 0; }
        Assert.Equal(0xFFFFFFFFu, CorDeFundo.Mediana(page, new PdfQuad(50, 50, 100, 70), 200, 1.0));
    }

    [Fact] // texto novo passa da borda direita da página -> aviso de borda
    public void AvisoSobreposicao_PastPageEdge_Warns()
    {
        var tp = new TextPage(0, Array.Empty<PdfCharacter>());
        Assert.Equal(AvisoSobreposicao.PassaDaBorda, AvisoSobreposicao.Calcular(tp, new PdfQuad(500, 100, 550, 112), novoRightPt: 620, larguraPaginaPt: 595));
    }

    [Fact] // texto novo invade um caractere à direita na mesma linha -> aviso de sobreposição
    public void AvisoSobreposicao_OverlapsCharToTheRight_Warns()
    {
        var tp = new TextPage(0, new[] { new PdfCharacter('X', 160, 101, 166, 111) });
        Assert.Equal(AvisoSobreposicao.CobreTexto, AvisoSobreposicao.Calcular(tp, new PdfQuad(100, 100, 150, 112), 170, 595));
    }

    [Fact] // espaço (caixa de altura zero) e caracteres de outras linhas não contam
    public void AvisoSobreposicao_SpacesAndOtherLines_NoWarning()
    {
        var tp = new TextPage(0, new[] { new PdfCharacter(' ', 152, 105, 156, 105), new PdfCharacter('Y', 160, 50, 166, 60) });
        Assert.Null(AvisoSobreposicao.Calcular(tp, new PdfQuad(100, 100, 150, 112), 170, 595));
    }

    [Fact] // recorte copia exatamente a janela pedida (clampada à página)
    public void Recorte_CopiesRequestedWindow_Clamped()
    {
        var page = Solid(100, 50, 1, 2, 3);
        var r = Recorte.De(page, xPx: 90, yPx: 40, larguraPx: 30, alturaPx: 30);
        Assert.Equal((10, 10), (r.WidthPx, r.HeightPx));
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, r.Bgra[..4]);
    }

    // ---- Comando Corrigir texto (DocumentViewModel) — setup no exemplar BuildForMarkup ------------

    private static readonly TextStyleProbe ProbeSerif11Preto =
        new("Texto antigo", TextFontFamily.Serif, false, false, 11, 0xFF000000u, 700, 72, 200, false);

    private static (DocumentViewModel doc, FakePdfEditor fake, FakeCorrigirTextoDialog dialog, List<string> errors) Build(string? pdfPath = null)
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-corrigir-cfg-{Guid.NewGuid():N}");
        var fake = new FakePdfEditor { ProbeResult = ProbeSerif11Preto };
        var dialog = new FakeCorrigirTextoDialog();
        var errors = new List<string>();
        var doc = new DocumentViewModel(
            DocumentSession.Open(pdfPath ?? Path.Combine(Fixtures.Root, "fixture-a4.pdf")),
            editor: fake,
            config: new AppConfig(configDir),
            notifyError: errors.Add,
            corrigirTextoDialog: dialog);
        return (doc, fake, dialog, errors);
    }

    // Mesmo gesto do exemplar `SelectSomeText` (DocumentViewModelTests): uma faixa fina -> 1 linha.
    private static void SelectOneLine(PageViewModel page)
    {
        page.BeginSelection(new Point(10, 10));
        page.UpdateSelection(new Point(300, 20));
    }

    // Todos os fixtures têm UMA linha de texto na página 0 (medido: arrasto da página inteira em
    // fixture-a4/-30p/-sumario -> 1 retângulo). PDF de 2 linhas montado em runtime com o editor REAL:
    // camada de texto (OCR) de 2 caixas em alturas diferentes sobre fixture-sem-texto.
    private static string TwoLinePdfPath()
    {
        var layer = new OcrTextLayer(0, 800, 1131, new[]
        {
            new OcrTextBox("PRIMEIRA", LeftPx: 100, TopPx: 200, WidthPx: 300, HeightPx: 40),
            new OcrTextBox("SEGUNDA", LeftPx: 100, TopPx: 400, WidthPx: 300, HeightPx: 40),
        });
        byte[] pdf = PdfEditorFactory.Create().ApplyOcrTextLayer(Fixtures.NoText(), new[] { layer });
        var path = Path.Combine(Path.GetTempPath(), $"mpdf-corrigir-2linhas-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    // Arrasto da página inteira -> as 2 linhas de TwoLinePdfPath.
    private static void SelectSeveralLines(PageViewModel page)
    {
        page.BeginSelection(new Point(10, 10));
        page.UpdateSelection(new Point(780, 1100));
    }

    [Fact]
    public void CorrigirTexto_NoSelection_CannotExecute()
    {
        var (doc, _, _, _) = Build();
        using var d = doc;
        Assert.False(d.CorrigirTextoCommand.CanExecute(null));
        SelectOneLine(d.Pages[0]);
        Assert.True(d.CorrigirTextoCommand.CanExecute(null)); // controle positivo: com seleção habilita
    }

    [Fact]
    public void CorrigirTexto_SignedDocument_CannotExecute()
    {
        var (doc, _, _, _) = Build();
        using var d = doc;
        SelectOneLine(d.Pages[0]);
        Assert.True(d.CorrigirTextoCommand.CanExecute(null)); // sanity antes

        d.IsSignedDocument = true;

        Assert.False(d.CorrigirTextoCommand.CanExecute(null));
    }

    [Fact]
    public async Task CorrigirTexto_TwoLineSelection_WarnsAndDoesNotOpenDialog()
    {
        string path = TwoLinePdfPath();
        try
        {
            var (doc, fake, dialog, errors) = Build(path);
            using var d = doc;
            SelectSeveralLines(d.Pages[0]);
            Assert.True(d.Pages[0].SelectionPointRects.Count >= 2, $"linhas={d.Pages[0].SelectionPointRects.Count} texto='{d.SelectedText}'"); // sanity: o gesto gerou mais de 1 linha

            await d.CorrigirTextoCommand.ExecuteAsync(null);

            Assert.Equal("Selecione um trecho de uma linha só.", Assert.Single(errors));
            Assert.Equal(0, dialog.Chamadas);
            Assert.Equal(0, fake.ApplyTextCorrectionCallCount);
            Assert.False(d.Session.IsEditInFlight);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CorrigirTexto_NoTextFoundInArea_WarnsAndDoesNotOpenDialog()
    {
        var (doc, fake, dialog, errors) = Build();
        using var d = doc;
        fake.ProbeResult = null;
        SelectOneLine(d.Pages[0]);

        await d.CorrigirTextoCommand.ExecuteAsync(null);

        Assert.Equal("Não foi encontrado texto nesse trecho.", Assert.Single(errors));
        Assert.Equal(0, dialog.Chamadas);
        Assert.False(d.Session.IsEditInFlight);
    }

    [Fact]
    public async Task CorrigirTexto_DialogPrefilledWithProbeStyle()
    {
        var (doc, _, dialog, errors) = Build();
        using var d = doc;
        SelectOneLine(d.Pages[0]);
        Assert.Single(d.Pages[0].SelectionPointRects); // sanity: 1 linha
        string? selecionado = d.SelectedText;
        Assert.False(string.IsNullOrEmpty(selecionado));

        await d.CorrigirTextoCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal(1, dialog.Chamadas);
        var p = dialog.UltimoPedido!;
        Assert.Equal(TextFontFamily.Serif, p.Familia);
        Assert.Equal(11, p.TamanhoPt);
        Assert.False(p.Negrito);
        Assert.False(p.Italico);
        Assert.Equal(0xFF000000u, p.CorArgb);
        Assert.Equal(selecionado, p.TextoAtual);
        Assert.NotNull(dialog.UltimaPrevia);
    }

    [Fact]
    public async Task CorrigirTexto_Confirmed_AppliesCorrectionThroughEditFunnel()
    {
        var (doc, fake, dialog, errors) = Build();
        using var d = doc;
        var page = d.Pages[0];
        SelectOneLine(page);
        var r = Assert.Single(page.SelectionPointRects);
        dialog.Resultado = new CorrigirTextoResultado("NOVA", TextFontFamily.Serif, false, false, 11, 0xFF000000u);
        Assert.False(d.CanUndo);

        await d.CorrigirTextoCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal(1, fake.ApplyTextCorrectionCallCount);
        var c = fake.LastCorrection!;
        Assert.Equal("NOVA", c.NewText);
        Assert.Equal(0, c.PageIndex);
        Assert.Equal(r.X, c.AreaPt.LeftPt, 0.01);
        Assert.Equal(r.Y, c.AreaPt.BottomPt, 0.01);
        Assert.Equal(r.X + r.Width, c.AreaPt.RightPt, 0.01);
        Assert.Equal(r.Y + r.Height, c.AreaPt.TopPt, 0.01);
        Assert.Equal(ProbeSerif11Preto.BaselineYPt, c.BaselineYPt);
        Assert.Equal(ProbeSerif11Preto.LeftXPt, c.LeftXPt);
        Assert.Equal(0xFF000000u, c.BackgroundArgb & 0xFF000000u); // fundo sempre opaco
        Assert.Null(c.MinOverlapRatio); // digital: pdfSweep com o padrão (qualquer interseção remove)
        Assert.Null(c.AreasPt);         // digital: a área única de sempre (rodada 5)
        Assert.True(d.Session.CanUndo);
        Assert.Equal(30, d.Pages.Count); // snapshot trocou pro marcador do fake
        Assert.False(d.HasActiveSelection);
        Assert.False(d.Session.IsEditInFlight);
    }

    [Fact] // revisão final C1: texto INVISÍVEL (OCR) -> a área cresce até a tinta do render (contém a seleção
    // + folga) e o tamanho vem da tinta, não do probe. (Digital: área = seleção — teste acima.)
    public async Task CorrigirTexto_InvisibleText_AreaGrowsToInkAndSizeFromInk()
    {
        var (doc, fake, dialog, errors) = Build();
        using var d = doc;
        var page = d.Pages[0];
        SelectOneLine(page);
        var r = Assert.Single(page.SelectionPointRects);
        // Caixa OCR = extensão da tinta da linha (base no pé, tamanho = altura), tamanho do probe errado.
        fake.ProbeResult = ProbeSerif11Preto with { Invisible = true, BaselineYPt = r.Y, SizePt = r.Height };
        dialog.Resultado = new CorrigirTextoResultado("NOVA", TextFontFamily.Serif, false, false, 11, 0xFF000000u);

        await d.CorrigirTextoCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.NotEqual(r.Height, dialog.UltimoPedido!.TamanhoPt); // tamanho veio da tinta (altura/0,70)
        Assert.InRange(dialog.UltimoPedido.TamanhoPt, 0.8 * r.Height, 1.6 * r.Height);
        var c = fake.LastCorrection!;
        Assert.True(c.AreaPt.LeftPt < r.X - 0.25 && c.AreaPt.RightPt > r.X + r.Width + 0.25, $"área {c.AreaPt} vs seleção {r}");
        Assert.InRange(c.AreaPt.BottomPt, r.Y - 1.5, r.Y);                       // presa à caixa OCR (+ folga)
        Assert.InRange(c.AreaPt.TopPt, r.Y + r.Height, r.Y + r.Height + 1.5);
        Assert.Equal(PlanejarCorrecao.SobreposicaoMinimaOcr, c.MinOverlapRatio);
    }

    [Fact] // rodada 2: camada OCR -> a janela recebe o TOKEN inteiro (com a pontuação) e a área parte dele
    public async Task CorrigirTexto_InvisibleText_DialogGetsWholeOcrToken()
    {
        var (doc, fake, dialog, errors) = Build();
        using var d = doc;
        var page = d.Pages[0];
        SelectOneLine(page);
        var r = Assert.Single(page.SelectionPointRects);
        var token = new PdfQuad(r.X, r.Y, r.X + r.Width + 3, r.Y + r.Height);
        double proximo = r.X + r.Width / 2; // rodada 5: início do próximo token OCR da linha
        fake.ProbeResult = ProbeSerif11Preto with
        {
            Invisible = true, BaselineYPt = r.Y, SizePt = r.Height, TokenText = "NOVEMBRO,", TokenAreaPt = token,
            NextTokenLeftPt = proximo,
        };
        dialog.Resultado = new CorrigirTextoResultado("DEZEMBRO,", TextFontFamily.SansSerif, false, false, 11, 0xFF000000u);

        await d.CorrigirTextoCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal("NOVEMBRO,", dialog.UltimoPedido!.TextoAtual);
        var c = fake.LastCorrection!;
        // Rodada 5 (estrutural): da caixa OCR do token (- 0,5 pt) até o início do próximo token (- 0,5 pt).
        Assert.Equal(token.LeftPt - 0.5, c.AreaPt.LeftPt, 1);
        Assert.Equal(proximo - 0.5, c.AreaPt.RightPt, 0);
    }

    [Fact] // rodada 4 (minor): glifo cuja caixa do PDFium tem largura zero ("l", "-", "|" — Docnet arredonda
    // para inteiro) -> seleção de largura zero; antes caía em "Área de correção inválida". Editor REAL.
    public async Task CorrigirTexto_GlifoDeLarguraZero_AbreJanelaEAplica()
    {
        var dialog = new FakeCorrigirTextoDialog { Resultado = new CorrigirTextoResultado("X", TextFontFamily.Serif, false, false, 11, 0xFF000000u) };
        var errors = new List<string>();
        // Página digital com "l - |" em Times 9 pt (relatório tipo Crystal), escrita pelo próprio editor numa
        // área em branco de fixture-sem-texto.
        var ed = PdfEditorFactory.Create();
        byte[] pdf = ed.ApplyTextCorrection(Fixtures.NoText(), new TextCorrection(0, new PdfQuad(400, 100, 401, 101),
            "Total l - | fim", TextFontFamily.Serif, false, false, 9, 0xFF000000u, 700, 100, 0xFFFFFFFFu));
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-corrigir-l-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        var d = new DocumentViewModel(DocumentSession.Open(path),
            editor: PdfEditorFactory.Create(), config: new AppConfig(Path.Combine(Path.GetTempPath(), $"mpdf-corrigir-cfg-{Guid.NewGuid():N}")),
            notifyError: errors.Add, corrigirTextoDialog: dialog);
        try
        {
            var page = d.Pages[0];
            SelectOneLine(page); page.ClearSelection(); // garante o cache da TextPage
            var tp = page.TextPageCache!;
            int k = Enumerable.Range(0, tp.Characters.Count)
                .First(i => !char.IsWhiteSpace(tp.Characters[i].Char) && tp.Characters[i].RightPt - tp.Characters[i].LeftPt < 0.5
                            && tp.Characters[i].TopPt - tp.Characters[i].BottomPt >= 1);
            var c = tp.Characters[k];
            double scale = d.Zoom * PageViewModel.PtToPx;
            var ponto = new Point((c.LeftPt + c.RightPt) / 2 * scale, (page.HeightPt - (c.BottomPt + c.TopPt) / 2) * scale);
            page.BeginSelection(ponto);
            page.UpdateSelection(ponto);
            var r = Assert.Single(page.SelectionPointRects);
            Assert.True(r.Width < 1, $"sanity: seleção de largura {r.Width} ('{c.Char}')");

            await d.CorrigirTextoCommand.ExecuteAsync(null);

            Assert.Empty(errors);
            Assert.Equal(1, dialog.Chamadas);
            Assert.True(d.Session.CanUndo); // a correção foi aplicada
        }
        finally { d.Dispose(); File.Delete(path); }
    }

    [Fact] // revisão: a seleção é capturada ANTES do 1º await — mexer nela enquanto o probe roda não muda
    // o texto que a janela recebe (sem a captura, cairia no fallback `probe.Text`).
    public async Task CorrigirTexto_SelectionClearedDuringProbe_DialogGetsOriginalText()
    {
        var (doc, fake, dialog, errors) = Build();
        using var d = doc;
        SelectOneLine(d.Pages[0]);
        string original = d.SelectedText!;
        Assert.NotEqual(ProbeSerif11Preto.Text, original); // sanity: fallback seria distinguível
        fake.OnProbeTextStyle = () => d.ClearSelection();

        await d.CorrigirTextoCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.False(d.HasActiveSelection); // o gancho realmente rodou
        Assert.Equal(original, dialog.UltimoPedido!.TextoAtual);
    }

    [Fact]
    public async Task CorrigirTexto_Cancelled_DoesNothing()
    {
        var (doc, fake, dialog, errors) = Build();
        using var d = doc;
        SelectOneLine(d.Pages[0]);
        var antes = d.Session.Snapshot;
        dialog.Resultado = null;

        await d.CorrigirTextoCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.Chamadas);
        Assert.Empty(errors);
        Assert.Equal(0, fake.ApplyTextCorrectionCallCount);
        Assert.False(d.Session.CanUndo);
        Assert.Same(antes, d.Session.Snapshot);
        Assert.False(d.Session.IsEditInFlight);
    }

    [Fact]
    public async Task CorrigirTexto_EditorThrows_NotifiesError()
    {
        var (doc, fake, dialog, errors) = Build();
        using var d = doc;
        SelectOneLine(d.Pages[0]);
        var antes = d.Session.Snapshot;
        fake.ThrowOnApplyTextCorrection = new PdfEditingException("x");
        dialog.Resultado = new CorrigirTextoResultado("NOVA", TextFontFamily.Serif, false, false, 11, 0xFF000000u);

        await d.CorrigirTextoCommand.ExecuteAsync(null);

        Assert.Equal("x", Assert.Single(errors));
        Assert.False(d.Session.IsEditInFlight); // EndEdit chamado
        Assert.False(d.Session.CanUndo);
        Assert.Same(antes, d.Session.Snapshot);
    }

    // ---------- Task 4: menu de contexto da página ----------

    [Fact] // sem seleção: os 2 itens desabilitados; com seleção de 1 linha: os 2 habilitados; sem doc: nada
    public void MenuPagina_EnabledFollowsSelectionAndCommand()
    {
        Assert.Equal((false, false), mPdf.App.Views.PdfViewerControl.EstadoMenuPagina(null));
        var (doc, _, _, _) = Build();
        using var d = doc;
        Assert.Equal((false, false), mPdf.App.Views.PdfViewerControl.EstadoMenuPagina(d));
        SelectOneLine(d.Pages[0]);
        Assert.Equal((true, true), mPdf.App.Views.PdfViewerControl.EstadoMenuPagina(d));
    }

    // ---------- Task 4: janela (sondas STA, mesmo harness de SignDialogTests) ----------

    [Fact] // a janela abre pré-preenchida, ResultadoAtual reflete os campos, e a prévia é pedida
    public void CorrigirTextoDialog_Prefilled_ResultReflectsFields()
    {
        RunOnSta(() =>
        {
            int previas = 0;
            var d = new mPdf.App.Views.CorrigirTextoDialog(
                new CorrigirTextoPedido("RUA ANTIGA", TextFontFamily.Serif, false, false, 11, 0xFF000000),
                _ => { Interlocked.Increment(ref previas); return new CorrigirTextoPrevia(null, null); });
            try
            {
                var novo = (System.Windows.Controls.TextBox)d.FindName("TextoNovoBox")!;
                Assert.Equal("RUA ANTIGA", novo.Text);
                novo.Text = "RUA NOVA";
                Assert.Equal("RUA NOVA", d.ResultadoAtual().TextoNovo);
                Assert.Equal(TextFontFamily.Serif, d.ResultadoAtual().Familia);
                Assert.Equal(11, d.ResultadoAtual().TamanhoPt);
            }
            finally { d.Close(); }
        });
    }

    [Fact] // estilo detectado entra nos controles; tamanho aceita vírgula/ponto, faz clamp 4..144 e cai no do pedido se inválido
    public void CorrigirTextoDialog_StyleAndSizeParsing()
    {
        RunOnSta(() =>
        {
            var d = new mPdf.App.Views.CorrigirTextoDialog(
                new CorrigirTextoPedido("abc", TextFontFamily.Monospace, true, true, 10.5, 0xFF123456),
                _ => new CorrigirTextoPrevia(null, null));
            try
            {
                var r = d.ResultadoAtual();
                Assert.Equal(TextFontFamily.Monospace, r.Familia);
                Assert.True(r.Negrito); Assert.True(r.Italico);
                Assert.Equal(10.5, r.TamanhoPt);
                Assert.Equal(0xFF123456u, r.CorArgb); // cor detectada fora da paleta é preservada
                var tam = (System.Windows.Controls.TextBox)d.FindName("TamanhoBox")!;
                Assert.Equal("10,5", tam.Text);
                tam.Text = "12.5"; Assert.Equal(12.5, d.ResultadoAtual().TamanhoPt);
                tam.Text = "9,25"; Assert.Equal(9.25, d.ResultadoAtual().TamanhoPt);
                tam.Text = "500"; Assert.Equal(144, d.ResultadoAtual().TamanhoPt);
                tam.Text = "1"; Assert.Equal(4, d.ResultadoAtual().TamanhoPt);
                tam.Text = "xyz"; Assert.Equal(10.5, d.ResultadoAtual().TamanhoPt);
                var familia = (System.Windows.Controls.ComboBox)d.FindName("FamiliaCombo")!;
                familia.SelectedIndex = 1; Assert.Equal(TextFontFamily.SansSerif, d.ResultadoAtual().Familia);
            }
            finally { d.Close(); }
        });
    }

    [Fact] // Aplicar fica desabilitado com texto novo vazio/só espaços (controle positivo: volta a habilitar)
    public void CorrigirTextoDialog_EmptyText_DisablesAplicar()
    {
        RunOnSta(() =>
        {
            var d = new mPdf.App.Views.CorrigirTextoDialog(
                new CorrigirTextoPedido("abc", TextFontFamily.Serif, false, false, 11, 0xFF000000),
                _ => new CorrigirTextoPrevia(null, null));
            try
            {
                var novo = (System.Windows.Controls.TextBox)d.FindName("TextoNovoBox")!;
                var aplicar = (System.Windows.Controls.Button)d.FindName("AplicarButton")!;
                Assert.True(aplicar.IsEnabled);
                novo.Text = "   ";
                Assert.False(aplicar.IsEnabled);
                novo.Text = "x";
                Assert.True(aplicar.IsEnabled);
            }
            finally { d.Close(); }
        });
    }

    [Fact] // prévia: digitação rápida -> debounce; nunca 2 chamadas simultâneas; a última chamada vê o texto final
    // e o resultado dela (recorte + aviso) chega à tela.
    public void CorrigirTextoDialog_Preview_DebouncedSerializedAndLatestWins()
    {
        RunOnSta(() =>
        {
            int emVoo = 0, maxEmVoo = 0, chamadas = 0;
            string? ultimoTexto = null;
            var d = new mPdf.App.Views.CorrigirTextoDialog(
                new CorrigirTextoPedido("abc", TextFontFamily.Serif, false, false, 11, 0xFF000000),
                r =>
                {
                    int n = Interlocked.Increment(ref emVoo);
                    lock (typeof(CorrigirTextoTests)) { maxEmVoo = Math.Max(maxEmVoo, n); chamadas++; ultimoTexto = r.TextoNovo; }
                    Thread.Sleep(500);
                    Interlocked.Decrement(ref emVoo);
                    return new CorrigirTextoPrevia(Solid(4, 2, 0, 0, 0), "aviso " + r.TextoNovo);
                });
            try
            {
                var novo = (System.Windows.Controls.TextBox)d.FindName("TextoNovoBox")!;
                var img = (System.Windows.Controls.Image)d.FindName("PreviaImage")!;
                var aviso = (System.Windows.Controls.TextBlock)d.FindName("AvisoText")!;

                novo.Text = "a1";
                Pump(TimeSpan.FromMilliseconds(400)); // debounce (300 ms) dispara -> 1a chamada (dura 500 ms)
                novo.Text = "a2"; novo.Text = "a3";     // chegam com a 1a ainda em voo
                Pump(TimeSpan.FromMilliseconds(380)); // debounce dispara enquanto a 1a roda -> fica pendente
                novo.Text = "a4";
                Pump(TimeSpan.FromMilliseconds(2200));

                Assert.Equal(1, maxEmVoo);
                Assert.Equal("a4", ultimoTexto);
                Assert.True(chamadas <= 4, $"chamadas demais: {chamadas}");
                Assert.NotNull(img.Source);
                Assert.Equal("aviso a4", aviso.Text);
                Assert.Equal(Visibility.Visible, aviso.Visibility);
            }
            finally { d.Close(); }
        });
    }

    private static void Pump(TimeSpan duracao)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = duracao };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action scenario)
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

        bool joined = thread.Join(TimeSpan.FromSeconds(15));
        Assert.True(joined, "thread STA não terminou dentro de 15s (BLOCKED: possível deadlock/hang do WPF)");
        if (threadEx is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(threadEx).Throw();
    }
}

internal sealed class FakeCorrigirTextoDialog : ICorrigirTextoDialogService
{
    public CorrigirTextoResultado? Resultado { get; set; }
    public CorrigirTextoPedido? UltimoPedido { get; private set; }
    public Func<CorrigirTextoResultado, CorrigirTextoPrevia>? UltimaPrevia { get; private set; }
    public int Chamadas { get; private set; }
    public CorrigirTextoResultado? Pedir(CorrigirTextoPedido pedido, Func<CorrigirTextoResultado, CorrigirTextoPrevia> previa)
    { Chamadas++; UltimoPedido = pedido; UltimaPrevia = previa; return Resultado; }
}
