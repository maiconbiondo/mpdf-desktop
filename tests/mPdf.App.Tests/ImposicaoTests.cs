using System.Collections.Generic;
using System.Linq;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

// Plano 25 (Task 1): testes headless do motor de imposição (N-up, livreto, escala, orientação).
public class ImposicaoTests
{
    private static readonly TamanhoPt A4Retrato = new(595, 842);
    private static readonly TamanhoPt A4Paisagem = new(842, 595);

    private static List<TamanhoPt> PaginasRetrato(int n) => Enumerable.Range(0, n).Select(_ => A4Retrato).ToList();

    [Fact]
    public void NUp1_UmaPagina_PreencheAFolha()
    {
        var r = Imposicao.Calcular(A4Retrato, PaginasRetrato(1), new LayoutImpressao { NUp = 1 });
        var c = Assert.Single(r);
        Assert.Equal(0, c.Folha);
        Assert.Equal(0, c.PaginaFonte);
        Assert.Equal(0, c.Rotacao);
        Assert.Equal(595, c.Largura, 3);
        Assert.Equal(842, c.Altura, 3);
        Assert.Equal(0, c.X, 3);
        Assert.Equal(0, c.Y, 3);
    }

    [Fact]
    public void NUp4_OitoPaginas_DuasFolhas_GradeCerta()
    {
        var r = Imposicao.Calcular(A4Retrato, PaginasRetrato(8), new LayoutImpressao { NUp = 4 });
        Assert.Equal(8, r.Count);
        Assert.Equal(2, r.Select(x => x.Folha).Distinct().Count());
        Assert.Equal(4, r.Count(x => x.Folha == 0));
        Assert.Equal(4, r.Count(x => x.Folha == 1));

        // célula = 297.5 × 421; página retrato em célula retrato -> sem rotação, ajuste preserva proporção.
        var p0 = r[0]; // folha 0, célula (0,0)
        Assert.Equal(0, p0.X, 3);
        Assert.Equal(0, p0.Y, 3);
        var p3 = r[3]; // folha 0, célula (c=1,r=1) na ordem horizontal
        Assert.True(p3.X > 297 && p3.X < 298, $"esperava X~297.5, veio {p3.X}");
        Assert.True(p3.Y > 420 && p3.Y < 422, $"esperava Y~421, veio {p3.Y}");
        Assert.Equal(0, r[4].Folha == 1 ? 0 : -1); // página 4 começa na folha 1
        Assert.Equal(1, r[4].Folha);
    }

    [Fact]
    public void NUp2_SegueOrientacaoDaFolha()
    {
        // Folha retrato -> grade 1 coluna × 2 linhas: as duas páginas empilhadas (Y diferente, X igual).
        var ret = Imposicao.Calcular(A4Retrato, PaginasRetrato(2), new LayoutImpressao { NUp = 2 });
        Assert.Equal(ret[0].X, ret[1].X, 3);
        Assert.True(ret[1].Y > ret[0].Y);

        // Folha paisagem -> grade 2 colunas × 1 linha: lado a lado (X diferente, Y igual).
        var pais = Imposicao.Calcular(A4Paisagem, PaginasRetrato(2), new LayoutImpressao { NUp = 2 });
        Assert.Equal(pais[0].Y, pais[1].Y, 3);
        Assert.True(pais[1].X > pais[0].X);
    }

    [Fact]
    public void Ordem_HorizontalVsVertical_TrocaOPreenchimento()
    {
        // Grade 2×2. Horizontal: célula 1 = (c=1,r=0). Vertical: célula 1 = (c=0,r=1).
        var h = Imposicao.Calcular(A4Retrato, PaginasRetrato(4), new LayoutImpressao { NUp = 4, Ordem = OrdemNup.Horizontal });
        var v = Imposicao.Calcular(A4Retrato, PaginasRetrato(4), new LayoutImpressao { NUp = 4, Ordem = OrdemNup.Vertical });
        // pág 1 (índice 1): horizontal vai pra direita (X grande, Y=0); vertical vai pra baixo (X=0, Y grande).
        Assert.True(h[1].X > 200 && h[1].Y < 1);
        Assert.True(v[1].X < 1 && v[1].Y > 200);
    }

