using System.IO;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// Task 3 (SDD desenho-caixa-melhorias): redimensionar/mover uma CAIXA (`Rectangle`) JÁ colocada — duplo
/// clique abre a MESMA caixa ajustável de 8 alças do carimbo/imagem (`StampBoxPurpose.EdicaoForma`,
/// `BeginShapeEditBox`); Confirmar faz lift (Remove+Add) com o rect NOVO, preservando
/// ColorArgb/FillArgb/Author/Id — espelha `ImageBoxTests.EditExisting_ResizeViaBox_ChangesRect`
/// (`BeginImageEditBox`/`ConfirmImageEditBoxAsync`), só que SEM bytes/cache: uma `Rectangle` é
/// totalmente reconstruível a partir de `ReadAnnotations`. Usa o `FakePdfEditor` de
/// `DocumentViewModelTests.cs` (registra Remove/Add) pra provar "exatamente 1 Remove + 1 Add" com
/// precisão — mesmo padrão dos testes de `SelectFillCommand_WithRectangleSelected_LiftsAndAppliesFillToThatBox`
/// (mesmo arquivo, seção de preenchimento da Task 2).
public class ShapeEditBoxTests
{
    private static (DocumentViewModel doc, FakePdfEditor fake, List<string> errors) Build(string autor = "Autor de Teste")
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-shapeedit-cfg-{Guid.NewGuid():N}");
        var fake = new FakePdfEditor();
        var errors = new List<string>();
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")),
            editor: fake,
            config: new AppConfig(configDir) { Autor = autor },
            notifyError: errors.Add);
        return (doc, fake, errors);
    }

    private static AnnotationData MakeRect(string id = "caixa-1", uint? colorArgb = DocumentViewModel.ColorAmarelo, uint? fillArgb = null) =>
        new()
        {
            Id = id, Kind = AnnotationKind.Rectangle, PageIndex = 0,
            LeftPt = 100, BottomPt = 100, RightPt = 300, TopPt = 250,
            ColorArgb = colorArgb, FillArgb = fillArgb, Author = "Autor de Teste",
        };

    [Fact] // duplo-clique (mirrorado pela View via `SelectedAnnotation = hit` + `BeginShapeEditBox(hit)`)
    // entra DIRETO em Adjusting no rect da anotação — sem passar por Drawing.
    public void BeginShapeEditBox_RectangleAnnotation_EntersAdjusting_WithAnnotationRect()
    {
        var (doc, _, _) = Build();
        using var d = doc;
        var rect = MakeRect();

        d.SelectedAnnotation = rect; // mesmo gesto da View: seleciona ANTES de abrir a caixa (hit-test)
        d.BeginShapeEditBox(rect);

        Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase);
        Assert.Equal(rect.LeftPt, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(rect.BottomPt, d.StampBoxRect.BottomPt, 0.01);
        Assert.Equal(rect.RightPt, d.StampBoxRect.RightPt, 0.01);
        Assert.Equal(rect.TopPt, d.StampBoxRect.TopPt, 0.01);
        Assert.Equal(0, d.StampBoxPageIndex);
        // rótulo do botão flutuante no propósito EdicaoForma (empurrado pra PageViewModel, ver
        // RefreshStampBoxOverlay) — prova indireta de que o PROPÓSITO certo (EdicaoForma, não
        // Assinatura/Imagem/EdicaoImagem) foi setado, já que `_stampBoxPurpose` é privado.
        Assert.Equal("Aplicar", d.Pages[0].StampBoxConfirmLabel);
    }

    [Fact] // Resize (alça) + Move (corpo) pela MESMA máquina de alças do carimbo/imagem; Confirmar ->
    // EXATAMENTE 1 Remove + 1 Add, com o rect NOVO e ColorArgb/FillArgb/Author/Id preservados (mesmo
    // pipeline de LiftSelectedAnnotationAsync que MoveSelectedAnnotationAsync/ConfirmImageEditBoxAsync
    // já usam).
    public async Task ResizeAndMove_ThenConfirm_LiftsExactlyOnce_WithNewRect_SameColorAndFill()
    {
        var (doc, fake, errors) = Build();
        using var d = doc;
        var rect = MakeRect(colorArgb: DocumentViewModel.ColorAmarelo, fillArgb: DocumentViewModel.ColorVerde);

        d.SelectedAnnotation = rect;
        d.BeginShapeEditBox(rect);
        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-80, 0)); // encolhe a borda direita: 300->220
        d.EndAdjustGesture(); // fronteira do gesto (mesmo mouse-up/LostMouseCapture da View)
        d.MoveBoxBy(new PdfPoint(20, 10)); // desloca a caixa inteira (+20 em x, +10 em y)

        await d.ConfirmStampBoxAsync();

        Assert.Empty(errors);
        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase); // saiu do modo de ajuste
        Assert.Equal(1, fake.RemoveAnnotationCallCount);
        Assert.Equal("caixa-1", fake.LastRemovedId);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var lifted = fake.LastAnnotation!;
        Assert.Equal("caixa-1", lifted.Id); // Id estável (preservado no lift)
        Assert.Equal(AnnotationKind.Rectangle, lifted.Kind);
        Assert.Equal(DocumentViewModel.ColorAmarelo, lifted.ColorArgb); // borda preservada
        Assert.Equal(DocumentViewModel.ColorVerde, lifted.FillArgb);    // preenchimento preservado
        Assert.Equal("Autor de Teste", lifted.Author);
        // rect mudou: 100,100-300,250 -> encolhe 80pt na direita (220) -> desloca (+20,+10)
        Assert.Equal(120, lifted.LeftPt, 0.5);
        Assert.Equal(110, lifted.BottomPt, 0.5);
        Assert.Equal(240, lifted.RightPt, 0.5);
        Assert.Equal(260, lifted.TopPt, 0.5);
        Assert.NotEqual(rect.LeftPt, lifted.LeftPt); // de fato NÃO é mais o rect original
    }

    [Fact] // Cancelar (Esc/botão "Cancelar") descarta sem tocar a anotação — nenhum Remove/Add.
    public void Cancel_DiscardsWithoutRemoveOrAdd()
    {
        var (doc, fake, _) = Build();
        using var d = doc;
        var rect = MakeRect();
        d.SelectedAnnotation = rect;
        d.BeginShapeEditBox(rect);
        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-50, 0));

        d.CancelStampBox();

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Equal(0, fake.RemoveAnnotationCallCount);
        Assert.Equal(0, fake.AddAnnotationCallCount);
    }

    [Fact] // Kind != Rectangle (ex.: StickyNote) -> no-op, mesmo filtro de SelectFillAsync/
    // EditSelectedAnnotationCommand (só Rectangle tem geometria editável por esta caixa).
    public void BeginShapeEditBox_NonRectangleKind_NoOp()
    {
        var (doc, _, errors) = Build();
        using var d = doc;
        var nota = new AnnotationData
        {
            Id = "nota-1", Kind = AnnotationKind.StickyNote, PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30,
        };

        d.BeginShapeEditBox(nota);

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Empty(errors);
    }

    [Fact] // documento ASSINADO (CanEdit=false) -> no-op silencioso, mesmo gate de
    // SelectFillAsync/MoveSelectedAnnotationAsync (a caixa nem abre).
    public void BeginShapeEditBox_SignedDocument_NoOp()
    {
        var (doc, _, errors) = Build();
        using var d = doc;
        d.IsSignedDocument = true;
        var rect = MakeRect();

        d.BeginShapeEditBox(rect);

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Empty(errors);
    }

    [Fact] // página GIRADA -> no-op (avisa "Página girada", mesmo padrão de BeginImageEditBox).
    public async Task BeginShapeEditBox_RotatedPage_NoOp()
    {
        var (doc, fake, errors) = Build();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 90 };
        await d.RefreshAnnotationsByPageAsync();
        var rect = MakeRect();

        d.BeginShapeEditBox(rect);

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Single(errors);
    }

    [Fact] // Preserva FillArgb no lift MESMO quando a borda não tem cor (ColorArgb null) — o fill não
    // "pega carona" em ColorArgb nem é perdido no `with` do lift.
    public async Task ConfirmShapeEditBox_PreservesFillArgb_EvenWithoutBorderColor()
    {
        var (doc, fake, errors) = Build();
        using var d = doc;
        var rect = MakeRect(colorArgb: null, fillArgb: DocumentViewModel.ColorVermelho);
        d.SelectedAnnotation = rect;
        d.BeginShapeEditBox(rect);
        d.MoveBoxBy(new PdfPoint(15, 5));

        await d.ConfirmStampBoxAsync();

        Assert.Empty(errors);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var lifted = fake.LastAnnotation!;
        Assert.Null(lifted.ColorArgb);
        Assert.Equal(DocumentViewModel.ColorVermelho, lifted.FillArgb);
    }

    [Fact] // seleção mudou entre Begin e Confirm (outra anotação selecionada no meio, ex.: novo hit-test
    // da View) -> no-op, mesmo guard de ConfirmImageEditBoxAsync ("seleção mudou").
    public async Task ConfirmShapeEditBox_SelectionChangedMeanwhile_NoOp()
    {
        var (doc, fake, errors) = Build();
        using var d = doc;
        var rect = MakeRect();
        d.SelectedAnnotation = rect;
        d.BeginShapeEditBox(rect);
        d.MoveBoxBy(new PdfPoint(10, 10));
        d.SelectedAnnotation = new AnnotationData
        {
            Id = "outra", Kind = AnnotationKind.Rectangle, PageIndex = 0,
            LeftPt = 0, BottomPt = 0, RightPt = 10, TopPt = 10,
        };

        await d.ConfirmStampBoxAsync();

        Assert.Empty(errors);
        Assert.Equal(0, fake.RemoveAnnotationCallCount);
        Assert.Equal(0, fake.AddAnnotationCallCount);
    }
}
