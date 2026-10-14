using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 3: `FormFieldData.WidgetRect` no FRAME EXIBIDO em /Rotate 0/90/180/270 (+ CropBox).
/// Oráculo: o widget tem aparência vermelha; o PDFium a desenha onde o campo está — o retângulo lido tem de
/// coincidir com a caixa dos pixels vermelhos (independente de `DisplayFrame`).
public class WidgetRectRotationTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    // Retângulo do widget no espaço do USUÁRIO (cabe dentro da CropBox 30,40 500x720).
    private static byte[] PageWithField(int rot, bool crop)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            if (crop) page.SetCropBox(new Rectangle(30, 40, 500, 720));
            if (rot != 0) page.SetRotation(rot);
            var form = PdfAcroForm.GetAcroForm(doc, true);
            var field = new TextFormFieldBuilder(doc, "nome").SetWidgetRectangle(new Rectangle(110, 600, 100, 20)).CreateText();
            field.GetFirstFormAnnotation().SetBackgroundColor(ColorConstants.RED);
            field.RegenerateField();
            form.AddField(field, page);
        }
        return ms.ToArray();
    }

    public static IEnumerable<object[]> Frames() => new[]
    {
        new object[] { 0, false }, new object[] { 90, false }, new object[] { 180, false }, new object[] { 270, false },
        new object[] { 0, true }, new object[] { 90, true }, new object[] { 180, true }, new object[] { 270, true },
    };

    [Theory]
    [MemberData(nameof(Frames))]
    public void ReadFormFields_WidgetRect_MatchesWhereThePageRendersTheField(int rot, bool crop)
    {
        var pdf = PageWithField(rot, crop);
        var f = Assert.Single(Editor.ReadFormFields(pdf));
        var r = f.WidgetRect!.Value;

        var blob = Find(Render(pdf), Red);
        Assert.True(blob.Count > 0, "o campo não foi renderizado");
        const double tol = 1.5;
        Assert.InRange(r.LeftPt, blob.L - tol, blob.L + tol);
        Assert.InRange(r.RightPt, blob.R - tol, blob.R + tol);
        Assert.InRange(r.BottomPt, blob.B - tol, blob.B + tol);
        Assert.InRange(r.TopPt, blob.T - tol, blob.T + tol);
    }

    [Theory] // rotação 0: valores inalterados (relativos à origem da CropBox)
    [InlineData(false, 110, 600, 210, 620)]
    [InlineData(true, 80, 560, 180, 580)]
    public void ReadFormFields_Rotation0_Pinned(bool crop, double l, double b, double r, double t)
    {
        var w = Assert.Single(Editor.ReadFormFields(PageWithField(0, crop))).WidgetRect!.Value;
        Assert.Equal(l, w.LeftPt, 0.01); Assert.Equal(b, w.BottomPt, 0.01);
        Assert.Equal(r, w.RightPt, 0.01); Assert.Equal(t, w.TopPt, 0.01);
    }
}
