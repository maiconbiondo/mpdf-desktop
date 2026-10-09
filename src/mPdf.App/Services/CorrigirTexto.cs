using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.App.Services;

/// Cor de fundo em volta de um trecho: mediana (por canal) dos pixels de uma moldura de 2..5 pt
/// FORA da área — os glifos dentro dela não entram. Devolve ARGB opaco.
public static class CorDeFundo
{
    public static uint Mediana(RenderedPage page, PdfQuad areaPt, double pageHeightPt, double scale)
    {
        var bs = new List<byte>(); var gs = new List<byte>(); var rs = new List<byte>();
        int x0 = (int)Math.Floor((areaPt.LeftPt - 5) * scale), x1 = (int)Math.Ceiling((areaPt.RightPt + 5) * scale);
        int y0 = (int)Math.Floor((pageHeightPt - areaPt.TopPt - 5) * scale), y1 = (int)Math.Ceiling((pageHeightPt - areaPt.BottomPt + 5) * scale);
        int ix0 = (int)Math.Floor((areaPt.LeftPt - 2) * scale), ix1 = (int)Math.Ceiling((areaPt.RightPt + 2) * scale);
        int iy0 = (int)Math.Floor((pageHeightPt - areaPt.TopPt - 2) * scale), iy1 = (int)Math.Ceiling((pageHeightPt - areaPt.BottomPt + 2) * scale);
        for (int y = Math.Max(0, y0); y < Math.Min(page.HeightPx, y1); y++)
        for (int x = Math.Max(0, x0); x < Math.Min(page.WidthPx, x1); x++)
        {
            if (x >= ix0 && x < ix1 && y >= iy0 && y < iy1) continue; // miolo (área + 2pt) fica de fora
            int i = (y * page.WidthPx + x) * 4;
            bs.Add(page.Bgra[i]); gs.Add(page.Bgra[i + 1]); rs.Add(page.Bgra[i + 2]);
        }
        if (bs.Count == 0) return 0xFFFFFFFF;
        static byte Med(List<byte> v) { v.Sort(); return v[v.Count / 2]; }
        return 0xFF000000u | ((uint)Med(rs) << 16) | ((uint)Med(gs) << 8) | Med(bs);
    }
}

/// Rodada 4 (revisão final): seleção com largura ou altura abaixo de 1 pt (caixa do PDFium de largura zero
/// em "l", "-", "|" — o Docnet arredonda as caixas para inteiro) é alargada 0,5 pt para cada lado, para não
/// virar uma área de correção inválida. Seleção normal volta intacta (caminho digital idêntico).
public static class AreaSelecao
{
    public static PdfQuad SemDegenerar(PdfQuad a)
    {
        if (a.RightPt - a.LeftPt < 1) a = a with { LeftPt = a.LeftPt - 0.5, RightPt = a.RightPt + 0.5 };
        if (a.TopPt - a.BottomPt < 1) a = a with { BottomPt = a.BottomPt - 0.5, TopPt = a.TopPt + 0.5 };
        return a;
    }
}

/// Aviso (não bloqueia) quando o texto novo sai da página ou cobre outro texto à direita na mesma linha.
public static class AvisoSobreposicao
{
    public const string PassaDaBorda = "O texto novo passa da borda da página.";
    public const string CobreTexto = "O texto novo vai cobrir outro texto à direita.";

    public static string? Calcular(TextPage page, PdfQuad areaPt, double novoRightPt, double larguraPaginaPt)
    {
        if (novoRightPt > larguraPaginaPt) return PassaDaBorda;
        if (novoRightPt <= areaPt.RightPt) return null;
        foreach (var c in page.Characters)
        {
            if (c.TopPt - c.BottomPt <= 0.01) continue;                       // espaço: caixa de altura zero
            bool mesmaLinha = c.BottomPt < areaPt.TopPt && c.TopPt > areaPt.BottomPt;
            bool aDireita = c.LeftPt >= areaPt.RightPt - 0.5 && c.LeftPt < novoRightPt;
            if (mesmaLinha && aDireita) return CobreTexto;
        }
        return null;
    }
}

/// Recorte retangular de um RenderedPage (BGRA, stride = WidthPx*4), clampado aos limites.
public static class Recorte
{
    public static RenderedPage De(RenderedPage page, int xPx, int yPx, int larguraPx, int alturaPx)
    {
        int x0 = Math.Clamp(xPx, 0, page.WidthPx), y0 = Math.Clamp(yPx, 0, page.HeightPx);
        int x1 = Math.Clamp(xPx + larguraPx, 0, page.WidthPx), y1 = Math.Clamp(yPx + alturaPx, 0, page.HeightPx);
        int w = Math.Max(0, x1 - x0), h = Math.Max(0, y1 - y0);
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(page.Bgra, ((y0 + y) * page.WidthPx + x0) * 4, dst, y * w * 4, w * 4);
        return new RenderedPage(w, h, dst);
    }
}

/// Geometria/estilo final de uma correção (área a limpar, tamanho, linha de base, x inicial, fundo e — só
/// na camada OCR — a fração mínima de sobreposição para o pdfSweep remover um glifo), a partir do probe,
/// da área selecionada (frame da CropBox, pt) e do render 2x da página.
public sealed record PlanoCorrecao(PdfQuad AreaPt, double TamanhoPt, double BaselineYPt, double LeftXPt, uint FundoArgb,
    double? MinOverlapRatio = null, IReadOnlyList<PdfQuad>? AreasPt = null);

