using System.Windows;
using mPdf.App.Services;
using mPdf.Editing;

namespace mPdf.App.Views;

/// Janela "Salvar como PDF/A" (Task 7, SDD "Salvar como PDF/A") — sem VM próprio (mesma estrutura de
/// `DialogoImpressao`/`SignDialog`: o code-behind LÊ os controles direto, sem um `IValueConverter`/VM
/// dedicado pra um diálogo deste tamanho). `Escolha` é COMPUTADA a cada leitura (nunca capturada só no
/// clique de "Salvar PDF/A") — assim um teste pode ler o estado default (PDF/A-1b + pesquisável marcado)
/// sem precisar simular um clique, e `PdfADialogService` só lê `Escolha` DEPOIS de `ShowDialog() == true`.
public partial class PdfADialog : Window
{
    public PdfADialog()
    {
        InitializeComponent();
    }

    /// Nível + "manter pesquisável" lidos AGORA dos controles (RbA1b/RbA2b/RbA3b/ChkPesquisavel) — nunca
    /// um campo capturado no passado, pra nunca dessincronizar do que a tela mostra.
    public PdfAEscolha Escolha => new(NivelSelecionado(), ChkPesquisavel.IsChecked == true);

    private PdfANivel NivelSelecionado()
    {
        if (RbA2b.IsChecked == true) return PdfANivel.A2B;
        if (RbA3b.IsChecked == true) return PdfANivel.A3B;
        return PdfANivel.A1B; // default (RbA1b.IsChecked="True" no XAML)
    }

    private void Salvar_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// Janela sem moldura (superfície escura rounded) — arrastar pelo header substitui o arraste da
    /// title bar nativa (mesmo padrão de ExportImageDialog/DialogoImpressao).
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
