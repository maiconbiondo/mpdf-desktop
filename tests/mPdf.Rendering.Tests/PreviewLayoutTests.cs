using System.Collections.Generic;
using mPdf.Rendering;
using Xunit;

namespace mPdf.Rendering.Tests;

public class PreviewLayoutTests
{
    [Fact] // vazio -> sem paginas, altura 0
    public void Vazio()
    {
        var (pgs, total) = PreviewLayout.Calcular(new List<(double, double)>(), 800, 8, 8);
        Assert.Empty(pgs);
        Assert.Equal(0, total);
    }

    [Fact] // 1 pagina A4 (595x842pt) em painel 611px, margem 8 -> fit-width em 595px, Y=8
    public void UmaPagina_FitWidth()
    {
        var (pgs, total) = PreviewLayout.Calcular(new[] { (595.0, 842.0) }, 611, 8, 8); // disponivel=611-16=595
        Assert.Single(pgs);
        Assert.Equal(0, pgs[0].Indice);
        Assert.Equal(8, pgs[0].YTopo);
        Assert.Equal(595, pgs[0].Largura);
        Assert.Equal(842, pgs[0].Altura);
        Assert.Equal(842 + 16, total); // pagina + margem topo+baixo
    }

    [Fact] // N paginas iguais empilham com gap; Y acumula
    public void VariasPaginas_YAcumula()
    {
        var t = new List<(double, double)> { (100, 200), (100, 200), (100, 200) };
        var (pgs, total) = PreviewLayout.Calcular(t, 108, 4, 10); // disponivel=100 -> scale 1
        Assert.Equal(3, pgs.Count);
        Assert.Equal(4, pgs[0].YTopo);
        Assert.Equal(4 + 200 + 10, pgs[1].YTopo);           // topo + alt + gap
        Assert.Equal(4 + 2 * (200 + 10), pgs[2].YTopo);
        Assert.Equal(4 + 3 * 200 + 2 * 10 + 4, total);       // margem + 3 alt + 2 gap + margem
    }

    [Fact] // alturas diferentes: Y usa a altura REAL de cada pagina
    public void AlturasDiferentes()
    {
        var t = new List<(double, double)> { (100, 100), (100, 300) };
        var (pgs, _) = PreviewLayout.Calcular(t, 108, 4, 10);
        Assert.Equal(4, pgs[0].YTopo);
        Assert.Equal(4 + 100 + 10, pgs[1].YTopo);
    }

    [Fact] // largura de painel <= margens: disponivel clampa em >=1, nao quebra
    public void LarguraMinima_NaoQuebra()
    {
        var (pgs, _) = PreviewLayout.Calcular(new[] { (100.0, 100.0) }, 4, 8, 8); // 4-16 < 0 -> clamp 1
        Assert.True(pgs[0].Largura >= 1 && pgs[0].Altura >= 1);
    }
}
