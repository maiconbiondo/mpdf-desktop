using System.Windows;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Janela "Configurações" (Task 2, Plano 17) — mesmo padrão de `SobreDialog`: `DataContext` é um
/// `ConfiguracoesViewModel` REAL, já construído pelo chamador (`Services.ConfiguracoesDialogService`) —
/// toda a lógica vive lá, testável sem esta janela (ver `ConfiguracoesViewModelTests`). Este code-behind
/// não tem NENHUMA lógica de estado — só hospeda o VM (bindings puros no XAML cobrem toda a troca de
/// painel por `ConfiguracoesEstado`).
public partial class ConfiguracoesDialog : Window
{
    public ConfiguracoesDialog(ConfiguracoesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// Janela sem moldura (superfície escura rounded) — arrastar pelo cabeçalho substitui o arraste da
    /// title bar nativa (só UX de janela, nenhuma lógica tocada). Mesmo padrão de `SobreDialog`.
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
