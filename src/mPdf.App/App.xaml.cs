using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using mPdf.App.Services;

namespace mPdf.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Obs 21 (revisão final pré-merge) — helper PURO/testável que decide taxa+reentrância; ver doc XML
    // de CrashDialogGate pros 3 eixos. 1 instância por processo (mesma vida do handler registrado
    // abaixo), nunca recriada entre exceções.
    private readonly CrashDialogGate _crashDialogGate = new();

    // Item 1 (revisão final pré-merge) — 3 redes de ÚLTIMA LINHA contra exceção não tratada, uma por
    // ORIGEM possível num app WPF:
    //   (a) DispatcherUnhandledException — a thread de UI; a mais provável na prática (ex.: um catch
    //       tipado que a revisão não cobriu escapando de um RelayCommand `async void`). Tenta
    //       CONTINUAR (`e.Handled = true`) — deixar propagar mata o processo incondicionalmente, e um
    //       erro de UI isolado não deveria custar um documento inteiro (talvez ainda não salvo).
    //   (b) TaskScheduler.UnobservedTaskException — uma Task fire-and-forget cuja falha ninguém
    //       observou; sem `SetObserved()`, o finalizer relança na thread de finalização, derrubando o
    //       processo de um jeito imprevisível e sem contexto nenhum.
    //   (c) AppDomain.CurrentDomain.UnhandledException — qualquer exceção não tratada em QUALQUER
    //       thread que o CLR já decidiu usar para MATAR o processo (terminating): não há como
    //       continuar nem mostrar UI de forma confiável aqui, só logar best-effort antes do fim.
    // `CrashLog.Append` é a ÚNICA responsável por escrever no disco (nunca lança — ver doc XML lá),
    // então nenhum dos 3 handlers abaixo precisa do próprio try/catch pra proteger o handler em si.
    //
    // NÃO TESTÁVEL headless por natureza: os 3 disparam de dentro do laço de mensagens do WPF (ou de um
    // finalizer/terminação de processo) — não há como provocar `DispatcherUnhandledException` de verdade
    // sem um `Dispatcher.Run()` bombeando a fila (só `ViewerIntegrationTests`/`PrintServiceTests` rodam
    // numa thread STA dedicada assim, e mesmo lá não há um jeito limpo de forçar a exceção ESCAPAR do
    // command até o Dispatcher sem simular o próprio bug que o handler existe para pegar), e um teste
    // não pode provocar `AppDomain.UnhandledException` (terminating) sem matar o PROCESSO DE TESTE
    // inteiro. O que É testável — e testado (`CrashLogTests`) — é o helper de log que os 3 chamam.

    // Task 1 (Plano 6): instância única — ver doc XML de ISingleInstanceService/SingleInstanceService
    // pro protocolo completo (mutex nomeado + pipe nomeado, endurecido). Fica vivo pela vida inteira
    // do PROCESSO primário (Dispose só em OnExit); numa instância SECUNDÁRIA o campo é descartado
    // (Shutdown chamado logo em seguida, ver OnStartup) sem nunca precisar sobreviver além disto.
    private SingleInstanceService? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Task 2 (Plano 6, associação .pdf): args[0] é o caminho que o Windows passa ao abrir um .pdf
        // associado a este .exe (ou uma linha de comando manual) — só aceito se realmente parecer um
        // caminho de PDF; qualquer outro argumento em args[0] é ignorado (nunca tenta abrir algo que
        // claramente não é um .pdf).
        string? argPath = e.Args.Length > 0 && e.Args[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? e.Args[0]
            : null;

        // Instância única ANTES de qualquer outra coisa: se o mutex nomeado já está tomado, esta é uma
        // instância SECUNDÁRIA — `TryAcquire` já encaminha `argPath` (se houver) pro pipe da PRIMÁRIA
        // por dentro. `Shutdown()` chamado AQUI, ANTES de `base.OnStartup(e)`, impede a janela do
        // `StartupUri` de sequer ser criada: o framework só materializa essa janela (`DoStartup`,
        // interno à `Application`) DEPOIS que este `OnStartup` retornar, checando se a aplicação já não
        // está encerrando — `Shutdown()` seta essa flag antes, então nenhuma janela chega a piscar na
        // tela pra uma instância secundária (decisão registrada/fonte no relatório da Task 1).
        _singleInstance = new SingleInstanceService(SingleInstanceNames.MutexName, SingleInstanceNames.PipeName);

        // Item 3 (revisão pós-Task 1): assina PathReceived ANTES de TryAcquire, não depois. TryAcquire
        // já dispara o Task.Run do listener quando vira primária (ver SingleInstanceService) — entre
        // aquele Task.Run começar e uma linha ANTIGA (embaixo, depois de base.OnStartup) assinar o
        // evento, uma 2ª instância que conectasse EXATAMENTE nessa janela minúscula teria seu caminho
        // silenciosamente perdido (PathReceived disparando sem nenhum assinante). Assinar primeiro
        // fecha essa janela — inofensivo pro caso comum (instância PRIMÁRIA de verdade, listener nem
        // começou a aceitar ainda) e pro caso SECUNDÁRIO (TryAcquire abaixo nunca inicia listener
        // nenhum, então PathReceived nunca dispara nesta instância de qualquer forma).
        _singleInstance.PathReceived += OnPathReceivedFromOtherInstance;

        // Item 1 (revisão pós-Task 1): instância única é BEST-EFFORT — uma falha em TryAcquire (ex.:
        // mutex nomeado já existindo com um tipo de handle incompatível, ou permissão negada) NUNCA
        // pode impedir o app de abrir. `ShouldContinueLaunch` captura qualquer exceção, registra em
        // CrashLog (chamável a qualquer momento, mesmo ANTES dos 3 handlers de crash abaixo serem
        // registrados — ver doc XML da classe) e devolve true — falha ABERTA: segue como lançamento
        // normal (só perde a proteção de instância única NESTA sessão).
        if (!SingleInstanceLaunchGate.ShouldContinueLaunch(_singleInstance, argPath, CrashLog.Append))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        if (argPath is not null)
        {
            // `MainWindow` (do `StartupUri`) ainda NÃO existe neste ponto — só é criada pelo PRÓPRIO
            // framework DEPOIS que este `OnStartup` retornar (mesmo mecanismo de `DoStartup` citado
            // acima). `BeginInvoke` com prioridade `ContextIdle` (mais baixa que `Loaded`/`Render`/
            // `Normal`/`Send`) agenda a continuação pra só rodar DEPOIS que o dispatcher já processou
            // tudo que tem prioridade maior — inclusive a criação/exibição da janela. Único hook
            // ASSÍNCRONO já disponível aqui (Obs 17: nunca fire-and-forget cego dentro de um
            // construtor) — isto É o fluxo correto, só adiado até a janela existir; a checagem
            // `IsLoaded`/`Loaded` dentro de `OpenExternalPathWhenWindowReady` é defesa adicional, não
            // depende cegamente da prioridade do dispatcher pra provar que a janela já carregou.
            Dispatcher.BeginInvoke(new Action(() => OpenExternalPathWhenWindowReady(argPath)), DispatcherPriority.ContextIdle);
        }
    }

    /// Roda depois que o `DoStartup` interno do framework já criou e mostrou a `MainWindow` do
    /// `StartupUri` (ver comentário em `OnStartup`) — mesmo assim confere `null`/tipo e `IsLoaded` por
    /// defesa, nunca assume a garantia de prioridade do dispatcher cegamente.
    private void OpenExternalPathWhenWindowReady(string path)
    {
        if (Application.Current.MainWindow is not mPdf.App.MainWindow mw) return; // defensivo: não deveria faltar aqui
        if (mw.IsLoaded) { _ = mw.ViewModel.OpenPath(path); return; }
        mw.Loaded += OnceLoaded;
        void OnceLoaded(object sender, RoutedEventArgs args)
        {
            mw.Loaded -= OnceLoaded;
            _ = mw.ViewModel.OpenPath(path);
        }
    }

    /// Uma instância SECUNDÁRIA nova chegou (usuário abriu outro .pdf com o app já rodando) — abre
    /// numa aba NOVA (dedupe de `OpenPath` cobre reabrir o mesmo caminho) e tenta trazer a janela pra
    /// frente. Dispara na thread de BACKGROUND do listener do pipe — `BeginInvoke` faz o marshal pra
    /// UI antes de `HandleExternalPathAsync` tocar em `MainWindow`/`ViewModel`.
    private void OnPathReceivedFromOtherInstance(string path) =>
        Dispatcher.BeginInvoke(new Action(() => _ = HandleExternalPathAsync(path)));

    private async Task HandleExternalPathAsync(string path)
    {
        if (Application.Current.MainWindow is not mPdf.App.MainWindow mw) return;
        await mw.ViewModel.OpenPath(path);
        if (mw.WindowState == WindowState.Minimized) mw.WindowState = WindowState.Normal;
        // Limite do SO (documentado no brief da Task 1): o Windows pode recusar o foreground lock e só
        // piscar a barra de tarefas em vez de trazer a janela pra frente de verdade — aceito, mesmo
        // comportamento de outros leitores de PDF nesse cenário.
        mw.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Rede (a) — ver doc XML da classe. `CrashLog.Append` roda SEMPRE, incondicionalmente (cada
    /// exceção é evidência própria, mesmo quando o `MessageBox` abaixo não aparece). O `MessageBox` em
    /// si passa por <see cref="CrashDialogGate"/> primeiro — Obs 21, 3 eixos:
    ///   TAXA: mais de <see cref="CrashDialogGate.MaxDialogsPerWindow"/> caixas em
    ///     <see cref="CrashDialogGate.Window"/> (10s) e o gate para de aprovar caixas novas (só log) —
    ///     protege contra um timer/render-loop que falha em cada tick virando um loop infinito de
    ///     modais empilhados.
    ///   REENTRÂNCIA: `MessageBox.Show` bombeia a fila do Dispatcher enquanto está aberto — uma 2ª
    ///     exceção pode chegar (reentrante DE VERDADE) antes da 1ª caixa fechar; o gate devolve `false`
    ///     incondicionalmente nesse caso, nunca uma 2ª caixa sobreposta.
    ///   RESÍDUO: `Exit()` no `finally` garante que o gate nunca fica travado em "mostrando" mesmo se
    ///     `MessageBox.Show` lançar; a janela deslizante de timestamps se poda sozinha a cada chamada.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Append(e.Exception);
        if (_crashDialogGate.TryEnter(DateTimeOffset.UtcNow))
        {
            try
            {
                MessageBox.Show(
                    $"Ocorreu um erro inesperado. O aplicativo tentará continuar. Detalhes registrados em: {CrashLog.DefaultPath}",
                    "mPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _crashDialogGate.Exit(); }
        }
        e.Handled = true; // tenta continuar — ver doc XML acima
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Append(e.Exception);
        e.SetObserved(); // evita que o finalizer relance numa thread sem contexto nenhum
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // TERMINATING: o processo já está morrendo (e.IsTerminating quase sempre true aqui) — só
        // logging best-effort, protegido pelo próprio CrashLog.Append (ver doc XML da classe).
        if (e.ExceptionObject is Exception ex) CrashLog.Append(ex);
    }
}

