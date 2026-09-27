using System.IO;
using Xunit;

namespace mPdf.App.Tests;

// v2.13.0: App.xaml.cs nao roda headless (ver doc XML da classe). Esta varredura prova que OnStartup
// roteia o /assinar na mesma posicao do /print (antes da instancia unica), encaminha a linha de comando
// pelo pipe, assina o CommandReceived antes do TryAcquire e executa o comando na 1a instancia.
public class AppStartupAssinarRoutingTests
{
    private static string AppXamlCs()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "mPdf.App", "App.xaml.cs"));
    }

    [Fact] // OnStartup chama AssinarContextoService.Parse, depois do /print e antes da instancia unica
    public void AppStartup_RoteiaAssinar()
    {
        var src = AppXamlCs();
        int iPrint = src.IndexOf("ImpressaoContextoService.Parse");
        int iAssinar = src.IndexOf("AssinarContextoService.Parse");
        int iSingle = src.IndexOf("new SingleInstanceService");
        Assert.True(iPrint >= 0 && iAssinar >= 0 && iSingle >= 0, "marcadores ausentes em App.xaml.cs");
        Assert.True(iPrint < iAssinar && iAssinar < iSingle,
            "AssinarContextoService.Parse deve vir depois do /print e antes de new SingleInstanceService");
    }

    [Fact] // o que vai pro pipe com /assinar e a LINHA DE COMANDO, e o gate recebe essa linha
    public void AppStartup_EncaminhaLinhaDeComando()
    {
        var src = AppXamlCs();
        int iMontar = src.IndexOf("ProtocoloInstanciaUnica.MontarLinha");
        int iGate = src.IndexOf("SingleInstanceLaunchGate.ShouldContinueLaunch(_singleInstance, linhaParaEncaminhar");
        Assert.True(iMontar >= 0 && iGate >= 0 && iMontar < iGate,
            "a linha de comando deve ser montada e passada ao ShouldContinueLaunch");
    }

    [Fact] // CommandReceived assinado ANTES do TryAcquire (mesma regra do PathReceived, Item 3 do Plano 6)
    public void AppStartup_AssinaCommandReceivedAntesDoTryAcquire()
    {
        var src = AppXamlCs();
        int iSub = src.IndexOf("_singleInstance.CommandReceived += OnCommandReceivedFromOtherInstance");
        int iGate = src.IndexOf("SingleInstanceLaunchGate.ShouldContinueLaunch(");
        Assert.True(iSub >= 0 && iGate >= 0 && iSub < iGate, "CommandReceived deve ser assinado antes do TryAcquire");
    }

    [Fact] // 1a instancia e pipe executam o comando pelo VM (ExecutarComandoAsync), com a janela pronta
    public void AppStartup_ExecutaComandoNaJanelaPronta()
    {
        var src = AppXamlCs();
        Assert.Contains("RunWhenWindowReady(vm => vm.ExecutarComandoAsync(cmdInicial))", src);
        Assert.Contains("await mw.ViewModel.ExecutarComandoAsync(comando)", src);
    }
}
