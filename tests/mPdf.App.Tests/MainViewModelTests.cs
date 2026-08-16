using System.IO;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

file sealed class FakeDialog(string? openResult, string? saveAsResult = null, string? imageResult = null, string? saveResult = null) : IFileDialogService
{
    public string? PickPdfToOpen() => openResult;
    public string? PickPdfToSaveAs(string currentPath) => saveAsResult;
    // Task 9 (Plano 3a): mesmo padrão dos 2 acima — devolve um caminho FIXO (ou null = cancelado).
    public string? PickImageToImport() => imageResult;
    // Task 4 (Plano 3b): mesmo padrão — usado por MergeCommand (SaveFileDialog do resultado unificado).
    public string? PickPdfToSave(string suggestedName) => saveResult;
}

// Task 4 (Plano 3b): fake do diálogo "Juntar documentos" — devolve uma lista de caminhos FIXA (já na
// ordem esperada), ou null = cancelado. CallCount prova que MergeCommand realmente chamou o diálogo.
file sealed class FakeMergeDialogService(IReadOnlyList<string>? result) : IMergeDialogService
{
    public int CallCount { get; private set; }
    public IReadOnlyList<string>? PickFilesToMerge() { CallCount++; return result; }
}

// Task 4 (Plano 3b): fake do diálogo "Dividir documento" — devolve (ranges, pasta) FIXOS, ou null =
// cancelado. Mesmo padrão de CallCount de FakeMergeDialogService acima.
file sealed class FakeSplitDialogService((string ranges, string folder)? result) : ISplitDialogService
{
    public int CallCount { get; private set; }
    public (string Ranges, string DestinationFolder)? PickSplitOptions()
    {
        CallCount++;
        return result is { } r ? (r.ranges, r.folder) : null;
    }
}

// Task 3 (Plano 3a): fake do prompt de fechar sujo — devolve uma CloseConfirmation FIXA (nunca abre
// MessageBox de verdade, que travaria a sessão de teste esperando um clique). CallCount prova que o
// prompt só é mostrado quando o documento está de fato sujo (ver CloseDocument_CleanDocument_...).
file sealed class FakeConfirmCloseService(CloseConfirmation result) : IConfirmCloseService
{
    public int CallCount { get; private set; }
    public CloseConfirmation Confirm(string documentTitle) { CallCount++; return result; }
}

// Task 5 (Plano 4): captura o BatchSignViewModel construído por MainViewModel.BatchSign SEM abrir
// janela nenhuma — CallCount/LastViewModel provam que MainViewModel realmente montou o VM (com o
// predicado isPathOpen ligado a Documents de verdade) e chamou a seam, sem precisar de um Window real.
file sealed class SpyBatchSignDialogService : IBatchSignDialogService
{
    public int CallCount { get; private set; }
    public BatchSignViewModel? LastViewModel { get; private set; }
    public void ShowBatchSignDialog(BatchSignViewModel viewModel) { CallCount++; LastViewModel = viewModel; }
}

