using mPdf.Documents;
using Xunit;

namespace mPdf.Documents.Tests;

// Rider (revisão pós-Task 3, Plano 3a): movida de mPdf.App.Tests pra cá — PendingDisposals em si já
// tinha se mudado pra mPdf.Documents (DocumentSession.Apply passou a depender dela; ver doc XML da
// classe), mas os testes ficaram pra trás na Task 3 original. A prova de "não deixa nada pendurado em
// rajada de disposes REAIS de DocumentViewModel" (WaitAll_BurstDisposeOfMultipleDocuments_Completes)
// FICOU em mPdf.App.Tests — esse teste específico depende de DocumentViewModel (tipo de mPdf.App, que
// este projeto de teste não referencia e não deveria) pra provar o cenário real que motivou o guard-rail
// (multi-aba fechada em rajada). Os testes "puros" (mecânica da própria fila, sem nenhum tipo do App)
// moram aqui, junto da classe que testam.
public class PendingDisposalsTests
{
    [Fact] // Enqueue + WaitAll drena o trabalho enfileirado antes de devolver o controle
    public void WaitAll_WaitsForQueuedActionToComplete()
    {
        using var completed = new ManualResetEventSlim();
        PendingDisposals.Enqueue(() =>
        {
            Thread.Sleep(200);
            completed.Set();
        });

        bool finished = PendingDisposals.WaitAll(TimeSpan.FromSeconds(5));

        Assert.True(finished, "WaitAll deveria retornar true dentro do timeout");
        Assert.True(completed.IsSet, "a ação enfileirada deveria ter concluído antes de WaitAll devolver o controle");
    }

    [Fact] // sem nada pendente, WaitAll não bloqueia (a "cauda" já é Task.CompletedTask)
    // Tolerância de 2s (não 1s): PendingDisposals é estático e compartilhado pelo processo de
    // teste inteiro — outras classes rodando em paralelo podem registrar descartes reais nesse
    // meio-tempo; a asserção real é "não espera o timeout de 5s".
    public void WaitAll_NothingPending_ReturnsImmediately()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        bool finished = PendingDisposals.WaitAll(TimeSpan.FromSeconds(5));

        Assert.True(finished);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"esperava retorno rápido (bem abaixo do timeout de 5s), levou {sw.Elapsed}");
    }

    [Fact] // GUARD-RAIL (revisão pós-Task 6 do Plano 2b, I1): prova de recusa observada, não inferida —
    // 8 ações enfileiradas de 8 THREADS PARALELAS DIFERENTES (não sequencialmente da mesma thread, que
    // não provaria nada sobre exclusão mútua real) nunca rodam concorrentemente. Se a fila NÃO fosse de
    // fato serial, `entered` chegaria a 2+ em algum momento — capturado via Interlocked, não inferido.
    public void Enqueue_NeverRunsMoreThanOneActionConcurrently()
    {
        int entered = 0, maxEntered = 0, completed = 0;
        var maxLock = new object();
        using var allDone = new CountdownEvent(8);

        void Work()
        {
            int now = Interlocked.Increment(ref entered);
            lock (maxLock) { if (now > maxEntered) maxEntered = now; }
            Thread.Sleep(15); // aumenta a chance de colisão se a exclusão mútua falhar
            Interlocked.Decrement(ref entered);
            Interlocked.Increment(ref completed);
            allDone.Signal();
        }

        var threads = Enumerable.Range(0, 8)
            .Select(_ => new Thread(() => PendingDisposals.Enqueue(Work)))
            .ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(); // garante que os 8 Enqueue já foram CHAMADOS (não que já rodaram)

        Assert.True(allDone.Wait(TimeSpan.FromSeconds(5)), "as 8 ações deveriam ter rodado dentro do timeout");
        Assert.True(PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, maxEntered); // nunca mais de 1 Work() em voo ao mesmo tempo
        Assert.Equal(8, completed);  // nenhuma ação foi perdida/pulada
    }
}
