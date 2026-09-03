using System.Windows;

namespace mPdf.App.Services;

/// Decisão do usuário ao fechar um documento SUJO (Task 3, Plano 3a).
public enum CloseConfirmation
{
    Save,
    Discard,
    Cancel,
}

/// Serviço injetável (mesmo padrão de `IFileDialogService`) para o prompt de "salvar antes de
/// fechar?" — abstrai o `MessageBox.Show` real para permitir testar os 3 caminhos (Salvar/Descartar/
/// Cancelar) headless, sem travar a sessão de teste esperando um clique que nunca vem.
public interface IConfirmCloseService
{
    CloseConfirmation Confirm(string documentTitle);
}

/// Implementação de produção — `MessageBox.Show` com 3 botões (Sim/Não/Cancelar), pt-BR. Mesmo
/// precedente já aberto por `MainViewModel.DefaultNotifyError` (MessageBox direto, sem mediação).
public sealed class MessageBoxConfirmCloseService : IConfirmCloseService
{
    public CloseConfirmation Confirm(string documentTitle)
    {
        var result = MessageBox.Show(
            $"\"{documentTitle}\" tem alterações não salvas.\nDeseja salvar antes de fechar?",
            "mPDF", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => CloseConfirmation.Save,
            MessageBoxResult.No => CloseConfirmation.Discard,
            _ => CloseConfirmation.Cancel,
        };
    }
}
