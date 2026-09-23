using mPdf.Rendering;
using Xunit;

namespace mPdf.Rendering.Tests;

public class LruCacheTests
{
    [Fact] // dentro do teto: guarda e devolve
    public void GuardaEDevolve()
    {
        var c = new LruCache<int, string>(2);
        c.Adicionar(1, "a"); c.Adicionar(2, "b");
        Assert.True(c.TryGet(1, out var v) && v == "a");
        Assert.True(c.Contem(2));
        Assert.Equal(2, c.Count);
    }

    [Fact] // passou do teto: evicta a MENOS recente
    public void EvictaMenosRecente()
    {
        var c = new LruCache<int, string>(2);
        c.Adicionar(1, "a"); c.Adicionar(2, "b");
        _ = c.TryGet(1, out _);   // 1 vira o mais recente -> 2 e o menos recente
        c.Adicionar(3, "c");      // estoura -> evicta 2
        Assert.False(c.Contem(2));
        Assert.True(c.Contem(1) && c.Contem(3));
        Assert.Equal(2, c.Count);
    }

    [Fact] // re-adicionar chave existente atualiza sem duplicar
    public void ReadicionarNaoDuplica()
    {
        var c = new LruCache<int, string>(2);
        c.Adicionar(1, "a"); c.Adicionar(1, "a2");
        Assert.Equal(1, c.Count);
        Assert.True(c.TryGet(1, out var v) && v == "a2");
    }

    [Fact] // TryGet de ausente -> false
    public void AusenteFalse() => Assert.False(new LruCache<int, string>(2).TryGet(9, out _));
}
