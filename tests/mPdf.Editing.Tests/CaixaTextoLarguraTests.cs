using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Pedido do usuário: "a caixa de texto está quebrando linha, ela deveria me permitir digitar a linha do tamanho da
/// página". Com `AnnotationData.AjustarLarguraAoTexto` (o App liga ao criar/editar), a caixa ALARGA na direção da
/// leitura até caber a linha mais longa, limitada à borda da página exibida − 18 pt; só o que passa disso quebra e
/// cresce a altura. Oráculos: releitura (`ReadAnnotations`, frame exibido) + render PDFium (a tinta numa linha só).
public class CaixaTextoLarguraTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();
    private const string LinhaLonga = "Esta linha inteira precisa caber sem quebrar nada"; // ~270 pt em 12 pt Helvetica
    private static readonly string TextoEnorme = string.Join(" ", Enumerable.Repeat("palavra", 60)); // > largura da página
    private const double M = 18;

    private static AnnotationData Caixa(string texto, double l, double b, double r, double t, bool ajustar = true, int? giro = null) => new()
    {
        Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, Content = texto, FontSizePt = 12,
        LeftPt = l, BottomPt = b, RightPt = r, TopPt = t, AjustarLarguraAoTexto = ajustar, ConteudoRotacaoGraus = giro,
    };

    private static (AnnotationData Lida, Blob Tinta) Colocar(byte[] pagina, AnnotationData a)
    {
        var pdf = Ed.AddAnnotation(pagina, a);
        var lida = Assert.Single(Ed.ReadAnnotations(pdf));
        Assert.False(lida.AjustarLarguraAoTexto); // pedido, nunca gravado
        return (lida, Find(Render(pdf), Dark));
    }

    private static void Perto(double esperado, double real, double tol, string o) =>
        Assert.True(Math.Abs(esperado - real) <= tol, $"{o}: esperado {esperado:F2} ±{tol}, obtido {real:F2}");

    [Fact] // 100 pt de largura, uma linha de ~270 pt: a caixa alarga para a direita e o texto fica numa linha só
    public void LinhaLonga_CaixaEstreita_AlargaParaADireita_UmaLinha()
    {
        var (a, tinta) = Colocar(Page(0, false), Caixa(LinhaLonga, 100, 700, 200, 760));
        Assert.Equal(100, a.LeftPt, 0.5);
        Assert.Equal(760, a.TopPt, 0.5);
        Assert.Equal(700, a.BottomPt, 0.5); // uma linha cabe nos 60 pt: altura intacta
        Assert.InRange(a.RightPt - a.LeftPt, 200, 595 - M - 100);
        Assert.True(tinta.T - tinta.B < 15, $"o texto quebrou: tinta com {tinta.T - tinta.B:F1} pt de altura");
        Assert.True(tinta.R > 290, $"a linha não chegou à direita da caixa original: tinta até x={tinta.R:F1}");
        Assert.True(tinta.R <= a.RightPt + 0.5, "tinta fora da caixa");
    }

    [Fact] // controle: SEM o pedido (mover/redimensionar) a largura do usuário fica e o texto quebra (comportamento antigo)
    public void SemPedido_MantemLargura_EQuebra()
    {
        var (a, tinta) = Colocar(Page(0, false), Caixa(LinhaLonga, 100, 700, 200, 760, ajustar: false));
        Assert.Equal(200, a.RightPt, 0.5);
        Assert.True(tinta.T - tinta.B > 20, $"esperava várias linhas; tinta com {tinta.T - tinta.B:F1} pt de altura");
    }

    [Fact] // linha maior que a página: alarga até a borda − 18 pt e SÓ ENTÃO quebra e cresce a altura
    public void LinhaMaiorQueAPagina_AlargaAteAMargem_EQuebra()
    {
        var (a, tinta) = Colocar(Page(0, false), Caixa(TextoEnorme, 100, 700, 200, 760));
        Perto(595 - M, a.RightPt, 0.5, "direita na margem");
        Assert.Equal(760, a.TopPt, 0.5);
        Assert.True(a.TopPt - a.BottomPt > 60.5, $"altura não cresceu: {a.TopPt - a.BottomPt:F1}");
        Assert.True(tinta.T - tinta.B > 20, "o texto devia quebrar em várias linhas");
        Assert.True(tinta.R <= 595 - M + 0.5, $"tinta passou da margem: x={tinta.R:F1}");
    }

    [Fact] // nunca estreita uma caixa que o usuário fez mais larga
    public void CaixaJaLarga_TextoCurto_NaoEstreita()
    {
        var (a, _) = Colocar(Page(0, false), Caixa("ok", 100, 700, 500, 760));
        Assert.Equal(500, a.RightPt, 0.5);
    }

    [Fact] // caixa já além do limite (colada perto da borda): não estreita nem passa da página
    public void CaixaAlemDoLimite_FicaComoEsta()
    {
        var (a, _) = Colocar(Page(0, false), Caixa(LinhaLonga, 480, 700, 590, 760));
        Assert.Equal(480, a.LeftPt, 0.5);
        Assert.Equal(590, a.RightPt, 0.5);
    }

    [Fact] // quebras explícitas valem: largura = a linha mais longa; 2 linhas, sem quebra automática
    public void QuebrasExplicitas_LarguraDaLinhaMaisLonga()
    {
        var (a, tinta) = Colocar(Page(0, false), Caixa("curta\n" + LinhaLonga, 100, 700, 200, 760));
        Assert.InRange(a.RightPt, 300, 595 - M - 1);
        Assert.InRange(tinta.T - tinta.B, 15, 30); // exatamente 2 linhas de 12 pt
    }

    [Fact] // revisão M3: espaços no fim da linha não contam — "abc" + 200 espaços não leva a caixa até a margem
    public void EspacosNoFimDaLinha_NaoAlargam()
    {
        var (a, tinta) = Colocar(Page(0, false), Caixa("abc" + new string(' ', 200) + "\nde" + new string(' ', 80), 100, 700, 120, 760));
        Assert.True(a.RightPt < 160, $"espaços finais alargaram a caixa até x={a.RightPt:F1}");
        Assert.True(a.RightPt > 120.5, "\"abc\" não cabe em 20 pt: devia alargar um pouco");
        Assert.InRange(tinta.T - tinta.B, 15, 30); // as 2 linhas, sem quebra automática
    }

    [Fact] // revisão I3: a medida que o App compara (velho × novo) — mesma do alargamento
    public void LarguraDaLinhaMaisLonga_MedeALinhaMaisLonga_NaFormatacao()
    {
        double curta = Ed.LarguraDaLinhaMaisLonga(Caixa("abc", 0, 0, 10, 10));
        double longa = Ed.LarguraDaLinhaMaisLonga(Caixa("abc\n" + LinhaLonga, 0, 0, 10, 10));
        Assert.True(longa > curta + 100, $"{curta:F1} × {longa:F1}");
        Assert.Equal(longa, Ed.LarguraDaLinhaMaisLonga(Caixa(LinhaLonga + "   \nabc", 0, 0, 10, 10)), 0.01); // espaços finais não contam
        Assert.True(Ed.LarguraDaLinhaMaisLonga(Caixa(LinhaLonga, 0, 0, 10, 10) with { FontSizePt = 24 }) > longa * 1.8);
        Assert.Equal(0, Ed.LarguraDaLinhaMaisLonga(Caixa("   ", 0, 0, 10, 10)));
        // a caixa alargada cabe essa medida
        var (a, _) = Colocar(Page(0, false), Caixa(LinhaLonga, 100, 700, 120, 760));
        Assert.True(a.RightPt - a.LeftPt >= Ed.LarguraDaLinhaMaisLonga(Caixa(LinhaLonga, 0, 0, 10, 10)) - 0.01);
    }

    [Fact] // página /Rotate 90 (exibida 842×595): alarga para a direita NA TELA, limite = largura exibida − 18
    public void PaginaGirada90_AlargaNaTela()
    {
        var (a, tinta) = Colocar(Page(90, false), Caixa(LinhaLonga, 100, 400, 200, 460));
        Assert.Equal(100, a.LeftPt, 0.5);
        Assert.Equal(460, a.TopPt, 0.5);
        Assert.InRange(a.RightPt, 300, 842 - M);
        Assert.True(tinta.T - tinta.B < 15, $"o texto quebrou: {tinta.T - tinta.B:F1} pt");
        Assert.True(tinta.R > 290, $"tinta até x={tinta.R:F1}");

        var (b, _) = Colocar(Page(90, false), Caixa(TextoEnorme, 100, 400, 200, 460));
        Perto(842 - M, b.RightPt, 0.5, "direita na margem (exibida)");
    }

    [Fact] // conteúdo girado 90° na tela: o texto corre PARA BAIXO — a caixa alarga para baixo, até y = 18
    public void ConteudoGirado90_AlargaNaDirecaoDaLeitura()
    {
        // exibida 40 de largura × 100 de altura = conteúdo 100 de largura × 40 de altura
        var (a, tinta) = Colocar(Page(0, false), Caixa(LinhaLonga, 300, 600, 340, 700, giro: 90));
        Assert.Equal(90, a.ConteudoRotacaoGraus);
        Assert.Equal(700, a.TopPt, 0.5);        // o começo da linha (topo na tela) fica parado
        Assert.Equal(300, a.LeftPt, 0.5);
        Assert.Equal(340, a.RightPt, 0.5);      // uma linha: a "altura" do conteúdo não cresceu
        Assert.InRange(700 - a.BottomPt, 200, 700 - M);
        Assert.True(tinta.R - tinta.L < 15, $"o texto quebrou: tinta com {tinta.R - tinta.L:F1} pt de largura");
        Assert.True(tinta.B < 590, $"a linha não desceu além da caixa original (base 600): tinta até y={tinta.B:F1}");
        Assert.True(tinta.B >= a.BottomPt - 0.5, "tinta fora da caixa");

        var (b, _) = Colocar(Page(0, false), Caixa(TextoEnorme, 300, 600, 340, 700, giro: 90));
        Perto(M, b.BottomPt, 0.5, "base na margem");
        Assert.True(b.LeftPt < 299.5, "a altura do conteúdo (para a esquerda na tela) devia crescer");
    }
}
