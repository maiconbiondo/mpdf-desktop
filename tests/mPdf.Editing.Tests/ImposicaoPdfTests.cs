using System.Collections.Generic;
using mPdf.Editing;
using mPdf.Rendering;
using Xunit;

namespace mPdf.Editing.Tests;

// Plano 25: IMPOSIÇÃO em PDF (IPdfEditor.ImporEmFolhas). Verifica contagem/tamanho das folhas, ausência
// de assinatura, e — renderizando o PDF de saída — que o conteúdo é POSICIONADO na região certa da
// folha (não em branco, não no lugar errado), inclusive rotacionado.
public class ImposicaoPdfTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    [Fact]
    public void ImporEmFolhas_ContaFolhas_TamanhoESemAssinatura()
    {
        var col = new List<ColocacaoFolha>
        {
            new(0, 0, 0, 0, 595, 842, 0), // folha 0 = página 0 cheia
            new(1, 1, 0, 0, 595, 842, 0), // folha 1 = página 1 cheia
        };
        var outp = Editor.ImporEmFolhas(Fixtures.ThirtyPages(), col, 595, 842);

        using var r = new PdfDocumentRenderer(outp);
        Assert.Equal(2, r.PageCount);
        var sz = r.GetPageSize(0);
        Assert.Equal(595, sz.WidthPt, 1);
        Assert.Equal(842, sz.HeightPt, 1);
        Assert.False(Editor.HasSignatures(outp));
    }

    [Fact]
    public void ImporEmFolhas_ColocaConteudoNaRegiaoCerta()
    {
        // Página com conteúdo (carimbo) colocada SÓ no quadrante superior-esquerdo (Y do topo).
        var col = new List<ColocacaoFolha> { new(0, 0, 0, 0, 297.5, 421, 0) };
        var outp = Editor.ImporEmFolhas(Fixtures.Carimbo(), col, 595, 842);

        using var r = new PdfDocumentRenderer(outp);
        var pg = r.RenderPage(0, 1.0);
        int supEsq = NaoBranco(pg, 0.00, 0.00, 0.50, 0.50); // onde a página foi colocada
        int infDir = NaoBranco(pg, 0.55, 0.55, 1.00, 1.00); // deve ficar em branco
        Assert.True(supEsq > 50, $"esperava conteúdo no quadrante superior-esquerdo, veio {supEsq}px");
        Assert.True(infDir == 0, $"quadrante inferior-direito deveria estar em branco, veio {infDir}px");
    }

    [Fact]
    public void ImporEmFolhas_Rotacao90_ContinuaVisivelNaRegiaoCerta()
    {
        // A4 (595×842) rotacionada 90° a 50%: pegada 421×297.5 (421/842 = 297.5/595 = 0.5 — consistente),
        // no canto SUPERIOR-ESQUERDO de uma folha 842×842.
        var col = new List<ColocacaoFolha> { new(0, 0, 0, 0, 421, 297.5, 90) };
        var outp = Editor.ImporEmFolhas(Fixtures.Carimbo(), col, 842, 842);

        using var r = new PdfDocumentRenderer(outp);
        var pg = r.RenderPage(0, 1.0);
        int dentro = NaoBranco(pg, 0.00, 0.00, 0.50, 0.36);  // onde a pegada rotacionada cai
        int fora = NaoBranco(pg, 0.55, 0.45, 1.00, 1.00);    // deve ficar em branco
        Assert.True(dentro > 20, $"página rotacionada deveria aparecer no canto sup-esq, veio {dentro}px");
        Assert.True(fora == 0, $"o resto da folha deveria estar em branco, veio {fora}px");
    }

    [Fact]
    public void ImporEmFolhas_ListaVazia_Recusa()
    {
        Assert.Throws<System.ArgumentException>(() => Editor.ImporEmFolhas(Fixtures.A4(), new List<ColocacaoFolha>(), 595, 842));
    }

    [Fact]
    public void ImporEmFolhas_PaginaForaDoDocumento_Recusa()
    {
        // A4 tem 1 página (índice 0); pedir a página 5 é inválido.
        var col = new List<ColocacaoFolha> { new(0, 5, 0, 0, 595, 842, 0) };
        Assert.Throws<System.ArgumentException>(() => Editor.ImporEmFolhas(Fixtures.A4(), col, 595, 842));
    }

    // Conta pixels NÃO-brancos numa região fracionária (0..1) da página renderizada (BGRA).
    private static int NaoBranco(RenderedPage p, double fx0, double fy0, double fx1, double fy1)
    {
        int x0 = (int)(fx0 * p.WidthPx), x1 = (int)(fx1 * p.WidthPx);
        int y0 = (int)(fy0 * p.HeightPx), y1 = (int)(fy1 * p.HeightPx);
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * p.WidthPx + x) * 4;
                if (p.Bgra[i] < 240 || p.Bgra[i + 1] < 240 || p.Bgra[i + 2] < 240) n++;
            }
        return n;
    }
}
