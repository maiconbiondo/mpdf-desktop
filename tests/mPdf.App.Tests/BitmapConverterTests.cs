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
        var bmp = BitmapConverter.ToBitmapSource(page);
        Assert.Equal(3, bmp.PixelWidth);
        Assert.Equal(2, bmp.PixelHeight);
        Assert.True(bmp.IsFrozen);
    }
}
