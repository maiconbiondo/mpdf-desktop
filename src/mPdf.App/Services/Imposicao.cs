namespace mPdf.App.Services;

// Plano 25 (Task 1): MOTOR DE IMPOSIÇÃO — matemática PURA da impressão avançada (N páginas por folha,
// livreto/booklet, escala), separada do desenho WPF (como o Plano 24 separou PreviewRenderer da janela),
// pra ser 100% testável headless. Dado o tamanho da FOLHA (em pt), os tamanhos das PÁGINAS-FONTE (em pt) e
// um LayoutImpressao, devolve a lista de COLOCAÇÕES: qual página-fonte vai em qual folha, em qual retângulo
// (já escalado, preservando proporção) e com qual rotação. NÃO renderiza nada — só calcula geometria.
//
// Sistema de coordenadas: origem no CANTO SUPERIOR-ESQUERDO da folha, X para a direita, Y para baixo, tudo
// em pontos (1 pt = 1/72"). É o mesmo sistema que o paginator WPF usa ao desenhar (Task 2).

public enum EscalaModo { Ajustar, TamanhoReal, Reduzir, Percentual }
public enum OrientacaoModo { Auto, Retrato, Paisagem }
public enum OrdemNup { Horizontal, Vertical }

public readonly record struct TamanhoPt(double Largura, double Altura);

/// Configuração de imposição escolhida pelo usuário no diálogo (Task 2).
public sealed record LayoutImpressao
{
    /// Páginas-fonte por folha: 1, 2, 4, 6, 9 ou 16. Ignorado quando Livreto=true (livreto é sempre 2-up).
    public int NUp { get; init; } = 1;
    public OrdemNup Ordem { get; init; } = OrdemNup.Horizontal;
    /// Livreto (booklet): imposição 2-up para dobrar e grampear no meio.
    public bool Livreto { get; init; }
    public EscalaModo Escala { get; init; } = EscalaModo.Ajustar;
    /// Usado quando Escala==Percentual (100 = tamanho real).
    public double Percentual { get; init; } = 100;
    public OrientacaoModo Orientacao { get; init; } = OrientacaoModo.Auto;
    /// Margem externa da folha (todos os lados), em pt.
    public double MargemPt { get; init; }
    /// Espaçamento (gutter) entre as células da grade, em pt.
    public double EspacamentoPt { get; init; }
    /// Desenhar uma linha de borda em volta de cada página colocada.
    public bool Borda { get; init; }
}

/// Uma página-fonte posicionada numa folha. Retângulo destino em pt (origem topo-esquerda da folha).
/// PaginaFonte == -1 significa célula em branco (padding do livreto). Rotacao ∈ {0,90,180,270}.
public readonly record struct Colocacao(
    int Folha,
    int PaginaFonte,
    double X, double Y,
    double Largura, double Altura,
    int Rotacao);

public static class Imposicao
{
    /// Calcula as colocações para imprimir `paginas` (na ordem dada) numa folha de tamanho `folha`,
    /// segundo `layout`. Devolve as colocações agrupadas por folha (na ordem das folhas).
    public static IReadOnlyList<Colocacao> Calcular(
        TamanhoPt folha, IReadOnlyList<TamanhoPt> paginas, LayoutImpressao layout)
    {
        if (folha.Largura <= 0 || folha.Altura <= 0) throw new ArgumentException("Folha com tamanho inválido.", nameof(folha));
        ArgumentNullException.ThrowIfNull(paginas);
        ArgumentNullException.ThrowIfNull(layout);
        if (paginas.Count == 0) return [];

        return layout.Livreto
            ? CalcularLivreto(folha, paginas, layout)
            : CalcularGrade(folha, paginas, layout);
    }

    // ---- N-up (grade cols×rows) ---------------------------------------------------------------------

    private static IReadOnlyList<Colocacao> CalcularGrade(TamanhoPt folha, IReadOnlyList<TamanhoPt> paginas, LayoutImpressao layout)
    {
        var (cols, rows) = GradePara(layout.NUp, folha);
        int porFolha = cols * rows;
        double margem = layout.MargemPt;
        double espac = layout.EspacamentoPt;
        double usavelW = folha.Largura - 2 * margem;
        double usavelH = folha.Altura - 2 * margem;
        if (usavelW <= 0 || usavelH <= 0) throw new ArgumentException("Margem maior que a folha.");
        double celW = (usavelW - (cols - 1) * espac) / cols;
        double celH = (usavelH - (rows - 1) * espac) / rows;

        var res = new List<Colocacao>(paginas.Count);
        for (int i = 0; i < paginas.Count; i++)
        {
            int folhaIdx = i / porFolha;
            int celIdx = i % porFolha;
            // ordem de preenchimento das células
            int c, r;
            if (layout.Ordem == OrdemNup.Horizontal) { r = celIdx / cols; c = celIdx % cols; }
            else { c = celIdx / rows; r = celIdx % rows; }

            double celX = margem + c * (celW + espac);
            double celY = margem + r * (celH + espac);
            res.Add(ColocarNaCelula(paginas[i], i, folhaIdx, celX, celY, celW, celH, layout));
        }
        return res;
    }

    /// Grade (colunas, linhas) para cada N-up. Para 2 e 6 (não-quadrados) a grade acompanha a orientação
    /// da FOLHA, pra maximizar o tamanho de cada página (2-up numa folha retrato = 1 coluna × 2 linhas;
    /// numa folha paisagem = 2 colunas × 1 linha). 4/9/16 são grades quadradas (orientação não muda).
    private static (int cols, int rows) GradePara(int nUp, TamanhoPt folha)
    {
        bool folhaPaisagem = folha.Largura >= folha.Altura;
        return nUp switch
        {
            1 => (1, 1),
            2 => folhaPaisagem ? (2, 1) : (1, 2),
            4 => (2, 2),
            6 => folhaPaisagem ? (3, 2) : (2, 3),
            9 => (3, 3),
            16 => (4, 4),
            _ => throw new ArgumentException($"N-up não suportado: {nUp} (use 1, 2, 4, 6, 9 ou 16).")
        };
    }

