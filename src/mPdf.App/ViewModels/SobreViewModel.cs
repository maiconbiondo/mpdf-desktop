using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mPdf.App.Services;

namespace mPdf.App.ViewModels;

/// Estado do diálogo "Sobre" (Task 2, Plano 11) — governa quais painéis a View mostra, mesmo espírito
/// de `BatchSignPhase` (`BatchSignViewModel`): a View nunca compara o enum diretamente no XAML, só os
/// bools computados abaixo (`IsOcioso`/`IsVerificando`/...), sem precisar de nenhum `IValueConverter`
/// novo.
public enum SobreEstado
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

/// VM da janela "Sobre" (Task 2, Plano 11) — hospeda versão/licenças + o fluxo inteiro de atualização
/// (verificar/baixar/instalar), mesmo padrão de `BatchSignViewModel`: um VM real e testável, a View só
/// hospeda como `DataContext` (ver `Views.SobreDialog`).
///
/// REDE SÓ POR CLIQUE (restrição estrutural do plano) — `UpdateService` só é construído DENTRO de
/// `VerificarAtualizacao` (o comando "Verificar atualização"), nunca no construtor deste VM nem em
/// qualquer caminho alcançável sem o usuário clicar explicitamente. `BaixarEInstalar` REUSA a MESMA
/// instância (campo `_service`) em vez de construir uma nova — por isso `new UpdateService(` aparece em
/// EXATAMENTE 1 lugar de todo `src/mPdf.App` (ver `UpdateNetworkConfinementTests.
/// UpdateServiceConstruction_OccursInExactlyOnePlace` — prova textual — e `SobreViewModelTests.
/// Construir_NaoInvocaFabricaDeFonte`/`VerificarAtualizacaoCommand_InvocaFabricaExatamenteUmaVezPorExecucao`
/// — prova COMPORTAMENTAL: a fábrica `_createSource` só é chamada dentro do comando, nunca ao construir
/// o VM).
///
/// INSTALAÇÃO reusa o fluxo de fechar documentos JÁ EXISTENTE (`confirmCloseAllDocuments`, produção:
/// `MainViewModel.ConfirmCloseAll` — MESMO prompt "salvar antes de fechar?" por documento sujo que
/// fechar a janela já usa, ver `MainWindow.OnClosing`) — nunca uma reimplementação paralela que poderia
/// divergir. `startInstaller`/`shutdown`/`confirmCloseAllDocuments` são parâmetros OBRIGATÓRIOS (sem
/// default `??`) — mesma disciplina de `BatchSignViewModel.pickFiles`/`isPathOpen`: uma omissão aqui não
/// pode silenciosamente iniciar um PROCESSO DE VERDADE nem encerrar a aplicação durante um teste headless
/// (risco pior que o hang de diálogo que a seam `UiPrompts` evita — o compilador já recusa qualquer
/// chamador, teste ou produção, que esqueça de passá-los). `createSource`/`confirmInstall` continuam
/// opcionais via `UiPrompts` (mesmo padrão de risco dos outros diálogos/serviços deste app — ver
/// `UiPrompts.CreateUpdateSource`/`CreateConfirmInstallUpdate`).
public sealed partial class SobreViewModel : ObservableObject
{
    private readonly Func<bool> _confirmCloseAllDocuments;
    private readonly Action<string> _startInstaller;
    private readonly Action _shutdown;
    private readonly Func<IUpdateSource> _createSource;
    private readonly IConfirmInstallUpdateService _confirmInstall;

    private UpdateService? _service;

