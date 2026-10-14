using iText.Commons.Exceptions;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace mPdf.Editing;

/// Revisão F2, fix round 1 (A1) — ver `IPdfEditor.DirecaoDoTexto` em Contract.cs. A direção sai da ORIENTAÇÃO DOS
/// GLIFOS (eixo x de TextMatrix × CTM, o sentido em que o glifo avança), nunca da ordem dos caracteres: texto
/// hebraico/árabe tem a ordem lógica da direita para a esquerda, mas os glifos continuam em pé (0 → null).
internal static class DirecaoDoTexto
{
    public static int? Ler(byte[] pdf, int pageIndex, IReadOnlyList<PdfQuad> quads)
    {
        if (quads.Count == 0) return null;
        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            PdfEditor.ValidatePageIndex(doc, pageIndex);
            var page = doc.GetPage(pageIndex + 1);
            var listener = new Listener(PageFrame.Frame(page), quads);
            new PdfCanvasProcessor(listener).ProcessPageContent(page);
            return listener.Resultado();
        }
        catch (BadPasswordException ex) { throw PdfEditor.WrapPassword(ex); }
        // Revisão F2, fix round 2: conteúdo que o iText não consegue processar (ITextException, ou NRE crua com /F99 ausente de
        // /Resources — o PDFium tolera e o texto é selecionável) = direção desconhecida = em pé (`null`), nunca uma
        // exceção crua no comando de marcação. Página fora do intervalo (ArgumentException) também = null.
        catch (Exception ex) when (PdfEditor.Malformado(ex)) { return null; }
    }

    private sealed class Listener(DisplayFrame frame, IReadOnlyList<PdfQuad> quads) : IEventListener
    {
        private readonly int[] _votos = new int[4]; // 0, 90, 180, 270

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo tri) return;
            foreach (var ch in tri.GetCharacterRenderInfos())
            {
                // Glifo dentro de algum quad (frame EXIBIDO): o meio da baseline, com 1 pt de folga.
                var b = ch.GetBaseline();
                var s = b.GetStartPoint(); var e = b.GetEndPoint();
                var m = frame.ToDisplayed(new PdfPoint((s.Get(0) + e.Get(0)) / 2, (s.Get(1) + e.Get(1)) / 2));
                if (!quads.Any(q => m.XPt >= Math.Min(q.LeftPt, q.RightPt) - 1 && m.XPt <= Math.Max(q.LeftPt, q.RightPt) + 1
                                    && m.YPt >= Math.Min(q.BottomPt, q.TopPt) - 1 && m.YPt <= Math.Max(q.BottomPt, q.TopPt) + 1))
                    continue;
                var t = ch.GetTextMatrix().Multiply(ch.GetGraphicsState().GetCtm());
                if (frame.ContentRotationOfAdvance(t.Get(0), t.Get(1)) is { } r) _votos[r / 90]++;
            }
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

        /// Maioria dos glifos; 0 (em pé) ou nenhum glifo axial = `null`.
        public int? Resultado()
        {
            int melhor = 0;
            for (int k = 1; k < 4; k++) if (_votos[k] > _votos[melhor]) melhor = k;
            return _votos[melhor] == 0 || melhor == 0 ? null : melhor * 90;
        }
    }
}
