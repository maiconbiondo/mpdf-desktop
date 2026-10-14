using iText.Kernel.Pdf;

namespace mPdf.Editing.Tests;

/// Revisão F2, fix round 1 (A3): o colar só leva a geometria do frame da cópia ao frame atual quando a página de
/// destino é COMPROVADAMENTE a mesma página da cópia. A identidade é a referência indireta (número + geração) do
/// dicionário da página. Esta sonda fixa que as operações do mPDF que reescrevem o documento PRESERVAM essa
/// referência (o iText em modo stamping mantém os números de objeto), e que páginas novas ganham números novos.
public class IdentidadeDaPaginaTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    internal static List<(int Num, int Gen)> Refs(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return Enumerable.Range(1, doc.GetNumberOfPages()).Select(p =>
        {
            var r = doc.GetPage(p).GetPdfObject().GetIndirectReference();
            return (r.GetObjNumber(), r.GetGenNumber());
        }).ToList();
    }

    private static byte[] Tres() => Ed.ExtractPages(Fixtures.ThirtyPages(), new[] { 0, 1, 2 });

    [Fact]
    public void RotatePages_PreservaAsReferencias() => Assert.Equal(Refs(Tres()), Refs(Ed.RotatePages(Tres(), new[] { 0, 2 }, 90)));

    [Fact]
    public void MovePage_PreservaAsReferencias_EmNovaOrdem()
    {
        var antes = Refs(Tres());
        Assert.Equal(new[] { antes[1], antes[0], antes[2] }, Refs(Ed.MovePage(Tres(), 1, 0)));
    }

    [Fact]
    public void DeletePages_PreservaAsRestantes()
    {
        var antes = Refs(Tres());
        Assert.Equal(new[] { antes[1], antes[2] }, Refs(Ed.DeletePages(Tres(), new[] { 0 })));
    }

    [Fact] // páginas inseridas são objetos NOVOS — nunca reaproveitam o número da página excluída
    public void InsertPages_PaginasNovasTemReferenciasNovas()
    {
        var semP0 = Ed.DeletePages(Tres(), new[] { 0 });
        var antes = Refs(Tres());
        var depois = Refs(Ed.InsertPages(semP0, Fixtures.A4(), 0));
        Assert.Equal(new[] { antes[1], antes[2] }, depois.Skip(1));
        Assert.DoesNotContain(depois[0], antes);
    }

    [Fact]
    public void AnotacoesECorrecao_PreservamAsReferencias()
    {
        var a = new AnnotationData { Id = "x", Kind = AnnotationKind.Rectangle, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 50, TopPt = 50, ColorArgb = 0xFFFF0000 };
        var com = Ed.AddAnnotation(Tres(), a);
        Assert.Equal(Refs(Tres()), Refs(com));
        Assert.Equal(Refs(Tres()), Refs(Ed.RemoveAnnotation(com, "x")));
        Assert.Equal(Refs(Tres()), Refs(Ed.AddAnnotation(Ed.RotatePages(com, new[] { 0 }, 90), a with { Id = "y" })));
    }
}
