using System.IO;
using System.Linq;

namespace mPdf.PreviewHandler.Tests;

/// Plano 24: guarda de REDE do módulo do preview handler — mesmo espírito da guarda de rede de
/// `src/mPdf.App` (UpdateNetworkConfinementTests), mas para `src/mPdf.PreviewHandler`, que NÃO pode
/// tocar rede NENHUMA (roda no prevhost do Explorer; só renderiza um arquivo local). Varre os `.cs` do
/// módulo e reprova nomeando arquivo:linha se achar um token da família de rede do BCL. A guarda AGPL
/// (iText/Docnet) já é coberta pela varredura de TODO `src/` em `mPdf.Editing.Tests.AgplGuardTests`.
public class PreviewHandlerGuardTests
{
    private static readonly string[] TokensRede =
        ["HttpClient", "WebRequest", "WebClient", "Socket", "TcpClient", "Dns", "System.Net"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void PreviewHandler_NaoTocaRede()
    {
        var modulo = Path.Combine(RepoRoot(), "src", "mPdf.PreviewHandler.Aot");
        var arquivos = Directory.EnumerateFiles(modulo, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(arquivos.Count > 0, "nenhum .cs encontrado em src/mPdf.PreviewHandler — piso de sanidade");

        var ocorrencias = new System.Collections.Generic.List<string>();
        foreach (var arq in arquivos)
        {
            var linhas = File.ReadAllLines(arq);
            for (int i = 0; i < linhas.Length; i++)
            {
                var linha = linhas[i];
                if (linha.TrimStart().StartsWith("//")) continue; // comentário inteiro não dispara
                foreach (var tok in TokensRede)
                    if (linha.Contains(tok))
                        ocorrencias.Add($"{Path.GetFileName(arq)}:{i + 1}: {tok}");
            }
        }

        Assert.True(ocorrencias.Count == 0,
            "src/mPdf.PreviewHandler não pode tocar rede — ocorrência(s):\n" + string.Join("\n", ocorrencias));
    }
}
