using System.Security.Cryptography.X509Certificates;
using mPdf.Editing;

namespace mPdf.Signing;

/// Nível de proteção DocMDP (spec ICP-Brasil §5.3/PAdES) — só os 2 valores que o Marco 0 precisou:
/// `None` (assinatura de aprovação, sem transform DocMDP) e `FormsAndSignatures` (P=2: permite
/// preencher formulários e adicionar novas assinaturas depois — o nível que validar.iti.gov.br
/// reconheceu como "Aprovada" no PoC, ver poc/mPdf.Poc.Signer/Signing/PadesSigner.cs). P=1
/// (NO_CHANGES_PERMITTED) e P=3 (ANNOTATION_MODIFICATION) do enum `AccessPermissions` do iText
/// nunca são expostos — nenhum caso de uso deste app pede um documento "congelado" (P=1) nem
/// "só comentários" (P=3) depois de assinado.
public enum DocMdpLevel { None, FormsAndSignatures }

/// Retângulo de carimbo visível, em pontos PDF, no frame NÃO-rotacionado da página (mesma convenção
/// de `AnnotationData`/`FormFieldData` em mPdf.Editing — `/Rotate` é atributo de EXIBIÇÃO, nunca muda
/// o `/Rect` gravado). `PageIndex` 0-based, mesma convenção do resto do contrato do app.
///
/// `ImageBytes` (Plano 21): quando NÃO-null, a APARÊNCIA do carimbo vira SÓ essa imagem (PNG/JPG —
/// a "rubrica" do usuário), aspect-fit centralizada no retângulo, sem moldura/selo/texto do carimbo
/// padrão. `null` (o caso comum, retrocompatível) = carimbo padrão vetorial+texto de sempre. A imagem
/// é PURAMENTE aparência: nenhuma mudança na mecânica criptográfica (append/DocMDP/nome de campo
/// idênticos — ver `PadesSigningEngine.ApplyVisibleStamp`). A validade jurídica vem da assinatura, não
/// do desenho.
public sealed record VisibleStampSpec(int PageIndex, PdfQuad Rect, byte[]? ImageBytes = null);

/// Pedido de assinatura. `CertificationLevel`: só é aceito (!= None) na 1ª assinatura do documento —
/// `PadesSigningEngine.Sign` RECUSA (`ArgumentException` pt-BR) um `CertificationLevel != None` quando
/// o documento já contém 1+ assinatura(s); um documento certificado só pode receber assinaturas de
/// APROVAÇÃO depois da 1ª (spec §5.3 — 2ª assinatura em doc certificado nunca carrega DocMDP próprio).
public sealed record SignRequest(byte[] Pdf, X509Certificate2 Certificate, string? Reason, string? Location,
    VisibleStampSpec? Stamp, DocMdpLevel? CertificationLevel);

