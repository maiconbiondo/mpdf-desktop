using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

/// Plano 18 (Task 2) — instalação SILENCIOSA + relaunch + mutex `Global\` casado. Cinco frentes:
///   (1) as flags exatas do launch silencioso (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`) e o
///       `ProcessStartInfo` (UseShellExecute=true para o UAC disparar) sobre o MESMO caminho verificado;
///   (2) o delegate de PRODUÇÃO (`MainViewModel.Configuracoes`) realmente usa
///       `SilentUpdateInstaller.BuildStartInfo` (prova textual — senão as flags não chegariam à produção);
///   (3) o `mpdf.iss` tem `AppMutex=`/`CloseApplications=yes`/`RestartApplications=no` e o `[Run]` com
///       `runasoriginaluser` E SEM `skipifsilent` (estrutural — o Inno não roda o relaunch em silencioso
///       se tiver `skipifsilent`);
///   (4) o `AppMutex=` do `.iss` == a constante `SingleInstanceNames.UpdateAppMutexName` do app (se
///       divergir, o Inno não detecta a instância e a troca do .exe em uso falha);
///   (5) o app ADQUIRE de fato o mutex `Global\` nomeado (um 2º open vê `createdNew == false`).
///
/// A guarda "só o VerifiedUpdateFile chega ao Process.Start" e a confinação de rede continuam cobertas,
/// sem alteração, por `ConfiguracoesViewModelTests`/`UpdateNetworkConfinementTests` — este arquivo só
/// ACRESCENTA a cobertura do modo silencioso (nada aqui afrouxa aquelas guardas).
public class SilentUpdateInstallerTests
{
    // ---- descoberta da raiz do repo (mesmo exemplar de UpdateNetworkConfinementTests) ------------------

    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static string IssPath => Path.Combine(RepoRoot, "tools", "installer", "mpdf.iss");
    private static string MainViewModelPath => Path.Combine(RepoRoot, "src", "mPdf.App", "ViewModels", "MainViewModel.cs");

    // ---- (1) flags silenciosas + ProcessStartInfo -------------------------------------------------------

    [Fact]
    public void SilentArguments_SaoExatamenteAsTresFlags()
    {
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", SilentUpdateInstaller.SilentArguments);
    }

    [Fact]
    public void BuildStartInfo_UsaOMesmoCaminhoVerificado_ComFlagsSilenciosas_EShellExecute()
    {
        const string caminhoVerificado = @"C:\Temp\mpdf-update\mPDF-Setup-9.9.9.exe";

        var psi = SilentUpdateInstaller.BuildStartInfo(caminhoVerificado);

        Assert.Equal(caminhoVerificado, psi.FileName); // o MESMO caminho verificado, intocado
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", psi.Arguments);
        Assert.True(psi.UseShellExecute, "UseShellExecute=true é necessário para o UAC/manifesto de admin do instalador disparar a elevação");
    }

    // ---- (2) o delegate de produção realmente usa BuildStartInfo (prova textual) ------------------------

    // Sem esta guarda, alguém poderia manter `SilentUpdateInstaller.BuildStartInfo` verde em isolamento
    // mas religar a produção ao antigo `new ProcessStartInfo(path) { UseShellExecute = true }` (sem as
    // flags), e o launch de produção voltaria a ser interativo (assistente do Inno) sem quebrar nenhum
    // outro teste. Ancora a fiação de produção ao helper testado acima.
    [Fact]
    public void ProducaoLancaViaBuildStartInfo()
    {
        var texto = File.ReadAllText(MainViewModelPath);
        Assert.Contains("SilentUpdateInstaller.BuildStartInfo(", texto);
    }

    // ---- helpers estruturais do .iss (varredura linha a linha, ignorando comentários `;`) --------------

