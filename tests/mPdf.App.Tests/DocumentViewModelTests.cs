using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

// Task 6 (Plano 3a): fake de IPdfEditor pros testes de ApplyMarkupCommand abaixo — registra a
// AnnotationData recebida (prova kind/quads/cor/autor sem tocar iText de verdade) e devolve bytes REAIS
// de um PDF válido: Session.ApplyEdit constrói um PdfDocumentRenderer (Docnet/PDFium) de verdade sobre
// o resultado pra reconstruir Pages/Thumbnails — bytes arbitrários fariam ApplyEdit lançar.
// Fixtures.ThirtyPages() como "bytes-marcador": 30 páginas é um contraste bem distinto do documento de
// entrada (fixture-a4, 1 página), então "Session.Snapshot realmente trocou pro resultado do fake" fica
// trivial de provar (PageCount muda de 1 pra 30), não só "não é mais o array antigo por referência".
// NÃO é `file` (diferente de FakeDialog/FakeConfirmCloseService em MainViewModelTests.cs): aparece
// literalmente no tipo de retorno de BuildForMarkup (um membro de DocumentViewModelTests, tipo NÃO
// file-scoped) — o compilador recusa um tipo `file` em assinatura de membro fora do próprio arquivo
// dele (CS9051), mesmo dentro do mesmo arquivo, porque o MEMBRO pertence a um tipo não-file.
internal sealed class FakePdfEditor : IPdfEditor
{
    public AnnotationData? LastAnnotation { get; private set; }
    public int AddAnnotationCallCount { get; private set; }
    public Exception? ThrowOnAddAnnotation { get; set; }

    /// Item 2 (revisão final pré-merge): simula um editor que devolve bytes que o PDFium REJEITA — não
    /// uma exceção TIPADA do próprio mPdf.Editing (isso já é o caso `ThrowOnAddAnnotation` acima), mas
    /// um resultado "bem-sucedido" pra API do editor cujo conteúdo é inválido. `Session.ApplyEdit`
    /// constrói um `PdfDocumentRenderer` sobre esse resultado e lança `ArgumentException` CRUA — é
    /// exatamente essa rede (`DocumentViewModel.TryApplyEdit`) que este campo exercita.
    public bool ReturnInvalidPdfBytes { get; set; }

    public byte[] AddAnnotation(byte[] pdf, AnnotationData annotation)
    {
        AddAnnotationCallCount++;
        LastAnnotation = annotation;
        if (ThrowOnAddAnnotation is { } ex) throw ex;
        if (ReturnInvalidPdfBytes) return new byte[] { 0x00, 0x01, 0x02, 0x03 };
        return Fixtures.ThirtyPages();
    }

    // Task 7 (Plano 3a): Remove/Read passam a ter implementação real no fake — Del e o lift (editar/
    // mover) precisam do PIPELINE Remove->Add, não só de Add isolado como as tasks anteriores exigiam.
    public string? LastRemovedId { get; private set; }
    public int RemoveAnnotationCallCount { get; private set; }
    public Exception? ThrowOnRemoveAnnotation { get; set; }

    public byte[] RemoveAnnotation(byte[] pdf, string annotationId)
    {
        RemoveAnnotationCallCount++;
        LastRemovedId = annotationId;
        if (ThrowOnRemoveAnnotation is { } ex) throw ex;
        return Fixtures.ThirtyPages();
    }

    /// Resultado fixo devolvido por `ReadAnnotations` — populado pelos testes que precisam de um cache
    /// `AnnotationsByPage` sintético (hit-test/seleção). `null` (default) preserva o comportamento
    /// anterior (`NotSupportedException`) para os testes que nunca chamam `ReadAnnotations` de verdade.
    public IReadOnlyList<AnnotationData>? ReadAnnotationsResult { get; set; }

    /// Trava opcional (revisão Opus, C1a — achado real): `RefreshAnnotationsByPageAsync` chama
    /// `ReadAnnotations` dentro de um `Task.Run` — um teste que dispara essa chamada fire-and-forget
    /// (via `OnSessionApplied`) e quer provar "o cache AINDA não terminou de atualizar" não pode
    /// confiar em timing (a leitura do fake é trivial/sem I/O, então pode terminar numa thread do pool
    /// EM PARALELO antes mesmo da linha de asserção seguinte rodar — flakou de verdade sob a carga da
    /// suíte completa, não é hipotético). Setado, `ReadAnnotations` BLOQUEIA a thread do pool que a
    /// chamou até o teste liberar via `SetResult` — determinístico, sem sleep.
    public TaskCompletionSource<bool>? ReadAnnotationsGate { get; set; }

    public IReadOnlyList<AnnotationData> ReadAnnotations(byte[] pdf)
    {
        ReadAnnotationsGate?.Task.Wait();
        return ReadAnnotationsResult ?? throw new NotSupportedException();
    }

    // C1 (revisão final pré-merge, Plano 3b): configurável pros testes de aviso "documento de origem
    // assinado" (OrganizerViewModel.ExtractSelected/MainViewModel.Merge/Split) — default `false`
    // preserva o comportamento de todo teste PRÉ-EXISTENTE que nunca configura `HasSignaturesResult`
    // (mesmo espírito de `PageRotationsResult`/`ReadOutlineResult` acima).
    public bool HasSignaturesResult { get; set; }
    public int HasSignaturesCallCount { get; private set; }
    public Exception? ThrowOnHasSignatures { get; set; }

    public bool HasSignatures(byte[] pdf)
    {
        HasSignaturesCallCount++;
        if (ThrowOnHasSignatures is { } ex) throw ex;
        return HasSignaturesResult;
    }

    public byte[] StripSignatures(byte[] pdf) => throw new NotSupportedException();

    // Task 3 (Plano 3b): Rotate/Delete/Move ganham implementação REAL no fake — o Organizador (VM
    // novo) precisa dos 3 pra valer (índices/graus recebidos + ApplyEdit), mesmo padrão de
    // AddAnnotation/RemoveAnnotation acima (registra a última chamada, devolve bytes REAIS
    // configuráveis — default Fixtures.ThirtyPages(), "bytes-marcador" contrastante quando o teste
    // não precisa de um resultado específico). ExtractPages/InsertPages/MergeDocuments/SplitByRanges
    // ganham a MESMA implementação real na Task 4 (ver abaixo, depois de MovePage).
    public IReadOnlyList<int>? LastRotatePageIndexes { get; private set; }
    public int LastRotateDegrees { get; private set; }
    public int RotatePagesCallCount { get; private set; }
    public Exception? ThrowOnRotatePages { get; set; }
    public byte[]? RotatePagesResult { get; set; }

    /// I1 (revisão final pré-merge, Plano 3b): trava opcional pra provar o pino "edição em voo" do
    /// OrganizerViewModel — mesmo padrão de `ReadAnnotationsGate` acima (bloqueia a thread do pool que
    /// chamou `RotatePages` até o teste liberar via `SetResult`, determinístico, sem sleep).
    public TaskCompletionSource<bool>? RotatePagesGate { get; set; }

    public byte[] RotatePages(byte[] pdf, IReadOnlyList<int> pageIndexes, int degreesClockwise)
    {
        RotatePagesGate?.Task.Wait();
        RotatePagesCallCount++;
        LastRotatePageIndexes = pageIndexes;
        LastRotateDegrees = degreesClockwise;
        if (ThrowOnRotatePages is { } ex) throw ex;
        return RotatePagesResult ?? Fixtures.ThirtyPages();
    }

    public IReadOnlyList<int>? LastDeletePageIndexes { get; private set; }
    public int DeletePagesCallCount { get; private set; }
    public Exception? ThrowOnDeletePages { get; set; }
    public byte[]? DeletePagesResult { get; set; }

    public byte[] DeletePages(byte[] pdf, IReadOnlyList<int> pageIndexes)
    {
        DeletePagesCallCount++;
        LastDeletePageIndexes = pageIndexes;
        if (ThrowOnDeletePages is { } ex) throw ex;
        return DeletePagesResult ?? Fixtures.ThirtyPages();
    }

    public int? LastMoveFromIndex { get; private set; }
    public int? LastMoveToIndex { get; private set; }
    public int MovePageCallCount { get; private set; }
    public Exception? ThrowOnMovePage { get; set; }
    public byte[]? MovePageResult { get; set; }

    public byte[] MovePage(byte[] pdf, int fromIndex, int toIndex)
    {
        MovePageCallCount++;
        LastMoveFromIndex = fromIndex;
        LastMoveToIndex = toIndex;
        if (ThrowOnMovePage is { } ex) throw ex;
        return MovePageResult ?? Fixtures.ThirtyPages();
    }

    // Task 4 (Plano 3b): Extrair/Inserir/Juntar/Dividir ganham implementação REAL no fake — mesmo
    // padrão de Rotate/Delete/Move acima (registra a última chamada, devolve bytes REAIS configuráveis,
    // default "bytes-marcador" contrastante quando o teste não precisa de um resultado específico).
    public IReadOnlyList<int>? LastExtractPageIndexes { get; private set; }
    public int ExtractPagesCallCount { get; private set; }
    public Exception? ThrowOnExtractPages { get; set; }
    public byte[]? ExtractPagesResult { get; set; }

    public byte[] ExtractPages(byte[] pdf, IReadOnlyList<int> pageIndexes)
    {
        ExtractPagesCallCount++;
        LastExtractPageIndexes = pageIndexes;
        if (ThrowOnExtractPages is { } ex) throw ex;
        return ExtractPagesResult ?? Fixtures.A4();
    }

    public byte[]? LastInsertSource { get; private set; }
    public int? LastInsertAtIndex { get; private set; }
    public int InsertPagesCallCount { get; private set; }
    public Exception? ThrowOnInsertPages { get; set; }
    public byte[]? InsertPagesResult { get; set; }

    public byte[] InsertPages(byte[] pdf, byte[] source, int atIndex)
    {
        InsertPagesCallCount++;
        LastInsertSource = source;
        LastInsertAtIndex = atIndex;
        if (ThrowOnInsertPages is { } ex) throw ex;
        return InsertPagesResult ?? Fixtures.ThirtyPages();
    }

    public IReadOnlyList<byte[]>? LastMergeInputs { get; private set; }
    public int MergeDocumentsCallCount { get; private set; }
    public Exception? ThrowOnMergeDocuments { get; set; }
    public byte[]? MergeDocumentsResult { get; set; }

    public byte[] MergeDocuments(IReadOnlyList<byte[]> pdfs)
    {
        MergeDocumentsCallCount++;
        LastMergeInputs = pdfs;
        if (ThrowOnMergeDocuments is { } ex) throw ex;
        return MergeDocumentsResult ?? Fixtures.ThirtyPages();
    }

    public IReadOnlyList<(int from, int to)>? LastSplitRanges { get; private set; }
    public int SplitByRangesCallCount { get; private set; }
    public Exception? ThrowOnSplitByRanges { get; set; }
    public IReadOnlyList<byte[]>? SplitByRangesResult { get; set; }

    public IReadOnlyList<byte[]> SplitByRanges(byte[] pdf, IReadOnlyList<(int from, int to)> ranges)
    {
        SplitByRangesCallCount++;
        LastSplitRanges = ranges;
        if (ThrowOnSplitByRanges is { } ex) throw ex;
        return SplitByRangesResult ?? new[] { Fixtures.A4(), Fixtures.A4() };
    }

    /// Costura de rotação (Task 3, Plano 3b) — default TODAS as páginas em 0 (não giradas), mesmo
    /// espírito de `ReadAnnotationsResult`/`HasSignatures` acima: preserva o comportamento de todo
    /// teste PRÉ-EXISTENTE que nunca configura rotação (nunca lança, nunca bloqueia nada) — só os
    /// testes da costura de rotação (abaixo) configuram `PageRotationsResult`.
    public IReadOnlyList<int>? PageRotationsResult { get; set; }
    public int GetPageRotationsCallCount { get; private set; }

    public IReadOnlyList<int> GetPageRotations(byte[] pdf)
    {
        GetPageRotationsCallCount++;
        return PageRotationsResult ?? Array.Empty<int>();
    }

    /// Sumário (Task 5, Plano 3b) — default lista VAZIA (mesmo espírito de `PageRotationsResult`, não
    /// de `ReadAnnotationsResult`): preserva o comportamento de todo teste PRÉ-EXISTENTE que nunca
    /// configura `ReadOutlineResult` — `RefreshOutlineAsync` sempre TERMINA sem lançar, `Outline` fica
    /// vazio (`HasOutline = false`), nunca precisa de configuração pra não quebrar testes alheios.
    public IReadOnlyList<OutlineNode>? ReadOutlineResult { get; set; }
    public int ReadOutlineCallCount { get; private set; }

    public IReadOnlyList<OutlineNode> ReadOutline(byte[] pdf)
    {
        ReadOutlineCallCount++;
        return ReadOutlineResult ?? Array.Empty<OutlineNode>();
    }

    // Motor de formulários AcroForm (Task 1, Plano 3c) — Task 2 (painel de Campos) passa a exercitar
    // ReadFormFields/HasXfa/SetFormFields de verdade (mesmo padrão de Rotate/Delete/Move ganhando
    // implementação real quando a task que os CONSOME chega, ver comentário de Task 3/Plano 3b acima).
    // FlattenForm ganha a MESMA implementação real na Task 3 (achatar formulário), ver abaixo.
    public IReadOnlyList<FormFieldData>? ReadFormFieldsResult { get; set; }
    public int ReadFormFieldsCallCount { get; private set; }
    public Exception? ThrowOnReadFormFields { get; set; }

    public IReadOnlyList<FormFieldData> ReadFormFields(byte[] pdf)
    {
        ReadFormFieldsCallCount++;
        if (ThrowOnReadFormFields is { } ex) throw ex;
        return ReadFormFieldsResult ?? Array.Empty<FormFieldData>();
    }

    public bool HasXfaResult { get; set; }
    public int HasXfaCallCount { get; private set; }
    public Exception? ThrowOnHasXfa { get; set; }

    public bool HasXfa(byte[] pdf)
    {
        HasXfaCallCount++;
        if (ThrowOnHasXfa is { } ex) throw ex;
        return HasXfaResult;
    }

    public IReadOnlyDictionary<string, string>? LastSetFormFieldsValues { get; private set; }
    public int SetFormFieldsCallCount { get; private set; }
    public Exception? ThrowOnSetFormFields { get; set; }
    public byte[]? SetFormFieldsResult { get; set; }

    public byte[] SetFormFields(byte[] pdf, IReadOnlyDictionary<string, string> values)
    {
        SetFormFieldsCallCount++;
        LastSetFormFieldsValues = values;
        if (ThrowOnSetFormFields is { } ex) throw ex;
        return SetFormFieldsResult ?? Fixtures.ThirtyPages();
    }

    // Task 3 (Plano 3c): FlattenForm ganha implementação REAL no fake — mesmo padrão de SetFormFields
    // acima (registra a chamada, devolve bytes REAIS configuráveis, default "bytes-marcador"
    // contrastante quando o teste não precisa de um resultado específico).
    public int FlattenFormCallCount { get; private set; }
    public Exception? ThrowOnFlattenForm { get; set; }
    public byte[]? FlattenFormResult { get; set; }

    public byte[] FlattenForm(byte[] pdf)
    {
        FlattenFormCallCount++;
        if (ThrowOnFlattenForm is { } ex) throw ex;
        return FlattenFormResult ?? Fixtures.ThirtyPages();
    }

    // Task 2 (Plano 7): ImageToPdf/IsSupportedImage ganham implementação REAL no fake — mesmo padrão de
    // Rotate/Delete/Move/Insert/Merge/Split acima (registra CADA chamada — não só a última: Juntar pode
    // converter VÁRIAS imagens numa única execução, "conversão por linha de imagem, ordem preservada"
    // precisa da lista inteira, não só do último item — devolve bytes REAIS configuráveis, default
    // "bytes-marcador" Fixtures.A4() quando o teste não precisa de um resultado específico).
    // `IsSupportedImageResult` default `true`: preserva o caso comum (o fake não faz sniff de verdade,
    // então testes de VM que não se importam com o conteúdo do arquivo — só com "é tratado como
    // imagem?" — não precisam configurar nada) — só os testes do detector em si (recusa por magic
    // bytes) setam `false`.
    public bool IsSupportedImageResult { get; set; } = true;
    public int IsSupportedImageCallCount { get; private set; }
    public byte[]? LastIsSupportedImageBytes { get; private set; }

    public bool IsSupportedImage(byte[]? bytes)
    {
        IsSupportedImageCallCount++;
        LastIsSupportedImageBytes = bytes;
        return IsSupportedImageResult;
    }

    public List<byte[]> ImageToPdfInputs { get; } = new();
    public int ImageToPdfCallCount => ImageToPdfInputs.Count;
    public Exception? ThrowOnImageToPdf { get; set; }
    public byte[]? ImageToPdfResult { get; set; }

    public byte[] ImageToPdf(byte[] image)
    {
        ImageToPdfInputs.Add(image);
        if (ThrowOnImageToPdf is { } ex) throw ex;
        return ImageToPdfResult ?? Fixtures.A4();
    }

    // Task 3 (Plano 7): "🖼 Imagem" — ToggleImageTool consulta os 2 métodos abaixo ANTES do modo de
    // colocação. Defaults preservam o comportamento de todo teste PRÉ-EXISTENTE que nunca configura
    // nada aqui (mesmo espírito de IsSupportedImageResult acima): imagem "dentro do teto" e "sem
    // rotação EXIF" são os casos mais comuns, só os testes do teto/EXIF em si configuram algo diferente.
    public bool IsWithinImagePixelLimitResult { get; set; } = true;
    public int IsWithinImagePixelLimitCallCount { get; private set; }

    public bool IsWithinImagePixelLimit(byte[] bytes)
    {
        IsWithinImagePixelLimitCallCount++;
        return IsWithinImagePixelLimitResult;
    }

    public int ReadJpegExifOrientationResult { get; set; }
    public int ReadJpegExifOrientationCallCount { get; private set; }

    public int ReadJpegExifOrientation(byte[] image)
    {
        ReadJpegExifOrientationCallCount++;
        return ReadJpegExifOrientationResult;
    }

    // Task 3 (Plano 7), fix pós-revisão: ToggleImageTool consulta este método ANTES do modo de
    // colocação. Default `false` preserva o comportamento de todo teste PRÉ-EXISTENTE (mesmo espírito
    // de IsWithinImagePixelLimitResult acima) — só o teste da recusa CMYK em si configura `true`.
    public bool IsCmykJpegResult { get; set; }
    public int IsCmykJpegCallCount { get; private set; }

    public bool IsCmykJpeg(byte[] bytes)
    {
        IsCmykJpegCallCount++;
        return IsCmykJpegResult;
    }

    // Task 3 (Plano 15): camada de texto invisível de OCR. Stub mínimo pra este fake satisfazer a
    // interface — a orquestração real (e os testes que exercitam este método) chegam na Task 4.
    // Registra as chamadas/entradas no mesmo espírito dos demais membros deste fake.
    public List<IReadOnlyList<OcrTextLayer>> ApplyOcrTextLayerInputs { get; } = new();
    public byte[]? ApplyOcrTextLayerResult { get; set; }
    /// Task 4 (Plano 15): simula o gate de assinatura (doc assinado -> `PdfSignedDocumentException`) OU
    /// qualquer outra falha tipada do motor — mesmo espírito de `ThrowOnAddAnnotation`.
    public Exception? ThrowOnApplyOcrTextLayer { get; set; }

    public byte[] ApplyOcrTextLayer(byte[] pdf, IReadOnlyList<OcrTextLayer> layers)
    {
        ApplyOcrTextLayerInputs.Add(layers);
        if (ThrowOnApplyOcrTextLayer is { } ex) throw ex;
        return ApplyOcrTextLayerResult ?? pdf;
    }
}

// Task 7 (Plano 3a): fake do prompt de texto de nota/caixa de texto — mesmo padrão de
// FakeConfirmCloseService (MainViewModelTests): devolve um texto FIXO (ou null = "usuário cancelou"),
// registra a última chamada (título/initialText) pra provar que o VM pediu o prompt CERTO (ex.: edição
// pré-preenche com o Content atual, criação pede vazio).
internal sealed class FakeAnnotationTextDialogService : IAnnotationTextDialogService
{
    public string? Result { get; set; }
    public int CallCount { get; private set; }
    public string? LastTitle { get; private set; }
    public string? LastInitialText { get; private set; }

    public string? PromptForText(string title, string? initialText = null)
    {
        CallCount++;
        LastTitle = title;
        LastInitialText = initialText;
        return Result;
    }
}

// Task 3 (Plano 3c): fake do prompt de confirmação "achatar formulário?" — mesmo padrão de
// FakeAnnotationTextDialogService acima: devolve um `bool` FIXO (confirmado/cancelado), registra a
// última mensagem pra provar que o VM consultou o diálogo CERTO.
internal sealed class FakeConfirmFlattenService(bool result) : IConfirmFlattenService
{
    public int CallCount { get; private set; }
    public string? LastMessage { get; private set; }

    public bool Confirm(string message)
    {
        CallCount++;
        LastMessage = message;
        return result;
    }
}

// Task 1 (Plano 5): fake do prompt de confirmação "organizador pode demorar" — MESMO padrão de
// FakeConfirmFlattenService acima.
internal sealed class FakeConfirmOrganizerScaleService(bool result) : IConfirmOrganizerScaleService
{
    public int CallCount { get; private set; }
    public string? LastMessage { get; private set; }

    public bool Confirm(string message)
    {
        CallCount++;
        LastMessage = message;
        return result;
    }
}