/// Revisão final C1 (2026-10-09) + rodada 2. Texto VISÍVEL (digital): área = seleção, tamanho/base/x do
/// probe, `MinOverlapRatio` null — comportamento original, inalterado. Texto INVISÍVEL (camada OCR): as
/// caixas do PDFium vêm da Helvetica invisível gravada pelo OCR (tamanho = altura da caixa OCR, base no
/// fundo da caixa, sem ajuste de largura) e NÃO batem com a tinta escaneada. Então:
/// - a faixa VERTICAL é a própria caixa OCR que o probe devolve (base = `BaselineYPt`, topo = base +
///   `SizePt`) — a caixa do OCR É a extensão vertical da tinta da palavra; a tinta só refina dentro dela
///   (linhas coladas não entram);
/// - a tinta refina a extensão HORIZONTAL (`AreaDeTinta`) e dá tamanho/base/x;
/// - tamanho limitado a [0,8; 1,6] x `SizePt` do probe (a altura da caixa OCR vai da altura-x, palavra
///   só com minúsculas baixas, a ascendente+descendente);
/// - `MinOverlapRatio` = `SobreposicaoMinimaOcr`: a descida da Helvetica invisível da linha de cima
///   invade a área e não pode ser apagada.
public static class PlanejarCorrecao
{
    /// Altura de maiúscula/ascendente em fração do corpo da fonte. Helvetica/Arial ~0,72, Times ~0,66-0,68
    /// (ascendentes 0,68); 0,70 fica no meio, erro < ~5% para as famílias comuns de documentos escaneados.
    /// Palavra só com minúsculas sem ascendente (ex.: "uma") mede a altura-x e subestima o tamanho — a
    /// janela deixa o usuário ajustar.
    public const double FracaoAltura = 0.70;

    /// Fração mínima da caixa de um glifo dentro da área para o pdfSweep removê-lo (camada OCR). Os glifos
    /// da própria palavra ficam ~0,8+ dentro (área = caixa OCR); a descida da linha de cima, ~0,2 nos
    /// testes de linhas coladas (entrelinha 1,0). 0,4 separa os dois casos com folga.
    public const double SobreposicaoMinimaOcr = 0.4;

    /// Folga (pt) em volta das caixas OCR das linhas vizinhas que não é limpa (rodada 5): antisserrilhado da
    /// borda delas + 1 pixel de scan de 200 dpi (0,36 pt) que o pdfSweep limparia inteiro.
    public const double FolgaVizinha = 0.5;

    /// Crescimento (pt) das peças de limpeza para que se sobreponham nas emendas (> 1 pixel de scan de 200 dpi).
    public const double Emenda = 0.4;

    /// Caracteres com descendente LARGO (decide como achar a base na tinta — ver `AreaDeTinta`): com eles, a
    /// base é a mediana dos pés das colunas. "J" fica de fora: em Arial/Helvetica não desce. Rodada 5: cedilha,
    /// ogonek, vírgula e ponto-e-vírgula saíram da lista — são marcas ESTREITAS (poucas colunas), que a regra
    /// "nível mais baixo alcançado por >= 15% das colunas" já ignora; a mediana dos pés falhava em "AÇÃO" no
    /// Arial (laterais de A/Ç/Ã/O terminam acima da base -> base 0,6 pt alta).
    private static readonly char[] LetrasComDescendente = "gjpqyQ()[]{}|".ToCharArray();

    /// Altura-x em fração do corpo (Times 0,45, Arial 0,52; medida no render 2x sai um pouco maior, pelo
    /// antisserrilhado e pela ultrapassagem das curvas): palavra só de letras baixas ("uma", "sexo").
    public const double FracaoAlturaX = 0.52;

    /// Que topo de tinta mede o tamanho (rodada 4): com maiúscula/ascendente/dígito, o topo do CORPO das
    /// letras (sem acento sobre maiúscula nem pingo); só com letras baixas mas com pingo/acento (i, j, á),
    /// o topo com as marcas (~ altura de ascendente); só letras baixas, a altura-x.
    private static (bool UsaCorpo, double Fracao) ClasseDeTopo(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return (false, FracaoAltura);
        bool alta = false, marca = false, letra = false;
        foreach (char c in texto)
        {
            char baseC = c.ToString().Normalize(System.Text.NormalizationForm.FormD)[0];
            bool acento = c.ToString().Normalize(System.Text.NormalizationForm.FormD).Length > 1;
            if (char.IsUpper(c) || char.IsDigit(c) || "bdfhklt".Contains(baseC)) alta = true;
            else if (char.IsLetter(c)) { letra = true; if (acento || baseC is 'i' or 'j') marca = true; }
        }
        return alta ? (true, FracaoAltura) : marca ? (false, FracaoAltura) : letra ? (false, FracaoAlturaX) : (false, FracaoAltura);
    }

    public static PlanoCorrecao Calcular(TextStyleProbe probe, PdfQuad areaPt, RenderedPage? render, double pageHeightPt, double scale)
    {
        if (!probe.Invisible)
        {
            uint f = render is null ? 0xFFFFFFFFu : CorDeFundo.Mediana(render, areaPt, pageHeightPt, scale);
            return new PlanoCorrecao(areaPt, probe.SizePt, probe.BaselineYPt, probe.LeftXPt, f);
        }
        double? ratio = SobreposicaoMinimaOcr;
        var caixa = (BottomPt: probe.BaselineYPt, TopPt: probe.BaselineYPt + probe.SizePt);
        // Sem tinta achada (scan claro) ou sem render: vertical = caixa OCR (rodada 3) — a área do token desce a
        // descida da Helvetica invisível e cortaria as ascendentes da linha de baixo.
        var areaCaixa = new PdfQuad(areaPt.LeftPt, caixa.BottomPt, areaPt.RightPt, caixa.TopPt);
        if (render is null) return new PlanoCorrecao(areaCaixa, probe.SizePt, probe.BaselineYPt, probe.LeftXPt, 0xFFFFFFFFu, ratio);
        uint fundo = CorDeFundo.Mediana(render, areaPt, pageHeightPt, scale);
        var texto = probe.TokenText ?? probe.Text;
        bool? desc = string.IsNullOrEmpty(texto) ? null : texto.IndexOfAny(LetrasComDescendente) >= 0;
        (double, double?)? limites = probe.TokenAreaPt is { } ta ? (ta.LeftPt, probe.NextTokenLeftPt) : null;
        var vizinhas = probe.NeighbourBoxesPt?.Select(v => RefinarVizinha(render, v, caixa, pageHeightPt, scale, fundo)).ToList();
        var tinta = AreaDeTinta.Expandir(render, areaPt, pageHeightPt, scale, fundo, caixa, desc, texto, limites, vizinhas);
        if (tinta is null) return new PlanoCorrecao(areaCaixa, probe.SizePt, probe.BaselineYPt, probe.LeftXPt, fundo, ratio);
        var (usaCorpo, fracao) = ClasseDeTopo(texto);
        double tamanho = ((usaCorpo ? tinta.TopoCorpoPt : tinta.TopoPt) - tinta.BaselinePt) / fracao;
        if (!double.IsFinite(tamanho) || tamanho <= 0) tamanho = probe.SizePt;
        // Teto 1,6 x a caixa OCR; palavra só de letras baixas ("uma", "voce"): a caixa é a altura-x (~0,45 em),
        // então o corpo real chega a ~2,2 x a caixa — teto 2,4 (rodada 5).
        tamanho = Math.Clamp(tamanho, 0.8 * probe.SizePt, (fracao == FracaoAlturaX ? 2.4 : 1.6) * probe.SizePt);
        tamanho = Math.Round(Math.Clamp(tamanho, 4, 144), 1);
        // Fundo reamostrado em volta da área NOVA: a moldura da seleção original pode cair sobre a tinta.
        uint fundoNovo = CorDeFundo.Mediana(render, tinta.AreaPt, pageHeightPt, scale);
        var areas = AreasSemVizinhos(tinta.AreaPt, probe with { NeighbourBoxesPt = vizinhas });
        if (areas is not null && vizinhas is not null)
            areas = areas.Concat(TintaPropriaNaSobreposicao(render, tinta.AreaPt, vizinhas, caixa, texto, tinta.LeftPt, tinta.RightTintaPt,
                pageHeightPt, scale, fundo)).ToList();
        return new PlanoCorrecao(tinta.AreaPt, tamanho, tinta.BaselinePt, tinta.LeftPt, fundoNovo, ratio, areas);
    }

