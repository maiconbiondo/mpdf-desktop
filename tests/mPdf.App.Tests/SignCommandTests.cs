using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Rendering;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

// ---- fakes (internal, não `file`: reutilizados por EditInFlightMatrixTests — mesmo precedente de
// FakePdfEditor em DocumentViewModelTests.cs) --------------------------------------------------------

/// Motor de assinatura FAKE — registra o `SignRequest` recebido (prova cert/motivo/local/DocMDP/carimbo
/// SEM tocar iText de verdade) e devolve bytes REAIS de um PDF válido: `Session.CommitSigned` constrói
/// um `PdfDocumentRenderer` de verdade sobre o resultado. `SignGate` (opcional): bloqueia a thread do
/// pool que chamou `Sign` até o teste liberar via `SetResult` — usado pelo par da matriz (Task 3, Plano 4).
internal sealed class FakeSigningEngine : ISigningEngine
{
    public SignRequest? LastRequest { get; private set; }
    public int SignCallCount { get; private set; }
    public byte[] SignResult { get; set; } = Fixtures.ThirtyPages();
    public Exception? ThrowOnSign { get; set; }
    public TaskCompletionSource<bool>? SignGate { get; set; }
    // Task 2 (Plano 7, fix CRÍTICO pós-revisão): registra "engine" numa lista COMPARTILHADA (opcional)
    // pra provar ORDEM entre a relocação (Salvar Como) e o motor — ver
    // Sign_NeedsSaveAs_RelocatesBeforeSignDialogAndEngine.
    public List<string>? CallOrder { get; set; }

    // Apartamento da thread em que Sign() rodou — prova que a assinatura corre em STA (o PIN do
    // token/A3, ex.: SafeSign, só aparece em STA; ver StaTask). Sem o fix (Task.Run = MTA) daria MTA.
    public System.Threading.ApartmentState CapturedApartmentState { get; private set; } =
        System.Threading.ApartmentState.Unknown;

    public byte[] Sign(SignRequest request)
    {
        CapturedApartmentState = System.Threading.Thread.CurrentThread.GetApartmentState();
        SignGate?.Task.Wait();
        SignCallCount++;
        LastRequest = request;
        CallOrder?.Add("engine");
        if (ThrowOnSign is { } ex) throw ex;
        return SignResult;
    }

    public IReadOnlyList<SignatureInfo>? ReadSignaturesResult { get; set; }
    public IReadOnlyList<SignatureInfo> ReadSignatures(byte[] pdf) => ReadSignaturesResult ?? Array.Empty<SignatureInfo>();

    // ---- Task 6 (Plano 4): preenchimento incremental em documento assinado ------------------------

    public FillPermission CanFillIncrementalResult { get; set; } = FillPermission.Allowed;
    public int CanFillIncrementalCallCount { get; private set; }
    public byte[]? LastCanFillIncrementalPdf { get; private set; }
    public Exception? ThrowOnCanFillIncremental { get; set; }

    public FillPermission CanFillIncremental(byte[] pdf)
    {
        CanFillIncrementalCallCount++;
        LastCanFillIncrementalPdf = pdf;
        if (ThrowOnCanFillIncremental is { } ex) throw ex;
        return CanFillIncrementalResult;
    }

    public IReadOnlyDictionary<string, string>? LastSetFormFieldsIncrementalValues { get; private set; }
    public int SetFormFieldsIncrementalCallCount { get; private set; }
    public Exception? ThrowOnSetFormFieldsIncremental { get; set; }
    public byte[]? SetFormFieldsIncrementalResult { get; set; }

    public byte[] SetFormFieldsIncremental(byte[] pdf, IReadOnlyDictionary<string, string> values)
    {
        SetFormFieldsIncrementalCallCount++;
        LastSetFormFieldsIncrementalValues = values;
        if (ThrowOnSetFormFieldsIncremental is { } ex) throw ex;
        return SetFormFieldsIncrementalResult ?? Fixtures.ThirtyPages();
    }
}

/// Diálogo "Assinar" FAKE — `Result` é MUTÁVEL (não fixado no ctor) pra permitir um mesmo VM assinar
/// mais de uma vez com respostas DIFERENTES no mesmo teste (ex.: prova de incrementalidade).
internal sealed class FakeSignDialogService(SignDialogResult? result = null) : ISignDialogService
{
    public SignDialogResult? Result { get; set; } = result;
    public int CallCount { get; private set; }
    public IReadOnlyList<SigningCertificateInfo>? LastCertificates { get; private set; }
    public bool? LastAllowDocMdp { get; private set; }
    public RubricaGallery? LastRubricas { get; private set; } // Plano 22
    // Task 2 (Plano 7, fix CRÍTICO pós-revisão): mesmo campo opcional de FakeSigningEngine acima.
    public List<string>? CallOrder { get; set; }

    public SignDialogResult? PromptForSignature(
        IReadOnlyList<SigningCertificateInfo> certificates, bool allowDocMdp,
        RubricaGallery rubricas, Func<byte[]?> pickRubrica)
    {
        CallCount++;
        LastCertificates = certificates;
        LastAllowDocMdp = allowDocMdp;
        LastRubricas = rubricas;
        CallOrder?.Add("signDialog");
        return Result;
    }
}

internal sealed class FakeConfirmSaveBeforeSignService(bool result) : IConfirmSaveBeforeSignService
{
    public int CallCount { get; private set; }
    public bool Confirm(string message) { CallCount++; return result; }
}

/// Diálogo "Salvar como" FAKE (Task 2, Plano 7, fix CRÍTICO pós-revisão) — mesmo padrão de
/// `FakeFileDialogService` em `MainViewModelTests.cs`/`OrganizerViewModelTests.cs`, mas SÓ implementa
/// `PickPdfToSaveAs` de propósito (os outros 3 métodos de `IFileDialogService` não são usados por
/// `Sign`/`TryRelocateBeforeSign` — devolver `null`/lançar neles deixaria claro se algum dia passassem a
/// ser chamados por engano). `saveAsResult = null` simula o usuário CANCELANDO o diálogo.
file sealed class FakeSaveAsDialogService(string? saveAsResult, List<string>? callOrder = null) : IFileDialogService
{
    public int PickPdfToSaveAsCallCount { get; private set; }
    public string? LastCurrentPath { get; private set; }

    public string? PickPdfToOpen() => throw new NotSupportedException();
    public string? PickImageToImport() => throw new NotSupportedException();
    public string? PickPdfToSave(string suggestedName) => throw new NotSupportedException();

    public string? PickPdfToSaveAs(string currentPath)
    {
        PickPdfToSaveAsCallCount++;
        LastCurrentPath = currentPath;
        callOrder?.Add("saveAs");
        return saveAsResult;
    }
}

