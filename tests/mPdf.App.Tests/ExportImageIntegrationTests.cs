using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using mPdf.App.ViewModels;
using mPdf.Editing;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

/// Task 4 (Plano 7): oráculo de pixels OBRIGATÓRIO — motor REAL (`PdfDocumentRenderer`, PDFium) dos DOIS
/// lados: um render INDEPENDENTE (mesmos bytes-fonte, mesma escala) é comparado contra o arquivo PNG/JPG
/// exportado, RELIDO do disco (nunca comparado contra um valor hand-calculado). PNG: igualdade ESTRITA
/// (fonte de pixels idêntica em ambos os lados — `BitmapConverter.ToBitmapSource` — e `PngBitmapEncoder`
/// é sem perdas). JPG: tolerância MEDIDA — mesma `JpegColorTolerance=30` já estabelecida em
/// `PdfEditorTests`/`ImageToolIntegrationTests` para qualidade ~90.
public class ExportImageIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-exportimg-px-{Guid.NewGuid():N}");
    public ExportImageIntegrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const int JpegColorTolerance = 30;
    private static readonly (byte R, byte G, byte B) Red = (255, 0, 0);
    private static readonly (byte R, byte G, byte B) Lime = (0, 255, 0);
    private static readonly (byte R, byte G, byte B) Blue = (0, 0, 255);
    private static readonly (byte R, byte G, byte B) Yellow = (255, 255, 0);

    /// Relê um PNG/JPG do disco pro MESMO shape de `RenderedPage` (BGRA32, opaco) — via WPF puro, o
    /// caminho que um usuário real teria "reabrindo o arquivo exportado num visualizador de imagens".
    private static RenderedPage DecodeImageFile(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);
        return new RenderedPage(converted.PixelWidth, converted.PixelHeight, buffer);
    }

    // mesma técnica de PdfEditorTests.CountDifferingPixels (RGB, ignora alfa -- o renderer sempre produz
    // buffer opaco, ver doc XML de PdfDocumentRenderer.RenderPage).
    private static int CountDifferingPixels(RenderedPage a, RenderedPage b)
    {
        Assert.Equal(a.WidthPx, b.WidthPx);
        Assert.Equal(a.HeightPx, b.HeightPx);
        int diff = 0;
        for (int i = 0; i + 2 < a.Bgra.Length; i += 4)
            if (a.Bgra[i] != b.Bgra[i] || a.Bgra[i + 1] != b.Bgra[i + 1] || a.Bgra[i + 2] != b.Bgra[i + 2])
                diff++;
        return diff;
    }

    private static int MaxChannelDiff(RenderedPage a, RenderedPage b)
    {
        Assert.Equal(a.WidthPx, b.WidthPx);
        Assert.Equal(a.HeightPx, b.HeightPx);
        int max = 0;
        for (int i = 0; i + 2 < a.Bgra.Length; i += 4)
        {
            max = Math.Max(max, Math.Abs(a.Bgra[i] - b.Bgra[i]));
            max = Math.Max(max, Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]));
            max = Math.Max(max, Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]));
        }
        return max;
    }

    private static void AssertPixelColor(RenderedPage page, int x, int y, (byte R, byte G, byte B) expected, int tolerance)
    {
        x = Math.Clamp(x, 0, page.WidthPx - 1); y = Math.Clamp(y, 0, page.HeightPx - 1);
        int i = (y * page.WidthPx + x) * 4;
        byte b = page.Bgra[i], g = page.Bgra[i + 1], r = page.Bgra[i + 2];
        Assert.True(Math.Abs(r - expected.R) <= tolerance && Math.Abs(g - expected.G) <= tolerance && Math.Abs(b - expected.B) <= tolerance,
            $"cor em ({x},{y}) fora da tolerância: esperado ~({expected.R},{expected.G},{expected.B}), obtido ({r},{g},{b})");
    }

    [Fact]
    public async Task Export_CurrentPage_Png_Dpi150_RereadMatchesRendererOutput_Strict()
    {
        byte[] pdf = Fixtures.A4();
        var dest = Path.Combine(_dir, "a4.png");
        var vm = new ExportImageViewModel(pdf, pageCount: 1, currentPageIndex: 0, baseFileName: "a4")
        { Destination = dest, Dpi = 150 };

        await vm.StartCommand.ExecuteAsync(null);

        using var renderer = new PdfDocumentRenderer(pdf);
        var expected = renderer.RenderPage(0, 150 / 72.0);
        var actual = DecodeImageFile(dest);

        Assert.Equal(expected.WidthPx, actual.WidthPx);
        Assert.Equal(expected.HeightPx, actual.HeightPx);
        Assert.Equal(0, CountDifferingPixels(expected, actual)); // igualdade ESTRITA, brief
    }

    [Fact]
    public async Task Export_CurrentPage_Jpg_Dpi150_RereadWithinMeasuredTolerance()
    {
        byte[] pdf = Fixtures.A4();
        var dest = Path.Combine(_dir, "a4.jpg");
        var vm = new ExportImageViewModel(pdf, pageCount: 1, currentPageIndex: 0, baseFileName: "a4")
        { Destination = dest, Dpi = 150, Format = ExportImageFormat.Jpg };

        await vm.StartCommand.ExecuteAsync(null);

        using var renderer = new PdfDocumentRenderer(pdf);
        var expected = renderer.RenderPage(0, 150 / 72.0);
        var actual = DecodeImageFile(dest);

        Assert.Equal(expected.WidthPx, actual.WidthPx);
        Assert.Equal(expected.HeightPx, actual.HeightPx);
        int maxDiff = MaxChannelDiff(expected, actual);
        Assert.True(maxDiff <= JpegColorTolerance,
            $"diferença de canal ({maxDiff}) acima da tolerância medida ({JpegColorTolerance}) para JPEG qualidade 90.");
    }

    [Fact] // fórmula exata do dpi->escala provada contra uma 2ª chamada REAL ao renderer na MESMA escala
    // (nenhum arredondamento hand-calculado no teste -- PDFium/Docnet decide, o teste só compara).
    public async Task Export_Dpi300_PixelDimensions_MatchDirectRendererCallAtSameScale()
    {
        byte[] pdf = Fixtures.A4();
        var dest = Path.Combine(_dir, "a4-300.png");
        var vm = new ExportImageViewModel(pdf, pageCount: 1, currentPageIndex: 0, baseFileName: "a4")
        { Destination = dest, Dpi = 300 };

        await vm.StartCommand.ExecuteAsync(null);

        using var renderer = new PdfDocumentRenderer(pdf);
        var expected = renderer.RenderPage(0, 300 / 72.0);
        var actual = DecodeImageFile(dest);

        Assert.Equal(expected.WidthPx, actual.WidthPx);
        Assert.Equal(expected.HeightPx, actual.HeightPx);
        Assert.Equal(0, CountDifferingPixels(expected, actual));
    }

    [Fact]
    public async Task Export_AllPages_Fixture30p_Generates30Files_ZeroPaddedToPageCountWidth()
    {
        byte[] pdf = Fixtures.ThirtyPages();
        var vm = new ExportImageViewModel(pdf, pageCount: 30, currentPageIndex: 0, baseFileName: "fixture-30p")
        { Destination = _dir, Range = ExportImageRange.AllPages, Dpi = 150 };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(30, vm.ExportedCount);
        var files = Directory.GetFiles(_dir, "*.png").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(30, files.Count);
        Assert.Contains("fixture-30p-p01.png", files);
        Assert.Contains("fixture-30p-p30.png", files);
        Assert.DoesNotContain("fixture-30p-p1.png", files); // sem padding não deveria existir
    }

    [Fact] // round-trip: foto (JPG) -> PdfEditor.ImageToPdf (Task 1, Plano 7) -> ExportImage (PNG) -- as
    // cores de canto do PNG exportado devem bater com as cores de canto CONHECIDAS da foto original,
    // dentro da MESMA tolerância JPEG já medida (a foto de origem É um JPEG comprimido).
    public async Task Export_ConvertedImageDocument_PngCorners_MatchOriginalPhoto_WithinTolerance()
    {
        var editor = PdfEditorFactory.Create();
        byte[] pdf = editor.ImageToPdf(Fixtures.Foto());
        var dest = Path.Combine(_dir, "foto.png");
        var vm = new ExportImageViewModel(pdf, pageCount: 1, currentPageIndex: 0, baseFileName: "foto")
        { Destination = dest, Dpi = 150 };

        await vm.StartCommand.ExecuteAsync(null);

        var actual = DecodeImageFile(dest);
        AssertPixelColor(actual, 5, 5, Red, JpegColorTolerance);
        AssertPixelColor(actual, actual.WidthPx - 5, 5, Lime, JpegColorTolerance);
        AssertPixelColor(actual, 5, actual.HeightPx - 5, Blue, JpegColorTolerance);
        AssertPixelColor(actual, actual.WidthPx - 5, actual.HeightPx - 5, Yellow, JpegColorTolerance);
    }
}
