using System.IO;
using System.Linq;
using System.Reflection;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

// ---- fakes locais mínimos (file-scoped, mesmo padrão de MainViewModelTests/OrganizerViewModelTests) ---

file sealed class SpyFileDialogService(string? saveResult = null) : IFileDialogService
{
    public string? PickPdfToOpen() => null;
    public string? PickPdfToSaveAs(string currentPath) => null;
    public string? PickImageToImport() => null;
    public string? PickPdfToSave(string suggestedName) => saveResult;
}

file sealed class SpyAnnotationTextDialogService(string? result) : IAnnotationTextDialogService
{
    public string? PromptForText(string title, string? initialText = null) => result;
}

file sealed class SpyMergeDialogService(IReadOnlyList<string>? result) : IMergeDialogService
{
    public IReadOnlyList<string>? PickFilesToMerge() => result;
}

file sealed class SpyConfirmCloseService(CloseConfirmation result) : IConfirmCloseService
{
    public int CallCount { get; private set; }
    public CloseConfirmation Confirm(string documentTitle) { CallCount++; return result; }
}

file sealed class SpyConfirmFlattenService(bool result) : IConfirmFlattenService
{
    public int CallCount { get; private set; }
    public bool Confirm(string message) { CallCount++; return result; }
}

file sealed class SpyConfirmOrganizerScaleService(bool result) : IConfirmOrganizerScaleService
{
    public int CallCount { get; private set; }
    public bool Confirm(string message) { CallCount++; return result; }
}

file sealed class SpyConfirmSaveBeforeSignService(bool result) : IConfirmSaveBeforeSignService
{
    public int CallCount { get; private set; }
    public bool Confirm(string message) { CallCount++; return result; }
}

file sealed class SpySignDialogService(SignDialogResult? result) : ISignDialogService
{
    public int CallCount { get; private set; }
    public SignDialogResult? PromptForSignature(
        IReadOnlyList<SigningCertificateInfo> certificates, bool allowDocMdp,
        RubricaGallery rubricas, Func<byte[]?> pickRubrica)
    { CallCount++; return result; }
}

file sealed class SpyBatchSignDialogService : IBatchSignDialogService
{
    public int CallCount { get; private set; }
    public void ShowBatchSignDialog(BatchSignViewModel viewModel) => CallCount++;
}

file sealed class SpyExportImageDialogService : IExportImageDialogService
{
    public int CallCount { get; private set; }
    public void ShowExportImageDialog(ExportImageViewModel viewModel) => CallCount++;
}

/// <summary>
/// Task 0 (Plano 3c) — prova de disparo (designing-guard-rails + Obs 19) pra cada um dos 9 membros de
/// <see cref="UiPrompts"/> que <see cref="UiPromptsTestGuard"/> troca. Disciplina seguida em CADA teste
/// "fires":
///   1. PLANTIO: constrói o VM real com o parâmetro-alvo OMITIDO (usa o default `?? UiPrompts.X`) — os
///      outros parâmetros necessários pra alcançar o caminho recebem fakes MUDOS/inertes (nunca a
///      versão real de produção), pra isolar qual default específico está sendo provado.
///   2. VERIFICAÇÃO DO PLANTIO (segura, nunca invoca UI real): antes de chamar o caminho perigoso,
///      confirma via reflexão que `UiPrompts.X` JÁ é a versão trocada pelo `[ModuleInitializer]`
///      (`AssertSwapped`/`AssertSwappedFactory` abaixo) — prova que a exceção que vem a seguir tem como
///      ORIGEM o initializer, não um acidente de código morto que nunca seria alcançado de qualquer jeito.
///   3. DISPARO: invoca o comando/método REAL do VM (não um mock do próprio VM) que alcança o default —
///      prova que o caminho de PRODUÇÃO de verdade foi exercitado, não só a seam isoladamente.
///   4. Controle NEGATIVO (pelo menos 1 por família diálogo/notify): troca `UiPrompts.X` LOCALMENTE pra
///      um substituto BENIGNO (nunca a implementação de produção real — chamar `MessageBox.Show`/
///      `OpenFileDialog` de verdade travaria esta própria suíte, o mesmo bug que esta task existe pra
///      evitar) e prova que o MESMO caminho NÃO lança — a exceção do passo 3 vem da troca do
///      initializer, não de um bug em outro lugar do VM. Sempre em try/finally restaurando a versão
///      throwing (as próximas classes de teste desta suíte dependem da seam continuar "armada").
/// </summary>
public class UiPromptsGuardTests
{
    // ==================================================================================================
    // Verificação SEGURA de que o [ModuleInitializer] já trocou os 9 membros — nunca invoca um diálogo
    // real: pra Action<string>, inspeciona Delegate.Method.DeclaringType (nunca chama o delegate); pra
    // Func<T> de fábrica, CHAMA a fábrica (construir um FileDialogService/... é inerte — nenhuma delas
    // tem construtor custom, só os MÉTODOS de diálogo mostram UI) e confere o TIPO do objeto devolvido.
    // ==================================================================================================

    private static void AssertActionSwapped(Action<string> action, string seamMemberName)
    {
        Assert.True(action.Method.DeclaringType != typeof(UiPrompts),
            $"UiPrompts.{seamMemberName} ainda aponta pra uma implementação de PRODUÇÃO — " +
            "UiPromptsTestGuard.Install() não rodou (ou rodou tarde demais). NÃO prossiga: invocar este " +
            "caminho abriria um MessageBox real.");
    }

