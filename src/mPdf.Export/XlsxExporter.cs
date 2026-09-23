using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace mPdf.Export;

/// Exportador para Excel (.xlsx) — Plano 16, Task 2. Grava um `SpreadsheetDocument` (OpenXML SDK,
/// MIT — a MESMA lib do `DocxExporter`, sem ClosedXML/SixLabors) inteiramente em memória. Uma ABA
/// por página do PDF, nomeada "Página N" (N em base 1). Para cada página tenta a detecção de tabela
/// por posição (`TableDetection`): se acha uma grade, escreve cada célula na posição (linha, coluna)
/// certa; senão cai no FALLBACK — cada linha do PDF (via `LayoutAnalysis`) vira uma linha da planilha
/// com o texto na coluna A. Nesta v1 todo valor é gravado como texto (`InlineString`), sem inferir
/// tipos numéricos.
public sealed class XlsxExporter : IXlsxExporter
{
    public byte[] Export(IReadOnlyList<ExportPage> pages)
    {
        using var stream = new MemoryStream();

        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            uint sheetId = 1;
            foreach (var page in pages)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                worksheetPart.Worksheet = new Worksheet(sheetData);

                FillSheet(sheetData, page);

                sheets.AppendChild(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = sheetId,
                    Name = $"Página {sheetId}",
                });
                sheetId++;
            }

            // Uma pasta de trabalho válida precisa de ≥1 aba: se não houve páginas, cria uma aba
            // vazia para o arquivo abrir sem corromper.
            if (sheetId == 1)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                worksheetPart.Worksheet = new Worksheet(new SheetData());
                sheets.AppendChild(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = 1,
                    Name = "Página 1",
                });
            }

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    /// Preenche uma aba: grade detectada nas células (linha,coluna), ou fallback texto-por-linha na
    /// coluna A.
    private static void FillSheet(SheetData sheetData, ExportPage page)
    {
        var lines = LayoutAnalysis.DetectLines(page);
        var table = TableDetection.Detect(lines);

        if (table is not null)
        {
            for (int r = 0; r < table.RowCount; r++)
            {
                var row = new Row { RowIndex = (uint)(r + 1) };
                for (int c = 0; c < table.ColumnCount; c++)
                {
                    string text = table.Cell(r, c);
                    if (string.IsNullOrEmpty(text)) continue; // célula vazia: não emite Cell (esparsa)
                    row.AppendChild(TextCell(ColumnName(c) + (r + 1), text));
                }
                sheetData.AppendChild(row);
            }
        }
        else
        {
            // Fallback: cada linha do PDF → uma linha da planilha, texto inteiro na coluna A.
            for (int r = 0; r < lines.Count; r++)
            {
                var row = new Row { RowIndex = (uint)(r + 1) };
                row.AppendChild(TextCell("A" + (r + 1), lines[r].Text));
                sheetData.AppendChild(row);
            }
        }
    }

    private static Cell TextCell(string cellReference, string text)
    {
        // InlineString mantém o texto dentro da própria planilha (sem tabela de strings compartilhada
        // separada) — mais simples e autossuficiente para reler estruturalmente.
        return new Cell
        {
            CellReference = cellReference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }),
        };
    }

    /// Converte um índice de coluna (0→A, 1→B, …, 25→Z, 26→AA) numa letra de coluna de planilha.
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        int n = index;
        do
        {
            name = (char)('A' + (n % 26)) + name;
            n = (n / 26) - 1;
        } while (n >= 0);
        return name;
    }
}
