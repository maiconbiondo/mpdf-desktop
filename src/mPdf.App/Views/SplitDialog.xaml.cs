using System.Windows;
using Microsoft.Win32;

namespace mPdf.App.Views;

/// Janela pt-BR de "Dividir documento" (Task 4, Plano 3b) — campo de texto para os intervalos de página
/// (validados pelo `PageRangeParser` NO VM, não aqui — esta janela só coleta a string crua) + seletor de
/// pasta de destino (`Microsoft.Win32.OpenFolderDialog`, disponível a partir do .NET 8). Mesmo precedente
/// de `AnnotationTextDialog`/`MergeFilesDialog`: sem mediação de VM, o serviço
/// (`Services.SplitDialogService`) lê `RangesText`/`DestinationFolder` direto depois de `ShowDialog()`.
public partial class SplitDialog : Window
{
    public string RangesText { get; private set; } = "";
    public string? DestinationFolder { get; private set; }

    public SplitDialog()
    {
        InitializeComponent();
    }

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Escolher pasta de destino" };
        if (dlg.ShowDialog() == true) FolderTextBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // Campos obrigatórios (ranges + pasta) — sem os dois, o diálogo permanece aberto em vez de
        // devolver um resultado incompleto que o VM teria que validar de novo (mesmo espírito de
        // MergeFilesDialog.Ok_Click recusar uma lista vazia).
        if (string.IsNullOrWhiteSpace(RangesTextBox.Text) || string.IsNullOrWhiteSpace(FolderTextBox.Text))
            return;
        RangesText = RangesTextBox.Text;
        DestinationFolder = FolderTextBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
