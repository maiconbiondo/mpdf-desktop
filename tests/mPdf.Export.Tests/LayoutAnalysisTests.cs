using mPdf.Export;

namespace mPdf.Export.Tests;

/// Testes das primitivas puras de `LayoutAnalysis` (chars→palavras→linhas→parágrafos) — Plano 16,
/// Task 1. Entrada sintética determinística (ver `TestFixtures`), sem OCR/PDFium.
public class LayoutAnalysisTests
{
    // --- palavras (gap de X) -------------------------------------------------------------------

    [Fact]
    public void GroupWords_SmallGap_StaysOneWord()
    {
        // charWidth=6 -> WordGapFactor(1.0) * avgCharWidth(6) = 6pt de limiar. Gap de 1pt (bem
        // abaixo do limiar) não deveria quebrar a palavra.
        var abc = TestFixtures.Run("abc", left: 0, bottom: 0);
        var def = TestFixtures.Run("def", left: abc[^1].RightPt + 1.0, bottom: 0);
        var page = TestFixtures.Page(0, 200, 200, abc, def);

        var lines = LayoutAnalysis.DetectLines(page);

        Assert.Single(lines);
        Assert.Single(lines[0].Words);
        Assert.Equal("abcdef", lines[0].Words[0].Text);
    }

    [Fact]
    public void GroupWords_LargeGap_SplitsIntoTwoWords()
    {
        // Mesmo limiar (6pt); gap de 20pt (bem acima) deveria quebrar em 2 palavras.
        var hello = TestFixtures.Run("hello", left: 0, bottom: 0);
        var world = TestFixtures.Run("world", left: hello[^1].RightPt + 20.0, bottom: 0);
        var page = TestFixtures.Page(0, 200, 200, hello, world);

        var lines = LayoutAnalysis.DetectLines(page);

        Assert.Single(lines);
        Assert.Equal(2, lines[0].Words.Count);
        Assert.Equal("hello", lines[0].Words[0].Text);
        Assert.Equal("world", lines[0].Words[1].Text);
    }

    [Fact]
    public void GroupWords_ExplicitZeroHeightSpace_SeparatesWordsAndDoesNotBecomeItsOwnLine()
    {
        // Cenário REAL do PDFium: o caractere de ESPAÇO entre palavras é extraído com ALTURA ZERO
        // (BottomPt == TopPt, no baseline da linha) e sem gap horizontal apreciável entre os glifos
        // vizinhos ("Pelo" colado em "presente"). ANTES da correção o espaço de altura zero nunca
        // casava com nenhuma linha (virava "linha" própria) e as palavras saíam GRUDADAS
        // ("Pelopresente"). DEPOIS: o espaço casa com a banda-Y da linha e separa as palavras.
        var pelo = TestFixtures.Run("Pelo", left: 0, bottom: 100, charWidth: 6, charHeight: 10); // banda [100,110]
        double afterPelo = pelo[^1].RightPt; // 24
        // Espaço de ALTURA ZERO no baseline da linha (y = 100, dentro da banda [100,110]).
        var space = new ExportChar(' ', afterPelo, 100, afterPelo + 3, 100);
        // "presente" começa logo após o espaço, COLADO (gap ~0 -> a heurística sozinha não separaria).
        var presente = TestFixtures.Run("presente", left: afterPelo + 3, bottom: 100, charWidth: 6, charHeight: 10);

        var page = TestFixtures.Page(0, 400, 400, pelo, new[] { space }, presente);

        var lines = LayoutAnalysis.DetectLines(page);

        // O espaço NÃO vira uma linha própria: tudo numa única linha de texto.
        Assert.Single(lines);
        // As duas palavras saem SEPARADAS (não "Pelopresente").
        Assert.Equal(2, lines[0].Words.Count);
        Assert.Equal("Pelo", lines[0].Words[0].Text);
        Assert.Equal("presente", lines[0].Words[1].Text);
        // E o texto da linha tem o espaço entre elas.
        Assert.Equal("Pelo presente", lines[0].Text);
    }

    // --- linhas + parágrafos (salto vertical) ---------------------------------------------------

    [Fact]
    public void DetectLinesAndGroupParagraphs_TwoCloseLinesPlusOneDistantLine_FormsTwoParagraphs()
    {
        // Linha A (topo) e linha B logo abaixo (gap 5pt < limiar 6pt = 0.6 * altura-de-linha 10pt):
        // mesmo parágrafo. Linha C bem mais abaixo (gap 25pt > 6pt): novo parágrafo.
        var lineA = TestFixtures.Run("Primeira", left: 0, bottom: 100, charHeight: 10); // banda [100,110]
        var lineB = TestFixtures.Run("Segunda", left: 0, bottom: 85, charHeight: 10);   // banda [85,95] -> gap A.bottom(100)-B.top(95)=5
        var lineC = TestFixtures.Run("Terceira", left: 0, bottom: 50, charHeight: 10);  // banda [50,60] -> gap B.bottom(85)-C.top(60)=25

        var page = TestFixtures.Page(0, 400, 400, lineA, lineB, lineC);

        var lines = LayoutAnalysis.DetectLines(page);
        Assert.Equal(3, lines.Count);
        Assert.Equal("Primeira", lines[0].Text);
        Assert.Equal("Segunda", lines[1].Text);
        Assert.Equal("Terceira", lines[2].Text);

        var paragraphs = LayoutAnalysis.GroupParagraphs(lines);

        Assert.Equal(2, paragraphs.Count);
        Assert.Equal(2, paragraphs[0].Lines.Count);
        Assert.Equal("Primeira Segunda", paragraphs[0].Text);
        Assert.Single(paragraphs[1].Lines);
        Assert.Equal("Terceira", paragraphs[1].Text);
    }

    [Fact]
    public void DetectLines_CharsOnSameYBand_FormOneLine()
    {
        var left = TestFixtures.Run("abc", left: 0, bottom: 0, charHeight: 10);
        var right = TestFixtures.Run("def", left: 100, bottom: 0, charHeight: 10); // mesma banda vertical
        var page = TestFixtures.Page(0, 400, 400, left, right);

        var lines = LayoutAnalysis.DetectLines(page);

        Assert.Single(lines);
        Assert.Equal(2, lines[0].Words.Count); // gap horizontal grande entre "abc" e "def" -> 2 palavras
    }

    [Fact]
    public void DetectLines_EmptyPage_ReturnsEmpty()
    {
        var page = new ExportPage(0, 400, 400, Array.Empty<ExportChar>());

        var lines = LayoutAnalysis.DetectLines(page);

        Assert.Empty(lines);
    }

    [Fact]
    public void GroupParagraphs_EmptyLines_ReturnsEmpty()
    {
        Assert.Empty(LayoutAnalysis.GroupParagraphs(Array.Empty<Line>()));
    }
}