// Herméticos: usam o ctor de 2 argumentos com um RecentFilesStore em diretório temporário,
// para nunca tocar em %AppData%\mPDF durante os testes (Task 8 introduziu o store de recentes).
public class MainViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-vm-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private MainViewModel Vm(string? dialogResult = null) =>
        new(new FakeDialog(dialogResult), new RecentFilesStore(_dir));

    // Task 3 (Plano 3a): helper completo (AppConfig + IConfirmCloseService injetáveis) para os testes
    // de Save/SaveAs/fechar-sujo — AppConfig aponta pra um subdiretório do MESMO _dir temporário
    // (nunca toca %AppData% real). confirmClose default (Cancelar) nunca é exercitado nos testes que
    // não mexem em documento sujo — só está aqui pra satisfazer o construtor.
    private MainViewModel VmFull(
        string? openResult = null,
        string? saveAsResult = null,
        Action<string>? notifyError = null,
        IConfirmCloseService? confirmClose = null,
        string? imageResult = null,
        StampGallery? stampGallery = null,
        string? saveResult = null,
        Action<string>? notifyInfo = null,
        IMergeDialogService? mergeDialog = null,
        ISplitDialogService? splitDialog = null,
        IPdfEditor? editor = null,
        IBatchSignDialogService? batchSignDialog = null,
        Func<IReadOnlyList<SigningCertificateInfo>>? listSigningCertificates = null,
        long? maxUndoRamBytes = null,
        long? maxUndoSpillBytes = null)
    {
        // Task 1 (Plano 5): maxUndoRamBytes/maxUndoSpillBytes OPCIONAIS (default null = defaults de
        // produção, ver DocumentSession) -- setados na MESMA instância de AppConfig ANTES de construir o
        // VM, pra provar que MainViewModel.OpenPath de fato LÊ esta config (não um literal hardcoded) ao
        // chamar DocumentSession.OpenAsync (ver OpenPath_PassesConfigUndoCeilings_...).
        var config = new AppConfig(Path.Combine(_dir, "config"));
        if (maxUndoRamBytes is { } ram) config.MaxUndoRamBytes = ram;
        if (maxUndoSpillBytes is { } spill) config.MaxUndoSpillBytes = spill;
        return new(new FakeDialog(openResult, saveAsResult, imageResult, saveResult),
            new RecentFilesStore(_dir),
            notifyError ?? (_ => { }),
            config,
            confirmClose ?? new FakeConfirmCloseService(CloseConfirmation.Cancel),
            stampGallery: stampGallery ?? new StampGallery(Path.Combine(_dir, "carimbos")),
            notifyInfo: notifyInfo ?? (_ => { }),
            mergeDialog: mergeDialog ?? new FakeMergeDialogService(null),
            splitDialog: splitDialog ?? new FakeSplitDialogService(null),
            editor: editor,
            batchSignDialog: batchSignDialog,
            listSigningCertificates: listSigningCertificates ?? (() => Array.Empty<SigningCertificateInfo>()));
    }

    // Mesmo padrão de DocumentSessionTests.CopyFixtureToTemp — testes de Save precisam de um arquivo
    // DESCARTÁVEL (não podem escrever em cima do fixture versionado no repo).
    private static string CopyFixtureToTemp()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-vm-save-{Guid.NewGuid():N}.pdf");
        File.Copy(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), tmp);
        return tmp;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    [Fact] // abrir caminho adiciona aba e seleciona
    public async Task OpenPath_AddsAndSelectsDocument()
    {
        var vm = Vm();
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        Assert.Single(vm.Documents);
        Assert.Same(vm.Documents[0], vm.SelectedDocument);
        Assert.Equal("fixture-a4.pdf", vm.Documents[0].Title);
    }

    [Fact] // diálogo cancelado (null) não adiciona nada
    public void OpenFileCommand_DialogCancelled_AddsNothing()
    {
        var vm = Vm();
        vm.OpenFileCommand.Execute(null);
        Assert.Empty(vm.Documents);
    }

    [Fact] // fechar aba LIMPA remove e faz dispose da sessão
    public async Task CloseDocument_RemovesFromCollection()
    {
        var vm = Vm();
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        var doc = vm.Documents[0];
        vm.CloseDocumentCommand.Execute(doc);
        Assert.Empty(vm.Documents);
    }

    [Fact] // Task 7: mesmo caminho completo (case-insensitive) já aberto -> seleciona a aba existente, não duplica
    public async Task OpenPath_SamePathTwice_SelectsExistingTab_NoDuplicate()
    {
        var vm = Vm();
        var path = Path.Combine(Fixtures.Root, "fixture-a4.pdf");

        await vm.OpenPath(path);
        var first = vm.Documents[0];
        await vm.OpenPath(path.ToUpperInvariant());

        Assert.Single(vm.Documents);
        Assert.Same(first, vm.SelectedDocument);
    }

    [Fact] // Task 7: recente que falha ao abrir some da lista persistida (notifyError injetado -> headless, sem MessageBox real)
    public async Task OpenPath_FailingPath_RemovesFromRecents_AndNotifiesError()
    {
        var recent = new RecentFilesStore(_dir);
        var badPath = Path.Combine(Path.GetTempPath(), $"nao-existe-mpdf-vm-{Guid.NewGuid():N}.pdf");
        recent.Add(badPath);
        string? notified = null;
        var vm = new MainViewModel(new FakeDialog(null), recent, msg => notified = msg);

        await vm.OpenPath(badPath);

        Assert.Empty(vm.Documents);
        Assert.DoesNotContain(badPath, recent.Load());
        Assert.NotNull(notified);
    }

    [Fact] // Revisão pós-Task 7: 2 chamadas concorrentes ao MESMO caminho (sem await entre elas — ex.:
    // OpenFileCommand e OpenRecentCommand disparados antes da 1ª abertura terminar) não podem abrir 2 abas
    public async Task OpenPath_ConcurrentSamePath_OpensOnlyOneTab()
    {
        var vm = Vm();
        var path = Path.Combine(Fixtures.Root, "fixture-a4.pdf");

        var t1 = vm.OpenPath(path);
        var t2 = vm.OpenPath(path);
        await Task.WhenAll(t1, t2);

        Assert.Single(vm.Documents);
    }

    // ---- Task 2 (Plano 7): Abrir imagem (.jpg/.jpeg/.png) -> converte e abre como documento novo NÃO
    // -SALVO, título "nome (convertido).pdf". FakePdfEditor (não sniffa de verdade) -> o conteúdo do
    // arquivo escrito aqui é irrelevante, só a EXTENSÃO decide se OpenPath entra no ramo de imagem.

    private string WriteTempImageFile(string fileName)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF });
        return path;
    }

    [Fact]
    public async Task OpenPath_ImagePath_ConvertsAndOpensAsUnsavedDocument_NeedsSaveAsTrue()
    {
        var imgPath = WriteTempImageFile("foto.jpg");
        var fake = new FakePdfEditor { ImageToPdfResult = Fixtures.A4() };
        var vm = VmFull(editor: fake);

        await vm.OpenPath(imgPath);

        Assert.Single(vm.Documents);
        Assert.Equal(1, fake.ImageToPdfCallCount);
        var doc = vm.Documents[0];
        Assert.Same(doc, vm.SelectedDocument);
        Assert.True(doc.NeedsSaveAs);
        Assert.Equal("foto (convertido).pdf", doc.Session.FileName);
        Assert.False(doc.IsDirty); // recém-escrito no temp -- OpenAsync considera "salvo" ali (ver relatório)
    }

    [Fact] // .pdf continua o fluxo normal -- NENHUMA chamada a ImageToPdf, NeedsSaveAs fica false
    public async Task OpenPath_PdfPath_DoesNotCallImageConversion()
    {
        var fake = new FakePdfEditor();
        var vm = VmFull(editor: fake);

        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));

        Assert.Equal(0, fake.ImageToPdfCallCount);
        Assert.False(vm.Documents[0].NeedsSaveAs);
    }

    [Fact] // motor recusa a conversão (corrompida/CMYK/teto) -- notificado pt-BR NOMEANDO o arquivo, nenhuma aba
    public async Task OpenPath_ImagePath_ConversionFails_NotifiesError_OpensNoDocument()
    {
        var imgPath = WriteTempImageFile("quebrada.png");
        var fake = new FakePdfEditor { ThrowOnImageToPdf = new PdfEditingException("Imagem corrompida.") };
        string? notified = null;
        var vm = VmFull(notifyError: msg => notified = msg, editor: fake);

        await vm.OpenPath(imgPath);

        Assert.Empty(vm.Documents);
        Assert.NotNull(notified);
        Assert.Contains("quebrada.png", notified);
    }

    [Fact] // extensão de imagem mas magic-bytes recusados -- mesma notificação nomeando o arquivo, ImageToPdf NUNCA chamado
    public async Task OpenPath_ImagePath_UnsupportedMagicBytes_NotifiesError_OpensNoDocument()
    {
        var imgPath = WriteTempImageFile("falsa.jpg");
        var fake = new FakePdfEditor { IsSupportedImageResult = false };
        string? notified = null;
        var vm = VmFull(notifyError: msg => notified = msg, editor: fake);

        await vm.OpenPath(imgPath);

        Assert.Empty(vm.Documents);
        Assert.NotNull(notified);
        Assert.Contains("falsa.jpg", notified);
        Assert.Equal(0, fake.ImageToPdfCallCount);
    }

    [Fact] // Dedupe (decisão registrada no relatório): abrir a MESMA imagem duas vezes -> DOIS documentos
    // (pastas temp com GUID diferente cada vez) -- diferente do dedupe por caminho de um PDF real.
    public async Task OpenPath_SameImageTwice_OpensTwoSeparateDocuments()
    {
        var imgPath = WriteTempImageFile("duplicada.jpg");
        var fake = new FakePdfEditor();
        var vm = VmFull(editor: fake);

        await vm.OpenPath(imgPath);
        await vm.OpenPath(imgPath);

        Assert.Equal(2, vm.Documents.Count);
        Assert.NotEqual(vm.Documents[0].Session.FilePath, vm.Documents[1].Session.FilePath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact] // Integração: motor REAL (VmFull sem editor -> PdfEditorFactory.Create()) + fixture real de foto.
    public async Task OpenPath_RealImageFixture_OpensAsOnePageUnsavedDocument()
    {
        var vm = VmFull();

        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-foto.jpg"));

        Assert.Single(vm.Documents);
        var doc = vm.Documents[0];
        Assert.Equal(1, doc.Session.Renderer.PageCount);
        Assert.True(doc.NeedsSaveAs);
        Assert.False(doc.IsDirty);
        Assert.Equal("fixture-foto (convertido).pdf", doc.Session.FileName);
    }

    // ---- Task 3 (Plano 3a): SaveCommand ------------------------------------------------------------

    [Fact] // "enabled when dirty": desabilitado num documento recém-aberto (limpo), habilita após Apply
    public async Task SaveCommand_DisabledWhenClean_EnabledAfterApply()
    {
        var vm = VmFull();
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.SelectedDocument!.Session.Apply(Fixtures.ThirtyPages());

        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact] // sem documento algum: SaveCommand/SaveAsCommand desabilitados (mesmo padrão de CanPrint)
    public void SaveAndSaveAsCommands_DisabledWithNoDocument()
    {
        var vm = VmFull();
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
    }

    [Fact] // ExecuteAsync chama a MESMA DocumentSession.Save de produção (agora em Task.Run — Task 2,
    // Plano 5): grava o snapshot, cria .bak (config default CriarBackup=true), limpa IsDirty ->
    // SaveCommand desabilita de novo.
    public async Task SaveCommand_WritesSnapshot_CreatesBackup_ClearsDirty()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            var vm = VmFull();
            await vm.OpenPath(tmp);
            vm.SelectedDocument!.Session.Apply(Fixtures.ThirtyPages());

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.False(vm.SelectedDocument.IsDirty);
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(tmp));
            Assert.True(File.Exists(tmp + ".bak"));
        }
        finally { File.Delete(tmp); TryDelete(tmp + ".bak"); }
    }

    [Fact] // erro de I/O (destino travado) vira notificação pt-BR; documento continua sujo
    public async Task SaveCommand_Fails_NotifiesError_DocumentStaysDirty()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            string? notified = null;
            var vm = VmFull(notifyError: msg => notified = msg);
            await vm.OpenPath(tmp);
            vm.SelectedDocument!.Session.Apply(Fixtures.ThirtyPages());

            using (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await vm.SaveCommand.ExecuteAsync(null);
            }

            Assert.NotNull(notified);
            Assert.True(vm.SelectedDocument.IsDirty);
        }
        finally { File.Delete(tmp); }
    }

    // Task 2 (Plano 5): IsSaving liga durante o await de Task.Run e desliga no finally, MESMO no
    // caminho de FALHA (destino travado) — o indicador nunca pode ficar "preso" ligado depois de um
    // erro (mesmo espírito de IsOpening, que também desliga incondicionalmente no finally de OpenPath).
    [Fact]
    public async Task SaveCommand_Fails_StillClearsIsSaving()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            var vm = VmFull();
            await vm.OpenPath(tmp);
            vm.SelectedDocument!.Session.Apply(Fixtures.ThirtyPages());

            using (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await vm.SaveCommand.ExecuteAsync(null);
            }

            Assert.False(vm.SelectedDocument.IsSaving);
        }
        finally { File.Delete(tmp); }
    }

    // Task 2 (Plano 5): IsSaving liga DURANTE o salvamento — provado observando o valor de dentro do
    // próprio Task.Run (via um AppConfig cuja leitura de CriarBackup é usada como ponto de observação
    // não é viável; em vez disso, um segundo Task que espera o pino armar e lê IsSaving nesse instante).
    [Fact]
    public async Task SaveCommand_IsSavingTrueWhileInFlight_FalseAfter()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            var vm = VmFull();
            await vm.OpenPath(tmp);
            var doc = vm.SelectedDocument!;
            doc.Session.Apply(Fixtures.ThirtyPages());

            var saveTask = vm.SaveCommand.ExecuteAsync(null); // não aguardado ainda

            // TryBeginEdit (dentro de Save) é síncrono, ANTES do 1º await -- pelo momento em que
            // ExecuteAsync devolve controle (suspenso no 1º await), o pino já está armado e IsSaving
            // já está true (setado ANTES do Task.Run, ver MainViewModel.Save).
            Assert.True(doc.IsSaving);
            Assert.True(doc.Session.IsEditInFlight);

            await saveTask;

            Assert.False(doc.IsSaving);
            Assert.False(doc.Session.IsEditInFlight);
        }
        finally { File.Delete(tmp); TryDelete(tmp + ".bak"); }
    }

    // Task 2 (Plano 5): CanSave/CanSaveAs compõem !Session.IsEditInFlight — uma edição JÁ em voo
    // (organizador, leitor, ou o próprio Save) desabilita os dois, fechando o minor do 3b pela via
    // inversa ("Save durante edição em voo" também vira "edição em voo bloqueia Save").
    [Fact]
    public async Task CanSave_And_CanSaveAs_FalseWhileEditInFlight()
    {
        var vm = VmFull();
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        var doc = vm.SelectedDocument!;
        doc.Session.Apply(Fixtures.ThirtyPages()); // suja -> CanSave normalmente true

        Assert.True(doc.Session.TryBeginEdit()); // simula qualquer comando mutador em voo
        try
        {
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.False(vm.SaveAsCommand.CanExecute(null));
        }
        finally { doc.Session.EndEdit(); }

        Assert.True(vm.SaveCommand.CanExecute(null)); // solto -> reabilitado
        Assert.True(vm.SaveAsCommand.CanExecute(null));
    }

    // ---- Task 3 (Plano 3a): SaveAsCommand ----------------------------------------------------------

    [Fact] // diálogo cancelado (null) -> FilePath não muda, nada é escrito
    public async Task SaveAsCommand_DialogCancelled_DoesNothing()
    {
        var path = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var vm = VmFull(saveAsResult: null);
        await vm.OpenPath(path);

        await vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(path, vm.SelectedDocument!.Session.FilePath);
    }

    [Fact] // escreve no NOVO caminho, atualiza FilePath, adiciona aos recentes (responsabilidade da VM)
    public async Task SaveAsCommand_WritesToNewPath_AddsToRecents()
    {
        var newPath = Path.Combine(Path.GetTempPath(), $"mpdf-vm-saveas-{Guid.NewGuid():N}.pdf");
        try
        {
            var vm = VmFull(saveAsResult: newPath);
            await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));

            await vm.SaveAsCommand.ExecuteAsync(null);

            Assert.Equal(newPath, vm.SelectedDocument!.Session.FilePath);
            Assert.True(File.Exists(newPath));
            Assert.Contains(newPath, vm.RecentFiles);
        }
        finally { TryDelete(newPath); }
    }

    // ---- Task 2 (Plano 7): Save/SaveAs num documento TEMP-BACKED (aberto de uma imagem convertida) --
    //
    // "Save exigindo Salvar como" (brief): Save NUNCA pode gravar silenciosamente de volta no arquivo
    // TEMP por trás deste tipo de documento — precisa desviar pro MESMO fluxo de "Salvar como".

    private async Task<(MainViewModel vm, DocumentViewModel doc)> OpenConvertedImageDirty(
        Action<string>? notifyError = null, string? saveAsResult = null, IConfirmCloseService? confirmClose = null)
    {
        var imgPath = WriteTempImageFile("foto-temp.jpg");
        var fake = new FakePdfEditor { ImageToPdfResult = Fixtures.A4() };
        var vm = VmFull(notifyError: notifyError, saveAsResult: saveAsResult, confirmClose: confirmClose, editor: fake);
        await vm.OpenPath(imgPath);
        var doc = vm.SelectedDocument!;
        doc.Session.Apply(Fixtures.ThirtyPages()); // suja -- CanSave passa a habilitado
        return (vm, doc);
    }

    [Fact] // Save num documento temp-backed -> desvia pro MESMO fluxo de SaveAs (diálogo, escreve no
    // destino ESCOLHIDO) -- o arquivo TEMP original nunca é reescrito por este Save.
    public async Task SaveCommand_TempBackedDocument_RoutesToSaveAs_NeverWritesTempPath()
    {
        var target = Path.Combine(_dir, "foto-temp (salva).pdf");
        var (vm, doc) = await OpenConvertedImageDirty(saveAsResult: target);
        var tempPath = doc.Session.FilePath;

        Assert.True(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(target, doc.Session.FilePath); // FilePath mudou -> prova que passou por SaveAs, não por Save
        Assert.True(File.Exists(target));
        Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(target));
        Assert.False(doc.NeedsSaveAs); // ganhou um "lar" definitivo -- próximo Save já pode gravar direto
        Assert.Equal(Fixtures.A4(), File.ReadAllBytes(tempPath)); // temp NUNCA reescrito (continua a conversão original, 1 página)
    }

    [Fact] // diálogo de "Salvar como" CANCELADO durante o Save desviado -> documento continua sujo, temp intocado
    public async Task SaveCommand_TempBackedDocument_SaveAsDialogCancelled_StaysDirtyAndTempUntouched()
    {
        var (vm, doc) = await OpenConvertedImageDirty(saveAsResult: null);
        var tempPath = doc.Session.FilePath;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(doc.IsDirty);
        Assert.True(doc.NeedsSaveAs);
        Assert.Equal(tempPath, doc.Session.FilePath);
        Assert.Equal(Fixtures.A4(), File.ReadAllBytes(tempPath));
    }

    [Fact] // fechar aba SUJA de um documento temp-backed, escolha "Salvar" -> pede Salvar Como, grava no
    // destino escolhido, NUNCA no temp (mesma garantia do Ctrl+S acima, agora pelo caminho de fechar aba).
    public async Task CloseDocument_DirtyTempBackedDocument_SaveChoice_PromptsSaveAs_WritesToChosenPath()
    {
        var target = Path.Combine(_dir, "foto-fechar (salva).pdf");
        var confirm = new FakeConfirmCloseService(CloseConfirmation.Save);
        var (vm, doc) = await OpenConvertedImageDirty(saveAsResult: target, confirmClose: confirm);
        var tempPath = doc.Session.FilePath;

        vm.CloseDocumentCommand.Execute(doc);

        Assert.Empty(vm.Documents);
        Assert.True(File.Exists(target));
        Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(target));
        Assert.Equal(Fixtures.A4(), File.ReadAllBytes(tempPath)); // temp nunca reescrito
        Assert.Contains(target, vm.RecentFiles);
    }

    [Fact] // "Salvar como" CANCELADO durante o fechamento -> aba NÃO fecha (mesma semântica de "Save que falha não fecha")
    public async Task CloseDocument_DirtyTempBackedDocument_SaveChoice_SaveAsCancelled_KeepsTabOpen()
    {
        var confirm = new FakeConfirmCloseService(CloseConfirmation.Save);
        var (vm, doc) = await OpenConvertedImageDirty(saveAsResult: null, confirmClose: confirm);

        vm.CloseDocumentCommand.Execute(doc);

        Assert.Single(vm.Documents);
    }

    // ---- Task 3 (Plano 3a): prompt de fechar sujo (3 caminhos) -------------------------------------

    [Fact] // documento LIMPO fecha DIRETO, sem perguntar nada — prova que o prompt só aparece quando sujo
    public async Task CloseDocument_CleanDocument_ClosesWithoutPrompting()
    {
        var confirm = new FakeConfirmCloseService(CloseConfirmation.Cancel);
        var vm = VmFull(confirmClose: confirm);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        var doc = vm.Documents[0];

        vm.CloseDocumentCommand.Execute(doc);

        Assert.Empty(vm.Documents);
        Assert.Equal(0, confirm.CallCount);
    }

    [Fact] // sujo + CANCELAR -> aba continua aberta, nada é salvo/descartado
    public async Task CloseDocument_Dirty_Cancel_KeepsTabOpen()
    {
        var confirm = new FakeConfirmCloseService(CloseConfirmation.Cancel);
        var vm = VmFull(confirmClose: confirm);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        var doc = vm.Documents[0];
        doc.Session.Apply(Fixtures.ThirtyPages());

        vm.CloseDocumentCommand.Execute(doc);

        Assert.Single(vm.Documents);
        Assert.Equal(1, confirm.CallCount);
    }

    [Fact] // sujo + DESCARTAR -> fecha SEM salvar (arquivo em disco continua o original, nunca tocado)
    public async Task CloseDocument_Dirty_Discard_ClosesWithoutSaving()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            var originalBytes = File.ReadAllBytes(tmp);
            var confirm = new FakeConfirmCloseService(CloseConfirmation.Discard);
            var vm = VmFull(confirmClose: confirm);
            await vm.OpenPath(tmp);
            var doc = vm.Documents[0];
            doc.Session.Apply(Fixtures.ThirtyPages());

            vm.CloseDocumentCommand.Execute(doc);

            Assert.Empty(vm.Documents);
            Assert.Equal(originalBytes, File.ReadAllBytes(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // sujo + SALVAR -> salva (mesma DocumentSession.Save de produção) E fecha
    public async Task CloseDocument_Dirty_Save_SavesThenCloses()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            var confirm = new FakeConfirmCloseService(CloseConfirmation.Save);
            var vm = VmFull(confirmClose: confirm);
            await vm.OpenPath(tmp);
            var doc = vm.Documents[0];
            doc.Session.Apply(Fixtures.ThirtyPages());

            vm.CloseDocumentCommand.Execute(doc);

            Assert.Empty(vm.Documents);
            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(tmp));
        }
        finally { File.Delete(tmp); TryDelete(tmp + ".bak"); }
    }

    [Fact] // sujo + SALVAR, mas o Save FALHA (destino travado) -> a aba NÃO fecha (nunca perder a
    // edição do usuário por um erro de I/O silencioso) e o erro é notificado.
    public async Task CloseDocument_Dirty_SaveFails_KeepsTabOpen_AndNotifiesError()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            string? notified = null;
            var confirm = new FakeConfirmCloseService(CloseConfirmation.Save);
            var vm = VmFull(notifyError: msg => notified = msg, confirmClose: confirm);
            await vm.OpenPath(tmp);
            var doc = vm.Documents[0];
            doc.Session.Apply(Fixtures.ThirtyPages());

            using (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                vm.CloseDocumentCommand.Execute(doc);
            }

            Assert.Single(vm.Documents);
            Assert.NotNull(notified);
        }
        finally { File.Delete(tmp); }
    }

    // ---- Fix pós-revisão (I3): ConfirmCloseAll — prompt ao fechar a JANELA inteira -----------------
    //
    // MainWindow.OnClosing chama ConfirmCloseAll() e cancela o fechamento (e.Cancel=true) se ela
    // devolver false — testável aqui na VM sem precisar de uma Window real (WPF Window.OnClosing não
    // é facilmente exercitável headless). Mesmo IConfirmCloseService/3 caminhos de CloseDocument.

    [Fact] // todos os documentos LIMPOS -> não pergunta nada, devolve true
    public async Task ConfirmCloseAll_AllClean_ReturnsTrueWithoutPrompting()
    {
        var confirm = new FakeConfirmCloseService(CloseConfirmation.Cancel);
        var vm = VmFull(confirmClose: confirm);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));

        Assert.True(vm.ConfirmCloseAll());
        Assert.Equal(0, confirm.CallCount);
    }

    [Fact] // sujo + CANCELAR -> ConfirmCloseAll devolve false (janela NÃO deve fechar)
    public async Task ConfirmCloseAll_Dirty_Cancel_ReturnsFalse()
    {
        var confirm = new FakeConfirmCloseService(CloseConfirmation.Cancel);
        var vm = VmFull(confirmClose: confirm);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        vm.Documents[0].Session.Apply(Fixtures.ThirtyPages());

        Assert.False(vm.ConfirmCloseAll());
    }

    [Fact] // sujo + DESCARTAR -> devolve true, mas NADA é salvo em disco
    public async Task ConfirmCloseAll_Dirty_Discard_ReturnsTrueWithoutSaving()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            var originalBytes = File.ReadAllBytes(tmp);
            var confirm = new FakeConfirmCloseService(CloseConfirmation.Discard);
            var vm = VmFull(confirmClose: confirm);
            await vm.OpenPath(tmp);
            vm.Documents[0].Session.Apply(Fixtures.ThirtyPages());

            Assert.True(vm.ConfirmCloseAll());
            Assert.Equal(originalBytes, File.ReadAllBytes(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // sujo + SALVAR -> salva (mesma DocumentSession.Save de produção) e devolve true
    public async Task ConfirmCloseAll_Dirty_Save_SavesAndReturnsTrue()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            var confirm = new FakeConfirmCloseService(CloseConfirmation.Save);
            var vm = VmFull(confirmClose: confirm);
            await vm.OpenPath(tmp);
            vm.Documents[0].Session.Apply(Fixtures.ThirtyPages());

            Assert.True(vm.ConfirmCloseAll());
            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(tmp));
        }
        finally { File.Delete(tmp); TryDelete(tmp + ".bak"); }
    }

    [Fact] // sujo + SALVAR, mas o Save FALHA (destino travado) -> devolve false (janela fica aberta) e notifica
    public async Task ConfirmCloseAll_Dirty_SaveFails_ReturnsFalse_AndNotifiesError()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            string? notified = null;
            var confirm = new FakeConfirmCloseService(CloseConfirmation.Save);
            var vm = VmFull(notifyError: msg => notified = msg, confirmClose: confirm);
            await vm.OpenPath(tmp);
            vm.Documents[0].Session.Apply(Fixtures.ThirtyPages());

            bool result;
            using (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = vm.ConfirmCloseAll();
            }

            Assert.False(result);
            Assert.NotNull(notified);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // MÚLTIPLOS documentos sujos: pára na PRIMEIRA negativa, sem perguntar aos demais
    public async Task ConfirmCloseAll_MultipleDirtyDocuments_StopsAtFirstCancel()
    {
        var confirm = new FakeConfirmCloseService(CloseConfirmation.Cancel);
        var vm = VmFull(confirmClose: confirm);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        vm.Documents[0].Session.Apply(Fixtures.ThirtyPages());
        vm.Documents[1].Session.Apply(Fixtures.A4());

        Assert.False(vm.ConfirmCloseAll());
        Assert.Equal(1, confirm.CallCount); // parou no primeiro documento sujo, nunca perguntou o segundo
    }

    // ---- Task 5 (Plano 3a): modo restrito para documentos assinados + "Editar uma cópia" -----------

    // fixture-carimbo copiada para um subdiretório PRÓPRIO do _dir temporário desta classe — cada
    // teste usa o SEU (subdir com nome distinto) pra "Editar uma cópia" poder escrever ao lado sem
    // testes concorrentes colidirem no mesmo nome de cópia.
    private string CopySignedFixtureToTemp(string subdirName)
    {
        var dir = Path.Combine(_dir, subdirName);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "assinado.pdf");
        File.Copy(Path.Combine(Fixtures.Root, "fixture-carimbo.pdf"), tmp);
        return tmp;
    }

    [Fact] // abrir um documento assinado liga o gate: IsSignedDocument true, CanEdit false, botão habilitado
    public async Task OpenPath_SignedFixture_SetsIsSignedDocumentTrue_AndEnablesEditCopy()
    {
        var vm = VmFull();
        var tmp = CopySignedFixtureToTemp("signed-gate-on");

        await vm.OpenPath(tmp);

        Assert.True(vm.SelectedDocument!.IsSignedDocument);
        Assert.False(vm.SelectedDocument.CanEdit);
        Assert.True(vm.EditCopyCommand.CanExecute(null));
    }

    [Fact] // documento SEM assinatura: gate desligado, botão desabilitado
    public async Task OpenPath_UnsignedFixture_IsSignedDocumentStaysFalse_EditCopyDisabled()
    {
        var vm = VmFull();

        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));

        Assert.False(vm.SelectedDocument!.IsSignedDocument);
        Assert.True(vm.SelectedDocument.CanEdit);
        Assert.False(vm.EditCopyCommand.CanExecute(null));
    }

    // ---- Task 2 (Plano 3c): cache de campos de formulário computado no fluxo já-async de OpenPath ----
    // (Obs 17 — NUNCA fire-and-forget num construtor; mesmo exemplar/precedente de IsSignedDocument
    // acima: PdfEditorFactory.Create() dedicado, não o `_editor` injetável — "não mexido" no isSigned,
    // mesma escolha aqui). Fixtures REAIS (não FakePdfEditor) — mesmo padrão dos 2 testes acima.

    [Fact]
    public async Task OpenPath_FormFixture_PopulatesFormFieldEditors_ExcludingOtherFields()
    {
        var vm = VmFull();

        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-formulario.pdf"));

        var doc = vm.SelectedDocument!;
        Assert.False(doc.IsXfaForm);
        Assert.True(doc.HasFormFields);
        // fixture-formulario.pdf: nome/observacoes/aceito (pág.0) + genero/estado/protocolo (pág.1) =
        // 6 campos editáveis; botao/assinatura1 (Other) FICAM DE FORA (Task 1 fix — nota de política).
        Assert.Equal(6, doc.FormFieldEditors.Count);
        Assert.DoesNotContain(doc.FormFieldEditors, f => f.Name is "botao" or "assinatura1");
        Assert.Contains(doc.FormFieldEditors, f => f.Name == "nome" && f.EditedValue == "Fulano de Tal");
    }

    [Fact]
    public async Task OpenPath_XfaFixture_SetsIsXfaFormTrue_FormFieldEditorsStayEmpty()
    {
        var vm = VmFull();

        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-xfa.pdf"));

        var doc = vm.SelectedDocument!;
        Assert.True(doc.IsXfaForm);
        Assert.False(doc.HasFormFields);
        Assert.Empty(doc.FormFieldEditors);
        Assert.False(doc.IsSignedDocument); // XFA sem assinatura nenhuma — HasSignatures agora é seguro
    }

    [Fact] // Important 2 (revisão): doc XFA-E-assinado — HasSignatures (corrigido no MOTOR) devolve o
    // valor REAL sem lançar; IsSignedDocument reflete isso (banner de assinado, "Editar uma cópia"
    // habilitado), mas CanEdit continua false de qualquer forma — o gate IsXfaForm cobre
    // independentemente do gate IsSignedDocument, os DOIS convivem sem conflito.
    public async Task OpenPath_XfaSignedFixture_SetsIsSignedDocumentTrue_CanEditStaysFalseViaXfaGate()
    {
        var vm = VmFull();

        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-xfa-assinado.pdf"));

        var doc = vm.SelectedDocument!;
        Assert.True(doc.IsXfaForm);
        Assert.True(doc.IsSignedDocument);
        Assert.False(doc.CanEdit);
        Assert.True(vm.EditCopyCommand.CanExecute(null)); // escape hatch de doc assinado disponível
        Assert.False(doc.HasFormFields); // XFA continua sem campos editáveis
    }

    [Fact]
    public async Task OpenPath_NoFormFixture_HasFormFieldsFalse_NotXfa()
    {
        var vm = VmFull();

        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));

        var doc = vm.SelectedDocument!;
        Assert.False(doc.IsXfaForm);
        Assert.False(doc.HasFormFields);
    }

    [Fact] // EditCopy: cria "<nome> (cópia editável).pdf" sem assinaturas, abre em aba NOVA já
    // editável (não-suja), e o arquivo original nunca é tocado.
    public async Task EditCopyCommand_CreatesUnsignedCopy_OpensNewTab_LeavesOriginalUntouched()
    {
        var vm = VmFull();
        var tmp = CopySignedFixtureToTemp("signed-editcopy-1");
        var originalBytes = File.ReadAllBytes(tmp);
        await vm.OpenPath(tmp);
        Assert.True(vm.EditCopyCommand.CanExecute(null));

        await vm.EditCopyCommand.ExecuteAsync(null);

        var expectedCopyPath = Path.Combine(Path.GetDirectoryName(tmp)!, "assinado (cópia editável).pdf");
        Assert.True(File.Exists(expectedCopyPath));
        Assert.False(PdfEditorFactory.Create().HasSignatures(File.ReadAllBytes(expectedCopyPath)));
        Assert.Equal(originalBytes, File.ReadAllBytes(tmp)); // original NUNCA tocado

        Assert.Equal(2, vm.Documents.Count); // aba original + aba nova da cópia
        Assert.Same(vm.Documents[1], vm.SelectedDocument); // OpenPath seleciona a aba recém-aberta
        Assert.Equal(expectedCopyPath, vm.SelectedDocument!.Session.FilePath);
        Assert.False(vm.SelectedDocument.IsSignedDocument); // a cópia É editável
        Assert.False(vm.SelectedDocument.IsDirty); // nasce NÃO-suja
    }

    [Fact] // colisão de nome: já existe uma "(cópia editável).pdf" -> a nova ganha " (2)"
    public async Task EditCopyCommand_NameCollision_AppendsCounterSuffix()
    {
        var vm = VmFull();
        var tmp = CopySignedFixtureToTemp("signed-editcopy-collision");
        var firstCopyPath = Path.Combine(Path.GetDirectoryName(tmp)!, "assinado (cópia editável).pdf");
        File.WriteAllBytes(firstCopyPath, Fixtures.A4()); // já existe algo com o nome "natural"
        await vm.OpenPath(tmp);

        await vm.EditCopyCommand.ExecuteAsync(null);

        var expectedSecondCopyPath = Path.Combine(Path.GetDirectoryName(tmp)!, "assinado (cópia editável) (2).pdf");
        Assert.True(File.Exists(expectedSecondCopyPath));
        Assert.Contains(vm.Documents, d => d.Session.FilePath == expectedSecondCopyPath);
        Assert.Equal(Fixtures.A4(), File.ReadAllBytes(firstCopyPath)); // a cópia PRÉ-EXISTENTE não é tocada
    }

    [Fact] // Important 2 (revisão pós-Task 2, Plano 7): EditCopy TRANSITIVAMENTE corrigido pelo fix
    // CRÍTICO de Sign/NeedsSaveAs -- depois que um documento temp-backed relocaliza (Salvar Como) ANTES
    // de assinar, `Session.FilePath` já é o caminho ESCOLHIDO pelo usuário; EditCopy deriva o nome da
    // cópia de `Session.FilePath` (`BuildEditableCopyPath`), então a cópia editável nasce IRMÃ do
    // caminho ESCOLHIDO -- pin de regressão pro fluxo completo "converter -> assinar (relocando) ->
    // editar uma cópia" (CanEditCopy exige IsSignedDocument -- depois do fix, todo doc assinado JÁ tem
    // um caminho real, nunca mais o temp original).
    public async Task EditCopyCommand_AfterSignRelocatedFromTempBacked_BuildsCopyNextToChosenPath_NotTemp()
    {
        var tempOriginal = CopyFixtureToTemp(); // simula o PDF temp de %TEMP%\mPDF\open-<guid>\
        var chosenDir = Path.Combine(_dir, "escolhido-pelo-usuario");
        Directory.CreateDirectory(chosenDir);
        var chosenTarget = Path.Combine(chosenDir, "meu documento assinado.pdf");
        try
        {
            using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
            var signDialogFake = new FakeSignDialogService(
                new SignDialogResult(cert, "Motivo", "Local", ApplyDocMdp: true, PlaceStamp: false));
            var saveAsDialogFake = new FakeDialog(openResult: null, saveAsResult: chosenTarget);

            var doc = new DocumentViewModel(
                DocumentSession.Open(tempOriginal),
                editor: PdfEditorFactory.Create(),
                config: new AppConfig(Path.Combine(_dir, "sign-config")),
                notifyError: _ => { }, notifyInfo: _ => { },
                dialogs: saveAsDialogFake,
                signDialog: signDialogFake,
                signingEngine: SigningEngineFactory.Create(),
                confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
                listSigningCertificates: () => new[] { new SigningCertificateInfo(cert, true, "Teste", false, false) })
            { NeedsSaveAs = true };

            await doc.SignCommand.ExecuteAsync(null);
            Assert.Equal(chosenTarget, doc.Session.FilePath); // sign relocalizou -- pré-condição deste teste
            Assert.True(doc.IsSignedDocument);

            var vm = VmFull();
            vm.Documents.Add(doc);
            vm.SelectedDocument = doc;
            Assert.True(vm.EditCopyCommand.CanExecute(null));

            await vm.EditCopyCommand.ExecuteAsync(null);

            var expectedCopyPath = Path.Combine(chosenDir, "meu documento assinado (cópia editável).pdf");
            var wrongTempSiblingPath = Path.Combine(
                Path.GetDirectoryName(tempOriginal)!, Path.GetFileNameWithoutExtension(tempOriginal) + " (cópia editável).pdf");
            Assert.True(File.Exists(expectedCopyPath)); // IRMÃ do caminho ESCOLHIDO
            Assert.False(File.Exists(wrongTempSiblingPath)); // NUNCA construída ao lado do temp original
            Assert.False(PdfEditorFactory.Create().HasSignatures(File.ReadAllBytes(expectedCopyPath)));
            TryDelete(expectedCopyPath);
        }
        finally { TryDelete(tempOriginal); TryDelete(chosenTarget); }
    }

    // ---- Deferência (Task 2, Plano 5): ApplyEditToSelectedDocument DELETADO --------------------------
    //
    // `MainViewModel.ApplyEditToSelectedDocument` (Task 5, Plano 3a) foi criado como um chokepoint
    // ANTECIPADO — "quando os comandos de anotação existirem, o DESPACHO de uma edição deve passar por
    // aqui" (comentário original). Não foi o que aconteceu: Tasks 6-9 (Plano 3a) e o restante do app
    // construíram cada comando mutador (ApplyMarkup/DeleteSelectedAnnotation/ApplyFormValues/
    // FlattenForm/Sign/organizador) DIRETO em `DocumentViewModel`/`OrganizerViewModel`, cada um com seu
    // PRÓPRIO catch de `PdfSignedDocumentException` (mesmo padrão de defesa em profundidade que este
    // método replicava) — nenhum deles nunca chamou `ApplyEditToSelectedDocument`. Grep confirmado
    // (revisão desta task): zero call sites de produção, só estes 2 testes. Método e testes DELETADOS
    // (não migrados) porque migrar duplicaria cobertura já existente por um caminho REAL:
    //   - "PdfSignedDocumentException -> notificado, snapshot intocado": já coberto por
    //     `DocumentViewModelTests.ApplyMarkupCommand_PdfSignedDocumentException_NotifiesError_
    //     LeavesSnapshotAndSelectionUnchanged` (e o mesmo par catch/notify em ApplyFormValues/
    //     FlattenForm, cada um com seu próprio teste).
    //   - "delegate produz snapshot novo -> ApplyEdit aplica de verdade, undo empilha": já coberto por
    //     qualquer teste de caminho feliz dos comandos reais acima (ex.: ApplyMarkupCommand, Rotate do
    //     organizador) — a mecânica de `Session.ApplyEdit` empilhando no undo não é específica deste
    //     método morto, é o contrato de `Session.ApplyEdit` em si (testado em DocumentSessionTests).
    // Também estava FORA do funil (`TryBeginEdit`/`EndEdit`) — mesmo se tivesse um call site, seria mais
    // um buraco a fechar, não um caminho a preservar.

    // ---- Task 9 (Plano 3a): galeria de carimbos de imagem ---------------------------------------------

    private static string WriteTempPng(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Fixtures.OnePixelPng());
        return path;
    }

    [Fact] // StampItems começa VAZIO numa galeria recém-criada (sem nenhum carimbo adicionado ainda).
    public void StampItems_EmptyGallery_StartsEmpty()
    {
        var vm = VmFull();
        Assert.Empty(vm.StampItems);
    }

    [Fact] // AddStampCommand: diálogo devolve um caminho -> StampGallery.Add copia -> StampItems reflete
    // o novo carimbo (nome + miniatura).
    public void AddStampCommand_CopiesIntoGallery_RefreshesStampItems()
    {
        string source = WriteTempPng(Path.Combine(_dir, "origem"), "logo.png");
        var vm = VmFull(imageResult: source);

        vm.AddStampCommand.Execute(null);

        var item = Assert.Single(vm.StampItems);
        Assert.Equal("logo.png", item.Name);
        Assert.NotNull(item.Thumbnail);
    }

    [Fact] // diálogo CANCELADO (null) -> nenhuma cópia, StampItems continua vazio.
    public void AddStampCommand_DialogCancelled_DoesNothing()
    {
        var vm = VmFull(imageResult: null);

        vm.AddStampCommand.Execute(null);

        Assert.Empty(vm.StampItems);
    }

    [Fact] // extensão fora de PNG/JPG -> notificado pt-BR, StampItems continua vazio (StampGallery.Add
    // lança ArgumentException, capturada aqui).
    public void AddStampCommand_InvalidExtension_NotifiesError_DoesNotAddItem()
    {
        string source = Path.Combine(_dir, "origem", "documento.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [1, 2, 3]);
        string? notified = null;
        var vm = VmFull(imageResult: source, notifyError: msg => notified = msg);

        vm.AddStampCommand.Execute(null);

        Assert.NotNull(notified);
        Assert.Empty(vm.StampItems);
    }

    [Fact] // RemoveStampCommand: apaga da galeria, StampItems atualiza.
    public void RemoveStampCommand_RemovesFromGallery_RefreshesStampItems()
    {
        string source = WriteTempPng(Path.Combine(_dir, "origem"), "carimbo.png");
        var vm = VmFull(imageResult: source);
        vm.AddStampCommand.Execute(null);
        Assert.Single(vm.StampItems);

        vm.RemoveStampCommand.Execute("carimbo.png");

        Assert.Empty(vm.StampItems);
    }

    [Fact] // SelectStampCommand: sem documento selecionado -> no-op (não há onde armar a ferramenta).
    public void SelectStampCommand_NoSelectedDocument_DoesNothing()
    {
        string source = WriteTempPng(Path.Combine(_dir, "origem"), "carimbo.png");
        var vm = VmFull(imageResult: source);
        vm.AddStampCommand.Execute(null);
        Assert.Null(vm.SelectedDocument);

        var ex = Record.Exception(() => vm.SelectStampCommand.Execute("carimbo.png"));

        Assert.Null(ex); // não lança, mesmo sem documento
    }

    [Fact] // SelectStampCommand: com documento selecionado, ativa a ferramenta de carimbo no documento
    // ATIVO com os bytes lidos da galeria (ponta a ponta: StampGallery.LoadBytes -> DocumentViewModel.ToggleStampTool).
    public async Task SelectStampCommand_WithSelectedDocument_ActivatesStampToolWithGalleryBytes()
    {
        string source = WriteTempPng(Path.Combine(_dir, "origem"), "carimbo.png");
        var vm = VmFull(imageResult: source);
        vm.AddStampCommand.Execute(null);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        Assert.Equal(AnnotationTool.None, vm.SelectedDocument!.ActiveTool);

        vm.SelectStampCommand.Execute("carimbo.png");

        Assert.Equal(AnnotationTool.ImageStamp, vm.SelectedDocument.ActiveTool);
    }

    // ---- Task 4 (Plano 3b): MergeCommand (Juntar) ---------------------------------------------------

    [Fact] // SEMPRE habilitado — funciona sem nenhum documento aberto (brief).
    public void MergeCommand_AlwaysEnabled_EvenWithNoDocumentOpen()
    {
        var vm = VmFull();
        Assert.True(vm.MergeCommand.CanExecute(null));
    }

    [Fact] // ordem preservada PONTA A PONTA: conteúdo real da página 0 é do 1º arquivo do diálogo, a
    // última página é do 2º — prova mais forte que só comparar contagens (que seriam iguais em qualquer
    // ordem). Roda SEM nenhum documento aberto de propósito (brief: "works without an open document").
    public async Task MergeCommand_PreservesOrder_WritesFile_OpensNewTab()
    {
        Directory.CreateDirectory(_dir);
        var savePath = Path.Combine(_dir, "unificado.pdf");
        var mergeDialog = new FakeMergeDialogService(new[]
        {
            Path.Combine(Fixtures.Root, "fixture-30p.pdf"), // 30 páginas, texto "pagina N de 30"
            Path.Combine(Fixtures.Root, "fixture-a4.pdf"),  // 1 página, texto "Fixture A4"
        });
        var infos = new List<string>();
        var vm = VmFull(saveResult: savePath, mergeDialog: mergeDialog, notifyInfo: infos.Add);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.True(File.Exists(savePath));
        using (var written = DocumentSession.Open(savePath))
        {
            Assert.Equal(31, written.Renderer.PageCount);
            Assert.Contains("pagina 1", written.Renderer.GetTextPage(0).Text);
            Assert.Contains("Fixture A4", written.Renderer.GetTextPage(30).Text); // última página = 2º arquivo
        }

        Assert.Single(vm.Documents); // abriu em aba NOVA (brief)
        Assert.Same(vm.Documents[0], vm.SelectedDocument);
        Assert.Equal(savePath, vm.SelectedDocument!.Session.FilePath);
        Assert.Equal(1, mergeDialog.CallCount);
        Assert.Single(infos); // notificação de sucesso pt-BR
    }

    [Fact] // diálogo de arquivos CANCELADO (null) -> nenhuma aba aberta, nenhum arquivo escrito
    public async Task MergeCommand_FilesDialogCancelled_DoesNothing()
    {
        var mergeDialog = new FakeMergeDialogService(null);
        var vm = VmFull(mergeDialog: mergeDialog);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Empty(vm.Documents);
    }

    [Fact] // SaveFileDialog CANCELADO (null) DEPOIS do merge já computado -> nenhum arquivo escrito, nenhuma aba
    public async Task MergeCommand_SaveDialogCancelled_WritesNoFile_OpensNoTab()
    {
        var mergeDialog = new FakeMergeDialogService(new[] { Path.Combine(Fixtures.Root, "fixture-a4.pdf") });
        var vm = VmFull(saveResult: null, mergeDialog: mergeDialog);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Empty(vm.Documents);
    }

    // ---- Task 2 (Plano 7): Juntar aceita imagens — conversão NA ENTRADA, por item, ANTES de
    // MergeDocuments (motor intocado: só recebe PDFs, mesmo se algumas entradas eram imagens no diálogo).

    [Fact] // lista mista PDF + imagem -- só a imagem passa por ImageToPdf; ORDEM preservada na lista final
    public async Task MergeCommand_MixedPdfAndImage_ConvertsImageOnly_PreservesOrder()
    {
        var pdfPath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var imgPath = WriteTempImageFile("pagina.jpg");
        var fake = new FakePdfEditor { ImageToPdfResult = Fixtures.ThirtyPages() };
        var mergeDialog = new FakeMergeDialogService(new[] { pdfPath, imgPath });
        // saveResult null (diálogo de salvar cancelado) -- MergeDocuments já rodou nesse ponto, então
        // basta pra provar entrada/ordem sem precisar gerenciar um arquivo de saída real.
        var vm = VmFull(saveResult: null, mergeDialog: mergeDialog, editor: fake);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.MergeDocumentsCallCount);
        Assert.NotNull(fake.LastMergeInputs);
        Assert.Equal(2, fake.LastMergeInputs!.Count);
        Assert.Equal(File.ReadAllBytes(pdfPath), fake.LastMergeInputs[0]); // pdf: bytes crus, NUNCA convertido
        Assert.Equal(Fixtures.ThirtyPages(), fake.LastMergeInputs[1]);     // imagem: resultado de ImageToPdf

        Assert.Single(fake.ImageToPdfInputs); // conversão chamada SÓ pra imagem, nunca pro pdf
        Assert.Equal(File.ReadAllBytes(imgPath), fake.ImageToPdfInputs[0]);
    }

    [Fact] // conversão de uma das imagens FALHA -- Juntar é ATÔMICO: aborta ANTES de MergeDocuments,
    // mensagem pt-BR NOMEIA o arquivo que falhou, nenhum arquivo é escrito, nenhuma aba abre.
    public async Task MergeCommand_ImageConversionFails_AbortsWithFilenameMessage_WritesNoFile()
    {
        var pdfPath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var imgPath = WriteTempImageFile("quebrada.jpg");
        var fake = new FakePdfEditor { ThrowOnImageToPdf = new PdfEditingException("Imagem corrompida.") };
        var mergeDialog = new FakeMergeDialogService(new[] { pdfPath, imgPath });
        string? notified = null;
        var vm = VmFull(notifyError: msg => notified = msg, mergeDialog: mergeDialog, editor: fake);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.MergeDocumentsCallCount);
        Assert.Empty(vm.Documents);
        Assert.NotNull(notified);
        Assert.Contains("quebrada.jpg", notified);
    }

    // ---- C1 (revisão final pré-merge, Plano 3b): aviso "documento(s) de origem assinado(s)" -----------

    [Fact] // fake reporta HasSignatures==true pra qualquer fonte -> aviso PLURAL (Merge aceita VÁRIAS
    // fontes, diferente de Extrair/Dividir — ver PdfEditor.MergeDocuments/mensagem em MainViewModel.Merge).
    public async Task MergeCommand_AnySourceSigned_AppendsUnsignedWarningToNotice()
    {
        Directory.CreateDirectory(_dir);
        var savePath = Path.Combine(_dir, "unificado-assinado.pdf");
        var fake = new FakePdfEditor { HasSignaturesResult = true };
        var mergeDialog = new FakeMergeDialogService(new[] { Path.Combine(Fixtures.Root, "fixture-a4.pdf") });
        var infos = new List<string>();
        var vm = VmFull(saveResult: savePath, mergeDialog: mergeDialog, notifyInfo: infos.Add, editor: fake);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Single(infos);
        Assert.Contains("assinado", infos[0]);
        Assert.Contains("NÃO está assinado", infos[0]);
    }

    [Fact] // fake reporta HasSignatures==false (default) -> notificação SEM o aviso extra.
    public async Task MergeCommand_NoSourceSigned_DoesNotAppendWarningToNotice()
    {
        Directory.CreateDirectory(_dir);
        var savePath = Path.Combine(_dir, "unificado-normal.pdf");
        var fake = new FakePdfEditor();
        var mergeDialog = new FakeMergeDialogService(new[] { Path.Combine(Fixtures.Root, "fixture-a4.pdf") });
        var infos = new List<string>();
        var vm = VmFull(saveResult: savePath, mergeDialog: mergeDialog, notifyInfo: infos.Add, editor: fake);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Single(infos);
        Assert.DoesNotContain("assinado", infos[0]);
    }

    // ---- Task 4 (Plano 3b): SplitCommand (Dividir) ---------------------------------------------------

    [Fact]
    public void SplitCommand_CanExecute_FalseWithoutDocument()
    {
        var vm = VmFull();
        Assert.False(vm.SplitCommand.CanExecute(null));
    }

    [Fact]
    public async Task SplitCommand_CanExecute_TrueWithDocumentOpen()
    {
        var vm = VmFull();
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        Assert.True(vm.SplitCommand.CanExecute(null));
    }

    [Fact] // fluxo feliz: 2 partes gravadas em disco com o nome "nome (parte K).pdf" na pasta escolhida
    public async Task SplitCommand_WritesPartFiles_WithExpectedNamesAndPageCounts()
    {
        var destDir = Path.Combine(_dir, "dividido");
        Directory.CreateDirectory(destDir);
        string? notified = null;
        var infos = new List<string>();
        var splitDialog = new FakeSplitDialogService(("1-5, 6-30", destDir));
        var vm = VmFull(notifyError: msg => notified = msg, notifyInfo: infos.Add, splitDialog: splitDialog);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-30p.pdf")); // 30 páginas

        await vm.SplitCommand.ExecuteAsync(null);

        var part1 = Path.Combine(destDir, "fixture-30p (parte 1).pdf");
        var part2 = Path.Combine(destDir, "fixture-30p (parte 2).pdf");
        Assert.True(File.Exists(part1));
        Assert.True(File.Exists(part2));
        using (var p1 = DocumentSession.Open(part1)) Assert.Equal(5, p1.Renderer.PageCount);
        using (var p2 = DocumentSession.Open(part2)) Assert.Equal(25, p2.Renderer.PageCount);
        Assert.Null(notified);
        Assert.Single(infos);
        Assert.Contains("2", infos[0]); // "2 arquivos criados em ..."
        Assert.Contains(destDir, infos[0]);
    }

    [Fact] // colisão de nome: já existe "fixture-30p (parte 1).pdf" -> a nova ganha " (2)" (exemplar:
    // BuildEditableCopyPath/StampGallery.Add); o arquivo PRÉ-EXISTENTE não é tocado.
    public async Task SplitCommand_NameCollision_AppendsCounterSuffix_LeavesExistingFileUntouched()
    {
        var destDir = Path.Combine(_dir, "dividido-colisao");
        Directory.CreateDirectory(destDir);
        var preExisting = Path.Combine(destDir, "fixture-30p (parte 1).pdf");
        File.WriteAllBytes(preExisting, Fixtures.A4());
        var splitDialog = new FakeSplitDialogService(("1-5, 6-30", destDir));
        var vm = VmFull(splitDialog: splitDialog);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));

        await vm.SplitCommand.ExecuteAsync(null);

        var collided = Path.Combine(destDir, "fixture-30p (parte 1) (2).pdf");
        Assert.True(File.Exists(collided));
        Assert.Equal(Fixtures.A4(), File.ReadAllBytes(preExisting)); // pré-existente intocado
    }

    [Fact] // string de intervalos inválida (fora dos limites) -> PageRangeParser lança ANTES de chamar o
    // motor — notificado pt-BR, nenhum arquivo é escrito.
    public async Task SplitCommand_InvalidRangeString_NotifiesError_WritesNoFiles()
    {
        var destDir = Path.Combine(_dir, "dividido-invalido");
        Directory.CreateDirectory(destDir);
        string? notified = null;
        var splitDialog = new FakeSplitDialogService(("1-999", destDir)); // fixture-30p só tem 30 páginas
        var vm = VmFull(notifyError: msg => notified = msg, splitDialog: splitDialog);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));

        await vm.SplitCommand.ExecuteAsync(null);

        Assert.NotNull(notified);
        Assert.Empty(Directory.GetFiles(destDir));
    }

    [Fact] // diálogo CANCELADO (null) -> nada acontece, sem exceção
    public async Task SplitCommand_DialogCancelled_DoesNothing()
    {
        var splitDialog = new FakeSplitDialogService(null);
        var vm = VmFull(splitDialog: splitDialog);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));

        var ex = await Record.ExceptionAsync(() => vm.SplitCommand.ExecuteAsync(null));

        Assert.Null(ex);
        Assert.Equal(1, splitDialog.CallCount);
    }

    // ---- Fix pós-revisão (Important): catch amplo de ArgumentException em Merge/Split -----------------
    //
    // MergeDocuments/SplitByRanges também podem lançar ArgumentException CRUA (lista vazia / índice
    // inválido, ver Contract.cs) — mesmo par de catches que OrganizerViewModel.TryRunEditAsync já aplica
    // pra Rotate/Delete/Move. Em uso normal esse caminho é INALCANÇÁVEL (o guard de lista vazia em Merge
    // e o pré-validação do PageRangeParser em Split já impedem chegar ao motor com um input que ele
    // rejeitaria) — por isso os 2 testes abaixo usam um FakePdfEditor injetado (ThrowOnMergeDocuments/
    // ThrowOnSplitByRanges, mesmos campos já usados por OrganizerViewModelTests/DocumentViewModelTests)
    // pra provar o catch DIRETAMENTE, sem depender de um input real capaz de furar o guard.

    [Fact]
    public async Task MergeCommand_EngineThrowsArgumentException_NotifiesError_WritesNoFile()
    {
        var fake = new FakePdfEditor { ThrowOnMergeDocuments = new ArgumentException("MergeDocuments requer ao menos 1 documento.") };
        string? notified = null;
        var mergeDialog = new FakeMergeDialogService(new[] { Path.Combine(Fixtures.Root, "fixture-a4.pdf") });
        var vm = VmFull(notifyError: msg => notified = msg, mergeDialog: mergeDialog, editor: fake);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.MergeDocumentsCallCount);
        Assert.NotNull(notified);
        Assert.Contains("MergeDocuments", notified);
        Assert.Empty(vm.Documents); // nenhuma aba aberta — falhou antes do SaveFileDialog/WriteNewFile
    }

    [Fact]
    public async Task SplitCommand_EngineThrowsArgumentException_NotifiesError_WritesNoFiles()
    {
        var destDir = Path.Combine(_dir, "dividido-argexcept");
        Directory.CreateDirectory(destDir);
        var fake = new FakePdfEditor { ThrowOnSplitByRanges = new ArgumentException("Intervalo inválido: fim (0) antes do início (5).") };
        string? notified = null;
        var splitDialog = new FakeSplitDialogService(("1-5", destDir)); // válido pro PARSER — a falha vem do MOTOR (fake)
        var vm = VmFull(notifyError: msg => notified = msg, splitDialog: splitDialog, editor: fake);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));

        await vm.SplitCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.SplitByRangesCallCount);
        Assert.NotNull(notified);
        Assert.Contains("Intervalo inválido", notified);
        Assert.Empty(Directory.GetFiles(destDir));
    }

    // ---- Fix pós-revisão (minor 3): SplitCommand em documento ASSINADO (paridade com Extrair) --------

    [Fact] // SplitByRanges é leitura pura, SEM gate de assinatura (mesma política de ExtractPages/
    // MergeDocuments — Contract.cs) — CanSplit fica true e o Split PRODUZ partes sem assinatura
    // (StripSignatures defensivo do motor), mesmo com o documento de origem assinado. C1 (revisão
    // final pré-merge): editor REAL (não fake) + fixture REALMENTE assinada — prova PONTA A PONTA
    // (não só com um FakePdfEditor.HasSignaturesResult) que a notificação de sucesso ganha o aviso.
    public async Task SplitCommand_SignedDocument_CanExecuteTrue_ProducesUnsignedParts()
    {
        var destDir = Path.Combine(_dir, "dividido-assinado");
        Directory.CreateDirectory(destDir);
        var tmp = CopySignedFixtureToTemp("signed-split");
        var splitDialog = new FakeSplitDialogService(("1", destDir));
        var infos = new List<string>();
        var vm = VmFull(splitDialog: splitDialog, notifyInfo: infos.Add);
        await vm.OpenPath(tmp);
        Assert.True(vm.SelectedDocument!.IsSignedDocument);

        Assert.True(vm.SplitCommand.CanExecute(null));

        await vm.SplitCommand.ExecuteAsync(null);

        var part1 = Path.Combine(destDir, "assinado (parte 1).pdf");
        Assert.True(File.Exists(part1));
        Assert.False(PdfEditorFactory.Create().HasSignatures(File.ReadAllBytes(part1)));

        Assert.Single(infos);
        Assert.Contains("assinado", infos[0]);
        Assert.Contains("NÃO está assinado", infos[0]);
    }

    // ---- C1 (revisão final pré-merge, Plano 3b): aviso "documento de origem assinado" (Split) ---------

    [Fact] // fake reporta HasSignatures==false (default), origem NÃO assinada -> notificação SEM aviso.
    public async Task SplitCommand_SourceUnsigned_DoesNotAppendWarningToNotice()
    {
        var destDir = Path.Combine(_dir, "dividido-sem-aviso");
        Directory.CreateDirectory(destDir);
        var splitDialog = new FakeSplitDialogService(("1-5, 6-30", destDir));
        var infos = new List<string>();
        var vm = VmFull(notifyInfo: infos.Add, splitDialog: splitDialog);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));

        await vm.SplitCommand.ExecuteAsync(null);

        Assert.Single(infos);
        Assert.DoesNotContain("assinado", infos[0]);
    }

    [Fact] // Rider: singular quando só 1 arquivo é criado ("1 arquivo criado", não "1 arquivos criados").
    public async Task SplitCommand_SinglePart_UsesSingularNoticeText()
    {
        var destDir = Path.Combine(_dir, "dividido-singular");
        Directory.CreateDirectory(destDir);
        var splitDialog = new FakeSplitDialogService(("1-30", destDir)); // 1 range só -> 1 arquivo
        var infos = new List<string>();
        var vm = VmFull(notifyInfo: infos.Add, splitDialog: splitDialog);
        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));

        await vm.SplitCommand.ExecuteAsync(null);

        Assert.Single(infos);
        Assert.StartsWith("1 arquivo criado", infos[0]);
        Assert.DoesNotContain("1 arquivos", infos[0]);
    }

    // ---- Task 5 (Plano 4): 🖊 Assinar em lote -----------------------------------------------------------

    [Fact] // mesmo espírito de Merge: opera sobre arquivos externos, habilitado mesmo sem NENHUM documento
    // aberto (nenhum CanExecute no comando).
    public void BatchSignCommand_AlwaysEnabled_EvenWithoutOpenDocument()
    {
        var vm = VmFull(batchSignDialog: new SpyBatchSignDialogService());
        Assert.True(vm.BatchSignCommand.CanExecute(null));
    }

    [Fact] // BatchSign só COORDENA: constrói o VM e delega pra seam -- prova que a seam É alcançada com
    // os certificados do catálogo injetado (não o catálogo real do Windows).
    public void BatchSignCommand_BuildsViewModel_WithCatalogCertificates_AndShowsDialog()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var certs = new SigningCertificateInfo[] { new(cert, IsRsa: true, "Cert Teste", false, false) };
        var spy = new SpyBatchSignDialogService();
        var vm = VmFull(batchSignDialog: spy, listSigningCertificates: () => certs);

        vm.BatchSignCommand.Execute(null);

        Assert.Equal(1, spy.CallCount);
        Assert.NotNull(spy.LastViewModel);
        Assert.Single(spy.LastViewModel!.Certificates);
    }

    [Fact] // WIRING real (risco do plano, brief): o predicado que MainViewModel.BatchSign passa pro
    // BatchSignViewModel (IsPathOpenInAnyTab) reflete Documents DE VERDADE -- abre via OpenPath real
    // (não um predicado fabricado à mão). `internal` (mesmo precedente de `ApplyEditToSelectedDocument`)
    // pra ser testável direto, sem reflexão nem abrir a janela do diálogo.
    public async Task IsPathOpenInAnyTab_ReflectsRealOpenDocuments()
    {
        var vm = VmFull();
        var openPath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        await vm.OpenPath(openPath); // abre de verdade numa aba
        var closedPath = Path.Combine(Fixtures.Root, "fixture-30p.pdf"); // nunca aberto nesta sessão

        Assert.True(vm.IsPathOpenInAnyTab(openPath));
        Assert.False(vm.IsPathOpenInAnyTab(closedPath));
    }

    // ---- Task 1 (Plano 5): teto de bytes no undo -- fiação PONTA A PONTA -----------------------------

    [Fact] // WIRING real (mesmo espírito de IsPathOpenInAnyTab_ReflectsRealOpenDocuments acima): prova
    // que OpenPath de fato LÊ AppConfig.MaxUndoRamBytes/MaxUndoSpillBytes (não um literal hardcoded) ao
    // chamar DocumentSession.OpenAsync -- a cadeia inteira AppConfig -> MainViewModel.OpenPath ->
    // DocumentSession.OpenAsync -> SnapshotStack -> DocumentSession.UndoHistoryLimitReached ->
    // DocumentViewModel -> _notifyInfo, cada elo já provado isolado nos outros arquivos de teste.
    public async Task OpenPath_PassesConfigUndoCeilings_ToDocumentSession_NoticeFlowsToNotifyInfo()
    {
        long unit = Fixtures.A4().LongLength;
        var infos = new List<string>();
        var vm = VmFull(notifyInfo: infos.Add, maxUndoRamBytes: unit, maxUndoSpillBytes: unit * 2);

        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        var doc = vm.Documents[0];
        for (int i = 0; i < 5; i++) doc.Session.ApplyEdit(Fixtures.A4()); // aritmética completa no relatório

        Assert.Single(infos);
        Assert.Equal(
            "Limite de histórico atingido; as edições mais antigas não podem mais ser desfeitas.",
            infos[0]);
    }

    [Fact] // controle negativo: SEM os ceilings customizados (defaults de produção), a mesma sequência
    // de edições NUNCA dispara o aviso -- prova que o teste acima realmente depende da config passada,
    // não de qualquer ApplyEdit em sequência.
    public async Task OpenPath_WithDefaultConfigCeilings_NeverRaisesUndoHistoryNotice()
    {
        var infos = new List<string>();
        var vm = VmFull(notifyInfo: infos.Add); // ceilings OMITIDOS -- defaults de produção

        await vm.OpenPath(Path.Combine(Fixtures.Root, "fixture-a4.pdf"));
        var doc = vm.Documents[0];
        for (int i = 0; i < 5; i++) doc.Session.ApplyEdit(Fixtures.A4());

        Assert.Empty(infos);
    }
}
