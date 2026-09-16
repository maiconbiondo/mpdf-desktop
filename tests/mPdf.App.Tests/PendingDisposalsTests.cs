using System.IO;
using System.Linq;
using mPdf.App.ViewModels;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

// Rider (revisão pós-Task 3, Plano 3a): a mecânica PURA de PendingDisposals (fila em si, sem nenhum
// tipo de mPdf.App) mudou pra tests/mPdf.Documents.Tests/PendingDisposalsTests.cs, junto da classe
// (que também já morava em mPdf.Documents desde a Task 3). ESTE teste ficou aqui de propósito: prova
// o cenário REAL que motivou o guard-rail (multi-aba fechada em rajada) descartando DocumentViewModel
// de verdade — um tipo de mPdf.App que mPdf.Documents.Tests não referencia (e não deveria, pra não
// inverter a direção de dependência do projeto).
public class PendingDisposalsTests
{
    [Fact] // Stress do cenário real que motivou I1 (revisão pós-Task 6, Plano 2b): multi-aba fechada em
    // rajada (MainWindow.OnClosed itera Documents; MainViewModel.CloseDocument por clique no ✕) — 4
    // documentos reais descartados em paralelo não deixam nada pendurado na fila.
    public void WaitAll_BurstDisposeOfMultipleDocuments_Completes()
    {
        var docs = Enumerable.Range(0, 4)
            .Select(_ => new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf"))))
            .ToArray();

        Parallel.ForEach(docs, d => d.Dispose());

        bool finished;
        try { finished = PendingDisposals.WaitAll(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { finished = true; }
        Assert.True(finished, "descarte de 4 documentos em rajada não drenou a tempo");
    }
}
