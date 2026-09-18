using System.IO;
using Xunit;

namespace mPdf.App.Tests;

// App.xaml.cs nao roda headless (ver doc XML da classe). Esta varredura prova que OnStartup roteia
// os modos de impressao ANTES de criar o SingleInstanceService, e chama Shutdown nesses modos.
public class AppStartupPrintRoutingTests
{
    private static string AppXamlCs()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "mPdf.App", "App.xaml.cs"));
    }

    [Fact] // OnStartup chama ImpressaoContextoService.Parse
    public void OnStartup_ChamaParse() => Assert.Contains("ImpressaoContextoService.Parse", AppXamlCs());

    [Fact] // o Parse vem ANTES da criacao do SingleInstanceService
    public void Parse_AntesDaInstanciaUnica()
    {
        var src = AppXamlCs();
        int iParse = src.IndexOf("ImpressaoContextoService.Parse");
        int iSingle = src.IndexOf("new SingleInstanceService");
        Assert.True(iParse >= 0 && iSingle >= 0 && iParse < iSingle,
            "Parse deve vir antes de new SingleInstanceService");
    }

    [Fact] // os dois verbos sao roteados (silencioso + avancado via DialogoImpressao)
    public void RoteiaAmbosOsModos()
    {
        var src = AppXamlCs();
        Assert.Contains("ImprimirSilencioso", src);
        Assert.Contains("DialogoImpressao", src);
    }
}
