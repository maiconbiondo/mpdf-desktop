using System.Security.Cryptography.X509Certificates;
using iText.Bouncycastleconnector;
using iText.Commons.Bouncycastle.Cert;
using iText.Commons.Exceptions;
using iText.Forms;
using iText.Forms.Form.Element;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Exceptions;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Layout.Renderer;
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

            // Task 1 (Plano 10): ver HybridXrefSafePdfReader.cs pro diagnóstico completo — sem isto, um
            // documento HÍBRIDO de entrada faz o iText propagar um 2º nível de hibridez pra revisão
            // nova (preservando o /XRefStm da entrada, atualizado ou não), e o carimbo fica invisível
            // pro PDFium (mPdf.Rendering), mesmo a assinatura continuando íntegra.
            var reader = new HybridXrefSafePdfReader(input);
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
                ApplyVisibleStamp(props, stamp, request.Certificate, request.Reason, request.Location);

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

    /// Plano 9 (Task 3): camada de APARÊNCIA do carimbo visível — moldura fina, selo translúcido
    /// (derivado da geometria de `Assets/logo.svg`, desenhado por primitivos vetoriais, nunca um PNG
    /// embutido) e texto pt-BR ("Assinado digitalmente por" — pedido do usuário, o Marco 0 usava o
    /// default em inglês do iText, `SignedAppearanceText`) com mais campos (CPF/CNPJ, motivo, local,
    /// emissor). NADA aqui toca `PdfPadesSigner`/`SignWithBaselineBProfile`/DocMDP/nome de campo — só o
    /// `SignatureFieldAppearance`/`Div` que descreve COMO o widget é desenhado; a mecânica criptográfica
    /// é 100% a mesma de antes desta task.
    ///
    /// DECISÃO (achado ao vivo, reconciliado no relatório desta task): o conteúdo NÃO é montado com
    /// `Paragraph`/`SetContent(Div)` populado normalmente — um `Div` de altura FIXA (a do retângulo do
    /// carimbo) descarta em SILÊNCIO qualquer filho que não caiba inteiro (nenhum overflow visível, nem
    /// erro), inclusive os PRIMEIROS filhos adicionados quando o texto de um nome comprido QUEBRA linha
    /// numa caixa estreita — provado ao vivo: nome+data (a garantia "sempre" da regra de prioridade do
    /// brief) sumiam JUNTOS assim que "nome" ocupava 2 linhas em vez de 1. Fix: `Div` vazio (só
    /// `Width`/`Height`, sem `Paragraph` nenhum), com `SetNextRenderer` apontando pra
    /// `StampAppearanceRenderer` — um `DivRenderer` customizado cujo `Draw(DrawContext)` desenha TUDO
    /// (moldura, selo, texto) direto no `PdfCanvas` do widget (`drawContext.GetCanvas()` — o canvas
    /// REAL do documento sendo assinado, não um documento à parte: `PdfFormXObject`s construídos fora
    /// deste ponto de extensão exigiriam um `PdfDocument` que `ApplyVisibleStamp` não tem acesso, ver
    /// `PdfCanvas(PdfFormXObject, PdfDocument)` — daí desenhar DENTRO do hook de render em vez de tentar
    /// pré-construir um XObject). Cada linha é posicionada manualmente (`BeginText`/`ShowText`), com
    /// truncamento próprio por largura (`TruncateToWidth`) — nome+data NUNCA são omitidos, na pior das
    /// hipóteses ficam truncados com "..." (nunca um comportamento pior que uma linha cortada).
    private static void ApplyVisibleStamp(
        SignerProperties props, VisibleStampSpec stamp, X509Certificate2 certificate, string? reason,
        string? location)
    {
        var r = stamp.Rect;
        float widthPt = (float)(r.RightPt - r.LeftPt), heightPt = (float)(r.TopPt - r.BottomPt);

        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA, PdfEncodings.WINANSI);
        var fontBold = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD, PdfEncodings.WINANSI);

        var div = new Div().SetWidth(widthPt).SetHeight(heightPt);
        div.SetNextRenderer(new StampAppearanceRenderer(div, certificate, reason, location, font, fontBold));

        var appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID).SetContent(div);
        // Revisão final do coordenador — achado real, verificado ao vivo com um render/scan pixel-a-
        // pixel: `SignatureFieldAppearance` aplica um PADDING PRÓPRIO default (~2pt de cada lado, ver
        // `SignatureFieldAppearance.DEFAULT_PADDING`) ANTES de repassar espaço pro `Div`/renderer
        // customizado — o retângulo REALMENTE desenhado (moldura+selo+texto, `GetOccupiedAreaBBox()`
        // dentro de `StampAppearanceRenderer`) fica ~4pt mais estreito e ~4pt mais baixo que o
        // retângulo NOMINAL pedido (`stamp.Rect`). Na caixa MÍNIMA (60×20pt) isso reduzia o orçamento
        // vertical disponível o bastante pra a linha de DATA (uma das 2 linhas SEMPRE desenhadas)
        // colidir com o próprio traço da moldura inferior. Zera os 4 paddings da APARÊNCIA (nunca do
        // `Div` — este continua sem padding próprio nenhum, só os insets manuais de
        // `StampAppearanceRenderer`) pra que `GetOccupiedAreaBBox()` corresponda ao retângulo NOMINAL
        // inteiro — camada de aparência pura, nenhuma mudança de mecânica criptográfica.
        appearance.SetProperty(Property.PADDING_TOP, UnitValue.CreatePointValue(0));
        appearance.SetProperty(Property.PADDING_RIGHT, UnitValue.CreatePointValue(0));
        appearance.SetProperty(Property.PADDING_BOTTOM, UnitValue.CreatePointValue(0));
        appearance.SetProperty(Property.PADDING_LEFT, UnitValue.CreatePointValue(0));
        props.SetPageNumber(stamp.PageIndex + 1) // contrato 0-based -> iText 1-based
             .SetPageRect(new Rectangle((float)r.LeftPt, (float)r.BottomPt, widthPt, heightPt))
             .SetSignatureAppearance(appearance);
    }

    /// Renderer customizado do conteúdo do carimbo (ver doc XML de `ApplyVisibleStamp` acima pro
    /// porquê de desenhar direto no `PdfCanvas` em vez de popular o `Div` com `Paragraph`s). Um `Div`
    /// NOVO precisa de uma instância NOVA deste renderer a cada `GetNextRenderer` (mesmo contrato de
    /// `IRenderer`/`DivRenderer` — o iText pode reusar o modelo pra relayout).
    private sealed class StampAppearanceRenderer(
        Div modelDiv, X509Certificate2 certificate, string? reason, string? location, PdfFont font, PdfFont fontBold)
        : DivRenderer(modelDiv)
    {
        // Fina o bastante pra caber em conjunto com nome+data até no tamanho MÍNIMO da caixa
        // (60×20pt, ver DocumentViewModel.MinStampBoxWidthPt/HeightPt, Plano 8) sem dominar o desenho —
        // "moldura fina" (brief), navy (Assets/logo.svg).
        private const float BorderPt = 0.75f, PaddingPt = 1.5f;
        private static readonly DeviceRgb NavyColor = new(0x1D, 0x4E, 0x89); // Assets/logo.svg: página
        private static readonly DeviceRgb TealColor = new(0x3E, 0xC1, 0xA7); // Assets/logo.svg: rubrica

        public override IRenderer GetNextRenderer() =>
            new StampAppearanceRenderer((Div)GetModelElement(), certificate, reason, location, font, fontBold);

        public override void Draw(DrawContext drawContext)
        {
            base.Draw(drawContext);
            var bbox = GetOccupiedAreaBBox();
            var canvas = drawContext.GetCanvas();

            DrawFrame(canvas, bbox);
            DrawWatermarkSeal(canvas, bbox);
            DrawText(canvas, bbox);
        }

        private void DrawFrame(PdfCanvas canvas, Rectangle bbox)
        {
            canvas.SaveState();
            canvas.SetStrokeColor(NavyColor);
            canvas.SetLineWidth(BorderPt);
            canvas.Rectangle(bbox.GetLeft() + BorderPt / 2, bbox.GetBottom() + BorderPt / 2,
                bbox.GetWidth() - BorderPt, bbox.GetHeight() - BorderPt);
            canvas.Stroke();
            canvas.RestoreState();
        }

        /// Selo simplificado derivado da geometria de `Assets/logo.svg` (página arredondada navy +
        /// rubrica em curva teal + ponto final) — vetorial (RoundRectangle/CurveTo/Circle do
        /// `PdfCanvas`, nunca um PNG embutido), translúcido (alfa 0.10 preenchimento / 0.12 traço — faixa
        /// pedida pelo brief, "~0.08-0.12"), centralizado no carimbo.
        ///
        /// Minor #1 (revisão do coordenador — achado real, verificado ao vivo com um render/crop):
        /// escalar SÓ pela ALTURA (`bbox.GetHeight() * 0.82`) funciona pros 2 tamanhos "de catálogo"
        /// (default 180×60, mínimo 60×20 — ambos mais largos que altos), mas numa caixa ESTREITA e ALTA
        /// (largura menor que altura) o selo calculado a partir da altura ficava mais LARGO que a
        /// própria caixa — a anotação recorta pelo `/BBox` na hora de renderizar (nenhum pixel vaza pra
        /// fora da PÁGINA), mas o selo em si aparecia cortado rente à moldura em vez de escalado e
        /// centralizado. Fix: escala pelo MENOR dos dois orçamentos — altura×0.82 (como antes) OU
        /// largura×0.82 convertida pro mesmo eixo via a proporção do selo (`SealAspect`, largura/altura)
        /// — preservando sempre a proporção e o centro. Nos 2 tamanhos "de catálogo" (sempre mais
        /// largos que altos) o orçamento de altura continua vencendo — comportamento IDÊNTICO ao de
        /// antes, sem regressão (ver `Sign_WithVisibleStamp_{Default,Min}Box_ShowsTranslucentWatermark...`).
        private static void DrawWatermarkSeal(PdfCanvas canvas, Rectangle bbox)
        {
            const float SealAspect = 0.62f; // largura/altura do selo -- mesma proporção de antes
            float cx = bbox.GetLeft() + bbox.GetWidth() / 2f;
            float cy = bbox.GetBottom() + bbox.GetHeight() / 2f;

            canvas.SaveState();
            canvas.SetExtGState(new PdfExtGState().SetFillOpacity(0.10f).SetStrokeOpacity(0.12f));

            float heightBudget = bbox.GetHeight() * 0.82f;
            float widthBudgetAsHeight = (bbox.GetWidth() * 0.82f) / SealAspect;
            float pageHeight = Math.Min(heightBudget, widthBudgetAsHeight);
            float pageWidth = pageHeight * SealAspect;
            canvas.SetFillColor(NavyColor);
            canvas.RoundRectangle(cx - pageWidth / 2, cy - pageHeight / 2, pageWidth, pageHeight, pageHeight * 0.10);
            canvas.Fill();

            canvas.SetStrokeColor(TealColor);
            canvas.SetLineWidth(Math.Max(1.0f, pageHeight * 0.05f));
            canvas.SetLineCapStyle(PdfCanvasConstants.LineCapStyle.ROUND);
            float sx = cx - pageWidth * 0.32f, sy = cy - pageHeight * 0.12f;
            canvas.MoveTo(sx, sy);
            canvas.CurveTo(
                sx + pageWidth * 0.18f, sy - pageHeight * 0.22f,
                sx + pageWidth * 0.30f, sy + pageHeight * 0.30f,
                sx + pageWidth * 0.46f, sy - pageHeight * 0.02f);
            canvas.CurveTo(
                sx + pageWidth * 0.55f, sy - pageHeight * 0.14f,
                sx + pageWidth * 0.62f, sy + pageHeight * 0.06f,
                sx + pageWidth * 0.72f, sy - pageHeight * 0.04f);
            canvas.Stroke();
            canvas.SetFillColor(TealColor);
            canvas.Circle(sx + pageWidth * 0.76f, sy - pageHeight * 0.05f, pageHeight * 0.03f);
            canvas.Fill();

            canvas.RestoreState();
        }

        /// Regra de prioridade (brief, "caixas pequenas"): nome+data SEMPRE desenhados (nunca omitidos —
        /// na pior das hipóteses truncados por `TruncateToWidth`, ver doc XML de `ApplyVisibleStamp`);
        /// legenda pt-BR, CPF/CNPJ, motivo+local e emissor entram um a um, na ordem de prioridade do
        /// brief, cada um checado de forma INDEPENDENTE contra o orçamento vertical acumulado até ali
        /// (`LineHeight` fixo por linha — decisão deliberada, nunca depende do motor de layout do iText
        /// pra decidir isso; `used` só CRESCE quando um campo de fato entra — um campo que NÃO coube
        /// não consome orçamento nenhum). Revisão (correção de redação — a versão anterior deste
        /// comentário prometia "drop from the bottom" estrito, o que o código NÃO garante): como os
        /// campos mais abaixo na prioridade usam fontes IGUAIS ou MENORES (`SmallSize` &lt;
        /// `CaptionSize`/`RegularSize`), é POSSÍVEL um campo de prioridade mais baixa (ex.: motivo/local,
        /// `SmallSize`) caber mesmo que um campo de prioridade mais alta mas mais "caro" (ex.: a legenda,
        /// `CaptionSize`) tenha ficado de fora — o orçamento não incrementado pelo campo descartado sobra
        /// pro próximo. Na prática isso só se manifesta numa faixa estreita de tamanhos intermediários
        /// (nem tão apertado quanto a caixa mínima, onde só nome+data cabem — ver
        /// `Sign_WithVisibleStamp_MinBox_ShowsOnlyNameAndDate` — nem tão folgado quanto o default, onde
        /// tudo cabe); não é o comportamento de prioridade estrita que o nome da técnica sugere, mas
        /// nunca quebra a garantia central do brief (nome+data sempre presentes).
        private void DrawText(PdfCanvas canvas, Rectangle bbox)
        {
            string cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            // Rider da revisão final: `issuerCn` cru carregava o sufixo ":CPF|CNPJ" (convenção do CN,
            // CnDocumentConvention) quando emissor == titular (certificados efêmeros self-signed, o
            // caminho usado em TODOS os testes deste módulo) -- "Emitido por: NOME:12345678901" era
            // ruído, não informação nova (o CPF/CNPJ já aparece na sua PRÓPRIA linha, ver `includeDocument`
            // acima). Reusa a MESMA convenção pra extrair só o nome do emissor, igual ao titular.
            string issuerCn = CnDocumentConvention.SplitNameAndDocument(
                certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true)).Name;
            var (name, documentNumber) = CnDocumentConvention.SplitNameAndDocument(cn);
            var now = DateTimeOffset.Now;
            string dateLabel = $"{now:dd/MM/yyyy HH:mm} {FormatTimezoneOffset(now.Offset)}";

            float left = bbox.GetLeft() + BorderPt + PaddingPt;
            float right = bbox.GetRight() - BorderPt - PaddingPt;
            float top = bbox.GetTop() - BorderPt - PaddingPt;
            float availableWidth = right - left;
            float availableHeight = top - (bbox.GetBottom() + BorderPt + PaddingPt);

            const float CaptionSize = 5.5f, NameSize = 7f, RegularSize = 5.5f, SmallSize = 5f;
            static float LineHeight(float size) => size * 1.12f;

            // linhas SEMPRE presentes -- nunca entram no orçamento condicional abaixo.
            float mandatoryHeight = LineHeight(NameSize) + LineHeight(RegularSize);
            float used = mandatoryHeight;

            bool includeCaption = availableHeight >= used + LineHeight(CaptionSize);
            if (includeCaption) used += LineHeight(CaptionSize);

            bool includeDocument = documentNumber is not null && availableHeight >= used + LineHeight(RegularSize);
            if (includeDocument) used += LineHeight(RegularSize);

            int reasonLocationLines = (reason is not null ? 1 : 0) + (location is not null ? 1 : 0);
            bool includeReasonLocation = reasonLocationLines > 0
                && availableHeight >= used + reasonLocationLines * LineHeight(SmallSize);
            if (includeReasonLocation) used += reasonLocationLines * LineHeight(SmallSize);

            bool includeIssuer = availableHeight >= used + LineHeight(SmallSize);

            canvas.SaveState();
            canvas.SetFillColor(ColorConstants.BLACK);
            float y = top;

            void DrawLine(string text, PdfFont lineFont, float size)
            {
                float lineHeight = LineHeight(size);
                y -= lineHeight;
                string truncated = TruncateToWidth(text, lineFont, size, availableWidth);
                canvas.BeginText().SetFontAndSize(lineFont, size)
                    .MoveText(left, y + (lineHeight - size) * 0.15f)
                    .ShowText(truncated).EndText();
            }

            if (includeCaption) DrawLine("Assinado digitalmente por", font, CaptionSize);
            DrawLine(name, fontBold, NameSize); // sempre
            if (includeDocument)
            {
                string label = documentNumber!.Length == 11
                    ? $"CPF: {FormatCpf(documentNumber)}"
                    : $"CNPJ: {FormatCnpj(documentNumber)}";
                DrawLine(label, font, RegularSize);
            }
            DrawLine(dateLabel, font, RegularSize); // sempre
            if (includeReasonLocation)
            {
                if (reason is not null) DrawLine($"Motivo: {reason}", font, SmallSize);
                if (location is not null) DrawLine($"Local: {location}", font, SmallSize);
            }
            if (includeIssuer) DrawLine($"Emitido por: {issuerCn}", font, SmallSize);

            canvas.RestoreState();
        }

        /// Encurta `text` até caber em `maxWidth` (reticências "..." no fim) — busca binária no
        /// comprimento, medindo com `PdfFont.GetWidth` (a MESMA fonte/tamanho que desenha a linha, nunca
        /// uma estimativa). `text` inteiro já cabe -> devolve sem tocar.
        private static string TruncateToWidth(string text, PdfFont font, float size, float maxWidth)
        {
            if (font.GetWidth(text, size) <= maxWidth) return text;
            const string Ellipsis = "...";
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (font.GetWidth(text[..mid] + Ellipsis, size) <= maxWidth) lo = mid; else hi = mid - 1;
            }
            return lo <= 0 ? Ellipsis : text[..lo] + Ellipsis;
        }

        private static string FormatTimezoneOffset(TimeSpan offset)
        {
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            return $"(UTC{sign}{offset.Duration():hh\\:mm})";
        }

        /// Mesma máscara de `SignatureRowViewModel.FormatCpf`/`FormatCnpj` (mPdf.App, Plano 4) — "P4
        /// mask helpers" do brief. Duplicação CROSS-ASSEMBLY que sobrevive (ver ledger do relatório
        /// desta task, "FormatCpf/FormatCnpj cross-assembly duplication"): a direção de dependência do
        /// app É App -> Signing, nunca o inverso — não haveria como referenciar o helper `private` do
        /// App a partir daqui sem inverter essa direção; relocação pra um lugar comum (`mPdf.Editing`?)
        /// é custo de v1.x, fora de escopo desta task.
        private static string FormatCpf(string digits) =>
            $"{digits[..3]}.{digits[3..6]}.{digits[6..9]}-{digits[9..11]}";

        private static string FormatCnpj(string digits) =>
            $"{digits[..2]}.{digits[2..5]}.{digits[5..8]}/{digits[8..12]}-{digits[12..14]}";
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
