using System.Windows;

namespace mPdf.App.Services;

/// Serviço injetável (mesmo padrão de `IConfirmCloseService`, Task 3 do Plano 3a) para o prompt de
/// confirmação "achatar formulário?" (Task 3, Plano 3c) — abstrai o `MessageBox.Show` real para permitir
/// testar os 2 caminhos (confirmar/cancelar) headless, sem travar a sessão de teste esperando um clique
/// que nunca vem. DIFERENTE de `IConfirmCloseService` de propósito: achatar é uma decisão binária
/// (confirmar/cancelar), não as 3 saídas de "salvar antes de fechar?" — um `bool` já cobre o contrato
/// inteiro, sem precisar de um enum dedicado.
public interface IConfirmFlattenService
{
    bool Confirm(string message);
}

/// Implementação de produção — `MessageBox.Show` com 2 botões (Sim/Não), pt-BR.
public sealed class MessageBoxConfirmFlattenService : IConfirmFlattenService
{
    public bool Confirm(string message) =>
        MessageBox.Show(message, "mPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
