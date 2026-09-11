using System.IO;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

/// Aba "Assinaturas" (Task 4, Plano 4) — cache/projeção de linhas (VM com `FakeSigningEngine`, exemplar:
/// testes de `Outline`/`RefreshOutlineAsync` em DocumentViewModelTests.cs) + seleção/navegação/destaque
/// do carimbo (exemplar: `SelectFormField`/painel de Campos) + 1 teste de integração ponta-a-ponta
/// (motor REAL + certificado efêmero REAL, exemplar: SignCommandTests — CopyFixtureToTemp discipline,
/// ver doc XML da classe lá pro porquê: `Sign` escreve em disco de verdade).
public class SignaturePanelTests : IDisposable
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

    private string CopyFixtureToTemp(string fixtureName = "fixture-a4.pdf")
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-sigpanel-{Guid.NewGuid():N}.pdf");
        File.Copy(Path.Combine(Fixtures.Root, fixtureName), tmp);
        _tempFilesToDelete.Add(tmp);
        _tempFilesToDelete.Add(tmp + ".bak");
        return tmp;
    }

    private string NewConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mpdf-sigpanel-cfg-{Guid.NewGuid():N}");
        _tempDirsToDelete.Add(dir);
        return dir;
    }

    private static (DocumentViewModel doc, FakePdfEditor editor, FakeSigningEngine engine, List<string> errors) BuildForSignaturesPanel()
    {
        var editor = new FakePdfEditor();
        var engine = new FakeSigningEngine();
        var errors = new List<string>();
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")),
            editor: editor,
            signingEngine: engine,
            notifyError: errors.Add);
        return (doc, editor, engine, errors);
    }

    private static SignatureInfo BuildInfo(
        string fieldName = "Assinatura1", string signerName = "Fulano de Tal", string? document = null,
        DateTimeOffset? signedAt = null, bool coversWhole = true, bool integrityValid = true,
        bool chainTrusted = false, string? reason = null, DocMdpLevel certification = DocMdpLevel.None,
        int? stampPageIndex = null, PdfQuad? stampRect = null) =>
        new(fieldName, signerName, document, "ETSI.CAdES.detached", signedAt, coversWhole, integrityValid,
            chainTrusted, reason, certification, stampPageIndex, stampRect);

    // ==== Cache: HasSignatures / SignatureRows (exemplar: HasOutline/Outline) ========================

    [Fact] // sem NENHUM refresh ainda (construtor não bombeia o Dispatcher em teste puro) — mesmo
    // estado "ainda carregando" que HasOutline/HasFormFields já documentam.
    public void HasSignatures_BeforeAnyRefresh_IsFalse()
    {
        var (doc, _, _, _) = BuildForSignaturesPanel();
        using var d = doc;

        Assert.False(d.HasSignatures);
        Assert.Empty(d.SignatureRows);
    }

    [Fact]
    public async Task SignatureRows_AfterRefresh_ProjectsFieldsFromReader()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        var signedAt = new DateTimeOffset(2026, 3, 10, 14, 30, 0, TimeSpan.Zero);
        engine.ReadSignaturesResult = new[]
        {
            BuildInfo(signerName: "Joao da Silva", document: "01672780838", signedAt: signedAt,
                reason: "Concordo com os termos", integrityValid: true, chainTrusted: true)
        };

        await d.RefreshSignaturesAsync();

        Assert.True(d.HasSignatures);
        var row = Assert.Single(d.SignatureRows);
        Assert.Equal("Joao da Silva", row.SignerName);
        Assert.Equal("e-CPF", row.DocumentKindLabel);
        Assert.Equal("01672780838", row.DocumentNumber);
        Assert.Equal("Motivo: Concordo com os termos", row.ReasonLabel);
        Assert.Equal("✔ Íntegra", row.IntegrityLabel);
        Assert.Equal("Cadeia confiável neste computador: sim", row.ChainTrustedLabel);
    }

    [Fact] // brief: "empty -> HasSignatures false (empty-state binding)" — resultado VAZIO explícito
    // do motor (documento sem assinatura), não só o default pré-refresh do teste acima.
    public async Task HasSignatures_EmptyReaderResult_IsFalse()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = Array.Empty<SignatureInfo>();

        await d.RefreshSignaturesAsync();

        Assert.False(d.HasSignatures);
    }

    [Fact] // refresh no Applied — CommitSigned dispara Applied diretamente (Session.CommitSigned),
    // então assinar atualiza o painel automaticamente; aqui prova só que RefreshSignaturesAsync
    // reflete o SNAPSHOT CORRENTE, não um resultado travado (mesmo padrão de
    // RefreshOutlineAsync_AfterApply_ReflectsNewSnapshot).
    public async Task SignatureRows_AfterApply_ReflectsNewSnapshot()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(signerName: "Original") };
        await d.RefreshSignaturesAsync();
        Assert.Equal("Original", d.SignatureRows[0].SignerName);

        d.Session.Apply(Fixtures.ThirtyPages()); // troca o snapshot
        engine.ReadSignaturesResult = new[] { BuildInfo(signerName: "Atualizado") };

        await d.RefreshSignaturesAsync();

        Assert.Single(d.SignatureRows);
        Assert.Equal("Atualizado", d.SignatureRows[0].SignerName);
    }

    [Fact] // brief: "data não disponível" pra SignedAt nulo (/M ausente ou inválido no PDF).
    public async Task SignatureRows_NullSignedAt_DateLabelIsNaoDisponivel()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(signedAt: null) };

        await d.RefreshSignaturesAsync();

        Assert.Equal("data não disponível", d.SignatureRows[0].DateLabel);
    }

    [Fact] // certificado fora da convenção do CN "NOME:CPF|CNPJ" (Document nulo) -> sem ícone/número,
    // nunca um texto errado.
    public async Task SignatureRows_NullDocument_KindLabelIsEmptyAndNumberIsNull()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(document: null) };

        await d.RefreshSignaturesAsync();

        Assert.Equal("", d.SignatureRows[0].DocumentKindLabel);
        Assert.Null(d.SignatureRows[0].DocumentNumber);
    }

    [Fact] // 14 dígitos -> e-CNPJ (brief: "e-CPF/e-CNPJ se derivável do tamanho de Document").
    public async Task SignatureRows_Cnpj14Digits_KindLabelIsECnpj()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(document: "12345678000199") };

        await d.RefreshSignaturesAsync();

        Assert.Equal("e-CNPJ", d.SignatureRows[0].DocumentKindLabel);
    }

    // ---- Revisão (item 1): CPF/CNPJ mascarado, visível no painel (DocumentNumberLabel) --------------

    [Fact]
    public async Task SignatureRows_Cpf_DocumentNumberLabelIsMasked()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(document: "01672780838") };

        await d.RefreshSignaturesAsync();

        Assert.Equal("CPF 016.727.808-38", d.SignatureRows[0].DocumentNumberLabel);
        Assert.True(d.SignatureRows[0].HasDocumentNumber);
    }

    [Fact]
    public async Task SignatureRows_Cnpj_DocumentNumberLabelIsMasked()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(document: "12345678000199") };

        await d.RefreshSignaturesAsync();

        Assert.Equal("CNPJ 12.345.678/0001-99", d.SignatureRows[0].DocumentNumberLabel);
        Assert.True(d.SignatureRows[0].HasDocumentNumber);
    }

    [Fact] // null -> row absent, nunca um rótulo vazio (revisão, item 1).
    public async Task SignatureRows_NullDocument_DocumentNumberLabelIsNullAndHasDocumentNumberFalse()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(document: null) };

        await d.RefreshSignaturesAsync();

        Assert.Null(d.SignatureRows[0].DocumentNumberLabel);
        Assert.False(d.SignatureRows[0].HasDocumentNumber);
    }

    // ---- Revisão (item 2): SignerLine composto no VM (headless-testável), sem parênteses vazios ------

    [Fact]
    public async Task SignerLine_WithDocumentKind_IncludesParenthetical()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(signerName: "Fulano de Tal", document: "01672780838") };

        await d.RefreshSignaturesAsync();

        Assert.Equal("Fulano de Tal  (e-CPF)", d.SignatureRows[0].SignerLine);
    }

    [Fact] // certificado fora da convenção RFB (Document nulo, ex.: certificados efêmeros de teste) ->
    // SEM parênteses vazios "Fulano  ()" (achado da revisão) — só o nome cru.
    public async Task SignerLine_NullDocument_NoParenthetical()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(signerName: "Fulano de Tal", document: null) };

        await d.RefreshSignaturesAsync();

        Assert.Equal("Fulano de Tal", d.SignatureRows[0].SignerLine);
    }

    [Fact] // I2 (revisão, Task 6/Plano 4): multi-assinatura — semântica INALTERADA quanto a QUANDO cada
    // rótulo aparece (1ª não cobre tudo, última cobre), só a REDAÇÃO do caso "não cobre tudo" mudou —
    // ordinal/total sobrevivem SÓ como identificação (" — assinatura N de M"), nunca mais como
    // "revisão N de M" (que insinuava contar revisões, não assinaturas — ver XML doc de CoverageLabel).
    public async Task SignatureRows_MultiSignature_CoverageLabelSemanticsUnchanged()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[]
        {
            BuildInfo(fieldName: "Assinatura1", coversWhole: false),
            BuildInfo(fieldName: "Assinatura2", coversWhole: true),
        };

        await d.RefreshSignaturesAsync();

        Assert.Equal(
            "Cobre uma revisão anterior do documento (houve adições depois desta assinatura) — assinatura 1 de 2",
            d.SignatureRows[0].CoverageLabel);
        Assert.Equal("Cobre o documento inteiro", d.SignatureRows[1].CoverageLabel);
    }

    [Fact] // I2 (revisão, Task 6/Plano 4, o achado central): documento com 1 ÚNICA assinatura, mas
    // CoversWholeDocument=false (o caso real de um preenchimento incremental TER acontecido depois de
    // assinar — a revisão do preenchimento ficou de fora da assinatura) — o rótulo ANTIGO ("Cobre a
    // revisão 1 de 1") lia como "cobre tudo" exatamente no momento em que parou de cobrir. O NOVO
    // rótulo nunca numera revisão nenhuma quando _total==1 (nenhuma outra assinatura pra identificar),
    // só afirma o fato verificável, e NUNCA implica cobertura total.
    public async Task SignatureRows_SingleSignatureCoversWholeDocumentFalse_ShowsPartialWording_NeverImpliesFullCoverage()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(fieldName: "Assinatura1", coversWhole: false) };

        await d.RefreshSignaturesAsync();

        var label = d.SignatureRows[0].CoverageLabel;
        Assert.Equal("Cobre uma revisão anterior do documento (houve adições depois desta assinatura)", label);
        Assert.DoesNotContain("documento inteiro", label);
        Assert.DoesNotContain("1 de 1", label); // nenhum resquício da redação antiga que insinuava "completo"
    }

    [Fact] // I2: assinatura única e NUNCA tocada depois (CoversWholeDocument=true) — continua a
    // redação de cobertura total, sem nenhum ordinal (nada pra identificar entre assinaturas).
    public async Task SignatureRows_SingleSignatureUntouched_ShowsWholeDocumentWording()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(fieldName: "Assinatura1", coversWhole: true) };

        await d.RefreshSignaturesAsync();

        Assert.Equal("Cobre o documento inteiro", d.SignatureRows[0].CoverageLabel);
    }

    [Fact] // brief: "'Certificada: alterações restritas' quando FormsAndSignatures".
    public async Task SignatureRows_CertificationFormsAndSignatures_ShowsLabel()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(certification: DocMdpLevel.FormsAndSignatures) };

        await d.RefreshSignaturesAsync();

        Assert.Equal("Certificada: alterações restritas", d.SignatureRows[0].CertificationLabel);
        Assert.True(d.SignatureRows[0].HasCertificationLabel);
    }

    [Fact] // None (assinatura de aprovação, caso comum) -> SEM rótulo nenhum, não "não certificada".
    public async Task SignatureRows_CertificationNone_NoLabel()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(certification: DocMdpLevel.None) };

        await d.RefreshSignaturesAsync();

        Assert.Null(d.SignatureRows[0].CertificationLabel);
        Assert.False(d.SignatureRows[0].HasCertificationLabel);
    }

    // ==== Seleção: clique na assinatura com carimbo -> ScrollToPage + destaque (gate de rotação) =====

    [Fact]
    public async Task SelectSignatureCommand_RowWithStamp_RaisesScrollToPageRequested()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(stampPageIndex: 0, stampRect: new PdfQuad(10, 10, 30, 30)) };
        await d.RefreshSignaturesAsync();
        int? scrolledTo = null;
        d.ScrollToPageRequested += idx => scrolledTo = idx;

        d.SelectSignatureCommand.Execute(d.SignatureRows[0]);

        Assert.Equal(0, scrolledTo);
        Assert.Same(d.SignatureRows[0], d.SelectedSignature);
    }

    [Fact]
    public async Task SelectSignature_PageNotRotated_HighlightsStamp()
    {
        var (doc, editor, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        editor.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        editor.PageRotationsResult = new[] { 0 };
        await d.RefreshAnnotationsByPageAsync(); // popula _pageRotations (cache compartilhado)
        var rect = new PdfQuad(10, 10, 30, 30);
        engine.ReadSignaturesResult = new[] { BuildInfo(stampPageIndex: 0, stampRect: rect) };
        await d.RefreshSignaturesAsync();

        d.SelectSignatureCommand.Execute(d.SignatureRows[0]);

        var expected = PageViewModel.PointRectToScreenRect(10, 10, 30, 30, d.Zoom, d.Pages[0].HeightPt);
        Assert.True(d.Pages[0].HasSignatureStampHighlight);
        Assert.Equal(expected, d.Pages[0].SignatureStampHighlightRect);
    }

    [Fact] // GATE DE ROTAÇÃO (revisão da Task 4): mesma política de SelectFormField — navegação é livre
    // de coordenadas (ScrollToPageRequested só usa o ÍNDICE, nunca um retângulo), então continua
    // disparando numa página girada; só o DESTAQUE fica suprimido (o retângulo ficaria geometricamente
    // errado). Sem aviso nenhum — mesmo silêncio do painel de Campos, não uma recusa.
    public async Task SelectSignature_PageRotated_NavigatesAndSelectsButSuppressesHighlight()
    {
        var (doc, editor, engine, errors) = BuildForSignaturesPanel();
        using var d = doc;
        editor.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        editor.PageRotationsResult = new[] { 90 };
        await d.RefreshAnnotationsByPageAsync();
        var rect = new PdfQuad(10, 10, 30, 30);
        engine.ReadSignaturesResult = new[] { BuildInfo(stampPageIndex: 0, stampRect: rect) };
        await d.RefreshSignaturesAsync();
        int? scrolledTo = null;
        d.ScrollToPageRequested += idx => scrolledTo = idx;

        d.SelectSignatureCommand.Execute(d.SignatureRows[0]);

        Assert.Equal(0, scrolledTo); // navegação disparou -- livre de coordenadas
        Assert.Same(d.SignatureRows[0], d.SelectedSignature); // seleção normal, sem recusa
        Assert.False(d.Pages[0].HasSignatureStampHighlight); // só o destaque fica suprimido
        Assert.Empty(errors); // sem aviso -- mesmo silêncio do painel de Campos
    }

    [Fact] // assinatura SEM carimbo (StampPageIndex null, o caso mais comum na prática — "aprovação
    // invisível") -> seleciona (estado "selecionado" na lista), mas nunca navega/destaca — não há
    // nada geométrico pra apontar.
    public async Task SelectSignature_RowWithoutStamp_SelectsButDoesNotScrollOrHighlight()
    {
        var (doc, _, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        engine.ReadSignaturesResult = new[] { BuildInfo(stampPageIndex: null, stampRect: null) };
        await d.RefreshSignaturesAsync();
        int? scrolledTo = null;
        d.ScrollToPageRequested += idx => scrolledTo = idx;

        d.SelectSignatureCommand.Execute(d.SignatureRows[0]);

        Assert.Null(scrolledTo);
        Assert.Same(d.SignatureRows[0], d.SelectedSignature);
        Assert.False(d.Pages[0].HasSignatureStampHighlight);
    }

    [Fact] // trocar/limpar a seleção limpa o destaque da página ANTERIOR (exemplar:
    // SelectFormField_SwitchingField_ClearsPreviousPageHighlight).
    public async Task SelectSignature_ClearingSelection_ClearsPreviousPageHighlight()
    {
        var (doc, editor, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        editor.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        editor.PageRotationsResult = new[] { 0 };
        await d.RefreshAnnotationsByPageAsync();
        engine.ReadSignaturesResult = new[] { BuildInfo(stampPageIndex: 0, stampRect: new PdfQuad(10, 10, 30, 30)) };
        await d.RefreshSignaturesAsync();
        d.SelectSignatureCommand.Execute(d.SignatureRows[0]);
        Assert.True(d.Pages[0].HasSignatureStampHighlight);

        d.SelectSignatureCommand.Execute(null);

        Assert.False(d.Pages[0].HasSignatureStampHighlight);
        Assert.Null(d.SelectedSignature);
    }

    [Fact] // Apply (qualquer edição) limpa a seleção de assinatura — mesmo espírito de
    // SessionApply_ClearsSelectedFormField (o carimbo cacheado pode não refletir mais o documento vivo).
    public async Task SessionApply_ClearsSelectedSignature()
    {
        var (doc, editor, engine, _) = BuildForSignaturesPanel();
        using var d = doc;
        editor.ReadAnnotationsResult = Array.Empty<AnnotationData>();
        editor.PageRotationsResult = new[] { 0 };
        await d.RefreshAnnotationsByPageAsync();
        engine.ReadSignaturesResult = new[] { BuildInfo(stampPageIndex: 0, stampRect: new PdfQuad(10, 10, 30, 30)) };
        await d.RefreshSignaturesAsync();
        d.SelectSignatureCommand.Execute(d.SignatureRows[0]);
        Assert.NotNull(d.SelectedSignature);

        d.Session.Apply(Fixtures.ThirtyPages());

        Assert.Null(d.SelectedSignature);
    }

    // ==== Integração: motor REAL + certificado efêmero REAL, pelo fluxo completo do VM ===============

    [Fact] // ponta a ponta: assina com o motor de PRODUÇÃO (SigningEngineFactory.Create()) e um
    // certificado RSA efêmero (NUNCA um certificado real do usuário/repositório) -> o cache do painel
    // (RefreshSignaturesAsync) mostra 1 assinatura com os campos corretos. Exemplar: SignCommandTests.
    // Sign_Integration_RealEngineWithEphemeralCertificates_ProducesTwoValidIncrementalSignatures.
    public async Task Sign_Integration_RealEngineWithEphemeralCertificate_PanelCacheShowsSignatureWithCorrectFields()
    {
        var tmp = CopyFixtureToTemp();
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate("Fulano de Tal");
        var realEngine = SigningEngineFactory.Create();
        var dialog = new FakeSignDialogService(
            new SignDialogResult(cert, "Aprovação", "Escritório", ApplyDocMdp: true, PlaceStamp: false));

        using var d = new DocumentViewModel(
            DocumentSession.Open(tmp),
            editor: PdfEditorFactory.Create(), // real -- HasSignatures precisa ler o PDF de verdade
            config: new AppConfig(NewConfigDir()),
            notifyError: _ => { }, notifyInfo: _ => { },
            signDialog: dialog, signingEngine: realEngine,
            confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
            listSigningCertificates: () => new[] { new SigningCertificateInfo(cert, true, "Fulano (RSA)", false, false) });

        await d.SignCommand.ExecuteAsync(null);
        Assert.True(d.IsSignedDocument);

        await d.RefreshSignaturesAsync();

        Assert.True(d.HasSignatures);
        var row = Assert.Single(d.SignatureRows);
        Assert.Equal("Fulano de Tal", row.SignerName);
        Assert.Equal("✔ Íntegra", row.IntegrityLabel);
        Assert.Equal("Cobre o documento inteiro", row.CoverageLabel);
        Assert.Equal("Motivo: Aprovação", row.ReasonLabel);
    }
}
