using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// Task 4 (SDD desenho-caixa-melhorias): copiar/colar anotação (clipboard INTERNO ao app, nunca a área
/// de transferência do Windows) + replicar na próxima página. Mesmo `FakePdfEditor`/harness headless de
/// `DocumentViewModelTests` (o fake é `internal`, mesmo assembly de testes — visível aqui sem duplicar).
/// Usa `fixture-30p.pdf` (não `fixture-a4.pdf`, 1 página só) — Replicar precisa de uma PRÓXIMA página de
/// verdade pro caminho feliz, e a "última página" (recusa) sai de graça do mesmo documento (índice 29),
/// sem precisar de um 2º arquivo. ImageStamp fica FORA do v1 (brief): `ReadAnnotations` nunca devolve
/// `ImageBytes` de volta (ver doc XML de `AnnotationData.ImageBytes`), então nem Copy nem Replicar
/// conseguiriam reconstruir a appearance de um carimbo — os testes de `CanExecute` abaixo fecham essa
/// exclusão nos 3 comandos novos.
public class Task4CopiarColarReplicarTests
{
    private static (DocumentViewModel doc, FakePdfEditor fake, List<string> errors) BuildForClipboard(
        string autor = "Autor de Teste")
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-clip-cfg-{Guid.NewGuid():N}");
        var fake = new FakePdfEditor();
        var errors = new List<string>();
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")),
            editor: fake,
            config: new AppConfig(configDir) { Autor = autor },
            notifyError: errors.Add);
        return (doc, fake, errors);
    }

    private static AnnotationData Rect(string id, int pageIndex, double left, double bottom, double right, double top,
        uint? colorArgb = null, uint? fillArgb = null) => new()
    {
        Id = id, Kind = AnnotationKind.Rectangle, PageIndex = pageIndex,
        LeftPt = left, BottomPt = bottom, RightPt = right, TopPt = top,
        ColorArgb = colorArgb, FillArgb = fillArgb,
    };

    // ---- Copiar: CanExecute -----------------------------------------------------------------------

    [Fact]
    public void CopyAnnotationCommand_CanExecute_FalseWithoutSelection()
    {
        var (doc, _, _) = BuildForClipboard();
        using var d = doc;
        Assert.False(d.CopyAnnotationCommand.CanExecute(null));
    }

    [Fact]
    public async Task CopyAnnotationCommand_CanExecute_TrueWithRectangleSelected()
    {
        var (doc, fake, _) = BuildForClipboard();
        using var d = doc;
        var rect = Rect("ret-1", 0, 10, 10, 60, 40);
        fake.ReadAnnotationsResult = new[] { rect };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);

        Assert.True(d.CopyAnnotationCommand.CanExecute(null));
    }

    [Fact] // ImageStamp fora do v1 (brief) — ReadAnnotations nunca devolve ImageBytes de volta.
    public async Task CopyAnnotationCommand_CanExecute_FalseForImageStamp()
    {
        var (doc, fake, _) = BuildForClipboard();
        using var d = doc;
        var img = new AnnotationData { Id = "img-1", Kind = AnnotationKind.ImageStamp, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { img };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);

        Assert.False(d.CopyAnnotationCommand.CanExecute(null));
    }

    // ---- Colar --------------------------------------------------------------------------------------

    [Fact] // caminho feliz: Copy -> Paste = 1 AddAnnotation com Id NOVO (diferente do original), rect
    // deslocado +12pt em X/Y, MESMA cor/preenchimento (o clone via `with` preserva os 2), e a colada vira
    // a SelectedAnnotation.
    public async Task PasteAnnotationCommand_AddsOnce_NewId_OffsetRect_SameColorAndFill_SelectsPasted()
    {
        var (doc, fake, errors) = BuildForClipboard();
        using var d = doc;
        var original = Rect("ret-1", 0, 100, 100, 160, 140, colorArgb: 0xFF112233, fillArgb: 0xFF445566);
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 120, 120);
        d.CopyAnnotationCommand.Execute(null);
        Assert.True(d.PasteAnnotationCommand.CanExecute(null));

        await d.PasteAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var pasted = fake.LastAnnotation!;
        Assert.NotNull(pasted.Id);
        Assert.NotEqual("ret-1", pasted.Id); // Id NOVO — nunca reaproveita o do original
        Assert.Equal(0, pasted.PageIndex);
        Assert.Equal(AnnotationKind.Rectangle, pasted.Kind);
        Assert.Equal(0xFF112233u, pasted.ColorArgb); // MESMA cor
        Assert.Equal(0xFF445566u, pasted.FillArgb);  // MESMO preenchimento
        Assert.Equal(112, pasted.LeftPt, 0.01); Assert.Equal(112, pasted.BottomPt, 0.01); // +12pt em X/Y
        Assert.Equal(60, pasted.RightPt - pasted.LeftPt, 0.01); // tamanho preservado
        Assert.Equal(40, pasted.TopPt - pasted.BottomPt, 0.01);

        // seleciona a colada — Session.ApplyEdit já disparou Applied SÍNCRONO (OnSessionApplied limpou a
        // seleção ANTES desta atribuição valer, ver doc XML de PasteAnnotation).
        Assert.NotNull(d.SelectedAnnotation);
        Assert.Equal(pasted.Id, d.SelectedAnnotation!.Id);
        Assert.Equal(30, d.Pages.Count); // ApplyEdit trocou Snapshot pro marcador do fake (30 páginas)
    }

    [Fact] // clique perto da BORDA da página -> +12pt estouraria o limite -> ClampToPage puxa de volta
    // pra dentro, MESMO tamanho preservado (mesma disciplina de PlaceAnnotationAtAsync_ClickNearEdge...).
    public async Task PasteAnnotationCommand_NearPageEdge_ClampsIntoPage()
    {
        var (doc, fake, errors) = BuildForClipboard();
        using var d = doc;
        double pageWidthPt = d.Pages[0].WidthPt, pageHeightPt = d.Pages[0].HeightPt;
        var original = Rect("ret-1", 0, pageWidthPt - 20, pageHeightPt - 20, pageWidthPt, pageHeightPt);
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, pageWidthPt - 10, pageHeightPt - 10);
        d.CopyAnnotationCommand.Execute(null);

        await d.PasteAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        var pasted = fake.LastAnnotation!;
        Assert.True(pasted.RightPt <= pageWidthPt + 0.01, $"RightPt {pasted.RightPt} extrapolou a largura da página");
        Assert.True(pasted.TopPt <= pageHeightPt + 0.01, $"TopPt {pasted.TopPt} extrapolou a altura da página");
        Assert.Equal(20, pasted.RightPt - pasted.LeftPt, 0.01); // tamanho preservado mesmo clampado
        Assert.Equal(20, pasted.TopPt - pasted.BottomPt, 0.01);
    }

    [Fact] // colar de novo cola OUTRA cópia, sempre +12pt do ORIGINAL copiado (não acumulado sobre a
    // última colada) — brief: "colar de novo cola outra cópia (mais deslocada) — ok", mas o clipboard em
    // si não muda a cada colagem.
    public async Task PasteAnnotationCommand_PasteTwice_BothOffsetFromOriginal_NotAccumulated()
    {
        var (doc, fake, _) = BuildForClipboard();
        using var d = doc;
        var original = Rect("ret-1", 0, 100, 100, 160, 140);
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 120, 120);
        d.CopyAnnotationCommand.Execute(null);

        await d.PasteAnnotationCommand.ExecuteAsync(null);
        var first = fake.LastAnnotation!;
        await d.PasteAnnotationCommand.ExecuteAsync(null);
        var second = fake.LastAnnotation!;

        Assert.Equal(2, fake.AddAnnotationCallCount);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(112, first.LeftPt, 0.01);
        Assert.Equal(112, second.LeftPt, 0.01); // MESMO deslocamento — não acumulou sobre a 1ª colada
    }

    [Fact]
    public void PasteAnnotationCommand_CanExecute_FalseWithoutClipboard()
    {
        var (doc, _, _) = BuildForClipboard();
        using var d = doc;
        Assert.False(d.PasteAnnotationCommand.CanExecute(null));
    }

    [Fact]
    public async Task PasteAnnotationCommand_CanExecute_FalseWhenSignedDocument()
    {
        var (doc, fake, _) = BuildForClipboard();
        using var d = doc;
        var original = Rect("ret-1", 0, 10, 10, 60, 40);
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        d.CopyAnnotationCommand.Execute(null);
        Assert.True(d.PasteAnnotationCommand.CanExecute(null)); // sanity antes

        d.IsSignedDocument = true;

        Assert.False(d.PasteAnnotationCommand.CanExecute(null));
    }

    // ---- Replicar na próxima página -------------------------------------------------------------------

    [Fact] // caminho feliz: MESMO retângulo na página seguinte (nenhum deslocamento — diferente de
    // Paste), Id NOVO, seleciona a réplica.
    public async Task ReplicarProximaPaginaCommand_AddsOnNextPage_SameRect_NewId_SelectsReplica()
    {
        var (doc, fake, errors) = BuildForClipboard();
        using var d = doc;
        var original = Rect("ret-1", 0, 100, 100, 160, 140, colorArgb: 0xFF112233, fillArgb: 0xFF445566);
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 120, 120);
        Assert.True(d.ReplicarProximaPaginaCommand.CanExecute(null));

        await d.ReplicarProximaPaginaCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var replica = fake.LastAnnotation!;
        Assert.NotEqual("ret-1", replica.Id);
        Assert.Equal(1, replica.PageIndex); // PageIndex + 1
        Assert.Equal(100, replica.LeftPt, 0.01); Assert.Equal(100, replica.BottomPt, 0.01); // MESMO rect
        Assert.Equal(160, replica.RightPt, 0.01); Assert.Equal(140, replica.TopPt, 0.01);
        Assert.Equal(0xFF112233u, replica.ColorArgb);
        Assert.Equal(0xFF445566u, replica.FillArgb);
        Assert.NotNull(d.SelectedAnnotation);
        Assert.Equal(replica.Id, d.SelectedAnnotation!.Id);
    }

    [Fact] // destino de tamanho DIFERENTE (aqui simulado clicando perto da borda da página de ORIGEM, que
    // tem o MESMO tamanho da destino nesta fixture) — clamp aplicado com o tamanho da página DESTINO.
    public async Task ReplicarProximaPaginaCommand_ClampsToDestinationPageSize()
    {
        var (doc, fake, errors) = BuildForClipboard();
        using var d = doc;
        double pageWidthPt = d.Pages[1].WidthPt, pageHeightPt = d.Pages[1].HeightPt;
        var original = Rect("ret-1", 0, pageWidthPt - 20, pageHeightPt - 20, pageWidthPt + 40, pageHeightPt + 40);
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectedAnnotation = original; // fora dos limites de propósito — hit-test não alcançaria isto

        await d.ReplicarProximaPaginaCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        var replica = fake.LastAnnotation!;
        Assert.True(replica.RightPt <= pageWidthPt + 0.01, $"RightPt {replica.RightPt} extrapolou a largura da página destino");
        Assert.True(replica.TopPt <= pageHeightPt + 0.01, $"TopPt {replica.TopPt} extrapolou a altura da página destino");
    }

    [Fact] // última página -> aviso pt-BR "Não há próxima página." + no-op (nenhum AddAnnotation).
    public async Task ReplicarProximaPaginaCommand_LastPage_NotifiesAndDoesNotAdd()
    {
        var (doc, fake, errors) = BuildForClipboard();
        using var d = doc;
        int lastPage = d.Pages.Count - 1;
        var original = Rect("ret-1", lastPage, 10, 10, 60, 40);
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(lastPage, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);

        await d.ReplicarProximaPaginaCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Contains(errors, e => e.Contains("Não há próxima página"));
    }

    [Fact] // destino GIRADA -> aviso pt-BR distinto + no-op (nenhum AddAnnotation) — MESMO gate/costura de
    // rotação (IsPageRotated) dos outros pontos de escrita, mas aqui checando a página DESTINO, não a de
    // origem.
    public async Task ReplicarProximaPaginaCommand_RotatedDestination_NotifiesAndDoesNotAdd()
    {
        var (doc, fake, errors) = BuildForClipboard();
        using var d = doc;
        var rotations = new int[30];
        rotations[1] = 90; // página DESTINO (0+1) girada — origem (0) continua reta
        fake.PageRotationsResult = rotations;
        var original = Rect("ret-1", 0, 10, 10, 60, 40);
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);

        await d.ReplicarProximaPaginaCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Contains(errors, e => e.Contains("girada"));
    }

    [Fact]
    public void ReplicarProximaPaginaCommand_CanExecute_FalseWithoutSelection()
    {
        var (doc, _, _) = BuildForClipboard();
        using var d = doc;
        Assert.False(d.ReplicarProximaPaginaCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReplicarProximaPaginaCommand_CanExecute_FalseForImageStamp()
    {
        var (doc, fake, _) = BuildForClipboard();
        using var d = doc;
        var img = new AnnotationData { Id = "img-1", Kind = AnnotationKind.ImageStamp, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { img };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);

        Assert.False(d.ReplicarProximaPaginaCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReplicarProximaPaginaCommand_CanExecute_FalseWhenSignedDocument()
    {
        var (doc, fake, _) = BuildForClipboard();
        using var d = doc;
        var original = Rect("ret-1", 0, 10, 10, 60, 40);
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.True(d.ReplicarProximaPaginaCommand.CanExecute(null)); // sanity antes

        d.IsSignedDocument = true;

        Assert.False(d.ReplicarProximaPaginaCommand.CanExecute(null));
    }

    // ---- Line/Arrow: pontos deslocados/preservados coerentemente com o bbox ---------------------------

    [Fact] // Paste de uma Line desloca LineStartPt/LineEndPt pelo MESMO delta do bbox (+12pt aqui, sem
    // clamp) — mesma disciplina de MoveSelectedAnnotationAsync_LineTool_TranslatesEndpointsByDelta.
    public async Task PasteAnnotationCommand_LineTool_TranslatesEndpointsByDelta()
    {
        var (doc, fake, errors) = BuildForClipboard();
        using var d = doc;
        var original = new AnnotationData
        {
            Id = "linha-1", Kind = AnnotationKind.Line, PageIndex = 0,
            LeftPt = 100, BottomPt = 100, RightPt = 160, TopPt = 160,
            LineStartPt = new PdfPoint(100, 100), LineEndPt = new PdfPoint(160, 160),
        };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 130, 130);
        d.CopyAnnotationCommand.Execute(null);

        await d.PasteAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        var pasted = fake.LastAnnotation!;
        Assert.Equal(112, pasted.LineStartPt!.Value.XPt, 0.01); Assert.Equal(112, pasted.LineStartPt.Value.YPt, 0.01);
        Assert.Equal(172, pasted.LineEndPt!.Value.XPt, 0.01); Assert.Equal(172, pasted.LineEndPt.Value.YPt, 0.01);
        // bbox coerente com os pontos deslocados.
        Assert.Equal(112, pasted.LeftPt, 0.01); Assert.Equal(112, pasted.BottomPt, 0.01);
        Assert.Equal(172, pasted.RightPt, 0.01); Assert.Equal(172, pasted.TopPt, 0.01);
    }

    [Fact] // Replicar de uma Arrow preserva LineStartPt/LineEndPt EXATOS (sem deslocamento, destino do
    // MESMO tamanho -> sem clamp) na página seguinte.
    public async Task ReplicarProximaPaginaCommand_ArrowTool_PreservesEndpointsAndBbox()
    {
        var (doc, fake, errors) = BuildForClipboard();
        using var d = doc;
        var original = new AnnotationData
        {
            Id = "seta-1", Kind = AnnotationKind.Arrow, PageIndex = 0,
            LeftPt = 100, BottomPt = 100, RightPt = 160, TopPt = 160,
            LineStartPt = new PdfPoint(100, 100), LineEndPt = new PdfPoint(160, 160),
        };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 130, 130);

        await d.ReplicarProximaPaginaCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        var replica = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.Arrow, replica.Kind);
        Assert.Equal(1, replica.PageIndex);
        Assert.Equal(100, replica.LineStartPt!.Value.XPt, 0.01); Assert.Equal(100, replica.LineStartPt.Value.YPt, 0.01);
        Assert.Equal(160, replica.LineEndPt!.Value.XPt, 0.01); Assert.Equal(160, replica.LineEndPt.Value.YPt, 0.01);
        Assert.Equal(100, replica.LeftPt, 0.01); Assert.Equal(100, replica.BottomPt, 0.01);
        Assert.Equal(160, replica.RightPt, 0.01); Assert.Equal(160, replica.TopPt, 0.01);
    }
}
