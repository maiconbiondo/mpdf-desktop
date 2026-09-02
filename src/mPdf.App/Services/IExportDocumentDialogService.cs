using mPdf.App.ViewModels;

namespace mPdf.App.Services;

/// Diálogo "Exportar como Word/Excel" (Task 3, Plano 16) — mesma disciplina de injeção de
/// <see cref="IExportImageDialogService"/>: produção abre uma janela WPF real
/// (<see cref="Views.ExportDocumentDialog"/>), testes injetam um fake que NUNCA abre janela nenhuma
/// (evita travar a suíte esperando um clique que nunca vem).
///
/// MESMA forma de <see cref="IExportImageDialogService"/>: hospeda um <see cref="ExportDocumentViewModel"/>
/// JÁ CONSTRUÍDO pelo chamador (<c>DocumentViewModel.ExportDocumentCoreAsync</c>) — alcance/destino +
/// progresso/cancelamento já vivem no VM, testável direto sem abrir janela nenhuma. Esta seam existe só
/// pra cobrir "mostrar a janela" — a MESMA classe de risco (headless test alcançando `Window.ShowDialog()`
/// de verdade) que motiva as outras entradas de <see cref="UiPrompts"/>.
public interface IExportDocumentDialogService
{
    void ShowExportDocumentDialog(ExportDocumentViewModel viewModel);
}

/// Implementação de produção — abre <see cref="Views.ExportDocumentDialog"/> como filha da janela
/// principal. Mesmo precedente de <see cref="ExportImageDialogService"/>: nenhuma mediação além de
/// mostrar a janela.
public sealed class ExportDocumentDialogService : IExportDocumentDialogService
{
    public void ShowExportDocumentDialog(ExportDocumentViewModel viewModel)
    {
        var dialog = new Views.ExportDocumentDialog(viewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
    }
}
