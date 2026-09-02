using mPdf.App.Services;

namespace mPdf.App.ViewModels;

/// VM da janela "Sobre" (Task 2, Plano 11; reduzido na Task 2 do Plano 17) — SÓ informações do app agora:
/// versão, licença AGPL-3.0, links. Os 3 controles que este VM hospedava antes (Tema, Nitidez extra do
/// texto, Verificar atualização) MIGRARAM pra `ConfiguracoesViewModel` (aberto pelo ⚙ do rail) — ver doc
/// XML daquela classe pra toda a lógica preservada (nada mudou além de ONDE ela mora). Este VM não tem
/// mais comando nenhum nem toca rede/config: só expõe `VersaoAtual`, lida uma vez no construtor.
public sealed class SobreViewModel
{
    /// Versão atual, lida uma vez no construtor (sem rede — ver `UpdateService.CurrentVersionText()`).
    public string VersaoAtual { get; } = UpdateService.CurrentVersionText();
}
