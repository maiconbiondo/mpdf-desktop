using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Ocr;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

// ===== Fakes REUSÁVEIS (internal, não file-scoped) da orquestração de OCR (Task 4, Plano 15) =========
// Também consumidos por UiPromptsGuardTests (prova de disparo/controle negativo do ocrProgress).

/// Rasterizador de OCR fake: páginas com/sem texto CONFIGURADAS, sem tocar o renderer nativo. `PageCount`
/// vem do tamanho de `hasText`; `RasterizeForOcr` devolve um `RenderedPage` fixo (dimensões pequenas —
/// os testes de orquestração não olham pixel, só o fluxo).
internal sealed class FakeOcrRasterizer : IOcrPageRasterizer
{
    private readonly bool[] _hasText;
    private readonly RenderedPage _rendered;
    public int RasterizeCallCount { get; private set; }
    public bool Disposed { get; private set; }

    public FakeOcrRasterizer(bool[] hasText, RenderedPage? rendered = null)
    {
        _hasText = hasText;
        _rendered = rendered ?? new RenderedPage(8, 8, new byte[8 * 8 * 4]);
    }

    public int PageCount => _hasText.Length;
    public bool PaginaTemTexto(int pageIndex) => _hasText[pageIndex];
    public RenderedPage RasterizeForOcr(int pageIndex) { RasterizeCallCount++; return _rendered; }
    public void Dispose() => Disposed = true;
}

/// Motor de OCR fake determinístico: devolve um resultado FIXO (default: 1 palavra "TESTE") e pode
/// LANÇAR em números de chamada específicos (1-based) — prova que uma página que falha não aborta as
/// demais.
internal sealed class FakeOcrEngine : IOcrEngine
{
    private readonly OcrEngineResult _result;
    private readonly HashSet<int> _throwOnCall;
    public int RecognizeCallCount { get; private set; }

    public FakeOcrEngine(OcrEngineResult? result = null, HashSet<int>? throwOnCall = null)
    {
        _result = result ?? new OcrEngineResult(new List<OcrWord> { new("TESTE", 10, 10, 50, 20, 90f) }, "TESTE");
        _throwOnCall = throwOnCall ?? new HashSet<int>();
    }

    public OcrEngineResult Recognize(ReadOnlySpan<byte> bgra, int widthPx, int heightPx, string languages)
    {
        RecognizeCallCount++;
        if (_throwOnCall.Contains(RecognizeCallCount)) throw new InvalidOperationException("falha simulada de OCR");
        return _result;
    }
}

/// Serviço de progresso fake: registra os reports "N de M" (observável) e permite CANCELAR depois de N
/// reports (simula o clique em Cancelar no meio do processamento). Nunca abre janela nenhuma.
internal sealed class FakeOcrProgressService : IOcrProgressService
{
    private readonly int _cancelAfterReports;
    public int StartCount { get; private set; }
    public FakeOcrProgressSession? LastSession { get; private set; }

    /// <param name="cancelAfterReports">-1 = nunca cancela; N &gt;= 1 = cancela quando o N-ésimo report chega.</param>
    public FakeOcrProgressService(int cancelAfterReports = -1) => _cancelAfterReports = cancelAfterReports;

    public IOcrProgressSession Start()
    {
        StartCount++;
        LastSession = new FakeOcrProgressSession(_cancelAfterReports);
        return LastSession;
    }
}

internal sealed class FakeOcrProgressSession : IOcrProgressSession
{
    private readonly CancellationTokenSource _cts = new();
    private readonly int _cancelAfterReports;
    public List<OcrProgress> Reports { get; } = new();
    public bool Disposed { get; private set; }

    public FakeOcrProgressSession(int cancelAfterReports) => _cancelAfterReports = cancelAfterReports;

    public CancellationToken Token => _cts.Token;
    public IProgress<OcrProgress> Progress => new SyncProgress(this);
    public void Dispose() { Disposed = true; _cts.Dispose(); }

