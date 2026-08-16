using System.Security.Cryptography.X509Certificates;
using iText.Bouncycastleconnector;
using iText.Commons.Bouncycastle.Cert;
using iText.Commons.Exceptions;
using iText.Forms;
using iText.Forms.Fields.Properties;
using iText.Forms.Form.Element;
using iText.Kernel.Exceptions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Signatures;

namespace mPdf.Signing;

/// Única classe deste módulo que assina de fato (mesmo espírito de `PdfEditor` em mPdf.Editing —
/// fronteira pública neutra em Contract.cs). ADAPTADO de
/// poc/mPdf.Poc.Signer/Signing/PadesSigner.cs, o código APROVADO pelo validador oficial do ITI no
/// Marco 0 (PAdES B-B: ETSI.CAdES.detached, SHA-256, append mode, DocMDP P=2, carimbo visível) — a
/// mecânica criptográfica (`PdfPadesSigner`/`StampingProperties().UseAppendMode()`/
/// `SignWithBaselineBProfile`/`X509Certificate2Signature`/`BuildChain`) NÃO foi reinventada, só
/// remapeada para o contrato neutro `SignRequest`/`DocMdpLevel`/`VisibleStampSpec` (ver task-1-report.md
/// para a lista completa do que mudou vs. o PoC, e por que nenhuma mudança é crypto-relevante). O
/// `SubFilter` gravado (`ETSI.CAdES.detached`) é o comportamento DEFAULT de `SignWithBaselineBProfile`
/// — nenhuma chamada nova/alterada aqui; é uma GARANTIA implícita da mecânica reusada, agora com
/// guarda de regressão do lado da LEITURA (`SignatureInfo.SubFilter`, ver SignatureReader.cs).
///
/// // HIPÓTESE (checklist do brief) + reflexão: `PdfSigner`/`PdfPadesSigner` +
/// `StampingProperties().UseAppendMode()` (SEMPRE — nunca reescreve); `SetCertificationLevel` só é
/// chamado quando `CertificationLevel == FormsAndSignatures`, e SÓ na 1ª assinatura — CONFIRMADO: o
/// enum real do iText 9.7.0 é `iText.Signatures.AccessPermissions` (não `CertificationLevel`, que era
/// o nome hipotetizado — mesmo achado que o PoC já fez, reutilizado aqui), com
/// `AccessPermissions.FORM_FIELDS_MODIFICATION` = P2 (o nível aprovado no ITI); a REGRA de recusa em
/// documento já assinado é NOVA nesta task (o PoC não a tinha — `Certify` lá era responsabilidade do
/// chamador via CLI) e vira `ArgumentException` pt-BR explícita dentro de `Sign`, decidida assim que
/// sabemos quantos sinais já existem (ver nota de revisão M8 sobre a redação anterior, abaixo) — nunca
/// uma falha lançada pelo iText por baixo. Bridge `X509Certificate2.GetRSAPrivateKey()` ->
/// `IExternalSignature`: o PoC já resolveu (`X509Certificate2Signature`, adaptado literalmente) —
/// REUSADO, não reinventado; a política RSA-only vira 2 checagens EXPLÍCITAS no topo de `Sign`
/// (`GuardAgainstNonRsaCertificate` — revisão I1: chave pública não-RSA é um caso, chave RSA mas
/// privada inacessível — token removido/PIN recusado, o caminho mais comum na prática — é OUTRO,
/// cada um com mensagem própria), em vez de deixar a falha lazy (`InvalidOperationException` genérica
/// só na hora de assinar de fato, lá dentro do PoC). Mudança de CAMADA de validação (fail-fast,
/// mensagens nomeando a causa) e de GRANULARIDADE (2 causas distintas em vez de 1 mensagem genérica),
/// nunca de mecânica criptográfica: nenhum certificado RSA-com-chave-acessível que o PoC aceitava
/// passa a ser recusado, e nenhum certificado sem chave RSA acessível (ECC OU RSA sem chave) que o
/// PoC recusava passa a ser aceito.
internal sealed class PadesSigningEngine : ISigningEngine
{
    public byte[] Sign(SignRequest request)
    {
        try
        {
            // InspectDocument abre o PDF (pode lançar ITextException num arquivo corrompido/inválido,
            // ou BadPasswordException num PDF protegido por senha — revisão M4) — precisa estar DENTRO
            // do try: nenhuma chamada ao iText neste método pode escapar sem passar por
            // WrapPassword/WrapGeneric, mesmo quando ainda não chegamos na parte que assina de fato
            // (achado de revisão: a 1ª versão desta task tinha essa chamada ANTES do try, vazando
            // ITextException cru pra fora da fronteira neutra num PDF malformado).
            var inspection = InspectDocument(request.Pdf);

            // M8 (revisão): a redação anterior deste comentário dizia que a recusa abaixo acontecia
            // "ANTES de tocar o iText" — impreciso: `InspectDocument`, uma linha acima, JÁ abriu o PDF
            // via iText pra saber `ExistingSignatures`. A REGRA em si (recusar `CertificationLevel` !=
            // None num doc já assinado) é NOSSA, não do iText — é isso que "antes de tocar o iText"
            // pretendia dizer (decidimos SEM chamar `PdfPadesSigner`/`SignWithBaselineBProfile`), mas a
            // frase literal estava errada sobre TER aberto o documento.
            if (request.CertificationLevel is DocMdpLevel.FormsAndSignatures && inspection.ExistingSignatures > 0)
                throw new ArgumentException(
                    "Não é possível certificar (DocMDP) um documento que já contém assinatura(s) — " +
                    "nível de certificação só é aceito na 1ª assinatura do documento.");

            // I1 (revisão final — o par de FormFillIncrementalEngine/CanFillIncremental, ver
            // DocMdpCertificationProbe): documento já CERTIFICADO com P=1 (NO_CHANGES_PERMITTED), ou com
            // certificação declarada mas ilegível (fail-closed), proíbe QUALQUER alteração — inclusive
            // uma NOVA assinatura de aprovação, mesmo sem `CertificationLevel` nenhum no pedido. ISO
            // 32000 §12.8.2.2/Table 254 é categórico: P=1 barra toda mudança; o iText NÃO impede a
            // escrita (mesmo achado crítico documentado no gate de preenchimento) e
            // `VerifySignatureIntegrityAndAuthenticity()` continuaria `true` nas duas assinaturas depois
            // — só um validador que CHECA DocMDP (o do ITI incluído) reportaria a violação, tarde demais
            // pro usuário que já viu ✔ na hora de assinar.
            if (inspection.CertificationProbe == DocMdpCertificationProbe.Result.UnreadableCertification ||
                (inspection.CertificationProbe == DocMdpCertificationProbe.Result.KnownLevel && inspection.CertificationP == 1))
                throw new PdfSigningException(
                    "O documento é certificado e não permite alterações (nível máximo de proteção). " +
                    "Não é possível adicionar assinaturas.");

            GuardAgainstNonRsaCertificate(request.Certificate);

            if (request.Stamp is { } stampToValidate)
                ValidateStamp(stampToValidate, inspection.PageCount);

            var chain = BuildChain(request.Certificate);
            var signature = new X509Certificate2Signature(request.Certificate);

            using var input = new MemoryStream(request.Pdf);
            using var output = new MemoryStream();

            var reader = new PdfReader(input);
            var padesSigner = new PdfPadesSigner(reader, output);
            padesSigner.SetStampingProperties(new StampingProperties().UseAppendMode()); // SEMPRE append

            // M7 (achado do revisor, escalado a hard gate pré-rollout): `inspection.FieldName` já vem
            // livre de colisão com QUALQUER campo existente, assinado OU em branco — ver
            // ChooseNonCollidingFieldName abaixo pro achado completo.
            var props = new SignerProperties().SetFieldName(inspection.FieldName);
            if (request.Reason is not null) props.SetReason(request.Reason);
            if (request.Location is not null) props.SetLocation(request.Location);
            if (request.CertificationLevel is DocMdpLevel.FormsAndSignatures)
                // P=2, spec §5.3: formulários + novas assinaturas — o único nível que o Marco 0 validou.
                props.SetCertificationLevel(AccessPermissions.FORM_FIELDS_MODIFICATION);

            if (request.Stamp is { } stamp)
                ApplyVisibleStamp(props, stamp, request.Certificate, request.Reason);

            padesSigner.SignWithBaselineBProfile(props, chain, signature);
            return output.ToArray();
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
    }

    public IReadOnlyList<SignatureInfo> ReadSignatures(byte[] pdf) => SignatureReader.Read(pdf);

    // Task 6 (Plano 4): delega pra FormFillIncrementalEngine (mesmo espírito de ReadSignatures acima
    // delegar pra SignatureReader — uma classe por responsabilidade dentro do módulo).
    public FillPermission CanFillIncremental(byte[] pdf) => FormFillIncrementalEngine.CanFillIncremental(pdf);
    public byte[] SetFormFieldsIncremental(byte[] pdf, IReadOnlyDictionary<string, string> values) =>
        FormFillIncrementalEngine.SetFormFieldsIncremental(pdf, values);

    private static void ApplyVisibleStamp(
        SignerProperties props, VisibleStampSpec stamp, X509Certificate2 certificate, string? reason)
    {
        var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID)
            .SetContent(new SignedAppearanceText()
                .SetSignedBy(cn)
                .SetReasonLine(reason is null ? "" : $"Motivo: {reason}")
                .SetSignDate(DateTime.Now));
        var r = stamp.Rect;
        props.SetPageNumber(stamp.PageIndex + 1) // contrato 0-based -> iText 1-based
             .SetPageRect(new Rectangle(
                 (float)r.LeftPt, (float)r.BottomPt,
                 (float)(r.RightPt - r.LeftPt), (float)(r.TopPt - r.BottomPt)))
             .SetSignatureAppearance(appearance);
    }

