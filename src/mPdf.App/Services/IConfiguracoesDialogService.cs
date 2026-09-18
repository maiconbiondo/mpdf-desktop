using mPdf.App.ViewModels;

namespace mPdf.App.Services;

/// Diálogo "Configurações" (Task 2, Plano 17) — mesma disciplina de injeção de
/// <see cref="ISobreDialogService"/>: produção abre uma janela WPF real (<see cref="Views.ConfiguracoesDialog"/>),
/// testes injetam um fake que NUNCA abre janela nenhuma (evita travar a suíte esperando um clique que
/// nunca vem). Hospeda um <see cref="ConfiguracoesViewModel"/> JÁ CONSTRUÍDO pelo chamador
/// (<c>MainViewModel.Configuracoes</c>) — toda a lógica (tema/nitidez/verificar/baixar/instalar) já vive
/// no VM, testável direto sem abrir janela nenhuma (ver <c>ConfiguracoesViewModelTests</c>). Mesma
/// classe de risco (headless test alcançando <c>Window.ShowDialog()</c> de verdade) que motiva as
/// outras entradas de <see cref="UiPrompts"/>.
public interface IConfiguracoesDialogService
{
    void ShowConfiguracoesDialog(ConfiguracoesViewModel viewModel);
}

/// Implementação de produção — abre <see cref="Views.ConfiguracoesDialog"/> como filha da janela
/// principal. Mesmo precedente de <see cref="SobreDialogService"/>: nenhuma mediação além de mostrar a
/// janela.
public sealed class ConfiguracoesDialogService : IConfiguracoesDialogService
{
    public void ShowConfiguracoesDialog(ConfiguracoesViewModel viewModel)
    {
        var dialog = new Views.ConfiguracoesDialog(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
    }
}
