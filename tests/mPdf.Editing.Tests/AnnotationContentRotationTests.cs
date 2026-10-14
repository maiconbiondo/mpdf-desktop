using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Xobject;
using mPdf.Rendering;
using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Revisão final das páginas giradas (I1 + M2): "Girar" DEPOIS de anotar faz a anotação girar JUNTO com a página
/// (o /AP dela estava em pé para a rotação antiga). O lift (Remove+Add: mover, redimensionar, mudar estilo) tem de
/// PRESERVAR essa orientação visual — antes ele regenerava o /AP "em pé" para a rotação NOVA dentro da caixa exibida
/// de proporção trocada (imagem 80×40 virava 40×80 esticada; texto requebrado numa caixa alta e estreita;
/// sublinhado horizontal ao lado de um texto vertical na tela). Contrato: `AnnotationData.ConteudoRotacaoGraus`
/// (horário, na tela; null = em pé). Oráculo: render PDFium — o que aparece logo depois de Girar, deslocado/escalado
/// exatamente como pedido.
public class AnnotationContentRotationTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    /// 80×40, 4 quadrantes: sup.-esq. VERMELHO, sup.-dir. VERDE, inf.-esq. AZUL, inf.-dir. AMARELO.
    internal static readonly byte[] Quad4Png = TestPng.Rgb(80, 40, (x, y) =>
        y < 20 ? (x < 40 ? (255, 0, 0) : (0, 200, 0)) : (x < 40 ? (0, 0, 255) : (255, 220, 0)));

    private static readonly (string Nome, Func<int, int, int, bool> Pred)[] Cores4 =
        { ("vermelho", Red), ("verde", Green), ("azul", Blue), ("amarelo", Yellow) };

    internal static AnnotationData Imagem() => new()
    {
        Id = "st", Kind = AnnotationKind.ImageStamp, PageIndex = 0, ImageBytes = Quad4Png,
        LeftPt = 100, BottomPt = 300, RightPt = 260, TopPt = 380,
    };

    internal static AnnotationData Texto() => new()
    {
        Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, Content = "Texto girado", FontSizePt = 14, Bold = true, // formatação faz round-trip no lift (Fix round 2)
        LeftPt = 100, BottomPt = 300, RightPt = 300, TopPt = 340,
    };

    private static AnnotationData Nota() => new()
    {
        Id = "nt", Kind = AnnotationKind.StickyNote, PageIndex = 0, Content = "nota", ColorArgb = 0xFFFFD000,
        LeftPt = 300, BottomPt = 300, RightPt = 320, TopPt = 320,
    };

    private static AnnotationData Marcacao(AnnotationKind k, uint argb)
    {
        var q = new PdfQuad(220, 300, 340, 315);
        return new AnnotationData { Id = "mk", Kind = k, PageIndex = 0, ColorArgb = argb,
            LeftPt = q.LeftPt, BottomPt = q.BottomPt, RightPt = q.RightPt, TopPt = q.TopPt, Quads = new[] { q } };
    }

    /// Página com a anotação colocada EM PÉ em `rotColocar`, depois girada +90 ("Girar").
    private static byte[] ColocarEGirar(int rotColocar, AnnotationData a) =>
        Ed.RotatePages(Ed.AddAnnotation(Page(rotColocar, false), a), new[] { 0 }, 90);

    private static AnnotationData Ler(byte[] pdf, string id) => Ed.ReadAnnotations(pdf).Single(x => x.Id == id);

    /// Lift do App: Remove + Add do que foi lido, alterado por `f` (os bytes da imagem vêm do cache do App).
    internal static byte[] Lift(byte[] pdf, string id, Func<AnnotationData, AnnotationData> f, byte[]? img = null)
    {
        var a = Ler(pdf, id);
        if (img is not null) a = a with { ImageBytes = img };
        return Ed.AddAnnotation(Ed.RemoveAnnotation(pdf, id), f(a));
    }

    internal static AnnotationData Mover(AnnotationData a, double dx, double dy) => a with
    {
        LeftPt = a.LeftPt + dx, RightPt = a.RightPt + dx, BottomPt = a.BottomPt + dy, TopPt = a.TopPt + dy,
        Quads = a.Quads?.Select(q => new PdfQuad(q.LeftPt + dx, q.BottomPt + dy, q.RightPt + dx, q.TopPt + dy)).ToList(),
    };

    /// Escala a caixa EXIBIDA a partir do canto inf.-esq. (proporção mantida).
    private static AnnotationData Escalar(AnnotationData a, double s) => a with
    {
        RightPt = a.LeftPt + (a.RightPt - a.LeftPt) * s, TopPt = a.BottomPt + (a.TopPt - a.BottomPt) * s,
    };

    private static void Perto(double esperado, double real, double tol, string o) =>
        Assert.True(Math.Abs(esperado - real) <= tol, $"{o}: esperado {esperado:F2} ±{tol}, obtido {real:F2}");

    private static void MesmaMancha(Blob esperado, Blob real, double tol, string ctx)
    {
        Assert.True(real.Count > 0, ctx + ": nada renderizado");
        Perto(esperado.L, real.L, tol, ctx + " L"); Perto(esperado.R, real.R, tol, ctx + " R");
        Perto(esperado.B, real.B, tol, ctx + " B"); Perto(esperado.T, real.T, tol, ctx + " T");
    }

    private static Blob Deslocar(Blob b, double dx, double dy) =>
        b with { L = b.L + dx, R = b.R + dx, B = b.B + dy, T = b.T + dy, Cx = b.Cx + dx, Cy = b.Cy + dy };

    private static Blob EscalarDe(Blob b, double ox, double oy, double s) =>
        b with { L = ox + (b.L - ox) * s, R = ox + (b.R - ox) * s, B = oy + (b.B - oy) * s, T = oy + (b.T - oy) * s };

    public static IEnumerable<object[]> Casos() => new[] { new object[] { 0 }, new object[] { 90 }, new object[] { 270 } };

    // ---------------------------------------------------------------- leitura

    [Theory, MemberData(nameof(Casos))] // 0→90, 90→180, 270→0
    public void DepoisDeGirar_LeituraDizQueOConteudoGirou90(int rotColocar)
    {
        foreach (var a in new[] { Imagem(), Texto(), Nota(), Marcacao(AnnotationKind.Underline, 0xFF00FF00), Marcacao(AnnotationKind.Strikeout, 0xFFFF0000) })
        {
            Assert.Null(Ler(Ed.AddAnnotation(Page(rotColocar, false), a), a.Id!).ConteudoRotacaoGraus); // em pé ao colocar
            Assert.Equal(90, Ler(ColocarEGirar(rotColocar, a), a.Id!).ConteudoRotacaoGraus);
        }
    }

    [Fact] // girar 4× volta a ficar em pé (null); 2× = de ponta-cabeça (180)
    public void GirarDuasVezes_180_QuatroVezes_EmPe()
    {
        var pdf = Ed.AddAnnotation(Page(0, false), Texto());
        pdf = Ed.RotatePages(pdf, new[] { 0 }, 90); pdf = Ed.RotatePages(pdf, new[] { 0 }, 90);
        Assert.Equal(180, Ler(pdf, "ft").ConteudoRotacaoGraus);
        pdf = Ed.RotatePages(pdf, new[] { 0 }, 90);
        Assert.Equal(270, Ler(pdf, "ft").ConteudoRotacaoGraus);
        pdf = Ed.RotatePages(pdf, new[] { 0 }, 90);
        Assert.Null(Ler(pdf, "ft").ConteudoRotacaoGraus);
    }

    // ---------------------------------------------------------------- imagem: mover → redimensionar → estilo

    [Theory, MemberData(nameof(Casos))]
    public void Imagem_DepoisDeGirar_MoverRedimensionarEstilo_PreservamOrientacaoEProporcao(int rotColocar)
    {
        var pdf = ColocarEGirar(rotColocar, Imagem());
        string ctx0 = $"colocada em {rotColocar}, girada +90";
        var p0 = Render(pdf);
        var caixa0 = Find(p0, NonWhite);
        Assert.True(caixa0.T - caixa0.B > 1.8 * (caixa0.R - caixa0.L), $"{ctx0}: a imagem 2:1 girou junto (fica alta na tela)");
        var manchas0 = Cores4.Select(c => Find(p0, c.Pred)).ToArray();

        // mover +15, -25 (exibido)
        pdf = Lift(pdf, "st", a => Mover(a, 15, -25), Quad4Png);
        var p1 = Render(pdf);
        for (int i = 0; i < 4; i++) MesmaMancha(Deslocar(manchas0[i], 15, -25), Find(p1, Cores4[i].Pred), 1.5, $"{ctx0} → mover: {Cores4[i].Nome}");
        Assert.Equal(90, Ler(pdf, "st").ConteudoRotacaoGraus);

        // redimensionar ×1,5 a partir do canto inf.-esq. exibido
        var lida = Ler(pdf, "st");
        double ox = lida.LeftPt, oy = lida.BottomPt;
        pdf = Lift(pdf, "st", a => Escalar(a, 1.5), Quad4Png);
        var p2 = Render(pdf);
        for (int i = 0; i < 4; i++)
            MesmaMancha(EscalarDe(Deslocar(manchas0[i], 15, -25), ox, oy, 1.5), Find(p2, Cores4[i].Pred), 2, $"{ctx0} → redimensionar: {Cores4[i].Nome}");

        // "estilo" (lift sem mudar geometria: autor)
        pdf = Lift(pdf, "st", a => a with { Author = "outro" }, Quad4Png);
        var p3 = Render(pdf);
        for (int i = 0; i < 4; i++)
            MesmaMancha(EscalarDe(Deslocar(manchas0[i], 15, -25), ox, oy, 1.5), Find(p3, Cores4[i].Pred), 2, $"{ctx0} → estilo: {Cores4[i].Nome}");
        Assert.Equal(90, Ler(pdf, "st").ConteudoRotacaoGraus);
    }

    // ---------------------------------------------------------------- caixa de texto

    private static (double W, double H, double ML, double MR, double MB, double MT) Tinta(byte[] pdf)
    {
        var ink = Find(Render(pdf), NonWhite); // tinta de qualquer cor (o "estilo" troca a cor)
        Assert.True(ink.Count > 0, "texto não renderizado");
        var a = Ler(pdf, "ft");
        return (ink.R - ink.L, ink.T - ink.B, ink.L - a.LeftPt, a.RightPt - ink.R, ink.B - a.BottomPt, a.TopPt - ink.T);
    }

    [Theory, MemberData(nameof(Casos))]
    public void Texto_DepoisDeGirar_MoverRedimensionarEstilo_NaoRequebraNemDesvira(int rotColocar)
    {
        var pdf = ColocarEGirar(rotColocar, Texto());
        string ctx0 = $"colocado em {rotColocar}, girado +90";
        var t0 = Tinta(pdf);
        Assert.True(t0.H > 4 * t0.W, $"{ctx0}: o texto girou junto (vertical na tela): {t0}");
        var caixa0 = Ler(pdf, "ft");

        // mover: tinta igual, mesmas margens nos 4 lados
        pdf = Lift(pdf, "ft", a => Mover(a, 15, -25));
        var t1 = Tinta(pdf);
        var c1 = Ler(pdf, "ft");
        Perto(caixa0.LeftPt + 15, c1.LeftPt, 0.01, ctx0 + " caixa L"); Perto(caixa0.TopPt - 25, c1.TopPt, 0.01, ctx0 + " caixa T");
        Perto(caixa0.RightPt - caixa0.LeftPt, c1.RightPt - c1.LeftPt, 0.01, ctx0 + " caixa largura (não cresceu)");
        Perto(caixa0.TopPt - caixa0.BottomPt, c1.TopPt - c1.BottomPt, 0.01, ctx0 + " caixa altura (não cresceu)");
        Perto(t0.W, t1.W, 1.5, ctx0 + " → mover: largura da tinta"); Perto(t0.H, t1.H, 1.5, ctx0 + " → mover: altura da tinta");
        Perto(t0.ML, t1.ML, 1.5, ctx0 + " → mover: margem esq."); Perto(t0.MR, t1.MR, 1.5, ctx0 + " → mover: margem dir.");
        Perto(t0.MB, t1.MB, 1.5, ctx0 + " → mover: margem inf."); Perto(t0.MT, t1.MT, 1.5, ctx0 + " → mover: margem sup.");

        // redimensionar ×1,5: o texto continua do mesmo tamanho e ancorado no MESMO canto (o "sup.-esq." do conteúdo)
        pdf = Lift(pdf, "ft", a => Escalar(a, 1.5));
        var t2 = Tinta(pdf);
        Perto(t0.W, t2.W, 1.5, ctx0 + " → redimensionar: largura da tinta"); Perto(t0.H, t2.H, 1.5, ctx0 + " → redimensionar: altura da tinta");
        if (t0.ML < t0.MR) Perto(t0.ML, t2.ML, 1.5, ctx0 + " → redimensionar: âncora esq."); else Perto(t0.MR, t2.MR, 1.5, ctx0 + " → redimensionar: âncora dir.");
        if (t0.MB < t0.MT) Perto(t0.MB, t2.MB, 1.5, ctx0 + " → redimensionar: âncora inf."); else Perto(t0.MT, t2.MT, 1.5, ctx0 + " → redimensionar: âncora sup.");

        // estilo: cor nova — mesmo desenho
        pdf = Lift(pdf, "ft", a => a with { ColorArgb = 0xFFFF0000 }); // vermelho
        var t3 = Tinta(pdf);
        Perto(t2.W, t3.W, 1.5, ctx0 + " → estilo: largura"); Perto(t2.H, t3.H, 1.5, ctx0 + " → estilo: altura");
        Perto(t2.ML, t3.ML, 1.5, ctx0 + " → estilo: margem esq."); Perto(t2.MT, t3.MT, 1.5, ctx0 + " → estilo: margem sup.");
        Assert.Equal(90, Ler(pdf, "ft").ConteudoRotacaoGraus);
    }

    [Fact] // conteúdo girado que não cabe: a caixa cresce para "baixo" DO CONTEÚDO (à esquerda na tela em 90°)
    public void Texto_ConteudoGirado90_CresceParaBaixoDoConteudo()
    {
        var pdf = ColocarEGirar(0, Texto());
        var c0 = Ler(pdf, "ft");
        pdf = Lift(pdf, "ft", a => a with { Content = "Texto girado com bastante conteudo para quebrar em varias linhas e passar da altura original da caixa de texto" });
        var c1 = Ler(pdf, "ft");
        Assert.Equal(c0.RightPt, c1.RightPt, 2); // o "topo" do conteúdo fica à direita e não se mexe
        Assert.Equal(c0.TopPt, c1.TopPt, 2); Assert.Equal(c0.BottomPt, c1.BottomPt, 2);
        Assert.True(c1.LeftPt < c0.LeftPt - 10, $"não cresceu para a esquerda: {c0.LeftPt} -> {c1.LeftPt}");
        var ink = Find(Render(pdf), Dark);
        Assert.True(ink.L >= c1.LeftPt - 1 && ink.R <= c1.RightPt + 1, "texto fora da caixa crescida");
    }

    // ---------------------------------------------------------------- nota

    [Theory, MemberData(nameof(Casos))]
    public void Nota_DepoisDeGirar_MoverEEditar_PreservamOIcone(int rotColocar)
    {
        var pdf = ColocarEGirar(rotColocar, Nota());
        var p0 = Render(pdf);
        var corpo0 = Find(p0, Yellow); var tinta0 = Find(p0, Dark);
        pdf = Lift(pdf, "nt", a => Mover(a, 15, -25));
        var p1 = Render(pdf);
        string ctx = $"nota colocada em {rotColocar}, girada";
        MesmaMancha(Deslocar(corpo0, 15, -25), Find(p1, Yellow), 1.5, ctx + " → mover: corpo");
        MesmaMancha(Deslocar(tinta0, 15, -25), Find(p1, Dark), 1.5, ctx + " → mover: contorno");
        pdf = Lift(pdf, "nt", a => a with { Content = "outra" });
        MesmaMancha(Deslocar(corpo0, 15, -25), Find(Render(pdf), Yellow), 1.5, ctx + " → editar: corpo");
    }

    // ---------------------------------------------------------------- M2: sublinhado/tachado em texto vertical

    [Theory]
    [InlineData(0, AnnotationKind.Underline)]
    [InlineData(90, AnnotationKind.Underline)]
    [InlineData(270, AnnotationKind.Underline)]
    [InlineData(0, AnnotationKind.Strikeout)]
    [InlineData(90, AnnotationKind.Strikeout)]
    [InlineData(270, AnnotationKind.Strikeout)]
    public void Marcacao_TextoVerticalNaTela_LiftDesenhaAoLongoDoTexto(int rotColocar, AnnotationKind kind)
    {
        var pdf = ColocarEGirar(rotColocar, Marcacao(kind, 0xFF00FF00));
        var traco0 = Find(Render(pdf), Green);
        string ctx = $"{kind} colocado em {rotColocar}, girado";
        Assert.True(traco0.T - traco0.B > 4 * (traco0.R - traco0.L), $"{ctx}: o traço girou junto (vertical): {traco0}");

        pdf = Lift(pdf, "mk", a => Mover(a, 15, -25));
        MesmaMancha(Deslocar(traco0, 15, -25), Find(Render(pdf), Green), 1.5, ctx + " → mover");

        pdf = Lift(pdf, "mk", a => a with { ColorArgb = 0xFF0000FF }); // estilo: cor
        MesmaMancha(Deslocar(traco0, 15, -25), Find(Render(pdf), (r, g, b) => b - r > 60 && b - g > 60), 1.5, ctx + " → cor");
        Assert.Equal(90, Ler(pdf, "mk").ConteudoRotacaoGraus);
    }

    // ---------------------------------------------------------------- fix round 3: ordem de /QuadPoints de terceiros

    /// Sublinhado de OUTRO programa em página NORMAL, quad (72,735)-(172,749) gravado em ordem anti-horária
    /// (`ordem` = índices dos cantos TL=0, TR=1, BL=2, BR=3), sem /AP.
    private static byte[] SublinhadoTerceiro(int rot, params int[] ordem)
    {
        var cantos = new (float X, float Y)[] { (72, 749), (172, 749), (72, 735), (172, 735) };
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            if (rot != 0) page.SetRotation(rot);
            var qp = ordem.SelectMany(i => new[] { cantos[i].X, cantos[i].Y }).ToArray();
            var ul = PdfTextMarkupAnnotation.CreateUnderline(new Rectangle(72, 735, 100, 14), qp);
            ul.SetName(new PdfString("mk"));
            ul.SetColor(new float[] { 0, 1, 0 });
            page.AddAnnotation(ul);
        }
        return ms.ToArray();
    }

    [Theory] // anti-horária a partir do sup.-esq. (TL,BL,BR,TR) e a do spec a partir do inf.-esq. (BL,BR,TR,TL)
    [InlineData(new[] { 0, 2, 3, 1 })]
    [InlineData(new[] { 2, 3, 1, 0 })]
    public void Terceiros_QuadPointsAntiHorario_Rotacao0_NaoViraVertical(int[] ordem)
    {
        var pdf = SublinhadoTerceiro(0, ordem);
        Assert.Null(Ler(pdf, "mk").ConteudoRotacaoGraus);
        pdf = Lift(pdf, "mk", a => a with { ColorArgb = 0xFF0000FF }); // recolorir
        var b = Find(Render(pdf), (r, g, bl) => bl - r > 60 && bl - g > 60);
        Assert.True(b.Count > 0, "nada renderizado");
        Assert.True(b.R - b.L > 90 && b.T - b.B <= 3, $"o sublinhado deveria continuar horizontal: L={b.L} B={b.B} R={b.R} T={b.T}");
    }

    [Theory] // a nossa ordem (TL,TR,BL,BR), texto horizontal no usuário: gira junto com a página (= /Rotate)
    [InlineData(0, null)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    public void NossaOrdem_QuadPoints_RotacaoLida(int rot, int? esperado) =>
        Assert.Equal(esperado, Ler(SublinhadoTerceiro(rot, 0, 1, 2, 3), "mk").ConteudoRotacaoGraus);

    // ---------------------------------------------------------------- controles

    [Theory, MemberData(nameof(Casos))]
    public void Controle_AnotacaoNovaNaPaginaGirada_SaiEmPe(int rotColocar)
    {
        var girada = Ed.RotatePages(Page(rotColocar, false), new[] { 0 }, 90);
        var comImagem = Ed.AddAnnotation(girada, Imagem());
        var comTexto = Ed.AddAnnotation(girada, Texto() with { LeftPt = 300, RightPt = 500, BottomPt = 100, TopPt = 140 });
        Assert.Null(Ler(comImagem, "st").ConteudoRotacaoGraus);
        Assert.Null(Ler(comTexto, "ft").ConteudoRotacaoGraus);
        var p = Render(comImagem);
        // imagem 2:1 em pé: vermelho sup.-esq., amarelo inf.-dir.
        MesmaMancha(new Blob(1, 100, 340, 180, 380, 0, 0), Find(p, Red), 1.5, "nova: vermelho");
        MesmaMancha(new Blob(1, 180, 300, 260, 340, 0, 0), Find(p, Yellow), 1.5, "nova: amarelo");
        var ink = Find(Render(comTexto), Dark);
        Assert.True(ink.R - ink.L > 4 * (ink.T - ink.B), "texto novo não está na horizontal");
        Assert.InRange(ink.L, 301, 306);
    }

    [Fact] // /AP de outro programa com /Matrix que não é rotação pura (escala) -> não reconhecida = em pé (null)
    public void Terceiros_MatrixNaoReconhecida_TrataComoEmPe()
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            page.SetRotation(90);
            var ft = new PdfFreeTextAnnotation(new Rectangle(100, 100, 50, 100), new PdfString("x"));
            ft.SetName(new PdfString("tp"));
            var ap = new PdfFormXObject(new Rectangle(0, 0, 100, 200));
            ap.GetPdfObject().Put(PdfName.Matrix, new PdfArray(new[] { 0.5f, 0, 0, 0.5f, 0, 0 }));
            ft.SetNormalAppearance(ap.GetPdfObject());
            page.AddAnnotation(ft);
            var ft2 = new PdfFreeTextAnnotation(new Rectangle(300, 100, 50, 100), new PdfString("y"));
            ft2.SetName(new PdfString("nr"));
            ft2.SetFlags(PdfAnnotation.NO_ROTATE);
            page.AddAnnotation(ft2);
        }
        var lidas = Ed.ReadAnnotations(ms.ToArray());
        Assert.Null(lidas.Single(a => a.Id == "tp").ConteudoRotacaoGraus);
        Assert.Null(lidas.Single(a => a.Id == "nr").ConteudoRotacaoGraus); // NoRotate: sempre em pé
    }

    [Theory]
    [InlineData(45)]
    [InlineData(-30)]
    public void ConteudoRotacao_ForaDeMultiploDe90_Recusa(int graus) =>
        Assert.Throws<ArgumentException>(() => Ed.AddAnnotation(Page(0, false), Texto() with { ConteudoRotacaoGraus = graus }));
}

