namespace mPdf.App.Services;

/// Resultado do diálogo da CAIXA DE TEXTO (FreeText) com formatação — texto + fonte/tamanho/estilo/cor.
/// `FontFamily` é "Helvetica"/"Times"/"Courier" (o motor `PdfEditor` normaliza; ver `NormalizeFamily`).
public sealed record AnnotationTextResult(
    string Text, double FontSizePt, bool Bold, bool Italic, string FontFamily, uint ColorArgb);

/// Coleta o texto de uma nota adesiva/caixa de texto — criação OU edição (Task 7, Plano 3a). Mesmo
/// padrão de injeção de `IConfirmCloseService`: produção abre uma janelinha real (`Views.
/// AnnotationTextDialog`), testes injetam um fake que devolve um texto fixo (ou `null`), sem travar a
/// sessão de teste esperando uma janela real.
public interface IAnnotationTextDialogService
{
    /// Nota adesiva (só texto — o ícone não usa fonte). `initialText` pré-preenche (edição); `null` =
    /// vazio (criação). Devolve o texto, ou `null` se cancelou.
    string? PromptForText(string title, string? initialText = null);

    /// Caixa de texto (FreeText) COM formatação — o diálogo mostra tamanho, negrito/itálico, cor e
    /// família de fonte. `initialText` pré-preenche o texto (edição). Devolve texto + formatação, ou
    /// `null` se cancelou. (A formatação inicial começa nos defaults; ler a formatação de uma caixa
    /// existente de volta fica pra depois.)
    AnnotationTextResult? PromptForTextFormatted(string title, string? initialText = null);
}

/// Implementação de produção — abre `Views.AnnotationTextDialog` (janela modal simples, pt-BR) como
/// filha da janela principal. Mesmo precedente de `MessageBoxConfirmCloseService`: nenhuma mediação
/// além de mostrar a janela e ler o resultado.
public sealed class AnnotationTextDialogService : IAnnotationTextDialogService
{
    public string? PromptForText(string title, string? initialText = null)
    {
        var dialog = new Views.AnnotationTextDialog(title, initialText, showFormatting: false)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.ResultText : null;
    }

    public AnnotationTextResult? PromptForTextFormatted(string title, string? initialText = null)
    {
        var dialog = new Views.AnnotationTextDialog(title, initialText, showFormatting: true)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.ResultFormat : null;
    }
}
