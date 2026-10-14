namespace mPdf.Editing.Tests;

/// Revisão F2 (A3): `IPdfEditor.MapearParaOutraVersao` leva a geometria do frame EXIBIDO de uma versão do documento
/// ao frame exibido de outra (a página girou entre copiar e colar). Oráculo: a MESMA anotação gravada na versão
/// antiga e relida depois de girar a página (ela gira junto com a página).
public class MapearParaOutraVersaoTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    private static readonly AnnotationData Retangulo = new()
    {
        Kind = AnnotationKind.Rectangle, PageIndex = 0, LeftPt = 60, BottomPt = 700, RightPt = 180, TopPt = 760, ColorArgb = 0xFFFF0000,
    };

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void PaginaGirada_MesmaPosicaoQueAAnotacaoGravadaQueGirouJunto(int rot)
    {
        byte[] antes = Fixtures.A4();
        byte[] depois = Ed.RotatePages(antes, new[] { 0 }, rot);
        var oraculo = Assert.Single(Ed.ReadAnnotations(Ed.RotatePages(Ed.AddAnnotation(antes, Retangulo), new[] { 0 }, rot)));

        var m = Ed.MapearParaOutraVersao(antes, depois, Retangulo);

        Assert.Equal(oraculo.LeftPt, m.LeftPt, 2); Assert.Equal(oraculo.BottomPt, m.BottomPt, 2);
        Assert.Equal(oraculo.RightPt, m.RightPt, 2); Assert.Equal(oraculo.TopPt, m.TopPt, 2);
    }

    [Fact]
    public void ConteudoOrientado_AcompanhaODeltaDeRotacao()
    {
        byte[] antes = Ed.RotatePages(Fixtures.A4(), new[] { 0 }, 90);
        byte[] depois = Ed.RotatePages(antes, new[] { 0 }, 90); // 180
        var caixa = Retangulo with { Kind = AnnotationKind.FreeText, Content = "x", ConteudoRotacaoGraus = 270 };
        Assert.Null(Ed.MapearParaOutraVersao(antes, depois, caixa).ConteudoRotacaoGraus); // 270 + 90 = 0
        Assert.Equal(90, Ed.MapearParaOutraVersao(antes, depois, caixa with { ConteudoRotacaoGraus = null }).ConteudoRotacaoGraus);
        Assert.Null(Ed.MapearParaOutraVersao(antes, depois, Retangulo).ConteudoRotacaoGraus); // sem orientação
    }

    [Fact] // Fix round 1: outra página no índice (p1 girada movida para 0) = sem mapeamento, mesmo com frame diferente
    public void OutraPaginaNoIndice_DevolveAMesmaInstancia()
    {
        byte[] dois = Ed.RotatePages(Ed.ExtractPages(Fixtures.ThirtyPages(), new[] { 0, 1 }), new[] { 1 }, 90);
        Assert.Same(Retangulo, Ed.MapearParaOutraVersao(dois, Ed.MovePage(dois, 1, 0), Retangulo));
        Assert.Same(Retangulo, Ed.MapearParaOutraVersao(dois, Ed.DeletePages(dois, new[] { 0 }), Retangulo));
        // a mesma página, girada: mapeia
        Assert.NotSame(Retangulo, Ed.MapearParaOutraVersao(dois, Ed.RotatePages(dois, new[] { 0 }, 90), Retangulo));
    }

    [Fact]
    public void MesmoFrameOuPaginaAusente_DevolveAMesmaInstancia()
    {
        byte[] a4 = Fixtures.A4();
        Assert.Same(Retangulo, Ed.MapearParaOutraVersao(a4, Ed.AddAnnotation(a4, Retangulo), Retangulo));
        var fora = Retangulo with { PageIndex = 5 };
        Assert.Same(fora, Ed.MapearParaOutraVersao(a4, Ed.RotatePages(a4, new[] { 0 }, 90), fora));
    }
}