    /// Entrelinha 1,0 (rodada 5): a caixa desta palavra e a de uma vizinha se SOBREPÕEM (a cedilha daqui e o til
    /// de lá caem nas mesmas linhas de pixels). Componentes CONEXOS de tinta (8-vizinhança) numa janela que
    /// cobre a área e as caixas vizinhas decidem de quem é cada pixel da sobreposição:
    /// - componente que toca a parte EXCLUSIVA da vizinha (caixa dela fora da nossa) -> da vizinha (protege,
    ///   mesmo se tocar a nossa também: glifos encostados);
    /// - que toca só a parte exclusiva desta palavra (área fora das caixas vizinhas) -> desta palavra;
    /// - solto (acento/cedilha descolado, todo dentro da sobreposição): desta palavra se estiver sobre uma letra
    ///   DELA que tem marca do lado da vizinha (acento em cima de Á/Ã/Ú... quando ela está embaixo; cedilha ou
    ///   descendente embaixo quando está em cima — a tinta é repartida entre as letras pela largura
    ///   aproximada); senão, de quem tiver tinta mais PERTO; empate -> da vizinha. (Cedilhas costumam ser
    ///   ligadas ao C, então caem na regra de cima.)
    /// Devolve tiras de 1 coluna de render com a tinta desta palavra que está dentro das caixas vizinhas.
    private static IEnumerable<PdfQuad> TintaPropriaNaSobreposicao(RenderedPage page, PdfQuad area, IReadOnlyList<PdfQuad> vizinhas,
        (double BottomPt, double TopPt) nossa, string? texto, double tintaEsqPt, double tintaDirPt,
        double pageHeightPt, double scale, uint fundoArgb)
    {
        int wp = page.WidthPx, hp = page.HeightPx;
        double lumFundo = 0.299 * (byte)(fundoArgb >> 16) + 0.587 * (byte)(fundoArgb >> 8) + 0.114 * (byte)fundoArgb;
        (int X0, int Y0, int X1, int Y1) Px(PdfQuad q) => (
            Math.Clamp((int)Math.Floor(q.LeftPt * scale), 0, wp), Math.Clamp((int)Math.Floor((pageHeightPt - q.TopPt) * scale), 0, hp),
            Math.Clamp((int)Math.Ceiling(q.RightPt * scale), 0, wp), Math.Clamp((int)Math.Ceiling((pageHeightPt - q.BottomPt) * scale), 0, hp));
        var a = Px(area);
        var viz = vizinhas.Select(v => Px(new PdfQuad(v.LeftPt - FolgaVizinha, v.BottomPt - FolgaVizinha, v.RightPt + FolgaVizinha, v.TopPt + FolgaVizinha)))
                          .Where(v => v.X0 < a.X1 && v.X1 > a.X0 && v.Y0 < a.Y1 && v.Y1 > a.Y0).ToList();
        if (viz.Count == 0) return Array.Empty<PdfQuad>();
        static bool Em((int X0, int Y0, int X1, int Y1) r, int x, int y) => x >= r.X0 && x < r.X1 && y >= r.Y0 && y < r.Y1;

        // Janela = área ∪ caixas vizinhas que a tocam.
        int jx0 = Math.Min(a.X0, viz.Min(v => v.X0)), jy0 = Math.Min(a.Y0, viz.Min(v => v.Y0));
        int jx1 = Math.Max(a.X1, viz.Max(v => v.X1)), jy1 = Math.Max(a.Y1, viz.Max(v => v.Y1));
        int w = jx1 - jx0, h = jy1 - jy0;
        var tinta = new bool[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = ((jy0 + y) * wp + jx0 + x) * 4;
            tinta[y * w + x] = lumFundo - (0.299 * page.Bgra[i + 2] + 0.587 * page.Bgra[i + 1] + 0.114 * page.Bgra[i]) >= AreaDeTinta.LimiarTinta;
        }
        var rot = new int[w * h]; int n = 0;
        var tocaNos = new List<bool>(); var tocaEla = new List<bool>(); var pixels = new List<List<int>>();
        var pilha = new Stack<int>();
        for (int k = 0; k < w * h; k++)
        {
            if (!tinta[k] || rot[k] != 0) continue;
            n++; tocaNos.Add(false); tocaEla.Add(false); var lista = new List<int>(); pixels.Add(lista);
            rot[k] = n; pilha.Push(k);
            while (pilha.Count > 0)
            {
                int c = pilha.Pop(); lista.Add(c);
                int cx = c % w, cy = c / w, gx = jx0 + cx, gy = jy0 + cy;
                bool emViz = viz.Any(v => Em(v, gx, gy));
                if (Em(a, gx, gy) && !emViz) tocaNos[n - 1] = true;
                if (emViz && !Em(a, gx, gy)) tocaEla[n - 1] = true; // fora da NOSSA área (caixa + 0,5 pt)
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int nk = ny * w + nx;
                    if (tinta[nk] && rot[nk] == 0) { rot[nk] = n; pilha.Push(nk); }
                }
            }
        }
        // 0 = indefinido, 1 = nosso, 2 = dela.
        var dono = new int[n];
        for (int c = 0; c < n; c++) dono[c] = tocaEla[c] ? 2 : tocaNos[c] ? 1 : 0;
        var nossos = Enumerable.Range(0, n).Where(c => dono[c] == 1).SelectMany(c => pixels[c]).ToList();
        var delas = Enumerable.Range(0, n).Where(c => dono[c] == 2).SelectMany(c => pixels[c]).ToList();
        int Dist(List<int> alvo, List<int> de)
        {
            int melhor = int.MaxValue;
            foreach (int p in de)
            {
                int px = p % w, py = p / w;
                foreach (int q in alvo)
                {
                    int d = Math.Max(Math.Abs(q % w - px), Math.Abs(q / w - py));
                    if (d < melhor) melhor = d;
                }
            }
            return melhor;
        }
        // Fatias (px de janela) das letras desta palavra com marca do lado de cada vizinha.
        var marcaCima = new List<(double A, double B)>(); var marcaBaixo = new List<(double A, double B)>();
        if (!string.IsNullOrEmpty(texto) && double.IsFinite(tintaDirPt) && tintaDirPt > tintaEsqPt)
        {
            var letras = texto.Where(ch => !char.IsWhiteSpace(ch)).ToList();
            double total = letras.Sum(AreaDeTinta.LarguraAprox), x0p = tintaEsqPt * scale - jx0, wpx = (tintaDirPt - tintaEsqPt) * scale, acum = 0;
            foreach (char ch in letras)
            {
                double ini = x0p + acum / total * wpx, lw = AreaDeTinta.LarguraAprox(ch) / total * wpx;
                acum += AreaDeTinta.LarguraAprox(ch);
                string d = ch.ToString().Normalize(System.Text.NormalizationForm.FormD);
                if (d.Length > 1 && d.Skip(1).Any(m => m is '\u0300' or '\u0301' or '\u0302' or '\u0303' or '\u0308' or '\u030A') || ch is 'i' or 'j')
                    marcaCima.Add((ini - 0.1 * lw, ini + 1.1 * lw));
                if (d.Length > 1 && d.Skip(1).Any(m => m is '\u0327' or '\u0328') || "gjpqyQ,;".IndexOf(ch) >= 0)
                    marcaBaixo.Add((ini - 0.1 * lw, ini + 1.1 * lw));
            }
        }
        int nossoMeioPx = (int)Math.Round((pageHeightPt - (nossa.BottomPt + nossa.TopPt) / 2) * scale) - jy0;
        for (int c = 0; c < n; c++)
        {
            if (dono[c] != 0) continue;
            double cx = pixels[c].Average(p => p % w), cy = pixels[c].Average(p => p / w);
            var fatias = cy < nossoMeioPx ? marcaCima : marcaBaixo; // a marca solta está acima ou abaixo de nós
            dono[c] = fatias.Any(f => cx >= f.A && cx <= f.B) ? 1 : Dist(nossos, pixels[c]) < Dist(delas, pixels[c]) ? 1 : 2;
        }

