using System;
using System.IO;

namespace mPdf.App.Services;

/// Log de última linha de defesa (revisão final pré-merge, item 1) — chamado pelos 3 handlers globais
/// de exceção não tratada registrados em `App.OnStartup` (`DispatcherUnhandledException`/
/// `TaskScheduler.UnobservedTaskException`/`AppDomain.CurrentDomain.UnhandledException`). Escrever no
/// log NUNCA pode lançar — um erro ao tentar logar um erro não pode mascarar/piorar a falha original
/// nem derrubar um handler que está tentando manter o processo vivo (`DispatcherUnhandledException`,
/// `e.Handled = true`) ou, no caso do handler `terminating`, atrasar o encerramento do processo.
///
/// Mesmo padrão de diretório injetável já usado por `RecentFilesStore`/`AppConfig`: `DefaultDirectory`
/// estático para produção, overload `internal` testável direto contra um diretório TEMPORÁRIO (via
/// `InternalsVisibleTo("mPdf.App.Tests")`, já declarado em AssemblyInfo.cs), sem tocar o disco real da
/// máquina em teste.
public static class CrashLog
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mPDF", "logs");

    /// Caminho completo do arquivo de log em produção — usado na mensagem pt-BR mostrada ao usuário
    /// (`App.OnDispatcherUnhandledException`), pra que o resgate (C1, mesmo espírito de
    /// `DocumentSession.BuildFailureMessage`) tenha payoff prático: sem o caminho NA MENSAGEM, o
    /// usuário não tem como achar os detalhes registrados.
    public static string DefaultPath => Path.Combine(DefaultDirectory, "erros.log");

    /// Ponto de entrada de produção — grava em `DefaultDirectory` (%AppData%\mPDF\logs\erros.log).
    public static void Append(Exception ex) => Append(ex, DefaultDirectory);

    internal static void Append(Exception ex, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "erros.log");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}";
            File.AppendAllText(path, entry);
        }
        catch { /* logging nunca pode lançar — ver doc XML da classe */ }
    }
}
