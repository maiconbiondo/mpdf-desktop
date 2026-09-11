using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace mPdf.Signing;

/// Um certificado do repositório de assinatura, já FILTRADO e CLASSIFICADO por
/// `CertificateCatalog.ListSigningCertificates` — ver XML doc de lá para as regras de filtro e
/// classificação. `X509Certificate2 Certificate` exposto direto (não um tipo neutro do módulo): o
/// contrato de assinatura já aceita `X509Certificate2` (`SignRequest.Certificate` em Contract.cs),
/// então não há fronteira nova a proteger aqui — ao contrário de `ISigningEngine`, este catálogo não
/// esconde biblioteca de terceiro nenhuma atrás de um tipo próprio (é BCL puro).
/// `IsRsa=false` significa "certificado ECC, listado mas não habilitado pra assinar" — decisão de
/// UI (Task 3) desabilitar a opção; este catálogo só CLASSIFICA, nunca esconde (um operador que só
/// tem certificado ECC precisa VER por que nada está disponível, não uma lista vazia sem explicação).
/// `IsIcpBrasilPersonal`/`IsIcpBrasilCompany`: `true` quando o Common Name segue a convenção
/// e-CPF/e-CNPJ (ver `CertificateCatalog` para a origem da regra); os dois ficam `false` quando o
/// certificado não segue essa convenção (outras PKIs, certificados efêmeros de teste) — nunca os
/// dois `true` ao mesmo tempo.
public sealed record SigningCertificateInfo(
    X509Certificate2 Certificate,
    bool IsRsa,
    string DisplayName,
    bool IsIcpBrasilPersonal,
    bool IsIcpBrasilCompany);

/// Abstração sobre a FONTE dos certificados — único ponto de variabilidade pra teste. A
/// implementação real (`WindowsX509StoreReader`) nunca é exercitada pelos testes unitários de
/// classificação/formatação (nenhum instala certificado nenhum em lugar nenhum); só o teste de
/// integração dedicado usa `WindowsX509StoreReader` de fato, sempre em modo leitura, contra o
/// repositório real desta máquina.
internal interface IX509StoreReader
{
    X509Certificate2Collection Read();
}

/// Implementação real: repositório PESSOAL do usuário (`CurrentUser\My`), aberto SÓ LEITURA
/// (`OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly` — nunca cria o repositório se não existir,
/// nunca escreve nele, nunca instala/remove certificado nenhum).
///
/// Um ÚNICO caminho de enumeração cobre os 3 cenários de assinatura deste app:
///   - A1 (arquivo .pfx importado): certificado e chave privada ficam de fato neste repositório,
///     gerenciados pelo CryptoAPI/CNG nativo do Windows.
///   - A3 (token/smartcard espetado): o middleware do fabricante (driver + CSP/KSP) PUBLICA o
///     certificado neste MESMO repositório com uma chave privada "proxy" — usá-la de fato aciona o
///     driver/hardware por baixo (é isso que dispara o pedido de PIN na hora de assinar).
///   - Conectores de nuvem (BirdID, VIDaaS, NeoID, SafeID etc.): mesmo padrão do A3 — o conector
///     instala um provider CNG próprio que publica o certificado aqui, e a chave privada "proxy"
///     aciona a autenticação na nuvem do fabricante (app/SMS/biometria) por baixo.
/// Por isso esta classe nunca precisa saber qual dos três está por trás de um certificado — o
/// Windows já normalizou os três num repositório só, com uma API só. PIN, autenticação de app,
/// biometria: tudo isso é responsabilidade do CSP/KSP do fabricante que publicou o certificado aqui,
/// NUNCA deste código — este catálogo só enumera e classifica, nunca autentica.
internal sealed class WindowsX509StoreReader : IX509StoreReader
{
    public X509Certificate2Collection Read()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates;
    }
}

/// Catálogo de certificados de assinatura do repositório do Windows — BCL PURO (nenhuma referência a
/// iText: enumerar/classificar certificados não precisa de PDF nenhum; ver mPdf.Signing.csproj —
/// este arquivo não usa o `PackageReference` de iText do projeto).
public static class CertificateCatalog
{
    // "NOME:CPF|CNPJ" — MESMA convenção documentada em SignatureReader.SplitNameAndDocument (Leiaute
    // dos Certificados Digitais da SRF v4.1, §2.1.12/3.1.12 — presente em todos os perfis PF/PJ
    // e-CPF/e-CNPJ), reaplicada aqui pra classificar o TIPO do certificado (pessoa física/jurídica)
    // em vez de extrair o documento de uma assinatura já feita. Mesma decisão de Task 1 (Plano 4):
    // convenção do CN, não os OIDs de SubjectAlternativeName (exigiriam parsing ASN.1 manual sem API
    // pronta no BCL/iText) — ver reconciliação completa em SignatureReader.cs.
    private static readonly Regex DocumentSuffixPattern =
        new(@"^(?<nome>.*):(?<doc>\d{11}|\d{14})$", RegexOptions.Compiled);

