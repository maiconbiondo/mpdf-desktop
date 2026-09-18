using System.Diagnostics;
using System.Threading;

namespace mPdf.App.Services;

/// Plano 18 (Task 2) — instalação SILENCIOSA + relaunch. Duas peças PURAS/testáveis extraídas do
/// delegate de produção do `_startInstaller` (em `MainViewModel.Configuracoes`) e do startup do app
/// (`App.OnStartup`), para que ambas possam ser exercitadas por teste headless sem iniciar um processo
/// real nem abrir uma janela:
///
///   1. <see cref="BuildStartInfo"/> — monta o `ProcessStartInfo` do instalador com as flags silenciosas
///      do Inno. A produção faz `Process.Start(SilentUpdateInstaller.BuildStartInfo(caminhoVerificado))`;
///      a GUARDA de segurança existente ("só o `VerifiedUpdateFile` verificado por SHA-256 chega ao
///      `Process.Start`") é preservada — este helper só ACRESCENTA argumentos ao MESMO caminho já
///      verificado, nunca escolhe o caminho.
///
///   2. <see cref="AcquireAppMutex"/> — adquire (e devolve, para o chamador SEGURAR pela vida inteira) o
///      mutex `Global\` nomeado (<see cref="SingleInstanceNames.UpdateAppMutexName"/>) que o instalador
///      (`AppMutex=`) usa para detectar a instância rodando. Independente do `SingleInstanceService`
///      (cujo comportamento de instância única NÃO é tocado).
public static class SilentUpdateInstaller
{
    /// Flags de instalação SILENCIOSA do Inno Setup:
    ///   `/VERYSILENT`        — sem assistente nenhum (nem a barra de progresso do `/SILENT`).
    ///   `/SUPPRESSMSGBOXES`  — sem caixas de mensagem (assume a resposta padrão).
    ///   `/NORESTART`         — o instalador NUNCA reinicia o Windows por conta própria (o relaunch do
    ///                          app é feito pela seção `[Run]` do `.iss`, não por reboot).
    /// O UAC (1 clique, fronteira do SO) permanece — `UseShellExecute=true` (em <see cref="BuildStartInfo"/>)
    /// faz o manifesto `requireAdministrator` do instalador disparar a elevação.
    public const string SilentArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    /// Parâmetro CUSTOM (o Inno ignora o que não conhece; só a função `[Code]` `DeveReabrir` do
    /// `mpdf.iss` o lê) que sinaliza ao instalador para NÃO relançar o mPDF ao fim. Usado SÓ pelo update
    /// do RMM (`/update` via `SilentUpdateRunner`), que precisa ser 100% invisível na estação — sem uma
    /// janela do mPDF pipocando após a atualização em massa. O botão "Atualizar" da UI NÃO passa este
    /// parâmetro (o usuário clicou e espera o app voltar). Ver a especificação
    /// `docs/superpowers/specs/2026-09-17-update-rmm-silencioso-e-padrao-design.md`.
    public const string ParametroNaoReabrir = "/NORELAUNCH";

    /// Monta o `ProcessStartInfo` para lançar o instalador VERIFICADO em modo silencioso.
    /// `reabrir` (default `true`): comportamento de sempre (o instalador relança o mPDF ao fim, via a
    /// seção `[Run]`). `reabrir: false` acrescenta <see cref="ParametroNaoReabrir"/> — o update do RMM
    /// usa isto para NÃO reabrir o app na estação.
    /// `UseShellExecute=true`: necessário para que o Windows honre o manifesto de admin do instalador e
    /// dispare o UAC (sem shell-execute a elevação não acontece e o `Process.Start` falharia por acesso
    /// negado ao tentar escrever em Arquivos de Programas). Rodado como SYSTEM/admin (agente mTI), não há
    /// UAC — a instalação é totalmente silenciosa.
    public static ProcessStartInfo BuildStartInfo(string caminhoInstaladorVerificado, bool reabrir = true) =>
        new(caminhoInstaladorVerificado)
        {
            Arguments = reabrir ? SilentArguments : SilentArguments + " " + ParametroNaoReabrir,
            UseShellExecute = true,
        };

    /// Adquire o mutex `Global\` de presença do app (nome em <see cref="SingleInstanceNames.UpdateAppMutexName"/>),
    /// `initiallyOwned:false` — o objeto NOMEADO passa a EXISTIR (é o que o Inno checa) e permanece
    /// enquanto QUALQUER handle a ele estiver aberto. O chamador (produção: um campo estático em `App`)
    /// deve MANTER a referência viva pela vida do processo e `Dispose()` no shutdown, liberando o objeto
    /// para o instalador prosseguir. Pode lançar (ex.: `UnauthorizedAccessException` se o processo não
    /// puder criar objetos no namespace `Global\`) — o chamador de produção trata como best-effort (uma
    /// falha aqui NUNCA pode impedir o app de abrir; no pior caso o instalador simplesmente não espera o
    /// mutex e o `CloseApplications` do Inno recai no fechamento por janela).
    public static Mutex AcquireAppMutex() =>
        new(initiallyOwned: false, SingleInstanceNames.UpdateAppMutexName);
}
