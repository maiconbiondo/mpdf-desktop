using mPdf.Export;

namespace mPdf.Export.Tests;

/// Testes da detecção de tabela por posição (Plano 16, Task 2). Entrada sintética 100% controlada
/// (sem PDFium/OCR), determinística — posições deliberadamente bem acima/abaixo dos limiares para
/// nunca cair em zona cinzenta.
public class TableDetectionTests
{
    [Fact]
    public void Detect_AlignedThreeByThreeGrid_ReturnsGridWithTextsInRightCells()
    {
        // 3 linhas × 3 colunas em X consistentes (0, 100, 200). Bottoms bem separados → 3 linhas
        // distintas. Cada palavra é um run contíguo (uma "célula").
        var page = TestFixtures.Page(0, 612, 792,
            TestFixtures.Run("R1C1", left: 0, bottom: 200),
            TestFixtures.Run("R1C2", left: 100, bottom: 200),
            TestFixtures.Run("R1C3", left: 200, bottom: 200),
            TestFixtures.Run("R2C1", left: 0, bottom: 170),
            TestFixtures.Run("R2C2", left: 100, bottom: 170),
            TestFixtures.Run("R2C3", left: 200, bottom: 170),
            TestFixtures.Run("R3C1", left: 0, bottom: 140),
            TestFixtures.Run("R3C2", left: 100, bottom: 140),
            TestFixtures.Run("R3C3", left: 200, bottom: 140));

        var grid = TableDetection.Detect(page);

        Assert.NotNull(grid);
        Assert.Equal(3, grid!.RowCount);
        Assert.Equal(3, grid.ColumnCount);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal($"R{r + 1}C{c + 1}", grid.Cell(r, c));
    }

    [Fact]
    public void Detect_RunningText_ReturnsNull_ForFallback()
    {
        // 4 linhas de "texto corrido": todas começam em X=0 (única coluna compartilhada), demais
        // palavras em X's que NÃO se alinham entre linhas → nenhuma 2ª coluna consistente.
        var page = TestFixtures.Page(0, 612, 792,
            Concat(TestFixtures.Run("Este", 0, 200), TestFixtures.Run("texto", 40, 200), TestFixtures.Run("corre", 95, 200)),
            Concat(TestFixtures.Run("Outra", 0, 170), TestFixtures.Run("frase", 55, 170), TestFixtures.Run("aqui", 130, 170)),
            Concat(TestFixtures.Run("Mais", 0, 140), TestFixtures.Run("palavras", 40, 140), TestFixtures.Run("soltas", 150, 140)),
            Concat(TestFixtures.Run("Linha", 0, 110), TestFixtures.Run("final", 70, 110)));

        var grid = TableDetection.Detect(page);

        Assert.Null(grid); // sinaliza "sem tabela" → fallback
    }

    [Fact]
    public void Detect_PartialColumnAlignmentBelowThreshold_ReturnsNull_NoFalseTable()
    {
        // 5 linhas: só 2 se alinham numa 2ª coluna (X=100). Limiar N = max(3, ceil(5/2)) = 3, então
        // uma 2ª coluna precisaria de ≥3 linhas → não vira tabela (evita falso-positivo).
        var page = TestFixtures.Page(0, 612, 792,
            Concat(TestFixtures.Run("Nome", 0, 200), TestFixtures.Run("Valor", 100, 200)),
            Concat(TestFixtures.Run("Item", 0, 170), TestFixtures.Run("Preco", 100, 170)),
            TestFixtures.Run("Texto corrido sem coluna", 0, 140),
            TestFixtures.Run("Outra linha corrida", 0, 110),
            TestFixtures.Run("Mais uma", 0, 80));

        var grid = TableDetection.Detect(page);

        Assert.Null(grid); // 2 linhas alinhadas < N=3 → não é tabela
    }

    [Fact]
    public void RowThreshold_IsHalfOfLines_WithFloorOfThree()
    {
        Assert.Equal(3, TableDetection.RowThreshold(3)); // ceil(1.5)=2 → piso 3
        Assert.Equal(3, TableDetection.RowThreshold(5)); // ceil(2.5)=3
        Assert.Equal(5, TableDetection.RowThreshold(10)); // metade
    }

    private static List<ExportChar> Concat(params List<ExportChar>[] runs)
    {
        var all = new List<ExportChar>();
        foreach (var run in runs) all.AddRange(run);
        return all;
    }
}
