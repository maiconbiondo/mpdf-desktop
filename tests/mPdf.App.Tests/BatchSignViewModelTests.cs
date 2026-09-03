using System.IO;
using System.Security.Cryptography.X509Certificates;
using mPdf.App.ViewModels;
using mPdf.Editing;
using mPdf.Rendering;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

// ---- fake (internal, não `file`: precisa aparecer na assinatura de BuildVm abaixo — mesmo precedente
// de FakeSigningEngine em SignCommandTests.cs, mas com controle POR CHAMADA, necessário pra simular
// "arquivo 2 de 3 falha" e "cancelamento no meio do arquivo 1") ------------------------------------------

internal sealed class FakeBatchSigningEngine : ISigningEngine
{
    public List<SignRequest> Requests { get; } = [];
    /// Índice (0-based, ordem de chamada) em que `Sign` deve lançar `ThrowOnIndexException` — `null`
    /// desliga (nenhuma chamada lança).
    public int? ThrowOnIndex { get; set; }
    public Exception? ThrowOnIndexException { get; set; }
    /// Bloqueia a chamada ATUAL até o teste liberar via `SetResult` — mesma mecânica de
    /// `FakeSigningEngine.SignGate` (SignCommandTests.cs), mas aqui o registro em `Requests` acontece
    /// ANTES do bloqueio, pra o teste conseguir observar "a assinatura do arquivo N já começou" via
    /// `Requests.Count` enquanto a chamada ainda está presa no Wait().
    public TaskCompletionSource<bool>? Gate { get; set; }

    public byte[] Sign(SignRequest request)
    {
        int index = Requests.Count;
        Requests.Add(request);
        Gate?.Task.Wait();
        if (ThrowOnIndex == index) throw ThrowOnIndexException!;
        return Fixtures.ThirtyPages();
    }

    public IReadOnlyList<SignatureInfo> ReadSignatures(byte[] pdf) => Array.Empty<SignatureInfo>();

    // Task 6 (Plano 4): BatchSignViewModel nunca preenche formulário — mesmo espírito de
    // FakePdfEditor.StripSignatures (membro nunca exercitado por este VM, lança se alcançado por engano).
    public FillPermission CanFillIncremental(byte[] pdf) => throw new NotSupportedException();
    public byte[] SetFormFieldsIncremental(byte[] pdf, IReadOnlyDictionary<string, string> values) =>
        throw new NotSupportedException();
}

/// Suíte de testes de `BatchSignViewModel` (Task 5, Plano 4). Fixture discipline (doutrina desde a
/// Task 3, Plano 4): TODO arquivo assinado por um teste é uma cópia TEMPORÁRIA (`NewTempFile`), NUNCA a
/// fixture compartilhada em `tests/fixtures` — `IDisposable` limpa tanto as cópias de entrada quanto os
/// arquivos "(assinado)" gerados pelo lote (registrados PREVENTIVAMENTE, antes de rodar, via
/// `ExpectedOutputPath` -- `TryDeleteFile` tolera um caminho que nunca chegou a ser criado).
public class BatchSignViewModelTests : IDisposable
{
    private readonly List<string> _tempFilesToDelete = [];

