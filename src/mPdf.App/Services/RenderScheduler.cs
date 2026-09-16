using mPdf.Rendering;

namespace mPdf.App.Services;

/// Um worker; fila com "último pedido por página vence"; tudo fora da thread de UI.
public sealed class RenderScheduler : IDisposable
{
    private readonly Func<int, double, RenderedPage> _render;
    private readonly Dictionary<int, (double Scale, Action<int, double, RenderedPage> Cb)> _queue = [];
    // Ordem de primeira solicitação (FIFO); Dictionary não garante ordem de iteração estável.
    // Entradas ficam obsoletas quando Cancel(page)/CancelPending removem do _queue mas não daqui —
    // o laço de dequeue do worker pula (descarta) qualquer índice que não esteja mais no _queue.
    private readonly Queue<int> _order = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private bool _disposed;

    public RenderScheduler(Func<int, double, RenderedPage> render)
    {
        _render = render;
        _worker = Task.Run(WorkerLoop);
    }

    // M2 (revisão pós-Task 6): o check de _disposed e o Release do _signal moraram FORA do lock —
    // corrida real com Dispose() (que despacha _cts.Dispose()/_signal.Dispose() de forma assíncrona
    // via ContinueWith, ver lá): Request() podia ler _disposed==false, ser suspensa pelo agendador
    // ANTES de chamar _signal.Release(), o worker então observar o cancelamento e o ContinueWith
    // descartar _signal — e só DEPOIS Request() retomar e chamar Release() num SemaphoreSlim já
    // descartado (ObjectDisposedException). Mover o check E o Release do signal pro MESMO lock que
    // Dispose() usa pra escrever _disposed fecha a corrida por completo (não só estreita a janela):
    // enquanto Request() segura o lock, Dispose() não consegue nem COMEÇAR a marcar _disposed=true,
    // então não há como o _signal ser descartado no meio de uma chamada de Request() em andamento.
    public void Request(int pageIndex, double scale, Action<int, double, RenderedPage> onRendered)
    {
        lock (_sync)
        {
            if (_disposed) return;
            // já enfileirada: só substitui escala/callback (última vence), sem mexer na posição FIFO
            if (!_queue.ContainsKey(pageIndex)) _order.Enqueue(pageIndex);
            _queue[pageIndex] = (scale, onRendered);
            _signal.Release();
        }
    }

    /// Remove o pedido pendente daquela página (ex.: container foi desrealizado). Se já estiver em
    /// render no worker, não interrompe — só evita que comece uma renderização não mais necessária.
    public void Cancel(int pageIndex)
    {
        lock (_sync) _queue.Remove(pageIndex);
    }

    public void CancelPending()
    {
        lock (_sync) { _queue.Clear(); _order.Clear(); }
    }

    private async Task WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_cts.Token); } catch (OperationCanceledException) { return; }
            int page = -1; double scale = 0; Action<int, double, RenderedPage>? cb = null;
            lock (_sync)
            {
                // pula índices obsoletos (cancelados) até achar um pedido ainda válido, na ordem FIFO
                while (_order.Count > 0)
                {
                    int candidate = _order.Dequeue();
                    if (_queue.Remove(candidate, out var entry))
                    {
                        page = candidate;
                        (scale, cb) = entry;
                        break;
                    }
                }
            }
            if (page == -1) continue; // nada válido para processar neste sinal
            try
            {
                var rendered = _render(page, scale);
                cb?.Invoke(page, scale, rendered);
            }
            catch
            {
                // página com erro de render não derruba o worker; fica como placeholder
            }
        }
    }

    public void Dispose()
    {
        // Escreve _disposed sob o MESMO lock que Request() usa pra ler/Release — ver comentário de
        // Request() acima: é essa simetria (leitura E escrita no mesmo lock) que fecha a corrida por
        // completo, não só o lado da leitura.
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cts.Cancel();
        _signal.Release();
        // Com a guarda em PdfDocumentRenderer, NÃO esperar o worker aqui é SEGURO: um worker abandonado
        // que só termine depois deste método retornar receberá ODE gerenciada (engolida pelo catch do
        // worker) ao tentar usar o renderer já descartado — nunca AV nativa. Bloquear na thread de UI
        // (mesmo com timeout curto) travava o fechamento da aba por segundos em digitalizações pesadas;
        // sem Wait algum, o fechamento é instantâneo.
        _worker.ContinueWith(_ => { _cts.Dispose(); _signal.Dispose(); }, TaskScheduler.Default);
    }
}
