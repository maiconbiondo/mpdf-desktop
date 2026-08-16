using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Janela "Exportar página como imagem" (Task 4, Plano 7) — DIFERENTE de `SignDialog`/`MergeFilesDialog`
/// (código-behind é o próprio VM do diálogo): aqui `DataContext` é um `ExportImageViewModel` REAL, já
/// construído pelo chamador (`Services.ExportImageDialogService`) — mesma estrutura de `BatchSignDialog`.
/// Este code-behind só resolve o que o binding puro não cobre sem inventar um `IValueConverter` novo: os
/// 3 grupos de RadioButton (formato/alcance/resolução), a escolha de destino (SaveFileDialog p/ página
/// única, OpenFolderDialog p/ todas as páginas — decide qual dos dois com base no Alcance CORRENTE do
/// VM) e o bloqueio de fechar a janela em pleno voo.
public partial class ExportImageDialog : Window
{
    private ExportImageViewModel ViewModel => (ExportImageViewModel)DataContext;

    public ExportImageDialog(ExportImageViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Estado inicial dos radios a partir do VM (defaults: Png/CurrentPage/150 -- ver ctor do VM).
        PngRadio.IsChecked = viewModel.Format == ExportImageFormat.Png;
        JpgRadio.IsChecked = viewModel.Format == ExportImageFormat.Jpg;
        CurrentPageRadio.IsChecked = viewModel.Range == ExportImageRange.CurrentPage;
        AllPagesRadio.IsChecked = viewModel.Range == ExportImageRange.AllPages;
        Dpi150Radio.IsChecked = viewModel.Dpi == 150;
        Dpi300Radio.IsChecked = viewModel.Dpi == 300;
    }

    private void PngRadio_Checked(object sender, RoutedEventArgs e) => ViewModel.Format = ExportImageFormat.Png;
    private void JpgRadio_Checked(object sender, RoutedEventArgs e) => ViewModel.Format = ExportImageFormat.Jpg;

    // Trocar o Alcance invalida um destino já escolhido (um CAMINHO DE ARQUIVO não serve pra "todas as
    // páginas", nem uma PASTA serve pra "página atual") -- limpa os dois lados (VM + TextBox) pra forçar
    // o usuário a escolher de novo, em vez de deixar StartCommand.CanExecute mentir sobre um destino que
    // não faz mais sentido pro alcance atual.
    private void CurrentPageRadio_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.Range = ExportImageRange.CurrentPage;
        ViewModel.Destination = null;
        DestinationTextBox.Text = "";
    }

    private void AllPagesRadio_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.Range = ExportImageRange.AllPages;
        ViewModel.Destination = null;
        DestinationTextBox.Text = "";
    }

    private void Dpi150Radio_Checked(object sender, RoutedEventArgs e) => ViewModel.Dpi = 150;
    private void Dpi300Radio_Checked(object sender, RoutedEventArgs e) => ViewModel.Dpi = 300;

    /// Página atual -> `SaveFileDialog` filtrado pelo formato CORRENTE (mesmo padrão de
    /// `FileDialogService.PickPdfToSaveAs`); todas as páginas -> `OpenFolderDialog` (mesmo padrão de
    /// `SplitDialog.PickFolder_Click`) -- os arquivos individuais ganham nome próprio
    /// (`ExportImageViewModel.BuildPagePath`, "nome-pNNN.ext"), a pasta é só o CONTAINER.
    private void PickDestination_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Range == ExportImageRange.CurrentPage)
        {
            string ext = ViewModel.Format == ExportImageFormat.Png ? "png" : "jpg";
            var dlg = new SaveFileDialog
            {
                Title = "Exportar página como imagem",
                Filter = ViewModel.Format == ExportImageFormat.Png
                    ? "Imagem PNG (*.png)|*.png"
                    : "Imagem JPG (*.jpg)|*.jpg",
                DefaultExt = ext,
                FileName = $"pagina.{ext}",
            };
            if (dlg.ShowDialog() != true) return;
            ViewModel.Destination = dlg.FileName;
            DestinationTextBox.Text = dlg.FileName;
        }
        else
        {
            var dlg = new OpenFolderDialog { Title = "Escolher pasta de destino" };
            if (dlg.ShowDialog() != true) return;
            ViewModel.Destination = dlg.FolderName;
            DestinationTextBox.Text = dlg.FolderName;
        }
    }

    /// Bloqueia fechar a janela (✕, Alt+F4, Esc no botão "Fechar") enquanto a exportação está EM VOO —
    /// mesmo contrato de `BatchSignDialog.Window_Closing`: força "Cancelar exportação" primeiro (que para
    /// ANTES da próxima página, nunca abandona um `Task.Run` no meio).
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (ViewModel.IsRunning) e.Cancel = true;
    }
}
