using System.IO;
using System.Text;
using iText.Kernel.Pdf;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.Signing.Tests;

/// Bug (revisor, na main): assinar um PDF/A-1b COM carimbo visível falhava com `PdfSigningException`
/// ("Não foi possível assinar o PDF"), causa `PdfAConformanceException: Transparency is not allowed.
/// CA shall be equal to 1` — o selo-marca-d'água do carimbo padrão usa alfa 0.10/0.12 (ExtGState), e
/// PDF/A-1 (ISO 19005-1 §6.4) proíbe QUALQUER transparência: CA/ca != 1 e imagens com /SMask. A2/A3
/// permitem, por isso os testes `Sign_PdfA_*` (fixture A2B) não pegavam. PDF/A-1b é o PADRÃO do
/// "Salvar como PDF/A" (PdfADialog), então é o caso mais comum no uso real.
///
/// Oráculos: (1) `Sign` retorna — o `PdfPadesSigner` abre a entrada PDF/A como `PdfADocument` e recusa
/// gravar uma revisão não-conforme; (2) a assinatura é íntegra; (3) varredura ESTRUTURAL da árvore de
/// aparência do widget: nenhum ExtGState com CA/ca &lt; 1 e nenhuma imagem com /SMask (independe do
/// verificador do iText); (4) veraPDF (manual, ver relatório da task).
public class PdfA1StampTests
{
    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();
    private static readonly PdfQuad StampRect = new(350, 50, 530, 110); // 180x60pt (default do app)

