using System.Collections.Generic;
using System.IO;
using System.Text;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

file sealed class SemDialogos : IFileDialogService
{
    public string? PickPdfToOpen() => null;
    public string? PickPdfToSaveAs(string currentPath) => null;
    public string? PickImageToImport() => null;
    public string? PickPdfToSave(string suggestedName) => null;
}

file sealed class Cancela : IConfirmCloseService
{
    public CloseConfirmation Confirm(string documentTitle) => CloseConfirmation.Cancel;
}

/// Revisão (I2): "Consertar arquivo" (PDF em Base64) gravava SEM o pino de edição — Salvar ficava habilitado durante o
/// conserto e os dois `AtomicWrite` do MESMO arquivo corriam juntos (medido: 1/20 edição perdida em silêncio, 19/20
/// erro espúrio). Agora o conserto arma `TryBeginEdit` antes do 1º await: Salvar/Salvar como ficam desabilitados
/// enquanto ele grava, e ele não roda com outra operação em voo. Determinístico: o pino é armado de forma SÍNCRONA,
/// então basta olhar o estado logo depois de disparar o comando, antes do await.
public class ConsertarPinoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-consertar-pino-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Embrulhado()
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, "alvara.pdf");
        File.WriteAllBytes(p, Encoding.ASCII.GetBytes("\"" + Convert.ToBase64String(Fixtures.A4()) + "\""));
        return p;
    }

    private MainViewModel Vm(List<string> erros) =>
        new(new SemDialogos(), new RecentFilesStore(_dir), erros.Add, new AppConfig(Path.Combine(_dir, "config")),
            new Cancela(), stampGallery: new StampGallery(Path.Combine(_dir, "carimbos")), notifyInfo: _ => { },
            listSigningCertificates: () => Array.Empty<SigningCertificateInfo>());

    [Fact]
    public async Task ConsertoEmVoo_ArmaOPino_SalvarFicaDesabilitado()
    {
        var erros = new List<string>();
        var vm = Vm(erros);
        await vm.OpenPath(Embrulhado());
        var doc = vm.SelectedDocument!;
        doc.Session.Apply(Fixtures.ThirtyPages()); // sujo: Salvar estaria habilitado
        Assert.True(vm.SaveCommand.CanExecute(null));

        var t = doc.ConsertarArquivoCommand.ExecuteAsync(null);
        Assert.True(doc.Session.IsEditInFlight, "o conserto devia armar o pino de edição");
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
        await t;

        Assert.False(doc.Session.IsEditInFlight);
        Assert.True(vm.SaveCommand.CanExecute(null)); // ainda sujo: a edição em memória continua por salvar
        Assert.Empty(erros);
    }

    [Fact]
    public async Task OutraOperacaoEmVoo_ConsertarDesabilitado_ENaoGrava()
    {
        var path = Embrulhado();
        var original = File.ReadAllBytes(path);
        using var doc = new DocumentViewModel(await DocumentSession.OpenAsync(path), notifyInfo: _ => { });
        Assert.True(doc.ConsertarArquivoCommand.CanExecute(null));

        Assert.True(doc.Session.TryBeginEdit()); // ex.: um Salvar em voo
        try
        {
            Assert.False(doc.ConsertarArquivoCommand.CanExecute(null));
            await doc.ConsertarArquivoCommand.ExecuteAsync(null); // forçado: tem de ser no-op
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(doc.NeedsEnvelopeFix);
        }
        finally { doc.Session.EndEdit(); }
        Assert.True(doc.ConsertarArquivoCommand.CanExecute(null)); // pino solto: volta a habilitar
    }
}
