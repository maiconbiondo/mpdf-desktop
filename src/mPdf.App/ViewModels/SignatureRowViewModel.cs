using System.Globalization;
using mPdf.Signing;

namespace mPdf.App.ViewModels;

/// Wrapper de EXIBIÇÃO em torno de `SignatureInfo` (Task 4, Plano 4) — o painel de Assinaturas é
/// somente-LEITURA (nenhum campo editável, ao contrário de `FormFieldViewModel`): este wrapper só
/// projeta o registro imutável do contrato neutro de `mPdf.Signing` em strings pt-BR já formatadas pro
/// binding direto do `ItemsControl` (`SignaturePanel.xaml`) — nenhuma lógica de
/// formatação/derivação vive em XAML, mesma disciplina já seguida pelo resto do app. Sem
/// `ObservableObject`/propriedades mutáveis: nada muda depois da construção (ao contrário de
/// `FormFieldViewModel.EditedValue`), então propriedades comuns bastam — 1 instância NOVA por linha a
/// cada refresh do cache (`DocumentViewModel.RefreshSignaturesAsync`), nunca reaproveitada por
/// identidade (mesmo espírito de `FormFieldEditors`/`AnnotationsByPage`).
public sealed class SignatureRowViewModel
{
    /// Registro original — fonte-da-verdade (mesmo padrão de `FormFieldViewModel.Data`). Exposto
    /// público pra bindings XAML que precisem de um campo cru (ex.: `Data.IntegrityValid` num
    /// `DataTrigger` de cor) sem precisar duplicar propriedade por propriedade neste wrapper.
    public SignatureInfo Data { get; }

    /// Posição (1-based) desta assinatura dentro da lista devolvida por `ISigningEngine.ReadSignatures`
    /// e o TOTAL de assinaturas do documento — usados só por `CoverageLabel` abaixo ("Cobre a revisão N
    /// de M"). HIPÓTESE assumida (não uma garantia formal do spec PDF): a ORDEM de
    /// `SignatureUtil.GetSignatureNames()` reflete a ordem de CRIAÇÃO das assinaturas — cada assinatura
    /// incremental é ANEXADA ao AcroForm, nunca reordenada (mesma suposição implícita que
    /// `PadesSigningEngine.Sign` já faz ao nomear campos sequencialmente, `Assinatura{existing+1}`).
    /// Puramente de EXIBIÇÃO, nunca usado em nenhuma verificação criptográfica — mesmo se a ordem real
    /// algum dia divergir, o pior caso é um rótulo "revisão N" cosmeticamente impreciso, nunca um
    /// comportamento de segurança errado.
    private readonly int _ordinal;
    private readonly int _total;

    public SignatureRowViewModel(SignatureInfo data, int ordinal, int total)
    {
        Data = data;
        _ordinal = ordinal;
        _total = total;
    }

    public string SignerName => Data.SignerName;

    /// Rótulo do tipo de certificado (brief: "kind icon e-CPF/e-CNPJ se derivável do tamanho de
    /// Document") — 11 dígitos = CPF (e-CPF, pessoa física), 14 = CNPJ (e-CNPJ, pessoa jurídica); ver
    /// convenção completa em `SignatureReader.SplitNameAndDocument`/XML doc de `SignatureInfo.Document`
    /// (Contract.cs). `Document` nulo (certificado fora da convenção do Leiaute RFB — ex.: certificados
    /// efêmeros de teste, outras PKIs) -> rótulo vazio, nunca um texto errado/adivinhado.
    public string DocumentKindLabel => Data.Document?.Length switch
    {
        11 => "e-CPF",
        14 => "e-CNPJ",
        _ => "",
    };

    public string? DocumentNumber => Data.Document;

