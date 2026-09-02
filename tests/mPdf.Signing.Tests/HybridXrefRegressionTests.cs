using iText.Kernel.Pdf;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.Signing.Tests;

/// Task 1 (Plano 10 — hotfix híbrido): o bug-raiz diagnosticado ao vivo por decompilação de iText 9.7.0
/// (ver `HybridXrefSafePdfReader.cs` pro relato técnico completo — inclusive as 2 tentativas de fix
/// descartadas por evidência empírica reproduzível ANTES da versão condicional final: uma corrompia
/// documentos xref-stream puros/não-híbridos, C1 de review; a outra — literalmente o que a review
/// pediu como "Variant B" — corrompia o RE-READ de documentos híbridos "de verdade" com uma revisão
/// clássica real por trás, achado empírico adicional desta mesma investigação). Documento HÍBRIDO
/// (rev1 clássica com um stream de xref construído à mão cobrindo os mesmos objetos + rev2 ponte
/// clássica com `/Prev` e `/XRefStm` apontando pra offsets DIFERENTES e válidos — mesma forma do
/// contrato real) assinado por `PadesSigningEngine.Sign`/`FormFillIncrementalEngine.
/// SetFormFieldsIncremental` (append mode, sempre) faz o iText propagar um 2º nível de hibridez pra
/// revisão nova — e o PDFium/Docnet usado por este app (`mPdf.Rendering`) não resolve corretamente uma
/// cadeia hibridizada em mais de um nível: o carimbo/widget novo fica INVISÍVEL. Fixture sintética em
/// `Fixtures.Hibrido()` (gerador temporário deletado, ver git log) reproduz exatamente essa forma;
/// `Fixtures.FullCompression()` (em memória, sem gerador — mesmo padrão de `Fixtures.
/// PasswordProtected`) cobre o lado NÃO-híbrido que expôs o C1 do review.
public class HybridXrefRegressionTests
{
    private static readonly ISigningEngine Engine = SigningEngineFactory.Create();

    /// 2ª review (rider 1, "o barato que vale a pena"): guarda de caminho de parse ESTRITO — a classe
    /// INTEIRA de bug que "Variant B" expôs (ver task-1-report.md, addendum) é o iText caindo,
    /// SILENCIOSAMENTE, no fallback `RebuildXref` (scan linear "N G obj", que às vezes recupera as
    /// páginas mas PERDE a assinatura, às vezes nem isso) sempre que a cadeia de `/Prev`/`/XRefStm`
    /// está malformada. `ReadSignatures`/`IntegrityValid` sozinhos NÃO pegam isso — `RebuildXref` pode
    /// muito bem produzir um documento que passa em AMBOS (0 assinaturas não é "assinatura inválida",
    /// é "nenhuma assinatura encontrada" — um `Assert.Single` já pegaria ESSE caso por acidente, mas a
    /// variante "página recuperada, campo achado, mas via reconstrução" não). `HasRebuiltXref()`
    /// (`PdfReader`, público) é o único sinal direto de "este documento só abriu por recuperação, não
    /// pelo caminho de parse normal" — colocado alongside CADA reread desta suíte que precede um
    /// `Engine.ReadSignatures`, pra nunca mais depender de um teste específico "acertar" o sintoma
    /// certo (crash vs assinatura perdida vs render 0%) pra pegar esta classe de bug.
    private static void AssertStrictParse(byte[] pdf)
    {
        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        Assert.False(reader.HasRebuiltXref(),
            "PDF caiu no fallback RebuildXref (scan linear) em vez do caminho de parse normal -- " +
            "sinal de xref/trailer malformado mascarado por recuperação silenciosa (mesma classe de " +
            "bug que passou despercebida sob \"Variant B\", ver task-1-report.md).");
    }

    // --- sanidade da fixture (não é o RED em si, mas prova que a fixture é o que alega ser) --------

    [Fact]
    public void Fixture_Hibrido_OpensAndReportsHybridXref()
    {
        using var reader = new PdfReader(new MemoryStream(Fixtures.Hibrido()));
        using var doc = new PdfDocument(reader);
        Assert.Equal(1, doc.GetNumberOfPages());
        Assert.True(reader.HasHybridXref(), "fixture-hibrido.pdf deveria reportar hybridXref==true");
    }

