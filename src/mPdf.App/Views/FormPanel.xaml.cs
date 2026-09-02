using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Aba "Campos" (Task 2, Plano 3c) — exemplar: OutlineView (VM-first, code-behind delega pro VM).
/// DataContext esperado: um DocumentViewModel (ver MainWindow.xaml — mesmo TabControl que já hospeda
/// ThumbnailsPanel/OutlineView).
///
/// Radio (`FormFieldType.Radio`): cada opção é um `RadioButton` dentro de um `ItemsControl` ANINHADO
/// (`ItemsSource="{Binding Options}"`, DataContext de cada item = a OPÇÃO em si, uma `string`) — perder
/// a referência ao `FormFieldViewModel` DONO do grupo é o problema central, resolvido por 2 handlers de
/// EVENTO (não bindings/conversores — `MultiBinding.ConvertBack` não devolve a STRING da opção marcada,
/// só o booleano `IsChecked`, então não daria pra escrever de volta em `EditedValue` só com XAML):
/// `RadioOption_Loaded` inicializa `IsChecked` (comparando a opção com `EditedValue` ATUAL) e
/// `RadioOption_Checked` escreve de volta quando o usuário marca uma opção. Os 2 sobem a árvore visual
/// (`FindOwningField`) até achar o `FormFieldViewModel`.
public partial class FormPanel : UserControl
{
    public FormPanel()
    {
        InitializeComponent();
    }

    /// Clique no NOME do campo -> seleciona esse campo (brief: "campo selecionado -> ScrollToPage +
    /// destaque"). Mesmo padrão EXATO de `OutlineView.Node_MouseLeftButtonUp` (handler na PRÓPRIA
    /// TextBlock clicável, não num ancestral esperando bubbling — mais robusto/direto): evento cru da
    /// View convertido em `SelectFormFieldCommand`.
    private void FieldBlock_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FormFieldViewModel field }) return;
        if (DataContext is not DocumentViewModel doc) return;
        if (doc.SelectFormFieldCommand.CanExecute(field)) doc.SelectFormFieldCommand.Execute(field);
    }

    /// Radio: inicializa o estado visual de CADA opção quando o container é gerado (container é
    /// RECRIADO toda vez que `FormFieldEditors`/`Options` mudam — `ItemsControl` normal, sem
    /// virtualização — então `Loaded` dispara de novo com o `EditedValue` CORRENTE sempre que precisa).
    private void RadioOption_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: string option } radio) return;
        if (FindOwningField(radio) is not { } field) return;
        radio.IsChecked = option == field.EditedValue;
    }

    /// Radio: usuário marca uma opção -> escreve o export value de volta em `EditedValue` (nunca uma
    /// constante fixa — a OPÇÃO clicada em si, `DataContext` do próprio RadioButton).
    private void RadioOption_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: string option } radio) return;
        if (FindOwningField(radio) is not { } field) return;
        field.EditedValue = option;
    }

    /// Sobe a árvore VISUAL (não lógica — `Options` é uma lista de `string`, sem elo de volta pro
    /// `FormFieldViewModel` dono) até achar o primeiro ancestral cujo `DataContext` É o
    /// `FormFieldViewModel` (o container gerado pelo ItemTemplate EXTERNO, `FormFieldEditors`) —
    /// `RelativeSource AncestorType` não alcança através de 2 ItemsControls aninhados com o mesmo
    /// critério de busca simples, então a varredura manual é o jeito mais direto/robusto.
    private static FormFieldViewModel? FindOwningField(DependencyObject start)
    {
        for (DependencyObject? d = start; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: FormFieldViewModel vm }) return vm;
        return null;
    }
}
