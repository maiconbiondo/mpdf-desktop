using System.Linq;
using System.Threading;
using mPdf.Rendering;
using Xunit;

namespace mPdf.Rendering.Tests;

public class PdfTextSearchTests
{
    // fixture-30p.pdf foi gerada por um loop `for (i = 1; i <= 30; i++) doc.Add(new
    // Paragraph($"Fixture mPDF - pagina {i} de 30"))` com AreaBreak entre parágrafos (ver
    // docs/superpowers/plans/2026-08-13-plano2a-nucleo-leitor.md, TempMakeFixtures) — logo
    // pageIndex 0 tem "pagina 1", pageIndex 6 tem "pagina 7". "pagina 7" não é substring de
    // nenhuma outra página (ex.: "pagina 27" tem "pagina 2" + "7", não "pagina" + " 7").
    [Fact]
    public void FindAll_ThirtyPageFixture_FindsPagina7OnPageIndex6()
    {
        using var r = new PdfDocumentRenderer(Fixtures.ThirtyPages());

        var hits = PdfTextSearch.FindAll(r, "pagina 7", CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(6, hit.PageIndex);
        Assert.Equal("pagina 7".Length, hit.Length);
        Assert.Equal(hit.Length, hit.Chars.Count);
        // reconstrói o trecho a partir dos PdfCharacter devolvidos e confere contra CharStart/página
        var page = r.GetTextPage(6);
        Assert.Equal("pagina 7", page.Text.Substring(hit.CharStart, hit.Length));
        Assert.Equal("pagina 7", new string(hit.Chars.Select(c => c.Char).ToArray()));
    }

    // Mesma busca, em maiúsculas: prova que a comparação ignora caixa (CompareOptions.IgnoreCase).
    [Fact]
    public void FindAll_UppercaseQuery_IsCaseInsensitive()
    {
        using var r = new PdfDocumentRenderer(Fixtures.ThirtyPages());

        var hits = PdfTextSearch.FindAll(r, "PAGINA 7", CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(6, hit.PageIndex);
    }

    // CAPACIDADE separada de cobertura (guard-rails): nenhuma fixture real tem acento, então a
    // insensibilidade a acento é provada diretamente sobre o núcleo do comparador (FindInText,
    // internal, exposto ao teste via InternalsVisibleTo) com uma string sintética.
    [Fact]
    public void FindInText_AccentInsensitive_PaginaMatchesPaginaAcentuada()
    {
        var hits = PdfTextSearch.FindInText("Esta é a página três.", "pagina").ToList();

        var hit = Assert.Single(hits);
        Assert.Equal("Esta é a ".Length, hit.start);
        Assert.Equal("página".Length, hit.len);
    }

    // Query vazia/só-espaços não deve nem abrir o lock do PDFium: prova positiva de que NENHUMA
    // página é percorrida — o renderer é descartado ANTES da chamada; se FindAll iterasse páginas
    // (via GetTextPage), a chamada lançaria ObjectDisposedException.
    [Fact]
    public void FindAll_WhitespaceQuery_ReturnsEmptyWithoutTouchingPages()
    {
        var r = new PdfDocumentRenderer(Fixtures.ThirtyPages());
        r.Dispose();

        var hits = PdfTextSearch.FindAll(r, "   ", CancellationToken.None);

        Assert.Empty(hits);
    }

    // Token já cancelado antes da primeira página: FindAll deve lançar (honestidade sobre
    // cancelamento) em vez de devolver uma lista parcial/vazia silenciosamente.
    [Fact]
    public void FindAll_PreCancelledToken_ThrowsOperationCanceled()
    {
        using var r = new PdfDocumentRenderer(Fixtures.ThirtyPages());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => PdfTextSearch.FindAll(r, "pagina", cts.Token));
    }

    // Item (a) da Task 1 (Plano 3a): documento com texto de verdade (fixture-a4, 1 página com
    // parágrafo) -> DocumentHasText devolve true (early-exit na própria página 1).
    [Fact]
    public void DocumentHasText_FixtureWithText_ReturnsTrue()
    {
        using var r = new PdfDocumentRenderer(Fixtures.A4());

        Assert.True(PdfTextSearch.DocumentHasText(r, CancellationToken.None));
    }

    // Documento GENUINAMENTE sem texto algum (fixture-sem-texto: 1 página A4 sem nenhum conteúdo de
    // texto) -> DocumentHasText devolve false — é exatamente o sinal que a Task 1 usa pra distinguir
    // "documento digitalizado" de "0 hits normais" na UI de busca.
    [Fact]
    public void DocumentHasText_FixtureWithoutText_ReturnsFalse()
    {
        using var r = new PdfDocumentRenderer(Fixtures.NoText());

        Assert.False(PdfTextSearch.DocumentHasText(r, CancellationToken.None));
    }

    // Mesma disciplina de cancelamento de FindAll: token já cancelado lança ANTES de tocar a
    // primeira página, sem devolver true/false "adivinhado".
    [Fact]
    public void DocumentHasText_PreCancelledToken_ThrowsOperationCanceled()
    {
        using var r = new PdfDocumentRenderer(Fixtures.ThirtyPages());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => PdfTextSearch.DocumentHasText(r, cts.Token));
    }
}
