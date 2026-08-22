using System.Linq;
using mPdf.Rendering;
using Xunit;

namespace mPdf.Rendering.Tests;

public class TextPageTests
{
    // fixture-a4.pdf foi gerada por PdfFixture.CreateSimplePdf("Fixture A4 do mPDF - pagina unica")
    // (ver tests/mPdf.Rendering.Tests/Fixtures.cs e o plano 2a, task 1) — NÃO contém "Documento de
    // teste" (esse texto é o default de CreateSimplePdf(), usado em outra fixture do PoC de assinatura).
    [Fact]
    public void GetTextPage_A4Fixture_ContainsKnownText()
    {
        using var r = new PdfDocumentRenderer(Fixtures.A4());
        var page = r.GetTextPage(0);
        Assert.Contains("Fixture A4 do mPDF", page.Text);
    }

    // todo caractere: Left<Right, Bottom<=Top (origem PDF inferior-esquerda), dentro da página 595x842pt.
    // DESCOBERTA da sonda ao vivo: o PDFium devolve caixa de altura ZERO (Bottom==Top) para o espaço
    // (' ') — não há tinta para medir, só o avanço horizontal (Left<Right continua > 0). Caracteres
    // com tinta (inclusive glifos finos como '-') sempre vieram com Bottom<Top estrito nesta fixture.
    // Por isso a checagem de altura usa <= (cobre o caso degenerado real) e a de largura usa < (nunca
    // degenerada, nem no espaço).
    [Fact]
    public void GetTextPage_CharactersHaveSaneGeometry()
    {
        const double tolerance = 2.0;
        using var r = new PdfDocumentRenderer(Fixtures.A4());
        var page = r.GetTextPage(0);

        Assert.NotEmpty(page.Characters);
        foreach (var c in page.Characters)
        {
            Assert.True(c.LeftPt < c.RightPt, $"Left ({c.LeftPt}) deveria ser < Right ({c.RightPt}) para '{c.Char}'");
            Assert.True(c.BottomPt <= c.TopPt, $"Bottom ({c.BottomPt}) deveria ser <= Top ({c.TopPt}) para '{c.Char}'");
            Assert.InRange(c.LeftPt, -tolerance, 595 + tolerance);
            Assert.InRange(c.RightPt, -tolerance, 595 + tolerance);
            Assert.InRange(c.BottomPt, -tolerance, 842 + tolerance);
            Assert.InRange(c.TopPt, -tolerance, 842 + tolerance);
        }

        // materializa antes de afirmar "não-vazio": Assert.All sobre uma sequência vazia passa
        // vaziamente (vacuous pass) — sem isso, um bug que zerasse page.Characters (ou filtrasse
        // todos como espaço) faria a asserção seguinte "passar" sem checar nada.
        var nonSpaces = page.Characters.Where(c => c.Char != ' ').ToList();
        Assert.NotEmpty(nonSpaces);
        Assert.All(nonSpaces,
            c => Assert.True(c.BottomPt < c.TopPt, $"Bottom ({c.BottomPt}) deveria ser < Top ({c.TopPt}) para '{c.Char}' (não-espaço)"));

        // Ancora a origem do eixo Y de forma absoluta (não só "dentro da página"): o texto da
        // fixture é uma única linha de parágrafo, com baseline medida em y≈789,33pt (origem PDF
        // inferior-esquerda). Uma conversão de base errada (ex.: subtrair 595 — a LARGURA da
        // página — em vez de 842, a ALTURA) ainda cairia dentro dos limites frouxos [0,842] do
        // teste acima, mas erraria essa faixa estreita e falharia aqui de forma clara.
        Assert.All(page.Characters, c => Assert.InRange(c.BottomPt, 780, 800));
    }

    [Fact] // prova de disparo da guarda de dispose (exemplar: RenderPage_AfterDispose_ThrowsObjectDisposed)
    public void GetTextPage_AfterDispose_Throws()
    {
        var r = new PdfDocumentRenderer(Fixtures.A4());
        r.Dispose();
        Assert.Throws<ObjectDisposedException>(() => r.GetTextPage(0));
    }
}
