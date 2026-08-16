using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

file sealed class NullDialog : IFileDialogService
{
    public string? PickPdfToOpen() => null;
    public string? PickPdfToSaveAs(string currentPath) => null;
    public string? PickImageToImport() => null;
    public string? PickPdfToSave(string suggestedName) => null;
}

/// Task 1 (Plano 6): prova ponta a ponta do MECANISMO que App.xaml.cs usa em produção — SEM tocar
/// App.xaml.cs em si (OnStartup/StartupUri não são testáveis headless, mesma categoria dos handlers de
/// crash — ver doc XML de App.OnDispatcherUnhandledException). Aqui: 2 instâncias "secundárias" REAIS
/// (SingleInstanceService com o MESMO par de nomes da "primária") enviam caminhos pelo PIPE real; o
/// handler de PathReceived usa o MESMO padrão que App.xaml.cs usa em produção (Dispatcher.BeginInvoke
/// pra marshal da thread de background do listener pra thread de UI, só então MainViewModel.OpenPath)
/// — prova que os 2 caminhos viram 2 abas de verdade (dedupe de OpenPath incluso de graça).
/// STA (mesmo padrão de ViewerIntegrationTests): precisa de um Dispatcher REAL rodando pra marshal
/// funcionar como em produção, não só um MTA que executa tudo inline.
public class SingleInstanceIntegrationTests
{
    [Fact]
    public void SecondaryInstances_SendPathsThroughRealPipe_OpenTwoTabsInMainViewModel()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(30));
        Assert.True(joined, "thread STA não terminou dentro de 30s (BLOCKED: possível deadlock/hang)");

        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunScenario()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var id = Guid.NewGuid().ToString("N");
        var mutexName = $"mpdf-test-mutex-{id}";
        var pipeName = $"mpdf-test-pipe-{id}";

        MainViewModel? vm = null;
        try
        {
            using var primary = new SingleInstanceService(mutexName, pipeName);

            var dir = Path.Combine(Path.GetTempPath(), $"mpdf-si-{id}");
            vm = new MainViewModel(new NullDialog(), new RecentFilesStore(dir));

            // Item 3 (revisão pós-Task 1): assina PathReceived ANTES de TryAcquire — mesma ordem do
            // fix em App.xaml.cs (fecha a janela "listener já rodando, ninguém assinado ainda").
            primary.PathReceived += path => dispatcher.BeginInvoke(new Action(() => { _ = vm.OpenPath(path); }));

            Assert.True(primary.TryAcquire(null));

            using (var second1 = new SingleInstanceService(mutexName, pipeName))
                Assert.False(second1.TryAcquire(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));

            Pump(() => vm.Documents.Count == 1, TimeSpan.FromSeconds(10));
            Assert.Single(vm.Documents);

            using (var second2 = new SingleInstanceService(mutexName, pipeName))
                Assert.False(second2.TryAcquire(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));

            Pump(() => vm.Documents.Count == 2, TimeSpan.FromSeconds(10));
            Assert.Equal(2, vm.Documents.Count);
        }
        finally
        {
            if (vm is not null)
                foreach (var doc in vm.Documents.ToArray()) doc.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    // Mesmo helper de ViewerIntegrationTests: bombeia a fila do dispatcher da thread ATUAL até a
    // condição ficar verdadeira ou o timeout vencer.
    private static void Pump(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(50);
        }
    }
}
