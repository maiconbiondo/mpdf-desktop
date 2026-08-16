using System.IO;
using System.IO.Pipes;
using System.Text;

namespace mPdf.App.Services;

/// Task 1 (Plano 6): ver doc XML de ISingleInstanceService pro contrato. Protocolo do pipe: 1 caminho
/// ABSOLUTO por conexão (validado — ver `Path.IsPathRooted` no loop principal, Item 5 da revisão: um
/// caminho RELATIVO chegando pelo pipe é tratado como malformado e ignorado, nunca repassado adiante),
/// UTF-8, terminado em '\n' (CRLF tolerado — o '\r' final é cortado). Escolha registrada: 1 caminho por
/// CONEXÃO (não um pipe persistente com várias linhas) — mais simples, e o caso de uso real
/// (SingleInstanceNames + App.xaml.cs) só precisa mandar 1 argv[0] por processo secundário que nasce e
/// morre.
///
/// Endurecimento do protocolo (brief da Task 1 + revisão pós-Task 1):
///  - Teto de tamanho de linha (<see cref="MaxLineLengthBytes"/>, 32KB — nenhum caminho de arquivo
///    legítimo chega perto disso): uma linha que ultrapassa o teto SEM nunca achar '\n' é descartada
///    (`ReadLineCappedAsync` devolve null), nunca cresce sem limite na memória do processo primário.
///  - Linha vazia/só espaço OU não-absoluta (Item 5) é IGNORADA — nunca dispara PathReceived com lixo
///    nem com um caminho que `OpenPath`/`DocumentSession.OpenAsync` não conseguiria resolver de forma
///    previsível (relativo a QUAL diretório de trabalho? o do processo secundário já morto).
///  - Crash do cliente NO MEIO do envio (desconexão abrupta, sem '\n') derruba a stream com
///    IOException — capturada no loop principal, que volta a escutar a PRÓXIMA conexão. O processo
///    primário nunca trava esperando um '\n' que não vai chegar.
///  - Falha SUSTENTADA de aceitar conexão (Item 2 da revisão — ex.: nome de pipe "squatted" por outro
///    processo do MESMO usuário, impedindo o bind) também cai no `catch (IOException)`, mas SEM o
///    backoff limitado (<see cref="BackoffMs"/>, 250ms, via `_backoffDelay`) o loop giraria em CPU
///    cheia pro resto da vida do processo primário — uma falha transitória de 1 cliente que
///    desconecta no meio e uma falha PERMANENTE de bind passam pelo MESMO catch, então o backoff se
///    aplica aos dois (custo aceito: no caso comum — cliente que cai no meio — perde-se até 250ms antes
///    de aceitar a PRÓXIMA conexão legítima, imperceptível pro caso de uso real).
///  - ACL do pipe: o DEFAULT do NamedPipeServerStream (sem opção nenhuma) NÃO é "só o usuário atual" —
///    é o descritor de segurança padrão do Win32 (controle total pro LocalSystem/Administradores/dono,
///    mas LEITURA também pro grupo Everyone e pra conta anônima). Verificado (Microsoft Learn, docs de
///    PipeOptions): sem endurecer, qualquer processo de OUTRO usuário na mesma máquina conseguiria ao
///    menos abrir o pipe pra leitura. `PipeOptions.CurrentUserOnly` (servidor E cliente, abaixo) troca
///    isso por um descritor que só permite o MESMO usuário (e no Windows, também a MESMA elevação) —
///    2ª camada de verdade por cima do nome único por usuário+sessão de SingleInstanceNames (que já
///    evita COLISÃO de nome entre usuários; CurrentUserOnly evita que um usuário B, mesmo SABENDO o
///    nome do pipe do usuário A por algum outro meio, consiga conectar nele).
///
/// FORA do modelo de ameaça (Item 4 da revisão, residual aceito): um processo MALICIOSO rodando como o
/// MESMO usuário (mesma elevação) ainda pode "ocupar" o nome do mutex/pipe primeiro (squatting) —
/// nenhuma ACL de pipe/mutex nomeado distingue processos do MESMO usuário entre si. Aceito: um atacante
/// que já roda código como o usuário tem vetores muito mais baratos e diretos (ler os arquivos do
/// usuário, injetar em processos dele, ler a área de transferência) do que orquestrar uma corrida de
/// squatting contra este mutex/pipe especificamente — mesma prática da indústria (nenhum app de
/// instância única via mutex/pipe nomeado do Windows se defende de um atacante já rodando como o mesmo
/// usuário). O que ESTE endurecimento garante é isolamento entre USUÁRIOS diferentes (a ameaça real
/// numa máquina compartilhada), não entre processos do mesmo usuário.
public sealed class SingleInstanceService : ISingleInstanceService
{
    private const int MaxLineLengthBytes = 32 * 1024;
    private const int ConnectTimeoutMs = 2000;
    // Item 2 (revisão pós-Task 1, Plano 6): teto do backoff numa falha SUSTENTADA de accept — ver doc
    // XML da classe.
    private const int BackoffMs = 250;

    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly Func<CancellationToken, Task> _backoffDelay;
    private readonly Func<CancellationToken, Task<Stream>> _acceptConnection;
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    public event Action<string>? PathReceived;

    public SingleInstanceService(string mutexName, string pipeName)
        : this(mutexName, pipeName, backoffDelay: null, acceptConnection: null) { }

