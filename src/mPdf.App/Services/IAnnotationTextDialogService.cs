namespace mPdf.App.Services;

/// Coleta o texto de uma nota adesiva/caixa de texto — criação OU edição (Task 7, Plano 3a). Mesmo
/// padrão de injeção de `IConfirmCloseService`: produção abre uma janelinha real (`Views.
/// AnnotationTextDialog`), testes injetam um fake que devolve um texto fixo (ou `null`), sem travar a
/// sessão de teste esperando uma janela real.
public interface IAnnotationTextDialogService
{
    /// `initialText` pré-preenche o campo (edição de uma anotação existente); `null` = campo vazio
    /// (criação nova). Devolve o texto digitado, ou `null` se o usuário cancelou (Esc/Cancelar/fechar a
    /// janela) — nesse caso o chamador NÃO deve criar/editar a anotação (nem desativar a ferramenta de
    /// colocação — ver `DocumentViewModel.PlaceAnnotationAtAsync`).
    string? PromptForText(string title, string? initialText = null);
}

/// Implementação de produção — abre `Views.AnnotationTextDialog` (janela modal simples, pt-BR) como
/// filha da janela principal. Mesmo precedente de `MessageBoxConfirmCloseService`: nenhuma mediação
/// além de mostrar a janela e ler o resultado.
public sealed class AnnotationTextDialogService : IAnnotationTextDialogService
{
    public string? PromptForText(string title, string? initialText = null)
    {
        var dialog = new Views.AnnotationTextDialog(title, initialText)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.ResultText : null;
    }
}
