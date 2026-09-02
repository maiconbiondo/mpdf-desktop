using System.IO;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

// Item 1 (revisão final pré-merge) — o helper de log É testável (diretório injetável, mesmo padrão de
// RecentFilesStoreTests); os 3 handlers globais em App.OnStartup NÃO são (ver doc XML lá).
public class CrashLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-crashlog-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact] // grava uma entrada timestampada com o ToString() completo da exceção (stack trace incluso —
    // é a ÚNICA fonte de diagnóstico pós-crash) no arquivo "erros.log" dentro do diretório injetado.
    public void Append_WritesTimestampedEntryWithExceptionDetails()
    {
        var ex = new InvalidOperationException("falha simulada de teste");

        CrashLog.Append(ex, _dir);

        var path = Path.Combine(_dir, "erros.log");
        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains("falha simulada de teste", content);
        Assert.Contains(nameof(InvalidOperationException), content);
        Assert.Contains(DateTime.Now.Year.ToString(), content); // timestamp presente
    }

    [Fact] // 2 chamadas -> 2 entradas (append, nunca overwrite) — cada crash é evidência própria, não
    // deve apagar o registro de um crash anterior na mesma sessão de app.
    public void Append_MultipleCalls_AppendsWithoutOverwriting()
    {
        CrashLog.Append(new Exception("primeiro"), _dir);
        CrashLog.Append(new Exception("segundo"), _dir);

        var content = File.ReadAllText(Path.Combine(_dir, "erros.log"));
        Assert.Contains("primeiro", content);
        Assert.Contains("segundo", content);
    }

    [Fact] // cria o diretório de log se ainda não existir (1ª execução do app na máquina) — mesmo
    // efeito colateral idempotente de RecentFilesStore/AppConfig.
    public void Append_CreatesDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(_dir));

        CrashLog.Append(new Exception("teste"), _dir);

        Assert.True(Directory.Exists(_dir));
    }

    [Fact] // logging NUNCA pode lançar (doc XML da classe) — um caminho que colide com um ARQUIVO
    // existente (Directory.CreateDirectory lança IOException) precisa ser engolido, não propagado: o
    // handler que chama isto (ex.: DispatcherUnhandledException) não pode ganhar uma 2ª exceção só por
    // tentar registrar a 1ª.
    public void Append_NeverThrows_EvenWhenDirectoryPathIsBlockedByAFile()
    {
        File.WriteAllText(_dir, "isto é um ARQUIVO, não um diretório"); // bloqueia CreateDirectory
        try
        {
            var thrown = Record.Exception(() => CrashLog.Append(new Exception("teste"), _dir));
            Assert.Null(thrown);
        }
        finally { File.Delete(_dir); }
    }

    [Fact] // DefaultDirectory/DefaultPath apontam pra %AppData%\mPDF\logs — mesma convenção de
    // RecentFilesStore.DefaultDirectory/AppConfig.DefaultDirectory (pasta "mPDF" em ApplicationData).
    public void DefaultDirectory_PointsUnderAppDataMPdfLogs()
    {
        Assert.EndsWith(Path.Combine("mPDF", "logs"), CrashLog.DefaultDirectory);
        Assert.Equal(Path.Combine(CrashLog.DefaultDirectory, "erros.log"), CrashLog.DefaultPath);
    }
}
