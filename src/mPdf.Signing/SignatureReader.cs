using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Commons.Bouncycastle.Cert;
using iText.Commons.Exceptions;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using iText.Signatures;
using mPdf.Editing;

namespace mPdf.Signing;

/// Leitura pura de assinaturas — ADAPTADO de poc/mPdf.Poc.Signer/Signing/SignatureVerifier.cs (a
/// verificação de integridade/autenticidade em si, `PdfPKCS7.VerifySignatureIntegrityAndAuthenticity`,
/// NÃO foi alterada). Campos novos vs. o PoC (`Document`, `SubFilter`, `ChainTrustedWindows`, `Reason`,
/// `Certification`) são leitura adicional do MESMO `PdfPKCS7`/dicionário de assinatura já aberto —
/// nenhum deles muda o que já era verificado. Este é o caminho que aceita PDFs de TERCEIROS (nunca
/// confiáveis por construção — o app é um LEITOR de assinaturas de outras pessoas) — toda leitura
/// adicional abaixo (data, cadeia) é defensiva por design, nunca deixa uma exceção não tipada do
/// iText/BCL escapar pro chamador por causa de um certificado/data embutido malformado.
internal static class SignatureReader
{
    public static IReadOnlyList<SignatureInfo> Read(byte[] pdf)
    {
        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            var util = new SignatureUtil(doc);
            // Task 4 (Plano 4): mesma instância de PdfAcroForm reusada pra TODAS as assinaturas deste
            // documento — GetAcroForm(doc, false) nunca cria o dicionário /AcroForm à toa (mesmo
            // parâmetro `false` de PdfEditor.ReadFormFields), `null` só quando o documento não tem
            // AcroForm nenhum (nunca deveria acontecer aqui — um documento COM assinatura sempre tem
            // /AcroForm com os campos de assinatura, mas `GetField` abaixo já trata `null` sem lançar).
            var acroForm = PdfAcroForm.GetAcroForm(doc, false);
            var result = new List<SignatureInfo>();
            foreach (var name in util.GetSignatureNames())
            {
                var pkcs7 = util.ReadSignatureData(name);
                var signingCert = pkcs7.GetSigningCertificate();
                // Confirmado via reflexão sobre itext.sign.dll 9.7.0 (mesmo achado que o PoC já fez):
                // CertificateInfo.GetSubjectFields(IX509Certificate) -> X500Name.GetField(string) -> string?
                var cn = CertificateInfo.GetSubjectFields(signingCert).GetField("CN") ?? "";
                var (signerName, document) = SplitNameAndDocument(cn);

                bool integrity;
                try { integrity = pkcs7.VerifySignatureIntegrityAndAuthenticity(); }
                catch { integrity = false; } // criptografia ilegível conta como quebrada, nunca como válida

                var sigDict = util.GetSignatureDictionary(name);
                // I6 (guarda de regressão PAdES): /SubFilter é o identificador do perfil de assinatura
                // — ETSI.CAdES.detached é o que a ICP-Brasil exige e o que PadesSigningEngine sempre
                // grava (comportamento DEFAULT de SignWithBaselineBProfile, nunca alterado por esta
                // task). Exposto aqui pra Sign_UsesEtsiCadesDetachedSubFilter travar esse valor
                // permanentemente — sem isso, um futuro refactor pra SignDetached (adbe.pkcs7.detached)
                // passaria por TODOS os outros testes sem ninguém notar a regressão.
                var subFilter = sigDict.GetAsName(PdfName.SubFilter)?.GetValue() ?? "";
                var reason = pkcs7.GetReason();
                var (stampPageIndex, stampRect) = ReadStampGeometry(acroForm?.GetField(name), doc);

                result.Add(new SignatureInfo(
                    FieldName: name,
                    SignerName: signerName,
                    Document: document,
                    SubFilter: subFilter,
                    SignedAt: TryToDateTimeOffset(pkcs7.GetSignDate()),
                    CoversWholeDocument: util.SignatureCoversWholeDocument(name),
                    IntegrityValid: integrity,
                    ChainTrustedWindows: IsChainTrustedByWindows(signingCert, pkcs7.GetSignCertificateChain()),
                    Reason: string.IsNullOrEmpty(reason) ? null : reason,
                    Certification: ReadCertificationLevel(sigDict),
                    StampPageIndex: stampPageIndex,
                    StampRect: stampRect));
            }
            return result;
        }
        catch (BadPasswordException ex)
        {
            throw new PdfPasswordRequiredException(
                "PDF protegido por senha — não é possível ler as assinaturas sem a senha correta.", ex);
        }
        catch (ITextException ex)
        {
            throw new PdfSigningException("Não foi possível ler as assinaturas do PDF.", ex);
        }
    }

    /// HIPÓTESE (checklist do brief) + reconciliação completa (Leiaute RFB v4.1, OIDs SAN considerados
    /// e descartados, etc.) — MOVIDA pra `CnDocumentConvention.cs` (Plano 9, Task 3, revisão): a mesma
    /// convenção CN "NOME:CPF|CNPJ" passou a ser precisada também do lado da ESCRITA
    /// (`PadesSigningEngine`/`StampAppearanceRenderer`, o carimbo visível novo), então a regex/split
    /// consolidaram num helper compartilhado do mesmo assembly (`mPdf.Signing`) — ver XML doc completo
    /// lá, não duplicado aqui.
    private static (string SignerName, string? Document) SplitNameAndDocument(string cn) =>
        CnDocumentConvention.SplitNameAndDocument(cn);

    /// Task 4 (Plano 4): página/retângulo do PRIMEIRO widget do campo de assinatura (`GetWidgets()[0]`)
    /// — MESMO padrão EXATO de `PdfEditor.BuildFormFieldData` (mPdf.Editing/PdfEditor.cs), reusado aqui
    /// em vez de reinventado: `ReadFormFields` já prova que essa mecânica funciona pra campo de
    /// assinatura (tipo `FormFieldType.Other`, incluso em `GetAllFormFields()` sem tratamento especial).
    /// `field` nulo (campo não encontrado no AcroForm — não deveria acontecer pra um nome vindo de
    /// `util.GetSignatureNames()`, mas defensivo do mesmo jeito que o resto deste leitor de PDF de
    /// TERCEIRO) ou sem widget nenhum -> `(null, null)`. Retângulo DEGENERADO (largura ou altura
    /// não-positiva — mesmo critério de `PadesSigningEngine.ValidateStamp` do lado da ESCRITA) também
    /// vira `(null, null)`: é o formato que uma assinatura SEM carimbo visível (`stamp: null` em
    /// `SignRequest`) produz na prática (widget presente, mas sem área visível) — tratado como "sem
    /// carimbo pra navegar", nunca como um retângulo real de 0 área.
    private static (int? PageIndex, PdfQuad? Rect) ReadStampGeometry(PdfFormField? field, PdfDocument doc)
    {
        var widgets = field?.GetWidgets();
        if (widgets is not { Count: > 0 }) return (null, null);
        var widget = widgets[0];
        var rect = widget.GetRectangle()?.ToRectangle();
        if (rect is null || rect.GetWidth() <= 0 || rect.GetHeight() <= 0) return (null, null);

        int? pageIndex = null;
        var page = widget.GetPage();
        if (page is not null)
        {
            int pageNumber = doc.GetPageNumber(page);
            if (pageNumber > 0) pageIndex = pageNumber - 1;
        }
        // Sem página resolvida (residual, nunca observado na prática — mesmo espírito defensivo de
        // PdfEditor.BuildFormFieldData) -> sem página pra rolar, trata como "sem carimbo" por inteiro.
        if (pageIndex is null) return (null, null);

        return (pageIndex, new PdfQuad(rect.GetLeft(), rect.GetBottom(), rect.GetRight(), rect.GetTop()));
    }

    /// P=2 (`FormsAndSignatures`) é o único nível que este motor grava (`PadesSigningEngine.Sign`) —
    /// presença de `/Reference` com `/TransformParams/P == 2` é suficiente pra reconhecer; qualquer
    /// outro valor (ou ausência) é `None`. Assinatura de APROVAÇÃO (sem DocMDP próprio, o único tipo
    /// permitido depois da 1ª — ver `PadesSigningEngine.Sign`) nunca tem `/Reference` -> `None`.
    private static DocMdpLevel ReadCertificationLevel(PdfDictionary sigDict)
    {
        var refArray = sigDict.GetAsArray(PdfName.Reference);
        if (refArray is null) return DocMdpLevel.None;
        for (int i = 0; i < refArray.Size(); i++)
        {
            var transformParams = refArray.GetAsDictionary(i)?.GetAsDictionary(PdfName.TransformParams);
            var p = transformParams?.GetAsNumber(PdfName.P);
            if (p is not null && p.IntValue() == 2) return DocMdpLevel.FormsAndSignatures;
        }
        return DocMdpLevel.None;
    }

    /// Revisão I3 (leitura de PDF de terceiro, não confiável por construção): `new
    /// DateTimeOffset(DateTime)` pode lançar `ArgumentOutOfRangeException` pra um `DateTime` perto de
    /// `DateTime.MinValue`/`MaxValue` combinado com o fuso horário LOCAL da máquina (ex.: fuso negativo
    /// empurra um `/M` próximo de `MinValue` pra antes do mínimo representável por `DateTimeOffset`) —
    /// teoricamente alcançável por um `/M` malformado/extremo num PDF de terceiro, mesmo que
    /// `PadesSigningEngine` (o lado que ESCREVE) nunca produza um valor assim. `signDate ==
    /// DateTime.MinValue` cobre o caso comum (ausência de `/M`, valor default do iText); esta checagem
    /// extra cobre valores PRÓXIMOS do extremo que passariam pela igualdade estrita. Não é barato
    /// construir um `/M` malformado através da API pública deste módulo pra testar (o valor vem do
    /// relógio no momento da assinatura — `SignRequest` não deixa o chamador controlar isso) — código
    /// defensivo documentado aqui, sem teste dedicado (ver task-1-report.md, seção "## Fix").
    private static DateTimeOffset? TryToDateTimeOffset(DateTime signDate)
    {
        if (signDate == DateTime.MinValue) return null;
        try { return new DateTimeOffset(signDate); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// Cadeia validada via `X509Chain` do Windows (repositório de raízes confiáveis da MÁQUINA) —
    /// sinal LOCAL auxiliar, nunca a validação oficial (ver XML doc de `SignatureInfo.ChainTrustedWindows`
    /// em Contract.cs). `RevocationMode = NoCheck`: evita depender de rede (CRL/OCSP) só para responder
    /// "a raiz é confiável nesta máquina" — checagem de revogação fica fora do escopo deste sinal.
    ///
    /// Revisão I5: `embeddedChain` (`PdfPKCS7.GetSignCertificateChain()` — os certificados
    /// INTERMEDIÁRIOS que a própria assinatura já embute) entra como `ChainPolicy.ExtraStore`. Sem
    /// isso, `X509Chain.Build` só enxergava o certificado FOLHA; num PDF de TERCEIRO cuja AC
    /// intermediária nunca foi instalada nesta máquina (o caso comum — este app é um LEITOR de PDFs
    /// assinados por OUTRAS pessoas, nunca instalamos o certificado de quem assinou), o elo
    /// folha->intermediária ficava `PartialChain` (a checagem nem chega a avaliar se a raiz é
    /// confiável — falso alarme "cadeia quebrada" quando na verdade só falta olhar o material que o
    /// PRÓPRIO PDF já carrega). Com `ExtraStore`, o elo resolve usando esse material embutido, e a
    /// única pendência que sobra é a raiz (AC-Raiz ICP-Brasil) não estar instalada como confiável
    /// (`UntrustedRoot`) — dependência que segue fora do escopo deste sinal por design (instalar/
    /// confiar raízes é decisão do gerente de TI, não deste código). Prova do MECANISMO em si, sem
    /// instalar nada em lugar nenhum: `SignatureReaderTests.
    /// X509Chain_WithExtraStore_ResolvesIntermediateInsteadOfPartialChain` (constrói os 2 certificados
    /// efêmeros — folha + AC — e mostra que `ChainStatus` deixa de conter `PartialChain` assim que a AC
    /// entra em `ExtraStore`, ainda terminando em `UntrustedRoot` porque a raiz não está instalada — o
    /// valor público `ChainTrustedWindows` continua `false` nos dois casos, e é honesto que continue).
    ///
    /// Revisão I3: `X509CertificateLoader.LoadCertificate`/`X509Chain.Build` podem lançar
    /// `CryptographicException` pra bytes de certificado malformados — um PDF de terceiro NÃO confiável
    /// pode ter um certificado (folha OU intermediário) embutido corrompido. Capturado e tratado como
    /// "não confiável" honesto (`false`), nunca como exceção não tipada escapando pro chamador; um item
    /// malformado especificamente dentro de `embeddedChain` é ignorado individualmente (tenta os
    /// demais) em vez de derrubar a checagem inteira. Certificados efêmeros self-signed de teste (nunca
    /// instalados no repositório Windows) continuam devolvendo `false` pelo caminho normal — valor
    /// honesto esperado, não um bug (ver testes deste módulo).
    private static bool IsChainTrustedByWindows(IX509Certificate signingCert, IX509Certificate[]? embeddedChain)
    {
        // Revisão 2, item 2: `embeddedChain` (PdfPKCS7.GetSignCertificateChain()) chegou aqui SEM
        // checagem de nulidade — um `foreach` sobre `null` é NullReferenceException NÃO tipada, que
        // escapa DOS DOIS catches abaixo (nenhum dos dois é NRE) — reintroduzia exatamente a classe de
        // buraco que a revisão I3 fechou (PDF de terceiro não confiável derrubando a leitura com uma
        // exceção crua). `?? []` trata "sem cadeia embutida" como "nenhum intermediário pra
        // ExtraStore", nunca uma falha.
        //
        // Revisão 2, item 3: `X509Chain.Dispose()` NÃO libera os certificados no `ExtraStore` — eles
        // são de posse de quem os criou (nós, via `X509CertificateLoader.LoadCertificate`), não da
        // cadeia. Sem dispose explícito, cada leitura de um PDF com cadeia embutida vazava os handles
        // nativos dos certificados carregados aqui. `extraStoreCerts` rastreia o que foi carregado
        // pra descartar no `finally`, único ponto de saída (sucesso, `CryptographicException`, ou
        // qualquer outra exceção que escape) — nunca pula o descarte.
        var extraStoreCerts = new List<X509Certificate2>();
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(signingCert.GetEncoded());
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            foreach (var embedded in embeddedChain ?? [])
            {
                try
                {
                    var loaded = X509CertificateLoader.LoadCertificate(embedded.GetEncoded());
                    extraStoreCerts.Add(loaded);
                    chain.ChainPolicy.ExtraStore.Add(loaded);
                }
                catch (CryptographicException)
                {
                    // item malformado da cadeia embutida: ignora esse elemento, tenta resolver com os demais
                }
            }
            bool built = chain.Build(cert);
            return built && chain.ChainStatus.Length == 0;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (var loaded in extraStoreCerts) loaded.Dispose();
        }
    }
}
