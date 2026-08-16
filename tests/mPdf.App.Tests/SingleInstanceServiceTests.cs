using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

/// Task 1 (Plano 6): serviço de instância única (mutex nomeado + pipe nomeado atrás de
/// ISingleInstanceService). Cada teste gera seu PRÓPRIO par de nomes únicos (Guid.NewGuid()) — NUNCA
/// os nomes fixos de produção (SingleInstanceNames) — pra nunca colidir entre testes concorrentes
/// (xunit paraleliza collections/classes) nem com uma instância real do app rodando na máquina do dev.
/// Item 3 (revisão pós-Task 1): todo teste que espera receber um caminho assina `PathReceived` ANTES
/// de chamar `TryAcquire` — mesma ordem exigida em produção (App.xaml.cs), fecha a mesma janela
/// "listener já rodando, ninguém assinado ainda" também aqui.
public class SingleInstanceServiceTests
{
    private static (string Mutex, string Pipe) NewNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return ($"mpdf-test-mutex-{id}", $"mpdf-test-pipe-{id}");
    }

    [Fact] // 1ª instância consegue o mutex -> primária
    public void TryAcquire_FirstInstance_ReturnsTrue()
    {
        var (mutexName, pipeName) = NewNames();
        using var svc = new SingleInstanceService(mutexName, pipeName);
        Assert.True(svc.TryAcquire(null));
    }

    [Fact] // 2ª instância encontra o mutex JÁ tomado -> secundária, deve sair
    public void TryAcquire_SecondInstance_MutexHeld_ReturnsFalse()
    {
        var (mutexName, pipeName) = NewNames();
        using var first = new SingleInstanceService(mutexName, pipeName);
        Assert.True(first.TryAcquire(null));

        using var second = new SingleInstanceService(mutexName, pipeName);
        Assert.False(second.TryAcquire(null));
    }

    [Fact] // 2ª instância detecta, ENVIA o caminho pelo pipe real, retorna false — a 1ª recebe via evento
    public void TryAcquire_SecondInstance_ForwardsPathToPrimary()
    {
        var (mutexName, pipeName) = NewNames();
        using var first = new SingleInstanceService(mutexName, pipeName);

        string? received = null;
        var gotIt = new ManualResetEventSlim(false);
        first.PathReceived += p => { received = p; gotIt.Set(); };

        Assert.True(first.TryAcquire(null));

        using var second = new SingleInstanceService(mutexName, pipeName);
        Assert.False(second.TryAcquire(@"C:\algum\Relatorio.pdf"));

        Assert.True(gotIt.Wait(TimeSpan.FromSeconds(5)), "primeira instância não recebeu o caminho a tempo");
        Assert.Equal(@"C:\algum\Relatorio.pdf", received);
    }

    [Fact] // caminho com espaços/acentuação sobrevive ao round-trip UTF-8 pelo pipe
    public void TryAcquire_PathWithSpacesAndAccents_RoundTrips()
    {
        var (mutexName, pipeName) = NewNames();
        using var first = new SingleInstanceService(mutexName, pipeName);

        string? received = null;
        var gotIt = new ManualResetEventSlim(false);
        first.PathReceived += p => { received = p; gotIt.Set(); };

        Assert.True(first.TryAcquire(null));

        var path = @"C:\Usuários\Fulano\Relatório Anual (2026) — versão final.pdf";
        using var second = new SingleInstanceService(mutexName, pipeName);
        Assert.False(second.TryAcquire(path));

        Assert.True(gotIt.Wait(TimeSpan.FromSeconds(5)), "caminho acentuado não chegou a tempo");
        Assert.Equal(path, received);
    }

    [Fact] // sem caminho pra encaminhar (2ª instância sem args) — nenhum evento espúrio na primária
    public void TryAcquire_SecondInstance_NullPathToForward_NoEventFired()
    {
        var (mutexName, pipeName) = NewNames();
        using var first = new SingleInstanceService(mutexName, pipeName);

        bool fired = false;
        first.PathReceived += _ => fired = true;

        Assert.True(first.TryAcquire(null));

        using var second = new SingleInstanceService(mutexName, pipeName);
        Assert.False(second.TryAcquire(null));

        Thread.Sleep(300); // só evidencia AUSÊNCIA de evento — nada pra aguardar de verdade
        Assert.False(fired);
    }

    [Fact] // Item 5 (revisão pós-Task 1): caminho RELATIVO chegando pelo pipe é ignorado (protocolo
           // promete "1 caminho ABSOLUTO por conexão" — ver doc XML de SingleInstanceService)
    public void TryAcquire_SecondInstance_NonRootedPath_Ignored_ServerKeepsListening()
    {
        var (mutexName, pipeName) = NewNames();
        using var first = new SingleInstanceService(mutexName, pipeName);

        bool fired = false;
        first.PathReceived += _ => fired = true;

        Assert.True(first.TryAcquire(null));

        using (var second = new SingleInstanceService(mutexName, pipeName))
            Assert.False(second.TryAcquire("relatorio.pdf")); // RELATIVO, não absoluto

        Thread.Sleep(300);
        Assert.False(fired);

        // servidor sobrevive: uma 2ª conexão com caminho ABSOLUTO em seguida ainda funciona.
        string? received = null;
        var gotIt = new ManualResetEventSlim(false);
        first.PathReceived += p => { received = p; gotIt.Set(); };

        using var third = new SingleInstanceService(mutexName, pipeName);
        Assert.False(third.TryAcquire(@"D:\ok.pdf"));
        Assert.True(gotIt.Wait(TimeSpan.FromSeconds(5)), "servidor não voltou a escutar após caminho relativo");
        Assert.Equal(@"D:\ok.pdf", received);
    }

    [Fact] // Dispose libera o mutex — a PRÓXIMA instância com o MESMO nome vira primária
    public void Dispose_ReleasesMutex_AllowsNewPrimary()
    {
        var (mutexName, pipeName) = NewNames();
        var first = new SingleInstanceService(mutexName, pipeName);
        Assert.True(first.TryAcquire(null));
        first.Dispose();

        using var second = new SingleInstanceService(mutexName, pipeName);
        Assert.True(second.TryAcquire(null));
    }

    [Fact] // protocolo malformado (linha > teto, sem quebra de linha) é IGNORADO — servidor sobrevive
    public void Server_OversizedLineWithoutNewline_Ignored_ServerKeepsListening()
    {
        var (mutexName, pipeName) = NewNames();
        using var first = new SingleInstanceService(mutexName, pipeName);

        bool fired = false;
        first.PathReceived += _ => fired = true;

        Assert.True(first.TryAcquire(null));

        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
        {
            client.Connect(2000);
            var junk = new byte[40 * 1024]; // > teto de 32KB, sem '\n' nenhum
            try { client.Write(junk, 0, junk.Length); client.Flush(); }
            catch (IOException)
            {
                // Esperado: o servidor pode fechar a conexão assim que o teto é excedido (ver
                // ReadLineCappedAsync), ANTES do cliente terminar de escrever os 40KB -- "Pipe is
                // broken" do lado de quem escreve é justamente a rejeição precoce funcionando.
            }
        }

        Thread.Sleep(500);
        Assert.False(fired);

        // servidor precisa ter voltado a escutar: uma 2ª conexão válida em seguida ainda funciona.
        string? received = null;
        var gotIt = new ManualResetEventSlim(false);
        first.PathReceived += p => { received = p; gotIt.Set(); };

        using var second = new SingleInstanceService(mutexName, pipeName);
        Assert.False(second.TryAcquire(@"D:\ok.pdf"));
        Assert.True(gotIt.Wait(TimeSpan.FromSeconds(5)), "servidor não voltou a escutar após linha malformada");
        Assert.Equal(@"D:\ok.pdf", received);
    }

    [Fact] // cliente cai NO MEIO do envio (sem newline) — servidor não trava, aceita a próxima conexão
    public void Server_ClientDisconnectsMidWrite_ServerSurvivesAndAcceptsNextConnection()
    {
        var (mutexName, pipeName) = NewNames();
        using var first = new SingleInstanceService(mutexName, pipeName);
        Assert.True(first.TryAcquire(null));

        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
        {
            client.Connect(2000);
            var partial = Encoding.UTF8.GetBytes(@"C:\caminho-pela-metade-sem-quebra-de-linha");
            client.Write(partial, 0, partial.Length);
            client.Flush();
            // sai do using aqui SEM nunca escrever '\n' -- pipe fecha abruptamente, como um crash real.
        }

        string? received = null;
        var gotIt = new ManualResetEventSlim(false);
        first.PathReceived += p => { received = p; gotIt.Set(); };

        using var second = new SingleInstanceService(mutexName, pipeName);
        Assert.False(second.TryAcquire(@"E:\completo.pdf"));
        Assert.True(gotIt.Wait(TimeSpan.FromSeconds(5)), "servidor não sobreviveu à desconexão no meio do envio");
        Assert.Equal(@"E:\completo.pdf", received);
    }

    [Fact] // Item 2 (revisão pós-Task 1): falha SUSTENTADA de aceitar conexão (ex.: pipe "squatted")
           // -- backoff é chamado ANTES de cada retry, e o loop SOBREVIVE (não morre, não trava).
    public void ListenLoop_SustainedAcceptFailure_BacksOffBeforeEachRetry_AndSurvives()
    {
        var (mutexName, pipeName) = NewNames();
        int acceptCallCount = 0;
        int delayCallCount = 0;
        var reachedFourthAttempt = new ManualResetEventSlim(false);

        Task<Stream> AcceptAlwaysFails(CancellationToken ct)
        {
            int n = Interlocked.Increment(ref acceptCallCount);
            if (n >= 4) reachedFourthAttempt.Set();
            throw new IOException("bind simulado -- pipe squatted (Item 2 da revisão)");
        }

        Task Backoff(CancellationToken ct)
        {
            Interlocked.Increment(ref delayCallCount);
            return Task.CompletedTask; // instantâneo -- prova a CHAMADA do backoff, sem esperar 250ms de verdade
        }

        using var svc = new SingleInstanceService(mutexName, pipeName, Backoff, AcceptAlwaysFails);
        Assert.True(svc.TryAcquire(null)); // mutex REAL -- só o accept do pipe é substituído

        Assert.True(reachedFourthAttempt.Wait(TimeSpan.FromSeconds(5)),
            "loop não sobreviveu a falhas sustentadas de accept (deveria fazer retry indefinidamente com backoff)");
        Assert.True(delayCallCount >= 3,
            $"esperava backoff ANTES de cada retry (>=3 chamadas de delay pra 4 tentativas de accept), teve {delayCallCount}");
    }
}

