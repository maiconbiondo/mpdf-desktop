using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using mPdf.Export;
// Mesma colisão de nomes de DocxExporter.cs: `mPdf.Export.Paragraph` (LayoutAnalysis) vs
// `DocumentFormat.OpenXml.Wordprocessing.Paragraph` (elemento OpenXML).
using WpParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;

namespace mPdf.Export.Tests;

/// Testes de VALIDADE ESTRUTURAL do `.docx` gerado — Plano 16, Task 1: abre o byte[] com
/// `WordprocessingDocument` (OpenXML SDK) e relê os parágrafos/texto esperados, não só "não-vazio".
public class DocxExporterTests
{
    private static IDocxExporter Exporter => new DocxExporter();

    private static List<string> ReadParagraphTexts(byte[] docxBytes)
    {
        using var stream = new MemoryStream(docxBytes);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var body = doc.MainDocumentPart!.Document.Body!;
        return body.Elements<WpParagraph>().Select(p => p.InnerText).ToList();
    }

    [Fact]
    public void Export_TwoParagraphsOnOnePage_DocxHasTwoParagraphsWithExpectedText()
    {
        var lineA = TestFixtures.Run("Primeira", left: 0, bottom: 100, charHeight: 10);
        var lineB = TestFixtures.Run("Segunda", left: 0, bottom: 85, charHeight: 10);
        var lineC = TestFixtures.Run("Terceira", left: 0, bottom: 50, charHeight: 10);
        var page = TestFixtures.Page(0, 612, 792, lineA, lineB, lineC);

        byte[] docx = Exporter.Export(new[] { page });

        var paragraphTexts = ReadParagraphTexts(docx);

        Assert.Equal(2, paragraphTexts.Count);
        Assert.Equal("Primeira Segunda", paragraphTexts[0]);
        Assert.Equal("Terceira", paragraphTexts[1]);
    }

    [Fact]
    public void Export_IsStructurallyValidOpenXmlPackage()
    {
        var line = TestFixtures.Run("Ola mundo", left: 0, bottom: 0, charHeight: 10);
        var page = TestFixtures.Page(0, 612, 792, line);

        byte[] docx = Exporter.Export(new[] { page });

        using var stream = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        Assert.NotNull(doc.MainDocumentPart);
        Assert.NotNull(doc.MainDocumentPart!.Document.Body);
    }

    [Fact]
    public void Export_MultiplePages_InsertsPageBreakBetweenPages()
    {
        var page1Line = TestFixtures.Run("PaginaUm", left: 0, bottom: 0, charHeight: 10);
        var page2Line = TestFixtures.Run("PaginaDois", left: 0, bottom: 0, charHeight: 10);
        var page1 = TestFixtures.Page(0, 612, 792, page1Line);
        var page2 = TestFixtures.Page(1, 612, 792, page2Line);

        byte[] docx = Exporter.Export(new[] { page1, page2 });

        using var stream = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var body = doc.MainDocumentPart!.Document.Body!;

        var breaks = body.Descendants<Break>().Where(b => b.Type is not null && b.Type == BreakValues.Page).ToList();
        Assert.Single(breaks);

        var paragraphTexts = body.Elements<WpParagraph>().Select(p => p.InnerText).ToList();
        Assert.Contains("PaginaUm", paragraphTexts);
        Assert.Contains("PaginaDois", paragraphTexts);
    }

    [Fact]
    public void Export_EmptyPages_ProducesValidDocxWithoutThrowing()
    {
        var page = new ExportPage(0, 612, 792, Array.Empty<ExportChar>());

        byte[] docx = Exporter.Export(new[] { page });

        using var stream = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        Assert.NotNull(doc.MainDocumentPart!.Document.Body);
    }

    [Fact]
    public void Export_NoPages_ProducesValidDocxWithoutThrowing()
    {
        byte[] docx = Exporter.Export(Array.Empty<ExportPage>());

        using var stream = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        Assert.NotNull(doc.MainDocumentPart!.Document.Body);
    }

    [Fact]
    public void Export_ParagraphRun_UsesDefaultReadableFont()
    {
        var line = TestFixtures.Run("Texto", left: 0, bottom: 0, charHeight: 10);
        var page = TestFixtures.Page(0, 612, 792, line);

        byte[] docx = Exporter.Export(new[] { page });

        using var stream = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var run = doc.MainDocumentPart!.Document.Body!.Descendants<Run>().First();
        var runFonts = run.RunProperties?.RunFonts;

        Assert.NotNull(runFonts);
        Assert.Equal("Calibri", runFonts!.Ascii?.Value);
    }
}
