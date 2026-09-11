using System.Windows;

namespace mPdf.App.Views;

/// Janelinha pt-BR de texto livre (Task 7, Plano 3a) — usada tanto pra CRIAR (Content vazio) quanto
/// EDITAR (Content pré-preenchido) uma nota adesiva/caixa de texto. Sem mediação de VM: o serviço
/// (`Services.AnnotationTextDialogService`) lê `ResultText` direto depois de `ShowDialog()`, mesmo
/// precedente de `MessageBox.Show` usado no resto do app.
public partial class AnnotationTextDialog : Window
{
    /// Preenchido só quando o usuário confirma (`DialogResult = true`); permanece `null` se cancelado
    /// (Esc, "Cancelar" ou fechar a janela pelo X — `IsCancel="True"` no botão Cancelar cobre o Esc;
    /// fechar pelo X deixa `DialogResult` null, que o chamador trata como "false" via `== true`).
    public string? ResultText { get; private set; }

    public AnnotationTextDialog(string title, string? initialText)
    {
        InitializeComponent();
        Title = title;
        ContentTextBox.Text = initialText ?? string.Empty;
        Loaded += (_, _) => { ContentTextBox.Focus(); ContentTextBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultText = ContentTextBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ResultText = null;
        DialogResult = false;
    }
}
