namespace mPdf.Documents;

/// Fila SERIAL de descarte nativo do PDFium (revisão pós-Task 6 do Plano 2b — motivo completo no
/// comentário de `DocumentViewModel.Dispose`, em mPdf.App, resumo aqui: NÃO é sobre exclusão mútua
/// entre disposes — isso já estava garantido por `PdfRenderLock.Gate` + o lock interno da própria
/// Docnet.Core. É sobre a INVARIANTE do processo: no máximo 1 teardown nativo em voo a qualquer
/// momento, mesmo se várias abas fecharem em rajada (fechar a janela com N abas abertas, por exemplo).
///
/// MOVIDA de `mPdf.App.Services` para cá na Task 3 do Plano 3a: `DocumentSession.Apply` (troca de
/// renderer numa sessão já aberta, sem fechar a aba) também precisa descartar o renderer ANTIGO de
/// forma serial — mas `DocumentSession` mora em `mPdf.Documents`, que `mPdf.App` REFERENCIA (não o
/// contrário); a classe não podia continuar em `mPdf.App` sem criar uma referência circular. Como
/// `PendingDisposals` sempre foi puro TPL (sem WPF/Docnet), descer pra `mPdf.Documents` não muda
/// nenhum comportamento — só corrige a camada onde ela deveria ter morado desde o início (é sobre o
/// ciclo de vida de recursos nativos do documento, não sobre UI). `mPdf.App` continua usando a MESMA
/// classe (agora via `using mPdf.Documents;`, já presente na maioria dos consumidores).
///
/// Implementação: cada `Enqueue(Action)` vira uma CONTINUAÇÃO (`Task.ContinueWith`) da última ação
/// enfileirada, nunca uma `Task` independente — o TPL garante que a continuação N+1 só COMEÇA depois
/// que a N termina (com qualquer status: sucesso, falha ou cancelamento — sem opções especiais,
/// `ContinueWith` roda incondicionalmente), então no máximo 1 `work()` executa por vez no processo
/// inteiro, não importa quantas threads chamem `Enqueue` ao mesmo tempo (a leitura+escrita de `_tail`
/// é protegida por `_sync`, então o encadeamento em si nunca corrompe).
///
/// `WaitAll` espera a ÚLTIMA continuação enfileirada até o momento da chamada; se ela tiver lançado,
/// `Task.Wait` relança como `AggregateException` — mesmo contrato observável de antes (baseado em
/// `Task.WaitAll`), então os chamadores existentes (`MainWindow.OnClosed`,
/// `ViewerIntegrationTests`) continuam funcionando sem alteração.
public static class PendingDisposals
{
    private static Task _tail = Task.CompletedTask;
    private static readonly object _sync = new();

    public static void Enqueue(Action work)
    {
        lock (_sync)
        {
            _tail = _tail.ContinueWith(_ => work(), TaskScheduler.Default);
        }
    }

    public static bool WaitAll(TimeSpan timeout)
    {
        Task tail;
        lock (_sync) { tail = _tail; }
        return tail.Wait(timeout);
    }
}
