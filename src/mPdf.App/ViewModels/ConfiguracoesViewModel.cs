using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mPdf.App.Services;
using mPdf.Documents;

namespace mPdf.App.ViewModels;

/// Estado do diálogo "Configurações" (Task 2, Plano 17) — governa quais painéis do bloco de atualização
/// a View mostra, mesmo espírito de `SobreEstado`/`BatchSignPhase`: a View nunca compara o enum
/// diretamente no XAML, só os bools computados abaixo (`IsOcioso`/`IsVerificando`/...), sem precisar de
/// nenhum `IValueConverter` novo.
public enum ConfiguracoesEstado
{
    Ocioso,
    Verificando,
    Atualizado,
    Erro,
    Disponivel,
    Baixando,
    /// Download verificado (hash batido) mas a instalação foi ADIADA — usuário recusou o prompt "fechar
    /// e instalar agora?" ou cancelou a resolução de documentos sujos. O arquivo baixado é MANTIDO no
    /// disco (nunca apagado só por adiar) e `CaminhoArquivoBaixado` mostra onde.
    AguardandoInstalacao,
}

/// VM do diálogo "Configurações" (Task 2, Plano 17) — MIGRADO de `SobreViewModel` (Task 2, Plano 11 +
/// Plano 13 + Plano 14): hospeda os 3 controles que saíram do "Sobre" — Tema (claro/escuro), Nitidez
/// extra do texto, e o fluxo inteiro de atualização (verificar/baixar/instalar). O "Sobre" fica só com
/// informações do app (versão/licença/links) — ver `SobreViewModel`, agora reduzido a `VersaoAtual`.
///
/// TODA A LÓGICA é a MESMA de antes (nenhum comportamento novo, só mudou de VM/janela) — ver histórico
/// de decisões nos comentários originais de `SobreViewModel` (Planos 11/13/14) pra contexto de cada
/// membro abaixo.
///
/// REDE SÓ POR CLIQUE (restrição estrutural, preservada da migração) — `UpdateService` só é construído
/// DENTRO de `VerificarAtualizacao` (o comando "Verificar atualização"), nunca no construtor deste VM
/// nem em qualquer caminho alcançável sem o usuário clicar explicitamente. `BaixarEInstalar` REUSA a
/// MESMA instância (campo `_service`) em vez de construir uma nova — por isso `new UpdateService(`
/// aparece em EXATAMENTE 1 lugar de todo `src/mPdf.App` (ver
/// `UpdateNetworkConfinementTests.UpdateServiceConstruction_OccursInExactlyOnePlace` — prova textual — e
/// `ConfiguracoesViewModelTests.Construir_NaoInvocaFabricaDeFonte`/
/// `VerificarAtualizacaoCommand_InvocaFabricaExatamenteUmaVezPorExecucao` — prova COMPORTAMENTAL).
///
/// INSTALAÇÃO reusa o fluxo de fechar documentos JÁ EXISTENTE (`confirmCloseAllDocuments`, produção:
/// `MainViewModel.ConfirmCloseAll`) — nunca uma reimplementação paralela que poderia divergir.
/// `startInstaller`/`shutdown`/`confirmCloseAllDocuments` são parâmetros OBRIGATÓRIOS (sem default
/// `??`) — mesma disciplina de `BatchSignViewModel.pickFiles`/`isPathOpen`: uma omissão aqui não pode
/// silenciosamente iniciar um PROCESSO DE VERDADE nem encerrar a aplicação durante um teste headless.
/// `createSource`/`confirmInstall` continuam opcionais via `UiPrompts` (mesmo padrão de risco dos
/// outros diálogos/serviços deste app — ver `UiPrompts.CreateUpdateSource`/`CreateConfirmInstallUpdate`).
public sealed partial class ConfiguracoesViewModel : ObservableObject
{
    private readonly Func<bool> _confirmCloseAllDocuments;
    private readonly Action<string> _startInstaller;
    private readonly Action _shutdown;
    private readonly Func<IUpdateSource> _createSource;
    private readonly IConfirmInstallUpdateService _confirmInstall;
    // AppConfig injetável (mesmo padrão de MainViewModel/DocumentViewModel — testes usam um diretório
    // temporário próprio, nunca tocam %AppData%\mPDF real) e um callback OPCIONAL que o chamador
    // (produção: `MainViewModel.Configuracoes`) usa pra re-renderizar todos os documentos JÁ ABERTOS
    // quando o toggle muda — este VM não conhece `DocumentViewModel`/`Documents` (ficaria acoplado a
    // MainViewModel), só chama o delegate. `null` (default de teste) = nenhum documento pra atualizar,
    // comportamento seguro pra qualquer teste que não passe o callback.
    private readonly AppConfig _config;
    private readonly Action<double> _applySupersampleFactor;
    // Callback que APLICA o tema ao vivo (produção: ThemeService.AplicarNoApp, que troca o dicionário
    // de tokens em Application.Resources). Opcional com no-op seguro — mesma classe de risco de
    // `_applySupersampleFactor` (um teste que não injeta nada nunca toca Application.Current).
    private readonly Action<ThemeMode> _aplicarTema;
    // Callback que APLICA a posição do menu de anotação AO VIVO (produção: seta
    // `MainViewModel.MenuAnotacaoNaBarraLateral`, cuja mudança troca a Visibility da pílula flutuante e da
    // tira do rail via bindings). Opcional com no-op seguro — mesma classe de risco de `_aplicarTema`/
    // `_applySupersampleFactor` (um teste que não injeta nada nunca toca a MainViewModel/UI).
    private readonly Action<PosicaoMenuAnotacao> _aplicarPosicaoMenuAnotacao;

