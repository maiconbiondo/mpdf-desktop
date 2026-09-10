using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Painel de miniaturas (Task 6) — exemplar de todo o pipeline: PageViewModel/PdfViewerControl,
/// simplificado (sem seleção de texto, sem overlays de busca, sem zoom — escala fixa). DataContext
/// esperado: um DocumentViewModel (ver MainWindow.xaml, mesmo padrão do ContentTemplate do TabControl
/// que dá o DocumentViewModel a um PdfViewerControl).
public partial class ThumbnailsPanel : UserControl
{
    /// Disparado quando o usuário clica numa miniatura — carrega o ÍNDICE (0-based) da página.
    /// MainWindow assina isto e repassa pro PdfViewerControl.ScrollToPage da aba ativa (mesmo
    /// espírito de evento simples de DocumentViewModel.ScrollToPageRequested).
    public event Action<int>? ThumbnailClicked;

    public ThumbnailsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Revisão pós-Task 6 (achado real, observado): o painel começa OCULTO por padrão e
        // ScrollCurrentIntoView() é pulado enquanto oculto (guarda de IsVisible lá dentro) — então
        // trocar de página com o painel escondido NÃO atualizava a posição de rolagem do rail. Sem
        // isto, ligar o toggle numa página avançada (ex.: 15) mostrava as miniaturas 1-6 (posição
        // default do ListBox), não a atual. Precisa de um "catch-up" no momento em que o painel FICA
        // visível, não só quando CurrentPage muda enquanto ele já está visível.
        IsVisibleChanged += OnIsVisibleChanged;
    }

    // O DataContext deste controle é trocado quando a aba ativa muda (MainWindow rebind, não recria
    // o painel) — assina/desassina em vez de assumir uma instância nova por documento, mesmo cuidado
    // de PdfViewerControl.OnDataContextChanged.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DocumentViewModel oldDoc) oldDoc.PropertyChanged -= OnDocumentPropertyChanged;
        if (e.NewValue is DocumentViewModel newDoc) newDoc.PropertyChanged += OnDocumentPropertyChanged;
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.CurrentPage)) ScrollCurrentIntoView();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return; // só a transição false -> true precisa de catch-up
        // Adiado com prioridade Loaded: a Visibility acabou de mudar (Collapsed -> Visible), o layout
        // do painel/ListBox ainda não rodou neste frame — mesmo exemplar de adiamento pós-layout já
        // usado em PdfViewerControl.FocusSearchBar e na âncora de zoom (OnDocumentPropertyChanged de
        // lá). Sem adiar, ScrollIntoView rodaria contra um ListBox ainda sem containers realizados.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollCurrentIntoView);
    }

    // Mantém a miniatura da página ATUAL em vista na rolagem do painel — só quando o painel está
    // visível (guarda simples pedida no brief: evita brigar com o usuário rolando o painel quando ele
    // nem está sendo mostrado, e evita custo de layout à toa com o painel colapsado). Chamado tanto
    // quando CurrentPage muda com o painel já visível quanto (catch-up) quando o painel FICA visível.
    private void ScrollCurrentIntoView()
    {
        if (!IsVisible) return;
        if (DataContext is not DocumentViewModel doc) return;
        int idx = doc.CurrentPage - 1;
        if (idx >= 0 && idx < doc.Thumbnails.Count) ThumbList.ScrollIntoView(doc.Thumbnails[idx]);
    }

    private void Thumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThumbnailViewModel thumb)
            ThumbnailClicked?.Invoke(thumb.Index);
    }

    private void Thumb_Loaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThumbnailViewModel t) t.OnRealized();
    }

    private void Thumb_Unloaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThumbnailViewModel t) t.OnDerealized();
    }

    // Recycling: Loaded/Unloaded podem AMBOS ser pulados quando um container reciclado troca de
    // DataContext sem sair/entrar na árvore visual — mesmo gap coberto em PdfViewerControl.Page_DataContextChanged.
    private void Thumb_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement { IsLoaded: true }) return;
        if (e.OldValue is ThumbnailViewModel oldT) oldT.OnDerealized();
        if (e.NewValue is ThumbnailViewModel newT) newT.OnRealized();
    }
}
