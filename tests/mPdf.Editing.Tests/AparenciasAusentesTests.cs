using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Revisão crítica F1, item E9: linhas/setas gravadas até a v2.16 não têm /AP — o PDFium não sintetiza /Line, então
/// continuam INVISÍVEIS (na tela e na impressão) mesmo depois da correção que passou a gravar /AP nas novas.
/// `IPdfEditor.RegenerarAparenciasAusentes` dá o /AP às que não têm, sem o usuário fazer nada (o App chama ao abrir,
/// sem marcar o documento como modificado). `null` = nada a fazer.
public class AparenciasAusentesTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    private static AnnotationData Linha(string id, AnnotationKind kind, double x1, double y1, double x2, double y2) => new()
    {
        Id = id, Kind = kind, PageIndex = 0, ColorArgb = 0xFF0000FF, StrokeWidthPt = 3,
        LeftPt = Math.Min(x1, x2), BottomPt = Math.Min(y1, y2), RightPt = Math.Max(x1, x2), TopPt = Math.Max(y1, y2),
        LineStartPt = new PdfPoint(x1, y1), LineEndPt = new PdfPoint(x2, y2),
    };

    /// O formato da v2.16: sem /AP e com /Rect = envelope dos 2 pontos (sem a margem da espessura/ponta).
    internal static byte[] ComoV216(byte[] pdf)
    {
        using var o = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), new PdfWriter(o)))
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
                foreach (var a in doc.GetPage(p).GetAnnotations().Where(a => PdfName.Line.Equals(a.GetSubtype())))
                {
                    var d = a.GetPdfObject();
                    d.Remove(PdfName.AP);
                    var l = d.GetAsArray(PdfName.L).ToFloatArray();
                    d.Put(PdfName.Rect, new PdfArray(new[] { Math.Min(l[0], l[2]), Math.Min(l[1], l[3]), Math.Max(l[0], l[2]), Math.Max(l[1], l[3]) }));
                }
        return o.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void LinhaESetaDaV216_FicamVisiveis_ComOMesmoDesenhoDaVersaoAtual(int rot)
    {
        var atual = Ed.AddAnnotation(Ed.AddAnnotation(Page(rot, false),
            Linha("ln", AnnotationKind.Line, 100, 100, 250, 180)), Linha("ar", AnnotationKind.Arrow, 300, 300, 420, 220));
        var v216 = ComoV216(atual);
        Assert.Equal(0, Find(Render(v216), Blue).Count); // o defeito: invisíveis

        var regen = Ed.RegenerarAparenciasAusentes(v216);
        Assert.NotNull(regen);
        var esperado = Find(Render(atual), Blue);
        var obtido = Find(Render(regen!), Blue);
        Assert.True(Math.Abs(obtido.Count - esperado.Count) <= 0.02 * esperado.Count, $"px azuis {obtido.Count} x {esperado.Count}");
        Assert.Equal((esperado.L, esperado.B, esperado.R, esperado.T), (obtido.L, obtido.B, obtido.R, obtido.T));

        // A leitura (o que o App seleciona/move) não muda.
        string Desc(byte[] pdf) => string.Join(";", Ed.ReadAnnotations(pdf).OrderBy(a => a.Id).Select(a =>
            $"{a.Id}:{a.Kind}:{a.ColorArgb:X8}:{a.StrokeWidthPt}:{a.LeftPt:F2},{a.BottomPt:F2},{a.RightPt:F2},{a.TopPt:F2}:{a.LineStartPt}:{a.LineEndPt}"));
        Assert.Equal(Desc(v216), Desc(regen!));

        Assert.Null(Ed.RegenerarAparenciasAusentes(regen!)); // idempotente: nada mais a fazer
    }

    [Fact]
    public void NadaAFazer_Null()
    {
        Assert.Null(Ed.RegenerarAparenciasAusentes(Page(0, false)));
        Assert.Null(Ed.RegenerarAparenciasAusentes(Fixtures.Anotada()));
        Assert.Null(Ed.RegenerarAparenciasAusentes(Ed.AddAnnotation(Page(0, false), Linha("ln", AnnotationKind.Line, 10, 10, 90, 90))));
    }

    [Fact] // linha sem cor (/C ausente = borda 0): ganha /AP vazio, continua invisível como sempre foi — sem lançar
    public void LinhaSemCor_NaoLanca()
    {
        var pdf = ComoV216(Ed.AddAnnotation(Page(0, false), Linha("ln", AnnotationKind.Line, 10, 10, 90, 90) with { ColorArgb = null }));
        var regen = Ed.RegenerarAparenciasAusentes(pdf);
        Assert.NotNull(regen);
        Assert.Single(Ed.ReadAnnotations(regen!));
    }

    [Fact] // documento ASSINADO: reescrever invalidaria a assinatura — não mexe (null)
    public void DocumentoAssinado_Null()
    {
        using var o = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(Fixtures.Carimbo())), new PdfWriter(o),
                   new StampingProperties().UseAppendMode()))
        {
            var l = new PdfLineAnnotation(new Rectangle(100, 100, 100, 50), new float[] { 100, 100, 200, 150 });
            l.SetColor(new float[] { 0, 0, 1 });
            doc.GetPage(1).AddAnnotation(l);
        }
        Assert.True(Ed.HasSignatures(o.ToArray()));
        Assert.Null(Ed.RegenerarAparenciasAusentes(o.ToArray()));
    }

    [Fact] // linha de outro programa sem /AP e com /L malformado: ignorada, sem lançar
    public void LinhaMalformada_Ignorada()
    {
        using var o = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(o)))
        {
            var l = new PdfLineAnnotation(new Rectangle(100, 100, 100, 50), new float[] { 100, 100, 200, 150 });
            var arr = new PdfArray(); arr.Add(new PdfNumber(1)); arr.Add(PdfNull.PDF_NULL);
            l.GetPdfObject().Put(PdfName.L, arr);
            doc.AddNewPage(PageSize.A4).AddAnnotation(l);
        }
        Assert.Null(Ed.RegenerarAparenciasAusentes(o.ToArray()));
    }
}
