using System.Collections.Generic;
using System.IO;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

file sealed class DialogoNenhum(string? saveAs = null) : IFileDialogService
{
    public string? PickPdfToOpen() => null;
    public string? PickPdfToSaveAs(string currentPath) => saveAs;
    public string? PickImageToImport() => null;
    public string? PickPdfToSave(string suggestedName) => null;
}

file sealed class JuntarEstes(IReadOnlyList<string> caminhos) : IMergeDialogService
{
    public IReadOnlyList<string>? PickFilesToMerge() => caminhos;
}

file sealed class FecharCancela : IConfirmCloseService
{
    public CloseConfirmation Confirm(string documentTitle) => CloseConfirmation.Cancel;
}

/// Revisão (I1): o prefixo "Não foi possível abrir o arquivo:" era do CANAL de erro inteiro do MainViewModel
/// (`UiPrompts.MainNotifyError`) — e esse mesmo canal ia para todo DocumentViewModel. Erro de SALVAR (e qualquer erro
/// de documento) saía como "Não foi possível abrir o arquivo:\nNão foi possível salvar o arquivo '…'". Agora o canal é
/// cru e só a ABERTURA prefixa a mensagem. Estes testes fixam o TEXTO que chega ao canal (o MessageBox mostra-o tal qual).
public class MensagensErroTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-msg-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private MainViewModel Vm(List<string> erros, string? saveAs = null, IReadOnlyList<string>? juntar = null) =>
        new(new DialogoNenhum(saveAs), new RecentFilesStore(_dir), erros.Add, new AppConfig(Path.Combine(_dir, "config")),
            new FecharCancela(), stampGallery: new StampGallery(Path.Combine(_dir, "carimbos")), notifyInfo: _ => { },
            mergeDialog: new JuntarEstes(juntar ?? Array.Empty<string>()),
            listSigningCertificates: () => Array.Empty<SigningCertificateInfo>());

    private string Copia()
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, "doc.pdf");
        File.Copy(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), p);
        return p;
    }

    [Fact] // abrir que falha: É a abertura — prefixo "abrir" + o motivo
    public async Task AbrirQueFalha_TemPrefixoDeAbrir()
    {
        var erros = new List<string>();
        var vm = Vm(erros);
        await vm.OpenPath(Path.Combine(_dir, "nao-existe.pdf"));
        var msg = Assert.Single(erros);
        Assert.StartsWith(MainViewModel.PrefixoErroAbrir, msg);
        Assert.Equal("Não foi possível abrir o arquivo:\n", MainViewModel.PrefixoErroAbrir);
    }

    [Fact] // salvar com o arquivo travado: só a mensagem de salvar, sem "abrir o arquivo"
    public async Task SalvarTravado_MensagemDeSalvar_SemPrefixoDeAbrir()
    {
        var erros = new List<string>();
        var caminho = Copia();
        var vm = Vm(erros);
        await vm.OpenPath(caminho);
        vm.SelectedDocument!.Session.Apply(Fixtures.ThirtyPages());
        using (new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.None))
            await vm.SaveCommand.ExecuteAsync(null);
        var msg = Assert.Single(erros);
        Assert.StartsWith($"Não foi possível salvar o arquivo '{caminho}'", msg);
        Assert.DoesNotContain("abrir o arquivo", msg);
        Assert.True(vm.SelectedDocument.IsDirty);
    }

    [Fact] // salvar como com o destino travado: idem
    public async Task SalvarComoTravado_MensagemDeSalvar_SemPrefixoDeAbrir()
    {
        var erros = new List<string>();
        var caminho = Copia();
        var destino = Path.Combine(_dir, "destino.pdf");
        File.WriteAllBytes(destino, new byte[] { 1 });
        var vm = Vm(erros, saveAs: destino);
        await vm.OpenPath(caminho);
        using (new FileStream(destino, FileMode.Open, FileAccess.Read, FileShare.None))
            await vm.SaveAsCommand.ExecuteAsync(null);
        var msg = Assert.Single(erros);
        Assert.StartsWith("Não foi possível salvar o arquivo", msg);
        Assert.DoesNotContain("abrir o arquivo", msg);
    }

    // Revisão N3: Juntar com um arquivo escolhido que não pode ser lido mostrava a mensagem crua do .NET (em inglês,
    // sem dizer QUAL dos arquivos). Agora: prefixo de abrir + o nome do arquivo + o motivo em pt-BR.
    [Fact]
    public async Task Juntar_ArquivoTravado_MensagemPtBrComONome()
    {
        var erros = new List<string>();
        var bom = Copia();
        var travado = Path.Combine(_dir, "travado.pdf");
        File.Copy(bom, travado);
        var vm = Vm(erros, juntar: new[] { bom, travado });
        using (new FileStream(travado, FileMode.Open, FileAccess.Read, FileShare.None))
            await vm.MergeCommand.ExecuteAsync(null);
        var msg = Assert.Single(erros);
        Assert.Equal(MainViewModel.PrefixoErroAbrir + "'travado.pdf' não pôde ser lido — pode estar aberto em outro programa.", msg);
    }

    [Fact]
    public async Task Juntar_ArquivoSumiu_MensagemPtBrComONome()
    {
        var erros = new List<string>();
        var bom = Copia();
        var vm = Vm(erros, juntar: new[] { bom, Path.Combine(_dir, "sumiu.pdf") });
        await vm.MergeCommand.ExecuteAsync(null);
        Assert.Equal(MainViewModel.PrefixoErroAbrir + "'sumiu.pdf' não foi encontrado.", Assert.Single(erros));
    }
}
