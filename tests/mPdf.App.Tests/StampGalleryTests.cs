using System.IO;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

// Task 9 (Plano 3a): StampGallery isolada (dir temp por teste, nunca toca %AppData%\mPDF\carimbos real
// — mesmo exemplar de RecentFilesStoreTests).
public class StampGalleryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-stamps-{Guid.NewGuid():N}");
    private readonly string _sourceDir;

    public StampGalleryTests()
    {
        _sourceDir = Path.Combine(Path.GetTempPath(), $"mpdf-stamps-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sourceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        try { Directory.Delete(_sourceDir, true); } catch { }
    }

    private string WriteSourceFile(string name, byte[]? bytes = null)
    {
        string path = Path.Combine(_sourceDir, name);
        File.WriteAllBytes(path, bytes ?? [1, 2, 3]);
        return path;
    }

    [Fact] // ctor cria o diretório se não existir — mesmo padrão de AppConfig/RecentFilesStore
    public void Ctor_CreatesDirectory_IfMissing()
    {
        Assert.False(Directory.Exists(_dir));
        _ = new StampGallery(_dir);
        Assert.True(Directory.Exists(_dir));
    }

    [Fact] // Add copia o arquivo PARA DENTRO da galeria, preservando o nome; Load lista o nome copiado.
    public void Add_CopiesFileIntoGallery_LoadListsIt()
    {
        var gallery = new StampGallery(_dir);
        string source = WriteSourceFile("logo.png");

        string name = gallery.Add(source);

        Assert.Equal("logo.png", name);
        Assert.True(File.Exists(Path.Combine(_dir, "logo.png")));
        Assert.Equal(["logo.png"], gallery.Load());
    }

    [Fact] // JPG também é aceito (não só PNG) — mesma checagem de extensão, case-insensitive.
    public void Add_AcceptsJpgAndJpeg_CaseInsensitive()
    {
        var gallery = new StampGallery(_dir);
        gallery.Add(WriteSourceFile("a.JPG"));
        gallery.Add(WriteSourceFile("b.jpeg"));

        Assert.Equal(2, gallery.Load().Count);
    }

    [Theory] // extensão fora de PNG/JPG -> ArgumentException pt-BR, NUNCA copia o arquivo.
    [InlineData("documento.pdf")]
    [InlineData("figura.gif")]
    [InlineData("semextensao")]
    public void Add_RejectsNonImageExtension_ThrowsArgumentException_DoesNotCopy(string fileName)
    {
        var gallery = new StampGallery(_dir);
        string source = WriteSourceFile(fileName);

        var ex = Assert.Throws<ArgumentException>(() => gallery.Add(source));
        Assert.Contains("PNG", ex.Message);
        Assert.Empty(gallery.Load());
    }

    [Fact] // dedupe por nome: 2º Add do MESMO nome de arquivo vira " (2)" (exemplar: EditCopy).
    public void Add_NameCollision_AppendsCounterSuffix()
    {
        var gallery = new StampGallery(_dir);
        string name1 = gallery.Add(WriteSourceFile("carimbo.png", [1]));

        // 2º arquivo de origem, MESMO nome base "carimbo.png" — precisa vir de outra pasta/origem pra
        // não sobrescrever o arquivo de origem original.
        string secondSourceDir = Path.Combine(_sourceDir, "sub");
        Directory.CreateDirectory(secondSourceDir);
        string secondSource = Path.Combine(secondSourceDir, "carimbo.png");
        File.WriteAllBytes(secondSource, [2, 2]);
        string name2 = gallery.Add(secondSource);

        Assert.Equal("carimbo.png", name1);
        Assert.Equal("carimbo (2).png", name2);
        Assert.Equal(new byte[] { 1 }, gallery.LoadBytes(name1));
        Assert.Equal(new byte[] { 2, 2 }, gallery.LoadBytes(name2));
        Assert.Equal(["carimbo (2).png", "carimbo.png"], gallery.Load()); // ordem alfabética
    }

    [Fact] // Load devolve lista vazia numa galeria recém-criada (sem nenhum Add ainda).
    public void Load_EmptyGallery_ReturnsEmpty()
    {
        var gallery = new StampGallery(_dir);
        Assert.Empty(gallery.Load());
    }

    [Fact] // Load ignora arquivos NÃO-imagem que por algum motivo estejam na pasta (ex.: um .txt solto).
    public void Load_IgnoresNonImageFiles()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "notas.txt"), "não é imagem");
        var gallery = new StampGallery(_dir);
        gallery.Add(WriteSourceFile("real.png"));

        Assert.Equal(["real.png"], gallery.Load());
    }

    [Fact] // LoadBytes lê o conteúdo REAL do arquivo copiado (prova que Add de fato copiou os bytes, não só o nome).
    public void LoadBytes_ReturnsCopiedFileContent()
    {
        var gallery = new StampGallery(_dir);
        byte[] original = [10, 20, 30, 40];
        string name = gallery.Add(WriteSourceFile("x.png", original));

        Assert.Equal(original, gallery.LoadBytes(name));
    }

    [Fact] // Remove apaga o arquivo da galeria — some de Load depois.
    public void Remove_DeletesFile_LoadNoLongerListsIt()
    {
        var gallery = new StampGallery(_dir);
        string name = gallery.Add(WriteSourceFile("apagar.png"));
        Assert.Single(gallery.Load());

        gallery.Remove(name);

        Assert.Empty(gallery.Load());
        Assert.False(File.Exists(Path.Combine(_dir, name)));
    }
}
