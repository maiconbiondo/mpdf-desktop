using System.IO;
using System.Linq;
using System.Windows;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Editing.Tests;
using mPdf.Rendering;

namespace mPdf.App.Tests;

/// Páginas giradas, Task 5: o App sem gate de rotação, com o motor e o PDFium REAIS. Oráculo da tela = o render
/// do PDFium: o que o usuário desenha/coloca aparece onde ele fez o gesto (frame EXIBIDO), o hit-test acha a
/// anotação onde ela aparece, e os overlays (seleção, destaque de campo) caem sobre os pixels desenhados — com
/// `PageViewModel.HeightPt` = altura EXIBIDA (PDFium) em 90/270. A rotação 0 é o controle em toda teoria.
public class PaginasGiradasVmTests
{
    private const uint Vermelho = DocumentViewModel.ColorVermelho; // 0xFFFF5555

    private static VmTemporaria Abrir(byte[] pdf) => VmTemporaria.Abrir(pdf, "girada");

    private static byte[] Girada(byte[] pdf, int rot, params int[] paginas) =>
        rot == 0 ? pdf : PdfEditorFactory.Create().RotatePages(pdf, paginas.Length == 0 ? new[] { 0 } : paginas, rot);

    /// Rotação do frame exibido de uma página W×H (frame da rotação 0) — tabela da análise §3 (usuário→exibido),
    /// aplicada aos 2 cantos. Oráculo INDEPENDENTE do `DisplayFrame` do motor.
    private static PdfQuad GirarRect(PdfQuad r, int rot, double w, double h) => rot switch
    {
        90 => new PdfQuad(r.BottomPt, w - r.RightPt, r.TopPt, w - r.LeftPt),
        180 => new PdfQuad(w - r.RightPt, h - r.TopPt, w - r.LeftPt, h - r.BottomPt),
        270 => new PdfQuad(h - r.TopPt, r.LeftPt, h - r.BottomPt, r.RightPt),
        _ => r,
    };

    /// Caixa (px do render, escala `s`) dos pixels "vermelhos" (R alto, G/B baixos), ou null.
    private static Int32Rect? CaixaVermelha(RenderedPage p)
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

    private static void IgualTela(Rect esperado, Rect real, double tolPx, string ctx)
    {
        Assert.True(Math.Abs(esperado.Left - real.Left) <= tolPx && Math.Abs(esperado.Top - real.Top) <= tolPx
            && Math.Abs(esperado.Right - real.Right) <= tolPx && Math.Abs(esperado.Bottom - real.Bottom) <= tolPx,
            $"{ctx}: esperado {esperado}, real {real} (tol {tolPx} px)");
    }

    private static Rect DaCaixa(Int32Rect c) => new(c.X, c.Y, c.Width, c.Height);

    // ---- Desenho -> render -> hit-test -> overlay -> mover ----------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Retangulo_DesenhadoNaTela_ApareceAli_SelecionaComOverlayCerto_EMove(int rot)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        var page = d.Pages[0];
        using (var r0 = new PdfDocumentRenderer(Fixtures.A4()))
        {
            var s0 = r0.GetPageSize(0);
            bool troca = rot is 90 or 270;
            Assert.Equal(troca ? s0.WidthPt : s0.HeightPt, page.HeightPt, 0); // HeightPt = altura EXIBIDA
        }
        d.ToggleRectangleToolCommand.Execute(null); // preset da caixa nova (sem borda, branca) — cores DEPOIS
        d.SelectedMarkupColorArgb = Vermelho;
        d.SelectedFillColorArgb = Vermelho;

        // gesto do usuário em pt EXIBIDOS (canto sup.-esq. da tela fica perto de y = HeightPt)
        double L = 80, B = page.HeightPt - 260, R = 230, T = page.HeightPt - 180;
        await d.CommitDrawingAsync(0, new[] { new PdfPoint(L, T), new PdfPoint(R, B) });
        Assert.Empty(vm.Errors);

        double s = d.Zoom * PageViewModel.PtToPx;
        var telaPedida = PageViewModel.PointRectToScreenRect(L, B, R, T, d.Zoom, page.HeightPt);
        using (var r = new PdfDocumentRenderer(d.Session.Snapshot))
        {
            var caixa = CaixaVermelha(r.RenderPage(0, s));
            Assert.NotNull(caixa);
            IgualTela(telaPedida, DaCaixa(caixa!.Value), 3, $"render do retângulo (rot {rot})");
        }

