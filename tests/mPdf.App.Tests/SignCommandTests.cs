using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
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

    public byte[] Sign(SignRequest request)
    {
        SignGate?.Task.Wait();
        SignCallCount++;
        LastRequest = request;
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

    public SignDialogResult? PromptForSignature(IReadOnlyList<SigningCertificateInfo> certificates, bool allowDocMdp)
    {
        CallCount++;
        LastCertificates = certificates;
        LastAllowDocMdp = allowDocMdp;
        return Result;
    }
}

internal sealed class FakeConfirmSaveBeforeSignService(bool result) : IConfirmSaveBeforeSignService
{
    public int CallCount { get; private set; }
    public bool Confirm(string message) { CallCount++; return result; }
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
        BuildForSigning(bool hasSignatures = false, bool confirmSaveResult = true, string fixture = "fixture-a4.pdf")
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
            config: new AppConfig(NewConfigDir()),
            notifyError: errors.Add,
            notifyInfo: infos.Add,
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

    // ---- com carimbo: modo de colocação -----------------------------------------------------------

    [Fact]
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
            Assert.False(d.Session.IsEditInFlight); // funil só arma no CLIQUE (PlaceSignatureStampAtAsync)
        }
    }

    [Fact]
    public async Task PlaceSignatureStampAtAsync_CommitsWithClampedStampRect()
    {
        var (doc, _, engine, dialog, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, "Motivo", "Local", ApplyDocMdp: true, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);
            Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool);

            await d.PlaceSignatureStampAtAsync(0, 100, 100); // bem dentro da página A4 -- sem clamp

            Assert.Empty(errors);
            Assert.Equal(1, engine.SignCallCount);
            var stamp = engine.LastRequest!.Stamp;
            Assert.NotNull(stamp);
            Assert.Equal(0, stamp!.PageIndex);
            Assert.Equal(100, stamp.Rect.LeftPt);
            Assert.Equal(100, stamp.Rect.BottomPt);
            Assert.Equal(280, stamp.Rect.RightPt); // 100 + DefaultStampWidthPt (180)
            Assert.Equal(160, stamp.Rect.TopPt);   // 100 + DefaultStampHeightPt (60)
            Assert.True(d.IsSignedDocument);
            Assert.Equal(AnnotationTool.None, d.ActiveTool); // one-shot: desativa após commit
            Assert.Single(infos);
        }
    }

    [Fact] // gate de rotação (exemplar: PlaceStampAtAsync) -- recusa com o MESMO aviso pt-BR, ferramenta
    // continua ativa (usuário pode tentar outra página), motor NUNCA alcançado.
    public async Task PlaceSignatureStampAtAsync_RotatedPage_RefusesWithNotice_ToolStaysActive()
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

            await d.PlaceSignatureStampAtAsync(0, 100, 100);

            Assert.Equal(0, engine.SignCallCount);
            Assert.Contains(errors, e => e.Contains("Página girada"));
            Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool); // continua ativa
            Assert.False(d.IsSignedDocument);
            Assert.False(d.Session.IsEditInFlight); // funil solto -- pode tentar outra página
        }
    }

    [Fact] // clique chega com a ferramenta ERRADA ativa (ex.: cancelou e ligou outra coisa) -> no-op.
    public async Task PlaceSignatureStampAtAsync_WrongActiveTool_NoOp()
    {
        var (doc, _, engine, _, _, _, _, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            Assert.Equal(AnnotationTool.None, d.ActiveTool);
            await d.PlaceSignatureStampAtAsync(0, 100, 100);
            Assert.Equal(0, engine.SignCallCount);
        }
    }

    // ---- BELT: mutação durante a janela de colocação (revisão do coordenador, achado real) ----------

    private static void SelectSomeText(PageViewModel page)
    {
        page.BeginSelection(new Point(10, 10));
        page.UpdateSelection(new Point(300, 20));
    }

    [Fact] // "placement-window mutation gap": entre o OK do diálogo e o clique, o funil NÃO está armado
    // -- QUALQUER mutador (aqui, ApplyMarkup, um comando REAL de produção, não um stub) continua
    // habilitado e PODE rodar nessa janela. O cinto estrutural em SignCoreAsync (comparação de
    // referência de Session.Snapshot) tem que pegar isso e recusar, mesmo sem nenhuma lista de comandos
    // a desabilitar -- cobre ApplyMarkup/FlattenForm/ApplyFormValues/Undo/Redo/anotações igualmente,
    // porque TODOS trocam a referência de Snapshot da mesma forma (Session.Apply).
    public async Task PlaceSignatureStampAtAsync_DocumentMutatedDuringPlacementWindow_RefusesAndResetsPlacement()
    {
        var (doc, editor, engine, dialog, _, errors, infos, cert) = BuildForSigning();
        using var d = doc;
        using (cert)
        {
            dialog.Result = new SignDialogResult(cert, null, null, ApplyDocMdp: false, PlaceStamp: true);
            await d.SignCommand.ExecuteAsync(null);
            Assert.Equal(AnnotationTool.SignatureStamp, d.ActiveTool);

            // Mutação REAL na janela sem funil -- mesmo caminho de produção de
            // ApplyMarkupCommand_Highlight_AppliesEdit... em DocumentViewModelTests (seleção real +
            // comando real), NÃO uma troca manual de Session.Snapshot.
            SelectSomeText(d.Pages[0]);
            Assert.True(d.HasActiveSelection); // sanity
            await d.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);
            Assert.Equal(1, editor.AddAnnotationCallCount); // sanity: a mutação REALMENTE aconteceu

            await d.PlaceSignatureStampAtAsync(0, 100, 100);

            Assert.Equal(0, engine.SignCallCount); // motor NUNCA alcançado
            var msg = Assert.Single(errors);
            Assert.Equal(
                "O documento foi alterado durante o posicionamento do carimbo. A assinatura foi cancelada — assine novamente.",
                msg);
            Assert.Empty(infos);
            Assert.False(d.IsSignedDocument);
            Assert.Equal(AnnotationTool.None, d.ActiveTool); // RESET completo -- não "tente outra página"
            Assert.False(d.Session.IsEditInFlight); // funil solto (armado só durante o clique em si)
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
}
