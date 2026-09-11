using System.Windows.Controls;

namespace mPdf.App.Views;

public partial class SearchBar : UserControl
{
    public SearchBar() => InitializeComponent();

    /// Chamado pelo PdfViewerControl (Ctrl+F) depois de abrir a barra (IsOpen=true) — foca e
    /// seleciona o texto do campo de busca pra digitar por cima de uma query anterior.
    public void FocusQueryBox()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }
}
