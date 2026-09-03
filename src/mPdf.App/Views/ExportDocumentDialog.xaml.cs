using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Janela "Exportar como Word/Excel" (Task 3, Plano 16) — mesma estrutura de `ExportImageDialog`:
/// `DataContext` é um `ExportDocumentViewModel` REAL, já construído pelo chamador
/// (`Services.ExportDocumentDialogService`). Este code-behind só resolve o que o binding puro não cobre
/// sem inventar um `IValueConverter` novo: os RadioButton de alcance (inteiro/intervalo), a escolha de
/// destino (`SaveFileDialog` filtrado pelo Kind) e o bloqueio de fechar a janela em pleno voo.
public partial class ExportDocumentDialog : Window
{
    private ExportDocumentViewModel ViewModel => (ExportDocumentViewModel)DataContext;

    public ExportDocumentDialog(ExportDocumentViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Estado inicial dos radios a partir do VM (default: AllPages -- ver ctor do VM).
        AllPagesRadio.IsChecked = viewModel.Range == ExportDocumentRange.AllPages;
        CustomRangeRadio.IsChecked = viewModel.Range == ExportDocumentRange.Custom;
    }

    // Guarda de nulidade em `RangeTextBox` pelo MESMO motivo documentado em ExportImageDialog: WPF dispara
    // `Checked` SINCRONAMENTE dentro de `InitializeComponent()`, antes do parser BAML alcançar o nó do
    // TextBox (que fica mais abaixo no mesmo arquivo) -- sem a guarda, tocar o campo lançaria NRE na
    // construção. Em uso normal (clique real) o campo já está conectado.
    private void AllPagesRadio_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.Range = ExportDocumentRange.AllPages;
        ViewModel.RangeError = null;
    }

    private void CustomRangeRadio_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.Range = ExportDocumentRange.Custom;
        ViewModel.RangeError = null;
    }

    /// `SaveFileDialog` filtrado pelo Kind (mesmo padrão de `ExportImageDialog.PickDestination_Click`);
    /// nome sugerido = nome do documento + extensão (.docx/.xlsx). O SaveFileDialog já confirma
    /// sobrescrita nativamente.
    private void PickDestination_Click(object sender, RoutedEventArgs e)
    {
        bool isWord = ViewModel.Kind == ExportDocumentKind.Word;
        var dlg = new SaveFileDialog
        {
            Title = ViewModel.DialogTitle,
            Filter = isWord
                ? "Documento do Word (*.docx)|*.docx"
                : "Planilha do Excel (*.xlsx)|*.xlsx",
            DefaultExt = ViewModel.FileExtension,
            FileName = ViewModel.SuggestedFileName,
        };
        if (dlg.ShowDialog() != true) return;
        ViewModel.Destination = dlg.FileName;
        DestinationTextBox.Text = dlg.FileName;
    }

    /// Bloqueia fechar a janela (✕, Alt+F4, Esc no botão "Fechar") enquanto a exportação está EM VOO —
    /// mesmo contrato de `ExportImageDialog.Window_Closing`: força "Cancelar exportação" primeiro.
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (ViewModel.IsRunning) e.Cancel = true;
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
