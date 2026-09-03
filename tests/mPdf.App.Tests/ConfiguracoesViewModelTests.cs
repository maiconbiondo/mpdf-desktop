using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;

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
    // T3 (Plano 18): captura as mensagens exibidas -- o mesmo seam agora é reusado pra 2 prompts
    // diferentes no fluxo enxuto (aprovação "Atualizar agora?" logo ao achar versão + a confirmação final
    // "Fechar e atualizar?"), então os testes precisam poder inspecionar QUAL texto cada chamada recebeu.
    public List<string> Messages { get; } = new();
    public bool Confirm(string message) { CallCount++; Messages.Add(message); return result; }
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

/// Task 2 (Plano 17) — testes de `ConfiguracoesViewModel`, MIGRADOS de `SobreViewModelTests` (Task 2,
/// Plano 11 + Plano 13 + Plano 14) junto com a lógica que migrou do "Sobre" pro "Configurações" — mesma
/// intenção de cada teste preservada, só o nome da classe/tipo mudou: (1) prova COMPORTAMENTAL de "rede
/// só por clique" (complementa a prova textual de `UpdateNetworkConfinementTests`) — a fábrica
/// `Func&lt;IUpdateSource&gt;` injetada tem ZERO chamadas só de construir o VM, exatamente 1 chamada por
/// execução do comando "Verificar atualização"; (2) transições de estado a partir dos 3 desfechos de
/// `VerificarAsync` (Disponivel/Atualizado/Erro); (3) `ProsseguirComInstalacaoAsync` (contínuação
/// pós-download, `internal` — testável sem precisar de uma rede real para o download, usando um
/// `UpdateService.VerifiedUpdateFile` REAL obtido pelo caminho legítimo
/// `UpdateService.VerifyAndFinalize` sobre um arquivo REAL em disco): prompt recusado -> não fecha nem
/// instala; documentos sujos não resolvidos -> não fecha nem instala; tudo confirmado -> instalador
/// inicia ANTES do shutdown, nesta ordem; (4) Tema/Nitidez extra: persistência + aplicação ao vivo.
public class ConfiguracoesViewModelTests
{
    private const string ValidSha = "058cf405f778fc15284646e9d7ad8377171681366424a448b588a17bb4a1c813";

    // Delegates "nunca deveriam ser chamados" para os testes que não exercitam o fluxo de instalação —
    // uma chamada inesperada derruba o teste imediatamente em vez de mascarar um bug de sequenciamento.
    private static Func<bool> NeverConfirmCloseAll => () => throw new InvalidOperationException("ConfirmCloseAll não deveria ter sido chamado neste teste.");
    private static Action<string> NeverStartInstaller => _ => throw new InvalidOperationException("startInstaller não deveria ter sido chamado neste teste.");
    private static Action NeverShutdown => () => throw new InvalidOperationException("shutdown não deveria ter sido chamado neste teste.");

    private static ConfiguracoesViewModel BuildVm(
        Func<IUpdateSource>? createSource = null,
        IConfirmInstallUpdateService? confirmInstall = null,
        Func<bool>? confirmCloseAllDocuments = null,
        Action<string>? startInstaller = null,
        Action? shutdown = null,
        Action<Exception>? logCrash = null,
        AppConfig? config = null,
        Action<double>? applySupersampleFactor = null,
        Action<ThemeMode>? aplicarTema = null,
        Action<PosicaoMenuAnotacao>? aplicarPosicaoMenuAnotacao = null,
        Func<byte[]?>? escolherRubricaImagem = null) => new(
        confirmCloseAllDocuments ?? NeverConfirmCloseAll,
        startInstaller ?? NeverStartInstaller,
        shutdown ?? NeverShutdown,
        createSource,
        confirmInstall,
        logCrash ?? (_ => { }), // default: nunca toca %AppData% real (ver SpyCrashLogSink no teste dedicado)
        config,
        applySupersampleFactor,
        aplicarTema,
        aplicarPosicaoMenuAnotacao,
        escolherRubricaImagem);

