using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using mPdf.App.Views;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

// Plano 25 (Task 2b): smoke do diálogo de impressão avançada — constrói (carrega o XAML, resolve os
// estilos do tema, inicializa a impressora com fallback) e renderiza a prévia da 1ª folha no ctor.
// STA (WPF). Não abre ShowDialog (bloquearia) nem imprime de verdade.
public class DialogoImpressaoTests
{
    [Fact]
    public void Constroi_ERenderizaPrevia_SemLancar()
    {
        Exception? erro = null;
        var t = new Thread(() =>
        {
            try
            {
                using var s = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
                var dlg = new DialogoImpressao(s, "teste"); // ctor: InitializeComponent + AtualizarPrevia
                Assert.NotNull(dlg.ImgPreview.Source); // a prévia da 1ª folha foi renderizada
            }
            catch (Exception ex) { erro = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(25)), "thread STA não terminou em 25s (possível hang do WPF)");
        if (erro is not null) ExceptionDispatchInfo.Capture(erro).Throw();
    }
}
