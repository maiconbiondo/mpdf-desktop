using System.Windows;
using mPdf.App.ViewModels;
using mPdf.Rendering;

namespace mPdf.App.Services;

/// Lógica PURA de seleção de texto (sem UI, sem PDFium): dado uma TextPage (Task 2) e dois pontos em
/// PONTOS (âncora e cursor, origem PDF padrão — inferior-esquerda, y cresce pra cima), resolve os
/// índices de caracteres selecionados por ORDEM DE TEXTO (não por interseção geométrica de
/// retângulo) e os retângulos por linha para desenhar o realce.
public static class TextSelection
{
    // Docnet.Core devolve BoundBox em INT (ver PdfDocumentRenderer.GetTextPage) — geometria
    // sub-ponto não é confiável; toda comparação de banda/altura usa esta tolerância.
    private const double ToleranceP = 1.0;

    public readonly record struct SelectionResult(
        int StartIndex, int EndIndex, string Text, IReadOnlyList<Rect> LineRects);

    /// pos em PIXELS DE TELA (origem topo-esquerda) -> ponto em PONTOS (origem PDF, inferior-esquerda).
    /// Reusa PageViewModel.PtToPx (mesma constante de conversão usada em DisplayWidth/DisplayHeight)
    /// em vez de duplicar o número mágico 96/72.
    public static Point ScreenToPagePoint(Point screenPx, double zoom, double pageHeightPt)
    {
        double scale = zoom * PageViewModel.PtToPx;
        if (scale <= 0) return new Point(0, pageHeightPt);
        return new Point(screenPx.X / scale, pageHeightPt - screenPx.Y / scale);
    }

    /// Seleção âncora->cursor: cada ponto é resolvido para o índice de caractere mais próximo (por
    /// ORDEM DE TEXTO), depois o intervalo [min,max] desses dois índices é selecionado — nunca uma
    /// interseção geométrica do retângulo de arrasto com as caixas dos caracteres.
    public static SelectionResult Select(TextPage page, Point anchorPt, Point cursorPt)
    {
        var chars = page.Characters;
        if (chars.Count == 0) return new SelectionResult(0, -1, string.Empty, []);

        int a = HitTestIndex(chars, anchorPt);
        int b = HitTestIndex(chars, cursorPt);
        int start = Math.Min(a, b), end = Math.Max(a, b);

        string text = page.Text.Substring(start, end - start + 1);
        var rects = BuildLineRects(chars, start, end);
        return new SelectionResult(start, end, text, rects);
    }

    // Acha o caractere cuja banda vertical (linha) contém o ponto (ou a mais próxima); dentro da
    // mesma banda, desempata pelo centro horizontal mais próximo. A banda domina o score (peso 1000)
    // porque decidir a LINHA errada troca o texto selecionado por um de outra linha inteira, enquanto
    // errar por um caractere na mesma linha só desloca a borda da seleção em uma posição.
    private static int HitTestIndex(IReadOnlyList<PdfCharacter> chars, Point pt)
    {
        int best = 0;
        double bestScore = double.MaxValue;
        for (int i = 0; i < chars.Count; i++)
        {
            var c = chars[i];
            var (bottom, top) = InkedBand(chars, i);
            double dy = pt.Y < bottom ? bottom - pt.Y : pt.Y > top ? pt.Y - top : 0;
            double midX = (c.LeftPt + c.RightPt) / 2;
            double dx = Math.Abs(pt.X - midX);
            double score = dy * 1000 + dx;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    // Banda vertical "com tinta" pro caractere de índice `index`: se a própria caixa já tem altura
    // (BottomPt<TopPt, além da tolerância), usa ela. Se for um espaço (altura zero — achado do
    // Task 2), busca o glifo com tinta mais próximo NA ORDEM DE TEXTO (antes ou depois, o que vier
    // primeiro) e herda a banda dele — nunca deixa um espaço carregar uma banda de altura zero.
    private static (double Bottom, double Top) InkedBand(IReadOnlyList<PdfCharacter> chars, int index)
    {
        var c = chars[index];
        if (c.TopPt - c.BottomPt >= ToleranceP) return (c.BottomPt, c.TopPt);

        for (int offset = 1; offset < chars.Count; offset++)
        {
            int before = index - offset, after = index + offset;
            if (before >= 0 && chars[before].TopPt - chars[before].BottomPt >= ToleranceP)
                return (chars[before].BottomPt, chars[before].TopPt);
            if (after < chars.Count && chars[after].TopPt - chars[after].BottomPt >= ToleranceP)
                return (chars[after].BottomPt, chars[after].TopPt);
        }
        return (c.BottomPt, c.TopPt); // página inteira sem nenhum glifo com tinta (caso degenerado)
    }

    // Funde os caracteres selecionados [start,end] em retângulos por linha: agrupa por sobreposição
    // de banda vertical (com o espaço já usando a banda herdada acima), então funde left/right/
    // bottom/top de todo caractere consecutivo cuja banda sobrepõe a banda aberta. Uma mudança de
    // banda fecha o retângulo atual e abre outro — é assim que uma seleção multi-linha vira múltiplos
    // retângulos, um por linha.
    private static List<Rect> BuildLineRects(IReadOnlyList<PdfCharacter> chars, int start, int end)
    {
        var rects = new List<Rect>();
        double left = 0, right = 0, bottom = 0, top = 0;
        bool open = false;

        for (int i = start; i <= end; i++)
        {
            var c = chars[i];
            var (b, t) = InkedBand(chars, i);

            if (open && OverlapsBand(bottom, top, b, t))
            {
                left = Math.Min(left, c.LeftPt);
                right = Math.Max(right, c.RightPt);
                bottom = Math.Min(bottom, b);
                top = Math.Max(top, t);
            }
            else
            {
                if (open) rects.Add(new Rect(new Point(left, bottom), new Point(right, top)));
                left = c.LeftPt; right = c.RightPt; bottom = b; top = t;
                open = true;
            }
        }
        if (open) rects.Add(new Rect(new Point(left, bottom), new Point(right, top)));
        return rects;
    }

    private static bool OverlapsBand(double b1, double t1, double b2, double t2) =>
        b1 <= t2 + ToleranceP && b2 <= t1 + ToleranceP;

    /// Sobrecarga pública de BuildLineRects pro trecho INTEIRO de uma lista de caracteres CONTÍGUOS
    /// (já em ordem de texto) — reusada pela busca (Task 5) pra converter SearchHit.Chars em
    /// retângulos de destaque, mesmo algoritmo de banda/fusão de Select acima. Como um SearchHit já
    /// vem só com os PdfCharacter do próprio trecho casado (não a página inteira), InkedBand resolve
    /// a banda de um eventual espaço na borda buscando vizinhos SÓ dentro do próprio trecho — mesma
    /// ressalva de caixa de altura zero do Task 2, aceita para a busca (ledger da Task 5).
    public static IReadOnlyList<Rect> BuildLineRects(IReadOnlyList<PdfCharacter> chars) =>
        chars.Count == 0 ? [] : BuildLineRects(chars, 0, chars.Count - 1);
}