/// Nomes de PRODUÇÃO (mutex/pipe estáveis, usados só por App.xaml.cs) — sanidade de que o nome
/// embute o usuário atual (Obs de segurança do brief: dois USUÁRIOS diferentes na mesma máquina não
/// podem colidir no mesmo mutex/pipe).
public class SingleInstanceNamesTests
{
    [Fact]
    public void MutexAndPipeNames_AreStable_AndDistinctFromEachOther()
    {
        Assert.Equal(SingleInstanceNames.MutexName, SingleInstanceNames.MutexName);
        Assert.Equal(SingleInstanceNames.PipeName, SingleInstanceNames.PipeName);
        Assert.NotEqual(SingleInstanceNames.MutexName, SingleInstanceNames.PipeName);
    }

    [Fact]
    public void MutexAndPipeNames_EmbedCurrentUser()
    {
        var user = Environment.UserName.Replace('\\', '_');
        Assert.Contains(user, SingleInstanceNames.MutexName);
        Assert.Contains(user, SingleInstanceNames.PipeName);
    }
}

/// Item 1 (revisão pós-Task 1, Plano 6): SingleInstanceLaunchGate.ShouldContinueLaunch — instância
/// única é BEST-EFFORT, uma falha em TryAcquire nunca pode bloquear o lançamento do app (fail-open).
public class SingleInstanceLaunchGateTests
{
    private sealed class ThrowingOnAcquireService : ISingleInstanceService
    {
        public event Action<string>? PathReceived { add { } remove { } }
        public bool TryAcquire(string? pathToForward) =>
            throw new UnauthorizedAccessException("mutex squatted (simulado)");
        public void Dispose() { }
    }

