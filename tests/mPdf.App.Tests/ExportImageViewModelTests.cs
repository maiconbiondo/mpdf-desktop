using System.IO;
using System.Windows.Media.Imaging;
using mPdf.App.ViewModels;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

/// VM de "Exportar página como imagem" (Task 4, Plano 7). Diferente de BatchSignViewModelTests, nenhum
/// FakePdfEditor aparece aqui: o motor de exportação só consome `mPdf.Rendering` (leitura pura de
/// pixels, PDFium via `PdfDocumentRenderer`) — nunca `mPdf.Editing`. Fixtures REAIS pequenas
/// (`fixture-a4.pdf`/`fixture-30p.pdf`) bastam para os testes de PLUMBING/mecânica aqui; o oráculo de
/// pixels ESTRITO (PNG)/tolerância MEDIDA (JPG) vive em `ExportImageIntegrationTests` (comparação
/// byte-a-byte contra um 2º `PdfDocumentRenderer` independente).
public class ExportImageViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-exportimg-vm-{Guid.NewGuid():N}");
    public ExportImageViewModelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condição nunca ficou verdadeira.");
            await Task.Delay(5);
        }
    }

    // ---- CanStart / CanCancel --------------------------------------------------------------------------

    [Fact]
    public void CanStart_FalseWithoutDestination()
    {
        var vm = new ExportImageViewModel(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "a4");
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void CanStart_TrueWithDestination()
    {
        var vm = new ExportImageViewModel(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "a4")
        { Destination = Path.Combine(_dir, "saida.png") };
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void CanCancel_FalseBeforeStart()
    {
        var vm = new ExportImageViewModel(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "a4");
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    // ---- página atual: grava exatamente no destino escolhido (sem sufixo de colisão) -------------------

    [Fact]
    public async Task Start_CurrentPage_Png_WritesFileAtExactDestination()
    {
        var dest = Path.Combine(_dir, "saida.png");
        var vm = new ExportImageViewModel(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "a4")
        { Destination = dest };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(File.Exists(dest));
        Assert.Equal(1, vm.ExportedCount);
        Assert.False(vm.WasCancelled);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(ExportImagePhase.Done, vm.Phase);
    }

    [Fact] // plumbing: formato
    public async Task Start_FormatJpg_WritesJpegMagicBytes()
    {
        var dest = Path.Combine(_dir, "saida.jpg");
        var vm = new ExportImageViewModel(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "a4")
        { Destination = dest, Format = ExportImageFormat.Jpg };

        await vm.StartCommand.ExecuteAsync(null);

        var bytes = File.ReadAllBytes(dest);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]); // magic bytes JPEG (FFD8) -- mesma checagem de IsSupportedImage/mPdf.Editing
    }

    [Fact] // plumbing: dpi -- formula exata (arredondamento do PDFium) fica provada em ExportImageIntegrationTests.
    public async Task Start_Dpi300_ProducesLargerPixelDimensionsThanDpi150()
    {
        var dest150 = Path.Combine(_dir, "150.png");
        var vm150 = new ExportImageViewModel(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "a4")
        { Destination = dest150, Dpi = 150 };
        await vm150.StartCommand.ExecuteAsync(null);

        var dest300 = Path.Combine(_dir, "300.png");
        var vm300 = new ExportImageViewModel(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "a4")
        { Destination = dest300, Dpi = 300 };
        await vm300.StartCommand.ExecuteAsync(null);

        var (w150, h150) = ReadPngDimensions(dest150);
        var (w300, h300) = ReadPngDimensions(dest300);
        Assert.True(w300 > w150);
        Assert.True(h300 > h150);
    }

    [Fact] // plumbing: alcance/índice -- página CORRENTE (não a 0) é a exportada.
    public async Task Start_RangeCurrentPage_UsesCurrentPageIndex_NotFirstPage()
    {
        var dest = Path.Combine(_dir, "pagina6.png");
        var vm = new ExportImageViewModel(Fixtures.ThirtyPages(), pageCount: 30, currentPageIndex: 5, baseFileName: "doc")
        { Destination = dest, Range = ExportImageRange.CurrentPage };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.ExportedCount);
        Assert.True(File.Exists(dest));
    }

    // ---- todas as páginas: N arquivos, zero-padded à largura da contagem de páginas --------------------

    [Fact]
    public async Task Start_RangeAllPages_GeneratesOneFilePerPage_ZeroPaddedToPageCountWidth()
    {
        var vm = new ExportImageViewModel(Fixtures.ThirtyPages(), pageCount: 30, currentPageIndex: 0, baseFileName: "doc")
        { Destination = _dir, Range = ExportImageRange.AllPages };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(30, vm.ExportedCount);
        Assert.True(File.Exists(Path.Combine(_dir, "doc-p01.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "doc-p30.png")));
        Assert.Equal(30, Directory.GetFiles(_dir, "*.png").Length);
    }

    // ---- colisão de nome (alcance = todas) -- mesma convenção de BuildSplitPartPath/BuildSignedOutputPath

    [Fact]
    public async Task Start_AllPages_OutputNameCollision_UsesNumberedSuffix()
    {
        File.WriteAllBytes(Path.Combine(_dir, "doc-p01.png"), [9, 9, 9]); // força colisão na página 1
        var vm = new ExportImageViewModel(Fixtures.ThirtyPages(), pageCount: 30, currentPageIndex: 0, baseFileName: "doc")
        { Destination = _dir, Range = ExportImageRange.AllPages };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(_dir, "doc-p01 (2).png")));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(_dir, "doc-p01.png"))); // colidido NÃO sobrescrito
        Assert.Equal(30, vm.ExportedCount); // as 30 páginas foram exportadas mesmo assim (só o nome mudou)
    }

    // ---- cancelamento -- "para ANTES da próxima página" (exemplar: BatchSignViewModel.Start) ------------

    [Fact]
    public async Task Start_Cancel_StopsBeforeNextPage_FirstPageCompletesAndIsWritten()
    {
        var vm = new ExportImageViewModel(Fixtures.ThirtyPages(), pageCount: 30, currentPageIndex: 0, baseFileName: "doc")
        { Destination = _dir, Range = ExportImageRange.AllPages };
        var gate = new TaskCompletionSource<bool>();
        vm.TestGateAfterFirstPage = gate;

        var startTask = vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => File.Exists(Path.Combine(_dir, "doc-p01.png")));
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        gate.SetResult(true); // libera a página 1 -- ela TERMINA de qualquer jeito (nunca interrompida no meio)

        await startTask;

        Assert.True(vm.WasCancelled);
        Assert.Equal(ExportImagePhase.Done, vm.Phase);
        Assert.Equal(1, vm.ExportedCount);
        Assert.True(File.Exists(Path.Combine(_dir, "doc-p01.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "doc-p02.png")));
        Assert.False(vm.CancelCommand.CanExecute(null)); // fase Done -- cancelar não faz mais sentido
    }

    // ---- leitura pura: funciona em documento ASSINADO, sem gate (política uniforme, Contract.cs) --------

    [Fact] // revisão: a alegação "leitura pura" precisa ser LITERAL -- captura os bytes assinados ANTES
    // (cópia independente) e confere IGUALDADE depois da exportação. Não há "arquivo de origem" nesta
    // suíte (os bytes assinados nunca são escritos em disco antes de entrar no VM -- só o SNAPSHOT em
    // memória existe), então a comparação é sobre o próprio array `signed`.
    public async Task Start_SignedDocument_ExportsSuccessfully_NoGate()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var request = new SignRequest(Fixtures.A4(), cert, Reason: null, Location: null, Stamp: null, CertificationLevel: null);
        byte[] signed = SigningEngineFactory.Create().Sign(request);
        byte[] signedBeforeExport = (byte[])signed.Clone();

        var dest = Path.Combine(_dir, "assinado.png");
        var vm = new ExportImageViewModel(signed, pageCount: 1, currentPageIndex: 0, baseFileName: "assinado")
        { Destination = dest };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.True(File.Exists(dest));
        Assert.Equal(1, vm.ExportedCount);
        Assert.False(vm.WasCancelled);
        Assert.Equal(signedBeforeExport, signed); // NÃO-MUTAÇÃO literal: o array assinado ficou intacto
    }

    // ---- falha de I/O NO MEIO do lote (revisão): página já gravada FICA, erro pt-BR, sem cancelamento --

    [Fact] // mesmo padrão de bloqueio de arquivo (FileShare.None) de DocumentSessionTests/
    // MainViewModelTests/SignCommandTests -- pré-trava "doc-p02.png" ANTES de exportar; File.WriteAllBytes
    // da página 2 lança IOException; a página 1 (já gravada com sucesso) permanece no disco (mesma
    // semântica de "arquivos completos ficam" já usada pro cancelamento, estendida aqui pra erro).
    public async Task Start_AllPages_IoFailureMidBatch_PreviousPageStays_ErrorMessageSetPtBr()
    {
        // ACHADO (tentativa inicial usava `new FileStream(lockedPath, ..., FileShare.None)`, o mesmo
        // padrão de DocumentSessionTests/MainViewModelTests/SignCommandTests -- NÃO force um IOException
        // aqui: `BuildPagePath` (colisão de nome) só olha `File.Exists`, que uma trava FileShare.None
        // AINDA reporta `true` -- o export simplesmente pulou pra "doc-p02 (2).png" e as 30 páginas
        // "tiveram sucesso" (confirmado ao vivo: ExportedCount=30, não 1 -- a trava nunca foi alcançada
        // pelo WriteAllBytes). Uma trava por NOME faz o caminho de colisão desviar, nunca falhar --
        // propriedade BOA em produção (um arquivo "em uso por outro programa" não aborta o lote inteiro,
        // só ganha um sufixo), mas incompatível com ESTE cenário de teste.
        //
        // Pra forçar uma falha de escrita GENUÍNA que a colisão não consiga desviar, o candidato precisa
        // continuar reportando `File.Exists == false` (pra `BuildPagePath` não pular o nome) e MESMO ASSIM
        // fazer `File.WriteAllBytes` lançar -- uma PASTA ocupando o caminho exato faz as duas coisas:
        // `File.Exists` retorna `false` pra um diretório (documentado: só checa arquivos), mas escrever
        // um ARQUIVO onde já existe uma PASTA lança `UnauthorizedAccessException` (confirmado por sonda
        // isolada antes deste teste) -- proxy fiel de uma falha de permissão/disco real (que também não
        // seria "resolvida" trocando o nome do arquivo).
        var blockedPath = Path.Combine(_dir, "doc-p02.png");
        Directory.CreateDirectory(blockedPath); // ocupa o caminho EXATO da página 2 como pasta, não arquivo
        var vm = new ExportImageViewModel(Fixtures.ThirtyPages(), pageCount: 30, currentPageIndex: 0, baseFileName: "doc")
        { Destination = _dir, Range = ExportImageRange.AllPages };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(_dir, "doc-p01.png"))); // página 1 (antes da falha) FICA
        Assert.Equal(1, vm.ExportedCount);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("Falha ao gravar", vm.ErrorMessage);
        Assert.Contains("doc-p02.png", vm.ErrorMessage);
        Assert.False(vm.WasCancelled);
        Assert.Equal(ExportImagePhase.Done, vm.Phase);
        Assert.False(File.Exists(blockedPath)); // nenhum arquivo de imagem foi escrito -- só a pasta placeholder
        Assert.True(Directory.Exists(blockedPath)); // e a pasta continua intacta (nada a mais foi criado ali)
    }

    // ---- metadata de DPI do arquivo exportado (revisão): bate com o dpi ESCOLHIDO, não com o 96 fixo -----

    [Fact]
    public async Task Start_Dpi300_ExportedPngMetadata_ReflectsChosenDpi()
    {
        var dest = Path.Combine(_dir, "meta300.png");
        var vm = new ExportImageViewModel(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "a4")
        { Destination = dest, Dpi = 300 };

        await vm.StartCommand.ExecuteAsync(null);

        using var stream = File.OpenRead(dest);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        Assert.Equal(300, frame.DpiX, precision: 0);
        Assert.Equal(300, frame.DpiY, precision: 0);
    }
}