    // --- O RED: carimbo visível some no render PDFium (mPdf.Rendering) sobre doc híbrido -----------

    [Fact] // GUARDA DE REGRESSÃO CENTRAL desta task: nasceu RED contra o bug real (append sobre doc
    // híbrido propaga um 2º nível de hibridez, que o PDFium/Docnet deste app não resolve) — ver
    // task-1-report.md pra dissecação antes/depois do trailer. Mesma região/padrão de
    // Sign_WithVisibleStamp_PaintsOnlyInsideStampRegion (PadesSigningEngineTests) — reaproveitado
    // aqui contra a fixture híbrida em vez da A4 normal.
    public void Sign_OnHybridDocument_VisibleStampRendersInPdfium()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var stamp = new VisibleStampSpec(0, new PdfQuad(350, 50, 550, 110));
        var signed = Engine.Sign(new SignRequest(
            Fixtures.Hibrido(), cert, "Teste híbrido", null, stamp, null));

        // assinatura precisa continuar íntegra independente do bug de render (mecânica cripto não muda)
        AssertStrictParse(signed);
        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.True(info.IntegrityValid);

        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(0, 1.0);
        int h = page.HeightPx;
        int stampLeft = 350, stampRight = 550, stampBottom = 50, stampTop = 110;
        int pxTop = h - stampTop, pxBottom = h - stampBottom;

        int nonWhite = 0;
        for (int y = pxTop; y < pxBottom; y++)
            for (int x = stampLeft; x < stampRight; x++)
            {
                int i = (y * page.WidthPx + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) nonWhite++;
            }

