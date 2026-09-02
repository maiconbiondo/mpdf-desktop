using System.IO;
using mPdf.App.Services;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

// Task 2 (Plano 7): ImageImport — costura ÚNICA de "abrir/juntar/inserir aceitam imagem" na fronteira do
// App (Abrir/Juntar/Inserir chamam isto em vez de duplicar ReadAllBytes+IsSupportedImage+ImageToPdf 3
// vezes). FakePdfEditor (DocumentViewModelTests.cs) — NUNCA o motor real: estes testes provam a
// ORQUESTRAÇÃO (extensão -> leitura -> sniff -> conversão -> nomear o arquivo no erro), não o
// comportamento de ImageToPdf em si (isso é responsabilidade de PdfEditorTests, Task 1).
public class ImageImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-imageimport-{Guid.NewGuid():N}");
    public ImageImportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteFile(string fileName, byte[]? bytes = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, bytes ?? new byte[] { 1, 2, 3 });
        return path;
    }

    // ---- IsImagePath: extensão, case-insensitive (mesmo critério do filtro dos diálogos) -------------

    [Theory]
    [InlineData("foto.jpg", true)]
    [InlineData("FOTO.JPG", true)]
    [InlineData("foto.jpeg", true)]
    [InlineData("foto.JPEG", true)]
    [InlineData("foto.png", true)]
    [InlineData("foto.PNG", true)]
    [InlineData("documento.pdf", false)]
    [InlineData("nota.txt", false)]
    [InlineData("sem-extensao", false)]
    public void IsImagePath_DecidesPorExtensao(string fileName, bool expected) =>
        Assert.Equal(expected, ImageImport.IsImagePath(fileName));

    // ---- ConvertToPdf: fluxo feliz ---------------------------------------------------------------------

    [Fact]
    public void ConvertToPdf_ReadsFile_ChecksMagicBytes_ThenConverts()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0x01 };
        var path = WriteFile("foto.jpg", bytes);
        var fake = new FakePdfEditor { ImageToPdfResult = Fixtures.ThirtyPages() };

        var result = ImageImport.ConvertToPdf(path, fake);

        Assert.Equal(1, fake.IsSupportedImageCallCount);
        Assert.Equal(bytes, fake.LastIsSupportedImageBytes);
        Assert.Single(fake.ImageToPdfInputs);
        Assert.Equal(bytes, fake.ImageToPdfInputs[0]);
        Assert.Equal(Fixtures.ThirtyPages(), result);
    }

    // ---- ConvertToPdf: magic-bytes recusam ANTES de chamar ImageToPdf ---------------------------------

    [Fact]
    public void ConvertToPdf_UnsupportedMagicBytes_ThrowsNamingFile_NeverCallsImageToPdf()
    {
        var path = WriteFile("foto.jpg");
        var fake = new FakePdfEditor { IsSupportedImageResult = false };

        var ex = Assert.Throws<PdfEditingException>(() => ImageImport.ConvertToPdf(path, fake));

        Assert.Contains("foto.jpg", ex.Message);
        Assert.Equal(0, fake.ImageToPdfCallCount);
    }

    // ---- ConvertToPdf: motor lança -> mensagem RE-EMBRULHADA nomeando o arquivo -----------------------

    [Fact]
    public void ConvertToPdf_EngineThrows_WrapsMessageWithFileName()
    {
        var path = WriteFile("retrato.png");
        var fake = new FakePdfEditor { ThrowOnImageToPdf = new PdfEditingException("Imagem corrompida.") };

        var ex = Assert.Throws<PdfEditingException>(() => ImageImport.ConvertToPdf(path, fake));

        Assert.Contains("retrato.png", ex.Message);
        Assert.Contains("Imagem corrompida.", ex.Message);
    }

    // ---- ConvertToPdf: arquivo inexistente -> mensagem pt-BR (mesmo texto de DocumentSession.Open) ----

    [Fact]
    public void ConvertToPdf_MissingFile_ThrowsFileNotFoundException_PtBrMessage()
    {
        var path = Path.Combine(_dir, "nao-existe.jpg");
        var fake = new FakePdfEditor();

        var ex = Assert.Throws<FileNotFoundException>(() => ImageImport.ConvertToPdf(path, fake));

        Assert.Equal("Arquivo não encontrado.", ex.Message);
    }

    // ---- ConvertToPdfAsync: mesmo núcleo, fora da UI thread (Task.Run) ---------------------------------

    [Fact]
    public async Task ConvertToPdfAsync_ConvertsSameAsSyncCore()
    {
        var bytes = new byte[] { 9, 9, 9 };
        var path = WriteFile("foto.jpeg", bytes);
        var fake = new FakePdfEditor { ImageToPdfResult = Fixtures.A4() };

        var result = await ImageImport.ConvertToPdfAsync(path, fake);

        Assert.Single(fake.ImageToPdfInputs);
        Assert.Equal(bytes, fake.ImageToPdfInputs[0]);
        Assert.Equal(Fixtures.A4(), result);
    }

    // ---- ReadOrConvertToPdf: usado por Juntar/Inserir, que misturam PDF e imagem na mesma lista --------

    [Fact]
    public void ReadOrConvertToPdf_PdfPath_ReadsRawBytes_NeverCallsImageToPdf()
    {
        var bytes = new byte[] { 5, 6, 7, 8 };
        var path = WriteFile("documento.pdf", bytes);
        var fake = new FakePdfEditor();

        var result = ImageImport.ReadOrConvertToPdf(path, fake);

        Assert.Equal(bytes, result);
        Assert.Equal(0, fake.ImageToPdfCallCount);
        Assert.Equal(0, fake.IsSupportedImageCallCount);
    }

    [Fact]
    public void ReadOrConvertToPdf_ImagePath_ConvertsViaEngine()
    {
        var bytes = new byte[] { 4, 2 };
        var path = WriteFile("foto.png", bytes);
        var fake = new FakePdfEditor { ImageToPdfResult = Fixtures.ThirtyPages() };

        var result = ImageImport.ReadOrConvertToPdf(path, fake);

        Assert.Single(fake.ImageToPdfInputs);
        Assert.Equal(bytes, fake.ImageToPdfInputs[0]);
        Assert.Equal(Fixtures.ThirtyPages(), result);
    }
}
