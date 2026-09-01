namespace mPdf.Export;

/// Uma palavra: caracteres adjacentes de uma linha agrupados por proximidade horizontal (gap de X).
public sealed record Word(string Text, double LeftPt, double RightPt, double BottomPt, double TopPt);

/// Uma linha: palavras que compartilham a mesma faixa vertical (Y), ordenadas por X (esquerda→direita).
public sealed record Line(IReadOnlyList<Word> Words, double BottomPt, double TopPt)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

/// Um parágrafo: linhas consecutivas cujo espaçamento vertical entre elas é "normal" (mesmo bloco de
/// texto); um salto maior inicia um novo parágrafo.
public sealed record Paragraph(IReadOnlyList<Line> Lines)
{
    public string Text => string.Join(" ", Lines.Select(l => l.Text));
}

/// Primitivas de layout PURAS (sem I/O, sem OpenXML) — chars→palavras→linhas→parágrafos. Reusadas
/// pelo `DocxExporter` (Task 1) e, na Task 2, pela detecção de tabela do `XlsxExporter` (que consome
/// as `Line`s daqui para procurar fronteiras de coluna). Toda a matemática assume a convenção de
/// `ExportChar`: pontos PDF, origem no canto inferior esquerdo (TopPt > BottomPt).
public static class LayoutAnalysis
{
    /// Limiar de sobreposição vertical para 2 caracteres pertencerem à MESMA linha: a interseção
    /// entre a faixa [BottomPt,TopPt] do caractere candidato e a faixa da linha corrente precisa
    /// cobrir mais da METADE da altura do caractere candidato. 0.5 é deliberadamente permissivo o
    /// bastante para tolerar pequenas variações de baseline entre glifos (acentos, ascendentes/
    /// descendentes) dentro da MESMA linha tipográfica, mas rígido o bastante para separar linhas
    /// adjacentes de texto normal (que não se sobrepõem verticalmente de jeito nenhum, overlap = 0).
    private const double LineOverlapFraction = 0.5;

    /// Limiar de gap horizontal para iniciar uma NOVA palavra: se a distância entre o fim de um
    /// caractere e o início do próximo exceder `WordGapFactor × largura-média-de-caractere-da-linha`,
    /// é um espaço entre palavras, não um espaçamento normal entre glifos adjacentes (kerning/
    /// espaçamento intra-palavra é tipicamente próximo de 0, um espaço real é comparável ou maior
    /// que a largura de um caractere). 1.0 escolhido para ficar claramente entre "colado" (~0) e
    /// "espaço real" (tipicamente ≥ largura de 1 caractere) — testes sintéticos usam gaps bem acima
    /// (palavra nova) ou bem abaixo (mesma palavra) desse limiar para não ficar em zona cinzenta.
    private const double WordGapFactor = 1.0;

    /// Limiar de salto vertical entre linhas para iniciar um NOVO parágrafo: se o espaço em branco
    /// entre o fundo de uma linha e o topo da próxima exceder `ParagraphGapFactor × altura-média-de-
    /// linha`, é uma quebra de parágrafo (espaçamento extra deliberado), não o espaçamento normal
    /// entre linhas consecutivas de um mesmo bloco (leading padrão, tipicamente uma fração pequena
    /// da altura da fonte). 0.6 escolhido para ficar acima do leading normal mas bem abaixo de um
    /// espaçamento de parágrafo típico — testes sintéticos usam gaps pequenos (mesma linha/parágrafo)
    /// ou vários múltiplos da altura de linha (novo parágrafo) para não ficar em zona cinzenta.
    private const double ParagraphGapFactor = 0.6;

    /// Agrupa os caracteres de uma página em linhas (por faixa de Y) e, dentro de cada linha, em
    /// palavras (por gap de X). Ordem de leitura: topo→base (maior TopPt primeiro), esquerda→direita
    /// dentro de cada linha.
    public static IReadOnlyList<Line> DetectLines(ExportPage page)
    {
        if (page.Chars.Count == 0) return Array.Empty<Line>();

        // Ordena por centro vertical decrescente: topo da página primeiro (maior Y primeiro), ordem
        // de leitura natural para um documento ocidental.
        var ordered = page.Chars
            .Select((c, i) => (Char: c, OriginalIndex: i))
            .OrderByDescending(t => (t.Char.TopPt + t.Char.BottomPt) / 2.0)
            .ToList();

        var lineBuckets = new List<List<ExportChar>>();
        var lineBands = new List<(double Bottom, double Top)>();

        foreach (var (ch, _) in ordered)
        {
            double charHeight = ch.TopPt - ch.BottomPt;
            int matchedLine = -1;

            for (int i = 0; i < lineBands.Count; i++)
            {
                var (bandBottom, bandTop) = lineBands[i];
                bool matches;
                if (charHeight > 0)
                {
                    // Char de altura normal: casa com a linha se a interseção vertical cobrir mais da
                    // metade da sua altura (regra original). A altura serve para ESTABELECER a banda de
                    // referência da linha.
                    double overlap = Math.Min(ch.TopPt, bandTop) - Math.Max(ch.BottomPt, bandBottom);
                    matches = overlap > LineOverlapFraction * charHeight;
                }
                else
                {
                    // Char de altura ~0 (ESPAÇO extraído pelo PDFium, BottomPt == TopPt no baseline):
                    // não tem faixa vertical própria para "sobrepor", então o guard de altura NÃO pode
                    // excluí-lo da linha. Casa com a linha cuja banda-Y já estabelecida (pelos glifos de
                    // altura normal) CONTÉM o ponto vertical do char — assim o espaço fica na linha certa,
                    // na posição X certa, em vez de virar uma "linha" própria e sumir do texto.
                    double y = ch.BottomPt; // == ch.TopPt
                    matches = y >= bandBottom && y <= bandTop;
                }
                if (matches)
                {
                    matchedLine = i;
                    break;
                }
            }

            if (matchedLine >= 0)
            {
                lineBuckets[matchedLine].Add(ch);
            }
            else
            {
                lineBuckets.Add(new List<ExportChar> { ch });
                lineBands.Add((ch.BottomPt, ch.TopPt)); // banda de referência = 1º char desta linha
            }
        }

        // As linhas foram descobertas na ordem em que o 1º caractere de cada uma apareceu na
        // varredura topo→base — já é a ordem de leitura correta (a 1ª linha descoberta é a mais alta).
        var lines = new List<Line>();
        foreach (var bucket in lineBuckets)
        {
            var sortedChars = bucket.OrderBy(c => c.LeftPt).ToList();
            var words = GroupWords(sortedChars);
            double bottom = bucket.Min(c => c.BottomPt);
            double top = bucket.Max(c => c.TopPt);
            lines.Add(new Line(words, bottom, top));
        }

        return lines;
    }

