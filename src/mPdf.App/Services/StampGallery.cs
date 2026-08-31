using System.IO;
using System.Linq;

namespace mPdf.App.Services;

/// Galeria de carimbos de imagem (Task 9, Plano 3a) — `%AppData%\mPDF\carimbos\` (injetável, mesmo
/// exemplar de `AppConfig`/`RecentFilesStore`: diretório recebido no construtor, testes usam um
/// diretório temporário próprio, nunca tocam a pasta real da máquina). `Add` COPIA o arquivo de origem
/// para DENTRO da galeria — dedupe por nome (colisão -> " (2)", " (3)"..., mesma convenção de
/// `MainViewModel.BuildEditableCopyPath`, exemplar de `EditCopy`). PNG/JPG apenas: checagem de
/// EXTENSÃO (não do conteúdo real do arquivo — v1), `ArgumentException` pt-BR para qualquer outra.
public sealed class StampGallery
{
    private static readonly string[] AllowedExtensions = [".png", ".jpg", ".jpeg"];
    private readonly string _directory;

    public StampGallery(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mPDF", "carimbos");

    /// Copia `sourcePath` para dentro da galeria. Devolve o NOME final do arquivo dentro da galeria
    /// (não o caminho completo — mesmos nomes que `Load()` lista). Colisão de nome com um carimbo já
    /// existente -> " (2)", " (3)"... (varredura simples por `File.Exists`, mesma aceitação de
    /// concorrência já documentada em `MainViewModel.BuildEditableCopyPath`/
    /// `DocumentSession.SweepOrphanTempFiles`).
    public string Add(string sourcePath)
    {
        string ext = Path.GetExtension(sourcePath);
        if (!AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Formato de imagem não suportado: '{ext}'. Use PNG ou JPG.", nameof(sourcePath));

        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string candidate = Path.Combine(_directory, baseName + ext);
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(_directory, $"{baseName} ({n}){ext}");

        File.Copy(sourcePath, candidate);
        return Path.GetFileName(candidate)!;
    }

    /// Lista os NOMES de arquivo da galeria (brief: "filename + bytes lazily" — os bytes só são lidos
    /// por `LoadBytes`, quando de fato precisos, nunca aqui). Ordem alfabética (determinística p/
    /// teste/UI).
    public IReadOnlyList<string> Load() =>
        Directory.EnumerateFiles(_directory)
            .Where(f => AllowedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// Lê os bytes de UM carimbo da galeria, por nome — lazy de propósito (só quando o usuário for
    /// exibir a miniatura ou de fato colocar aquele carimbo na página, nunca em `Load()`).
    public byte[] LoadBytes(string name) => File.ReadAllBytes(Path.Combine(_directory, name));

    /// Remove um carimbo da galeria pelo nome.
    public void Remove(string name) => File.Delete(Path.Combine(_directory, name));
}
