using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using DocumentFormat.OpenXml.Packaging;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

/// Fake do diálogo "Exportar como Word/Excel" (Task 3, Plano 16): captura o VM já construído pelo
/// comando e (opcionalmente) o DIRIGE até o fim — destino + `StartCommand` — para exercitar o pipeline
/// completo (extração -> exportador -> gravação) a partir de `DocumentViewModel.ExportDocumentCoreAsync`
/// sem abrir janela nenhuma.
internal sealed class FakeExportDocumentDialogService : IExportDocumentDialogService
{
    private readonly string? _drivenDestination;
    private readonly ExportDocumentRange _range;
    private readonly string _rangeText;
    public int ShowCount { get; private set; }
    public ExportDocumentViewModel? LastViewModel { get; private set; }

    /// <param name="drivenDestination">Se != null, o fake seta o destino e RODA a exportação até o fim
    /// (síncrono via GetResult) ao ser mostrado — simula o usuário escolhendo destino e clicando Exportar.
    /// Se null, o fake só registra a chamada (não roda nada) — usado para provar "diálogo mostrado/não
    /// mostrado".</param>
    public FakeExportDocumentDialogService(string? drivenDestination = null,
        ExportDocumentRange range = ExportDocumentRange.AllPages, string rangeText = "")
    {
        _drivenDestination = drivenDestination;
        _range = range;
        _rangeText = rangeText;
    }

    public void ShowExportDocumentDialog(ExportDocumentViewModel viewModel)
    {
        ShowCount++;
        LastViewModel = viewModel;
        if (_drivenDestination is null) return;
        viewModel.Range = _range;
        viewModel.RangeText = _rangeText;
        viewModel.Destination = _drivenDestination;
        viewModel.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    }
}

