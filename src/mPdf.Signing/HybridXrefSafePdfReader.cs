using iText.Kernel.Pdf;

namespace mPdf.Signing;

/// Task 1 (Plano 10 — hotfix híbrido). RAIZ DO BUG (dissecada ao vivo por decompilação de
/// itext.kernel.dll/itext.sign.dll 9.7.0 — ver task-1-report.md pro relato completo, incluindo as 2
/// hipóteses de fix INTERMEDIÁRIAS que a review/investigação empírica descartou antes desta versão):
///
/// 1) `PdfDocument.Open` (`trailer = new PdfDictionary(reader.trailer)`) copia o trailer INTEIRO do
///    `PdfReader` pra dentro da revisão nova — se a entrada é HÍBRIDA (trailer clássico com `/XRefStm`,
///    PDF 32000-1:2008 §7.5.8.4), essa chave vem junto.
/// 2) Em modo append (SEMPRE, nos 2 motores), `PdfDocument.Open` TAMBÉM força
///    `writer.properties.isFullCompression = reader.HasXrefStm()` incondicionalmente
///    (`OverrideFullCompressionInWriterProperties`, decompilado) — `HasXrefStm()`/o campo `xrefStm` é
///    uma flag de UNIÃO, decompilada em 2 pontos de escrita DISTINTOS: (i) sozinho, quando o ÚLTIMO
///    `startxref` do arquivo aponta DIRETO pra um objeto de stream (documento moderno comum, full
///    compression, NUNCA híbrido — `hybridXref` fica `false`); (ii) JUNTO com `hybridXref=true`, quando
///    o último trailer é CLÁSSICO e carrega `/XRefStm` (híbrido de verdade). `WriterProperties().
///    SetFullCompressionMode(true)` (a HIPÓTESE original do brief) é MOOT: o iText já pede full
///    compression sozinho sempre que `xrefStm` é `true`, por QUALQUER dos 2 motivos.
/// 3) Com `isFullCompression=true` (via `xrefStm`) E `hybridXref=true`, `PdfXrefTable.
///    WriteXrefTableAndTrailer` escreve as DUAS formas na revisão nova (stream + tabela clássica) —
///    empilhando um 2º nível de hibridez sobre a hibridez já existente na entrada. O PDFium/Docnet
///    usado por este app (`mPdf.Rendering`) não resolve corretamente essa pilha: o carimbo/widget novo
///    fica INVISÍVEL (0px), mesmo a assinatura continuando criptograficamente íntegra.
///
/// DUAS TENTATIVAS DE FIX DESCARTADAS (achados empíricos, ambos com evidência reproduzível — ver
/// task-1-report.md pra dissecação completa):
///
/// - **"Força tudo clássico, incondicional"** (zera `xrefStm` E `hybridXref` SEMPRE): corrige o caso
///   híbrido, mas C1 (achado de review): sobre um documento NÃO-híbrido cuja entrada já é xref-STREAM
///   puro (caso (i) acima — o formato moderno mais comum), zerar `xrefStm` força
///   `OverrideFullCompressionInWriterProperties` a desligar full compression pra ESTA revisão, que
///   então vira uma tabela CLÁSSICA cujo `/Prev` aponta pra um objeto de STREAM (a única entrada que a
///   entrada tinha). iText não sabe reler essa transição (mesma limitação do próximo item, invertida) —
///   `PdfException` ("/Pages must be PdfDictionary") no reread, `ReadSignatures` lança, PDFium 0px.
/// - **"Só zera hybridXref, preserva xrefStm sempre"** (a correção que a review pediu como Variant B):
///   corrige o C1 acima (documento xref-stream puro nunca é tocado), mas achado empírico ADICIONAL
///   desta investigação (fixture híbrida de verdade, com uma revisão anterior CLÁSSICA real por trás —
///   exatamente a forma de um documento híbrido typical, incluindo o contrato real do usuário): com
///   `xrefStm=true` preservado e `hybridXref=false` forçado, a revisão nova vira uma xref STREAM PURA
///   cujo `/Prev` aponta pra uma revisão CLÁSSICA anterior (a ponte híbrida de entrada). Decompilação de
///   `PdfReader.ReadXrefStream`/`ReadXref` mostra que o LOOP de `/Prev` de `ReadXrefStream` só sabe
///   caminhar entre objetos de STREAM — ao encontrar texto clássico ("trailer\n<<...") no offset de
///   `/Prev`, retorna `false` silenciosamente; o `ReadXref` de nível superior cai no fallback de
///   `RebuildXref` (scan linear "N G obj", sem noção de objetos comprimidos DENTRO de um `/Type/ObjStm`)
///   — que funciona por acidente em fixtures triviais (tudo tem um header top-level visível) mas
///   PERDE objetos genuinamente comprimidos (o caso comum quando a revisão anterior foi escrita com
///   full compression de verdade, como qualquer append intermediário deste próprio app faria). Mesmo
///   sintoma do C1 (PdfException, ReadSignatures lança, PDFium 0px) — comprovado ao vivo contra uma
///   fixture híbrida de 2+ revisões reais (nunca hand-crafted em binário — só texto ASCII pra ponte +
///   escrita 100% nativa do iText pro resto), inclusive num append TRIVIAL sem assinatura nenhuma.
///
/// O FIX (esta versão, condicional): as 2 mutações (remover `/XRefStm` do trailer, zerar `xrefStm` E
/// `hybridXref`) só acontecem quando a entrada É híbrida (`hybridXref` verdadeiro ANTES do fix). Nesse
/// caso — e SÓ nesse caso — a revisão nova fica puramente CLÁSSICA, o que é SEMPRE seguro porque a
/// PRÓPRIA entrada híbrida já tinha uma camada clássica alcançável (é a definição de híbrido: trailer
/// clássico + `/XRefStm`) — `/Prev` aponta clássico-pra-clássico, nunca clássico-pra-stream. Quando a
/// entrada NÃO é híbrida (`hybridXref` já `false` — documento clássico comum OU xref-stream puro),
/// NADA é tocado: o comportamento é IDÊNTICO ao iText sem fix nenhum (stream puro continua stream puro,
/// clássico continua clássico) — nem o C1 do review nem o achado adicional acima se aplicam, porque a
/// condição de entrada pro fix nunca dispara.
///
/// Mecânica: `PdfReader.ReadPdf()` é `protected internal virtual` (`PdfReader` não é `sealed`) —
/// acessível a uma subclasse mesmo fora do assembly do iText (`protected` sempre permite override
/// cross-assembly, diferente de `internal` puro). Sobrescrita aqui: deixa `base.ReadPdf()` fazer TODO o
/// parsing de verdade e, em seguida, aplica as mutações condicionais acima ANTES de `PdfDocument.Open`
/// copiar o trailer e consultar `HasXrefStm()`/`hybridXref` pra decidir a forma da revisão nova.
///
/// Escopo: só os 2 pontos de append (`PadesSigningEngine.Sign`, `FormFillIncrementalEngine.
/// SetFormFieldsIncremental`) usam esta subclasse — `InspectDocument`/`CanFillIncremental` (leitura
/// pura, nunca escrevem revisão nova) continuam com `PdfReader` comum, sem necessidade nenhuma do fix.
internal sealed class HybridXrefSafePdfReader : PdfReader
{
    public HybridXrefSafePdfReader(Stream inputStream) : base(inputStream)
    {
    }

    // C# exige "protected" (sem "internal") ao sobrescrever um membro "protected internal" de OUTRO
    // assembly — a parte "internal" do modificador original é escopada ao assembly do iText, que esta
    // subclasse não integra; "protected" sozinho já preserva o dispatch virtual corretamente (é assim
    // que PdfDocument.Open, dentro do iText, acaba chamando ESTA sobrescrita via `reader.ReadPdf()`).
    protected override void ReadPdf()
    {
        base.ReadPdf();
        // CONDICIONAL de propósito (ver XML doc acima, "O FIX"): só entra aqui quando a entrada É
        // híbrida. Documento xref-stream puro (não-híbrido) ou clássico comum NUNCA tem `hybridXref`
        // verdadeiro aqui — as 2 linhas abaixo, e a remoção da chave, ficam intocadas, preservando
        // byte-a-byte o comportamento que o iText já tinha sem este fix (achado C1 de review).
        if (hybridXref)
        {
            trailer?.Remove(PdfName.XRefStm);
            xrefStm = false;
            hybridXref = false;
        }
    }
}