/// Folha de evidência (relatório da revisão final): imagem 4 cores + caixa de texto + sublinhado, colocados em pé em
/// /Rotate 0, depois "Girar" +90, depois mover, depois redimensionar. Só age com `GIRADAS_FIXSHEET` = PNG de saída.
public class AnnotationContentRotationSheet
{
    [SkippableFact] // sem GIRADAS_FIXSHEET: PULADO (nunca "aprovado" sem asserção)
    public void Write()
    {
        string? outPath = Environment.GetEnvironmentVariable("GIRADAS_FIXSHEET");
        Skip.If(string.IsNullOrEmpty(outPath), "GIRADAS_FIXSHEET não definida — folha de evidência não gerada.");
        var ed = PdfEditorFactory.Create();
        var u = new AnnotationData { Id = "mk", Kind = AnnotationKind.Underline, PageIndex = 0, ColorArgb = 0xFF00A000,
            LeftPt = 300, BottomPt = 420, RightPt = 440, TopPt = 435, Quads = new[] { new PdfQuad(300, 420, 440, 435) } };
        var pdf = AnnotationRotationRenderTests.Page(0, false);
        pdf = ed.AddAnnotation(pdf, AnnotationContentRotationTests.Imagem());
        pdf = ed.AddAnnotation(pdf, AnnotationContentRotationTests.Texto() with { LeftPt = 300, BottomPt = 300, RightPt = 500, TopPt = 340 });
        pdf = ed.AddAnnotation(pdf, u);
        var etapas = new List<byte[]> { pdf };
        pdf = ed.RotatePages(pdf, new[] { 0 }, 90); etapas.Add(pdf);
        foreach (var id in new[] { "st", "ft", "mk" })
            pdf = AnnotationContentRotationTests.Lift(pdf, id, a => AnnotationContentRotationTests.Mover(a, 40, -40),
                id == "st" ? AnnotationContentRotationTests.Quad4Png : null);
        etapas.Add(pdf);
        foreach (var id in new[] { "st", "ft" })
            pdf = AnnotationContentRotationTests.Lift(pdf, id, a => a with
            { RightPt = a.LeftPt + (a.RightPt - a.LeftPt) * 1.4, TopPt = a.BottomPt + (a.TopPt - a.BottomPt) * 1.4 },
                id == "st" ? AnnotationContentRotationTests.Quad4Png : null);
        etapas.Add(pdf);

        const double scale = 0.6;
        const int cell = 520;
        int W = etapas.Count * cell, H = cell;
        var rgb = new (int R, int G, int B)[W * H];
        Array.Fill(rgb, (200, 200, 200));
        for (int k = 0; k < etapas.Count; k++)
        {
            var p = AnnotationRotationRenderTests.Render(etapas[k], scale);
            int ox = k * cell + (cell - p.WidthPx) / 2, oy = (cell - p.HeightPx) / 2;
            for (int y = 0; y < p.HeightPx; y++)
                for (int x = 0; x < p.WidthPx; x++)
                {
                    int i = (y * p.WidthPx + x) * 4;
                    rgb[(oy + y) * W + ox + x] = (p.Bgra[i + 2], p.Bgra[i + 1], p.Bgra[i]);
                }
        }
        File.WriteAllBytes(outPath, TestPng.Rgb(W, H, (x, y) => rgb[y * W + x]));
    }
}
