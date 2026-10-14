using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Revisão crítica F1, item E2: caixa de texto tão estreita que nem um glifo + o padding cabe na largura do CONTEÚDO
/// (o caso real: caixa com o conteúdo girado 90/270 — a página foi girada depois — redimensionada até a altura
/// exibida mínima do App, 20 pt, vira 20 pt de LARGURA de texto) fazia o iText devolver LayoutResult NOTHING e
/// `GrowFreeTextBoxToFitText` estourar NullReferenceException crua no lift (fora do que o App captura).
/// Regra: nada cabe = a caixa fica como está (nunca lança); qualquer outra falha sai como PdfEditingException.
public class CaixaEstreitaTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    private static AnnotationData Caixa(double fs) => new()
    {
        Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, Content = "Muito texto aqui", FontSizePt = fs,
        LeftPt = 100, BottomPt = 300, RightPt = 300, TopPt = 360,
    };

    [Theory]
    [InlineData(18, 90)]
    [InlineData(24, 90)]
    [InlineData(36, 90)]
    [InlineData(36, 270)]
    public void ConteudoGirado_RedimensionadoA20ptDeAltura_NaoLanca(double fs, int giro)
    {
        // Colocada em página normal, depois "Girar" (o conteúdo gira junto: ConteudoRotacaoGraus = giro).
        var pdf = Ed.RotatePages(Ed.AddAnnotation(Page(0, false), Caixa(fs)), new[] { 0 }, giro);
        var lida = Ed.ReadAnnotations(pdf).Single();
        Assert.Equal(giro, lida.ConteudoRotacaoGraus);

        // Redimensionar até a altura EXIBIDA mínima do App (20 pt) = 20 pt de largura do texto.
        var nova = lida with { TopPt = lida.BottomPt + 20 };
        var res = Ed.AddAnnotation(Ed.RemoveAnnotation(pdf, "ft"), nova);

        var relida = Ed.ReadAnnotations(res).Single();
        Assert.Equal(giro, relida.ConteudoRotacaoGraus);
        Assert.Equal(fs, relida.FontSizePt);
        Assert.True(relida.RightPt - relida.LeftPt >= nova.RightPt - nova.LeftPt - 0.5, "a caixa não pode encolher");
        Assert.True(relida.TopPt - relida.BottomPt >= 19.5, "a caixa não pode encolher");
    }

    [Theory] // página normal: caixa mais estreita que um glifo + padding — a mesma regra (antes também lançava)
    [InlineData(36, 10)]
    [InlineData(72, 15)]
    public void PaginaNormal_CaixaMaisEstreitaQueUmGlifo_NaoLanca(double fs, double largura)
    {
        var a = Caixa(fs) with { RightPt = 100 + largura };
        var res = Ed.AddAnnotation(Page(0, false), a);
        var lida = Ed.ReadAnnotations(res).Single();
        Assert.Equal(100 + largura, lida.RightPt, 3);
    }
}