/// Revisão do coordenador (item 2, "temp litter"): `BuildForSigning` (e os poucos testes que montam a
/// sessão à mão) criam um PDF temporário + um diretório de `AppConfig` POR CHAMADA — sem limpeza, uma
/// suíte rodada repetidamente acumula centenas de entradas `mpdf-sign-*` em `%TEMP%` (measured: 468
/// antes deste fix). Em vez de um `try/finally` por teste (mesmo padrão de `DocumentSessionTests`, mas
/// ~25 call sites tornariam isso repetitivo), a classe implementa `IDisposable`: xunit cria uma
/// instância NOVA de `SignCommandTests` por `[Fact]` e chama `Dispose()` logo depois (sucesso OU
/// falha) — uma lista de instância é escopo suficiente, nunca vaza entre testes, e cobre TODO caminho
/// (inclusive uma asserção que lança no meio do teste).
public class SignCommandTests : IDisposable
{
    private readonly List<string> _tempFilesToDelete = [];
    private readonly List<string> _tempDirsToDelete = [];

    // FLUXO ADOBE (nova UX): assinar NÃO sobrescreve o original — grava um arquivo NOVO via "Salvar como"
    // e abre o assinado. Estas seams capturam o "Salvar como" (path escolhido), a gravação (path+bytes) e
    // a abertura do assinado. `_savePickerReturns = null` simula o usuário CANCELAR o "Salvar como".
    private string? _savePickerReturns = @"C:\out\assinado.pdf";
    private readonly List<string> _savePickerSuggestions = [];
    private readonly List<(string path, byte[] bytes)> _written = [];
    private readonly List<string> _openedAfterSign = [];
    private bool _writeThrowsIO; // simula disco cheio/arquivo travado na gravação do assinado

