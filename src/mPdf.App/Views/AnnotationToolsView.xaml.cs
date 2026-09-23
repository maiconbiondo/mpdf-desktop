using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace mPdf.App.Views;

/// Conjunto ÚNICO das ferramentas de anotação (Plano 17, Task 3) — apresentado em DOIS layouts: horizontal
/// (a pílula flutuante do centro-inferior, comportamento de sempre) e vertical (a tira de ícones no rail de
/// 58px, opção "Barra de anotação na barra lateral"). NÃO duplica nem a lógica nem os comandos: todo botão
/// liga aos MESMOS `ICommand` do `MainViewModel`/`SelectedDocument` (ApplyMarkup×3, sticky/freetext/ink/
/// rectangle/line/arrow, cores, galeria de carimbos, imagem) — só o `Orientation` muda a disposição. O
/// `DataContext` é herdado do host (a MainViewModel), igual à pílula original; nenhuma superfície de render/
/// overlay/seleção é tocada (fronteira SAGRADA — isto é CHROME).
public partial class AnnotationToolsView : UserControl
{
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(AnnotationToolsView),
            new PropertyMetadata(Orientation.Horizontal));

    /// Horizontal = pílula flutuante; Vertical = tira no rail. Governa a orientação do painel-raiz, do
    /// sub-painel de swatches e (via DataTrigger no XAML) das divisórias.
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public AnnotationToolsView() => InitializeComponent();

    /// Revisão 2 (compacta cores da barra): os 2 popups de swatches (Contorno/Preench.) fecham sozinhos
    /// ao clicar fora (StaysOpen="False", XAML) mas NÃO ao escolher uma cor — um clique num swatch é
    /// "dentro" do Popup, então o fechamento automático não dispara. Este handler é ligado (Click, ALÉM
    /// do Command que já aplica a cor de sempre) em CADA botão-swatch dos 2 popups: sobe a árvore lógica/
    /// visual a partir do botão clicado até achar o Popup que o hospeda (Popup.Child é pai LÓGICO do seu
    /// conteúdo, então LogicalTreeHelper.GetParent chega lá; o fallback VisualTreeHelper cobre qualquer
    /// nó sem pai lógico no caminho) e fecha (IsOpen=false) — e também desmarca o ToggleButton dono
    /// (Popup.PlacementTarget), já que IsOpen é ligado a IsChecked só numa direção (ToggleButton -> Popup,
    /// ver XAML) e não o contrário: sem isso o botão ficaria "preso" aceso depois de fechar o popup.
    private void SwatchPicked(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject node) return;

        DependencyObject? current = node;
        while (current is not null && current is not Popup)
            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);

        if (current is Popup popup)
        {
            popup.IsOpen = false;
            if (popup.PlacementTarget is ToggleButton toggle) toggle.IsChecked = false;
        }
    }
}
