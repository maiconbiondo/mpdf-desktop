using System.IO;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;

namespace mPdf.App.Tests;

/// `DocumentViewModel` REAL (motor real por padrão) sobre uma CÓPIA temporária do PDF, com um `AppConfig` numa pasta
/// temporária própria. O `Dispose` apaga TUDO o que o teste criou: o PDF (e o `.bak` que um Salvar criaria) e a
/// pasta do config (`AppConfig` cria a pasta e grava config.json — antes cada cópia desta fábrica deixava a pasta
/// para trás em %TEMP%). Mesmo padrão de limpeza de `AssinarPipeTests.Dispose` (Directory.Delete recursivo).
///
/// `prefixo` nomeia os arquivos (`mpdf-{prefixo}-vm-*.pdf`, `mpdf-{prefixo}-cfg-*`) para quem procura vazamento.
/// `Dialog` é sempre um `FakeAnnotationTextDialogService` (nenhum teste abre diálogo WPF real).
internal sealed class VmTemporaria : IDisposable
{
    public DocumentViewModel D { get; }
    public FakeAnnotationTextDialogService Dialog { get; }
    public List<string> Errors { get; }
    public string Path { get; }
    public string CfgDir { get; }

    private VmTemporaria(DocumentViewModel d, FakeAnnotationTextDialogService dialog, List<string> errors, string path, string cfgDir)
    {
        D = d; Dialog = dialog; Errors = errors; Path = path; CfgDir = cfgDir;
    }

    public static VmTemporaria Abrir(byte[] pdf, string prefixo, string? autor = "Teste", IPdfEditor? editor = null,
        FakeAnnotationTextDialogService? annotationDialog = null, ICorrigirTextoDialogService? corrigirTextoDialog = null)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpdf-{prefixo}-vm-{Guid.NewGuid():N}.pdf");
        string cfgDir = PastasDeConfigTemporarias.NovoCaminho(prefixo);
        try
        {
            File.WriteAllBytes(path, pdf);
            var dialog = annotationDialog ?? new FakeAnnotationTextDialogService();
            var errors = new List<string>();
            var config = new AppConfig(cfgDir);
            if (autor is not null) config.Autor = autor;
            var d = new DocumentViewModel(DocumentSession.Open(path), editor: editor ?? PdfEditorFactory.Create(),
                config: config, notifyError: errors.Add, annotationDialog: dialog, corrigirTextoDialog: corrigirTextoDialog);
            return new VmTemporaria(d, dialog, errors, path, cfgDir);
        }
        catch
        {
            Apagar(path, cfgDir);
            throw;
        }
    }

    public void Dispose()
    {
        D.Dispose();
        Apagar(Path, CfgDir);
    }

    private static void Apagar(string path, string cfgDir)
    {
        foreach (var f in new[] { path, path + ".bak" })
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        PastasDeConfigTemporarias.Apagar(cfgDir);
    }
}

/// Pastas de `AppConfig` em %TEMP% (`mpdf-{prefixo}-cfg-{guid}`) para os testes com VM FALSO (FakePdfEditor) que
/// constroem o `DocumentViewModel` num `Build*` próprio: a classe de teste guarda uma instância, chama `Nova` no
/// builder e a apaga no seu `Dispose` (xUnit cria uma instância da classe por teste). `AppConfig` cria a pasta e grava
/// config.json; sem isto cada execução da suíte deixava milhares de pastas em %TEMP%.
internal sealed class PastasDeConfigTemporarias : IDisposable
{
    private readonly List<string> _pastas = [];

    public string Nova(string prefixo)
    {
        string dir = NovoCaminho(prefixo);
        _pastas.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _pastas) Apagar(dir);
        _pastas.Clear();
    }

    internal static string NovoCaminho(string prefixo) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpdf-{prefixo}-cfg-{Guid.NewGuid():N}");

    internal static void Apagar(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