/// Relatório de 1 assinatura lida do PDF. `Document`: CPF/CNPJ extraído do subject do certificado
/// (ver HIPÓTESE + reconciliação em `SignatureReader.SplitNameAndDocument`) — lido SÓ da convenção do
/// Common Name (`"NOME:CPF|CNPJ"`, Leiaute RFB v4.1 §2.1.12/3.1.12); os OIDs de `SubjectAlternativeName`
/// (2.16.76.1.3.x) NÃO são interpretados nesta versão (exigiriam parsing ASN.1 manual — ver
/// reconciliação completa no código-fonte). `null` quando o certificado não segue a convenção do CN
/// (ex.: certificados efêmeros de teste, certificados de outras PKIs). `SubFilter`: identificador do
/// perfil de assinatura gravado no PDF (`/SubFilter` do dicionário `/Sig`) — `"ETSI.CAdES.detached"`
/// é o valor EXIGIDO pela ICP-Brasil (PAdES B-B) e o que `PadesSigningEngine` sempre grava; exposto
/// aqui como GUARDA DE REGRESSÃO permanente (um futuro refactor pra `SignDetached`/`adbe.pkcs7.detached`
/// mudaria este valor silenciosamente sem quebrar nenhum outro teste — ver
/// `PadesSigningEngineTests.Sign_UsesEtsiCadesDetachedSubFilter`). `CoversWholeDocument`: `false` é o
/// valor ESPERADO (não um alerta) pra qualquer assinatura que não seja a ÚLTIMA de um documento
/// multi-assinado — cada assinatura cobre a revisão em que foi criada, só a mais recente cobre o
/// arquivo INTEIRO; Tasks 3-5 (UI) não devem tratar `false` aqui como sinal de problema por si só.
/// `ChainTrustedWindows`: cadeia validada via `X509Chain` do Windows (repositório de raízes
/// confiáveis da MÁQUINA), incluindo os certificados INTERMEDIÁRIOS embutidos na própria assinatura
/// (`PdfPKCS7.GetSignCertificateChain()` como `ExtraStore`) — a validação OFICIAL de uma assinatura
/// ICP-Brasil continua sendo a do ITI (validar.iti.gov.br); este campo é um sinal LOCAL auxiliar,
/// nunca a fonte de verdade legal, e continua `false` sempre que a RAIZ (AC-Raiz ICP-Brasil) não
/// estiver instalada como confiável nesta máquina — dependência que este módulo não resolve nem tenta
/// resolver (fora de escopo: instalar/confiar raízes é decisão do gerente de TI, não deste código).
/// `StampPageIndex`/`StampRect` (Task 4, Plano 4): página (0-based) e retângulo (pontos PDF, frame
/// NÃO-ROTACIONADO) do PRIMEIRO widget do campo de assinatura — mesma convenção EXATA de
/// `VisibleStampSpec`/`FormFieldData.WidgetRect` (mPdf.Editing/Contract.cs: `/Rotate` é atributo de
/// EXIBIÇÃO, nunca muda o `/Rect` gravado). Os DOIS ficam `null` juntos (nunca só um) quando a
/// assinatura não tem carimbo visível (`SignRequest.Stamp == null` na hora de assinar — o caso mais
/// comum na prática, "aprovação invisível") ou quando o widget tem um `/Rect` degenerado (largura ou
/// altura não-positiva — mesmo critério de `PadesSigningEngine.ValidateStamp` do lado da ESCRITA).
/// Consumido só pelo painel de Assinaturas (Task 4) pra decidir se um clique navega/destaca a página
/// (`DocumentViewModel.SelectSignature`) — nunca usado em nenhuma verificação criptográfica.
public sealed record SignatureInfo(string FieldName, string SignerName, string? Document, string SubFilter,
    DateTimeOffset? SignedAt, bool CoversWholeDocument, bool IntegrityValid, bool ChainTrustedWindows,
    string? Reason, DocMdpLevel Certification, int? StampPageIndex, PdfQuad? StampRect);

