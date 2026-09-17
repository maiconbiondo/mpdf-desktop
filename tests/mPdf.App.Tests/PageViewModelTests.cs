using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using mPdf.App.ViewModels;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

// Item (b) da Task 1 (Plano 3a): prefetch da TextPage em OnRealized, fora da thread de UI, guardado
// por flag idempotente — sem esperar o primeiro gesto de seleção (BeginSelection, que continua
// existindo como fallback SÍNCRONO). TextPageCache é interno, exposto só pra teste via
// InternalsVisibleTo (mesmo padrão de DocumentViewModel.ThumbnailRenderer).
public class PageViewModelTests
{
    private static DocumentViewModel Doc() =>
        new(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));

    [Fact]
    public void OnRealized_PrefetchesTextPage_WithoutWaitingForSelection()
    {
        using var doc = Doc();
        var page = doc.Pages[0];
        Assert.Null(page.TextPageCache); // sanity: nada carregado antes de realizar

        page.OnRealized();

        WaitUntil(() => page.TextPageCache is not null, TimeSpan.FromSeconds(5));
        Assert.NotNull(page.TextPageCache);
        Assert.Equal(0, page.TextPageCache!.PageIndex);
    }

    [Fact] // Os dois caminhos coexistem: mesmo que a página NUNCA tenha sido realizada (sem
    // prefetch), o primeiro gesto de seleção ainda carrega a TextPage sozinho, síncrono.
    public void BeginSelection_StillLoadsTextPage_WithoutPriorRealize()
    {
        using var doc = Doc();
        var page = doc.Pages[0];
        Assert.Null(page.TextPageCache);

        page.BeginSelection(new Point(10, 10));

        Assert.NotNull(page.TextPageCache);
    }

    [Fact] // Guard idempotente: um ciclo de derealize/realize NÃO reagenda o prefetch nem descarta o
    // cache já pronto (OnDerealized só libera o bitmap — ImageSource — não a TextPage).
    public void OnRealized_AfterDerealize_KeepsPrefetchedTextPageInstance()
    {
        using var doc = Doc();
        var page = doc.Pages[0];

        page.OnRealized();
        WaitUntil(() => page.TextPageCache is not null, TimeSpan.FromSeconds(5));
        var cached = page.TextPageCache;

        page.OnDerealized();
        page.OnRealized(); // 2ª realização — flag já true, não deve reagendar nem trocar a instância

        Assert.Same(cached, page.TextPageCache);
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout) Thread.Sleep(10);
    }
}