    /// Recusa ANTES de tocar `PdfPadesSigner`/`SignerProperties.SetPageNumber` — achados de revisão:
    /// I2 (`PageIndex` fora do intervalo do documento fazia `IndexOutOfRangeException` NÃO tipada
    /// escapar) e M3 (um `PdfQuad` degenerado/invertido — largura ou altura não-positiva — era aceito
    /// silenciosamente e produzia um carimbo INVISÍVEL de 0 área, nunca um erro; pior que recusar na
    /// hora). Mesmo espírito de `PdfEditor.ValidatePageIndex` (mPdf.Editing/PdfEditor.cs).
    private static void ValidateStamp(VisibleStampSpec stamp, int pageCount)
    {
        if (stamp.PageIndex < 0 || stamp.PageIndex >= pageCount)
            throw new ArgumentOutOfRangeException(nameof(stamp), stamp.PageIndex,
                $"Índice de página do carimbo {stamp.PageIndex} fora do intervalo válido " +
                $"(0 a {pageCount - 1}).");

        var r = stamp.Rect;
        if (r.RightPt <= r.LeftPt || r.TopPt <= r.BottomPt)
            throw new ArgumentException(
                $"Retângulo do carimbo é degenerado ou invertido (Left={r.LeftPt}, Bottom={r.BottomPt}, " +
                $"Right={r.RightPt}, Top={r.TopPt}) — largura e altura precisam ser positivas.",
                nameof(stamp));
    }

