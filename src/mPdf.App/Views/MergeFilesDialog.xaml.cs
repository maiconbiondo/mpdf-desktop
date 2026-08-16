using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace mPdf.App.Views;

/// Janela pt-BR de "Juntar documentos" (Task 4, Plano 3b) — lista ORDENÁVEL de arquivos com
/// Adicionar/Remover/mover ↑↓, mesmo precedente de `AnnotationTextDialog` (sem mediação de VM: o
/// serviço, `Services.MergeDialogService`, lê `OrderedPaths` direto depois de `ShowDialog()`).
public partial class MergeFilesDialog : Window
{
    /// Coleção observável ligada ao `ListBox` da view (`{Binding Files}`) — `DataContext = this` no
    /// construtor, mesmo padrão simples de code-behind-como-VM já usado nos diálogos deste app.
    public ObservableCollection<string> Files { get; } = [];

    /// Preenchido só quando o usuário confirma com pelo menos 1 arquivo (`DialogResult = true`);
    /// permanece `null` se cancelado — mesmo contrato de `AnnotationTextDialog.ResultText`.
    public IReadOnlyList<string>? OrderedPaths { get; private set; }

    public MergeFilesDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Adicionar arquivos",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var path in dlg.FileNames) Files.Add(path);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is string path) Files.Remove(path);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int i = FilesList.SelectedIndex;
        if (i <= 0) return;
        (Files[i - 1], Files[i]) = (Files[i], Files[i - 1]);
        FilesList.SelectedIndex = i - 1;
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int i = FilesList.SelectedIndex;
        if (i < 0 || i >= Files.Count - 1) return;
        (Files[i + 1], Files[i]) = (Files[i], Files[i + 1]);
        FilesList.SelectedIndex = i + 1;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Files.Count == 0) return; // nada a juntar — mantém o diálogo aberto (botão não deveria nem estar habilitado, ver brief)
        OrderedPaths = Files.ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        OrderedPaths = null;
        DialogResult = false;
    }
}