    /// Certificados de assinatura do repositório REAL do Windows (`CurrentUser\My`, só leitura) —
    /// ponto de entrada público deste catálogo. Filtro (nesta ordem): tem chave privada + não
    /// expirado (`NotAfter`) + uso de chave de ASSINATURA (`digitalSignature` OU `nonRepudiation`,
    /// quando a extensão KeyUsage está presente — ver `HasSigningKeyUsage`). Nenhum filtro por
    /// algoritmo — RSA e ECC são ambos LISTADOS, só classificados (`IsRsa`) diferente.
    public static IReadOnlyList<SigningCertificateInfo> ListSigningCertificates() =>
        ListSigningCertificates(new WindowsX509StoreReader());

    /// Overload `internal` — único ponto de INJEÇÃO de `IX509StoreReader` (seam de teste, exposto via
    /// `InternalsVisibleTo` em AssemblyInfo.cs). Testes unitários de classificação/formatação usam um
    /// fake em memória aqui; NUNCA instalam certificado nenhum no repositório real (ver
    /// CertificateCatalogTests) — só o teste de integração dedicado chama o overload público acima,
    /// contra o repositório de verdade, com asserções puramente estruturais.
    internal static IReadOnlyList<SigningCertificateInfo> ListSigningCertificates(IX509StoreReader storeReader)
    {
        var now = DateTime.Now; // NotBefore/NotAfter do X509Certificate2 já são expostos em hora LOCAL
        var result = new List<SigningCertificateInfo>();
        foreach (X509Certificate2 cert in storeReader.Read())
        {
            // Revisão do coordenador: um certificado REJEITADO por qualquer filtro abaixo é NOSSO pra
            // descartar — só o que entra em `result` (Classify) passa a pertencer ao CHAMADOR. Sem
            // este Dispose, cada enumeração vazava 1 handle nativo por certificado rejeitado; num
            // repositório real (que acumula certificados expirados/antigos ao longo dos anos), isso
            // não é um caso raro.
            if (!cert.HasPrivateKey) { cert.Dispose(); continue; }
            if (cert.NotAfter <= now) { cert.Dispose(); continue; } // expirado -> EXCLUÍDO, nunca listado
            if (!HasSigningKeyUsage(cert)) { cert.Dispose(); continue; }

            result.Add(Classify(cert));
        }
        return result.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Extensão KeyUsage AUSENTE não é motivo de exclusão (RFC 5280 §4.2.1.3: o uso só é restringido
    // quando a extensão está presente) — só um certificado com a extensão PRESENTE e sem NENHUM dos
    // dois bits de assinatura marcado é excluído. Aceita `DigitalSignature` OU `NonRepudiation`:
    // revisão do coordenador — algumas ACs ICP-Brasil marcam o uso de assinatura como
    // `nonRepudiation`, às vezes SEM `digitalSignature` junto; um certificado assim sumia
    // silenciosamente do catálogo quando só o 1º bit era aceito. Nome honesto (não só
    // "HasDigitalSignatureKeyUsage"): o método aceita os dois bits que significam "pode assinar",
    // não só um deles.
    private static bool HasSigningKeyUsage(X509Certificate2 cert)
    {
        var extension = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return extension is null
            || extension.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)
            || extension.KeyUsages.HasFlag(X509KeyUsageFlags.NonRepudiation);
    }

    private static SigningCertificateInfo Classify(X509Certificate2 cert)
    {
        // RSA habilita a assinatura de fato (PadesSigningEngine é RSA-only — ver
        // GuardAgainstNonRsaCertificate); ECC continua LISTADO aqui, só marcado IsRsa=false —
        // desabilitar a opção na UI é decisão da Task 3, não deste catálogo.
        var isRsa = cert.GetRSAPublicKey() is not null;

        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var issuer = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        var match = DocumentSuffixPattern.Match(cn);

        string signerName;
        string? tipo = null;
        var isPersonal = false;
        var isCompany = false;
        if (match.Success)
        {
            signerName = match.Groups["nome"].Value.Trim();
            isPersonal = match.Groups["doc"].Value.Length == 11;
            isCompany = !isPersonal;
            tipo = isPersonal ? "e-CPF" : "e-CNPJ";
        }
        else
        {
            signerName = cn;
        }

        var validade = cert.NotAfter.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        var displayName = tipo is null
            ? $"{signerName} — {issuer} — válido até {validade}"
            : $"{signerName} ({tipo}) — {issuer} — válido até {validade}";

        return new SigningCertificateInfo(cert, isRsa, displayName, isPersonal, isCompany);
    }
}
