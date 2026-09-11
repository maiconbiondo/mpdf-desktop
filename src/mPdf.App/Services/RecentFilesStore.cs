using System.IO;
using System.Text.Json;

namespace mPdf.App.Services;

public sealed class RecentFilesStore
{
    private const int Max = 10;
    private readonly string _file;

    public RecentFilesStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "recentes.json");
    }

    /// Override de ISOLAMENTO DE TESTE (Plano 18) — mesmo mecanismo/mesma env var de
    /// `AppConfig.DefaultDirectory` (ver doc XML lá): se `MPDF_CONFIG_DIR` estiver setada (não-vazia),
    /// usa esse diretório em vez de `%AppData%\mPDF`, pra testes (inclusive `ShellTests`/`Task5Tests`, que
    /// constroem a `MainWindow` de produção via construtor sem parâmetros) nunca tocarem os recentes REAIS
    /// do usuário. Em produção a variável nunca é setada -> comportamento idêntico ao de sempre.
    public static string DefaultDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("MPDF_CONFIG_DIR");
            return !string.IsNullOrEmpty(overrideDir)
                ? overrideDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mPDF");
        }
    }

    public IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? []
                : [];
        }
        catch (JsonException) { return []; }   // arquivo corrompido = lista vazia, nunca crash
    }

    public void Add(string path)
    {
        var list = Load().ToList();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
        File.WriteAllText(_file, JsonSerializer.Serialize(list));
    }

    // Task 7: recente que falha ao abrir (arquivo movido/apagado) some da lista em vez de continuar
    // oferecendo um caminho morto no menu.
    public void Remove(string path)
    {
        var list = Load().ToList();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(_file, JsonSerializer.Serialize(list));
    }
}
