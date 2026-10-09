using System.IO;
using System.Text;
using mPdf.App.ViewModels;
using mPdf.Documents;
using Xunit;

namespace mPdf.App.Tests;

public class Base64BannerTests
{
    private static string WriteWrapped()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-b64vm-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("\"" + Convert.ToBase64String(Fixtures.A4()) + "\""));
        return path;
    }

    [Fact]
    public async Task Base64_OpenedDocument_NeedsEnvelopeFix()
    {
        string path = WriteWrapped();
        try
        {
            using var doc = new DocumentViewModel(await DocumentSession.OpenAsync(path));
            Assert.True(doc.NeedsEnvelopeFix);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ConsertarArquivo_RewritesFileAndHidesBanner()
    {
        string path = WriteWrapped();
        try
        {
            using var doc = new DocumentViewModel(await DocumentSession.OpenAsync(path), notifyInfo: _ => { });
            var changed = new List<string?>();
            doc.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            await doc.ConsertarArquivoCommand.ExecuteAsync(null);
            Assert.False(doc.NeedsEnvelopeFix);
            Assert.Contains(nameof(DocumentViewModel.NeedsEnvelopeFix), changed);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 5));
        }
        finally { File.Delete(path); }
    }
    // Revisão final M1: feedback depois de consertar + guarda contra duplo clique.
    private const string Consertado = "Arquivo consertado — agora ele abre em qualquer programa.";

    [Fact]
    public async Task ConsertarArquivo_Limpo_AvisaConsertado()
    {
        string path = WriteWrapped();
        try
        {
            var infos = new List<string>();
            using var doc = new DocumentViewModel(await DocumentSession.OpenAsync(path), notifyInfo: infos.Add);
            await doc.ConsertarArquivoCommand.ExecuteAsync(null);
            Assert.Equal(Consertado, Assert.Single(infos));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ConsertarArquivo_ComAlteracoes_AvisaQueNaoForamSalvas()
    {
        string path = WriteWrapped();
        try
        {
            var infos = new List<string>();
            using var doc = new DocumentViewModel(await DocumentSession.OpenAsync(path), notifyInfo: infos.Add);
            doc.Session.Apply(Fixtures.ThirtyPages());
            Assert.True(doc.IsDirty); // sanity
            await doc.ConsertarArquivoCommand.ExecuteAsync(null);
            Assert.Equal(Consertado + " Suas alterações ainda não foram salvas.", Assert.Single(infos));
        }
        finally { File.Delete(path); }
    }

    [Fact] // enquanto grava, o comando fica desabilitado (duplo clique não dispara 2 gravações); depois volta
    public async Task ConsertarArquivo_EmAndamento_ComandoDesabilitado()
    {
        string path = WriteWrapped();
        try
        {
            var infos = new List<string>();
            using var doc = new DocumentViewModel(await DocumentSession.OpenAsync(path), notifyInfo: infos.Add);
            Assert.True(doc.ConsertarArquivoCommand.CanExecute(null));
            var t = doc.ConsertarArquivoCommand.ExecuteAsync(null);
            Assert.False(doc.ConsertarArquivoCommand.CanExecute(null));
            await t;
            Assert.Single(infos);
        }
        finally { File.Delete(path); }
    }

    [Fact] // já consertado: rodar de novo não regrava nem avisa outra vez
    public async Task ConsertarArquivo_JaConsertado_NaoFazNada()
    {
        string path = WriteWrapped();
        try
        {
            var infos = new List<string>();
            using var doc = new DocumentViewModel(await DocumentSession.OpenAsync(path), notifyInfo: infos.Add);
            await doc.ConsertarArquivoCommand.ExecuteAsync(null);
            await doc.ConsertarArquivoCommand.ExecuteAsync(null);
            Assert.Single(infos);
        }
        finally { File.Delete(path); }
    }
}
