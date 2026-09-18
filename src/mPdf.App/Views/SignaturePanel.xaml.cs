using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Aba "Assinaturas" (Task 4, Plano 4) — exemplar: OutlineView/FormPanel (VM-first, code-behind delega
/// pro VM). DataContext esperado: um DocumentViewModel (ver MainWindow.xaml — mesmo TabControl que já
/// hospeda ThumbnailsPanel/OutlineView/FormPanel).
public partial class SignaturePanel : UserControl
{
    public SignaturePanel()
    {
        InitializeComponent();
    }

    /// Clique no signatário (brief: "clique na assinatura com carimbo -> ScrollToPage + destaque") —
    /// mesmo padrão EXATO de OutlineView.Node_MouseLeftButtonUp/FormPanel.FieldBlock_MouseLeftButtonUp
    /// (evento cru da View convertido em SelectSignatureCommand). Disparado em QUALQUER linha (com ou
    /// sem carimbo) — o comando do VM já decide o que fazer com cada caso (ver
    /// DocumentViewModel.SelectSignature).
    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SignatureRowViewModel row }) return;
        if (DataContext is not DocumentViewModel doc) return;
        if (doc.SelectSignatureCommand.CanExecute(row)) doc.SelectSignatureCommand.Execute(row);
    }
}
