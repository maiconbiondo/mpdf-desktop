using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Data;

namespace mPdf.App.Views;

/// Task 2 (Plano 7): sufixo "(imagem)" nas linhas de imagem da lista de "Juntar documentos"
/// (`MergeFilesDialog.xaml` — `ListBox.ItemTemplate`). Vive em `Views` (não `Services`, onde mora
/// `ImageImport.IsImagePath`) porque `IValueConverter` é um tipo WPF-only; duplicar a checagem de
/// extensão aqui (3 strings) é mais simples do que expor `ImageImport` como `public` só pra este uso de
/// apresentação — `MergeFilesDialog.Files` continua uma lista de CAMINHOS crus (o que
/// `MainViewModel.Merge`/`ImageImport` de fato leem); este converter é PURAMENTE de exibição, nunca
/// muta o que está em `Files`.
public sealed class ImageFileLabelConverter : IValueConverter
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string path = value as string ?? string.Empty;
        bool isImage = ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        return isImage ? $"{path} (imagem)" : path;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
