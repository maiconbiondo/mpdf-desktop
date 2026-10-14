using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace mPdf.Editing;

/// FRAME EXIBIDO da página — fonte única de verdade da conversão entre o frame que o App usa (caixas de
/// caractere do PDFium/Docnet, cliques na página renderizada) e o espaço do usuário do iText.
///
/// O PDFium exibe a CropBox RECORTADA pela MediaBox (CPDF_Page: `m_BBox = CropBox ∩ MediaBox`; sem
/// CropBox, ou interseção vazia, vale a MediaBox) e devolve coordenadas relativas ao canto
/// inferior-esquerdo dessa caixa. O iText grava /Rect, /QuadPoints, /InkList, /L e conteúdo no espaço do
/// usuário ABSOLUTO. Numa página recortada no Acrobat/Foxit (origem da CropBox != 0,0) a diferença é a
/// origem — sem conversão, tudo cai deslocado por ela (fix/markup-cropbox e v2.14.0 "Corrigir texto").
/// Compartilhado com mPdf.Signing (InternalsVisibleTo) para o carimbo da assinatura visível.
internal static class PageFrame
{
    /// A caixa exibida (CropBox ∩ MediaBox), normalizada, no espaço do usuário.
    public static Rectangle DisplayedBox(PdfPage page)
    {
        var media = page.GetMediaBox();
        var crop = page.GetCropBox();
        if (crop is null || crop.GetWidth() <= 0 || crop.GetHeight() <= 0) return media;
        var inter = crop.GetIntersection(media);
        return inter is null || inter.GetWidth() <= 0 || inter.GetHeight() <= 0 ? media : inter;
    }

    /// Origem (canto inferior-esquerdo) do frame exibido no espaço do usuário: some-a para ir do frame
    /// exibido ao espaço do usuário; subtraia-a para voltar.
    public static (double X, double Y) DisplayedOrigin(PdfPage page)
    {
        var b = DisplayedBox(page);
        return (b.GetLeft(), b.GetBottom());
    }

    /// O frame COMPLETO da página (rotação + caixa exibida não-rotacionada + origem): use-o para converter
    /// qualquer geometria exibido↔usuário em página girada. Ver `DisplayFrame`.
    public static DisplayFrame Frame(PdfPage page)
    {
        var b = DisplayedBox(page);
        return new DisplayFrame(RawRotation(page), b.GetWidth(), b.GetHeight(), b.GetLeft(), b.GetBottom());
    }

    /// A rotação que o PDFium APLICA na exibição (0/90/180/270) — a mesma em todo o motor (frame, OCR,
    /// carimbo, RotatePages). Ver `NormalizeRotation`.
    public static int Rotation(PdfPage page) => NormalizeRotation(RawRotation(page));

    /// Regra do PDFium (`CPDF_Page::GetPageRotation`): `r = (raw / 90) % 4; if (r < 0) r += 4` — divisão
    /// inteira truncando para zero, ANTES do módulo. Fix round 1 (2026-10-13), medido no render: /Rotate 135
    /// é exibido como 90, −135 como 270, 45 como 0, 315 como 270. (O `% 360` do iText daria −135 -> 225,
    /// que o PDFium exibe como 180 — por isso a regra é aplicada sobre o valor CRU, ver `RawRotation`.)
    /// Múltiplos de 90 (o caso de todo PDF bem-formado) saem iguais ao `% 360` de antes.
    public static int NormalizeRotation(int raw)
    {
        int r = (raw / 90) % 4;
        if (r < 0) r += 4;
        return r * 90;
    }

    /// `/Rotate` CRU da página (herdável: procura no nó /Pages ancestral quando a página não tem), sem o
    /// `% 360` que `PdfPage.GetRotation()` aplica. Ausente = 0.
    public static int RawRotation(PdfPage page)
    {
        var dict = page.GetPdfObject();
        for (int depth = 0; dict is not null && depth < 64; depth++) // 64: defesa contra /Parent cíclico
        {
            var n = dict.GetAsNumber(PdfName.Rotate);
            if (n is not null) return n.IntValue();
            dict = dict.GetAsDictionary(PdfName.Parent);
        }
        return 0;
    }

