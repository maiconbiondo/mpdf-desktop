using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Revisão crítica F1, item E3: a orientação do texto de um Underline/StrikeOut só é lida da ordem de /QuadPoints
/// quando a ordem é a do spec (TL,TR,BL,BR do CONTEÚDO): P1→P2 e P3→P4 na direção do texto E P1/P2 na borda de
/// CIMA (P1→P2 × P1→P3 com o sinal de TL,TR,BL,BR). As ordens ESPELHADAS de outros programas (TR,TL,BR,BL;
/// TL,BL,TR,BR; BL,BR,TL,TR; BR,TR,BL,TL) eram lidas como texto girado 180/90/270 e recolorir transformava o
/// sublinhado em "overline" ou num tique vertical; agora = `null` (em pé — o que o PDFium desenha: ele usa só P3/P2).
/// LIMITE (documentado): as 3 ordens que são ROTAÇÕES da ordem do spec (TR,BR,TL,BL / BR,BL,TR,TL / BL,TL,BR,TR) são
/// geometricamente idênticas a texto girado 90/180/270 de verdade (é a nossa própria ordem depois de "Girar") — não
/// há como distingui-las só pelos vértices; continuam lidas como girado.
public class QuadOrderTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    // O quad do revisor: palavra em (72,735)-(172,749) numa página NÃO girada.
    private static readonly Dictionary<string, (float X, float Y)> V = new()
    {
        ["TL"] = (72, 749), ["TR"] = (172, 749), ["BL"] = (72, 735), ["BR"] = (172, 735),
    };

    /// Ordem → orientação esperada em página NÃO girada.
    public static IEnumerable<object?[]> Ordens() => new (string, int?)[]
    {
        // a ordem do spec e as suas 3 rotações (texto girado de verdade)
        ("TL,TR,BL,BR", null), ("TR,BR,TL,BL", 90), ("BR,BL,TR,TL", 180), ("BL,TL,BR,TR", 270),
        // as 4 ordens paralelas ESPELHADAS
        ("BL,BR,TL,TR", null), ("TR,TL,BR,BL", null), ("TL,BL,TR,BR", null), ("BR,TR,BL,TL", null),
        // anti-horárias / P3→P4 não paralelo a P1→P2
        ("TL,BL,BR,TR", null), ("BL,BR,TR,TL", null), ("TR,TL,BL,BR", null),
    }.Select(o => new object?[] { o.Item1, o.Item2 });

    private static byte[] Terceiro(string ordem, AnnotationKind kind)
    {
        var qp = ordem.Split(',').SelectMany(k => new[] { V[k].X, V[k].Y }).ToArray();
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            var r = new Rectangle(72, 735, 100, 14);
            var a = kind == AnnotationKind.Underline
                ? PdfTextMarkupAnnotation.CreateUnderline(r, qp)
                : PdfTextMarkupAnnotation.CreateStrikeout(r, qp);
            a.SetName(new PdfString("mk"));
            a.SetColor(new float[] { 0, 0, 1 });
            page.AddAnnotation(a);
        }
        return ms.ToArray();
    }

    [Theory, MemberData(nameof(Ordens))]
    public void PaginaNormal_OrdemDeOutroPrograma_LeituraERecolorir(string ordem, int? esperado)
    {
        foreach (var kind in new[] { AnnotationKind.Underline, AnnotationKind.Strikeout })
        {
            var pdf = Terceiro(ordem, kind);
            var lida = Ed.ReadAnnotations(pdf).Single();
            Assert.True(lida.ConteudoRotacaoGraus == esperado, $"{ordem} {kind}: lida {lida.ConteudoRotacaoGraus}, esperado {esperado}");
            if (esperado is not null) continue; // rotação genuína: o lift desenha na direção do texto (outros testes)

            // Só recolorir (o lift do App) → o traço continua horizontal e no mesmo lugar (sob a palavra / no meio).
            var rec = Ed.AddAnnotation(Ed.RemoveAnnotation(pdf, "mk"), lida with { ColorArgb = 0xFFFF0000 });
            var t = Find(Render(rec), Red);
            Assert.True(t.Count > 0, $"{ordem} {kind}: nada desenhado");
            Assert.True(t.R - t.L >= 90, $"{ordem} {kind}: traço não horizontal ({t.L},{t.B})-({t.R},{t.T})");
            Assert.True(t.T - t.B <= 3, $"{ordem} {kind}: traço não horizontal ({t.L},{t.B})-({t.R},{t.T})");
            double yEsperado = kind == AnnotationKind.Underline ? 736 : 742;
            Assert.True(Math.Abs((t.B + t.T) / 2 - yEsperado) <= 2, $"{ordem} {kind}: traço em y={(t.B + t.T) / 2}, esperado ~{yEsperado}");
        }
    }

    [Theory] // a NOSSA ordem (TL,TR,BL,BR do conteúdo) continua sendo lida em todas as rotações do conteúdo
    [InlineData(1, 90)]
    [InlineData(2, 180)]
    [InlineData(3, 270)]
    [InlineData(4, null)]
    public void NossaOrdem_DepoisDeGirarNVezes_LeAOrientacao(int vezes, int? esperado)
    {
        foreach (var kind in new[] { AnnotationKind.Underline, AnnotationKind.Strikeout })
        {
            var q = new PdfQuad(220, 300, 340, 315);
            var pdf = Ed.AddAnnotation(Page(0, false), new AnnotationData
            {
                Id = "mk", Kind = kind, PageIndex = 0, ColorArgb = 0xFF00FF00,
                LeftPt = q.LeftPt, BottomPt = q.BottomPt, RightPt = q.RightPt, TopPt = q.TopPt, Quads = new[] { q },
            });
            for (int i = 0; i < vezes; i++) pdf = Ed.RotatePages(pdf, new[] { 0 }, 90);
            Assert.Equal(esperado, Ed.ReadAnnotations(pdf).Single().ConteudoRotacaoGraus);
        }
    }

    [Theory] // colocada em página JÁ girada (em pé) e lida de volta: em pé
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void NossaOrdem_ColocadaEmPaginaGirada_LidaEmPe(int rot)
    {
        var q = new PdfQuad(220, 300, 340, 315);
        var pdf = Ed.AddAnnotation(Page(rot, false), new AnnotationData
        {
            Id = "mk", Kind = AnnotationKind.Underline, PageIndex = 0, ColorArgb = 0xFF00FF00,
            LeftPt = q.LeftPt, BottomPt = q.BottomPt, RightPt = q.RightPt, TopPt = q.TopPt, Quads = new[] { q },
        });
        Assert.Null(Ed.ReadAnnotations(pdf).Single().ConteudoRotacaoGraus);
    }
}
