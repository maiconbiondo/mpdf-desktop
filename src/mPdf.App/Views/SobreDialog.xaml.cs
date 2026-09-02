using System.Windows;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Janela "Sobre" (Task 2, Plano 11) — mesmo padrão de `BatchSignDialog`: `DataContext` é um
/// `SobreViewModel` REAL, já construído pelo chamador (`Services.SobreDialogService`) — toda a lógica
/// vive lá, testável sem esta janela (ver `SobreViewModelTests`). Este code-behind não tem NENHUMA
/// lógica de estado — só hospeda o VM (bindings puros no XAML cobrem toda a troca de painel por
/// `SobreEstado`).
public partial class SobreDialog : Window
{
    public SobreDialog(SobreViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// Plano 14 (Task 4): janela sem moldura (superfície escura rounded) — arrastar pelo cabeçalho
    /// substitui o arraste da title bar nativa (só UX de janela, nenhuma lógica tocada).
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
