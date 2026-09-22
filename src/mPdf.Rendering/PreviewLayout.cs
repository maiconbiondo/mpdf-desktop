using System;
using System.Collections.Generic;

namespace mPdf.Rendering;

/// Uma pagina posicionada no empilhamento vertical do preview: indice no PDF, Y do topo (px, antes do
/// scroll), largura/altura de exibicao (px, fit-width).
public readonly record struct PaginaPreviewLayout(int Indice, int YTopo, int Largura, int Altura);

/// Layout PURO das paginas empilhadas (fit-width, com gap) para o preview do Explorer. Sem render:
/// so aritmetica sobre os tamanhos das paginas. AOT-compativel.
public static class PreviewLayout
{
    public static (IReadOnlyList<PaginaPreviewLayout> Paginas, int AlturaTotal) Calcular(
        IReadOnlyList<(double LarguraPt, double AlturaPt)> tamanhos,
        int larguraPainelPx, int margemPx, int gapPx, double maxScale = 3.0)
    {
        var lista = new List<PaginaPreviewLayout>(tamanhos.Count);
        if (tamanhos.Count == 0) return (lista, 0);

        int disponivel = Math.Max(1, larguraPainelPx - 2 * margemPx);
        int y = margemPx;
        for (int i = 0; i < tamanhos.Count; i++)
        {
            var (wPt, hPt) = tamanhos[i];
            double scale = wPt > 0 ? disponivel / wPt : 1.0;
            scale = Math.Clamp(scale, 0.1, maxScale);
            int larg = Math.Max(1, (int)Math.Round(wPt * scale));
            int alt = Math.Max(1, (int)Math.Round(hPt * scale));
            lista.Add(new PaginaPreviewLayout(i, y, larg, alt));
            y += alt + gapPx;
        }
        int total = y - gapPx + margemPx; // remove o ultimo gap, acrescenta a margem inferior
        return (lista, total);
    }
}
