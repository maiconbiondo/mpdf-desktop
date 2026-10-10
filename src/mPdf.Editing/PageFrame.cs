using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace mPdf.Editing;

/// FRAME EXIBIDO da página — fonte única de verdade da conversão entre o frame que o App usa (caixas de
/// caractere do PDFium/Docnet, cliques na página renderizada) e o espaço do usuário do iText.
///
/// O PDFium exibe a CropBox RECORTADA pela MediaBox (CPDF_Page: `m_BBox = CropBox ∩ MediaBox`; sem
/// CropBox, ou interseção vazia, vale a MediaBox) e devolve coordenadas relativas ao canto
/// inferior-esquerdo dessa caixa. O iText grava /Rect, /QuadPoints, /InkList, /L e conteúdo no espaço do
/// usuário ABSOLUTO. Numa página recortada no Acrobat/Foxit (origem da CropBox != 0,0) a diferença é a
/// origem — sem conversão, tudo cai deslocado por ela (fix/markup-cropbox e v2.14.0 "Corrigir texto").
/// Compartilhado com mPdf.Signing (InternalsVisibleTo) para o carimbo da assinatura visível.
internal static class PageFrame
{
    /// A caixa exibida (CropBox ∩ MediaBox), normalizada, no espaço do usuário.
    public static Rectangle DisplayedBox(PdfPage page)
    {
        var media = page.GetMediaBox();
        var crop = page.GetCropBox();
        if (crop is null || crop.GetWidth() <= 0 || crop.GetHeight() <= 0) return media;
        var inter = crop.GetIntersection(media);
        return inter is null || inter.GetWidth() <= 0 || inter.GetHeight() <= 0 ? media : inter;
    }

    /// Origem (canto inferior-esquerdo) do frame exibido no espaço do usuário: some-a para ir do frame
    /// exibido ao espaço do usuário; subtraia-a para voltar.
    public static (double X, double Y) DisplayedOrigin(PdfPage page)
    {
        var b = DisplayedBox(page);
        return (b.GetLeft(), b.GetBottom());
    }

    /// Translada TODA a geometria de uma anotação (bbox, Quads, InkStrokes, Line*) por (dx, dy).
    public static AnnotationData Translate(AnnotationData a, double dx, double dy)
    {
        if (dx == 0 && dy == 0) return a;
        PdfPoint P(PdfPoint p) => new(p.XPt + dx, p.YPt + dy);
        return a with
        {
            LeftPt = a.LeftPt + dx, RightPt = a.RightPt + dx,
            BottomPt = a.BottomPt + dy, TopPt = a.TopPt + dy,
            Quads = a.Quads?.Select(q => new PdfQuad(q.LeftPt + dx, q.BottomPt + dy, q.RightPt + dx, q.TopPt + dy)).ToList(),
            InkStrokes = a.InkStrokes?.Select(s => (IReadOnlyList<PdfPoint>)s.Select(P).ToList()).ToList(),
            LineStartPt = a.LineStartPt is { } s0 ? P(s0) : null,
            LineEndPt = a.LineEndPt is { } e0 ? P(e0) : null,
        };
    }
}
