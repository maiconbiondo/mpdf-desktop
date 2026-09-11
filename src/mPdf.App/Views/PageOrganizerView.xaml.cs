using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Organizador de páginas (Task 3, Plano 3b) — exemplar: `ThumbnailsPanel` (Task 6, Plano 3a). Mesmo
/// trio realize/derealize (Loaded/Unloaded/DataContextChanged — recycling do ItemsControl pode pular os
/// dois primeiros ao reciclar um container, mesmo gap já coberto lá); clique/Ctrl+clique repassa direto
/// pro VM (`OrganizerViewModel.ToggleSelect`), sem estado próprio neste code-behind.
public partial class PageOrganizerView : UserControl
{
    public PageOrganizerView()
    {
        InitializeComponent();
    }

    // Plano 14 (Task 5): "Concluir" fecha o organizador — seta IsOrganizerOpen=false no MESMO
    // DocumentViewModel que o ToggleButton "Organizar" da command bar alterna (mesma propriedade,
    // mesmo efeito de fechar; nenhuma lógica nova — só o gatilho na UI). O setter dispara
    // OnIsOrganizerOpenChanged (descarta o renderer do organizador etc., inalterado).
    private void Concluir_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DocumentViewModel doc) doc.IsOrganizerOpen = false;
    }

    private void Page_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OrganizerPageViewModel page) return;
        if (DataContext is not DocumentViewModel doc || doc.Organizer is not { } organizer) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        organizer.ToggleSelect(page.Index, ctrl);
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OrganizerPageViewModel p) p.OnRealized();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OrganizerPageViewModel p) p.OnDerealized();
    }

    // Recycling: Loaded/Unloaded podem AMBOS ser pulados quando um container reciclado troca de
    // DataContext sem sair/entrar na árvore visual — mesmo gap coberto em
    // ThumbnailsPanel.Thumb_DataContextChanged/PdfViewerControl.Page_DataContextChanged.
    private void Page_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement { IsLoaded: true }) return;
        if (e.OldValue is OrganizerPageViewModel oldP) oldP.OnDerealized();
        if (e.NewValue is OrganizerPageViewModel newP) newP.OnRealized();
    }
}
