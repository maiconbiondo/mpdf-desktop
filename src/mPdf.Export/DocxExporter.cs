using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
// Alias necessário: `mPdf.Export.Paragraph` (tipo de LayoutAnalysis) colide com
// `DocumentFormat.OpenXml.Wordprocessing.Paragraph` (elemento OpenXML) — mesmo nome, namespaces
// diferentes. `WpParagraph` desambigua o tipo OpenXML sem forçar qualificação total em todo lugar.
using WpParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;

namespace mPdf.Export;

/// Exportador para Word (.docx) — Plano 16, Task 1. Consome `LayoutAnalysis` (chars→linhas→
/// parágrafos) e grava um `WordprocessingDocument` (OpenXML SDK, MIT) inteiramente em memória:
/// cada `Paragraph` detectado vira um `Paragraph` OpenXML com um único `Run` de texto simples —
/// SEM tentar reproduzir layout/colunas do PDF original (decisão do usuário, spec seção 2.2: "texto
/// editável em parágrafos, não um clone da diagramação"). Fonte padrão legível (Calibri 11pt).
/// Páginas do PDF são concatenadas; uma quebra de página (`Break` tipo `Page`) separa cada página
/// da seguinte, exceto a última.
public sealed class DocxExporter : IDocxExporter
{
    /// Fonte padrão: Calibri, legível, disponível em qualquer Windows moderno (fonte padrão do
    /// próprio Word desde 2007) — não tenta inferir a fonte original do PDF.
    private const string DefaultFontName = "Calibri";

    /// Tamanho padrão: 11pt — tamanho de corpo de texto legível comum (padrão do Word moderno),
    /// expresso em meios-pontos pelo formato OpenXML (`FontSize` usa a unidade "half-points").
    private const string DefaultFontSizeHalfPoints = "22"; // 11pt × 2

    public byte[] Export(IReadOnlyList<ExportPage> pages)
    {
        using var stream = new MemoryStream();

        using (var wordDocument = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = wordDocument.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            bool wroteAnyPage = false;

            for (int pageIdx = 0; pageIdx < pages.Count; pageIdx++)
            {
                var page = pages[pageIdx];

                if (wroteAnyPage)
                {
                    // Quebra de página entre páginas do PDF (não obrigatória pelo plano, mas evita
                    // que o texto de páginas distintas se misture num único bloco visual no Word).
                    var breakParagraph = new WpParagraph(new Run(new Break { Type = BreakValues.Page }));
                    body.AppendChild(breakParagraph);
                }

                var lines = LayoutAnalysis.DetectLines(page);
                var paragraphs = LayoutAnalysis.GroupParagraphs(lines);

                foreach (var paragraph in paragraphs)
                {
                    body.AppendChild(BuildOpenXmlParagraph(paragraph.Text));
                }

                wroteAnyPage = true;
            }

            // Documento sem NENHUM parágrafo de texto (todas as páginas vazias): OpenXML exige pelo
            // menos um corpo válido — um `SectionProperties` sozinho já satisfaz o Word, mas para
            // manter o arquivo simples/previsível adicionamos um parágrafo vazio.
            if (!wroteAnyPage || body.Elements<WpParagraph>().Count() == 0)
            {
                body.AppendChild(new WpParagraph());
            }

            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    private static WpParagraph BuildOpenXmlParagraph(string text)
    {
        var runProperties = new RunProperties(
            new RunFonts { Ascii = DefaultFontName, HighAnsi = DefaultFontName },
            new FontSize { Val = DefaultFontSizeHalfPoints });

        var run = new Run(runProperties, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new WpParagraph(run);
    }
}