        // Tiras: pixels nossos (+ 1 px de antisserrilhado) que caem dentro das caixas vizinhas — o resto da área
        // já é limpo pelas peças —, sempre a pelo menos 1 px de qualquer tinta da vizinha (o pdfSweep limpa
        // pixels inteiros da imagem do scan, que é mais fina que o render: o fosso evita comer a borda dela).
        bool PertoDe(int x, int y, int quem)
        {
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int r = rot[ny * w + nx];
                if (r != 0 && dono[r - 1] == quem) return true;
            }
            return false;
        }
        var tiras = new List<PdfQuad>();
        for (int x = 0; x < w; x++)
        {
            int ini = -1;
            for (int y = 0; y <= h; y++)
            {
                bool marca = false;
                if (y < h)
                {
                    int k = y * w + x, gx = jx0 + x, gy = jy0 + y;
                    marca = Em(a, gx, gy) && viz.Any(v => Em(v, gx, gy)) && PertoDe(x, y, 1) && !PertoDe(x, y, 2);
                }
                if (marca && ini < 0) ini = y;
                if (!marca && ini >= 0)
                {
                    tiras.Add(new PdfQuad((jx0 + x) / scale - PlanejarCorrecao.Emenda / 2, pageHeightPt - (jy0 + y) / scale,
                        (jx0 + x + 1) / scale + PlanejarCorrecao.Emenda / 2, pageHeightPt - (jy0 + ini) / scale)); // emendas (ver AreasSemVizinhos)
                    ini = -1;
                }
            }
        }
        return tiras;
    }

    /// A caixa vizinha que o probe devolve vai até o início do PRÓXIMO token da linha dela (a borda direita da
    /// caixa OCR não fica no PDF). Aqui ela encolhe até a tinta real da palavra vizinha, medida só nas linhas
    /// de pixels da caixa dela que ficam FORA da faixa desta palavra (assim a cedilha/acento DESTA palavra que
    /// cai no vão entre duas palavras da outra linha não protege nada) + 0,5 pt.
    private static PdfQuad RefinarVizinha(RenderedPage page, PdfQuad v, (double BottomPt, double TopPt) nossa,
        double pageHeightPt, double scale, uint fundoArgb)
    {
        int wp = page.WidthPx, hp = page.HeightPx;
        double lumFundo = 0.299 * (byte)(fundoArgb >> 16) + 0.587 * (byte)(fundoArgb >> 8) + 0.114 * (byte)fundoArgb;
        int x0 = Math.Clamp((int)Math.Floor(v.LeftPt * scale), 0, wp), x1 = Math.Clamp((int)Math.Ceiling(v.RightPt * scale), 0, wp);
        int y0 = Math.Clamp((int)Math.Floor((pageHeightPt - v.TopPt) * scale), 0, hp), y1 = Math.Clamp((int)Math.Ceiling((pageHeightPt - v.BottomPt) * scale), 0, hp);
        int ny0 = (int)Math.Floor((pageHeightPt - nossa.TopPt) * scale), ny1 = (int)Math.Ceiling((pageHeightPt - nossa.BottomPt) * scale);
        int ultima = -1;
        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
            {
                if (y >= ny0 && y < ny1) continue; // linhas de pixels que também são desta palavra
                int i = (y * wp + x) * 4;
                if (lumFundo - (0.299 * page.Bgra[i + 2] + 0.587 * page.Bgra[i + 1] + 0.114 * page.Bgra[i]) >= AreaDeTinta.LimiarTinta) { ultima = x; break; }
            }
        return ultima < 0 ? v : v with { RightPt = Math.Min(v.RightPt, (ultima + 1) / scale + 0.5) };
    }

    /// Rodada 5 (I-1): retângulos a limpar = a área MENOS as caixas OCR das linhas vizinhas (pixels dentro delas
    /// são da outra linha — acento/cedilha dela), MAIS uma faixa no miolo da palavra ([15%, 65%] da caixa OCR)
    /// que garante a remoção dos glifos invisíveis dela (o pdfSweep mede a sobreposição por retângulo).
    /// Sem vizinhos: null (a área única de sempre).
    internal static IReadOnlyList<PdfQuad>? AreasSemVizinhos(PdfQuad area, TextStyleProbe probe)
    {
        // Caixa vizinha + FolgaVizinha: a caixa OCR é a tinta acima do limiar (o antisserrilhado fica fora) e o
        // pdfSweep limpa pixels INTEIROS da imagem do scan que a borda do retângulo corta.
        var viz = probe.NeighbourBoxesPt?
            .Select(v => new PdfQuad(v.LeftPt - FolgaVizinha, v.BottomPt - FolgaVizinha, v.RightPt + FolgaVizinha, v.TopPt + FolgaVizinha))
            .Where(v => v.LeftPt < area.RightPt && v.RightPt > area.LeftPt && v.BottomPt < area.TopPt && v.TopPt > area.BottomPt).ToList();
        if (viz is not { Count: > 0 }) return null;
        var pecas = new List<PdfQuad> { area };
        foreach (var vp in viz) pecas = pecas.SelectMany(p => Subtrair(p, vp)).ToList();
        // O pdfSweep só limpa o pixel da imagem que cabe INTEIRO num retângulo: na emenda de duas peças, o pixel
        // cortado ao meio não sairia. Cada peça cresce Sobreposicao (> 1 pixel de scan de 200 dpi) e volta a
        // ser recortada pelas caixas vizinhas.
        pecas = pecas.Select(p => new PdfQuad(Math.Max(area.LeftPt, p.LeftPt - Emenda), Math.Max(area.BottomPt, p.BottomPt - Emenda),
                                               Math.Min(area.RightPt, p.RightPt + Emenda), Math.Min(area.TopPt, p.TopPt + Emenda))).ToList();
        foreach (var vp in viz) pecas = pecas.SelectMany(p => Subtrair(p, vp)).ToList();
        var faixa = new PdfQuad(area.LeftPt, probe.BaselineYPt + 0.15 * probe.SizePt, area.RightPt, probe.BaselineYPt + 0.65 * probe.SizePt);
        // A faixa também não entra nas caixas vizinhas (com entrelinha 1,0 o acento da linha de baixo chega nela).
        var faixas = new List<PdfQuad> { faixa };
        foreach (var v in viz) faixas = faixas.SelectMany(p => Subtrair(p, v)).ToList();
        pecas.AddRange(faixas);
        return pecas.Where(p => p.RightPt - p.LeftPt > 0.05 && p.TopPt - p.BottomPt > 0.05).ToList();
    }

    /// p − v em até 4 retângulos: as faixas de CIMA e de BAIXO ocupam a largura toda de p e as laterais só a
    /// altura de v — peças maximais na horizontal, para não criar emendas no meio de uma linha de texto (o
    /// pdfSweep não limpa o pixel de scan cortado por uma emenda).
    internal static IEnumerable<PdfQuad> Subtrair(PdfQuad p, PdfQuad v)
    {
        if (v.LeftPt >= p.RightPt || v.RightPt <= p.LeftPt || v.BottomPt >= p.TopPt || v.TopPt <= p.BottomPt) { yield return p; yield break; }
        double b = Math.Max(p.BottomPt, v.BottomPt), t = Math.Min(p.TopPt, v.TopPt);
        if (v.LeftPt > p.LeftPt) yield return new PdfQuad(p.LeftPt, b, v.LeftPt, t);
        if (v.RightPt < p.RightPt) yield return new PdfQuad(v.RightPt, b, p.RightPt, t);
        if (v.BottomPt > p.BottomPt) yield return p with { TopPt = v.BottomPt };
        if (v.TopPt < p.TopPt) yield return p with { BottomPt = v.TopPt };
    }
}

