using System.IO;
using System.Windows.Media.Imaging;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Documents;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

public class ZoomAnchorTests
{
    private static DocumentViewModel Doc() =>
        new(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));

    [Fact] // zoom muda mas o bitmap antigo (esticado) continua visível até a nova renderização chegar
    public void ApplyZoom_KeepsStaleBitmapUntilReplacement()
    {
        using var doc = Doc();
        using var scheduler = new RenderScheduler((i, sc) => new RenderedPage(1, 1, new byte[4]));
        var page = new PageViewModel(0, new PdfPageSize(595, 842), scheduler, doc);
        page.ImageSource = new BitmapImage();   // simula bitmap de um render anterior já entregue

        page.ApplyZoom(2.0);

        Assert.NotNull(page.ImageSource);       // sem "flash" de tela em branco durante o zoom
    }

    [Fact] // compensação de offset: mesma proporção do zoom, para a posição de leitura não pular
    public void ZoomChange_RescalesScrollOffset()
    {
        Assert.Equal(200.0, PdfViewerControl.ComputeAnchoredOffset(100.0, 1.0, 2.0), 3);
        Assert.Equal(50.0, PdfViewerControl.ComputeAnchoredOffset(100.0, 2.0, 1.0), 3);
        Assert.Equal(0.0, PdfViewerControl.ComputeAnchoredOffset(0.0, 1.0, 3.0), 3);
    }

    [Fact] // regressão: o callback de âncora adiado deve virar no-op se a aba trocou de DataContext
    // enquanto ele estava na fila do Dispatcher (entregas de render em prioridade Normal podem
    // atrasar o callback, agendado em Loaded, até depois de uma troca de aba rápida)
    public void IsCurrentDocument_DetectsDataContextSwap()
    {
        using var docA = Doc();
        using var docB = Doc();

        Assert.True(PdfViewerControl.IsCurrentDocument(docA, docA));    // ainda a mesma aba -> aplica
        Assert.False(PdfViewerControl.IsCurrentDocument(docB, docA));   // trocou de aba -> vira no-op
        Assert.False(PdfViewerControl.IsCurrentDocument(null, docA));   // sem DataContext (aba fechada) -> vira no-op
    }
}