    /// Agrupa caracteres JÁ ordenados por X (de uma única linha) em palavras. Dois separadores:
    /// (1) um ESPAÇO extraído pelo PDFium — separador DEFINITIVO: o PDFium emite o espaço entre
    /// palavras como um glifo de ALTURA ZERO (`Char == ' '` e `TopPt == BottomPt`, no baseline);
    /// onde o motor de extração viu esse espaço há fronteira de palavra, independentemente da
    /// métrica de gap (o defeito original perdia esses espaços). E (2) um gap horizontal grande
    /// (heurística `WordGapFactor`) — a rede de segurança para quando NÃO há espaço extraído (dois
    /// glifos afastados sem espaço entre eles). O espaço extraído manda; o gap é secundário. A
    /// qualificação por altura zero é deliberada: casa exatamente o sinal real do PDFium e não
    /// reinterpreta glifos de espaço de altura NORMAL (que, tendo largura, já são tratados pelo gap).
    /// A largura média (base do limiar de gap) considera só os glifos visíveis (ignora os espaços)
    /// para não distorcer a métrica.
    private static IReadOnlyList<Word> GroupWords(IReadOnlyList<ExportChar> lineChars)
    {
        if (lineChars.Count == 0) return Array.Empty<Word>();

        var glyphs = lineChars.Where(c => c.Char != ' ').ToList();
        double avgCharWidth = glyphs.Count > 0
            ? glyphs.Average(c => c.RightPt - c.LeftPt)
            : lineChars.Average(c => c.RightPt - c.LeftPt);
        double gapThreshold = avgCharWidth * WordGapFactor;

        var words = new List<Word>();
        var current = new List<ExportChar>();

        foreach (var ch in lineChars)
        {
            bool isExtractedSpace = ch.Char == ' ' && ch.TopPt <= ch.BottomPt; // espaço de altura ~0
            if (isExtractedSpace)
            {
                // Espaço extraído: fecha a palavra corrente; o espaço em si não entra em palavra nenhuma.
                if (current.Count > 0)
                {
                    words.Add(BuildWord(current));
                    current = new List<ExportChar>();
                }
                continue;
            }

            if (current.Count > 0)
            {
                double gap = ch.LeftPt - current[^1].RightPt;
                if (gap > gapThreshold)
                {
                    words.Add(BuildWord(current));
                    current = new List<ExportChar>();
                }
            }
            current.Add(ch);
        }

        if (current.Count > 0) words.Add(BuildWord(current));

        return words;
    }

    private static Word BuildWord(IReadOnlyList<ExportChar> chars)
    {
        string text = new string(chars.Select(c => c.Char).ToArray());
        return new Word(
            text,
            chars.Min(c => c.LeftPt),
            chars.Max(c => c.RightPt),
            chars.Min(c => c.BottomPt),
            chars.Max(c => c.TopPt));
    }

    /// Agrupa linhas (já na ordem de leitura topo→base, ex. saída de `DetectLines`) em parágrafos,
    /// por salto vertical entre linhas consecutivas.
    public static IReadOnlyList<Paragraph> GroupParagraphs(IReadOnlyList<Line> lines)
    {
        if (lines.Count == 0) return Array.Empty<Paragraph>();

        var paragraphs = new List<Paragraph>();
        var current = new List<Line> { lines[0] };

        for (int i = 1; i < lines.Count; i++)
        {
            var previous = lines[i - 1];
            var line = lines[i];

            double previousHeight = previous.TopPt - previous.BottomPt;
            double lineHeight = line.TopPt - line.BottomPt;
            double avgHeight = (previousHeight + lineHeight) / 2.0;

            double gap = previous.BottomPt - line.TopPt; // espaço em branco entre as 2 linhas

            if (avgHeight > 0 && gap > ParagraphGapFactor * avgHeight)
            {
                paragraphs.Add(new Paragraph(current));
                current = new List<Line>();
            }
            current.Add(line);
        }
        paragraphs.Add(new Paragraph(current));

        return paragraphs;
    }
}
