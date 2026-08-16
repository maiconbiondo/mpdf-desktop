namespace mPdf.App.Services;

/// Item 1 (revisão pós-Task 1, Plano 6): decide se o lançamento deve CONTINUAR (mostrar a janela
/// normalmente) ou SAIR (uma instância primária já existe e recebeu o caminho). Extraído de
/// App.xaml.cs pra ficar testável sem precisar de Application/Dispatcher/MainWindow reais —
/// App.xaml.cs em si continua NÃO testável headless (mesma categoria dos handlers de crash, ver doc
/// XML da classe App).
///
/// Instância única é BEST-EFFORT: uma falha em `TryAcquire` (ex.: `UnauthorizedAccessException`/
/// `WaitHandleCannotBeOpenedException` — um mutex NOMEADO já existindo como um tipo de handle
/// diferente, ou permissão negada por alguma política do ambiente) NUNCA pode impedir o app de abrir.
/// `ShouldContinueLaunch` captura qualquer exceção de `TryAcquire`, registra via `logError` (produção:
/// `CrashLog.Append`) e devolve `true` — falha ABERTA: o lançamento segue como se esta fosse uma
/// instância primária normal. Custo aceito do fail-open: esta sessão especificamente perde a proteção
/// de instância única (pode acabar com 2 janelas se a causa raiz persistir) — estritamente melhor que
/// recusar abrir o app por causa de um mecanismo auxiliar (instância única não é o motivo de existir
/// do mPDF).
public static class SingleInstanceLaunchGate
{
    public static bool ShouldContinueLaunch(ISingleInstanceService service, string? pathToForward, Action<Exception> logError)
    {
        try
        {
            return service.TryAcquire(pathToForward);
        }
        catch (Exception ex)
        {
            logError(ex);
            return true; // fail-open — ver doc XML da classe
        }
    }
}