    private UpdateService? _service;

    /// Estado do toggle "Nitidez extra do texto" — inicializado da config PERSISTIDA
    /// (`AppConfig.NitidezExtra`) no construtor, então reabrir o diálogo sempre mostra o estado real,
    /// não um default hardcoded. `OnNitidezExtraChanged` abaixo persiste E propaga pro callback de
    /// re-render — mesmo padrão "1 propriedade observável, 1 partial void reage" já usado por
    /// `DocumentViewModel.SupersampleFactor`/`OnSupersampleFactorChanged`.
    [ObservableProperty] private bool nitidezExtra;

    /// Estado do toggle "Tema escuro" — inicializado da config PERSISTIDA (`AppConfig.ThemeMode`) no
    /// construtor. Marcado = Escuro (o default v2.0); desmarcado = Claro. `OnTemaEscuroChanged` persiste
    /// E aplica ao vivo — mesmo padrão de `NitidezExtra` acima.
    [ObservableProperty] private bool temaEscuro;

    /// Estado da opção "Menu de anotação na barra lateral" (Plano 17, Task 3) — inicializado da config
    /// PERSISTIDA (`AppConfig.PosicaoMenuAnotacao`) no construtor. Marcado = BarraLateral (tira vertical no
    /// rail); desmarcado = Flutuante (a pílula do centro-inferior, padrão). `OnMenuAnotacaoNaBarraLateralChanged`
    /// persiste E aplica ao vivo — mesmo padrão de `TemaEscuro`/`NitidezExtra` acima.
    [ObservableProperty] private bool menuAnotacaoNaBarraLateral;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BaixarEInstalarCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerificarAtualizacaoCommand))]
    private ConfiguracoesEstado estado = ConfiguracoesEstado.Ocioso;

    [ObservableProperty] private string? mensagemErro;
    [ObservableProperty] private UpdateInfo? atualizacaoDisponivel;
    [ObservableProperty] private long bytesBaixados;
    [ObservableProperty] private long bytesTotais;
    [ObservableProperty] private string? caminhoArquivoBaixado;

    public bool IsOcioso => Estado == ConfiguracoesEstado.Ocioso;
    public bool IsVerificando => Estado == ConfiguracoesEstado.Verificando;
    public bool IsAtualizado => Estado == ConfiguracoesEstado.Atualizado;
    public bool IsErro => Estado == ConfiguracoesEstado.Erro;
    public bool IsDisponivel => Estado == ConfiguracoesEstado.Disponivel;
    public bool IsBaixando => Estado == ConfiguracoesEstado.Baixando;
    public bool IsAguardandoInstalacao => Estado == ConfiguracoesEstado.AguardandoInstalacao;

    /// "Verificar atualização" continua oferecido depois de um erro ou de já estar atualizado (permite
    /// tentar de novo) — só se esconde enquanto uma verificação/download já está em andamento ou um
    /// resultado que tem seu PRÓPRIO botão de ação (Disponível -> "Baixar e instalar") está em tela.
    public bool PodeVerificar => Estado is ConfiguracoesEstado.Ocioso or ConfiguracoesEstado.Atualizado or ConfiguracoesEstado.Erro;

    private readonly Action<Exception> _logCrash;

    public ConfiguracoesViewModel(
        Func<bool> confirmCloseAllDocuments,
        Action<string> startInstaller,
        Action shutdown,
        Func<IUpdateSource>? createSource = null,
        IConfirmInstallUpdateService? confirmInstall = null,
        Action<Exception>? logCrash = null,
        AppConfig? config = null,
        Action<double>? applySupersampleFactor = null,
        Action<ThemeMode>? aplicarTema = null,
        Action<PosicaoMenuAnotacao>? aplicarPosicaoMenuAnotacao = null)
    {
        _confirmCloseAllDocuments = confirmCloseAllDocuments;
        _startInstaller = startInstaller;
        _shutdown = shutdown;
        _createSource = createSource ?? UiPrompts.CreateUpdateSource;
        _confirmInstall = confirmInstall ?? UiPrompts.CreateConfirmInstallUpdate();
        // I3 (revisão de segurança, herdada da migração): mesmo precedente de
        // `SingleInstanceLaunchGate.ShouldContinueLaunch` (App.xaml.cs) — `CrashLog.Append` passado como
        // DELEGATE, não chamado direto — permite um teste injetar um sink que NÃO escreve no %AppData%
        // real da máquina.
        _logCrash = logCrash ?? CrashLog.Append;
        _config = config ?? new AppConfig(AppConfig.DefaultDirectory);
        _applySupersampleFactor = applySupersampleFactor ?? (_ => { });
        _aplicarTema = aplicarTema ?? (_ => { });
        _aplicarPosicaoMenuAnotacao = aplicarPosicaoMenuAnotacao ?? (_ => { });
        temaEscuro = _config.ThemeMode == ThemeMode.Escuro; // lido direto no campo -- NÃO dispara handler
        // Plano 17 (Task 3): estado inicial lido direto no campo -- NÃO dispara OnMenuAnotacaoNaBarraLateralChanged
        // (mesmo raciocínio de nitidezExtra/temaEscuro: é o estado INICIAL, não uma mudança de UI).
        menuAnotacaoNaBarraLateral = _config.PosicaoMenuAnotacao == PosicaoMenuAnotacao.BarraLateral;
        nitidezExtra = _config.NitidezExtra; // lido direto no campo -- NÃO dispara OnNitidezExtraChanged
        // (não é uma mudança de UI, é o estado INICIAL; disparar o handler aqui re-gravaria a config com
        // o MESMO valor que acabou de ser lido dela e chamaria o callback de re-render sem necessidade
        // nenhuma -- nenhum documento existe ainda quando este VM é construído por MainViewModel.Configuracoes).
    }

    partial void OnEstadoChanged(ConfiguracoesEstado value)
    {
        OnPropertyChanged(nameof(IsOcioso));
        OnPropertyChanged(nameof(IsVerificando));
        OnPropertyChanged(nameof(IsAtualizado));
        OnPropertyChanged(nameof(IsErro));
        OnPropertyChanged(nameof(IsDisponivel));
        OnPropertyChanged(nameof(IsBaixando));
        OnPropertyChanged(nameof(IsAguardandoInstalacao));
        OnPropertyChanged(nameof(PodeVerificar));
    }

    /// Toggle "Nitidez extra do texto" — persiste na config JÁ (não espera "Fechar") e re-renderiza todo
    /// documento ABERTO agora, via o callback do chamador. Mesmo padrão exato de
    /// `DocumentViewModel.OnSupersampleFactorChanged`.
    partial void OnNitidezExtraChanged(bool value)
    {
        _config.NitidezExtra = value;
        _applySupersampleFactor(value ? DocumentViewModel.NitidezExtraSupersampleFactor : 1.0);
    }

    /// Toggle "Tema escuro" — persiste na config JÁ (não espera "Fechar") e aplica o tema AO VIVO via o
    /// callback (ThemeService troca o dicionário de tokens; os {DynamicResource Cor.*} re-pintam a UI
    /// inteira sem reiniciar). Mesmo padrão de `OnNitidezExtraChanged`.
    partial void OnTemaEscuroChanged(bool value)
    {
        var modo = value ? ThemeMode.Escuro : ThemeMode.Claro;
        _config.ThemeMode = modo;
        _aplicarTema(modo);
    }

    /// Opção "Menu de anotação na barra lateral" (Plano 17, Task 3) — persiste na config JÁ (não espera
    /// "Fechar") e aplica AO VIVO via o callback (a MainViewModel troca a Visibility da pílula flutuante
    /// e da tira do rail, sem recriar a janela). Mesmo padrão de `OnTemaEscuroChanged`.
    partial void OnMenuAnotacaoNaBarraLateralChanged(bool value)
    {
        var pos = value ? PosicaoMenuAnotacao.BarraLateral : PosicaoMenuAnotacao.Flutuante;
        _config.PosicaoMenuAnotacao = pos;
        _aplicarPosicaoMenuAnotacao(pos);
    }

    // Minor (revisão): reentrância — sem este guard, cliques repetidos em "Verificar atualização"
    // enquanto uma checagem já está em voo (`Estado == Verificando`) disparariam `VerificarAtualizacao`
    // de novo, descartando (`_service?.Dispose()`) o `UpdateService`/`HttpClient` da chamada ANTERIOR
    // ainda em uso pelo `await _service.VerificarAsync()` dela — mesmo risco de corrida que
    // `CanBaixar`/`BaixarEInstalarCommand` já evita pro download (padrão espelhado aqui).
    private bool CanVerificar() => Estado != ConfiguracoesEstado.Verificando;

    /// "Verificar atualização" — ÚNICO comando que constrói `UpdateService` (ver doc XML da classe). Um
    /// `_service` anterior (de uma verificação prévia nesta mesma sessão do diálogo) é descartado antes
    /// de trocar — evita acumular `HttpClient`s vivos a cada "tentar de novo".
    [RelayCommand(CanExecute = nameof(CanVerificar))]
    private async Task VerificarAtualizacao()
    {
        Estado = ConfiguracoesEstado.Verificando;
        MensagemErro = null;
        AtualizacaoDisponivel = null;

        _service?.Dispose();
        _service = new UpdateService(_createSource()); // ÚNICO ponto de construção em todo o app
        var resultado = await _service.VerificarAsync();

        switch (resultado.Status)
        {
            case UpdateCheckStatus.Disponivel:
                AtualizacaoDisponivel = resultado.Info;
                Estado = ConfiguracoesEstado.Disponivel;
                break;
            case UpdateCheckStatus.Atualizado:
                Estado = ConfiguracoesEstado.Atualizado;
                break;
            case UpdateCheckStatus.Erro:
            default:
                MensagemErro = resultado.MensagemErro;
                Estado = ConfiguracoesEstado.Erro;
                break;
        }
    }

    private bool CanBaixar() => Estado == ConfiguracoesEstado.Disponivel;

    /// "Baixar e instalar" — reusa `_service` (construído por `VerificarAtualizacao`, nunca reconstruído
    /// aqui). Cancelamento genuíno (se algum dia uma UI de cancelar for oferecida) volta pro estado
    /// Disponível, oferecendo tentar de novo, sem tratar como erro.
    [RelayCommand(CanExecute = nameof(CanBaixar))]
    private async Task BaixarEInstalar()
    {
        if (_service is null || AtualizacaoDisponivel is not { } info) return;

        Estado = ConfiguracoesEstado.Baixando;
        BytesBaixados = 0;
        BytesTotais = info.TamanhoBytes;
        var progresso = new Progress<(long Baixados, long Total)>(p =>
        {
            BytesBaixados = p.Baixados;
            BytesTotais = p.Total;
        });

        DownloadResult resultado;
        try
        {
            resultado = await _service.BaixarEVerificarAsync(info, progresso, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Estado = ConfiguracoesEstado.Disponivel;
            return;
        }

        if (resultado.Status == DownloadStatus.Recusado)
        {
            MensagemErro = resultado.MensagemErro;
            Estado = ConfiguracoesEstado.Erro;
            return;
        }

        await ProsseguirComInstalacaoAsync(resultado.Arquivo!);
    }

    /// Contínuação pós-download: prompt "fechar e instalar agora?" -> resolver documentos sujos pelo
    /// fluxo JÁ EXISTENTE (`_confirmCloseAllDocuments`) -> iniciar o instalador -> encerrar o app, NESTA
    /// ORDEM (o instalador precisa estar rodando ANTES do processo atual encerrar). Qualquer recusa
    /// (prompt ou documento sujo não resolvido) mantém o arquivo já verificado no disco e avisa onde ele
    /// está — nunca apaga um download que já passou pela verificação de hash só porque a instalação foi
    /// adiada.
    ///
    /// `internal` (não `private`) de propósito: testável DIRETAMENTE com um `UpdateService.
    /// VerifiedUpdateFile` real (obtido pelo caminho legítimo `UpdateService.VerifyAndFinalize` sobre um
    /// arquivo real em disco), sem precisar de rede nenhuma para o passo de DOWNLOAD em si — ver
    /// `ConfiguracoesViewModelTests.ProsseguirComInstalacao_*`.
    internal Task ProsseguirComInstalacaoAsync(UpdateService.VerifiedUpdateFile arquivo)
    {
        if (!_confirmInstall.Confirm("Fechar o mPDF e instalar a atualização agora?"))
        {
            CaminhoArquivoBaixado = arquivo.CaminhoArquivo;
            Estado = ConfiguracoesEstado.AguardandoInstalacao;
            return Task.CompletedTask;
        }

        if (!_confirmCloseAllDocuments())
        {
            CaminhoArquivoBaixado = arquivo.CaminhoArquivo;
            Estado = ConfiguracoesEstado.AguardandoInstalacao;
            return Task.CompletedTask;
        }

        // I3 (revisão de segurança/robustez, herdada da migração): `_startInstaller` (produção:
        // `Process.Start`) pode lançar por motivos fora do controle do app — try/catch amplo aceitável
        // aqui pelos mesmos motivos documentados na versão original em `SobreViewModel`.
        try
        {
            _startInstaller(arquivo.CaminhoArquivo);
        }
        catch (Exception ex)
        {
            _logCrash(ex);
            CaminhoArquivoBaixado = arquivo.CaminhoArquivo;
            MensagemErro = $"Não foi possível iniciar o instalador. O arquivo verificado está em: {arquivo.CaminhoArquivo}";
            Estado = ConfiguracoesEstado.Erro;
            return Task.CompletedTask;
        }

        _shutdown();
        return Task.CompletedTask;
    }
}