// Task 3 (Plano 7): fake de IFileDialogService pra ToggleImageTool — só implementa PickImageToImport
// (o único diálogo que este VM abre); os outros 3 lançam NotSupportedException (nunca deveriam ser
// chamados por um teste desta ferramenta — mesmo espírito defensivo de FakePdfEditor.StripSignatures).
// `imagePath`: caminho devolvido pelo diálogo, `null` = usuário cancelou (mesmo contrato de produção).
internal sealed class FakePickImageDialog(string? imagePath) : IFileDialogService
{
    public int PickImageToImportCallCount { get; private set; }

    public string? PickPdfToOpen() => throw new NotSupportedException();
    public string? PickPdfToSaveAs(string currentPath) => throw new NotSupportedException();
    public string? PickImageToImport() { PickImageToImportCallCount++; return imagePath; }
    public string? PickPdfToSave(string suggestedName) => throw new NotSupportedException();
}

// Task 4 (Plano 7) — "📤 Exportar": fake que NUNCA abre janela nenhuma, só registra a chamada + captura
// o VM recebido (pra inspecionar Format/Range/Dpi default e o índice de página passado, sem precisar de
// um ExportImageDialogService real).
internal sealed class SpyExportImageDialogService : IExportImageDialogService
{
    public int CallCount { get; private set; }
    public ExportImageViewModel? LastViewModel { get; private set; }
    public void ShowExportImageDialog(ExportImageViewModel viewModel) { CallCount++; LastViewModel = viewModel; }
}

