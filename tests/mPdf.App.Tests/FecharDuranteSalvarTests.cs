using System.Collections.Generic;
using System.IO;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

file sealed class SemDialogoArq : IFileDialogService
{
    public string? PickPdfToOpen() => null;
    public string? PickPdfToSaveAs(string currentPath) => null;
    public string? PickImageToImport() => null;
    public string? PickPdfToSave(string suggestedName) => null;
}

internal sealed class ContaConfirmacoes(CloseConfirmation resposta) : IConfirmCloseService
{
    public int Chamadas { get; private set; }
    public CloseConfirmation Confirm(string documentTitle) { Chamadas++; return resposta; }
}

/// Revisão M2: fechar a aba (ou a janela) com um Salvar em voo perguntava de novo (o documento ainda está sujo até a
/// conclusão), e "Salvar" no prompt rodava `TrySaveSync` gravando o MESMO arquivo junto com a gravação do pool — e o
/// documento era descartado com o pino armado. Agora fechar é BLOQUEADO enquanto `IsSaving` (aviso ao usuário), e
/// volta a funcionar quando a gravação termina. Determinístico: `Save` marca `IsSaving` antes do 1º await.
public class FecharDuranteSalvarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-fechar-salvando-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (MainViewModel Vm, ContaConfirmacoes Confirmar, List<string> Infos) Vm()
    {
        var confirmar = new ContaConfirmacoes(CloseConfirmation.Save);
        var infos = new List<string>();
        var vm = new MainViewModel(new SemDialogoArq(), new RecentFilesStore(_dir), _ => { }, new AppConfig(Path.Combine(_dir, "config")),
            confirmar, stampGallery: new StampGallery(Path.Combine(_dir, "carimbos")), notifyInfo: infos.Add,
            listSigningCertificates: () => Array.Empty<SigningCertificateInfo>());
        return (vm, confirmar, infos);
    }

    private async Task<DocumentViewModel> AbrirSujo(MainViewModel vm)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, "doc.pdf");
        File.Copy(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), p);
        await vm.OpenPath(p);
        vm.SelectedDocument!.Session.Apply(Fixtures.ThirtyPages());
        return vm.SelectedDocument;
    }

    [Fact]
    public async Task FecharAba_DuranteSalvar_EBloqueado_DepoisFecha()
    {
        var (vm, confirmar, infos) = Vm();
        var doc = await AbrirSujo(vm);

        var salvando = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(doc.IsSaving);
        vm.CloseDocumentCommand.Execute(doc);
        Assert.Contains(doc, vm.Documents);          // não fechou
        Assert.Equal(0, confirmar.Chamadas);          // nem perguntou (nenhuma 2ª gravação)
        Assert.Equal("Aguarde: uma operação no arquivo ainda está em andamento.", Assert.Single(infos));
        Assert.Equal(MainViewModel.AguardeOperacaoNotice, infos[0]);
        await salvando;

        Assert.False(doc.IsDirty);
        vm.CloseDocumentCommand.Execute(doc);         // terminou: fecha normalmente (limpo, sem prompt)
        Assert.DoesNotContain(doc, vm.Documents);
    }

    [Fact] // revisão N2: qualquer operação com o pino armado (aqui o conserto do Base64) também bloqueia o fechar
    public async Task FecharAbaEJanela_DuranteConserto_EBloqueado_DepoisFecha()
    {
        var (vm, confirmar, infos) = Vm();
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, "alvara.pdf");
        File.WriteAllBytes(p, System.Text.Encoding.ASCII.GetBytes("\"" + Convert.ToBase64String(Fixtures.A4()) + "\""));
        await vm.OpenPath(p);
        var doc = vm.SelectedDocument!;
        doc.Session.Apply(Fixtures.ThirtyPages()); // sujo: sem a guarda, o prompt "Salvar" gravaria junto

        var consertando = doc.ConsertarArquivoCommand.ExecuteAsync(null);
        Assert.True(doc.Session.IsEditInFlight);
        vm.CloseDocumentCommand.Execute(doc);
        Assert.Contains(doc, vm.Documents);
        Assert.False(vm.ConfirmCloseAll());
        Assert.Equal(0, confirmar.Chamadas);
        Assert.Equal(new[] { MainViewModel.AguardeOperacaoNotice, MainViewModel.AguardeOperacaoNotice }, infos.Where(i => i.StartsWith("Aguarde")));
        await consertando;

        Assert.True(vm.ConfirmCloseAll()); // agora pergunta (sujo) e o fake responde Salvar
        Assert.Equal(1, confirmar.Chamadas);
        foreach (var d in vm.Documents) d.Dispose();
    }

    [Fact]
    public async Task FecharJanela_DuranteSalvar_NaoFecha()
    {
        var (vm, confirmar, _) = Vm();
        var doc = await AbrirSujo(vm);

        var salvando = vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.ConfirmCloseAll());
        Assert.Equal(0, confirmar.Chamadas);
        await salvando;
        Assert.True(vm.ConfirmCloseAll()); // limpo: fecha sem perguntar
        foreach (var d in vm.Documents) d.Dispose();
    }
}
