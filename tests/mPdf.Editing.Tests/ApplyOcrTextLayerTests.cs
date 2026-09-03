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
