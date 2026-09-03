using System.IO;
using System.Security.Cryptography.X509Certificates;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;
using iText.Signatures;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.Signing.Tests;

public class PadesSigningEngineTests
{
    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();

    private static byte[] SignFixture(
        X509Certificate2? cert = null, VisibleStampSpec? stamp = null,
        DocMdpLevel? certificationLevel = null, string? reason = null, string? location = null,
        byte[]? pdf = null)
    {
        using var certificate = cert ?? TestCertificateFactory.CreateSelfSigned();
        return Engine.Sign(new SignRequest(
            pdf ?? Fixtures.A4(), certificate, reason, location, stamp, certificationLevel));
    }

    /// Plano 9 (Task 3): o texto do carimbo visível novo é desenhado DIRETO no `PdfCanvas` do widget
    /// (`PadesSigningEngine.ApplyVisibleStamp`/`StampAppearanceRenderer`), nunca via o formulário
    /// interativo em si — `PdfTextExtractor`/`GetTextFromPage` padrão do iText só varre o CONTEÚDO DA
    /// PÁGINA, nunca o `/AP/N` (appearance stream) de uma anotação de widget (confirmado ao vivo:
    /// `GetTextFromPage` devolve string vazia pro carimbo). Extrai do stream `/AP/N` do 1º (e único)
    /// widget da página diretamente — mesmo par `PdfCanvasProcessor`/`LocationTextExtractionStrategy`
    /// que o iText usa por baixo de `GetTextFromPage`, só apontado pro XObject certo.
    private static string ExtractStampAppearanceText(byte[] pdf, int pageIndex = 0)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var page = doc.GetPage(pageIndex + 1);
        var annot = Assert.Single(page.GetAnnotations());
        var apDict = annot.GetPdfObject().GetAsDictionary(PdfName.AP);
        var stream = apDict?.GetAsStream(PdfName.N);
        Assert.NotNull(stream);
        var xobj = new PdfFormXObject(stream);
        var strategy = new LocationTextExtractionStrategy();
        var processor = new PdfCanvasProcessor(strategy);
        processor.ProcessContent(stream!.GetBytes(), xobj.GetResources());
        return strategy.GetResultantText();
    }

    [Fact] // bytes corrompidos (não é um PDF) -> PdfSigningException neutra, nunca um tipo iText cru —
    // mesmo espírito de PdfEditorTests.AddAnnotation_CorruptBytes_ThrowsPdfEditingException
    // (mPdf.Editing.Tests). Achado de revisão (ver comentário em PadesSigningEngine.Sign): a 1ª versão
    // desta task tinha `CountSignatures` FORA do try/catch, vazando ITextException cru aqui.
    public void Sign_CorruptBytes_ThrowsPdfSigningException()
    {
        var corrupt = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        using var cert = TestCertificateFactory.CreateSelfSigned();
        Assert.Throws<PdfSigningException>(() =>
            Engine.Sign(new SignRequest(corrupt, cert, null, null, null, null)));
    }

    [Fact] // M4 (review 1) + revisão 2 item 1: PDF protegido por senha (senha de USUÁRIO exigida pra
    // abrir) -> PdfPasswordRequiredException tipada, nunca BadPasswordException crua do iText nem
    // PdfSigningException genérica — o chamador (UI, Tasks 3-5) pode pedir a senha ao usuário em vez
    // de só reportar falha.
    public void Sign_PasswordProtectedPdf_ThrowsPdfPasswordRequiredException()
    {
        var encrypted = Fixtures.PasswordProtected();
        using var cert = TestCertificateFactory.CreateSelfSigned();
        Assert.Throws<PdfPasswordRequiredException>(() =>
            Engine.Sign(new SignRequest(encrypted, cert, null, null, null, null)));
    }

    [Fact] // resultado continua sendo um PDF válido (assinatura sempre em modo append)
    public void Sign_ProducesPdfBytes()
    {
        var signed = SignFixture();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(signed, 0, 4));
    }

    [Fact] // 1 assinatura, íntegra e cobrindo o documento todo
    public void Sign_ProducesOneVerifiableSignatureCoveringWholeDocument()
    {
        var signed = SignFixture();
        var infos = Engine.ReadSignatures(signed);
        var info = Assert.Single(infos);
        Assert.True(info.IntegrityValid);
        Assert.True(info.CoversWholeDocument);
        Assert.Equal("Assinatura1", info.FieldName);
    }

    // --- I6 (review): guarda de regressão do marcador PAdES -------------------------------------

    [Fact] // GUARDA DE REGRESSÃO PERMANENTE: nenhum outro teste desta suíte checava o /SubFilter
    // gravado — um futuro refactor de PadesSigningEngine pra SignDetached (adbe.pkcs7.detached, PAdES
    // básico, NÃO aceito pela ICP-Brasil) passaria pelos outros 20+ testes sem ninguém notar. Este
    // teste trava o valor exato exigido pelo Marco 0 (validado pelo ITI).
    public void Sign_UsesEtsiCadesDetachedSubFilter()
    {
        var signed = SignFixture();
        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Equal("ETSI.CAdES.detached", info.SubFilter);
    }

    // --- Invariante central 1: 2ª assinatura incremental preserva a 1ª ------------------------

    [Fact]
    public void Sign_SecondIncrementalSignature_KeepsFirstSignatureValid()
    {
        using var cert1 = TestCertificateFactory.CreateSelfSigned("Signatario Um");
        using var cert2 = TestCertificateFactory.CreateSelfSigned("Signatario Dois");

        var once = Engine.Sign(new SignRequest(
            Fixtures.A4(), cert1, null, null, null, DocMdpLevel.FormsAndSignatures));
        var twice = Engine.Sign(new SignRequest(
            once, cert2, null, null, null, DocMdpLevel.None));

        var infos = Engine.ReadSignatures(twice);
        Assert.Equal(2, infos.Count);
        Assert.All(infos, i => Assert.True(i.IntegrityValid, $"{i.FieldName} deveria continuar íntegra"));
        Assert.Equal("Assinatura1", infos[0].FieldName);
        Assert.Equal("Assinatura2", infos[1].FieldName);
        Assert.Contains(infos, i => i.SignerName.Contains("Signatario Um"));
        Assert.Contains(infos, i => i.SignerName.Contains("Signatario Dois"));
        // a 1ª assinatura cobre uma revisão anterior; a 2ª (última) cobre o arquivo todo
        Assert.False(infos[0].CoversWholeDocument);
        Assert.True(infos[1].CoversWholeDocument);
    }

    // --- Invariante central 4: DocMDP só na 1ª assinatura ---------------------------------------

    [Fact]
    public void Sign_WithCertificationLevel_SetsDocMdpOnFirstSignature()
    {
        var signed = SignFixture(certificationLevel: DocMdpLevel.FormsAndSignatures);
        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Equal(DocMdpLevel.FormsAndSignatures, info.Certification);
    }

    [Fact] // sem CertificationLevel -> assinatura de aprovação, sem DocMDP
    public void Sign_WithoutCertificationLevel_HasNoDocMdp()
    {
        var signed = SignFixture(certificationLevel: null);
        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.Equal(DocMdpLevel.None, info.Certification);
    }

    [Fact] // motor RECUSA CertificationLevel != None num documento que já tem 1+ assinatura(s)
    public void Sign_CertificationLevelOnAlreadySignedDocument_ThrowsArgumentException()
    {
        var once = SignFixture(certificationLevel: DocMdpLevel.FormsAndSignatures);

        var ex = Assert.Throws<ArgumentException>(() =>
            SignFixture(pdf: once, certificationLevel: DocMdpLevel.FormsAndSignatures));
        Assert.Contains("certificar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // 2ª assinatura (approval, sem certificar) num doc já certificado continua permitida
    public void Sign_ApprovalSignatureOnCertifiedDocument_Succeeds()
    {
        var once = SignFixture(certificationLevel: DocMdpLevel.FormsAndSignatures);
        var twice = SignFixture(pdf: once, certificationLevel: DocMdpLevel.None);
        Assert.Equal(2, Engine.ReadSignatures(twice).Count);
    }

    // --- I1 (revisão final): Sign RECUSA acrescentar sobre um documento certificado P=1 ----------

    [Fact] // ISO 32000 §12.8.2.2/Table 254: P=1 (NO_CHANGES_PERMITTED) proíbe QUALQUER alteração,
    // inclusive uma NOVA assinatura de aprovação (sem CertificationLevel nenhum no pedido) — achado do
    // revisor, mesmo par de
    // FormFillIncrementalEngineTests.SetFormFieldsIncremental_CertifiedNoChangesPermitted_ThrowsPdfSigningException:
    // sem este gate, Sign aceitava a 2ª assinatura sobre um doc P=1 de TERCEIRO (ambas IntegrityValid),
    // mas um validador conforme (o do ITI incluído) reportaria violação de DocMDP depois de arquivado.
    public void Sign_OnP1CertifiedDocument_ThrowsPdfSigningExceptionNamingCertification()
    {
        using var cert1 = TestCertificateFactory.CreateSelfSigned();
        var certifiedP1 = RawSigner.SignWithCertificationLevel(
            Fixtures.A4(), cert1, AccessPermissions.NO_CHANGES_PERMITTED);

        using var cert2 = TestCertificateFactory.CreateSelfSigned("Segundo Signatario");
        var ex = Assert.Throws<PdfSigningException>(() =>
            Engine.Sign(new SignRequest(certifiedP1, cert2, null, null, null, null)));
        Assert.Contains("certificado", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // CONTROLE NEGATIVO (não pode over-close): P=2 — o único nível que este motor EMITE — continua
    // aceitando uma 2ª assinatura de aprovação. Mesmo cenário de
    // Sign_ApprovalSignatureOnCertifiedDocument_Succeeds acima, repetido aqui de propósito, lado a lado
    // com o teste de P=1, pra deixar explícito que o gate novo não ficou estrito demais.
    public void Sign_OnP2CertifiedDocument_StillAllowsApprovalSignature()
    {
        var once = SignFixture(certificationLevel: DocMdpLevel.FormsAndSignatures);
        var twice = SignFixture(pdf: once, certificationLevel: DocMdpLevel.None);
        var infos = Engine.ReadSignatures(twice);
        Assert.Equal(2, infos.Count);
        Assert.All(infos, i => Assert.True(i.IntegrityValid));
    }

    // --- Invariante central 5: certificado ECC é recusado (política RSA-only) ------------------

    [Fact]
    public void Sign_EccCertificate_ThrowsPdfSigningExceptionNamingRsaOnlyPolicy()
    {
        using var eccCert = TestCertificateFactory.CreateEccSelfSigned();
        var ex = Assert.Throws<PdfSigningException>(() =>
            Engine.Sign(new SignRequest(Fixtures.A4(), eccCert, null, null, null, null)));
        Assert.Contains("RSA", ex.Message);
    }

    [Fact] // I1 (review): chave pública RSA, mas privada INACESSÍVEL (token removido/PIN recusado é o
    // caminho mais comum na prática) — causa DISTINTA da política RSA-only acima, mensagem própria
    // nomeando o sintoma real (acesso à chave), não confundindo o usuário fazendo-o trocar de
    // certificado à toa.
    public void Sign_RsaCertificateWithoutAccessiblePrivateKey_ThrowsPdfSigningExceptionAboutKeyAccess()
    {
        using var publicOnly = TestCertificateFactory.CreateRsaPublicKeyOnlyCertificate();
        var ex = Assert.Throws<PdfSigningException>(() =>
            Engine.Sign(new SignRequest(Fixtures.A4(), publicOnly, null, null, null, null)));
        Assert.Contains("chave privada", ex.Message, StringComparison.OrdinalIgnoreCase);
        // não deve reaproveitar a mensagem da política RSA-only (causa diferente)
        Assert.DoesNotContain("aceita somente certificados RSA", ex.Message);
    }

    // --- Invariante central 3: adulteração de 1 byte assinado invalida a integridade -----------

    [Fact]
    public void Sign_TamperedByteInsideSignedRange_MakesIntegrityInvalid()
    {
        var signed = SignFixture();
        var tampered = (byte[])signed.Clone();
        // offset 200: mesmo ponto usado no PoC (poc/mPdf.Poc.Signer.Tests/SignatureVerifierTests.cs) —
        // cai no conteúdo da 1ª revisão (dentro do ByteRange assinado, fora do /Contents em si; append
        // mode preserva o prefixo intacto).
        tampered[200] ^= 0xFF;
        var infos = Engine.ReadSignatures(tampered);
        Assert.Contains(infos, i => !i.IntegrityValid);
    }

    // --- Invariante central 2: carimbo visível na região pedida, 0 px fora dela ----------------

    [Fact]
    public void Sign_WithVisibleStamp_PaintsOnlyInsideStampRegion()
    {
        // Mesmo retângulo do roteiro de validação manual do Marco 0 (docs/superpowers/marco0-protocolo.md,
        // `--carimbo 1,350,50,200,60`) — canto inferior direito de uma página A4, longe de qualquer
        // conteúdo pré-existente da fixture.
        const double Left = 350, Bottom = 50, Right = 550, Top = 110;
        var original = Fixtures.A4();
        var stamp = new VisibleStampSpec(0, new PdfQuad(Left, Bottom, Right, Top));
        var signed = SignFixture(stamp: stamp, reason: "Teste de carimbo visível");

        using var rendererBefore = new PdfDocumentRenderer(original);
        using var rendererAfter = new PdfDocumentRenderer(signed);
        var pageBefore = rendererBefore.RenderPage(0, 1.0);
        var pageAfter = rendererAfter.RenderPage(0, 1.0);
        Assert.Equal(pageBefore.WidthPx, pageAfter.WidthPx);
        Assert.Equal(pageBefore.HeightPx, pageAfter.HeightPx);
        int w = pageBefore.WidthPx, h = pageBefore.HeightPx;

        // Mesmo padrão de prova de
        // PdfEditorTests.RoundTrip_RealSignedFixture_PreservesEverythingOutsideAnnotation (mPdf.Editing.Tests,
        // "probe 3a"): banda de Margin px na BORDA do retângulo é ignorada (antialiasing), núcleo
        // interior precisa ter pixels diferentes (o carimbo em si), e ZERO pixels diferentes fora do
        // retângulo com folga.
        const int Margin = 4;
        int stampLeft = (int)Left, stampRight = (int)Right;
        int stampTop = h - (int)Top, stampBottom = h - (int)Bottom; // Y invertido: origem PDF é inferior-esquerda

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

        // Medido ao vivo (ver task-1-report.md): página A4 renderizada 595x842 px; diffInsideCore=2885,
        // diffOutsidePadded=0 — limiar 100 folgado abaixo do valor real, ainda longe o bastante de 0
        // pra não confundir com ruído de antialiasing.
        Assert.Equal(0, diffOutsidePadded);
        Assert.True(diffInsideCore > 100,
            $"carimbo não visível: só {diffInsideCore} pixels diferentes no núcleo da região");
    }

    [Fact] // I2 (review): PageIndex do carimbo fora do intervalo do documento — antes lançava
    // IndexOutOfRangeException NÃO tipada (achado de revisão); agora recusa com tipo/mensagem pt-BR
    // ANTES de tocar PdfPadesSigner/SetPageNumber, mesmo espírito de PdfEditor.ValidatePageIndex.
    public void Sign_StampPageIndexOutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var stamp = new VisibleStampSpec(99, new PdfQuad(350, 50, 550, 110)); // fixture A4 só tem 1 página (índice 0)
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SignFixture(stamp: stamp));
        Assert.Contains("página", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Revisão 2 (item 5): o ramo `>=` de ValidateStamp já tinha teste acima; o ramo `< 0`
    // (PageIndex negativo) ainda não tinha nenhum — `bool` composto com `||` pode ter metade nunca
    // exercitada mesmo com o outro lado verde. Mesmo espírito de "cobertura de ramo", não só de linha.
    public void Sign_StampPageIndexNegative_ThrowsArgumentOutOfRangeException()
    {
        var stamp = new VisibleStampSpec(-1, new PdfQuad(350, 50, 550, 110));
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SignFixture(stamp: stamp));
        Assert.Contains("página", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory] // M3 (review): retângulo degenerado/invertido (largura ou altura não-positiva) — antes
    // era aceito silenciosamente e produzia um carimbo INVISÍVEL (0 área), nunca um erro; agora recusa
    // explicitamente. Right==Left (largura 0), Top==Bottom (altura 0), Right<Left (invertido).
    [InlineData(550, 50, 550, 110)]  // largura zero
    [InlineData(350, 110, 550, 110)] // altura zero
    [InlineData(550, 50, 350, 110)]  // Right < Left (invertido)
    public void Sign_StampWithDegenerateOrInvertedRect_ThrowsArgumentException(
        double left, double bottom, double right, double top)
    {
        var stamp = new VisibleStampSpec(0, new PdfQuad(left, bottom, right, top));
        Assert.Throws<ArgumentException>(() => SignFixture(stamp: stamp));
    }

    [Fact] // o widget de assinatura aparece na página/posição pedida (prova complementar, nível
    // dicionário) — lido via mPdf.Editing.IPdfEditor.ReadFormFields (contrato NEUTRO, nunca iText
    // direto: este projeto de teste não tem PackageReference pro iText, só o que chega transitivamente
    // pela ProjectReference sem PrivateAssets — ver mPdf.Signing.Tests.csproj). Campo /Sig cai em
    // FormFieldType.Other (ver MapFormFieldType em PdfEditor.cs) mas o WidgetRect ainda é populado.
    public void Sign_WithVisibleStamp_CreatesWidgetAtRequestedRect()
    {
        const double Left = 350, Bottom = 50, Right = 550, Top = 110;
        var stamp = new VisibleStampSpec(0, new PdfQuad(Left, Bottom, Right, Top));
        var signed = SignFixture(stamp: stamp);

        var editor = PdfEditorFactory.Create();
        var field = Assert.Single(editor.ReadFormFields(signed));
        Assert.Equal(FormFieldType.Other, field.Type);
        Assert.NotNull(field.WidgetRect);
        var rect = field.WidgetRect!.Value;
        Assert.Equal(Left, rect.LeftPt, 1);
        Assert.Equal(Right, rect.RightPt, 1);
    }

    // --- M7 (revisão final, escalado a hard gate pré-rollout): colisão de nome com campo de
    // assinatura EM BRANCO pré-existente --------------------------------------------------------

    [Fact] // ACHADO DO REVISOR (probado ao vivo): fixture-formulario.pdf tem um placeholder de
    // assinatura EM BRANCO chamado "assinatura1" (página 1 — ver
    // mPdf.Editing.Tests.PdfEditorTests.ReadFormFields_FixtureFormulario_...). ANTES desta correção,
    // $"Assinatura{existing + 1}" (existing=0 na 1ª assinatura) colidia com esse nome — o iText assinava
    // DENTRO do campo pré-existente, herdando o /Rect ANTIGO dele, e o retângulo escolhido pelo usuário
    // era descartado SILENCIOSAMENTE (mesmo retângulo do probe do revisor: pedido (100,100)-(280,160)).
    public void Sign_DocumentWithBlankPreexistingSignatureField_ChoosesNonCollidingNameAndStampsAtChosenRect()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        const double Left = 100, Bottom = 100, Right = 280, Top = 160;
        var original = Fixtures.Formulario();
        var stamp = new VisibleStampSpec(1, new PdfQuad(Left, Bottom, Right, Top)); // "assinatura1" está na página 1
        var signed = Engine.Sign(new SignRequest(original, cert, null, null, stamp, null));

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.False(string.Equals(info.FieldName, "assinatura1", StringComparison.OrdinalIgnoreCase),
            $"nome gerado colidiu com o placeholder pré-existente: {info.FieldName}");
        Assert.True(info.IntegrityValid);

        // o carimbo pousou EXATAMENTE no retângulo pedido (nível dicionário) — não no /Rect antigo do
        // placeholder (o sintoma exato do bug: retângulo pedido descartado silenciosamente).
        Assert.Equal(1, info.StampPageIndex);
        Assert.NotNull(info.StampRect);
        var rect = info.StampRect!.Value;
        Assert.Equal(Left, rect.LeftPt, 1);
        Assert.Equal(Bottom, rect.BottomPt, 1);
        Assert.Equal(Right, rect.RightPt, 1);
        Assert.Equal(Top, rect.TopPt, 1);

        // placeholder original continua intacto — não foi consumido nem escrito
        var fields = PdfEditorFactory.Create().ReadFormFields(signed);
        var placeholder = Assert.Single(fields, f => f.Name == "assinatura1");
        Assert.Equal(FormFieldType.Other, placeholder.Type);

        // prova complementar por PIXEL (probe 3a, mesmo padrão de Sign_WithVisibleStamp_PaintsOnlyInsideStampRegion):
        // o carimbo renderiza NO retângulo pedido, independente do que o dicionário afirma.
        using var rendererBefore = new PdfDocumentRenderer(original);
        using var rendererAfter = new PdfDocumentRenderer(signed);
        var pageBefore = rendererBefore.RenderPage(1, 1.0);
        var pageAfter = rendererAfter.RenderPage(1, 1.0);
        int w = pageBefore.WidthPx, h = pageBefore.HeightPx;
        const int Margin = 4;
        int stampLeft = (int)Left, stampRight = (int)Right;
        int stampTop = h - (int)Top, stampBottom = h - (int)Bottom;
        int diffInsideCore = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool differs = pageBefore.Bgra[i] != pageAfter.Bgra[i]
                    || pageBefore.Bgra[i + 1] != pageAfter.Bgra[i + 1]
                    || pageBefore.Bgra[i + 2] != pageAfter.Bgra[i + 2];
                if (!differs) continue;
                bool insideCore = x >= stampLeft + Margin && x < stampRight - Margin
                    && y >= stampTop + Margin && y < stampBottom - Margin;
                if (insideCore) diffInsideCore++;
            }
        Assert.True(diffInsideCore > 100,
            $"carimbo não renderizou no retângulo pedido: só {diffInsideCore} pixels diferentes no núcleo");
    }

    // ==== Plano 9 (Task 3): carimbo em português + marca d'água ====================================
    // Layout NOVO da camada de aparência (ApplyVisibleStamp) — texto pt-BR, marca d'água translúcida,
    // mais campos (CPF/CNPJ, motivo, local, emissor), regra de prioridade em caixas pequenas. NENHUM
    // destes testes toca a mecânica criptográfica (SignWithBaselineBProfile/DocMDP/nome de campo/
    // ByteRange) — só a camada visual; as guardas cripto continuam nos testes ACIMA, inalteradas.

    private const double StampLeft = 350, StampBottom = 50, StampRight = 530, StampTop = 110; // 180x60pt
    // = DefaultStampWidthPt/DefaultStampHeightPt (DocumentViewModel, Plano 8) — mesmo tamanho default
    // que a caixa ajustável do carimbo produz sem o usuário redimensionar.
    private const double MinStampLeft = 350, MinStampBottom = 50, MinStampRight = 410, MinStampTop = 70; // 60x20pt
    // = MinStampBoxWidthPt/MinStampBoxHeightPt (DocumentViewModel, Plano 8) — o menor retângulo que a
    // UI permite o usuário desenhar.

    [Fact]
    public void Sign_WithVisibleStamp_AppearanceTextIsPortugueseNotEnglish()
    {
        var stamp = new VisibleStampSpec(0, new PdfQuad(StampLeft, StampBottom, StampRight, StampTop));
        var signed = SignFixture(stamp: stamp);

        var text = ExtractStampAppearanceText(signed);
        Assert.Contains("Assinado digitalmente por", text);
        Assert.DoesNotContain("Digitally signed by", text);
        Assert.Contains("(UTC", text); // brief: data + fuso, ex. "(UTC-03:00)"
    }

    [Fact] // HIPÓTESE reconciliada (brief): fonte padrão do PDF (Helvetica) + WinAnsiEncoding cobre
    // ã/ç/é — provado aqui com um nome real acentuado (não um ASCII de teste) + sufixo CPF (Leiaute
    // RFB v4.1, mesma convenção de SignatureReader.SplitNameAndDocument) — CPF sintético com dígito
    // verificador inválido de propósito (não é um CPF real de ninguém).
    public void Sign_WithVisibleStamp_AppearanceTextPreservesAccentsAndMasksCpf()
    {
        using var cert = TestCertificateFactory.CreateSelfSignedWithDocumentSuffix("João Conceição:12345678901");
        var stamp = new VisibleStampSpec(0, new PdfQuad(StampLeft, StampBottom, StampRight, StampTop));
        var signed = SignFixture(cert: cert, stamp: stamp);

        var text = ExtractStampAppearanceText(signed);
        Assert.Contains("João Conceição", text);
        Assert.Contains("CPF: 123.456.789-01", text); // mesma máscara de SignatureRowViewModel.FormatCpf (P4)
    }

    [Fact]
    public void Sign_WithVisibleStamp_AppearanceTextMasksCnpj()
    {
        using var cert = TestCertificateFactory.CreateSelfSignedWithDocumentSuffix("EMPRESA TESTE LTDA:12345678000199");
        var stamp = new VisibleStampSpec(0, new PdfQuad(StampLeft, StampBottom, StampRight, StampTop));
        var signed = SignFixture(cert: cert, stamp: stamp);

        var text = ExtractStampAppearanceText(signed);
        Assert.Contains("CNPJ: 12.345.678/0001-99", text); // mesma máscara de SignatureRowViewModel.FormatCnpj (P4)
    }

    [Fact] // Certificado SEM a convenção CN "NOME:CPF|CNPJ" (o caminho comum nos outros testes deste
    // arquivo, TestCertificateFactory.CreateSelfSigned) + sem motivo/local -- nenhum dos 4 campos
    // opcionais aparece; só a legenda+nome+data (sempre presentes).
    public void Sign_WithVisibleStamp_WithoutOptionalFields_OmitsThem()
    {
        var stamp = new VisibleStampSpec(0, new PdfQuad(StampLeft, StampBottom, StampRight, StampTop));
        var signed = SignFixture(stamp: stamp);

        var text = ExtractStampAppearanceText(signed);
        Assert.DoesNotContain("CPF", text);
        Assert.DoesNotContain("CNPJ", text);
        Assert.DoesNotContain("Motivo", text);
        Assert.DoesNotContain("Local", text);
    }

    [Fact] // Regra de prioridade (brief): "min 60×20pt: name+date always; then CPF/CNPJ; then
    // motivo/local; then emissor — drop from the bottom". Testado no tamanho DEFAULT (180x60pt, ver
    // StampLeft/Top acima) com TODOS os campos opcionais fornecidos -- cabem todos (nome curto, motivo/
    // local curtos): a régua de prioridade não corta nada que caiba de verdade.
    public void Sign_WithVisibleStamp_DefaultBox_ShowsCpfMotivoLocalAndIssuer()
    {
        using var cert = TestCertificateFactory.CreateSelfSignedWithDocumentSuffix("Joana Petit:01672780838");
        var stamp = new VisibleStampSpec(0, new PdfQuad(StampLeft, StampBottom, StampRight, StampTop));
        var signed = SignFixture(cert: cert, stamp: stamp, reason: "Aprovacao", location: "Sao Paulo");

        var text = ExtractStampAppearanceText(signed);
        Assert.Contains("Assinado digitalmente por", text);
        Assert.Contains("Joana Petit", text);
        Assert.Contains("CPF: 016.727.808-38", text);
        Assert.Contains("Motivo: Aprovacao", text);
        Assert.Contains("Local: Sao Paulo", text);
        Assert.Contains("Emitido por:", text);
    }

    [Fact] // Mesmo cenário acima (motivo+local+CPF fornecidos), mas na caixa MÍNIMA (60x20pt, brief +
    // MinStampBoxWidthPt/HeightPt do Plano 8) -- só nome+data sobrevivem (a garantia "always" do
    // brief); CPF/motivo/local/emissor E a legenda "Assinado digitalmente por" ficam de fora por falta
    // de espaço vertical -- provam a régua de prioridade "drop from the bottom" de verdade, não só a
    // ausência quando o campo nem foi pedido (teste acima já cobre isso).
    public void Sign_WithVisibleStamp_MinBox_ShowsOnlyNameAndDate()
    {
        using var cert = TestCertificateFactory.CreateSelfSignedWithDocumentSuffix("Ana Reis:01672780838");
        var stamp = new VisibleStampSpec(0, new PdfQuad(MinStampLeft, MinStampBottom, MinStampRight, MinStampTop));
        var signed = SignFixture(cert: cert, stamp: stamp, reason: "Aprovacao", location: "Sao Paulo");

        var text = ExtractStampAppearanceText(signed);
        Assert.Contains("Ana Reis", text); // nome curto -- cabe sem truncar
        Assert.DoesNotContain("CPF", text);
        Assert.DoesNotContain("Motivo", text);
        Assert.DoesNotContain("Local", text);
        Assert.DoesNotContain("Emitido", text);
        Assert.DoesNotContain("Assinado digitalmente por", text); // legenda cai antes do CPF na prioridade
    }

    /// Regressão da revisão final do coordenador (achado real, medido ao vivo, não hipotético):
    /// `SignatureFieldAppearance` aplica um padding PRÓPRIO default (~2pt de cada lado) antes de
    /// repassar espaço pro `Div`/`StampAppearanceRenderer` -- o retângulo REALMENTE desenhado
    /// (`GetOccupiedAreaBBox()`) ficava ~4pt mais estreito/baixo que o retângulo NOMINAL pedido. Na
    /// caixa MÍNIMA (60×20pt) isso reduzia o orçamento vertical o bastante pra a linha de DATA (uma
    /// das 2 linhas SEMPRE desenhadas, nunca omitida) colidir com o próprio traço da moldura inferior
    /// -- medido ao vivo (scan pixel-a-pixel antes do fix): pixels de tinta de texto (média tão escura
    /// quanto 49/255) na faixa logo ACIMA do traço da moldura, onde deveria haver só espaço livre (ou
    /// tingimento da marca d'água, nunca glifo). Fix: zera os 4 paddings da APARÊNCIA
    /// (`ApplyVisibleStamp`, `Property.PADDING_TOP/RIGHT/BOTTOM/LEFT`) -- este teste é a VIGIA
    /// permanente contra uma reintrodução do problema (por este padding específico ou qualquer causa
    /// futura que reduza o espaço disponível na caixa mínima).
    [Fact]
    public void Sign_WithVisibleStamp_MinBox_NoTextCollidesWithBottomFrameStroke()
    {
        var stamp = new VisibleStampSpec(0, new PdfQuad(MinStampLeft, MinStampBottom, MinStampRight, MinStampTop));
        var signed = SignFixture(stamp: stamp);

        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(0, 1.0);
        int w = page.WidthPx, h = page.HeightPx;

        // o traço da moldura inferior fica ~1px acima da borda NOMINAL (BorderPt/2 de inset, ver
        // DrawFrame) -- a "zona de folga" que o fix garante (medida ao vivo: ~3pt) fica logo ACIMA
        // dele, nas linhas offsetFromBottom=2..3 (offset=0 é a borda nominal em si -- branco fora do
        // carimbo; offset=1 é o PRÓPRIO traço da moldura, escuro por natureza -- excluído de propósito
        // desta varredura, que procura só por COLISÃO de texto, não pela moldura).
        int bottomEdgeRow = h - (int)MinStampBottom;
        const int ClearanceZoneStartOffset = 2, ClearanceZoneEndOffset = 3;
        int left = (int)MinStampLeft + 3, right = (int)MinStampRight - 3; // afasta das verticais da moldura

        int darkestInZone = 255;
        for (int offset = ClearanceZoneStartOffset; offset <= ClearanceZoneEndOffset; offset++)
        {
            int y = bottomEdgeRow - offset;
            for (int x = left; x <= right; x++)
            {
                int i = (y * w + x) * 4;
                int avg = (page.Bgra[i] + page.Bgra[i + 1] + page.Bgra[i + 2]) / 3;
                darkestInZone = Math.Min(darkestInZone, avg);
            }
        }

        Assert.True(darkestInZone > 200,
            $"texto colidindo com a moldura inferior na caixa mínima (pixel mais escuro na zona de " +
            $"folga={darkestInZone}, esperado >200 -- só tingimento leve da marca d'água, nunca glifo)");
    }

    /// Marca d'água (brief: "selo translúcido... alfa ~0.08-0.12", "vetor... NUNCA fora do carimbo"):
    /// um carimbo SEM marca d'água (o layout ANTIGO, `SignedAppearanceText` puro) só produz pixels
    /// BIMODAIS na região interior -- branco de fundo (255) ou glifo de texto (preto/cinza de
    /// antialiasing na borda do traço) -- NUNCA uma faixa de cinza-claro UNIFORME cobrindo uma área
    /// GRANDE, porque nada ali é desenhado com alfa fracionário. Medido ao vivo contra o layout antigo
    /// (ver task-3-report.md): banda [215,250) (nem branco puro, nem antialiasing de borda de glifo)
    /// cobre só ~7% da área interior do carimbo -- ruído de antialiasing de texto, não um tingimento
    /// deliberado. Limiar 15% fica ACIMA desse ruído.
    ///
    /// MARGEM PROPORCIONAL (revisão do coordenador -- achado real, não hipotético): a 1ª versão deste
    /// teste usava uma margem FIXA (6pt) pra afastar da moldura antes de medir -- calibrada pro
    /// retângulo DEFAULT (60pt de altura, 6pt é ~10% disso), mas devastadora no MÍNIMO (20pt de altura,
    /// 6pt de cada lado = 12pt removidos de 20 = 60% da altura descartada, sobrando uma faixa central
    /// estreita demais pra medir o selo de verdade -- fração medida caiu pra ~13.8%, abaixo do limiar,
    /// sem o selo ter ficado menos visível). Fix: margem = 10% da ALTURA do próprio retângulo do
    /// carimbo (escala junto com a caixa, nunca um valor absoluto) -- testado nos DOIS tamanhos que
    /// importam na prática (`MeasureLightTintFraction`, helper compartilhado abaixo).
    ///
    /// RECALIBRAÇÃO (revisão final do coordenador, depois do fix de padding zero em
    /// `ApplyVisibleStamp`): zerar o padding default de `SignatureFieldAppearance` aumenta o retângulo
    /// REALMENTE desenhado (antes encolhido ~2pt de cada lado) -- medido ao vivo, a fração no DEFAULT
    /// subiu (16,7% -> 18,9%, mais área de selo disponível), mas no MÍNIMO CAIU (17,8% -> ~15,0%, perto
    /// do limiar antigo de 15%): com o retângulo cheio, o texto (sempre nome+data, 2 linhas fixas) fica
    /// posicionado de forma diferente dentro da caixa mínima, deslocando um pouco a área tingida em
    /// relação à janela de amostra (que continua fixa em 10% da altura NOMINAL, sem mudança). Limiar
    /// recalibrado pros 2 testes abaixo: 12% -- continua BEM acima do ruído do layout antigo (~7%, sem
    /// marca d'água nenhuma) e abaixo das 2 frações medidas de verdade (mínimo ~15,0%, default ~18,9%),
    /// preservando folga nas duas pontas.
    private static double MeasureLightTintFraction(byte[] signed, double left, double bottom, double right, double top)
    {
        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(0, 1.0);
        int w = page.WidthPx, h = page.HeightPx;

        double marginPt = (top - bottom) * 0.10; // 10% da ALTURA do retângulo -- nunca um pt fixo
        int pxLeft = (int)(left + marginPt), pxRight = (int)(right - marginPt);
        int pxTop = h - (int)(top - marginPt), pxBottom = h - (int)(bottom + marginPt);

        int lightTint = 0, total = 0;
        for (int y = pxTop; y <= pxBottom; y++)
            for (int x = pxLeft; x <= pxRight; x++)
            {
                int i = (y * w + x) * 4;
                int avgPix = (page.Bgra[i] + page.Bgra[i + 1] + page.Bgra[i + 2]) / 3;
                total++;
                if (avgPix is >= 215 and < 250) lightTint++;
            }
        return lightTint / (double)total;
    }

    // Limiar compartilhado pelos 2 testes abaixo -- ver derivação completa no XML doc de
    // MeasureLightTintFraction acima (12% fica acima do ruído do layout antigo, ~7%, e abaixo das 2
    // frações medidas de verdade, mínimo ~15,0% e default ~18,9%).
    private const double LightTintFractionThreshold = 0.12;

    [Fact]
    public void Sign_WithVisibleStamp_DefaultBox_ShowsTranslucentWatermarkOverALargeArea()
    {
        var stamp = new VisibleStampSpec(0, new PdfQuad(StampLeft, StampBottom, StampRight, StampTop));
        var signed = SignFixture(stamp: stamp);

        double fraction = MeasureLightTintFraction(signed, StampLeft, StampBottom, StampRight, StampTop);
        Assert.True(fraction > LightTintFractionThreshold,
            $"nenhuma região extensa de tingimento translúcido na caixa DEFAULT (180x60pt) -- fração em " +
            $"[215,250)={fraction:P1} (esperado >{LightTintFractionThreshold:P0})");
    }

    [Fact] // mesma medida acima, mas no tamanho MÍNIMO real da UI (60x20pt, DocumentViewModel.
    // MinStampBoxWidthPt/HeightPt) -- prova que o selo continua MENSURAVELMENTE visível mesmo na
    // caixa mais apertada que o usuário pode desenhar, não só no default espaçoso.
    public void Sign_WithVisibleStamp_MinBox_ShowsTranslucentWatermarkOverALargeArea()
    {
        var stamp = new VisibleStampSpec(0, new PdfQuad(MinStampLeft, MinStampBottom, MinStampRight, MinStampTop));
        var signed = SignFixture(stamp: stamp);

        double fraction = MeasureLightTintFraction(signed, MinStampLeft, MinStampBottom, MinStampRight, MinStampTop);
        Assert.True(fraction > LightTintFractionThreshold,
            $"nenhuma região extensa de tingimento translúcido na caixa MÍNIMA (60x20pt) -- fração em " +
            $"[215,250)={fraction:P1} (esperado >{LightTintFractionThreshold:P0})");
    }

    /// Minor #1 da revisão (achado real, verificado ao vivo com um render/crop antes de escrever a
    /// asserção): o selo escalava só pela ALTURA do retângulo (`bbox.GetHeight() * 0.82`), sem
    /// considerar a LARGURA -- numa caixa ESTREITA e ALTA (largura menor que altura; nunca exercitada
    /// pelos 2 tamanhos "de catálogo" acima, que são sempre mais largos que altos), o selo calculado a
    /// partir da altura ficava mais LARGO que a própria caixa. Como toda anotação PDF é recortada pelo
    /// PRÓPRIO `/BBox` na hora de renderizar (confirmado ao vivo: um teste de "0 px fora do retângulo"
    /// como `Sign_WithVisibleStamp_PaintsOnlyInsideStampRegion` continuava passando mesmo com o selo
    /// sangrando -- o VISOR nunca deixa nada vazar pra fora da página, então esse tipo de prova não
    /// pega este defeito), o efeito visível não é "vaza pra fora da página": é o selo aparecer CORTADO
    /// RENTE à moldura de propósito (sem a margem que TODA a extensão vertical do retângulo teria),
    /// em vez de escalado pra caber inteiro e centralizado.
    ///
    /// Prova: existe uma FAIXA de margem sem tingimento nenhum entre a borda do selo e a borda do
    /// carimbo -- medida perto da borda ESQUERDA (a direita tem o traço da rubrica cruzando por perto,
    /// que também tinge, e atrapalharia a medida). ACHADO ao vivo (medido com um scan pixel-a-pixel,
    /// não hipotético): numa caixa 40x150pt a margem escalada é só ~3.6pt (9% de 40pt) -- perto demais
    /// da moldura pra medir com folga de antialiasing sem ambiguidade. A margem é sempre ~9% da LARGURA
    /// da caixa (`(1-0.82)/2`) quando escalada pela largura -- pra ter uma margem generosa o bastante
    /// de medir (a prova precisa de robustez, não de um valor mínimo específico), a caixa deste teste é
    /// bem mais larga que a mínima estreita possível (200x500pt -- ainda MUITO mais alta que larga,
    /// aspecto 0.4, bem abaixo da proporção do próprio selo 0.62 -- garante escala por LARGURA, não
    /// altura) só pra deixar a margem resultante (~18pt) folgada o bastante pra amostrar sem ruído.
    [Fact]
    public void Sign_WithVisibleStamp_NarrowTallBox_WatermarkScalesDownWithMarginFromEdge()
    {
        const double Left = 350, Bottom = 50, Right = 550, Top = 550; // 200pt largo x 500pt alto
        var stamp = new VisibleStampSpec(0, new PdfQuad(Left, Bottom, Right, Top));
        var signed = SignFixture(stamp: stamp);

        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(0, 1.0);
        int w = page.WidthPx, h = page.HeightPx;

        // altura vertical orçada pro selo (bbox.Height * 0.82) é sempre >= a orçada pela largura numa
        // caixa mais alta que larga -- logo o CENTRO vertical da caixa é onde o selo (sem o clamp)
        // ficaria mais largo, o pior caso pra estourar a largura da caixa.
        double cyPt = (Bottom + Top) / 2;
        int py = h - (int)cyPt;
        // margem esperada ~18pt (9% de 200pt) de cada lado -- amostra a 10pt da borda esquerda, dentro
        // da margem com folga confortável dos dois lados (longe da moldura de 0.75pt E longe da borda
        // real do selo a ~18pt).
        int px = (int)Left + 10;

        const int half = 2; // janela 5x5px, robusta a antialiasing
        long sum = 0; int n = 0;
        for (int y = py - half; y <= py + half; y++)
            for (int x = px - half; x <= px + half; x++)
            {
                int i = (y * w + x) * 4;
                sum += page.Bgra[i] + page.Bgra[i + 1] + page.Bgra[i + 2];
                n++;
            }
        double avg = sum / (double)(n * 3);

        Assert.True(avg > 250,
            $"selo sem margem da borda esquerda numa caixa estreita/alta (média perto da borda={avg:0.0}, " +
            $"esperado quase branco puro >250) -- selo não escalado pra largura, ficando rente à moldura");
    }

    // --- Plano 21: rubrica (PNG) como aparência do carimbo -------------------------------------------

    // 1x1 PNG vermelho (mesmo base64 de mPdf.Editing.Tests.Fixtures.OnePixelPng) — decodifica sem erro
    // no ImageDataFactory, pinta pixels distintos do fundo branco quando escalado pro bbox.
    private static byte[] RubricaPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    /// A aparência da rubrica é SÓ a imagem: o stream `/AP/N` do widget carrega um XObject de imagem nos
    /// seus recursos. Varre o `/XObject` dos recursos do appearance stream procurando `/Subtype /Image`.
    private static bool AppearanceHasImageXObject(byte[] pdf, int pageIndex = 0)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var page = doc.GetPage(pageIndex + 1);
        var annot = Assert.Single(page.GetAnnotations());
        var stream = annot.GetPdfObject().GetAsDictionary(PdfName.AP)?.GetAsStream(PdfName.N);
        Assert.NotNull(stream);
        // A aparência de assinatura do iText aninha o conteúdo em XObjects de formulário (camadas n0/n2) —
        // a imagem da rubrica fica um nível abaixo. Varre recursivamente os `/XObject` dos recursos.
        return StreamTreeHasImage(stream!, new HashSet<PdfObject>());
    }

    private static bool StreamTreeHasImage(PdfStream stream, HashSet<PdfObject> seen)
    {
        if (!seen.Add(stream)) return false;
        if (PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype))) return true;
        var xobjects = stream.GetAsDictionary(PdfName.Resources)?.GetAsDictionary(PdfName.XObject);
        if (xobjects is null) return false;
        foreach (var key in xobjects.KeySet())
            if (xobjects.GetAsStream(key) is { } child && StreamTreeHasImage(child, seen))
                return true;
        return false;
    }

    [Fact] // rubrica -> assinatura ÍNTEGRA (cripto intacta) + aparência com imagem, SEM o texto do
    // carimbo padrão ("Assinado digitalmente por" nunca é desenhado no modo rubrica).
    public void Sign_WithRubrica_IntegrityValidAndAppearanceIsImageOnly()
    {
        const double Left = 350, Bottom = 50, Right = 550, Top = 110;
        var stamp = new VisibleStampSpec(0, new PdfQuad(Left, Bottom, Right, Top), RubricaPng());
        var signed = SignFixture(stamp: stamp);

        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.True(info.IntegrityValid);

        Assert.True(AppearanceHasImageXObject(signed), "aparência da rubrica não tem XObject de imagem");
        var text = ExtractStampAppearanceText(signed);
        Assert.DoesNotContain("Assinado digitalmente por", text);
    }

    [Fact] // prova por PIXEL: a rubrica renderiza (não-branco) dentro do retângulo pedido — mesmo padrão
    // de AddAnnotation_ImageStamp_RendersNonBlankInStampRegion (mPdf.Editing.Tests).
    public void Sign_WithRubrica_RendersNonBlankInStampRegion()
    {
        const double Left = 350, Bottom = 50, Right = 550, Top = 110;
        var stamp = new VisibleStampSpec(0, new PdfQuad(Left, Bottom, Right, Top), RubricaPng());
        var signed = SignFixture(stamp: stamp);

        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(0, 1.0);
        int w = page.WidthPx, h = page.HeightPx;
        int cx = (int)((Left + Right) / 2), cy = h - (int)((Bottom + Top) / 2);
        int i = (cy * w + cx) * 4;
        bool nonWhite = page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250;
        Assert.True(nonWhite, "centro do retângulo da rubrica ficou branco — imagem não renderizou");
    }

    [Fact] // bytes de imagem inválidos (não é PNG/JPG) -> PdfSigningException pt-BR acionável, ANTES de
    // assinar (ValidateStamp), nunca um carimbo silenciosamente em branco nem exceção crua do iText.
    public void Sign_WithRubrica_InvalidImage_ThrowsPdfSigningException()
    {
        var stamp = new VisibleStampSpec(0, new PdfQuad(350, 50, 550, 110), new byte[] { 1, 2, 3, 4, 5 });
        Assert.Throws<PdfSigningException>(() => SignFixture(stamp: stamp));
    }

    [Fact] // retrocompat: carimbo PADRÃO (ImageBytes null) continua desenhando o texto — nenhuma regressão.
    public void Sign_WithStandardStamp_StillDrawsText_WhenNoImage()
    {
        var stamp = new VisibleStampSpec(0, new PdfQuad(350, 50, 550, 110));
        var signed = SignFixture(stamp: stamp);
        Assert.Contains("Assinado digitalmente por", ExtractStampAppearanceText(signed));
        Assert.False(AppearanceHasImageXObject(signed), "carimbo padrão não deveria embutir imagem");
    }
}
