using mPdf.Export;

namespace mPdf.Export.Tests;

/// Fabricantes de `ExportChar`/`ExportPage` sintéticos — entrada 100% controlada e determinística
/// (sem OCR/PDFium/ruído), como pede o plano (Task 1, seção TESTES). Convenção: pontos PDF, origem
/// no canto inferior esquerdo (TopPt > BottomPt), mesma de `ExportChar`.
internal static class TestFixtures
{
    /// Gera os caracteres de `text` colados lado a lado (sem gap entre eles — RightPt de um char
    /// == LeftPt do próximo), começando em (`left`,`bottom`), cada char com `charWidth`×`charHeight`.
    /// Usado para representar uma única "palavra" ou um segmento contíguo de texto.
    public static List<ExportChar> Run(string text, double left, double bottom, double charWidth = 6.0, double charHeight = 10.0)
    {
        var chars = new List<ExportChar>();
        double x = left;
        foreach (char c in text)
        {
            chars.Add(new ExportChar(c, x, bottom, x + charWidth, bottom + charHeight));
            x += charWidth;
        }
        return chars;
    }

    public static ExportPage Page(int pageIndex, double widthPt, double heightPt, params IEnumerable<ExportChar>[] runs)
    {
        var all = new List<ExportChar>();
        foreach (var run in runs) all.AddRange(run);
        return new ExportPage(pageIndex, widthPt, heightPt, all);
    }
}