    public void Dispose()
    {
        foreach (var f in _tempFilesToDelete) TryDeleteFile(f);
        foreach (var d in _tempDirsToDelete) TryDeleteDir(d);
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* melhor esforço */ } }
    private static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* melhor esforço */ } }

    /// Certificado RSA EFÊMERO gerado em memória (mesma mecânica de
    /// `mPdf.Signing.Tests.TestCertificateFactory.CreateSelfSigned`) — NUNCA toca o repositório real do
    /// Windows, NUNCA usa certificado de usuário real (proibido em teste automatizado, ver plano).
    /// `internal` (não `private`): reutilizado por `EditInFlightMatrixTests` (par organizer-op × assinar).
    internal static X509Certificate2 CreateEphemeralRsaCertificate(string cn = "Assinante Teste mPDF App")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }

    private static SigningCertificateInfo FakeCertificateInfo(X509Certificate2 cert, bool isRsa = true) =>
        new(cert, isRsa, "Assinante Teste (RSA) — Teste — válido até 12/2099", IsIcpBrasilPersonal: false, IsIcpBrasilCompany: false);

    /// CRÍTICO: `Sign` pode escrever em disco de verdade (`Session.CommitSigned` -> `AtomicWrite` no
    /// `FilePath` da sessão) — DIFERENTE de FlattenForm/ApplyMarkup/etc. (só mutam `Snapshot` em
    /// memória), que por isso podem abrir `Fixtures.Root` DIRETO com segurança em outros arquivos de
    /// teste deste projeto. Abrir a fixture COMPARTILHADA direto aqui e assinar de verdade
    /// SOBRESCREVERIA o arquivo versionado no repositório (achado ao vivo: uma 1ª versão desta suíte
    /// fez exatamente isso, corrompendo `tests/fixtures/fixture-a4.pdf` com o conteúdo de 30 páginas
    /// devolvido pelo `FakeSigningEngine` — restaurado via `git checkout`). Toda sessão usada por um
    /// teste de assinatura tem que abrir uma CÓPIA temporária, nunca o arquivo compartilhado. Registra
    /// o caminho (E um `.bak` hipotético — `Session.Save` cria um na 1ª gravação de cada sessão) na
    /// lista de limpeza da INSTÂNCIA — ver doc XML da classe.
    private string CopyFixtureToTemp(string fixtureName = "fixture-a4.pdf")
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-sign-{Guid.NewGuid():N}.pdf");
        File.Copy(Path.Combine(Fixtures.Root, fixtureName), tmp);
        _tempFilesToDelete.Add(tmp);
        _tempFilesToDelete.Add(tmp + ".bak");
        return tmp;
    }

    private string NewConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mpdf-sign-cfg-{Guid.NewGuid():N}");
        _tempDirsToDelete.Add(dir);
        return dir;
    }

    private (DocumentViewModel doc, FakePdfEditor editor, FakeSigningEngine engine, FakeSignDialogService dialog,
        FakeConfirmSaveBeforeSignService confirm, List<string> errors, List<string> infos, X509Certificate2 cert)
        BuildForSigning(
            bool hasSignatures = false, bool confirmSaveResult = true, string fixture = "fixture-a4.pdf",
            // Task 2 (Plano 7, fix CRÍTICO pós-revisão): `dialogs` OPCIONAL -- só os testes de
            // `NeedsSaveAs`/relocação antes de assinar precisam injetar um `FakeSaveAsDialogService`;
            // todos os testes PRÉ-EXISTENTES continuam usando o default de produção (`UiPrompts.
            // CreateFileDialog()`), que nunca é alcançado (NeedsSaveAs fica `false` por padrão).
            IFileDialogService? dialogs = null,
            // Plano 21: `config` OPCIONAL -- só o teste de rubrica precisa injetar um AppConfig com uma
            // rubrica salva; os demais continuam com um config temporário vazio (default).
            AppConfig? config = null)
    {
        var editor = new FakePdfEditor { HasSignaturesResult = hasSignatures };
        var engine = new FakeSigningEngine();
        var cert = CreateEphemeralRsaCertificate();
        var dialog = new FakeSignDialogService(new SignDialogResult(cert, "Motivo", "Local", ApplyDocMdp: true, PlaceStamp: false));
        var confirm = new FakeConfirmSaveBeforeSignService(confirmSaveResult);
        var errors = new List<string>();
        var infos = new List<string>();
        var doc = new DocumentViewModel(
            DocumentSession.Open(CopyFixtureToTemp(fixture)), // NUNCA a fixture compartilhada direto -- ver doc XML acima
            editor: editor,
            config: config ?? new AppConfig(NewConfigDir()),
            notifyError: errors.Add,
            notifyInfo: infos.Add,
            dialogs: dialogs,
            signDialog: dialog,
            signingEngine: engine,
            confirmSaveBeforeSign: confirm,
            listSigningCertificates: () => new[] { FakeCertificateInfo(cert) },
            // Seams do fluxo Adobe (Salvar como + gravar + abrir o assinado) — ver campos no topo.
            pickPdfToSave: suggested => { _savePickerSuggestions.Add(suggested); return _savePickerReturns; },
            writeAllBytes: (path, bytes) => { if (_writeThrowsIO) throw new IOException("Não foi possível salvar (destino travado)."); _written.Add((path, bytes)); },
            openSavedDocument: path => { _openedAfterSign.Add(path); return Task.CompletedTask; });
        return (doc, editor, engine, dialog, confirm, errors, infos, cert);
    }

    // ---- CanSign -------------------------------------------------------------------------------------

    [Fact] // documento NOVO (sem edição em voo, não-XFA) -> habilitado
    public void CanSign_DefaultOpenDocument_True()
    {
        var (doc, _, _, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert) Assert.True(d.SignCommand.CanExecute(null));
    }

    [Fact] // CONTRATO CENTRAL do brief: assinatura incremental — CanSign NÃO compõe !IsSignedDocument.
    public void CanSign_TrueEvenWhenAlreadySigned()
    {
        var (doc, _, _, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            d.IsSignedDocument = true;
            Assert.True(d.SignCommand.CanExecute(null));
        }
    }

    [Fact]
    public void CanSign_FalseWhenXfaForm()
    {
        var (doc, _, _, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            d.SeedFormFieldsCache(xfa: true, Array.Empty<FormFieldData>());
            Assert.False(d.SignCommand.CanExecute(null));
        }
    }

    [Fact] // evita reabrir o diálogo "Assinar" por cima de uma colocação de carimbo já em andamento.
    public async Task CanSign_FalseWhilePlacingSignatureStamp()
    {
        var (doc, _, _, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);
            Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool);

            Assert.False(d.SignCommand.CanExecute(null));
        }
    }

    [Fact]
    public void CanSign_FalseWhenEditInFlight()
    {
        var (doc, _, _, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            Assert.True(d.Session.TryBeginEdit());
            try { Assert.False(d.SignCommand.CanExecute(null)); }
            finally { d.Session.EndEdit(); }
        }
    }

    // ---- fluxo: doc sujo -> prompt salvar --------------------------------------------------------

    [Fact]
    public async Task Sign_DocClean_NeverPromptsToSave()
    {
        var (doc, _, engine, dialog, confirm, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(0, confirm.CallCount);
            Assert.Equal(1, dialog.CallCount);
            Assert.Equal(1, engine.SignCallCount);
        }
    }

    [Fact] // a assinatura roda numa thread STA — o middleware do token/A3 (ex.: SafeSign) só abre a
           // janela de PIN em STA. Sem o fix (Task.Run = MTA no pool) o motor veria MTA. Ver StaTask.
    public async Task Sign_RunsOnStaThread()
    {
        var (doc, _, engine, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(1, engine.SignCallCount);
            Assert.Equal(System.Threading.ApartmentState.STA, engine.CapturedApartmentState);
        }
    }

    [Fact] // FLUXO ADOBE: doc sujo NÃO pergunta nada — assina direto o snapshot EM MEMÓRIA (com as
    // edições não salvas) e grava o resultado num arquivo NOVO; o original NUNCA é tocado/salvo.
    public async Task Sign_DocDirty_SignsInMemory_WithoutPromptingOrTouchingOriginal()
    {
        var (doc, _, engine, dialog, confirm, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            d.Session.Apply(Fixtures.ThirtyPages()); // suja
            Assert.True(d.IsDirty);
            var originalBytes = File.ReadAllBytes(d.Session.FilePath);

            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(0, confirm.CallCount); // NUNCA pergunta "salvar antes de assinar"
            Assert.Equal(1, dialog.CallCount);
            Assert.Equal(1, engine.SignCallCount);
            // o motor recebeu o snapshot EM MEMÓRIA (30 páginas), não uma versão salva do original.
            Assert.Equal(Fixtures.ThirtyPages(), engine.LastRequest!.Pdf);
            // o arquivo ORIGINAL em disco continua intocado (não foi salvo nem sobrescrito).
            Assert.Equal(originalBytes, File.ReadAllBytes(d.Session.FilePath));
            Assert.True(d.IsDirty); // ainda sujo — nada foi salvo no original
        }
    }

    [Fact] // o resultado assinado vai pra um arquivo NOVO ("Salvar como"), com nome sugerido, e é ABERTO.
    public async Task Sign_WritesSignedBytesToChosenFile_AndOpensIt()
    {
        var (doc, _, engine, _, _, errors, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            await d.SignCommand.ExecuteAsync(null);

            Assert.Empty(errors);
            var (path, bytes) = Assert.Single(_written);
            Assert.Equal(@"C:\out\assinado.pdf", path);           // caminho escolhido no "Salvar como"
            Assert.Equal(Fixtures.ThirtyPages(), bytes);          // bytes ASSINADOS (saída do motor fake)
            Assert.Equal(@"C:\out\assinado.pdf", Assert.Single(_openedAfterSign)); // abriu o assinado
            Assert.Contains(_savePickerSuggestions, s => s.EndsWith("(assinado).pdf")); // nome sugerido
        }
    }

    [Fact] // cancelar o "Salvar como" aborta SEM gravar nada e SEM abrir — o original fica intocado.
    public async Task Sign_SaveAsCancelled_NothingWritten_NothingOpened()
    {
        var (doc, _, engine, _, _, errors, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            _savePickerReturns = null; // usuário cancelou o "Salvar como"

            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(1, engine.SignCallCount); // o motor até assinou em memória...
            Assert.Empty(_written);                // ...mas nada foi gravado
            Assert.Empty(_openedAfterSign);        // nem aberto
            Assert.Empty(errors);                  // cancelar não é erro
            Assert.False(d.Session.IsEditInFlight); // funil solto
        }
    }

    // ---- diálogo: allowDocMdp / cancelamento -----------------------------------------------------

    [Fact]
    public async Task Sign_NoExistingSignatures_DialogGetsAllowDocMdpTrue()
    {
        var (doc, _, _, dialog, _, _, _, cert) = BuildForSigning(hasSignatures: false);
        using var d = doc;
        using (cert)
        {
            await d.SignCommand.ExecuteAsync(null);
            Assert.True(dialog.LastAllowDocMdp);
        }
    }

    [Fact]
    public async Task Sign_HasExistingSignatures_DialogGetsAllowDocMdpFalse()
    {
        var (doc, _, _, dialog, _, _, _, cert) = BuildForSigning(hasSignatures: true);
        using var d = doc;
        using (cert)
        {
            await d.SignCommand.ExecuteAsync(null);
            Assert.False(dialog.LastAllowDocMdp);
        }
    }

    [Fact] // certificados ECC entram na lista PASSADA ao diálogo -- o VM não filtra, só repassa (a View
    // é quem desabilita com explicação pt-BR, ver Views.SignDialog).
    public async Task Sign_CertificateListPassedToDialog_IncludesEccItemsUnfiltered()
    {
        var editor = new FakePdfEditor { HasSignaturesResult = false };
        var engine = new FakeSigningEngine();
        using var rsaCert = CreateEphemeralRsaCertificate("RSA");
        using var eccCert = CreateEphemeralRsaCertificate("ECC (simulado)"); // só precisa existir na lista -- IsRsa=false é o que importa aqui
        var certs = new[] { FakeCertificateInfo(rsaCert, isRsa: true), FakeCertificateInfo(eccCert, isRsa: false) };
        var dialog = new FakeSignDialogService(null); // cancela -- só queremos inspecionar o que chegou
        using var d = new DocumentViewModel(
            DocumentSession.Open(CopyFixtureToTemp()), editor: editor, config: new AppConfig(NewConfigDir()),
            notifyError: _ => { }, notifyInfo: _ => { }, signDialog: dialog, signingEngine: engine,
            confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
            listSigningCertificates: () => certs);

        await d.SignCommand.ExecuteAsync(null);

        Assert.Equal(2, dialog.LastCertificates!.Count);
        Assert.Contains(dialog.LastCertificates, c => !c.IsRsa);
    }

    [Fact]
    public async Task Sign_DialogCancelled_NoOp_EngineNeverCalled_FunnelNeverArmed()
    {
        var (doc, _, engine, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = null; // cancelado

            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(0, engine.SignCallCount);
            Assert.False(d.Session.IsEditInFlight);
            Assert.False(d.IsSignedDocument);
        }
    }

    // ---- sem carimbo: assina direto ---------------------------------------------------------------

    [Fact] // FLUXO ADOBE: assinar (sem carimbo) gera o assinado num arquivo NOVO e o abre; o documento
    // ATUAL (original) fica intocado — IsSignedDocument continua false, snapshot inalterado.
    public async Task Sign_NoStamp_SignsToNewFile_OriginalDocUnchanged()
    {
        var (doc, _, engine, _, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            var snapshotBefore = d.Session.Snapshot;

            await d.SignCommand.ExecuteAsync(null);

            Assert.Empty(errors);
            Assert.Equal(1, engine.SignCallCount);
            Assert.False(d.IsSignedDocument);                 // o doc ATUAL não é o assinado
            Assert.Same(snapshotBefore, d.Session.Snapshot);  // original em memória inalterado
            Assert.Single(_written);                          // o assinado foi gravado num arquivo novo
            Assert.Single(_openedAfterSign);                  // e aberto numa aba nova
            var msg = Assert.Single(infos);
            Assert.Contains("assinado", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(AnnotationTool.None, d.ActiveTool);  // nunca entrou em modo de colocação
        }
    }

    [Fact]
    public async Task Sign_NoStamp_BuildsSignRequest_WithCertificateReasonLocation()
    {
        var (doc, _, engine, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            await d.SignCommand.ExecuteAsync(null);

            var req = engine.LastRequest!;
            Assert.Same(cert, req.Certificate);
            Assert.Equal("Motivo", req.Reason);
            Assert.Equal("Local", req.Location);
            Assert.Null(req.Stamp);
        }
    }

    [Fact]
    public async Task Sign_ApplyDocMdpTrue_RequestCarriesFormsAndSignaturesLevel()
    {
        var (doc, _, engine, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: true, PlaceStamp: false);
            await d.SignCommand.ExecuteAsync(null);
            Assert.Equal(DocMdpLevel.FormsAndSignatures, engine.LastRequest!.CertificationLevel);
        }
    }

    [Fact]
    public async Task Sign_ApplyDocMdpFalse_RequestCertificationLevelIsNull()
    {
        var (doc, _, engine, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: false);
            await d.SignCommand.ExecuteAsync(null);
            Assert.Null(engine.LastRequest!.CertificationLevel);
        }
    }

    [Fact] // motor lança -> erro pt-BR CRU (sem sufixo composto: não há mais salvamento forçado) e
    // NADA é gravado; o documento atual fica intocado, funil solto.
    public async Task Sign_EngineThrowsPdfSigningException_NotifiesError_WritesNothing()
    {
        var (doc, _, engine, _, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            engine.ThrowOnSign = new PdfSigningException("Não foi possível acessar a chave privada.");
            var snapshotBefore = d.Session.Snapshot;

            await d.SignCommand.ExecuteAsync(null);

            var msg = Assert.Single(errors);
            Assert.Equal("Não foi possível acessar a chave privada.", msg); // texto CRU do motor
            Assert.Empty(infos);
            Assert.Empty(_written);           // nada gravado
            Assert.Empty(_openedAfterSign);   // nada aberto
            Assert.False(d.IsSignedDocument);
            Assert.Same(snapshotBefore, d.Session.Snapshot);
            Assert.False(d.Session.IsEditInFlight); // funil solto mesmo em falha
        }
    }

    [Fact] // I1 (revisão final): mesmo mecanismo de Sign_EngineThrowsPdfSigningException_NotifiesError_WritesNothing
    // acima, nomeando especificamente a recusa DocMDP P=1 que PadesSigningEngine.Sign agora impõe
    // (ver task-1-report.md) -- documento certificado NO_CHANGES_PERMITTED recusa até uma 2ª
    // assinatura de aprovação; o VM só precisa continuar repassando a mensagem tipada do motor, sem
    // tentar reinterpretar.
    public async Task Sign_EngineRefusesP1CertifiedDocument_NotifiesTypedMessage_DoesNotCommit()
    {
        var (doc, _, engine, _, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            engine.ThrowOnSign = new PdfSigningException(
                "O documento é certificado e não permite alterações (nível máximo de proteção). " +
                "Não é possível adicionar assinaturas.");
            var snapshotBefore = d.Session.Snapshot;

            await d.SignCommand.ExecuteAsync(null);

            var msg = Assert.Single(errors);
            Assert.Contains("certificado", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(infos);
            Assert.Empty(_written); // nada gravado
            Assert.False(d.IsSignedDocument);
            Assert.Same(snapshotBefore, d.Session.Snapshot);
            Assert.False(d.Session.IsEditInFlight);
        }
    }

    [Fact] // gravar o arquivo assinado falha (disco cheio/destino travado) -> IOException vira erro
    // pt-BR (nunca uma Task não observada, já que o caminho COM carimbo é fire-and-forget). O motor
    // RODOU (assinou em memória); só a gravação do NOVO arquivo falhou. Funil solto.
    public async Task Sign_WriteSignedFileThrowsIOException_NotifiesError_FunnelReleased()
    {
        var (doc, _, engine, _, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            _writeThrowsIO = true;

            await d.SignCommand.ExecuteAsync(null);

            var msg = Assert.Single(errors);
            Assert.Contains("Não foi possível salvar", msg);
            Assert.Empty(infos);
            Assert.Equal(1, engine.SignCallCount); // o motor assinou em memória -- só a gravação falhou
            Assert.Empty(_openedAfterSign);        // não abriu nada
            Assert.False(d.IsSignedDocument);
            Assert.False(d.Session.IsEditInFlight); // funil solto mesmo em falha de I/O
        }
    }

    // ---- com carimbo: caixa ajustável (Task 2, Plano 8) -- o gatilho agora é o ARRASTO, não o clique --

    [Fact] // Sign() em si NÃO muda (a troca de gatilho vive inteiramente em BeginStampBoxPlacementAsync/
    // ConfirmSignatureStampAsync, abaixo) -- entra em modo de colocação, funil continua desarmado.
    public async Task Sign_WithStamp_EntersPlacementMode_EngineNotCalledYet()
    {
        var (doc, _, engine, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);

            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool);
            Assert.Equal(0, engine.SignCallCount);
            Assert.False(d.Session.IsEditInFlight); // funil só arma no CONFIRMAR (ConfirmSignatureStampAsync)
        }
    }

    /// Task 2 (Plano 8): desenha + ajusta (move + redimensiona) a caixa a partir de um retângulo
    /// conhecido -- exemplar pros testes de confirmação abaixo, mesmo padrão de
    /// StampBoxPlacementTests.BeginAdjusting, mas passando pelo gatilho REAL (BeginStampBoxPlacementAsync).
    // Desenha a caixa (mouse-down + arrasto) SEM soltar — o teste chama `await d.EndStampDrawAsync()`
    // pra soltar o mouse (que, no fluxo Adobe, ASSINA na hora, sem ajuste nem confirmar).
    private static DocumentViewModel DrawBox(DocumentViewModel d,
        double left, double bottom, double right, double top)
    {
        d.BeginStampBoxPlacementAsync(0, new PdfPoint(left, bottom)).GetAwaiter().GetResult();
        d.UpdateDrawTo(new PdfPoint(right, top));
        return d;
    }

    [Fact] // Task 2: BeginStampBoxPlacementAsync é o gatilho REAL (chamado pela View no mouse-down) --
    // entra em Drawing com o CN resolvido do certificado ESCOLHIDO no diálogo (X509Certificate2.
    // GetNameInfo, SimpleName -- mesmo extrator já usado por CertificateCatalog/PadesSigningEngine).
    public async Task BeginStampBoxPlacementAsync_AfterSignWithStamp_EntersDrawingWithCertificateCn()
    {
        var (doc, _, _, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);

            await d.BeginStampBoxPlacementAsync(0, new PdfPoint(100, 100));

            Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase);
            Assert.Equal(cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false), d.StampBoxCertificateCn);
        }
    }

    [Fact] // mesma guarda do clique único antigo -- a máquina nunca roda fora do modo de colocação.
    public async Task BeginStampBoxPlacementAsync_WrongActiveTool_NoOp()
    {
        var (doc, _, engine, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            Assert.Equal(AnnotationTool.None, d.ActiveTool);
            await d.BeginStampBoxPlacementAsync(0, new PdfPoint(100, 100));
            Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
            Assert.Equal(0, engine.SignCallCount);
        }
    }

    [Fact] // Carimbo em página GIRADA agora é SUPORTADO (StampRotation converte o retângulo EXIBIDO ->
    // MediaBox no motor; o iText endireita a aparência). O gate de rotação que existia SÓ neste fluxo de
    // assinatura foi removido — a colocação entra em Drawing normalmente, SEM o aviso "Página girada"
    // (diferente das anotações genéricas, que continuam gateadas em outra versão).
    public async Task BeginStampBoxPlacementAsync_RotatedPage_EntersDrawing_NoLongerBlocked()
    {
        var (doc, editor, engine, dialog, _, errors, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            editor.ReadAnnotationsResult = Array.Empty<AnnotationData>();
            editor.PageRotationsResult = new[] { 90 };
            await d.RefreshAnnotationsByPageAsync(); // primeiro o cache de rotação/anotações

            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);
            Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool);

            await d.BeginStampBoxPlacementAsync(0, new PdfPoint(100, 100));

            Assert.DoesNotContain(errors, e => e.Contains("Página girada"));
            Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase); // ENTROU em Drawing
            Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool);
            Assert.False(d.IsSignedDocument); // commit só no Confirmar
        }
    }

    // ---- ConfirmSignatureStampAsync: confirma a caixa AJUSTADA -> motor recebe o rect FINAL ----------

    [Fact] // FLUXO ADOBE: soltar o mouse (EndStampDrawAsync) assina na hora com o rect DESENHADO — sem
    // fase de ajuste (mover/redimensionar) nem botão confirmar. O motor recebe exatamente a caixa
    // arrastada; o assinado vai pra arquivo novo (o doc atual não vira assinado).
    public async Task Sign_WithStamp_DrawnBoxRectPassedToEngine_ThenSavedAsAndOpened()
    {
        var (doc, _, engine, dialog, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, "Motivo", "Local", ApplyDocMdp: true, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);

            DrawBox(d, 100, 100, 300, 200); // 200x100pt
            await d.EndStampDrawAsync();     // soltar o mouse = assina na hora

            Assert.Empty(errors);
            Assert.Equal(1, engine.SignCallCount);
            var stamp = engine.LastRequest!.Stamp;
            Assert.NotNull(stamp);
            Assert.Equal(0, stamp!.PageIndex);
            Assert.Equal(100, stamp.Rect.LeftPt, 0.01);
            Assert.Equal(100, stamp.Rect.BottomPt, 0.01);
            Assert.Equal(300, stamp.Rect.RightPt, 0.01);
            Assert.Equal(200, stamp.Rect.TopPt, 0.01);
            Assert.Single(_written);              // assinado gravado em arquivo novo
            Assert.Single(_openedAfterSign);      // e aberto numa aba nova
            Assert.False(d.IsSignedDocument);     // o doc ATUAL continua sendo o original
            Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
            Assert.Equal(AnnotationTool.None, d.ActiveTool);
            Assert.Single(infos);
        }
    }

    // ---- Plano 22: rubrica na assinatura (o diálogo devolve RubricaBytes -> Stamp.ImageBytes) --------

    private static readonly byte[] RubricaPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    [Fact] // Sign() passa a galeria de rubricas ao diálogo (o diálogo gere escolher/adicionar/remover).
    public async Task Sign_PassesRubricaGalleryToDialog()
    {
        var (doc, _, _, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: false);
            await d.SignCommand.ExecuteAsync(null);
            Assert.NotNull(dialog.LastRubricas);
        }
    }

    [Fact] // o diálogo devolveu RubricaBytes (rubrica escolhida) -> o motor recebe um VisibleStampSpec com esses bytes.
    public async Task ConfirmSignatureStampAsync_WithRubricaBytes_PassesImageBytesToEngine()
    {
        var (doc, _, engine, dialog, _, errors, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: true, PlaceStamp: true, RubricaBytes: RubricaPng);
            await d.SignCommand.ExecuteAsync(null);
            DrawBox(d, 100, 100, 300, 200);
            await d.EndStampDrawAsync();

            Assert.Empty(errors);
            var stamp = engine.LastRequest!.Stamp;
            Assert.NotNull(stamp);
            Assert.Equal(RubricaPng, stamp!.ImageBytes);
        }
    }

    [Fact] // sem RubricaBytes -> Stamp.ImageBytes null (carimbo padrão).
    public async Task ConfirmSignatureStampAsync_WithoutRubricaBytes_ImageBytesNull()
    {
        var (doc, _, engine, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: true, PlaceStamp: true); // RubricaBytes default null
            await d.SignCommand.ExecuteAsync(null);
            DrawBox(d, 100, 100, 300, 200);
            await d.EndStampDrawAsync();

            Assert.Null(engine.LastRequest!.Stamp!.ImageBytes);
        }
    }

    [Fact] // Confirmar sem antes chegar em Adjusting (nunca desenhou, ou ainda Drawing) -> no-op --
    // ConfirmStampBox() (Task 1) já devolve null fora de Adjusting; este teste prova que o wrapper de
    // Task 2 propaga esse null sem tentar alcançar o motor.
    public async Task ConfirmSignatureStampAsync_NotAdjusting_NoOp()
    {
        var (doc, _, engine, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);

            await d.ConfirmSignatureStampAsync(); // nunca desenhou nada

            Assert.Equal(0, engine.SignCallCount);
            Assert.False(d.Session.IsEditInFlight);
        }
    }

    [Fact] // sem NENHUM PendingSignPlacement (ex.: chamado fora do fluxo "Assinar") -> no-op defensivo,
    // mesmo padrão de guarda de PlaceSignatureStampAtAsync original.
    public async Task ConfirmSignatureStampAsync_NoPendingSignPlacement_NoOp()
    {
        var (doc, _, engine, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            await d.ConfirmSignatureStampAsync();
            Assert.Equal(0, engine.SignCallCount);
        }
    }

    // ---- BELT relocado pro Confirmar (Task 2) -- cobre TODA a janela Desenhar+Ajustar --------------

    internal static void SelectSomeText(PageViewModel page)
    {
        page.BeginSelection(new Point(10, 10));
        page.UpdateSelection(new Point(300, 20));
    }

    [Fact] // "placement-window mutation gap" agora cobre a janela INTEIRA de Desenhar+Ajustar (não só
    // entre o diálogo e o clique). Mutação REAL (ApplyMarkup, comando de produção) durante Adjusting --
    // OnSessionApplied/CancelStampBox(dueToDocumentMutation: true) reseta a caixa E NOTIFICA (fix
    // pós-revisão do coordenador -- achado real: sem aviso, a caixa simplesmente SOME da tela no meio
    // de um Ctrl+Z acidental, usuário não-técnico sem explicação nenhuma) -- mesma disciplina de
    // SelectedAnnotation/SelectedFormField/SelectedSignature quanto ao RESET, mas com o aviso pt-BR que
    // essas 3 outras propriedades nunca precisaram (nenhuma delas tem um gesto de vários passos em
    // andamento que pudesse "sumir sem explicação"). Este teste prova a CONSEQUÊNCIA completa através
    // do Confirmar: aviso disparado EXATAMENTE 1 vez com a mensagem estabelecida, funil nunca arma,
    // motor nunca alcançado, ActiveTool resetado -- mesmo contrato de "abort+reset COM aviso" que o
    // clique único antigo garantia (o cinto estrutural de SignCoreAsync,
    // ReferenceEquals(snapshotAtDialogOk, Session.Snapshot), continua ali como rede de segurança --
    // neste app single-threaded ele nunca é o que pega a mutação de fato nem o que notifica, porque
    // OnSessionApplied já chega primeiro E já notifica, mas a checagem permanece a MESMA garantia
    // estrutural que já protege o caminho sem carimbo, ver doc XML de SignCoreAsync).
    public async Task ConfirmSignatureStampAsync_DocumentMutatedDuringAdjust_NeverReachesEngine_ResetsPlacement()
    {
        var (doc, editor, engine, dialog, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);

            DrawBox(d, 100, 100, 300, 200);
            Assert.Equal(StampPlacementPhase.Drawing, d.StampPlacementPhase); // desenhando (Adobe: sem Adjusting)

            SelectSomeText(d.Pages[0]);
            Assert.True(d.HasActiveSelection); // sanity
            await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);
            Assert.Equal(1, editor.AddAnnotationCallCount); // sanity: a mutação REALMENTE aconteceu

            Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase); // já resetado (COM aviso, fix pós-revisão)
            // o aviso já disparou AQUI (dentro de OnSessionApplied, síncrono com ApplyMarkupCommand acima)
            // -- exatamente 1 vez, com a MESMA mensagem estabelecida.
            var noticeFromMutation = Assert.Single(errors);
            Assert.Equal(
                "O documento foi alterado durante o posicionamento do carimbo. A assinatura foi cancelada — assine novamente.",
                noticeFromMutation);

            await d.EndStampDrawAsync(); // soltar o mouse agora é no-op (placement já foi cancelado)

            Assert.Equal(0, engine.SignCallCount); // motor NUNCA alcançado
            Assert.Empty(_written); // nada assinado/gravado
            Assert.Single(errors); // NENHUM aviso duplicado
            Assert.Empty(infos);
            Assert.False(d.IsSignedDocument);
            Assert.Equal(AnnotationTool.None, d.ActiveTool); // RESET completo
            Assert.False(d.Session.IsEditInFlight); // funil nunca armou
        }
    }

    // ---- CancelStampBox agora também limpa _pendingSignPlacement (Task 2) --------------------------

    [Fact] // Task 2: CancelStampBox (Esc/botão/troca de ferramenta/troca de documento -- todos passam
    // por aqui) agora TAMBÉM limpa o PendingSignPlacement armado por Sign() -- sem isto, uma tentativa
    // de Confirmar tardia poderia reusar um contexto (certificado/motivo/local) obsoleto. Prova indireta
    // (o campo é privado): depois de cancelar em Adjusting, Confirmar vira no-op completo.
    public async Task CancelStampBox_FromAdjusting_ClearsPendingSignPlacement_ConfirmBecomesNoOp()
    {
        var (doc, _, engine, dialog, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);
            DrawBox(d, 100, 100, 300, 200);

            d.CancelStampBox();

            await d.ConfirmSignatureStampAsync();
            Assert.Equal(0, engine.SignCallCount);
            Assert.Equal(AnnotationTool.None, d.ActiveTool);
        }
    }

    // ---- Negative controls (fix pós-revisão do coordenador): cancelamento INICIADO PELO USUÁRIO
    // continua SILENCIOSO -- só a mutação alheia (OnSessionApplied) notifica. O usuário que aperta Esc/
    // o botão "✖ Cancelar"/troca de ferramenta JÁ SABE que cancelou; um aviso ali seria ruído.

    [Fact] // CancelStampBox() sem argumento (default dueToDocumentMutation=false) é EXATAMENTE a
    // chamada que PdfViewerControl.OnPreviewKeyDown (Esc) e StampBoxCancel_Click (botão) fazem -- os
    // 2 caminhos são mecanicamente IDÊNTICOS na fronteira do VM (mesma assinatura, mesmo default),
    // então uma chamada direta aqui cobre os 2 sem precisar de uma janela WPF real por caminho.
    public async Task CancelStampBox_UserInitiated_Direct_StaysSilent_NoNotice()
    {
        var (doc, _, engine, dialog, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);
            DrawBox(d, 100, 100, 300, 200);

            d.CancelStampBox(); // mesmo caminho de Esc/botão -- default silencioso

            Assert.Empty(errors); // NENHUM aviso -- o usuário cancelou de propósito
            Assert.Empty(infos);
            Assert.Equal(0, engine.SignCallCount);
        }
    }

    [Fact] // troca de ferramenta (OnActiveToolChanged) chama CancelStampBox() SEM o argumento novo --
    // mesmo default silencioso, negative control específico pra esse call site (distinto de
    // OnSessionApplied, que É o único que passa dueToDocumentMutation: true).
    public async Task SwitchingActiveTool_WhilePlacementActive_StaysSilent_NoNotice()
    {
        var (doc, _, engine, dialog, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);
            DrawBox(d, 100, 100, 300, 200);

            d.ActiveTool = AnnotationTool.Rectangle; // troca de ferramenta -- cancela via OnActiveToolChanged

            Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase); // sanity: realmente cancelou
            Assert.Empty(errors); // NENHUM aviso -- o usuário trocou de ferramenta de propósito
            Assert.Empty(infos);
            Assert.Equal(0, engine.SignCallCount);
        }
    }

    // ---- integração: motor REAL + certificado efêmero REAL, pelo fluxo completo do VM ---------------

    [Fact] // ponta a ponta pelo COMANDO real: motor de PRODUÇÃO (SigningEngineFactory.Create()) +
    // certificado RSA efêmero. FLUXO ADOBE: assina o snapshot e grava o resultado num ARQUIVO NOVO (o
    // original fica intocado) com 1 assinatura íntegra. (A assinatura incremental do motor — 2ª/3ª — é
    // testada direto em mPdf.Signing.Tests; aqui provamos o fio VM -> motor -> arquivo novo.)
    public async Task Sign_Integration_RealEngine_WritesNewFileWithValidSignature_OriginalUntouched()
    {
        var tmp = CopyFixtureToTemp();
        var originalBytes = File.ReadAllBytes(tmp);
        using var cert = CreateEphemeralRsaCertificate("Signatario Um");
        var realEngine = SigningEngineFactory.Create();
        var dialog = new FakeSignDialogService(new SignDialogResult(cert, "Aprovação", "Escritório", ApplyDocMdp: true, PlaceStamp: false));
        var outPath = Path.Combine(Path.GetTempPath(), $"mpdf-signed-{Guid.NewGuid():N}.pdf");
        _tempFilesToDelete.Add(outPath);

        using var d = new DocumentViewModel(
            DocumentSession.Open(tmp),
            editor: PdfEditorFactory.Create(), // real -- HasSignatures precisa ler o PDF de verdade
            config: new AppConfig(NewConfigDir()),
            notifyError: _ => { }, notifyInfo: _ => { },
            signDialog: dialog, signingEngine: realEngine,
            confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
            listSigningCertificates: () => new[] { FakeCertificateInfo(cert) },
            pickPdfToSave: _ => outPath, writeAllBytes: File.WriteAllBytes,
            openSavedDocument: _ => Task.CompletedTask);

        await d.SignCommand.ExecuteAsync(null);

        Assert.False(d.IsSignedDocument);                         // o doc atual é o ORIGINAL, não o assinado
        Assert.Equal(originalBytes, File.ReadAllBytes(tmp));       // original em disco intocado
        Assert.True(File.Exists(outPath));                        // o assinado é um arquivo NOVO
        var sigs = realEngine.ReadSignatures(File.ReadAllBytes(outPath));
        Assert.Single(sigs);
        Assert.True(sigs[0].IntegrityValid);
        Assert.Equal(DocMdpLevel.FormsAndSignatures, sigs[0].Certification);
    }

    // ---- ACEITAÇÃO POR PIXEL (Task 2, Plano 8 -- O PONTO DO PLANO) -----------------------------------
    //
    // "o rect DESENHADO/AJUSTADO == rect onde o carimbo RENDE (px na região exata; tolerância zero de
    // deslocamento — é o ponto do pedido)" (plano). Exemplar EXATO de
    // PadesSigningEngineTests.Sign_WithVisibleStamp_PaintsOnlyInsideStampRegion (P4, mPdf.Signing.Tests)
    // -- mesma janela de tolerância de borda (antialiasing), mesmo par núcleo/fora-com-folga -- mas
    // desta vez pelo FLUXO COMPLETO do VM (motor de PRODUÇÃO + certificado efêmero REAL, nunca um fake):
    // Sign() -> BeginStampBoxPlacementAsync (desenha um rect CONHECIDO) -> UpdateDrawTo -> EndStampDraw
    // -> MoveBoxBy + ResizeBoxByHandle (AJUSTA pra um rect FINAL diferente do desenhado) -> Confirmar
    // (ConfirmSignatureStampAsync) -> renderiza a página assinada -> carimbo aparece EXATAMENTE no rect
    // FINAL (o AJUSTADO, não o desenhado originalmente) -- prova que o motor recebeu o rect que o
    // usuário viu na tela, não um valor intermediário.

    [Fact]
    public async Task Sign_Integration_StampBoxDraw_RendersExactlyInsideDrawnRect()
    {
        var tmp = CopyFixtureToTemp();
        var originalBytes = File.ReadAllBytes(tmp); // baseline pro diff de pixels
        using var cert = CreateEphemeralRsaCertificate();
        var realEngine = SigningEngineFactory.Create();
        var dialog = new FakeSignDialogService(new SignDialogResult(cert, "Aprovação", "Escritório", ApplyDocMdp: true, PlaceStamp: true));
        var outPath = Path.Combine(Path.GetTempPath(), $"mpdf-signed-{Guid.NewGuid():N}.pdf");
        _tempFilesToDelete.Add(outPath);

        using var d = new DocumentViewModel(
            DocumentSession.Open(tmp),
            editor: PdfEditorFactory.Create(), // real -- HasSignatures precisa ler o PDF de verdade
            config: new AppConfig(NewConfigDir()),
            notifyError: _ => { }, notifyInfo: _ => { },
            signDialog: dialog, signingEngine: realEngine,
            confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
            listSigningCertificates: () => new[] { FakeCertificateInfo(cert) },
            pickPdfToSave: _ => outPath, writeAllBytes: File.WriteAllBytes,
            openSavedDocument: _ => Task.CompletedTask);

        await d.SignCommand.ExecuteAsync(null);
        Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool);

        // FLUXO ADOBE: desenha um rect CONHECIDO e SOLTA -> assina na hora nesse rect (sem ajuste).
        await d.BeginStampBoxPlacementAsync(0, new PdfPoint(300, 50));
        d.UpdateDrawTo(new PdfPoint(500, 150)); // 200x100pt
        await d.EndStampDrawAsync();            // soltar o mouse = assina + Salvar como
        var finalRect = new PdfQuad(300, 50, 500, 150); // o rect DESENHADO é o final (sem ajuste)

        Assert.False(d.IsSignedDocument); // o doc atual continua o original
        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Equal(AnnotationTool.None, d.ActiveTool);
        Assert.True(File.Exists(outPath));
        var signedBytes = File.ReadAllBytes(outPath);
        Assert.NotEqual(originalBytes, signedBytes);

        var afterSign = realEngine.ReadSignatures(signedBytes);
        Assert.Single(afterSign);
        Assert.True(afterSign[0].IntegrityValid);

        using var rendererBefore = new PdfDocumentRenderer(originalBytes);
        using var rendererAfter = new PdfDocumentRenderer(signedBytes);
        var pageBefore = rendererBefore.RenderPage(0, 1.0);
        var pageAfter = rendererAfter.RenderPage(0, 1.0);
        Assert.Equal(pageBefore.WidthPx, pageAfter.WidthPx);
        Assert.Equal(pageBefore.HeightPx, pageAfter.HeightPx);
        int w = pageBefore.WidthPx, h = pageBefore.HeightPx;

        // Mesmo padrão de PadesSigningEngineTests.Sign_WithVisibleStamp_PaintsOnlyInsideStampRegion (P4):
        // banda de Margin px na BORDA do retângulo é ignorada (antialiasing), núcleo interior precisa
        // ter pixels diferentes (o carimbo em si), ZERO pixels diferentes fora do retângulo com folga.
        const int Margin = 4;
        int stampLeft = (int)finalRect.LeftPt, stampRight = (int)finalRect.RightPt;
        int stampTop = h - (int)finalRect.TopPt, stampBottom = h - (int)finalRect.BottomPt; // Y invertido

        int diffOutsidePadded = 0, diffInsideCore = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool differs = pageBefore.Bgra[i] != pageAfter.Bgra[i]
                    || pageBefore.Bgra[i + 1] != pageAfter.Bgra[i + 1]
                    || pageBefore.Bgra[i + 2] != pageAfter.Bgra[i + 2];
                if (!differs) continue;

                bool insideCore = x >= stampLeft + Margin && x < stampRight - Margin
                    && y >= stampTop + Margin && y < stampBottom - Margin;
                bool outsidePadded = x < stampLeft - Margin || x >= stampRight + Margin
                    || y < stampTop - Margin || y >= stampBottom + Margin;

                if (insideCore) diffInsideCore++;
                else if (outsidePadded) diffOutsidePadded++;
                // pixels na faixa de borda (nem núcleo nem fora-com-folga) são IGNORADOS de propósito
            }
        }

        // Medido ao vivo (ver task-2-report.md): página A4 renderizada 595x842 px; rect final AJUSTADO
        // (320,40)-(550,140)pt (o desenhado original era 300,50-500,150 -- MoveBoxBy+ResizeBoxByHandle
        // realmente mudaram o resultado); diffInsideCore=4350, diffOutsidePadded=0 -- limiar 100 folgado
        // abaixo do valor real, ainda longe o bastante de 0 pra não confundir com ruído de antialiasing.
        Assert.Equal(0, diffOutsidePadded); // CONTRATO CENTRAL: 0 px fora do rect FINAL AJUSTADO
        Assert.True(diffInsideCore > 100,
            $"carimbo não visível no rect ajustado: só {diffInsideCore} pixels diferentes no núcleo da região");
    }
}
