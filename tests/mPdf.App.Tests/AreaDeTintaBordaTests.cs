using mPdf.App.Services;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.App.Tests;

/// Fix round 2 (revisão de feat/ocr-largura): quem é dono da tinta nas BORDAS da caixa OCR. Render sintético
/// a 2x: "palavras" de letras de 6 px com 2 px entre letras, linhas 80..96 (altura 17 -> lacuna de palavra do
/// perfil = 0,18 x 17 ≈ 3 px). Com 3 px entre duas palavras (itálico, tinta borrada, justificação apertada) o
/// perfil de colunas junta as duas numa "palavra de tinta" só; a regra de borda do fix round 1 decidia por essa
/// palavra juntada e (esquerda) deixava a palavra corrigida inteira visível ou (direita) engolia a próxima.
/// A borda da caixa cai numa coluna em BRANCO aqui — nada a atravessa, então nada pode mudar.
public class AreaDeTintaBordaTests
{
    private const double Scale = 2, HPt = 100;

    private static RenderedPage Pagina(params (int A, int B)[] palavras)
    {
        int w = 700, h = 200;
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        foreach (var (a, b) in palavras)
            for (int x = a; x <= b; x++)
            {
                if ((x - a) % 8 >= 6) continue; // 2 px entre letras
                for (int y = 80; y <= 96; y++) { int k = (y * w + x) * 4; px[k] = px[k + 1] = px[k + 2] = 0; }
            }
        return new RenderedPage(w, h, px);
    }

    [Theory] // borda ESQUERDA: palavra anterior longa, lacuna normal (7 px) e apertada (3 px), com e sem próximo token
    [InlineData(7, true)]
    [InlineData(3, true)]
    [InlineData(7, false)]
    [InlineData(3, false)]
    public void BordaEsquerda_LacunaApertada_LimpaAPalavraInteiraSemEntrarNaAnterior(int lacuna, bool temProximo)
    {
        int antA = 100, antB = 197;               // anterior (98 px)
        int a = antB + 1 + lacuna, b = a + 45;    // a corrigida (46 px)
        int proxA = b + 1 + 8, proxB = proxA + 45;
        var page = temProximo ? Pagina((antA, antB), (a, b), (proxA, proxB)) : Pagina((antA, antB), (a, b));
        var area = new PdfQuad(a / Scale, HPt - 97 / Scale, (b + 1) / Scale, HPt - 80 / Scale);

        var r = AreaDeTinta.Expandir(page, area, HPt, Scale, 0xFFFFFFFF, (area.BottomPt, area.TopPt), false, "ABCDEF",
            (a / Scale, temProximo ? proxA / Scale : null), null);

        Assert.NotNull(r);
        Assert.True(r!.AreaPt.LeftPt <= a / Scale && r.AreaPt.RightPt >= (b + 1) / Scale,
            $"área [{r.AreaPt.LeftPt:F1}..{r.AreaPt.RightPt:F1}] não cobre a tinta [{a / Scale:F1}..{(b + 1) / Scale:F1}]");
        Assert.True(r.AreaPt.LeftPt >= (antB + 1) / Scale, $"área entra na anterior: {r.AreaPt.LeftPt:F1} < {(antB + 1) / Scale:F1}");
        if (temProximo) Assert.True(r.AreaPt.RightPt <= proxA / Scale, $"área entra na próxima: {r.AreaPt.RightPt:F1} > {proxA / Scale:F1}");
        Assert.Equal(a / Scale, r.LeftPt, 0.6);
    }

    [Theory] // corte DIREITO: palavra corrigida longa, a próxima curta logo depois (3 px apertado, 7 px normal)
    [InlineData(3)]
    [InlineData(7)]
    public void BordaDireita_LacunaApertada_NaoEngoleAProxima(int lacuna)
    {
        int a = 100, b = 197;
        int proxA = b + 1 + lacuna, proxB = proxA + 45;
        var page = Pagina((a, b), (proxA, proxB));
        var area = new PdfQuad(a / Scale, HPt - 97 / Scale, (b + 1) / Scale, HPt - 80 / Scale);

        var r = AreaDeTinta.Expandir(page, area, HPt, Scale, 0xFFFFFFFF, (area.BottomPt, area.TopPt), false, "ABCDEFGHIJKL",
            (a / Scale, proxA / Scale), null);

        Assert.NotNull(r);
        Assert.True(r!.AreaPt.LeftPt <= a / Scale && r.AreaPt.RightPt >= (b + 1) / Scale,
            $"área [{r.AreaPt.LeftPt:F1}..{r.AreaPt.RightPt:F1}] não cobre a tinta [{a / Scale:F1}..{(b + 1) / Scale:F1}]");
        Assert.True(r.AreaPt.RightPt <= proxA / Scale, $"área engole a próxima: {r.AreaPt.RightPt:F1} > {proxA / Scale:F1}");
    }

    [Theory] // controle positivo das regras de borda: a caixa corta uma CORRIDA de tinta (sem coluna em branco); o
    // dono é o lado cuja tinta está mais PERTO da corrida (2 px entre letras x 8 px entre palavras)
    [InlineData(true)]   // rabo da anterior -> não limpa, a palavra começa depois
    [InlineData(false)]  // começo desta (a caixa começou 2 px tarde) -> começa no início da corrida
    public void BordaEsquerda_CorridaAtravessando_DonoPeloLadoMaisPerto(bool raboDaAnterior)
    {
        RenderedPage page;
        int borda, b;
        if (raboDaAnterior)
        {
            // anterior [98..199] termina na corrida [194..199] (2 px depois da letra de antes); esta em [208..253].
            page = Pagina((98, 199), (208, 253));
            borda = 198; b = 253;
        }
        else
        {
            // anterior [90..185]; esta [194..253] começa na corrida [194..199]; a caixa OCR começa em 196.
            page = Pagina((90, 185), (194, 253));
            borda = 196; b = 253;
        }
        var area = new PdfQuad(borda / Scale, HPt - 97 / Scale, (b + 1) / Scale, HPt - 80 / Scale);

        var r = AreaDeTinta.Expandir(page, area, HPt, Scale, 0xFFFFFFFF, (area.BottomPt, area.TopPt), false, "ABCDEF",
            (borda / Scale, null), null);

        Assert.NotNull(r);
        if (raboDaAnterior) Assert.True(r!.AreaPt.LeftPt >= 200 / Scale, $"limpa o rabo da anterior: {r.AreaPt.LeftPt:F1}");
        else
        {
            Assert.True(r!.AreaPt.LeftPt <= 194 / Scale, $"não pega o começo da corrida: {r.AreaPt.LeftPt:F1}");
            Assert.True(r.AreaPt.LeftPt >= 186 / Scale, $"entra na anterior: {r.AreaPt.LeftPt:F1}");
        }
        Assert.True(r.AreaPt.RightPt >= (b + 1) / Scale, $"não cobre a palavra: {r.AreaPt.RightPt:F1}");
    }
}
