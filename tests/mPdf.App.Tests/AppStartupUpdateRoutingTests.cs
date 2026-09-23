using System.IO;
using Xunit;

namespace mPdf.App.Tests;

// App.xaml.cs nao roda headless (ver doc XML da classe / AppStartupPrintRoutingTests). Esta varredura
// prova que OnStartup roteia /checkupdate e /update via SilentUpdateRunner ANTES de criar o
// SingleInstanceService, e roda o runner em Task.Run (evita deadlock de dispatcher).
public class AppStartupUpdateRoutingTests
{
    private static string AppXamlCs()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "mPdf.App", "App.xaml.cs"));
    }

    [Fact] // OnStartup roteia /checkupdate e /update via SilentUpdateRunner
    public void RoteiaCheckEUpdate()
    {
        var src = AppXamlCs();
        Assert.Contains("/checkupdate", src);
        Assert.Contains("/update", src);
        Assert.Contains("SilentUpdateRunner", src);
    }

    [Fact] // o roteamento vem ANTES da instancia unica
    public void AntesDaInstanciaUnica()
    {
        var src = AppXamlCs();
        int iRunner = src.IndexOf("SilentUpdateRunner");
        int iSingle = src.IndexOf("new SingleInstanceService");
        Assert.True(iRunner >= 0 && iSingle >= 0 && iRunner < iSingle, "roteamento de update deve vir antes da instancia unica");
    }

    [Fact] // roda o runner em Task.Run (evita deadlock de dispatcher) — NA REGIAO do roteamento de update
    public void UsaTaskRun()
    {
        // "Task.Run" tambem aparece em comentarios pre-existentes DEPOIS da instancia unica; exigir que
        // ocorra ENTRE o roteamento de update (SilentUpdateRunner) e o new SingleInstanceService prova
        // que e o roteamento NOVO que usa Task.Run, nao uma mencao alheia.
        var src = AppXamlCs();
        int iRunner = src.IndexOf("SilentUpdateRunner");
        int iSingle = src.IndexOf("new SingleInstanceService");
        Assert.True(iRunner >= 0 && iSingle >= 0 && iRunner < iSingle, "roteamento de update deve preceder a instancia unica");
        int iTaskRun = src.IndexOf("Task.Run", iRunner);
        Assert.True(iTaskRun >= 0 && iTaskRun < iSingle,
            "o roteamento de update deve usar Task.Run ANTES da instancia unica (nao um Task.Run alheio depois)");
    }
}
