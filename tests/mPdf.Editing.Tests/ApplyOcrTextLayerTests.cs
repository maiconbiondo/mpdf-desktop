using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using mPdf.Rendering;

namespace mPdf.Editing.Tests;

/// Task 3 (Plano 15): camada de texto invisível de OCR (`IPdfEditor.ApplyOcrTextLayer`).
///
/// Provas:
///  - EXTRAÍVEL: o texto gravado é recuperável (busca/cópia) via iText PdfTextExtractor.
///  - INVISÍVEL: render mode 3 — o texto NÃO pinta pixels (diff de pixel ZERO no render via PDFium,
///    motor INDEPENDENTE do iText que escreveu), APESAR de ser extraível.
///  - 4 ROTAÇÕES: a baseline extraída (no frame não-rotacionado do MediaBox), reaplicada por uma
///    reimplementação INDEPENDENTE de `T_θ` no teste, bate na baseline EXIBIDA esperada — prova
///    px-provada de que a caixa cai na posição certa nas 4 rotações. E sempre DENTRO do MediaBox.
///  - GATE DE ASSINATURA: doc assinado -> `PdfSignedDocumentException`.
public class ApplyOcrTextLayerTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    // fixture-a4.pdf: 595x842 pt, /Rotate 0. Base de todos os testes (rotações geradas via RotatePages).
    private const double A4WidthPt = 595, A4HeightPt = 842;

    // --- Extraível --------------------------------------------------------

    [Fact] // dado 1 caixa "CONTRATO" numa página em branco -> o PDF resultante tem texto EXTRAÍVEL "CONTRATO"
    public void ApplyOcrTextLayer_SingleBox_ProducesExtractableText()
    {
        var layer = new OcrTextLayer(0, 800, 1131, new[]
        {
            new OcrTextBox("CONTRATO", LeftPx: 100, TopPx: 100, WidthPx: 300, HeightPx: 40),
        });

        var result = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[] { layer });

        Assert.Contains("CONTRATO", ExtractText(result, page1Based: 1));
    }

    [Fact] // texto vazio/só-espaços é IGNORADO (nenhum operador de texto gravado) — página fica sem texto
    public void ApplyOcrTextLayer_EmptyOrWhitespaceText_IsIgnored()
    {
        var layer = new OcrTextLayer(0, 800, 1131, new[]
        {
            new OcrTextBox("   ", LeftPx: 100, TopPx: 100, WidthPx: 300, HeightPx: 40),
            new OcrTextBox("", LeftPx: 100, TopPx: 200, WidthPx: 300, HeightPx: 40),
        });

        var result = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[] { layer });

        Assert.True(string.IsNullOrWhiteSpace(ExtractText(result, page1Based: 1)));
    }

    [Fact] // layers vazio -> devolve um PDF equivalente (reprocessado), sem lançar; page count preservado
    public void ApplyOcrTextLayer_EmptyLayers_NoThrow_PreservesPageCount()
    {
        var result = Editor.ApplyOcrTextLayer(Fixtures.NoText(), Array.Empty<OcrTextLayer>());

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(result)));
        Assert.Equal(1, doc.GetNumberOfPages());
    }

    [Fact] // PageIndex fora do intervalo -> ArgumentOutOfRangeException, antes de gravar qualquer coisa
    public void ApplyOcrTextLayer_InvalidPageIndex_Throws()
    {
        var layer = new OcrTextLayer(99, 800, 1131, new[]
        {
            new OcrTextBox("X", 10, 10, 20, 20),
        });

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[] { layer }));
    }

    // --- Invisível (render mode 3) ----------------------------------------

    [Fact] // o texto é EXTRAÍVEL mas NÃO pinta NENHUM pixel: diff de render (PDFium) == 0 vs. o original,
    // apesar da string estar presente. Prova simultânea de "invisível E extraível".
    public void ApplyOcrTextLayer_TextIsExtractableButInvisible_ZeroPixelDiff()
    {
        byte[] original = Fixtures.NoText();
        var layer = new OcrTextLayer(0, 800, 1131, new[]
        {
            // caixa BEM larga/alta, no meio da página — se pintasse tinta, seriam MILHARES de px alterados
            new OcrTextBox("TEXTO INVISIVEL DE OCR PESQUISAVEL", LeftPx: 50, TopPx: 400, WidthPx: 700, HeightPx: 60),
        });
        byte[] result = Editor.ApplyOcrTextLayer(original, new[] { layer });

        // Extraível (iText)
        Assert.Contains("PESQUISAVEL", ExtractText(result, page1Based: 1));

        // Invisível: diff de pixel == 0 (render mode 3 não pinta nada) — motor INDEPENDENTE (PDFium).
        using var rendererBefore = new PdfDocumentRenderer(original);
        using var rendererAfter = new PdfDocumentRenderer(result);
        var before = rendererBefore.RenderPage(0, 1.0);
        var after = rendererAfter.RenderPage(0, 1.0);
        Assert.Equal(before.WidthPx, after.WidthPx);
        Assert.Equal(before.HeightPx, after.HeightPx);

        int diff = 0;
        for (int i = 0; i < before.Bgra.Length; i++)
            if (before.Bgra[i] != after.Bgra[i]) diff++;
        Assert.Equal(0, diff);
    }

    // --- 4 rotações (px-provado) ------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ApplyOcrTextLayer_FourRotations_BaselineLandsAtExpectedDisplayPosition(int rotation)
    {
        // Página A4 na rotação-alvo (via RotatePages — código já provado). MediaBox continua 595x842;
        // só /Rotate muda.
        byte[] pdf = Fixtures.NoText();
        if (rotation != 0) pdf = Editor.RotatePages(pdf, new[] { 0 }, rotation);
        Assert.Equal(rotation, Editor.GetPageRotations(pdf)[0]); // sanity: a rotação-alvo foi aplicada

        // Dimensões EXIBIDAS (o bitmap de OCR seria rasterizado assim): 90/270 trocam W<->H.
        bool swap = rotation is 90 or 270;
        double dispWpt = swap ? A4HeightPt : A4WidthPt;
        double dispHpt = swap ? A4WidthPt : A4HeightPt;

        // Bitmap-fonte em px == dimensões exibidas em pt (fator de escala = 1.0, arredondado).
        int srcW = (int)Math.Round(dispWpt);
        int srcH = (int)Math.Round(dispHpt);

        // Caixa no canto SUPERIOR-ESQUERDO do que o usuário VÊ (origem px topo-esquerda).
        const double Lpx = 100, Tpx = 120, Wpx = 200, Hpx = 30;
        var layer = new OcrTextLayer(0, srcW, srcH, new[]
        {
            new OcrTextBox("CONTRATO", Lpx, Tpx, Wpx, Hpx),
        });
        byte[] result = Editor.ApplyOcrTextLayer(pdf, new[] { layer });

        // Baseline REAL do texto, no frame não-rotacionado do MediaBox (iText devolve o ponto em
        // espaço de usuário — sem CTM extra, é exatamente o (tx,ty) que a implementação gravou).
        var (bx, by, text) = ExtractFirstBaseline(result);
        Assert.Contains("CONTRATO", text);

        // Dentro do MediaBox (sempre) — prova de coerência mínima do plano.
        Assert.InRange(bx, 0, A4WidthPt);
        Assert.InRange(by, 0, A4HeightPt);

        // Prova FORTE: reaplicando T_θ (reimplementação INDEPENDENTE abaixo) à baseline extraída,
        // deve cair na baseline EXIBIDA esperada (canto inferior-esquerdo da caixa, y-up display).
        double fatorX = dispWpt / srcW, fatorY = dispHpt / srcH;
        double expXdisp = Lpx * fatorX;
        double expYdisp = dispHpt - (Tpx + Hpx) * fatorY;
        var (gotXdisp, gotYdisp) = DisplayForward(rotation, bx, by, A4WidthPt, A4HeightPt);

        Assert.True(Math.Abs(expXdisp - gotXdisp) < 1.0, $"X exibido: esperado {expXdisp}, obtido {gotXdisp}");
        Assert.True(Math.Abs(expYdisp - gotYdisp) < 1.0, $"Y exibido: esperado {expYdisp}, obtido {gotYdisp}");
    }

    // --- Gate de assinatura -----------------------------------------------

    [Fact] // fixture-carimbo tem 1 assinatura PAdES real -> ApplyOcrTextLayer recusa (mesma disciplina
    // de AddAnnotation/RotatePages) — o App (Task 4) trata como as demais edições.
    public void ApplyOcrTextLayer_OnSignedDocument_Throws()
    {
        var layer = new OcrTextLayer(0, 595, 842, new[] { new OcrTextBox("X", 10, 10, 20, 20) });
        Assert.Throws<PdfSignedDocumentException>(
            () => Editor.ApplyOcrTextLayer(Fixtures.Carimbo(), new[] { layer }));
    }

    // --- Largura certa (feat/ocr-largura: Tz na camada invisível) ---------
    // Oráculo: as caixas de caractere do PDFium (motor INDEPENDENTE do iText que escreveu), no frame
    // EXIBIDO — o mesmo que o App usa pra busca/seleção. Largura da palavra = min Left → max Right.

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ApplyOcrTextLayer_WordWidthMatchesOcrBox(int rotation)
    {
        byte[] pdf = Fixtures.NoText();
        if (rotation != 0) pdf = Editor.RotatePages(pdf, new[] { 0 }, rotation);
        bool swap = rotation is 90 or 270;
        double dispWpt = swap ? A4HeightPt : A4WidthPt, dispHpt = swap ? A4WidthPt : A4HeightPt;
        const double Scale = 2.0; // bitmap a 144 dpi: fator px->pt = 0,5 (prova que a largura usa fatorX)
        // Helvetica natural a 14 pt: "Joaquim" ≈ 52,9 pt numa caixa bem ESTREITA (35 pt, 66%) e "Avenida" ≈ 50,6 pt
        // numa um pouco estreita (42 pt, 83%): as duas são COMPRIMIDAS até a caixa. O Tz escala o AVANÇO; o PDFium
        // mede a caixa do GLIFO, então os recuos laterais da 1ª/última letra (escalados) ficam dentro da caixa.
        // "Gomes" ≈ 41,2 pt numa caixa LARGA (90 pt): NUNCA estica (fix round 2 — esticar juntava palavras na
        // extração), fica na largura natural, começando na borda esquerda da caixa e dentro dela.
        var layer = new OcrTextLayer(0, (int)Math.Round(dispWpt * Scale), (int)Math.Round(dispHpt * Scale), new[]
        {
            new OcrTextBox("Joaquim", 60 * Scale, 100 * Scale, 35 * Scale, 14 * Scale),
            new OcrTextBox("Avenida", 60 * Scale, 200 * Scale, 42 * Scale, 14 * Scale),
            new OcrTextBox("Gomes", 60 * Scale, 300 * Scale, 90 * Scale, 14 * Scale),
        });

        byte[] result = Editor.ApplyOcrTextLayer(pdf, new[] { layer });

        foreach (var (word, caixa) in new[] { ("Joaquim", 35.0), ("Avenida", 42.0) })
        {
            var w = WordExtentDisplayed(result, word);
            Assert.True(Math.Abs(w.Left - 60) <= 2, $"'{word}': esquerda {w.Left:F2} (esperado 60)");
            Assert.True(Math.Abs(w.Width - caixa) <= 2, $"'{word}': largura {w.Width:F2} (esperado {caixa})");
        }
        var larga = WordExtentDisplayed(result, "Gomes");
        double natural = Editor.MeasureTextWidth(TextFontFamily.SansSerif, false, false, 14, "Gomes");
        Assert.True(Math.Abs(larga.Left - 60) <= 2, $"esquerda larga {larga.Left:F2} (esperado 60)");
        Assert.True(Math.Abs(larga.Width - natural) <= 2 && larga.Width < 90, $"largura larga {larga.Width:F2} (natural {natural:F2}, caixa 90)");
    }

    [Fact] // "Joaquim" "Pereira" com caixas estreitas: a Helvetica natural da 1ª passaria por cima da 2ª
    public void ApplyOcrTextLayer_AdjacentWordsExtractSeparately()
    {
        // Helvetica natural a 14 pt: "Joaquim" ≈ 52,9 pt, mas a caixa tem 38 pt e a próxima começa 4 pt depois.
        var layer = new OcrTextLayer(0, 595, 842, new[]
        {
            new OcrTextBox("Joaquim", 100, 100, 38, 14),
            new OcrTextBox("Pereira", 142, 100, 36, 14),
        });

        byte[] result = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[] { layer });

        using var r = new PdfDocumentRenderer(result);
        string text = r.GetTextPage(0).Text;
        Assert.Contains("Joaquim Pereira", text);
        Assert.DoesNotContain("JoaquimPereira", text);
        var a = WordExtentDisplayed(result, "Joaquim");
        var b = WordExtentDisplayed(result, "Pereira");
        Assert.True(a.Left + a.Width < b.Left, $"as palavras se sobrepõem: Joaquim até {a.Left + a.Width:F2}, Pereira de {b.Left:F2}");
    }

    [Theory] // CropBox deslocada (MediaBox maior): largura E posição no frame exibido batem com a caixa
    [InlineData(30f, 50f, 0)]
    [InlineData(30f, 50f, 90)]
    [InlineData(30f, 50f, 180)]
    [InlineData(30f, 50f, 270)]
    public void ApplyOcrTextLayer_CroppedPage_WidthMatchesBox(float ox, float oy, int rotation)
    {
        byte[] pdf;
        using (var output = new MemoryStream())
        {
            using (var doc = new PdfDocument(new PdfWriter(output)))
            {
                var page = doc.AddNewPage(new PageSize(595 + 2 * ox + 40, 842 + 2 * oy + 25));
                page.SetCropBox(new Rectangle(ox, oy, 595, 842));
                page.SetRotation(rotation);
            }
            pdf = output.ToArray();
        }
        bool swap = rotation is 90 or 270;
        double dispW = swap ? 842 : 595, dispH = swap ? 595 : 842;
        const double Scale = 2.0;
        var layer = new OcrTextLayer(0, (int)(dispW * Scale), (int)(dispH * Scale), new[]
        {
            new OcrTextBox("PALAVRA", 100 * Scale, 200 * Scale, 38 * Scale, 12 * Scale), // natural ≈ 51 pt -> comprime
        });

        byte[] result = Editor.ApplyOcrTextLayer(pdf, new[] { layer });

        var w = WordExtentDisplayed(result, "PALAVRA");
        Assert.True(Math.Abs(w.Left - 100) <= 2, $"esquerda {w.Left:F2} (esperado 100)");
        Assert.True(Math.Abs(w.Width - 38) <= 2, $"largura {w.Width:F2} (esperado 38)");
    }

    [Fact] // caixa sem largura (WidthPx <= 0) -> comportamento antigo: Helvetica natural, sem Tz. Vem DEPOIS de
    // uma caixa escalada: prova também que o Tz da anterior não vaza para ela.
    public void ApplyOcrTextLayer_ZeroWidthBox_FallsBackToNaturalWidth()
    {
        var layer = new OcrTextLayer(0, 595, 842, new[]
        {
            new OcrTextBox("Joaquim", 100, 100, 30, 14), // comprimida (natural ≈ 52,9 pt)
            new OcrTextBox("Pereira", 100, 200, 0, 14),   // sem largura
            new OcrTextBox("Gomes", 100, 300, -5, 14),    // largura negativa (defesa)
        });

        byte[] result = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[] { layer });

        Assert.True(WordExtentDisplayed(result, "Joaquim").Width < 33); // sanity: a 1ª foi mesmo comprimida
        foreach (var word in new[] { "Pereira", "Gomes" })
        {
            double natural = Editor.MeasureTextWidth(TextFontFamily.SansSerif, false, false, 14, word);
            var w = WordExtentDisplayed(result, word);
            Assert.True(Math.Abs(w.Width - natural) <= 2, $"'{word}': largura {w.Width:F2}, natural {natural:F2}");
            Assert.True(Math.Abs(w.Left - 100) <= 2, $"'{word}': esquerda {w.Left:F2} (esperado 100)");
        }
    }

    [Fact] // controle do gerador de teste da camada LEGADA (usado pelo Corrigir texto nas duas camadas): ele
    // tem de continuar SEM ajuste de largura — se "consertarem" o helper, a cobertura de PDFs antigos some calada.
    public void LegacyOcrLayer_KeepsNaturalHelveticaWidth()
    {
        var layer = new OcrTextLayer(0, 595, 842, new[] { new OcrTextBox("Joaquim", 100, 100, 35, 14) });
        byte[] legacy = LegacyOcrLayer.Apply(Fixtures.NoText(), new[] { layer });
        byte[] nova = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[] { layer });
        double natural = Editor.MeasureTextWidth(TextFontFamily.SansSerif, false, false, 14, "Joaquim");
        Assert.True(Math.Abs(WordExtentDisplayed(legacy, "Joaquim").Width - natural) <= 2);
        Assert.True(Math.Abs(WordExtentDisplayed(nova, "Joaquim").Width - 35) <= 2);
    }

    /// Extensão horizontal EXIBIDA da palavra pelas caixas de caractere do PDFium. Em página girada o Docnet
    /// transforma os 2 cantos separadamente (Left/Right podem vir trocados) — min/max dá a caixa exibida.
    private static (double Left, double Width) WordExtentDisplayed(byte[] pdf, string word)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var tp = r.GetTextPage(0);
        int i = tp.Text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{word}' não extraído pelo PDFium: '{tp.Text}'");
        var cs = tp.Characters.Skip(i).Take(word.Length).ToList();
        double l = cs.Min(c => Math.Min(c.LeftPt, c.RightPt)), rr = cs.Max(c => Math.Max(c.LeftPt, c.RightPt));
        return (l, rr - l);
    }

    // --- helpers ----------------------------------------------------------

    private static string ExtractText(byte[] pdf, int page1Based)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return PdfTextExtractor.GetTextFromPage(doc.GetPage(page1Based));
    }

    /// Baseline (start point) da 1ª porção de texto renderizada, em espaço de usuário do MediaBox.
    private static (double X, double Y, string Text) ExtractFirstBaseline(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var listener = new BaselineCapture();
        new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(1));
        var first = listener.First!;
        return first.Value;
    }

    /// `T_θ`: ponto NÃO-rotacionado (y-up, origem 0) -> ponto EXIBIDO (y-up), reimplementado de forma
    /// INDEPENDENTE da produção (não chama nada de PdfEditor) — se ambos estivessem errados do MESMO
    /// jeito o teste não pegaria, então esta é uma derivação separada a partir da definição de /Rotate.
    private static (double X, double Y) DisplayForward(int rotation, double x, double y, double w, double h) =>
        rotation switch
        {
            90 => (y, w - x),
            180 => (w - x, h - y),
            270 => (h - y, x),
            _ => (x, y),
        };

    private sealed class BaselineCapture : IEventListener
    {
        public (double X, double Y, string Text)? First { get; private set; }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo tri) return;
            var text = tri.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (First is not null) return;
            var start = tri.GetBaseline().GetStartPoint();
            First = (start.Get(Vector.I1), start.Get(Vector.I2), text);
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
    }
}
