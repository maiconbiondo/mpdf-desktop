namespace mPdf.Export;

/// Uma grade tabular detectada por posição: `RowCount` × `ColumnCount` células de texto.
/// `Cells[linha][coluna]` é o texto daquela célula (string vazia se a célula não tem palavra).
/// Determinística: linhas na ordem de leitura (topo→base, herdada de `LayoutAnalysis.DetectLines`),
/// colunas da esquerda→direita (X crescente).
public sealed record TableGrid(int RowCount, int ColumnCount, IReadOnlyList<IReadOnlyList<string>> Cells)
{
    public string Cell(int row, int column) => Cells[row][column];
}

/// Detecção de tabela por POSIÇÃO (heurística, melhor-esforço) — Plano 16, Task 2. Consome as
/// `Line`s que `LayoutAnalysis` (Task 1) já produz (chars→palavras→linhas) e procura fronteiras de
/// COLUNA: posições X de início de palavra compartilhadas por muitas linhas. O objetivo é acertar
/// tabelas LIMPAS e ALINHADAS (relatórios, extratos, listas em grade) e NUNCA quebrar — quando não
/// há estrutura de coluna consistente devolve `null` e o `XlsxExporter` cai no fallback (texto por
/// linha). Não infere tipos nem interpreta bordas/linhas de grade (o PDF não as expõe como texto);
/// é puramente posicional.
public static class TableDetection
{
    /// Tolerância (em pontos PDF) para duas posições X de início de palavra serem consideradas a
    /// MESMA fronteira de coluna. Colunas reais de um relatório têm um pequeno jitter de sub-ponto
    /// entre linhas (arredondamento do PDFium, kerning do 1º glifo); 6pt ≈ a largura de um caractere
    /// de corpo — largo o bastante para tolerar esse jitter, estreito o bastante para não fundir duas
    /// colunas distintas (que num layout tabular ficam dezenas de pontos separadas). Clustering por
    /// single-linkage sobre os X ordenados.
    public const double ColumnClusterTolerancePt = 6.0;

    /// Limiar N (mínimo de linhas que precisam compartilhar uma fronteira de X para ela contar como
    /// COLUNA, e mínimo de linhas-candidatas a virar tabela): `max(3, ceil(totalLinhas / 2))`. Ou
    /// seja, uma coluna precisa aparecer em pelo menos METADE das linhas E em pelo menos 3 linhas.
    /// O piso de 3 evita que 2 linhas que por acaso se alinhem em 2 X's virem uma "tabela" falsa
    /// (um cabeçalho + uma linha não é uma tabela confiável por posição); o "metade" escala para
    /// documentos maiores onde ruído esparso não deve ditar as colunas.
    public static int RowThreshold(int totalLines) => Math.Max(3, (int)Math.Ceiling(totalLines / 2.0));

    /// Conveniência: extrai as linhas da página (via `LayoutAnalysis`) e detecta a tabela.
    public static TableGrid? Detect(ExportPage page) => Detect(LayoutAnalysis.DetectLines(page));

    /// Detecta uma tabela a partir de linhas já extraídas (reuso direto das `Line`s da Task 1).
    /// Devolve `null` quando não há estrutura de coluna consistente (→ fallback do exportador).
    public static TableGrid? Detect(IReadOnlyList<Line> lines)
    {
        // Precisa de pelo menos 2 linhas para haver "várias linhas compartilhando" uma coluna.
        if (lines.Count < 2) return null;

        int n = RowThreshold(lines.Count);

        // 1. Reúne as posições X de INÍCIO (LeftPt) de TODAS as palavras de TODAS as linhas,
        //    lembrando de qual linha cada uma veio (para contar linhas DISTINTAS por cluster).
        var starts = new List<(double X, int LineIndex)>();
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (var word in lines[i].Words)
                starts.Add((word.LeftPt, i));
        }
        if (starts.Count == 0) return null;

        // 2. Agrupa os X em clusters por single-linkage (X ordenados; gap ≤ tolerância = mesmo cluster).
        starts.Sort((a, b) => a.X.CompareTo(b.X));
        var clusters = new List<List<(double X, int LineIndex)>>();
        foreach (var s in starts)
        {
            if (clusters.Count == 0 || s.X - clusters[^1][^1].X > ColumnClusterTolerancePt)
                clusters.Add(new List<(double, int)>());
            clusters[^1].Add(s);
        }

        // 3. Uma fronteira de COLUNA é um cluster compartilhado por ≥N linhas DISTINTAS. A posição da
        //    coluna é a média dos X do cluster (centro robusto contra o jitter).
        var columns = new List<double>();
        foreach (var cluster in clusters)
        {
            int distinctLines = cluster.Select(p => p.LineIndex).Distinct().Count();
            if (distinctLines >= n)
                columns.Add(cluster.Average(p => p.X));
        }
        columns.Sort();

        // 4. Precisa de ≥2 colunas consistentes para ser uma tabela; senão → fallback.
        if (columns.Count < 2) return null;

        // 5. Cada palavra cai na célula (linha, coluna): a coluna é a fronteira mais próxima À
        //    ESQUERDA da palavra (maior X de coluna ≤ LeftPt da palavra, com folga da tolerância).
        //    Palavras que caem na mesma célula são unidas por espaço, preservando a ordem de leitura.
        var cells = new List<IReadOnlyList<string>>(lines.Count);
        foreach (var line in lines)
        {
            var rowText = new string[columns.Count];
            foreach (var word in line.Words)
            {
                int col = 0;
                for (int c = 0; c < columns.Count; c++)
                {
                    if (word.LeftPt + ColumnClusterTolerancePt >= columns[c]) col = c;
                    else break;
                }
                rowText[col] = string.IsNullOrEmpty(rowText[col]) ? word.Text : rowText[col] + " " + word.Text;
            }
            for (int c = 0; c < rowText.Length; c++) rowText[c] ??= string.Empty;
            cells.Add(rowText);
        }

        return new TableGrid(lines.Count, columns.Count, cells);
    }
}
