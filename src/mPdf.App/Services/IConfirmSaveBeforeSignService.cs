using System.Windows;

namespace mPdf.App.Services;

/// Prompt "salvar antes de assinar?" (Task 3, Plano 4) — mesma disciplina de injeção de
/// `IConfirmFlattenService` (Task 3, Plano 3c): produção abre um `MessageBox` real (Sim/Não), testes
/// injetam um fake que devolve confirmado/cancelado fixo, sem travar a sessão de teste esperando um
/// clique. Consultado ANTES do funil (`Session.TryBeginEdit`) em `DocumentViewModel.Sign` — mesmo
/// contrato de ordem já usado por `FlattenForm` (cancelar/recusar nunca arma o pino compartilhado).
/// Interface SEPARADA de `IConfirmFlattenService` (em vez de reusar aquela, apesar do formato idêntico
/// `bool Confirm(string message)`): são diálogos SEMANTICAMENTE distintos — misturar os dois atrás do
/// mesmo seam faria `UiPromptsTestGuard`/`UiPromptsCoverageTests` não conseguir distinguir qual prompt
/// um teste headless alcançou por engano.
public interface IConfirmSaveBeforeSignService
{
    bool Confirm(string message);
}

/// Implementação de produção — `MessageBox.Show` (Sim/Não), pt-BR. Mesmo precedente de
/// `MessageBoxConfirmFlattenService`.
public sealed class MessageBoxConfirmSaveBeforeSignService : IConfirmSaveBeforeSignService
{
    public bool Confirm(string message) =>
        MessageBox.Show(message, "mPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