/// Tinta real de uma palavra escaneada (pontos, frame da página exibida): `AreaPt` = área a limpar,
/// `TopoPt` = topo da tinta, `BaselinePt` = base das letras (perfil horizontal da tinta, ignora
/// descendentes; com a caixa OCR, o fundo dela quando não há descendente), `LeftPt` = início da tinta.
/// `TopoCorpoPt` (rodada 4) = topo da tinta CONTÍGUA a partir do miolo das letras — sem marcas soltas
/// acima (acento sobre maiúscula, pingo do i/j).
public sealed record TintaPalavra(PdfQuad AreaPt, double TopoPt, double BaselinePt, double LeftPt, double TopoCorpoPt,
    double RightTintaPt = double.NaN);

/// Revisão final C1/rodada 2: acha a TINTA da(s) palavra(s) selecionada(s) num render (BGRA, y para baixo).
/// Algoritmo:
/// 1. Tinta = pixel com luminância pelo menos `LimiarTinta` abaixo da do fundo amostrado.
/// 2. Faixa vertical: com `faixaPt` (caixa OCR, caminho do app) = essa faixa; sem ela = seleção ±
///    0,6 x altura da seleção. Linha-semente = a linha de pixels dentro da seleção (e da faixa) com mais
///    tinta nas colunas da seleção. Cresce para cima/baixo enquanto há tinta nessas colunas, tolerando
///    lacunas de até 0,12 x altura da seleção (pingo do i, acento), sem sair da faixa.
/// 3. Perfil de colunas nessas linhas; colunas com tinta separadas por lacuna até 0,18 x altura da tinta
///    formam uma palavra (espaço entre palavras ~0,25 em + entrelinhas laterais = 0,25-0,38 x altura da
///    tinta — a altura da tinta chega a ~1 em com ascendente+descendente; entre letras fica abaixo de ~0,12;
///    0,22 juntava "Joaquim Pereira" num scan Times 11 pt a 144 dpi). Entram as palavras com pelo menos 50% da largura dentro da seleção (uma vizinha só
///    "beliscada" pela seleção fica de fora). Sem nenhuma, vale a tinta dentro das colunas da seleção.
/// 4. Extensão vertical recalculada nas colunas escolhidas (descendentes fora da seleção entram, dentro da
///    faixa); folga de 1 pt na horizontal. Horizontal: a área cobre a tinta e a seleção, mas NUNCA entra na
///    tinta de uma palavra vizinha (a Helvetica invisível do OCR pode ser mais larga que a tinta).
///    Vertical: sem `faixaPt`, tinta ± 1 pt unida à seleção; com `faixaPt`, a faixa (caixa OCR, que inclui
///    acentos e cedilha) ± 0,5 pt — nem a seleção do PDFium (desce a descida da Helvetica, que invade a
///    linha de baixo) nem a tinta crescida (pode parar antes de um acento solto). Cortada na página.
/// Devolve null se não há tinta dentro da seleção (o chamador mantém seleção + probe).
public static class AreaDeTinta
{
    public const int LimiarTinta = 80;

