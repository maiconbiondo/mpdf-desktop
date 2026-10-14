using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using mPdf.Rendering;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 2: AddAnnotation/ReadAnnotations no FRAME EXIBIDO para todos os kinds em /Rotate
/// 0/90/180/270 (+ CropBox deslocada). Oráculo: render PDFium a 1 px/pt — a marca aparece na região EXIBIDA
/// pedida, e o que tem orientação (texto do FreeText, imagem do carimbo, ícone da nota, traço do
/// sublinhado/tachado, ponta da seta) sai EM PÉ para o leitor.
public class AnnotationRotationRenderTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    /// A4 em branco; `crop` = CropBox (30,40) 500×720 (exibida 500×720, ou 720×500 em 90/270).
    internal static byte[] Page(int rot, bool crop)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            if (crop) page.SetCropBox(new Rectangle(30, 40, 500, 720));
            if (rot != 0) page.SetRotation(rot);
        }
        return ms.ToArray();
    }

    public static IEnumerable<object[]> Frames() => new[]
    {
        new object[] { 0, false }, new object[] { 90, false }, new object[] { 180, false }, new object[] { 270, false },
        new object[] { 0, true }, new object[] { 90, true }, new object[] { 270, true },
    };

    /// Imagem-teste ASSIMÉTRICA 40×40: faixa VERMELHA no topo (10 linhas), faixa VERDE à esquerda (10 colunas,
    /// abaixo do topo), resto AZUL. Em pé = vermelho em cima, verde à esquerda.
    internal static readonly byte[] MarkerPng = TestPng.Rgb(40, 40, (x, y) =>
        y < 10 ? (255, 0, 0) : x < 10 ? (0, 200, 0) : (0, 0, 255));

    // Geometria EXIBIDA de cada kind — cabe em 500×500 (toda página exibida do teste).
    private static readonly PdfQuad HlQuad = new(60, 440, 180, 455);
    private static readonly PdfQuad HlQuad2 = new(60, 420, 130, 435);
    private static readonly PdfQuad UlQuad = new(220, 440, 340, 455);
    private static readonly PdfQuad SoQuad = new(60, 380, 180, 395);
    private static readonly PdfQuad NtBox = new(380, 430, 400, 450);
    private static readonly PdfQuad FtBox = new(220, 330, 440, 380);
    private static readonly PdfQuad RcBox = new(60, 250, 180, 350);
    private static readonly PdfPoint LnStart = new(220, 200), LnEnd = new(340, 260);
    private static readonly PdfPoint ArStart = new(360, 260), ArEnd = new(460, 200);
    private static readonly PdfPoint[] InkPts = { new(60, 100), new(120, 180), new(180, 120) };
    private static readonly PdfQuad StBox = new(220, 60, 320, 160);
    internal const string FtText = "Texto em pe";

    internal static IEnumerable<AnnotationData> Kinds() => new[]
    {
        new AnnotationData { Id = "hl", Kind = AnnotationKind.Highlight, PageIndex = 0, ColorArgb = 0xFFFFFF00,
            LeftPt = 60, BottomPt = 420, RightPt = 180, TopPt = 455, Quads = new[] { HlQuad, HlQuad2 } },
        new AnnotationData { Id = "ul", Kind = AnnotationKind.Underline, PageIndex = 0, ColorArgb = 0xFF00FF00,
            LeftPt = UlQuad.LeftPt, BottomPt = UlQuad.BottomPt, RightPt = UlQuad.RightPt, TopPt = UlQuad.TopPt, Quads = new[] { UlQuad } },
        new AnnotationData { Id = "so", Kind = AnnotationKind.Strikeout, PageIndex = 0, ColorArgb = 0xFFFF0000,
            LeftPt = SoQuad.LeftPt, BottomPt = SoQuad.BottomPt, RightPt = SoQuad.RightPt, TopPt = SoQuad.TopPt, Quads = new[] { SoQuad } },
        new AnnotationData { Id = "nt", Kind = AnnotationKind.StickyNote, PageIndex = 0, ColorArgb = 0xFFFFD000, Content = "nota",
            LeftPt = NtBox.LeftPt, BottomPt = NtBox.BottomPt, RightPt = NtBox.RightPt, TopPt = NtBox.TopPt },
        new AnnotationData { Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, Content = FtText, FontSizePt = 14, Bold = true,
            LeftPt = FtBox.LeftPt, BottomPt = FtBox.BottomPt, RightPt = FtBox.RightPt, TopPt = FtBox.TopPt },
        new AnnotationData { Id = "rc", Kind = AnnotationKind.Rectangle, PageIndex = 0, ColorArgb = 0xFFFF0000, FillArgb = 0xFF00FFFF, StrokeWidthPt = 3,
            LeftPt = RcBox.LeftPt, BottomPt = RcBox.BottomPt, RightPt = RcBox.RightPt, TopPt = RcBox.TopPt },
        new AnnotationData { Id = "ln", Kind = AnnotationKind.Line, PageIndex = 0, ColorArgb = 0xFF0000FF, StrokeWidthPt = 2,
            LeftPt = 220, BottomPt = 200, RightPt = 340, TopPt = 260, LineStartPt = LnStart, LineEndPt = LnEnd },
        new AnnotationData { Id = "ar", Kind = AnnotationKind.Arrow, PageIndex = 0, ColorArgb = 0xFF0000FF, StrokeWidthPt = 2,
            LeftPt = 360, BottomPt = 200, RightPt = 460, TopPt = 260, LineStartPt = ArStart, LineEndPt = ArEnd },
        new AnnotationData { Id = "ik", Kind = AnnotationKind.Ink, PageIndex = 0, ColorArgb = 0xFF000000, StrokeWidthPt = 2,
            LeftPt = 60, BottomPt = 100, RightPt = 180, TopPt = 180, InkStrokes = new[] { (IReadOnlyList<PdfPoint>)InkPts } },
        new AnnotationData { Id = "st", Kind = AnnotationKind.ImageStamp, PageIndex = 0, ImageBytes = MarkerPng,
            LeftPt = StBox.LeftPt, BottomPt = StBox.BottomPt, RightPt = StBox.RightPt, TopPt = StBox.TopPt },
    };

    internal static AnnotationData Kind(string id) => Kinds().Single(k => k.Id == id);

    // ---------------------------------------------------------------- análise de pixels (frame exibido, pt)

    internal readonly record struct Blob(int Count, double L, double B, double R, double T, double Cx, double Cy);

    internal static RenderedPage Render(byte[] pdf, double scale = 1.0)
    {
        using var r = new PdfDocumentRenderer(pdf);
        return r.RenderPage(0, scale);
    }

    /// Pixels que satisfazem `pred(r,g,b)` → caixa e centróide em pt do frame exibido (y para cima).
    internal static Blob Find(RenderedPage p, Func<int, int, int, bool> pred)
    {
        int n = 0, minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1;
        double sx = 0, sy = 0;
        for (int y = 0; y < p.HeightPx; y++)
            for (int x = 0; x < p.WidthPx; x++)
            {
                int i = (y * p.WidthPx + x) * 4;
                if (!pred(p.Bgra[i + 2], p.Bgra[i + 1], p.Bgra[i])) continue;
                n++; sx += x + 0.5; sy += p.HeightPx - y - 0.5;
                minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
            }
        if (n == 0) return new Blob(0, 0, 0, 0, 0, 0, 0);
        return new Blob(n, minx, p.HeightPx - 1 - maxy, maxx + 1, p.HeightPx - miny, sx / n, sy / n);
    }

    /// Quantos pixels que satisfazem `pred` há num raio `rad` (pt) em torno de `c` (exibido).
    internal static int CountNear(RenderedPage p, PdfPoint c, double rad, Func<int, int, int, bool> pred)
    {
        int n = 0;
        for (int y = Math.Max(0, (int)(p.HeightPx - c.YPt - rad)); y <= Math.Min(p.HeightPx - 1, (int)(p.HeightPx - c.YPt + rad)); y++)
            for (int x = Math.Max(0, (int)(c.XPt - rad)); x <= Math.Min(p.WidthPx - 1, (int)(c.XPt + rad)); x++)
            {
                double dx = x + 0.5 - c.XPt, dy = p.HeightPx - y - 0.5 - c.YPt;
                if (dx * dx + dy * dy > rad * rad) continue;
                int i = (y * p.WidthPx + x) * 4;
                if (pred(p.Bgra[i + 2], p.Bgra[i + 1], p.Bgra[i])) n++;
            }
        return n;
    }

    internal static bool Yellow(int r, int g, int b) => r > 200 && g > 200 && b < 120;
    internal static bool Red(int r, int g, int b) => r > 150 && r - g > 80 && r - b > 80;
    internal static bool Green(int r, int g, int b) => g > 150 && g - r > 80 && g - b > 80;
    internal static bool Blue(int r, int g, int b) => b > 150 && r < 100 && g < 100;
    internal static bool Cyan(int r, int g, int b) => g > 200 && b > 200 && r < 90;
    internal static bool Dark(int r, int g, int b) => r + g + b < 300;
    internal static bool NonWhite(int r, int g, int b) => r + g + b < 720;

    private static void Near(double expected, double actual, double tol, string what) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"{what}: esperado {expected:F2} ±{tol}, obtido {actual:F2}");

    private static void AssertBox(Blob b, PdfQuad q, double tol, string ctx)
    {
        Assert.True(b.Count > 0, "nada renderizado: " + ctx);
        Near(q.LeftPt, b.L, tol, ctx + " L"); Near(q.RightPt, b.R, tol, ctx + " R");
        Near(q.BottomPt, b.B, tol, ctx + " B"); Near(q.TopPt, b.T, tol, ctx + " T");
    }

    private static RenderedPage RenderKind(string id, int rot, bool crop) =>
        Render(Editor.AddAnnotation(Page(rot, crop), Kind(id)));

    // ---------------------------------------------------------------- render por kind × rotação

    [Theory, MemberData(nameof(Frames))]
    public void Highlight_LandsOnDisplayedQuads(int rot, bool crop)
    {
        var p = RenderKind("hl", rot, crop);
        string ctx = $"rot={rot} crop={crop}";
        AssertBox(Find(p, Yellow), new PdfQuad(60, 420, 180, 455), 1.5, ctx);
        // as DUAS faixas, não o envelope: o vão entre elas (y 435..440) fica branco
        Assert.Equal(0, CountNear(p, new PdfPoint(100, 437.5), 1.5, Yellow));
        Assert.True(CountNear(p, new PdfPoint(150, 447), 4, Yellow) > 40, ctx);
        Assert.Equal(0, CountNear(p, new PdfPoint(160, 427), 3, Yellow)); // 2ª faixa termina em x=130
    }

    [Theory, MemberData(nameof(Frames))]
    public void Underline_IsHorizontalLineUnderTheWord(int rot, bool crop)
    {
        var b = Find(RenderKind("ul", rot, crop), Green);
        string ctx = $"rot={rot} crop={crop} blob L={b.L} B={b.B} R={b.R} T={b.T}";
        Assert.True(b.Count > 0, "nada renderizado: " + ctx);
        Near(UlQuad.LeftPt, b.L, 1.5, ctx + " L"); Near(UlQuad.RightPt, b.R, 1.5, ctx + " R");
        Assert.True(b.T - b.B <= 3, "traço não é horizontal/fino: " + ctx);
        Assert.InRange(b.Cy, UlQuad.BottomPt, UlQuad.BottomPt + 2.5); // junto à base da palavra
    }

    [Theory, MemberData(nameof(Frames))]
    public void Strikeout_IsHorizontalLineThroughTheWord(int rot, bool crop)
    {
        var b = Find(RenderKind("so", rot, crop), Red);
        string ctx = $"rot={rot} crop={crop} blob L={b.L} B={b.B} R={b.R} T={b.T}";
        Assert.True(b.Count > 0, "nada renderizado: " + ctx);
        Near(SoQuad.LeftPt, b.L, 1.5, ctx + " L"); Near(SoQuad.RightPt, b.R, 1.5, ctx + " R");
        Assert.True(b.T - b.B <= 3, "traço não é horizontal/fino: " + ctx);
        Near((SoQuad.BottomPt + SoQuad.TopPt) / 2, b.Cy, 1.5, ctx + " meio");
    }

    [Theory, MemberData(nameof(Frames))]
    public void Rectangle_BorderAndFillInDisplayedBox(int rot, bool crop)
    {
        var p = RenderKind("rc", rot, crop);
        string ctx = $"rot={rot} crop={crop}";
        AssertBox(Find(p, Red), RcBox, 2, ctx + " borda");
        var fill = Find(p, Cyan);
        Near((RcBox.LeftPt + RcBox.RightPt) / 2, fill.Cx, 1.5, ctx + " fill cx");
        Near((RcBox.BottomPt + RcBox.TopPt) / 2, fill.Cy, 1.5, ctx + " fill cy");
        Assert.True(fill.Count > 0.8 * 114 * 94, ctx + " fill count " + fill.Count);
    }

    [Theory, MemberData(nameof(Frames))]
    public void Ink_FollowsDisplayedStroke(int rot, bool crop)
    {
        var p = RenderKind("ik", rot, crop);
        string ctx = $"rot={rot} crop={crop}";
        AssertBox(Find(p, Dark), new PdfQuad(60, 100, 180, 180), 2.5, ctx);
        foreach (var v in InkPts) Assert.True(CountNear(p, v, 3, Dark) > 3, $"{ctx} sem tinta perto de {v}");
        Assert.Equal(0, CountNear(p, new PdfPoint(120, 102), 4, Dark)); // o traço é um "Λ", não um "V"
    }

    [Theory, MemberData(nameof(Frames))]
    public void Line_IsVisibleAlongDisplayedSegment(int rot, bool crop)
    {
        var p = RenderKind("ln", rot, crop);
        string ctx = $"rot={rot} crop={crop}";
        AssertBox(Find(p, Blue), new PdfQuad(220, 200, 340, 260), 2.5, ctx);
        foreach (var v in new[] { LnStart, LnEnd, new PdfPoint(280, 230) })
            Assert.True(CountNear(p, v, 3, Blue) > 3, $"{ctx} sem traço perto de {v}");
        Assert.Equal(0, CountNear(p, new PdfPoint(225, 255), 5, Blue)); // diagonal certa, não a espelhada
        Assert.Equal(0, CountNear(p, new PdfPoint(335, 205), 5, Blue));
    }

    [Theory, MemberData(nameof(Frames))] // envelope de altura ZERO (linha horizontal na tela): o /Rect precisa da margem
    public void Line_HorizontalOnScreen_IsVisible(int rot, bool crop)
    {
        var a = new AnnotationData { Id = "h", Kind = AnnotationKind.Line, PageIndex = 0, ColorArgb = 0xFF0000FF, StrokeWidthPt = 2,
            LeftPt = 100, BottomPt = 300, RightPt = 200, TopPt = 300, LineStartPt = new PdfPoint(100, 300), LineEndPt = new PdfPoint(200, 300) };
        var b = Find(Render(Editor.AddAnnotation(Page(rot, crop), a)), Blue);
        string ctx = $"rot={rot} crop={crop} blob L={b.L} B={b.B} R={b.R} T={b.T}";
        Assert.True(b.Count > 150, "linha invisível: " + ctx);
        Near(100, b.L, 1.5, ctx + " L"); Near(200, b.R, 1.5, ctx + " R");
        Near(300, b.Cy, 0.5, ctx + " y");
        Assert.True(b.T - b.B <= 3.5, "não é horizontal: " + ctx);
        var back = Assert.Single(Editor.ReadAnnotations(Editor.AddAnnotation(Page(rot, crop), a)));
        Assert.Equal(300, back.BottomPt, 0.01); Assert.Equal(300, back.TopPt, 0.01); // bbox = envelope dos 2 pontos
    }

    [Theory, MemberData(nameof(Frames))]
    public void Arrow_IsVisible_WithHeadAtTheEnd(int rot, bool crop)
    {
        var p = RenderKind("ar", rot, crop);
        string ctx = $"rot={rot} crop={crop}";
        var b = Find(p, Blue);
        Assert.True(b.Count > 0, "nada renderizado: " + ctx);
        Near(ArStart.XPt, b.L, 2.5, ctx + " L"); Near(ArStart.YPt, b.T, 2.5, ctx + " T");
        foreach (var v in new[] { ArStart, ArEnd, new PdfPoint(410, 230) })
            Assert.True(CountNear(p, v, 3, Blue) > 3, $"{ctx} sem traço perto de {v}");
        int head = CountNear(p, ArEnd, 12, Blue), tail = CountNear(p, ArStart, 12, Blue);
        Assert.True(head > 1.6 * tail, $"{ctx} ponta da seta não está no fim: fim={head} início={tail}");
    }

    [Theory, MemberData(nameof(Frames))]
    public void FreeText_TextIsUprightAtTopLeftOfDisplayedBox(int rot, bool crop)
    {
        var b = Find(RenderKind("ft", rot, crop), Dark);
        string ctx = $"rot={rot} crop={crop} ink L={b.L} B={b.B} R={b.R} T={b.T}";
        Assert.True(b.Count > 0, "nada renderizado: " + ctx);
        Assert.InRange(b.L, FtBox.LeftPt + 1, FtBox.LeftPt + 6);      // começa à esquerda (padding 2)
        Assert.InRange(b.T, FtBox.TopPt - 9, FtBox.TopPt - 1);        // ancorado no TOPO da caixa
        Assert.InRange(b.R, FtBox.LeftPt + 60, FtBox.LeftPt + 110);   // "Texto em pe" @14pt negrito ≈ 80 pt
        Assert.True(b.R - b.L > 4 * (b.T - b.B), "texto não está horizontal: " + ctx);
        Assert.True(b.B > FtBox.BottomPt + 20, "texto não está no topo: " + ctx);
    }

    [Theory, MemberData(nameof(Frames))]
    public void ImageStamp_IsUprightInDisplayedBox(int rot, bool crop)
    {
        var p = RenderKind("st", rot, crop);
        string ctx = $"rot={rot} crop={crop}";
        AssertBox(Find(p, NonWhite), StBox, 1.5, ctx + " imagem");
        // vermelho = faixa do TOPO (25 pt), verde = faixa da ESQUERDA (abaixo do topo)
        AssertBox(Find(p, Red), new PdfQuad(220, 135, 320, 160), 1.5, ctx + " vermelho/topo");
        AssertBox(Find(p, Green), new PdfQuad(220, 60, 245, 135), 1.5, ctx + " verde/esquerda");
    }

    [Theory, MemberData(nameof(Frames))]
    public void StickyNote_IconIsUprightAtTopLeftOfDisplayedBox(int rot, bool crop)
    {
        var p = RenderKind("nt", rot, crop);
        string ctx = $"rot={rot} crop={crop}";
        var all = Find(p, NonWhite);
        AssertBox(all, NtBox, 1.5, ctx + " ícone");
        // ícone de balão (o mesmo desenho do PDFium em rotação 0): corpo em cima, "rabicho" embaixo à
        // ESQUERDA; as linhas de texto do balão são HORIZONTAIS na tela.
        var fill = Find(p, Yellow);
        Assert.True(fill.Cy > NtBox.BottomPt + 11, $"{ctx} corpo do balão não está em cima (cy={fill.Cy})");
        int tailLeft = CountNear(p, new PdfPoint(NtBox.LeftPt + 6, NtBox.BottomPt + 2), 2.5, NonWhite);
        int tailRight = CountNear(p, new PdfPoint(NtBox.RightPt - 6, NtBox.BottomPt + 2), 2.5, NonWhite);
        Assert.True(tailLeft > 3 && tailRight == 0, $"{ctx} rabicho não está embaixo à esquerda: esq={tailLeft} dir={tailRight}");
        int rowDark = 0;
        for (double x = NtBox.LeftPt + 4; x <= NtBox.RightPt - 4; x += 1)
            if (CountNear(p, new PdfPoint(x, NtBox.TopPt - 4.2), 0.6, Dark) > 0) rowDark++;
        Assert.True(rowDark >= 10, $"{ctx} linha de texto do balão não é horizontal ({rowDark})");
    }

    /// Task 6 (validação no app real): a nota com cor escolhida (/C azul) aparecia AMARELA em página normal (o
    /// PDFium gera o ícone sem /AP e ignora /C no preenchimento) e AZUL em página girada (/AP nosso pintado com
    /// /C) — a mesma nota mudava de cor conforme a rotação. Oráculo: a cor do corpo do balão em 90/180/270 é a
    /// MESMA que o PDFium desenha em rotação 0, para qualquer /C.
    [Theory]
    [InlineData(90, 0xFF1E6FE0u)]
    [InlineData(180, 0xFF1E6FE0u)]
    [InlineData(270, 0xFF1E6FE0u)]
    [InlineData(90, 0xFFFF5555u)]
    [InlineData(90, 0xFFFFD000u)]
    public void StickyNote_PaginaGirada_MesmaCorDoIconeQueARotacao0(int rot, uint argb)
    {
        (int R, int G, int B) Corpo(int r)
        {
            var nota = Kind("nt") with { ColorArgb = argb };
            var p = Render(Editor.AddAnnotation(Page(r, false), nota));
            // centro do corpo do balão (entre as linhas de texto): 4 pt abaixo do topo, 3 pt da esquerda
            int x = (int)(NtBox.LeftPt + 3), y = (int)(p.HeightPx - (NtBox.TopPt - 6));
            int i = (y * p.WidthPx + x) * 4;
            return (p.Bgra[i + 2], p.Bgra[i + 1], p.Bgra[i]);
        }
        var c0 = Corpo(0);
        var cr = Corpo(rot);
        Assert.True(Math.Abs(c0.R - cr.R) + Math.Abs(c0.G - cr.G) + Math.Abs(c0.B - cr.B) <= 30,
            $"rot={rot} /C={argb:X8}: corpo do balão {cr} != rotação 0 {c0}");
    }
}