/// TDD/integração dos comandos "Exportar como Word (.docx)"/"Exportar como Excel (.xlsx)" (Task 3, Plano
/// 16) — orquestração no App: extrai texto+posições via mPdf.Rendering (GetTextPage/GetPageSize), mapeia
/// PdfCharacter->ExportChar, monta ExportPage e chama IDocxExporter/IXlsxExporter (T1/T2), com alcance,
/// progresso e cancelamento. LEITURA PURA (o PDF nunca é tocado; funciona em assinado, byte-idêntico).
public sealed class ExportDocumentIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-exportdoc-{Guid.NewGuid():N}");
    public ExportDocumentIntegrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string A4Path => Path.Combine(Fixtures.Root, "fixture-a4.pdf");
    private static string ThirtyPagesPath => Path.Combine(Fixtures.Root, "fixture-30p.pdf");

    private string WriteTempPdf(byte[] bytes)
    {
        var path = Path.Combine(_dir, $"src-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// PDF de 1 página SEM camada de texto (imagem pura de uma frase) — mesmo padrão de OcrCommandTests.
    private static byte[] BuildImageOnlyPdf(string frase)
    {
        const int w = 900, h = 300;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.HighQuality;
            using var font = new Font(FontFamily.GenericSansSerif, 44f, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(frase, font, Brushes.Black, new PointF(20f, 110f));
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        IPdfEditor editor = PdfEditorFactory.Create();
        return editor.ImageToPdf(ms.ToArray());
    }

    /// fixture-a4 assinada com um certificado RSA EFÊMERO (motor de assinatura REAL) — doc genuinamente
    /// assinado, ainda com o texto "Fixture A4 do mPDF" extraível.
    private static byte[] SignedA4()
    {
        using X509Certificate2 cert = SignCommandTests.CreateEphemeralRsaCertificate();
        ISigningEngine engine = SigningEngineFactory.Create();
        return engine.Sign(new SignRequest(Fixtures.A4(), cert, null, null, null, null));
    }

    private static string ReadDocxText(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return doc.MainDocumentPart?.Document?.Body?.InnerText ?? "";
    }

    private static string ReadXlsxText(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wbPart = doc.WorkbookPart;
        if (wbPart is null) return "";
        return string.Concat(wbPart.WorksheetParts.Select(wp => wp.Worksheet.InnerText));
    }

    // Oráculo TOLERANTE a espaçamento: o exportador reconstrói palavras a partir das POSIÇÕES dos
    // caracteres (não da sequência crua), então o espaçamento inter-palavra é melhor-esforço (a nota
    // honesta do diálogo diz isso). Para provar que o TEXTO CONHECIDO atravessa o pipeline, comparo o
    // conteúdo sem espaços em branco.
    private static string Strip(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    // ---- Word ponta-a-ponta: PDF-texto -> .docx com o texto conhecido --------------------------------

    [Fact]
    public async Task ExportWord_TextPdf_DocxContainsKnownText()
    {
        var dest = Path.Combine(_dir, "saida.docx");
        var vm = new ExportDocumentViewModel(Fixtures.A4(), pageCount: 1, ExportDocumentKind.Word, "fixture-a4")
        { Destination = dest };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.Succeeded);
        Assert.False(vm.WasCancelled);
        Assert.True(File.Exists(dest));
        Assert.Contains("FixtureA4domPDF", Strip(ReadDocxText(dest)));
    }

    // ---- Word ponta-a-ponta: as PALAVRAS saem SEPARADAS por espaço (bug do espaço de altura zero) ----

    [Fact]
    public async Task ExportWord_TextPdf_WordsAreSeparatedBySpaces()
    {
        // Doc REAL com uma frase de várias palavras ("Fixture A4 do mPDF - pagina unica"). O PDFium
        // extrai os espaços como caracteres de ALTURA ZERO; a análise de layout precisa preservá-los.
        // ANTES da correção o texto saía GRUDADO ("FixtureA4domPDF..."); DEPOIS as palavras têm espaço.
        var dest = Path.Combine(_dir, "separado.docx");
        var vm = new ExportDocumentViewModel(Fixtures.A4(), pageCount: 1, ExportDocumentKind.Word, "fixture-a4")
        { Destination = dest };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.Succeeded);
        string text = ReadDocxText(dest);
        Assert.Contains("Fixture A4 do mPDF", text);   // palavras separadas por espaço, não grudadas
        Assert.Contains("pagina unica", text);
        Assert.DoesNotContain("FixtureA4domPDF", text); // a versão grudada NÃO pode aparecer
    }

    // ---- Excel ponta-a-ponta: as PALAVRAS saem SEPARADAS por espaço numa célula ----------------------

    [Fact]
    public async Task ExportExcel_TextPdf_WordsAreSeparatedBySpaces()
    {
        var dest = Path.Combine(_dir, "separado.xlsx");
        var vm = new ExportDocumentViewModel(Fixtures.A4(), pageCount: 1, ExportDocumentKind.Excel, "fixture-a4")
        { Destination = dest };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.Succeeded);
        string text = ReadXlsxText(dest);
        Assert.Contains("Fixture A4 do mPDF", text);   // célula com o texto e espaços preservados
        Assert.DoesNotContain("FixtureA4domPDF", text);
    }

    // ---- Excel ponta-a-ponta: PDF-texto -> .xlsx com o texto conhecido -------------------------------

    [Fact]
    public async Task ExportExcel_TextPdf_XlsxContainsKnownText()
    {
        var dest = Path.Combine(_dir, "saida.xlsx");
        var vm = new ExportDocumentViewModel(Fixtures.A4(), pageCount: 1, ExportDocumentKind.Excel, "fixture-a4")
        { Destination = dest };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.Succeeded);
        Assert.True(File.Exists(dest));
        Assert.Contains("FixtureA4domPDF", Strip(ReadXlsxText(dest)));
    }

    // ---- Sem texto (escaneado sem OCR) -> aviso via prompt, NENHUM arquivo, diálogo não abre ---------

    [Fact]
    public async Task ExportWord_ScannedNoOcr_WarnsAndDoesNotOpenDialog()
    {
        string path = WriteTempPdf(BuildImageOnlyPdf("Documento escaneado"));
        var infos = new List<string>();
        var dialog = new FakeExportDocumentDialogService(); // não dirige nada
        using var doc = new DocumentViewModel(
            DocumentSession.Open(path),
            exportDocumentDialog: dialog,
            notifyInfo: infos.Add,
            notifyError: _ => { });

        await doc.ExportDocumentCoreAsync(ExportDocumentKind.Word);

        Assert.Equal(0, dialog.ShowCount); // diálogo NUNCA abriu -> nenhum arquivo gerado
        Assert.Contains(infos, m => m.Contains("não tem texto pesquisável") && m.Contains("OCR"));
        Assert.False(doc.IsDirty); // leitura pura — PDF intocado
    }

    // ---- VM: alcance sem nenhum caractere -> NoTextInRange, nenhum arquivo (guarda defensiva) ---------

    [Fact]
    public async Task ExportWord_ImagePdf_AtVmLevel_NoTextInRange_NoFile()
    {
        var dest = Path.Combine(_dir, "vazio.docx");
        var vm = new ExportDocumentViewModel(BuildImageOnlyPdf("Só imagem"), pageCount: 1, ExportDocumentKind.Word, "img")
        { Destination = dest };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.NoTextInRange);
        Assert.False(vm.Succeeded);
        Assert.False(File.Exists(dest)); // nenhum arquivo quando o alcance não tem texto
    }

    // ---- Reuso do OCR: a camada invisível do Plano 15 aparece no export ------------------------------

    [Fact]
    public async Task ExportWord_OcrTextLayerReused_DocxContainsOcrText()
    {
        // Monta um PDF-imagem OCR'd aplicando a MESMA camada de texto invisível do OCR (Plano 15) via o
        // IPdfEditor REAL — prova que o export lê exatamente o texto que o OCR grava.
        IPdfEditor editor = PdfEditorFactory.Create();
        var layer = new OcrTextLayer(0, 800, 1131, new[]
        {
            new OcrTextBox("REUSOOCR", LeftPx: 100, TopPx: 400, WidthPx: 300, HeightPx: 40),
        });
        byte[] ocrPdf = editor.ApplyOcrTextLayer(Fixtures.NoText(), new[] { layer });

        var dest = Path.Combine(_dir, "ocr.docx");
        var vm = new ExportDocumentViewModel(ocrPdf, pageCount: 1, ExportDocumentKind.Word, "ocr")
        { Destination = dest };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.Succeeded);
        Assert.Contains("REUSOOCR", Strip(ReadDocxText(dest)));
    }

    // ---- Somente-leitura em assinado: exporta E o PDF de origem fica BYTE-IDÊNTICO -------------------

    [Fact]
    public async Task ExportWord_SignedDocument_ReadOnly_SourcePdfByteIdentical()
    {
        byte[] signedBytes = SignedA4();
        string path = WriteTempPdf(signedBytes);
        byte[] before = File.ReadAllBytes(path); // bytes do PDF ANTES de exportar

        var dest = Path.Combine(_dir, "assinado.docx");
        var dialog = new FakeExportDocumentDialogService(drivenDestination: dest); // dirige até o fim
        using var doc = new DocumentViewModel(
            DocumentSession.Open(path),
            exportDocumentDialog: dialog,
            notifyInfo: _ => { },
            notifyError: _ => { });

        // pré-condição: doc genuinamente assinado (o export NÃO passa pelo gate — é leitura pura).
        byte[] snapshotBefore = doc.Session.Snapshot;

        await doc.ExportDocumentCoreAsync(ExportDocumentKind.Word);

        Assert.Equal(1, dialog.ShowCount);
        Assert.True(dialog.LastViewModel!.Succeeded);
        Assert.True(File.Exists(dest));
        Assert.Contains("FixtureA4domPDF", Strip(ReadDocxText(dest)));

        // PROVA somente-leitura: o snapshot da sessão e os bytes em disco ficam BYTE-IDÊNTICOS.
        Assert.False(doc.IsDirty);
        Assert.True(doc.Session.Snapshot.SequenceEqual(snapshotBefore));
        Assert.True(File.ReadAllBytes(path).SequenceEqual(before));
    }

    // ---- Alcance "1-2" num doc de 30 páginas exporta só essas ----------------------------------------

    [Fact]
    public async Task ExportWord_Range_ExportsOnlySelectedPages()
    {
        var dest = Path.Combine(_dir, "intervalo.docx");
        var vm = new ExportDocumentViewModel(Fixtures.ThirtyPages(), pageCount: 30, ExportDocumentKind.Word, "30p")
        {
            Range = ExportDocumentRange.Custom,
            RangeText = "1-2",
            Destination = dest,
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.Succeeded);
        Assert.Equal(2, vm.ExportedPageCount);
        string text = Strip(ReadDocxText(dest));
        Assert.Contains("pagina1", text);
        Assert.Contains("pagina2", text);
        Assert.DoesNotContain("pagina3", text); // as demais NÃO entram
    }

    // ---- Intervalo inválido -> RangeError, permanece em Options, nenhum arquivo ----------------------

    [Fact]
    public async Task ExportWord_InvalidRange_SetsRangeError_NoFile()
    {
        var dest = Path.Combine(_dir, "erro.docx");
        var vm = new ExportDocumentViewModel(Fixtures.ThirtyPages(), pageCount: 30, ExportDocumentKind.Word, "30p")
        {
            Range = ExportDocumentRange.Custom,
            RangeText = "99-200", // fora dos limites
            Destination = dest,
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.NotNull(vm.RangeError);
        Assert.True(vm.CanEditOptions); // ficou em Options
        Assert.False(File.Exists(dest));
    }

    // ---- Cancelar no meio -> nenhum arquivo (nem parcial) --------------------------------------------

    [Fact]
    public async Task ExportWord_CancelMidway_NoFileWritten()
    {
        var dest = Path.Combine(_dir, "cancelado.docx");
        var vm = new ExportDocumentViewModel(Fixtures.ThirtyPages(), pageCount: 30, ExportDocumentKind.Word, "30p")
        { Destination = dest };

        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.TestGateReachedAfterFirstPage = reached;
        vm.TestGateAfterFirstPage = gate;

        var startTask = vm.StartCommand.ExecuteAsync(null);
        await reached.Task;                 // página 1 extraída, laço pausado no gate
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);     // cancela
        gate.SetResult(true);               // libera o gate -> laço vê o cancelamento antes da página 2
        await startTask;

        Assert.True(vm.WasCancelled);
        Assert.False(vm.Succeeded);
        Assert.False(File.Exists(dest));    // grava só ao concluir -> cancelado = NENHUM arquivo (nem parcial)
    }

    // ---- Comandos habilitados com documento aberto, inclusive assinado (leitura pura) ----------------

    [Fact]
    public void ExportCommands_EnabledWithDocumentOpen_IncludingSigned()
    {
        using var doc = new DocumentViewModel(DocumentSession.Open(A4Path)) { IsSignedDocument = true };
        Assert.True(doc.ExportWordCommand.CanExecute(null));
        Assert.True(doc.ExportExcelCommand.CanExecute(null));
    }
}