    public static TintaPalavra? Expandir(RenderedPage page, PdfQuad areaPt, double pageHeightPt, double scale, uint fundoArgb,
        (double BottomPt, double TopPt)? faixaPt = null, bool? temDescendente = null, string? texto = null,
        (double LeftPt, double? NextLeftPt)? limitesOcr = null, IReadOnlyList<PdfQuad>? alheiasPt = null)
    {
        int wp = page.WidthPx, hp = page.HeightPx;
        if (wp <= 0 || hp <= 0 || scale <= 0) return null;
        double lumFundo = Lum((byte)(fundoArgb >> 16), (byte)(fundoArgb >> 8), (byte)fundoArgb);
        // Rodada 5: pixels dentro das caixas OCR das linhas vizinhas não são tinta DESTA palavra (com entrelinha
        // 1,0 o til da linha de baixo cai dentro da caixa da de cima e virava o "pé" das colunas).
        var alheias = (alheiasPt ?? Array.Empty<PdfQuad>()).Select(q => (
            X0: (int)Math.Floor(q.LeftPt * scale), X1: (int)Math.Ceiling(q.RightPt * scale),
            Y0: (int)Math.Floor((pageHeightPt - q.TopPt) * scale), Y1: (int)Math.Ceiling((pageHeightPt - q.BottomPt) * scale))).ToList();
        bool Tinta(int x, int y)
        {
            foreach (var q in alheias) if (x >= q.X0 && x < q.X1 && y >= q.Y0 && y < q.Y1) return false;
            int i = (y * wp + x) * 4;
            return lumFundo - Lum(page.Bgra[i + 2], page.Bgra[i + 1], page.Bgra[i]) >= LimiarTinta;
        }
        int RowOf(double yPt, bool ceil) => Math.Clamp((int)(ceil ? Math.Ceiling((pageHeightPt - yPt) * scale) : Math.Floor((pageHeightPt - yPt) * scale)), 0, hp);

        int sx0 = Math.Clamp((int)Math.Floor(areaPt.LeftPt * scale), 0, wp);
        int sx1 = Math.Clamp((int)Math.Ceiling(areaPt.RightPt * scale), 0, wp);
        int sy0 = RowOf(areaPt.TopPt, false), sy1 = RowOf(areaPt.BottomPt, true);
        if (sx1 <= sx0 || sy1 <= sy0) return null;
        int linha = sy1 - sy0;
        int by0, by1;
        double meioPt = 0.5;
        if (faixaPt is { } f)
        {
            if (!(f.TopPt > f.BottomPt)) return null;
            // A caixa OCR já É a extensão vertical da tinta: a busca fica dentro dela (sem folga — com linhas
            // coladas, a descida da linha de cima chega a ~1 pt da caixa); a folga de 0,5 pt é só da área.
            by0 = RowOf(f.TopPt, false); by1 = RowOf(f.BottomPt, true);
            linha = Math.Max(1, by1 - by0);
        }
        else { by0 = Math.Max(0, sy0 - (int)Math.Round(0.6 * linha)); by1 = Math.Min(hp, sy1 + (int)Math.Round(0.6 * linha)); }
        int gapV = Math.Max(1, (int)Math.Round(0.12 * linha));

        bool LinhaTemTinta(int y, int x0, int x1) { for (int x = x0; x < x1; x++) if (Tinta(x, y)) return true; return false; }
        (int Top, int Bot) Crescer(int semente, int x0, int x1)
        {
            int top = semente, bot = semente;
            for (int y = semente - 1; y >= by0 && top - y <= gapV + 1; y--) if (LinhaTemTinta(y, x0, x1)) top = y;
            for (int y = semente + 1; y < by1 && y - bot <= gapV + 1; y++) if (LinhaTemTinta(y, x0, x1)) bot = y;
            return (top, bot);
        }

        // 2. Linha-semente (dentro da seleção e da faixa; com faixa, se a interseção for vazia, a faixa toda).
        int ry0 = Math.Max(sy0, by0), ry1 = Math.Min(sy1, by1);
        if (ry1 <= ry0) { ry0 = by0; ry1 = by1; }
        int semente = -1, melhor = 0;
        for (int y = ry0; y < ry1; y++)
        {
            int n = 0;
            for (int x = sx0; x < sx1; x++) if (Tinta(x, y)) n++;
            if (n > melhor) { melhor = n; semente = y; }
        }
        if (semente < 0) return null;
        var (top, bot) = Crescer(semente, sx0, sx1);
        // Altura de referência para a lacuna entre palavras: do topo CONTÍGUO do corpo (sem acento solto sobre
        // maiúscula, rodada 4) até o pé — com o acento, a lacuna mínima crescia ~40% e "MÚSICA ÁVILA" (Times,
        // serifas dos dois "A" quase se tocando) virava uma palavra só.
        int topCorpo0 = semente;
        for (int y = semente - 1; y >= top && LinhaTemTinta(y, sx0, sx1); y--) topCorpo0 = y;
        int alt = bot - topCorpo0 + 1;

        // 3. Palavras por perfil de colunas.
        int wx0 = Math.Max(0, sx0 - 6 * alt), wx1 = Math.Min(wp, sx1 + 6 * alt);
        var col = new bool[wx1 - wx0];
        for (int x = wx0; x < wx1; x++)
            for (int y = top; y <= bot; y++) if (Tinta(x, y)) { col[x - wx0] = true; break; }
        int gapH = Math.Max(2, (int)Math.Round(0.18 * alt));
        var palavras = new List<(int A, int B)>();
        int ini = -1, ult = -1;
        for (int i = 0; i < col.Length; i++)
        {
            if (!col[i]) continue;
            if (ini >= 0 && i - ult - 1 > gapH) { palavras.Add((ini + wx0, ult + wx0)); ini = -1; }
            if (ini < 0) ini = i;
            ult = i;
        }
        if (ini >= 0) palavras.Add((ini + wx0, ult + wx0));
        var escolhidas = palavras.Where(p =>
        {
            int sobre = Math.Min(p.B + 1, sx1) - Math.Max(p.A, sx0);
            return sobre >= 0.5 * (p.B - p.A + 1);
        }).ToList();
        int xa, xb;
        int? limiteDireitoPx = null;
        if (limitesOcr is { } lo)
        {
            // Rodada 5 (X-1), ESTRUTURAL: a palavra vai do início da caixa OCR dela até o início da caixa do
            // PRÓXIMO token da linha (a Helvetica invisível não tem a largura da tinta, e lacunas entre
            // maiúsculas do Arial — E|I, M|E, B|R — passavam do limiar de "palavra" da tinta). Última palavra
            // da linha: a tinta segue enquanto as lacunas forem menores que 0,25 em (em ~ corpo / 0,72).
            xa = Math.Clamp((int)Math.Floor(lo.LeftPt * scale), 0, wp - 1);
            xb = xa;
            if (lo.NextLeftPt is double prox)
            {
                limiteDireitoPx = Math.Clamp((int)Math.Floor((prox - meioPt) * scale), xa + 1, wp);
                for (int x = xa; x < limiteDireitoPx; x++)
                    for (int y = top; y <= bot; y++) if (Tinta(x, y)) { xb = x; break; }
            }
            else
            {
                int gapEm = Math.Max(2, (int)Math.Round(0.25 * alt / 0.72));
                for (int x = xa; x < wp && x - xb <= gapEm; x++)
                    for (int y = top; y <= bot; y++) if (Tinta(x, y)) { xb = x; break; }
            }
        }
        else if (escolhidas.Count > 0) { xa = escolhidas.Min(p => p.A); xb = escolhidas.Max(p => p.B); }
        else
        {
            xa = -1; xb = -1;
            for (int x = sx0; x < sx1; x++) if (col[x - wx0]) { if (xa < 0) xa = x; xb = x; }
            if (xa < 0) return null;
        }

        // 4. Extensão vertical final, linha de base e área.
        (top, bot) = Crescer(semente, xa, xb + 1);
        // Topo do CORPO: só linhas de pixels contíguas a partir da semente (para na 1ª linha sem tinta) — o
        // acento solto sobre a maiúscula e o pingo do i ficam de fora (a folga deles é de ~1 px no render 2x).
        int topCorpo = semente;
        for (int y = semente - 1; y >= top && LinhaTemTinta(y, xa, xb + 1); y--) topCorpo = y;
        // Com o texto (rodada 4): o topo do corpo vem só das letras ALTAS SEM acento (maiúscula/dígito/b d f h
        // k l t) — cada letra ocupa uma fatia de [xa, xb] proporcional à largura aproximada dela e mede-se o
        // topo da tinta na fatia alargada 15% de cada lado (a largura é aproximada; a haste do "b" fica na borda); vale a mediana. O acento de um "Á" de Times que
        // quase encosta na letra não entra (a contiguidade sozinha o juntava ao corpo em "Ávila").
        if (TopoDasLetrasAltas(texto, xa, xb, top, bot, Tinta) is int topLetras) topCorpo = topLetras;
        // Linha de base (rodada 3, I-A), pelo pé (pixel de tinta mais baixo) de cada coluna:
        // - palavra SEM letra de descendente (`temDescendente` false, ex.: "PONTA", "TAXA"): a base é o nível
        //   mais baixo da tinta (hastes, curvas e pernas pousam nela). A mediana dos pés falhava aqui:
        //   maiúsculas com barra só em cima (T, P, F, V, Y) têm colunas com o pé alto, que puxavam a base
        //   1-2 pt para cima e deixavam o texto novo pequeno e alto;
        // - COM descendente (g, j, p, q, y, ç, Q, vírgula...): mediana dos pés (critério da rodada 1 — as
        //   colunas de descendente são minoria);
        // - texto desconhecido (null): nível mais baixo se >= 30% das colunas pousam nele, senão mediana.
        var pes = new List<int>();
        for (int x = xa; x <= xb; x++)
            for (int y = bot; y >= top; y--) if (Tinta(x, y)) { pes.Add(y); break; }
        pes.Sort();
        int pe = bot;
        if (pes.Count > 0)
        {
            int maisBaixo = pes[^1], tolPe = Math.Max(1, (int)Math.Round(0.06 * alt));
            int noFundo = pes.Count(f => f >= maisBaixo - tolPe);
            bool semDescendente = temDescendente is bool d ? !d : noFundo >= 0.3 * pes.Count;
            if (semDescendente)
            {
                // Rodada 4: o nível mais baixo que pelo menos 15% das colunas alcançam — tinta isolada abaixo
                // da base (cedilha de um Ç que o texto do OCR não trouxe, sujeira do scan) não vira a base.
                pe = maisBaixo;
                for (int k = pes.Count - 1; k >= 0; k--)
                {
                    int nivel = pes[k];
                    if (pes.Count(f => f >= nivel && f <= nivel + tolPe) >= 0.15 * pes.Count) { pe = nivel; break; } // o nível e o logo abaixo (a ultrapassagem das curvas não puxa a base)
                }
            }
            else pe = pes[pes.Count / 2];
        }
        double basePt = pageHeightPt - (pe + 1) / scale;
        // Com a caixa OCR: o fundo dela (medido na imagem original, resolução maior que a do render) é a base
        // quando não há descendente de verdade abaixo da base do perfil (diferença até 0,12 x altura da tinta).
        // (0,06 x altura: só o antisserrilhado; a ponta de uma vírgula ou cedilha, que a caixa inclui, fica fora)
        if (faixaPt is { } fb && (basePt - fb.BottomPt) * scale <= 0.06 * alt && basePt >= fb.BottomPt - 0.5)
            basePt = fb.BottomPt;

        int pad = Math.Max(1, (int)Math.Round(scale));
        // Horizontal: tinta ± 1 pt, estendida até as bordas da seleção, mas sem entrar na tinta vizinha.
        int limL = palavras.Where(p => p.B < xa).Select(p => p.B + 1).DefaultIfEmpty(0).Max();
        int limR = palavras.Where(p => p.A > xb).Select(p => p.A).DefaultIfEmpty(wp).Min();
        int px0 = Math.Max(0, Math.Min(xa - pad, Math.Max(sx0, limL)));
        int px1 = Math.Min(wp, Math.Max(xb + 1 + pad, Math.Min(sx1, limR)));
        double leftPt = px0 / scale, rightPt = px1 / scale, topPt, bottomPt;
        if (limitesOcr is { } lo2)
        {
            // Limpa TUDO entre o início da caixa e o próximo token (o vão é fundo), ou até a tinta + 1 pt.
            leftPt = Math.Max(0, lo2.LeftPt - meioPt);
            rightPt = limiteDireitoPx is int lim ? lim / scale : Math.Min(wp, xb + 1 + pad) / scale;
        }
        if (faixaPt is null)
        {
            int py0 = Math.Max(0, top - pad), py1 = Math.Min(hp, bot + 1 + pad);
            topPt = Math.Max(areaPt.TopPt, pageHeightPt - py0 / scale);
            bottomPt = Math.Min(areaPt.BottomPt, pageHeightPt - py1 / scale);
        }
        else
        {
            // Rodada 4 (C-1): com a caixa OCR, a área LIMPA na vertical é a caixa inteira ± 0,5 pt — a caixa do
            // OCR inclui acentos e cedilha; a tinta crescida (que pode parar antes de um acento solto sobre a
            // maiúscula) serve só para medir tamanho e base.
            var fx = faixaPt.Value;
            topPt = Math.Min(pageHeightPt, fx.TopPt + meioPt);
            bottomPt = Math.Max(0, fx.BottomPt - meioPt);
        }
        return new TintaPalavra(new PdfQuad(leftPt, bottomPt, rightPt, topPt),
            pageHeightPt - top / scale, basePt, xa / scale, pageHeightPt - topCorpo / scale, (xb + 1) / scale);
    }

