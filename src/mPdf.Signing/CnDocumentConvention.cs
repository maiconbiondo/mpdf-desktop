using System.Text.RegularExpressions;

namespace mPdf.Signing;

/// Convenção ICP-Brasil de Common Name "NOME:CPF|CNPJ" (Leiaute RFB v4.1 §2.1.12/3.1.12) — consolidado
/// aqui (Plano 9, Task 3, revisão) porque os DOIS lados do módulo precisavam da MESMA regra: leitura
/// (`SignatureReader.SplitNameAndDocument`, original, Plano 4) e escrita (`PadesSigningEngine`
/// /`StampAppearanceRenderer`, Plano 9 — o carimbo visível novo também precisa extrair CPF/CNPJ do CN
/// pra mostrar no texto). A 1ª versão desta task duplicava a regex/método em `PadesSigningEngine.cs`
/// (a fronteira de arquivos daquele brief não permitia tocar `SignatureReader.cs`); revisão do
/// coordenador liberou a fronteira especificamente pra esta consolidação — mesmo assembly (`mPdf.
/// Signing`), custo de acoplamento zero (nenhum dos dois lados expõe isto publicamente, `internal`
/// nos dois sentidos).
///
/// HIPÓTESE (checklist do brief original) + reconciliação (ver XML doc anterior em SignatureReader.cs,
/// preservada aqui): extração de CPF/CNPJ via OIDs SubjectAlternativeName (2.16.76.1.3.x) exigiria
/// decodificar ASN.1 DER manualmente, sem API pronta no BCL/iText — DECISÃO: usar só a convenção do CN
/// ("&lt;NOME&gt;:&lt;CPF|CNPJ&gt;", mesma seção do leiaute oficial, presente em TODOS os perfis PF/PJ),
/// zero parsing binário novo. Certificados fora da convenção (efêmeros de teste, outras PKIs) não casam
/// o padrão -> `Document` fica `null`, `Name` é o CN cru, sem quebrar nada.
internal static class CnDocumentConvention
{
    private static readonly Regex DocumentSuffixPattern =
        new(@"^(?<nome>.*):(?<doc>\d{11}|\d{14})$", RegexOptions.Compiled);

    public static (string Name, string? Document) SplitNameAndDocument(string cn)
    {
        var match = DocumentSuffixPattern.Match(cn);
        return match.Success
            ? (match.Groups["nome"].Value.Trim(), match.Groups["doc"].Value)
            : (cn, null);
    }
}
