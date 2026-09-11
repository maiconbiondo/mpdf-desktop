namespace mPdf.Export;

/// Tipos NEUTROS de ENTRADA do módulo `mPdf.Export` (Plano 16, Task 1). Espelham
/// `mPdf.Rendering.PdfCharacter`/`TextPage` (a extração de texto+posições já usada pela
/// seleção/busca — Plano 2b), mas `mPdf.Export` NÃO referencia `mPdf.Rendering`: o `mPdf.App`
/// (raiz de composição, Task 3) mapeia `PdfCharacter` → `ExportChar` campo a campo. Isso mantém
/// `mPdf.Export` isolado (testável com fixtures sintéticos, sem PDFium) e preserva a fronteira
/// AGPL (nenhum tipo de Rendering/Editing/Docnet/iText cruza para este módulo).
///
/// Unidades: todas as coordenadas em PONTOS PDF (1/72 polegada), origem no canto INFERIOR
/// esquerdo da página — mesma convenção de `PdfCharacter` (BottomPt/TopPt, não Y crescente para
/// baixo). `LayoutAnalysis` (mesmo módulo) depende dessa convenção para agrupar linhas/parágrafos.
public sealed record ExportPage(int PageIndex, double WidthPt, double HeightPt, IReadOnlyList<ExportChar> Chars);

/// Um caractere posicionado numa página, em pontos PDF. `LeftPt`/`RightPt` = extensão horizontal;
/// `BottomPt`/`TopPt` = extensão vertical (TopPt > BottomPt — origem embaixo).
public readonly record struct ExportChar(char Char, double LeftPt, double BottomPt, double RightPt, double TopPt);

/// Exportador para Word (.docx) — Task 1. Cada parágrafo detectado por `LayoutAnalysis` vira um
/// `Paragraph` OpenXML; texto simples, sem tentar reproduzir layout/colunas (decisão do usuário,
/// spec seção 2.2). Implementação: `DocxExporter`.
public interface IDocxExporter
{
    byte[] Export(IReadOnlyList<ExportPage> pages);
}

/// Exportador para Excel (.xlsx) — Task 2. Uma aba por página; tabela detectada por posição
/// (`TableDetection`, a partir das `Line`s de `LayoutAnalysis`) vira uma grade de células, ou cai no
/// fallback texto-por-linha (coluna A). Implementação: `XlsxExporter`.
public interface IXlsxExporter
{
    byte[] Export(IReadOnlyList<ExportPage> pages);
}