    [Fact]
    public void Escala_Modos()
    {
        var pag = new List<TamanhoPt> { A4Retrato };
        // Ajustar numa folha 2x maior -> escala ~2 (destina o dobro).
        var folhaGrande = new TamanhoPt(1190, 1684);
        var ajustar = Imposicao.Calcular(folhaGrande, pag, new LayoutImpressao { NUp = 1, Escala = EscalaModo.Ajustar })[0];
        Assert.Equal(1190, ajustar.Largura, 1);

        // Reduzir NUNCA amplia: na folha grande, fica no tamanho real (595).
        var reduzir = Imposicao.Calcular(folhaGrande, pag, new LayoutImpressao { NUp = 1, Escala = EscalaModo.Reduzir })[0];
        Assert.Equal(595, reduzir.Largura, 1);

        // TamanhoReal = 100% sempre.
        var real = Imposicao.Calcular(folhaGrande, pag, new LayoutImpressao { NUp = 1, Escala = EscalaModo.TamanhoReal })[0];
        Assert.Equal(595, real.Largura, 1);

        // Percentual 50% -> metade.
        var meio = Imposicao.Calcular(folhaGrande, pag, new LayoutImpressao { NUp = 1, Escala = EscalaModo.Percentual, Percentual = 50 })[0];
        Assert.Equal(297.5, meio.Largura, 1);
    }

    [Fact]
    public void PreservaProporcao_EAutoRotaciona()
    {
        // Página PAISAGEM (842×595) numa folha RETRATO, N-up=1: Auto gira 90° pra alinhar à célula retrato.
        var r = Imposicao.Calcular(A4Retrato, new List<TamanhoPt> { A4Paisagem }, new LayoutImpressao { NUp = 1 })[0];
        Assert.Equal(90, r.Rotacao);
        // Proporção preservada: a "pegada" girada é 595(L)×842(A) -> ao ajustar na folha 595×842 fica 595×842.
        Assert.Equal(595.0 / 842.0, r.Largura / r.Altura, 3);
    }

    [Fact]
    public void Livreto_QuatroPaginas_OrdemSaddleStitch()
    {
        // 4 páginas, folha paisagem: 1 folha física, 2 faces (frente/verso), 2 células cada.
        var r = Imposicao.Calcular(A4Paisagem, PaginasRetrato(4), new LayoutImpressao { Livreto = true });
        Assert.Equal(4, r.Count);
        Assert.Equal(2, r.Select(x => x.Folha).Distinct().Count());

        // Face 0 (folha 0): esquerda = pág 4 (idx 3), direita = pág 1 (idx 0).
        var face0 = r.Where(x => x.Folha == 0).OrderBy(x => x.X).ToList();
        Assert.Equal(3, face0[0].PaginaFonte); // esquerda
        Assert.Equal(0, face0[1].PaginaFonte); // direita
        // Face 1 (folha 1): esquerda = pág 2 (idx 1), direita = pág 3 (idx 2).
        var face1 = r.Where(x => x.Folha == 1).OrderBy(x => x.X).ToList();
        Assert.Equal(1, face1[0].PaginaFonte);
        Assert.Equal(2, face1[1].PaginaFonte);
    }

    [Fact]
    public void Livreto_SeisPaginas_CompletaComBrancoAteMultiploDe4()
    {
        // 6 páginas -> padded 8 -> 4 faces. As páginas 7 e 8 (índices 6,7) não existem -> células em branco (-1).
        var r = Imposicao.Calcular(A4Paisagem, PaginasRetrato(6), new LayoutImpressao { Livreto = true });
        Assert.Equal(8, r.Count);                       // 4 faces × 2 células
        Assert.Equal(4, r.Select(x => x.Folha).Distinct().Count());
        Assert.Equal(2, r.Count(x => x.PaginaFonte == -1)); // 2 células em branco (8-6)
        // As 6 páginas reais aparecem todas exatamente uma vez.
        var reais = r.Where(x => x.PaginaFonte >= 0).Select(x => x.PaginaFonte).OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(0, 6), reais);
    }
}
