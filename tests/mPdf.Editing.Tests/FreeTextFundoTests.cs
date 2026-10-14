using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using iText.IO.Font.Constants;
using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Revisão crítica F1, item E4: num FreeText do Acrobat/PDFBox o /C é o FUNDO da caixa e a cor do texto vem do
/// /DA (`rg`). O mPDF lia /C como cor do TEXTO: mover/editar uma caixa amarela com texto azul pintava o texto de
/// amarelo e o fundo sumia. Regra: texto = /DA rg; /C que não é o espelho da cor do texto (o que o próprio mPDF
/// grava desde a v2.16) = fundo, em `AnnotationData.FillArgb`, que o lift redesenha no /AP. As caixas do mPDF
/// (sem `FillArgb`) continuam byte a byte iguais (pins em AnnotationRotationPinTests/FreeTextFormatoRoundTripTests).
public class FreeTextFundoTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();
    private const uint Amarelo = 0xFFFFFF00, Azul = 0xFF0000FF;

    /// Caixa à moda do Acrobat: /C amarelo (fundo), /DA com texto azul e um /AP que pinta os dois.
    internal static byte[] Acrobat(int rot = 0)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            if (rot != 0) page.SetRotation(rot);
            var r = new Rectangle(100, 600, 200, 50);
            var ft = new PdfFreeTextAnnotation(r, new PdfString("Texto azul"));
            ft.SetName(new PdfString("ft"));
            ft.SetColor(new float[] { 1, 1, 0 });
            ft.GetPdfObject().Put(PdfName.DA, new PdfString("/Helv 12 Tf 0 0 1 rg"));
            var ap = new PdfFormXObject(new Rectangle(0, 0, 200, 50));
            var cv = new PdfCanvas(ap, doc);
            cv.SetFillColor(new DeviceRgb(255, 255, 0)).Rectangle(0, 0, 200, 50).Fill();
            cv.BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 12)
                .SetFillColor(new DeviceRgb(0, 0, 255)).MoveText(4, 34).ShowText("Texto azul").EndText();
            ft.SetNormalAppearance(ap.GetPdfObject());
            page.AddAnnotation(ft);
        }
        return ms.ToArray();
    }

    private static AnnotationData Ler(byte[] pdf) => Ed.ReadAnnotations(pdf).Single(a => a.Id == "ft");

    [Fact]
    public void Acrobat_LeFundoETextoSeparados()
    {
        var lida = Ler(Acrobat());
        Assert.Equal(Azul, lida.ColorArgb);
        Assert.Equal(Amarelo, lida.FillArgb);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void Acrobat_MoverEEditar_MantemFundoAmareloETextoAzul(int rot)
    {
        var pdf = Acrobat(rot);
        var antes = Render(pdf);
        var fundo0 = Find(antes, Yellow);
        Assert.True(fundo0.Count > 8000, $"fixture: fundo {fundo0.Count}");

        pdf = AnnotationContentRotationTests.Lift(pdf, "ft", a => AnnotationContentRotationTests.Mover(a, 10, -10));
        Confere(pdf, fundo0, 10, -10, $"rot {rot} movida");

        pdf = AnnotationContentRotationTests.Lift(pdf, "ft", a => a with { Content = "Outro texto azul" });
        Confere(pdf, fundo0, 10, -10, $"rot {rot} editada");
        var lida = Ler(pdf);
        Assert.Equal((Azul, (uint?)Amarelo), (lida.ColorArgb!.Value, lida.FillArgb));
    }

    private static void Confere(byte[] pdf, Blob fundo0, double dx, double dy, string ctx)
    {
        var p = Render(pdf);
        var fundo = Find(p, Yellow);
        var texto = Find(p, Blue);
        Assert.True(fundo.Count > 0.9 * fundo0.Count, $"{ctx}: fundo amarelo sumiu ({fundo.Count} de {fundo0.Count} px)");
        Assert.True(Math.Abs(fundo.L - (fundo0.L + dx)) <= 1.5 && Math.Abs(fundo.T - (fundo0.T + dy)) <= 1.5,
            $"{ctx}: fundo em ({fundo.L},{fundo.T}), esperado ({fundo0.L + dx},{fundo0.T + dy})");
        Assert.True(texto.Count > 40, $"{ctx}: texto azul sumiu ({texto.Count} px)");
        Assert.True(texto.L >= fundo.L && texto.R <= fundo.R && texto.B >= fundo.B && texto.T <= fundo.T, $"{ctx}: texto fora do fundo");
    }

    [Fact] // /C sem cor no /DA: /C é o fundo, texto preto (default)
    public void Terceiro_CSemCorNoDa_EFundo()
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var ft = new PdfFreeTextAnnotation(new Rectangle(100, 100, 200, 50), new PdfString("x"));
            ft.SetName(new PdfString("ft"));
            ft.GetPdfObject().Put(PdfName.DA, new PdfString("/Helv 10 Tf"));
            ft.SetColor(new float[] { 0, 1, 0 });
            doc.AddNewPage(PageSize.A4).AddAnnotation(ft);
        }
        var lida = Ler(ms.ToArray());
        Assert.Null(lida.ColorArgb);
        Assert.Equal(0xFF00FF00u, lida.FillArgb);
    }

    [Theory] // caixa do mPDF (texto colorido, /C = espelho do /DA): nada de fundo, mesma leitura de antes
    [InlineData(0xFFD32F2Fu)]
    [InlineData(0xFF1D4E89u)]
    [InlineData(0xFF000000u)]
    public void CaixaDoMpdf_CEspelhoDoTexto_SemFundo(uint cor)
    {
        foreach (int rot in new[] { 0, 90 })
        {
            var pdf = Ed.AddAnnotation(Page(rot, false), new AnnotationData
            {
                Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, Content = "Caixa", ColorArgb = cor,
                LeftPt = 100, BottomPt = 300, RightPt = 300, TopPt = 340,
            });
            var lida = Ler(pdf);
            Assert.Equal(cor, lida.ColorArgb);
            Assert.Null(lida.FillArgb);
        }
    }

    [Fact] // a caixa do mPDF COM fundo (FillArgb) faz round-trip e desenha o fundo
    public void CaixaComFundo_RoundTripEDesenha()
    {
        var a = new AnnotationData
        {
            Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, Content = "Caixa", ColorArgb = Azul, FillArgb = Amarelo,
            LeftPt = 100, BottomPt = 300, RightPt = 300, TopPt = 340,
        };
        var pdf = Ed.AddAnnotation(Page(0, false), a);
        var lida = Ler(pdf);
        Assert.Equal((Azul, (uint?)Amarelo), (lida.ColorArgb!.Value, lida.FillArgb));
        var fundo = Find(Render(pdf), Yellow);
        Assert.True(fundo.Count > 0.9 * 200 * 40, $"fundo {fundo.Count}");
        Assert.True(Find(Render(pdf), Blue).Count > 20);
    }
}