public class DocumentViewModelTests
{
    [Fact] // 30 páginas viram 30 PageViewModels com tamanho A4 em pixels de tela (zoom 1.0)
    public void Ctor_CreatesPageViewModels_WithDisplaySizes()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        Assert.Equal(30, doc.Pages.Count);
        Assert.Equal(595 * 96.0 / 72.0, doc.Pages[0].DisplayWidth, 2.0);
    }

    [Fact] // Task 6: uma ThumbnailViewModel por página, em tamanho proporcional à escala FIXA de
    // miniatura (independente do Zoom do documento — contraste com o teste acima, que usa PtToPx*zoom)
    public void Ctor_CreatesThumbnailViewModels_WithProportionalSizes()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        Assert.Equal(30, doc.Thumbnails.Count);
        Assert.Equal(595 * ThumbnailViewModel.Scale, doc.Thumbnails[0].DisplayWidth, 1.0);
        Assert.Equal(842 * ThumbnailViewModel.Scale, doc.Thumbnails[0].DisplayHeight, 1.0);
    }

    [Fact] // Task 6: Dispose fecha os DOIS renderers — o principal (via Session, já coberto por
    // ViewerIntegrationTests) E o SEGUNDO renderer dedicado a miniaturas (contrato: cache de
    // render-reader de escala única -> segundo PdfDocumentRenderer sobre o mesmo Snapshot).
    public void Dispose_DisposesThumbnailRendererToo()
    {
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        var thumbnailRenderer = doc.ThumbnailRenderer; // internal, exposto via InternalsVisibleTo

        doc.Dispose();
        // M1 (revisão pós-Task 6): mesmo try/catch(AggregateException) usado nos outros dois
        // call-sites de WaitAll (MainWindow.OnClosed, ViewerIntegrationTests) — um descarte faltoso
        // não pode mascarar a asserção real deste teste (o SEGUNDO renderer fechou, verificado
        // abaixo via ObjectDisposedException); AggregateException só significa que a fila TERMINOU
        // (com falha) dentro do timeout, o que ainda conta como "não travou".
        bool finished;
        try { finished = PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { finished = true; }
        Assert.True(finished, "descarte do renderer de miniaturas (offloaded) não terminou a tempo");

        Assert.Throws<ObjectDisposedException>(() => thumbnailRenderer.RenderPage(0, ThumbnailViewModel.Scale));
    }

    [Fact] // Task 6: trocar CurrentPage atualiza o flag IsCurrent da miniatura correspondente
    // (1-based -> 0-based, mesma convenção de UpdateCurrentPageFromScroll)
    public void CurrentPageChange_UpdatesThumbnailIsCurrent()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        Assert.True(doc.Thumbnails[0].IsCurrent);

        doc.CurrentPage = 5;

        Assert.False(doc.Thumbnails[0].IsCurrent);
        Assert.True(doc.Thumbnails[4].IsCurrent);
        for (int i = 0; i < doc.Thumbnails.Count; i++)
        {
            if (i == 4) continue;
            Assert.False(doc.Thumbnails[i].IsCurrent);
        }
    }

    [Fact] // mudar o zoom atualiza o tamanho de exibição de todas as páginas
    public void ZoomChange_UpdatesDisplaySizes()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        var before = doc.Pages[0].DisplayWidth;
        doc.Zoom = 2.0;
        Assert.Equal(before * 2, doc.Pages[0].DisplayWidth, 2.0);
    }

    [Fact] // busca de ponta a ponta (revisão da Task 5, I6b): PdfTextSearch.FindAll DE VERDADE via
    // Search.RunSearchAsync (não um fake) — só a página do hit ("pagina 15" -> índice 14, 0-based)
    // recebe retângulos de destaque; todas as outras 29 páginas ficam vazias.
    // M-3 (revisão 2): também prova o CONTRATO DE ORDEM entre ApplySearchResults e ScrollToPageRequested
    // — CurrentHighlightRects da página do hit já precisa estar preenchido ANTES do evento disparar,
    // porque PdfViewerControl.ScrollToPage lê CurrentHighlightRects pra calcular o alvo do scroll (se
    // a ordem inverter, o cálculo degrada silenciosamente, sem exceção nem teste falhando). Assinar o
    // evento e checar DENTRO do handler é o único jeito de pinar essa ordem (fora do handler, sempre
    // vai estar preenchido de qualquer forma, já que RunSearchAsync já terminou).
    public async Task Search_RealFixture_HighlightsOnlyTheHitPage()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));

        int? scrolledToPageIndex = null;
        bool currentHighlightPopulatedBeforeEvent = false;
        doc.ScrollToPageRequested += pageIndex =>
        {
            scrolledToPageIndex = pageIndex;
            currentHighlightPopulatedBeforeEvent = doc.Pages[14].CurrentHighlightRects.Count > 0;
        };

        doc.Search.Query = "pagina 15";
        await doc.Search.RunSearchAsync();

        Assert.Equal(14, scrolledToPageIndex);
        Assert.True(currentHighlightPopulatedBeforeEvent,
            "CurrentHighlightRects da página do hit precisa estar preenchido ANTES do ScrollToPageRequested disparar");

        Assert.NotEmpty(doc.Pages[14].HighlightRects);
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            if (i == 14) continue;
            Assert.Empty(doc.Pages[i].HighlightRects);
        }
    }

    [Fact] // Item (a) da Task 1 (Plano 3a), ponta a ponta: busca DE VERDADE (PdfTextSearch.FindAll +
    // PdfTextSearch.DocumentHasText via DocumentViewModel.ProbeDocumentHasText, sem fakes) num
    // documento SEM texto algum (fixture-sem-texto) -> rótulo "digitalizado", não "Nenhum resultado".
    public async Task Search_DocumentWithNoText_ShowsScannedDocumentMessage()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-sem-texto.pdf")));

        doc.Search.Query = "qualquer coisa";
        await doc.Search.RunSearchAsync();

        Assert.Equal("Documento sem texto pesquisável (digitalizado)", doc.Search.ResultCountLabel);
    }

    // ---- Task 3 (Plano 3a): Session.Apply reconstrói Pages/Thumbnails --------------------------

    [Fact] // Pages/Thumbnails refletem o documento NOVO imediatamente após Apply retornar (a
    // reconstrução é SÍNCRONA, disparada pelo handler de Session.Applied) — fixture-a4 (1 página) ->
    // fixture-30p (30 páginas), a prova mais direta de "as coleções observáveis são do documento novo".
    public void SessionApply_RebuildsPagesAndThumbnails_ForTheNewDocument()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        Assert.Single(doc.Pages);
        Assert.Single(doc.Thumbnails);

        doc.Session.Apply(Fixtures.ThirtyPages());

        Assert.Equal(30, doc.Pages.Count);
        Assert.Equal(30, doc.Thumbnails.Count);
        Assert.True(doc.Thumbnails[0].IsCurrent); // destaque de miniatura resetado pra página 1
        Assert.Equal(1, doc.CurrentPage);
        Assert.Equal("Página 1 de 30", doc.PageCountLabel);
    }

    [Fact] // Item 4 (revisão final pré-merge) — CurrentPage/scroll PRESERVADOS através de um Apply que
    // não muda a contagem de página: antes do fix, anotar a página 30 de um documento de 30 páginas
    // jogava a visão de volta pro topo (CurrentPage sempre resetava pra 1, incondicional). doc com 30
    // páginas, CurrentPage=15, Apply(mesmo documento de 30 páginas — simula uma anotação normal) ->
    // CurrentPage continua 15 E ScrollToPageRequested dispara com 14 (0-based — mesma convenção de
    // ApplySearchResults/hit.PageIndex, ver Search_RealFixture_HighlightsOnlyTheHitPage acima).
    public void SessionApply_SameLargerPageCount_PreservesCurrentPage_AndRequestsScrollToIt()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        doc.CurrentPage = 15;

        int? scrolledToPageIndex = null;
        doc.ScrollToPageRequested += pageIndex => scrolledToPageIndex = pageIndex;

        doc.Session.Apply(Fixtures.ThirtyPages()); // mesmo documento (30 páginas)

        Assert.Equal(15, doc.CurrentPage);
        Assert.Equal(14, scrolledToPageIndex);
        Assert.True(doc.Thumbnails[14].IsCurrent);
        Assert.False(doc.Thumbnails[0].IsCurrent);
    }

    [Fact] // Item 4 (revisão final pré-merge) — a página capturada pode não existir mais no documento
    // NOVO (ex.: Undo pra um snapshot mais curto) -> cai pro comportamento ANTIGO (volta pro topo),
    // nunca tenta restaurar um índice fora do intervalo.
    public void SessionApply_CurrentPageBeyondNewPageCount_ResetsToOne()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        doc.CurrentPage = 20;

        doc.Session.Apply(Fixtures.A4()); // documento NOVO tem só 1 página — 20 não existe mais

        Assert.Equal(1, doc.CurrentPage);
        Assert.True(doc.Thumbnails[0].IsCurrent);
    }

    [Fact] // Session.Apply marca a sessão suja (hash do snapshot novo difere do salvo/aberto) — o VM
    // espelha isso em IsDirty/Title ("•" no nome exibido).
    public void SessionApply_MarksDirty_AndUpdatesTitle()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        Assert.False(doc.IsDirty);
        Assert.Equal("fixture-a4.pdf", doc.Title);

        doc.Session.Apply(Fixtures.ThirtyPages());

        Assert.True(doc.IsDirty);
        Assert.Equal("fixture-a4.pdf •", doc.Title);
    }

    [Fact] // Task 2 (Plano 7, rider da revisão): NeedsSaveAs adiciona "(não salvo)" ao título -- pista
    // visível de que este documento é temp-backed; combina com o "•" de sujo quando os dois são true.
    // [ObservableProperty] (fix pós-revisão -- deixou de ser plain property) dispara PropertyChanged
    // pra Title mesmo sem nenhum evento de Session envolvido (a aba recém-aberta precisa refletir isto
    // de imediato, não só na PRÓXIMA vez que IsDirty/FilePath mudarem por coincidência).
    public void NeedsSaveAs_True_AppendsSuffixToTitle_AndRaisesPropertyChangedForTitle()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        var titleChanges = new List<string>();
        doc.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DocumentViewModel.Title)) titleChanges.Add(doc.Title); };

        doc.NeedsSaveAs = true;

        Assert.Equal("fixture-a4.pdf (não salvo)", doc.Title);
        Assert.Single(titleChanges);
        Assert.Equal("fixture-a4.pdf (não salvo)", titleChanges[0]);

        doc.Session.Apply(Fixtures.ThirtyPages()); // suja também -- os dois sufixos combinam
        Assert.Equal("fixture-a4.pdf (não salvo) •", doc.Title);

        doc.NeedsSaveAs = false;
        Assert.Equal("fixture-a4.pdf •", doc.Title);
    }

    [Fact] // o renderer DEDICADO de miniaturas (_thumbnailRenderer) também é trocado no Apply — não é
    // só Session.Renderer. Prova indireta: o renderer de miniaturas ANTES do Apply é descartado
    // (ObjectDisposedException após drenar PendingDisposals), igual ao contrato de Dispose_DisposesThumbnailRendererToo.
    public void SessionApply_ReplacesThumbnailRenderer_DisposingTheOldOne()
    {
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        var oldThumbnailRenderer = doc.ThumbnailRenderer;

        doc.Session.Apply(Fixtures.ThirtyPages());

        Assert.NotSame(oldThumbnailRenderer, doc.ThumbnailRenderer);
        bool finished;
        try { finished = PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { finished = true; }
        Assert.True(finished, "descarte do renderer de miniaturas ANTIGO (offloaded) não terminou a tempo");
        Assert.Throws<ObjectDisposedException>(() => oldThumbnailRenderer.RenderPage(0, ThumbnailViewModel.Scale));

        doc.Dispose();
    }

    [Fact] // I4 (revisão pós-Task 3) — landmine real: busca com hit na página 14 (0-based, "pagina 15"
    // no fixture-30p) seguida de Apply pra um documento MENOR (fixture-a4, 1 página só) — o hit antigo
    // aponta pra um índice de página que NÃO EXISTE MAIS. Sem OnSessionApplied fechar a busca (I4),
    // navegar (Next) reaplicaria esse hit contra as Pages NOVAS via
    // ApplySearchResults -> Pages[14] com Pages.Count==1 -> ArgumentOutOfRangeException real, disparável
    // pelo usuário (busca -> edição que encolhe o doc -> "Próximo"). Prova: Next() não lança, e a busca
    // foi fechada/limpa (mesmo efeito de Search.CloseCommand).
    public async Task SessionApply_ToSmallerDocument_ClosesStaleSearch_NextDoesNotThrow()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        doc.Search.Query = "pagina 15";
        await doc.Search.RunSearchAsync();
        Assert.NotEmpty(doc.Pages[14].HighlightRects); // sanity: achou o hit de verdade na página 14
        Assert.NotEqual(string.Empty, doc.Search.ResultCountLabel); // ex.: "1 de 1"

        doc.Session.Apply(Fixtures.A4()); // documento NOVO tem só 1 página — índice 14 do hit antigo não existe mais

        Assert.Equal(string.Empty, doc.Search.Query); // busca fechada/limpa (CloseCommand)
        var ex = Record.Exception(() => doc.Search.NextCommand.Execute(null));
        Assert.Null(ex); // NÃO lança ArgumentOutOfRangeException
        Assert.Equal(string.Empty, doc.Search.ResultCountLabel); // sem hits, sem query -> rótulo vazio
    }

    // ---- Task 4 (Plano 3a): Undo/Redo ------------------------------------------------------------

    [Fact] // CanUndo/CanRedo (e o CanExecute dos comandos gerados) espelham Session.CanUndo/CanRedo —
    // mesmo padrão de IsDirty/SaveCommand da Task 3.
    public void UndoRedoCommands_CanExecute_MirrorsSessionState()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        Assert.False(doc.CanUndo);
        Assert.False(doc.UndoCommand.CanExecute(null));
        Assert.False(doc.CanRedo);
        Assert.False(doc.RedoCommand.CanExecute(null));

        doc.Session.ApplyEdit(Fixtures.ThirtyPages());

        Assert.True(doc.CanUndo);
        Assert.True(doc.UndoCommand.CanExecute(null));
        Assert.False(doc.CanRedo);
        Assert.False(doc.RedoCommand.CanExecute(null));
    }

    [Fact] // UndoCommand.Execute chama Session.Undo() de verdade — reconstrói Pages/Thumbnails (mesmo
    // caminho de Session.Applied já provado em SessionApply_RebuildsPagesAndThumbnails...) e liga Redo.
    public void UndoCommand_Execute_RevertsDocument_AndEnablesRedo()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        doc.Session.ApplyEdit(Fixtures.ThirtyPages());
        Assert.Equal(30, doc.Pages.Count);

        doc.UndoCommand.Execute(null);

        Assert.Single(doc.Pages); // voltou pro documento original (1 página)
        Assert.False(doc.CanUndo);
        Assert.False(doc.UndoCommand.CanExecute(null));
        Assert.True(doc.CanRedo);
        Assert.True(doc.RedoCommand.CanExecute(null));
    }

    [Fact] // RedoCommand.Execute reaplica a edição desfeita — espelho exato do teste acima.
    public void RedoCommand_Execute_ReappliesUndoneEdit()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        doc.Session.ApplyEdit(Fixtures.ThirtyPages());
        doc.UndoCommand.Execute(null);

        doc.RedoCommand.Execute(null);

        Assert.Equal(30, doc.Pages.Count);
        Assert.True(doc.CanUndo);
        Assert.False(doc.CanRedo);
    }

    // ---- Task 1 (Plano 5): teto de bytes no undo -- notificação roteada até o VM ------------------

    [Fact] // mesmo exemplar de como Session.CanUndoRedoChanged/Applied já fluem até este VM
    // (OnSessionCanUndoRedoChanged/OnSessionApplied) — aqui Session.UndoHistoryLimitReached vira
    // _notifyInfo(texto EXATO do brief), 1x por documento mesmo com vários descartes genuínos (a
    // aritmética completa -- por que 5 ApplyEdit geram 2 descartes -- está em DocumentSessionTests e no
    // relatório da task).
    public void UndoHistoryLimitReached_RoutesToNotifyInfo_ExactBriefText_OnceDespiteMultipleDiscards()
    {
        long unit = Fixtures.A4().LongLength;
        var infos = new List<string>();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), maxRamBytes: unit, maxSpillBytes: unit * 2),
            notifyInfo: infos.Add);

        for (int i = 0; i < 5; i++) doc.Session.ApplyEdit(Fixtures.A4());

        Assert.Single(infos);
        Assert.Equal(
            "Limite de histórico atingido; as edições mais antigas não podem mais ser desfeitas.",
            infos[0]);
    }

    // (Nenhum teste dedicado "Dispose desinscreve" pra este evento — mesmo precedente dos outros 4
    // eventos de Session que DocumentViewModel.Dispose já desinscreve: Dispose() TAMBÉM enfileira
    // `session.Dispose()` em PendingDisposals, então "continuar usando `session` depois de `doc.Dispose()`"
    // não é um cenário suportado/estável pra testar — a simetria assinar-em-ctor/desassinar-em-Dispose já
    // é estruturalmente idêntica à dos outros 5 `Session.X -= ...` da mesma linha em `Dispose()`.)

    // ---- Task 5 (Plano 3a): IsSignedDocument / CanEdit -------------------------------------------

    [Fact] // default: documento recém-construído (checagem de assinatura ainda não rodou — quem roda
    // é MainViewModel.OpenPath, não o ctor deste VM) -> IsSignedDocument false, CanEdit true
    public void IsSignedDocument_DefaultsFalse_CanEditDefaultsTrue()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));

        Assert.False(doc.IsSignedDocument);
        Assert.True(doc.CanEdit);
    }

    [Fact] // setter público (quem calcula é MainViewModel, via mPdf.Editing — este VM só guarda o
    // resultado) -> CanEdit vira o OPOSTO, com notificação de propriedade derivada
    public void IsSignedDocument_SetTrue_FlipsCanEditToFalse_AndNotifiesBothProperties()
    {
        using var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        var changed = new List<string>();
        doc.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        doc.IsSignedDocument = true;

        Assert.False(doc.CanEdit);
        Assert.Contains(nameof(DocumentViewModel.IsSignedDocument), changed);
        Assert.Contains(nameof(DocumentViewModel.CanEdit), changed);
    }

    // ---- Task 6 (Plano 3a): ApplyMarkupCommand (marca-texto/sublinhado/riscado) --------------------

    private static (DocumentViewModel doc, FakePdfEditor fake, List<string> errors) BuildForMarkup(
        string autor = "Autor de Teste")
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-markup-cfg-{Guid.NewGuid():N}");
        var fake = new FakePdfEditor();
        var errors = new List<string>();
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")),
            editor: fake,
            config: new AppConfig(configDir) { Autor = autor },
            notifyError: errors.Add);
        return (doc, fake, errors);
    }

    private static void SelectSomeText(PageViewModel page)
    {
        page.BeginSelection(new Point(10, 10));
        page.UpdateSelection(new Point(300, 20));
    }

    [Fact] // sem seleção ativa: CanExecute false, mesmo com CanEdit=true (default de um doc novo)
    public void ApplyMarkupCommand_CanExecute_FalseWithoutSelection()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;

        Assert.False(d.ApplyMarkupCommand.CanExecute(AnnotationKind.Highlight));
    }

    [Fact] // BeginSelection (mesmo gesto real de arrasto usado pela UI) chama SetSelectionOwner ->
    // HasActiveSelection vira true -> CanExecute reavalia pra true.
    public void ApplyMarkupCommand_CanExecute_TrueAfterSelection()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;

        SelectSomeText(d.Pages[0]);

        Assert.True(d.HasActiveSelection);
        Assert.True(d.ApplyMarkupCommand.CanExecute(AnnotationKind.Highlight));
    }

    [Fact] // documento assinado desabilita mesmo com seleção ativa — CanApplyMarkup = CanEdit &&
    // HasActiveSelection, os dois lados do gate testados juntos aqui.
    public void ApplyMarkupCommand_CanExecute_FalseWhenSignedDocument()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        SelectSomeText(d.Pages[0]);
        Assert.True(d.ApplyMarkupCommand.CanExecute(AnnotationKind.Highlight)); // sanity antes

        d.IsSignedDocument = true;

        Assert.False(d.ApplyMarkupCommand.CanExecute(AnnotationKind.Highlight));
    }

    [Fact] // ponta a ponta: seleção real -> ApplyMarkupCommand.ExecuteAsync -> o FAKE recebe a
    // AnnotationData certa (kind/página/quads/cor/autor) -> Session.ApplyEdit troca o snapshot pro
    // resultado do fake -> seleção limpa -> undo habilitado de graça (ApplyEdit, não Apply). A limpeza
    // da seleção acontece via Session.Applied -> OnSessionApplied (Task 3, pré-existente), NÃO por uma
    // chamada explícita dentro de ApplyMarkup — ver ACHADO no doc XML de DocumentViewModel.ApplyMarkup
    // (testei via mutação: uma chamada extra ali seria código morto). A prova de mutação de verdade
    // (que uma chamada REAL de ClearSelection existe em algum lugar do caminho) está em
    // SessionApply_ClearsActiveSelection_MutationProofOnOnSessionApplied, abaixo.
    public async Task ApplyMarkupCommand_Highlight_AppliesEdit_PassesRightData_ClearsSelection_EnablesUndo()
    {
        var (doc, fake, errors) = BuildForMarkup(autor: "Fulano de Tal");
        using var d = doc;
        var page = d.Pages[0];
        SelectSomeText(page);
        var expectedRects = page.SelectionPointRects.ToList();
        Assert.NotEmpty(expectedRects); // sanity: a seleção real (fixture-a4 tem texto) produziu retângulos
        Assert.False(d.CanUndo);

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);

        Assert.Empty(errors);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.Highlight, sent.Kind);
        Assert.Equal(0, sent.PageIndex);
        Assert.Equal(d.SelectedMarkupColorArgb, sent.ColorArgb);
        Assert.Equal("Fulano de Tal", sent.Author);
        Assert.NotNull(sent.Quads);
        Assert.Equal(expectedRects.Count, sent.Quads!.Count);
        for (int i = 0; i < expectedRects.Count; i++)
        {
            Assert.Equal(expectedRects[i].X, sent.Quads[i].LeftPt, 0.01);
            Assert.Equal(expectedRects[i].Y, sent.Quads[i].BottomPt, 0.01);
            Assert.Equal(expectedRects[i].X + expectedRects[i].Width, sent.Quads[i].RightPt, 0.01);
            Assert.Equal(expectedRects[i].Y + expectedRects[i].Height, sent.Quads[i].TopPt, 0.01);
        }

        // Session.ApplyEdit REALMENTE aconteceu: Snapshot virou o marcador do fake (30 páginas), não
        // mais o documento original (1 página) — prova por CONTAGEM DE PÁGINA, não só "!= array antigo".
        Assert.Equal(30, d.Pages.Count);
        Assert.Equal(Fixtures.ThirtyPages(), d.Session.Snapshot);

        // seleção limpa
        Assert.False(d.HasActiveSelection);
        Assert.False(d.ApplyMarkupCommand.CanExecute(AnnotationKind.Highlight));

        // undo habilitado de graça — ApplyEdit (não Apply) empilhou o snapshot pré-edição
        Assert.True(d.CanUndo);
    }

    [Fact] // typed net (deveria ser inalcançável via UI real — CanExecute já barra num doc assinado —
    // mas testado como rede de segurança, mesmo espírito de MainViewModel.ApplyEditToSelectedDocument,
    // Task 5): fake simula PdfSignedDocumentException -> notificado, Snapshot INTOCADO, seleção NÃO
    // limpa (usuário pode querer tentar de novo depois de "Editar uma cópia").
    public async Task ApplyMarkupCommand_PdfSignedDocumentException_NotifiesError_LeavesSnapshotAndSelectionUnchanged()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ThrowOnAddAnnotation = new PdfSignedDocumentException("assinado");
        SelectSomeText(d.Pages[0]);
        var snapshotBefore = d.Session.Snapshot;

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);

        var msg = Assert.Single(errors);
        Assert.Contains("assinado", msg, System.StringComparison.OrdinalIgnoreCase);
        Assert.Same(snapshotBefore, d.Session.Snapshot);
        Assert.True(d.HasActiveSelection);
        Assert.False(d.CanUndo);
    }

    [Fact] // qualquer outra falha do iText (ex.: PDF corrompido) -> PdfEditingException notificada em
    // pt-BR (a mensagem em si) — mesmo tratamento/preservação de estado do teste acima.
    public async Task ApplyMarkupCommand_PdfEditingException_NotifiesError_LeavesSnapshotAndSelectionUnchanged()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ThrowOnAddAnnotation = new PdfEditingException("Não foi possível processar o PDF.");
        SelectSomeText(d.Pages[0]);
        var snapshotBefore = d.Session.Snapshot;

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);

        var msg = Assert.Single(errors);
        Assert.Equal("Não foi possível processar o PDF.", msg);
        Assert.Same(snapshotBefore, d.Session.Snapshot);
        Assert.True(d.HasActiveSelection);
        Assert.False(d.CanUndo);
    }

    [Fact] // Item 2 (revisão final pré-merge) — TryApplyEdit: o editor devolve bytes "com sucesso" (não
    // uma exceção tipada), mas o PDFium os REJEITA (ArgumentException crua vinda de dentro de
    // Session.ApplyEdit) — o comando precisa completar SEM lançar (senão o AsyncRelayCommand relança em
    // cima do Dispatcher -> crash do processo), notificar em pt-BR, e preservar Snapshot/seleção — mesmo
    // contrato dos 2 testes de exceção tipada acima.
    public async Task ApplyMarkupCommand_EditorReturnsInvalidPdfBytes_NotifiesError_LeavesSnapshotAndSelectionUnchanged()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ReturnInvalidPdfBytes = true;
        SelectSomeText(d.Pages[0]);
        var snapshotBefore = d.Session.Snapshot;

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);

        var msg = Assert.Single(errors);
        Assert.Contains("não pôde ser aplicado", msg, System.StringComparison.OrdinalIgnoreCase);
        Assert.Same(snapshotBefore, d.Session.Snapshot);
        Assert.True(d.HasActiveSelection);
        Assert.False(d.CanUndo);
    }

    [Fact] // Autor da anotação vem de AppConfig.Autor (injetado no ctor), não de um literal fixo — um
    // valor DIFERENTE do usado no teste "AppliesEdit" acima prova que não é coincidência/hardcode.
    public async Task ApplyMarkupCommand_UsesAutorFromInjectedAppConfig()
    {
        var (doc, fake, _) = BuildForMarkup(autor: "Ciclano da Silva");
        using var d = doc;
        SelectSomeText(d.Pages[0]);

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);

        Assert.Equal("Ciclano da Silva", fake.LastAnnotation!.Author);
    }

    [Fact] // botões de cor da toolbar (brief): 3 comandos parameterless trocam SelectedMarkupColorArgb
    // — default amarelo, sem exigir seleção ativa nem CanEdit (escolher cor não é uma edição em si).
    public void SelectColorCommands_ChangeSelectedMarkupColorArgb()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        Assert.Equal(DocumentViewModel.ColorAmarelo, d.SelectedMarkupColorArgb); // default

        d.SelectColorVerdeCommand.Execute(null);
        Assert.Equal(DocumentViewModel.ColorVerde, d.SelectedMarkupColorArgb);

        d.SelectColorVermelhoCommand.Execute(null);
        Assert.Equal(DocumentViewModel.ColorVermelho, d.SelectedMarkupColorArgb);

        d.SelectColorAmareloCommand.Execute(null);
        Assert.Equal(DocumentViewModel.ColorAmarelo, d.SelectedMarkupColorArgb);
    }

    [Fact] // MUTATION-PROOF de verdade (ver ACHADO no doc XML de DocumentViewModel.ApplyMarkup): a
    // limpeza de seleção depois de uma edição NÃO vem de uma chamada explícita em ApplyMarkup — vem de
    // Session.Applied -> OnSessionApplied (Task 3, pré-existente: "ClearSelection(); //
    // _pageWithSelection apontava pra um PageViewModel que está prestes a sumir"). Esta prova roda
    // Session.Apply DIRETO (nem passa por ApplyMarkupCommand) pra isolar exatamente essa linha.
    // Verificado manualmente: comentar a chamada de ClearSelection() dentro de OnSessionApplied faz
    // esta asserção falhar (HasActiveSelection continua true); restaurado, teste volta a verde — ver
    // task-6-report.md pela evidência RED->GREEN completa.
    public void SessionApply_ClearsActiveSelection_MutationProofOnOnSessionApplied()
    {
        using var d = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
        SelectSomeText(d.Pages[0]);
        Assert.True(d.HasActiveSelection); // sanity: seleção real antes do Apply

        d.Session.Apply(Fixtures.ThirtyPages());

        Assert.False(d.HasActiveSelection);
    }

    // ==== Task 7 (Plano 3a): Nota adesiva + caixa de texto (criar/editar/mover/excluir + lift) ======

    private static (DocumentViewModel doc, FakePdfEditor fake, FakeAnnotationTextDialogService dialog, List<string> errors) BuildForAnnotations(
        string autor = "Autor de Teste", IFileDialogService? dialogs = null)
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-annot-cfg-{Guid.NewGuid():N}");
        var fake = new FakePdfEditor();
        var dialog = new FakeAnnotationTextDialogService();
        var errors = new List<string>();
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")),
            editor: fake,
            config: new AppConfig(configDir) { Autor = autor },
            notifyError: errors.Add,
            annotationDialog: dialog,
            dialogs: dialogs);
        return (doc, fake, dialog, errors);
    }

    // Grava `bytes` num arquivo TEMPORÁRIO de verdade (Task 3, Plano 7 — "🖼 Imagem") — ToggleImageTool
    // lê o caminho devolvido pelo diálogo via `File.ReadAllBytes` de verdade (não seamado), mesmo
    // padrão de `ImageImportTests.WriteFile`. Sem limpeza explícita (mesmo espírito de `configDir` em
    // BuildForAnnotations acima — resíduo de %TEMP% aceito, nunca tocado pela suíte de novo).
    private static string WriteTempImageFile(byte[] bytes, string ext = ".png")
    {
        var path = Path.Combine(Path.GetTempPath(), $"mpdf-imgtool-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- ActiveTool: toggles mutuamente exclusivos, gated em CanEdit --------------------------------

    [Fact]
    public void ToggleTools_AreMutuallyExclusive()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        Assert.Equal(AnnotationTool.None, d.ActiveTool);

        d.ToggleStickyNoteToolCommand.Execute(null);
        Assert.Equal(AnnotationTool.StickyNote, d.ActiveTool);

        d.ToggleFreeTextToolCommand.Execute(null);
        Assert.Equal(AnnotationTool.FreeText, d.ActiveTool); // trocou, não acumulou

        d.ToggleFreeTextToolCommand.Execute(null); // clicar de novo no MESMO botão desliga
        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    [Fact] // mesmo gate CanEdit já usado por ApplyMarkupCommand (Task 6) — documento assinado desabilita.
    public void ToolCommands_CanExecute_FalseWhenSignedDocument()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        Assert.True(d.ToggleStickyNoteToolCommand.CanExecute(null));
        Assert.True(d.ToggleFreeTextToolCommand.CanExecute(null));

        d.IsSignedDocument = true;

        Assert.False(d.ToggleStickyNoteToolCommand.CanExecute(null));
        Assert.False(d.ToggleFreeTextToolCommand.CanExecute(null));
    }

    // ---- PlaceAnnotationAtAsync: ferramenta -> clique -> diálogo -> AddAnnotation -> tool off --------

    [Fact] // StickyNote: ícone 20x20pt FIXO no ponto clicado (canto inferior-esquerdo = ponto clicado),
    // cor = SelectedMarkupColorArgb (mesmo seletor de cor da toolbar, Task 6), autor do AppConfig.
    public async Task PlaceAnnotationAtAsync_StickyNoteTool_AddsFixedSizeIcon_AppliesEdit_DeactivatesTool()
    {
        var (doc, fake, dialog, errors) = BuildForAnnotations();
        using var d = doc;
        dialog.Result = "minha nota";
        d.ActiveTool = AnnotationTool.StickyNote;

        await d.PlaceAnnotationAtAsync(0, 100, 700);

        Assert.Empty(errors);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.StickyNote, sent.Kind);
        Assert.Equal(0, sent.PageIndex);
        Assert.Equal(100, sent.LeftPt, 0.01); Assert.Equal(700, sent.BottomPt, 0.01);
        Assert.Equal(120, sent.RightPt, 0.01); Assert.Equal(720, sent.TopPt, 0.01); // 20x20pt
        Assert.Equal("minha nota", sent.Content);
        Assert.Equal(d.SelectedMarkupColorArgb, sent.ColorArgb);
        Assert.Equal("Autor de Teste", sent.Author);

        Assert.Equal(30, d.Pages.Count); // ApplyEdit trocou Snapshot pro marcador do fake (30 páginas)
        Assert.Equal(AnnotationTool.None, d.ActiveTool); // one-shot: desativa sozinha
    }

    [Fact] // FreeText: caixa 200x60pt FIXA (espelho exato do teste acima).
    public async Task PlaceAnnotationAtAsync_FreeTextTool_AddsDefaultSizeBox_AppliesEdit_DeactivatesTool()
    {
        var (doc, fake, dialog, _) = BuildForAnnotations();
        using var d = doc;
        dialog.Result = "meu texto";
        d.ActiveTool = AnnotationTool.FreeText;

        await d.PlaceAnnotationAtAsync(0, 50, 500);

        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.FreeText, sent.Kind);
        Assert.Equal(50, sent.LeftPt, 0.01); Assert.Equal(500, sent.BottomPt, 0.01);
        Assert.Equal(250, sent.RightPt, 0.01); Assert.Equal(560, sent.TopPt, 0.01); // 200x60pt
        Assert.Equal("meu texto", sent.Content);
        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    [Fact] // clique perto da borda -> retângulo CLAMPADO pra dentro da página (fixture-a4 é 595x842pt),
    // tamanho preservado (só a posição desloca).
    public async Task PlaceAnnotationAtAsync_NearPageEdge_ClampsRectToPageBounds()
    {
        var (doc, fake, dialog, _) = BuildForAnnotations();
        using var d = doc;
        dialog.Result = "perto da borda";
        d.ActiveTool = AnnotationTool.FreeText; // 200x60pt

        await d.PlaceAnnotationAtAsync(0, 590, 5); // canto inferior-direito da página

        var sent = fake.LastAnnotation!;
        Assert.True(sent.RightPt <= 595.01, $"RightPt {sent.RightPt} extrapolou a largura da página");
        Assert.True(sent.LeftPt >= -0.01, $"LeftPt {sent.LeftPt} negativo");
        Assert.True(sent.TopPt <= 842.01, $"TopPt {sent.TopPt} extrapolou a altura da página");
        Assert.True(sent.BottomPt >= -0.01, $"BottomPt {sent.BottomPt} negativo");
        Assert.Equal(200, sent.RightPt - sent.LeftPt, 0.01); // tamanho preservado
        Assert.Equal(60, sent.TopPt - sent.BottomPt, 0.01);
    }

    [Fact] // diálogo cancelado -> nenhum AddAnnotation/ApplyEdit, ferramenta continua ATIVA.
    public async Task PlaceAnnotationAtAsync_DialogCancelled_DoesNotApplyEdit_ToolStaysActive()
    {
        var (doc, fake, dialog, _) = BuildForAnnotations();
        using var d = doc;
        dialog.Result = null;
        d.ActiveTool = AnnotationTool.StickyNote;

        await d.PlaceAnnotationAtAsync(0, 100, 700);

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Equal(AnnotationTool.StickyNote, d.ActiveTool);
        Assert.Single(d.Pages); // nenhuma edição aplicada — documento original (1 página)
    }

    [Fact] // sem ferramenta ativa: no-op (defesa — a UI real só chama isto com uma ferramenta ligada).
    public async Task PlaceAnnotationAtAsync_NoActiveTool_DoesNothing()
    {
        var (doc, fake, dialog, _) = BuildForAnnotations();
        using var d = doc;
        dialog.Result = "não deveria ser usado";

        await d.PlaceAnnotationAtAsync(0, 100, 700);

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Equal(0, dialog.CallCount);
    }

    [Fact] // MUTATION-PROOF (regra explícita da task): comentar "ActiveTool = AnnotationTool.None" no
    // fim de PlaceAnnotationAtAsync faz ESTE teste falhar — verificado manualmente (RED->GREEN, ver
    // task-7-report.md). Isolado do teste "AppliesEdit..." acima pra nomear exatamente o que prova.
    public async Task PlaceAnnotationAtAsync_Success_DeactivatesToolAfterPlacement_MutationProof()
    {
        var (doc, _, dialog, _) = BuildForAnnotations();
        using var d = doc;
        dialog.Result = "texto";
        d.ActiveTool = AnnotationTool.FreeText;

        await d.PlaceAnnotationAtAsync(0, 100, 700);

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    // ---- AnnotationsByPage (cache) / HitTestAnnotation / SelectAnnotationAt -------------------------

    [Fact] // RefreshAnnotationsByPageAsync agrupa por página — fixture-a4 tem 1 página só.
    public async Task RefreshAnnotationsByPageAsync_PopulatesCachePerPage()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        var a1 = new AnnotationData { Id = "a1", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 1, BottomPt = 1, RightPt = 2, TopPt = 2 };
        fake.ReadAnnotationsResult = new[] { a1 };

        await d.RefreshAnnotationsByPageAsync();

        Assert.Single(d.AnnotationsByPage); // 1 página
        Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal("a1", d.AnnotationsByPage[0][0].Id);
    }

    [Fact] // geometria pura: ponto dentro do retângulo de UMA anotação -> aquela anotação; fora de
    // qualquer retângulo -> null.
    public async Task HitTestAnnotation_PointInsideRect_ReturnsThatAnnotation()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        var a1 = new AnnotationData { Id = "a1", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        var a2 = new AnnotationData { Id = "a2", Kind = AnnotationKind.FreeText, PageIndex = 0, LeftPt = 100, BottomPt = 100, RightPt = 300, TopPt = 160 };
        fake.ReadAnnotationsResult = new[] { a1, a2 };
        await d.RefreshAnnotationsByPageAsync();

        Assert.Equal("a1", d.HitTestAnnotation(0, 20, 20)?.Id);
        Assert.Equal("a2", d.HitTestAnnotation(0, 200, 130)?.Id);
        Assert.Null(d.HitTestAnnotation(0, 500, 500));
    }

    [Fact] // topmost-last: 2 retângulos sobrepostos na mesma área — a ÚLTIMA da lista ("de cima") vence.
    public async Task HitTestAnnotation_OverlappingRects_ReturnsTopmostLast()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        var deBaixo = new AnnotationData { Id = "de-baixo", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 50, TopPt = 50 };
        var deCima = new AnnotationData { Id = "de-cima", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 20, BottomPt = 20, RightPt = 60, TopPt = 60 };
        fake.ReadAnnotationsResult = new[] { deBaixo, deCima };
        await d.RefreshAnnotationsByPageAsync();

        Assert.Equal("de-cima", d.HitTestAnnotation(0, 30, 30)?.Id); // dentro dos 2 retângulos
    }

    [Fact] // C1a (revisão Opus, crash real fechado): cache construído a partir de um snapshot que já
    // NÃO é mais o Session.Snapshot corrente (ex.: um Apply aconteceu e a atualização assíncrona do
    // cache ainda não chegou) nunca serve um hit-test — mesmo que o ponto caia GEOMETRICAMENTE dentro
    // de um retângulo do cache velho, o resultado é `null` (a View degrada pro fallback de seleção de
    // texto). Fecha também I3 (mover em silêncio pra posição errada): sem seleção não há o que mover.
    public async Task HitTestAnnotation_StaleCache_ReturnsNullEvenIfGeometricallyInsideRect()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        var a1 = new AnnotationData { Id = "a1", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { a1 };
        await d.RefreshAnnotationsByPageAsync();
        Assert.NotNull(d.HitTestAnnotation(0, 20, 20)); // sanity: acerta enquanto o cache está FRESCO

        // ACHADO real (não hipotético): a 1ª versão deste teste só confiava em "sem await, o Task.Run
        // do refresh não teve tempo de rodar ainda" — FLAKOU sob a carga da suíte completa (thread do
        // pool ociosa terminou a leitura trivial do fake EM PARALELO antes da asserção rodar). Trava
        // determinística: a PRÓXIMA ReadAnnotations (a que OnSessionApplied dispara no Apply abaixo)
        // bloqueia até o teste liberar — garante que o cache AINDA não pôde ter atualizado.
        fake.ReadAnnotationsGate = new TaskCompletionSource<bool>();

        // Muda Session.Snapshot — dispara OnSessionApplied -> RefreshAnnotationsByPageAsync fire-and-
        // forget, que agora fica PRESO na trava acima até este teste liberar.
        d.Session.Apply(Fixtures.ThirtyPages());

        Assert.Null(d.HitTestAnnotation(0, 20, 20)); // cache desatualizado -> hit-test SEMPRE null

        fake.ReadAnnotationsGate.SetResult(true); // libera a leitura travada — limpeza, sem Task presa
    }

    [Fact] // clique fora de qualquer retângulo desmarca a seleção ativa.
    public async Task SelectAnnotationAt_NoHit_ClearsSelectedAnnotation()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        var a1 = new AnnotationData { Id = "a1", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { a1 };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);

        d.SelectAnnotationAt(0, 500, 500);

        Assert.Null(d.SelectedAnnotation);
    }

    // ---- COSTURA DE ROTAÇÃO (Task 3, Plano 3b — requisito de 1ª ordem) ------------------------------
    // PDFium relata o quadro ROTACIONADO (PageSizes/hit-tests/drags) enquanto os retângulos de
    // AnnotationData do iText ficam SEMPRE no quadro NÃO-ROTACIONADO (provado no Task 2). v1: interação
    // de anotação DESLIGA em página girada (ver doc XML de DocumentViewModel.IsPageRotated) em vez de
    // compor a transformação — os testes abaixo provam o gate (hit-test nulo + no-op com aviso) e um
    // controle NEGATIVO (página SEM rotação continua funcionando normalmente).

    [Fact]
    public async Task HitTestAnnotation_PageRotated_ReturnsNullEvenIfGeometricallyInsideRect()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        var a1 = new AnnotationData { Id = "a1", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { a1 };
        fake.PageRotationsResult = new[] { 90 };
        await d.RefreshAnnotationsByPageAsync();

        Assert.Null(d.HitTestAnnotation(0, 20, 20)); // geometricamente dentro do retângulo — mesmo assim null
    }

    [Fact] // controle negativo: página SEM rotação (0°) continua com hit-test normal — o gate não
    // over-bloqueia página nenhuma que não esteja de fato girada.
    public async Task HitTestAnnotation_PageNotRotated_StillHitTestsNormally()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        var a1 = new AnnotationData { Id = "a1", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { a1 };
        fake.PageRotationsResult = new[] { 0 };
        await d.RefreshAnnotationsByPageAsync();

        Assert.Equal("a1", d.HitTestAnnotation(0, 20, 20)?.Id);
    }

    [Fact] // M2 (revisão Opus) — controle negativo pro GATE DE ESCRITA (não só o de leitura acima):
    // `PageRotationsResult` explícito `new[] { 0 }` — NÃO o array vazio que só o padrão "cache ainda
    // não carregou" da fake usa por default — prova que `IsPageRotated` avalia corretamente um array
    // do TAMANHO REAL que `GetPageRotations` de produção sempre devolve (nunca vazio pra um documento
    // com páginas), não só o fallback "desconhecido = não girada" que um array vazio dispararia por
    // acidente e deixaria este caminho sem cobertura real.
    public async Task PlaceAnnotationAtAsync_PageNotRotated_StillPlaces()
    {
        var (doc, fake, dialog, errors) = BuildForAnnotations();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 0 };
        await d.RefreshAnnotationsByPageAsync();
        dialog.Result = "texto qualquer";
        d.ToggleStickyNoteToolCommand.Execute(null);

        await d.PlaceAnnotationAtAsync(0, 100, 100);

        Assert.Equal(1, fake.AddAnnotationCallCount);
        Assert.Empty(errors);
        Assert.Equal(AnnotationTool.None, d.ActiveTool); // one-shot: ferramenta desativa após sucesso
    }

    [Fact] // I1 (revisão Opus) — MUTATION-PROOF: sem `await EnsureRotationCacheFreshAsync()` antes do
    // gate (ver doc XML de `IsPageRotated`/`EnsureRotationCacheFreshAsync`), este teste FALHARIA — a
    // escrita passaria despercebida numa página que ACABOU de virar rotacionada. Simula a JANELA DE
    // OBSOLESCÊNCIA que Delete/Move (organizador, Task 3) introduzem ao RE-INDEXAR páginas: um
    // `Session.Apply` troca `Session.Snapshot` SEM deixar o refresh fire-and-forget de
    // `OnSessionApplied` alcançar (xUnit puro não bombeia `Dispatcher` — mesmo padrão já documentado em
    // `HitTestAnnotation_StaleCache_ReturnsNullEvenIfGeometricallyInsideRect` acima) — o cache de
    // rotação fica preso no snapshot ANTIGO até este teste forçar a escrita a lidar com isso sozinha.
    public async Task PlaceAnnotationAtAsync_StaleRotationCacheAfterApply_RefreshesBeforeGating()
    {
        var (doc, fake, dialog, errors) = BuildForAnnotations();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 0 }; // documento ORIGINAL: página 0 NÃO girada
        await d.RefreshAnnotationsByPageAsync();
        Assert.Equal(1, fake.GetPageRotationsCallCount);

        // Session.Apply troca o snapshot SEM disparar (de verdade) o refresh fire-and-forget de
        // OnSessionApplied — cache fica OBSOLETO aqui, de propósito.
        d.Session.Apply(Fixtures.ThirtyPages());
        // Cenário do achado do revisor: no documento NOVO, a página 0 (que antes NÃO era) agora ESTÁ
        // rotacionada (ex.: era a antiga página 1, deslocada pro índice 0 por um Delete/Move).
        fake.PageRotationsResult = new[] { 90 };

        dialog.Result = "texto qualquer";
        d.ToggleStickyNoteToolCommand.Execute(null);

        await d.PlaceAnnotationAtAsync(0, 100, 100);

        Assert.True(fake.GetPageRotationsCallCount >= 2,
            "EnsureRotationCacheFreshAsync deveria ter disparado um refresh novo (cache estava obsoleto)");
        Assert.Equal(0, fake.AddAnnotationCallCount); // gateado com dado FRESCO (rotacionado), não o velho
        Assert.Contains(errors, e => e.Contains("Página girada"));
    }

    [Fact]
    public async Task PlaceAnnotationAtAsync_PageRotated_NotifiesAndDoesNotAddAnnotation()
    {
        var (doc, fake, dialog, errors) = BuildForAnnotations();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>(); // Read/GetPageRotations = MESMO refresh (ver doc XML)
        fake.PageRotationsResult = new[] { 90 };
        await d.RefreshAnnotationsByPageAsync();
        dialog.Result = "texto qualquer";
        d.ToggleStickyNoteToolCommand.Execute(null);

        await d.PlaceAnnotationAtAsync(0, 100, 100);

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Contains(errors, e => e.Contains("Página girada"));
        Assert.Equal(AnnotationTool.StickyNote, d.ActiveTool); // ferramenta continua ativa (no-op, não falha)
    }

    [Fact]
    public async Task PlaceStampAtAsync_PageRotated_NotifiesAndDoesNotAddAnnotation()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 180 };
        await d.RefreshAnnotationsByPageAsync();
        d.ToggleStampTool(Fixtures.OnePixelPng());

        await d.PlaceStampAtAsync(0, 100, 100);

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Contains(errors, e => e.Contains("Página girada"));
    }

    [Fact]
    public async Task CommitDrawingAsync_PageRotated_NotifiesAndDoesNotAddAnnotation()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 270 };
        await d.RefreshAnnotationsByPageAsync();
        d.ToggleRectangleToolCommand.Execute(null);

        await d.CommitDrawingAsync(0, new[] { new PdfPoint(10, 10), new PdfPoint(50, 50) });

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Contains(errors, e => e.Contains("Página girada"));
    }

    [Fact]
    public async Task ApplyMarkup_PageRotated_NotifiesAndDoesNotAddAnnotation()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 90 };
        await d.RefreshAnnotationsByPageAsync();
        SelectSomeText(d.Pages[0]);

        await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Contains(errors, e => e.Contains("Página girada"));
    }

    // ---- Del exclui -----------------------------------------------------------------------------

    [Fact] // Del -> RemoveAnnotation com o Id CERTO -> ApplyEdit -> seleção limpa (via OnSessionApplied,
    // mesmo mecanismo já provado pra seleção de texto na Task 6).
    public async Task DeleteSelectedAnnotationCommand_RemovesViaApplyEdit_WithRightId()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        var target = new AnnotationData { Id = "para-excluir", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { target };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);
        Assert.True(d.DeleteSelectedAnnotationCommand.CanExecute(null));

        await d.DeleteSelectedAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal(1, fake.RemoveAnnotationCallCount);
        Assert.Equal("para-excluir", fake.LastRemovedId);
        Assert.Equal(30, d.Pages.Count); // ApplyEdit trocou Snapshot pro marcador do fake
        Assert.Null(d.SelectedAnnotation); // OnSessionApplied limpa
    }

    [Fact]
    public void DeleteSelectedAnnotationCommand_CanExecute_FalseWithoutSelection()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        Assert.False(d.DeleteSelectedAnnotationCommand.CanExecute(null));
    }

    [Fact] // C1b (revisão Opus, crash real fechado): `RemoveAnnotation` "não encontrada" chega como
    // `InvalidOperationException` CRUA (mesmo tipo que `mPdf.Editing.PdfEditor.RemoveAnnotation` lança
    // de verdade — ver `RemoveAnnotation_UnknownId_Throws` em PdfEditorTests). Usa o hook
    // `FakePdfEditor.ThrowOnRemoveAnnotation` (existia desde a 1ª rodada desta task, nunca exercitado).
    // Prova o caminho "notifica, NÃO derruba o processo" (a rede tipada nova em DeleteSelectedAnnotation)
    // — o teste TERMINAR sem lançar já é metade da prova; a outra metade é o self-heal (cache renovado).
    public async Task DeleteSelectedAnnotationCommand_RemoveThrowsInvalidOperationException_NotifiesError_DoesNotCrash_SelfHeals()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        var target = new AnnotationData { Id = "fantasma", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { target };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);
        fake.ThrowOnRemoveAnnotation = new InvalidOperationException("Anotação 'fantasma' não encontrada no PDF.");
        // Self-heal (prova DIRETA, não indireta): troca o resultado do fake ANTES de disparar o Delete
        // — a ÚNICA leitura que pode acontecer depois disso é a de auto-cura dentro do catch de
        // DeleteSelectedAnnotation (RefreshAnnotationsByPageAsync é AWAITADA lá, não fire-and-forget —
        // ver doc XML —, então ao `ExecuteAsync` completar o cache já reflete este valor NOVO).
        var updated = new AnnotationData { Id = "outra", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 1, BottomPt = 1, RightPt = 2, TopPt = 2 };
        fake.ReadAnnotationsResult = new[] { updated };

        var ex = await Record.ExceptionAsync(() => d.DeleteSelectedAnnotationCommand.ExecuteAsync(null));

        Assert.Null(ex); // NUNCA escapa pro AsyncRelayCommand/Dispatcher — é isso que fechava o crash
        var msg = Assert.Single(errors);
        Assert.Contains("fantasma", msg);
        Assert.Single(d.Pages); // Session.ApplyEdit NUNCA aconteceu — snapshot intocado (fixture-a4, 1 página)

        var refreshed = Assert.Single(d.AnnotationsByPage[0]); // self-heal PROVADO: cache já é o NOVO
        Assert.Equal("outra", refreshed.Id);
    }

    // ---- editar (lift): Remove -> modifica -> Add(mesmo Id) ----------------------------------------

    [Fact] // Content muda, cor NULA preservada (não inventa cor), demais campos (posição/autor) intactos.
    public async Task EditSelectedAnnotationCommand_Lifts_RemoveThenAddSameId_ContentChanged_ColorNullPreserved()
    {
        var (doc, fake, dialog, errors) = BuildForAnnotations();
        using var d = doc;
        var original = new AnnotationData
        {
            Id = "nota-1", Kind = AnnotationKind.StickyNote, PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30,
            ColorArgb = null, Content = "texto antigo", Author = "Fulano",
        };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        dialog.Result = "texto novo";
        var snapshotBefore = d.Session.Snapshot;
        Assert.False(d.CanUndo);

        await d.EditSelectedAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal("texto antigo", dialog.LastInitialText); // diálogo pré-preenchido com o Content ATUAL
        Assert.Equal(1, fake.RemoveAnnotationCallCount);
        Assert.Equal("nota-1", fake.LastRemovedId);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var lifted = fake.LastAnnotation!;
        Assert.Equal("nota-1", lifted.Id); // MESMO Id — lift, não uma anotação nova
        Assert.Equal("texto novo", lifted.Content);
        Assert.Null(lifted.ColorArgb); // preservado — não inventa cor
        Assert.Equal("Fulano", lifted.Author); // preservado
        Assert.Equal(10, lifted.LeftPt); Assert.Equal(30, lifted.TopPt); // posição preservada

        // I2 (revisão Opus): pin que o lift INTEIRO (Remove+Add) resulta em EXATAMENTE 1 ApplyEdit — o
        // snapshot mudou (marcador do fake, 30 páginas) E 1 ÚNICO Undo() basta pra voltar ao estado
        // PRÉ-lift, byte a byte (não uma pilha de 2 entradas de undo pra uma operação lógica só).
        Assert.Equal(30, d.Pages.Count);
        Assert.True(d.CanUndo);
        d.UndoCommand.Execute(null);
        Assert.Same(snapshotBefore, d.Session.Snapshot);
        Assert.False(d.CanUndo);
    }

    [Fact] // diálogo cancelado -> nenhum Remove/Add, seleção intocada.
    public async Task EditSelectedAnnotationCommand_DialogCancelled_DoesNothing()
    {
        var (doc, fake, dialog, _) = BuildForAnnotations();
        using var d = doc;
        var original = new AnnotationData { Id = "nota-1", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30, Content = "original" };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        dialog.Result = null;

        await d.EditSelectedAnnotationCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.RemoveAnnotationCallCount);
        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.NotNull(d.SelectedAnnotation);
    }

    [Fact]
    public void EditSelectedAnnotationCommand_CanExecute_FalseWithoutSelection()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        Assert.False(d.EditSelectedAnnotationCommand.CanExecute(null));
    }

    // ---- mover (lift): mesmo pipeline, muda geometria em vez de Content -----------------------------

    [Fact] // arrastar -> lift com NOVA posição (mesmo tamanho, Left/Bottom deslocados), Content preservado.
    public async Task MoveSelectedAnnotationAsync_Lifts_WithNewPosition()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        var original = new AnnotationData { Id = "nota-1", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30, Content = "fixa" };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        var snapshotBefore = d.Session.Snapshot;
        Assert.False(d.CanUndo);

        await d.MoveSelectedAnnotationAsync(100, 200);

        Assert.Empty(errors);
        Assert.Equal(1, fake.RemoveAnnotationCallCount);
        Assert.Equal("nota-1", fake.LastRemovedId);
        var lifted = fake.LastAnnotation!;
        Assert.Equal("nota-1", lifted.Id);
        Assert.Equal(100, lifted.LeftPt, 0.01); Assert.Equal(200, lifted.BottomPt, 0.01);
        Assert.Equal(120, lifted.RightPt, 0.01); Assert.Equal(220, lifted.TopPt, 0.01); // 20x20pt preservado
        Assert.Equal("fixa", lifted.Content);

        // I2 (revisão Opus): mesmo pin do teste de editar acima — o snapshot mudou E 1 Undo() só basta
        // pra restaurar o estado PRÉ-lift byte a byte (o Remove+Add não vira 2 entradas de undo).
        Assert.Equal(30, d.Pages.Count);
        Assert.True(d.CanUndo);
        d.UndoCommand.Execute(null);
        Assert.Same(snapshotBefore, d.Session.Snapshot);
        Assert.False(d.CanUndo);
    }

    [Fact]
    public async Task MoveSelectedAnnotationAsync_NoSelection_DoesNothing()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;

        await d.MoveSelectedAnnotationAsync(100, 200);

        Assert.Equal(0, fake.RemoveAnnotationCallCount);
        Assert.Equal(0, fake.AddAnnotationCallCount);
    }

    [Fact] // I4 (revisão Opus): a saída PRECOCE por CanEdit=false (documento assinado) ainda restaura o
    // overlay pra posição REAL da anotação — a View já deslocou AnnotationSelectionRect pra uma posição
    // de PREVIEW durante o arrasto (Page_MouseMove) ANTES de MoveSelectedAnnotationAsync descobrir que o
    // documento está assinado; sem a restauração, o overlay ficaria "grudado" numa posição que a
    // anotação nunca teve de verdade.
    public async Task MoveSelectedAnnotationAsync_SignedDocument_ResetsOverlayToActualPosition()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        var original = new AnnotationData { Id = "nota-1", Kind = AnnotationKind.StickyNote, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);
        var expected = PageViewModel.PointRectToScreenRect(10, 10, 30, 30, d.Zoom, d.Pages[0].HeightPt);
        Assert.Equal(expected, d.Pages[0].AnnotationSelectionRect); // sanity: overlay já na posição REAL

        // simula o preview de arrasto que a View já aplicou (Page_MouseMove) — posição DIFERENTE da real
        d.Pages[0].AnnotationSelectionRect = new Rect(999, 999, 20, 20);
        d.IsSignedDocument = true; // CanEdit vira false

        await d.MoveSelectedAnnotationAsync(500, 500);

        Assert.Equal(0, fake.RemoveAnnotationCallCount); // nunca chegou a tentar o lift
        Assert.Equal(expected, d.Pages[0].AnnotationSelectionRect); // overlay voltou pra posição REAL
    }

    // ==== Task 4 (Plano 7): "📤 Exportar" (página como imagem) =========================================
    //
    // Testes AQUI provam a FIAÇÃO (comando -> diálogo, sem gate) — a lógica de exportação em si (formato/
    // alcance/dpi/cancelamento/colisão/pixels) é testada isoladamente em ExportImageViewModelTests/
    // ExportImageIntegrationTests, sem precisar de DocumentViewModel/janela nenhuma.

    [Fact] // leitura pura: SEM CanExecute -- funciona mesmo com o documento ASSINADO (CanEdit == false).
    public void ExportImageCommand_CanExecute_TrueEvenOnSignedDocument()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        d.IsSignedDocument = true;

        Assert.False(d.CanEdit); // sanity: o documento está de fato no estado "bloqueado pra edição"
        Assert.True(d.ExportImageCommand.CanExecute(null));
    }

    [Fact] // caminho feliz: constrói o VM com o snapshot/contagem de páginas/página CORRENTE certos e
    // delega pro diálogo injetado -- nenhuma janela real aberta (SpyExportImageDialogService).
    public void ExportImageCommand_Execute_ShowsDialogWithSessionSnapshotAndCurrentPage()
    {
        var spy = new SpyExportImageDialogService();
        using var d = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")),
            exportImageDialog: spy);
        d.CurrentPage = 6; // 1-based -- índice 0-based esperado: 5

        d.ExportImageCommand.Execute(null);

        Assert.Equal(1, spy.CallCount);
        Assert.NotNull(spy.LastViewModel);
        Assert.Equal(30, spy.LastViewModel!.PageCount);
        Assert.Equal(5, spy.LastViewModel.CurrentPageIndex); // ASSERTÁVEL: CurrentPage(6, 1-based) - 1 == 5
        Assert.Equal(ExportImagePhase.Options, spy.LastViewModel.Phase); // diálogo mostrado, nada executado ainda
    }

    [Fact] // leitura pura: funciona em documento assinado E o diálogo é de fato alcançado (não recusado
    // antes por nenhum gate) -- prova de disparo complementar à de UiPromptsGuardTests (que prova o
    // SEAM; esta prova o CAMINHO de produção real do comando, com IsSignedDocument=true).
    public void ExportImageCommand_Execute_SignedDocument_ReachesDialog_NoGate()
    {
        var spy = new SpyExportImageDialogService();
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")), exportImageDialog: spy);
        d.IsSignedDocument = true;

        var ex = Record.Exception(() => d.ExportImageCommand.Execute(null));

        Assert.Null(ex);
        Assert.Equal(1, spy.CallCount);
    }

    // ==== Task 8 (Plano 3a): Desenho livre (Ink) + formas (Rectangle/Line/Arrow) =====================

    // ---- ActiveTool: 4 toggles novos, mesma exclusividade mútua/gate CanEdit dos 2 de Task 7 ---------

    [Fact] // mesma exclusividade mútua de ToggleTools_AreMutuallyExclusive (Task 7), estendida aos 4
    // botões novos — inclusive TROCANDO entre os 2 grupos (StickyNote/FreeText <-> Ink/Rectangle/Line/
    // Arrow), não só dentro do mesmo grupo.
    public void ToggleDrawingTools_AreMutuallyExclusive_WithEachOtherAndExistingTools()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;

        d.ToggleInkToolCommand.Execute(null);
        Assert.Equal(AnnotationTool.Ink, d.ActiveTool);

        d.ToggleRectangleToolCommand.Execute(null);
        Assert.Equal(AnnotationTool.Rectangle, d.ActiveTool); // trocou, não acumulou

        d.ToggleLineToolCommand.Execute(null);
        Assert.Equal(AnnotationTool.Line, d.ActiveTool);

        d.ToggleArrowToolCommand.Execute(null);
        Assert.Equal(AnnotationTool.Arrow, d.ActiveTool);

        d.ToggleFreeTextToolCommand.Execute(null); // grupo da Task 7 desliga o grupo novo
        Assert.Equal(AnnotationTool.FreeText, d.ActiveTool);

        d.ToggleArrowToolCommand.Execute(null); // e vice-versa
        Assert.Equal(AnnotationTool.Arrow, d.ActiveTool);

        d.ToggleArrowToolCommand.Execute(null); // clicar de novo no MESMO botão desliga
        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    [Fact] // mesmo gate CanEdit já usado pelas ferramentas de Task 6/7 — documento assinado desabilita.
    public void DrawingToolCommands_CanExecute_FalseWhenSignedDocument()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        Assert.True(d.ToggleInkToolCommand.CanExecute(null));
        Assert.True(d.ToggleRectangleToolCommand.CanExecute(null));
        Assert.True(d.ToggleLineToolCommand.CanExecute(null));
        Assert.True(d.ToggleArrowToolCommand.CanExecute(null));

        d.IsSignedDocument = true;

        Assert.False(d.ToggleInkToolCommand.CanExecute(null));
        Assert.False(d.ToggleRectangleToolCommand.CanExecute(null));
        Assert.False(d.ToggleLineToolCommand.CanExecute(null));
        Assert.False(d.ToggleArrowToolCommand.CanExecute(null));
    }

    // ---- CommitDrawingAsync: path do arrasto -> AnnotationData da forma certa -> AddAnnotation -> tool off

    [Fact] // Ink: TODOS os pontos do path viram InkStrokes[0], em PONTOS de página — path "conhecido"
    // convertido pela MESMA TextSelection.ScreenToPagePoint que a View usa de verdade (não um cálculo
    // duplicado só pro teste), provando a Y-INVERSÃO real: descer 100px de TELA em linha reta precisa
    // resultar num Y de PÁGINA que DIMINUI (origem PDF é inferior-esquerda, Y cresce pra cima).
    public async Task CommitDrawingAsync_InkTool_SendsInkStrokesInPoints_WithYInversion_AppliesEdit_DeactivatesTool()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = AnnotationTool.Ink;

        var screenPath = new[] { new Point(50, 50), new Point(50, 100), new Point(50, 150) };
        double pageHeightPt = d.Pages[0].HeightPt;
        var pathPt = screenPath
            .Select(p => TextSelection.ScreenToPagePoint(p, d.Zoom, pageHeightPt))
            .Select(p => new PdfPoint(p.X, p.Y))
            .ToList();

        await d.CommitDrawingAsync(0, pathPt);

        Assert.Empty(errors);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.Ink, sent.Kind);
        Assert.Equal(0, sent.PageIndex);
        Assert.NotNull(sent.InkStrokes);
        var stroke = Assert.Single(sent.InkStrokes!);
        Assert.Equal(3, stroke.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(pathPt[i].XPt, stroke[i].XPt, 0.01);
            Assert.Equal(pathPt[i].YPt, stroke[i].YPt, 0.01);
        }
        Assert.True(stroke[1].YPt < stroke[0].YPt); // Y INVERSION propriamente dita
        Assert.True(stroke[2].YPt < stroke[1].YPt);

        Assert.Equal(d.SelectedMarkupColorArgb, sent.ColorArgb);
        Assert.Equal("Autor de Teste", sent.Author);
        Assert.Equal(30, d.Pages.Count); // ApplyEdit trocou Snapshot pro marcador do fake (30 páginas)
        Assert.Equal(AnnotationTool.None, d.ActiveTool); // one-shot
    }

    [Fact] // Rectangle: bbox = envelope MIN/MAX dos 2 pontos do arrasto, sem geometria extra (InkStrokes/
    // LineStart-EndPt continuam nulos).
    public async Task CommitDrawingAsync_RectangleTool_SendsBoundingBox_AppliesEdit_DeactivatesTool()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = AnnotationTool.Rectangle;

        var pathPt = new[] { new PdfPoint(50, 100), new PdfPoint(200, 250) };

        await d.CommitDrawingAsync(0, pathPt);

        Assert.Empty(errors);
        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.Rectangle, sent.Kind);
        Assert.Equal(50, sent.LeftPt, 0.01); Assert.Equal(100, sent.BottomPt, 0.01);
        Assert.Equal(200, sent.RightPt, 0.01); Assert.Equal(250, sent.TopPt, 0.01);
        Assert.Null(sent.InkStrokes);
        Assert.Null(sent.LineStartPt); Assert.Null(sent.LineEndPt);
        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    [Fact] // Line: LineStartPt/LineEndPt = âncora/final EXATOS (não normalizados por min/max) — o 1º
    // ponto do path é de propósito o "maior", provando que CommitDrawingAsync não os reordena.
    public async Task CommitDrawingAsync_LineTool_SendsStartAndEndPoints_AppliesEdit_DeactivatesTool()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = AnnotationTool.Line;

        var start = new PdfPoint(300, 320);
        var end = new PdfPoint(30, 40);
        var pathPt = new[] { start, end };

        await d.CommitDrawingAsync(0, pathPt);

        Assert.Empty(errors);
        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.Line, sent.Kind);
        Assert.Equal(start.XPt, sent.LineStartPt!.Value.XPt, 0.01); Assert.Equal(start.YPt, sent.LineStartPt.Value.YPt, 0.01);
        Assert.Equal(end.XPt, sent.LineEndPt!.Value.XPt, 0.01); Assert.Equal(end.YPt, sent.LineEndPt.Value.YPt, 0.01);
        Assert.Equal(30, sent.LeftPt, 0.01); Assert.Equal(40, sent.BottomPt, 0.01); // bbox = envelope
        Assert.Equal(300, sent.RightPt, 0.01); Assert.Equal(320, sent.TopPt, 0.01);
        Assert.Null(sent.InkStrokes);
        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    [Fact] // Arrow: MESMO shape de Line (LineStartPt/LineEndPt) — só o Kind muda; a seta em si (/LE) é
    // responsabilidade do mPdf.Editing, não do VM (ver PdfEditorTests.AddAnnotation_Arrow_RoundTrips_AsArrowKind).
    public async Task CommitDrawingAsync_ArrowTool_SendsStartAndEndPoints_KindArrow_AppliesEdit_DeactivatesTool()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = AnnotationTool.Arrow;

        var pathPt = new[] { new PdfPoint(10, 10), new PdfPoint(100, 150) };

        await d.CommitDrawingAsync(0, pathPt);

        Assert.Empty(errors);
        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.Arrow, sent.Kind);
        Assert.Equal(10, sent.LineStartPt!.Value.XPt, 0.01);
        Assert.Equal(100, sent.LineEndPt!.Value.XPt, 0.01);
        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    [Fact] // A1 (achado da revisão do Task 8, corrigido no fix batch do coordenador): geometria desenhada
    // além da borda da página é CLAMPADA (mesmo ClampToPage de PlaceAnnotationAtAsync/
    // MoveSelectedAnnotationAsync) — CLAMPA-ENTÃO-TRANSLADA: o bbox E cada ponto do traço são deslocados
    // pelo MESMO delta, a FORMA nunca muda (diffs entre pontos preservados), só desliza pra dentro da
    // página inteira. Exemplar: PlaceAnnotationAtAsync_NearPageEdge_ClampsRectToPageBounds.
    public async Task CommitDrawingAsync_InkTool_DragOvershootingEdge_ClampsGeometryToPageBounds()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = AnnotationTool.Ink;
        double pageWidthPt = d.Pages[0].WidthPt;

        // bbox X vai de (largura-15) até (largura+55) -> extrapola a largura da página em 55pt. x2 fica
        // ENTRE x0 e x1 de propósito (não é um 3º extremo) — o bbox continua definido só por x0/x1.
        double x0 = pageWidthPt - 15, x1 = pageWidthPt + 55, x2 = pageWidthPt - 5;
        var pathPt = new[] { new PdfPoint(x0, 100), new PdfPoint(x1, 120), new PdfPoint(x2, 150) };

        await d.CommitDrawingAsync(0, pathPt);

        Assert.Empty(errors);
        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.Ink, sent.Kind);
        Assert.True(sent.RightPt <= pageWidthPt + 0.01, $"RightPt {sent.RightPt} extrapolou a largura da página");
        Assert.True(sent.LeftPt >= -0.01, $"LeftPt {sent.LeftPt} negativo");
        Assert.Equal(x1 - x0, sent.RightPt - sent.LeftPt, 0.01); // largura do bbox PRESERVADA (só deslizou)

        Assert.NotNull(sent.InkStrokes);
        var stroke = Assert.Single(sent.InkStrokes!);
        Assert.Equal(3, stroke.Count);
        // FORMA preservada: diffs entre pontos consecutivos não mudam, só a posição absoluta desliza.
        Assert.Equal(x1 - x0, stroke[1].XPt - stroke[0].XPt, 0.01);
        Assert.Equal(x2 - x0, stroke[2].XPt - stroke[0].XPt, 0.01);
        Assert.Equal(100, stroke[0].YPt, 0.01); Assert.Equal(120, stroke[1].YPt, 0.01); Assert.Equal(150, stroke[2].YPt, 0.01); // Y não precisou clamp
        Assert.True(stroke.All(p => p.XPt <= pageWidthPt + 0.01), "algum ponto do traço ainda extrapola a página depois do clamp");
    }

    [Theory] // sem ferramenta de DESENHO ativa (None ou uma de COLOCAÇÃO, Task 7): no-op — a UI real só
    // chama isto com Ink/Rectangle/Line/Arrow ativa.
    [InlineData(AnnotationTool.None)]
    [InlineData(AnnotationTool.StickyNote)]
    [InlineData(AnnotationTool.FreeText)]
    public async Task CommitDrawingAsync_NonDrawingTool_DoesNothing(AnnotationTool tool)
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = tool;

        await d.CommitDrawingAsync(0, new[] { new PdfPoint(0, 0), new PdfPoint(100, 100) });

        Assert.Equal(0, fake.AddAnnotationCallCount);
    }

    [Fact]
    public async Task CommitDrawingAsync_PageIndexOutOfRange_DoesNothing()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = AnnotationTool.Ink;

        await d.CommitDrawingAsync(99, new[] { new PdfPoint(0, 0), new PdfPoint(50, 50) });

        Assert.Equal(0, fake.AddAnnotationCallCount);
    }

    // ---- MIN-GESTURE GUARD (brief): bbox do path < 3px de diagonal de TELA -> nenhum commit ----------

    [Fact] // zoom 1.0 (PtToPx = 96/72): 1,5pt de página -> 1,5 * 1,3333 = 2px de tela — ABAIXO do
    // limiar de 3px. Ferramenta continua ATIVA (nem tenta, nem desativa).
    public async Task CommitDrawingAsync_DragBelowMinGesture_DoesNotCommit()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = AnnotationTool.Rectangle;
        Assert.Equal(1.0, d.Zoom); // sanity — a conta acima assume zoom 1.0

        var pathPt = new[] { new PdfPoint(100, 100), new PdfPoint(101.5, 100) };

        await d.CommitDrawingAsync(0, pathPt);

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Equal(AnnotationTool.Rectangle, d.ActiveTool);
    }

    [Fact] // mesmo cenário, mas 3pt de página -> 4px de tela: ACIMA do limiar -> commita normalmente
    // (prova que o guard não é um falso-positivo generalizado).
    public async Task CommitDrawingAsync_DragAboveMinGesture_Commits()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = AnnotationTool.Rectangle;

        var pathPt = new[] { new PdfPoint(100, 100), new PdfPoint(103, 100) };

        await d.CommitDrawingAsync(0, pathPt);

        Assert.Equal(1, fake.AddAnnotationCallCount);
    }

    [Fact] // MUTATION-PROOF (regra explícita da task, exemplar Task 7): comentar "ActiveTool =
    // AnnotationTool.None" no fim de CommitDrawingAsync faz ESTE teste falhar — verificado manualmente
    // (RED->GREEN, ver task-8-report.md).
    public async Task CommitDrawingAsync_Success_DeactivatesToolAfterCommit_MutationProof()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ActiveTool = AnnotationTool.Ink;

        await d.CommitDrawingAsync(0, new[] { new PdfPoint(10, 10), new PdfPoint(50, 60) });

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    // ---- EditSelectedAnnotationCommand: filtrado por Kind (só StickyNote/FreeText têm Content textual) -

    [Theory] // Ink/Rectangle/Line/Arrow não têm Content textual editável pelo diálogo — sem este filtro,
    // um duplo-clique num desenho recém-criado abriria "Editar caixa de texto" à toa (o Content gravado
    // nunca apareceria visualmente). Delete continua liberado pra QUALQUER kind (2ª asserção).
    [InlineData(AnnotationKind.Ink)]
    [InlineData(AnnotationKind.Rectangle)]
    [InlineData(AnnotationKind.Line)]
    [InlineData(AnnotationKind.Arrow)]
    [InlineData(AnnotationKind.ImageStamp)] // Task 9 (Plano 3a): mesmo filtro, ImageStamp também não é liftável
    public async Task EditSelectedAnnotationCommand_CanExecute_FalseForDrawingKinds_DeleteStaysTrue(AnnotationKind kind)
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        var target = new AnnotationData { Id = "desenho-1", Kind = kind, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { target };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);

        Assert.False(d.EditSelectedAnnotationCommand.CanExecute(null));
        Assert.True(d.DeleteSelectedAnnotationCommand.CanExecute(null));
    }

    // ---- MoveSelectedAnnotationAsync: geometria PRÓPRIA (InkStrokes/LineStart-EndPt) translada junto --

    [Fact] // mover uma anotação Ink translada CADA ponto de InkStrokes pelo MESMO delta do bbox — sem
    // isso a tinta ficaria "para trás", descolada do retângulo que passou a envolvê-la (PDFium sintetiza
    // a aparência a partir do /InkList, não do /Rect — ver PdfEditor.BuildAnnotation).
    public async Task MoveSelectedAnnotationAsync_InkTool_TranslatesInkStrokesByDelta()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        var original = new AnnotationData
        {
            Id = "traco-1", Kind = AnnotationKind.Ink, PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30,
            InkStrokes = new IReadOnlyList<PdfPoint>[] { new[] { new PdfPoint(10, 10), new PdfPoint(30, 30) } },
        };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);

        await d.MoveSelectedAnnotationAsync(110, 210); // delta = (+100, +200)

        Assert.Empty(errors);
        var lifted = fake.LastAnnotation!;
        Assert.Equal(110, lifted.LeftPt, 0.01); Assert.Equal(210, lifted.BottomPt, 0.01);
        Assert.NotNull(lifted.InkStrokes);
        var stroke = Assert.Single(lifted.InkStrokes!);
        Assert.Equal(110, stroke[0].XPt, 0.01); Assert.Equal(210, stroke[0].YPt, 0.01);
        Assert.Equal(130, stroke[1].XPt, 0.01); Assert.Equal(230, stroke[1].YPt, 0.01);
    }

    [Fact] // mesma translação por delta pra Line/Arrow (LineStartPt/LineEndPt) — espelho exato do teste
    // de Ink acima.
    public async Task MoveSelectedAnnotationAsync_LineTool_TranslatesEndpointsByDelta()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        var original = new AnnotationData
        {
            Id = "linha-1", Kind = AnnotationKind.Line, PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30,
            LineStartPt = new PdfPoint(10, 10), LineEndPt = new PdfPoint(30, 30),
        };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);

        await d.MoveSelectedAnnotationAsync(60, 70); // delta = (+50, +60)

        Assert.Empty(errors);
        var lifted = fake.LastAnnotation!;
        Assert.Equal(60, lifted.LineStartPt!.Value.XPt, 0.01); Assert.Equal(70, lifted.LineStartPt.Value.YPt, 0.01);
        Assert.Equal(80, lifted.LineEndPt!.Value.XPt, 0.01); Assert.Equal(90, lifted.LineEndPt.Value.YPt, 0.01);
    }

    [Fact] // THE QUADS BUG (fix batch do coordenador): Quads (Highlight/Underline/Strikeout, Task 6)
    // NUNCA eram traduzidos ao mover — bug PRÉ-EXISTENTE desde a Task 7 (mover ficou genérico pra
    // QUALQUER kind selecionável, incl. os 3 de marcação de texto que já existiam desde a Task 6), só
    // descoberto ao revisar a MESMA classe de bug pros kinds novos do Task 8 (Ink/Line/Arrow, testes
    // acima). Espelho exato de MoveSelectedAnnotationAsync_InkTool_TranslatesInkStrokesByDelta, agora
    // pra Highlight — sem o fix, o texto realmente marcado (via /QuadPoints) ficaria "para trás",
    // descolado do bbox que passou a envolvê-lo.
    public async Task MoveSelectedAnnotationAsync_HighlightTool_TranslatesQuadsByDelta()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        var original = new AnnotationData
        {
            Id = "marca-1", Kind = AnnotationKind.Highlight, PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30,
            Quads = new[] { new PdfQuad(10, 10, 30, 30) },
        };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);

        await d.MoveSelectedAnnotationAsync(60, 70); // delta = (+50, +60)

        Assert.Empty(errors);
        var lifted = fake.LastAnnotation!;
        Assert.Equal(60, lifted.LeftPt, 0.01); Assert.Equal(70, lifted.BottomPt, 0.01);
        Assert.NotNull(lifted.Quads);
        var q = Assert.Single(lifted.Quads!);
        Assert.Equal(60, q.LeftPt, 0.01); Assert.Equal(70, q.BottomPt, 0.01);
        Assert.Equal(80, q.RightPt, 0.01); Assert.Equal(90, q.TopPt, 0.01);
    }

    [Fact] // Underline/Strikeout partilham o MESMO campo Quads de Highlight — 1 kind representativo já
    // basta (mesmo código de TranslateQuads, sem ramificação por kind), mas prova MÚLTIPLOS quads (uma
    // marcação multi-linha real) pra fechar a cobertura que o teste acima (1 quad só) não cobre.
    public async Task MoveSelectedAnnotationAsync_UnderlineTool_TranslatesMultipleQuadsByDelta()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        var original = new AnnotationData
        {
            Id = "sublinhado-1", Kind = AnnotationKind.Underline, PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 100, TopPt = 40,
            Quads = new[] { new PdfQuad(10, 30, 100, 40), new PdfQuad(10, 10, 60, 20) },
        };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);

        await d.MoveSelectedAnnotationAsync(30, 50); // delta = (+20, +40)

        Assert.Empty(errors);
        var lifted = fake.LastAnnotation!;
        Assert.NotNull(lifted.Quads);
        Assert.Equal(2, lifted.Quads!.Count);
        var q1 = lifted.Quads.Single(q => q.RightPt > 90); // o quad(10,30,100,40) original
        var q2 = lifted.Quads.Single(q => q.RightPt < 90); // o quad(10,10,60,20) original
        Assert.Equal(30, q1.LeftPt, 0.01); Assert.Equal(70, q1.BottomPt, 0.01);
        Assert.Equal(120, q1.RightPt, 0.01); Assert.Equal(80, q1.TopPt, 0.01);
        Assert.Equal(30, q2.LeftPt, 0.01); Assert.Equal(50, q2.BottomPt, 0.01);
        Assert.Equal(80, q2.RightPt, 0.01); Assert.Equal(60, q2.TopPt, 0.01);
    }

    // ==== Task 9 (Plano 3a): Carimbos de imagem (galeria) ============================================

    // PNG sintético de tamanho PIXEL conhecido (exemplar: BitmapConverter.ToBitmapSource) — usado pelos
    // testes de escala natural/clamp abaixo, que precisam de dimensões DIFERENTES de 1x1 (a fixture
    // OnePixelPng não serve pra provar a proporção 2:1 do encolhimento a MaxStampWidthPt).
    private static byte[] MakePng(int widthPx, int heightPx)
    {
        var bmp = BitmapSource.Create(widthPx, heightPx, 96, 96, PixelFormats.Bgra32, null,
            new byte[widthPx * heightPx * 4], widthPx * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    [Fact] // ToggleStampTool liga a ferramenta com os bytes recebidos (exemplar: ToggleStickyNoteTool).
    public void ToggleStampTool_ActivatesImageStampTool()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        Assert.Equal(AnnotationTool.None, d.ActiveTool);

        d.ToggleStampTool(Fixtures.OnePixelPng());

        Assert.Equal(AnnotationTool.ImageStamp, d.ActiveTool);
    }

    [Fact] // clicar no MESMO carimbo (mesmo CONTEÚDO de bytes, não a mesma referência) já ativo DESLIGA
    // — mesma semântica de "clicar de novo desliga" dos Toggle*Tool.
    public void ToggleStampTool_SameStampContentAgain_TogglesOff()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ToggleStampTool(Fixtures.OnePixelPng());
        Assert.Equal(AnnotationTool.ImageStamp, d.ActiveTool);

        d.ToggleStampTool(Fixtures.OnePixelPng()); // outra chamada = outra REFERÊNCIA, mesmo conteúdo

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    [Fact] // documento assinado (CanEdit=false) -> ToggleStampTool não ativa nada.
    public void ToggleStampTool_SignedDocument_DoesNotActivate()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        d.IsSignedDocument = true;

        d.ToggleStampTool(Fixtures.OnePixelPng());

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    [Fact] // caminho feliz: imagem PEQUENA (bem abaixo de MaxStampWidthPt) fica no tamanho NATURAL
    // (1px = 1pt, DECISÃO v1) — bbox 40x20pt no ponto clicado (canto inferior-esquerdo), ImageBytes =
    // os MESMOS bytes da galeria, Author do AppConfig, one-shot desativa a ferramenta.
    public async Task PlaceStampAtAsync_SmallImage_UsesNaturalSize_AddsAnnotation_AppliesEdit_DeactivatesTool()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        byte[] png = MakePng(40, 20);
        d.ToggleStampTool(png);

        await d.PlaceStampAtAsync(0, 100, 700);

        Assert.Empty(errors);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.ImageStamp, sent.Kind);
        Assert.Equal(0, sent.PageIndex);
        Assert.Equal(100, sent.LeftPt, 0.01); Assert.Equal(700, sent.BottomPt, 0.01);
        Assert.Equal(140, sent.RightPt, 0.01); Assert.Equal(720, sent.TopPt, 0.01); // 40x20pt natural
        Assert.Same(png, sent.ImageBytes);
        Assert.Equal("Autor de Teste", sent.Author);
        Assert.Equal(30, d.Pages.Count); // ApplyEdit trocou Snapshot pro marcador do fake (30 páginas)
        Assert.Equal(AnnotationTool.None, d.ActiveTool); // one-shot: desativa sozinha
    }

    [Fact] // imagem MAIOR que MaxStampWidthPt (300x150px, proporção 2:1) -> largura CLAMPADA a 150pt,
    // altura escalada preservando a proporção (75pt) — prova que a PROPORÇÃO é preservada, não só a
    // largura.
    public async Task PlaceStampAtAsync_LargeImage_ScalesDownToMaxWidthPt_PreservingAspectRatio()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ToggleStampTool(MakePng(300, 150));

        await d.PlaceStampAtAsync(0, 50, 50);

        var sent = fake.LastAnnotation!;
        Assert.Equal(150, sent.RightPt - sent.LeftPt, 0.5); // largura clampada a 150pt
        Assert.Equal(75, sent.TopPt - sent.BottomPt, 0.5);  // altura escalada na MESMA proporção 2:1
    }

    [Fact] // clique perto da borda -> bbox CLAMPADO pra dentro da página (mesmo ClampToPage de
    // PlaceAnnotationAtAsync), tamanho preservado.
    public async Task PlaceStampAtAsync_NearPageEdge_ClampsRectToPageBounds()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ToggleStampTool(MakePng(300, 150)); // 150x75pt depois de escalado

        await d.PlaceStampAtAsync(0, 590, 5); // canto inferior-direito da página (fixture-a4: 595x842pt)

        var sent = fake.LastAnnotation!;
        Assert.True(sent.RightPt <= 595.01, $"RightPt {sent.RightPt} extrapolou a largura da página");
        Assert.True(sent.LeftPt >= -0.01, $"LeftPt {sent.LeftPt} negativo");
        Assert.True(sent.BottomPt >= -0.01, $"BottomPt {sent.BottomPt} negativo");
        Assert.Equal(150, sent.RightPt - sent.LeftPt, 0.5); // tamanho preservado
        Assert.Equal(75, sent.TopPt - sent.BottomPt, 0.5);
    }

    [Fact] // sem ferramenta ativa: no-op (defesa — a UI real só chama isto com ImageStamp ativo).
    public async Task PlaceStampAtAsync_NoActiveTool_DoesNothing()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;

        await d.PlaceStampAtAsync(0, 100, 700);

        Assert.Equal(0, fake.AddAnnotationCallCount);
    }

    [Fact]
    public async Task PlaceStampAtAsync_PageIndexOutOfRange_DoesNothing()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ToggleStampTool(Fixtures.OnePixelPng());

        await d.PlaceStampAtAsync(99, 100, 700);

        Assert.Equal(0, fake.AddAnnotationCallCount);
    }

    [Fact] // MUTATION-PROOF (exemplar: PlaceAnnotationAtAsync_Success_DeactivatesToolAfterPlacement_MutationProof).
    public async Task PlaceStampAtAsync_Success_DeactivatesToolAfterPlacement_MutationProof()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ToggleStampTool(Fixtures.OnePixelPng());

        await d.PlaceStampAtAsync(0, 100, 700);

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
    }

    // ---- ImageStamp: NÃO liftável (DECISÃO v1) — mover é no-op, excluir continua funcionando --------

    [Fact] // DECISÃO DE DESIGN (brief): ImageStamp não é liftável — MoveSelectedAnnotationAsync não
    // chama Remove/Add nenhum, e o overlay volta pra posição REAL (mesma restauração já provada pro
    // caso "documento assinado", exemplar MoveSelectedAnnotationAsync_SignedDocument_ResetsOverlayToActualPosition).
    public async Task MoveSelectedAnnotationAsync_ImageStampKind_DoesNotLift_RestoresOverlayToActualPosition()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        var original = new AnnotationData { Id = "carimbo-1", Kind = AnnotationKind.ImageStamp, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { original };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.NotNull(d.SelectedAnnotation);
        var expected = PageViewModel.PointRectToScreenRect(10, 10, 30, 30, d.Zoom, d.Pages[0].HeightPt);

        // simula o preview de arrasto que a View já aplicou (Page_MouseMove) — posição DIFERENTE da real
        d.Pages[0].AnnotationSelectionRect = new Rect(999, 999, 20, 20);

        await d.MoveSelectedAnnotationAsync(200, 300);

        Assert.Empty(errors);
        Assert.Equal(0, fake.RemoveAnnotationCallCount); // nunca tentou o lift (Remove+Add)
        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Equal(expected, d.Pages[0].AnnotationSelectionRect); // overlay voltou pra posição REAL
    }

    [Fact] // Excluir continua funcionando pra ImageStamp — RemoveAnnotation só apaga por Id, nunca
    // precisa reler a imagem (exemplar: DeleteSelectedAnnotationCommand_RemovesViaApplyEdit_WithRightId).
    public async Task DeleteSelectedAnnotationCommand_ImageStampKind_RemovesViaApplyEdit()
    {
        var (doc, fake, _, errors) = BuildForAnnotations();
        using var d = doc;
        var target = new AnnotationData { Id = "carimbo-2", Kind = AnnotationKind.ImageStamp, PageIndex = 0, LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30 };
        fake.ReadAnnotationsResult = new[] { target };
        await d.RefreshAnnotationsByPageAsync();
        d.SelectAnnotationAt(0, 20, 20);
        Assert.True(d.DeleteSelectedAnnotationCommand.CanExecute(null));

        await d.DeleteSelectedAnnotationCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal(1, fake.RemoveAnnotationCallCount);
        Assert.Equal("carimbo-2", fake.LastRemovedId);
        Assert.Equal(30, d.Pages.Count); // ApplyEdit trocou Snapshot pro marcador do fake
    }

    // ==== Task 3 (Plano 7): "🖼 Imagem" — click-to-place a partir de OpenFileDialog (não-galeria) ======
    //
    // Exemplar EXATO: o mecanismo de carimbo de imagem da galeria (Task 9, Plano 3a) —
    // ToggleStampTool/PlaceStampAtAsync, REUSADOS sem alteração de contrato: ToggleImageTool só decide
    // OS BYTES (escolhidos via diálogo, validados, normalizados) e então ativa o MESMO ActiveTool.
    // ImageStamp com os MESMOS bytes pendentes — PlaceStampAtAsync (já testado acima) comita do mesmo
    // jeito, seja a origem a galeria ou o diálogo. Única diferença de COMPORTAMENTO: validação (magic-
    // bytes + teto de pixels) ANTES do modo de colocação (a galeria não valida nada na ativação — só na
    // decodificação em PlaceStampAtAsync) e o tamanho default MAIOR (MaxPickedImageWidthPt=200pt vs
    // MaxStampWidthPt=150pt da galeria).

    [Fact] // cancelado -> ActiveTool nunca muda (brief: "cancel pick -> no mode").
    public void ToggleImageTool_PickCancelled_NoModeChange()
    {
        var dialogs = new FakePickImageDialog(null);
        var (doc, fake, _, errors) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;

        d.ToggleImageToolCommand.Execute(null);

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
        Assert.Equal(1, dialogs.PickImageToImportCallCount);
        Assert.Empty(errors);
        Assert.Equal(0, fake.IsSupportedImageCallCount); // nem chegou a validar — cancelou antes
    }

    [Fact] // magic-bytes recusados -> ActiveTool continua None (recusa ANTES do modo de colocação,
    // brief: "unsupported file refused BEFORE placement mode") — mesma mensagem nomeando os formatos
    // suportados de ImageImport.ConvertToPdf, agora nomeando o arquivo escolhido.
    public void ToggleImageTool_UnsupportedFile_RefusesBeforePlacementMode()
    {
        var path = WriteTempImageFile(new byte[] { 1, 2, 3 }, ".gif");
        var dialogs = new FakePickImageDialog(path);
        var (doc, fake, _, errors) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;
        fake.IsSupportedImageResult = false;

        d.ToggleImageToolCommand.Execute(null);

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
        Assert.Single(errors);
        Assert.Contains(Path.GetFileName(path), errors[0]);
        Assert.Contains("JPG", errors[0]);
        Assert.Contains("PNG", errors[0]);
        Assert.Equal(0, fake.IsWithinImagePixelLimitCallCount); // nunca chegou a checar o teto
    }

    [Fact] // teto de pixels recusado -> ActiveTool continua None, ANTES do modo de colocação (mesmo
    // espírito do teste acima — brief: "apply the same pixel ceiling check").
    public void ToggleImageTool_OversizedImage_RefusesBeforePlacementMode()
    {
        var path = WriteTempImageFile(MakePng(10, 10));
        var dialogs = new FakePickImageDialog(path);
        var (doc, fake, _, errors) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;
        fake.IsWithinImagePixelLimitResult = false;

        d.ToggleImageToolCommand.Execute(null);

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
        Assert.Single(errors);
        Assert.Contains("50", errors[0]);
        Assert.Contains("MP", errors[0]);
    }

    [Fact] // fix pós-revisão (Important): JPEG CMYK recusado ANTES do modo de colocação — mesmo
    // espírito do teto de pixels acima, checado logo depois dele. Mensagem EXATA pedida pela revisão.
    public void ToggleImageTool_CmykJpeg_RefusesBeforePlacementMode()
    {
        var path = WriteTempImageFile(MakePng(10, 10)); // conteúdo real irrelevante — fake decide
        var dialogs = new FakePickImageDialog(path);
        var (doc, fake, _, errors) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;
        fake.IsCmykJpegResult = true;

        d.ToggleImageToolCommand.Execute(null);

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
        Assert.Single(errors);
        Assert.Equal("JPEG CMYK não é suportado. Converta para RGB.", errors[0]);
        Assert.Equal(0, fake.ReadJpegExifOrientationCallCount); // nunca chegou a normalizar EXIF
    }

    [Fact] // controle NEGATIVO: JPEG RGB (IsCmykJpegResult=false, default) NÃO é bloqueado por este
    // gate — prova que o detector é PRECISO (só bloqueia quando o motor de fato reporta CMYK).
    public void ToggleImageTool_RgbJpeg_NotBlockedByCmykGate()
    {
        var path = WriteTempImageFile(MakePng(10, 10));
        var dialogs = new FakePickImageDialog(path);
        var (doc, fake, _, errors) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;

        d.ToggleImageToolCommand.Execute(null);

        Assert.Empty(errors);
        Assert.Equal(AnnotationTool.ImageStamp, d.ActiveTool);
        Assert.Equal(1, fake.IsCmykJpegCallCount);
    }

    [Fact] // caminho feliz: valida, entra em modo de colocação com os bytes LIDOS DO DISCO (sem rotação
    // EXIF — ReadJpegExifOrientationResult default 0, ver FakePdfEditor).
    public void ToggleImageTool_ValidImage_EntersPlacementModeWithFileBytes()
    {
        var bytes = MakePng(40, 20);
        var path = WriteTempImageFile(bytes);
        var dialogs = new FakePickImageDialog(path);
        var (doc, fake, _, errors) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;

        d.ToggleImageToolCommand.Execute(null);

        Assert.Empty(errors);
        Assert.Equal(AnnotationTool.ImageStamp, d.ActiveTool);
        Assert.Equal(1, fake.IsSupportedImageCallCount);
        Assert.Equal(1, fake.IsWithinImagePixelLimitCallCount);
        Assert.Equal(1, fake.ReadJpegExifOrientationCallCount);
    }

    [Fact] // click-to-place comita via o MESMO PlaceStampAtAsync da galeria — Kind/PageIndex/ImageBytes/
    // Author + rect no tamanho NATURAL (40x20px = 40x20pt, bem abaixo dos 2 tetos de largura).
    public async Task ToggleImageTool_ThenPlaceStampAtAsync_AddsAnnotationWithImagePayloadAndRect()
    {
        var bytes = MakePng(40, 20);
        var path = WriteTempImageFile(bytes);
        var dialogs = new FakePickImageDialog(path);
        var (doc, fake, _, errors) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;
        d.ToggleImageToolCommand.Execute(null);

        await d.PlaceStampAtAsync(0, 100, 700);

        Assert.Empty(errors);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        var sent = fake.LastAnnotation!;
        Assert.Equal(AnnotationKind.ImageStamp, sent.Kind);
        Assert.Equal(0, sent.PageIndex);
        Assert.Equal(100, sent.LeftPt, 0.01); Assert.Equal(700, sent.BottomPt, 0.01);
        Assert.Equal(140, sent.RightPt, 0.01); Assert.Equal(720, sent.TopPt, 0.01); // 40x20pt natural
        Assert.Equal(bytes, sent.ImageBytes);
        Assert.Equal("Autor de Teste", sent.Author);
        Assert.Equal(AnnotationTool.None, d.ActiveTool); // one-shot: mesmo contrato de PlaceStampAtAsync
    }

    [Fact] // brief: default ~200pt de largura (vs 150pt da galeria, Task 9/Plano 3a, inalterado) —
    // imagem 400x200px (2:1) escolhida via "🖼 Imagem" clampa a 200pt de largura / 100pt de altura;
    // a MESMA imagem via ToggleStampTool (galeria) clamparia a 150pt/75pt (teste separado abaixo).
    public async Task PlaceStampAtAsync_ViaPickedImageTool_ScalesToMaxPickedImageWidthPt()
    {
        var bytes = MakePng(400, 200);
        var path = WriteTempImageFile(bytes);
        var dialogs = new FakePickImageDialog(path);
        var (doc, fake, _, _) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;
        d.ToggleImageToolCommand.Execute(null);

        await d.PlaceStampAtAsync(0, 50, 50);

        var sent = fake.LastAnnotation!;
        Assert.Equal(200, sent.RightPt - sent.LeftPt, 0.5); // largura clampada a 200pt (não 150pt)
        Assert.Equal(100, sent.TopPt - sent.BottomPt, 0.5); // altura na MESMA proporção 2:1
    }

    [Fact] // INVARIANTE PIN (Task 9, Plano 3a inalterado): a MESMA imagem 400x200px via ToggleStampTool
    // (galeria) continua clampando a 150pt/75pt — prova que o teto maior de "🖼 Imagem" não vazou pro
    // caminho da galeria (2 caps distintos, cada um só aplicado à sua própria origem).
    public async Task PlaceStampAtAsync_ViaGalleryStampTool_StillScalesToMaxStampWidthPt()
    {
        var (doc, fake, _, _) = BuildForAnnotations();
        using var d = doc;
        d.ToggleStampTool(MakePng(400, 200));

        await d.PlaceStampAtAsync(0, 50, 50);

        var sent = fake.LastAnnotation!;
        Assert.Equal(150, sent.RightPt - sent.LeftPt, 0.5);
        Assert.Equal(75, sent.TopPt - sent.BottomPt, 0.5);
    }

    [Fact] // costura de rotação (exemplar: PlaceStampAtAsync_PageRotated_NotifiesAndDoesNotAddAnnotation)
    // — página girada recusa com o MESMO aviso pt-BR, ferramenta continua ativa (usuário tenta outra
    // página), nenhuma anotação adicionada.
    public async Task PlaceStampAtAsync_ViaPickedImageTool_PageRotated_NotifiesAndDoesNotAddAnnotation()
    {
        var path = WriteTempImageFile(MakePng(10, 10));
        var dialogs = new FakePickImageDialog(path);
        var (doc, fake, _, errors) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 90 };
        await d.RefreshAnnotationsByPageAsync(); // popula o cache de rotação ANTES da ferramenta ativar
        d.ToggleImageToolCommand.Execute(null);

        await d.PlaceStampAtAsync(0, 100, 100);

        Assert.Equal(0, fake.AddAnnotationCallCount);
        Assert.Contains(errors, e => e.Contains("girada"));
        Assert.Equal(AnnotationTool.ImageStamp, d.ActiveTool); // continua ativa — mesmo contrato do exemplar
    }

    [Fact] // mesmo gate CanEdit de todo Toggle*ToolCommand (exemplar: ToolCommands_CanExecute_FalseWhenSignedDocument).
    public void ToggleImageToolCommand_CanExecute_FalseWhenSignedDocument()
    {
        var (doc, _, _, _) = BuildForAnnotations();
        using var d = doc;
        Assert.True(d.ToggleImageToolCommand.CanExecute(null));

        d.IsSignedDocument = true;

        Assert.False(d.ToggleImageToolCommand.CanExecute(null));
    }

    [Fact] // "clicar de novo desliga" (mesma semântica dos outros Toggle*Tool) — clicar com ImageStamp
    // já ativo (de QUALQUER origem, galeria ou "🖼 Imagem") desliga sem reabrir o diálogo.
    public void ToggleImageTool_AlreadyActive_TogglesOffWithoutReopeningDialog()
    {
        var dialogs = new FakePickImageDialog(null);
        var (doc, _, _, _) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;
        d.ToggleStampTool(Fixtures.OnePixelPng()); // ativa via galeria, não via diálogo
        Assert.Equal(AnnotationTool.ImageStamp, d.ActiveTool);

        d.ToggleImageToolCommand.Execute(null);

        Assert.Equal(AnnotationTool.None, d.ActiveTool);
        Assert.Equal(0, dialogs.PickImageToImportCallCount); // nunca abriu o diálogo — só desligou
    }

    [Fact] // EXIF (brief: "does the stamp path honor EXIF?" — NÃO por padrão; ToggleImageTool corrige
    // ANTES do modo de colocação). Simula uma foto "precisando de correção" via
    // ReadJpegExifOrientationResult=90 (o fake não lê EXIF de verdade — a leitura real é provada pelos
    // testes de PdfEditorTests; este teste prova que o VM AGE sobre o ângulo devolvido): os bytes que
    // chegam em AddAnnotation são DIFERENTES dos bytes crus do arquivo (WPF girou e reencodificou) —
    // a prova visual (upright de verdade) é o teste de px com o motor REAL, ver PdfEditorImageToolTests.
    public async Task ToggleImageTool_ExifRotationReported_PlacedBytesDifferFromRawFileBytes()
    {
        var rawBytes = MakePng(40, 20);
        var path = WriteTempImageFile(rawBytes);
        var dialogs = new FakePickImageDialog(path);
        var (doc, fake, _, errors) = BuildForAnnotations(dialogs: dialogs);
        using var d = doc;
        fake.ReadJpegExifOrientationResult = 90;
        d.ToggleImageToolCommand.Execute(null);
        Assert.Equal(AnnotationTool.ImageStamp, d.ActiveTool); // sanity: normalização não falhou

        await d.PlaceStampAtAsync(0, 100, 700);

        Assert.Empty(errors);
        Assert.Equal(1, fake.AddAnnotationCallCount);
        Assert.NotEqual(rawBytes, fake.LastAnnotation!.ImageBytes); // reencodificado pelo WPF, não os bytes crus do PNG
    }

    // ==== Task 3 (Plano 3b): Organizador de páginas — toggle IsOrganizerOpen ==========================

    [Fact]
    public void IsOrganizerOpen_True_CreatesOrganizerWithPages()
    {
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        Assert.Null(d.Organizer);

        d.IsOrganizerOpen = true;

        Assert.NotNull(d.Organizer);
        Assert.Equal(30, d.Organizer!.Pages.Count);
    }

    [Fact] // M3 rider (revisão Opus) — comentário original SUPERAFIRMAVA o que este teste prova
    // ("comandos não reagem mais depois de Dispose" nunca foi exercitado aqui). O que ESTE teste de
    // fato garante: a PROPRIEDADE pública `Organizer` volta a `null` quando o toggle desliga (o
    // contrato que a View consome pra parar de renderizar `PageOrganizerView` — ver `MainWindow.xaml`,
    // `Visibility="{Binding IsOrganizerOpen}"`). `organizerBeforeClose` é só um sanity check (havia
    // mesmo um organizador não-nulo pra descartar) — não prova nada sobre o comportamento PÓS-Dispose.
    public void IsOrganizerOpen_False_DisposesOrganizerAndClearsProperty()
    {
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        d.IsOrganizerOpen = true;
        var organizerBeforeClose = d.Organizer;

        d.IsOrganizerOpen = false;

        Assert.Null(d.Organizer); // contrato público que a View consome
        Assert.NotNull(organizerBeforeClose); // sanity: havia mesmo algo pra descartar
    }

    [Fact] // "sair do organizador volta pra página EQUIVALENTE" (brief) — a PRIMEIRA selecionada.
    public void IsOrganizerOpen_False_WithSelection_ReturnsToFirstSelectedPage()
    {
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        int? scrolledTo = null;
        d.ScrollToPageRequested += idx => scrolledTo = idx;
        d.IsOrganizerOpen = true;
        d.Organizer!.ToggleSelect(14, ctrl: false); // índice 14 = página 15

        d.IsOrganizerOpen = false;

        Assert.Equal(15, d.CurrentPage);
        Assert.Equal(14, scrolledTo);
    }

    [Fact] // sem seleção nenhuma no organizador ao fechar -> mantém a página que já estava CORRENTE.
    public void IsOrganizerOpen_False_NoSelection_ReturnsToPreviousCurrentPage()
    {
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        d.CurrentPage = 7;
        d.IsOrganizerOpen = true;

        d.IsOrganizerOpen = false;

        Assert.Equal(7, d.CurrentPage);
    }

    [Fact] // fechar a ABA (Dispose) com o organizador ainda aberto não pode lançar nem vazar o renderer
    // dedicado do organizador — mesma disciplina de Dispose_DisposesThumbnailRendererToo.
    public void Dispose_WhileOrganizerOpen_DisposesWithoutThrowing()
    {
        var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        d.IsOrganizerOpen = true;

        var ex = Record.Exception(() => d.Dispose());

        Assert.Null(ex);
    }

    // ==== Deferência (Task 2, Plano 5): FitWidthRecalcRequested — viewport obsoleto pós-organizador ====
    //
    // Ver doc XML de `DocumentViewModel.FitWidthRecalcRequested` pro cenário completo (organizador
    // Collapsed congela `PdfViewerControl.ViewportWidth`, um resize de janela com o organizador aberto
    // deixa um "Ajustar à largura" anterior desalinhado quando o leitor volta). Testável NESTE nível
    // (sem STA/WPF real) porque a DECISÃO de disparar — "o ÚLTIMO ajuste de zoom foi FitWidth?" — é
    // pura lógica de VM; só o CONSUMO do evento (medir `ViewportWidth` de verdade e chamar `FitWidth`
    // de volta) precisa de uma View real, e esse fiapo é só 2 linhas espelhando `ScrollToPage`/
    // `FocusSearchBar` já existentes (sem teste próprio também, mesmo precedente).

    [Fact] // último ajuste foi "Ajustar à largura" -> fechar o organizador dispara o pedido de recálculo
    public void FitWidthRecalcRequested_FiresOnOrganizerClose_AfterFitWidth()
    {
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        int fired = 0;
        d.FitWidthRecalcRequested += () => fired++;
        d.FitWidth(800);
        d.IsOrganizerOpen = true;

        d.IsOrganizerOpen = false;

        Assert.Equal(1, fired);
    }

    [Fact] // nunca chamou FitWidth (estado inicial) -> fechar o organizador NÃO dispara nada
    public void FitWidthRecalcRequested_DoesNotFireOnOrganizerClose_WhenFitWidthNeverCalled()
    {
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        int fired = 0;
        d.FitWidthRecalcRequested += () => fired++;
        d.IsOrganizerOpen = true;

        d.IsOrganizerOpen = false;

        Assert.Equal(0, fired);
    }

    [Fact] // FitWidth seguido de um zoom MANUAL (+) -> a escolha explícita do usuário NUNCA é
    // sobrescrita ao fechar o organizador (ver doc XML: "nunca sobrescreve um zoom manual").
    public void FitWidthRecalcRequested_DoesNotFireOnOrganizerClose_AfterZoomIn()
    {
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        int fired = 0;
        d.FitWidthRecalcRequested += () => fired++;
        d.FitWidth(800);
        d.ZoomInCommand.Execute(null);
        d.IsOrganizerOpen = true;

        d.IsOrganizerOpen = false;

        Assert.Equal(0, fired);
    }

    [Fact] // espelho do teste acima pro zoom (−)
    public void FitWidthRecalcRequested_DoesNotFireOnOrganizerClose_AfterZoomOut()
    {
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        int fired = 0;
        d.FitWidthRecalcRequested += () => fired++;
        d.FitWidth(800);
        d.ZoomOutCommand.Execute(null);
        d.IsOrganizerOpen = true;

        d.IsOrganizerOpen = false;

        Assert.Equal(0, fired);
    }

    [Fact] // FitWidth seguido de "Página inteira" -> mesma disciplina (o outro modo de ajuste também
    // é uma escolha explícita distinta de "Ajustar à largura", não deve ser sobrescrito).
    public void FitWidthRecalcRequested_DoesNotFireOnOrganizerClose_AfterFitPage()
    {
        using var d = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        int fired = 0;
        d.FitWidthRecalcRequested += () => fired++;
        d.FitWidth(800);
        d.FitPage(800, 600);
        d.IsOrganizerOpen = true;

        d.IsOrganizerOpen = false;

        Assert.Equal(0, fired);
    }

    // ==== Task 1 (Plano 5): aviso de escala do organizador (>200 páginas) ============================
    //
    // Ledger: 14,7 s pra encher a grade a 510 páginas -- >200 páginas pede confirmação ANTES de entrar
    // no modo organizador (seam UiPrompts, 4 arquivos: IConfirmOrganizerScaleService.cs, UiPrompts.cs,
    // UiPromptsTestGuard.cs, UiPromptsCoverageTests.cs — provas de disparo/coverage em
    // UiPromptsGuardTests.cs). Fronteira EXATA do brief: 200 não avisa, 201 avisa.
    //
    // Fixtures de 200/201 páginas construídas em RUNTIME (motor REAL, PdfEditorFactory.Create()) sobre
    // cópias de fixture-a4.pdf via MergeDocuments/ExtractPages -- NUNCA versionamos um fixture de
    // 200+ páginas no repo só pra isto. `Lazy<>` + campo STATIC: computado 1 única vez pra toda a
    // classe de teste (a 1ª [Fact] desta seção que rodar paga o custo; as demais reusam os bytes já
    // prontos), nunca uma alocação sintética gigante (~200 páginas de ~1 KB cada, não "525 MB").
    private static readonly Lazy<byte[]> Doc201Pages = new(() =>
        PdfEditorFactory.Create().MergeDocuments(Enumerable.Repeat(Fixtures.A4(), 201).ToArray()));
    private static readonly Lazy<byte[]> Doc200Pages = new(() =>
        PdfEditorFactory.Create().ExtractPages(Doc201Pages.Value, Enumerable.Range(0, 200).ToArray()));

    private static string WriteTempPdf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mpdf-organizer-scale-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact] // FRONTEIRA: exatamente 200 páginas NÃO pede confirmação -- abre direto, mesmo comportamento
    // pré-Task-1 (o confirm sequer é consultado).
    public void IsOrganizerOpen_True_At200Pages_NeverPrompts_OpensDirectly()
    {
        var confirm = new FakeConfirmOrganizerScaleService(result: true);
        var path = WriteTempPdf(Doc200Pages.Value);
        try
        {
            using var d = new DocumentViewModel(DocumentSession.Open(path), confirmOrganizerScale: confirm);

            d.IsOrganizerOpen = true;

            Assert.Equal(0, confirm.CallCount); // nunca consultado
            Assert.NotNull(d.Organizer);
            Assert.Equal(200, d.Organizer!.Pages.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact] // FRONTEIRA: 201 páginas (1 a mais) PEDE confirmação -- texto exato do brief, com N = 201.
    public void IsOrganizerOpen_True_At201Pages_PromptsWithExactMessage()
    {
        var confirm = new FakeConfirmOrganizerScaleService(result: true);
        var path = WriteTempPdf(Doc201Pages.Value);
        try
        {
            using var d = new DocumentViewModel(DocumentSession.Open(path), confirmOrganizerScale: confirm);

            d.IsOrganizerOpen = true;

            Assert.Equal(1, confirm.CallCount);
            Assert.Equal(
                "Este documento tem 201 páginas; o organizador pode levar alguns segundos para carregar. Continuar?",
                confirm.LastMessage);
            Assert.NotNull(d.Organizer); // aceito -> abre
            Assert.Equal(201, d.Organizer!.Pages.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact] // recusa (brief: "recusa mantém o leitor") -- Organizer NUNCA chega a ser criado (não é
    // criado-e-descartado; o gate corre ANTES de OpenOrganizer) e o toggle volta pra false sozinho
    // (contrato público que a View consome via TwoWay binding, mesmo binding do teste
    // IsOrganizerOpen_False_DisposesOrganizerAndClearsProperty acima).
    public void IsOrganizerOpen_True_At201Pages_Declined_KeepsReader_OrganizerStaysNull()
    {
        var confirm = new FakeConfirmOrganizerScaleService(result: false);
        var path = WriteTempPdf(Doc201Pages.Value);
        try
        {
            using var d = new DocumentViewModel(DocumentSession.Open(path), confirmOrganizerScale: confirm);

            d.IsOrganizerOpen = true;

            Assert.Equal(1, confirm.CallCount);
            Assert.Null(d.Organizer); // nunca chegou a abrir
            Assert.False(d.IsOrganizerOpen); // toggle reverteu sozinho
        }
        finally { File.Delete(path); }
    }

    // (confirmOrganizerScale OMITIDO -> alcança UiPrompts.CreateConfirmOrganizerScale -- prova de
    // disparo em UiPromptsGuardTests.cs, mesmo precedente de onde as provas "Omitido -> ThrowsViaUiPrompts"
    // de confirmFlatten/confirmSaveBeforeSign/etc. já vivem, não aqui.)

    // ==== Sumário (Task 5, Plano 3b) ================================================================
    //
    // Exemplar: testes de `GetPageRotations`/`AnnotationsByPage` acima — `RefreshOutlineAsync` é
    // chamado EXPLICITAMENTE (`await d.RefreshOutlineAsync()`), nunca esperando o `_dispatcher.
    // BeginInvoke` fire-and-forget disparar sozinho (xUnit puro não bombeia a fila do Dispatcher —
    // mesmo motivo documentado no campo `_dispatcher` de DocumentViewModel).

    [Fact] // sem NENHUM refresh ainda (construtor não bombeia o Dispatcher em teste puro): Outline
    // vazio, HasOutline false — o mesmo estado "ainda carregando" que a UI mostra por uma fração de
    // segundo em produção (ver doc XML de HasOutline).
    public void HasOutline_BeforeAnyRefresh_IsFalse()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;

        Assert.False(d.HasOutline);
        Assert.Empty(d.Outline);
    }

    [Fact] // árvore exposta tal como o editor devolveu — títulos, aninhamento e PageIndex preservados
    // (a árvore em si não é reprocessada pelo VM, só repassada).
    public async Task Outline_AfterRefresh_ExposesTreeFromEditor()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        var filho = new OutlineNode("Seção 1.1", 1, Array.Empty<OutlineNode>());
        var raiz = new OutlineNode("Capítulo 1", 0, new[] { filho });
        fake.ReadOutlineResult = new[] { raiz };

        await d.RefreshOutlineAsync();

        Assert.Single(d.Outline);
        Assert.Equal("Capítulo 1", d.Outline[0].Title);
        Assert.Equal(0, d.Outline[0].PageIndex);
        Assert.Single(d.Outline[0].Children);
        Assert.Equal("Seção 1.1", d.Outline[0].Children[0].Title);
        Assert.Equal(1, d.Outline[0].Children[0].PageIndex);
    }

    [Fact] // brief: "empty outline -> HasOutline false (empty-state binding)" — resultado VAZIO
    // explícito do editor (documento sem /Outlines), não só o default pré-refresh do teste acima.
    public async Task HasOutline_EmptyEditorResult_IsFalse()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadOutlineResult = Array.Empty<OutlineNode>();

        await d.RefreshOutlineAsync();

        Assert.False(d.HasOutline);
    }

    [Fact]
    public async Task HasOutline_NonEmptyEditorResult_IsTrue()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadOutlineResult = new[] { new OutlineNode("Capítulo 1", 0, Array.Empty<OutlineNode>()) };

        await d.RefreshOutlineAsync();

        Assert.True(d.HasOutline);
    }

    [Fact] // clique num nó COM página -> ScrollToPageRequested dispara com o ÍNDICE certo (0-based,
    // mesma convenção de busca/restauração de página pós-Apply).
    public void NavigateToOutlineNodeCommand_NodeWithPage_RaisesScrollToPageRequestedWithRightIndex()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        int? scrolledTo = null;
        d.ScrollToPageRequested += idx => scrolledTo = idx;
        var node = new OutlineNode("Capítulo 3", 20, Array.Empty<OutlineNode>());

        d.NavigateToOutlineNodeCommand.Execute(node);

        Assert.Equal(20, scrolledTo);
    }

    [Fact] // I2 (revisão final pré-merge, Plano 3b): o comando não guarda NENHUM estado de "último nó
    // ativado" — 2 execuções seguidas com o MESMO node disparam ScrollToPageRequested 2 VEZES. Esta é a
    // metade "VM" do fix de I2: a metade "View" (OutlineView.xaml adiciona MouseLeftButtonUp na
    // TextBlock do item template, ver OutlineView.xaml.cs) garante que um 2º CLIQUE real no MESMO nó
    // chega até este MESMO comando mesmo quando `TreeView.SelectedItemChanged` não dispara de novo (WPF
    // só dispara em MUDANÇA de seleção — clicar 2x no nó JÁ selecionado é "sem mudança" pro TreeView).
    // Prova ponta-a-ponta da metade View: `ViewerIntegrationTests.MainWindow_SumarioTab_...` (STA).
    public void NavigateToOutlineNodeCommand_SameNodeActivatedTwice_RaisesScrollToPageRequestedTwice()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        var pages = new List<int>();
        d.ScrollToPageRequested += pages.Add;
        var node = new OutlineNode("Capítulo 2", 10, Array.Empty<OutlineNode>());

        d.NavigateToOutlineNodeCommand.Execute(node); // 1º "clique"
        d.NavigateToOutlineNodeCommand.Execute(node); // 2º "clique" no MESMO node

        Assert.Equal(new[] { 10, 10 }, pages);
    }

    [Fact] // nó SEM página (organizacional puro, ex.: "Anexos") -> no-op, ScrollToPageRequested NUNCA
    // dispara (brief: "nodes without page -> not clickable/no-op").
    public void NavigateToOutlineNodeCommand_NodeWithoutPage_NoOp()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        bool raised = false;
        d.ScrollToPageRequested += _ => raised = true;
        var node = new OutlineNode("Anexos", null, Array.Empty<OutlineNode>());

        d.NavigateToOutlineNodeCommand.Execute(node);

        Assert.False(raised);
    }

    [Fact] // node == null (nada selecionado na TreeView) -> no-op, nunca NullReferenceException.
    public void NavigateToOutlineNodeCommand_NullNode_NoOp()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        bool raised = false;
        d.ScrollToPageRequested += _ => raised = true;

        d.NavigateToOutlineNodeCommand.Execute(null);

        Assert.False(raised);
    }

    [Fact] // refresh no Applied (brief item 6/5) — troca o resultado do fake ENTRE dois refreshes,
    // simulando o que o Apply de verdade dispara (fire-and-forget via `_dispatcher`, ver
    // `OnSessionApplied`/doc XML de `Outline`); chamar `RefreshOutlineAsync` de novo depois de
    // `Session.Apply` prova que o método reflete o SNAPSHOT CORRENTE, não um resultado travado no
    // documento antigo — mesmo padrão de `PlaceAnnotationAtAsync_StaleRotationCacheAfterApply_
    // RefreshesBeforeGating` pra rotação.
    public async Task RefreshOutlineAsync_AfterApply_ReflectsNewSnapshot()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadOutlineResult = new[] { new OutlineNode("Original", 0, Array.Empty<OutlineNode>()) };
        await d.RefreshOutlineAsync();
        Assert.Equal("Original", d.Outline[0].Title);

        d.Session.Apply(Fixtures.ThirtyPages()); // troca o snapshot — outline antigo pode ter sumido/mudado
        fake.ReadOutlineResult = new[] { new OutlineNode("Atualizado", 5, Array.Empty<OutlineNode>()) };

        await d.RefreshOutlineAsync();

        Assert.Single(d.Outline);
        Assert.Equal("Atualizado", d.Outline[0].Title);
        Assert.Equal(5, d.Outline[0].PageIndex);
    }

    // ==== Task 2 (Plano 3c): painel de Campos (formulário) ===========================================

    private static FormFieldData TextField(string name = "nome", string value = "Fulano de Tal", int pageIndex = 0, PdfQuad? rect = null) =>
        new(name, FormFieldType.Text, value, Array.Empty<string>(), pageIndex, rect, IsReadOnly: false);

    private static FormFieldData OtherField(string name = "botao") =>
        new(name, FormFieldType.Other, null, Array.Empty<string>(), 0, null, IsReadOnly: false);

    // ---- Cache: SeedFormFieldsCache (carga INICIAL — Obs 17, ver MainViewModel.OpenPath) -------------

    [Fact]
    public void SeedFormFieldsCache_PopulatesEditorsFromRawFields()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;

        d.SeedFormFieldsCache(false, new[] { TextField() });

        Assert.False(d.IsXfaForm);
        Assert.True(d.HasFormFields);
        Assert.Single(d.FormFieldEditors);
        Assert.Equal("nome", d.FormFieldEditors[0].Name);
        Assert.Equal("Fulano de Tal", d.FormFieldEditors[0].EditedValue);
    }

    [Fact] // Task 1 fix (nota de política explícita no relatório): Other (botão/assinatura) é RECUSADO
    // por SetFormFields — a UI nunca pode oferecê-lo como "campo preenchível".
    public void SeedFormFieldsCache_FiltersOutOtherTypeFields()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;

        d.SeedFormFieldsCache(false, new[] { TextField(), OtherField() });

        Assert.Single(d.FormFieldEditors);
        Assert.Equal("nome", d.FormFieldEditors[0].Name);
    }

    [Fact]
    public void SeedFormFieldsCache_XfaTrue_EmptyEditorsRegardlessOfRawFields()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;

        d.SeedFormFieldsCache(true, new[] { TextField() });

        Assert.True(d.IsXfaForm);
        Assert.False(d.HasFormFields);
        Assert.Empty(d.FormFieldEditors);
    }

    [Fact]
    public void HasFormFields_FalseByDefault_BeforeAnySeedOrRefresh()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;

        Assert.False(d.HasFormFields);
        Assert.False(d.IsXfaForm);
    }

    // ---- Cache: RefreshFormFieldsAsync (Applied — read-gate por snapshot, exemplar AnnotationsByPage) --

    [Fact]
    public async Task RefreshFormFieldsAsync_PopulatesFromEditor()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField() };

        await d.RefreshFormFieldsAsync();

        Assert.True(d.HasFormFields);
        Assert.Equal("nome", d.FormFieldEditors[0].Name);
    }

    [Fact]
    public async Task RefreshFormFieldsAsync_ChecksXfaBeforeReadFormFields_NeverCallsReadOnXfaDoc()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.HasXfaResult = true;
        // Se ReadFormFields FOSSE chamado, lançaria — contrato pinado (Task 1 fix): ReadFormFields
        // lança PdfEditingException em documento XFA. O gate precisa impedir a chamada, não capturar a
        // exceção depois.
        fake.ThrowOnReadFormFields = new InvalidOperationException("não deveria ter sido chamado");

        await d.RefreshFormFieldsAsync();

        Assert.Equal(0, fake.ReadFormFieldsCallCount);
        Assert.True(d.IsXfaForm);
        Assert.False(d.HasFormFields);
    }

    [Fact] // refresh no Applied (exemplar RefreshOutlineAsync_AfterApply_ReflectsNewSnapshot acima) —
    // troca o resultado do fake ENTRE dois refreshes, simulando o que Apply de verdade dispara.
    public async Task RefreshFormFieldsAsync_AfterApply_ReflectsNewSnapshot()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Original") };
        await d.RefreshFormFieldsAsync();
        Assert.Equal("Original", d.FormFieldEditors[0].EditedValue);

        d.Session.Apply(Fixtures.ThirtyPages());
        fake.ReadFormFieldsResult = new[] { TextField("outro", "Novo") };

        await d.RefreshFormFieldsAsync();

        Assert.Single(d.FormFieldEditors);
        Assert.Equal("outro", d.FormFieldEditors[0].Name);
    }

    [Fact] // GATE DE LEITURA: leitura falha (mesmo após 1 retry de 500ms) -> cache NÃO avança, fica no
    // que já tinha (não trava a UI com uma exceção, nem finge que atualizou).
    public async Task RefreshFormFieldsAsync_ReadFails_CacheStaysAtPreviousState()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Original") };
        await d.RefreshFormFieldsAsync();

        d.Session.Apply(Fixtures.ThirtyPages());
        fake.ThrowOnReadFormFields = new IOException("falha simulada");

        await d.RefreshFormFieldsAsync(); // ~500ms (1 retry) até desistir

        Assert.Equal("Original", d.FormFieldEditors[0].EditedValue); // cache velho preservado
    }

    // ---- Preservação de edição em curso através de um refresh BEM-SUCEDIDO (Important 1, revisão) ----
    // ACHADO da revisão: um refresh que SUCEDE (diferente do teste acima, que falha) substituía
    // FormFieldEditors por instâncias NOVAS/limpas, apagando SILENCIOSAMENTE qualquer edição em curso do
    // usuário — Aplicar virava um no-op indistinguível de "nada mudou" (`changed.Count == 0`). Fix:
    // RefreshFormFieldsAsync agora RE-APLICA o valor digitado (por NOME) nos campos que sobrevivem
    // editáveis à releitura; um campo dirty que sumiu (ou virou readonly/Other) dispara aviso pt-BR
    // NOMEANDO o campo e descarta só AQUELE valor — os demais campos dirty continuam preservados.

    [Fact]
    public async Task RefreshFormFieldsAsync_PreservesDirtyEditAcrossSuccessfulRefresh()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Original") };
        await d.RefreshFormFieldsAsync();
        d.FormFieldEditors[0].EditedValue = "Digitado pelo usuário"; // dirty ANTES da edição alheia

        // Edição alheia em outro lugar (organizador, outra aba) — o cache do painel não sabia disso,
        // não que o VALOR do campo mudou de verdade (mesmo campo/valor na leitura NOVA).
        d.Session.Apply(Fixtures.ThirtyPages());
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Original") };

        await d.RefreshFormFieldsAsync();

        Assert.Equal("Digitado pelo usuário", d.FormFieldEditors[0].EditedValue); // sobreviveu
        Assert.True(d.FormFieldEditors[0].IsDirty);
        Assert.Empty(errors); // nada perdido, nenhum aviso

        // Aplicar de verdade usa o valor PRESERVADO — prova que não é só um campo "com aparência
        // dirty", o dado realmente chega no dicionário enviado ao motor.
        await d.ApplyFormValuesCommand.ExecuteAsync(null);
        Assert.Equal(1, fake.SetFormFieldsCallCount);
        Assert.Equal("Digitado pelo usuário", fake.LastSetFormFieldsValues!["nome"]);
    }

    [Fact] // um valor preservado que, por coincidência, bate com o Data.Value NOVO simplesmente deixa de
    // ser dirty (comportamento correto e automático de FormFieldViewModel.IsDirty — não um caso especial).
    public async Task RefreshFormFieldsAsync_PreservedValueEqualsNewDataValue_NoLongerDirty()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Original") };
        await d.RefreshFormFieldsAsync();
        d.FormFieldEditors[0].EditedValue = "Convergiu";

        d.Session.Apply(Fixtures.ThirtyPages());
        // a edição alheia HAPPENED a mudar o campo pro MESMO valor que o usuário já tinha digitado.
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Convergiu") };

        await d.RefreshFormFieldsAsync();

        Assert.Equal("Convergiu", d.FormFieldEditors[0].EditedValue);
        Assert.False(d.FormFieldEditors[0].IsDirty); // preservado, mas não mais "alterado" (bate com o novo original)
    }

    [Fact] // campo dirty que SUMIU na releitura -> aviso pt-BR NOMEANDO o campo, valor descartado;
    // outro campo dirty que SOBREVIVEU continua preservado (a perda é POR CAMPO, não tudo-ou-nada).
    public async Task RefreshFormFieldsAsync_DirtyFieldDisappears_NotifiesAndDiscardsValue_OtherDirtyFieldsPreserved()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Original"), TextField("cidade", "SP") };
        await d.RefreshFormFieldsAsync();
        d.FormFieldEditors.First(f => f.Name == "nome").EditedValue = "Novo Nome";
        d.FormFieldEditors.First(f => f.Name == "cidade").EditedValue = "RJ";

        d.Session.Apply(Fixtures.ThirtyPages());
        fake.ReadFormFieldsResult = new[] { TextField("cidade", "SP") }; // "nome" SUMIU da leitura nova

        await d.RefreshFormFieldsAsync();

        Assert.Single(errors);
        Assert.Contains("nome", errors[0]);
        Assert.Single(d.FormFieldEditors);
        Assert.Equal("RJ", d.FormFieldEditors[0].EditedValue); // "cidade" sobreviveu preservado
        Assert.True(d.FormFieldEditors[0].IsDirty);
    }

    [Fact] // campo dirty que virou READONLY na releitura — mesma classe de "perdido" que sumir/virar
    // Other (a revisão agrupa os 3 casos): editor desabilitado não pode mais receber o valor digitado.
    public async Task RefreshFormFieldsAsync_DirtyFieldBecomesReadOnly_NotifiesAndDiscardsValue()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Original") };
        await d.RefreshFormFieldsAsync();
        d.FormFieldEditors[0].EditedValue = "Novo Nome";

        d.Session.Apply(Fixtures.ThirtyPages());
        fake.ReadFormFieldsResult = new[]
        {
            new FormFieldData("nome", FormFieldType.Text, "Original", Array.Empty<string>(), 0, null, IsReadOnly: true),
        };

        await d.RefreshFormFieldsAsync();

        Assert.Single(errors);
        Assert.Contains("nome", errors[0]);
        Assert.Equal("Original", d.FormFieldEditors[0].EditedValue); // não preservado — valor de leitura
        Assert.False(d.FormFieldEditors[0].IsDirty);
        Assert.True(d.FormFieldEditors[0].IsReadOnly);
    }

    [Fact] // mesma classe — campo dirty que virou Other (botão/assinatura) desaparece da lista editável
    // por construção (BuildFormFieldEditors filtra Other) — mesmo tratamento de "sumiu".
    public async Task RefreshFormFieldsAsync_DirtyFieldBecomesOther_NotifiesAndDiscardsValue()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Original") };
        await d.RefreshFormFieldsAsync();
        d.FormFieldEditors[0].EditedValue = "Novo Nome";

        d.Session.Apply(Fixtures.ThirtyPages());
        fake.ReadFormFieldsResult = new[] { OtherField("nome") };

        await d.RefreshFormFieldsAsync();

        Assert.Single(errors);
        Assert.Contains("nome", errors[0]);
        Assert.Empty(d.FormFieldEditors); // Other filtrado — lista editável fica vazia
    }

    [Fact] // 2 campos dirty somem juntos -> UM aviso só, nomeando os 2 (não 2 MessageBoxes separadas).
    public async Task RefreshFormFieldsAsync_MultipleDirtyFieldsDisappear_NotifiesNamingAll()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField("nome", "A"), TextField("cidade", "B") };
        await d.RefreshFormFieldsAsync();
        d.FormFieldEditors[0].EditedValue = "X";
        d.FormFieldEditors[1].EditedValue = "Y";

        d.Session.Apply(Fixtures.ThirtyPages());
        fake.ReadFormFieldsResult = Array.Empty<FormFieldData>(); // os 2 sumiram

        await d.RefreshFormFieldsAsync();

        Assert.Single(errors);
        Assert.Contains("nome", errors[0]);
        Assert.Contains("cidade", errors[0]);
    }

    [Fact] // controle negativo: campo NÃO-dirty que some na releitura não gera aviso nenhum — só
    // perdas de edição EM CURSO (dirty) importam; um campo intocado sumir é só "o documento mudou".
    public async Task RefreshFormFieldsAsync_NonDirtyFieldDisappears_NoNotice()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Original") };
        await d.RefreshFormFieldsAsync();
        // NÃO edita — campo fica limpo (não-dirty).

        d.Session.Apply(Fixtures.ThirtyPages());
        fake.ReadFormFieldsResult = Array.Empty<FormFieldData>();

        await d.RefreshFormFieldsAsync();

        Assert.Empty(errors);
        Assert.Empty(d.FormFieldEditors);
    }

    // ---- Aplicar alterações: funil (TryBeginEdit -> SetFormFields -> ApplyEdit), só os ALTERADOS ------

    [Fact]
    public async Task ApplyFormValuesCommand_OnlyDirtyFieldsIncludedInDictionary()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField("nome", "Fulano"), TextField("cidade", "SP") });
        d.FormFieldEditors[0].EditedValue = "Novo Nome"; // só este mudou

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal(1, fake.SetFormFieldsCallCount);
        var sent = Assert.Single(fake.LastSetFormFieldsValues!);
        Assert.Equal("nome", sent.Key);
        Assert.Equal("Novo Nome", sent.Value);
    }

    [Fact]
    public async Task ApplyFormValuesCommand_NoChanges_DoesNotCallSetFormFields()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.SetFormFieldsCallCount);
        Assert.Empty(errors);
        Assert.False(d.Session.IsEditInFlight); // pino solto mesmo no caminho no-op
    }

    [Fact]
    public async Task ApplyFormValuesCommand_Success_AppliesEditAndAllowsUndo()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField("nome", "Fulano") });
        d.FormFieldEditors[0].EditedValue = "Novo Nome";
        fake.SetFormFieldsResult = Fixtures.ThirtyPages();
        var before = d.Session.Snapshot;

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.NotSame(before, d.Session.Snapshot);
        Assert.True(d.Session.CanUndo);

        // undo desfaz o preenchimento (brief) — refresca o cache pra provar que o valor RESTAURADO
        // aparece de verdade, não só que Session.Snapshot voltou (mesma disciplina de re-provar "funciona
        // de verdade" já usada em EditInFlightMatrixTests).
        fake.ReadFormFieldsResult = new[] { TextField("nome", "Fulano") }; // estado ORIGINAL, pré-edição
        d.Session.Undo();
        Assert.Same(before, d.Session.Snapshot);
        await d.RefreshFormFieldsAsync();
        Assert.Equal("Fulano", d.FormFieldEditors[0].EditedValue);
    }

    [Fact]
    public async Task ApplyFormValuesCommand_SignedDocument_NotifiesAndDoesNotApply()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        d.FormFieldEditors[0].EditedValue = "Novo Nome";
        fake.ThrowOnSetFormFields = new PdfSignedDocumentException("assinado");

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.Contains(errors, e => e.Contains("assinada", StringComparison.OrdinalIgnoreCase) || e.Contains("assinado", StringComparison.OrdinalIgnoreCase));
        Assert.False(d.Session.IsDirty);
    }

    [Fact] // GATE DE LEITURA MANDATÓRIO (diferente de ApplyMarkup): a escrita usa o cache — se a
    // releitura falhar persistentemente (mesmo após o retry), recusa em vez de aplicar dados obsoletos.
    public async Task ApplyFormValuesCommand_CacheNeverConverges_RefusesWithStaleNotice_NeverCallsSetFormFields()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() }); // cache FRESCO
        d.FormFieldEditors[0].EditedValue = "Novo Nome"; // dirty

        // invalida o cache "de fora" (mesmo truque de RefreshFormFieldsAsync_ReadFails_...): o refresh
        // fire-and-forget de um Applied alheio nunca roda de verdade num teste xunit sem Dispatcher.Run().
        d.Session.Apply(Fixtures.ThirtyPages());
        fake.ThrowOnReadFormFields = new IOException("falha simulada");

        await d.ApplyFormValuesCommand.ExecuteAsync(null); // ~500ms (1 retry) até desistir

        Assert.Equal(0, fake.SetFormFieldsCallCount);
        Assert.Single(errors);
        Assert.False(d.Session.IsEditInFlight); // pino solto mesmo na recusa
    }

    [Fact]
    public void CanApplyFormValues_FalseWithoutFields()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;

        Assert.False(d.ApplyFormValuesCommand.CanExecute(null));
    }

    [Fact]
    public void CanApplyFormValues_TrueWithFieldsAndCanEdit()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });

        Assert.True(d.ApplyFormValuesCommand.CanExecute(null));
    }

    [Fact]
    public void CanApplyFormValues_FalseWhenSignedDocument()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });

        d.IsSignedDocument = true;

        Assert.False(d.ApplyFormValuesCommand.CanExecute(null));
    }

    // ---- CanEdit também compõe IsXfaForm (achado real: HasSignatures/GuardAgainstSignedDocument
    // lançam PdfException pra QUALQUER documento com /XFA — não só a checagem de assinatura durante o
    // open, mas TODO mutador que passa por GuardAgainstSignedDocument, AddAnnotation/RotatePages/etc.
    // inclusos. "Formulário XFA... o documento abre para leitura" só é seguro se NENHUM comando
    // mutador tentar chegar no motor num doc XFA — CanEdit é o gate ÚNICO que todos eles já compõem.) --

    [Fact]
    public void CanEdit_FalseWhenXfaForm()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;

        d.IsXfaForm = true;

        Assert.False(d.CanEdit);
    }

    [Fact] // prova que o gate propaga pros comandos de anotação/desenho, não só CanEdit isolado —
    // mesmo espírito de CanApplyMarkup_CanExecute_FalseWhenSignedDocument.
    public void ToggleStickyNoteToolCommand_CanExecute_FalseWhenXfaForm()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        Assert.True(d.ToggleStickyNoteToolCommand.CanExecute(null)); // sanity

        d.IsXfaForm = true;

        Assert.False(d.ToggleStickyNoteToolCommand.CanExecute(null));
    }

    [Fact]
    public void CanApplyFormValues_FalseWhenXfaForm()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        // XFA nunca popula FormFieldEditors (SeedFormFieldsCache/RefreshFormFieldsAsync já garantem
        // isso), mas o teste prova o gate CanEdit em si, independente de HasFormFields — usa
        // reflection-free bypass: seta os campos primeiro (não-XFA), DEPOIS liga IsXfaForm, provando
        // que mesmo com campos presentes o gate recusa.
        d.SeedFormFieldsCache(false, new[] { TextField() });
        Assert.True(d.ApplyFormValuesCommand.CanExecute(null)); // sanity

        d.IsXfaForm = true;

        Assert.False(d.ApplyFormValuesCommand.CanExecute(null));
    }

    // ---- Task 3 (Plano 3c): FlattenFormCommand ("Achatar formulário") -------------------------------
    // Diálogo de confirmação INJETÁVEL (_confirmFlatten) — BuildForMarkup não serve aqui (o default de
    // UiPrompts.CreateConfirmFlatten é a versão que LANÇA, ver UiPromptsTestGuard); helper PRÓPRIO que
    // injeta um FakeConfirmFlattenService controlável.

    private static (DocumentViewModel doc, FakePdfEditor fake, List<string> errors, List<string> infos, FakeConfirmFlattenService confirm)
        BuildForFlatten(bool confirmResult = true)
    {
        var fake = new FakePdfEditor();
        var errors = new List<string>();
        var infos = new List<string>();
        var confirm = new FakeConfirmFlattenService(confirmResult);
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")),
            editor: fake,
            notifyError: errors.Add,
            notifyInfo: infos.Add,
            confirmFlatten: confirm);
        return (doc, fake, errors, infos, confirm);
    }

    [Fact] // contrato do brief: cancelar o diálogo não arma o funil nem chama o motor — no-op completo.
    public async Task FlattenFormCommand_ConfirmDeclined_NoFunnelArmNoEngineCall()
    {
        var (doc, fake, errors, infos, confirm) = BuildForFlatten(confirmResult: false);
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        var before = d.Session.Snapshot;

        await d.FlattenFormCommand.ExecuteAsync(null);

        Assert.Equal(1, confirm.CallCount); // o diálogo FOI consultado
        Assert.Equal(0, fake.FlattenFormCallCount); // mas o motor NUNCA foi alcançado
        Assert.False(d.Session.IsEditInFlight); // funil nunca armou
        Assert.Same(before, d.Session.Snapshot); // nada mudou
        Assert.Empty(errors);
        Assert.Empty(infos);
    }

    [Fact]
    public async Task FlattenFormCommand_ConfirmAccepted_CallsFlattenFormAppliesEditAndNotifiesSuccess()
    {
        var (doc, fake, errors, infos, confirm) = BuildForFlatten(confirmResult: true);
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        fake.FlattenFormResult = Fixtures.ThirtyPages();
        var before = d.Session.Snapshot;

        await d.FlattenFormCommand.ExecuteAsync(null);

        Assert.Equal(1, confirm.CallCount);
        Assert.Equal(1, fake.FlattenFormCallCount);
        Assert.NotSame(before, d.Session.Snapshot); // ApplyEdit de verdade
        Assert.True(d.Session.CanUndo); // entrou no histórico de desfazer
        Assert.Empty(errors);
        Assert.Single(infos); // sucesso notificado em pt-BR
        Assert.Contains("sucesso", infos[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(d.Session.IsEditInFlight); // pino solto
    }

    [Fact]
    public async Task FlattenFormCommand_SignedDocument_NotifiesAndDoesNotApply()
    {
        var (doc, fake, errors, infos, _) = BuildForFlatten(confirmResult: true);
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        fake.ThrowOnFlattenForm = new PdfSignedDocumentException("assinado");

        await d.FlattenFormCommand.ExecuteAsync(null);

        Assert.Contains(errors, e => e.Contains("assinad", StringComparison.OrdinalIgnoreCase));
        Assert.False(d.Session.IsDirty);
        Assert.Empty(infos);
        Assert.False(d.Session.IsEditInFlight); // pino solto mesmo na recusa
    }

    [Fact]
    public void CanFlattenForm_FalseWithoutFields()
    {
        var (doc, _, _, _, _) = BuildForFlatten();
        using var d = doc;

        Assert.False(d.FlattenFormCommand.CanExecute(null));
    }

    [Fact]
    public void CanFlattenForm_TrueWithFieldsAndCanEdit()
    {
        var (doc, _, _, _, _) = BuildForFlatten();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });

        Assert.True(d.FlattenFormCommand.CanExecute(null));
    }

    [Fact]
    public void CanFlattenForm_FalseWhenSignedDocument()
    {
        var (doc, _, _, _, _) = BuildForFlatten();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });

        d.IsSignedDocument = true;

        Assert.False(d.FlattenFormCommand.CanExecute(null));
    }

    [Fact]
    public void CanFlattenForm_FalseWhenXfaForm()
    {
        var (doc, _, _, _, _) = BuildForFlatten();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        Assert.True(d.FlattenFormCommand.CanExecute(null)); // sanity

        d.IsXfaForm = true;

        Assert.False(d.FlattenFormCommand.CanExecute(null));
    }

    [Fact] // Undo (brief): flatten -> Undo -> o cache de campos mostra os campos de NOVO — motor REAL
    // (PdfEditorFactory.Create(), não FakePdfEditor) sobre uma sessão REAL da fixture-formulario.pdf.
    // RefreshFormFieldsAsync chamado EXPLICITAMENTE depois do Undo — o fire-and-forget de
    // OnSessionApplied via _dispatcher.BeginInvoke nunca dispara num teste xunit puro sem
    // Dispatcher.Run() bombeando a fila (mesma disciplina já usada por EditInFlightMatrixTests).
    public async Task FlattenFormCommand_ThenUndo_FieldsCacheShowsFieldsAgain()
    {
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-formulario.pdf"));
        using var d = new DocumentViewModel(session, editor: PdfEditorFactory.Create(),
            notifyError: _ => { }, notifyInfo: _ => { }, confirmFlatten: new FakeConfirmFlattenService(true));
        using (session)
        {
            await d.RefreshFormFieldsAsync();
            Assert.True(d.HasFormFields);
            int fieldCountBefore = d.FormFieldEditors.Count;

            await d.FlattenFormCommand.ExecuteAsync(null);
            await d.RefreshFormFieldsAsync(); // idem — refresh pós-Applied é fire-and-forget, não roda sozinho aqui

            Assert.False(d.HasFormFields);
            Assert.Empty(d.FormFieldEditors);

            d.Session.Undo();
            await d.RefreshFormFieldsAsync();

            Assert.True(d.HasFormFields);
            Assert.Equal(fieldCountBefore, d.FormFieldEditors.Count);
        }
    }

    // ---- Seleção: campo selecionado -> ScrollToPage + destaque do widget (gate de rotação só aqui) ----

    [Fact]
    public void SelectFormFieldCommand_RaisesScrollToPageRequested()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField(pageIndex: 0) });
        int? scrolledTo = null;
        d.ScrollToPageRequested += idx => scrolledTo = idx;

        d.SelectFormFieldCommand.Execute(d.FormFieldEditors[0]);

        Assert.Equal(0, scrolledTo);
        Assert.Same(d.FormFieldEditors[0], d.SelectedFormField);
    }

    [Fact]
    public async Task SelectFormField_PageNotRotated_HighlightsWidget()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 0 };
        await d.RefreshAnnotationsByPageAsync(); // popula _pageRotations (cache compartilhado)
        var rect = new PdfQuad(10, 10, 30, 30);
        d.SeedFormFieldsCache(false, new[] { TextField(pageIndex: 0, rect: rect) });

        d.SelectFormFieldCommand.Execute(d.FormFieldEditors[0]);

        var expected = PageViewModel.PointRectToScreenRect(10, 10, 30, 30, d.Zoom, d.Pages[0].HeightPt);
        Assert.True(d.Pages[0].HasFormFieldHighlight);
        Assert.Equal(expected, d.Pages[0].FormFieldHighlightRect);
    }

    [Fact] // GATE DE ROTAÇÃO (brief: "gate de rotação só no destaque, preencher continua livre") — o
    // destaque NÃO liga numa página girada, mesmo com WidgetRect presente.
    public async Task SelectFormField_PageRotated_DoesNotHighlight()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 90 };
        await d.RefreshAnnotationsByPageAsync();
        var rect = new PdfQuad(10, 10, 30, 30);
        d.SeedFormFieldsCache(false, new[] { TextField(pageIndex: 0, rect: rect) });

        d.SelectFormFieldCommand.Execute(d.FormFieldEditors[0]);

        Assert.False(d.Pages[0].HasFormFieldHighlight);
    }

    [Fact] // preencher continua LIVRE de coordenadas mesmo numa página girada (brief) — Aplicar não
    // consulta rotação nenhuma; só o destaque visual é afetado (teste acima).
    public async Task ApplyFormValuesCommand_PageRotated_StillApplies()
    {
        var (doc, fake, errors) = BuildForMarkup();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 90 };
        await d.RefreshAnnotationsByPageAsync();
        d.SeedFormFieldsCache(false, new[] { TextField(pageIndex: 0) });
        d.FormFieldEditors[0].EditedValue = "Novo Nome";

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.Empty(errors);
        Assert.Equal(1, fake.SetFormFieldsCallCount);
    }

    [Fact] // sem WidgetRect (campo residual sem widget, ver XML doc de FormFieldData) -> sem destaque,
    // mas ScrollToPage ainda dispara (navegação não depende de coordenadas).
    public void SelectFormField_NullWidgetRect_ScrollsButDoesNotHighlight()
    {
        var (doc, _, _) = BuildForMarkup();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField(pageIndex: 0, rect: null) });
        int? scrolledTo = null;
        d.ScrollToPageRequested += idx => scrolledTo = idx;

        d.SelectFormFieldCommand.Execute(d.FormFieldEditors[0]);

        Assert.Equal(0, scrolledTo);
        Assert.False(d.Pages[0].HasFormFieldHighlight);
    }

    [Fact] // trocar de campo selecionado limpa o destaque da página ANTERIOR (exemplar:
    // UpdateAnnotationSelectionOverlay) — evita 2 páginas "grudadas" com destaque simultâneo.
    public async Task SelectFormField_SwitchingField_ClearsPreviousPageHighlight()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 0 };
        await d.RefreshAnnotationsByPageAsync();
        var rect = new PdfQuad(10, 10, 30, 30);
        d.SeedFormFieldsCache(false, new[] { TextField("a", pageIndex: 0, rect: rect), TextField("b", pageIndex: 0, rect: rect) });
        d.SelectFormFieldCommand.Execute(d.FormFieldEditors[0]);
        Assert.True(d.Pages[0].HasFormFieldHighlight);

        d.SelectFormFieldCommand.Execute(null);

        Assert.False(d.Pages[0].HasFormFieldHighlight);
        Assert.Null(d.SelectedFormField);
    }

    [Fact] // Apply (qualquer edição) limpa a seleção de campo — mesmo espírito de SelectedAnnotation em
    // OnSessionApplied (aponta pra algo que pode ter mudado/sumido).
    public async Task SessionApply_ClearsSelectedFormField()
    {
        var (doc, fake, _) = BuildForMarkup();
        using var d = doc;
        fake.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        fake.PageRotationsResult = new[] { 0 };
        await d.RefreshAnnotationsByPageAsync();
        d.SeedFormFieldsCache(false, new[] { TextField(pageIndex: 0, rect: new PdfQuad(10, 10, 30, 30)) });
        d.SelectFormFieldCommand.Execute(d.FormFieldEditors[0]);
        Assert.NotNull(d.SelectedFormField);

        d.Session.Apply(Fixtures.ThirtyPages());

        Assert.Null(d.SelectedFormField);
    }
}