/// Folha de contato (evidência visual para o relatório): todos os kinds × rotações numa grade PNG. Só age com
/// `GIRADAS_SHEET` = caminho do PNG de saída.
public class AnnotationRotationContactSheet
{
    [SkippableFact] // sem GIRADAS_SHEET: PULADO (nunca "aprovado" sem asserção)
    public void Write()
    {
        string? outPath = Environment.GetEnvironmentVariable("GIRADAS_SHEET");
        Skip.If(string.IsNullOrEmpty(outPath), "GIRADAS_SHEET não definida — folha de contato não gerada.");
        var editor = PdfEditorFactory.Create();
        const double scale = 0.6;
        const int cell = 520;
        var frames = AnnotationRotationRenderTests.Frames().Select(f => ((int)f[0], (bool)f[1])).ToList();
        int cols = 4, rows = (frames.Count + cols - 1) / cols;
        int W = cols * cell, H = rows * cell;
        var rgb = new (int R, int G, int B)[W * H];
        Array.Fill(rgb, (200, 200, 200));
        for (int k = 0; k < frames.Count; k++)
        {
            var (rot, crop) = frames[k];
            var pdf = Labeled(AnnotationRotationRenderTests.Page(rot, crop), $"/Rotate {rot}{(crop ? " + CropBox" : "")}");
            foreach (var a in AnnotationRotationRenderTests.Kinds()) pdf = editor.AddAnnotation(pdf, a);
            var p = AnnotationRotationRenderTests.Render(pdf, scale);
            int ox = (k % cols) * cell + (cell - p.WidthPx) / 2, oy = (k / cols) * cell + (cell - p.HeightPx) / 2;
            for (int y = 0; y < p.HeightPx; y++)
                for (int x = 0; x < p.WidthPx; x++)
                {
                    int i = (y * p.WidthPx + x) * 4;
                    rgb[(oy + y) * W + ox + x] = (p.Bgra[i + 2], p.Bgra[i + 1], p.Bgra[i]);
                }
        }
        File.WriteAllBytes(outPath, TestPng.Rgb(W, H, (x, y) => rgb[y * W + x]));
    }

