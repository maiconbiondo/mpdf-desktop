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

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mPDF");

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
