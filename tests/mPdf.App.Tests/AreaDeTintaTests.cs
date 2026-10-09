using mPdf.App.Services;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.App.Tests;

/// Revisão final C1: expansão da área de limpeza até a TINTA real da palavra (páginas escaneadas).
/// Bitmaps sintéticos em escala 1 (1 px = 1 pt), página 400x100 pt.
public class AreaDeTintaTests
{
    private const int W = 400, H = 100;
    private const uint Fundo = 0xFFF8F0D0; // papel amarelado

    private static RenderedPage Pagina()
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 0xD0; px[i + 1] = 0xF0; px[i + 2] = 0xF8; px[i + 3] = 255; }
        return new RenderedPage(W, H, px);
    }

    /// Pinta tinta (cinza bem escuro) no retângulo de pixels [x0,x1) x [y0,y1) (y para baixo).
    private static void Tinta(RenderedPage p, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        { int i = (y * W + x) * 4; p.Bgra[i] = p.Bgra[i + 1] = p.Bgra[i + 2] = 30; }
    }

    /// Palavra de 3 "letras" (lacunas de 3 px) em x 100..130, corpo nas linhas 40..59 (linha de base na
    /// borda inferior da 59 = 40 pt), descendente da 3ª letra até a linha 65; palavra vizinha em x 150..170
    /// (lacuna de 20 px); linha de cima (linhas 10..24) cobrindo as duas palavras.
    private static RenderedPage Cena()
    {
        var p = Pagina();
        Tinta(p, 100, 40, 108, 60); Tinta(p, 111, 40, 119, 60); Tinta(p, 122, 40, 130, 60);
        Tinta(p, 126, 60, 130, 66);  // descendente
        Tinta(p, 150, 40, 170, 60);  // palavra vizinha
        Tinta(p, 100, 10, 170, 25);  // linha de cima
        return p;
    }

    [Fact] // seleção cobre 2/3 da palavra (e só parte da altura): cresce até a tinta inteira, com 1 pt de folga
    public void Expandir_PalavraComLacunas_PegaTintaInteiraSemVizinhas()
    {
        var t = AreaDeTinta.Expandir(Cena(), new PdfQuad(100, 38, 120, 55), H, 1.0, Fundo);
        Assert.NotNull(t);
        Assert.Equal(99, t!.AreaPt.LeftPt, 1);
        Assert.Equal(131, t.AreaPt.RightPt, 1);   // não passa da lacuna grande (vizinha fica)
        Assert.Equal(61, t.AreaPt.TopPt, 1);      // topo da tinta (linha 40) + 1, não a linha de cima
        Assert.Equal(33, t.AreaPt.BottomPt, 1);   // descendente (até a linha 65) - 1
        Assert.Equal(40, t.BaselinePt, 1);        // base das letras sem descendente
        Assert.Equal(60, t.TopoPt, 1);
        Assert.Equal(100, t.LeftPt, 1);
    }

    [Fact] // seleção que invade um pouco a palavra vizinha não a puxa inteira (só a própria seleção fica)
    public void Expandir_SelecaoInvadeVizinha_NaoIncluiVizinha()
    {
        var t = AreaDeTinta.Expandir(Cena(), new PdfQuad(102, 38, 153, 55), H, 1.0, Fundo);
        Assert.NotNull(t);
        // Rodada 2: a área para na borda da tinta vizinha (150) — nem até 171 (vizinha inteira) nem até a
        // borda da seleção (153), que comeria uma fatia da vizinha.
        Assert.Equal(150, t!.AreaPt.RightPt, 1);
        Assert.Equal(99, t.AreaPt.LeftPt, 1);
    }

    [Fact] // a área nunca fica menor que a seleção original
    public void Expandir_NuncaEncolhe()
    {
        var sel = new PdfQuad(90, 20, 140, 70);
        var t = AreaDeTinta.Expandir(Cena(), sel, H, 1.0, Fundo);
        Assert.NotNull(t);
        Assert.True(t!.AreaPt.LeftPt <= sel.LeftPt && t.AreaPt.RightPt >= sel.RightPt
                    && t.AreaPt.BottomPt <= sel.BottomPt && t.AreaPt.TopPt >= sel.TopPt, t.AreaPt.ToString());
    }

    [Fact] // sem tinta na seleção -> null (o chamador mantém a seleção e o probe)
    public void Expandir_SemTinta_Null() =>
        Assert.Null(AreaDeTinta.Expandir(Cena(), new PdfQuad(250, 38, 300, 55), H, 1.0, Fundo));

    [Fact] // tinta encostada na borda da página: a folga é cortada na página
    public void Expandir_NaBorda_ClampaNaPagina()
    {
        var p = Pagina();
        Tinta(p, 0, 0, 12, 20);
        var t = AreaDeTinta.Expandir(p, new PdfQuad(0, 85, 8, 98), H, 1.0, Fundo);
        Assert.NotNull(t);
        Assert.Equal(0, t!.AreaPt.LeftPt, 1);
        Assert.Equal(H, t.AreaPt.TopPt, 1);
        Assert.Equal(13, t.AreaPt.RightPt, 1);
    }

    [Fact] // escala 2 (render 2x do app): mesmos resultados em pontos
    public void Expandir_Escala2_ConvertePraPontos()
    {
        var p1 = Cena();
        var p2 = new RenderedPage(W * 2, H * 2, new byte[W * H * 16]);
        for (int y = 0; y < H * 2; y++) for (int x = 0; x < W * 2; x++)
            Buffer.BlockCopy(p1.Bgra, ((y / 2) * W + x / 2) * 4, p2.Bgra, (y * W * 2 + x) * 4, 4);
        var t = AreaDeTinta.Expandir(p2, new PdfQuad(100, 38, 120, 55), H, 2.0, Fundo);
        Assert.NotNull(t);
        Assert.Equal(99, t!.AreaPt.LeftPt, 1);
        Assert.Equal(131, t.AreaPt.RightPt, 1);
        Assert.Equal(40, t.BaselinePt, 1);
    }
    [Fact] // rodada 2: com a faixa da caixa OCR, linhas coladas acima/abaixo (2 px de folga) não entram
    public void Expandir_ComFaixaOcr_LinhasColadasFicamFora()
    {
        var p = Pagina();
        Tinta(p, 100, 40, 108, 60); Tinta(p, 111, 40, 119, 60); Tinta(p, 122, 40, 130, 60);
        Tinta(p, 126, 60, 130, 66);  // descendente
        Tinta(p, 100, 30, 170, 38);  // descendentes da linha de cima, 2 px acima
        Tinta(p, 100, 68, 170, 80);  // ascendentes da linha de baixo, 2 px abaixo
        var sel = new PdfQuad(100, 30, 120, 55); // seleção do PDFium desce até a linha de baixo

        var semFaixa = AreaDeTinta.Expandir(p, sel, H, 1.0, Fundo);
        var comFaixa = AreaDeTinta.Expandir(p, sel, H, 1.0, Fundo, (BottomPt: 34, TopPt: 60));

        Assert.NotNull(semFaixa);
        // Controle: sem a faixa, a linha vizinha (mais tinta nas colunas da seleção) vira a semente.
        Assert.NotEqual(60, semFaixa!.TopoPt, 1);
        Assert.NotNull(comFaixa);
        Assert.Equal(60, comFaixa!.TopoPt, 1);
        // Rodada 4: a área limpa é a caixa OCR (34..60) ± 0,5 pt — não a seleção (30) nem a linha de cima.
        Assert.Equal(60.5, comFaixa.AreaPt.TopPt, 1);
        Assert.Equal(33.5, comFaixa.AreaPt.BottomPt, 1);
        Assert.Equal(40, comFaixa.BaselinePt, 1);
        Assert.Equal(131, comFaixa.AreaPt.RightPt, 1);
    }
    [Fact] // rodada 3 (minor): sem tinta (scan claro) ou sem render, a área de OCR fica na caixa OCR na vertical —
    // a área do token desce a descida da Helvetica invisível e cortaria as ascendentes da linha de baixo.
    public void Planejar_OcrSemTinta_AreaPresaNaCaixaOcr()
    {
        var probe = new TextStyleProbe("jogo", TextFontFamily.SansSerif, false, false, 20, 0xFF000000u,
            BaselineYPt: 40, LeftXPt: 100, RightXPt: 130, Invisible: true, TokenText: "jogo",
            TokenAreaPt: new PdfQuad(100, 35, 130, 60));
        var tokenArea = probe.TokenAreaPt!.Value; // desce até 35 (descida da Helvetica)

        foreach (var render in new RenderedPage?[] { Pagina(), null })
        {
            var plano = PlanejarCorrecao.Calcular(probe, tokenArea, render, H, 1.0);
            Assert.Equal(40, plano.AreaPt.BottomPt, 1);   // fundo da caixa OCR, não 35
            Assert.Equal(60, plano.AreaPt.TopPt, 1);      // base + tamanho
            Assert.Equal(100, plano.AreaPt.LeftPt, 1);
            Assert.Equal(130, plano.AreaPt.RightPt, 1);
            Assert.Equal(PlanejarCorrecao.SobreposicaoMinimaOcr, plano.MinOverlapRatio);
        }
    }

    [Fact] // controle: texto digital sem tinta continua com a área = seleção
    public void Planejar_DigitalSemTinta_AreaIgualSelecao()
    {
        var probe = new TextStyleProbe("x", TextFontFamily.Serif, false, false, 20, 0xFF000000u, 40, 100, 130, false);
        var sel = new PdfQuad(100, 35, 130, 60);
        Assert.Equal(sel, PlanejarCorrecao.Calcular(probe, sel, Pagina(), H, 1.0).AreaPt);
        Assert.Equal(sel, PlanejarCorrecao.Calcular(probe, sel, null, H, 1.0).AreaPt);
    }
    [Fact] // rodada 4 (C-1): acento solto acima da maiúscula (5 px de folga, mais que a tolerância de crescimento) — a área limpa é a caixa OCR inteira
    // (que inclui o acento); o tamanho é medido pelo corpo (sem o acento).
    public void Expandir_AcentoSoltoSobreMaiuscula_AreaCobreCaixaETopoCorpoSemAcento()
    {
        var p = Pagina();
        Tinta(p, 100, 40, 108, 60); Tinta(p, 111, 40, 119, 60); Tinta(p, 122, 40, 130, 60); // corpo (topo 60 pt)
        Tinta(p, 113, 30, 118, 35);                                                      // acento (topo 70 pt, 5 px acima)
        var t = AreaDeTinta.Expandir(p, new PdfQuad(100, 36, 125, 58), H, 1.0, Fundo, (BottomPt: 40, TopPt: 70));

        Assert.NotNull(t);
        Assert.Equal(70.5, t!.AreaPt.TopPt, 1);   // caixa + 0,5 pt: o acento está dentro
        Assert.Equal(39.5, t.AreaPt.BottomPt, 1);
        Assert.Equal(60, t.TopoCorpoPt, 1);       // corpo: sem o acento
        Assert.Equal(40, t.BaselinePt, 1);
    }
    [Fact] // rodada 5: subtração de retângulos (área menos a caixa OCR vizinha) cobre exatamente a diferença
    public void Subtrair_RetanguloMenosVizinho_CobreADiferenca()
    {
        var p = new PdfQuad(0, 0, 10, 10);
        var pecas = PlanejarCorrecao.Subtrair(p, new PdfQuad(5, 5, 15, 15)).ToList();
        Assert.Equal(75, pecas.Sum(q => (q.RightPt - q.LeftPt) * (q.TopPt - q.BottomPt)), 6);
        Assert.DoesNotContain(pecas, q => q.RightPt > 5 && q.TopPt > 5 && q.LeftPt < 10 && q.BottomPt < 10 && q.LeftPt >= 5 && q.BottomPt >= 5);
        Assert.Equal(new[] { p }, PlanejarCorrecao.Subtrair(p, new PdfQuad(20, 20, 30, 30)).ToArray()); // sem interseção
    }

    [Fact] // rodada 5: com vizinha, a limpeza vira peças sem a caixa dela + a faixa do miolo da palavra
    public void AreasSemVizinhos_TiraCaixaVizinhaEPoeFaixaDoMiolo()
    {
        var probe = new TextStyleProbe("GONÇALVES", TextFontFamily.SansSerif, false, false, 20, 0xFF000000u, 40, 100, 200, true,
            NeighbourBoxesPt: new[] { new PdfQuad(120, 20, 160, 42) });
        var areas = PlanejarCorrecao.AreasSemVizinhos(new PdfQuad(99.5, 39.5, 200, 60.5), probe)!;
        bool Limpa(double x, double y) => areas.Any(q => x >= q.LeftPt && x <= q.RightPt && y >= q.BottomPt && y <= q.TopPt);
        Assert.False(Limpa(140, 41));   // dentro da caixa vizinha (+0,25): não limpa
        Assert.True(Limpa(110, 41));    // fora dela: limpa
        Assert.True(Limpa(140, 50));    // faixa do miolo (base + 15%..65% da caixa)
        Assert.Null(PlanejarCorrecao.AreasSemVizinhos(new PdfQuad(99.5, 39.5, 200, 60.5), probe with { NeighbourBoxesPt = null }));
    }
}