    // Linhas de comentário do Inno começam (após trim) com ';' — precisam ser ignoradas: o próprio arquivo
    // MENCIONA "skipifsilent" em comentários explicativos, então um `Contains` cru sobre o texto inteiro
    // teria um falso NEGATIVO na guarda (5). Só as DIRETIVAS reais contam.
    private static string[] IssDirectiveLines() =>
        File.ReadAllLines(IssPath)
            .Where(l => !l.TrimStart().StartsWith(";") && l.Trim().Length > 0)
            .ToArray();

    /// Extrai o valor de uma diretiva `Chave=valor` do `[Setup]` (primeira ocorrência não comentada).
    private static string? IssSetupDirectiveValue(string key)
    {
        foreach (var line in IssDirectiveLines())
        {
            var m = Regex.Match(line.Trim(), $@"^{Regex.Escape(key)}\s*=\s*(.+)$");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    /// A linha da diretiva `Filename:` do `[Run]` que relança o app (a que tem `runasoriginaluser`).
    private static string? IssRelaunchRunLine() =>
        IssDirectiveLines().FirstOrDefault(l =>
            l.TrimStart().StartsWith("Filename:", StringComparison.OrdinalIgnoreCase) &&
            l.Contains("runasoriginaluser"));

    // ---- (3) diretivas estruturais do .iss --------------------------------------------------------------

    [Fact]
    public void Iss_TemAppMutex_CloseApplications_RestartApplications()
    {
        Assert.False(string.IsNullOrWhiteSpace(IssSetupDirectiveValue("AppMutex")), "mpdf.iss deveria ter uma diretiva AppMutex= não vazia");
        Assert.Equal("yes", IssSetupDirectiveValue("CloseApplications"));
        Assert.Equal("no", IssSetupDirectiveValue("RestartApplications"));
    }

    [Fact]
    public void Iss_TemRunDeRelaunch_ComRunAsOriginalUser_SemSkipIfSilent()
    {
        var runLine = IssRelaunchRunLine();
        Assert.NotNull(runLine); // existe um [Run] Filename com runasoriginaluser
        Assert.Contains("runasoriginaluser", runLine!);
        Assert.Contains("nowait", runLine);
        Assert.Contains("postinstall", runLine);
        // A CHAVE do relaunch silencioso: SEM skipifsilent (senão o Inno pula o [Run] no /VERYSILENT e o
        // app não reabre sozinho após a atualização silenciosa).
        Assert.DoesNotContain("skipifsilent", runLine);
        // relança o próprio exe do app (o token {#MyAppExeName} é definido no topo do .iss).
        Assert.Contains("{#MyAppExeName}", runLine);
    }

    // ---- (4) o AppMutex do .iss == a constante do app ---------------------------------------------------

    [Fact]
    public void AppMutexNoIssCasaComAConstante()
    {
        var appMutexNoIss = IssSetupDirectiveValue("AppMutex");
        Assert.Equal(SingleInstanceNames.UpdateAppMutexName, appMutexNoIss);
    }

    [Fact]
    public void ConstanteDoMutex_EGlobal() // sanidade: precisa ser Global\ para o instalador ELEVADO enxergar
    {
        Assert.StartsWith(@"Global\", SingleInstanceNames.UpdateAppMutexName);
    }

    // ---- (5) o app adquire de fato o mutex Global nomeado ----------------------------------------------

    [Fact]
    public void AcquireAppMutex_TornaOObjetoNomeadoExistente()
    {
        // Enquanto ESTE handle estiver vivo, o objeto nomeado existe — um 2º open com o MESMO nome não é
        // o criador (createdNew == false). É exatamente o que o instalador (AppMutex=) observa: a
        // EXISTÊNCIA do mutex nomeado enquanto o app roda. (Independente de o app real estar aberto ou não
        // na máquina: se estiver, o objeto já existe e a asserção também vale.)
        using var held = SilentUpdateInstaller.AcquireAppMutex();
        using var probe = new Mutex(initiallyOwned: false, SingleInstanceNames.UpdateAppMutexName, out bool createdNew);
        Assert.False(createdNew, "o mutex Global nomeado deveria já existir (segurado por AcquireAppMutex) — o Inno detecta a instância por essa existência");
    }
}
