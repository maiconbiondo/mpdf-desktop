using System.IO;
using Xunit;

namespace mPdf.App.Tests;

/// Task 3 (impressao pelo menu de contexto) — varredura estrutural do `mpdf.iss`: confirma que o
/// instalador expõe a task opt-in "contextprint" e registra os dois verbos classicos
/// (Imprimir / Impressão avançada) sob SystemFileAssociations\.pdf\shell, cada um com seu
/// `command` apontando pro `.exe` com `/print`/`/printadv`. Verificação FINAL (menu real do
/// Explorer) é manual — ver Step 5 do brief.
public class InstaladorContextPrintTests
{
    private static string Iss()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "tools", "installer", "mpdf.iss"));
    }

    [Fact] // existe a task opt-in contextprint
    public void Task_Contextprint_Existe()
    {
        var s = Iss();
        Assert.Contains("Name: \"contextprint\"", s);
    }

    [Fact] // registra os dois verbos sob SystemFileAssociations\.pdf\shell com /print e /printadv
    public void Verbos_Registrados()
    {
        var s = Iss();
        Assert.Contains(@"SystemFileAssociations\.pdf\shell\mPDFImprimir", s);
        Assert.Contains(@"SystemFileAssociations\.pdf\shell\mPDFImprimirAvancado", s);
        Assert.Contains("/print ", s);
        Assert.Contains("/printadv ", s);
    }

    [Fact] // as chaves de contexto sao removidas na desinstalacao (uninsdeletekey) e presas a task
    public void Verbos_DesinstalamLimpo()
    {
        var s = Iss();
        // As 6 linhas de Registry do contextprint (2 verbos x MUIVerb+Icon+command) sao TODAS presas a task.
        int tags = System.Text.RegularExpressions.Regex.Matches(s, "Tasks: contextprint").Count;
        Assert.Equal(6, tags);
        // E as duas chaves RAIZ de verbo carregam uninsdeletekey (a remocao leva as subchaves junto).
        Assert.Matches(@"shell\\mPDFImprimir"".*uninsdeletekey", s);
        Assert.Matches(@"shell\\mPDFImprimirAvancado"".*uninsdeletekey", s);
    }
}
