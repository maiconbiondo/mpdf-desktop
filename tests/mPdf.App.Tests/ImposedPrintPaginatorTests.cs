using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using mPdf.App.Services;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

// Plano 25 (Task 2): paginador de impressão avançada. Contagem de FOLHAS por modo (não precisa de papel/
// STA) + um smoke de GetPage numa thread STA (o DrawingVisual/render não pode lançar).
public class ImposedPrintPaginatorTests
{
    private static string Fixture => Path.Combine(Fixtures.Root, "fixture-30p.pdf"); // 30 páginas

    [Fact]
    public void PageCount_NUp1_UmaFolhaPorPagina()
    {
        using var s = DocumentSession.Open(Fixture);
        var p = new ImposedPrintPaginator(s, new LayoutImpressao { NUp = 1 }, 300, null);
        try { Assert.Equal(30, p.PageCount); } finally { p.Dispose(); }
    }

    [Fact]
    public void PageCount_NUp4_TetoDaDivisao()
    {
        using var s = DocumentSession.Open(Fixture);
        var p = new ImposedPrintPaginator(s, new LayoutImpressao { NUp = 4 }, 300, null);
        try { Assert.Equal(8, p.PageCount); } finally { p.Dispose(); } // ceil(30/4)=8
    }

    [Fact]
    public void PageCount_Livreto_PaddedMultiploDe4_DivididoPor2()
    {
        using var s = DocumentSession.Open(Fixture);
        var p = new ImposedPrintPaginator(s, new LayoutImpressao { Livreto = true }, 300, null);
        try { Assert.Equal(16, p.PageCount); } finally { p.Dispose(); } // padded 32 / 2 faces = 16
    }

    [Fact]
    public void PageCount_ComRange_UsaSoAsPaginasDoRange()
    {
        using var s = DocumentSession.Open(Fixture);
        // range 1..8 (8 páginas), N-up=4 -> 2 folhas.
        var p = new ImposedPrintPaginator(s, new LayoutImpressao { NUp = 4 }, 300, new System.Windows.Controls.PageRange(1, 8));
        try { Assert.Equal(2, p.PageCount); } finally { p.Dispose(); }
    }

    [Fact]
    public void GetPage_NUp4_ComBorda_NaoLanca_ERetornaFolhaComTamanho()
    {
        Exception? erro = null;
        var t = new Thread(() =>
        {
            try
            {
                using var s = DocumentSession.Open(Fixture);
                var p = new ImposedPrintPaginator(s, new LayoutImpressao { NUp = 4, Borda = true }, 150, null)
                {
                    PageSize = new Size(794, 1123) // A4 em DIPs
                };
                try
                {
                    Assert.Equal(8, p.PageCount);
                    DocumentPage folha = p.GetPage(0); // 1ª folha: 4 páginas impostas + bordas
                    Assert.NotNull(folha.Visual);
                    Assert.Equal(794, folha.Size.Width, 1);
                    Assert.Equal(1123, folha.Size.Height, 1);
                }
                finally { p.Dispose(); }
            }
            catch (Exception ex) { erro = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(20)), "thread STA não terminou em 20s (possível hang do WPF)");
        if (erro is not null) ExceptionDispatchInfo.Capture(erro).Throw();
    }
}
