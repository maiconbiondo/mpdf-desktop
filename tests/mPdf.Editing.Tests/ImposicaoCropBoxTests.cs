using System.Text;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using mPdf.Rendering;
using Xunit.Abstractions;
using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Revisão crítica F1, item E8: na rotação 0 a imposição usava `xobj.GetBBox()` (a CropBox CRUA do iText), enquanto
/// o ramo girado usa `PageFrame.DisplayedBox` (CropBox ∩ MediaBox — o que o PDFium exibe e o App mede). Página com
/// CropBox MAIOR que a MediaBox caía encolhida e fora do slot na rotação 0. Agora todas as rotações usam a mesma
/// matriz (DisplayedBox); com CropBox ⊆ MediaBox (o caso comum) a matriz é numericamente a mesma de antes (pin).
public class ImposicaoCropBoxTests
{
    private readonly ITestOutputHelper _out;
    public ImposicaoCropBoxTests(ITestOutputHelper o) => _out = o;

    private static IPdfEditor Ed => PdfEditorFactory.Create();

    /// MediaBox 400×400 inteira pintada de preto; CropBox [0 0 600 600] (o PDFium exibe 400×400).
    private static byte[] CropMaiorQueMedia(int rot)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(new PageSize(400, 400));
            page.SetCropBox(new Rectangle(0, 0, 600, 600));
            if (rot != 0) page.SetRotation(rot);
            new PdfCanvas(page).SetFillColor(ColorConstants.BLACK).Rectangle(0, 0, 400, 400).Fill();
        }
        return ms.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void CropBoxMaiorQueMedia_CaiNoSlotComoExibida(int rot)
    {
        var src = CropMaiorQueMedia(rot);
        using (var r = new PdfDocumentRenderer(src))
        {
            var s = r.GetPageSize(0);
            Assert.Equal((400.0, 400.0), (Math.Round(s.WidthPt), Math.Round(s.HeightPt))); // o que o App mede
        }
        var sheet = Ed.ImporEmFolhas(src, new[] { new ColocacaoFolha(0, 0, 100, 100, 200, 200, 0) }, 400, 400);
        var ink = Find(Render(sheet), Dark);
        _out.WriteLine($"rot {rot}: ({ink.L},{ink.B})-({ink.R},{ink.T}) n={ink.Count}");
        Assert.True(Math.Abs(ink.L - 100) <= 1 && Math.Abs(ink.R - 300) <= 1 && Math.Abs(ink.B - 100) <= 1 && Math.Abs(ink.T - 300) <= 1,
            $"rot {rot}: tinta em ({ink.L},{ink.B})-({ink.R},{ink.T}), esperado (100,100)-(300,300)");
    }

    /// PIN (capturado ANTES da mudança): conteúdo da folha (operadores `cm` com a matriz) para uma página NÃO girada
    /// com CropBox deslocada DENTRO da MediaBox, nas 4 rotações de colocação — tem de continuar byte a byte igual.
    [Fact]
    public void CropDentroDaMedia_MatrizIgualAAntes()
    {
        byte[] src;
        using (var ms = new MemoryStream())
        {
            using (var doc = new PdfDocument(new PdfWriter(ms)))
            {
                var page = doc.AddNewPage(new PageSize(400, 600));
                page.SetCropBox(new Rectangle(30.5f, 40.25f, 330, 500));
                new PdfCanvas(page).SetFillColor(ColorConstants.BLACK).Rectangle(50, 60, 100, 100).Fill();
            }
            src = ms.ToArray();
        }
        var cols = new[]
        {
            new ColocacaoFolha(0, 0, 10.5, 20, 165, 250, 0), new ColocacaoFolha(0, 0, 200, 20, 250, 165, 90),
            new ColocacaoFolha(0, 0, 10, 300, 165, 250, 180), new ColocacaoFolha(0, 0, 200, 300, 250, 165, 270),
        };
        var sheet = Ed.ImporEmFolhas(src, cols, 595, 842);
        using var d = new PdfDocument(new PdfReader(new MemoryStream(sheet)));
        var conteudo = Encoding.ASCII.GetString(d.GetPage(1).GetContentBytes());
        _out.WriteLine(conteudo);
        Assert.Equal(PinConteudo, conteudo.Replace("\r", ""));
    }

    private const string PinConteudo =
        "q\n0.5 0 0 0.5 -4.75 551.88 cm\n/Fm1 Do\nQ\nq\n0 -0.5 0.5 0 179.88 837.25 cm\n/Fm1 Do\nQ\nq\n-0.5 0 0 -0.5 190.25 562.13 cm\n/Fm1 Do\nQ\nq\n0 0.5 -0.5 0 470.13 361.75 cm\n/Fm1 Do\nQ\n";
}
