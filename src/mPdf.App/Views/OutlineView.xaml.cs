using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using mPdf.Editing;

namespace mPdf.App.Views;

/// Aba "Sumário" (Task 5, Plano 3b) — exemplar: ThumbnailsPanel (evento cru da View convertido em
/// comando do VM). DataContext esperado: um DocumentViewModel (ver MainWindow.xaml — TabControl
/// dentro do painel esquerdo colapsável, mesmo DataContext que ThumbnailsPanel já usa).
///
/// I2 (revisão final pré-merge, Plano 3b): 2 handlers convergem no MESMO ponto de entrada
/// (`Activate`) — `Tree_SelectedItemChanged` (navegação via SELEÇÃO: teclado/setas, ou o 1º clique
/// num nó ainda não selecionado) e `Node_MouseLeftButtonUp` (navegação via CLIQUE, em QUALQUER nó,
/// selecionado ou não — cobre o caso que `SelectedItemChanged` não alcança: reclicar um nó JÁ
/// selecionado, que WPF trata como "sem mudança" e NUNCA dispara aquele evento). O codebase já
/// documenta esta mesma armadilha em `ScrollToPageRequested` ("SEMPRE dispara, mesmo se for a mesma
/// página") — aqui o problema é o INVERSO: o evento de origem (`SelectedItemChanged`) é quem PARA de
/// disparar, não o consumidor. Os 2 handlers PODEM disparar os 2 pro MESMO clique (clicar um nó NOVO
/// muda a seleção E gera o MouseUp) — inofensivo por construção: `Activate` sempre delega pro MESMO
/// comando do VM (`NavigateToOutlineNodeCommand`/`ScrollToPageRequested`), que é IDEMPOTENTE por
/// natureza (rolar 2x pro mesmo índice não produz nenhum glitch visível, só um scroll redundante).
public partial class OutlineView : UserControl
{
    public OutlineView()
    {
        InitializeComponent();
    }

    // TreeView.SelectedItem não é uma DependencyProperty bindable em WPF de fábrica — capturado aqui
    // via SelectedItemChanged e repassado pro comando do VM, que já sabe fazer no-op pra nó sem página
    // (ver DocumentViewModel.NavigateToOutlineNode). Cobre navegação via TECLADO (setas) e o 1º clique
    // num nó ainda não selecionado — ver doc XML da classe pro caso que ISTO NÃO cobre.
    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineNode node) Activate(node);
    }

    // I2: MouseLeftButtonUp na TextBlock do item template (DataContext = o OutlineNode do nó clicado,
    // mesmo padrão de ThumbnailsPanel.Thumb_MouseLeftButtonUp) — dispara em TODO clique, reclique num
    // nó JÁ selecionado incluso, ao contrário de SelectedItemChanged acima.
    private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OutlineNode node) Activate(node);
    }

    private void Activate(OutlineNode node)
    {
        if (DataContext is not ViewModels.DocumentViewModel doc) return;
        if (doc.NavigateToOutlineNodeCommand.CanExecute(node)) doc.NavigateToOutlineNodeCommand.Execute(node);
    }
}
