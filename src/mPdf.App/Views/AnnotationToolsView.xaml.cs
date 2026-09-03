using System.Windows;
using System.Windows.Controls;

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
}
