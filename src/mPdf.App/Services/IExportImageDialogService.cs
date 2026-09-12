using mPdf.App.ViewModels;

namespace mPdf.App.Services;

/// Diálogo "Exportar página como imagem" (Task 4, Plano 7) — mesma disciplina de injeção de
/// <see cref="IBatchSignDialogService"/>: produção abre uma janela WPF real
/// (<see cref="Views.ExportImageDialog"/>), testes injetam um fake que NUNCA abre janela nenhuma (evita
/// travar a suíte esperando um clique que nunca vem).
///
/// MESMA forma de <see cref="IBatchSignDialogService"/> (não a de <see cref="ISplitDialogService"/>):
/// hospeda um <see cref="ExportImageViewModel"/> JÁ CONSTRUÍDO pelo chamador
/// (<c>DocumentViewModel.ExportImage</c>) — formato/alcance/resolução/destino + progresso/cancelamento
/// já vivem no VM, testável direto sem abrir janela nenhuma (ver ExportImageViewModelTests/
/// ExportImageIntegrationTests). Esta seam existe só pra cobrir "mostrar a janela" — a MESMA classe de
/// risco (headless test alcançando `Window.ShowDialog()` de verdade) que motiva as outras entradas de
/// `UiPrompts`.
public interface IExportImageDialogService
{
    void ShowExportImageDialog(ExportImageViewModel viewModel);
}

/// Implementação de produção — abre <see cref="Views.ExportImageDialog"/> como filha da janela principal.
/// Mesmo precedente de <see cref="BatchSignDialogService"/>: nenhuma mediação além de mostrar a janela.
public sealed class ExportImageDialogService : IExportImageDialogService
{
    public void ShowExportImageDialog(ExportImageViewModel viewModel)
    {
        var dialog = new Views.ExportImageDialog(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
    }
}