    /// Translada TODA a geometria de uma anotação (bbox, Quads, InkStrokes, Line*) por (dx, dy).
    public static AnnotationData Translate(AnnotationData a, double dx, double dy)
    {
        if (dx == 0 && dy == 0) return a;
        PdfPoint P(PdfPoint p) => new(p.XPt + dx, p.YPt + dy);
        return a with
        {
            LeftPt = a.LeftPt + dx, RightPt = a.RightPt + dx,
            BottomPt = a.BottomPt + dy, TopPt = a.TopPt + dy,
            Quads = a.Quads?.Select(q => new PdfQuad(q.LeftPt + dx, q.BottomPt + dy, q.RightPt + dx, q.TopPt + dy)).ToList(),
            InkStrokes = a.InkStrokes?.Select(s => (IReadOnlyList<PdfPoint>)s.Select(P).ToList()).ToList(),
            LineStartPt = a.LineStartPt is { } s0 ? P(s0) : null,
            LineEndPt = a.LineEndPt is { } e0 ? P(e0) : null,
        };
    }
}

/// Páginas giradas (2026-10-13): a transformação ÚNICA entre o FRAME EXIBIDO (o que o PDFium mostra — já girado
/// por `/Rotate`, y-para-cima, relativo ao canto inferior-esquerdo da caixa exibida CropBox∩MediaBox) e o ESPAÇO
/// DO USUÁRIO ABSOLUTO do iText (não-rotacionado). Antes havia 3 cópias desta álgebra (carimbo da assinatura,
/// camada OCR, e só-translação nas anotações); agora o carimbo (`PadesSigningEngine`, via `PageFrame.Frame(...).ToUser`) e a camada OCR usam esta.
///
/// `Width`×`Height` = dimensões NÃO-rotacionadas da caixa exibida (`PageFrame.DisplayedBox`); `(OriginX,OriginY)`
/// = seu canto inferior-esquerdo no espaço do usuário. `/Rotate` é horário na exibição (PDF 32000-1 §7.7.3.3):
///
/// | rot | usuário(rel)→exibido `(x,y)→(X,Y)` | exibido→usuário(rel) `(X,Y)→(x,y)` | `ContentMatrix` | exibida |
/// |-----|------------------------------------|------------------------------------|-----------------|---------|
/// | 0   | (x, y)                             | (X, Y)                             | [1 0 0 1]       | W×H     |
/// | 90  | (y, W−x)                           | (W−Y, X)                           | [0 1 −1 0]      | H×W     |
/// | 180 | (W−x, H−y)                         | (W−X, H−Y)                         | [−1 0 0 −1]     | W×H     |
/// | 270 | (H−y, x)                           | (Y, H−X)                           | [0 −1 1 0]      | H×W     |
///
/// usuário_abs = origem + exibido→usuário(rel). Verificado por sonda (glifos iText × caixas PDFium, renders —
/// `.superpowers/sdd/rotacao-sonda/`) e por `DocnetRotationFrameTests`/`StampRotationRenderTests`.
/// Rotação 0 é a identidade exata além da origem (mesmas operações de ponto flutuante de antes: `rel + origem`).
/// Retângulos: rotações múltiplas de 90° levam retângulo em retângulo; o resultado é renormalizado (L≤R, B≤T).
internal readonly record struct DisplayFrame
{
    /// `/Rotate` normalizado a {0, 90, 180, 270} pela regra do PDFium (`PageFrame.NormalizeRotation`).
    public int Rotation { get; }
    /// Largura/altura NÃO-rotacionadas da caixa exibida (espaço do usuário).
    public double Width { get; }
    public double Height { get; }
    /// Canto inferior-esquerdo da caixa exibida no espaço do usuário.
    public double OriginX { get; }
    public double OriginY { get; }

    public DisplayFrame(int rotation, double width, double height, double originX = 0, double originY = 0)
    {
        Rotation = PageFrame.NormalizeRotation(rotation);
        Width = width; Height = height; OriginX = originX; OriginY = originY;
    }

    /// Em 90/270 a largura/altura exibidas são as do usuário TROCADAS.
    public bool SwapsAxes => Rotation is 90 or 270;
    /// Dimensões EXIBIDAS (as que o PDFium devolve em `GetPageSize`).
    public double DisplayedWidth => SwapsAxes ? Height : Width;
    public double DisplayedHeight => SwapsAxes ? Width : Height;

    /// Ponto exibido → usuário RELATIVO à origem (sem somá-la) — para quem soma a origem por conta própria.
    public PdfPoint ToUserRelative(PdfPoint d) => Rotation switch
    {
        90 => new(Width - d.YPt, d.XPt),
        180 => new(Width - d.XPt, Height - d.YPt),
        270 => new(d.YPt, Height - d.XPt),
        _ => d,
    };

    /// Ponto exibido → usuário ABSOLUTO.
    public PdfPoint ToUser(PdfPoint d)
    {
        var r = ToUserRelative(d);
        return new(r.XPt + OriginX, r.YPt + OriginY);
    }

    /// Ponto usuário ABSOLUTO → exibido.
    public PdfPoint ToDisplayed(PdfPoint u)
    {
        double x = u.XPt - OriginX, y = u.YPt - OriginY;
        return Rotation switch
        {
            90 => new(y, Width - x),
            180 => new(Width - x, Height - y),
            270 => new(Height - y, x),
            _ => new(x, y),
        };
    }

    /// VETOR do usuário → exibido (só a rotação; sem origem): a diferença de dois pontos levados por `ToDisplayed`.
    /// Usado para a direção do texto (Corrigir texto: o glifo avança para a direita na tela?).
    public (double X, double Y) VectorToDisplayed(double vx, double vy) => Rotation switch
    {
        90 => (vy, -vx),
        180 => (-vx, -vy),
        270 => (-vy, vx),
        _ => (vx, vy),
    };

    /// `ToDisplayed` como matriz PDF `[a b c d e f]` (`X = a·x + c·y + e`, `Y = b·x + d·y + f`) — usuário ABSOLUTO
    /// → exibido. Para compor com outra matriz (ex.: imposição de uma página girada numa folha).
    public double[] UserToDisplayedMatrix()
    {
        double ox = OriginX, oy = OriginY, w = Width, h = Height;
        return Rotation switch
        {
            90 => [0, -1, 1, 0, -oy, w + ox],      // (y', W − x')
            180 => [-1, 0, 0, -1, w + ox, h + oy], // (W − x', H − y')
            270 => [0, 1, -1, 0, h + oy, -ox],     // (H − y', x')
            _ => [1, 0, 0, 1, -ox, -oy],
        };
    }

    /// Retângulo exibido → usuário RELATIVO à origem, normalizado.
    public PdfQuad ToUserRelative(PdfQuad d) =>
        Normalize(ToUserRelative(new PdfPoint(d.LeftPt, d.BottomPt)), ToUserRelative(new PdfPoint(d.RightPt, d.TopPt)));

    /// Retângulo exibido → usuário ABSOLUTO, normalizado.
    public PdfQuad ToUser(PdfQuad d) =>
        Normalize(ToUser(new PdfPoint(d.LeftPt, d.BottomPt)), ToUser(new PdfPoint(d.RightPt, d.TopPt)));

    /// Retângulo usuário ABSOLUTO → exibido, normalizado.
    public PdfQuad ToDisplayed(PdfQuad u) =>
        Normalize(ToDisplayed(new PdfPoint(u.LeftPt, u.BottomPt)), ToDisplayed(new PdfPoint(u.RightPt, u.TopPt)));

    /// `/QuadPoints` de UM retângulo exibido, por vértice: TL, TR, BL, BR EXIBIDOS levados ao usuário absoluto
    /// (8 floats). Em rotação 0 coincide com o retângulo transladado (ordem de `BuildQuadPoints`).
    public float[] QuadPointsToUser(PdfQuad d)
    {
        var tl = ToUser(new PdfPoint(d.LeftPt, d.TopPt));
        var tr = ToUser(new PdfPoint(d.RightPt, d.TopPt));
        var bl = ToUser(new PdfPoint(d.LeftPt, d.BottomPt));
        var br = ToUser(new PdfPoint(d.RightPt, d.BottomPt));
        return new[] { (float)tl.XPt, (float)tl.YPt, (float)tr.XPt, (float)tr.YPt,
                       (float)bl.XPt, (float)bl.YPt, (float)br.XPt, (float)br.YPt };
    }

    /// `/QuadPoints` de UM retângulo exibido cujo TEXTO está girado `contentRotation` graus (horário, na tela): os
    /// vértices saem na ordem TL, TR, BL, BR DO CONTEÚDO (TL→TR = direção do texto), levados ao usuário absoluto.
    /// `contentRotation` 0 = `QuadPointsToUser(d)`. A ordem é o que `ContentRotationOfAdvance` lê de volta.
    public float[] ContentQuadPointsToUser(PdfQuad d, int contentRotation)
    {
        if (PageFrame.NormalizeRotation(contentRotation) == 0) return QuadPointsToUser(d);
        var (cw, ch) = ContentSize(contentRotation, d.RightPt - d.LeftPt, d.TopPt - d.BottomPt);
        var cf = new DisplayFrame(contentRotation, cw, ch); // "usuário" = conteúdo; "exibido" = o quad na tela
        var self = this;
        PdfPoint V(double x, double y)
        {
            var r = cf.ToDisplayed(new PdfPoint(x, y));
            return self.ToUser(new PdfPoint(r.XPt + d.LeftPt, r.YPt + d.BottomPt));
        }
        var tl = V(0, ch); var tr = V(cw, ch); var bl = V(0, 0); var br = V(cw, 0);
        return new[] { (float)tl.XPt, (float)tl.YPt, (float)tr.XPt, (float)tr.YPt,
                       (float)bl.XPt, (float)bl.YPt, (float)br.XPt, (float)br.YPt };
    }

    /// Tamanho do CONTEÚDO (em pé) de uma caixa exibida `dw`×`dh` cujo conteúdo está girado `contentRotation` graus
    /// na tela: em 90/270 a largura do conteúdo é a altura exibida.
    public static (double W, double H) ContentSize(int contentRotation, double dw, double dh) =>
        PageFrame.NormalizeRotation(contentRotation) is 90 or 270 ? (dh, dw) : (dw, dh);

    /// Retângulo exibido RELATIVO ao canto inf.-esq. de uma caixa exibida `dw`×`dh` → o mesmo retângulo no frame do
    /// CONTEÚDO (em pé, `ContentSize`) quando o conteúdo está girado `contentRotation` graus (horário) na tela.
    public static PdfQuad DisplayedToContent(PdfQuad displayedRel, int contentRotation, double dw, double dh)
    {
        var (cw, ch) = ContentSize(contentRotation, dw, dh);
        return new DisplayFrame(contentRotation, cw, ch).ToUser(displayedRel);
    }

    /// Rotação do conteúdo (horária, na tela) cujo texto AVANÇA na direção `(vx, vy)` do USUÁRIO: 0 = para a direita
    /// na tela, 90 = para baixo, 180 = para a esquerda, 270 = para cima. `null` se a direção não é ~axial (±5,7°).
    public int? ContentRotationOfAdvance(double vx, double vy)
    {
        var (x, y) = VectorToDisplayed(vx, vy);
        if (x > 0 && Math.Abs(y) <= 0.1 * x) return 0;
        if (y < 0 && Math.Abs(x) <= 0.1 * -y) return 90;
        if (x < 0 && Math.Abs(y) <= 0.1 * -x) return 180;
        if (y > 0 && Math.Abs(x) <= 0.1 * y) return 270;
        return null;
    }

    /// Rotação do conteúdo (horária, na tela) de um /AP cuja /Matrix tem a parte linear `(a,b,c,d)`: o conteúdo foi
    /// desenhado "em pé para a rotação Q" (`ContentMatrix` de Q) e a página está em `Rotation` → girou `Rotation − Q`.
    /// `null` se a matriz não é uma das 4 rotações puras (escala, cisalhamento: não reconhecida).
    public int? ContentRotationOfMatrix(double a, double b, double c, double d)
    {
        foreach (int q in new[] { 0, 90, 180, 270 })
        {
            var m = new DisplayFrame(q, 0, 0).ContentMatrix;
            if (Math.Abs(m.A - a) < 1e-3 && Math.Abs(m.B - b) < 1e-3 && Math.Abs(m.C - c) < 1e-3 && Math.Abs(m.D - d) < 1e-3)
                return (Rotation - q + 360) % 360;
        }
        return null;
    }

    /// Parte LINEAR `(a,b,c,d)` que desenha conteúdo EM PÉ para o leitor: a imagem, no usuário, dos eixos
    /// exibidos (+X avanço, +Y subida) — rotaciona em −θ. Uso: `/Matrix` de um /AP com BBox nas dimensões
    /// EXIBIDAS, ou a parte linear de `Tm` (ver `TextMatrix`).
    public (double A, double B, double C, double D) ContentMatrix => Rotation switch
    {
        90 => (0, 1, -1, 0),
        180 => (-1, 0, 0, -1),
        270 => (0, -1, 1, 0),
        _ => (1, 0, 0, 1),
    };

    /// Matriz de texto `Tm` `[a b c d e f]` (`x' = a*x + c*y + e`, `y' = b*x + d*y + f`) que põe a baseline no
    /// ponto EXIBIDO `displayedOrigin` com o texto em pé: `ContentMatrix` + `ToUser(displayedOrigin)`. A escala
    /// do glifo fica fora (só `Tf`); a parte linear tem determinante 1.
    public (double A, double B, double C, double D, double E, double F) TextMatrix(PdfPoint displayedOrigin)
    {
        var (a, b, c, d) = ContentMatrix;
        var p = ToUser(displayedOrigin);
        return (a, b, c, d, p.XPt, p.YPt);
    }

    /// TODA a geometria de uma anotação (bbox, Quads, InkStrokes, Line*) exibido → usuário absoluto. Rotação 0
    /// é exatamente `PageFrame.Translate(a, OriginX, OriginY)` (o mesmo objeto quando a origem é 0,0).
    public AnnotationData ToUser(AnnotationData a) =>
        Rotation == 0 ? PageFrame.Translate(a, OriginX, OriginY) : Map(a, ToUser, ToUser);

    /// TODA a geometria de uma anotação usuário absoluto → exibido. Rotação 0 = `Translate(−origem)`.
    public AnnotationData ToDisplayed(AnnotationData a) =>
        Rotation == 0 ? PageFrame.Translate(a, -OriginX, -OriginY) : Map(a, ToDisplayed, ToDisplayed);

    private static AnnotationData Map(AnnotationData a, Func<PdfPoint, PdfPoint> p, Func<PdfQuad, PdfQuad> q)
    {
        var bbox = q(new PdfQuad(a.LeftPt, a.BottomPt, a.RightPt, a.TopPt));
        return a with
        {
            LeftPt = bbox.LeftPt, BottomPt = bbox.BottomPt, RightPt = bbox.RightPt, TopPt = bbox.TopPt,
            Quads = a.Quads?.Select(q).ToList(),
            InkStrokes = a.InkStrokes?.Select(s => (IReadOnlyList<PdfPoint>)s.Select(p).ToList()).ToList(),
            LineStartPt = a.LineStartPt is { } s0 ? p(s0) : null,
            LineEndPt = a.LineEndPt is { } e0 ? p(e0) : null,
        };
    }

    private static PdfQuad Normalize(PdfPoint a, PdfPoint b) =>
        new(Math.Min(a.XPt, b.XPt), Math.Min(a.YPt, b.YPt), Math.Max(a.XPt, b.XPt), Math.Max(a.YPt, b.YPt));
}
