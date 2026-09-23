using System.IO;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

/// Task 3 (Plano 7): px integration pra "🖼 Imagem" — motor REAL (`PdfEditorFactory.Create()`, NUNCA
/// `FakePdfEditor`) sobre uma sessão REAL de `fixture-a4.pdf`, exercitando `ToggleImageTool` +
/// `PlaceStampAtAsync` ponta a ponta e renderizando o PDF resultante via PDFium (`mPdf.Rendering`,
/// motor INDEPENDENTE do iText que escreveu — mesmo padrão de
/// `PdfEditorTests.AddAnnotation_ImageStamp_RendersNonBlankInStampRegion`/
/// `ImageToPdf_JpegWithExifOrientation6_OpensUpright`). 2 provas:
///   1. `fixture-foto.jpg` aparece na REGIÃO certa (cantos de cor conhecida) e NÃO vaza pra fora do bbox.
///   2. `fixture-foto-exif90.jpg` (MESMOS pixels, pré-rotacionados 90° CCW + EXIF Orientation=6) entra
///      EM PÉ — mesmas cores de canto de (1) — a prova CARREGADA (brief): `NormalizeExifRotation`
///      corrige de verdade, não só "não lança". Sem este fix, o carimbo entraria DE LADO (a mesma
///      armadilha que `ImageToPdf`/Task 1 já corrigiu no OUTRO caminho de imagem).
public class ImageToolIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-imgtool-px-{Guid.NewGuid():N}");
    public ImageToolIntegrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteFile(byte[] bytes, string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // Cores de canto conhecidas das 2 fixtures (mesmas de PdfEditorTests — geradas via probe, ver
    // task-1-report.md): TL=vermelho puro, TR=verde/lime puro, BL=azul puro, BR=amarelo puro.
    private const int JpegColorTolerance = 30;
    private static readonly (byte R, byte G, byte B) Red = (255, 0, 0);
    private static readonly (byte R, byte G, byte B) Lime = (0, 255, 0);
    private static readonly (byte R, byte G, byte B) Blue = (0, 0, 255);
    private static readonly (byte R, byte G, byte B) Yellow = (255, 255, 0);

    private static void AssertPixelColor(RenderedPage page, int x, int y, (byte R, byte G, byte B) expected, int tolerance)
    {
        x = Math.Clamp(x, 0, page.WidthPx - 1); y = Math.Clamp(y, 0, page.HeightPx - 1);
        int i = (y * page.WidthPx + x) * 4;
        byte b = page.Bgra[i], g = page.Bgra[i + 1], r = page.Bgra[i + 2];
        Assert.True(Math.Abs(r - expected.R) <= tolerance && Math.Abs(g - expected.G) <= tolerance && Math.Abs(b - expected.B) <= tolerance,
            $"cor em ({x},{y}) fora da tolerância: esperado ~({expected.R},{expected.G},{expected.B}), obtido ({r},{g},{b})");
    }

    /// Coloca `fixtureBytes` via ToggleImageTool+PlaceStampAtAsync (motor real) e devolve a página
    /// renderizada + o bbox REAL da anotação (lido de volta via ReadAnnotations, não hand-calculado —
    /// evita reproduzir a matemática de NaturalStampSize/ClampToPage à mão no teste, que arriscaria
    /// divergir do código de produção sem o teste perceber).
    private static async Task<(RenderedPage page, int left, int right, int topPx, int bottomPx)> PlaceAndRender(
        byte[] fixtureBytes, string fileName, string dir)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, fixtureBytes);
        var dialogs = new FakePickImageDialog(path);
        var editor = PdfEditorFactory.Create();
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        using var d = new DocumentViewModel(session, editor: editor,
            notifyError: _ => { }, notifyInfo: _ => { }, dialogs: dialogs);
        using (session)
        {
            d.ToggleImageToolCommand.Execute(null);
            if (d.ActiveTool != AnnotationTool.ImageStamp)
                throw new InvalidOperationException("ToggleImageTool não entrou em modo de colocação — validação recusou a fixture.");

            await d.PlaceStampAtAsync(0, 100, 600); // canto inferior-esquerdo do carimbo, em pt

            var stamp = Assert.Single(editor.ReadAnnotations(session.Snapshot));
            Assert.Equal(AnnotationKind.ImageStamp, stamp.Kind);

            using var renderer = new PdfDocumentRenderer(session.Snapshot);
            var page = renderer.RenderPage(0, 1.0);
            int h = page.HeightPx;
            int left = (int)Math.Round(stamp.LeftPt), right = (int)Math.Round(stamp.RightPt);
            int topPx = h - (int)Math.Round(stamp.TopPt), bottomPx = h - (int)Math.Round(stamp.BottomPt);
            return (page, left, right, topPx, bottomPx);
        }
    }

    private static void AssertNoBleedOutsideBbox(RenderedPage page, int left, int right, int topPx, int bottomPx, int margin = 10)
    {
        int outsidePainted = 0;
        int yFrom = Math.Max(0, topPx - margin), yTo = Math.Min(page.HeightPx, bottomPx + margin);
        int xFrom = Math.Max(0, left - margin), xTo = Math.Min(page.WidthPx, right + margin);
        for (int y = yFrom; y < yTo; y++)
        {
            for (int x = xFrom; x < xTo; x++)
            {
                bool insideBbox = x >= left && x < right && y >= topPx && y < bottomPx;
                if (insideBbox) continue;
                int i = (y * page.WidthPx + x) * 4;
                byte b = page.Bgra[i], g = page.Bgra[i + 1], r = page.Bgra[i + 2];
                bool isWhite = r > 250 && g > 250 && b > 250;
                if (!isWhite) outsidePainted++;
            }
        }
        Assert.Equal(0, outsidePainted);
    }

    [Fact]
    public async Task PlaceStampAtAsync_ViaPickedImageTool_Foto_RendersInRegion_NoBleedOutside()
    {
        var (page, left, right, topPx, bottomPx) = await PlaceAndRender(Fixtures.Foto(), "fixture-foto.jpg", _dir);

        AssertPixelColor(page, left + 5, topPx + 5, Red, JpegColorTolerance);
        AssertPixelColor(page, right - 5, topPx + 5, Lime, JpegColorTolerance);
        AssertPixelColor(page, left + 5, bottomPx - 5, Blue, JpegColorTolerance);
        AssertPixelColor(page, right - 5, bottomPx - 5, Yellow, JpegColorTolerance);
        AssertNoBleedOutsideBbox(page, left, right, topPx, bottomPx);
    }

    [Fact] // TESTE CARREGADO (brief): fixture-foto-exif90 tem os MESMOS pixels de fixture-foto, só que
    // pré-rotacionados 90° CCW + EXIF Orientation=6. Sem NormalizeExifRotation, o carimbo entraria DE
    // LADO (TL não seria vermelho) — mesmas cores de canto esperadas de
    // PlaceStampAtAsync_ViaPickedImageTool_Foto_RendersInRegion_NoBleedOutside provam que abriu EM PÉ.
    public async Task PlaceStampAtAsync_ViaPickedImageTool_FotoExif90_RendersUprightInRegion()
    {
        var (page, left, right, topPx, bottomPx) = await PlaceAndRender(Fixtures.FotoExif90(), "fixture-foto-exif90.jpg", _dir);

        AssertPixelColor(page, left + 5, topPx + 5, Red, JpegColorTolerance);
        AssertPixelColor(page, right - 5, topPx + 5, Lime, JpegColorTolerance);
        AssertPixelColor(page, left + 5, bottomPx - 5, Blue, JpegColorTolerance);
        AssertPixelColor(page, right - 5, bottomPx - 5, Yellow, JpegColorTolerance);
        AssertNoBleedOutsideBbox(page, left, right, topPx, bottomPx);
    }
}
