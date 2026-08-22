using mPdf.App.ViewModels;

namespace mPdf.App.Services;

/// Diálogo "Sobre" (Task 2, Plano 11) — mesma disciplina de injeção de <see cref="IBatchSignDialogService"/>:
/// produção abre uma janela WPF real (<see cref="Views.SobreDialog"/>), testes injetam um fake que NUNCA
/// abre janela nenhuma (evita travar a suíte esperando um clique que nunca vem). Hospeda um
/// <see cref="SobreViewModel"/> JÁ CONSTRUÍDO pelo chamador (<c>MainViewModel.Sobre</c>) — toda a lógica
/// (verificar/baixar/instalar) já vive no VM, testável direto sem abrir janela nenhuma (ver
/// <c>SobreViewModelTests</c>). Mesma classe de risco (headless test alcançando <c>Window.ShowDialog()</c>
/// de verdade) que motiva as outras entradas de <see cref="UiPrompts"/>.
public interface ISobreDialogService
{
    void ShowSobreDialog(SobreViewModel viewModel);
}

/// Implementação de produção — abre <see cref="Views.SobreDialog"/> como filha da janela principal.
/// Mesmo precedente de <see cref="BatchSignDialogService"/>: nenhuma mediação além de mostrar a janela.
public sealed class SobreDialogService : ISobreDialogService
{
    public void ShowSobreDialog(SobreViewModel viewModel)
    {
        var dialog = new Views.SobreDialog(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
    }
}