    /// Coloca uma página-fonte numa célula: escolhe rotação (Auto), calcula a escala segundo o modo, e
    /// centraliza o retângulo destino dentro da célula.
    private static Colocacao ColocarNaCelula(TamanhoPt pagina, int paginaIdx, int folhaIdx,
        double celX, double celY, double celW, double celH, LayoutImpressao layout)
    {
        // Decide rotação: em Auto, gira 90° se isso permitir uma escala de AJUSTE maior (página paisagem
        // numa célula retrato e vice-versa). Retrato/Paisagem forçam a orientação da página.
        int rotacao = EscolherRotacao(pagina, celW, celH, layout.Orientacao);
        bool girada = rotacao == 90 || rotacao == 270;
        // (pw,ph) = "pegada" da página na folha JÁ considerando a rotação (girada troca L/A).
        double pw = girada ? pagina.Altura : pagina.Largura;
        double ph = girada ? pagina.Largura : pagina.Altura;

        double ajuste = Math.Min(celW / pw, celH / ph); // escala que faz caber na célula preservando proporção
        double escala = layout.Escala switch
        {
            EscalaModo.Ajustar => ajuste,
            EscalaModo.Reduzir => Math.Min(1.0, ajuste),
            EscalaModo.TamanhoReal => 1.0,
            EscalaModo.Percentual => Math.Max(0.01, layout.Percentual / 100.0),
            _ => ajuste
        };

        double destW = pw * escala;
        double destH = ph * escala;
        double destX = celX + (celW - destW) / 2;
        double destY = celY + (celH - destH) / 2;
        return new Colocacao(folhaIdx, paginaIdx, destX, destY, destW, destH, rotacao);
    }

    private static int EscolherRotacao(TamanhoPt pagina, double celW, double celH, OrientacaoModo orient)
    {
        bool paginaPaisagem = pagina.Largura >= pagina.Altura;
        bool celulaPaisagem = celW >= celH;
        return orient switch
        {
            OrientacaoModo.Retrato => paginaPaisagem ? 90 : 0,
            OrientacaoModo.Paisagem => paginaPaisagem ? 0 : 90,
            // Auto: alinha a orientação da página com a da célula (gira 90° se divergirem) — maximiza o
            // aproveitamento da célula.
            _ => paginaPaisagem == celulaPaisagem ? 0 : 90
        };
    }

    // ---- Livreto (booklet, 2-up saddle-stitch) ------------------------------------------------------

    private static IReadOnlyList<Colocacao> CalcularLivreto(TamanhoPt folha, IReadOnlyList<TamanhoPt> paginas, LayoutImpressao layout)
    {
        int n = paginas.Count;
        int padded = ((n + 3) / 4) * 4; // múltiplo de 4 (páginas em branco completam)
        int faces = padded / 2;         // cada face impressa é uma folha 2-up (frente/verso alternados)

        // Ordem de imposição saddle-stitch (índices 1-based; 0 = em branco). Para cada folha física i
        // (0..padded/4-1): frente = [padded-2i, 1+2i], verso = [2+2i, padded-1-2i].
        var ordem = new List<(int esq, int dir)>(faces);
        int folhasFisicas = padded / 4;
        for (int i = 0; i < folhasFisicas; i++)
        {
            ordem.Add((padded - 2 * i, 1 + 2 * i));       // frente
            ordem.Add((2 + 2 * i, padded - 1 - 2 * i));   // verso
        }

        double margem = layout.MargemPt;
        double espac = layout.EspacamentoPt;
        double usavelW = folha.Largura - 2 * margem;
        double usavelH = folha.Altura - 2 * margem;
        if (usavelW <= 0 || usavelH <= 0) throw new ArgumentException("Margem maior que a folha.");
        double celW = (usavelW - espac) / 2; // duas colunas (esquerda/direita)
        double celH = usavelH;

        var res = new List<Colocacao>(padded);
        for (int f = 0; f < ordem.Count; f++)
        {
            var (esq, dir) = ordem[f];
            AdicionarCelulaLivreto(res, paginas, esq - 1, f, margem, margem, celW, celH, layout);
            AdicionarCelulaLivreto(res, paginas, dir - 1, f, margem + celW + espac, margem, celW, celH, layout);
        }
        return res;
    }

    private static void AdicionarCelulaLivreto(List<Colocacao> res, IReadOnlyList<TamanhoPt> paginas,
        int paginaIdx, int folhaIdx, double celX, double celY, double celW, double celH, LayoutImpressao layout)
    {
        if (paginaIdx < 0 || paginaIdx >= paginas.Count)
        {
            // Página em branco (padding do livreto): registra a célula vazia (o paginator não desenha nada).
            res.Add(new Colocacao(folhaIdx, -1, celX, celY, 0, 0, 0));
            return;
        }
        // No livreto a escala é sempre AJUSTAR à metade da folha (preservando proporção) — é o padrão.
        var layoutAjuste = layout with { Escala = EscalaModo.Ajustar, Orientacao = OrientacaoModo.Auto };
        res.Add(ColocarNaCelula(paginas[paginaIdx], paginaIdx, folhaIdx, celX, celY, celW, celH, layoutAjuste));
    }
}
