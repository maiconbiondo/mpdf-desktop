using System.IO;
using System.Linq;
using mPdf.Documents;

namespace mPdf.App.Services;

/// Galeria de RUBRICAS (Plano 22) — imagens (PNG/JPG) usadas como aparência do carimbo de assinatura,
/// guardadas em `<config>/rubricas/`. Mesmo exemplar de `StampGallery`/`AppConfig`/`RecentFilesStore`:
/// diretório injetável no construtor (testes usam um temporário próprio, nunca tocam a pasta real), com
/// `DefaultDirectory` estático pra produção. A rubrica é escolhida/ADICIONADA no DIÁLOGO DE ASSINAR
/// (Plano 22 — antes ficava em Configurações, uma só). `Add` recebe BYTES JÁ VALIDADOS (o chamador
/// valida via `IPdfEditor.IsSupportedImage`/limite de pixels/CMYK antes) e grava com um id gerado; a
/// extensão do arquivo é irrelevante (o decodificador — `BitmapImage`/`ImageDataFactory` — reconhece o
/// formato pelo conteúdo, não pela extensão).
public sealed class RubricaGallery
{
    private readonly string _directory;

    public RubricaGallery(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// `<AppConfig.DefaultDirectory>/rubricas` — herda o override `MPDF_CONFIG_DIR` de `AppConfig`
    /// (isolamento de teste, ver `AppConfig.DefaultDirectory`), então em teste nunca toca a pasta real.
    public static string DefaultDirectory => Path.Combine(AppConfig.DefaultDirectory, "rubricas");

    /// Grava `bytes` (já validados) como uma rubrica nova; devolve o id (nome do arquivo dentro da
    /// galeria). Id gerado (Guid) — nunca colide, o usuário nunca vê o nome do arquivo.
    public string Add(byte[] bytes)
    {
        string id = $"rubrica-{Guid.NewGuid():N}.png";
        File.WriteAllBytes(Path.Combine(_directory, id), bytes);
        return id;
    }

    /// Lista os ids (nomes de arquivo) das rubricas salvas, ordem alfabética (determinística p/ UI/teste).
    /// Os bytes só são lidos por `LoadBytes` (lazy), nunca aqui.
    public IReadOnlyList<string> Load() =>
        Directory.EnumerateFiles(_directory)
            .Select(f => Path.GetFileName(f)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// Bytes de UMA rubrica, por id.
    public byte[] LoadBytes(string id) => File.ReadAllBytes(Path.Combine(_directory, id));

    /// Remove uma rubrica pelo id (idempotente — no-op se não existir).
    public void Remove(string id)
    {
        var p = Path.Combine(_directory, id);
        try { if (File.Exists(p)) File.Delete(p); }
        catch (IOException) { /* best-effort — não crash se travado */ }
    }
}
