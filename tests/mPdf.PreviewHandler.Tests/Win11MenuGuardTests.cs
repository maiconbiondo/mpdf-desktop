using System.IO;

namespace mPdf.PreviewHandler.Tests;

/// Task 1 (menu Win11): guarda de varredura de fonte pro comando de shell `IExplorerCommand` "mPDF"
/// (Abrir/Imprimir/Impressão avançada) no DLL AOT. Mesmo espírito de `PdfPreviewHandlerComContractTests`/
/// `PreviewMultiPaginaGuardTests`: o shell/COM não roda headless em CI, então a disciplina é varredura de
/// texto-fonte + build gerenciado (sem ILC nativo).
public class Win11MenuGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static string ModuloDir() => Path.Combine(RepoRoot(), "src", "mPdf.PreviewHandler.Aot");

    private static string Ler(string arquivo) => File.ReadAllText(Path.Combine(ModuloDir(), arquivo));

    [Fact]
    public void ClassFactory_RoteiaPorClsid_IncluindoMpdfRootCommand()
    {
        var s = Ler("ClassFactory.cs");
        Assert.Contains("DllGetClassObject", s);
        Assert.Contains("MpdfRootCommand", s);
        Assert.Contains("PdfPreviewHandler.Clsid", s);
        Assert.Contains("MpdfRootCommand.Clsid", s);
    }

    [Fact]
    public void MpdfExplorerCommand_Existe_ComInterfaceETitulos()
    {
        var caminho = Path.Combine(ModuloDir(), "MpdfExplorerCommand.cs");
        Assert.True(File.Exists(caminho), "MpdfExplorerCommand.cs não encontrado em src/mPdf.PreviewHandler.Aot");

        var s = File.ReadAllText(caminho);
        Assert.Contains("IExplorerCommand", s);
        Assert.Contains("EnumSubCommands", s);
        Assert.Contains("\"mPDF\"", s);
        Assert.Contains("\"Abrir\"", s);
        Assert.Contains("\"Imprimir\"", s);
        Assert.Contains("\"Impressão avançada\"", s);
    }

    [Fact]
    public void Subcomandos_UsamVerbosELancamOExeCerto()
    {
        var s = Ler("MpdfExplorerCommand.cs");
        Assert.Contains("\"/print\"", s);
        Assert.Contains("\"/printadv\"", s);
        Assert.Contains("mPdf.App.exe", s);
    }

    [Fact] // resolve o exe pela pasta da PROPRIA DLL (GetModuleHandleEx), NAO por AppContext.BaseDirectory
           // (que sob o COM surrogate dllhost.exe aponta pra System32 -> os verbos virariam no-op).
    public void MpdfExplorerCommand_ResolveExePelaPastaDaDll_NaoPorBaseDirectory()
    {
        var s = Ler("MpdfExplorerCommand.cs");
        Assert.DoesNotContain("Program Files", s);
        Assert.DoesNotContain(@"C:\", s);
        // CaminhoExe usa DiretorioDestaDll (modulo da DLL) — e NAO pode cair no BaseDirectory.
        Assert.Contains("DiretorioDestaDll", s);
        Assert.DoesNotContain("AppContext.BaseDirectory", s);
    }

    [Fact]
    public void ComInterop_DeclaraOsIidsNovosDoExplorerCommand()
    {
        var s = Ler("ComInterop.cs");
        Assert.Contains("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9", s); // IExplorerCommand
        Assert.Contains("a88826f8-186f-4987-aade-ea0cef8fbfe8", s); // IEnumExplorerCommand
        Assert.Contains("b63ea76d-1f85-456f-a19c-48159efa858b", s); // IShellItemArray
    }
}
