using System.IO;
using Xunit;

namespace mPdf.PreviewHandler.Tests;

public class PreviewMultiPaginaGuardTests
{
    private static string Src()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "mPdf.PreviewHandler.Aot", "PreviewWindow.cs"));
    }

    [Fact] // usa o layout multi-pagina + cache LRU (nao renderiza mais so a pagina 0)
    public void UsaLayoutECache()
    {
        var s = Src();
        Assert.Contains("PreviewLayout.Calcular", s);
        Assert.Contains("LruCache", s);
    }

    [Fact] // NAO ha mais o render hardcoded so da pagina 0 (RenderFitWidth(pdf, 0, ...))
    public void SemPagina0Hardcoded()
        => Assert.DoesNotContain("RenderFitWidth(pdf, 0", Src());
}