        await d.RefreshAnnotationsByPageAsync();
        page = d.Pages[0]; // Session.Applied reconstrói Pages
        var hit = d.HitTestAnnotation(0, (L + R) / 2, (B + T) / 2);
        Assert.NotNull(hit);
        Assert.Equal(AnnotationKind.Rectangle, hit!.Kind);
        Assert.Null(d.HitTestAnnotation(0, R + 40, B - 40)); // fora: nada
        d.SelectedAnnotation = hit;
        Assert.True(page.HasAnnotationSelection);
        IgualTela(telaPedida, page.AnnotationSelectionRect, 3, $"overlay de seleção (rot {rot})");

        // mover +60 pt para a direita e -50 pt (para baixo na tela)
        await d.MoveSelectedAnnotationAsync(L + 60, B - 50);
        Assert.Empty(vm.Errors);
        using (var r = new PdfDocumentRenderer(d.Session.Snapshot))
        {
            var caixa = CaixaVermelha(r.RenderPage(0, s));
            var telaMovida = PageViewModel.PointRectToScreenRect(L + 60, B - 50, R + 60, T - 50, d.Zoom, page.HeightPt);
            IgualTela(telaMovida, DaCaixa(caixa!.Value), 3, $"render depois de mover (rot {rot})");
        }
    }

    // ---- Overlay: anotação que já existia na página e GIROU com ela --------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Overlay_AnotacaoQueGirouComAPagina_CaiSobreOsPixels(int rot)
    {
        var ed = PdfEditorFactory.Create();
        var r0 = new PdfQuad(100, 600, 250, 700);
        byte[] reta = ed.AddAnnotation(Fixtures.A4(), new AnnotationData
        {
            Kind = AnnotationKind.Rectangle, PageIndex = 0, LeftPt = r0.LeftPt, BottomPt = r0.BottomPt, RightPt = r0.RightPt, TopPt = r0.TopPt,
            ColorArgb = Vermelho, FillArgb = Vermelho, Author = "Teste",
        });
        double w, h;
        using (var rr = new PdfDocumentRenderer(reta)) { var sz = rr.GetPageSize(0); w = sz.WidthPt; h = sz.HeightPt; }
        using var vm = Abrir(Girada(reta, rot)); // girar a página depois gira a anotação junto (PDF padrão)
        var d = vm.D;
        var page = d.Pages[0];
        await d.RefreshAnnotationsByPageAsync();

        var esperadoPt = GirarRect(r0, rot, w, h);
        var hit = d.HitTestAnnotation(0, (esperadoPt.LeftPt + esperadoPt.RightPt) / 2, (esperadoPt.BottomPt + esperadoPt.TopPt) / 2);
        Assert.NotNull(hit);
        d.SelectedAnnotation = hit;

        double s = d.Zoom * PageViewModel.PtToPx;
        var esperadoTela = new Rect(esperadoPt.LeftPt * s, (page.HeightPt - esperadoPt.TopPt) * s,
            (esperadoPt.RightPt - esperadoPt.LeftPt) * s, (esperadoPt.TopPt - esperadoPt.BottomPt) * s);
        IgualTela(esperadoTela, page.AnnotationSelectionRect, 3, $"overlay vs oráculo da rotação (rot {rot})");
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        IgualTela(DaCaixa(CaixaVermelha(r.RenderPage(0, s))!.Value), page.AnnotationSelectionRect, 3, $"overlay vs render (rot {rot})");

        // zoom muda: overlay reconvertido com a MESMA altura exibida
        d.Zoom = 2.0;
        double s2 = 2.0 * PageViewModel.PtToPx;
        IgualTela(new Rect(esperadoTela.X / s * s2, esperadoTela.Y / s * s2, esperadoTela.Width / s * s2, esperadoTela.Height / s * s2),
            page.AnnotationSelectionRect, 4, $"overlay em zoom 2x (rot {rot})");
    }

    // ---- Destaque de campo de formulário (WidgetRect real) -----------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void DestaqueDeCampo_PaginaGirada_CaiSobreOWidget(int rot)
    {
        var ed = PdfEditorFactory.Create();
        byte[] form = Fixtures.Formulario();
        var campos0 = ed.ReadFormFields(form);
        var campo0 = campos0.First(c => c.WidgetRect is not null && c.PageIndex == 0);
        double w, h;
        using (var rr = new PdfDocumentRenderer(form)) { var sz = rr.GetPageSize(0); w = sz.WidthPt; h = sz.HeightPt; }

        byte[] girado = Girada(form, rot);
        using var vm = Abrir(girado);
        var d = vm.D;
        d.SeedFormFieldsCache(false, ed.ReadFormFields(girado));
        var alvo = d.FormFieldEditors.First(f => f.Data.Name == campo0.Name);

        d.SelectFormFieldCommand.Execute(alvo);

        var page = d.Pages[0];
        var esperadoPt = GirarRect(campo0.WidgetRect!.Value, rot, w, h);
        double s = d.Zoom * PageViewModel.PtToPx;
        Assert.True(page.HasFormFieldHighlight);
        IgualTela(new Rect(esperadoPt.LeftPt * s, (page.HeightPt - esperadoPt.TopPt) * s,
                (esperadoPt.RightPt - esperadoPt.LeftPt) * s, (esperadoPt.TopPt - esperadoPt.BottomPt) * s),
            page.FormFieldHighlightRect, 2, $"destaque do campo '{campo0.Name}' (rot {rot})");
    }

    // ---- Nota / caixa de texto / imagem / marcação / replicar --------------------------------------------

    [Theory]
    [InlineData(0, AnnotationTool.StickyNote)]
    [InlineData(90, AnnotationTool.StickyNote)]
    [InlineData(270, AnnotationTool.StickyNote)]
    [InlineData(0, AnnotationTool.FreeText)]
    [InlineData(90, AnnotationTool.FreeText)]
    [InlineData(180, AnnotationTool.FreeText)]
    [InlineData(270, AnnotationTool.FreeText)]
    public async Task NotaECaixaDeTexto_PaginaGirada_ColocadaNoClique(int rot, AnnotationTool tool)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        vm.Dialog.Result = "Texto em pé";
        if (tool == AnnotationTool.StickyNote) d.ToggleStickyNoteToolCommand.Execute(null); else d.ToggleFreeTextToolCommand.Execute(null);
        double x = 150, y = d.Pages[0].HeightPt - 150;

        await d.PlaceAnnotationAtAsync(0, x, y);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var a = Assert.Single(d.AnnotationsByPage[0]);
        Assert.True(a.LeftPt <= x + 1 && a.RightPt >= x - 1 && a.BottomPt <= y + 1 && a.TopPt >= y - 1,
            $"rot {rot}: {a.Kind} em {a.LeftPt:F1},{a.BottomPt:F1},{a.RightPt:F1},{a.TopPt:F1} não contém o clique ({x},{y})");
        Assert.Same(a, d.HitTestAnnotation(0, (a.LeftPt + a.RightPt) / 2, (a.BottomPt + a.TopPt) / 2));
    }


    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Imagem_PaginaGirada_ApareceNoClique(int rot)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        d.ToggleStampTool(ImagemCacheAposDesfazerTests.ImagemA);
        double x = 200, y = d.Pages[0].HeightPt - 200;

        await d.PlaceStampAtAsync(0, x, y);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var a = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(AnnotationKind.ImageStamp, a.Kind);
        Assert.True(a.RightPt - a.LeftPt > a.TopPt - a.BottomPt, $"rot {rot}: imagem paisagem deve continuar paisagem NA TELA");
        AssertImagemAEmPe(d.Session.Snapshot, a, $"imagem por clique (rot {rot})");
    }

    /// Imagem A (4 quadrantes: vermelho | verde / azul | amarelo) em pé e sem espelho dentro do rect EXIBIDO `a`.
    private static void AssertImagemAEmPe(byte[] pdf, AnnotationData a, string ctx)
    {
        var se = ImagemCacheAposDesfazerTests.CorEm(pdf, a.PageIndex, a.LeftPt + 6, a.TopPt - 6);
        var sd = ImagemCacheAposDesfazerTests.CorEm(pdf, a.PageIndex, a.RightPt - 6, a.TopPt - 6);
        var ie = ImagemCacheAposDesfazerTests.CorEm(pdf, a.PageIndex, a.LeftPt + 6, a.BottomPt + 6);
        var id = ImagemCacheAposDesfazerTests.CorEm(pdf, a.PageIndex, a.RightPt - 6, a.BottomPt + 6);
        Assert.True(se.R > 160 && se.G < 90 && se.B < 90, $"{ctx}: sup.-esq. deveria ser vermelho: {se}");
        Assert.True(sd.G > 120 && sd.R < 90 && sd.B < 90, $"{ctx}: sup.-dir. deveria ser verde: {sd}");
        Assert.True(ie.B > 160 && ie.R < 90 && ie.G < 90, $"{ctx}: inf.-esq. deveria ser azul: {ie}");
        Assert.True(id.R > 160 && id.G > 140 && id.B < 90, $"{ctx}: inf.-dir. deveria ser amarelo: {id}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task ImagemPelaCaixa_PaginaGirada_InsereNoRetanguloArrastado(int rot)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        d.ToggleStampTool(ImagemCacheAposDesfazerTests.ImagemA);
        double hp = d.Pages[0].HeightPt;

        await d.BeginImageBoxPlacementAsync(0, new PdfPoint(100, hp - 100));
        Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase); // síncrono: Drawing já no mouse-down
        d.UpdateDrawTo(new PdfPoint(300, hp - 200));
        var caixa = d.StampBoxRect;
        await d.EndStampDrawAsync(); // soltar o mouse coloca a imagem (fluxo Adobe)

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var a = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(AnnotationKind.ImageStamp, a.Kind);
        Assert.Equal(caixa.LeftPt, a.LeftPt, 1); Assert.Equal(caixa.TopPt, a.TopPt, 1);
        Assert.Equal(caixa.RightPt, a.RightPt, 1); Assert.Equal(caixa.BottomPt, a.BottomPt, 1);
        AssertImagemAEmPe(d.Session.Snapshot, a, $"imagem pela caixa (rot {rot})");

        // mover a imagem (lift com os bytes do cache): continua A, em pé, no lugar novo
        d.SelectedAnnotation = a;
        await d.MoveSelectedAnnotationAsync(a.LeftPt + 40, a.BottomPt - 120);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var movida = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(a.LeftPt + 40, movida.LeftPt, 1); Assert.Equal(a.BottomPt - 120, movida.BottomPt, 1);
        AssertImagemAEmPe(d.Session.Snapshot, movida, $"imagem movida (rot {rot})");
    }

    // ---- Revisão da Task 5: colar, cor/preenchimento, Linha/Seta, editar caixa de texto ---------------------

    private static async Task<AnnotationData> DesenharRetangulo(DocumentViewModel d, double l, double b, double r, double t)
    {
        d.ToggleRectangleToolCommand.Execute(null); // preset: sem borda, preenchimento branco
        await d.CommitDrawingAsync(0, new[] { new PdfPoint(l, t), new PdfPoint(r, b) });
        await d.RefreshAnnotationsByPageAsync();
        return d.HitTestAnnotation(0, (l + r) / 2, (b + t) / 2)!;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Preenchimento_PaginaGirada_RepintaACaixaNaTela(int rot)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        double hp = d.Pages[0].HeightPt;
        var caixa = await DesenharRetangulo(d, 100, hp - 300, 250, hp - 200);
        using (var r0 = new PdfDocumentRenderer(d.Session.Snapshot)) Assert.Null(CaixaVermelha(r0.RenderPage(0, 1.0))); // branca ainda
        d.SelectedAnnotation = caixa;

        await d.SelectFillVermelhoCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        double s = d.Zoom * PageViewModel.PtToPx;
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        IgualTela(PageViewModel.PointRectToScreenRect(100, hp - 300, 250, hp - 200, d.Zoom, hp),
            DaCaixa(CaixaVermelha(r.RenderPage(0, s))!.Value), 3, $"preenchimento (rot {rot})");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Colar_PaginaGirada_CopiaDeslocadaNaTela(int rot)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        double hp = d.Pages[0].HeightPt;
        var orig = await DesenharRetangulo(d, 100, hp - 300, 250, hp - 200);
        d.SelectedAnnotation = orig;
        d.CopyAnnotationCommand.Execute(null);

        await d.PasteAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        Assert.Equal(2, d.AnnotationsByPage[0].Count);
        var copia = d.AnnotationsByPage[0].Single(a => a.Id != orig.Id);
        Assert.Equal(orig.LeftPt + 12, copia.LeftPt, 1); Assert.Equal(orig.BottomPt + 12, copia.BottomPt, 1); // +12 pt na TELA
        Assert.Equal(orig.RightPt + 12, copia.RightPt, 1); Assert.Equal(orig.TopPt + 12, copia.TopPt, 1);
        // pinta a cópia de vermelho: o render mostra o vermelho no rect da cópia
        d.SelectedAnnotation = copia;
        await d.SelectFillVermelhoCommand.ExecuteAsync(null);
        double s = d.Zoom * PageViewModel.PtToPx;
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        var vermelho = DaCaixa(CaixaVermelha(r.RenderPage(0, s))!.Value);
        var telaCopia = PageViewModel.PointRectToScreenRect(copia.LeftPt, copia.BottomPt, copia.RightPt, copia.TopPt, d.Zoom, hp);
        IgualTela(telaCopia, vermelho, 3, $"cópia colada (rot {rot})");
    }

    [Theory]
    [InlineData(0, AnnotationTool.Line)]
    [InlineData(90, AnnotationTool.Line)]
    [InlineData(180, AnnotationTool.Line)]
    [InlineData(270, AnnotationTool.Line)]
    [InlineData(90, AnnotationTool.Arrow)]
    [InlineData(270, AnnotationTool.Arrow)]
    public async Task LinhaESeta_PaginaGirada_HorizontalNaTela_SelecionavelPerto(int rot, AnnotationTool tool)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        if (tool == AnnotationTool.Line) d.ToggleLineToolCommand.Execute(null); else d.ToggleArrowToolCommand.Execute(null);
        d.SelectedMarkupColorArgb = Vermelho;
        double hp = d.Pages[0].HeightPt, y = hp - 250;

        await d.CommitDrawingAsync(0, new[] { new PdfPoint(100, y), new PdfPoint(300, y) });

        Assert.Empty(vm.Errors);
        double s = d.Zoom * PageViewModel.PtToPx;
        using (var r = new PdfDocumentRenderer(d.Session.Snapshot))
        {
            var c = CaixaVermelha(r.RenderPage(0, s));
            Assert.NotNull(c);
            var tela = DaCaixa(c!.Value);
            Assert.True(tela.Width > 4 * tela.Height, $"rot {rot}: {tool} deveria ser HORIZONTAL na tela: {tela}");
            Assert.InRange(tela.Left, 100 * s - 12, 100 * s + 4); // seta: a ponta pode passar um pouco
            Assert.InRange((tela.Top + tela.Bottom) / 2, (hp - y) * s - 4, (hp - y) * s + 4);
        }
        await d.RefreshAnnotationsByPageAsync();
        Assert.NotNull(d.HitTestAnnotation(0, 200, y + 2)); // 2 pt acima da linha: dentro da folga
        Assert.Null(d.HitTestAnnotation(0, 200, y + 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task EditarCaixaDeTexto_PaginaGirada_TrocaOTextoNoMesmoLugar(int rot)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        vm.Dialog.Result = "Primeiro";
        d.ToggleFreeTextToolCommand.Execute(null);
        double x = 150, y = d.Pages[0].HeightPt - 150;
        await d.PlaceAnnotationAtAsync(0, x, y);
        await d.RefreshAnnotationsByPageAsync();
        var a = Assert.Single(d.AnnotationsByPage[0]);
        d.SelectedAnnotation = a;
        vm.Dialog.Result = "Segundo texto";

        await d.EditSelectedAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var e = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal("Segundo texto", e.Content);
        Assert.Equal(a.LeftPt, e.LeftPt, 1); Assert.Equal(a.TopPt, e.TopPt, 1); // canto sup.-esq. na tela fica
        Assert.Same(e, d.HitTestAnnotation(0, (e.LeftPt + e.RightPt) / 2, (e.BottomPt + e.TopPt) / 2));
    }

    private static List<PdfCharacter> Palavra(TextPage tp, string w)
    {
        int i = tp.Text.IndexOf(w, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{w}' não está no texto: {tp.Text}");
        return tp.Characters.Skip(i).Take(w.Length).ToList();
    }

    /// Página com o texto EM PÉ na tela em qualquer rotação (fixture-sem-texto girada + linha escrita depois).
    private static byte[] TextoEmPe(int rot)
    {
        var ed = PdfEditorFactory.Create();
        byte[] pdf = Girada(Fixtures.NoText(), rot);
        return ed.ApplyTextCorrection(pdf, new TextCorrection(0, new PdfQuad(20, 20, 21, 21), "Endereco: RUA ANTIGA 123",
            TextFontFamily.Serif, false, false, 14, 0xFF000000u, 300, 72, 0xFFFFFFFFu));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task MarcaTexto_PaginaGirada_CobreAPalavraSelecionada(int rot)
    {
        using var vm = Abrir(TextoEmPe(rot));
        var d = vm.D;
        var page = d.Pages[0];
        page.BeginSelection(new Point(1, 1)); page.ClearSelection();
        var cs = Palavra(page.TextPageCache!, "ANTIGA");
        double s = d.Zoom * PageViewModel.PtToPx;
        Point Centro(PdfCharacter c) => new((c.LeftPt + c.RightPt) / 2 * s, (page.HeightPt - (c.BottomPt + c.TopPt) / 2) * s);
        page.BeginSelection(Centro(cs[0]));
        page.UpdateSelection(Centro(cs[^1]));
        Assert.Equal("ANTIGA", page.SelectedText?.Trim());
        d.SelectedMarkupColorArgb = Vermelho;

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);

        Assert.Empty(vm.Errors);
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        var caixa = CaixaVermelha(r.RenderPage(0, s));
        Assert.NotNull(caixa);
        var palavra = PageViewModel.PointRectToScreenRect(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt),
            cs.Max(c => c.RightPt), cs.Max(c => c.TopPt), d.Zoom, page.HeightPt);
        var real = DaCaixa(caixa!.Value);
        // O realce cobre a palavra (horizontal na tela, mesma linha) — folga de meia altura de linha.
        double tol = palavra.Height * 0.6 + 2;
        Assert.True(real.Width > real.Height, $"rot {rot}: realce deitado na tela? {real}");
        IgualTela(palavra, real, tol, $"realce vs palavra (rot {rot})");
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(90, 0)]
    [InlineData(180, 270)]
    public async Task Replicar_ParaPaginaComOutraRotacao_MesmoLugarNaTela(int rotOrigem, int rotDestino)
    {
        var ed = PdfEditorFactory.Create();
        byte[] dois = ed.ExtractPages(Fixtures.ThirtyPages(), new[] { 0, 1 });
        if (rotOrigem != 0) dois = ed.RotatePages(dois, new[] { 0 }, rotOrigem);
        if (rotDestino != 0) dois = ed.RotatePages(dois, new[] { 1 }, rotDestino);
        using var vm = Abrir(dois);
        var d = vm.D;
        d.ToggleRectangleToolCommand.Execute(null); // preset da caixa nova (sem borda, branca) — cores DEPOIS
        d.SelectedMarkupColorArgb = Vermelho;
        d.SelectedFillColorArgb = Vermelho;
        // perto do canto inf.-esq. da tela: cabe nas duas páginas (retrato e paisagem) sem o clamp mexer
        await d.CommitDrawingAsync(0, new[] { new PdfPoint(60, 60), new PdfPoint(180, 120) });
        await d.RefreshAnnotationsByPageAsync();
        d.SelectedAnnotation = d.HitTestAnnotation(0, 120, 90);
        Assert.NotNull(d.SelectedAnnotation);
        var origem = d.SelectedAnnotation!;

        await d.ReplicarProximaPaginaCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var copia = Assert.Single(d.AnnotationsByPage[1]);
        Assert.Equal(origem.LeftPt, copia.LeftPt, 1); Assert.Equal(origem.BottomPt, copia.BottomPt, 1);
        Assert.Equal(origem.RightPt, copia.RightPt, 1); Assert.Equal(origem.TopPt, copia.TopPt, 1);
        double s = d.Zoom * PageViewModel.PtToPx;
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        IgualTela(PageViewModel.PointRectToScreenRect(copia.LeftPt, copia.BottomPt, copia.RightPt, copia.TopPt, d.Zoom, d.Pages[1].HeightPt),
            DaCaixa(CaixaVermelha(r.RenderPage(1, s))!.Value), 3, $"render da réplica ({rotOrigem}->{rotDestino})");
    }

    // ---- Revisão final (I1): Girar DEPOIS de colocar + mover/redimensionar pelo App ------------------------

    /// Imagem A girada 90° horário JUNTO com a página: azul | vermelho / amarelo | verde (cantos da tela).
    private static void AssertImagemAGirada90(byte[] pdf, AnnotationData a, string ctx)
    {
        var se = ImagemCacheAposDesfazerTests.CorEm(pdf, a.PageIndex, a.LeftPt + 6, a.TopPt - 6);
        var sd = ImagemCacheAposDesfazerTests.CorEm(pdf, a.PageIndex, a.RightPt - 6, a.TopPt - 6);
        var ie = ImagemCacheAposDesfazerTests.CorEm(pdf, a.PageIndex, a.LeftPt + 6, a.BottomPt + 6);
        var id = ImagemCacheAposDesfazerTests.CorEm(pdf, a.PageIndex, a.RightPt - 6, a.BottomPt + 6);
        Assert.True(se.B > 160 && se.R < 90 && se.G < 90, $"{ctx}: sup.-esq. deveria ser azul: {se}");
        Assert.True(sd.R > 160 && sd.G < 90 && sd.B < 90, $"{ctx}: sup.-dir. deveria ser vermelho: {sd}");
        Assert.True(ie.R > 160 && ie.G > 140 && ie.B < 90, $"{ctx}: inf.-esq. deveria ser amarelo: {ie}");
        Assert.True(id.G > 120 && id.R < 90 && id.B < 90, $"{ctx}: inf.-dir. deveria ser verde: {id}");
    }

    [Fact]
    public async Task Imagem_GirarDepoisDeColocar_MoverERedimensionar_PreservamGiroEProporcao()
    {
        using var vm = Abrir(Fixtures.A4());
        var d = vm.D;
        d.ToggleStampTool(ImagemCacheAposDesfazerTests.ImagemA);
        double hp = d.Pages[0].HeightPt;
        await d.BeginImageBoxPlacementAsync(0, new PdfPoint(100, hp - 100));
        d.UpdateDrawTo(new PdfPoint(300, hp - 200));
        await d.EndStampDrawAsync();
        Assert.Empty(vm.Errors);

        // Girar 90° pelo organizador (o comando real)
        using (var org = new OrganizerViewModel(d.Session, PdfEditorFactory.Create(), vm.Errors.Add, () => true))
        {
            org.ToggleSelect(0, ctrl: false);
            await org.RotateSelectedCommand.ExecuteAsync(null);
        }
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var g = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(90, g.ConteudoRotacaoGraus);
        double w = g.RightPt - g.LeftPt, h = g.TopPt - g.BottomPt;
        Assert.True(h > 1.8 * w, $"a imagem 2:1 girou junto (alta na tela): {w:F1}×{h:F1}");
        AssertImagemAGirada90(d.Session.Snapshot, g, "após Girar");

        // mover (arrastar): mesma orientação e proporção, no lugar novo
        d.SelectedAnnotation = g;
        await d.MoveSelectedAnnotationAsync(g.LeftPt + 40, g.BottomPt - 60);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var m = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(g.LeftPt + 40, m.LeftPt, 1); Assert.Equal(g.BottomPt - 60, m.BottomPt, 1);
        Assert.Equal(w, m.RightPt - m.LeftPt, 1); Assert.Equal(h, m.TopPt - m.BottomPt, 1);
        Assert.Equal(90, m.ConteudoRotacaoGraus);
        AssertImagemAGirada90(d.Session.Snapshot, m, "após mover");

        // redimensionar pela caixa (alça inf.-dir.): o aspect-fit usa a proporção GIRADA (continua alta)
        d.SelectedAnnotation = m;
        d.BeginImageEditBox(m);
        Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase);
        d.ResizeBoxByHandle(StampBoxHandle.BottomRight, new PdfPoint(40, -80));
        await d.ConfirmStampBoxAsync();
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var r = Assert.Single(d.AnnotationsByPage[0]);
        double rw = r.RightPt - r.LeftPt, rh = r.TopPt - r.BottomPt;
        Assert.True(rw > w + 10, $"não cresceu: {w:F1} -> {rw:F1}");
        Assert.Equal(h / w, rh / rw, 2);
        AssertImagemAGirada90(d.Session.Snapshot, r, "após redimensionar");
    }

    // ---- Fix F3 (T1): casos que eram testes "_PageRotated_" com o FakePdfEditor (documento retrato + uma rotação
    // falsa que nenhum código lia, idênticos aos gêmeos "_PageNotRotated_") e não tinham equivalente real aqui ----

    /// Desenha um retângulo com borda E preenchimento vermelhos (o preset da caixa nova é sem borda/branca — as
    /// cores vêm DEPOIS do Toggle) e devolve a anotação relida pelo hit-test no centro.
    private static async Task<AnnotationData> RetanguloVermelho(DocumentViewModel d, double l, double b, double r, double t, bool preencher = true)
    {
        d.ToggleRectangleToolCommand.Execute(null);
        d.SelectedMarkupColorArgb = Vermelho;
        d.SelectedFillColorArgb = preencher ? Vermelho : null;
        await d.CommitDrawingAsync(0, new[] { new PdfPoint(l, t), new PdfPoint(r, b) });
        await d.RefreshAnnotationsByPageAsync();
        return d.HitTestAnnotation(0, (l + r) / 2, (b + t) / 2)!;
    }

    private static int PixelsVermelhos(RenderedPage p)
    {
        int n = 0;
        for (int i = 0; i < p.Bgra.Length; i += 4)
            if (p.Bgra[i + 2] > 200 && p.Bgra[i + 1] < 140 && p.Bgra[i] < 140) n++;
        return n;
    }

    [Theory] // espessura pelo lift (Remove+Add) numa página girada: a largura nova vale, o retângulo fica no mesmo lugar NA TELA
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Espessura_PaginaGirada_LiftAplicaALarguraNoMesmoLugarDaTela(int rot)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        double hp = d.Pages[0].HeightPt;
        var caixa = await RetanguloVermelho(d, 100, hp - 300, 250, hp - 200, preencher: false);
        Assert.NotNull(caixa);
        double s = d.Zoom * PageViewModel.PtToPx;
        int antes;
        using (var r0 = new PdfDocumentRenderer(d.Session.Snapshot)) antes = PixelsVermelhos(r0.RenderPage(0, s));
        d.SelectedAnnotation = caixa;

        await d.SelectEspessuraGrossaCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var a = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(DocumentViewModel.EspessuraGrossa, a.StrokeWidthPt);
        Assert.Equal(caixa.LeftPt, a.LeftPt, 1); Assert.Equal(caixa.BottomPt, a.BottomPt, 1);
        Assert.Equal(caixa.RightPt, a.RightPt, 1); Assert.Equal(caixa.TopPt, a.TopPt, 1);
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        var p = r.RenderPage(0, s);
        Assert.True(PixelsVermelhos(p) > antes * 1.3, $"rot {rot}: a borda não engrossou ({antes} -> {PixelsVermelhos(p)} px)");
        IgualTela(PageViewModel.PointRectToScreenRect(100, hp - 300, 250, hp - 200, d.Zoom, hp),
            DaCaixa(CaixaVermelha(p)!.Value), 3, $"borda grossa (rot {rot})");
    }

    [Theory] // a caixa de edição da forma (mover pela caixa) abre sobre o retângulo EXIBIDO e o move na tela
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task CaixaDeEdicaoDaForma_PaginaGirada_AbreSobreORetanguloEMoveNaTela(int rot)
    {
        using var vm = Abrir(Girada(Fixtures.A4(), rot));
        var d = vm.D;
        double hp = d.Pages[0].HeightPt;
        var caixa = await RetanguloVermelho(d, 100, hp - 300, 250, hp - 200);
        Assert.NotNull(caixa);
        d.SelectedAnnotation = caixa;

        d.BeginShapeEditBox(caixa);
        Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase);
        Assert.Equal(caixa.LeftPt, d.StampBoxRect.LeftPt, 2); Assert.Equal(caixa.BottomPt, d.StampBoxRect.BottomPt, 2);
        Assert.Equal(caixa.RightPt, d.StampBoxRect.RightPt, 2); Assert.Equal(caixa.TopPt, d.StampBoxRect.TopPt, 2);
        d.MoveBoxBy(new PdfPoint(60, -50));
        await d.ConfirmStampBoxAsync();

        Assert.Empty(vm.Errors);
        double s = d.Zoom * PageViewModel.PtToPx;
        using var r = new PdfDocumentRenderer(d.Session.Snapshot);
        IgualTela(PageViewModel.PointRectToScreenRect(160, hp - 350, 310, hp - 250, d.Zoom, hp),
            DaCaixa(CaixaVermelha(r.RenderPage(0, s))!.Value), 3, $"forma movida pela caixa (rot {rot})");
    }

    [Theory] // preencher e Aplicar um campo de formulário numa página girada grava o valor (preencher não usa coordenadas)
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task AplicarFormulario_PaginaGirada_GravaOValor(int rot)
    {
        var ed = PdfEditorFactory.Create();
        byte[] girado = Girada(Fixtures.Formulario(), rot);
        using var vm = Abrir(girado);
        var d = vm.D;
        d.SeedFormFieldsCache(false, ed.ReadFormFields(girado));
        var alvo = d.FormFieldEditors.First(f => f.Type == FormFieldType.Text && !f.IsReadOnly && f.Data.PageIndex == 0);
        alvo.EditedValue = "Novo Nome";

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.Empty(vm.Errors);
        Assert.Equal("Novo Nome", ed.ReadFormFields(d.Session.Snapshot).Single(f => f.Name == alvo.Name).Value);
        using var r0 = new PdfDocumentRenderer(Fixtures.Formulario());
        using var r1 = new PdfDocumentRenderer(d.Session.Snapshot);
        var (s0, s1) = (r0.GetPageSize(0), r1.GetPageSize(0));
        Assert.Equal(rot is 90 or 270 ? s0.WidthPt : s0.HeightPt, s1.HeightPt, 0); // a página continua girada
    }
}