    // Task 2 (Plano 13): diretório de config TEMPORÁRIO -- mesmo padrão de AppConfigTests, nunca toca
    // %AppData%\mPDF real durante a suíte. Devolve o DIRETÓRIO junto (não só a instância) pra testes que
    // precisam reabrir uma 2ª instância sobre o MESMO config.json e provar persistência entre instâncias.
    private static (string Dir, AppConfig Config) TempConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mpdf-config-cfg-{Guid.NewGuid():N}");
        return (dir, new AppConfig(dir));
    }

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
        Assert.Equal(ConfiguracoesEstado.Verificando, vm.Estado);
        Assert.False(vm.VerificarAtualizacaoCommand.CanExecute(null)); // em voo -> reentrância bloqueada

        tcs.SetResult(null); // libera a checagem em voo (release sem TagName -> mapeado como erro)
        await executando;

        Assert.NotEqual(ConfiguracoesEstado.Verificando, vm.Estado);
        Assert.True(vm.VerificarAtualizacaoCommand.CanExecute(null)); // terminou -> pode tentar de novo
    }

    // ---- transições de estado a partir de VerificarAsync -------------------------------------------------

    // T3 (Plano 18): fluxo enxuto -- achar uma versão nova dispara IMEDIATAMENTE um prompt curto de
    // aprovação ("Nova versão X.Y disponível — Atualizar agora?"), em vez de só acender uma tela com
    // botão esperando o clique. Este teste RECUSA o prompt -- prova que Estado permanece Disponivel (a
    // tela/botão "Baixar e instalar" continua ali como fallback manual) e que NADA foi baixado/instalado.
    [Fact]
    public async Task VerificarAtualizacaoCommand_UpdateDisponivel_PromptaAprovacao_RecusarMantemDisponivelComoFallback()
    {
        var v = UpdateService.CurrentVersion();
        var release = new LatestRelease($"v{v.Major}.{v.Minor}.{v.Build + 1}", $"notas.\n\nSHA256: {ValidSha}",
            "mPDF-Setup-9.9.9.exe", "https://example.invalid/x.exe", 100);
        var confirm = new FakeConfirmInstallUpdateService(result: false);
        var vm = BuildVm(createSource: () => new FakeUpdateSource(release), confirmInstall: confirm);

        await vm.VerificarAtualizacaoCommand.ExecuteAsync(null);

        Assert.Equal(1, confirm.CallCount);
        Assert.Contains("Atualizar agora", confirm.Messages[0]);
        Assert.Contains(release.TagName, confirm.Messages[0]); // a versão aparece no prompt curto
        Assert.Equal(ConfiguracoesEstado.Disponivel, vm.Estado);
        Assert.NotNull(vm.AtualizacaoDisponivel);
        Assert.True(vm.BaixarEInstalarCommand.CanExecute(null)); // fallback manual continua oferecido
    }

    // Aprova o prompt de "Atualizar agora?" -- prova que o download é disparado AUTOMATICAMENTE (sem
    // precisar clicar em "Baixar e instalar" à parte), pelo MESMO caminho de código do botão. Usa um host
    // de asset FORA da allowlist (mesma URL "https://example.invalid" já usada nos outros testes desta
    // suíte) pra obter uma falha DETERMINÍSTICA sem tocar rede nenhuma (IsAllowedAssetHost recusa antes de
    // qualquer HttpClient.GetAsync) -- assim o teste fica hermético e ainda prova que o gatilho automático
    // realmente alcançou `UpdateService.BaixarEVerificarAsync`.
    [Fact]
    public async Task VerificarAtualizacaoCommand_UpdateDisponivel_AprovarPrompt_DisparaDownloadAutomaticamente()
    {
        var v = UpdateService.CurrentVersion();
        var release = new LatestRelease($"v{v.Major}.{v.Minor}.{v.Build + 1}", $"notas.\n\nSHA256: {ValidSha}",
            "mPDF-Setup-9.9.9.exe", "https://example.invalid/x.exe", 100);
        var confirm = new FakeConfirmInstallUpdateService(result: true);
        var vm = BuildVm(createSource: () => new FakeUpdateSource(release), confirmInstall: confirm);

        await vm.VerificarAtualizacaoCommand.ExecuteAsync(null);

        // só 1 chamada ao confirm (a aprovação inicial) -- BaixarEVerificarAsync falhou ANTES de chegar
        // na confirmação final "Fechar e atualizar?" (host recusado pela allowlist).
        Assert.Equal(1, confirm.CallCount);
        Assert.Equal(ConfiguracoesEstado.Erro, vm.Estado);
        Assert.Contains("confiável", vm.MensagemErro);
    }

    [Fact]
    public async Task VerificarAtualizacaoCommand_JaAtualizado_SetsEstadoAtualizado()
    {
        var release = new LatestRelease("v" + UpdateService.CurrentVersionText(), "notas", "x.exe", "https://x.invalid/x.exe", 1);
        var vm = BuildVm(createSource: () => new FakeUpdateSource(release));

        await vm.VerificarAtualizacaoCommand.ExecuteAsync(null);

        Assert.Equal(ConfiguracoesEstado.Atualizado, vm.Estado);
        Assert.False(vm.BaixarEInstalarCommand.CanExecute(null));
    }

    [Fact]
    public async Task VerificarAtualizacaoCommand_Erro_SetsEstadoErroComMensagem()
    {
        var vm = BuildVm(createSource: () => new ThrowingUpdateSource());

        await vm.VerificarAtualizacaoCommand.ExecuteAsync(null);

        Assert.Equal(ConfiguracoesEstado.Erro, vm.Estado);
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
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-configvm-test-{Guid.NewGuid():N}.exe");
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
            var confirm = new FakeConfirmInstallUpdateService(result: false);
            var vm = BuildVm(confirmInstall: confirm);

            await vm.ProsseguirComInstalacaoAsync(arquivo);

            Assert.Equal(ConfiguracoesEstado.AguardandoInstalacao, vm.Estado);
            Assert.Equal(arquivo.CaminhoArquivo, vm.CaminhoArquivoBaixado);
            Assert.True(File.Exists(arquivo.CaminhoArquivo), "prompt recusado deveria MANTER o arquivo baixado");
            // T3 (Plano 18): mensagem enxuta da confirmação final, com a nota discreta do UAC embutida.
            Assert.Contains("Fechar e atualizar", confirm.Messages[0]);
            Assert.Contains("permissão do Windows", confirm.Messages[0]);
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

            Assert.Equal(ConfiguracoesEstado.AguardandoInstalacao, vm.Estado);
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
            Assert.Equal(ConfiguracoesEstado.Erro, vm.Estado);
            Assert.NotNull(vm.MensagemErro);
            // T3 (Plano 18): mensagem enxuta ("Não foi possível iniciar a atualização.") com o caminho do
            // arquivo verificado como FALLBACK -- mesma disciplina de hoje, só o texto encurtado.
            Assert.StartsWith("Não foi possível iniciar a atualização.", vm.MensagemErro);
            Assert.Contains(arquivo.CaminhoArquivo, vm.MensagemErro); // caminho do arquivo verificado SURGE na mensagem
            Assert.Equal(arquivo.CaminhoArquivo, vm.CaminhoArquivoBaixado); // e também na propriedade dedicada
            Assert.True(File.Exists(arquivo.CaminhoArquivo), "o arquivo verificado NUNCA deveria ser apagado por essa falha");
        }
        finally { TryDelete(arquivo.CaminhoArquivo); }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    // ---- "Nitidez extra do texto" -- toggle no Configurações, persistência + re-render -----------------

    [Fact] // default OFF: sem config.json ainda -> NitidezExtra false (mesma disciplina "default seguro"
    // de AppConfig.NitidezExtra, refletida aqui no VM que a hospeda).
    public void NitidezExtra_DefaultsToFalse_WhenConfigHasNoFileYet()
    {
        var (_, config) = TempConfig();
        var vm = BuildVm(config: config);
        Assert.False(vm.NitidezExtra);
    }

    [Fact] // VM lê o estado JÁ PERSISTIDO na config no momento da construção -- reabrir o diálogo depois
    // de já ter ligado o toggle numa sessão anterior mostra o estado real, não um default hardcoded.
    public void NitidezExtra_InitializesFromPersistedConfig_WhenAlreadyOn()
    {
        var (_, config) = TempConfig();
        config.NitidezExtra = true;

        var vm = BuildVm(config: config);

        Assert.True(vm.NitidezExtra);
    }

    [Fact] // construir o VM NÃO grava nada na config nem chama o callback de re-render -- só LÊ o estado
    // inicial (prova de que OnNitidezExtraChanged não dispara espontaneamente na inicialização do campo).
    public void Construir_NaoGravaConfigNemChamaCallbackDeRerender()
    {
        var (_, config) = TempConfig();
        var callbackChamado = false;

        _ = BuildVm(config: config, applySupersampleFactor: _ => callbackChamado = true);

        Assert.False(callbackChamado);
        Assert.False(config.NitidezExtra); // config não foi tocada -- continua no default
    }

    [Fact] // ligar o toggle -> persiste TRUE na MESMA AppConfig (visível numa 2ª instância) e chama o
    // callback com o fator de produção (2.0, NÃO 1.5 -- ver DocumentViewModel.NitidezExtraSupersampleFactor).
    public void SetNitidezExtraTrue_PersistsToConfig_AndAppliesFactorTwo()
    {
        var (dir, config) = TempConfig();
        double? fatorAplicado = null;
        var vm = BuildVm(config: config, applySupersampleFactor: f => fatorAplicado = f);

        vm.NitidezExtra = true;

        Assert.Equal(2.0, fatorAplicado);
        var config2 = new AppConfig(dir);
        Assert.True(config2.NitidezExtra);
    }

    [Fact] // desligar de volta -> persiste FALSE e o callback recebe 1.0 (comportamento de hoje, off).
    public void SetNitidezExtraFalse_PersistsToConfig_AndAppliesFactorOne()
    {
        var (dir, config) = TempConfig();
        config.NitidezExtra = true;
        double? fatorAplicado = null;
        var vm = BuildVm(config: config, applySupersampleFactor: f => fatorAplicado = f);
        Assert.True(vm.NitidezExtra); // pré-condição: começa ligado

        vm.NitidezExtra = false;

        Assert.Equal(1.0, fatorAplicado);
        Assert.False(new AppConfig(dir).NitidezExtra);
    }

    [Fact] // liga -> desliga -> liga de novo: garante que o toggle não fica "preso" na 1ª mudança (mesmo
    // espírito de AppConfigTests.CriarBackup_CanBeToggledBackAndForth), e que o callback é chamado 1x por
    // mudança (nunca 0, nunca 2).
    public void NitidezExtra_CanBeToggledBackAndForth_CallbackFiresOncePerChange()
    {
        var (_, config) = TempConfig();
        var chamadas = new List<double>();
        var vm = BuildVm(config: config, applySupersampleFactor: chamadas.Add);

        vm.NitidezExtra = true;
        vm.NitidezExtra = false;
        vm.NitidezExtra = true;

        Assert.Equal(new[] { 2.0, 1.0, 2.0 }, chamadas);
    }

    [Fact] // omitir applySupersampleFactor (default de teste) -> ligar o toggle NÃO lança -- mesma
    // disciplina de risco de _logCrash (no-op seguro, nunca uma exceção de "callback obrigatório esquecido").
    public void NitidezExtra_ApplySupersampleFactorOmitted_TogglingDoesNotThrow()
    {
        var (_, config) = TempConfig();
        var vm = BuildVm(config: config); // applySupersampleFactor OMITIDO

        var ex = Record.Exception(() => vm.NitidezExtra = true);

        Assert.Null(ex);
    }

    // ---- toggle de tema (TemaEscuro) -- mesma disciplina de NitidezExtra acima --------------------------

    [Fact] // default: sem config.json ainda -> ThemeMode.Escuro -> o toggle nasce MARCADO (tema escuro).
    public void TemaEscuro_DefaultsToTrue_WhenConfigHasNoFileYet()
    {
        var (_, config) = TempConfig();
        var vm = BuildVm(config: config);
        Assert.True(vm.TemaEscuro);
    }

    [Fact] // inicializa do config PERSISTIDO: se o usuário deixou Claro, o toggle abre DESMARCADO.
    public void TemaEscuro_InitializesFromPersistedConfig_WhenClaro()
    {
        var (dir, config) = TempConfig();
        config.ThemeMode = ThemeMode.Claro;

        var vm = BuildVm(config: new AppConfig(dir));
        Assert.False(vm.TemaEscuro);
    }

    [Fact] // construir o VM NÃO dispara OnTemaEscuroChanged (o estado inicial é lido no campo, não setado).
    public void Construir_NaoAplicaTema_NemToca_Config()
    {
        var (_, config) = TempConfig();
        bool callbackChamado = false;

        _ = BuildVm(config: config, aplicarTema: _ => callbackChamado = true);

        Assert.False(callbackChamado);
        Assert.Equal(ThemeMode.Escuro, config.ThemeMode); // default, não regravado
    }

    [Fact] // desmarcar -> persiste Claro E aplica Claro ao vivo (callback recebe o modo).
    public void SetTemaEscuroFalse_PersistsClaro_AndAppliesClaro()
    {
        var (dir, config) = TempConfig();
        ThemeMode? aplicado = null;
        var vm = BuildVm(config: config, aplicarTema: m => aplicado = m);

        vm.TemaEscuro = false;

        Assert.Equal(ThemeMode.Claro, aplicado);
        Assert.Equal(ThemeMode.Claro, new AppConfig(dir).ThemeMode);
    }

    [Fact] // marcar de volta -> persiste Escuro E aplica Escuro.
    public void SetTemaEscuroTrue_PersistsEscuro_AndAppliesEscuro()
    {
        var (dir, config) = TempConfig();
        config.ThemeMode = ThemeMode.Claro;
        ThemeMode? aplicado = null;
        var vm = BuildVm(config: new AppConfig(dir), aplicarTema: m => aplicado = m);
        Assert.False(vm.TemaEscuro); // pré-condição

        vm.TemaEscuro = true;

        Assert.Equal(ThemeMode.Escuro, aplicado);
        Assert.Equal(ThemeMode.Escuro, new AppConfig(dir).ThemeMode);
    }

    [Fact] // omitir aplicarTema (default de teste) -> alternar NÃO lança (no-op seguro, como _logCrash).
    public void TemaEscuro_AplicarTemaOmitido_TogglingDoesNotThrow()
    {
        var (_, config) = TempConfig();
        var vm = BuildVm(config: config); // aplicarTema OMITIDO

        var ex = Record.Exception(() => vm.TemaEscuro = false);

        Assert.Null(ex);
    }

    // ---- posição do menu de anotação (MenuAnotacaoNaBarraLateral) -- mesma disciplina do TemaEscuro acima

    [Fact] // default: sem config.json ainda -> Flutuante -> a opção nasce DESMARCADA (pílula flutuante).
    public void MenuAnotacao_DefaultsToFlutuante_WhenConfigHasNoFileYet()
    {
        var (_, config) = TempConfig();
        var vm = BuildVm(config: config);
        Assert.False(vm.MenuAnotacaoNaBarraLateral);
    }

    [Fact] // inicializa do config PERSISTIDO: se o usuário deixou BarraLateral, a opção abre MARCADA.
    public void MenuAnotacao_InitializesFromPersistedConfig_WhenBarraLateral()
    {
        var (dir, config) = TempConfig();
        config.PosicaoMenuAnotacao = PosicaoMenuAnotacao.BarraLateral;

        var vm = BuildVm(config: new AppConfig(dir));
        Assert.True(vm.MenuAnotacaoNaBarraLateral);
    }

    [Fact] // construir o VM NÃO dispara OnMenuAnotacaoNaBarraLateralChanged (estado inicial lido no campo).
    public void Construir_NaoAplicaPosicaoMenu_NemToca_Config()
    {
        var (_, config) = TempConfig();
        bool callbackChamado = false;

        _ = BuildVm(config: config, aplicarPosicaoMenuAnotacao: _ => callbackChamado = true);

        Assert.False(callbackChamado);
        Assert.Equal(PosicaoMenuAnotacao.Flutuante, config.PosicaoMenuAnotacao); // default, não regravado
    }

    [Fact] // marcar -> persiste BarraLateral E aplica ao vivo (callback recebe a posição).
    public void SetMenuAnotacaoTrue_PersistsBarraLateral_AndAppliesBarraLateral()
    {
        var (dir, config) = TempConfig();
        PosicaoMenuAnotacao? aplicada = null;
        var vm = BuildVm(config: config, aplicarPosicaoMenuAnotacao: p => aplicada = p);

        vm.MenuAnotacaoNaBarraLateral = true;

        Assert.Equal(PosicaoMenuAnotacao.BarraLateral, aplicada);
        Assert.Equal(PosicaoMenuAnotacao.BarraLateral, new AppConfig(dir).PosicaoMenuAnotacao);
    }

    [Fact] // desmarcar de volta -> persiste Flutuante E aplica Flutuante.
    public void SetMenuAnotacaoFalse_PersistsFlutuante_AndAppliesFlutuante()
    {
        var (dir, config) = TempConfig();
        config.PosicaoMenuAnotacao = PosicaoMenuAnotacao.BarraLateral;
        PosicaoMenuAnotacao? aplicada = null;
        var vm = BuildVm(config: new AppConfig(dir), aplicarPosicaoMenuAnotacao: p => aplicada = p);
        Assert.True(vm.MenuAnotacaoNaBarraLateral); // pré-condição

        vm.MenuAnotacaoNaBarraLateral = false;

        Assert.Equal(PosicaoMenuAnotacao.Flutuante, aplicada);
        Assert.Equal(PosicaoMenuAnotacao.Flutuante, new AppConfig(dir).PosicaoMenuAnotacao);
    }

    [Fact] // omitir aplicarPosicaoMenuAnotacao (default de teste) -> alternar NÃO lança (no-op seguro).
    public void MenuAnotacao_AplicarOmitido_TogglingDoesNotThrow()
    {
        var (_, config) = TempConfig();
        var vm = BuildVm(config: config); // aplicarPosicaoMenuAnotacao OMITIDO

        var ex = Record.Exception(() => vm.MenuAnotacaoNaBarraLateral = true);

        Assert.Null(ex);
    }

    // ---- Plano 21: rubrica salva ------------------------------------------------------------------

    private static readonly byte[] RubricaPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    [Fact] // construir sem rubrica salva -> TemRubrica false, prévia null
    public void Rubrica_SemRubricaSalva_TemRubricaFalse()
    {
        var (_, config) = TempConfig();
        var vm = BuildVm(config: config);
        Assert.False(vm.TemRubrica);
        Assert.Null(vm.RubricaPreviewBytes);
    }

    [Fact] // construir com rubrica JÁ salva -> TemRubrica true refletido no estado inicial
    public void Rubrica_ComRubricaSalva_TemRubricaTrueNaConstrucao()
    {
        var (dir, config) = TempConfig();
        config.SalvarRubrica(RubricaPng);
        var vm = BuildVm(config: new AppConfig(dir));
        Assert.True(vm.TemRubrica);
        Assert.Equal(RubricaPng, vm.RubricaPreviewBytes);
    }

    [Fact] // EscolherRubrica: callback devolve bytes -> salva no config, TemRubrica vira true, persiste
    public void EscolherRubrica_ComBytes_SalvaEAtualizaEstado()
    {
        var (dir, config) = TempConfig();
        var vm = BuildVm(config: config, escolherRubricaImagem: () => RubricaPng);

        vm.EscolherRubricaCommand.Execute(null);

        Assert.True(vm.TemRubrica);
        Assert.Equal(RubricaPng, vm.RubricaPreviewBytes);
        Assert.True(new AppConfig(dir).TemRubrica); // persistiu em disco
    }

    [Fact] // EscolherRubrica: callback devolve null (cancelado/rejeitado) -> nada muda, sem rubrica salva
    public void EscolherRubrica_Cancelado_NaoSalvaNada()
    {
        var (dir, config) = TempConfig();
        var vm = BuildVm(config: config, escolherRubricaImagem: () => null);

        vm.EscolherRubricaCommand.Execute(null);

        Assert.False(vm.TemRubrica);
        Assert.False(new AppConfig(dir).TemRubrica);
    }

    [Fact] // RemoverRubrica: remove do config e zera o estado
    public void RemoverRubrica_ApagaEZeraEstado()
    {
        var (dir, config) = TempConfig();
        config.SalvarRubrica(RubricaPng);
        var vm = BuildVm(config: new AppConfig(dir));
        Assert.True(vm.TemRubrica); // pré-condição

        vm.RemoverRubricaCommand.Execute(null);

        Assert.False(vm.TemRubrica);
        Assert.Null(vm.RubricaPreviewBytes);
        Assert.False(new AppConfig(dir).TemRubrica);
    }

    [Fact] // omitir escolherRubricaImagem (default de teste) -> executar o comando NÃO lança (no-op seguro)
    public void EscolherRubrica_CallbackOmitido_NaoLanca()
    {
        var (_, config) = TempConfig();
        var vm = BuildVm(config: config); // escolherRubricaImagem OMITIDO

        var ex = Record.Exception(() => vm.EscolherRubricaCommand.Execute(null));

        Assert.Null(ex);
        Assert.False(vm.TemRubrica);
    }
}

file sealed class ThrowingUpdateSource : IUpdateSource
{
    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct) =>
        throw new HttpRequestException("sem rede neste teste");
}

// Minor (revisão) — controla manualmente QUANDO GetLatestAsync "termina", pra segurar o VM em
// ConfiguracoesEstado.Verificando tempo suficiente pro teste observar o CanExecute do comando nesse meio-tempo.
file sealed class BlockingUpdateSource(TaskCompletionSource<LatestRelease?> tcs) : IUpdateSource
{
    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct) => tcs.Task;
}