    public void Dispose()
    {
        foreach (var f in _tempFilesToDelete) TryDeleteFile(f);
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* melhor esforço */ } }

    /// Conteúdo ARBITRÁRIO (não precisa ser um PDF válido) -- suficiente pra qualquer teste que NÃO
    /// exercite `PlaceStamp: true` (o motor é sempre FAKE, nunca valida os bytes de entrada). Testes que
    /// exercitam o carimbo (geometria via `PdfDocumentRenderer`, PDFium de verdade) usam
    /// `NewTempFixtureCopy` abaixo.
    private string NewTempFile(byte[]? content = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mpdf-batchsign-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, content ?? [1, 2, 3]);
        _tempFilesToDelete.Add(path);
        _tempFilesToDelete.Add(ExpectedOutputPath(path));
        return path;
    }

    /// Cópia TEMPORÁRIA de uma fixture real (`fixture-a4.pdf`) -- usada só pelos testes que precisam de
    /// um PDF de verdade (geometria do carimbo via PDFium, ou o motor REAL de assinatura na integração).
    private string NewTempFixtureCopy(string fixtureName = "fixture-a4.pdf")
    {
        var path = Path.Combine(Path.GetTempPath(), $"mpdf-batchsign-{Guid.NewGuid():N}.pdf");
        File.Copy(Path.Combine(Fixtures.Root, fixtureName), path);
        _tempFilesToDelete.Add(path);
        _tempFilesToDelete.Add(ExpectedOutputPath(path));
        return path;
    }

    /// MESMO algoritmo de `BatchSignViewModel.BuildSignedOutputPath` (duplicado aqui só pra prever o
    /// caminho de saída antecipadamente e registrar limpeza -- não é o código sob teste).
    private static string ExpectedOutputPath(string originalPath)
    {
        string dir = Path.GetDirectoryName(originalPath)!;
        string baseName = $"{Path.GetFileNameWithoutExtension(originalPath)} (assinado)";
        string ext = Path.GetExtension(originalPath);
        return Path.Combine(dir, baseName + ext);
    }

    private static string CollisionOutputPath(string originalPath, int n)
    {
        string dir = Path.GetDirectoryName(originalPath)!;
        string baseName = $"{Path.GetFileNameWithoutExtension(originalPath)} (assinado)";
        string ext = Path.GetExtension(originalPath);
        return Path.Combine(dir, $"{baseName} ({n}){ext}");
    }

    private static SigningCertificateInfo RsaCertInfo(X509Certificate2 cert) =>
        new(cert, IsRsa: true, "Assinante Teste (RSA) — Teste — válido até 12/2099", IsIcpBrasilPersonal: false, IsIcpBrasilCompany: false);

    private static SigningCertificateInfo EccCertInfo(X509Certificate2 cert) =>
        new(cert, IsRsa: false, "Assinante Teste (ECC) — Teste — válido até 12/2099", IsIcpBrasilPersonal: false, IsIcpBrasilCompany: false);

    private static BatchSignViewModel BuildVm(
        FakeBatchSigningEngine engine,
        IReadOnlyList<SigningCertificateInfo>? certificates = null,
        Func<string, bool>? isPathOpen = null,
        Func<IReadOnlyList<string>?>? pickFiles = null,
        X509Certificate2? cert = null,
        IPdfEditor? editor = null) =>
        new(
            certificates ?? (cert is null ? Array.Empty<SigningCertificateInfo>() : [RsaCertInfo(cert)]),
            isPathOpen: isPathOpen ?? (_ => false),
            pickFiles: pickFiles ?? (() => null),
            signingEngine: engine,
            editor: editor);


    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condição nunca ficou verdadeira.");
            await Task.Delay(5);
        }
    }

    // ---- AddFiles / RemoveFile -----------------------------------------------------------------------

    [Fact]
    public void AddFiles_PickerReturnsFiles_AddsAllToList()
    {
        var f1 = NewTempFile();
        var f2 = NewTempFile();
        var vm = BuildVm(new FakeBatchSigningEngine(), pickFiles: () => [f1, f2]);

        vm.AddFilesCommand.Execute(null);

        Assert.Equal([f1, f2], vm.Files);
        Assert.Null(vm.AddFilesNotice);
    }

    [Fact]
    public void AddFiles_PickerCancelled_NoOp()
    {
        var vm = BuildVm(new FakeBatchSigningEngine(), pickFiles: () => null);

        vm.AddFilesCommand.Execute(null);

        Assert.Empty(vm.Files);
    }

    [Fact] // risco do plano: arquivo aberto numa aba -- recusado da lista, com aviso pt-BR.
    public void AddFiles_PathOpenInAnotherTab_RefusedWithNotice_NotAddedToList()
    {
        var openFile = NewTempFile();
        var closedFile = NewTempFile();
        var vm = BuildVm(new FakeBatchSigningEngine(),
            isPathOpen: p => string.Equals(p, openFile, StringComparison.OrdinalIgnoreCase),
            pickFiles: () => [openFile, closedFile]);

        vm.AddFilesCommand.Execute(null);

        Assert.Equal([closedFile], vm.Files);
        Assert.NotNull(vm.AddFilesNotice);
        Assert.Contains(Path.GetFileName(openFile), vm.AddFilesNotice);
    }

    [Fact]
    public void AddFiles_AllPathsOpen_NoticeMentionsPluralCount()
    {
        var f1 = NewTempFile();
        var f2 = NewTempFile();
        var vm = BuildVm(new FakeBatchSigningEngine(), isPathOpen: _ => true, pickFiles: () => [f1, f2]);

        vm.AddFilesCommand.Execute(null);

        Assert.Empty(vm.Files);
        Assert.Contains("2", vm.AddFilesNotice);
    }

    [Fact]
    public void RemoveFile_RemovesFromList()
    {
        var f1 = NewTempFile();
        var f2 = NewTempFile();
        var vm = BuildVm(new FakeBatchSigningEngine(), pickFiles: () => [f1, f2]);
        vm.AddFilesCommand.Execute(null);

        vm.RemoveFileCommand.Execute(f1);

        Assert.Equal([f2], vm.Files);
    }

    // ---- CanStart --------------------------------------------------------------------------------------

    [Fact]
    public void CanStart_False_WhenNoFiles()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var vm = BuildVm(new FakeBatchSigningEngine(), cert: cert);
        vm.SelectedCertificate = vm.Certificates[0];

        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void CanStart_False_WhenNoCertificateSelected()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var vm = BuildVm(new FakeBatchSigningEngine(), cert: cert, pickFiles: () => [NewTempFile()]);
        vm.AddFilesCommand.Execute(null);

        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void CanStart_False_WhenSelectedCertificateIsEcc()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var vm = BuildVm(new FakeBatchSigningEngine(), certificates: [EccCertInfo(cert)], pickFiles: () => [NewTempFile()]);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];

        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void CanStart_True_WhenFilesAndRsaCertificateSelected()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var vm = BuildVm(new FakeBatchSigningEngine(), cert: cert, pickFiles: () => [NewTempFile()]);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];

        Assert.True(vm.StartCommand.CanExecute(null));
    }

    // ---- Start: progresso / resultados / não-aborta ---------------------------------------------------

    [Fact]
    public async Task Start_ProgressText_SequenceReflectsFileNOfM()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var engine = new FakeBatchSigningEngine();
        var files = new[] { NewTempFile(), NewTempFile(), NewTempFile() };
        var vm = BuildVm(engine, cert: cert, pickFiles: () => files);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];

        var progressUpdates = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BatchSignViewModel.ProgressText)) progressUpdates.Add(vm.ProgressText);
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Assinando arquivo 1 de 3…", "Assinando arquivo 2 de 3…", "Assinando arquivo 3 de 3…"],
            progressUpdates);
    }

    [Fact]
    public async Task Start_AllSucceed_ResultsHaveExactMessageAndPhaseIsDone()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var engine = new FakeBatchSigningEngine();
        var f1 = NewTempFile();
        var vm = BuildVm(engine, cert: cert, pickFiles: () => [f1]);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];

        await vm.StartCommand.ExecuteAsync(null);

        var result = Assert.Single(vm.Results);
        Assert.True(result.Succeeded);
        Assert.Equal($"{Path.GetFileName(f1)} assinado", result.Message);
        Assert.Equal(BatchSignPhase.Done, vm.Phase);
        Assert.True(File.Exists(ExpectedOutputPath(f1)));
    }

    [Fact] // contrato central do brief: erro em UM arquivo não aborta o lote -- os demais são processados.
    public async Task Start_OneFileFails_OthersStillProcessed_PartialFailureListed()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var engine = new FakeBatchSigningEngine
        {
            ThrowOnIndex = 1, // 2º arquivo (0-based) falha
            ThrowOnIndexException = new PdfSigningException("Não foi possível acessar a chave privada."),
        };
        var f1 = NewTempFile();
        var f2 = NewTempFile();
        var f3 = NewTempFile();
        var vm = BuildVm(engine, cert: cert, pickFiles: () => [f1, f2, f3]);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Results.Count); // os TRÊS foram tentados, nenhum pulado
        Assert.Equal(3, engine.Requests.Count);
        Assert.True(vm.Results[0].Succeeded);
        Assert.False(vm.Results[1].Succeeded);
        Assert.Equal($"{Path.GetFileName(f2)}: Não foi possível acessar a chave privada.", vm.Results[1].Message);
        Assert.True(vm.Results[2].Succeeded);
        Assert.True(File.Exists(ExpectedOutputPath(f1)));
        Assert.False(File.Exists(ExpectedOutputPath(f2))); // arquivo que falhou não produz saída
        Assert.True(File.Exists(ExpectedOutputPath(f3)));
        Assert.Equal(BatchSignPhase.Done, vm.Phase);
    }

    [Fact] // I1 (revisão final): mesmo mecanismo de Start_OneFileFails_OthersStillProcessed_PartialFailureListed
    // acima, nomeando especificamente a recusa DocMDP P=1 -- um arquivo certificado NO_CHANGES_PERMITTED
    // no meio do lote vira uma linha de falha com a mensagem tipada do motor; o lote NÃO aborta (o
    // catch-all de SignOneFile, contrato central do brief, já garante isso estruturalmente), os demais
    // arquivos assinam normalmente.
    public async Task Start_FileCertifiedP1_ThrowsTypedRefusal_OthersStillProcessed()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var engine = new FakeBatchSigningEngine
        {
            ThrowOnIndex = 1, // 2º arquivo (0-based) é o certificado P=1
            ThrowOnIndexException = new PdfSigningException(
                "O documento é certificado e não permite alterações (nível máximo de proteção). " +
                "Não é possível adicionar assinaturas."),
        };
        var f1 = NewTempFile();
        var f2 = NewTempFile();
        var f3 = NewTempFile();
        var vm = BuildVm(engine, cert: cert, pickFiles: () => [f1, f2, f3]);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Results.Count); // os TRÊS foram tentados, nenhum pulado -- lote não abortou
        Assert.True(vm.Results[0].Succeeded);
        Assert.False(vm.Results[1].Succeeded);
        Assert.Contains("certificado", vm.Results[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.Results[2].Succeeded);
        Assert.True(File.Exists(ExpectedOutputPath(f1)));
        Assert.False(File.Exists(ExpectedOutputPath(f2))); // arquivo recusado não produz saída
        Assert.True(File.Exists(ExpectedOutputPath(f3)));
        Assert.Equal(BatchSignPhase.Done, vm.Phase);
    }

    // ---- colisão de nome -------------------------------------------------------------------------------

    [Fact]
    public async Task Start_OutputNameCollision_UsesNumberedSuffix()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var engine = new FakeBatchSigningEngine();
        var f1 = NewTempFile();
        File.WriteAllBytes(ExpectedOutputPath(f1), [9, 9, 9]); // já existe "(assinado).pdf" -- força colisão
        _tempFilesToDelete.Add(CollisionOutputPath(f1, 2));
        var vm = BuildVm(engine, cert: cert, pickFiles: () => [f1]);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.Results[0].Succeeded);
        Assert.True(File.Exists(CollisionOutputPath(f1, 2)));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(ExpectedOutputPath(f1))); // o arquivo colidido NÃO foi sobrescrito
    }

    // ---- carimbo: canto inferior direito, clampado -----------------------------------------------------

    [Fact]
    public async Task Start_WithStamp_LastPageBottomRightRect()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var engine = new FakeBatchSigningEngine();
        var f1 = NewTempFixtureCopy(); // fixture-a4.pdf: 1 página, 595x842pt
        var vm = BuildVm(engine, cert: cert, pickFiles: () => [f1]);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];
        vm.PlaceStamp = true;

        await vm.StartCommand.ExecuteAsync(null);

        var stamp = engine.Requests[0].Stamp;
        Assert.NotNull(stamp);
        Assert.Equal(0, stamp!.PageIndex); // única página do documento
        Assert.Equal(395, stamp.Rect.LeftPt, precision: 3);   // 595 - 20 (margem) - 180 (largura)
        Assert.Equal(20, stamp.Rect.BottomPt, precision: 3);  // margem
        Assert.Equal(575, stamp.Rect.RightPt, precision: 3);  // 595 - 20 (margem)
        Assert.Equal(80, stamp.Rect.TopPt, precision: 3);     // 20 + 60 (altura)
        Assert.Null(engine.Requests[0].CertificationLevel); // v1: nunca oferece DocMDP em lote
    }

    // ---- carimbo em página GIRADA: transformação de frame (achado CRÍTICO da revisão) -------------------
    //
    // `ComputeStampRect`/`TransformVisualRectToContentFrame` são `internal static` (funções PURAS, sem
    // I/O nenhum) especificamente pra permitir estes testes RÁPIDOS e DETERMINÍSTICOS com números
    // EXATOS — complementares (não substitutos) do oráculo pixel-a-pixel obrigatório mais abaixo
    // (`Start_Integration_RotatedLastPage_...`), que prova a transformação ponta-a-ponta contra o motor
    // e o renderer REAIS. Página de referência: A4 NÃO-rotacionado 595x842pt (mesma fixture de
    // `Start_WithStamp_LastPageBottomRightRect` acima). Valores calculados à mão pela álgebra documentada
    // no XML doc de `TransformVisualRectToContentFrame` (mesma álgebra em task-5-report.md).

    [Fact] // rotação 0 = identidade -- MESMOS números de Start_WithStamp_LastPageBottomRightRect acima.
    public void ComputeStampRect_Rotation0_Identity()
    {
        var rect = BatchSignViewModel.ComputeStampRect(0, displayWidthPt: 595, displayHeightPt: 842);
        Assert.Equal(395, rect.LeftPt, precision: 3);
        Assert.Equal(20, rect.BottomPt, precision: 3);
        Assert.Equal(575, rect.RightPt, precision: 3);
        Assert.Equal(80, rect.TopPt, precision: 3);
    }

    [Fact] // rotação 90: página de EXIBIÇÃO é 842x595 (largura/altura trocadas -- GetPageSize já rotacionado).
    // Retângulo visual (canto inferior-direito, margem 20, 180x60): dx=[642,822], dy=[20,80].
    // Transformado pro frame de conteúdo (Wu=Hd=595 -- ver derivação): Left=Hd-dyTop=595-80=515,
    // Right=Hd-dyBottom=595-20=575, Bottom=dxLeft=642, Top=dxRight=822.
    public void ComputeStampRect_Rotation90_TransformsIntoContentFrame()
    {
        var rect = BatchSignViewModel.ComputeStampRect(90, displayWidthPt: 842, displayHeightPt: 595);
        Assert.Equal(515, rect.LeftPt, precision: 3);
        Assert.Equal(642, rect.BottomPt, precision: 3);
        Assert.Equal(575, rect.RightPt, precision: 3);
        Assert.Equal(822, rect.TopPt, precision: 3);
        // sanity: dentro da página de CONTEÚDO real (595x842) -- a versão COM BUG (achado do revisor)
        // produzia X 642-822, inteiramente FORA de uma página de 595pt de largura.
        Assert.True(rect.RightPt <= 595, $"carimbo fora da página de conteúdo: RightPt={rect.RightPt}");
    }

    [Fact] // rotação 180: página de EXIBIÇÃO continua 595x842 (sem troca). Retângulo visual dx=[395,575],
    // dy=[20,80]. Transformado: Left=Wd-dxRight=595-575=20, Right=Wd-dxLeft=595-395=200,
    // Bottom=Hd-dyTop=842-80=762, Top=Hd-dyBottom=842-20=822 -- canto INFERIOR-DIREITO visual vira
    // SUPERIOR-ESQUERDO no conteúdo (a página inteira virou de cabeça pra baixo).
    public void ComputeStampRect_Rotation180_TransformsIntoContentFrame()
    {
        var rect = BatchSignViewModel.ComputeStampRect(180, displayWidthPt: 595, displayHeightPt: 842);
        Assert.Equal(20, rect.LeftPt, precision: 3);
        Assert.Equal(762, rect.BottomPt, precision: 3);
        Assert.Equal(200, rect.RightPt, precision: 3);
        Assert.Equal(822, rect.TopPt, precision: 3);
    }

    [Fact] // rotação 270: página de EXIBIÇÃO é 842x595. Retângulo visual dx=[642,822], dy=[20,80].
    // Transformado (Wd=842): Left=dyBottom=20, Right=dyTop=80, Bottom=Wd-dxRight=842-822=20,
    // Top=Wd-dxLeft=842-642=200.
    public void ComputeStampRect_Rotation270_TransformsIntoContentFrame()
    {
        var rect = BatchSignViewModel.ComputeStampRect(270, displayWidthPt: 842, displayHeightPt: 595);
        Assert.Equal(20, rect.LeftPt, precision: 3);
        Assert.Equal(20, rect.BottomPt, precision: 3);
        Assert.Equal(80, rect.RightPt, precision: 3);
        Assert.Equal(200, rect.TopPt, precision: 3);
    }

    [Fact]
    public void ComputeStampRect_UnknownRotation_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BatchSignViewModel.ComputeStampRect(45, 595, 842));
    }

    [Fact] // WIRING (não só a álgebra pura acima): `SignOneFile` realmente consulta
    // `_editor.GetPageRotations` e alimenta CEGAMENTE o resultado na transformação -- prova com um
    // FakePdfEditor declarando rotação 90 sobre uma fixture NÃO-rotacionada em disco (fixture-a4.pdf).
    // A GEOMETRIA (`renderer.GetPageSize`) vem do PDFium REAL lendo o arquivo de verdade -- como o
    // arquivo NÃO está fisicamente rotacionado, `GetPageSize` continua devolvendo 595x842 (a fixture),
    // NÃO o par trocado; é exatamente essa independência (rotação vem do editor, geometria vem do
    // renderer, `SignOneFile` nunca reconcilia os dois) que este teste prova estar fiada corretamente --
    // o `expected` usa a MESMA combinação (rotação=90 sobre 595x842) que `SignOneFile` efetivamente vê.
    public async Task Start_WithStamp_EditorReportsRotation90_UsesTransformedRect()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var engine = new FakeBatchSigningEngine();
        var fakeEditor = new FakePdfEditor { PageRotationsResult = new[] { 90 } };
        var f1 = NewTempFixtureCopy(); // fixture-a4.pdf: 1 página, 595x842pt (mas o EDITOR FAKE diz 90)
        var vm = BuildVm(engine, cert: cert, pickFiles: () => [f1], editor: fakeEditor);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];
        vm.PlaceStamp = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, fakeEditor.GetPageRotationsCallCount);
        var stamp = engine.Requests[0].Stamp!;
        // displaySize vem do renderer REAL sobre o arquivo REAL (595x842, sem rotação física) -- só a
        // rotação em si (90) vem do fake, exatamente o que SignOneFile efetivamente consome.
        var expected = BatchSignViewModel.ComputeStampRect(90, displayWidthPt: 595, displayHeightPt: 842);
        Assert.Equal(expected.LeftPt, stamp.Rect.LeftPt, precision: 3);
        Assert.Equal(expected.BottomPt, stamp.Rect.BottomPt, precision: 3);
        Assert.Equal(expected.RightPt, stamp.Rect.RightPt, precision: 3);
        Assert.Equal(expected.TopPt, stamp.Rect.TopPt, precision: 3);
    }

    // ---- Minor: guarda de PDF sem páginas -----------------------------------------------------------

    /// PDF sintaticamente válido, mas com `/Pages /Kids []` (0 páginas) — confirmado empiricamente
    /// (sondagem ao vivo) que Docnet/PDFium aceita construir o `PdfDocumentRenderer` sobre isto
    /// (`PageCount == 0`), NÃO é um caso hipotético descartável: sem a guarda explícita, `SignOneFile`
    /// chamaria `GetPageSize(-1)`, que lançaria de dentro do Docnet com uma mensagem NÃO-tipada.
    private static byte[] ZeroPagePdfBytes()
    {
        string header = "%PDF-1.4\n";
        string obj1 = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
        string obj2 = "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n";
        int offset1 = header.Length;
        int offset2 = offset1 + obj1.Length;
        int xrefOffset = offset2 + obj2.Length;
        string xref = "xref\n0 3\n0000000000 65535 f \n" +
            $"{offset1:D10} 00000 n \n{offset2:D10} 00000 n \n";
        string trailer = $"trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF";
        return System.Text.Encoding.ASCII.GetBytes(header + obj1 + obj2 + xref + trailer);
    }

    [Fact]
    public async Task Start_WithStamp_ZeroPagePdf_FailsWithExplicitNotice_EngineNeverCalled()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var engine = new FakeBatchSigningEngine();
        var f1 = NewTempFile(ZeroPagePdfBytes());
        var vm = BuildVm(engine, cert: cert, pickFiles: () => [f1]);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];
        vm.PlaceStamp = true;

        await vm.StartCommand.ExecuteAsync(null);

        var result = Assert.Single(vm.Results);
        Assert.False(result.Succeeded);
        Assert.Equal($"{Path.GetFileName(f1)}: arquivo sem páginas.", result.Message);
        Assert.Empty(engine.Requests); // motor NUNCA alcançado -- recusado ANTES de assinar
        Assert.False(File.Exists(ExpectedOutputPath(f1)));
    }

    // ---- cancelamento ------------------------------------------------------------------------------------

    [Fact]
    public void CanCancel_FalseBeforeStart()
    {
        var vm = BuildVm(new FakeBatchSigningEngine());
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact] // "para ANTES do próximo arquivo" -- o arquivo em voo TERMINA (nunca é interrompido no meio).
    public async Task Start_Cancel_StopsBeforeNextFile_CurrentFileCompletesAndIsWritten()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var engine = new FakeBatchSigningEngine { Gate = new TaskCompletionSource<bool>() };
        var f1 = NewTempFile();
        var f2 = NewTempFile();
        var f3 = NewTempFile();
        var vm = BuildVm(engine, cert: cert, pickFiles: () => [f1, f2, f3]);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];

        var startTask = vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => engine.Requests.Count == 1); // arquivo 1 já entrou no motor (preso no Gate)
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        engine.Gate!.SetResult(true); // libera o arquivo 1 -- ele TERMINA de qualquer jeito

        await startTask;

        Assert.Single(engine.Requests); // arquivos 2 e 3 NUNCA alcançaram o motor
        Assert.True(vm.WasCancelled);
        Assert.Equal(BatchSignPhase.Done, vm.Phase);
        Assert.Single(vm.Results);
        Assert.True(File.Exists(ExpectedOutputPath(f1))); // arquivo 1 foi gravado normalmente (não interrompido)
        Assert.False(File.Exists(ExpectedOutputPath(f2)));
        Assert.False(File.Exists(ExpectedOutputPath(f3)));
        Assert.False(vm.CancelCommand.CanExecute(null)); // fase Done -- cancelar não faz mais sentido
    }

    // ---- integração: motor REAL + certificado efêmero REAL ---------------------------------------------

    [Fact] // ponta a ponta: motor de PRODUÇÃO (SigningEngineFactory.Create()), 2 arquivos, certificado RSA
    // efêmero (NUNCA um certificado real do usuário/repositório) -- saídas válidas via ReadSignatures,
    // originais byte-idênticos ao conteúdo copiado (nunca tocados).
    public async Task Start_Integration_RealEngineSignsTwoFiles_OutputsValidSignatures_OriginalsUntouched()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var realEngine = SigningEngineFactory.Create();
        var f1 = NewTempFixtureCopy();
        var f2 = NewTempFixtureCopy();
        var originalBytes = Fixtures.A4();
        var vm = new BatchSignViewModel(
            [RsaCertInfo(cert)],
            isPathOpen: _ => false,
            pickFiles: () => [f1, f2],
            signingEngine: realEngine);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];
        vm.PlaceStamp = true;
        vm.Reason = "Aprovação";
        vm.Location = "Escritório";

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Results.Count);
        Assert.All(vm.Results, r => Assert.True(r.Succeeded));

        foreach (var (original, output) in new[] { (f1, ExpectedOutputPath(f1)), (f2, ExpectedOutputPath(f2)) })
        {
            Assert.True(File.Exists(output));
            var signatures = realEngine.ReadSignatures(File.ReadAllBytes(output));
            var sig = Assert.Single(signatures);
            Assert.True(sig.IntegrityValid);
            Assert.Equal(DocMdpLevel.None, sig.Certification); // v1: nunca DocMDP em lote
            Assert.Equal(originalBytes, File.ReadAllBytes(original)); // original NUNCA sobrescrito
        }
    }

    // ---- ORÁCULO OBRIGATÓRIO da revisão: carimbo VISÍVEL no canto inferior-direito EXIBIDO, pras 4
    // rotações, ponta-a-ponta (motor REAL + editor REAL + PDFium REAL) -- prova a transformação de
    // frame de verdade, não só a álgebra isolada acima. `RenderPage` compõe `/Rotate` (renderiza o que o
    // usuário VÊ, mesma garantia de RenderPage_SignatureStampAnnotation_IsPainted em
    // mPdf.Rendering.Tests) -- contar pixels não-brancos na região visual esperada É a prova de que o
    // carimbo caiu no lugar CERTO, em qualquer rotação.
    // ACHADO DA PRÓPRIA MUTAÇÃO (autoavaliação): a 1ª versão deste teste contava pixels PINTADOS em
    // isolamento (exemplar `RenderPage_SignatureStampAnnotation_IsPainted`) -- mutação plantada
    // (`rotation = 0` fixo, reintroduzindo o bug) mostrou que rotação=180 continuava passando mesmo com
    // o carimbo GEOMETRICAMENTE errado: o conteúdo PRÉ-EXISTENTE da fixture que cai no canto
    // inferior-direito EXIBIDO de uma página 180°-rotacionada (texto do topo da página original, que o
    // giro de 180° leva pro canto oposto) já excede sozinho o limiar de 100 pixels, mascarando a
    // ausência do carimbo. Trocado pro exemplar CORRETO pra este caso (`Sign_WithVisibleStamp_
    // PaintsOnlyInsideStampRegion`, mPdf.Signing.Tests): compara ANTES (fonte rotacionada, sem
    // assinatura) x DEPOIS (saída assinada) e conta pixels que DIFEREM na região -- isola o carimbo do
    // conteúdo pré-existente, não pode ser mascarado por ele.
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Start_Integration_RotatedLastPage_StampPaintedAtVisualBottomRight(int rotationDegrees)
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var realEngine = SigningEngineFactory.Create();
        var realEditor = PdfEditorFactory.Create();

        byte[] source = Fixtures.A4(); // 1 página, 595x842pt, SEM rotação
        if (rotationDegrees != 0)
            source = realEditor.RotatePages(source, new[] { 0 }, rotationDegrees);
        var path = NewTempFile(source);

        using var rendererBefore = new PdfDocumentRenderer(source); // fonte rotacionada, AINDA sem assinar
        var displaySize = rendererBefore.GetPageSize(0); // frame de EXIBIÇÃO (o que o usuário vê)
        var pageBefore = rendererBefore.RenderPage(0, 1.0); // PDFium COMPÕE /Rotate ao renderizar

        var vm = new BatchSignViewModel(
            [RsaCertInfo(cert)],
            isPathOpen: _ => false,
            pickFiles: () => [path],
            signingEngine: realEngine,
            editor: realEditor);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];
        vm.PlaceStamp = true;

        await vm.StartCommand.ExecuteAsync(null);

        var result = Assert.Single(vm.Results);
        Assert.True(result.Succeeded, $"[rotação {rotationDegrees}] {result.Message}");

        var outputBytes = File.ReadAllBytes(ExpectedOutputPath(path));
        using var rendererAfter = new PdfDocumentRenderer(outputBytes); // `using` direto: teste não tem
        // concorrência nenhuma pra justificar a fila serial de PendingDisposals (só a produção precisa
        // dela — ver doc XML de BatchSignViewModel.SignOneFile).
        var pageAfter = rendererAfter.RenderPage(0, 1.0);
        Assert.Equal(pageBefore.WidthPx, pageAfter.WidthPx);
        Assert.Equal(pageBefore.HeightPx, pageAfter.HeightPx);

        // MESMA margem/tamanho do carimbo do lote (StampMarginPt=20, StampWidthPt=180,
        // StampHeightPt=60) -- região VISUAL esperada, no frame de EXIBIÇÃO (displaySize), não no de
        // conteúdo (é isso que RenderPage mostra).
        const double margin = 20, w = 180, h = 60;
        int xMin = (int)(displaySize.WidthPt - margin - w);
        int xMax = (int)(displaySize.WidthPt - margin);
        int yMinPt = (int)margin;
        int yMaxPt = (int)(margin + h);

        int diffInRegion = CountDifferingPixelsInRegion(pageBefore, pageAfter, pageBefore.HeightPx, xMin, xMax, yMinPt, yMaxPt);
        // Medido ao vivo (task-5-report.md): pixels DIFERENTES (não só "pintados") na região visual
        // esperada — rotação 0=2694, 90=2712, 180=2712, 270=2712 — limiar 100 (mesmo padrão de
        // Sign_WithVisibleStamp_PaintsOnlyInsideStampRegion) folgado bem abaixo do valor real. As 4
        // rotações batem PERTO uma da outra porque o diff isola o carimbo em si (mesmo tamanho, mesma
        // posição VISUAL em todas) do conteúdo pré-existente — diferente da 1ª versão deste teste
        // ("pintados" em isolamento), que rotação=180 sozinha já mascarava (ver comentário acima).
        Assert.True(diffInRegion > 100,
            $"[rotação {rotationDegrees}] carimbo não visível no canto inferior-direito EXIBIDO: só {diffInRegion} pixels diferentes na região");
    }

    /// EXEMPLAR: `PadesSigningEngineTests.Sign_WithVisibleStamp_PaintsOnlyInsideStampRegion`
    /// (mPdf.Signing.Tests) — conta pixels (RGB, ignora alfa) que DIFEREM entre um render ANTES/DEPOIS
    /// dentro de uma região em pontos PDF (convertida pra pixels de imagem, origem topo, escala 1.0).
    /// Mais robusto que `CountPaintedPixelsInRegion` (que só olha o DEPOIS): isola o carimbo do
    /// conteúdo PRÉ-EXISTENTE da página, que pode por si só exceder o limiar (achado real desta
    /// revisão — ver comentário do teste acima).
    private static int CountDifferingPixelsInRegion(
        RenderedPage before, RenderedPage after, int heightPx, int xMin, int xMax, int yMinPt, int yMaxPt)
    {
        int diff = 0;
        int w = before.WidthPx;
        for (int y = heightPx - yMaxPt; y < heightPx - yMinPt; y++)
            for (int x = xMin; x < xMax; x++)
            {
                int i = (y * w + x) * 4;
                if (before.Bgra[i] != after.Bgra[i] || before.Bgra[i + 1] != after.Bgra[i + 1] || before.Bgra[i + 2] != after.Bgra[i + 2])
                    diff++;
            }
        return diff;
    }

    [Fact] // Minor (revisão): entrada JÁ CERTIFICADA (DocMDP FormsAndSignatures, simulando um arquivo
    // que chegou ao lote já assinado/certificado por outro fluxo, ex. SignDialog Task 3) -- o motor NÃO
    // pode recusar porque o lote NUNCA tenta aplicar um DocMDP novo (CertificationLevel sempre null,
    // ver doc XML da classe); prova ponta-a-ponta com o motor REAL que a 2ª assinatura (aprovação, do
    // lote) entra por cima sem erro, e as DUAS ficam íntegras.
    public async Task Start_Integration_AlreadyCertifiedInput_SignsIncrementally_BothSignaturesValid()
    {
        using var cert1 = SignCommandTests.CreateEphemeralRsaCertificate("Certificador");
        using var cert2 = SignCommandTests.CreateEphemeralRsaCertificate("Aprovador do lote");
        var realEngine = SigningEngineFactory.Create();

        byte[] certified = realEngine.Sign(new SignRequest(
            Fixtures.A4(), cert1, "Certificação", null, null, DocMdpLevel.FormsAndSignatures));
        var path = NewTempFile(certified);

        var vm = new BatchSignViewModel(
            [RsaCertInfo(cert2)],
            isPathOpen: _ => false,
            pickFiles: () => [path],
            signingEngine: realEngine);
        vm.AddFilesCommand.Execute(null);
        vm.SelectedCertificate = vm.Certificates[0];

        await vm.StartCommand.ExecuteAsync(null);

        var result = Assert.Single(vm.Results);
        Assert.True(result.Succeeded, result.Message); // motor NÃO recusa -- CertificationLevel sempre null no lote

        var signatures = realEngine.ReadSignatures(File.ReadAllBytes(ExpectedOutputPath(path)));
        Assert.Equal(2, signatures.Count);
        Assert.All(signatures, s => Assert.True(s.IntegrityValid)); // as DUAS íntegras
        Assert.Equal(DocMdpLevel.FormsAndSignatures, signatures[0].Certification); // 1ª: certificação prévia
        Assert.Equal(DocMdpLevel.None, signatures[1].Certification); // 2ª: aprovação do lote
    }
}