    private static double Lum(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;

    /// Largura aproximada (em em) de um caractere — só para repartir a tinta da palavra entre as letras.
    internal static double LarguraAprox(char c) =>
        "iljtfrI.,;:'!|".IndexOf(c) >= 0 ? 0.3 : "mwMW".IndexOf(c) >= 0 ? 0.85 : char.IsUpper(c) ? 0.7 : char.IsDigit(c) ? 0.55 : 0.5;

    private static bool LetraAltaSemAcento(char c)
    {
        if (c.ToString().Normalize(System.Text.NormalizationForm.FormD).Length > 1) return false;
        return char.IsUpper(c) || char.IsDigit(c) || "bdfhklt".IndexOf(c) >= 0;
    }

    private static int? TopoDasLetrasAltas(string? texto, int xa, int xb, int top, int bot, Func<int, int, bool> tinta)
    {
        if (string.IsNullOrEmpty(texto)) return null;
        var letras = texto.Where(c => !char.IsWhiteSpace(c)).ToList();
        double total = letras.Sum(LarguraAprox);
        if (total <= 0) return null;
        double larguraPx = xb - xa + 1, acum = 0;
        var topos = new List<int>();
        foreach (char c in letras)
        {
            double a = xa + acum / total * larguraPx, w = LarguraAprox(c) / total * larguraPx;
            acum += LarguraAprox(c);
            if (!LetraAltaSemAcento(c)) continue;
            int c0 = (int)Math.Round(a - 0.15 * w), c1 = (int)Math.Round(a + 1.15 * w); // fatia + 15%: haste do "b"/"d" na borda; o "M" de Times tem o V embaixo no meio
            int? t = null;
            for (int y = top; y <= bot && t is null; y++)
                for (int x = Math.Max(xa, c0); x <= Math.Min(xb, c1); x++) if (tinta(x, y)) { t = y; break; }
            if (t is int tt) topos.Add(tt);
        }
        if (topos.Count == 0) return null;
        topos.Sort();
        return topos[topos.Count / 2];
    }
}

/// Probe do "Corrigir texto" com o encaixe da camada OCR (revisão final, rodada 2 — I-2/I-3): sonda a
/// seleção; se o texto for INVISÍVEL (OCR), a seleção é encaixada nos tokens OCR INTEIROS que ela toca
/// (`TextStyleProbe.TokenText`/`TokenAreaPt`, com a pontuação colada, ex.: "NOVEMBRO,"): corrigir parte de
/// uma palavra escaneada não tem como localizar as letras na tinta, e a pontuação colada é apagada junto
/// com a tinta, então ela precisa aparecer no texto atual/novo da janela. O encaixe vem do iText porque
/// as caixas do PDFium não separam tokens OCR colados (sem espaço sintetizado entre eles quando a
/// Helvetica invisível de um encosta no outro). Texto digital: sem encaixe (área e texto da seleção).
/// Chamado fora da UI thread (só toca o editor).
public static class PrepararCorrecao
{
    public static (TextStyleProbe? Estilo, PdfQuad AreaPt, string? Texto) Sondar(IPdfEditor editor, byte[] pdf, int pageIndex,
        PdfQuad areaPt, string? texto)
    {
        var probe = editor.ProbeTextStyle(pdf, pageIndex, areaPt);
        if (probe is not { Invisible: true, TokenText: { } token, TokenAreaPt: { } tokenArea }) return (probe, areaPt, texto);
        return (probe with { LeftXPt = tokenArea.LeftPt }, tokenArea, token);
    }
}
