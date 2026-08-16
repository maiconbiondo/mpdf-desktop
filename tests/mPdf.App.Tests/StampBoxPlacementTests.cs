using System.IO;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// Task 1 (Plano 8): máquina de estados da caixa ajustável do carimbo de assinatura
/// (`StampPlacementPhase`/`BeginStampBoxPlacement`/`UpdateDrawTo`/`EndStampDraw`/`MoveBoxBy`/
/// `ResizeBoxByHandle`/`CancelStampBox`/`ConfirmStampBox`) + o overlay que ela empurra pra
/// `PageViewModel` (`HasStampBox`/`IsStampBoxAdjusting`/`StampBoxScreenRect`/`StampBoxPreviewText`).
/// NADA aqui toca Session/`_editor`/motor de assinatura — mesmo escopo "só o rect, exposto pra quem
/// chamar" do brief da Task 1 (Task 2 integra com o fluxo de assinar de verdade). `BuildForStampBox`
/// abre a fixture COMPARTILHADA direto (sem cópia temporária): diferente de `SignCommandTests`, nada
/// aqui grava em disco (`Session.CommitSigned`) — mesmo precedente de
/// `DocumentViewModelTests.BuildForAnnotations`.
public class StampBoxPlacementTests
{
    private static (DocumentViewModel doc, List<string> errors) BuildForStampBox()
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-stampbox-cfg-{Guid.NewGuid():N}");
        var errors = new List<string>();
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")),
            config: new AppConfig(configDir),
            notifyError: errors.Add);
        return (doc, errors);
    }

    // ---- BeginStampBoxPlacement --------------------------------------------------------------------

    [Fact] // mesmo guard de PlaceSignatureStampAtAsync -- máquina nunca roda fora do modo de colocação.
    public void BeginStampBoxPlacement_ActiveToolNotSignatureStamp_DoesNothing()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        Assert.Equal(AnnotationTool.None, d.ActiveTool);

        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=Teste");

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
    }

    [Fact]
    public void BeginStampBoxPlacement_ValidPage_EntersDrawingPhase_WithDegenerateRectAtStart()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;

        d.BeginStampBoxPlacement(0, new PdfPoint(100, 150), "CN=Assinante Teste");

        Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase);
        Assert.Equal(0, d.StampBoxPageIndex);
        Assert.Equal("CN=Assinante Teste", d.StampBoxCertificateCn);
        Assert.NotNull(d.StampBoxDateLabel);
        Assert.Equal(100, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(100, d.StampBoxRect.RightPt, 0.01); // degenerado: largura zero até o 1º UpdateDrawTo
        Assert.Equal(150, d.StampBoxRect.BottomPt, 0.01);
        Assert.Equal(150, d.StampBoxRect.TopPt, 0.01);
        Assert.Null(d.StampBoxNotice);
    }

    [Fact]
    public void BeginStampBoxPlacement_ClampsStartPointToPageBounds()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        double pageW = d.Pages[0].WidthPt, pageH = d.Pages[0].HeightPt;

        d.BeginStampBoxPlacement(0, new PdfPoint(-50, pageH + 999), "CN=X");

        Assert.Equal(0, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(pageH, d.StampBoxRect.TopPt, 0.01);
    }

    [Fact]
    public void BeginStampBoxPlacement_PageIndexOutOfRange_DoesNothing()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;

        d.BeginStampBoxPlacement(99, new PdfPoint(0, 0), "CN=X");

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
    }

    // ---- UpdateDrawTo: normalização de arrasto em qualquer direção + clamp --------------------------

    [Fact]
    public void UpdateDrawTo_NotDrawingPhase_DoesNothing()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;

        d.UpdateDrawTo(new PdfPoint(200, 200)); // phase ainda None -- sem crash, sem efeito

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Equal(default, d.StampBoxRect);
    }

    [Theory] // arrasto em qualquer direção -- envelope MIN/MAX da âncora x ponto corrente, sempre
    // Left<=Right e Bottom<=Top (exemplar: new Rect(anchorPx, currentPx) da ferramenta Retângulo).
    [InlineData(100, 100, 300, 250)] // baixo-direita (direção "normal")
    [InlineData(300, 250, 100, 100)] // cima-esquerda (invertido nos 2 eixos)
    [InlineData(100, 250, 300, 100)] // baixo-esquerda -> topo-direita (X normal, Y invertido)
    [InlineData(300, 100, 100, 250)] // topo-direita -> baixo-esquerda (X invertido, Y normal)
    public void UpdateDrawTo_NormalizesRect_RegardlessOfDragDirection(double ax, double ay, double cx, double cy)
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(ax, ay), "CN=X");

        d.UpdateDrawTo(new PdfPoint(cx, cy));

        Assert.Equal(Math.Min(ax, cx), d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(Math.Max(ax, cx), d.StampBoxRect.RightPt, 0.01);
        Assert.Equal(Math.Min(ay, cy), d.StampBoxRect.BottomPt, 0.01);
        Assert.Equal(Math.Max(ay, cy), d.StampBoxRect.TopPt, 0.01);
    }

    [Fact]
    public void UpdateDrawTo_ClampsCurrentPointToPageBounds()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        double pageW = d.Pages[0].WidthPt;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X");

        d.UpdateDrawTo(new PdfPoint(pageW + 500, -500));

        Assert.Equal(pageW, d.StampBoxRect.RightPt, 0.01);
        Assert.Equal(0, d.StampBoxRect.BottomPt, 0.01);
    }

    // ---- EndStampDraw: mínimo 60x20pt -- abaixo disso fica em Drawing (NÃO cancela) -----------------

    [Fact]
    public void EndStampDraw_BelowMinimum_StaysDrawing_SetsSubtleNotice()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X");
        d.UpdateDrawTo(new PdfPoint(130, 110)); // 30x10pt -- abaixo do mínimo 60x20pt

        d.EndStampDraw();

        Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase); // NÃO cancela
        Assert.True(d.HasStampBoxNotice);
        Assert.False(string.IsNullOrEmpty(d.StampBoxNotice));
    }

    [Fact]
    public void EndStampDraw_AtOrAboveMinimum_TransitionsToAdjusting_ClearsNotice()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X");
        d.UpdateDrawTo(new PdfPoint(160, 120)); // exatamente 60x20pt

        d.EndStampDraw();

        Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase);
        Assert.False(d.HasStampBoxNotice);
        Assert.Null(d.StampBoxNotice);
    }

    [Fact] // prova "NÃO cancela": depois do aviso, o usuário continua arrastando a PARTIR de onde parou
    // e ainda consegue chegar a Adjusting -- a máquina nunca joga fora o gesto em andamento.
    public void EndStampDraw_AfterTooSmallNotice_UserKeepsDragging_StillReachesAdjusting()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X");
        d.UpdateDrawTo(new PdfPoint(120, 105));
        d.EndStampDraw();
        Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase); // ainda pequeno demais

        d.UpdateDrawTo(new PdfPoint(200, 150)); // continua o MESMO gesto (âncora preservada em 100,100)
        d.EndStampDraw();

        Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase);
        Assert.Equal(100, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(200, d.StampBoxRect.RightPt, 0.01);
    }

    [Fact]
    public void EndStampDraw_NotDrawingPhase_DoesNothing()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;

        d.EndStampDraw();

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
    }

    // ---- MoveBoxBy: desloca preservando tamanho, clampado à página ----------------------------------

    private static DocumentViewModel BeginAdjusting(DocumentViewModel d, double left = 100, double bottom = 100, double right = 300, double top = 200)
    {
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(left, bottom), "CN=Assinante Teste");
        d.UpdateDrawTo(new PdfPoint(right, top));
        d.EndStampDraw();
        return d;
    }

    [Fact]
    public void MoveBoxBy_ShiftsRect_PreservesSize()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc);
        double width = d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt;
        double height = d.StampBoxRect.TopPt - d.StampBoxRect.BottomPt;

        d.MoveBoxBy(new PdfPoint(20, -10));

        Assert.Equal(120, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(90, d.StampBoxRect.BottomPt, 0.01);
        Assert.Equal(width, d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(height, d.StampBoxRect.TopPt - d.StampBoxRect.BottomPt, 0.01);
    }

    [Fact]
    public void MoveBoxBy_ClampsToPageBounds_PreservesSize()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc);
        double pageW = d.Pages[0].WidthPt;
        double width = d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt;

        d.MoveBoxBy(new PdfPoint(pageW + 999, 0)); // arrasta muito além da borda direita

        Assert.Equal(pageW, d.StampBoxRect.RightPt, 0.01);
        Assert.Equal(pageW - width, d.StampBoxRect.LeftPt, 0.01); // deslizou, não encolheu
    }

    [Fact]
    public void MoveBoxBy_NotAdjustingPhase_DoesNothing()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X"); // ainda Drawing, não Adjusting

        d.MoveBoxBy(new PdfPoint(50, 50));

        Assert.Equal(100, d.StampBoxRect.LeftPt, 0.01); // inalterado
    }

    // ---- ResizeBoxByHandle: 8 alças, clamp, mínimo, inversão ao cruzar ------------------------------

    [Fact]
    public void ResizeBoxByHandle_Right_ExpandsWidth_KeepsLeftFixed()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc); // 100,100 - 300,200

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(50, 0));

        Assert.Equal(100, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(350, d.StampBoxRect.RightPt, 0.01);
        Assert.Equal(100, d.StampBoxRect.BottomPt, 0.01);
        Assert.Equal(200, d.StampBoxRect.TopPt, 0.01);
    }

    [Fact]
    public void ResizeBoxByHandle_Left_MovesLeftEdge_KeepsRightFixed()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc); // 100,100 - 300,200

        d.ResizeBoxByHandle(StampBoxHandle.Left, new PdfPoint(-40, 0));

        Assert.Equal(60, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(300, d.StampBoxRect.RightPt, 0.01);
    }

    [Fact]
    public void ResizeBoxByHandle_TopLeft_MovesBothAxes_KeepsBottomRightFixed()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc); // 100,100 - 300,200

        d.ResizeBoxByHandle(StampBoxHandle.TopLeft, new PdfPoint(-20, 30));

        Assert.Equal(80, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(300, d.StampBoxRect.RightPt, 0.01); // fixo
        Assert.Equal(100, d.StampBoxRect.BottomPt, 0.01); // fixo
        Assert.Equal(230, d.StampBoxRect.TopPt, 0.01);
    }

    [Fact] // CONTRATO CENTRAL: arrastar a alça Right além da borda Left INVERTE a caixa (right vira o
    // novo left) em vez de recusar/travar em width=0 -- normalização automática via min/max. Delta
    // escolhido pra pousar BEM longe da zona de mínimo (60pt em torno do Left=100 fixo, [40,160]) --
    // isolar a inversão do clamp de mínimo, testado separadamente em BelowMinimum_ClampsToMinimumSize.
    public void ResizeBoxByHandle_CrossingOppositeEdge_FlipsRect()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc); // 100,100 - 300,200 (Left=100 fixo por esta alça)

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-280, 0)); // 300-280=20, cruza o Left=100

        Assert.Equal(20, d.StampBoxRect.LeftPt, 0.01);   // o que era "right" virou o novo left
        Assert.Equal(100, d.StampBoxRect.RightPt, 0.01); // o Left original agora é o right
        Assert.Equal(80, d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt, 0.01); // 80pt, acima do mínimo
    }

    [Fact] // prova a PERMANÊNCIA do mapeamento alça->escalar bruto: depois de UM cruzamento, chamadas
    // SEGUINTES da MESMA alça continuam movendo o MESMO canto físico (o valor que já foi "right"), não
    // saltam pro lado oposto -- é isto que sustenta "a alça gruda no dedo do usuário" através da
    // inversão. Caixa mais larga (100-400) e deltas que pousam longe da zona de mínimo em torno do
    // Left=100 fixo (mesma cautela do teste acima).
    public void ResizeBoxByHandle_ContinuedDragAfterFlip_KeepsTrackingSamePhysicalEdge()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc, 100, 100, 400, 200); // largura 300pt

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-100, 0)); // 400-100=300 -- ainda do lado direito
        Assert.Equal(100, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(300, d.StampBoxRect.RightPt, 0.01);

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-280, 0)); // 300-280=20 -- cruza o Left=100
        Assert.Equal(20, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(100, d.StampBoxRect.RightPt, 0.01);

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-10, 0)); // continua no MESMO sentido: 20-10=10
        Assert.Equal(10, d.StampBoxRect.LeftPt, 0.01); // se a alça tivesse "saltado" pro lado errado, isto falharia
        Assert.Equal(100, d.StampBoxRect.RightPt, 0.01);
    }

    [Fact]
    public void ResizeBoxByHandle_BelowMinimum_ClampsToMinimumSize()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc); // 100,100 - 300,200 (largura 200pt)

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-195, 0)); // tentaria width=5pt (< mínimo 60pt)

        Assert.Equal(60, d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt, 0.01); // travado no mínimo
        Assert.Equal(100, d.StampBoxRect.LeftPt, 0.01); // lado fixo preservado
    }

    // ---- Regressão (revisão pós-Task 1): mínimo perto da borda da página ---------------------------
    // ACHADO REAL do revisor (probe reproduzido, não hipotético): quando o lado FIXO da alça já está a
    // MENOS de `minSizePt` da borda da página que a alça está sendo arrastada em direção, o clamp
    // antigo (página primeiro, mínimo depois) reintroduzia o próprio estouro que o mínimo existia pra
    // evitar -- produzia largura/altura ABAIXO do mínimo em silêncio. Fix: `ResizeAxis` agora deriva a
    // faixa válida como INTERSEÇÃO de [restrição de mínimo] com [limites de página] num único clamp
    // (ver doc XML do método). Os 2 primeiros testes abaixo são os PROBES EXATOS do revisor.

    [Fact] // PROBE 1 do revisor: caixa com Left=10 (fixo pra alça Right, só 10pt da borda 0 -- menos
    // que o mínimo 60pt), arrastar a alça Right por -1000pt. Antes do fix: largura virava 10pt.
    public void ResizeBoxByHandle_Right_FixedEdgeNearLeftBoundary_ClampsToMinimum_NotBelow()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc, left: 10, bottom: 100, right: 100, top: 150); // largura 90pt, válida

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-1000, 0));

        Assert.Equal(10, d.StampBoxRect.LeftPt, 0.01); // lado fixo preservado
        Assert.Equal(70, d.StampBoxRect.RightPt, 0.01); // gruda em fixedEdge+mínimo (10+60), NÃO em 0
        Assert.True(d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt >= 60 - 0.01, "largura abaixo do mínimo");
    }

    [Fact] // PROBE 2 do revisor: caixa com Right perto da borda direita da página (só ~5pt de folga --
    // menos que o mínimo 60pt), arrastar a alça Left por +1000pt. Antes do fix: largura virava 5pt.
    public void ResizeBoxByHandle_Left_FixedEdgeNearRightBoundary_ClampsToMinimum_NotBelow()
    {
        var (doc, _) = BuildForStampBox();
        double pageW = doc.Pages[0].WidthPt;
        using var d = BeginAdjusting(doc, left: 300, bottom: 100, right: pageW - 5, top: 150);

        d.ResizeBoxByHandle(StampBoxHandle.Left, new PdfPoint(1000, 0));

        Assert.Equal(pageW - 5, d.StampBoxRect.RightPt, 0.01); // lado fixo preservado
        Assert.Equal(pageW - 65, d.StampBoxRect.LeftPt, 0.01); // gruda em fixedEdge-mínimo, NÃO na borda
        Assert.True(d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt >= 60 - 0.01, "largura abaixo do mínimo");
    }

    [Fact] // COMPOSTO (cruzamento perto da borda, alça de BORDA): a alça Right tenta cruzar o Left=5
    // (só 5pt da borda 0, sem espaço pro mínimo do lado negativo) -- a alça se RECUSA a cruzar (não
    // existe posição válida do lado negativo) e gruda no mínimo do lado ATUAL (positivo) em vez disso.
    public void ResizeBoxByHandle_Right_CrossingAttemptTowardNearEdge_HoldsOnValidSide()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc, left: 5, bottom: 100, right: 100, top: 150); // largura 95pt

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-200, 0)); // tentaria cruzar bem além do Left=5

        Assert.Equal(5, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(65, d.StampBoxRect.RightPt, 0.01); // 5+60, nunca cruzou (lado negativo não cabia)
    }

    [Fact] // COBERTURA (Important do revisor): alça de CANTO, inversão diagonal nos 2 eixos AO MESMO
    // TEMPO, longe de qualquer borda de página (sem interação com o clamp de mínimo perto da borda --
    // isola o comportamento de cruzamento diagonal puro).
    public void ResizeBoxByHandle_TopRight_CornerHandle_DiagonalFlip_BothAxesCross()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc, left: 200, bottom: 200, right: 400, top: 400); // 200x200pt

        d.ResizeBoxByHandle(StampBoxHandle.TopRight, new PdfPoint(-300, -300)); // cruza Left E Bottom

        Assert.Equal(100, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(200, d.StampBoxRect.RightPt, 0.01);
        Assert.Equal(100, d.StampBoxRect.BottomPt, 0.01);
        Assert.Equal(200, d.StampBoxRect.TopPt, 0.01);
    }

    [Fact] // COBERTURA (Important do revisor): mais uma alça de BORDA (Bottom, eixo Y — usa
    // MinStampBoxHeightPt=20pt, diferente da largura=60pt dos testes acima), perto da borda SUPERIOR
    // da página desta vez (os testes de canto acima já cobrem perto de Y=0 — este cobre o lado oposto:
    // Top FIXO perto de pageH, alça Bottom arrastada PRA CIMA tentando cruzar o Top e passar da borda).
    public void ResizeBoxByHandle_Bottom_FixedEdgeNearTopBoundary_ClampsToMinimumHeight()
    {
        var (doc, _) = BuildForStampBox();
        double pageH = doc.Pages[0].HeightPt;
        using var d = BeginAdjusting(doc, left: 100, bottom: pageH - 115, right: 300, top: pageH - 15); // altura 100pt

        d.ResizeBoxByHandle(StampBoxHandle.Bottom, new PdfPoint(0, 1000)); // arrasta Bottom PRA CIMA, além do Top

        Assert.Equal(pageH - 15, d.StampBoxRect.TopPt, 0.01); // lado fixo preservado
        Assert.Equal(pageH - 35, d.StampBoxRect.BottomPt, 0.01); // Top-mínimo (20), não a borda pageH
        Assert.True(d.StampBoxRect.TopPt - d.StampBoxRect.BottomPt >= 20 - 0.01, "altura abaixo do mínimo");
    }

    [Fact] // O GAP EXATO que deixou o bug passar (Important do revisor): alça de CANTO cruzando os 2
    // eixos AO MESMO TEMPO com o canto FIXO perto de AMBAS as bordas (perto de X=0 e Y=0) -- combina
    // cruzamento + mínimo-perto-da-borda nos 2 eixos simultaneamente, exatamente a composição que os
    // testes de canto "longe da borda" e os testes de borda "perto da borda" (isolados) não cobriam.
    public void ResizeBoxByHandle_TopRight_CornerHandle_CrossingNearPageCorner_ClampsBothAxesToMinimum()
    {
        var (doc, _) = BuildForStampBox();
        // Xa=10 (perto de X=0), Ya=10 (perto de Y=0) -- ambos fixos pra alça TopRight; Xb/Yb começam
        // longe (largura/altura válidas) e são arrastados de volta, cruzando os fixos, tentando ir
        // além das bordas próximas.
        using var d = BeginAdjusting(doc, left: 10, bottom: 10, right: 200, top: 150);

        d.ResizeBoxByHandle(StampBoxHandle.TopRight, new PdfPoint(-1000, -1000));

        Assert.Equal(10, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(70, d.StampBoxRect.RightPt, 0.01); // 10+60 (largura), não cruzou pra perto de X=0
        Assert.True(d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt >= 60 - 0.01, "largura abaixo do mínimo");
        Assert.Equal(10, d.StampBoxRect.BottomPt, 0.01);
        Assert.Equal(30, d.StampBoxRect.TopPt, 0.01); // 10+20 (altura), não cruzou pra perto de Y=0
        Assert.True(d.StampBoxRect.TopPt - d.StampBoxRect.BottomPt >= 20 - 0.01, "altura abaixo do mínimo");
    }

    [Fact]
    public void ResizeBoxByHandle_ClampsToPageBounds()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc);
        double pageW = d.Pages[0].WidthPt;

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(pageW + 999, 0));

        Assert.Equal(pageW, d.StampBoxRect.RightPt, 0.01);
    }

    [Fact]
    public void ResizeBoxByHandle_NotAdjustingPhase_DoesNothing()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X"); // Drawing, não Adjusting

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(50, 0));

        Assert.Equal(100, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(100, d.StampBoxRect.RightPt, 0.01);
    }

    // ---- EndAdjustGesture + regressão (revisão final da branch): canonicalização na FRONTEIRA do gesto
    // ---- ResizeBoxByHandle legitimamente deixa os 4 escalares brutos INVERTIDOS depois de um
    // cruzamento (contrato da alça, ver doc XML de lá) -- mas só até o gesto TERMINAR. Sem
    // re-canonicalizar na fronteira (mouse-up/LostMouseCapture, ver PdfViewerControl.xaml.cs), o
    // PRÓXIMO gesto herdava a inversão e produzia 2 bugs reais (achados do revisor, ambos reproduzidos
    // ao vivo abaixo antes do fix).

    [Fact]
    public void EndAdjustGesture_NotAdjustingPhase_DoesNothing_NoCrash()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;

        d.EndAdjustGesture(); // fase None -- no-op seguro

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
    }

    [Fact] // prova direta: depois de canonicalizar, o mapeamento fixo alça->escalar bruto volta a
    // corresponder à posição VISUAL correta (a alça Left volta a mover a borda esquerda de verdade).
    public void EndAdjustGesture_CanonicalizesInvertedRawScalars_SoNextGestureMovesCorrectEdge()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc); // 100,100 - 300,200

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-280, 0)); // flip -> [20,100]
        Assert.Equal(20, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(100, d.StampBoxRect.RightPt, 0.01);

        d.EndAdjustGesture(); // fronteira do gesto -- mesma chamada que a View faz no mouse-up
        d.ResizeBoxByHandle(StampBoxHandle.Left, new PdfPoint(-5, 0)); // NOVO gesto, alça Left

        Assert.Equal(100, d.StampBoxRect.RightPt, 0.01); // borda direita PRESERVADA
        Assert.Equal(15, d.StampBoxRect.LeftPt, 0.01); // borda esquerda moveu (20-5)
    }

    [Fact] // REGRESSÃO I1 (probe exato do revisor, reproduzido ao vivo antes do fix): Resize que inverte
    // os brutos seguido DIRETO de um Move -- SEM EndAdjustGesture no meio, exercitando o CINTO
    // defensivo de MoveBoxBy (não a canonicalização da View) -- não pode colapsar a caixa a zero perto
    // da borda. Repro exato: Adjusting 100,100-300,200 -> Resize(Right,-280) [inverte] ->
    // MoveBoxBy(-200,0) -> largura virava 0 antes do fix.
    public void MoveBoxBy_AfterResizeLeavesRawScalarsInverted_DoesNotCollapseToZero()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc); // 100,100 - 300,200

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-280, 0)); // Xa=100 fixo, Xb=20 -- INVERTIDO
        Assert.Equal(20, d.StampBoxRect.LeftPt, 0.01); // sanity: de fato inverteu

        d.MoveBoxBy(new PdfPoint(-200, 0)); // SEM EndAdjustGesture no meio -- exercita o cinto de MoveBoxBy

        double width = d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt;
        Assert.True(width >= 60 - 0.01, $"largura colapsou: {width}pt (esperado >= 60pt)");
    }

    [Fact] // REGRESSÃO I2 (probe exato do revisor, reproduzido ao vivo antes do fix): depois de um
    // cruzamento (flip) e a fronteira do gesto canonicalizada (EndAdjustGesture -- mesma chamada real
    // que a View faz no mouse-up), um NOVO gesto pela alça Left deve mover a borda ESQUERDA visual,
    // NUNCA a direita. Repro exato: flip pra [20,100] -> Resize(Left,-10) -> borda direita virava 90
    // (deveria continuar 100) antes do fix.
    public void ResizeBoxByHandle_NewGestureAfterFlip_MovesLeftEdge_NeverRightEdge()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc); // 100,100 - 300,200

        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(-280, 0)); // flip -> [20,100]
        Assert.Equal(20, d.StampBoxRect.LeftPt, 0.01);
        Assert.Equal(100, d.StampBoxRect.RightPt, 0.01);
        d.EndAdjustGesture(); // fronteira do gesto (mouse-up real, ver Page_MouseLeftButtonUp)

        d.ResizeBoxByHandle(StampBoxHandle.Left, new PdfPoint(-10, 0)); // NOVO gesto, alça Left

        Assert.Equal(100, d.StampBoxRect.RightPt, 0.01); // borda direita PRESERVADA (bug: virava 90)
        Assert.Equal(10, d.StampBoxRect.LeftPt, 0.01); // borda esquerda moveu (20-10)
    }

    [Fact] // FUZZ (adotado da revisão final da branch, achado I1/I2 do revisor) -- ~4000 operações
    // aleatórias (seed FIXA, determinístico) de Resize (alça aleatória) e Move intercaladas, com
    // fronteiras de gesto realistas (EndAdjustGesture entre "arrastos" de tipo diferente -- mesma
    // disciplina que a View real aplica no mouse-up, ver Page_MouseLeftButtonUp/ResetGestureState).
    // Depois de CADA operação (não só nas fronteiras), o invariante largura>=60pt && altura>=20pt &&
    // dentro da página tem que se manter. O revisor reproduziu uma violação na iteração 21 antes do
    // fix; com a canonicalização na fronteira do gesto (+ o cinto de MoveBoxBy), as 4000 passam.
    public void ResizeAndMove_RandomFuzzSequence_AlwaysRespectsMinimumAndPageBounds()
    {
        const int opCount = 4000;
        var rng = new Random(20260816); // seed fixa -- MESMA sequência sempre, determinístico

        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc, 150, 150, 350, 350); // início com folga de todos os lados
        double pageW = d.Pages[0].WidthPt, pageH = d.Pages[0].HeightPt;

        var handles = Enum.GetValues<StampBoxHandle>();
        StampBoxHandle? currentResizeHandle = null;
        bool currentlyMoving = false;

        for (int i = 0; i < opCount; i++)
        {
            // ~15% de chance de terminar o gesto corrente ANTES do próximo op -- fronteira real (mesma
            // disciplina da View: EndAdjustGesture no mouse-up antes do PRÓXIMO gesto poder começar).
            if ((currentResizeHandle is not null || currentlyMoving) && rng.NextDouble() < 0.15)
            {
                d.EndAdjustGesture();
                currentResizeHandle = null;
                currentlyMoving = false;
            }

            if (currentResizeHandle is null && !currentlyMoving)
            {
                if (rng.NextDouble() < 0.5) currentlyMoving = true;
                else currentResizeHandle = handles[rng.Next(handles.Length)];
            }

            double dx = rng.NextDouble() * 400 - 200; // delta grande o bastante pra cruzar/estourar a página
            double dy = rng.NextDouble() * 400 - 200;

            if (currentlyMoving) d.MoveBoxBy(new PdfPoint(dx, dy));
            else d.ResizeBoxByHandle(currentResizeHandle!.Value, new PdfPoint(dx, dy));

            double width = d.StampBoxRect.RightPt - d.StampBoxRect.LeftPt;
            double height = d.StampBoxRect.TopPt - d.StampBoxRect.BottomPt;
            Assert.True(width >= 60 - 0.01, $"iter {i}: largura {width}pt abaixo do mínimo (op={(currentlyMoving ? "Move" : currentResizeHandle.ToString())})");
            Assert.True(height >= 20 - 0.01, $"iter {i}: altura {height}pt abaixo do mínimo (op={(currentlyMoving ? "Move" : currentResizeHandle.ToString())})");
            Assert.True(d.StampBoxRect.LeftPt >= -0.01 && d.StampBoxRect.RightPt <= pageW + 0.01,
                $"iter {i}: fora da página (X) [{d.StampBoxRect.LeftPt},{d.StampBoxRect.RightPt}] pageW={pageW}");
            Assert.True(d.StampBoxRect.BottomPt >= -0.01 && d.StampBoxRect.TopPt <= pageH + 0.01,
                $"iter {i}: fora da página (Y) [{d.StampBoxRect.BottomPt},{d.StampBoxRect.TopPt}] pageH={pageH}");
        }
    }

    // ---- CancelStampBox: reseta TUDO, sem armar funil, de qualquer fase -----------------------------

    [Fact]
    public void CancelStampBox_FromDrawing_ResetsPhaseAndFields()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X");

        d.CancelStampBox();

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Equal(-1, d.StampBoxPageIndex);
        Assert.Null(d.StampBoxCertificateCn);
        Assert.Null(d.StampBoxNotice);
        Assert.Equal(AnnotationTool.None, d.ActiveTool); // reseta TUDO -- inclui sair do modo
    }

    [Fact]
    public void CancelStampBox_FromAdjusting_ResetsAndDeactivatesTool()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc);

        d.CancelStampBox();

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Equal(AnnotationTool.None, d.ActiveTool);
        Assert.False(d.Pages[0].HasStampBox);
        Assert.False(d.Pages[0].IsStampBoxAdjusting);
    }

    [Fact] // GUARD crítico (comentário corrigido, revisão final da branch — PlaceSignatureStampAtAsync
    // foi DELETADO pela Task 2, não é mais o fluxo real): a janela real hoje é entre Sign() ativar
    // ActiveTool=SignatureStamp e o 1º mouse-down chamar BeginStampBoxPlacementAsync -- nesse instante,
    // StampPlacementPhase continua None. Chamar CancelStampBox() nessa janela (ex.: Esc antes de
    // qualquer arrasto começar) NUNCA pode desativar a ferramenta por engano.
    public void CancelStampBox_WhenPhaseAlreadyNone_DoesNotTouchActiveTool()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp; // janela real: ferramenta ativa, arrasto ainda não começou

        d.CancelStampBox();

        Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool); // intocado
    }

    // ---- ConfirmStampBox ------------------------------------------------------------------------

    [Fact]
    public void ConfirmStampBox_FromAdjusting_ReturnsPlacement_ResetsPhase()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc, 100, 100, 300, 200);

        var result = d.ConfirmStampBox();

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.PageIndex);
        Assert.Equal(100, result.Value.Rect.LeftPt, 0.01);
        Assert.Equal(300, result.Value.Rect.RightPt, 0.01);
        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
    }

    [Fact]
    public void ConfirmStampBox_FromDrawing_ReturnsNull_DoesNotReset()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X");

        var result = d.ConfirmStampBox();

        Assert.Null(result);
        Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase); // continua desenhando
    }

    [Fact]
    public void ConfirmStampBox_FromNone_ReturnsNull()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;

        Assert.Null(d.ConfirmStampBox());
    }

    // ---- Cancelamento por troca de ferramenta --------------------------------------------------------

    [Theory]
    [InlineData(false)] // cancela ainda em Drawing
    [InlineData(true)]  // cancela já em Adjusting
    public void SwitchingActiveTool_WhilePlacementActive_CancelsBox(bool reachAdjusting)
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X");
        if (reachAdjusting) { d.UpdateDrawTo(new PdfPoint(300, 200)); d.EndStampDraw(); }

        d.ActiveTool = AnnotationTool.Rectangle; // troca de ferramenta

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Equal(AnnotationTool.Rectangle, d.ActiveTool); // a NOVA ferramenta continua ativa
    }

    // ---- Cancelamento por edição alheia (mutation-gap, OnSessionApplied) ----------------------------

    [Fact] // qualquer Apply (mesmo alheio, SEM tocar ActiveTool -- ex.: Undo/Redo, uma anotação
    // DIFERENTE excluída, um Flatten) durante Adjusting cancela a caixa -- mesma disciplina de
    // SelectedAnnotation/SelectedFormField/SelectedSignature (nunca sobrevivem a um Apply). Chama
    // Session.ApplyEdit DIRETO (não um comando do VM) de propósito: isola o caminho do
    // OnSessionApplied, sem passar pelo OnActiveToolChanged (que JÁ cancelaria sozinho se a edição
    // viesse de trocar de ferramenta -- ver SwitchingActiveTool_WhilePlacementActive_CancelsBox acima).
    public void ApplyEdit_WhileAdjusting_WithActiveToolUnchanged_CancelsStampBoxPlacement()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc);
        Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool); // continua a MESMA ferramenta

        d.Session.ApplyEdit(Fixtures.ThirtyPages()); // edição alheia, ActiveTool intocado

        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Equal(-1, d.StampBoxPageIndex);
    }

    // ---- Overlay (PageViewModel): bool + Rect empurrados, sobrevivem a zoom -------------------------

    [Fact]
    public void Overlay_Drawing_SetsHasStampBox_ButNotAdjusting()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X");

        Assert.True(d.Pages[0].HasStampBox);
        Assert.False(d.Pages[0].IsStampBoxAdjusting);
    }

    [Fact] // I3 (revisão final da branch, achado real do revisor) -- isola o fix do VM, HEADLESS (sem
    // WPF/XAML): StampBoxHandlePoints tem que ficar genuinamente VAZIO durante Drawing, incl. no
    // estado "pequeno demais, permanece Drawing" -- verificado ao vivo escrevendo o STA companheiro
    // (Viewer_StampBoxHandles_HiddenDuringDrawing_ExactlyEightDuringAdjusting) que o cinto de
    // Visibility no XAML sozinho MASCARA uma regressão aqui (o WPF nem gera os containers de um
    // ItemsControl Collapsed, então a árvore visual mostra 0 mesmo se a COLEÇÃO tivesse 8) -- este
    // teste é o único que prova o fix do VM (RefreshStampBoxOverlay/PageViewModel.ApplyZoom só
    // preenchem em Adjusting) de forma isolada, sem depender do cinto do XAML pra "passar por acaso".
    public void HandlePoints_PopulatedOnlyInAdjusting_EmptyDuringDrawing()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=X");
        Assert.Empty(d.Pages[0].StampBoxHandlePoints); // Drawing, arrasto ainda pequeno (degenerado)

        d.UpdateDrawTo(new PdfPoint(120, 105)); // 20x5pt -- abaixo do mínimo
        d.EndStampDraw();
        Assert.Equal(StampPlacementPhase.Drawing, doc.StampPlacementPhase); // permanece Drawing (aviso sutil)
        Assert.Empty(d.Pages[0].StampBoxHandlePoints); // achado EXATO do revisor: nem "pequeno demais"

        d.UpdateDrawTo(new PdfPoint(300, 200)); // continua o MESMO gesto até um tamanho válido
        Assert.Empty(d.Pages[0].StampBoxHandlePoints); // ainda Drawing (retângulo válido, mas não solto)

        d.EndStampDraw();
        Assert.Equal(StampPlacementPhase.Adjusting, doc.StampPlacementPhase);
        Assert.Equal(8, d.Pages[0].StampBoxHandlePoints.Count);
    }

    [Fact]
    public void Overlay_Adjusting_SetsIsStampBoxAdjusting()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc);

        Assert.True(d.Pages[0].HasStampBox);
        Assert.True(d.Pages[0].IsStampBoxAdjusting);
    }

    [Fact]
    public void Overlay_Cancel_ClearsFlagsOnOwningPage()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc);
        Assert.True(d.Pages[0].HasStampBox);

        d.CancelStampBox();

        Assert.False(d.Pages[0].HasStampBox);
        Assert.False(d.Pages[0].IsStampBoxAdjusting);
    }

    [Fact] // a projeção de tela usa o MESMO PointRectToScreenRect que todo outro overlay do app --
    // nenhuma matemática nova (exemplar: SignaturePanelTests).
    public void Overlay_ScreenRect_MatchesPointRectToScreenRectConversion()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc, 100, 100, 300, 200);

        var expected = PageViewModel.PointRectToScreenRect(100, 100, 300, 200, d.Zoom, d.Pages[0].HeightPt);

        Assert.Equal(expected, d.Pages[0].StampBoxScreenRect);
    }

    [Fact]
    public void Overlay_PreviewText_ContainsCertificateCnAndDateLabel()
    {
        var (doc, _) = BuildForStampBox();
        using var d = doc;
        d.ActiveTool = AnnotationTool.SignatureStamp;
        d.BeginStampBoxPlacement(0, new PdfPoint(100, 100), "CN=Fulano de Tal");

        var preview = d.Pages[0].StampBoxPreviewText;

        Assert.NotNull(preview);
        Assert.Contains("CN=Fulano de Tal", preview);
        Assert.Contains(d.StampBoxDateLabel!, preview);
    }

    [Fact] // risco declarado no plano: zoom NO MEIO do ajuste -- o rect vive em pontos de página
    // (zoom-invariante); só a projeção de tela precisa reconverter (exemplar: os 3 overlays
    // existentes -- FormField/AnnotationSelection/SignatureStampHighlight -- via ApplyZoom).
    public void Overlay_ZoomChangeMidAdjust_RecomputesScreenRect()
    {
        var (doc, _) = BuildForStampBox();
        using var d = BeginAdjusting(doc, 100, 100, 300, 200);
        var rectAtZoom1 = d.Pages[0].StampBoxScreenRect;

        d.Zoom = 2.0;

        var expected = PageViewModel.PointRectToScreenRect(100, 100, 300, 200, 2.0, d.Pages[0].HeightPt);
        Assert.Equal(expected, d.Pages[0].StampBoxScreenRect);
        Assert.NotEqual(rectAtZoom1, d.Pages[0].StampBoxScreenRect); // realmente mudou, não é coincidência
    }
}
