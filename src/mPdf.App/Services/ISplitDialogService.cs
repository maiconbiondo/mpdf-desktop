namespace mPdf.App.Services;

/// Diálogo "Dividir documento" (Task 4, Plano 3b) — coleta a string de intervalos de página pt-BR (ex.:
/// "1-5, 8, 10-12", ver `PageRangeParser`) e a pasta de destino dos arquivos gerados. Mesmo padrão de
/// injeção de `IMergeDialogService`/`IConfirmCloseService`: produção abre uma janela WPF real
/// (`Views.SplitDialog`), testes injetam um fake com valores FIXOS, sem travar a sessão de teste.
public interface ISplitDialogService
{
    /// Devolve (texto de intervalos cru — ainda não parseado, `PageRangeParser.Parse` faz isso no VM —,
    /// pasta de destino escolhida), ou `null` se o usuário cancelou.
    (string Ranges, string DestinationFolder)? PickSplitOptions();
}

/// Implementação de produção — abre `Views.SplitDialog` como filha da janela principal.
public sealed class SplitDialogService : ISplitDialogService
{
    public (string Ranges, string DestinationFolder)? PickSplitOptions()
    {
        var dialog = new Views.SplitDialog
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        if (dialog.ShowDialog() != true) return null;
        return (dialog.RangesText, dialog.DestinationFolder!);
    }
}
