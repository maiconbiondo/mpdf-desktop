using System.IO;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

public class RecentFilesStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-rec-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact] // adicionar coloca no topo, sem duplicar, persistindo entre instâncias
    public void Add_DedupesAndPersists()
    {
        var s1 = new RecentFilesStore(_dir);
        s1.Add(@"C:\a.pdf"); s1.Add(@"C:\b.pdf"); s1.Add(@"C:\a.pdf");
        var s2 = new RecentFilesStore(_dir);
        Assert.Equal([@"C:\a.pdf", @"C:\b.pdf"], s2.Load());
    }

    [Fact] // máximo de 10 entradas
    public void Add_CapsAtTen()
    {
        var s = new RecentFilesStore(_dir);
        for (int i = 0; i < 15; i++) s.Add($@"C:\{i}.pdf");
        Assert.Equal(10, s.Load().Count);
        Assert.Equal(@"C:\14.pdf", s.Load()[0]);
    }

    [Fact] // Task 7: Remove tira da lista e persiste entre instâncias (recente inválido)
    public void Remove_DeletesAndPersists()
    {
        var s1 = new RecentFilesStore(_dir);
        s1.Add(@"C:\a.pdf"); s1.Add(@"C:\b.pdf");

        s1.Remove(@"C:\a.pdf");

        var s2 = new RecentFilesStore(_dir);
        Assert.Equal([@"C:\b.pdf"], s2.Load());
    }
}
