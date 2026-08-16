using System.Diagnostics;

namespace mPdf.App.Services;

/// Nomes ESTÁVEIS (mesmo GUID em toda execução) de mutex/pipe de instância única — usados SÓ em
/// produção (App.xaml.cs). Testes (SingleInstanceServiceTests) NUNCA usam estes nomes fixos: cada
/// teste gera seu próprio par via Guid.NewGuid(), pra nunca colidir entre execuções concorrentes/CI
/// nem com uma instância real do app rodando na máquina do dev.
///
/// Segurança multiusuário (brief da Task 1): o nome embute Environment.UserName E o SessionId do
/// processo atual. UserName sozinho já bastaria pra separar duas CONTAS diferentes na mesma máquina
/// (o namespace "Local\" do Mutex já é por sessão do Windows, então duas contas seriam sessões
/// diferentes de qualquer forma) -- mas pipes nomeados NÃO são isolados por sessão do jeito que
/// objetos "Local\" são (o namespace \\.\pipe\ é compartilhado pela máquina inteira, entre sessões).
/// Sem o SessionId, DUAS sessões RDP do MESMO usuário poderiam acabar com pipes de mesmo nome —
/// a instância secundária da sessão A enviaria o caminho pro pipe da sessão B por engano (janela
/// errada recebendo o PDF). Incluir os dois garante que mutex e pipe ficam sempre "emparelhados" com
/// a MESMA execução: cada usuário, em cada sessão, vê sua própria instância única.
public static class SingleInstanceNames
{
    // GUID fixo do app -- gerado uma vez, NUNCA trocar (trocar quebraria a detecção de instância
    // única entre uma versão já rodando e outra recém-instalada por cima; toda versão NOVA continua
    // vendo esta mesma constante, então a troca continua funcionando entre atualizações).
    private const string AppId = "6f2b0f0c-6a86-4d0a-9b7e-6e6f9d6d9b2a";

    public static string MutexName { get; } = BuildName("mutex");
    public static string PipeName { get; } = BuildName("pipe");

    private static string BuildName(string kind)
    {
        // Backslash não é válido em nome de mutex/pipe (fora o prefixo "Local\"/"Global\") -- um
        // UserName no formato "DOMINIO\usuario" (raro com Environment.UserName, mas não impossível em
        // alguns cenários de domínio) quebraria a construção do nome; saneia por garantia barata.
        var user = Environment.UserName.Replace('\\', '_');
        var sessionId = Process.GetCurrentProcess().SessionId;
        return $"mPdf-{AppId}-{kind}-{user}-{sessionId}";
    }
}
