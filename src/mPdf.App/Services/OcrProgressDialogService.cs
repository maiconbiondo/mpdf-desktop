using System.Threading;
using System.Windows;

namespace mPdf.App.Services;

/// Implementação de PRODUÇÃO do <see cref="IOcrProgressService"/> (Task 4, Plano 15) — abre a
/// <see cref="Views.OcrProgressDialog"/> (janela escura MODELESS) como filha da janela principal e a
/// opera. `Start` roda na thread de UI (o comando de OCR chama antes do 1º `await`): cria a janela, um
/// <see cref="CancellationTokenSource"/> ligado ao botão Cancelar, e um <see cref="Progress{T}"/> criado
/// AQUI (captura o `SynchronizationContext` da UI) — assim os reports vindos do `Task.Run` do OCR voltam
/// marshalados pra thread de UI antes de tocar a janela. `Dispose` da sessão fecha a janela e libera o CTS.
///
/// Seam com hang-guard (mesma classe de risco dos outros diálogos): roteado por
/// <see cref="UiPrompts.CreateOcrProgress"/> — um teste headless que alcança este default de produção
/// falha NOMEADO (via `UiPromptsTestGuard`) em vez de tentar abrir uma `Window` fora de uma thread STA.
public sealed class OcrProgressDialogService : IOcrProgressService
{
    public IOcrProgressSession Start()
    {
        var dialog = new Views.OcrProgressDialog
        {
            Owner = Application.Current?.MainWindow,
        };
        var cts = new CancellationTokenSource();
        dialog.Cancelamento += () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };
        // Progress criado na thread de UI -> callbacks marshalados pra cá a partir do Task.Run do OCR.
        var progress = new Progress<OcrProgress>(dialog.Atualizar);
        dialog.Show();
        return new Session(dialog, cts, progress);
    }

    private sealed class Session(Views.OcrProgressDialog dialog, CancellationTokenSource cts, IProgress<OcrProgress> progress)
        : IOcrProgressSession
    {
        public CancellationToken Token => cts.Token;
        public IProgress<OcrProgress> Progress => progress;

        public void Dispose()
        {
            dialog.Close();
            cts.Dispose();
        }
    }
}
