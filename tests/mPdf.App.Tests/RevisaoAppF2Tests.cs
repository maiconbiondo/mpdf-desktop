using System.IO;
using System.Linq;
using System.Windows;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.App.Tests;

/// Revisão crítica F2 (achados de APP, A1–A9): cada teste reproduz o cenário do revisor pelo `DocumentViewModel`
/// REAL com o motor e o PDFium REAIS (mesmo oráculo de `PaginasGiradasVmTests`: o render do PDFium).
public class RevisaoAppF2Tests
{
    internal const uint Vermelho = DocumentViewModel.ColorVermelho;

    internal static VmTemporaria Abrir(byte[] pdf, IPdfEditor? editor = null) => VmTemporaria.Abrir(pdf, "f2", editor: editor);

    internal static byte[] Girada(byte[] pdf, int rot, params int[] paginas) =>
        rot == 0 ? pdf : PdfEditorFactory.Create().RotatePages(pdf, paginas.Length == 0 ? new[] { 0 } : paginas, rot);

    /// Caixa (px do render) dos pixels "vermelhos", ou null.
    internal static Int32Rect? CaixaVermelha(RenderedPage p)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < p.HeightPx; y++)
        for (int x = 0; x < p.WidthPx; x++)
        {
            int i = (y * p.WidthPx + x) * 4;
            byte bl = p.Bgra[i], g = p.Bgra[i + 1], rd = p.Bgra[i + 2];
            if (rd > 200 && g < 140 && bl < 140) { l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); }
        }
        return r < 0 ? null : new Int32Rect(l, t, r - l + 1, b - t + 1);
    }

    private static List<PdfCharacter> Palavra(TextPage tp, string w)
    {
        int i = tp.Text.IndexOf(w, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{w}' não está no texto: {tp.Text}");
        return tp.Characters.Skip(i).Take(w.Length).ToList();
    }

    /// Linha de texto horizontal escrita na página SEM rotação (`TextoEmPe(0)` de PaginasGiradasVmTests).
    private static byte[] ComTexto() => PdfEditorFactory.Create().ApplyTextCorrection(Fixtures.NoText(),
        new TextCorrection(0, new PdfQuad(20, 20, 21, 21), "Endereco: RUA ANTIGA 123",
            TextFontFamily.Serif, false, false, 14, 0xFF000000u, 300, 72, 0xFFFFFFFFu));

    /// Seleciona `w` na página 0 pelo gesto (centro do 1º ao centro do último caractere, em px de tela).
    private static (List<PdfCharacter> Cs, Rect TelaPalavra) Selecionar(DocumentViewModel d, string w)
    {
        var page = d.Pages[0];
        page.BeginSelection(new Point(1, 1)); page.ClearSelection();
        var cs = Palavra(page.TextPageCache!, w);
        double s = d.Zoom * PageViewModel.PtToPx;
        Point Centro(PdfCharacter c) => new((c.LeftPt + c.RightPt) / 2 * s, (page.HeightPt - (c.BottomPt + c.TopPt) / 2) * s);
        page.BeginSelection(Centro(cs[0]));
        page.UpdateSelection(Centro(cs[^1]));
        Assert.Equal(w, page.SelectedText?.Trim());
        var tela = PageViewModel.PointRectToScreenRect(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt),
            cs.Max(c => c.RightPt), cs.Max(c => c.TopPt), d.Zoom, page.HeightPt);
        return (cs, tela);
    }

    // ---- A1: sublinhado/tachado em texto que corre na VERTICAL na tela (página girada com "Girar") ---------

    [Theory]
    [InlineData(0, AnnotationKind.Underline)]
    [InlineData(0, AnnotationKind.Strikeout)]
    [InlineData(90, AnnotationKind.Underline)]
    [InlineData(90, AnnotationKind.Strikeout)]
    [InlineData(180, AnnotationKind.Underline)]
    [InlineData(180, AnnotationKind.Strikeout)]
    [InlineData(270, AnnotationKind.Underline)]
    [InlineData(270, AnnotationKind.Strikeout)]
    public async Task A1_SublinhadoTachado_TextoQueGirouComAPagina_LinhaAoLongoDaPalavra(int rot, AnnotationKind kind)
    {
        using var vm = Abrir(Girada(ComTexto(), rot));
        var d = vm.D;
        var (_, palavra) = Selecionar(d, "ANTIGA");
        d.SelectedMarkupColorArgb = Vermelho;

        await d.ApplyMarkupCommand.ExecuteAsync(kind);

        Assert.Empty(vm.Errors);
        double s = d.Zoom * PageViewModel.PtToPx;
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        var c = CaixaVermelha(r.RenderPage(0, s));
        Assert.NotNull(c);
        var linha = new Rect(c!.Value.X, c.Value.Y, c.Value.Width, c.Value.Height);
        bool vertical = rot is 90 or 270; // o texto corre na vertical NA TELA
        string ctx = $"{kind} rot {rot}: linha {linha}, palavra {palavra}";
        if (vertical)
        {
            Assert.True(linha.Height > 4 * linha.Width, $"{ctx} — deveria ser uma linha VERTICAL (ao longo do texto)");
            Assert.True(linha.Height >= 0.8 * palavra.Height, $"{ctx} — a linha deveria cobrir a palavra inteira");
            Assert.InRange(linha.Top, palavra.Top - 4, palavra.Top + 0.2 * palavra.Height);
        }
        else
        {
            Assert.True(linha.Width > 4 * linha.Height, $"{ctx} — deveria ser uma linha HORIZONTAL (ao longo do texto)");
            Assert.True(linha.Width >= 0.8 * palavra.Width, $"{ctx} — a linha deveria cobrir a palavra inteira");
            Assert.InRange(linha.Left, palavra.Left - 4, palavra.Left + 0.2 * palavra.Width);
        }
        // Dentro da faixa da palavra (sublinhado: no "pé" do texto; tachado: no meio).
        double meio = vertical ? linha.Left + linha.Width / 2 : linha.Top + linha.Height / 2;
        double ini = vertical ? palavra.Left : palavra.Top, tam = vertical ? palavra.Width : palavra.Height;
        double rel = (meio - ini) / tam; // 0 = borda esquerda/de cima da palavra na tela, 1 = direita/de baixo
        // "pé" do texto na tela: rot 0 = embaixo (1), 90 = à esquerda (0), 180 = em cima (0), 270 = à direita (1)
        double pe = rot is 0 or 270 ? 1 : 0;
        if (kind == AnnotationKind.Strikeout) Assert.InRange(rel, 0.25, 0.75);
        else Assert.InRange(Math.Abs(rel - pe), 0, 0.3);
    }

    [Theory] // controle: texto escrito DEPOIS de girar (em pé na tela) continua com o traço horizontal
    [InlineData(90)]
    [InlineData(270)]
    public async Task A1_Controle_TextoEmPeNaTela_TracoHorizontal(int rot)
    {
        var pdf = PdfEditorFactory.Create().ApplyTextCorrection(Girada(Fixtures.NoText(), rot),
            new TextCorrection(0, new PdfQuad(20, 20, 21, 21), "Endereco: RUA ANTIGA 123",
                TextFontFamily.Serif, false, false, 14, 0xFF000000u, 300, 72, 0xFFFFFFFFu));
        using var vm = Abrir(pdf);
        var d = vm.D;
        var (_, palavra) = Selecionar(d, "ANTIGA");
        d.SelectedMarkupColorArgb = Vermelho;

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Underline);

        Assert.Empty(vm.Errors);
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        var c = CaixaVermelha(r.RenderPage(0, d.Zoom * PageViewModel.PtToPx))!.Value;
        Assert.True(c.Width > 4 * c.Height && c.Width >= 0.8 * palavra.Width, $"rot {rot}: {c} x palavra {palavra}");
        await d.RefreshAnnotationsByPageAsync();
        Assert.Null(Assert.Single(d.AnnotationsByPage[0]).ConteudoRotacaoGraus);
    }

    [Fact] // Highlight não muda: nunca recebe ConteudoRotacaoGraus
    public async Task A1_Realce_TextoVertical_SemConteudoRotacao()
    {
        using var vm = Abrir(Girada(ComTexto(), 90));
        var d = vm.D;
        Selecionar(d, "ANTIGA");
        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        Assert.Null(Assert.Single(d.AnnotationsByPage[0]).ConteudoRotacaoGraus);
    }

    // ---- Fix round 1 (A1): a direção vem da ORIENTAÇÃO dos glifos (motor), não da ordem dos caracteres -------

    /// Uma linha cujos glifos (em pé) são desenhados da DIREITA para a ESQUERDA — a ordem de texto que o PDFium
    /// devolve corre para a esquerda, como num texto hebraico/árabe. `hebraico` usa a Arial do Windows (glifos
    /// hebraicos reais); senão, letras latinas posicionadas uma a uma da direita para a esquerda.
    private static byte[] LinhaDaDireitaParaAEsquerda(bool hebraico)
    {
        using var ms = new MemoryStream();
        using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfWriter(ms)))
        {
            var page = doc.AddNewPage(iText.Kernel.Geom.PageSize.A4);
            var font = hebraico
                ? iText.Kernel.Font.PdfFontFactory.CreateFont(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"), iText.IO.Font.PdfEncodings.IDENTITY_H)
                : iText.Kernel.Font.PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
            string texto = hebraico ? "שלוםעולם" : "ANTIGAXY";
            var cv = new iText.Kernel.Pdf.Canvas.PdfCanvas(page);
            double x = 400;
            foreach (char c in texto) // 1º caractere LÓGICO mais à direita
            {
                double w = font.GetWidth(c.ToString(), 18);
                x -= w + 1;
                cv.BeginText().SetFontAndSize(font, 18).MoveText(x, 500).ShowText(c.ToString()).EndText();
            }
        }
        return ms.ToArray();
    }

    // Nota: letras LATINAS desenhadas da direita para a esquerda não reproduzem o caso — o PDFium as devolve em ordem
    // visual (esquerda→direita); só a escrita RTL de verdade (hebraico) sai em ordem lógica da direita para a esquerda.
    [Theory] // RTL na página em pé: sublinhado no PÉ do texto (antes: a ordem dos centros dava 180 = "overline")
    [InlineData(true)]
    public async Task A1r1_TextoDaDireitaParaAEsquerda_SublinhadoNoPe(bool hebraico)
    {
        if (hebraico && !File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"))) return; // máquina sem a Arial: só o caso latino
        using var vm = Abrir(LinhaDaDireitaParaAEsquerda(hebraico));
        var d = vm.D;
        var page = d.Pages[0];
        page.BeginSelection(new Point(1, 1)); page.ClearSelection();
        var cs = page.TextPageCache!.Characters.Where(c => c.RightPt - c.LeftPt > 0.5).ToList();
        Assert.True(cs.Count >= 6, $"caracteres: {cs.Count}");
        double s = d.Zoom * PageViewModel.PtToPx;
        Point Centro(PdfCharacter c) => new((c.LeftPt + c.RightPt) / 2 * s, (page.HeightPt - (c.BottomPt + c.TopPt) / 2) * s);
        page.BeginSelection(Centro(cs[0]));
        page.UpdateSelection(Centro(cs[^1]));
        Assert.True((cs[0].LeftPt + cs[0].RightPt) / 2 > (cs[^1].LeftPt + cs[^1].RightPt) / 2,
            "pré-condição: a ordem de texto corre da direita para a esquerda");
        var palavra = PageViewModel.PointRectToScreenRect(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt),
            cs.Max(c => c.RightPt), cs.Max(c => c.TopPt), d.Zoom, page.HeightPt);
        d.SelectedMarkupColorArgb = Vermelho;

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Underline);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        Assert.Null(Assert.Single(d.AnnotationsByPage[0]).ConteudoRotacaoGraus);
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        var c = CaixaVermelha(r.RenderPage(0, s))!.Value;
        Assert.True(c.Width > 4 * c.Height, $"horizontal: {c}");
        Assert.True(c.Y + c.Height / 2.0 > palavra.Top + 0.7 * palavra.Height, $"no pé do texto: {c} x palavra {palavra}");
    }

    [Theory] // seleção arrastada de TRÁS PARA A FRENTE (do último ao primeiro caractere): mesma direção
    [InlineData(90)]
    [InlineData(270)]
    public async Task A1r1_SelecaoArrastadaAoContrario_LinhaAoLongoDoTexto(int rot)
    {
        using var vm = Abrir(Girada(ComTexto(), rot));
        var d = vm.D;
        var page = d.Pages[0];
        page.BeginSelection(new Point(1, 1)); page.ClearSelection();
        var cs = Palavra(page.TextPageCache!, "ANTIGA");
        double s = d.Zoom * PageViewModel.PtToPx;
        Point Centro(PdfCharacter c) => new((c.LeftPt + c.RightPt) / 2 * s, (page.HeightPt - (c.BottomPt + c.TopPt) / 2) * s);
        page.BeginSelection(Centro(cs[^1]));
        page.UpdateSelection(Centro(cs[0]));
        Assert.Equal("ANTIGA", page.SelectedText?.Trim());
        d.SelectedMarkupColorArgb = Vermelho;

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Strikeout);

        Assert.Empty(vm.Errors);
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        var c = CaixaVermelha(r.RenderPage(0, s))!.Value;
        Assert.True(c.Height > 4 * c.Width, $"rot {rot}: vertical (ao longo do texto): {c}");
    }

    [Fact] // seleção de 2 linhas na página girada (incl. a "quebra de linha" de 1 caractere + 1): as duas ao longo do texto
    public async Task A1r1_SelecaoMultiLinha_PaginaGirada_TracosAoLongoDoTexto()
    {
        var ed = PdfEditorFactory.Create();
        byte[] pdf = ed.ApplyTextCorrection(ComTexto(), new TextCorrection(0, new PdfQuad(20, 20, 21, 21), "Segunda linha X",
            TextFontFamily.Serif, false, false, 14, 0xFF000000u, 300, 48, 0xFFFFFFFFu));
        using var vm = Abrir(Girada(pdf, 90));
        var d = vm.D;
        var page = d.Pages[0];
        page.BeginSelection(new Point(1, 1)); page.ClearSelection();
        var tp = page.TextPageCache!;
        var a = Palavra(tp, "123")[^1]; // último caractere da 1ª linha
        var b = Palavra(tp, "Segunda")[0]; // 1º caractere da 2ª linha
        double s = d.Zoom * PageViewModel.PtToPx;
        Point Centro(PdfCharacter c) => new((c.LeftPt + c.RightPt) / 2 * s, (page.HeightPt - (c.BottomPt + c.TopPt) / 2) * s);
        page.BeginSelection(Centro(a));
        page.UpdateSelection(Centro(b));
        d.SelectedMarkupColorArgb = Vermelho;

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Underline);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        Assert.Equal(90, Assert.Single(d.AnnotationsByPage[0]).ConteudoRotacaoGraus);
    }

    /// A4 com "ANTIGA PALAVRA" em /F99 SEM /F99 em /Resources (mesma página de FonteAusenteTests do motor), girada `rot`.
    private static byte[] PaginaFonteAusente(int rot)
    {
        using var ms = new MemoryStream();
        using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfWriter(ms)))
        {
            var page = doc.AddNewPage(iText.Kernel.Geom.PageSize.A4);
            page.GetFirstContentStream().SetData(System.Text.Encoding.ASCII.GetBytes("BT /F99 18 Tf 100 500 Td (ANTIGA PALAVRA) Tj ET"));
            if (rot != 0) page.SetRotation(rot);
        }
        return ms.ToArray();
    }

    [Theory] // fix round 2: página com /F99 ausente de /Resources (o PDFium tolera; o iText lança NRE crua) — o
    // sublinhado/tachado é gravado "em pé" em vez de derrubar o comando com "erro inesperado"
    [InlineData(0, AnnotationKind.Underline)]
    [InlineData(0, AnnotationKind.Strikeout)]
    [InlineData(90, AnnotationKind.Underline)]
    [InlineData(90, AnnotationKind.Strikeout)]
    public async Task A1r2_FonteAusenteNosRecursos_MarcacaoGravadaSemErro(int rot, AnnotationKind kind)
    {
        using var vm = Abrir(PaginaFonteAusente(rot));
        var d = vm.D;
        var (_, _) = Selecionar(d, "ANTIGA");
        d.SelectedMarkupColorArgb = Vermelho;

        await d.ApplyMarkupCommand.ExecuteAsync(kind);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        Assert.Equal(kind, Assert.Single(d.AnnotationsByPage[0]).Kind);
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        Assert.NotNull(CaixaVermelha(r.RenderPage(0, d.Zoom * PageViewModel.PtToPx)));
    }

    [Theory] // o motor, direto: glifos do texto escrito em pé e girado junto com a página
    [InlineData(0, null)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    public void A1r1_Motor_DirecaoDoTexto(int rot, int? esperado)
    {
        var pdf = Girada(ComTexto(), rot);
        using var r = new PdfDocumentRenderer(pdf);
        var tp = r.GetTextPage(0);
        var cs = Palavra(tp, "ANTIGA");
        var quad = new PdfQuad(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt), cs.Max(c => c.RightPt), cs.Max(c => c.TopPt));
        Assert.Equal(esperado, PdfEditorFactory.Create().DirecaoDoTexto(pdf, 0, new[] { quad }));
        Assert.Null(PdfEditorFactory.Create().DirecaoDoTexto(pdf, 0, new[] { new PdfQuad(1, 1, 5, 5) })); // sem glifos
    }

    // ---- A2: replicar/colar para página MENOR que a anotação (paisagem -> retrato) ------------------------

    /// 2 páginas A4: a 1ª girada 90° (842×595 na tela), a 2ª retrato (595×842) — com `a` na 1ª.
    private static byte[] PaisagemERetrato(AnnotationData a)
    {
        var ed = PdfEditorFactory.Create();
        byte[] dois = ed.RotatePages(ed.ExtractPages(Fixtures.ThirtyPages(), new[] { 0, 1 }), new[] { 0 }, 90);
        return ed.AddAnnotation(dois, a);
    }

    public static TheoryData<string> KindsLargos => new() { "Line", "Ink", "Highlight", "FreeText" };

    private static AnnotationData Larga(string kind) => kind switch
    {
        "Line" => new AnnotationData
        {
            Kind = AnnotationKind.Line, PageIndex = 0, LeftPt = 60, BottomPt = 300, RightPt = 780, TopPt = 300,
            LineStartPt = new PdfPoint(60, 300), LineEndPt = new PdfPoint(780, 300), ColorArgb = Vermelho, StrokeWidthPt = 3,
        },
        "Ink" => new AnnotationData
        {
            Kind = AnnotationKind.Ink, PageIndex = 0, LeftPt = 60, BottomPt = 280, RightPt = 780, TopPt = 340, ColorArgb = Vermelho,
            InkStrokes = new[] { (IReadOnlyList<PdfPoint>)Enumerable.Range(0, 37).Select(i => new PdfPoint(60 + i * 20, i % 2 == 0 ? 280 : 340)).ToList() },
        },
        "Highlight" => new AnnotationData
        {
            Kind = AnnotationKind.Highlight, PageIndex = 0, LeftPt = 60, BottomPt = 270, RightPt = 780, TopPt = 320, ColorArgb = Vermelho,
            Quads = new[] { new PdfQuad(60, 300, 780, 320), new PdfQuad(60, 270, 400, 290) },
        },
        _ => new AnnotationData
        {
            Kind = AnnotationKind.FreeText, PageIndex = 0, LeftPt = 60, BottomPt = 400, RightPt = 780, TopPt = 460,
            Content = "Caixa larga", ColorArgb = Vermelho, FontSizePt = 12,
        },
    };

    [Theory]
    [MemberData(nameof(KindsLargos))]
    public async Task A2_Replicar_PaisagemParaRetrato_TodaAGeometriaCabeNaPaginaENoBbox(string kind)
    {
        using var vm = Abrir(PaisagemERetrato(Larga(kind)));
        var d = vm.D;
        await d.RefreshAnnotationsByPageAsync();
        var orig = Assert.Single(d.AnnotationsByPage[0]);
        d.SelectedAnnotation = orig;

        await d.ReplicarProximaPaginaCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var c = Assert.Single(d.AnnotationsByPage[1]);
        double pw = d.Pages[1].WidthPt, ph = d.Pages[1].HeightPt;
        AssertGeometriaDentro(c, pw, ph, kind);
        // escala uniforme: proporção do bbox preservada (FreeText pode crescer na altura para caber o texto)
        double w0 = orig.RightPt - orig.LeftPt, w1 = c.RightPt - c.LeftPt;
        Assert.InRange(w1, pw - 2, pw + 0.5);
        if (kind != "FreeText" && orig.TopPt - orig.BottomPt > 1)
            Assert.Equal((orig.TopPt - orig.BottomPt) / w0, (c.TopPt - c.BottomPt) / w1, 2);

        if (kind == "FreeText") return;
        double s = d.Zoom * PageViewModel.PtToPx;
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        var caixa = CaixaVermelha(r.RenderPage(1, s));
        Assert.NotNull(caixa);
        Assert.True(caixa!.Value.Width >= 0.9 * pw * s, $"{kind}: a réplica deveria ocupar a largura da página: {caixa}");
        var telaBbox = PageViewModel.PointRectToScreenRect(c.LeftPt, c.BottomPt, c.RightPt, c.TopPt, d.Zoom, ph);
        telaBbox.Inflate(6, 6);
        Assert.True(telaBbox.Contains(new Rect(caixa.Value.X, caixa.Value.Y, caixa.Value.Width, caixa.Value.Height)),
            $"{kind}: pixels {caixa} fora do bbox {telaBbox}");
    }

    internal static void AssertGeometriaDentro(AnnotationData c, double pw, double ph, string ctx)
    {
        const double tol = 0.6;
        bool Dentro(double x, double y) => x >= c.LeftPt - tol && x <= c.RightPt + tol && y >= c.BottomPt - tol && y <= c.TopPt + tol
            && x >= -tol && x <= pw + tol && y >= -tol && y <= ph + tol;
        Assert.True(c.LeftPt >= -tol && c.RightPt <= pw + tol && c.BottomPt >= -tol && c.TopPt <= ph + tol, $"{ctx}: bbox fora da página");
        if (c.LineStartPt is { } a0) Assert.True(Dentro(a0.XPt, a0.YPt), $"{ctx}: início da linha {a0} fora");
        if (c.LineEndPt is { } e0) Assert.True(Dentro(e0.XPt, e0.YPt), $"{ctx}: fim da linha {e0} fora");
        foreach (var st in c.InkStrokes ?? Array.Empty<IReadOnlyList<PdfPoint>>())
            foreach (var p in st) Assert.True(Dentro(p.XPt, p.YPt), $"{ctx}: ponto de tinta {p} fora de {c.LeftPt:F1}..{c.RightPt:F1}");
        foreach (var q in c.Quads ?? Array.Empty<PdfQuad>())
            Assert.True(Dentro(q.LeftPt, q.BottomPt) && Dentro(q.RightPt, q.TopPt), $"{ctx}: quad {q} fora");
    }

    [Fact] // a função pura: cabe -> só translada; não cabe -> escala uniforme em torno do canto
    public void A2_EncaixarNaPagina_TransladaOuEscala()
    {
        var linha = Larga("Line");
        var t = DocumentViewModel.EncaixarNaPagina(linha with { RightPt = 300, LineEndPt = new PdfPoint(300, 300) }, 72, 312, 595, 842);
        Assert.Equal(new PdfPoint(72, 312), t.LineStartPt); Assert.Equal(new PdfPoint(312, 312), t.LineEndPt);
        var e = DocumentViewModel.EncaixarNaPagina(linha, 60, 300, 595, 842);
        Assert.Equal(0, e.LeftPt, 3); Assert.Equal(595, e.RightPt, 3);
        Assert.Equal(0, e.LineStartPt!.Value.XPt, 3); Assert.Equal(595, e.LineEndPt!.Value.XPt, 3);
        AssertGeometriaDentro(DocumentViewModel.EncaixarNaPagina(Larga("Ink"), 60, 280, 595, 842), 595, 842, "ink");
        AssertGeometriaDentro(DocumentViewModel.EncaixarNaPagina(Larga("Highlight"), 60, 270, 595, 842), 595, 842, "highlight");
    }

    // ---- A3: colar DEPOIS de "Girar" a página ----------------------------------------------------------------

    private static async Task Girar90(VmTemporaria vm)
    {
        using (var org = new OrganizerViewModel(vm.D.Session, PdfEditorFactory.Create(), vm.Errors.Add, () => true))
        {
            org.ToggleSelect(0, ctrl: false);
            await org.RotateSelectedCommand.ExecuteAsync(null);
        }
        Assert.Empty(vm.Errors);
        await vm.D.RefreshAnnotationsByPageAsync();
    }

    [Fact] // o cenário do revisor: retângulo (60,700)-(180,760) na A4 retrato -> copiar -> Girar 90° -> colar
    public async Task A3_ColarDepoisDeGirar_CopiaMais12ptDoOriginalNaTela()
    {
        using var vm = Abrir(Fixtures.A4());
        var d = vm.D;
        d.ToggleRectangleToolCommand.Execute(null);
        d.SelectedMarkupColorArgb = Vermelho;
        await d.CommitDrawingAsync(0, new[] { new PdfPoint(60, 760), new PdfPoint(180, 700) });
        await d.RefreshAnnotationsByPageAsync();
        d.SelectedAnnotation = d.HitTestAnnotation(0, 120, 730);
        Assert.NotNull(d.SelectedAnnotation);
        d.CopyAnnotationCommand.Execute(null);

        await Girar90(vm);
        var orig = Assert.Single(d.AnnotationsByPage[0]); // girou junto: 700..760 × 415..535 na tela
        Assert.Equal(700, orig.LeftPt, 1); Assert.Equal(415, orig.BottomPt, 1);

        await d.PasteAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var copia = d.AnnotationsByPage[0].Single(a => a.Id != orig.Id);
        Assert.Equal(orig.LeftPt + 12, copia.LeftPt, 1); Assert.Equal(orig.BottomPt + 12, copia.BottomPt, 1);
        Assert.Equal(orig.RightPt + 12, copia.RightPt, 1); Assert.Equal(orig.TopPt + 12, copia.TopPt, 1);

        // colar de novo (sem girar): de novo +12 do ORIGINAL (o clipboard já está no frame novo)
        await d.PasteAnnotationCommand.ExecuteAsync(null);
        await d.RefreshAnnotationsByPageAsync();
        Assert.Equal(2, d.AnnotationsByPage[0].Count(a => Math.Abs(a.LeftPt - (orig.LeftPt + 12)) < 1 && Math.Abs(a.BottomPt - (orig.BottomPt + 12)) < 1));
    }

    // ---- A4: Linha/Seta reta selecionada pela folga, mas overlay com altura (ou largura) zero -----------------

    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(90, true)]
    public async Task A4_LinhaRetaSelecionada_OverlayVisivelSobreALinha(int rot, bool horizontal)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        d.ToggleLineToolCommand.Execute(null);
        var (a, b) = horizontal ? (new PdfPoint(100, 400), new PdfPoint(300, 400)) : (new PdfPoint(200, 300), new PdfPoint(200, 500));
        await d.CommitDrawingAsync(0, new[] { a, b });
        await d.RefreshAnnotationsByPageAsync();
        var hit = horizontal ? d.HitTestAnnotation(0, 200, 401) : d.HitTestAnnotation(0, 201, 400); // 1 pt fora
        Assert.NotNull(hit);
        d.SelectedAnnotation = hit;

        var page = d.Pages[0];
        var o = page.AnnotationSelectionRect;
        Assert.True(o.Width >= PageViewModel.MinAnnotationOverlayPx && o.Height >= PageViewModel.MinAnnotationOverlayPx, $"overlay {o}");
        var linha = PageViewModel.PointRectToScreenRect(a.XPt, Math.Min(a.YPt, b.YPt), b.XPt, Math.Max(a.YPt, b.YPt), d.Zoom, page.HeightPt);
        Assert.InRange(linha.Left, o.Left - 0.01, o.Right + 0.01); Assert.InRange(linha.Right, o.Left - 0.01, o.Right + 0.01);
        Assert.InRange(linha.Top, o.Top - 0.01, o.Bottom + 0.01); Assert.InRange(linha.Bottom, o.Top - 0.01, o.Bottom + 0.01);
        // centrado na linha: a linha fica no meio do retângulo fino
        Assert.Equal(horizontal ? linha.Top : linha.Left, horizontal ? o.Top + o.Height / 2 : o.Left + o.Width / 2, 1);

        d.Zoom = 2.0; // reconversão no zoom novo mantém o mínimo
        Assert.True(page.AnnotationSelectionRect.Height >= PageViewModel.MinAnnotationOverlayPx
            && page.AnnotationSelectionRect.Width >= PageViewModel.MinAnnotationOverlayPx, $"zoom 2x: {page.AnnotationSelectionRect}");
    }

    [Fact] // a prévia do arrasto (PdfViewerControl) usa o mesmo helper: base com tamanho mínimo; retângulo normal intacto
    public void A4_AnnotationOverlayRect_TamanhoMinimoSoQuandoPreciso()
    {
        var linha = new AnnotationData { Kind = AnnotationKind.Line, PageIndex = 0, LeftPt = 100, BottomPt = 400, RightPt = 300, TopPt = 400 };
        var r = PageViewModel.AnnotationOverlayRect(linha, 1.0, 842);
        Assert.True(r.Height >= PageViewModel.MinAnnotationOverlayPx);
        var caixa = linha with { Kind = AnnotationKind.Rectangle, TopPt = 500 };
        Assert.Equal(PageViewModel.PointRectToScreenRect(100, 400, 300, 500, 1.0, 842), PageViewModel.AnnotationOverlayRect(caixa, 1.0, 842));
    }

    // ---- A5: tamanho da fonte digitado em pt-BR ("10,5") virava 105 pt ------------------------------------

    [Theory]
    [InlineData("10,5", 10.5)]
    [InlineData("10.5", 10.5)]
    [InlineData(" 12 ", 12.0)]
    [InlineData("8", 8.0)]
    [InlineData("200", 200.0)]
    [InlineData("1.000", null)] // milhar não é tamanho de fonte
    [InlineData("9,125", 9.125)] // fix round 1: 3 casas (o que FormatFontSize exibe) voltam iguais
    [InlineData("10.125", 10.125)]
    [InlineData("10,1255", null)]
    [InlineData("1,000", null)]
    [InlineData("105", 105.0)]
    [InlineData("0", null)]
    [InlineData("201", null)]
    [InlineData("abc", null)]
    [InlineData("10,5,1", null)]
    [InlineData("", null)]
    public void A5_ParseFontSize_ptBRInvariante(string raw, double? esperado) =>
        Assert.Equal(esperado, mPdf.App.Views.AnnotationTextDialog.ParseFontSize(raw));

    [Theory]
    [InlineData(10.5, "10,5")]
    [InlineData(12.0, "12")]
    [InlineData(9.25, "9,25")]
    [InlineData(9.125, "9,125")]
    public void A5_FormatFontSize_VirgulaPtBR(double v, string esperado) =>
        Assert.Equal(esperado, mPdf.App.Views.AnnotationTextDialog.FormatFontSize(v));

    [Theory] // fix round 1: exibir e confirmar sem mexer devolve o MESMO tamanho
    [InlineData(10.5)]
    [InlineData(9.125)]
    [InlineData(11.333)]
    [InlineData(12.0)]
    public void A5_RoundTrip_ExibidoEConfirmado(double v) =>
        Assert.Equal(v, mPdf.App.Views.AnnotationTextDialog.ParseFontSize(mPdf.App.Views.AnnotationTextDialog.FormatFontSize(v)));

    [Theory] // diálogo real (STA): mostra "10,5" e devolve 10,5 pt tanto para "10,5" quanto para "10.5"
    [InlineData("10,5")]
    [InlineData("10.5")]
    public void A5_Dialogo_TamanhoDecimal_DevolveOValorCerto(string digitado)
    {
        Exception? erro = null;
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                var dlg = new mPdf.App.Views.AnnotationTextDialog("Editar", "x", showFormatting: true,
                    new AnnotationTextResult("x", 10.5, false, false, "Helvetica", 0xFF000000u));
                Assert.Equal("10,5", dlg.SizeCombo.Text);
                dlg.SizeCombo.Text = digitado;
                try
                {
                    typeof(mPdf.App.Views.AnnotationTextDialog).GetMethod("Ok_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .Invoke(dlg, new object?[] { null, null });
                }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
                Assert.Equal(10.5, dlg.ResultFormat!.FontSizePt);
            }
            catch (Exception ex) { erro = ex; }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(25)));
        if (erro is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(erro).Throw();
    }

    // ---- A6: cache de bytes da imagem relia o PDF 3x DENTRO do pino de edição --------------------------------

    /// Proxy do `IPdfEditor` REAL que conta `ReadAnnotations` (total e com a edição em voo — o pino).
    public class EditorContador : System.Reflection.DispatchProxy
    {
        internal IPdfEditor Real = null!;
        internal Func<bool> EmVoo = () => false;
        internal int Leituras, LeiturasNoPino;
        protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? args)
        {
            if (m!.Name == nameof(IPdfEditor.ReadAnnotations)) { Interlocked.Increment(ref Leituras); if (EmVoo()) Interlocked.Increment(ref LeiturasNoPino); }
            try { return m.Invoke(Real, args); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is { } inner)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
        }
        internal static (IPdfEditor Editor, EditorContador Contador) Criar()
        {
            var p = Create<IPdfEditor, EditorContador>();
            var c = (EditorContador)(object)p;
            c.Real = PdfEditorFactory.Create();
            return (p, c);
        }
    }

    [Fact]
    public async Task A6_ColocarEMoverImagem_NenhumaLeituraDoPdfDentroDoPino_EOCacheContinuaCerto()
    {
        var (editor, cont) = EditorContador.Criar();
        using var vm = Abrir(Fixtures.A4(), editor);
        var d = vm.D;
        cont.EmVoo = () => d.Session.IsEditInFlight;
        await d.RefreshAnnotationsByPageAsync();
        cont.Leituras = cont.LeiturasNoPino = 0;

        d.ToggleStampTool(ImagemCacheAposDesfazerTests.ImagemA);
        await d.PlaceStampAtAsync(0, 200, 500);
        Assert.Empty(vm.Errors);
        Assert.Equal(0, cont.LeiturasNoPino); // antes: 3 (2 do set-diff + 1 refresh), todas com o pino armado

        await d.RefreshAnnotationsByPageAsync();
        var a = Assert.Single(d.AnnotationsByPage[0]);
        d.SelectedAnnotation = a;
        cont.LeiturasNoPino = 0;
        await d.MoveSelectedAnnotationAsync(a.LeftPt + 40, a.BottomPt - 60);
        Assert.Empty(vm.Errors);
        Assert.Equal(0, cont.LeiturasNoPino); // antes: 3

        // o cache segue mapeando a imagem certa: mover de novo (lift com os bytes do cache) redesenha A em pé
        await d.RefreshAnnotationsByPageAsync();
        var m = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(a.Id, m.Id);
        d.SelectedAnnotation = m;
        await d.MoveSelectedAnnotationAsync(m.LeftPt + 10, m.BottomPt + 10);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var m2 = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(m.LeftPt + 10, m2.LeftPt, 1);
        Assert.True(ImagemCacheAposDesfazerTests.Vermelho(ImagemCacheAposDesfazerTests.CorEm(d.Session.Snapshot, 0, m2.LeftPt + 6, m2.TopPt - 6)));
    }

    [Fact] // a caixa de imagem (arrastar para inserir) também não relê o PDF no pino
    public async Task A6_ImagemPelaCaixa_NenhumaLeituraNoPino()
    {
        var (editor, cont) = EditorContador.Criar();
        using var vm = Abrir(Fixtures.A4(), editor);
        var d = vm.D;
        cont.EmVoo = () => d.Session.IsEditInFlight;
        d.ToggleStampTool(ImagemCacheAposDesfazerTests.ImagemA);
        await d.BeginImageBoxPlacementAsync(0, new PdfPoint(100, 700));
        d.UpdateDrawTo(new PdfPoint(300, 600));
        cont.LeiturasNoPino = 0;
        await d.EndStampDrawAsync();
        Assert.Empty(vm.Errors);
        Assert.Equal(0, cont.LeiturasNoPino);
        await d.RefreshAnnotationsByPageAsync();
        var a = Assert.Single(d.AnnotationsByPage[0]);
        d.SelectedAnnotation = a;
        d.BeginImageEditBox(a); // só abre com os bytes no cache, sob o Id da imagem
        Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase);
        Assert.Empty(vm.Errors);
    }

    // ---- A7: linhas/setas gravadas até a v2.16 (sem /AP) ficam visíveis ao abrir, sem sujar o documento ---------

    private const uint Azul = 0xFF0000FFu;

    /// A4 com uma linha azul de 3 pt no formato da v2.16: sem /AP e com /Rect = envelope do /L.
    internal static byte[] LinhaV216(byte[]? basePdf = null, iText.Kernel.Pdf.WriterProperties? wp = null)
    {
        var com = PdfEditorFactory.Create().AddAnnotation(basePdf ?? Fixtures.A4(), new AnnotationData
        {
            Kind = AnnotationKind.Line, PageIndex = 0, LeftPt = 100, BottomPt = 300, RightPt = 400, TopPt = 300,
            LineStartPt = new PdfPoint(100, 300), LineEndPt = new PdfPoint(400, 300), ColorArgb = Azul, StrokeWidthPt = 3,
        });
        using var o = new MemoryStream();
        using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(new MemoryStream(com)),
                   new iText.Kernel.Pdf.PdfWriter(o, wp ?? new iText.Kernel.Pdf.WriterProperties())))
            foreach (var a in doc.GetPage(1).GetAnnotations().Where(a => iText.Kernel.Pdf.PdfName.Line.Equals(a.GetSubtype())))
            {
                var d = a.GetPdfObject();
                d.Remove(iText.Kernel.Pdf.PdfName.AP);
                var l = d.GetAsArray(iText.Kernel.Pdf.PdfName.L).ToFloatArray();
                d.Put(iText.Kernel.Pdf.PdfName.Rect, new iText.Kernel.Pdf.PdfArray(new[] { Math.Min(l[0], l[2]), Math.Min(l[1], l[3]), Math.Max(l[0], l[2]), Math.Max(l[1], l[3]) }));
            }
        return o.ToArray();
    }

    internal static int PixelsAzuis(byte[] pdf)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var p = r.RenderPage(0, 1.0);
        int n = 0;
        for (int i = 0; i < p.Bgra.Length; i += 4) if (p.Bgra[i] > 200 && p.Bgra[i + 1] < 90 && p.Bgra[i + 2] < 90) n++;
        return n;
    }

    private sealed class SemDialogo : IFileDialogService
    {
        public string? PickPdfToOpen() => null;
        public string? PickPdfToSaveAs(string currentPath) => null;
        public string? PickImageToImport() => null;
        public string? PickPdfToSave(string suggestedName) => null;
    }

    private sealed class NuncaFecha : IConfirmCloseService
    {
        public CloseConfirmation Confirm(string documentTitle) => CloseConfirmation.Cancel;
    }

    private static async Task<(MainViewModel Vm, List<string> Erros, string Dir)> AbrirNoApp(byte[] pdf, string nome)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpdf-f2-main-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, nome);
        File.WriteAllBytes(path, pdf);
        var erros = new List<string>();
        var vm = new MainViewModel(new SemDialogo(), new RecentFilesStore(System.IO.Path.Combine(dir, "recentes")), erros.Add,
            new AppConfig(System.IO.Path.Combine(dir, "cfg")) { Autor = "Teste" }, new NuncaFecha());
        await vm.OpenPath(path);
        return (vm, erros, dir);
    }

    private static void Limpar(MainViewModel vm, string dir)
    {
        foreach (var d in vm.Documents.ToList()) d.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A7_AbrirDocumentoDaV216_LinhaVisivel_NaoModificado_SemDesfazer()
    {
        byte[] v216 = LinhaV216();
        Assert.Equal(0, PixelsAzuis(v216)); // o defeito: invisível
        var (vm, erros, dir) = await AbrirNoApp(v216, "v216.pdf");
        try
        {
            Assert.Empty(erros);
            var doc = Assert.Single(vm.Documents);
            Assert.True(PixelsAzuis(doc.Session.Snapshot) > 300, "a linha da v2.16 deveria aparecer ao abrir");
            Assert.False(doc.Session.IsDirty);
            Assert.False(doc.Session.CanUndo);
            Assert.False(doc.CanUndo);
            Assert.Equal(v216, File.ReadAllBytes(System.IO.Path.Combine(dir, "v216.pdf"))); // o disco não é tocado
        }
        finally { Limpar(vm, dir); }
    }

    [Fact] // documento assinado: nada é reescrito (a linha segue invisível; assinatura intacta)
    public async Task A7_DocumentoAssinado_BytesIntocados()
    {
        byte[] assinado;
        using (var o = new MemoryStream())
        {
            using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(new MemoryStream(File.ReadAllBytes(System.IO.Path.Combine(Fixtures.Root, "fixture-carimbo.pdf")))),
                       new iText.Kernel.Pdf.PdfWriter(o), new iText.Kernel.Pdf.StampingProperties().UseAppendMode()))
            {
                var l = new iText.Kernel.Pdf.Annot.PdfLineAnnotation(new iText.Kernel.Geom.Rectangle(100, 100, 100, 50), new float[] { 100, 100, 200, 150 });
                l.SetColor(new float[] { 0, 0, 1 });
                doc.GetPage(1).AddAnnotation(l);
            }
            assinado = o.ToArray();
        }
        var (vm, erros, dir) = await AbrirNoApp(assinado, "assinado.pdf");
        try
        {
            Assert.Empty(erros);
            var doc = Assert.Single(vm.Documents);
            Assert.Equal(assinado, doc.Session.Snapshot);
            Assert.False(doc.Session.IsDirty);
        }
        finally { Limpar(vm, dir); }
    }

    [Fact] // senha de PROPRIETÁRIO (abre sem pedir senha): a regeneração não consegue reescrever -> abre como antes
    public async Task A7_SenhaDeProprietario_AbreComoAntes()
    {
        var wp = new iText.Kernel.Pdf.WriterProperties().SetStandardEncryption(null, System.Text.Encoding.ASCII.GetBytes("dono"),
            iText.Kernel.Pdf.EncryptionConstants.ALLOW_PRINTING, iText.Kernel.Pdf.EncryptionConstants.ENCRYPTION_AES_128);
        byte[] protegido = LinhaV216(wp: wp);
        Assert.Throws<PdfPasswordRequiredException>(() => PdfEditorFactory.Create().RegenerarAparenciasAusentes(protegido));
        var (vm, erros, dir) = await AbrirNoApp(protegido, "protegido.pdf");
        try
        {
            Assert.Empty(erros);
            var doc = Assert.Single(vm.Documents);
            Assert.Equal(protegido, doc.Session.Snapshot);
            Assert.False(doc.Session.IsDirty);
        }
        finally { Limpar(vm, dir); }
    }

    [Fact] // lote: os bytes ENTREGUES à assinatura já têm o /AP (a linha não fica invisível para sempre depois de assinar)
    public async Task A7_AssinarEmLote_RegeneraAntesDeAssinar()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpdf-f2-lote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string f = System.IO.Path.Combine(dir, "v216.pdf");
        File.WriteAllBytes(f, LinhaV216());
        try
        {
            var engine = new FakeBatchSigningEngine();
            var vm = new BatchSignViewModel(
                [new mPdf.Signing.SigningCertificateInfo(cert, IsRsa: true, "Teste", IsIcpBrasilPersonal: false, IsIcpBrasilCompany: false)],
                isPathOpen: _ => false, pickFiles: () => [f], signingEngine: engine);
            vm.AddFilesCommand.Execute(null);
            vm.SelectedCertificate = vm.Certificates[0];

            await vm.StartCommand.ExecuteAsync(null);

            Assert.True(Assert.Single(vm.Results).Succeeded);
            Assert.True(PixelsAzuis(Assert.Single(engine.Requests).Pdf) > 300, "a linha deveria ir visível para a assinatura");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---- A8: recusa do carimbo em página com /Rotate fora de múltiplo de 90 (E6) -----------------------------

    /// 2 páginas A4, a ÚLTIMA com /Rotate 135 (malformado).
    private static byte[] UltimaPagina135()
    {
        using var ms = new MemoryStream();
        using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfWriter(ms)))
        {
            doc.AddNewPage(iText.Kernel.Geom.PageSize.A4);
            doc.AddNewPage(iText.Kernel.Geom.PageSize.A4).GetPdfObject().Put(iText.Kernel.Pdf.PdfName.Rotate, new iText.Kernel.Pdf.PdfNumber(135));
        }
        return ms.ToArray();
    }

    [Fact] // lote (motor REAL): o carimbo vai sempre na última página — a mensagem não pode mandar "pôr em outra página"
    public async Task A8_Lote_UltimaPaginaComRotacaoForaDoPadrao_MensagemDoLote()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpdf-f2-lote135-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string f = System.IO.Path.Combine(dir, "torta.pdf");
        File.WriteAllBytes(f, UltimaPagina135());
        try
        {
            var vm = new BatchSignViewModel(
                [new mPdf.Signing.SigningCertificateInfo(cert, IsRsa: true, "Teste", IsIcpBrasilPersonal: false, IsIcpBrasilCompany: false)],
                isPathOpen: _ => false, pickFiles: () => [f]);
            vm.AddFilesCommand.Execute(null);
            vm.SelectedCertificate = vm.Certificates[0];
            vm.PlaceStamp = true;

            await vm.StartCommand.ExecuteAsync(null);

            var r = Assert.Single(vm.Results);
            Assert.False(r.Succeeded);
            Assert.Equal(BatchSignViewModel.RotacaoForaDoPadraoNoLote("torta.pdf", 135), r.Message);
            Assert.DoesNotContain("coloque o carimbo em outra página", r.Message); // a orientação do motor, que o lote não permite
            Assert.Contains("Sem carimbo", r.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void A8_Lote_TextoPtBR() => Assert.Equal(
        "torta.pdf: a última página tem uma rotação fora do padrão (135°) e o carimbo visível sairia torto. " +
        "Assine este arquivo com \"Sem carimbo\" (assinatura invisível) ou abra-o no mPDF e assine com o carimbo em outra página.",
        BatchSignViewModel.RotacaoForaDoPadraoNoLote("torta.pdf", 135));

    [Fact] // assinatura única (motor REAL): a recusa do motor chega ao usuário pelo _notifyError, nada é gravado
    public async Task A8_AssinarUmDocumento_CarimboNaPagina135_MostraAMensagemDoMotor()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpdf-f2-sign135-{Guid.NewGuid():N}.pdf");
        string cfg = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpdf-f2-sign135-cfg-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, UltimaPagina135());
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var erros = new List<string>();
        bool gravou = false;
        try
        {
            using var d = new DocumentViewModel(DocumentSession.Open(path), editor: PdfEditorFactory.Create(),
                config: new AppConfig(cfg), notifyError: erros.Add, notifyInfo: _ => { },
                signDialog: new FakeSignDialogService(new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true)),
                signingEngine: mPdf.Signing.SigningEngineFactory.Create(),
                confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
                listSigningCertificates: () => new[] { new mPdf.Signing.SigningCertificateInfo(cert, true, "Teste", false, false) },
                pickPdfToSave: _ => { gravou = true; return null; }, writeAllBytes: (_, _) => gravou = true,
                openSavedDocument: _ => Task.CompletedTask);

            await d.SignCommand.ExecuteAsync(null);
            await d.BeginStampBoxPlacementAsync(1, new PdfPoint(100, 100));
            d.UpdateDrawTo(new PdfPoint(300, 200));
            await d.EndStampDrawAsync();

            var msg = Assert.Single(erros);
            Assert.Contains("rotação fora do padrão (135°)", msg);
            Assert.Contains("coloque o carimbo em outra página", msg); // aqui o usuário PODE escolher a página
            Assert.False(gravou);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
            try { Directory.Delete(cfg, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- Fix round 1 (A3): só mapeia quando a página de destino É a página da cópia ---------------------------

    /// 2 páginas: p0 em pé com uma caixa de texto em 100,600–300,660; p1 com /Rotate 90.
    private static byte[] DuasPaginasComCaixa()
    {
        var ed = PdfEditorFactory.Create();
        byte[] dois = ed.RotatePages(ed.ExtractPages(Fixtures.ThirtyPages(), new[] { 0, 1 }), new[] { 1 }, 90);
        return ed.AddAnnotation(dois, new AnnotationData
        {
            Kind = AnnotationKind.FreeText, PageIndex = 0, LeftPt = 100, BottomPt = 600, RightPt = 300, TopPt = 660,
            Content = "Caixa", ColorArgb = Vermelho, FontSizePt = 12,
        });
    }

    public static TheoryData<string> OutraPaginaNoIndice => new() { "MoverP1ParaEsquerda", "ExcluirP0", "InserirGiradaEm0" };

    [Theory] // a página 0 do colar deixou de ser a página da cópia: cola em pé, como antes da F2 (+12, encaixada)
    [MemberData(nameof(OutraPaginaNoIndice))]
    public async Task A3r1_PaginaDoIndiceMudou_ColaSemMapear(string operacao)
    {
        using var vm = Abrir(DuasPaginasComCaixa());
        var d = vm.D;
        await d.RefreshAnnotationsByPageAsync();
        var orig = Assert.Single(d.AnnotationsByPage[0]);
        d.SelectedAnnotation = orig;
        d.CopyAnnotationCommand.Execute(null);

        using (var org = new OrganizerViewModel(d.Session, PdfEditorFactory.Create(), vm.Errors.Add, () => true))
        {
            switch (operacao)
            {
                case "MoverP1ParaEsquerda": org.ToggleSelect(1, ctrl: false); await org.MoveSelectionLeftCommand.ExecuteAsync(null); break;
                case "ExcluirP0": org.ToggleSelect(0, ctrl: false); await org.DeleteSelectedCommand.ExecuteAsync(null); break;
                default:
                    var ed = PdfEditorFactory.Create();
                    d.Session.ApplyEdit(ed.InsertPages(d.Session.Snapshot, ed.RotatePages(Fixtures.A4(), new[] { 0 }, 90), 0));
                    break;
            }
        }
        Assert.Empty(vm.Errors);
        Assert.Equal(90, RotacaoGravada.Ler(d.Session.Snapshot)[0]); // o índice 0 agora é uma página girada
        await d.RefreshAnnotationsByPageAsync();
        var antes = d.AnnotationsByPage[0].Select(a => a.Id).ToHashSet();

        await d.PasteAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var copia = d.AnnotationsByPage[0].Single(a => !antes.Contains(a.Id));
        Assert.Null(copia.ConteudoRotacaoGraus); // em pé (antes: deitada, 90)
        Assert.Equal(orig.LeftPt + 12, copia.LeftPt, 1);
        Assert.Equal(orig.RightPt - orig.LeftPt, copia.RightPt - copia.LeftPt, 1);
        Assert.True(copia.TopPt <= d.Pages[0].HeightPt + 0.5, $"fora da página: {copia.TopPt} > {d.Pages[0].HeightPt}");
    }

    [Fact] // a mesma página que só GIROU continua mapeada; desfazer o giro volta ao frame da cópia
    public async Task A3r1_GirarEDesfazer_ContinuaMapeando()
    {
        using var vm = Abrir(Fixtures.A4());
        var d = vm.D;
        d.ToggleRectangleToolCommand.Execute(null);
        await d.CommitDrawingAsync(0, new[] { new PdfPoint(60, 760), new PdfPoint(180, 700) });
        await d.RefreshAnnotationsByPageAsync();
        d.SelectedAnnotation = d.HitTestAnnotation(0, 120, 730);
        d.CopyAnnotationCommand.Execute(null);
        await Girar90(vm);
        d.UndoCommand.Execute(null); // desfaz o giro: página em pé de novo
        await d.RefreshAnnotationsByPageAsync();
        var orig = Assert.Single(d.AnnotationsByPage[0]);

        await d.PasteAnnotationCommand.ExecuteAsync(null);

        await d.RefreshAnnotationsByPageAsync();
        var copia = d.AnnotationsByPage[0].Single(a => a.Id != orig.Id);
        Assert.Equal(orig.LeftPt + 12, copia.LeftPt, 1); Assert.Equal(orig.BottomPt + 12, copia.BottomPt, 1);
    }

    [Fact] // conteúdo orientado (caixa de texto): a cópia colada depois de Girar gira junto, como o original
    public async Task A3_ColarCaixaDeTextoDepoisDeGirar_ConteudoGiradoComoOOriginal()
    {
        using var vm = Abrir(Fixtures.A4());
        var d = vm.D;
        vm.Dialog.Result = "Texto";
        d.ToggleFreeTextToolCommand.Execute(null);
        await d.PlaceAnnotationAtAsync(0, 100, 600);
        await d.RefreshAnnotationsByPageAsync();
        d.SelectedAnnotation = Assert.Single(d.AnnotationsByPage[0]);
        d.CopyAnnotationCommand.Execute(null);

        await Girar90(vm);
        var orig = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(90, orig.ConteudoRotacaoGraus);

        await d.PasteAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var copia = d.AnnotationsByPage[0].Single(a => a.Id != orig.Id);
        Assert.Equal(90, copia.ConteudoRotacaoGraus);
        Assert.Equal(orig.RightPt - orig.LeftPt, copia.RightPt - copia.LeftPt, 1);
        Assert.Equal(orig.TopPt - orig.BottomPt, copia.TopPt - copia.BottomPt, 1);
        Assert.Equal(orig.LeftPt + 12, copia.LeftPt, 1); Assert.Equal(orig.BottomPt + 12, copia.BottomPt, 1);
    }
}
