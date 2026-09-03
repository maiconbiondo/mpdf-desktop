using System.IO;
using System.Linq;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// Plano 21 (Task 5): imagem pela CAIXA AJUSTÁVEL — "Inserir imagem" desenha tamanho+posição (não mais
/// clique único de tamanho fixo) e uma imagem JÁ colocada pode ser movida/redimensionada clicando nela
/// (abre a mesma caixa). Motor REAL (`PdfEditorFactory.Create()`) sobre `fixture-a4.pdf` — ReadAnnotations
/// devolve o Id da anotação criada, o que o cache de bytes (app-side) precisa pra permitir o lift.
public class ImageBoxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-imgbox-{Guid.NewGuid():N}");
    public ImageBoxTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // 1x1 PNG vermelho (mesmo de Fixtures.OnePixelPng) — quadrado (proporção 1:1), decodifica sem erro.
    private static readonly byte[] SquarePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    private DocumentViewModel NewDocWithPendingImage(out DocumentSession session, out IPdfEditor editor)
    {
        var path = Path.Combine(_dir, "rubrica.png");
        File.WriteAllBytes(path, SquarePng);
        editor = PdfEditorFactory.Create();
        session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        var doc = new DocumentViewModel(session, editor: editor,
            notifyError: _ => { }, notifyInfo: _ => { }, dialogs: new FakePickImageDialog(path));
        doc.ToggleImageToolCommand.Execute(null); // "Inserir imagem" -> ativa ImageStamp + PendingImageUsesBox
        return doc;
    }

    [Fact] // "Inserir imagem" usa a caixa (PendingImageUsesBox true).
    public void ToggleImageTool_UsesBox()
    {
        using var d = NewDocWithPendingImage(out var session, out _);
        using (session)
        {
            Assert.Equal(AnnotationTool.ImageStamp, d.ActiveTool);
            Assert.True(d.PendingImageUsesBox);
        }
    }

    [Fact] // a galeria (ToggleStampTool) usa clique único (PendingImageUsesBox false).
    public void ToggleStampTool_Gallery_UsesSingleClick()
    {
        var editor = PdfEditorFactory.Create();
        using var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        using var d = new DocumentViewModel(session, editor: editor, notifyError: _ => { }, notifyInfo: _ => { });

        d.ToggleStampTool(SquarePng);

        Assert.Equal(AnnotationTool.ImageStamp, d.ActiveTool);
        Assert.False(d.PendingImageUsesBox);
    }

    [Fact] // colocar via caixa: desenhar -> ajustar -> Confirmar adiciona um ImageStamp no rect aspect-fit.
    public async Task PlaceViaBox_AddsImageStampAtAspectFitRect()
    {
        using var d = NewDocWithPendingImage(out var session, out var editor);
        using (session)
        {
            await d.BeginImageBoxPlacementAsync(0, new PdfPoint(100, 100));
            Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase);
            d.UpdateDrawTo(new PdfPoint(300, 250)); // caixa 200x150pt
            d.EndStampDraw();
            Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase);

            await d.ConfirmStampBoxAsync();

            Assert.Equal(AnnotationTool.None, d.ActiveTool);
            Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
            var stamp = Assert.Single(editor.ReadAnnotations(session.Snapshot));
            Assert.Equal(AnnotationKind.ImageStamp, stamp.Kind);
            // imagem 1:1 aspect-fit numa caixa 200x150 -> 150x150 centralizado (cx=200, cy=175).
            Assert.Equal(150, stamp.RightPt - stamp.LeftPt, 0.5);
            Assert.Equal(150, stamp.TopPt - stamp.BottomPt, 0.5);
            Assert.Equal(200, (stamp.LeftPt + stamp.RightPt) / 2, 0.5);
            Assert.Equal(175, (stamp.BottomPt + stamp.TopPt) / 2, 0.5);
        }
    }

    [Fact] // caixa pequena demais (< mínimo) -> permanece em Drawing, NÃO adiciona nada (mesma regra do carimbo).
    public async Task PlaceViaBox_TooSmall_StaysDrawing_AddsNothing()
    {
        using var d = NewDocWithPendingImage(out var session, out var editor);
        using (session)
        {
            await d.BeginImageBoxPlacementAsync(0, new PdfPoint(100, 100));
            d.UpdateDrawTo(new PdfPoint(105, 105)); // 5x5pt < mínimo
            d.EndStampDraw();
            Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase);
            Assert.Empty(editor.ReadAnnotations(session.Snapshot));
        }
    }

    [Fact] // editar depois: clicar numa imagem colocada abre a caixa; redimensionar + Salvar muda o rect.
    public async Task EditExisting_ResizeViaBox_ChangesRect()
    {
        using var d = NewDocWithPendingImage(out var session, out var editor);
        using (session)
        {
            // coloca via caixa
            await d.BeginImageBoxPlacementAsync(0, new PdfPoint(100, 100));
            d.UpdateDrawTo(new PdfPoint(300, 300)); // 200x200 -> imagem 200x200 centralizada
            d.EndStampDraw();
            await d.ConfirmStampBoxAsync();

            var placed = Assert.Single(d.AnnotationsByPage[0], a => a.Kind == AnnotationKind.ImageStamp);
            double larguraAntes = placed.RightPt - placed.LeftPt;

            // seleciona e abre a caixa de edição (bytes cacheados no place)
            d.SelectedAnnotation = placed;
            d.BeginImageEditBox(placed);
            Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase);

            // encolhe pela alça direita e salva
            d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-80, 0));
            await d.ConfirmStampBoxAsync();

            var imgsDepois = editor.ReadAnnotations(session.Snapshot).Where(a => a.Kind == AnnotationKind.ImageStamp).ToList();
            var depois = Assert.Single(imgsDepois);
            Assert.True(depois.RightPt - depois.LeftPt < larguraAntes - 30,
                $"imagem não encolheu (antes={larguraAntes:0}, depois={depois.RightPt - depois.LeftPt:0})");
        }
    }

    [Fact] // sem bytes no cache (imagem de OUTRA sessão) -> BeginImageEditBox avisa e NÃO abre a caixa.
    public void EditExisting_NoCachedBytes_NotifiesAndDoesNotOpenBox()
    {
        var editor = PdfEditorFactory.Create();
        using var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        var errors = new List<string>();
        using var d = new DocumentViewModel(session, editor: editor, notifyError: errors.Add, notifyInfo: _ => { });

        // anotação de imagem "externa" (nunca passou pelo place desta sessão -> sem bytes no cache)
        var externa = new AnnotationData
        {
            Id = "img-externa", Kind = AnnotationKind.ImageStamp, PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 60, TopPt = 60,
        };
        d.BeginImageEditBox(externa);

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase); // não abriu
        Assert.Single(errors);
    }
}
