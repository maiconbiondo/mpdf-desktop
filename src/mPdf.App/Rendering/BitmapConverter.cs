using System.Windows.Media;
using System.Windows.Media.Imaging;
using mPdf.Rendering;

namespace mPdf.App.Rendering;

public static class BitmapConverter
{
    public static BitmapSource ToBitmapSource(RenderedPage page)
    {
        var bmp = BitmapSource.Create(
            page.WidthPx, page.HeightPx, 96, 96,
            PixelFormats.Bgra32, null, page.Bgra, page.WidthPx * 4);
        bmp.Freeze(); // permite criar em thread de fundo e usar na UI
        return bmp;
    }
}
