using System.Text;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using mPdf.Rendering;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 6: `ImporEmFolhas` copiava a página-fonte como XObject (`CopyAsFormXObject`) SEM a
/// rotação do /Rotate, enquanto o `DialogoImpressao` calcula a imposição com os tamanhos EXIBIDOS do PDFium
/// (já girados) — a página saía deitada e esticada. Oráculo: render PDFium — numa folha do tamanho EXIBIDO,
/// colocação 1:1, a folha imposta renderiza igual ao render da própria página-fonte (marca assimétrica: texto
/// em pé + bloco no canto superior-esquerdo exibido).
public class ImposicaoRotationTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    /// Página 400×600 (retrato não-quadrado, para a troca de eixos aparecer) com /Rotate `rot`, opcionalmente
    /// recortada, com conteúdo EM PÉ no frame exibido: palavra na baseline (40, 60) e bloco 60×30 encostado
    /// no canto superior-esquerdo exibido (assimétrico em X e Y).
    internal static byte[] RotatedSource(int rot, bool crop)
    {
        var size = new PageSize(400, 600);
        byte[] blank;
        using (var ms0 = new MemoryStream())
        {
            using (var doc0 = new PdfDocument(new PdfWriter(ms0))) doc0.AddNewPage(size);
            blank = ms0.ToArray();
        }
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(blank)), new PdfWriter(ms)))
        {
            var page = doc.GetPage(1);
            page.GetPdfObject().Put(PdfName.Rotate, new PdfNumber(rot));
            if (crop) page.SetCropBox(new Rectangle(30, 40, 330, 500));
            var f = PageFrame.Frame(page);
            var canvas = new PdfCanvas(page);
            var m = f.TextMatrix(new PdfPoint(40, 60));
            canvas.BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD), 28)
                .SetTextMatrix((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F)
                .ShowText("UPRIGHT").EndText();
            var block = f.ToUser(new PdfQuad(10, f.DisplayedHeight - 40, 70, f.DisplayedHeight - 10));
            canvas.SetFillColor(ColorConstants.BLACK)
                .Rectangle(block.LeftPt, block.BottomPt, block.RightPt - block.LeftPt, block.TopPt - block.BottomPt)
                .Fill();
        }
        return ms.ToArray();
    }

    private static bool[] DarkMask(RenderedPage p)
    {
        var m = new bool[p.WidthPx * p.HeightPx];
        for (int i = 0; i < m.Length; i++)
            m[i] = p.Bgra[i * 4] + p.Bgra[i * 4 + 1] + p.Bgra[i * 4 + 2] < 384;
        return m;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public void ImporEmFolhas_PaginaGirada_SaiComoExibida(int rot, bool crop)
    {
        var src = RotatedSource(rot, crop);
        using var rs = new PdfDocumentRenderer(src);
        var size = rs.GetPageSize(0); // EXIBIDO — o que o DialogoImpressao usa
        var expected = rs.RenderPage(0, 1.0);

        var imposed = Editor.ImporEmFolhas(src,
            new[] { new ColocacaoFolha(0, 0, 0, 0, size.WidthPt, size.HeightPt, 0) }, size.WidthPt, size.HeightPt);

        using var ri = new PdfDocumentRenderer(imposed);
        var actual = ri.RenderPage(0, 1.0);
        Assert.Equal(expected.WidthPx, actual.WidthPx);
        Assert.Equal(expected.HeightPx, actual.HeightPx);
        var a = DarkMask(expected);
        var b = DarkMask(actual);
        int dark = a.Count(x => x), diff = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diff++;
        Assert.True(dark > 1500, $"fixture sem tinta suficiente ({dark}px)");
        Assert.True(diff < dark / 20, $"rot={rot} crop={crop}: {diff}px diferentes de {dark}px escuros");
    }

    /// Página girada na imposição com rotação própria (livreto/N-up gira a pegada 90°): a página-fonte girada
    /// 90 no motor + 90 na imposição = o bloco do canto superior-esquerdo EXIBIDO vai para o canto
    /// superior-DIREITO da pegada (giro horário), e a pegada inteira tem o tamanho exibido trocado.
    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void ImporEmFolhas_PaginaGirada_ComRotacaoDaImposicao(int rot)
    {
        var src = RotatedSource(rot, crop: false);
        using var rs = new PdfDocumentRenderer(src);
        var size = rs.GetPageSize(0); // 600×400 exibido
        // Pegada girada 90°: largura = altura exibida e vice-versa, escala 1.
        var imposed = Editor.ImporEmFolhas(src,
            new[] { new ColocacaoFolha(0, 0, 0, 0, size.HeightPt, size.WidthPt, 90) }, size.HeightPt, size.WidthPt);

        using var ri = new PdfDocumentRenderer(imposed);
        var p = ri.RenderPage(0, 1.0);
        Assert.Equal((int)Math.Round(size.HeightPt), p.WidthPx);
        // Bloco (60×30 exibido, no topo-esquerdo) girado horário: 30×60 no topo-DIREITO.
        int topRight = Dark(p, p.WidthPx - 40, 10, p.WidthPx - 10, 70);
        int topLeft = Dark(p, 0, 0, 40, 70);
        Assert.True(topRight > 1500, $"bloco esperado no topo-direito, veio {topRight}px");
        Assert.True(topLeft < 50, $"topo-esquerdo deveria estar vazio, veio {topLeft}px");
    }

    private static int Dark(RenderedPage p, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * p.WidthPx + x) * 4;
                if (p.Bgra[i] + p.Bgra[i + 1] + p.Bgra[i + 2] < 384) n++;
            }
        return n;
    }

    /// PIN (gravado no código ANTERIOR ao conserto): em página sem /Rotate o operador `cm` de cada colocação é
    /// byte a byte o de antes — rotação 0 continua a identidade (com e sem CropBox, com rotação de imposição).
    [Fact]
    public void ImporEmFolhas_Rotacao0_ConteudoDaFolhaInalterado()
    {
        var plain = RotatedSource(0, crop: false);
        var cropped = RotatedSource(0, crop: true);
        var col = new[]
        {
            new ColocacaoFolha(0, 0, 10, 20, 200, 300, 0),
            new ColocacaoFolha(0, 0, 300, 20, 300, 200, 90),
            new ColocacaoFolha(0, 0, 10, 400, 200, 300, 180),
            new ColocacaoFolha(0, 0, 300, 400, 300, 200, 270),
        };
        Assert.Equal(PinPlain.ReplaceLineEndings("\n"), SheetContent(Editor.ImporEmFolhas(plain, col, 842, 842)));
        Assert.Equal(PinCropped.ReplaceLineEndings("\n"), SheetContent(Editor.ImporEmFolhas(cropped, col, 842, 842)));
    }

    private const string PinPlain =
        @"q
0.5 0 0 0.5 10 522 cm
/Fm1 Do
Q
q
0 -0.5 0.5 0 300 822 cm
/Fm1 Do
Q
q
-0.5 0 0 -0.5 210 442 cm
/Fm1 Do
Q
q
0 0.5 -0.5 0 600 242 cm
/Fm1 Do
Q";
    private const string PinCropped =
        @"q
0.60606 0 0 0.60606 -8.18 497.76 cm
/Fm1 Do
Q
q
0 -0.6 0.6 0 276 838 cm
/Fm1 Do
Q
q
-0.60606 0 0 -0.60606 228.18 469.27 cm
/Fm1 Do
Q
q
0 0.6 -0.6 0 624 224 cm
/Fm1 Do
Q";

    private static string SheetContent(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Encoding.ASCII.GetString(doc.GetPage(1).GetContentBytes()).Replace("\r", "").Trim();
    }
}
