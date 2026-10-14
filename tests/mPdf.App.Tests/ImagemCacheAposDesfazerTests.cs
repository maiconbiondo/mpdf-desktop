using System.IO;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Editing.Tests;
using mPdf.Rendering;

namespace mPdf.App.Tests;

/// Revisão da Task 5 (páginas giradas): sem o refresh do cache de anotações que vinha antes do gate de rotação, o
/// "antes" do set-diff que descobre o Id da imagem recém-colocada saía de `AnnotationsByPage` OBSOLETO logo depois
/// de um desfazer/refazer — a imagem A (que voltou com o desfazer, ainda fora do cache) parecia "nova" e recebia os
/// bytes da imagem B; mover A depois a redesenhava como B (e isso ia para o PDF salvo). O "antes" e o "depois"
/// passam a vir dos PRÓPRIOS PDFs (antes/depois da edição). Oráculo: a cor do canto superior esquerdo de A (imagem
/// de 4 cores assimétrica) depois de mover A.
public class ImagemCacheAposDesfazerTests
{
    /// 4 quadrantes de cores diferentes (sup.-esq., sup.-dir., inf.-esq., inf.-dir.) — detecta troca de imagem E
    /// imagem deitada/espelhada.
    internal static byte[] Png4Cores(System.Drawing.Color se, System.Drawing.Color sd, System.Drawing.Color ie, System.Drawing.Color id)
    {
        using var bmp = new System.Drawing.Bitmap(200, 100);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            using var b1 = new System.Drawing.SolidBrush(se); g.FillRectangle(b1, 0, 0, 100, 50);
            using var b2 = new System.Drawing.SolidBrush(sd); g.FillRectangle(b2, 100, 0, 100, 50);
            using var b3 = new System.Drawing.SolidBrush(ie); g.FillRectangle(b3, 0, 50, 100, 50);
            using var b4 = new System.Drawing.SolidBrush(id); g.FillRectangle(b4, 100, 50, 100, 50);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    internal static readonly byte[] ImagemA = Png4Cores(System.Drawing.Color.FromArgb(220, 20, 20), System.Drawing.Color.FromArgb(20, 160, 20),
        System.Drawing.Color.FromArgb(20, 20, 220), System.Drawing.Color.FromArgb(230, 200, 20)); // vermelho | verde / azul | amarelo
    internal static readonly byte[] ImagemB = Png4Cores(System.Drawing.Color.FromArgb(20, 160, 20), System.Drawing.Color.FromArgb(220, 20, 20),
        System.Drawing.Color.FromArgb(230, 200, 20), System.Drawing.Color.FromArgb(20, 20, 220)); // verde | vermelho / amarelo | azul

    /// Cor (R,G,B) do render no ponto (pt EXIBIDOS) da página.
    internal static (int R, int G, int B) CorEm(byte[] pdf, int pageIndex, double xPt, double yPt)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var hPt = r.GetPageSize(pageIndex).HeightPt;
        const double s = 2.0;
        var p = r.RenderPage(pageIndex, s);
        int x = (int)(xPt * s), y = (int)((hPt - yPt) * s);
        int i = (y * p.WidthPx + x) * 4;
        return (p.Bgra[i + 2], p.Bgra[i + 1], p.Bgra[i]);
    }

    internal static bool Vermelho((int R, int G, int B) c) => c.R > 160 && c.G < 90 && c.B < 90;

    private static VmTemporaria Abrir(byte[] pdf) => VmTemporaria.Abrir(pdf, "img-undo");

    private static async Task ColocarPorClique(DocumentViewModel d, byte[] png, double x, double y)
    {
        d.ToggleStampTool(png);
        await d.PlaceStampAtAsync(0, x, y);
    }

    private static async Task ColocarPelaCaixa(DocumentViewModel d, byte[] png, double x, double y)
    {
        d.ToggleStampTool(png);
        await d.BeginImageBoxPlacementAsync(0, new PdfPoint(x, y));
        d.UpdateDrawTo(new PdfPoint(x + 150, y - 75)); // mesma proporção 2:1 da imagem
        await d.EndStampDrawAsync();
    }

