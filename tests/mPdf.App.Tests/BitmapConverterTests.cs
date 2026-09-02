using mPdf.App.Rendering;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

public class BitmapConverterTests
{
    [Fact] // BGRA vira BitmapSource com as mesmas dimensões, congelado (thread-safe)
    public void ToBitmapSource_PreservesDimensions_AndIsFrozen()
    {
        var page = new RenderedPage(3, 2, new byte[3 * 2 * 4]);
        var bmp = BitmapConverter.ToBitmapSource(page, 96, 96);
        Assert.Equal(3, bmp.PixelWidth);
        Assert.Equal(2, bmp.PixelHeight);
        Assert.True(bmp.IsFrozen);
    }

    [Fact] // Task 2 (Plano 9): o hardcode de 96 morreu -- dpiX/dpiY vêm do CHAMADOR e viram a tag do
    // BitmapSource sem alteração nenhuma (nenhum default escondido dentro do método).
    public void ToBitmapSource_TagsCallerRequestedDpi()
    {
        var page = new RenderedPage(3, 2, new byte[3 * 2 * 4]);
        var bmp = BitmapConverter.ToBitmapSource(page, 144, 144);
        Assert.Equal(144, bmp.DpiX);
        Assert.Equal(144, bmp.DpiY);
    }

    [Fact] // dpiX/dpiY são independentes -- prova que nenhum dos 2 é ignorado ou trocado pelo outro.
    public void ToBitmapSource_TagsAsymmetricDpi_Independently()
    {
        var page = new RenderedPage(3, 2, new byte[3 * 2 * 4]);
        var bmp = BitmapConverter.ToBitmapSource(page, 96, 144);
        Assert.Equal(96, bmp.DpiX);
        Assert.Equal(144, bmp.DpiY);
    }
}
