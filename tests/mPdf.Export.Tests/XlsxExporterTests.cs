using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using mPdf.Export;

namespace mPdf.Export.Tests;

/// Testes de VALIDADE ESTRUTURAL do `.xlsx` gerado — Plano 16, Task 2: abre o byte[] com
/// `SpreadsheetDocument` (OpenXML SDK) e relê as células nas posições esperadas, não só "não-vazio".
public class XlsxExporterTests
{
    private static IXlsxExporter Exporter => new XlsxExporter();

    [Fact]
    public void Export_AlignedGrid_WritesNineCellsInGridPositions()
    {
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

        byte[] xlsx = Exporter.Export(new[] { page });

        using var stream = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var wsPart = GetWorksheet(doc, "Página 1");

        string[] cols = { "A", "B", "C" };
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal($"R{r + 1}C{c + 1}", CellValue(wsPart, $"{cols[c]}{r + 1}"));
    }

    [Fact]
    public void Export_RunningText_FallsBackToOneLinePerRowInColumnA()
    {
        var page = TestFixtures.Page(0, 612, 792,
            Concat(TestFixtures.Run("Este", 0, 200), TestFixtures.Run("texto", 40, 200), TestFixtures.Run("corre", 95, 200)),
            Concat(TestFixtures.Run("Outra", 0, 170), TestFixtures.Run("frase", 55, 170), TestFixtures.Run("aqui", 130, 170)),
            Concat(TestFixtures.Run("Mais", 0, 140), TestFixtures.Run("palavras", 40, 140), TestFixtures.Run("soltas", 150, 140)),
            Concat(TestFixtures.Run("Linha", 0, 110), TestFixtures.Run("final", 70, 110)));

        byte[] xlsx = Exporter.Export(new[] { page });

        using var stream = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var wsPart = GetWorksheet(doc, "Página 1");

        // Cada linha do PDF numa linha, texto inteiro na coluna A; nada na coluna B.
        Assert.Equal("Este texto corre", CellValue(wsPart, "A1"));
        Assert.Equal("Outra frase aqui", CellValue(wsPart, "A2"));
        Assert.Equal("Mais palavras soltas", CellValue(wsPart, "A3"));
        Assert.Equal("Linha final", CellValue(wsPart, "A4"));
        Assert.Null(CellValue(wsPart, "B1"));
    }

    [Fact]
    public void Export_PartialAlignmentBelowThreshold_FallsBack_NoFalseTable()
    {
        var page = TestFixtures.Page(0, 612, 792,
            Concat(TestFixtures.Run("Nome", 0, 200), TestFixtures.Run("Valor", 100, 200)),
            Concat(TestFixtures.Run("Item", 0, 170), TestFixtures.Run("Preco", 100, 170)),
            TestFixtures.Run("Texto corrido sem coluna", 0, 140),
            TestFixtures.Run("Outra linha corrida", 0, 110),
            TestFixtures.Run("Mais uma", 0, 80));

        byte[] xlsx = Exporter.Export(new[] { page });

        using var stream = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var wsPart = GetWorksheet(doc, "Página 1");

        // Fallback: as 2 primeiras linhas ficam inteiras na coluna A (não viram 2 colunas).
        Assert.Equal("Nome Valor", CellValue(wsPart, "A1"));
        Assert.Equal("Item Preco", CellValue(wsPart, "A2"));
        Assert.Equal("Texto corrido sem coluna", CellValue(wsPart, "A3"));
        Assert.Null(CellValue(wsPart, "B1")); // prova: nenhuma 2ª coluna
    }

    [Fact]
    public void Export_MultiplePages_CreatesOneSheetPerPageNamedPagina()
    {
        var page1 = TestFixtures.Page(0, 612, 792, TestFixtures.Run("Um", 0, 100));
        var page2 = TestFixtures.Page(1, 612, 792, TestFixtures.Run("Dois", 0, 100));

        byte[] xlsx = Exporter.Export(new[] { page1, page2 });

        using var stream = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);
        var sheetNames = doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Select(s => s.Name!.Value).ToList();

        Assert.Equal(new[] { "Página 1", "Página 2" }, sheetNames);
        Assert.Equal("Um", CellValue(GetWorksheet(doc, "Página 1"), "A1"));
        Assert.Equal("Dois", CellValue(GetWorksheet(doc, "Página 2"), "A1"));
    }

    [Fact]
    public void Export_NoPages_ProducesValidWorkbookWithoutThrowing()
    {
        byte[] xlsx = Exporter.Export(Array.Empty<ExportPage>());

        using var stream = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(stream, isEditable: false);

        Assert.NotNull(doc.WorkbookPart!.Workbook.Sheets);
        Assert.NotEmpty(doc.WorkbookPart.Workbook.Sheets!.Elements<Sheet>());
    }

    private static WorksheetPart GetWorksheet(SpreadsheetDocument doc, string sheetName)
    {
        var sheet = doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().First(s => s.Name == sheetName);
        return (WorksheetPart)doc.WorkbookPart.GetPartById(sheet.Id!);
    }

    private static string? CellValue(WorksheetPart wsPart, string cellReference)
    {
        var cell = wsPart.Worksheet.Descendants<Cell>().FirstOrDefault(c => c.CellReference == cellReference);
        if (cell is null) return null;
        if (cell.DataType is not null && cell.DataType == CellValues.InlineString)
            return cell.InlineString?.Text?.Text;
        return cell.CellValue?.Text;
    }

    private static List<ExportChar> Concat(params List<ExportChar>[] runs)
    {
        var all = new List<ExportChar>();
        foreach (var run in runs) all.AddRange(run);
        return all;
    }
}
