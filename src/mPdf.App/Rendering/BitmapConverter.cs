using System.Windows.Media;
using System.Windows.Media.Imaging;
using mPdf.Rendering;

namespace mPdf.App.Rendering;

public static class BitmapConverter
{
    /// `dpiX`/`dpiY` SEM valor default (Task 2, Plano 9) — o hardcode 96 que morava aqui morreu de
    /// propósito: cada um dos 5 chamadores deste método (viewer, miniaturas, organizador, impressão,
    /// export) precisa DECLARAR explicitamente o DPI que está pedindo, nunca herdar 96 por acidente.
    /// O viewer real passa o DPI do MONITOR (96 × `DocumentViewModel.DpiFactor`, ver
    /// `PageViewModel.RequestRender`); miniaturas/organizador passam 96 fixo (escala própria, pequenas,
    /// custo sem ganho); impressão/export já tratam DPI explicitamente nos PRÓPRIOS pipelines (o valor
    /// que entra aqui não afeta o resultado final deles — ver doc XML de cada call site).
    public static BitmapSource ToBitmapSource(RenderedPage page, double dpiX, double dpiY)
    {
        var bmp = BitmapSource.Create(
            page.WidthPx, page.HeightPx, dpiX, dpiY,
            PixelFormats.Bgra32, null, page.Bgra, page.WidthPx * 4);
        bmp.Freeze(); // permite criar em thread de fundo e usar na UI
        return bmp;
    }
}