    // IProgress SÍNCRONO (não o Progress<T> de produção que marshala pro SynchronizationContext): nos
    // testes o Report é chamado da thread do Task.Run e só encosta numa List — lida após o await (barreira
    // de conclusão da Task), determinístico.
    private sealed class SyncProgress(FakeOcrProgressSession owner) : IProgress<OcrProgress>
    {
        public void Report(OcrProgress value)
        {
            owner.Reports.Add(value);
            if (owner._cancelAfterReports >= 1 && owner.Reports.Count >= owner._cancelAfterReports)
                owner._cts.Cancel();
        }
    }
}

/// TDD/integração do comando "Reconhecer texto (OCR)" (Task 4, Plano 15) — orquestração no App:
/// determina páginas-alvo (T2) -> render 300dpi (T2) -> `IOcrEngine.Recognize` (T1) -> mapeia
/// `OcrEngineResult`->`OcrTextLayer` -> `ApplyOcrTextLayer` (T3) no funil existente, async, com
/// progresso/cancelamento. Fakes determinísticos para a orquestração; UM teste com o motor REAL prova
/// ponta-a-ponta que a busca (a mesma do Ctrl+F) ACHA um termo conhecido no doc resultante.
public sealed class OcrCommandTests
{
    private static string A4Path => Path.Combine(Fixtures.Root, "fixture-a4.pdf");

    private static string WriteTempPdf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mpdf-ocr-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// PDF de 1 página SEM camada de texto (imagem pura de uma frase conhecida) — mesmo padrão de
    /// OcrPageRasterizerTests.BuildImageOnlyPdf (System.Drawing -> PNG -> ImageToPdf).
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

    // ---- Ponta-a-ponta com o motor REAL: escaneado -> OCR -> a busca (Ctrl+F) acha o termo -----------

    [Fact]
    public async Task RecognizeText_MotorReal_PdfImagem_FicaPesquisavel()
    {
        string path = WriteTempPdf(BuildImageOnlyPdf("Documento escaneado"));
        try
        {
            var infos = new List<string>();
            using var doc = new DocumentViewModel(
                DocumentSession.Open(path),
                // editor REAL (default) + rasterizer REAL (default) + motor REAL (default lazy Tesseract).
                ocrProgress: new FakeOcrProgressService(),
                notifyInfo: infos.Add,
                notifyError: _ => { });

            await doc.RecognizeTextCoreAsync();

            // O snapshot resultante deve ter texto EXTRAÍVEL que a MESMA busca do Ctrl+F acha.
            using var renderer = new PdfDocumentRenderer(doc.Session.Snapshot);
            var hits = PdfTextSearch.FindAll(renderer, "escaneado", CancellationToken.None);
            Assert.NotEmpty(hits);
            Assert.True(doc.IsDirty); // a camada foi aplicada como edição (entra no dirty/save)
        }
        finally { File.Delete(path); }
    }

    // ---- Pula página que já tem texto (real): "nada a reconhecer", não altera ------------------------