    /// Versão atual, lida uma vez no construtor (sem rede — ver `UpdateService.CurrentVersionText`).
    public string VersaoAtual { get; } = UpdateService.CurrentVersionText();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BaixarEInstalarCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerificarAtualizacaoCommand))]
    private SobreEstado estado = SobreEstado.Ocioso;

    [ObservableProperty] private string? mensagemErro;
    [ObservableProperty] private UpdateInfo? atualizacaoDisponivel;
    [ObservableProperty] private long bytesBaixados;
    [ObservableProperty] private long bytesTotais;
    [ObservableProperty] private string? caminhoArquivoBaixado;

    public bool IsOcioso => Estado == SobreEstado.Ocioso;
    public bool IsVerificando => Estado == SobreEstado.Verificando;
    public bool IsAtualizado => Estado == SobreEstado.Atualizado;
    public bool IsErro => Estado == SobreEstado.Erro;
    public bool IsDisponivel => Estado == SobreEstado.Disponivel;
    public bool IsBaixando => Estado == SobreEstado.Baixando;
    public bool IsAguardandoInstalacao => Estado == SobreEstado.AguardandoInstalacao;

    /// "Verificar atualização" continua oferecido depois de um erro ou de já estar atualizado (permite
    /// tentar de novo) — só se esconde enquanto uma verificação/download já está em andamento ou um
    /// resultado que tem seu PRÓPRIO botão de ação (Disponível -> "Baixar e instalar") está em tela.
    public bool PodeVerificar => Estado is SobreEstado.Ocioso or SobreEstado.Atualizado or SobreEstado.Erro;

    private readonly Action<Exception> _logCrash;

    public SobreViewModel(
        Func<bool> confirmCloseAllDocuments,
        Action<string> startInstaller,
        Action shutdown,
        Func<IUpdateSource>? createSource = null,
        IConfirmInstallUpdateService? confirmInstall = null,
        Action<Exception>? logCrash = null)
    {
        _confirmCloseAllDocuments = confirmCloseAllDocuments;
        _startInstaller = startInstaller;
        _shutdown = shutdown;
        _createSource = createSource ?? UiPrompts.CreateUpdateSource;
        _confirmInstall = confirmInstall ?? UiPrompts.CreateConfirmInstallUpdate();
        // I3 (revisão de segurança): mesmo precedente de `SingleInstanceLaunchGate.ShouldContinueLaunch`
        // (App.xaml.cs) — `CrashLog.Append` passado como DELEGATE, não chamado direto — permite um teste
        // injetar um sink que NÃO escreve no %AppData% real da máquina (isolamento; `CrashLog.Append` em
        // si nunca lança, então não é risco de HANG, só de poluir estado real durante teste — mesma
        // classe de risco "isolamento, não trava suíte" já documentada pra `IPdfEditor`/`ISigningEngine`
        // neste codebase, por isso o default é opcional `??`, não obrigatório).
        _logCrash = logCrash ?? CrashLog.Append;
    }

    partial void OnEstadoChanged(SobreEstado value)
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

    // Minor (revisão): reentrância — sem este guard, cliques repetidos em "Verificar atualização"
    // enquanto uma checagem já está em voo (`Estado == Verificando`) disparariam `VerificarAtualizacao`
    // de novo, descartando (`_service?.Dispose()`) o `UpdateService`/`HttpClient` da chamada ANTERIOR
    // ainda em uso pelo `await _service.VerificarAsync()` dela — mesmo risco de corrida que
    // `CanBaixar`/`BaixarEInstalarCommand` já evita pro download (padrão espelhado aqui).
    private bool CanVerificar() => Estado != SobreEstado.Verificando;

    /// "Verificar atualização" — ÚNICO comando que constrói `UpdateService` (ver doc XML da classe). Um
    /// `_service` anterior (de uma verificação prévia nesta mesma sessão do diálogo) é descartado antes
    /// de trocar — evita acumular `HttpClient`s vivos a cada "tentar de novo".
    [RelayCommand(CanExecute = nameof(CanVerificar))]
    private async Task VerificarAtualizacao()
    {
        Estado = SobreEstado.Verificando;
        MensagemErro = null;
        AtualizacaoDisponivel = null;

        _service?.Dispose();
        _service = new UpdateService(_createSource()); // ÚNICO ponto de construção em todo o app
        var resultado = await _service.VerificarAsync();

        switch (resultado.Status)
        {
            case UpdateCheckStatus.Disponivel:
                AtualizacaoDisponivel = resultado.Info;
                Estado = SobreEstado.Disponivel;
                break;
            case UpdateCheckStatus.Atualizado:
                Estado = SobreEstado.Atualizado;
                break;
            case UpdateCheckStatus.Erro:
            default:
                MensagemErro = resultado.MensagemErro;
                Estado = SobreEstado.Erro;
                break;
        }
    }

    private bool CanBaixar() => Estado == SobreEstado.Disponivel;

    /// "Baixar e instalar" — reusa `_service` (construído por `VerificarAtualizacao`, nunca reconstruído
    /// aqui). Cancelamento genuíno (se algum dia uma UI de cancelar for oferecida) volta pro estado
    /// Disponível, oferecendo tentar de novo, sem tratar como erro.
    [RelayCommand(CanExecute = nameof(CanBaixar))]
    private async Task BaixarEInstalar()
    {
        if (_service is null || AtualizacaoDisponivel is not { } info) return;

        Estado = SobreEstado.Baixando;
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
            Estado = SobreEstado.Disponivel;
            return;
        }

        if (resultado.Status == DownloadStatus.Recusado)
        {
            MensagemErro = resultado.MensagemErro;
            Estado = SobreEstado.Erro;
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
    /// `SobreViewModelTests.ProsseguirComInstalacao_*`. Não-`async` (`Task` simples, sem `await` interno
    /// — todos os passos são síncronos): evita o warning CS1998 de um `async` sem `await` de verdade,
    /// mantendo a mesma assinatura `Task`-retornante que `BaixarEInstalar` já espera.
    internal Task ProsseguirComInstalacaoAsync(UpdateService.VerifiedUpdateFile arquivo)
    {
        if (!_confirmInstall.Confirm("Fechar o mPDF e instalar a atualização agora?"))
        {
            CaminhoArquivoBaixado = arquivo.CaminhoArquivo;
            Estado = SobreEstado.AguardandoInstalacao;
            return Task.CompletedTask;
        }

        if (!_confirmCloseAllDocuments())
        {
            CaminhoArquivoBaixado = arquivo.CaminhoArquivo;
            Estado = SobreEstado.AguardandoInstalacao;
            return Task.CompletedTask;
        }

        // I3 (revisão de segurança/robustez): `_startInstaller` (produção: `Process.Start`) pode lançar
        // por motivos fora do controle do app (antivírus bloqueando/colocando em quarentena, arquivo
        // removido entre a verificação e este ponto, permissão negada) — SEM este try/catch, a exceção
        // escapava do comando (`RelayCommand` síncrono aqui — nada a observar via
        // `TaskScheduler.UnobservedTaskException`, o revisor provou que esse handler nunca dispara) e o
        // VM ficava CONGELADO em `Baixando`/sem nenhuma mensagem, com um instalador VERIFICADO parado no
        // disco que o usuário não sabia que existia. `catch (Exception)` amplo é aceitável aqui
        // especificamente porque é uma chamada ISOLADA de I/O de SO (não esconde um bug de lógica deste
        // método — tudo ANTES já rodou; nada depois de `_startInstaller` faz parte deste `try`) e é
        // sempre registrado via `CrashLog.Append` antes de qualquer outra coisa, nunca engolido em
        // silêncio. `_shutdown()` NUNCA roda neste caminho — encerrar o app quando o instalador não
        // chegou a abrir deixaria o usuário sem app E sem instalador rodando.
        try
        {
            _startInstaller(arquivo.CaminhoArquivo);
        }
        catch (Exception ex)
        {
            _logCrash(ex);
            CaminhoArquivoBaixado = arquivo.CaminhoArquivo;
            MensagemErro = $"Não foi possível iniciar o instalador. O arquivo verificado está em: {arquivo.CaminhoArquivo}";
            Estado = SobreEstado.Erro;
            return Task.CompletedTask;
        }

        _shutdown();
        return Task.CompletedTask;
    }
}