    /// Revisão (item 1): `DocumentNumber` (dígitos crus) era computado/testado mas NUNCA bindado no
    /// XAML — um usuário não-técnico não conseguia VER o CPF/CNPJ que o brief lista como campo próprio.
    /// Rótulo completo, já mascarado, pronto pro binding (`"CPF 123.456.789-01"`/`"CNPJ
    /// 12.345.678/0001-99"`) — `null` quando `Document` é nulo (certificado fora da convenção RFB): o
    /// painel não deve mostrar uma linha vazia, só omitir (ver `HasDocumentNumber` abaixo).
    public string? DocumentNumberLabel => Data.Document?.Length switch
    {
        11 => $"CPF {FormatCpf(Data.Document)}",
        14 => $"CNPJ {FormatCnpj(Data.Document)}",
        _ => null,
    };

    public bool HasDocumentNumber => DocumentNumberLabel is not null;

    // Máscara pura de exibição — o contrato (`SignatureReader.DocumentSuffixPattern`) já garante que
    // `Document` de 11/14 dígitos é SEMPRE só dígitos (`\d{11}|\d{14}`), então nenhuma sanitização extra
    // é necessária aqui além de fatiar por posição fixa.
    private static string FormatCpf(string digits) =>
        $"{digits[..3]}.{digits[3..6]}.{digits[6..9]}-{digits[9..11]}";

    private static string FormatCnpj(string digits) =>
        $"{digits[..2]}.{digits[2..5]}.{digits[5..8]}/{digits[8..12]}-{digits[12..14]}";

    /// Revisão (item 2): a linha do signatário era composta via `MultiBinding`/`StringFormat` direto no
    /// XAML (`"{0}  ({1})"`) — quando `DocumentKindLabel` é `""` (qualquer certificado fora da convenção
    /// RFB, incluso os efêmeros usados em teste), o resultado renderizado era o artefato
    /// `"Fulano  ()"` (parênteses vazios). Composição movida pro VM (headless-testável, mesmo espírito
    /// dos outros rótulos computados desta classe) — só inclui o parêntese quando há um tipo pra mostrar.
    public string SignerLine => string.IsNullOrEmpty(DocumentKindLabel)
        ? SignerName
        : $"{SignerName}  ({DocumentKindLabel})";

    /// pt-BR (brief, texto EXATO pro caso nulo: "data não disponível") — `SignedAt` já é `null` no
    /// contrato pra qualquer valor ausente/inválido de `/M` (ver XML doc de
    /// `SignatureReader.TryToDateTimeOffset`), nenhum tratamento extra precisa acontecer aqui.
    /// `CultureInfo.InvariantCulture` — mesmo precedente de `CertificateCatalog.cs`
    /// (`cert.NotAfter.ToString("MM/yyyy", CultureInfo.InvariantCulture)`): o padrão "dd/MM/yyyy HH:mm"
    /// já é literal (24h, sem nome de mês/AM-PM), então a cultura não muda o resultado — só documenta a
    /// intenção e blinda contra qualquer cultura exótica no ambiente de execução.
    public string DateLabel => Data.SignedAt is { } dt
        ? dt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
        : "data não disponível";

    public string ReasonLabel =>
        string.IsNullOrEmpty(Data.Reason) ? "Motivo não informado." : $"Motivo: {Data.Reason}";

    /// O indicador LOAD-BEARING do painel (brief) — ✔/✖, texto EXATO pedido pelo brief.
    public string IntegrityLabel => Data.IntegrityValid ? "✔ Íntegra" : "✖ Violada";

