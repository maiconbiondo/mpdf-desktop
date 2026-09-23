using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// <summary>
/// Task 6 (SDD "Salvar como PDF/A") — TDD da orquestração no App: `SalvarComoPdfACoreAsync` rasteriza
/// cada página (T2, mesmo rasterizer de OCR), opcionalmente roda OCR (T1) nas páginas sem texto, chama
/// `IPdfEditor.CriarPdfADeImagens` (T2-4) e grava um arquivo NOVO — leitura pura, sem
/// `TryBeginEdit`/`ApplyEdit` (mesma classe de `ExportImage`/`ExportDocumentCoreAsync`). Os testes
/// chamam o core DIRETO (bypass de `_pdfADialog`, que ainda lança por padrão — Task 7 liga a view) e
/// reusam os fakes de OCR já existentes (`FakeOcrRasterizer`/`FakeOcrEngine`/`FakeOcrProgressService`,
/// ver `OcrCommandTests.cs`) + `FakePdfEditor` (`DocumentViewModelTests.cs`, já expõe
/// `UltimoPdfAPaginas`/`UltimoPdfANivel`/`UltimoPdfAFonte`).
/// </summary>
public class PdfAOrchestrationTests
{
    private static string A4Path => Path.Combine(Fixtures.Root, "fixture-a4.pdf");
    private static string SavedPath => Path.Combine(Path.GetTempPath(), "mpdf-pdfa-orchestration-saida.pdf");

    [Fact] // manterPesquisavel=false: nenhuma página recebe texto/fonte — o editor devolve páginas
    // SÓ-IMAGEM (Texto null) e fonte NULL (CriarPdfADeImagens não precisa embutir nada).
    public async Task SemOcr_ChamaEditorSemTextoNemFonte()
    {
        var fake = new FakePdfEditor();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false, false }), // 2 páginas
            ocrProgress: new FakeOcrProgressService(),
            pickPdfToSave: _ => SavedPath,
            writeAllBytes: (_, _) => { },
            notifyInfo: _ => { },
            notifyError: _ => { });

        await doc.SalvarComoPdfACoreAsync(PdfANivel.A1B, manterPesquisavel: false);

        Assert.NotNull(fake.UltimoPdfAPaginas);
        Assert.Equal(2, fake.UltimoPdfAPaginas!.Count);
        Assert.Null(fake.UltimoPdfAFonte);
        Assert.All(fake.UltimoPdfAPaginas!, p => Assert.Null(p.Texto));
    }

    [Fact] // manterPesquisavel=true: página SEM texto -> OCR roda (T1) e as caixas reconhecidas +
    // a fonte invisível (T5) chegam ao editor.
    public async Task ComOcr_PassaFonteECaixas()
    {
        var fake = new FakePdfEditor();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            ocrEngine: new FakeOcrEngine(), // default: devolve 1 palavra "TESTE"
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false }), // 1 página SEM texto
            ocrProgress: new FakeOcrProgressService(),
            pickPdfToSave: _ => SavedPath,
            writeAllBytes: (_, _) => { },
            notifyInfo: _ => { },
            notifyError: _ => { });

        await doc.SalvarComoPdfACoreAsync(PdfANivel.A2B, manterPesquisavel: true);

        Assert.NotNull(fake.UltimoPdfAFonte);
        Assert.NotNull(fake.UltimoPdfAPaginas);
        Assert.Contains(fake.UltimoPdfAPaginas!, p => p.Texto is { Count: > 0 });
    }

    [Fact] // OCR falha numa página (2ª chamada) não aborta a conversão (T1/T6) — a página falha entra
    // só como imagem (Texto null) e a página seguinte, que teve sucesso, chega com as caixas.
    public async Task FalhaDeOcrNumaPagina_NaoAborta_PaginaViraSoImagem()
    {
        var fake = new FakePdfEditor();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            ocrEngine: new FakeOcrEngine(throwOnCall: new HashSet<int> { 2 }), // 2ª chamada de Recognize falha
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false, false }), // 2 páginas SEM texto -> ambas OCR'am
            ocrProgress: new FakeOcrProgressService(),
            pickPdfToSave: _ => SavedPath,
            writeAllBytes: (_, _) => { },
            notifyInfo: _ => { },
            notifyError: _ => { });

        await doc.SalvarComoPdfACoreAsync(PdfANivel.A2B, manterPesquisavel: true);

        Assert.NotNull(fake.UltimoPdfAPaginas);
        Assert.Equal(2, fake.UltimoPdfAPaginas!.Count); // a página que falhou não abortou a conversão
        Assert.NotNull(fake.UltimoPdfAPaginas![0].Texto); // 1ª chamada de OCR teve sucesso -> caixas
        Assert.Null(fake.UltimoPdfAPaginas![1].Texto); // 2ª chamada falhou -> página só-imagem
    }

    [Fact] // o nível escolhido (A1B/A2B/A3B) chega intacto ao editor.
    public async Task Nivel_ChegaAoEditor()
    {
        var fake = new FakePdfEditor();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false }),
            ocrProgress: new FakeOcrProgressService(),
            pickPdfToSave: _ => SavedPath,
            writeAllBytes: (_, _) => { },
            notifyInfo: _ => { },
            notifyError: _ => { });

        await doc.SalvarComoPdfACoreAsync(PdfANivel.A3B, manterPesquisavel: false);

        Assert.Equal(PdfANivel.A3B, fake.UltimoPdfANivel);
    }

    [Fact] // cancelamento no meio do progresso interrompe ANTES de compor/gravar — nem o editor nem o
    // "Salvar como…"/gravação são alcançados.
    public async Task Cancelado_NaoGrava()
    {
        var fake = new FakePdfEditor();
        bool pickCalled = false, writeCalled = false;
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            ocrEngine: new FakeOcrEngine(),
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false, false, false }), // 3 páginas
            ocrProgress: new FakeOcrProgressService(cancelAfterReports: 1), // cancela ao 1º report
            pickPdfToSave: _ => { pickCalled = true; return SavedPath; },
            writeAllBytes: (_, _) => { writeCalled = true; },
            notifyInfo: _ => { },
            notifyError: _ => { });

        await doc.SalvarComoPdfACoreAsync(PdfANivel.A1B, manterPesquisavel: false);

        Assert.False(pickCalled);
        Assert.False(writeCalled);
        Assert.Null(fake.UltimoPdfAPaginas); // o editor nunca chegou a ser chamado
    }

    [Fact] // documento assinado: a conversão RODA (leitura pura, nunca toca o original) e a mensagem de
    // sucesso avisa explicitamente que o resultado NÃO está assinado.
    public async Task Assinado_ConverteEAvisaNaoAssinado()
    {
        var fake = new FakePdfEditor();
        var infos = new List<string>();
        using var doc = new DocumentViewModel(
            DocumentSession.Open(A4Path),
            editor: fake,
            rasterizerFactory: _ => new FakeOcrRasterizer(new[] { false }),
            ocrProgress: new FakeOcrProgressService(),
            pickPdfToSave: _ => SavedPath,
            writeAllBytes: (_, _) => { },
            notifyInfo: infos.Add,
            notifyError: _ => { })
        { IsSignedDocument = true };

        await doc.SalvarComoPdfACoreAsync(PdfANivel.A1B, manterPesquisavel: false);

        Assert.NotNull(fake.UltimoPdfAPaginas); // a conversão rodou normalmente (leitura pura)
        Assert.Contains(infos, m => m.Contains("NÃO está assinado"));
    }
}
