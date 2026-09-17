using iText.Kernel.Pdf;

namespace mPdf.Signing;

/// Discriminador COMPARTILHADO do nível DocMDP efetivo de um documento — ÚNICA implementação no módulo
/// (revisão final, I1: extraído de `FormFillIncrementalEngine.ReadDocMdpLevel` pra `PadesSigningEngine.
/// Sign` reusar sem duplicar). ACHADO DO REVISOR: `Sign` aceitava uma 2ª assinatura (mesmo só de
/// aprovação, sem `CertificationLevel` nenhum) sobre um documento JÁ certificado `P=1`
/// (`NO_CHANGES_PERMITTED`) — probado ao vivo: 2ª assinatura aceita, as duas `IntegrityValid`, mas ISO
/// 32000 §12.8.2.2/Table 254 proíbe CATEGORICAMENTE qualquer alteração num documento `P=1`, inclusive
/// novas assinaturas; um validador conforme (o do ITI incluído) reporta violação de DocMDP mesmo assim.
/// Um usuário que assina um PDF de TERCEIRO `P=1` via este app veria ✔ na hora, mas o documento seria
/// rejeitado depois de arquivado. Só `FormFillIncrementalEngine` tinha esse enforcement até agora (pro
/// caminho de preenchimento incremental) — `PadesSigningEngine.Sign` precisa do MESMO gate, reusando o
/// MESMO discriminador (não uma cópia com deriva potencial).
internal static class DocMdpCertificationProbe
{
    /// 3 estados DISTINTOS, nunca colapsados num só `int?` — ver XML doc de `ReadLevel` abaixo pro
    /// histórico completo da reconciliação (2 furos corrigidos, default do spec pra `/P` ausente).
    public enum Result
    {
        /// Sem `/Perms/DocMDP` no catálogo — documento sem certificação nenhuma, só assinatura(s) de
        /// aprovação (legal per ISO 32000).
        NoCertification,
        /// `/Perms/DocMDP` presente E um `/Reference` com `/TransformMethod` `/DocMDP` (o transform
        /// REAL, não um impostor) com `/TransformParams` presente foi encontrado — `p` (parâmetro out
        /// de `ReadLevel`) contém o valor: o de `/TransformParams/P` quando a chave existe, ou `2` (o
        /// DEFAULT do spec — ISO 32000-1 Table 254, `/P` é OPCIONAL) quando `/TransformParams` existe
        /// mas `/P` não.
        KnownLevel,
        /// `/Perms/DocMDP` EXISTE (o documento SE DECLARA certificado) mas não foi possível achar/ler o
        /// `P` real (array `/Reference` ausente/vazio, ou só contém transforms de OUTRO tipo). FAIL
        /// CLOSED — nunca degrada pra `NoCertification`.
        UnreadableCertification,
    }

    /// Lê o nível DocMDP efetivo via `/Perms/DocMDP` do CATÁLOGO (a assinatura certificadora oficial do
    /// documento, se houver) — MAIS PRECISO que `SignatureReader.ReadCertificationLevel` (que escaneia
    /// `/Reference` de TODAS as assinaturas e só reconhece `P==2`, colapsando qualquer outro valor —
    /// inclusive `P==1` — em "sem certificação"): aquele método serve só pra EXIBIÇÃO no painel de
    /// Assinaturas, nunca precisou distinguir P=1 de "sem certificação"; os 2 chamadores deste método
    /// (`FormFillIncrementalEngine.CanFillIncremental`, `PadesSigningEngine.Sign`) precisam da distinção
    /// exata.
    ///
    /// HISTÓRICO DA RECONCILIAÇÃO (2 furos achados pelo revisor no `ReadDocMdpP` original, Task 6):
    ///
    /// (a) A varredura original pegava o PRIMEIRO `/TransformParams/P` que achasse em QUALQUER entrada
    /// do array `/Reference` — mas um `/Reference` pode ter MÚLTIPLAS entradas de transform DIFERENTES
    /// (`/DocMDP`, `/FieldMDP`, `/UR3`, ...) na MESMA assinatura, cada uma com seu PRÓPRIO
    /// `/TransformParams`. Um PDF hostil/malformado pode plantar uma entrada de `/FieldMDP` ANTES da
    /// entrada `/DocMDP` real, com uma chave `/P` ESPÚRIA — a varredura ingênua "primeiro /P que achar"
    /// devolvia esse valor plantado em vez do `/P` real do `/DocMDP` — bypass COMPLETO do gate. Fix: só
    /// uma entrada cujo `/TransformMethod` seja LITERALMENTE `/DocMDP` conta.
    ///
    /// (b) A varredura original devolvia `null` (== "sem certificação") sempre que `/Reference` estivesse
    /// ausente OU nenhuma entrada tivesse `/P` legível — SEM distinguir esse caso de "não há
    /// `/Perms/DocMDP` nenhum" (o caso legítimo de aprovação-apenas). Um documento com `/Perms/DocMDP`
    /// PRESENTE (o documento SE DECLARA certificado) mas com `/Reference` ausente/vazio/malformado
    /// degradava SILENCIOSAMENTE pra "sem certificação" — o oposto do enforcement que este gate existe
    /// pra fazer. Fix: `/Perms/DocMDP` presente sem um `P` real e legível -> `UnreadableCertification`,
    /// tratado como recusa pelos chamadores — FAIL CLOSED.
    public static Result ReadLevel(PdfDocument doc, out int p)
    {
        p = 0;
        var perms = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms);
        var docMdpSigDict = perms?.GetAsDictionary(PdfName.DocMDP);
        if (docMdpSigDict is null) return Result.NoCertification;

        var refArray = docMdpSigDict.GetAsArray(PdfName.Reference);
        if (refArray is not null)
        {
            for (int i = 0; i < refArray.Size(); i++)
            {
                var refDict = refArray.GetAsDictionary(i);
                // (a) só a entrada CUJO /TransformMethod é literalmente /DocMDP conta — nunca confunde
                // com um /FieldMDP (ou qualquer outro transform) que porventura também tenha uma chave
                // /P no seu /TransformParams, mesmo que apareça ANTES no array.
                if (refDict is null || !PdfName.DocMDP.Equals(refDict.GetAsName(PdfName.TransformMethod)))
                    continue;
                var transformParams = refDict.GetAsDictionary(PdfName.TransformParams);
                // `/TransformParams` AUSENTE (não o dicionário inteiro, só a chave) continua fail-closed
                // — cai no `continue` e, sem outra entrada /DocMDP no array, termina em
                // `UnreadableCertification` abaixo. Só quando o dicionário `/TransformParams` EXISTE mas
                // a chave `/P` DENTRO dele está ausente é que o default do spec entra em jogo (linha
                // seguinte) — os 2 casos são estruturalmente diferentes e não compartilham fallback.
                if (transformParams is null) continue;
                // ISO 32000-1 Table 254: /P é OPCIONAL, com valor DEFAULT 2 quando ausente — não é um
                // caso "ilegível"/hostil, é um documento perfeitamente válido que confia no default do
                // próprio spec.
                var pNum = transformParams.GetAsNumber(PdfName.P);
                p = pNum?.IntValue() ?? 2;
                return Result.KnownLevel;
            }
        }
        // (b) /Perms/DocMDP EXISTE mas nenhuma entrada /DocMDP com /P legível foi encontrada — FAIL
        // CLOSED, nunca `NoCertification`.
        return Result.UnreadableCertification;
    }
}
