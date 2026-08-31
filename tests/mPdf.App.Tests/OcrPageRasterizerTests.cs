using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using mPdf.App.Services;
using mPdf.Editing;

namespace mPdf.App.Tests;

/// TDD do render de OCR (~300 DPI) e da detecção de página-com-texto (Task 2, Plano 15). Seam
/// SEPARADO do pipeline de tela: `OcrPageRasterizer` só chama `PdfDocumentRenderer.RenderPage`/
/// `GetTextPage` (mPdf.Rendering) sobre um renderer PRÓPRIO — nunca o `PageViewModel`/
/// `RenderScheduler`/overlay de tela (ver ViewerIntegrationTests/PdfViewerControl*Tests, que
/// continuam intocados por esta task).
public sealed class OcrPageRasterizerTests
{
    /// Gera um PDF de 1 página SEM camada de texto (imagem pura): renderiza uma frase num bitmap via
    /// System.Drawing, codifica PNG, e converte com o `ImageToPdf` existente (Plano 2b) -- que NUNCA
    /// grava texto, só a imagem. Mesmo padrão de bitmap sintético de tests/mPdf.Ocr.Tests
    /// (TesseractOcrEngineTests.RenderTextBitmap), mas aqui o produto final é o PDF-imagem, não o
    /// reconhecimento em si (isso é escopo da T4).
    private static byte[] BuildImageOnlyPdf()
    {
        const int w = 800, h = 300;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.HighQuality;
            using var font = new Font(FontFamily.GenericSansSerif, 40f, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString("Pagina escaneada sem texto", font, Brushes.Black, new PointF(20f, 100f));
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        byte[] png = ms.ToArray();

        IPdfEditor editor = PdfEditorFactory.Create();
        return editor.ImageToPdf(png);
    }

    [Fact]
    public void PaginaTemTexto_DocumentoComTextoReal_RetornaTrue()
    {
        using var rasterizer = new OcrPageRasterizer(Fixtures.A4());

        Assert.True(rasterizer.PaginaTemTexto(0));
    }

    [Fact]
    public void PaginaTemTexto_PdfImagemSemCamadaDeTexto_RetornaFalse()
    {
        byte[] pdf = BuildImageOnlyPdf();
        using var rasterizer = new OcrPageRasterizer(pdf);

        Assert.False(rasterizer.PaginaTemTexto(0));
    }

    /// Fixture já existente do repo (1 página A4 genuinamente sem NENHUM texto, nem invisível) --
    /// segunda prova do caminho falso, independente do ImageToPdf.
    [Fact]
    public void PaginaTemTexto_FixtureSemTexto_RetornaFalse()
    {
        using var rasterizer = new OcrPageRasterizer(Fixtures.NoText());

        Assert.False(rasterizer.PaginaTemTexto(0));
    }

    /// Página A4 (595x842pt) a 300 DPI: 595/72*300 ≈ 2479px, 842/72*300 ≈ 3508px -- mesma ordem de
    /// grandeza do "A4 a 300dpi ≈ 2480x3508" citado no plano. Tolerância de alguns px (arredondamento
    /// do renderer nativo).
    [Fact]
    public void RasterizeForOcr_PaginaA4_DimensoesCoerentesCom300Dpi()
    {
        using var rasterizer = new OcrPageRasterizer(Fixtures.A4());

        var page = rasterizer.RasterizeForOcr(0);

        Assert.InRange(page.WidthPx, 2460, 2500);
        Assert.InRange(page.HeightPx, 3490, 3530);
        Assert.Equal(page.WidthPx * page.HeightPx * 4, page.Bgra.Length);
    }

    /// Integração render->OCR: alimenta o bitmap rasterizado (300dpi) de uma página-imagem conhecida
    /// no motor da T1 (mPdf.Ocr) e confere que o texto sai -- prova que o SEAM inteiro (Rendering ->
    /// App -> Ocr) funciona de ponta a ponta, não só cada peça isolada. Asserção tolerante a ruído
    /// (normalizada, "contém"), igual ao padrão de TesseractOcrEngineTests.
    [Fact]
    public void RasterizeForOcr_PdfImagemComFrase_OcrReconheceTexto()
    {
        byte[] pdf = BuildImageOnlyPdf();
        using var rasterizer = new OcrPageRasterizer(pdf);

        var page = rasterizer.RasterizeForOcr(0);

        using var engine = new mPdf.Ocr.TesseractOcrEngine();
        var result = engine.Recognize(page.Bgra, page.WidthPx, page.HeightPx, mPdf.Ocr.TesseractOcrEngine.DefaultLanguages);

        string norm = Normalize(result.PlainText);
        // Tolerante a ruído: exige achar a maioria das palavras-chave da frase conhecida.
        string[] esperadas = { "pagina", "escaneada", "sem", "texto" };
        int achadas = esperadas.Count(p => norm.Contains(p, StringComparison.Ordinal));
        Assert.True(achadas >= 3,
            $"OCR do render 300dpi deveria conter a maioria das palavras. Achadas {achadas}/4. Texto: '{norm.Trim()}'");
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s.Normalize(System.Text.NormalizationForm.FormD))
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else sb.Append(' ');
        }
        return sb.ToString();
    }
}
