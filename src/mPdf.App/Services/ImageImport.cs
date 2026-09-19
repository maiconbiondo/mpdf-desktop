using System.IO;
using System.Linq;
using System.Threading.Tasks;
using mPdf.Editing;

namespace mPdf.App.Services;

/// Task 2 (Plano 7): ponto ÚNICO de conversão "imagem -> PDF" na fronteira do App — Abrir
/// (`MainViewModel.OpenPath`), Juntar (`MainViewModel.Merge`) e Inserir (`OrganizerViewModel.Insert`)
/// chamam este helper em vez de duplicar a sequência ReadAllBytes+IsSupportedImage+ImageToPdf em 3
/// lugares. `IPdfEditor` é passado por PARÂMETRO (nunca resolvido aqui via `PdfEditorFactory.Create()`)
/// de propósito: os 3 chamadores já têm um campo `_editor` injetável (usado por Merge/Split/Insert desde
/// tasks anteriores, justamente para permitir testar com um `FakePdfEditor` sem o motor real) — este
/// helper reaproveita a MESMA seam, nunca introduz uma 2ª forma de resolver o editor.
internal static class ImageImport
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    /// Critério de "isto é uma imagem?" — EXTENSÃO (não magic-bytes), mesma convenção do filtro dos
    /// diálogos de arquivo (`IFileDialogService.PickPdfToOpen`/`MergeFilesDialog`): um usuário que
    /// escolheu um arquivo *.jpg/*.jpeg/*.png num diálogo, ou passou um desses caminhos por linha de
    /// comando/associação de arquivo, está pedindo pra abrir/juntar/inserir uma IMAGEM. `IsSupportedImage`
    /// (dentro de `ConvertToPdf` abaixo) é quem confirma via magic-bytes DEPOIS de ler o arquivo — a
    /// checagem de verdade sobre o CONTEÚDO, não sobre o nome.
    public static bool IsImagePath(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// Núcleo SÍNCRONO da conversão — lê `path`, confirma magic-bytes via `IsSupportedImage` (recusa
    /// ANTES de pagar o custo de `ImageToPdf`/iText) e converte. Chamado DIRETO de dentro de um
    /// `Task.Run` já em voo quando o chamador processa VÁRIOS arquivos numa única passada (Merge: 1
    /// `Task.Run` para o lote inteiro, não 1 por arquivo) — `ConvertToPdfAsync` abaixo é só o wrapper de
    /// conveniência para os chamadores de 1 arquivo por vez (Abrir/Inserir).
    ///
    /// Toda falha (arquivo ausente, magic-bytes recusados, `ImageToPdf` do motor lançando por
    /// corrupção/CMYK/teto de pixels) sai daqui NOMEANDO o arquivo — decisão de design (Task 2, Plano 7):
    /// `IPdfEditor.ImageToPdf` só conhece BYTES, nunca o caminho de origem, então suas próprias mensagens
    /// não podem nomear o arquivo; este helper é o único ponto que conhece os dois (caminho + motor), por
    /// isso é quem re-embrulha a exceção do motor numa mensagem nomeando o arquivo — usado tanto pelo
    /// Abrir (1 arquivo, erro claro) quanto pelo Juntar (N arquivos — sem nomear, o usuário não saberia
    /// QUAL dos vários falhou).
    public static byte[] ConvertToPdf(string path, IPdfEditor editor)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Arquivo não encontrado.", path);

        byte[] bytes = File.ReadAllBytes(path);
        if (!editor.IsSupportedImage(bytes))
            throw new PdfEditingException(
                $"'{Path.GetFileName(path)}' não é uma imagem JPG/PNG válida — formatos suportados: JPG, PNG.");

        try
        {
            return editor.ImageToPdf(bytes);
        }
        catch (PdfEditingException ex)
        {
            throw new PdfEditingException(
                $"Não foi possível converter a imagem '{Path.GetFileName(path)}': {ex.Message}", ex);
        }
    }

    /// Envelope assíncrono de `ConvertToPdf` — decodificar uma imagem é CPU-bound (mesmo contrato de
    /// `IPdfEditor.ImageToPdf`/`PdfEditorFactory`), então roda inteiro dentro de `Task.Run`. Usado pelos
    /// chamadores que processam 1 arquivo de cada vez (Abrir, Inserir) — Juntar processa o LOTE inteiro
    /// dentro de um único `Task.Run` próprio e chama `ReadOrConvertToPdf`/`ConvertToPdf` (síncronos)
    /// direto de lá, para não aninhar N `Task.Run` dentro de 1.
    public static Task<byte[]> ConvertToPdfAsync(string path, IPdfEditor editor) =>
        Task.Run(() => ConvertToPdf(path, editor));

    /// Lê `path` como PDF normal (bytes crus, sem tocar o motor) OU converte via `ConvertToPdf` quando
    /// `IsImagePath` — usado por Juntar/Inserir, que misturam PDFs e imagens na MESMA lista/diálogo de
    /// origem e precisam de "PDF pronto para o motor" independente do tipo de entrada.
    public static byte[] ReadOrConvertToPdf(string path, IPdfEditor editor) =>
        IsImagePath(path) ? ConvertToPdf(path, editor) : File.ReadAllBytes(path);
}
