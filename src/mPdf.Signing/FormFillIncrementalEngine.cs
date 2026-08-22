using System.Linq;
using iText.Commons.Exceptions;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using mPdf.Editing;

namespace mPdf.Signing;

/// Motor de preenchimento INCREMENTAL de formulário sobre documento JÁ ASSINADO (Task 6, Plano 4 —
/// spec §5.2, a costura deferida do Plano 3c). Separado de `PadesSigningEngine` (mesmo espírito de
/// `SignatureReader`: uma classe por responsabilidade dentro do módulo, `PadesSigningEngine` só
/// delega). Ver XML doc completo de `FillPermission`/`ISigningEngine.CanFillIncremental`/
/// `SetFormFieldsIncremental` em Contract.cs para a tabela de decisão DocMDP e a reconciliação
/// empírica — não repetida aqui.
internal static class FormFillIncrementalEngine
{
    public static FillPermission CanFillIncremental(byte[] pdf)
    {
        var editor = PdfEditorFactory.Create();
        bool hasSignatures, hasXfa;
        try
        {
            hasSignatures = editor.HasSignatures(pdf);
            // HasXfa só importa quando o documento está assinado (o único caso em que este motor se
            // aplica) — mesma ordem de decisão de `MainViewModel.OpenPath` (HasXfa é barato, raio-X do
            // dicionário cru, mas só precisamos da resposta aqui pra decidir XfaUnsupported).
            hasXfa = hasSignatures && editor.HasXfa(pdf);
        }
        // `IPdfEditor.HasSignatures`/`HasXfa` lançam os tipos NEUTROS de mPdf.Editing — precisam ser
        // re-envolvidos nos tipos deste módulo pra não vazar um canal de erro DIFERENTE do que
        // `ISigningEngine` promete (`ReadSignatures`/`Sign` já usam `PdfSigningException`/
        // `PdfPasswordRequiredException`, nunca os de mPdf.Editing).
        catch (mPdf.Editing.PdfPasswordRequiredException ex)
        {
            throw new PdfPasswordRequiredException(PasswordMessage, ex);
        }
        catch (mPdf.Editing.PdfEditingException ex)
        {
            throw new PdfSigningException(GenericReadErrorMessage, ex);
        }

        if (!hasSignatures) return FillPermission.NotSigned;
        // Residual de StripSignatures (Plano 3c) NÃO herdado aqui: HasXfa checado ANTES de tocar
        // SignatureUtil/PdfAcroForm (que lançariam PdfException — ver Contract.cs).
        if (hasXfa) return FillPermission.XfaUnsupported;

        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            // Revisão final: discriminador movido pra DocMdpCertificationProbe (ÚNICA implementação do
            // módulo — PadesSigningEngine.Sign agora reusa o MESMO, ver histórico completo lá).
            var probe = DocMdpCertificationProbe.ReadLevel(doc, out int p);
            // Só P=1 proíbe (ver Contract.cs — ACHADO CRÍTICO: o iText não impede a escrita nem P=1,
            // este motor é o ÚNICO ponto de enforcement do PREENCHIMENTO). P=2/P=3/ausência de
            // certificação -> Allowed. `UnreadableCertification` -> DeniedByDocMdp, FAIL CLOSED: o
            // documento se declara certificado (`/Perms/DocMDP` existe) mas o `P` real não pôde ser
            // confirmado — nunca degrada silenciosamente pra "sem certificação" (que liberaria o
            // preenchimento).
            return probe switch
            {
                DocMdpCertificationProbe.Result.NoCertification => FillPermission.Allowed,
                DocMdpCertificationProbe.Result.KnownLevel => p == 1 ? FillPermission.DeniedByDocMdp : FillPermission.Allowed,
                DocMdpCertificationProbe.Result.UnreadableCertification => FillPermission.DeniedByDocMdp,
                _ => FillPermission.DeniedByDocMdp, // nunca alcançável — mesma disciplina defensiva do resto do módulo
            };
        }
        catch (BadPasswordException ex) { throw new PdfPasswordRequiredException(PasswordMessage, ex); }
        catch (ITextException ex) { throw new PdfSigningException(GenericReadErrorMessage, ex); }
    }

    public static byte[] SetFormFieldsIncremental(byte[] pdf, IReadOnlyDictionary<string, string> values)
    {
        // Defesa em profundidade — mesmo espírito de `GuardAgainstSignedDocument` em `PdfEditor`: este
        // motor não confia que todo chamador (presente e futuro) sempre checou `CanFillIncremental`
        // antes. Reusa a MESMA decisão (nenhuma checagem duplicada com regra diferente).
        switch (CanFillIncremental(pdf))
        {
            case FillPermission.NotSigned:
                throw new PdfSigningException(
                    "Documento não está assinado — use o preenchimento normal (edição comum), não este motor.");
            case FillPermission.XfaUnsupported:
                throw new PdfSigningException(
                    "Formulário XFA não é suportado em documento assinado.");
            case FillPermission.DeniedByDocMdp:
                throw new PdfSigningException(
                    "Documento certificado não permite nenhuma alteração (DocMDP P=1) — preenchimento " +
                    "recusado para preservar a garantia declarada pela certificação.");
        }

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            // Task 1 (Plano 10): ver HybridXrefSafePdfReader.cs (mPdf.Signing) pro diagnóstico completo
            // — mesmo fix de PadesSigningEngine.Sign, mesmo bug de classe (append sobre doc híbrido
            // propaga um 2º nível de hibridez pra revisão nova; valor preenchido fica invisível pro
            // PDFium/mPdf.Rendering).
            var reader = new HybridXrefSafePdfReader(input);
            var writer = new PdfWriter(output);
            // SEMPRE append — a assinatura existente nunca é tocada/reescrita, só uma revisão NOVA é
            // acrescentada por cima (mesma mecânica de `PadesSigningEngine.Sign`). PROVA CENTRAL
            // (reconciliada ao vivo, ver Contract.cs): `IntegrityValid` de toda assinatura existente
            // sobrevive a este caminho em TODOS os casos Allowed testados (P2, P3-implícito, aprovação
            // apenas, múltiplas assinaturas).
            using (var doc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode()))
            {
                var acroForm = PdfAcroForm.GetAcroForm(doc, false);

                // TODAS as entradas validadas (existe + não readonly + tipo preenchível + opção válida)
                // ANTES de escrever QUALQUER campo — mesmo espírito de `PdfEditor.SetFormFields`.
                var toSet = new List<(PdfFormField Field, string Value)>();
                foreach (var (name, value) in values)
                {
                    var field = acroForm?.GetField(name);
                    if (field is null)
                        throw new ArgumentException(
                            $"Campo de formulário não encontrado: '{name}'.", nameof(values));
                    if (field.IsReadOnly())
                        throw new ArgumentException(
                            $"Campo '{name}' é somente leitura — não é possível definir valor.", nameof(values));

                    var type = MapFormFieldType(field);
                    // Other cobre push button E campo de assinatura (inclusive um placeholder AINDA NÃO
                    // assinado) — NUNCA escreve `/V` de um `/Sig` (o próprio Plano 4 assina esses mesmos
                    // placeholders depois; um `/V` poluído aqui seria um risco real, mesma nota de
                    // política de `PdfEditor.SetFormFields`, Task 1 fix do Plano 3c).
                    if (type == FormFieldType.Other)
                        throw new ArgumentException(
                            $"Campo '{name}' não é preenchível (botão ou assinatura).", nameof(values));
                    if (type is FormFieldType.Radio or FormFieldType.Combo or FormFieldType.ListBox)
                    {
                        var options = ReadOptions(field, type);
                        if (!options.Contains(value))
                            throw new ArgumentException(
                                $"Valor '{value}' inválido para o campo '{name}' — opções válidas: " +
                                $"{string.Join(", ", options)}.", nameof(values));
                    }
                    toSet.Add((field, value));
                }

                // SetValue(string) de 1 argumento regenera a aparência por default (mesmo achado do
                // Task 1/Plano 3c — comportamento de campo INDEPENDENTE de como o PdfDocument foi
                // aberto, confirmado ao vivo em modo append também, ver Contract.cs/task-6-report.md).
                foreach (var (field, value) in toSet) field.SetValue(value);
            }
        }
        catch (BadPasswordException ex) { throw new PdfPasswordRequiredException(PasswordMessage, ex); }
        catch (ITextException ex) { throw new PdfSigningException(GenericWriteErrorMessage, ex); }
        return output.ToArray();
    }

    private const string PasswordMessage =
        "PDF protegido por senha — não é possível preencher sem a senha correta.";
    private const string GenericReadErrorMessage =
        "Não foi possível avaliar a permissão de preenchimento do PDF.";
    private const string GenericWriteErrorMessage =
        "Não foi possível preencher o formulário do PDF assinado.";

    // Duplicado de `PdfEditor.MapFormFieldType`/`ReadOptions` (mPdf.Editing) — mesmo precedente de
    // `SignatureReader.ReadStampGeometry` mirando `PdfEditor.BuildFormFieldData`: reimplementado aqui,
    // não chamado (os 2 são `private` em `PdfEditor`, e cada módulo mantém sua PRÓPRIA fronteira de
    // iText — ver AgplGuardTests/PrivateAssets=compile). Checkbox E radio são AMBOS `PdfButtonFormField`
    // (`/FT /Btn`); Combo/ListBox são AMBOS `PdfChoiceFormField` (`/FT /Ch`); `PdfSignatureFormField` e
    // qualquer outro tipo caem em `Other`.
    private static FormFieldType MapFormFieldType(PdfFormField field) => field switch
    {
        PdfTextFormField => FormFieldType.Text,
        PdfChoiceFormField choice => choice.IsCombo() ? FormFieldType.Combo : FormFieldType.ListBox,
        PdfButtonFormField button => button.IsRadio() ? FormFieldType.Radio
            : button.IsPushButton() ? FormFieldType.Other
            : FormFieldType.Checkbox,
        _ => FormFieldType.Other,
    };

    private static IReadOnlyList<string> ReadOptions(PdfFormField field, FormFieldType type)
    {
        if (type is FormFieldType.Combo or FormFieldType.ListBox && field is PdfChoiceFormField choice)
        {
            var arr = choice.GetOptions();
            if (arr is null) return Array.Empty<string>();
            var list = new List<string>(arr.Size());
            for (int i = 0; i < arr.Size(); i++)
            {
                var entry = arr.Get(i);
                if (entry is PdfArray pair && pair.Size() > 0)
                    list.Add(pair.Get(0) is PdfString pairStr ? pairStr.ToUnicodeString() : pair.Get(0).ToString()!);
                else if (entry is PdfString s)
                    list.Add(s.ToUnicodeString());
            }
            return list;
        }
        if (type is FormFieldType.Checkbox or FormFieldType.Radio)
        {
            var states = field.GetAppearanceStates();
            return states is null ? Array.Empty<string>() : states.Where(s => s != "Off").Distinct().ToArray();
        }
        return Array.Empty<string>();
    }
}
