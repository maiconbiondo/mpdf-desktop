using System.Linq;
using System.Security.Cryptography.X509Certificates;
using iText.Bouncycastleconnector;
using iText.Commons.Bouncycastle.Cert;
using iText.Kernel.Pdf;
using iText.Signatures;
using mPdf.Editing;
using mPdf.Rendering;
using Xunit;

namespace mPdf.Signing.Tests;

/// Task 6 (Plano 4): preenchimento incremental de formulário em documento JÁ ASSINADO (spec §5.2 — a
/// costura deferida do Plano 3c). PROVA CENTRAL (a que importa mais que qualquer outra deste arquivo):
/// depois de `SetFormFieldsIncremental`, a assinatura EXISTENTE continua `IntegrityValid`
/// (`ReadSignatures` antes/depois) E o valor preenchido RENDE de verdade (motor independente PDFium,
/// via `mPdf.Rendering` — nunca só o dicionário cru do iText que escreveu). Tabela de decisão DocMDP
/// completa (reconciliada por sonda ao vivo — probe console referenciando itext 9.7.0 direto) documentada
/// no XML doc de `FillPermission` em Contract.cs; não repetida aqui.
public class FormFillIncrementalEngineTests
{
    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();

    // ---- Helpers de fixture: assina fixture-formulario.pdf com um nível de certificação -------------

    /// Via `ISigningEngine.Sign` (motor de PRODUÇÃO, mesmo caminho que qualquer assinatura real deste
    /// app usa) — cobre P=None (aprovação, sem certificação) e P=2 (FormsAndSignatures), os 2 únicos
    /// níveis que `DocMdpLevel` expõe por design (este app nunca EMITE P=1/P=3 — ver Contract.cs).
    private static byte[] SignFormulario(X509Certificate2 cert, DocMdpLevel? level) =>
        Engine.Sign(new SignRequest(Fixtures.Formulario(), cert, null, null, null, level));

    /// Sonda-nível: fabrica um documento certificado com P=1 (NO_CHANGES_PERMITTED) usando a API CRUA do
    /// iText (fora de `ISigningEngine`/`DocMdpLevel`, que não têm como pedir P=1 — este app nunca emite
    /// esse nível por design). Necessário porque `CanFillIncremental` precisa recusar um documento de
    /// TERCEIRO que chegue certificado assim, mesmo que este app jamais produza um. Reusa
    /// `X509Certificate2Signature` (internal, visível via `InternalsVisibleTo` — AssemblyInfo.cs) em vez
    /// de duplicar a ponte de assinatura; `BuildChain` é duplicado aqui porque o original em
    /// `PadesSigningEngine` é `private` (mesmo precedente de `FormFlattenIntegrationTests.
    /// CountPaintedPixelsInRegion`: helper pequeno reimplementado, "não compartilhável entre assemblies
    /// de teste distintos").
    private static byte[] SignFormularioWithRawCertificationLevel(X509Certificate2 cert, AccessPermissions level)
    {
        using var input = new System.IO.MemoryStream(Fixtures.Formulario());
        using var output = new System.IO.MemoryStream();
        var padesSigner = new PdfPadesSigner(new PdfReader(input), output);
        padesSigner.SetStampingProperties(new StampingProperties().UseAppendMode());
        var props = new SignerProperties().SetFieldName("Assinatura1").SetCertificationLevel(level);
        var chain = BuildChain(cert);
        var signature = new X509Certificate2Signature(cert);
        padesSigner.SignWithBaselineBProfile(props, chain, signature);
        return output.ToArray();
    }

    private static IX509Certificate[] BuildChain(X509Certificate2 certificate)
    {
        var factory = BouncyCastleFactoryCreator.GetFactory();
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.Build(certificate);
        return chain.ChainElements
            .Select(e => factory.CreateX509Certificate(new System.IO.MemoryStream(e.Certificate.RawData)))
            .ToArray();
    }

    // ---- CanFillIncremental: tabela de decisão ------------------------------------------------------

    [Fact]
    public void CanFillIncremental_UnsignedDocument_ReturnsNotSigned()
    {
        Assert.Equal(FillPermission.NotSigned, Engine.CanFillIncremental(Fixtures.Formulario()));
    }