/// Decisão de permissão de preenchimento INCREMENTAL de formulário sobre um documento JÁ ASSINADO
/// (Task 6, Plano 4 — a costura deferida do Plano 3c §5.2). Tabela de decisão RECONCILIADA por sonda ao
/// vivo (probe console referenciando itext 9.7.0 direto + `fixture-formulario.pdf` assinada de verdade
/// — ver task-6-report.md, seção "Reconciliação DocMDP", para os números medidos de cada caso):
///
///   - `NotSigned`: `IPdfEditor.HasSignatures(pdf) == false` — este motor não se aplica; o chamador usa
///     o caminho NORMAL (`mPdf.Editing.IPdfEditor.SetFormFields`, gate `GuardAgainstSignedDocument`).
///   - `XfaUnsupported`: documento ASSINADO com formulário XFA (`IPdfEditor.HasXfa(pdf) == true`) — o
///     mesmo achado empírico documentado em `mPdf.Editing/Contract.cs` (`PdfAcroForm.GetAcroForm`/
///     `SignatureUtil` lançam `PdfException: Root element is missing` ao tocar um `/XFA` malformado/
///     dummy) se aplica aqui: `SignatureUtil`, usado por `ReadSignatures`/DocMDP, lançaria se chamado
///     direto num doc XFA-e-assinado (confirmado ao vivo contra `fixture-xfa-assinado.pdf` — sonda
///     dedicada no probe). `CanFillIncremental`/`SetFormFieldsIncremental` checam `HasXfa` ANTES de
///     tocar `SignatureUtil`/`PdfAcroForm` (via `IPdfEditor`, que já resolve isso com uma varredura crua
///     do dicionário) — nunca introduzem esse crash, mesmo espírito do residual já conhecido de
///     `StripSignatures` (Plano 3c) que este módulo deliberadamente NÃO herda.
///   - `DeniedByDocMdp`: documento CERTIFICADO (1ª assinatura com transform DocMDP — `/Perms/DocMDP` no
///     catálogo) com `P == 1` (`NO_CHANGES_PERMITTED` — ISO 32000 Table 254: nenhuma mudança é
///     permitida, qualquer alteração invalida a garantia declarada pelo certificador). ACHADO CRÍTICO
///     (sonda ao vivo, item "8" do probe): o iText NÃO IMPEDE a escrita — `PdfAcroForm.GetField(...).
///     SetValue(...)` num documento P=1, em modo append, é aceito SILENCIOSAMENTE pela biblioteca, E
///     `PdfPKCS7.VerifySignatureIntegrityAndAuthenticity()` continua devolvendo `true` depois (a
///     integridade CRIPTOGRÁFICA da revisão assinada não foi tocada — só uma revisão NOVA foi
///     acrescentada por cima, o que é exatamente o que "modo append" significa). Ou seja:
///     `IntegrityValid` NÃO é um oráculo de conformidade DocMDP — só de integridade byte-a-byte da
///     revisão assinada. Este módulo é o ÚNICO ponto de enforcement da regra P=1; sem o gate abaixo,
///     `SetFormFieldsIncremental` produziria um PDF que TODO leitor conforme (inclusive o validador do
///     ITI) reportaria como violação de DocMDP, mesmo com a assinatura permanecendo "íntegra" no sentido
///     estrito.
///   - `Allowed`: os 3 casos restantes, TODOS confirmados preservando `IntegrityValid` de TODA
///     assinatura existente (sonda ao vivo, itens 3/4/6 do probe — números no relatório):
///     (a) certificado com `P == 2` (`FORM_FIELDS_MODIFICATION` — o único nível que
///     `PadesSigningEngine.Sign` grava, spec §5.3); (b) certificado com `P == 3`
///     (`ANNOTATION_MODIFICATION` — ISO 32000 Table 254: P=3 é um SUPERCONJUNTO de P=2, permite os
///     MESMOS preenchimentos de formulário MAIS anotações — nenhum motivo pra recusar aqui só porque
///     este app nunca EMITE P=3, ver `DocMdpLevel`); (c) SEM certificação nenhuma (só assinatura(s) de
///     APROVAÇÃO — nenhum `/Perms/DocMDP` no catálogo) — legal per ISO 32000 (mudanças são permitidas
///     por padrão; só uma certificação declara restrição), confirmado com um documento assinado por 1
///     E por 2 certificados de aprovação em sequência (as DUAS assinaturas continuam íntegras depois do
///     preenchimento).
///
/// `P` é lido do dicionário BRUTO `catalog/Perms/DocMDP/Reference[]/TransformParams/P` — MAIS PRECISO
/// que a heurística de `SignatureReader.ReadCertificationLevel` (que escaneia `/Reference` de TODAS as
/// assinaturas e só reconhece `P==2`, colapsando qualquer outro valor — inclusive `P==1` — em `None`):
/// aquele método serve só para EXIBIÇÃO no painel de Assinaturas (nunca precisou distinguir P=1 de "sem
/// certificação"); este contexto de GATE precisa da distinção exata.
public enum FillPermission
{
    NotSigned,
    Allowed,
    DeniedByDocMdp,
    XfaUnsupported,
}