        // PRÉ-FIX: nonWhite fica em 0 (o PDFium usado por este app não resolve corretamente uma cadeia
        // com hibridez em mais de 1 nível, ver HybridXrefSafePdfReader.cs) — POR ISSO o teste nasce RED.
        // PÓS-FIX: o carimbo (moldura+selo+texto) pinta um número de pixels bem acima deste limiar
        // (mesma ordem de grandeza medida em Sign_WithVisibleStamp_PaintsOnlyInsideStampRegion).
        Assert.True(nonWhite > 100,
            $"carimbo não renderizou sobre doc híbrido: só {nonWhite} pixels não-brancos na região " +
            "(bug de hibridez de 2 níveis — ver task-1-report.md)");
    }

    // --- append-only: o fix NÃO pode alterar 1 byte sequer das revisões anteriores -----------------

    [Fact] // prova de PREFIXO byte-idêntico — mesma garantia que todo append deste app sempre teve
    // (mecânica do iText: `PdfDocument.Open`, modo append, copia os bytes CRUS do reader pro writer
    // ANTES de escrever a xref/trailer nova, ver HybridXrefSafePdfReader.cs) — aqui verificada
    // EXPLICITAMENTE contra a fixture híbrida, porque é exatamente o cenário que o fix desta task toca.
    public void Sign_OnHybridDocument_PreservesInputBytesAsPrefix()
    {
        var original = Fixtures.Hibrido();
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = Engine.Sign(new SignRequest(original, cert, null, null, null, null));

        Assert.True(signed.Length > original.Length);
        Assert.Equal(original, signed[..original.Length]);
    }

    // --- N=2 incremental sobre híbrido preserva a 1ª assinatura -------------------------------------

    [Fact] // mesmo exemplar de Sign_SecondIncrementalSignature_KeepsFirstSignatureValid
    // (PadesSigningEngineTests) — repetido aqui contra doc híbrido: o fix (HybridXrefSafePdfReader) é
    // aplicado nas 2 chamadas (a 1ª lê a fixture híbrida; a 2ª lê o RESULTADO da 1ª, que já não é mais
    // híbrido — reader.HasXrefStm() volta a ser false por conta própria, sem esforço extra).
    public void Sign_SecondIncrementalSignatureOnHybridDocument_KeepsFirstSignatureValid()
    {
        using var cert1 = TestCertificateFactory.CreateSelfSigned("Signatario Um");
        using var cert2 = TestCertificateFactory.CreateSelfSigned("Signatario Dois");

        var once = Engine.Sign(new SignRequest(Fixtures.Hibrido(), cert1, null, null, null, null));
        var twice = Engine.Sign(new SignRequest(once, cert2, null, null, null, null));

        AssertStrictParse(twice);
        var infos = Engine.ReadSignatures(twice);
        Assert.Equal(2, infos.Count);
        Assert.All(infos, i => Assert.True(i.IntegrityValid, $"{i.FieldName} deveria continuar íntegra"));
        Assert.False(infos[0].CoversWholeDocument);
        Assert.True(infos[1].CoversWholeDocument);
    }

    // --- SetFormFieldsIncremental sobre híbrido JÁ ASSINADO: valor renderiza + assinatura preservada -

    [Fact] // mesmo exemplar de FormFillIncrementalEngineTests.SetFormFieldsIncremental_CheckboxOnSignedDoc_
    // ValueAppearsInRender, contra a fixture híbrida (campo de texto "campo1", widget em
    // (50,600)-(200,620)pt — ver Fixtures.Hibrido()/gerador deletado). Prova as 2 garantias centrais do
    // brief juntas: o valor preenchido RENDE (PDFium independente) e a assinatura PRÉ-EXISTENTE
    // continua íntegra depois do preenchimento incremental — mesmo fix (HybridXrefSafePdfReader) nos 2
    // motores, mesma fixture híbrida.
    public void SetFormFieldsIncremental_OnSignedHybridDocument_ValueRendersAndSignaturePreserved()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = Engine.Sign(new SignRequest(Fixtures.Hibrido(), cert, null, null, null, null));
        AssertStrictParse(signed);
        var beforeInfo = Assert.Single(Engine.ReadSignatures(signed));
        Assert.True(beforeInfo.IntegrityValid);

        var filled = Engine.SetFormFieldsIncremental(signed,
            new Dictionary<string, string> { ["campo1"] = "Preenchido" });

        AssertStrictParse(filled);
        var afterInfo = Assert.Single(Engine.ReadSignatures(filled));
        Assert.True(afterInfo.IntegrityValid,
            "PROVA CENTRAL: assinatura deveria continuar íntegra depois do preenchimento incremental sobre doc híbrido");

        using var renderer = new PdfDocumentRenderer(filled);
        var page = renderer.RenderPage(0, 1.0);
        int h = page.HeightPx;
        // campo1: retângulo (50,600)-(200,620)pt (ver gerador deletado) -- Y invertido, origem PDF é
        // inferior-esquerda.
        int pxTop = h - 620, pxBottom = h - 600;
        int nonWhite = 0;
        for (int y = pxTop; y < pxBottom; y++)
            for (int x = 50; x < 200; x++)
            {
                int i = (y * page.WidthPx + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) nonWhite++;
            }
        Assert.True(nonWhite > 5,
            $"valor preenchido não renderizou sobre doc híbrido assinado: só {nonWhite} pixels não-brancos");
    }

    // --- C1 (review): doc xref-STREAM PURO, NUNCA híbrido -- a lacuna de corpus que escondeu a 1ª
    // versão quebrada do fix (zerava `xrefStm` incondicionalmente, corrompendo exatamente este caso).
    // `Fixtures.FullCompression()` nunca passa por `hybridXref==true` em `HybridXrefSafePdfReader` —
    // as 3 mutações do fix são NO-OP aqui por construção (ver XML doc de HybridXrefSafePdfReader.cs) —
    // então este bloco é a prova de que o fix atual realmente respeita essa condição, não só em teoria.

    [Fact]
    public void Fixture_FullCompression_OpensAndReportsNonHybridXrefStream()
    {
        using var reader = new PdfReader(new MemoryStream(Fixtures.FullCompression()));
        using var doc = new PdfDocument(reader);
        Assert.Equal(1, doc.GetNumberOfPages());
        Assert.True(reader.HasXrefStm(), "fixture full-compression deveria ser xref-stream");
        Assert.False(reader.HasHybridXref(), "fixture full-compression NÃO deveria ser híbrida");
    }

    [Fact] // GUARDA DE REGRESSÃO C1: sign -> reread pelo PRÓPRIO iText (ReadSignatures) não pode
    // lançar, IntegrityValid precisa continuar true, e o carimbo precisa renderizar -- exatamente as 3
    // consequências que a 1ª versão do fix quebrava silenciosamente (PdfException "/Pages must be
    // PdfDictionary" no reread, sem RED nenhum acusar antes desta task existir).
    public void Sign_OnFullCompressionNonHybridDocument_RereadsAndRendersCorrectly()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var stamp = new VisibleStampSpec(0, new PdfQuad(350, 50, 550, 110));
        var signed = Engine.Sign(new SignRequest(
            Fixtures.FullCompression(), cert, "Teste full-compression", null, stamp, null));

        // reread pelo PRÓPRIO iText não pode lançar (era exatamente isto que quebrava sob C1)
        AssertStrictParse(signed);
        var info = Assert.Single(Engine.ReadSignatures(signed));
        Assert.True(info.IntegrityValid);

        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(0, 1.0);
        int h = page.HeightPx;
        int pxTop = h - 110, pxBottom = h - 50;
        int nonWhite = 0;
        for (int y = pxTop; y < pxBottom; y++)
            for (int x = 350; x < 550; x++)
            {
                int i = (y * page.WidthPx + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) nonWhite++;
            }
        Assert.True(nonWhite > 100,
            $"carimbo não renderizou sobre doc full-compression não-híbrido: só {nonWhite} px");
    }

    [Fact] // mesmo par de garantias de SetFormFieldsIncremental_OnSignedHybridDocument_..., agora pro
    // lado NÃO-híbrido (campo "campoFC", ver Fixtures.FullCompression()).
    public void SetFormFieldsIncremental_OnSignedFullCompressionDocument_ValueRendersAndSignaturePreserved()
    {
        using var cert = TestCertificateFactory.CreateSelfSigned();
        var signed = Engine.Sign(new SignRequest(Fixtures.FullCompression(), cert, null, null, null, null));
        AssertStrictParse(signed);
        Assert.True(Assert.Single(Engine.ReadSignatures(signed)).IntegrityValid);

        var filled = Engine.SetFormFieldsIncremental(signed,
            new Dictionary<string, string> { ["campoFC"] = "Preenchido" });

        AssertStrictParse(filled);
        var afterInfo = Assert.Single(Engine.ReadSignatures(filled));
        Assert.True(afterInfo.IntegrityValid,
            "assinatura deveria continuar íntegra depois do preenchimento incremental sobre doc full-compression");

        using var renderer = new PdfDocumentRenderer(filled);
        var page = renderer.RenderPage(0, 1.0);
        int h = page.HeightPx;
        int pxTop = h - 620, pxBottom = h - 600;
        int nonWhite = 0;
        for (int y = pxTop; y < pxBottom; y++)
            for (int x = 50; x < 200; x++)
            {
                int i = (y * page.WidthPx + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) nonWhite++;
            }
        Assert.True(nonWhite > 5,
            $"valor preenchido não renderizou sobre doc full-compression assinado: só {nonWhite} px");
    }

    // --- cinto de campo: contrato real do usuário, SE presente localmente ---------------------------

    [SkippableFact] // Task 1 (Plano 10, review I3): NUNCA um caminho literal no repositório —
    // `tools/export-public.ps1` recusa a exportação pública se achar tokens proibidos (nome do
    // usuário, nome do cliente), mesmo dentro de um comentário ou de uma string de teste. O caminho
    // vem de uma variável de ambiente local (MPDF_TESTE_CAMPO) que só existe na máquina do
    // controlador -- ausente em qualquer outro ambiente (CI, export público), o teste PULA de verdade
    // (Skip.If, xunit.skippablefact — nunca um early-return silencioso que o runner conta como
    // "passou"). Re-assinar o arquivo REAL não é possível (já está assinado pelo usuário) -- em vez
    // disso, extrai os bytes PRÉ-assinatura (prefixo até o ByteRange da assinatura existente) e assina
    // ESSES bytes, EFÊMERO, pelo motor JÁ CORRIGIDO, provando que o mesmo documento que hoje soma 0%
    // no carimbo (histórico, assinado ANTES do fix) passaria a renderizar corretamente se assinado
    // HOJE. NUNCA escreve o conteúdo do arquivo real no repositório/saída de teste -- só bytes
    // efêmeros em memória.
    public void Sign_RealHybridContractPreSignatureBytes_StampNowRenders()
    {
        string? path = Environment.GetEnvironmentVariable("MPDF_TESTE_CAMPO");
        Skip.If(string.IsNullOrEmpty(path) || !File.Exists(path),
            "MPDF_TESTE_CAMPO não setada ou arquivo ausente -- cinto de campo pulado neste ambiente.");

        var realBytes = File.ReadAllBytes(path!);
        // ByteRange da 1ª assinatura do arquivo real termina em 276781 (achado ao vivo do controlador,
        // ver task-1-brief.md) -- os bytes ANTES desse ponto são o documento híbrido tal como estava
        // no instante em que foi assinado pela 1ª vez, byte-idênticos ao que o motor recebeu então.
        const int preSignatureLength = 276781;
        Skip.IfNot(realBytes.Length > preSignatureLength,
            "arquivo local menor que o esperado -- não é o contrato conhecido, pulando.");
        var preSignatureBytes = realBytes[..preSignatureLength];

        using var reader = new PdfReader(new MemoryStream(preSignatureBytes));
        int pageCount;
        using (var doc = new PdfDocument(reader))
        {
            Assert.True(reader.HasHybridXref(), "pré-condição: bytes extraídos deveriam ser híbridos");
            pageCount = doc.GetNumberOfPages();
        }
        // Minor 5 (review): precondições explícitas ANTES de assumir página índice 4 e a região de
        // checagem em pixels -- nunca um IndexOutOfRange/recorte silencioso se o arquivo local mudar
        // de forma (renegociação de contrato, nova versão assinada com menos páginas etc.).
        Skip.IfNot(pageCount >= 5, $"contrato local tem só {pageCount} página(s) -- esperava >= 5.");
        using var sizeRenderer = new PdfDocumentRenderer(preSignatureBytes);
        var pageSize = sizeRenderer.GetPageSize(4);
        Skip.IfNot(pageSize.WidthPt * 2 >= 1068,
            $"página índice 4 estreita demais ({pageSize.WidthPt}pt) pra região de checagem [607,1068]px.");

        using var cert = TestCertificateFactory.CreateSelfSigned();
        // região de checagem do brief: px [607,1068]x[462,527] em escala 2 na página índice 4 -- converte
        // pra retângulo em PONTOS (origem PDF inferior-esquerda) usando a altura REAL da página (nunca
        // A4 chutado -- o contrato real pode não ser A4), lida via mPdf.Rendering antes de assinar.
        double leftPt = 607.0 / 2, rightPt = 1068.0 / 2;
        double topPt = pageSize.HeightPt - 462.0 / 2, bottomPt = pageSize.HeightPt - 527.0 / 2;
        var stamp = new VisibleStampSpec(4, new PdfQuad(leftPt, bottomPt, rightPt, topPt));
        var signed = Engine.Sign(new SignRequest(preSignatureBytes, cert, "Teste contrato real", null, stamp, null));
        AssertStrictParse(signed);

        using var renderer = new PdfDocumentRenderer(signed);
        var page = renderer.RenderPage(4, 2.0);
        int nonWhite = 0;
        for (int y = 462; y < 527; y++)
            for (int x = 607; x < 1068; x++)
            {
                int i = (y * page.WidthPx + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) nonWhite++;
            }
        Assert.True(nonWhite > 1000,
            $"carimbo não renderizou sobre os bytes pré-assinatura do contrato real: só {nonWhite} px");
    }
}