    /// Fail-fast ANTES de tocar o iText — a política é RSA-only (mesmo algoritmo que o PoC aprovou no
    /// ITI). Revisão I1: 2 causas DISTINTAS de "não dá pra assinar com este certificado", cada uma com
    /// mensagem própria — a versão anterior conflava as duas numa checagem só
    /// (`GetRSAPrivateKey() is null`), o que confundia o caso mais comum na prática (token A3
    /// removido, PIN recusado, certificado importado sem a flag `Exportable`) com "certificado errado
    /// (ECC)", levando o usuário a trocar de certificado quando o problema real era acesso ao
    /// token/driver.
    private static void GuardAgainstNonRsaCertificate(X509Certificate2 certificate)
    {
        // Chave PÚBLICA não é RSA (ex.: ECC) -> é mesmo uma política de algoritmo, nomeada explicitamente.
        if (certificate.GetRSAPublicKey() is null)
            throw new PdfSigningException(
                "Este motor de assinatura aceita somente certificados RSA (política aprovada no " +
                "Marco 0 com o validador oficial do ITI) — o certificado informado usa outro " +
                "algoritmo de chave.");

        // Chave pública É RSA, mas a privada não está acessível por este processo/usuário — mensagem
        // separada, nomeando o sintoma real em vez da política de algoritmo (que não é o problema aqui).
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is null)
            throw new PdfSigningException(
                "Não foi possível acessar a chave privada do certificado (token removido, PIN " +
                "recusado ou permissão negada?).");
    }

    /// Tudo que `Sign` precisa saber sobre o documento ANTES de decidir se/como assinar — 1 `PdfDocument`
    /// aberto só, nunca reaberto por checagem (mesma disciplina de custo de `CountSignatures` original).
    private readonly record struct DocumentInspection(
        int ExistingSignatures, int PageCount, string FieldName,
        DocMdpCertificationProbe.Result CertificationProbe, int CertificationP);

    private static DocumentInspection InspectDocument(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        int existing = new SignatureUtil(doc).GetSignatureNames().Count;
        int pageCount = doc.GetNumberOfPages();
        string fieldName = ChooseNonCollidingFieldName(doc);
        var certProbe = DocMdpCertificationProbe.ReadLevel(doc, out int certP);
        return new DocumentInspection(existing, pageCount, fieldName, certProbe, certP);
    }

    /// M7 (achado do revisor, escalado a hard gate PRÉ-ROLLOUT): o nome gerado ANTES
    /// (`$"Assinatura{existing + 1}"`, contando só assinaturas JÁ ASSINADAS via `SignatureUtil.
    /// GetSignatureNames()`) podia colidir com um campo de assinatura EM BRANCO pré-existente no
    /// documento — nossas PRÓPRIAS fixtures de formulário têm um placeholder assim
    /// (`fixture-formulario.pdf`, campo `"assinatura1"`, ver `mPdf.Editing.Tests.PdfEditorTests.
    /// ReadFormFields_FixtureFormulario_...`). Provado ao vivo pelo revisor: pedido de carimbo em
    /// (100,100)-(280,160) resultou no carimbo aparecendo em (300,700)-(450,750) — o iText assinou
    /// DENTRO do campo pré-existente (herdando o `/Rect` ANTIGO daquele placeholder) em vez de criar um
    /// campo NOVO no retângulo pedido; o retângulo escolhido pelo usuário foi DESCARTADO
    /// SILENCIOSAMENTE, sem erro nenhum. Fix: varre TODOS os nomes de campo de formulário existentes
    /// (`PdfAcroForm.GetAllFormFields()` devolve campos assinados E em branco igualmente) e escolhe o
    /// primeiro `"AssinaturaN"` que ainda não existe — comparação `OrdinalIgnoreCase` (defesa contra
    /// qualquer forma de colisão por capitalização, não só a exata observada).
    private static string ChooseNonCollidingFieldName(PdfDocument doc)
    {
        var acroForm = PdfAcroForm.GetAcroForm(doc, false);
        var taken = acroForm is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(acroForm.GetAllFormFields().Keys, StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            var candidate = $"Assinatura{n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// Constrói a cadeia completa (folha -> raiz) — ADAPTADO literalmente de
    /// poc/mPdf.Poc.Signer/Signing/PadesSigner.cs (o ITI precisa da cadeia embutida na assinatura).
    private static IX509Certificate[] BuildChain(X509Certificate2 certificate)
    {
        var factory = BouncyCastleFactoryCreator.GetFactory();
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // cadeia só para embutir
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.Build(certificate);
        if (chain.ChainStatus.Any(s => s.Status.HasFlag(X509ChainStatusFlags.PartialChain)))
            throw new PdfSigningException(
                "Não foi possível montar a cadeia completa do certificado (cadeia parcial). " +
                "Instale a cadeia da AC (ICP-Brasil) na máquina e tente novamente.");
        return chain.ChainElements
            .Select(e => factory.CreateX509Certificate(new MemoryStream(e.Certificate.RawData)))
            .ToArray();
    }

    private static PdfPasswordRequiredException WrapPassword(BadPasswordException ex) =>
        new("PDF protegido por senha — não é possível assinar sem a senha correta.", ex);

    private static PdfSigningException WrapGeneric(ITextException ex) =>
        new("Não foi possível assinar o PDF.", ex);
}