/// Fronteira ÚNICA por onde iText pode ser referenciado neste módulo (mesmo espírito de `IPdfEditor`
/// em mPdf.Editing/Contract.cs — ver AgplGuardTests). Consumida pelo App só através desta interface e
/// dos tipos neutros acima.
public interface ISigningEngine
{
    /// Assina SEMPRE em modo incremental (append) — nunca reescreve o PDF, preserva qualquer
    /// assinatura anterior íntegra (invariante central: assinar 2x mantém a 1ª `IntegrityValid`).
    byte[] Sign(SignRequest request);
    /// Leitura pura, sem gate — lê inclusive de documentos assinados por certificados reais fora
    /// deste app (compat: `fixture-carimbo.pdf`, gerado no Marco 0 com o PoC self-signed antigo).
    IReadOnlyList<SignatureInfo> ReadSignatures(byte[] pdf);

    // --- Task 6 (Plano 4): preenchimento incremental de formulário em documento assinado ------------
    // Ver XML doc completo de `FillPermission` acima para a tabela de decisão DocMDP reconciliada.

    /// Decide se `SetFormFieldsIncremental` pode preencher `pdf` sem invalidar/violar nenhuma
    /// assinatura existente — ver `FillPermission`. Leitura pura, sem gate (não muta `pdf`). Mesmo
    /// canal de erro de `ReadSignatures`/`Sign` (`PdfSigningException`/`PdfPasswordRequiredException`)
    /// pra PDF corrompido/protegido por senha.
    FillPermission CanFillIncremental(byte[] pdf);

    /// Preenche 1+ campos (nome -> valor) de um documento JÁ ASSINADO, em modo APPEND — a assinatura
    /// existente nunca é tocada/reescrita, só uma revisão NOVA é acrescentada por cima (mesma mecânica
    /// de `Sign`: `PdfDocument(reader, writer, StampingProperties().UseAppendMode())`). GATE: recusa
    /// (`PdfSigningException`, mensagem nomeando a causa) quando `CanFillIncremental(pdf) !=
    /// FillPermission.Allowed` — `NotSigned` (chame o caminho normal de `mPdf.Editing` em vez deste),
    /// `DeniedByDocMdp` (P=1, ver `FillPermission`), `XfaUnsupported` (formulário XFA). MESMAS regras de
    /// validação de campo que `IPdfEditor.SetFormFields` (Plano 3c): campo citado em `values` que não
    /// existe -> `ArgumentException` pt-BR nomeando o campo; campo `IsReadOnly` -> `ArgumentException`
    /// nomeando o campo; campo `FormFieldType.Other` (push button OU campo de assinatura — inclusive um
    /// placeholder AINDA NÃO assinado, que o próprio Plano 4 pode assinar depois) -> `ArgumentException`
    /// nomeando o campo, NUNCA escreve `/V` de um `/Sig`; valor fora das opções válidas de
    /// Radio/Combo/ListBox -> `ArgumentException`. TODAS as entradas são validadas ANTES de escrever
    /// QUALQUER campo (mesmo espírito de `SetFormFields`/`RotatePages`/etc.).
    byte[] SetFormFieldsIncremental(byte[] pdf, IReadOnlyDictionary<string, string> values);
}

public static class SigningEngineFactory
{
    public static ISigningEngine Create() => new PadesSigningEngine();
}

/// Canal de erro NEUTRO deste módulo (mesmo espírito de `PdfEditingException` em mPdf.Editing) —
/// qualquer falha do iText/BouncyCastle ao assinar ou ler chega ao chamador como uma exceção deste
/// namespace, nunca como um tipo `iText.*`/`Org.BouncyCastle.*` cru. A exceção original é preservada
/// em `InnerException`.
public class PdfSigningException : Exception
{
    public PdfSigningException(string message, Exception? inner = null) : base(message, inner) { }
}

/// PDF protegido por senha — caso mais específico e mais ACIONÁVEL de `PdfSigningException` (o
/// chamador pode pedir a senha ao usuário em vez de só reportar falha genérica). Mesmo espírito de
/// `PdfPasswordRequiredException` em mPdf.Editing/Contract.cs.
public sealed class PdfPasswordRequiredException : PdfSigningException
{
    public PdfPasswordRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}
