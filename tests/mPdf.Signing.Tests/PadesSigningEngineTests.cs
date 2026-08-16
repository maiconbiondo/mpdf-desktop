using System.Security.Cryptography.X509Certificates;
using iText.Signatures;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.Signing.Tests;

public class PadesSigningEngineTests
{
    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();

    private static byte[] SignFixture(
        X509Certificate2? cert = null, VisibleStampSpec? stamp = null,
        DocMdpLevel? certificationLevel = null, string? reason = null, byte[]? pdf = null)
    {
        using var certificate = cert ?? TestCertificateFactory.CreateSelfSigned();
        return Engine.Sign(new SignRequest(
            pdf ?? Fixtures.A4(), certificate, reason, null, stamp, certificationLevel));
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
}
