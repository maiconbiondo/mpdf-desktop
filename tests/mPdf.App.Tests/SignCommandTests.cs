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

    public byte[] Sign(SignRequest request)
    {
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
    public bool? LastHasRubrica { get; private set; } // Plano 21
    // Task 2 (Plano 7, fix CRÍTICO pós-revisão): mesmo campo opcional de FakeSigningEngine acima.
    public List<string>? CallOrder { get; set; }

    public SignDialogResult? PromptForSignature(
        IReadOnlyList<SigningCertificateInfo> certificates, bool allowDocMdp, bool hasRubrica)
    {
        CallCount++;
        LastCertificates = certificates;
        LastAllowDocMdp = allowDocMdp;
        LastHasRubrica = hasRubrica;
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
            listSigningCertificates: () => new[] { FakeCertificateInfo(cert) });
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

    [Fact] // recusa aborta o fluxo INTEIRO — nem o diálogo de assinatura nem o motor são alcançados.
    public async Task Sign_DocDirty_ConfirmDeclined_AbortsWithoutSigning()
    {
        var (doc, _, engine, dialog, confirm, _, _, cert) = BuildForSigning(confirmSaveResult: false);
        using var d = doc;
        using (cert)
        {
            d.Session.Apply(Fixtures.ThirtyPages()); // suja
            Assert.True(d.IsDirty);

            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(1, confirm.CallCount);
            Assert.Equal(0, dialog.CallCount);
            Assert.Equal(0, engine.SignCallCount);
            Assert.True(d.IsDirty); // continua sujo -- nada foi salvo
            Assert.False(d.Session.IsEditInFlight); // funil NUNCA armou
        }
    }

    [Fact] // aceito -> salva ANTES de assinar (assina o snapshot SALVO, nunca bytes não persistidos).
    public async Task Sign_DocDirty_ConfirmAccepted_SavesBeforeSigning()
    {
        var tmp = CopyFixtureToTemp();
        var editor = new FakePdfEditor { HasSignaturesResult = false };
        var engine = new FakeSigningEngine();
        using var cert = CreateEphemeralRsaCertificate();
        var dialog = new FakeSignDialogService(new SignDialogResult(cert, null, null, ApplyDocMdp: true, PlaceStamp: false));
        var confirm = new FakeConfirmSaveBeforeSignService(true);
        using var d = new DocumentViewModel(
            DocumentSession.Open(tmp), editor: editor, config: new AppConfig(NewConfigDir()),
            notifyError: _ => { }, notifyInfo: _ => { }, signDialog: dialog, signingEngine: engine,
            confirmSaveBeforeSign: confirm, listSigningCertificates: () => new[] { FakeCertificateInfo(cert) });

        d.Session.Apply(Fixtures.ThirtyPages());
        Assert.True(d.IsDirty);

        await d.SignCommand.ExecuteAsync(null);

        Assert.Equal(1, confirm.CallCount);
        Assert.Equal(1, engine.SignCallCount);
        // o motor recebeu o snapshot JÁ SALVO (30 páginas), prova de que Save aconteceu ANTES do Sign.
        Assert.Equal(Fixtures.ThirtyPages(), engine.LastRequest!.Pdf);
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

    [Fact]
    public async Task Sign_NoStamp_SignsImmediately_CommitsAndNotifiesExactMessage()
    {
        var (doc, _, engine, _, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            await d.SignCommand.ExecuteAsync(null);

            Assert.Empty(errors);
            Assert.Equal(1, engine.SignCallCount);
            Assert.True(d.IsSignedDocument);
            Assert.Equal(Fixtures.ThirtyPages(), d.Session.Snapshot); // trocou pro resultado do motor
            var msg = Assert.Single(infos);
            Assert.Equal("Documento assinado e salvo. O histórico de desfazer foi limpo.", msg);
            Assert.Equal(AnnotationTool.None, d.ActiveTool); // nunca entrou em modo de colocação
        }
    }

    [Fact] // brief: "after signing, CanEdit false, banner visible, organizer mutators disabled" -- o
    // MESMO mecanismo de OnIsSignedDocumentChanged que já existia pra HasSignatures na abertura reage
    // aqui de graça (SignCoreAsync só seta IsSignedDocument=true, nenhum código NOVO de propagação).
    public async Task Sign_NoStamp_AfterSigning_CanEditFalse_AndOrganizerMutatorsDisabled()
    {
        var (doc, _, _, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            d.IsOrganizerOpen = true;
            d.Organizer!.ToggleSelect(0, ctrl: false);
            Assert.True(d.Organizer!.RotateSelectedCommand.CanExecute(null)); // sanity ANTES de assinar

            await d.SignCommand.ExecuteAsync(null);

            Assert.False(d.CanEdit);
            Assert.False(d.Organizer!.RotateSelectedCommand.CanExecute(null)); // CanEdit=false propagou pro organizador
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

    [Fact] // controle: doc LIMPO (sem salvamento forçado) -> mensagem de erro é o texto CRU do motor,
    // SEM o sufixo composto (ver ComposeSignFailureMessage) — contraste direto do teste "compound" abaixo.
    public async Task Sign_EngineThrowsPdfSigningException_NotifiesError_DoesNotCommit()
    {
        var (doc, _, engine, _, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            engine.ThrowOnSign = new PdfSigningException("Não foi possível acessar a chave privada.");
            var snapshotBefore = d.Session.Snapshot;

            await d.SignCommand.ExecuteAsync(null);

            var msg = Assert.Single(errors);
            Assert.Equal("Não foi possível acessar a chave privada.", msg); // SEM sufixo -- doc não estava sujo
            Assert.Empty(infos);
            Assert.False(d.IsSignedDocument);
            Assert.Same(snapshotBefore, d.Session.Snapshot);
            Assert.False(d.Session.IsEditInFlight); // funil solto mesmo em falha
        }
    }

    [Fact] // "compound failure message" (revisão do coordenador, achado real): o salvamento FORÇADO
    // pré-assinatura (doc sujo -> aceitou salvar) JÁ aconteceu quando o motor falha -- sem o sufixo, o
    // usuário veria só o erro do motor, sem saber que o arquivo em disco já foi sobrescrito (sem
    // assinatura nenhuma). Caminho composto: sujo -> aceita salvar -> motor lança -> mensagem tem as
    // DUAS partes.
    public async Task Sign_DirtyDocAcceptedSave_EngineThrows_MessageMentionsBothEngineErrorAndUnsignedSave()
    {
        var (doc, _, engine, _, confirm, errors, infos, cert) = BuildForSigning(confirmSaveResult: true);
        using var d = doc;
        using (cert)
        {
            d.Session.Apply(Fixtures.ThirtyPages()); // suja
            engine.ThrowOnSign = new PdfSigningException("Não foi possível acessar a chave privada.");

            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(1, confirm.CallCount); // prova que o salvamento forçado FOI consultado/aceito
            var msg = Assert.Single(errors);
            Assert.Contains("Não foi possível acessar a chave privada.", msg);
            Assert.Contains("O documento foi salvo, mas NÃO está assinado.", msg);
            Assert.Empty(infos);
            Assert.False(d.IsSignedDocument);
        }
    }

    [Fact] // I1 (revisão final): mesmo mecanismo de Sign_EngineThrowsPdfSigningException_NotifiesError_DoesNotCommit
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
            Assert.False(d.IsSignedDocument);
            Assert.Same(snapshotBefore, d.Session.Snapshot);
            Assert.False(d.Session.IsEditInFlight);
        }
    }

    [Fact] // I2 (revisão final, achado do revisor): Session.CommitSigned grava em disco de verdade
    // (AtomicWrite) -- se o destino estiver travado (arquivo aberto por outro processo, sharing
    // violation -- ou disco cheio na prática) a IOException NÃO era pega antes: escapava de
    // SignCoreAsync sem notificação nenhuma. No caminho COM carimbo (fire-and-forget
    // `_ = doc.PlaceSignatureStampAtAsync(...)`, PdfViewerControl.xaml.cs) isso virava uma Task não
    // observada -- silêncio TOTAL do ponto de vista do usuário (TaskScheduler.UnobservedTaskException
    // só loga em CrashLog, nunca mostra MessageBox nenhum). Mesmo exemplar de
    // DocumentSessionTests.Save_DestinationLocked_ThrowsReadableException_OriginalIntact_TempCleanedUp
    // (mPdf.Documents.Tests) -- trava o destino de VERDADE com FileShare.None, não um mock/fake.
    public async Task Sign_CommitSignedThrowsIOException_NotifiesComposedMessage_FunnelReleased()
    {
        var (doc, _, engine, _, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            using (new FileStream(d.Session.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await d.SignCommand.ExecuteAsync(null);
            }

            var msg = Assert.Single(errors);
            Assert.Contains("Não foi possível salvar", msg); // mesma mensagem de AtomicWrite.BuildFailureMessage
            Assert.Empty(infos);
            Assert.Equal(1, engine.SignCallCount); // o motor RODOU (assinou em memória) -- só o commit em disco falhou
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
    private static DocumentViewModel DrawBox(DocumentViewModel d,
        double left, double bottom, double right, double top)
    {
        d.BeginStampBoxPlacementAsync(0, new PdfPoint(left, bottom)).GetAwaiter().GetResult();
        d.UpdateDrawTo(new PdfPoint(right, top));
        d.EndStampDraw();
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

    [Fact] // gate de rotação (exemplar: PlaceSignatureStampAtAsync original, migrado pro INÍCIO do
    // arrasto) -- recusa com o MESMO aviso pt-BR, ferramenta continua ativa (usuário pode tentar outra
    // página), a máquina NUNCA entra em Drawing.
    public async Task BeginStampBoxPlacementAsync_RotatedPage_RefusesWithNotice_ToolStaysActive()
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

            Assert.Equal(0, engine.SignCallCount);
            Assert.Contains(errors, e => e.Contains("Página girada"));
            Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool); // continua ativa
            Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase); // NUNCA entrou em Drawing
            Assert.False(d.IsSignedDocument);
            Assert.False(d.Session.IsEditInFlight); // funil solto -- pode tentar outra página
        }
    }

    // ---- ConfirmSignatureStampAsync: confirma a caixa AJUSTADA -> motor recebe o rect FINAL ----------

    [Fact] // CONTRATO CENTRAL do Task 2: o motor recebe o rect AJUSTADO (mover + redimensionar), não
    // mais o tamanho fixo 180x60 do clique único antigo (DefaultStampWidthPt/DefaultStampHeightPt).
    public async Task ConfirmSignatureStampAsync_CommitsWithAdjustedRect_NotTheOldFixedSize()
    {
        var (doc, _, engine, dialog, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, "Motivo", "Local", ApplyDocMdp: true, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);

            DrawBox(d, 100, 100, 300, 200); // 200x100pt -- bem diferente do fixo 180x60
            d.MoveBoxBy(new PdfPoint(20, -10));                          // 120,90 - 320,190
            d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(50, 0)); // 120,90 - 370,190

            await d.ConfirmSignatureStampAsync();

            Assert.Empty(errors);
            Assert.Equal(1, engine.SignCallCount);
            var stamp = engine.LastRequest!.Stamp;
            Assert.NotNull(stamp);
            Assert.Equal(0, stamp!.PageIndex);
            Assert.Equal(120, stamp.Rect.LeftPt, 0.01);
            Assert.Equal(90, stamp.Rect.BottomPt, 0.01);
            Assert.Equal(370, stamp.Rect.RightPt, 0.01);
            Assert.Equal(190, stamp.Rect.TopPt, 0.01);
            Assert.True(d.IsSignedDocument);
            Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
            Assert.Equal(AnnotationTool.None, d.ActiveTool); // one-shot: desativa após commit
            Assert.Single(infos);
        }
    }

    // ---- Plano 21: rubrica na assinatura (UseRubrica -> Stamp.ImageBytes) ----------------------------

    private static readonly byte[] RubricaPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    [Fact] // Sign() informa hasRubrica ao diálogo conforme o config tenha (ou não) rubrica salva.
    public async Task Sign_PassesHasRubricaToDialog_WhenConfigHasRubrica()
    {
        var config = new AppConfig(NewConfigDir());
        config.SalvarRubrica(RubricaPng);
        var (doc, _, _, dialog, _, _, _, cert) = BuildForSigning(config: config);
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: false);
            await d.SignCommand.ExecuteAsync(null);
            Assert.True(dialog.LastHasRubrica);
        }
    }

    [Fact] // "Minha rubrica" (UseRubrica) -> o motor recebe um VisibleStampSpec com os bytes da rubrica.
    public async Task ConfirmSignatureStampAsync_WithRubrica_PassesImageBytesToEngine()
    {
        var config = new AppConfig(NewConfigDir());
        config.SalvarRubrica(RubricaPng);
        var (doc, _, engine, dialog, _, errors, _, cert) = BuildForSigning(config: config);
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: true, PlaceStamp: true, UseRubrica: true);
            await d.SignCommand.ExecuteAsync(null);
            DrawBox(d, 100, 100, 300, 200);
            await d.ConfirmSignatureStampAsync();

            Assert.Empty(errors);
            var stamp = engine.LastRequest!.Stamp;
            Assert.NotNull(stamp);
            Assert.Equal(RubricaPng, stamp!.ImageBytes);
        }
    }

    [Fact] // sem UseRubrica -> Stamp.ImageBytes null (carimbo padrão) MESMO havendo rubrica salva no config.
    public async Task ConfirmSignatureStampAsync_WithoutRubrica_ImageBytesNull()
    {
        var config = new AppConfig(NewConfigDir());
        config.SalvarRubrica(RubricaPng);
        var (doc, _, engine, dialog, _, _, _, cert) = BuildForSigning(config: config);
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: true, PlaceStamp: true); // UseRubrica default false
            await d.SignCommand.ExecuteAsync(null);
            DrawBox(d, 100, 100, 300, 200);
            await d.ConfirmSignatureStampAsync();

            Assert.Null(engine.LastRequest!.Stamp!.ImageBytes);
        }
    }

    [Fact] // UseRubrica mas rubrica removida entre abrir o diálogo e placar -> aborta com aviso, não entra
    // em modo de colocação, motor nunca é chamado.
    public async Task Sign_UseRubrica_ButNoRubricaSaved_AbortsWithNotice()
    {
        var config = new AppConfig(NewConfigDir()); // SEM rubrica salva
        var (doc, _, engine, dialog, _, errors, _, cert) = BuildForSigning(config: config);
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: true, PlaceStamp: true, UseRubrica: true);
            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(AnnotationTool.None, d.ActiveTool); // não entrou em colocação
            Assert.Equal(0, engine.SignCallCount);
            Assert.Single(errors);
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
            Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase); // sanity

            SelectSomeText(d.Pages[0]);
            Assert.True(d.HasActiveSelection); // sanity
            await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);
            Assert.Equal(1, editor.AddAnnotationCallCount); // sanity: a mutação REALMENTE aconteceu

            Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase); // já resetado (COM aviso, fix pós-revisão)
            // o aviso já disparou AQUI (dentro de OnSessionApplied, síncrono com ApplyMarkupCommand acima)
            // -- exatamente 1 vez, com a MESMA mensagem que o cinto de SignCoreAsync usa.
            var noticeFromMutation = Assert.Single(errors);
            Assert.Equal(
                "O documento foi alterado durante o posicionamento do carimbo. A assinatura foi cancelada — assine novamente.",
                noticeFromMutation);

            await d.ConfirmSignatureStampAsync();

            Assert.Equal(0, engine.SignCallCount); // motor NUNCA alcançado
            Assert.Single(errors); // NENHUM aviso duplicado no Confirmar -- continua sendo só o de OnSessionApplied
            Assert.Empty(infos);
            Assert.False(d.IsSignedDocument);
            Assert.Equal(AnnotationTool.None, d.ActiveTool); // RESET completo -- não "tente de novo" com o pending antigo
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

    // ---- Task 2 (Plano 7, fix CRÍTICO pós-revisão): NeedsSaveAs precisa relocar (Salvar Como) ANTES de
    // assinar — sem isto, `SignCoreAsync`/`Session.CommitSigned` gravaria o PDF ASSINADO de volta no
    // MESMO arquivo temporário em `%TEMP%\mPDF\open-<guid>\`, e `MarkSaved` limparia `IsDirty`: a
    // assinatura (documento LEGAL) desapareceria em silêncio na próxima limpeza do SO — achado end-to-end
    // confirmado pelo revisor, o caso de uso CENTRAL do Plano 7 ("abrir foto -> assinar").

    private string NewSaveAsTargetPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mpdf-sign-relocated-{Guid.NewGuid():N}.pdf");
        _tempFilesToDelete.Add(path);
        return path;
    }

    [Fact] // (a) SaveAs roda ANTES do diálogo de assinatura E antes do motor -- ordem, não só CallCount.
    public async Task Sign_NeedsSaveAs_RelocatesBeforeSignDialogAndEngine()
    {
        var callOrder = new List<string>();
        var target = NewSaveAsTargetPath();
        var dialogs = new FakeSaveAsDialogService(target, callOrder);
        var (doc, _, engine, dialog, _, _, _, cert) = BuildForSigning(dialogs: dialogs);
        using var d = doc;
        using (cert)
        {
            d.NeedsSaveAs = true;
            dialog.CallOrder = callOrder;
            engine.CallOrder = callOrder;

            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "saveAs", "signDialog", "engine" }, callOrder);
            Assert.Equal(1, dialogs.PickPdfToSaveAsCallCount);
        }
    }

    [Fact] // (b) diálogo de relocação CANCELADO -- Sign aborta LIMPO: funil nunca arma, motor nunca
    // chamado, diálogo de assinatura nunca chamado, documento intocado (mesmo contrato de recusa já
    // usado por "doc sujo -> confirmação recusada" acima).
    public async Task Sign_NeedsSaveAs_SaveAsCancelled_AbortsCleanly_FunnelNeverArmed()
    {
        var dialogs = new FakeSaveAsDialogService(saveAsResult: null);
        var (doc, _, engine, dialog, confirm, errors, _, cert) = BuildForSigning(dialogs: dialogs);
        using var d = doc;
        using (cert)
        {
            d.NeedsSaveAs = true;
            var originalPath = d.Session.FilePath;

            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(1, dialogs.PickPdfToSaveAsCallCount);
            Assert.Equal(0, confirm.CallCount); // nem chegou no dirty-check/forced-save existente
            Assert.Equal(0, dialog.CallCount); // diálogo de assinatura NUNCA aberto
            Assert.Equal(0, engine.SignCallCount);
            Assert.False(d.Session.IsEditInFlight); // funil NUNCA armado
            Assert.True(d.NeedsSaveAs); // ainda precisa relocar
            Assert.Equal(originalPath, d.Session.FilePath); // nada mudou
            Assert.Empty(errors); // cancelar não é uma falha -- sem notificação de erro
        }
    }

    [Fact] // (c) integração: motor REAL + certificado efêmero REAL -- assina no caminho ESCOLHIDO
    // (nunca no temp original), NeedsSaveAs zera, estado pós-assinatura é "limpo" (fecharia sem prompt).
    public async Task Sign_NeedsSaveAs_Accepted_Integration_SignsAtChosenPath_NeedsSaveAsCleared()
    {
        var tempPath = CopyFixtureToTemp(); // simula o PDF temporário em %TEMP%\mPDF\open-<guid>\
        var originalTempBytes = File.ReadAllBytes(tempPath);
        var target = NewSaveAsTargetPath();
        using var cert = CreateEphemeralRsaCertificate();
        var realEngine = SigningEngineFactory.Create();
        var dialogs = new FakeSaveAsDialogService(target);
        var signDialog = new FakeSignDialogService(new SignDialogResult(cert, "Aprovação", "Escritório", ApplyDocMdp: true, PlaceStamp: false));

        using var d = new DocumentViewModel(
            DocumentSession.Open(tempPath),
            editor: PdfEditorFactory.Create(), // real -- HasSignatures precisa ler o PDF de verdade
            config: new AppConfig(NewConfigDir()),
            notifyError: _ => { }, notifyInfo: _ => { },
            dialogs: dialogs,
            signDialog: signDialog, signingEngine: realEngine,
            confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
            listSigningCertificates: () => new[] { FakeCertificateInfo(cert) });
        d.NeedsSaveAs = true;

        await d.SignCommand.ExecuteAsync(null);

        // CHOSEN-PATH assertion (o cerne do fix): o arquivo assinado está no destino que o usuário
        // escolheu no diálogo de "Salvar como" -- NUNCA no arquivo temp original.
        Assert.Equal(target, d.Session.FilePath);
        Assert.True(File.Exists(target));
        var signaturesAtTarget = realEngine.ReadSignatures(File.ReadAllBytes(target));
        Assert.Single(signaturesAtTarget);
        Assert.True(signaturesAtTarget[0].IntegrityValid);
        Assert.Equal(File.ReadAllBytes(target), d.Session.Snapshot); // gravado atomicamente no destino ESCOLHIDO

        // o arquivo TEMP original nunca foi tocado por CommitSigned -- continua exatamente como estava
        // (a fixture sem assinatura nenhuma copiada por CopyFixtureToTemp), prova de que a assinatura
        // NUNCA foi parar em %TEMP%.
        Assert.Equal(originalTempBytes, File.ReadAllBytes(tempPath));

        // estado pós-assinatura "limpo": NeedsSaveAs zerado + IsDirty falso (CommitSigned sempre marca
        // salvo) -- é exatamente o par que MainViewModel.TryResolveDirtyDocument/CanSave leem pra
        // decidir "fecha sem perguntar nada"/"Salvar desabilitado" (comportamento normal de doc limpo).
        Assert.False(d.NeedsSaveAs);
        Assert.False(d.IsDirty);
        Assert.True(d.IsSignedDocument);
    }

    [Fact] // (d) regressão: documento NÃO temp-backed (NeedsSaveAs=false, o caso comum) -- fluxo
    // BYTE-IDÊNTICO ao de antes desta fix, nenhum diálogo novo, nenhuma chamada a PickPdfToSaveAs.
    public async Task Sign_NotNeedingSaveAs_NoRelocationPrompt_UnchangedFlow()
    {
        var dialogs = new FakeSaveAsDialogService(saveAsResult: "NUNCA_DEVERIA_SER_USADO");
        var (doc, _, engine, dialog, confirm, _, _, cert) = BuildForSigning(dialogs: dialogs);
        using var d = doc;
        using (cert)
        {
            Assert.False(d.NeedsSaveAs);
            var originalPath = d.Session.FilePath;

            await d.SignCommand.ExecuteAsync(null);

            Assert.Equal(0, dialogs.PickPdfToSaveAsCallCount); // SaveAs NUNCA invocado
            Assert.Equal(0, confirm.CallCount); // documento limpo -- mesmo fluxo de Sign_DocClean_NeverPromptsToSave
            Assert.Equal(1, dialog.CallCount);
            Assert.Equal(1, engine.SignCallCount);
            Assert.Equal(originalPath, d.Session.FilePath); // caminho nunca mudou
        }
    }

    // ---- integração: motor REAL + certificado efêmero REAL, pelo fluxo completo do VM ---------------

    [Fact] // ponta a ponta pelo COMANDO real: motor de PRODUÇÃO (SigningEngineFactory.Create(), o mesmo
    // PadesSigningEngine que o app usa), certificado RSA efêmero (NUNCA um certificado real do
    // usuário/repositório). Assina uma vez -> 1 assinatura íntegra no ARQUIVO gravado; assina de novo
    // (incremental, mesmo VM/sessão) -> 2 assinaturas, as DUAS íntegras (invariante central do plano).
    public async Task Sign_Integration_RealEngineWithEphemeralCertificates_ProducesTwoValidIncrementalSignatures()
    {
        var tmp = CopyFixtureToTemp();
        using var cert1 = CreateEphemeralRsaCertificate("Signatario Um");
        using var cert2 = CreateEphemeralRsaCertificate("Signatario Dois");
        var realEngine = SigningEngineFactory.Create();
        var dialog = new FakeSignDialogService(new SignDialogResult(cert1, "Aprovação", "Escritório", ApplyDocMdp: true, PlaceStamp: false));

        using var d = new DocumentViewModel(
            DocumentSession.Open(tmp),
            editor: PdfEditorFactory.Create(), // real -- HasSignatures precisa ler o PDF de verdade
            config: new AppConfig(NewConfigDir()),
            notifyError: _ => { }, notifyInfo: _ => { },
            signDialog: dialog, signingEngine: realEngine,
            confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
            listSigningCertificates: () => new[] { FakeCertificateInfo(cert1) });

        await d.SignCommand.ExecuteAsync(null);
        Assert.True(d.IsSignedDocument);

        var afterFirst = realEngine.ReadSignatures(d.Session.Snapshot);
        Assert.Single(afterFirst);
        Assert.True(afterFirst[0].IntegrityValid);
        Assert.Equal(DocMdpLevel.FormsAndSignatures, afterFirst[0].Certification);
        Assert.Equal(File.ReadAllBytes(tmp), d.Session.Snapshot); // gravado atomicamente no disco

        // 2ª assinatura, INCREMENTAL, mesmo VM/sessão -- sem DocMDP (doc já certificado).
        dialog.Result = new SignDialogResult(cert2, null, null, ApplyDocMdp: false, PlaceStamp: false);
        Assert.True(d.SignCommand.CanExecute(null)); // contrato central: doc JÁ assinado continua assinável

        await d.SignCommand.ExecuteAsync(null);

        var afterSecond = realEngine.ReadSignatures(d.Session.Snapshot);
        Assert.Equal(2, afterSecond.Count);
        Assert.All(afterSecond, s => Assert.True(s.IntegrityValid)); // a 1ª CONTINUA íntegra -- invariante central
        Assert.Equal(File.ReadAllBytes(tmp), d.Session.Snapshot);

        // histórico de desfazer/refazer limpo pelas DUAS assinaturas (decisão registrada).
        Assert.False(d.CanUndo);
        Assert.False(d.CanRedo);
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
    public async Task Sign_Integration_StampBoxDrawAdjustConfirm_RendersExactlyInsideFinalRect()
    {
        var tmp = CopyFixtureToTemp();
        using var cert = CreateEphemeralRsaCertificate();
        var realEngine = SigningEngineFactory.Create();
        var dialog = new FakeSignDialogService(new SignDialogResult(cert, "Aprovação", "Escritório", ApplyDocMdp: true, PlaceStamp: true));

        using var d = new DocumentViewModel(
            DocumentSession.Open(tmp),
            editor: PdfEditorFactory.Create(), // real -- HasSignatures precisa ler o PDF de verdade
            config: new AppConfig(NewConfigDir()),
            notifyError: _ => { }, notifyInfo: _ => { },
            signDialog: dialog, signingEngine: realEngine,
            confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
            listSigningCertificates: () => new[] { FakeCertificateInfo(cert) });

        await d.SignCommand.ExecuteAsync(null);
        Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool);

        // Desenha um rect CONHECIDO -- longe de qualquer conteúdo pré-existente da fixture, mesmo canto
        // do roteiro de validação manual do Marco 0 (docs/superpowers/marco0-protocolo.md).
        await d.BeginStampBoxPlacementAsync(0, new PdfPoint(300, 50));
        d.UpdateDrawTo(new PdfPoint(500, 150)); // 200x100pt -- bem acima do mínimo 60x20pt
        d.EndStampDraw();
        Assert.Equal(StampPlacementPhase.Adjusting, d.StampPlacementPhase);

        // AJUSTA (mover + redimensionar) pra um rect FINAL DIFERENTE do desenhado -- é isto que a
        // aceitação por pixel precisa provar: o motor recebe o AJUSTADO, não o desenhado original.
        d.MoveBoxBy(new PdfPoint(20, -10));                          // 320,40 - 520,140
        d.ResizeBoxByHandle(StampBoxHandle.Right, new PdfPoint(30, 0)); // 320,40 - 550,140
        var finalRect = d.StampBoxRect;
        Assert.NotEqual(300, finalRect.LeftPt, 0.01); // sanity: realmente é DIFERENTE do desenhado

        var originalBytes = File.ReadAllBytes(tmp); // ANTES de confirmar -- baseline pro diff de pixels

        await d.ConfirmSignatureStampAsync();

        Assert.True(d.IsSignedDocument);
        Assert.Equal(StampPlacementPhase.None, d.StampPlacementPhase);
        Assert.Equal(AnnotationTool.None, d.ActiveTool);
        var signedBytes = d.Session.Snapshot;
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
