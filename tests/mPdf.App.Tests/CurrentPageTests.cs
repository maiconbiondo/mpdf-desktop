using System.IO;
using mPdf.App.ViewModels;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

public class CurrentPageTests
{
    [Fact] // offset 0 -> página 1; offset após 2 páginas -> página 3
    public void UpdateCurrentPageFromScroll_FindsPageByOffset()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        double pageH = doc.Pages[0].DisplayHeight + 12;   // margem vertical total por página
        doc.UpdateCurrentPageFromScroll(0);
        Assert.Equal(1, doc.CurrentPage);
        doc.UpdateCurrentPageFromScroll(pageH * 2 + 1);
        Assert.Equal(3, doc.CurrentPage);
        Assert.Equal("Página 3 de 30", doc.PageCountLabel);
    }
}
