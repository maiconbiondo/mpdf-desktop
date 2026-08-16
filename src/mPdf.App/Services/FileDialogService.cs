using System.IO;
using Microsoft.Win32;

namespace mPdf.App.Services;

public sealed class FileDialogService : IFileDialogService
{
    public string? PickPdfToOpen()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Abrir PDF",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickPdfToSaveAs(string currentPath)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Salvar como",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileName(currentPath),
            InitialDirectory = Path.GetDirectoryName(currentPath),
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickImageToImport()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Adicionar carimbo",
            Filter = "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickPdfToSave(string suggestedName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Salvar como",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = suggestedName,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
