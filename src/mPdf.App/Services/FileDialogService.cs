using System.IO;
using Microsoft.Win32;

namespace mPdf.App.Services;

public sealed class FileDialogService : IFileDialogService
{
    // Task 2 (Plano 7): filtro estendido para aceitar imagens (*.jpg/*.jpeg/*.png) além de PDF — este
    // MESMO diálogo é usado tanto por `MainViewModel.OpenFile` ("Abrir") quanto por
    // `OrganizerViewModel.Insert` ("Inserir"), então os dois ganham suporte a imagem de graça, sem
    // duplicar o filtro em 2 lugares. Um caminho de imagem escolhido aqui é convertido na fronteira do
    // App (`ImageImport`) pelos dois chamadores — este serviço só devolve o caminho escolhido, nunca
    // decide o que fazer com ele.
    public string? PickPdfToOpen()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Abrir",
            Filter = "Documentos PDF e imagens (*.pdf;*.jpg;*.jpeg;*.png)|*.pdf;*.jpg;*.jpeg;*.png|" +
                     "Documentos PDF (*.pdf)|*.pdf|Imagens (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
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
        // Task 3 (Plano 7): título NEUTRO ("Escolher imagem", antes "Adicionar carimbo") — este MESMO
        // diálogo agora serve 2 chamadores (MainViewModel.AddStamp, galeria de carimbos, Task 9/Plano 3a;
        // e DocumentViewModel.ToggleImageTool, "🖼 Imagem", Task 3/Plano 7) — ver XML doc em
        // IFileDialogService. Nenhum teste depende do texto exato (varrido antes da troca).
        var dlg = new OpenFileDialog
        {
            Title = "Escolher imagem",
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
