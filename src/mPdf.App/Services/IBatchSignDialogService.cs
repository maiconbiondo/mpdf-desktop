using mPdf.App.ViewModels;

namespace mPdf.App.Services;

/// Diálogo "Assinar em lote" (Task 5, Plano 4) — mesma disciplina de injeção de
/// <see cref="IMergeDialogService"/>/<see cref="ISignDialogService"/>: produção abre uma janela WPF real
/// (<see cref="Views.BatchSignDialog"/>), testes injetam um fake que NUNCA abre janela nenhuma (evita
/// travar a suíte esperando um clique que nunca vem).
///
/// DIFERENTE de <see cref="ISignDialogService"/>/<see cref="IMergeDialogService"/> (que devolvem um
/// resultado simples e fecham): este diálogo hospeda um <see cref="BatchSignViewModel"/> JÁ CONSTRUÍDO
/// pelo chamador (<c>MainViewModel.BatchSign</c>) — toda a lógica (adicionar/remover arquivo, assinar em
/// background, progresso, cancelar, resultados) já vive no VM, testável direto sem abrir janela nenhuma
/// (ver BatchSignViewModelTests). Esta seam existe só pra cobrir "mostrar a janela" — a MESMA classe de
/// risco (headless test alcançando `Window.ShowDialog()` de verdade) que motiva as outras 8 entradas de
/// `UiPrompts`.
public interface IBatchSignDialogService
{
    void ShowBatchSignDialog(BatchSignViewModel viewModel);
}

/// Implementação de produção — abre <see cref="Views.BatchSignDialog"/> como filha da janela principal.
/// Mesmo precedente de <see cref="MergeDialogService"/>/<see cref="SignDialogService"/>: nenhuma
/// mediação além de mostrar a janela.
public sealed class BatchSignDialogService : IBatchSignDialogService
{
    public void ShowBatchSignDialog(BatchSignViewModel viewModel)
    {
        var dialog = new Views.BatchSignDialog(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
    }
}