    [Fact]
    public void AcquireThrows_LogsException_FailsOpen_ReturnsTrue()
    {
        Exception? logged = null;
        bool result = SingleInstanceLaunchGate.ShouldContinueLaunch(
            new ThrowingOnAcquireService(), @"C:\x.pdf", ex => logged = ex);

        Assert.True(result, "falha em TryAcquire deveria falhar ABERTA (continuar o lançamento)");
        Assert.IsType<UnauthorizedAccessException>(logged);
    }

    [Fact] // invariante: comportamento normal (primária, sem exceção) preservado
    public void NormalPrimary_ReturnsTrue_NeverLogs()
    {
        var (mutexName, pipeName) = NewNames();
        using var svc = new SingleInstanceService(mutexName, pipeName);

        bool loggedAnything = false;
        Assert.True(SingleInstanceLaunchGate.ShouldContinueLaunch(svc, null, _ => loggedAnything = true));
        Assert.False(loggedAnything);
    }

    [Fact] // invariante: comportamento normal (secundária, sem exceção) preservado
    public void NormalSecondary_ReturnsFalse_NeverLogs()
    {
        var (mutexName, pipeName) = NewNames();
        using var first = new SingleInstanceService(mutexName, pipeName);
        Assert.True(first.TryAcquire(null));

        using var second = new SingleInstanceService(mutexName, pipeName);
        bool loggedAnything = false;
        Assert.False(SingleInstanceLaunchGate.ShouldContinueLaunch(second, null, _ => loggedAnything = true));
        Assert.False(loggedAnything);
    }

    private static (string Mutex, string Pipe) NewNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return ($"mpdf-test-mutex-{id}", $"mpdf-test-pipe-{id}");
    }
}
