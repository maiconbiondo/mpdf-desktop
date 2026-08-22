using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using mPdf.App.Services;
using mPdf.App.ViewModels;

namespace mPdf.App.Tests;

// ---- fakes locais (file-scoped, mesmo padrão de UiPromptsGuardTests/MainViewModelTests) -----------------

file sealed class SpyUpdateSourceFactory
{
    public int CallCount { get; private set; }
    private readonly LatestRelease? _result;

    public SpyUpdateSourceFactory(LatestRelease? result) => _result = result;

    public IUpdateSource Create()
    {
        CallCount++;
        return new FakeUpdateSource(_result);
    }
}

file sealed class FakeUpdateSource(LatestRelease? result) : IUpdateSource
{
    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct) => Task.FromResult(result);
}

file sealed class FakeConfirmInstallUpdateService(bool result) : IConfirmInstallUpdateService
{
    public int CallCount { get; private set; }
    public bool Confirm(string message) { CallCount++; return result; }
}

// I3 (revisão de segurança) — spy do sink de crash-log: nunca escreve no %AppData% real da máquina de
// teste (mesma disciplina de isolamento de `CrashLogTests`, que usa o overload `internal` com diretório
// injetável — aqui o sink inteiro é substituído, então nem essa sobrecarga precisa ser tocada).
file sealed class SpyCrashLogSink
{
    public int CallCount { get; private set; }
    public Exception? LastException { get; private set; }
    public void Append(Exception ex) { CallCount++; LastException = ex; }
}

/// Task 2 (Plano 11) — testes de `SobreViewModel`: (1) prova COMPORTAMENTAL de "rede só por clique"
/// (complementa a prova textual de `UpdateNetworkConfinementTests`) — a fábrica `Func&lt;IUpdateSource&gt;`
/// injetada tem ZERO chamadas só de construir o VM, exatamente 1 chamada por execução do comando
/// "Verificar atualização"; (2) transições de estado a partir dos 3 desfechos de `VerificarAsync`
/// (Disponivel/Atualizado/Erro); (3) `ProsseguirComInstalacaoAsync` (contínuação pós-download, `internal`
/// — testável sem precisar de uma rede real para o download, usando um `UpdateService.VerifiedUpdateFile`
/// REAL obtido pelo caminho legítimo `UpdateService.VerifyAndFinalize` sobre um arquivo REAL em disco):
/// prompt recusado -> não fecha nem instala; documentos sujos não resolvidos -> não fecha nem instala;
/// tudo confirmado -> instalador inicia ANTES do shutdown, nesta ordem.
public class SobreViewModelTests
{
    private const string ValidSha = "058cf405f778fc15284646e9d7ad8377171681366424a448b588a17bb4a1c813";

    // Delegates "nunca deveriam ser chamados" para os testes que não exercitam o fluxo de instalação —
    // uma chamada inesperada derruba o teste imediatamente em vez de mascarar um bug de sequenciamento.
    private static Func<bool> NeverConfirmCloseAll => () => throw new InvalidOperationException("ConfirmCloseAll não deveria ter sido chamado neste teste.");
    private static Action<string> NeverStartInstaller => _ => throw new InvalidOperationException("startInstaller não deveria ter sido chamado neste teste.");
    private static Action NeverShutdown => () => throw new InvalidOperationException("shutdown não deveria ter sido chamado neste teste.");

    private static SobreViewModel BuildVm(
        Func<IUpdateSource>? createSource = null,
        IConfirmInstallUpdateService? confirmInstall = null,
        Func<bool>? confirmCloseAllDocuments = null,
        Action<string>? startInstaller = null,
        Action? shutdown = null,
        Action<Exception>? logCrash = null) => new(
        confirmCloseAllDocuments ?? NeverConfirmCloseAll,
        startInstaller ?? NeverStartInstaller,
        shutdown ?? NeverShutdown,
        createSource,
        confirmInstall,
        logCrash ?? (_ => { })); // default: nunca toca %AppData% real (ver SpyCrashLogSink no teste dedicado)

    // ---- rede só por clique — prova comportamental ------------------------------------------------------