    [Fact]
    public async Task RecognizeText_DocumentoComTexto_NaoAdicionaCamada()
    {
        var fake = new FakePdfEditor();
        var infos = new List<string>();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),   // fixture COM texto real
            editor: fake,
            // rasterizer REAL (default): PaginaTemTexto(0) == true para o A4.
            ocrProgress: new FakeOcrProgressService(),
            notifyInfo: infos.Add,
            notifyError: _ => { });

        await doc.RecognizeTextCoreAsync();

        Assert.Empty(fake.ApplyOcrTextLayerInputs);           // nenhuma camada aplicada
        Assert.False(doc.IsDirty);                            // documento intocado
        Assert.Contains(infos, m => m.Contains("Nada a reconhecer"));
    }

    // ---- Progresso "N de M" observável --------------------------------------------------------------

    [Fact]
    public async Task RecognizeText_ReportaProgressoNdeM()
    {
        var fake = new FakePdfEditor();  // ApplyOcrTextLayer devolve o pdf original (válido) -> ApplyEdit OK
        var progress = new FakeOcrProgressService();
        var infos = new List<string>();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            ocrEngine: new FakeOcrEngine(),
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false, false }), // 2 páginas SEM texto
            ocrProgress: progress,
            notifyInfo: infos.Add,
            notifyError: _ => { });

        await doc.RecognizeTextCoreAsync();

        Assert.NotNull(progress.LastSession);
        Assert.Equal(new[] { new OcrProgress(1, 2), new OcrProgress(2, 2) }, progress.LastSession!.Reports);
        Assert.True(progress.LastSession.Disposed);           // a faixa foi fechada ao fim
        Assert.Single(fake.ApplyOcrTextLayerInputs);          // 1 passo de edição
        Assert.Equal(2, fake.ApplyOcrTextLayerInputs[0].Count); // com 2 layers
        Assert.Contains(infos, m => m.Contains("2 página"));
    }

    // ---- Cancelamento no meio: nada gravado ---------------------------------------------------------

    [Fact]
    public async Task RecognizeText_CanceladoNoMeio_NadaGravado()
    {
        var fake = new FakePdfEditor();
        var engine = new FakeOcrEngine();
        var progress = new FakeOcrProgressService(cancelAfterReports: 1); // cancela ao 1º report
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            ocrEngine: engine,
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false, false, false }), // 3 páginas
            ocrProgress: progress,
            notifyInfo: _ => { },
            notifyError: _ => { });

        await doc.RecognizeTextCoreAsync();

        Assert.Empty(fake.ApplyOcrTextLayerInputs);   // cancelado -> nunca aplica
        Assert.False(doc.IsDirty);                    // nada gravado sem salvar
        Assert.True(engine.RecognizeCallCount < 3);   // interrompeu antes de processar todas
        Assert.False(doc.Session.IsEditInFlight);     // funil liberado (finally EndEdit)
    }

    // ---- Falha numa página conta e SEGUE ------------------------------------------------------------

    [Fact]
    public async Task RecognizeText_FalhaNumaPagina_SegueEInforma()
    {
        var fake = new FakePdfEditor();
        var engine = new FakeOcrEngine(throwOnCall: new HashSet<int> { 1 }); // 1ª página falha
        var infos = new List<string>();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            ocrEngine: engine,
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false, false }),
            ocrProgress: new FakeOcrProgressService(),
            notifyInfo: infos.Add,
            notifyError: _ => { });

        await doc.RecognizeTextCoreAsync();

        Assert.Equal(2, engine.RecognizeCallCount);           // não abortou na 1ª falha
        Assert.Single(fake.ApplyOcrTextLayerInputs);          // aplicou o que deu (1 layer)
        Assert.Single(fake.ApplyOcrTextLayerInputs[0]);       // exatamente 1 layer (a página que deu certo)
        Assert.Contains(infos, m => m.Contains("não puderam ser reconhecidas"));
    }

    // ---- Gate de assinatura: aviso/cópia não-assinada (não edita in-place) ---------------------------

    [Fact]
    public async Task RecognizeText_DocumentoAssinado_AvisaEditarUmaCopia()
    {
        var fake = new FakePdfEditor { ThrowOnApplyOcrTextLayer = new PdfSignedDocumentException("assinado") };
        var erros = new List<string>();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            ocrEngine: new FakeOcrEngine(),
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false }),
            ocrProgress: new FakeOcrProgressService(),
            notifyInfo: _ => { },
            notifyError: erros.Add);

        await doc.RecognizeTextCoreAsync();

        Assert.False(doc.IsDirty);                                          // não editou in-place
        Assert.Contains(erros, m => m.Contains("assinado") && m.Contains("Editar uma cópia"));
    }

    // ---- CanExecute: desabilitado sem documento editável (assinado) ----------------------------------

    [Fact]
    public void RecognizeTextCommand_DocumentoAssinado_Desabilitado()
    {
        using var doc = new DocumentViewModel(DocumentSession.Open(A4Path)) { IsSignedDocument = true };
        Assert.False(doc.RecognizeTextCommand.CanExecute(null));
    }

    [Fact]
    public void RecognizeTextCommand_DocumentoEditavel_Habilitado()
    {
        using var doc = new DocumentViewModel(DocumentSession.Open(A4Path));
        Assert.True(doc.RecognizeTextCommand.CanExecute(null));
    }
}