    /// Move A (selecionada pelo hit-test no cache FRESCO) e devolve a cor do canto sup.-esq. de A na posição nova.
    private static async Task<(int R, int G, int B)> MoverAECorDoCanto(DocumentViewModel d, List<string> errors, double ax, double ay)
    {
        await d.RefreshAnnotationsByPageAsync();
        var a = d.HitTestAnnotation(0, ax + 5, ay - 5);
        Assert.NotNull(a);
        Assert.Equal(AnnotationKind.ImageStamp, a!.Kind);
        d.SelectedAnnotation = a;
        double dx = 0, dy = -250; // para baixo na tela, longe de B
        await d.MoveSelectedAnnotationAsync(a.LeftPt + dx, a.BottomPt + dy);
        Assert.Empty(errors);
        return CorEm(d.Session.Snapshot, 0, a.LeftPt + dx + 6, a.TopPt + dy - 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColocarB_LogoAposDesfazerQueTrouxeA_NaoTrocaOsBytesDeA(bool pelaCaixa)
    {
        var vm = Abrir(Fixtures.A4());
        var (d, errors) = (vm.D, vm.Errors);
        try
        {
            double ax = 80, ay = 760;
            await ColocarPorClique(d, ImagemA, ax, ay - 75); // clique = canto inf.-esq.
            await d.RefreshAnnotationsByPageAsync();
            var a = Assert.Single(d.AnnotationsByPage[0]);
            Assert.True(Vermelho(CorEm(d.Session.Snapshot, 0, a.LeftPt + 6, a.TopPt - 6)), "sanity: canto sup.-esq. de A é vermelho");
            d.SelectedAnnotation = a;
            await d.DeleteSelectedAnnotationCommand.ExecuteAsync(null);
            await d.RefreshAnnotationsByPageAsync(); // cache: sem A
            d.Session.Undo(); // A volta; o refresh de Session.Applied não roda (sem Dispatcher) -> cache obsoleto

            if (pelaCaixa) await ColocarPelaCaixa(d, ImagemB, 300, 500); else await ColocarPorClique(d, ImagemB, 300, 425);
            Assert.Empty(errors);

            var cor = await MoverAECorDoCanto(d, errors, a.LeftPt, a.TopPt);
            Assert.True(Vermelho(cor), $"A foi redesenhada com os bytes de B: canto sup.-esq. {cor}");
        }
        finally { vm.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColocarB_LogoAposRefazerQueTrouxeA_NaoTrocaOsBytesDeA(bool pelaCaixa)
    {
        var vm = Abrir(Fixtures.A4());
        var (d, errors) = (vm.D, vm.Errors);
        try
        {
            double ax = 80, ay = 760;
            await ColocarPorClique(d, ImagemA, ax, ay - 75);
            await d.RefreshAnnotationsByPageAsync();
            var a = Assert.Single(d.AnnotationsByPage[0]);
            d.Session.Undo(); // A some
            await d.RefreshAnnotationsByPageAsync(); // cache: sem A
            d.Session.Redo(); // A volta; cache obsoleto

            if (pelaCaixa) await ColocarPelaCaixa(d, ImagemB, 300, 500); else await ColocarPorClique(d, ImagemB, 300, 425);
            Assert.Empty(errors);

            var cor = await MoverAECorDoCanto(d, errors, a.LeftPt, a.TopPt);
            Assert.True(Vermelho(cor), $"A foi redesenhada com os bytes de B: canto sup.-esq. {cor}");
        }
        finally { vm.Dispose(); }
    }

    [Fact] // mover A logo após um desfazer que trouxe OUTRA imagem C de volta: o lift de A não pode dar os bytes de A a C.
    public async Task MoverA_LogoAposDesfazerQueTrouxeC_NaoDaOsBytesDeAParaC()
    {
        var vm = Abrir(Fixtures.A4());
        var (d, errors) = (vm.D, vm.Errors);
        try
        {
            await ColocarPorClique(d, ImagemB, 300, 425); // C (bytes B)
            await ColocarPorClique(d, ImagemA, 80, 685);  // A
            await d.RefreshAnnotationsByPageAsync();
            var c = d.HitTestAnnotation(0, 305, 495)!;
            var a = d.HitTestAnnotation(0, 85, 755)!;
            Assert.NotEqual(c.Id, a.Id);
            d.SelectedAnnotation = c;
            await d.DeleteSelectedAnnotationCommand.ExecuteAsync(null);
            await d.RefreshAnnotationsByPageAsync(); // cache: só A
            d.Session.Undo(); // C volta; cache obsoleto (só A)
            d.SelectedAnnotation = a;
            await d.MoveSelectedAnnotationAsync(a.LeftPt, a.BottomPt - 250); // lift de A com cache obsoleto
            Assert.Empty(errors);

            // Agora move C: tem de continuar com os bytes de C (canto sup.-esq. VERDE), não os de A.
            await d.RefreshAnnotationsByPageAsync();
            var c2 = d.HitTestAnnotation(0, 305, 495);
            Assert.NotNull(c2);
            d.SelectedAnnotation = c2;
            await d.MoveSelectedAnnotationAsync(c2!.LeftPt + 100, c2.BottomPt);
            Assert.Empty(errors);
            var cor = CorEm(d.Session.Snapshot, 0, c2.LeftPt + 100 + 6, c2.TopPt - 6);
            Assert.False(Vermelho(cor), $"C foi redesenhada com os bytes de A: canto sup.-esq. {cor}");
            Assert.True(cor.G > 120 && cor.R < 90, $"canto sup.-esq. de C deveria ser verde: {cor}");
        }
        finally { vm.Dispose(); }
    }
}