    [Fact]
    public void Construir_NaoInvocaFabricaDeFonte()
    {
        var spy = new SpyUpdateSourceFactory(null);
        _ = BuildVm(createSource: spy.Create);

        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task VerificarAtualizacaoCommand_InvocaFabricaExatamenteUmaVezPorExecucao()
    {
        var spy = new SpyUpdateSourceFactory(null);
        var vm = BuildVm(createSource: spy.Create);

        await vm.VerificarAtualizacaoCommand.ExecuteAsync(null);
        Assert.Equal(1, spy.CallCount);

        await vm.VerificarAtualizacaoCommand.ExecuteAsync(null); // clicar "Verificar" de novo
        Assert.Equal(2, spy.CallCount);
    }

    // Minor (revisão) — reentrância: CanExecute de VerificarAtualizacaoCommand precisa ficar FALSO
    // enquanto uma checagem já está em voo (mesmo padrão já provado pra BaixarEInstalarCommand via
    // BaixarEInstalarCommand_Ocioso_CanExecuteIsFalse/CanBaixar). `ExecuteAsync` (chamado em outros
    // testes desta classe) NÃO consulta CanExecute — só `ICommand.CanExecute`/o binding de UI fazem — por
    // isso este teste confere `.CanExecute(null)` diretamente enquanto a Task de verificação ainda não
    // completou (via `TaskCompletionSource`), não uma 2ª chamada de `ExecuteAsync`.
    [Fact]
    public async Task VerificarAtualizacaoCommand_EnquantoVerificando_CanExecuteEhFalso()
    {
        var tcs = new TaskCompletionSource<LatestRelease?>();
        var vm = BuildVm(createSource: () => new BlockingUpdateSource(tcs));
        Assert.True(vm.VerificarAtualizacaoCommand.CanExecute(null)); // Ocioso -> pode verificar

        var executando = vm.VerificarAtualizacaoCommand.ExecuteAsync(null);
        Assert.Equal(SobreEstado.Verificando, vm.Estado);
        Assert.False(vm.VerificarAtualizacaoCommand.CanExecute(null)); // em voo -> reentrância bloqueada

        tcs.SetResult(null); // libera a checagem em voo (release sem TagName -> mapeado como erro)
        await executando;

        Assert.NotEqual(SobreEstado.Verificando, vm.Estado);
        Assert.True(vm.VerificarAtualizacaoCommand.CanExecute(null)); // terminou -> pode tentar de novo
    }

    // ---- transições de estado a partir de VerificarAsync -------------------------------------------------

    [Fact]
    public async Task VerificarAtualizacaoCommand_UpdateDisponivel_SetsEstadoDisponivelComInfo()
    {
        var v = UpdateService.CurrentVersion();
        var release = new LatestRelease($"v{v.Major}.{v.Minor}.{v.Build + 1}", $"notas.\n\nSHA256: {ValidSha}",
            "mPDF-Setup-9.9.9.exe", "https://example.invalid/x.exe", 100);
        var vm = BuildVm(createSource: () => new FakeUpdateSource(release));

        await vm.VerificarAtualizacaoCommand.ExecuteAsync(null);

        Assert.Equal(SobreEstado.Disponivel, vm.Estado);
        Assert.NotNull(vm.AtualizacaoDisponivel);
        Assert.True(vm.BaixarEInstalarCommand.CanExecute(null));
    }

    [Fact]
    public async Task VerificarAtualizacaoCommand_JaAtualizado_SetsEstadoAtualizado()
    {
        var release = new LatestRelease("v" + UpdateService.CurrentVersionText(), "notas", "x.exe", "https://x.invalid/x.exe", 1);
        var vm = BuildVm(createSource: () => new FakeUpdateSource(release));

        await vm.VerificarAtualizacaoCommand.ExecuteAsync(null);

        Assert.Equal(SobreEstado.Atualizado, vm.Estado);
        Assert.False(vm.BaixarEInstalarCommand.CanExecute(null));
    }

    [Fact]
    public async Task VerificarAtualizacaoCommand_Erro_SetsEstadoErroComMensagem()
    {
        var vm = BuildVm(createSource: () => new ThrowingUpdateSource());

        await vm.VerificarAtualizacaoCommand.ExecuteAsync(null);

        Assert.Equal(SobreEstado.Erro, vm.Estado);
        Assert.False(string.IsNullOrEmpty(vm.MensagemErro));
        Assert.False(vm.BaixarEInstalarCommand.CanExecute(null));
    }

    [Fact]
    public void BaixarEInstalarCommand_Ocioso_CanExecuteIsFalse()
    {
        var vm = BuildVm();
        Assert.False(vm.BaixarEInstalarCommand.CanExecute(null));
    }

    // ---- ProsseguirComInstalacaoAsync — fluxo de instalação (sujos resolvidos ANTES do shutdown) --------

    private static UpdateService.VerifiedUpdateFile RealVerifiedFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-sobrevm-test-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "instalador simulado");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var result = UpdateService.VerifyAndFinalize(path, hash); // caminho LEGÍTIMO — VerifiedUpdateFile só nasce daqui
        Assert.Equal(DownloadStatus.Verificado, result.Status);
        return result.Arquivo!;
    }

    [Fact]
    public async Task ProsseguirComInstalacao_PromptRecusado_NaoFechaNemInstala_MantemArquivo()
    {
        var arquivo = RealVerifiedFile();
        try
        {
            var vm = BuildVm(confirmInstall: new FakeConfirmInstallUpdateService(result: false));

            await vm.ProsseguirComInstalacaoAsync(arquivo);

            Assert.Equal(SobreEstado.AguardandoInstalacao, vm.Estado);
            Assert.Equal(arquivo.CaminhoArquivo, vm.CaminhoArquivoBaixado);
            Assert.True(File.Exists(arquivo.CaminhoArquivo), "prompt recusado deveria MANTER o arquivo baixado");
        }
        finally { TryDelete(arquivo.CaminhoArquivo); }
    }

    [Fact]
    public async Task ProsseguirComInstalacao_DocumentosSujosNaoResolvidos_NaoFechaNemInstala()
    {
        var arquivo = RealVerifiedFile();
        try
        {
            var vm = BuildVm(
                confirmInstall: new FakeConfirmInstallUpdateService(result: true),
                confirmCloseAllDocuments: () => false); // usuário cancelou salvar um documento sujo

            await vm.ProsseguirComInstalacaoAsync(arquivo);

            Assert.Equal(SobreEstado.AguardandoInstalacao, vm.Estado);
            Assert.True(File.Exists(arquivo.CaminhoArquivo));
        }
        finally { TryDelete(arquivo.CaminhoArquivo); }
    }

    [Fact]
    public async Task ProsseguirComInstalacao_TudoConfirmado_IniciaInstaladorAntesDoShutdown_NestaOrdem()
    {
        var arquivo = RealVerifiedFile();
        var chamadas = new List<string>();
        try
        {
            var vm = BuildVm(
                confirmInstall: new FakeConfirmInstallUpdateService(result: true),
                confirmCloseAllDocuments: () => true,
                startInstaller: path => chamadas.Add($"start:{path}"),
                shutdown: () => chamadas.Add("shutdown"));

            await vm.ProsseguirComInstalacaoAsync(arquivo);

            Assert.Equal(new[] { $"start:{arquivo.CaminhoArquivo}", "shutdown" }, chamadas);
        }
        finally { TryDelete(arquivo.CaminhoArquivo); }
    }

    // ---- I3 (CRÍTICO/Important, revisão de segurança) — Process.Start pode lançar (AV bloqueando/------
    // ---- quarentena, arquivo removido, permissão negada); sem tratamento, o VM ficava CONGELADO em ----
    // ---- Baixando sem log/mensagem nenhuma. Provado ao vivo pelo revisor (UnobservedTaskException -----
    // ---- nunca dispara pra um RelayCommand síncrono). --------------------------------------------------

    [Fact]
    public async Task ProsseguirComInstalacao_StartInstallerLanca_NaoFecha_RegistraLog_MostraMensagemComCaminho()
    {
        var arquivo = RealVerifiedFile();
        var crashLog = new SpyCrashLogSink();
        var falhaSimulada = new InvalidOperationException("bloqueado pelo antivírus (simulado)");
        try
        {
            var vm = BuildVm(
                confirmInstall: new FakeConfirmInstallUpdateService(result: true),
                confirmCloseAllDocuments: () => true,
                startInstaller: _ => throw falhaSimulada,
                shutdown: NeverShutdown, // shutdown NUNCA deveria ser alcançado neste cenário
                logCrash: crashLog.Append);

            var ex = await Record.ExceptionAsync(() => vm.ProsseguirComInstalacaoAsync(arquivo));

            Assert.Null(ex); // a exceção do instalador é CAPTURADA, nunca escapa pro chamador
            Assert.Equal(1, crashLog.CallCount);
            Assert.Same(falhaSimulada, crashLog.LastException);
            Assert.Equal(SobreEstado.Erro, vm.Estado);
            Assert.NotNull(vm.MensagemErro);
            Assert.Contains(arquivo.CaminhoArquivo, vm.MensagemErro); // caminho do arquivo verificado SURGE na mensagem
            Assert.Equal(arquivo.CaminhoArquivo, vm.CaminhoArquivoBaixado); // e também na propriedade dedicada
            Assert.True(File.Exists(arquivo.CaminhoArquivo), "o arquivo verificado NUNCA deveria ser apagado por essa falha");
        }
        finally { TryDelete(arquivo.CaminhoArquivo); }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

file sealed class ThrowingUpdateSource : IUpdateSource
{
    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct) =>
        throw new HttpRequestException("sem rede neste teste");
}

// Minor (revisão) — controla manualmente QUANDO GetLatestAsync "termina", pra segurar o VM em
// SobreEstado.Verificando tempo suficiente pro teste observar o CanExecute do comando nesse meio-tempo.
file sealed class BlockingUpdateSource(TaskCompletionSource<LatestRelease?> tcs) : IUpdateSource
{
    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct) => tcs.Task;
}