    private static void AssertFactorySwapped<TThrowing>(Func<object> factory, string seamMemberName)
    {
        var instance = factory(); // seguro: construir *ThrowingXxxService é inerte, nunca mostra UI
        Assert.True(instance.GetType() == typeof(TThrowing),
            $"UiPrompts.{seamMemberName} devolveu {instance.GetType().Name}, esperado {typeof(TThrowing).Name} " +
            "-- UiPromptsTestGuard.Install() não trocou este membro. NÃO prossiga.");
    }

    [Fact]
    public void ModuleInitializer_SwappedAllSeamMembers_BeforeThisTestRan()
    {
        AssertActionSwapped(UiPrompts.NotifyInfo, nameof(UiPrompts.NotifyInfo));
        AssertActionSwapped(UiPrompts.MainNotifyError, nameof(UiPrompts.MainNotifyError));
        AssertActionSwapped(UiPrompts.DocumentNotifyError, nameof(UiPrompts.DocumentNotifyError));
        AssertFactorySwapped<ThrowingFileDialogService>(() => UiPrompts.CreateFileDialog(), nameof(UiPrompts.CreateFileDialog));
        AssertFactorySwapped<ThrowingAnnotationTextDialogService>(() => UiPrompts.CreateAnnotationDialog(), nameof(UiPrompts.CreateAnnotationDialog));
        AssertFactorySwapped<ThrowingMergeDialogService>(() => UiPrompts.CreateMergeDialog(), nameof(UiPrompts.CreateMergeDialog));
        AssertFactorySwapped<ThrowingSplitDialogService>(() => UiPrompts.CreateSplitDialog(), nameof(UiPrompts.CreateSplitDialog));
        AssertFactorySwapped<ThrowingConfirmCloseService>(() => UiPrompts.CreateConfirmClose(), nameof(UiPrompts.CreateConfirmClose));
        AssertFactorySwapped<ThrowingConfirmFlattenService>(() => UiPrompts.CreateConfirmFlatten(), nameof(UiPrompts.CreateConfirmFlatten));
        AssertFactorySwapped<ThrowingConfirmSaveBeforeSignService>(() => UiPrompts.CreateConfirmSaveBeforeSign(), nameof(UiPrompts.CreateConfirmSaveBeforeSign));
        AssertFactorySwapped<ThrowingSignDialogService>(() => UiPrompts.CreateSignDialog(), nameof(UiPrompts.CreateSignDialog));
        AssertFactorySwapped<ThrowingBatchSignDialogService>(() => UiPrompts.CreateBatchSignDialog(), nameof(UiPrompts.CreateBatchSignDialog));
        AssertFactorySwapped<ThrowingConfirmOrganizerScaleService>(() => UiPrompts.CreateConfirmOrganizerScale(), nameof(UiPrompts.CreateConfirmOrganizerScale));
        AssertFactorySwapped<ThrowingExportImageDialogService>(() => UiPrompts.CreateExportImageDialog(), nameof(UiPrompts.CreateExportImageDialog));
        // Task 3 (Plano 16):
        AssertFactorySwapped<ThrowingExportDocumentDialogService>(() => UiPrompts.CreateExportDocumentDialog(), nameof(UiPrompts.CreateExportDocumentDialog));
        // Task 2 (Plano 11):
        AssertFactorySwapped<ThrowingSobreDialogService>(() => UiPrompts.CreateSobreDialog(), nameof(UiPrompts.CreateSobreDialog));
        // Task 2 (Plano 17):
        AssertFactorySwapped<ThrowingConfiguracoesDialogService>(() => UiPrompts.CreateConfiguracoesDialog(), nameof(UiPrompts.CreateConfiguracoesDialog));
        AssertFactorySwapped<ThrowingUpdateSource>(() => UiPrompts.CreateUpdateSource(), nameof(UiPrompts.CreateUpdateSource));
        AssertFactorySwapped<ThrowingConfirmInstallUpdateService>(() => UiPrompts.CreateConfirmInstallUpdate(), nameof(UiPrompts.CreateConfirmInstallUpdate));
        // Task 4 (Plano 15):
        AssertFactorySwapped<ThrowingOcrProgressService>(() => UiPrompts.CreateOcrProgress(), nameof(UiPrompts.CreateOcrProgress));
    }

    private static string A4Path => Path.Combine(Fixtures.Root, "fixture-a4.pdf");

    // Task 1 (Plano 5): fixture de 201 páginas construída em RUNTIME (motor REAL) sobre 201 cópias de
    // fixture-a4.pdf -- mesmo exemplar de DocumentViewModelTests.Doc201Pages (duplicado aqui, não
    // compartilhado entre arquivos de teste, pra manter cada suíte auto-contida).
    private static readonly Lazy<byte[]> Doc201Pages = new(() =>
        PdfEditorFactory.Create().MergeDocuments(Enumerable.Repeat(Fixtures.A4(), 201).ToArray()));

    private static string WriteTempPdf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mpdf-uiprompts-organizer-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ==================================================================================================
    // DocumentViewModel — 5 defaults: notifyError, annotationDialog, dialogs (encaminhado pro
    // Organizer), notifyInfo (encaminhado pro Organizer), confirmFlatten.
    // ==================================================================================================

    [Fact]
    public async Task DocumentViewModel_AnnotationDialogOmitted_PlaceAnnotation_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingAnnotationTextDialogService>(() => UiPrompts.CreateAnnotationDialog(), nameof(UiPrompts.CreateAnnotationDialog));

        using var doc = new DocumentViewModel(DocumentSession.Open(A4Path)); // annotationDialog OMITIDO
        doc.ActiveTool = AnnotationTool.StickyNote;