    [Fact]
    public void CanFillIncremental_CertifiedFormsAndSignatures_ReturnsAllowed()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        Assert.Equal(FillPermission.Allowed, Engine.CanFillIncremental(signed));
    }

    [Fact] // sem certificação nenhuma (só assinatura de aprovação) — legal per ISO 32000, ver Contract.cs
    public void CanFillIncremental_ApprovalOnlySignature_ReturnsAllowed()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, level: null);
        Assert.Equal(FillPermission.Allowed, Engine.CanFillIncremental(signed));
    }

    [Fact] // ACHADO CRÍTICO (ver Contract.cs): iText não impede a escrita nem invalida IntegrityValid
    // num doc P=1 — este motor é o ÚNICO ponto de enforcement dessa regra.
    public void CanFillIncremental_CertifiedNoChangesPermitted_ReturnsDeniedByDocMdp()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormularioWithRawCertificationLevel(cert, AccessPermissions.NO_CHANGES_PERMITTED);
        Assert.Equal(FillPermission.DeniedByDocMdp, Engine.CanFillIncremental(signed));
    }

    // ---- I1 (revisão): 2 formatos de ataque contra o discriminador DocMDP — provados diretamente
    // contra o dicionário CRU (fora de ISigningEngine/DocMdpLevel, que não têm como fabricar isto) --------

    /// Ataque A: planta uma entrada `/Reference` ESPÚRIA (`/TransformMethod` `/FieldMDP`, não
    /// `/DocMDP`) com sua PRÓPRIA `/TransformParams/P` — ANTES da entrada `/DocMDP` real no array —
    /// num documento já certificado com `level`. Uma varredura ingênua ("primeiro `/P` que achar")
    /// devolveria o `P` da entrada espúria em vez do `P` real; o discriminador correto ignora qualquer
    /// entrada cujo `/TransformMethod` não seja literalmente `/DocMDP`.
    private static byte[] PlantSpuriousFieldMdpReferenceBeforeRealDocMdp(byte[] certifiedPdf, int spuriousP)
    {
        using var input = new System.IO.MemoryStream(certifiedPdf);
        using var output = new System.IO.MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
        {
            var perms = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms);
            var docMdpSigDict = perms!.GetAsDictionary(PdfName.DocMDP)!;
            var refArray = docMdpSigDict.GetAsArray(PdfName.Reference)!;
            var spurious = new PdfDictionary();
            spurious.Put(PdfName.Type, PdfName.SigRef);
            spurious.Put(PdfName.TransformMethod, PdfName.FieldMDP);
            var spuriousParams = new PdfDictionary();
            spuriousParams.Put(PdfName.P, new PdfNumber(spuriousP));
            spurious.Put(PdfName.TransformParams, spuriousParams);
            refArray.Add(0, spurious); // PREPEND — antes da entrada /DocMDP real
            docMdpSigDict.SetModified();
        }
        return output.ToArray();
    }

    /// Ataque B: `/Perms/DocMDP` continua presente (o documento SE DECLARA certificado) mas
    /// `/Reference` é removido inteiramente — simula um PDF malformado/corrompido onde a única fonte de
    /// verdade do `P` real ficou ilegível.
    private static byte[] RemoveDocMdpReferenceArray(byte[] certifiedPdf)
    {
        using var input = new System.IO.MemoryStream(certifiedPdf);
        using var output = new System.IO.MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
        {
            var perms = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms);
            var docMdpSigDict = perms!.GetAsDictionary(PdfName.DocMDP)!;
            docMdpSigDict.Remove(PdfName.Reference);
            docMdpSigDict.SetModified();
        }
        return output.ToArray();
    }

    /// Localiza a entrada `/Reference` REAL (`/TransformMethod` `/DocMDP`) dentro de `/Perms/DocMDP` —
    /// helper compartilhado pelos 2 mutadores abaixo (shape C e o caso do default `/P`, M4).
    private static PdfDictionary GetRealDocMdpReferenceEntry(PdfDictionary docMdpSigDict)
    {
        var refArray = docMdpSigDict.GetAsArray(PdfName.Reference)!;
        for (int i = 0; i < refArray.Size(); i++)
        {
            var refDict = refArray.GetAsDictionary(i);
            if (refDict is not null && PdfName.DocMDP.Equals(refDict.GetAsName(PdfName.TransformMethod)))
                return refDict;
        }
        throw new InvalidOperationException("Nenhuma entrada /Reference com /TransformMethod /DocMDP encontrada.");
    }

    /// Shape C (re-revisão, M4): remove a chave `/TransformParams` INTEIRA da entrada `/DocMDP` real —
    /// diferente de só remover `/P` de dentro dela (o caso do default do spec, testado separadamente
    /// abaixo). Continua fail-closed: um dicionário de parâmetros AUSENTE não é a mesma coisa que um
    /// dicionário PRESENTE com uma chave opcional ausente.
    private static byte[] RemoveTransformParamsFromRealDocMdpReference(byte[] certifiedPdf)
    {
        using var input = new System.IO.MemoryStream(certifiedPdf);
        using var output = new System.IO.MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
        {
            var perms = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms);
            var docMdpSigDict = perms!.GetAsDictionary(PdfName.DocMDP)!;
            var realRef = GetRealDocMdpReferenceEntry(docMdpSigDict);
            realRef.Remove(PdfName.TransformParams);
            docMdpSigDict.SetModified();
        }
        return output.ToArray();
    }

    /// M4 (re-revisão): remove SÓ a chave `/P` de dentro de um `/TransformParams` que continua
    /// PRESENTE — ISO 32000-1 Table 254 declara `/P` OPCIONAL com DEFAULT 2, um documento
    /// perfeitamente válido, não um ataque.
    private static byte[] RemovePFromRealDocMdpTransformParams(byte[] certifiedPdf)
    {
        using var input = new System.IO.MemoryStream(certifiedPdf);
        using var output = new System.IO.MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
        {
            var perms = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms);
            var docMdpSigDict = perms!.GetAsDictionary(PdfName.DocMDP)!;
            var realRef = GetRealDocMdpReferenceEntry(docMdpSigDict);
            var transformParams = realRef.GetAsDictionary(PdfName.TransformParams)!;
            transformParams.Remove(PdfName.P);
            transformParams.SetModified();
        }
        return output.ToArray();
    }

    [Fact] // M4 (re-revisão): /TransformParams presente, /P AUSENTE de dentro dele -> default do spec
    // (ISO 32000-1 Table 254, /P é opcional, default 2) -> Allowed, NUNCA DeniedByDocMdp. O round 1
    // desta revisão regrediu silenciosamente aqui exatamente porque esta forma não tinha teste nenhum.
    public void CanFillIncremental_DocMdpTransformParamsPresentButPAbsent_DefaultsToP2_ReturnsAllowed()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var certifiedP2 = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        var mutated = RemovePFromRealDocMdpTransformParams(certifiedP2);

        Assert.Equal(FillPermission.Allowed, Engine.CanFillIncremental(mutated));
    }

    [Fact] // shape C (re-revisão, M4): diferente do caso acima — aqui é o dicionário /TransformParams
    // INTEIRO que está ausente, não só a chave /P dentro dele. Continua fail-closed (DeniedByDocMdp) —
    // o fix do default de /P não pode "vazar" pra este caso estruturalmente diferente.
    public void CanFillIncremental_DocMdpTransformParamsEntirelyMissing_StaysFailClosed()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var certifiedP2 = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        var mutated = RemoveTransformParamsFromRealDocMdpReference(certifiedP2);

        Assert.Equal(FillPermission.DeniedByDocMdp, Engine.CanFillIncremental(mutated));
    }

    [Fact] // I1(a): bypass tentado pelo revisor — /FieldMDP espúrio com /P=2 plantado ANTES do /DocMDP
    // real (que tem /P=1, NO_CHANGES_PERMITTED) não pode enganar o discriminador.
    public void CanFillIncremental_FieldMdpStrayPBeforeRealDocMdpP1_StillReturnsDeniedByDocMdp()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var certifiedP1 = SignFormularioWithRawCertificationLevel(cert, AccessPermissions.NO_CHANGES_PERMITTED);
        var attacked = PlantSpuriousFieldMdpReferenceBeforeRealDocMdp(certifiedP1, spuriousP: 2);

        Assert.Equal(FillPermission.DeniedByDocMdp, Engine.CanFillIncremental(attacked));
    }

    [Fact] // I1(b): FAIL CLOSED — /Perms/DocMDP presente mas /Reference ilegível nunca degrada pra
    // "sem certificação" (que liberaria o preenchimento); mesmo num documento originalmente P=2.
    public void CanFillIncremental_DocMdpPresentButReferenceMissing_FailsClosedToDeniedByDocMdp()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var certifiedP2 = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        var malformed = RemoveDocMdpReferenceArray(certifiedP2);

        Assert.Equal(FillPermission.DeniedByDocMdp, Engine.CanFillIncremental(malformed));
    }

    [Fact] // M1 (rider): P=3 (ANNOTATION_MODIFICATION) — ISO 32000 Table 254 é um SUPERCONJUNTO de P=2
    // (permite os MESMOS preenchimentos + anotações); verificado empiricamente, não só por análise —
    // Allowed E a assinatura preservada íntegra depois do preenchimento (mesma prova central).
    public void CanFillIncremental_CertifiedAnnotationModification_ReturnsAllowedAndPreservesIntegrity()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormularioWithRawCertificationLevel(cert, AccessPermissions.ANNOTATION_MODIFICATION);
        Assert.Equal(FillPermission.Allowed, Engine.CanFillIncremental(signed));

        var filled = Engine.SetFormFieldsIncremental(signed, new Dictionary<string, string> { ["nome"] = "P3 Preenchido" });
        Assert.True(Assert.Single(Engine.ReadSignatures(filled)).IntegrityValid);
    }

    [Fact] // residual conhecido de StripSignatures (Plano 3c) NÃO herdado aqui — HasXfa checado ANTES
    // de tocar SignatureUtil/PdfAcroForm (que lançariam PdfException, ver Contract.cs).
    public void CanFillIncremental_SignedXfaDocument_ReturnsXfaUnsupported()
    {
        Assert.Equal(FillPermission.XfaUnsupported, Engine.CanFillIncremental(Fixtures.XfaAssinado()));
    }

    // ---- SetFormFieldsIncremental: PROVA CENTRAL (integridade + renderização) ------------------------

    [Fact] // A PROVA CENTRAL. Certificado P=2 (o nível que PadesSigningEngine.Sign realmente grava) —
    // ReadSignatures ANTES e DEPOIS do preenchimento, e o valor precisa RENDER (motor independente).
    public void SetFormFieldsIncremental_CertifiedFormsAndSignaturesDoc_KeepsSignatureValidAndRendersValue()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);

        var beforeInfo = Assert.Single(Engine.ReadSignatures(signed));
        Assert.True(beforeInfo.IntegrityValid, "assinatura já deveria estar íntegra antes do preenchimento");

        var filled = Engine.SetFormFieldsIncremental(signed,
            new Dictionary<string, string> { ["nome"] = "Preenchido Apos Assinar", ["aceito"] = "Yes" });

        var afterInfo = Assert.Single(Engine.ReadSignatures(filled));
        Assert.True(afterInfo.IntegrityValid,
            "PROVA CENTRAL: assinatura deveria continuar íntegra depois do preenchimento incremental");
        Assert.Equal(beforeInfo.FieldName, afterInfo.FieldName);

        // valor lido de volta pelo PRÓPRIO iText (dicionário cru) — a prova de RENDER (independente) vem
        // no teste dedicado abaixo (checkbox "aceito", mesma região/exemplar de
        // PdfEditorTests.SetFormFields_Checkbox_ValueAppearsInRender).
        Assert.Equal("Preenchido Apos Assinar", PdfEditorFactory.Create()
            .ReadFormFields(filled).Single(f => f.Name == "nome").Value);
    }

    [Fact] // exemplar EXATO de PdfEditorTests.SetFormFields_Checkbox_ValueAppearsInRender — mesma região
    // do widget "aceito" (50,600)-(70,620) pt, escala 1.0. Prova que o append-mode fill regenera a
    // aparência igual ao stamping normal (não é um caminho de código diferente por baixo).
    public void SetFormFieldsIncremental_CheckboxOnSignedDoc_ValueAppearsInRender()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        var filled = Engine.SetFormFieldsIncremental(signed, new Dictionary<string, string> { ["aceito"] = "Yes" });

        using var rendererBefore = new PdfDocumentRenderer(signed);
        using var rendererAfter = new PdfDocumentRenderer(filled);
        var pageBefore = rendererBefore.RenderPage(0, 1.0);
        var pageAfter = rendererAfter.RenderPage(0, 1.0);

        int h = pageAfter.HeightPx;
        int paintedBefore = CountPaintedPixelsInRegion(pageBefore, h, 50, 70, 600, 620);
        int paintedAfter = CountPaintedPixelsInRegion(pageAfter, h, 50, 70, 600, 620);

        // Medido ao vivo (probe, task-6-report.md): antes=0, depois=83 — MESMO número exato do exemplar
        // não-assinado (PdfEditorTests.SetFormFields_Checkbox_ValueAppearsInRender). Limiares folgados,
        // mesma disciplina do exemplar.
        Assert.True(paintedBefore < 5, $"checkbox já aparecia pintado ANTES do preenchimento: {paintedBefore} px");
        Assert.True(paintedAfter > 20, $"checkbox marcado não renderizou: só {paintedAfter} px (antes: {paintedBefore})");
    }

    private static int CountPaintedPixelsInRegion(RenderedPage page, int heightPx, int xMin, int xMax, int yMinPt, int yMaxPt)
    {
        int painted = 0;
        for (int y = heightPx - yMaxPt; y < heightPx - yMinPt; y++)
            for (int x = xMin; x < xMax; x++)
            {
                int i = (y * page.WidthPx + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) painted++;
            }
        return painted;
    }

    [Fact] // aprovação apenas (sem certificação) — mesma prova de integridade, sem repetir o render (já
    // provado acima; o mecanismo de escrita é o MESMO independente do nível DocMDP).
    public void SetFormFieldsIncremental_ApprovalOnlySignature_KeepsSignatureValid()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, level: null);

        var filled = Engine.SetFormFieldsIncremental(signed, new Dictionary<string, string> { ["nome"] = "Aprovacao Apenas" });

        var info = Assert.Single(Engine.ReadSignatures(filled));
        Assert.True(info.IntegrityValid);
    }

    [Fact] // 2 assinaturas (certificadora P2 + aprovação) — as DUAS continuam íntegras depois do
    // preenchimento, não só a última.
    public void SetFormFieldsIncremental_TwoSignatures_BothRemainValid()
    {
        using var cert1 = TestCertificateFactory.CreateSelfSigned("Primeiro Signatario");
        using var cert2 = TestCertificateFactory.CreateSelfSigned("Segundo Signatario");
        var once = SignFormulario(cert1, DocMdpLevel.FormsAndSignatures);
        var twice = Engine.Sign(new SignRequest(once, cert2, null, null, null, null));

        var filled = Engine.SetFormFieldsIncremental(twice, new Dictionary<string, string> { ["nome"] = "Depois de 2 assinaturas" });

        var infos = Engine.ReadSignatures(filled);
        Assert.Equal(2, infos.Count);
        Assert.All(infos, i => Assert.True(i.IntegrityValid, $"{i.FieldName} deveria continuar íntegra"));
    }

    // ---- SetFormFieldsIncremental: recusas do gate (defesa em profundidade — mesmas checagens de
    // CanFillIncremental, mas o motor não pode CONFIAR que todo chamador sempre checa antes) -----------

    [Fact]
    public void SetFormFieldsIncremental_UnsignedDocument_ThrowsPdfSigningException()
    {
        Assert.Throws<PdfSigningException>(() => Engine.SetFormFieldsIncremental(Fixtures.Formulario(),
            new Dictionary<string, string> { ["nome"] = "Nao Deveria Escrever" }));
    }

    [Fact] // GATE CRÍTICO: P=1 recusa mesmo o iText permitindo a escrita silenciosamente (ver Contract.cs)
    public void SetFormFieldsIncremental_CertifiedNoChangesPermitted_ThrowsPdfSigningException()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormularioWithRawCertificationLevel(cert, AccessPermissions.NO_CHANGES_PERMITTED);
        Assert.Throws<PdfSigningException>(() => Engine.SetFormFieldsIncremental(signed,
            new Dictionary<string, string> { ["nome"] = "Nao Deveria Escrever" }));
    }

    [Fact] // NUNCA o crash cru do iText (PdfException) — tipo NEUTRO deste módulo, mesmo canal de
    // qualquer outra falha (não introduz o residual de StripSignatures, ver Contract.cs).
    public void SetFormFieldsIncremental_SignedXfaDocument_ThrowsPdfSigningException()
    {
        Assert.Throws<PdfSigningException>(() => Engine.SetFormFieldsIncremental(Fixtures.XfaAssinado(),
            new Dictionary<string, string> { ["qualquer"] = "valor" }));
    }

    // ---- SetFormFieldsIncremental: validação de campo — MESMAS regras de IPdfEditor.SetFormFields -----

    [Fact]
    public void SetFormFieldsIncremental_NonexistentField_ThrowsArgumentExceptionNamingField()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        var ex = Assert.Throws<ArgumentException>(() => Engine.SetFormFieldsIncremental(signed,
            new Dictionary<string, string> { ["campo-que-nao-existe"] = "x" }));
        Assert.Contains("campo-que-nao-existe", ex.Message);
    }

    [Fact] // "protocolo" (página 1) é readonly na fixture — ver PdfEditorTests, mesmo campo.
    public void SetFormFieldsIncremental_ReadOnlyField_ThrowsArgumentExceptionNamingField()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        var ex = Assert.Throws<ArgumentException>(() => Engine.SetFormFieldsIncremental(signed,
            new Dictionary<string, string> { ["protocolo"] = "outro valor" }));
        Assert.Contains("protocolo", ex.Message);
    }

    [Fact] // "botao" (push button, Other) — NUNCA escreve /V de um botão/campo de assinatura (Plano 4
    // vai assinar esses mesmos placeholders depois; um /V poluído seria um risco real, ver Contract.cs).
    public void SetFormFieldsIncremental_PushButtonField_ThrowsArgumentExceptionNamingField()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        var ex = Assert.Throws<ArgumentException>(() => Engine.SetFormFieldsIncremental(signed,
            new Dictionary<string, string> { ["botao"] = "x" }));
        Assert.Contains("botao", ex.Message);
    }

    [Fact]
    public void SetFormFieldsIncremental_InvalidComboValue_ThrowsArgumentException()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        Assert.Throws<ArgumentException>(() => Engine.SetFormFieldsIncremental(signed,
            new Dictionary<string, string> { ["estado"] = "XX" })); // não é SP/RJ/MG
    }

    [Fact]
    public void SetFormFieldsIncremental_InvalidRadioValue_ThrowsArgumentException()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        Assert.Throws<ArgumentException>(() => Engine.SetFormFieldsIncremental(signed,
            new Dictionary<string, string> { ["genero"] = "X" })); // não é M/F
    }

    [Fact] // vários campos de tipos diferentes na MESMA chamada — a validação em 2 passos não perde
    // nenhuma entrada (exemplar: PdfEditorTests.SetFormFields_MultipleFieldsAtOnce_AllApply).
    public void SetFormFieldsIncremental_MultipleFieldsAtOnce_AllApply()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);
        var filled = Engine.SetFormFieldsIncremental(signed, new Dictionary<string, string>
        {
            ["nome"] = "Ciclano",
            ["aceito"] = "Yes",
            ["genero"] = "F",
            ["estado"] = "MG",
        });

        var read = PdfEditorFactory.Create().ReadFormFields(filled);
        Assert.Equal("Ciclano", read.Single(f => f.Name == "nome").Value);
        Assert.Equal("Yes", read.Single(f => f.Name == "aceito").Value);
        Assert.Equal("F", read.Single(f => f.Name == "genero").Value);
        Assert.Equal("MG", read.Single(f => f.Name == "estado").Value);
    }

    [Fact] // TODAS as entradas validadas ANTES de escrever QUALQUER campo — 1 inválida derruba a
    // chamada INTEIRA, nenhum campo aplicado parcialmente, e a assinatura nem chega a ser tocada
    // (exemplar: PdfEditorTests.SetFormFields_OneValidOneInvalid_ThrowsAndAppliesNeither).
    public void SetFormFieldsIncremental_OneValidOneInvalid_ThrowsAndAppliesNeitherSignatureStillValid()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = SignFormulario(cert, DocMdpLevel.FormsAndSignatures);

        Assert.Throws<ArgumentException>(() => Engine.SetFormFieldsIncremental(signed, new Dictionary<string, string>
        {
            ["nome"] = "Deveria Falhar Junto",
            ["campo-inexistente"] = "x",
        }));

        // fixture assinada original não foi alterada (SetFormFieldsIncremental recebe bytes, nunca muta
        // in-place) — nem o valor mudou, nem a assinatura foi tocada.
        Assert.Equal("Fulano de Tal", PdfEditorFactory.Create().ReadFormFields(signed).Single(f => f.Name == "nome").Value);
        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);
    }
}
