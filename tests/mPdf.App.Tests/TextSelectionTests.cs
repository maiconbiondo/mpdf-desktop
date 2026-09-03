using System.Collections.Generic;
using System.Windows;
using mPdf.App.Services;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

// Lógica pura de TextSelection (Task 3): sem PDFium, sem UI — TextPage sintética construída à
// mão (mesmo padrão de testabilidade de ComputeAnchoredOffset/IsCurrentDocument no Task 1).
public class TextSelectionTests
{
    // Uma "linha" só: "AB CD" (5 caracteres, índices 0..4), banda de tinta comum [700,712]pt.
    // O espaço (índice 2) replica a DESCOBERTA do Task 2: caixa de altura zero (Bottom==Top),
    // aqui no meio da banda da linha (706), com Left<Right preservando o avanço horizontal.
    private static TextPage SingleLinePage() => new(0,
    [
        new PdfCharacter('A', 100, 700, 108, 712),
        new PdfCharacter('B', 108, 700, 116, 712),
        new PdfCharacter(' ', 116, 706, 124, 706),
        new PdfCharacter('C', 124, 700, 132, 712),
        new PdfCharacter('D', 132, 700, 140, 712),
    ]);

    [Fact] // arrasto sobre a linha, do centro de 'B' ao centro de 'C', seleciona "B C" (índices 1..3)
    public void Select_DragOverLine_SelectsCorrectSubstring()
    {
        var page = SingleLinePage();
        var anchor = new Point(112, 706); // centro de 'B'
        var cursor = new Point(128, 706); // centro de 'C'

        var result = TextSelection.Select(page, anchor, cursor);

        Assert.Equal(1, result.StartIndex);
        Assert.Equal(3, result.EndIndex);
        Assert.Equal("B C", result.Text);
    }

    [Fact] // mesmo arrasto, mas ao contrário (cursor antes da âncora) — o resultado normaliza igual
    public void Select_ReversedDrag_NormalizesToSameRange()
    {
        var page = SingleLinePage();
        var anchor = new Point(128, 706); // centro de 'C'
        var cursor = new Point(112, 706); // centro de 'B'

        var result = TextSelection.Select(page, anchor, cursor);

        Assert.Equal(1, result.StartIndex);
        Assert.Equal(3, result.EndIndex);
        Assert.Equal("B C", result.Text);
    }

    [Fact] // px de tela (origem topo-esquerda) -> pt de página (origem PDF, inferior-esquerda)
    public void ScreenToPagePoint_ConvertsWithYInversion()
    {
        var p1 = TextSelection.ScreenToPagePoint(new Point(96, 0), zoom: 1.0, pageHeightPt: 842);
        Assert.Equal(72.0, p1.X, 3);
        Assert.Equal(842.0, p1.Y, 3);   // topo da tela (y=0px) -> topo da página em pt

        var p2 = TextSelection.ScreenToPagePoint(new Point(192, 192), zoom: 2.0, pageHeightPt: 842);
        Assert.Equal(72.0, p2.X, 3);
        Assert.Equal(770.0, p2.Y, 3);   // 192px / (2.0 * 96/72) = 72pt abaixo do topo
    }

    [Fact] // espaço dentro da seleção herda a banda vertical da linha — nunca emite retângulo de
    // altura zero (contrato do Task 2: caixa do espaço tem Bottom==Top, sem tinta pra medir)
    public void Select_SpaceInSelection_InheritsLineBand_NoZeroHeightRect()
    {
        var page = SingleLinePage();
        var anchor = new Point(112, 706); // 'B'
        var cursor = new Point(128, 706); // 'C' (seleção atravessa o espaço no meio)

        var result = TextSelection.Select(page, anchor, cursor);

        var rect = Assert.Single(result.LineRects);   // uma linha só -> um retângulo fundido
        Assert.True(rect.Height > 0, $"altura deveria ser > 0, foi {rect.Height}");
        Assert.Equal(12.0, rect.Height, 3);            // banda herdada da linha: 712-700
    }

    // Duas linhas: "AB" na banda [700,712], "CD" na banda [687,699] — gap de 1pt entre elas
    // (700-699=1), exatamente NA tolerância de OverlapsBand (ToleranceP=1.0), então ainda se
    // sobrepõem "por tolerância" e contam como banda única. Para forçar duas linhas DISTINTAS de
    // verdade nos testes abaixo, a banda da linha 2 é abaixada mais (gap > 1pt).
    private static TextPage TwoLinesPage() => new(0,
    [
        new PdfCharacter('A', 100, 700, 108, 712),
        new PdfCharacter('B', 108, 700, 116, 712),
        new PdfCharacter('C', 100, 680, 108, 692),   // gap pro fundo da linha 1: 700-692=8pt (>1pt)
        new PdfCharacter('D', 108, 680, 116, 692),
    ]);

    [Fact] // TESE CENTRAL do design de BuildLineRects (agrupar por banda vertical, uma mudança de
    // banda fecha um retângulo e abre outro) — até este fix, 0 testes exercitavam multi-linha.
    public void Select_DragAcrossTwoLines_YieldsTwoLineRects()
    {
        var page = TwoLinesPage();
        var anchor = new Point(104, 706); // centro de 'A' (linha 1)
        var cursor = new Point(112, 686); // centro de 'D' (linha 2)

        var result = TextSelection.Select(page, anchor, cursor);

        Assert.Equal(0, result.StartIndex);
        Assert.Equal(3, result.EndIndex);
        Assert.Equal("ABCD", result.Text);
        Assert.Equal(2, result.LineRects.Count);
    }

    // Duas linhas com entrelinha APERTADA: banda da linha 2 é [687,699], só 1pt abaixo da linha 1
    // ([700,712] -> gap = 700-699 = 1pt). Isso cai DENTRO da tolerância de OverlapsBand (1pt) —
    // então as duas linhas fundem num único retângulo.
    private static TextPage TwoLinesTightLeadingPage() => new(0,
    [
        new PdfCharacter('E', 100, 700, 108, 712),
        new PdfCharacter('F', 108, 700, 116, 712),
        new PdfCharacter('G', 100, 687, 108, 699),   // gap pro fundo da linha 1: 700-699=1pt (<=1pt)
        new PdfCharacter('H', 108, 687, 116, 699),
    ]);

    [Fact] // DEGENERADO documentado (não é bug): a mesma tolerância de 1pt que deixa o espaço
    // herdar a banda da linha vizinha (geometria do Docnet é inteira — ver PdfDocumentRenderer)
    // também impede distinguir duas linhas cuja entrelinha é <=1pt — elas fundem num retângulo só.
    // Este teste fixa esse comportamento ATUAL como aceito, em vez de deixá-lo invisível.
    public void Select_DragAcrossTwoLines_TightLeadingMergesIntoOneRect()
    {
        var page = TwoLinesTightLeadingPage();
        var anchor = new Point(104, 706); // centro de 'E' (linha 1)
        var cursor = new Point(112, 693); // centro de 'H' (linha 2)

        var result = TextSelection.Select(page, anchor, cursor);

        Assert.Equal("EFGH", result.Text);
        Assert.Single(result.LineRects);   // comportamento conhecido/aceito, ver comentário acima
    }
}