    /// Rótulo em pé no topo da página exibida (via `DisplayFrame.TextMatrix`).
    private static byte[] Labeled(byte[] pdf, string label)
    {
        using var o = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), new PdfWriter(o)))
        {
            var page = doc.GetPage(1);
            var f = PageFrame.Frame(page);
            var m = f.TextMatrix(new PdfPoint(20, f.DisplayedHeight - 30));
            var font = iText.Kernel.Font.PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA_BOLD);
            new iText.Kernel.Pdf.Canvas.PdfCanvas(page).BeginText().SetFontAndSize(font, 18)
                .SetTextMatrix((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F).ShowText(label).EndText();
        }
        return o.ToArray();
    }
}

/// PNG RGB mínimo (sem pacote): assinatura + IHDR + IDAT (zlib) + IEND.
internal static class TestPng
{
    public static byte[] Rgb(int w, int h, Func<int, int, (int R, int G, int B)> px)
    {
        var raw = new byte[h * (1 + 3 * w)];
        for (int y = 0; y < h; y++)
        {
            int o = y * (1 + 3 * w);
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = px(x, y);
                raw[o + 1 + 3 * x] = (byte)r; raw[o + 2 + 3 * x] = (byte)g; raw[o + 3 + 3 * x] = (byte)b;
            }
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Chunk(png, "IHDR", new byte[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w,
            (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 2, 0, 0, 0 });
        using (var z = new MemoryStream())
        {
            using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) zs.Write(raw);
            Chunk(png, "IDAT", z.ToArray());
        }
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        void U32(uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        U32((uint)data.Length);
        var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(td);
        uint crc = 0xFFFFFFFF;
        foreach (byte d in td)
        {
            crc ^= d;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        U32(crc ^ 0xFFFFFFFF);
    }
}
