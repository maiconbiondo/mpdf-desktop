namespace mPdf.Documents.Tests;

public static class Fixtures
{
    // sobe da pasta bin até a raiz do repo (onde está mPdf.slnx) e entra em tests/fixtures
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "tests", "fixtures");
        }
    }
    public static byte[] A4() => File.ReadAllBytes(Path.Combine(Root, "fixture-a4.pdf"));
    public static byte[] ThirtyPages() => File.ReadAllBytes(Path.Combine(Root, "fixture-30p.pdf"));
}
