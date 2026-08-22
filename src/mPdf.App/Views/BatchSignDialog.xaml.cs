using System.ComponentModel;
using System.Windows;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Janela "Assinar em lote" (Task 5, Plano 4) — DIFERENTE de `SignDialog`/`MergeFilesDialog` (código-
/// behind é o próprio VM do diálogo): aqui `DataContext` é um `BatchSignViewModel` REAL, já construído
/// pelo chamador (`Services.BatchSignDialogService`) — a lógica toda vive lá, testável sem esta janela
/// (ver BatchSignViewModelTests). Este code-behind só resolve o que o binding puro não cobre sem
/// inventar um `IValueConverter` novo: os RadioButtons de carimbo (não há binding two-way direto pra
/// "qual dos dois está marcado" sem um conversor) e o bloqueio de fechar a janela em pleno voo.
public partial class BatchSignDialog : Window
{
    private BatchSignViewModel ViewModel => (BatchSignViewModel)DataContext;

    public BatchSignDialog(BatchSignViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Estado inicial dos radios a partir do VM (default PlaceStamp=false -- "Sem carimbo" marcado).
        NoStampRadio.IsChecked = !viewModel.PlaceStamp;
        PlaceStampRadio.IsChecked = viewModel.PlaceStamp;
    }

    private void NoStampRadio_Checked(object sender, RoutedEventArgs e) => ViewModel.PlaceStamp = false;
    private void PlaceStampRadio_Checked(object sender, RoutedEventArgs e) => ViewModel.PlaceStamp = true;

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is string path) ViewModel.RemoveFileCommand.Execute(path);
    }

    /// Bloqueia fechar a janela (✕ da barra de título, Alt+F4, Esc no botão "Fechar") enquanto o lote
    /// está EM VOO — forçar o usuário a clicar "Cancelar assinatura" primeiro (que para ANTES do próximo
    /// arquivo, nunca abandona um `Task.Run` de assinatura no meio). Sem isto, fechar a janela durante
    /// Running deixaria o `Task.Run` órfão rodando em background sem ninguém pra observar o resultado.
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (ViewModel.IsRunning) e.Cancel = true;
    }
}
