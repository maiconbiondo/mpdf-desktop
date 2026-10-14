using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace mPdf.Editing.Tests;

/// Revisão F2, fix round 2: página cujo conteúdo usa `/F99` SEM `/F99` em /Resources. O PDFium tolera (desenha com uma
/// fonte padrão; o texto é selecionável), mas o `PdfCanvasProcessor` do iText lança `NullReferenceException` CRUA.
/// Toda consulta do motor que o App chama tem de degradar com segurança (null/false/no-op) ou lançar
/// `PdfEditingException` — nunca uma exceção crua (no App vira "Ocorreu um erro inesperado").
public class FonteAusenteTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    /// A4 com "ANTIGA" em /F99 inexistente (e uma linha sem /AP, para a regeneração ter o que fazer), girada `rot`.
    internal static byte[] PaginaFonteAusente(int rot = 0)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            var stream = page.GetFirstContentStream();
            stream.SetData(System.Text.Encoding.ASCII.GetBytes("BT /F99 18 Tf 100 500 Td (ANTIGA PALAVRA) Tj ET"));
            if (rot != 0) page.SetRotation(rot);
        }
        return ms.ToArray();
    }

    private static readonly PdfQuad Tudo = new(0, 0, 900, 900);

    [Fact] // a sonda que motivou a rodada: o iText lança NRE crua ao processar o conteúdo
    public void Sonda_ITextLancaNreCrua()
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(PaginaFonteAusente())));
        Assert.ThrowsAny<NullReferenceException>(() =>
            new iText.Kernel.Pdf.Canvas.Parser.PdfCanvasProcessor(new iText.Kernel.Pdf.Canvas.Parser.Listener.LocationTextExtractionStrategy())
                .ProcessPageContent(doc.GetPage(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void DirecaoDoTexto_Null(int rot) => Assert.Null(Ed.DirecaoDoTexto(PaginaFonteAusente(rot), 0, new[] { Tudo }));

    [Fact] // Corrigir texto: sonda e aplicação lançam PdfEditingException (o App já captura), nunca a NRE crua
    public void ProbeEApply_PdfEditingException()
    {
        var pdf = PaginaFonteAusente();
        var e1 = Record.Exception(() => Ed.ProbeTextStyle(pdf, 0, new PdfQuad(90, 490, 300, 520)));
        Assert.True(e1 is null or PdfEditingException, $"sonda: {e1?.GetType()}");
        var e2 = Record.Exception(() => Ed.ApplyTextCorrection(pdf, new TextCorrection(0, new PdfQuad(90, 490, 300, 520), "NOVA",
            TextFontFamily.Serif, false, false, 18, 0xFF000000u, 300, 72, 0xFFFFFFFFu)));
        Assert.True(e2 is null or PdfEditingException, $"aplicar: {e2?.GetType()}");
    }

    [Fact] // as demais consultas novas da F1/F2 não processam conteúdo: funcionam normalmente
    public void DemaisConsultas_SemExcecaoCrua()
    {
        var pdf = PaginaFonteAusente();
        Assert.False(Ed.TemConteudoInterativo(pdf));
        Assert.Null(Ed.RegenerarAparenciasAusentes(pdf));
        var a = new AnnotationData { Kind = AnnotationKind.Rectangle, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 50, TopPt = 50 };
        Assert.NotSame(a, Ed.MapearParaOutraVersao(pdf, Ed.RotatePages(pdf, new[] { 0 }, 90), a));
        Assert.Single(Ed.ReadAnnotations(Ed.AddAnnotation(pdf, a)));
    }
}