    private static byte[] Sign(byte[] pdf, VisibleStampSpec? stamp)
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        return Engine.Sign(new SignRequest(pdf, cert, null, null, stamp, null));
    }

    // 8x8 PNG RGBA em xadrez com metade dos pixels TRANSPARENTES (mesmo de PadesSigningEngineTests).
    internal static byte[] RubricaPngTransparent() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUA" +
        "AAAJcEhZcwAADsMAAA7DAcdvqGQAAAAdSURBVChTY2BgYGDgEjnxHyeNVRCJBgN0wSFnAgAMKTyhBiyh5wAAAABJRU5ErkJggg==");

    /// Tudo o que a árvore `/AP/N` do widget (recursiva pelos `/XObject`) carrega e que importa aqui.
    internal sealed record AppearanceScan(
        List<(float? Fill, float? Stroke)> Opacities, int ImagesWithSMask, int Images, string Content,
        int ImagesWithStencilMask = 0);

    internal static AppearanceScan ScanAppearance(byte[] pdf, int pageIndex = 0)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var annot = Assert.Single(doc.GetPage(pageIndex + 1).GetAnnotations());
        var root = annot.GetPdfObject().GetAsDictionary(PdfName.AP)?.GetAsStream(PdfName.N);
        Assert.NotNull(root);
        var opac = new List<(float?, float?)>();
        int smask = 0, images = 0, stencil = 0;
        var content = new StringBuilder();
        var seen = new HashSet<PdfObject>();

        void Walk(PdfStream s)
        {
            if (!seen.Add(s)) return;
            if (PdfName.Image.Equals(s.GetAsName(PdfName.Subtype)))
            {
                images++;
                if (s.Get(PdfName.SMask) is { } m && !PdfName.None.Equals(m)) smask++;
                if (s.GetAsStream(PdfName.Mask) is { } hard && hard.GetAsBool(PdfName.ImageMask) == true) stencil++;
                return;
            }
            content.Append(Encoding.Latin1.GetString(s.GetBytes())).Append('\n');
            var res = s.GetAsDictionary(PdfName.Resources);
            if (res?.GetAsDictionary(PdfName.ExtGState) is { } gs)
                foreach (var k in gs.KeySet())
                    if (gs.GetAsDictionary(k) is { } g)
                        opac.Add((g.GetAsNumber(PdfName.ca)?.FloatValue(), g.GetAsNumber(PdfName.CA)?.FloatValue()));
            if (res?.GetAsDictionary(PdfName.XObject) is { } xo)
                foreach (var k in xo.KeySet())
                    if (xo.GetAsStream(k) is { } child) Walk(child);
        }

        Walk(root!);
        return new AppearanceScan(opac, smask, images, content.ToString(), stencil);
    }

    private static void AssertNoTransparency(AppearanceScan scan)
    {
        foreach (var (fill, stroke) in scan.Opacities)
        {
            Assert.True(fill is null or 1f, $"ExtGState com ca={fill} (PDF/A-1 exige 1)");
            Assert.True(stroke is null or 1f, $"ExtGState com CA={stroke} (PDF/A-1 exige 1)");
        }
        Assert.Equal(0, scan.ImagesWithSMask);
    }

    [Fact]
    public void Sign_PdfA1B_WithDefaultVisibleStamp_SignsWithoutTransparency()
    {
        var signed = Sign(Fixtures.PdfA(PdfANivel.A1B), new VisibleStampSpec(0, StampRect));

        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);
        var scan = ScanAppearance(signed);
        AssertNoTransparency(scan);
        Assert.Contains("0 0 0 rg\nBT", scan.Content); // texto preto POR CIMA do selo opaco (legível)
        // Selo SÓ-TRAÇO (fix round 1 — nada de página navy preenchida, que virava bloco claro no scan):
        // tons = alfa sobre o papel de scan de referência (222,220,212): contorno navy a 0.12 ->
        // (199,203,203); rubrica teal a 0.12 -> (203,217,207).
        Assert.Contains("0.78039 0.79608 0.79608 RG\n", scan.Content);
        Assert.Contains("0.79608 0.85098 0.81176 RG\n", scan.Content);
        Assert.DoesNotContain("0.11373 0.30588 0.53725 rg", scan.Content); // nem navy puro...
        Assert.DoesNotContain("0.9098 0.92941 0.95294 rg", scan.Content);  // ...nem o bloco claro de afe3298
    }

    [Fact] // a mesma imagem PNG com alfa que o A2B aceita com /SMask: em PDF/A-1 é achatada sobre branco.
    public void Sign_PdfA1B_WithTransparentRubrica_SignsWithoutSMaskAndRendersImage()
    {
        var signed = Sign(Fixtures.PdfA(PdfANivel.A1B),
            new VisibleStampSpec(0, new PdfQuad(100, 100, 280, 160), RubricaPngTransparent()));

        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);
        var scan = ScanAppearance(signed);
        Assert.Equal(1, scan.Images);
        AssertNoTransparency(scan);

        // A imagem achatada continua visível (tinta no retângulo) — PDFium.
        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(0, 1.0);
        bool anyInk = false;
        for (int y = page.HeightPx - 160; y < page.HeightPx - 100 && !anyInk; y++)
            for (int x = 100; x < 280; x++)
            {
                int i = (y * page.WidthPx + x) * 4;
                if (page.Bgra[i] < 200 || page.Bgra[i + 1] < 200 || page.Bgra[i + 2] < 200) { anyInk = true; break; }
            }
        Assert.True(anyInk, "rubrica achatada não renderizou no PDF/A-1b");
    }

    // ---- Página escaneada (papel cinza): o PDF/A do mPDF é SEMPRE scan, nunca fundo branco ----------

    private static readonly (int R, int G, int B) Paper = (222, 220, 212);

    private static (int R, int G, int B) PixelAt(RenderedPage page, double scale, double xPt, double yPt)
    {
        int x = (int)(xPt * scale), y = page.HeightPx - 1 - (int)(yPt * scale);
        int i = (y * page.WidthPx + x) * 4;
        return (page.Bgra[i + 2], page.Bgra[i + 1], page.Bgra[i]);
    }

    private static bool Near((int R, int G, int B) a, (int R, int G, int B) b, int tol) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    [Fact] // Revisão (fix round 1): achatar a rubrica sobre BRANCO colava um retângulo branco por cima do
    // scan, escondendo a linha de assinatura. Agora o alfa vira máscara STENCIL de 1 bit (/Mask com
    // /ImageMask true — permitida em PDF/A-1): onde a rubrica é transparente o papel continua visível.
    public void Sign_PdfA1B_GreyScan_TransparentRubrica_KeepsPaperUnderTransparentPixels()
    {
        var signed = Sign(Fixtures.PdfAScan(PdfANivel.A1B, Paper.R, Paper.G, Paper.B),
            new VisibleStampSpec(0, new PdfQuad(100, 100, 280, 160), RubricaPngTransparent()));
        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);
        var scan = ScanAppearance(signed);
        AssertNoTransparency(scan);
        Assert.Equal(1, scan.ImagesWithStencilMask);

        const double S = 2;
        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(0, S);
        int white = 0, ink = 0;
        for (double y = 100.25; y < 160; y += 0.5)
            for (double x = 100.25; x < 280; x += 0.5)
            {
                var p = PixelAt(page, S, x, y);
                if (p.R > 240 && p.G > 240 && p.B > 240) white++;
                if (p.B > p.R + 60) ink++;
            }
        Assert.Equal(0, white); // nenhum pixel branco na caixa: o papel cinza não foi coberto
        Assert.True(ink > 0, "rubrica não renderizou (nenhum pixel azul)");
        // célula transparente do xadrez (canto sup. esq. da imagem, 60x60 centrada: x 160..220) = papel
        Assert.True(Near(PixelAt(page, S, 163.75, 156.25), Paper, 8), "célula transparente não mostra o papel");
    }

    [Fact] // Revisão (fix round 1): o selo opaco PREENCHIDO virava um bloco claro sobre o scan cinza.
    public void Sign_PdfA1B_GreyScan_DefaultStamp_NoPaleBlockUnderSeal()
    {
        var signed = Sign(Fixtures.PdfAScan(PdfANivel.A1B, Paper.R, Paper.G, Paper.B),
            new VisibleStampSpec(0, StampRect));
        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);
        AssertNoTransparency(ScanAppearance(signed));

        const double S = 2;
        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(0, S);
        // Interior do selo (caixa 350..530 x 50..110; selo ~424.75..455.25 x 55.4..104.6), à direita do
        // texto e longe do traço da rubrica: tem de ser o papel.
        for (double y = 92; y <= 98; y += 0.5)
            for (double x = 444; x <= 452; x += 0.5)
                Assert.True(Near(PixelAt(page, S, x, y), Paper, 8), $"bloco claro do selo em ({x},{y})");
    }

    [Fact] // 2 assinaturas (carimbo + rubrica) sobre o mesmo PDF/A-1b escaneado — ambas íntegras.
    public void Sign_PdfA1B_GreyScan_TwoSignatures_BothIntegrityValid()
    {
        var once = Sign(Fixtures.PdfAScan(PdfANivel.A1B, Paper.R, Paper.G, Paper.B), new VisibleStampSpec(0, StampRect));
        var twice = Sign(once, new VisibleStampSpec(0, new PdfQuad(100, 100, 280, 160), RubricaPngTransparent()));
        var sigs = Engine.ReadSignatures(twice);
        Assert.Equal(2, sigs.Count);
        Assert.All(sigs, s => Assert.True(s.IntegrityValid));
    }

    [Fact] // controle: sem carimbo nunca dependeu de transparência — já assinava antes do fix.
    public void Sign_PdfA1B_Invisible_Signs()
    {
        var signed = Sign(Fixtures.PdfA(PdfANivel.A1B), null);
        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);
    }

    // ---- Pinos: fora de PDF/A-1 o desenho fica EXATAMENTE como antes do fix -------------------------

    /// Bloco INTEIRO do selo (q..Q) no appearance stream da caixa 180x60, capturado na main (bec5c1d)
    /// ANTES do fix: ExtGState translúcido (ca 0.10 / CA 0.12) + navy/teal puros, mesmos operadores e
    /// coordenadas. Qualquer mudança aqui é regressão visual fora do PDF/A-1.
    private const string NormalSealBlock =
        "q\n/Gs1 gs\n0.11373 0.30588 0.53725 rg\n" +
        "79.67 5.4 m\n100.33 5.4 l\n103.05 5.4 105.25 7.6 105.25 10.32 c\n105.25 49.68 l\n" +
        "105.25 52.4 103.05 54.6 100.33 54.6 c\n79.67 54.6 l\n76.95 54.6 74.75 52.4 74.75 49.68 c\n" +
        "74.75 10.32 l\n74.75 7.6 76.95 5.4 79.67 5.4 c\nh\nf\n" +
        "0.24314 0.75686 0.6549 RG\n2.46 w\n1 J\n80.24 24.1 m\n" +
        "85.73 13.27 89.39 38.86 94.27 23.11 c\n97.02 17.21 99.15 27.05 102.2 22.13 c\nS\n" +
        "0.24314 0.75686 0.6549 rg\n104.9 21.64 m\n104.9 22.45 104.24 23.11 103.42 23.11 c\n" +
        "102.61 23.11 101.95 22.45 101.95 21.64 c\n101.95 20.82 102.61 20.16 103.42 20.16 c\n" +
        "104.24 20.16 104.9 20.82 104.9 21.64 c\nf\nQ\n";

    [Theory]
    [InlineData(false)] // PDF comum
    [InlineData(true)]  // PDF/A-2b (permite transparência)
    public void Sign_NonPdfA1_DefaultStamp_KeepsTranslucentSealUnchanged(bool pdfA2b)
    {
        var pdf = pdfA2b ? Fixtures.PdfA(PdfANivel.A2B) : Fixtures.A4();
        var scan = ScanAppearance(Sign(pdf, new VisibleStampSpec(0, StampRect)));

        var gs = Assert.Single(scan.Opacities);
        Assert.Equal((0.10f, 0.12f), gs);
        Assert.Contains(NormalSealBlock, scan.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sign_NonPdfA1_TransparentRubrica_KeepsSMask(bool pdfA2b)
    {
        var pdf = pdfA2b ? Fixtures.PdfA(PdfANivel.A2B) : Fixtures.A4();
        var scan = ScanAppearance(Sign(pdf,
            new VisibleStampSpec(0, new PdfQuad(100, 100, 280, 160), RubricaPngTransparent())));
        Assert.Equal(1, scan.ImagesWithSMask);
    }
}