    /// REVISÃO (I2, painel de revisão da Task 6/Plano 4 — "trust-panel misinformation"): a redação
    /// ORIGINAL ("Cobre a revisão N de M") usava `_ordinal`/`_total` — posição/contagem de ASSINATURAS —
    /// como se fossem "revisão N de M revisões". Isso já era só uma aproximação (ver comentário de
    /// `_ordinal`/`_total` acima) mas ficou FALSO na prática assim que o Task 6 introduziu o
    /// preenchimento incremental: um documento com 1 ÚNICA assinatura, preenchido DEPOIS via
    /// `SetFormFieldsIncremental` (que anexa uma revisão NOVA sem adicionar nenhuma assinatura),
    /// continua tendo `_ordinal=1`/`_total=1` — o rótulo antigo mostrava "Cobre a revisão 1 de 1",
    /// lido pelo usuário como "cobre tudo", exatamente no momento em que `CoversWholeDocument` virou
    /// `false` (a revisão do preenchimento ficou de FORA da assinatura). `_total` conta ASSINATURAS,
    /// nunca REVISÕES — não é a métrica certa pra "quanto do documento está coberto".
    ///
    /// FIX: a fonte de verdade agora é SÓ `CoversWholeDocument` (brief: "'Cobre o documento inteiro'
    /// quando true; NUNCA implica cobertura total quando false") — `true` -> "Cobre o documento
    /// inteiro"; `false` -> frase que não numera revisão nenhuma, só afirma o fato verificável ("houve
    /// adições depois desta assinatura", verdadeiro tanto pra uma assinatura anterior num documento
    /// multi-assinado quanto pra uma assinatura seguida de preenchimento incremental). O ordinal/total
    /// de assinaturas (`_ordinal`/`_total`) sobrevive SÓ como IDENTIFICAÇÃO (" — assinatura N de M"),
    /// nunca mais como aritmética de cobertura — e só aparece quando `_total > 1` (múltiplas
    /// assinaturas de verdade): pra uma ÚNICA assinatura seguida de preenchimento, "assinatura 1 de 1"
    /// seria ruído que ainda lembraria a redação antiga, sem agregar identificação nenhuma (não há
    /// outra assinatura pra distinguir desta).
    public string CoverageLabel => Data.CoversWholeDocument
        ? "Cobre o documento inteiro"
        : _total > 1
            ? $"Cobre uma revisão anterior do documento (houve adições depois desta assinatura) — assinatura {_ordinal} de {_total}"
            : "Cobre uma revisão anterior do documento (houve adições depois desta assinatura)";

    public string ChainTrustedLabel => Data.ChainTrustedWindows
        ? "Cadeia confiável neste computador: sim"
        : "Cadeia confiável neste computador: não";

    /// Tooltip pt-BR (brief: "com tooltip explicando não != forjada") — mesma reconciliação de
    /// `SignatureInfo.ChainTrustedWindows` (Contract.cs): "não" é um sinal LOCAL (ex.: a raiz ICP-Brasil
    /// não está instalada nesta máquina), nunca prova de assinatura forjada; a validação OFICIAL
    /// continua sendo a do ITI.
    public string ChainTrustedTooltip =>
        "\"Não\" indica só que a cadeia de certificação não pôde ser validada NESTE computador (ex.: a " +
        "raiz ICP-Brasil não está instalada) — não significa que a assinatura é falsa. A validação " +
        "oficial é sempre a do ITI (validar.iti.gov.br).";

    /// DocMDP (brief: "'Certificada: alterações restritas' quando FormsAndSignatures") — `None` (o caso
    /// comum, assinatura de aprovação) não mostra rótulo NENHUM, nunca um "não certificada" — ver XML
    /// doc de `DocMdpLevel` (Contract.cs): nenhum caso de uso deste app produz um 3º nível.
    public string? CertificationLabel =>
        Data.Certification == DocMdpLevel.FormsAndSignatures ? "Certificada: alterações restritas" : null;

    public bool HasCertificationLabel => CertificationLabel is not null;

    /// Clique-pra-navegar (brief) só faz sentido quando o widget geométrico existe — ver XML doc de
    /// `SignatureInfo.StampPageIndex`/`StampRect` (Contract.cs): os dois são `null` juntos numa
    /// assinatura sem carimbo visível (caso mais comum na prática).
    public bool HasStamp => Data.StampPageIndex is not null;
}
