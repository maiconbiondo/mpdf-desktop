namespace mPdf.App.Services;

/// Implementação de PRODUÇÃO do seam definido em `PdfAConversao.cs` (Task 6) — abre `Views.PdfADialog`
/// como filha da janela principal, mesmo precedente de `ExportImageDialogService`/`ExportDocumentDialogService`.
/// `null` = usuário cancelou (`DialogResult != true`); só lê `dialog.Escolha` no caminho de confirmação.
public sealed class PdfADialogService : IPdfADialogService
{
    public PdfAEscolha? Perguntar()
    {
        var dialog = new Views.PdfADialog
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.Escolha : null;
    }
}
