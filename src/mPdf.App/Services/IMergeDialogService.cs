namespace mPdf.App.Services;

/// Diálogo "Juntar documentos" (Task 4, Plano 3b) — coleta uma lista ORDENADA de caminhos de PDF a
/// concatenar. Mesmo padrão de injeção de `IConfirmCloseService`/`IAnnotationTextDialogService`:
/// produção abre uma janela WPF real (`Views.MergeFilesDialog`, com Adicionar/Remover/mover ↑↓ e
/// OK/Cancelar), testes injetam um fake que devolve uma lista FIXA (ou `null`), sem travar a sessão de
/// teste esperando uma janela real.
public interface IMergeDialogService
{
    /// Devolve os caminhos NA ORDEM escolhida pelo usuário (a ordem em que os documentos serão
    /// concatenados por `MergeDocuments`), ou `null` se o usuário cancelou. Uma lista vazia confirmada
    /// com OK não deveria ser possível pela UI de produção (botão OK desabilitado sem itens) — o
    /// chamador (`MainViewModel.Merge`) trata `null` OU lista vazia da mesma forma (nada a fazer),
    /// defesa em profundidade contra uma implementação futura que não imponha essa regra.
    IReadOnlyList<string>? PickFilesToMerge();
}

/// Implementação de produção — abre `Views.MergeFilesDialog` como filha da janela principal. Mesmo
/// precedente de `AnnotationTextDialogService`: nenhuma mediação além de mostrar a janela e ler o
/// resultado.
public sealed class MergeDialogService : IMergeDialogService
{
    public IReadOnlyList<string>? PickFilesToMerge()
    {
        var dialog = new Views.MergeFilesDialog
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.OrderedPaths : null;
    }
}
