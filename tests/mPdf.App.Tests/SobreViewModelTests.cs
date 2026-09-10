using mPdf.App.ViewModels;

namespace mPdf.App.Tests;

/// Task 2 (Plano 11; reduzido na Task 2 do Plano 17) — `SobreViewModel` agora só expõe `VersaoAtual`
/// (sem rede/config/comando nenhum). Os testes de Tema/Nitidez/Atualização MIGRARAM pra
/// `ConfiguracoesViewModelTests` (junto com a lógica, ver `ConfiguracoesViewModel`).
public class SobreViewModelTests
{
    [Fact]
    public void VersaoAtual_MatchesUpdateServiceCurrentVersionText()
    {
        var vm = new SobreViewModel();
        Assert.Equal(mPdf.App.Services.UpdateService.CurrentVersionText(), vm.VersaoAtual);
    }
}
