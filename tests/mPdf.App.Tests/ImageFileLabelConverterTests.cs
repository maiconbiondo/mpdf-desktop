using System.Globalization;
using mPdf.App.Views;
using Xunit;

namespace mPdf.App.Tests;

// Task 2 (Plano 7): sufixo "(imagem)" nas linhas de imagem da lista de "Juntar documentos"
// (MergeFilesDialog.xaml). Converter puro (IValueConverter) -- testável direto, sem precisar
// instanciar a Window/ListBox de verdade.
public class ImageFileLabelConverterTests
{
    private readonly ImageFileLabelConverter _converter = new();

    [Theory]
    [InlineData(@"C:\pastas\foto.jpg", @"C:\pastas\foto.jpg (imagem)")]
    [InlineData(@"C:\pastas\FOTO.JPG", @"C:\pastas\FOTO.JPG (imagem)")]
    [InlineData(@"C:\pastas\foto.jpeg", @"C:\pastas\foto.jpeg (imagem)")]
    [InlineData(@"C:\pastas\foto.png", @"C:\pastas\foto.png (imagem)")]
    public void Convert_ImagePath_AppendsSuffix(string path, string expected) =>
        Assert.Equal(expected, _converter.Convert(path, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_PdfPath_ReturnsPathUnchanged()
    {
        var path = @"C:\pastas\documento.pdf";
        Assert.Equal(path, _converter.Convert(path, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Convert_NullValue_ReturnsEmptyString() =>
        Assert.Equal(string.Empty, _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void ConvertBack_ThrowsNotSupported() =>
        Assert.Throws<NotSupportedException>(() =>
            _converter.ConvertBack("x", typeof(string), null, CultureInfo.InvariantCulture));
}