    // Item 2 (revisão pós-Task 1, Plano 6): construtor com os 2 seams de TESTE — permitem provar o
    // backoff limitado do loop numa falha SUSTENTADA de accept (ex.: pipe "squatted") sem esperar
    // 250ms de verdade N vezes e sem precisar derrubar um pipe nomeado real pra simular a falha.
    // Produção SEMPRE usa o construtor de 2 argumentos acima (que encadeia aqui passando null pros
    // dois — vira os defaults via `??` no corpo) — nenhum call site de produção passa estes 2
    // argumentos extras.
    public SingleInstanceService(
        string mutexName,
        string pipeName,
        Func<CancellationToken, Task>? backoffDelay,
        Func<CancellationToken, Task<Stream>>? acceptConnection)
    {
        _mutexName = mutexName;
        _pipeName = pipeName;
        _backoffDelay = backoffDelay ?? (ct => Task.Delay(BackoffMs, ct));
        _acceptConnection = acceptConnection ?? DefaultAcceptConnectionAsync;
    }

    public bool TryAcquire(string? pathToForward)
    {
        _mutex = new Mutex(initiallyOwned: true, name: _mutexName, createdNew: out bool createdNew);
        if (createdNew)
        {
            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            return true;
        }

        // Não conseguimos o mutex: já existe uma instância primária. O handle que acabamos de abrir
        // pra ele não representa posse nenhuma (só um handle pro objeto NOMEADO já existente) — solta
        // já, não tem nada pra liberar/possuir.
        _mutex.Dispose();
        _mutex = null;

        if (!string.IsNullOrWhiteSpace(pathToForward))
        {
            try { SendToPrimary(pathToForward); }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
            {
                // A instância primária pode ter fechado bem entre a checagem do mutex acima e a
                // conexão do pipe aqui (janela de corrida pequena, mas real) — best-effort: a
                // secundária ainda sai (retorna false), só não consegue avisar ninguém.
            }
        }
        return false;
    }

    private void SendToPrimary(string path)
    {
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        client.Connect(ConnectTimeoutMs); // lança TimeoutException se a primária não aceitar a tempo
        var bytes = Encoding.UTF8.GetBytes(path + "\n");
        client.Write(bytes, 0, bytes.Length);
        client.Flush();
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Stream? server = null;
            try
            {
                server = await _acceptConnection(token).ConfigureAwait(false);

                var line = await ReadLineCappedAsync(server, token).ConfigureAwait(false);
                // Item 5 (revisão pós-Task 1): só repassa se for um caminho ABSOLUTO de verdade — a
                // doc do protocolo promete "1 caminho absoluto por conexão"; sem esta checagem, uma
                // linha relativa (protocolo malformado OU um bug em algum futuro remetente) chegaria a
                // PathReceived e MainViewModel.OpenPath tentaria abrir relativo ao diretório de
                // trabalho ATUAL do processo primário — quase certamente não é o que o usuário quis.
                if (!string.IsNullOrWhiteSpace(line) && Path.IsPathRooted(line))
                {
                    PathReceived?.Invoke(line);
                }
            }
            catch (OperationCanceledException) { break; } // Dispose cancelou — encerra o loop
            catch (ObjectDisposedException) { break; }     // idem, corrida de teardown
            catch (IOException)
            {
                // 2 causas possíveis, MESMO catch (ver doc XML da classe): (a) cliente desconectou/
                // crashou no meio de UMA conexão — transitório; ou (b) falha SUSTENTADA de aceitar
                // conexão nenhuma (Item 2 — ex.: pipe "squatted"). Sem o backoff abaixo, o caso (b)
                // giraria em CPU cheia pro resto da vida do processo — o `catch(OperationCanceledException)`
                // aninhado cobre o Dispose cancelando ENQUANTO o backoff está em andamento.
                try { await _backoffDelay(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally { server?.Dispose(); }
        }
    }

    /// Default de PRODUÇÃO de `_acceptConnection` — cria uma `NamedPipeServerStream` nova (endurecida
    /// com `CurrentUserOnly`, ver doc XML da classe) e espera 1 conexão. Lança IOException/
    /// OperationCanceledException tanto num bind que falha quanto numa espera cancelada — o chamador
    /// (`ListenLoopAsync`) trata os 2 casos.
    private async Task<Stream> DefaultAcceptConnectionAsync(CancellationToken token)
    {
        var server = new NamedPipeServerStream(
            _pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// Lê uma linha terminada em '\n' de `stream`, com teto de <see cref="MaxLineLengthBytes"/> bytes.
    /// Devolve null (linha "inválida", ignorada pelo chamador) em 3 casos: EOF sem nunca achar '\n'
    /// (cliente fechou/crashou no meio), teto excedido sem achar '\n' (protocolo malformado), ou
    /// IOException durante a leitura (mesma categoria de desconexão abrupta).
    private static async Task<string?> ReadLineCappedAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            int read;
            try { read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false); }
            catch (IOException) { return null; }
            if (read == 0) return null; // EOF sem '\n' -- linha incompleta

            int newlineIndex = Array.IndexOf(chunk, (byte)'\n', 0, read);
            if (newlineIndex >= 0)
            {
                buffer.Write(chunk, 0, newlineIndex);
                break;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxLineLengthBytes) return null; // excedeu o teto -- descarta
        }

        var bytes = buffer.ToArray();
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1]; // tolera CRLF
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* loop já encerrando/encerrado -- não pode travar o Dispose */ }
        _cts?.Dispose();

        if (_mutex is { } mutex)
        {
            // Só chegamos aqui com _mutex != null quando ESTA instância é a primária (TryAcquire já
            // zera o campo no ramo secundário, ver acima) -- ReleaseMutex sempre seguro.
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