        var ex = await Record.ExceptionAsync(() => doc.PlaceAnnotationAtAsync(0, 100, 700));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateAnnotationDialog), ioe.Message);
    }

    [Fact] // controle negativo: com um fake BENIGNO local, o MESMO caminho não lança — prova que a
    // exceção acima vem da troca do initializer, não de um bug em PlaceAnnotationAtAsync.
    public async Task DocumentViewModel_AnnotationDialogOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateAnnotationDialog;
        try
        {
            UiPrompts.CreateAnnotationDialog = () => new SpyAnnotationTextDialogService(null); // cancelado, nunca lança
            using var doc = new DocumentViewModel(DocumentSession.Open(A4Path));
            doc.ActiveTool = AnnotationTool.StickyNote;

            var ex = await Record.ExceptionAsync(() => doc.PlaceAnnotationAtAsync(0, 100, 700));

            Assert.Null(ex);
            Assert.Equal(AnnotationTool.StickyNote, doc.ActiveTool); // cancelado -> ferramenta continua ativa (contrato existente)
        }
        finally { UiPrompts.CreateAnnotationDialog = original; }
    }

    [Fact]
    public async Task DocumentViewModel_NotifyErrorOmitted_SignedDocumentEdit_ThrowsViaUiPrompts()
    {
        AssertActionSwapped(UiPrompts.DocumentNotifyError, nameof(UiPrompts.DocumentNotifyError));

        var fake = new FakePdfEditor { ThrowOnAddAnnotation = new PdfSignedDocumentException("assinado") };
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            annotationDialog: new SpyAnnotationTextDialogService("texto")); // notifyError OMITIDO
        doc.ActiveTool = AnnotationTool.StickyNote;

        var ex = await Record.ExceptionAsync(() => doc.PlaceAnnotationAtAsync(0, 100, 700));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.DocumentNotifyError), ioe.Message);
        Assert.Equal(1, fake.AddAnnotationCallCount); // prova que o caminho REAL (_editor.AddAnnotation) foi alcançado
    }

    [Fact] // controle negativo (DocumentNotifyError): fake benigno local -> mensagem capturada, sem lançar.
    public async Task DocumentViewModel_NotifyErrorOmitted_NegativeControl_WithBenignAction_DoesNotThrow()
    {
        var original = UiPrompts.DocumentNotifyError;
        var captured = new List<string>();
        try
        {
            UiPrompts.DocumentNotifyError = captured.Add;
            var fake = new FakePdfEditor { ThrowOnAddAnnotation = new PdfSignedDocumentException("assinado") };
            using var doc = new DocumentViewModel(
                DocumentSession.Open(A4Path), editor: fake, annotationDialog: new SpyAnnotationTextDialogService("texto"));
            doc.ActiveTool = AnnotationTool.StickyNote;

            var ex = await Record.ExceptionAsync(() => doc.PlaceAnnotationAtAsync(0, 100, 700));

            Assert.Null(ex);
            Assert.Single(captured);
        }
        finally { UiPrompts.DocumentNotifyError = original; }
    }

    [Fact] // dialogs de DocumentViewModel é ENCAMINHADO pro Organizer que ele mesmo cria — prova que a
    // fiação de DocumentViewModel (não só a de OrganizerViewModel, provada abaixo) alcança UiPrompts.
    public async Task DocumentViewModel_DialogsOmitted_OrganizerExtractSelected_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingFileDialogService>(() => UiPrompts.CreateFileDialog(), nameof(UiPrompts.CreateFileDialog));

        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")),
            notifyInfo: _ => { }); // dialogs OMITIDO; notifyInfo faked pra isolar (não é o alvo deste teste)
        doc.IsOrganizerOpen = true;
        doc.Organizer!.ToggleSelect(0, ctrl: false);

        var ex = await Record.ExceptionAsync(() => doc.Organizer!.ExtractSelectedCommand.ExecuteAsync(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateFileDialog), ioe.Message);
    }

    [Fact]
    public async Task DocumentViewModel_NotifyInfoOmitted_OrganizerExtractSelected_ThrowsViaUiPrompts()
    {
        AssertActionSwapped(UiPrompts.NotifyInfo, nameof(UiPrompts.NotifyInfo));

        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-uiprompts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            using var doc = new DocumentViewModel(
                DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")),
                dialogs: new SpyFileDialogService(Path.Combine(tmpDir, "extraido.pdf"))); // notifyInfo OMITIDO
            doc.IsOrganizerOpen = true;
            doc.Organizer!.ToggleSelect(0, ctrl: false);

            var ex = await Record.ExceptionAsync(() => doc.Organizer!.ExtractSelectedCommand.ExecuteAsync(null));

            var ioe = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains(nameof(UiPrompts.NotifyInfo), ioe.Message);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact] // Task 3 (Plano 3c): confirmFlatten OMITIDO -> FlattenFormCommand alcança o diálogo de
    // produção via UiPrompts.CreateConfirmFlatten, trocado pelo guard pra uma versão que LANÇA.
    public async Task DocumentViewModel_ConfirmFlattenOmitted_FlattenForm_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingConfirmFlattenService>(() => UiPrompts.CreateConfirmFlatten(), nameof(UiPrompts.CreateConfirmFlatten));

        var fake = new FakePdfEditor();
        var fields = new[] { new FormFieldData("nome", FormFieldType.Text, "Original", Array.Empty<string>(), 0, null, IsReadOnly: false) };
        using var doc = new DocumentViewModel(DocumentSession.Open(A4Path), editor: fake); // confirmFlatten OMITIDO
        doc.SeedFormFieldsCache(false, fields);

        var ex = await Record.ExceptionAsync(() => doc.FlattenFormCommand.ExecuteAsync(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateConfirmFlatten), ioe.Message);
        Assert.Equal(0, fake.FlattenFormCallCount); // o diálogo é consultado ANTES do funil/motor
    }

    [Fact] // controle negativo (confirmFlatten): fake benigno local devolvendo `false` (cancelado) ->
    // não lança, e o motor nunca é alcançado.
    public async Task DocumentViewModel_ConfirmFlattenOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateConfirmFlatten;
        try
        {
            UiPrompts.CreateConfirmFlatten = () => new SpyConfirmFlattenService(false);
            var fake = new FakePdfEditor();
            var fields = new[] { new FormFieldData("nome", FormFieldType.Text, "Original", Array.Empty<string>(), 0, null, IsReadOnly: false) };
            using var doc = new DocumentViewModel(DocumentSession.Open(A4Path), editor: fake);
            doc.SeedFormFieldsCache(false, fields);

            var ex = await Record.ExceptionAsync(() => doc.FlattenFormCommand.ExecuteAsync(null));

            Assert.Null(ex);
            Assert.Equal(0, fake.FlattenFormCallCount);
        }
        finally { UiPrompts.CreateConfirmFlatten = original; }
    }

    [Fact] // Task 1 (Plano 5): confirmOrganizerScale OMITIDO -> abrir o organizador num documento com
    // mais de 200 páginas alcança o diálogo de produção via UiPrompts.CreateConfirmOrganizerScale,
    // trocado pelo guard pra uma versão que LANÇA.
    public void DocumentViewModel_ConfirmOrganizerScaleOmitted_OpenOrganizerOn201Pages_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingConfirmOrganizerScaleService>(() => UiPrompts.CreateConfirmOrganizerScale(), nameof(UiPrompts.CreateConfirmOrganizerScale));

        var path = WriteTempPdf(Doc201Pages.Value);
        try
        {
            using var doc = new DocumentViewModel(DocumentSession.Open(path)); // confirmOrganizerScale OMITIDO

            var ex = Record.Exception(() => doc.IsOrganizerOpen = true);

            var ioe = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains(nameof(UiPrompts.CreateConfirmOrganizerScale), ioe.Message);
            Assert.Null(doc.Organizer); // o diálogo é consultado ANTES de abrir
        }
        finally { File.Delete(path); }
    }

    [Fact] // controle negativo (confirmOrganizerScale): fake benigno local devolvendo `true` (continuar)
    // -> não lança, organizador abre normalmente.
    public void DocumentViewModel_ConfirmOrganizerScaleOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateConfirmOrganizerScale;
        var path = WriteTempPdf(Doc201Pages.Value);
        try
        {
            UiPrompts.CreateConfirmOrganizerScale = () => new SpyConfirmOrganizerScaleService(true);
            using var doc = new DocumentViewModel(DocumentSession.Open(path));

            var ex = Record.Exception(() => doc.IsOrganizerOpen = true);

            Assert.Null(ex);
            Assert.NotNull(doc.Organizer);
        }
        finally
        {
            UiPrompts.CreateConfirmOrganizerScale = original;
            File.Delete(path);
        }
    }

    [Fact] // Task 3 (Plano 4): confirmSaveBeforeSign OMITIDO -> SignCommand alcança o diálogo de
    // produção via UiPrompts.CreateConfirmSaveBeforeSign (só consultado quando o doc está SUJO).
    public async Task DocumentViewModel_ConfirmSaveBeforeSignOmitted_SignDirtyDoc_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingConfirmSaveBeforeSignService>(() => UiPrompts.CreateConfirmSaveBeforeSign(), nameof(UiPrompts.CreateConfirmSaveBeforeSign));

        using var doc = new DocumentViewModel(DocumentSession.Open(A4Path)); // confirmSaveBeforeSign OMITIDO
        doc.Session.Apply(Fixtures.ThirtyPages()); // suja o documento
        Assert.True(doc.IsDirty);

        var ex = await Record.ExceptionAsync(() => doc.SignCommand.ExecuteAsync(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateConfirmSaveBeforeSign), ioe.Message);
    }

    [Fact] // controle negativo (confirmSaveBeforeSign): fake benigno local devolvendo `false` (recusado)
    // -> não lança, e o diálogo de assinatura (CreateSignDialog) nunca é alcançado.
    public async Task DocumentViewModel_ConfirmSaveBeforeSignOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateConfirmSaveBeforeSign;
        try
        {
            UiPrompts.CreateConfirmSaveBeforeSign = () => new SpyConfirmSaveBeforeSignService(false);
            using var doc = new DocumentViewModel(DocumentSession.Open(A4Path));
            doc.Session.Apply(Fixtures.ThirtyPages());

            var ex = await Record.ExceptionAsync(() => doc.SignCommand.ExecuteAsync(null));

            Assert.Null(ex);
        }
        finally { UiPrompts.CreateConfirmSaveBeforeSign = original; }
    }

    [Fact] // Task 3 (Plano 4): signDialog OMITIDO -> SignCommand alcança o diálogo de produção via
    // UiPrompts.CreateSignDialog (doc LIMPO -> confirmSaveBeforeSign nem é consultado, isola o alvo).
    public async Task DocumentViewModel_SignDialogOmitted_Sign_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingSignDialogService>(() => UiPrompts.CreateSignDialog(), nameof(UiPrompts.CreateSignDialog));

        using var doc = new DocumentViewModel(DocumentSession.Open(A4Path)); // signDialog OMITIDO

        var ex = await Record.ExceptionAsync(() => doc.SignCommand.ExecuteAsync(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateSignDialog), ioe.Message);
    }

    [Fact] // controle negativo (signDialog): fake benigno local devolvendo `null` (cancelado) -> não
    // lança, e nenhuma edição é armada.
    public async Task DocumentViewModel_SignDialogOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateSignDialog;
        try
        {
            UiPrompts.CreateSignDialog = () => new SpySignDialogService(null);
            using var doc = new DocumentViewModel(DocumentSession.Open(A4Path));

            var ex = await Record.ExceptionAsync(() => doc.SignCommand.ExecuteAsync(null));

            Assert.Null(ex);
            Assert.False(doc.Session.IsEditInFlight);
        }
        finally { UiPrompts.CreateSignDialog = original; }
    }

    [Fact] // Task 4 (Plano 7): exportImageDialog OMITIDO -> ExportImageCommand alcança o diálogo de
    // produção via UiPrompts.CreateExportImageDialog, trocado pelo guard pra uma versão que LANÇA.
    public void DocumentViewModel_ExportImageDialogOmitted_ExportImageCommand_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingExportImageDialogService>(() => UiPrompts.CreateExportImageDialog(), nameof(UiPrompts.CreateExportImageDialog));

        using var doc = new DocumentViewModel(DocumentSession.Open(A4Path)); // exportImageDialog OMITIDO

        var ex = Record.Exception(() => doc.ExportImageCommand.Execute(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateExportImageDialog), ioe.Message);
    }

    [Fact] // controle negativo (exportImageDialog): fake benigno local -> não lança, diálogo "mostrado" 1x.
    public void DocumentViewModel_ExportImageDialogOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateExportImageDialog;
        try
        {
            var spy = new SpyExportImageDialogService();
            UiPrompts.CreateExportImageDialog = () => spy;
            using var doc = new DocumentViewModel(DocumentSession.Open(A4Path));

            var ex = Record.Exception(() => doc.ExportImageCommand.Execute(null));

            Assert.Null(ex);
            Assert.Equal(1, spy.CallCount);
        }
        finally { UiPrompts.CreateExportImageDialog = original; }
    }

    [Fact] // Task 4 (Plano 15): ocrProgress OMITIDO -> RecognizeText alcança a faixa de progresso de
    // produção via UiPrompts.CreateOcrProgress (só na FASE 2, quando há página-alvo), trocada pelo guard
    // pra uma versão que LANÇA. Rasterizer fake (1 página SEM texto) garante que a fase 2 é alcançada.
    public async Task DocumentViewModel_OcrProgressOmitted_RecognizeText_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingOcrProgressService>(() => UiPrompts.CreateOcrProgress(), nameof(UiPrompts.CreateOcrProgress));

        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false })); // ocrProgress OMITIDO

        var ex = await Record.ExceptionAsync(() => doc.RecognizeTextCoreAsync());

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateOcrProgress), ioe.Message);
    }

    [Fact] // controle negativo (ocrProgress): fake benigno local -> não lança, faixa "aberta" 1x.
    public async Task DocumentViewModel_OcrProgressOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateOcrProgress;
        try
        {
            var spy = new FakeOcrProgressService();
            UiPrompts.CreateOcrProgress = () => spy;
            using var doc = new DocumentViewModel(
                DocumentSession.Open(A4Path),
                ocrEngine: new FakeOcrEngine(),
                rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false }),
                notifyInfo: _ => { }, notifyError: _ => { });

            var ex = await Record.ExceptionAsync(() => doc.RecognizeTextCoreAsync());

            Assert.Null(ex);
            Assert.Equal(1, spy.StartCount);
        }
        finally { UiPrompts.CreateOcrProgress = original; }
    }

    // ==================================================================================================
    // OrganizerViewModel — construído DIRETO (não via DocumentViewModel) — 2 defaults: dialogs, notifyInfo.
    // ==================================================================================================

    private static (OrganizerViewModel vm, DocumentSession session) BuildOrganizer(
        IFileDialogService? dialogs = null, Action<string>? notifyInfo = null, string fixture = "fixture-30p.pdf")
    {
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, fixture));
        var vm = new OrganizerViewModel(session, new FakePdfEditor(), _ => { }, () => true, dialogs, notifyInfo);
        return (vm, session);
    }

    [Fact]
    public async Task OrganizerViewModel_DialogsOmitted_ExtractSelected_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingFileDialogService>(() => UiPrompts.CreateFileDialog(), nameof(UiPrompts.CreateFileDialog));

        var (vm, session) = BuildOrganizer(notifyInfo: _ => { }); // dialogs OMITIDO
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            var ex = await Record.ExceptionAsync(() => vm.ExtractSelectedCommand.ExecuteAsync(null));

            var ioe = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains(nameof(UiPrompts.CreateFileDialog), ioe.Message);
        }
    }

    [Fact] // controle negativo (dialogs de OrganizerViewModel): fake benigno local que devolve `null`
    // (cancelado) -> ExtractSelected retorna cedo, sem lançar.
    public async Task OrganizerViewModel_DialogsOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateFileDialog;
        try
        {
            UiPrompts.CreateFileDialog = () => new SpyFileDialogService(saveResult: null);
            var (vm, session) = BuildOrganizer(notifyInfo: _ => { });
            using (session) using (vm)
            {
                vm.ToggleSelect(0, ctrl: false);
                var ex = await Record.ExceptionAsync(() => vm.ExtractSelectedCommand.ExecuteAsync(null));
                Assert.Null(ex);
            }
        }
        finally { UiPrompts.CreateFileDialog = original; }
    }

    [Fact]
    public async Task OrganizerViewModel_NotifyInfoOmitted_ExtractSelected_ThrowsViaUiPrompts()
    {
        AssertActionSwapped(UiPrompts.NotifyInfo, nameof(UiPrompts.NotifyInfo));

        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-uiprompts-org-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var (vm, session) = BuildOrganizer(dialogs: new SpyFileDialogService(Path.Combine(tmpDir, "extraido.pdf")));
            using (session) using (vm)
            {
                vm.ToggleSelect(0, ctrl: false);
                var ex = await Record.ExceptionAsync(() => vm.ExtractSelectedCommand.ExecuteAsync(null));

                var ioe = Assert.IsType<InvalidOperationException>(ex);
                Assert.Contains(nameof(UiPrompts.NotifyInfo), ioe.Message);
            }
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    // ==================================================================================================
    // MainViewModel — 4 defaults do ctor de 11 argumentos (annotationDialog, notifyInfo, mergeDialog,
    // splitDialog) + 2 defaults ENCADEADOS dos overloads de conveniência (MainNotifyError no ctor de 2
    // args, CreateConfirmClose no ctor de 3 args) — não alcançáveis pela reflexão de
    // UiPromptsCoverageTests (não são parâmetros opcionais), provados aqui diretamente.
    // ==================================================================================================

    private static string TempDir([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(Path.GetTempPath(), $"mpdf-uiprompts-main-{name}-{Guid.NewGuid():N}");

    private MainViewModel BuildMain(
        string dir,
        IAnnotationTextDialogService? annotationDialog = null,
        Action<string>? notifyInfo = null,
        IMergeDialogService? mergeDialog = null,
        ISplitDialogService? splitDialog = null,
        IFileDialogService? dialogs = null,
        IBatchSignDialogService? batchSignDialog = null) =>
        new(dialogs ?? new SpyFileDialogService(),
            new RecentFilesStore(dir),
            _ => { },
            new AppConfig(Path.Combine(dir, "config")),
            new SpyConfirmCloseService(CloseConfirmation.Cancel),
            annotationDialog: annotationDialog,
            stampGallery: new StampGallery(Path.Combine(dir, "carimbos")),
            notifyInfo: notifyInfo,
            mergeDialog: mergeDialog,
            splitDialog: splitDialog,
            editor: new FakePdfEditor(),
            batchSignDialog: batchSignDialog);

    [Fact]
    public async Task MainViewModel_MergeDialogOmitted_MergeCommand_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingMergeDialogService>(() => UiPrompts.CreateMergeDialog(), nameof(UiPrompts.CreateMergeDialog));

        var dir = TempDir();
        var vm = BuildMain(dir); // mergeDialog OMITIDO

        var ex = await Record.ExceptionAsync(() => vm.MergeCommand.ExecuteAsync(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateMergeDialog), ioe.Message);
    }

    [Fact] // controle negativo (mergeDialog): fake benigno local -> null (cancelado) -> Merge retorna cedo.
    public async Task MainViewModel_MergeDialogOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateMergeDialog;
        try
        {
            UiPrompts.CreateMergeDialog = () => new SpyMergeDialogService(null);
            var vm = BuildMain(TempDir());
            var ex = await Record.ExceptionAsync(() => vm.MergeCommand.ExecuteAsync(null));
            Assert.Null(ex);
        }
        finally { UiPrompts.CreateMergeDialog = original; }
    }

    [Fact]
    public async Task MainViewModel_SplitDialogOmitted_SplitCommand_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingSplitDialogService>(() => UiPrompts.CreateSplitDialog(), nameof(UiPrompts.CreateSplitDialog));

        var dir = TempDir();
        var vm = BuildMain(dir); // splitDialog OMITIDO
        await vm.OpenPath(A4Path); // Split exige SelectedDocument (CanSplit)

        var ex = await Record.ExceptionAsync(() => vm.SplitCommand.ExecuteAsync(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateSplitDialog), ioe.Message);
    }

    [Fact] // Task 5 (Plano 4): batchSignDialog OMITIDO -> BatchSignCommand alcança o diálogo de produção
    // via UiPrompts.CreateBatchSignDialog. `CertificateCatalog.ListSigningCertificates()` (produção) só
    // lê o repositório do Windows read-only, sem UI nenhuma -- não faz esta suíte travar (mesma isenção
    // documentada em DocumentViewModel/`_listSigningCertificates`).
    public void MainViewModel_BatchSignDialogOmitted_BatchSignCommand_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingBatchSignDialogService>(() => UiPrompts.CreateBatchSignDialog(), nameof(UiPrompts.CreateBatchSignDialog));

        var vm = BuildMain(TempDir()); // batchSignDialog OMITIDO

        var ex = Record.Exception(() => vm.BatchSignCommand.Execute(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateBatchSignDialog), ioe.Message);
    }

    [Fact] // controle negativo (batchSignDialog): fake benigno local -> não lança, diálogo "mostrado" 1x.
    public void MainViewModel_BatchSignDialogOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateBatchSignDialog;
        try
        {
            var spy = new SpyBatchSignDialogService();
            UiPrompts.CreateBatchSignDialog = () => spy;
            var vm = BuildMain(TempDir());

            var ex = Record.Exception(() => vm.BatchSignCommand.Execute(null));

            Assert.Null(ex);
            Assert.Equal(1, spy.CallCount);
        }
        finally { UiPrompts.CreateBatchSignDialog = original; }
    }

    [Fact]
    public async Task MainViewModel_AnnotationDialogOmitted_PlaceAnnotationOnOpenedDoc_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingAnnotationTextDialogService>(() => UiPrompts.CreateAnnotationDialog(), nameof(UiPrompts.CreateAnnotationDialog));

        var dir = TempDir();
        var vm = BuildMain(dir); // annotationDialog OMITIDO -> propagado pra cada DocumentViewModel aberto (ver OpenPath)
        await vm.OpenPath(A4Path);
        var doc = vm.SelectedDocument!;
        doc.ActiveTool = AnnotationTool.StickyNote;

        var ex = await Record.ExceptionAsync(() => doc.PlaceAnnotationAtAsync(0, 100, 700));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateAnnotationDialog), ioe.Message);
    }

    [Fact]
    public async Task MainViewModel_NotifyInfoOmitted_SuccessfulMerge_ThrowsViaUiPrompts()
    {
        AssertActionSwapped(UiPrompts.NotifyInfo, nameof(UiPrompts.NotifyInfo));

        var dir = TempDir();
        var savePath = Path.Combine(dir, "unificado.pdf");
        Directory.CreateDirectory(dir);
        var vm = BuildMain(dir,
            mergeDialog: new SpyMergeDialogService([A4Path, A4Path]),
            dialogs: new SpyFileDialogService(savePath)); // notifyInfo OMITIDO

        var ex = await Record.ExceptionAsync(() => vm.MergeCommand.ExecuteAsync(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.NotifyInfo), ioe.Message);
        Assert.True(File.Exists(savePath)); // prova que o caminho REAL (merge + escrita) foi alcançado antes do notify
    }

    // ---- overloads de conveniência (defaults ENCADEADOS, não parâmetros opcionais) --------------------

    [Fact] // ctor de 2 argumentos encadeia UiPrompts.MainNotifyError (era `DefaultNotifyError` antes da Task 0)
    public async Task MainViewModel_TwoArgCtor_OpenPathFailure_ThrowsViaMainNotifyError()
    {
        AssertActionSwapped(UiPrompts.MainNotifyError, nameof(UiPrompts.MainNotifyError));

        var dir = TempDir();
        var vm = new MainViewModel(new SpyFileDialogService(), new RecentFilesStore(dir));
        var badPath = Path.Combine(Path.GetTempPath(), $"nao-existe-{Guid.NewGuid():N}.pdf");

        var ex = await Record.ExceptionAsync(() => vm.OpenPath(badPath));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.MainNotifyError), ioe.Message);
    }

    [Fact] // controle negativo (MainNotifyError): fake benigno local -> mensagem capturada, sem lançar.
    public async Task MainViewModel_TwoArgCtor_NegativeControl_WithBenignAction_DoesNotThrow()
    {
        var original = UiPrompts.MainNotifyError;
        var captured = new List<string>();
        try
        {
            UiPrompts.MainNotifyError = captured.Add;
            var dir = TempDir();
            var vm = new MainViewModel(new SpyFileDialogService(), new RecentFilesStore(dir));
            var badPath = Path.Combine(Path.GetTempPath(), $"nao-existe-{Guid.NewGuid():N}.pdf");

            var ex = await Record.ExceptionAsync(() => vm.OpenPath(badPath));

            Assert.Null(ex);
            Assert.Single(captured);
        }
        finally { UiPrompts.MainNotifyError = original; }
    }

    [Fact] // ctor de 3 argumentos encadeia UiPrompts.CreateConfirmClose() (era `new
    // MessageBoxConfirmCloseService()` antes da Task 0) -- fecha um documento SUJO sem confirmClose explícito.
    public async Task MainViewModel_ThreeArgCtor_CloseDirtyDocument_ThrowsViaCreateConfirmClose()
    {
        AssertFactorySwapped<ThrowingConfirmCloseService>(() => UiPrompts.CreateConfirmClose(), nameof(UiPrompts.CreateConfirmClose));

        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var vm = new MainViewModel(new SpyFileDialogService(), new RecentFilesStore(dir), _ => { });
        await vm.OpenPath(A4Path);
        var doc = vm.SelectedDocument!;
        doc.Session.Apply(Fixtures.ThirtyPages()); // suja o documento (mesmo padrão de DocumentViewModelTests)
        Assert.True(doc.IsDirty);

        var ex = Record.Exception(() => vm.CloseDocumentCommand.Execute(doc));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateConfirmClose), ioe.Message);
    }

    [Fact] // Task 2 (Plano 11): sobreDialog OMITIDO -> SobreCommand alcança o diálogo de produção via
    // UiPrompts.CreateSobreDialog, trocado pelo guard pra uma versão que LANÇA.
    public void MainViewModel_SobreDialogOmitted_SobreCommand_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingSobreDialogService>(() => UiPrompts.CreateSobreDialog(), nameof(UiPrompts.CreateSobreDialog));

        var vm = BuildMain(TempDir()); // sobreDialog OMITIDO

        var ex = Record.Exception(() => vm.SobreCommand.Execute(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateSobreDialog), ioe.Message);
    }

    [Fact] // controle negativo (sobreDialog): fake benigno local -> não lança, diálogo "mostrado" 1x.
    public void MainViewModel_SobreDialogOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateSobreDialog;
        try
        {
            var spy = new SpySobreDialogService();
            UiPrompts.CreateSobreDialog = () => spy;
            var vm = BuildMain(TempDir());

            var ex = Record.Exception(() => vm.SobreCommand.Execute(null));

            Assert.Null(ex);
            Assert.Equal(1, spy.CallCount);
        }
        finally { UiPrompts.CreateSobreDialog = original; }
    }

    [Fact] // Task 2 (Plano 17): configuracoesDialog OMITIDO -> ConfiguracoesCommand alcança o diálogo de
    // produção via UiPrompts.CreateConfiguracoesDialog, trocado pelo guard pra uma versão que LANÇA.
    public void MainViewModel_ConfiguracoesDialogOmitted_ConfiguracoesCommand_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingConfiguracoesDialogService>(() => UiPrompts.CreateConfiguracoesDialog(), nameof(UiPrompts.CreateConfiguracoesDialog));

        var vm = BuildMain(TempDir()); // configuracoesDialog OMITIDO

        var ex = Record.Exception(() => vm.ConfiguracoesCommand.Execute(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateConfiguracoesDialog), ioe.Message);
    }

    [Fact] // controle negativo (configuracoesDialog): fake benigno local -> não lança, diálogo "mostrado" 1x.
    public void MainViewModel_ConfiguracoesDialogOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateConfiguracoesDialog;
        try
        {
            var spy = new SpyConfiguracoesDialogService();
            UiPrompts.CreateConfiguracoesDialog = () => spy;
            var vm = BuildMain(TempDir());

            var ex = Record.Exception(() => vm.ConfiguracoesCommand.Execute(null));

            Assert.Null(ex);
            Assert.Equal(1, spy.CallCount);
        }
        finally { UiPrompts.CreateConfiguracoesDialog = original; }
    }

    // ==================================================================================================
    // ConfiguracoesViewModel (Task 2, Plano 17 — MIGRADO de SobreViewModel) — 2 defaults via UiPrompts:
    // createSource, confirmInstall (os 3 delegates de instalação — confirmCloseAllDocuments/
    // startInstaller/shutdown — são OBRIGATÓRIOS, sem default `??`, mesma disciplina de
    // BatchSignViewModel.pickFiles/isPathOpen — ver doc XML de ConfiguracoesViewModel).
    // ==================================================================================================

    private static ConfiguracoesViewModel BuildConfiguracoes(
        Func<IUpdateSource>? createSource = null,
        IConfirmInstallUpdateService? confirmInstall = null,
        Func<bool>? confirmCloseAllDocuments = null,
        Action<string>? startInstaller = null,
        Action? shutdown = null) => new(
        confirmCloseAllDocuments ?? (() => true),
        startInstaller ?? (_ => { }),
        shutdown ?? (() => { }),
        createSource,
        confirmInstall);

    [Fact] // createSource OMITIDO -> VerificarAtualizacaoCommand alcança UiPrompts.CreateUpdateSource,
    // trocado pelo guard pra uma versão que LANÇA (a MESMA disciplina de risco que os diálogos: uma
    // suíte headless não pode bater rede real por engano).
    public async Task ConfiguracoesViewModel_CreateSourceOmitted_VerificarAtualizacao_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingUpdateSource>(() => UiPrompts.CreateUpdateSource(), nameof(UiPrompts.CreateUpdateSource));

        var vm = BuildConfiguracoes(); // createSource OMITIDO

        var ex = await Record.ExceptionAsync(() => vm.VerificarAtualizacaoCommand.ExecuteAsync(null));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(nameof(UiPrompts.CreateUpdateSource), ioe.Message);
    }

    [Fact] // controle negativo (createSource): fake benigno local -> não lança.
    public async Task ConfiguracoesViewModel_CreateSourceOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateUpdateSource;
        try
        {
            UiPrompts.CreateUpdateSource = () => new SpyUpdateSource(null);
            var vm = BuildConfiguracoes();

            var ex = await Record.ExceptionAsync(() => vm.VerificarAtualizacaoCommand.ExecuteAsync(null));

            Assert.Null(ex);
        }
        finally { UiPrompts.CreateUpdateSource = original; }
    }

    [Fact] // confirmInstall OMITIDO -> ProsseguirComInstalacaoAsync alcança UiPrompts.CreateConfirmInstallUpdate.
    public async Task ConfiguracoesViewModel_ConfirmInstallOmitted_ProsseguirComInstalacao_ThrowsViaUiPrompts()
    {
        AssertFactorySwapped<ThrowingConfirmInstallUpdateService>(() => UiPrompts.CreateConfirmInstallUpdate(), nameof(UiPrompts.CreateConfirmInstallUpdate));

        var vm = BuildConfiguracoes(); // confirmInstall OMITIDO
        var arquivo = WriteRealVerifiedFile();
        try
        {
            var ex = await Record.ExceptionAsync(() => vm.ProsseguirComInstalacaoAsync(arquivo));

            var ioe = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains(nameof(UiPrompts.CreateConfirmInstallUpdate), ioe.Message);
        }
        finally { File.Delete(arquivo.CaminhoArquivo); }
    }

    [Fact] // controle negativo (confirmInstall): fake benigno local devolvendo `false` -> não lança.
    public async Task ConfiguracoesViewModel_ConfirmInstallOmitted_NegativeControl_WithBenignFactory_DoesNotThrow()
    {
        var original = UiPrompts.CreateConfirmInstallUpdate;
        var arquivo = WriteRealVerifiedFile();
        try
        {
            UiPrompts.CreateConfirmInstallUpdate = () => new SpyConfirmInstallUpdateService(false);
            var vm = BuildConfiguracoes();

            var ex = await Record.ExceptionAsync(() => vm.ProsseguirComInstalacaoAsync(arquivo));

            Assert.Null(ex);
        }
        finally
        {
            UiPrompts.CreateConfirmInstallUpdate = original;
            File.Delete(arquivo.CaminhoArquivo);
        }
    }

    private static UpdateService.VerifiedUpdateFile WriteRealVerifiedFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-uiprompts-update-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "instalador simulado");
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        return UpdateService.VerifyAndFinalize(path, hash).Arquivo!;
    }
}

file sealed class SpySobreDialogService : ISobreDialogService
{
    public int CallCount { get; private set; }
    public void ShowSobreDialog(SobreViewModel viewModel) => CallCount++;
}

file sealed class SpyConfiguracoesDialogService : IConfiguracoesDialogService
{
    public int CallCount { get; private set; }
    public void ShowConfiguracoesDialog(ConfiguracoesViewModel viewModel) => CallCount++;
}

file sealed class SpyUpdateSource(LatestRelease? result) : IUpdateSource
{
    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct) => Task.FromResult(result);
}

file sealed class SpyConfirmInstallUpdateService(bool result) : IConfirmInstallUpdateService
{
    public bool Confirm(string message) => result;
}
