using System.Windows;

namespace mPdf.App.Services;

/// Serviço injetável (Task 1, Plano 5 — mesmo padrão de <see cref="IConfirmFlattenService"/>) para o
/// prompt de confirmação "documento grande, o organizador pode demorar" — abstrai o `MessageBox.Show`
/// real pra permitir testar os 2 caminhos (continuar/cancelar) headless. Ledger: 14,7 s pra encher a
/// grade de miniaturas a 510 páginas — abrir o organizador num documento MUITO grande sem aviso nenhum
/// parece um travamento do app; o aviso dá ao usuário a chance de desistir antes de esperar. `bool` (não
/// um enum dedicado) — mesma razão de `IConfirmFlattenService`: é uma decisão binária, continuar ou não.
public interface IConfirmOrganizerScaleService
{
    bool Confirm(string message);
}

/// Implementação de produção — `MessageBox.Show` com 2 botões (Sim/Não), pt-BR.
public sealed class MessageBoxConfirmOrganizerScaleService : IConfirmOrganizerScaleService
{
    public bool Confirm(string message) =>
        MessageBox.Show(message, "mPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
