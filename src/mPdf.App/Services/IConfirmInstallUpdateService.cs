using System.Windows;

namespace mPdf.App.Services;

/// Serviço injetável (Task 2, Plano 11 — mesmo padrão de <see cref="IConfirmFlattenService"/>/
/// <see cref="IConfirmOrganizerScaleService"/>) para o prompt "Fechar o mPDF e instalar a atualização
/// agora?" — abstrai o `MessageBox.Show` real pra permitir testar os 2 caminhos (confirmar/cancelar)
/// headless, sem travar a sessão de teste esperando um clique que nunca vem. `bool` (não um enum
/// dedicado) — mesma razão de <see cref="IConfirmFlattenService"/>: decisão binária.
public interface IConfirmInstallUpdateService
{
    bool Confirm(string message);
}

/// Implementação de produção — `MessageBox.Show` com 2 botões (Sim/Não), pt-BR.
public sealed class MessageBoxConfirmInstallUpdateService : IConfirmInstallUpdateService
{
    public bool Confirm(string message) =>
        MessageBox.Show(message, "mPDF", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
