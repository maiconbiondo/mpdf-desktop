using iText.Commons.Exceptions;
using iText.Forms;
using iText.Forms.Fields;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Colors;
using iText.Kernel.Exceptions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Annot.DA;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Navigation;
using iText.Kernel.Pdf.Xobject;
using iText.Kernel.Utils;
using iText.Signatures;

namespace mPdf.Editing;

/// Única classe do projeto que toca tipos do iText de fato (a fronteira pública em Contract.cs é
/// neutra). Interna: só alcançável via PdfEditorFactory.Create().
internal sealed class PdfEditor : IPdfEditor
{
    /// Espessura de traço FIXA v1 (Task 8, Plano 3a, brief: "stroke width fixed 2pt") — constante,
    /// documentada aqui em vez de virar campo do contrato: nenhum caso de uso desta task pede largura
    /// variável por anotação.
    private const float StrokeWidthPt = 2f;

    public bool HasSignatures(byte[] pdf)
    {
        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            return CountSignatures(doc) > 0;
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
    }

    public IReadOnlyList<AnnotationData> ReadAnnotations(byte[] pdf)
    {
        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            var result = new List<AnnotationData>();
            for (int pageNumber = 1; pageNumber <= doc.GetNumberOfPages(); pageNumber++)
            {
                var page = doc.GetPage(pageNumber);
                foreach (var annot in page.GetAnnotations())
                {
                    var subtype = annot.GetSubtype();
                    // DECISÃO (task-2-brief): widgets são campos de formulário/assinatura, não
                    // anotações de usuário — EXCLUÍDOS de ReadAnnotations. Confirmado com
                    // fixture-carimbo.pdf (tem 1 widget da assinatura visível/PAdES e NENHUMA
                    // anotação de usuário): a lista resultante deve vir vazia.
                    if (PdfName.Widget.Equals(subtype)) continue;

                    var kind = MapKind(subtype, annot);
                    if (kind is null) continue; // subtype ainda não suportado pela leitura (ex.: Popup)

                    var rect = annot.GetRectangle()?.ToRectangle();
                    var name = annot.GetName();
                    var title = annot.GetTitle(); // /T — autor em anotações de marcação (ver HIPÓTESE abaixo em AddAnnotation)
                    var contents = annot.GetContents();
                    var lineEndpoints = ReadLineEndpoints(annot); // Task 8 (Plano 3a) — só não-nulo p/ Line/Arrow

                    result.Add(new AnnotationData
                    {
                        Id = name?.ToUnicodeString(),
                        Kind = kind.Value,
                        PageIndex = pageNumber - 1,
                        LeftPt = rect?.GetLeft() ?? 0,
                        BottomPt = rect?.GetBottom() ?? 0,
                        RightPt = rect?.GetRight() ?? 0,
                        TopPt = rect?.GetTop() ?? 0,
                        ColorArgb = ReadColorArgb(annot),
                        Content = contents?.ToUnicodeString(),
                        Author = title?.ToUnicodeString(),
                        Quads = ReadQuads(annot),
                        InkStrokes = ReadInkStrokes(annot),
                        LineStartPt = lineEndpoints?.Start,
                        LineEndPt = lineEndpoints?.End,
                    });
                }
            }
            return result;
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
    }

    public byte[] AddAnnotation(byte[] pdf, AnnotationData annotation)
    {
        // Highlight (Task 2) + Underline/Strikeout (Task 6, Plano 3a) + StickyNote/FreeText (Task 7,
        // Plano 3a) + Ink/Rectangle/Line/Arrow (Task 8, Plano 3a) + ImageStamp (Task 9, Plano 3a — mesma
        // mecânica de bbox/cor/NM/T dos 9 anteriores; só o tipo concreto do iText e a appearance stream
        // custom da imagem mudam, ver BuildAnnotation/bloco de appearance abaixo). Todos os 10 kinds do
        // enum têm implementação agora. Checagem de INPUT pura — não abre o PDF, então fica antes de
        // qualquer coisa que toque iText.
        if (annotation.Kind is not (AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.Strikeout
            or AnnotationKind.StickyNote or AnnotationKind.FreeText
            or AnnotationKind.Ink or AnnotationKind.Rectangle or AnnotationKind.Line or AnnotationKind.Arrow
            or AnnotationKind.ImageStamp))
            throw new NotSupportedException(
                $"AddAnnotation: tipo '{annotation.Kind}' não reconhecido pelo módulo de edição.");

        // Formato do Id: outra checagem de INPUT pura (revisão pós-M11, rodada 2 — item 2a). Só o Id
        // INFORMADO é validado; nulo (= "gerar um GUID") nunca cai aqui.
        if (annotation.Id is not null && string.IsNullOrWhiteSpace(annotation.Id))
            throw new ArgumentException(
                "Id de anotação não pode ser vazio nem conter só espaços.", nameof(annotation));

        // Geometria OBRIGATÓRIA dos 3 kinds novos que carregam pontos próprios (Task 8, Plano 3a) —
        // checagem de INPUT pura, mesmo espírito do Id acima: falha ANTES de tocar o PDF, nunca grava
        // uma anotação degenerada (Ink sem nenhum traço; Line/Arrow sem os 2 pontos que o /L do spec
        // exige). Rectangle não entra aqui — sua geometria inteira já é o bbox Left/Bottom/Right/Top
        // que todo AnnotationData sempre carrega, sem campo extra nenhum (ver BuildAnnotation).
        if (annotation.Kind == AnnotationKind.Ink && (annotation.InkStrokes is null || annotation.InkStrokes.Count == 0))
            throw new ArgumentException("Ink requer InkStrokes com pelo menos 1 traço.", nameof(annotation));
        if (annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow
            && (annotation.LineStartPt is null || annotation.LineEndPt is null))
            throw new ArgumentException("Line/Arrow requer LineStartPt e LineEndPt.", nameof(annotation));
        // ImageStamp (Task 9, Plano 3a): mesma disciplina — a appearance stream customizada (ver bloco
        // abaixo) não existe sem os bytes da imagem; falha ANTES de tocar o PDF, nunca grava um /Stamp
        // sem nenhuma aparência visual.
        if (annotation.Kind == AnnotationKind.ImageStamp && (annotation.ImageBytes is null || annotation.ImageBytes.Length == 0))
            throw new ArgumentException("ImageStamp requer ImageBytes (os bytes PNG/JPG do carimbo).", nameof(annotation));

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                GuardAgainstSignedDocument(doc);
                ValidatePageIndex(doc, annotation.PageIndex); // ArgumentOutOfRangeException — antes de tocar a página/anotação
                // Unicidade de Id (item 2a): ANTES de qualquer escrita — nunca duplica um /NM existente.
                if (annotation.Id is not null) GuardAgainstDuplicateId(doc, annotation.Id);

                var page = doc.GetPage(annotation.PageIndex + 1); // iText é 1-based; contrato é 0-based
                var bbox = new Rectangle(
                    (float)annotation.LeftPt, (float)annotation.BottomPt,
                    (float)(annotation.RightPt - annotation.LeftPt),
                    (float)(annotation.TopPt - annotation.BottomPt));

                var markup = BuildAnnotation(annotation, bbox);

                // Sentinela de cor (pós-M7): null -> NENHUM /C escrito (não default para preto). Vale
                // pros 5 kinds: Highlight/Underline/Strikeout usam /C como cor de marcação; StickyNote
                // como cor do ÍCONE; FreeText também recebe aqui (round-trip via ReadColorArgb, que só
                // enxerga /C) — a cor de TEXTO visível de FreeText é escrita separadamente no /DA
                // abaixo, espelhando este mesmo valor (ver bloco FreeText mais abaixo).
                if (annotation.ColorArgb is { } argb)
                {
                    var (r, g, b) = ArgbToRgb(argb);
                    markup.SetColor(new DeviceRgb(r, g, b));
                }

                // NM: honra o Id informado pelo chamador (pós-M11/I5 — estabilidade de Id entre
                // chamadas, a Task 7/undo-redo depende disso); gera um GUID só se Id vier nulo.
                markup.SetName(new PdfString(annotation.Id ?? Guid.NewGuid().ToString("N")));
                if (annotation.Content is not null) markup.SetContents(annotation.Content);
                // /T é o campo de autor em anotações de marcação (PdfMarkupAnnotation herda
                // GetTitle/SetTitle de PdfAnnotation) — confirmado via reflexão contra itext.kernel.dll
                // 9.7.0 (não há GetAuthor/SetAuthor dedicado; /T É o autor por definição do spec PDF).
                if (annotation.Author is not null) markup.SetTitle(new PdfString(annotation.Author));

                // FreeText (Task 7, Plano 3a): `// HIPÓTESE:` do brief — "appearance/DefaultAppearance
                // para o texto" — reconciliada por reflexão contra itext.kernel.dll 9.7.0:
                // `PdfFreeTextAnnotation.SetDefaultAppearance(AnnotationDefaultAppearance)` monta o /DA
                // ("/Helv 12 Tf 0 0 1 rg" etc.) a partir de `SetFont`/`SetFontSize`/`SetColor` —
                // reprodução type-safe do string de /DA cru. Tamanho fixo 12pt (v1, brief). Cor: espelha
                // o MESMO ColorArgb já escrito em /C acima (ausência de cor -> sem operador de cor no
                // /DA, o leitor assume preto por default do spec, nunca escrevemos preto explícito à
                // toa). PDFium (o motor de renderização deste app) sintetiza a aparência visual do texto
                // a partir do /DA quando não há /AP explícito (comportamento padrão do spec para
                // FreeText) — nenhuma appearance stream é construída manualmente aqui.
                if (markup is PdfFreeTextAnnotation freeText)
                {
                    var da = new AnnotationDefaultAppearance()
                        .SetFont(StandardAnnotationFont.Helvetica)
                        .SetFontSize(12f);
                    if (annotation.ColorArgb is { } daArgb)
                    {
                        var (dr, dg, db) = ArgbToRgb(daArgb);
                        da.SetColor(new DeviceRgb(dr, dg, db));
                    }
                    freeText.SetDefaultAppearance(da);
                }

                // ImageStamp (Task 9, Plano 3a): a IMAGEM em si é uma appearance stream custom — um
                // PdfFormXObject do TAMANHO DO BBOX (o App já manda o bbox com a proporção certa da
                // imagem, calculada a partir do tamanho natural — ver DocumentViewModel.PlaceStampAtAsync
                // — então preencher o bbox inteiro nunca distorce), com a imagem desenhada dentro via
                // PdfCanvas.AddImageFittedIntoRectangle, gravada como /AP /N via SetNormalAppearance.
                // Confirmado empiricamente (probe project, ver task-9-report.md): o XObject de imagem
                // resultante fica AUTOCONTIDO dentro de /AP/N/Resources/XObject (nenhum recurso extra
                // precisa ser anexado à PÁGINA) e PDFium/qualquer leitor de PDF pinta a partir de /AP/N
                // (nunca precisou de /Name — o ícone padrão que SetIconName grava, nunca usado aqui).
                // ImageDataFactory.Create detecta PNG/JPG automaticamente pelos bytes (magic number) —
                // sem branch por extensão. Falha de decodificação (bytes não são uma imagem válida) sobe
                // como ITextException, capturada pelo catch genérico abaixo (WrapGeneric) — mesmo canal
                // de erro neutro de qualquer outra falha do iText.
                if (markup is PdfStampAnnotation stamp)
                {
                    var imageData = ImageDataFactory.Create(annotation.ImageBytes!);
                    var appearance = new PdfFormXObject(bbox);
                    new PdfCanvas(appearance, doc).AddImageFittedIntoRectangle(imageData, bbox, false);
                    stamp.SetNormalAppearance(appearance.GetPdfObject());
                }

                // Espessura de traço FIXA 2pt v1 (Task 8, Plano 3a — brief). Só os 3 kinds cujo visual
                // é uma BORDA/TRAÇO precisam disto: Highlight/Underline/Strikeout marcam texto (sem
                // contorno); StickyNote/FreeText não têm contorno nesta v1. `SetBorder` mora em
                // `PdfAnnotation` (a raiz de toda a hierarquia, não só `PdfMarkupAnnotation`) — reconciliado
                // empiricamente (ver task-8-report.md): grava `/Border [0 0 2]` (raios de canto 0, sem
                // dash), servindo Ink/Square/Line de uma vez sem um caso por kind.
                if (annotation.Kind is AnnotationKind.Ink or AnnotationKind.Rectangle or AnnotationKind.Line or AnnotationKind.Arrow)
                    markup.SetBorder(new PdfAnnotationBorder(0, 0, StrokeWidthPt));

                page.AddAnnotation(markup);
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    public byte[] RemoveAnnotation(byte[] pdf, string annotationId)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        bool removed = false;
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                GuardAgainstSignedDocument(doc);

                // Remove EXATAMENTE 1 (item 2b, revisão pós-M11 rodada 2): para no primeiro /NM que
                // bater — nunca "remove todas as que baterem". AddAnnotation garante Id único por
                // construção (GuardAgainstDuplicateId), então duplicidade só pode vir de um PDF de
                // origem EXTERNA (residual, não testável sem um fixture com /NM duplicado plantado à
                // mão — documentado em vez de fabricado; ver task-2-report.md).
                for (int pageNumber = 1; pageNumber <= doc.GetNumberOfPages() && !removed; pageNumber++)
                {
                    var page = doc.GetPage(pageNumber);
                    // .ToList(): materializa antes de remover — GetAnnotations() é apoiada no
                    // /Annots vivo da página, mutar durante o foreach quebraria a enumeração.
                    foreach (var annot in page.GetAnnotations().ToList())
                    {
                        if (annot.GetName()?.ToUnicodeString() == annotationId)
                        {
                            page.RemoveAnnotation(annot);
                            removed = true;
                            break;
                        }
                    }
                }
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        if (!removed)
            throw new InvalidOperationException($"Anotação '{annotationId}' não encontrada no PDF.");
        return output.ToArray();
    }

    /// `// HIPÓTESE:` reconciliada por reflexão contra itext.forms.dll 9.7.0 (ver task-5-report.md):
    /// `PdfAcroForm.RemoveField(string)` remove o campo E o(s) widget(s) associado(s) da(s) página(s)
    /// de uma vez — confirmado empiricamente contra fixture-carimbo.pdf (1 assinatura PAdES real): a
    /// contagem de anotações da página cai de 1 para 0 no mesmo passo, sem precisar iterar
    /// `page.GetAnnotations()` à mão feito RemoveAnnotation acima. Só os nomes que `SignatureUtil`
    /// enxerga (campos `/FT /Sig`) são removidos — outros campos de formulário (texto, checkbox etc.),
    /// se algum dia existirem no PDF, nunca são tocados por este laço.
    public byte[] StripSignatures(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                var signatureNames = new SignatureUtil(doc).GetSignatureNames();
                if (signatureNames.Count > 0)
                {
                    // AcroForm SEMPRE existe se há >=1 assinatura (é onde o campo /FT /Sig mora) — o
                    // `?.` é defesa em profundidade, não uma hipótese de que possa ser nulo aqui.
                    var acroForm = PdfAcroForm.GetAcroForm(doc, false);
                    foreach (var name in signatureNames)
                        acroForm?.RemoveField(name);
                }
                // Doc sem assinatura: laço acima nunca roda — no-op por construção, mas o documento
                // ainda passa pelo ciclo PdfReader->PdfWriter (reescrita equivalente), então mesmo aqui
                // devolvemos um PDF válido reprocessado, nunca os bytes originais intocados.
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    // --- Task 2 (Plano 3b): motor de organização de páginas -------------------------------------
    // Decisões de contrato (extensão de IPdfEditor, índices 0-based, gate por operação) registradas
    // no XML doc de IPdfEditor em Contract.cs — não repetidas aqui.

    /// `// HIPÓTESE:` do brief — `PdfPage.SetRotation`/`GetRotation` — reconciliada por sonda
    /// empírica (probe project isolado, mesmo método da Task 8/9 do Plano 3a; ver task-2-report.md,
    /// Plano 3b): `SetRotation(int)` é ABSOLUTO, NÃO aditivo — chamar `SetRotation(90)` duas vezes
    /// seguidas (em 2 saves separados) grava `/Rotate 90` as DUAS vezes, nunca 180. Por isso este
    /// método LÊ a rotação existente (`GetRotation()`) antes de escrever, soma `degreesClockwise` e
    /// normaliza `% 360` — uma rotação dupla de 90+90 (2 chamadas separadas a RotatePages) dá 180,
    /// confirmado empiricamente e coberto por teste (RotatePages_DoubleRotate90Plus90_Is180).
    public byte[] RotatePages(byte[] pdf, IReadOnlyList<int> pageIndexes, int degreesClockwise)
    {
        // Checagem de INPUT pura (não abre o PDF) — só 90/180/270 são ângulos válidos (mesmo
        // espírito de AddAnnotation.Kind).
        if (degreesClockwise is not (90 or 180 or 270))
            throw new ArgumentException(
                $"Ângulo de rotação inválido: {degreesClockwise}. Use 90, 180 ou 270 graus.",
                nameof(degreesClockwise));

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                GuardAgainstSignedDocument(doc);
                // Duplicatas em pageIndexes contam 1 vez só (mesmo raciocínio de DeletePages abaixo):
                // sem Distinct(), [0,0] giraria a página 0 DUAS vezes na mesma chamada — 90+90=180 em
                // vez dos 90 esperados de "girar a página 0 uma vez" (coberto por teste,
                // RotatePages_DuplicateIndexes_RotatesOnce).
                var distinct = pageIndexes.Distinct().ToList();
                // TODOS os índices validados ANTES de mutar QUALQUER página — nunca gira metade da
                // lista e falha na outra metade a meio caminho.
                foreach (var idx in distinct) ValidatePageIndex(doc, idx);

                foreach (var idx in distinct)
                {
                    var page = doc.GetPage(idx + 1); // iText é 1-based; contrato é 0-based
                    int existing = page.GetRotation();
                    page.SetRotation((existing + degreesClockwise) % 360);
                }
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    /// Costura de rotação (Task 3, Plano 3b — ver XML doc em Contract.cs): leitura pura de `/Rotate` por
    /// página, normalizada para 0/90/180/270. `GetRotation()` já devolve um valor não-negativo neste
    /// codebase (`RotatePages` só escreve somas de 90/180/270 sobre um valor existente também
    /// não-negativo, sempre `% 360`) — a normalização `((raw % 360) + 360) % 360` aqui é defesa em
    /// profundidade contra um `/Rotate` de origem EXTERNA fora desse invariante (o spec PDF permite
    /// múltiplos negativos de 90), não uma correção de um caso observado neste módulo.
    public IReadOnlyList<int> GetPageRotations(byte[] pdf)
    {
        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            var result = new int[doc.GetNumberOfPages()];
            for (int i = 0; i < result.Length; i++)
            {
                int raw = doc.GetPage(i + 1).GetRotation();
                result[i] = ((raw % 360) + 360) % 360;
            }
            return result;
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
    }

    /// Limite de profundidade de recursão de `BuildOutlineNode` (revisão pós-Task 5, Important): o
    /// escritório abre PDFs de origem EXTERNA diariamente — um `/Outlines` FORJADO (ex.: `/First` de um
    /// nó filho apontando de volta pra um dicionário ANCESTRAL) poderia produzir um grafo cíclico que
    /// `BuildOutlineNode` percorreria pra sempre; mesmo sem ciclo, uma cadeia aninhada simplesmente MUITO
    /// funda (construível de propósito, sem precisar de ciclo nenhum) já basta pra estourar a pilha.
    /// `StackOverflowException` NÃO é capturável em .NET — derrubaria o PROCESSO inteiro, sem chance de
    /// notificar o usuário (diferente de toda outra defesa deste módulo, que sempre devolve um valor
    /// neutro ou lança uma exceção CAPTURÁVEL). 64 é generoso pra qualquer sumário real (a fixture de
    /// teste tem 3 níveis; nenhum documento de uso comum passa de uma dezena). Além do limite,
    /// `BuildOutlineNode` PARA de descender e devolve o nó COM `Children` vazio — nunca lança, nunca
    /// derruba a leitura da árvore JÁ construída até aquele ponto (mesmo espírito de `ResolvePageIndex`
    /// devolvendo `null` em vez de propagar uma falha isolada). Testado via
    /// `ReadOutline_DeeplyNestedOutline_StopsDescendingAtMaxDepth` (fixture com 100 níveis reais, gerada
    /// 1x via PoC — prova o CAMINHO DE RECURSÃO de verdade, não só a função de decisão isolada).
    private const int MaxOutlineDepth = 64;

    /// Sumário/bookmarks (Task 5, Plano 3b — ver HIPÓTESE/RESOLUÇÃO DE DESTINO em Contract.cs):
    /// leitura pura, sem gate de assinatura. `GetOutlines(false)` devolve a raiz VIRTUAL da árvore
    /// (achado empírico: `null` quando o documento não tem `/Outlines` nenhum — nunca uma raiz vazia)
    /// — só os FILHOS dela viram o `IReadOnlyList<OutlineNode>` de topo devolvido aqui.
    public IReadOnlyList<OutlineNode> ReadOutline(byte[] pdf)
    {
        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            var root = doc.GetOutlines(false);
            if (root is null) return Array.Empty<OutlineNode>();
            var names = doc.GetCatalog().GetNameTree(PdfName.Dests);
            return root.GetAllChildren().Select(child => BuildOutlineNode(child, doc, names, depth: 0)).ToArray();
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
    }

    /// `depth` — profundidade do nó ATUAL (0 = filho direto da raiz virtual). Ver `MaxOutlineDepth`
    /// acima: em `depth >= MaxOutlineDepth`, este nó ainda é construído normalmente (título/página
    /// resolvidos), só `Children` é forçado vazio SEM consultar `GetAllChildren()` de novo — é isso que
    /// quebra a recursão (nunca chega a chamar `BuildOutlineNode` numa profundidade 65).
    private static OutlineNode BuildOutlineNode(PdfOutline outline, PdfDocument doc, IPdfNameTreeAccess names, int depth)
    {
        var children = depth >= MaxOutlineDepth
            ? Array.Empty<OutlineNode>()
            : outline.GetAllChildren().Select(child => BuildOutlineNode(child, doc, names, depth + 1)).ToArray();
        return new OutlineNode(outline.GetTitle() ?? string.Empty, ResolvePageIndex(outline, doc, names), children);
    }

    /// `null` (sem página) por TRÊS causas colapsadas de propósito — ver doc XML de `OutlineNode.
    /// PageIndex` em Contract.cs: (1) `GetDestination()` nulo — nó organizacional puro, sem `/Dest`/
    /// `/A`; (2) `GetDestination()` presente mas `GetDestinationPage` não resolve pra um `PdfDictionary`
    /// que `doc.GetPage`/`GetPageNumber` reconhece — destino nomeado sem entrada na árvore `/Dests`, ou
    /// qualquer outra forma de destino quebrado/inesperado; (3) a resolução LANÇA (`doc.GetPage(dict)`
    /// contra um dicionário que não é uma página válida do documento — cenário nunca observado na sonda
    /// empírica com `DeletePages`, ver task-5-report.md, mas defesa em profundidade contra um PDF de
    /// origem EXTERNA com outline malformado). Nunca deixa a exceção subir — um bookmark isolado
    /// quebrado não pode derrubar a leitura da árvore inteira, mesmo espírito de `MapKind` devolvendo
    /// `null` pra um subtype de anotação não suportado em `ReadAnnotations`.
    private static int? ResolvePageIndex(PdfOutline outline, PdfDocument doc, IPdfNameTreeAccess names)
    {
        var destination = outline.GetDestination();
        if (destination is null) return null;
        try
        {
            if (destination.GetDestinationPage(names) is not PdfDictionary pageDict) return null;
            var page = doc.GetPage(pageDict);
            int pageNumber = doc.GetPageNumber(page);
            return pageNumber > 0 ? pageNumber - 1 : null;
        }
        catch (ITextException) { return null; }
    }

    public byte[] DeletePages(byte[] pdf, IReadOnlyList<int> pageIndexes)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                GuardAgainstSignedDocument(doc);
                foreach (var idx in pageIndexes) ValidatePageIndex(doc, idx);

                // Duplicatas em pageIndexes contam 1 vez só pro "excluir todas" E pra remoção em si
                // (remover o mesmo número 2x lançaria na 2ª tentativa).
                var distinct = pageIndexes.Distinct().ToList();
                if (distinct.Count == doc.GetNumberOfPages())
                    throw new ArgumentException(
                        "Não é possível excluir todas as páginas do documento.", nameof(pageIndexes));

                // Ordem DESCENDENTE (maior índice primeiro): `RemovePage(int)` é por número 1-based;
                // remover um número menor primeiro desloraria os números maiores AINDA não
                // removidos (a página que era #5 vira #4 depois de remover a #2) — descendente evita
                // o deslocamento por construção, sem precisar recalcular nada (confirmado
                // empiricamente, ver task-2-report.md).
                foreach (var idx in distinct.OrderByDescending(i => i))
                    doc.RemovePage(idx + 1);
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    /// `// HIPÓTESE:` do brief não indicava a API — reconciliada por sonda empírica (probe project
    /// isolado): `PdfDocument.MovePage(int pageNumberFrom, int insertBeforePosition)` EXISTE
    /// (1-based nos 2 argumentos), mas `insertBeforePosition` é medido na numeração ORIGINAL
    /// (pré-remoção) do documento, não na posição FINAL 0-based que este contrato promete. Fórmula
    /// reconciliada empiricamente (testada contra ~10 combinações from/to num doc de 5 páginas,
    /// incluindo mover para o início/fim e o caso `fromIndex==toIndex` como no-op): para pousar a
    /// página exatamente no índice 0-based `toIndex` desejado, o argumento nativo é `toIndex+1`
    /// quando `toIndex &lt;= fromIndex`, ou `toIndex+2` quando `toIndex &gt; fromIndex` (mover para
    /// frente desloca os itens intermediários 1 posição pra trás depois que a origem sai do lugar).
    public byte[] MovePage(byte[] pdf, int fromIndex, int toIndex)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                GuardAgainstSignedDocument(doc);
                ValidatePageIndex(doc, fromIndex);
                ValidatePageIndex(doc, toIndex);

                int from1 = fromIndex + 1;
                int to1 = toIndex <= fromIndex ? toIndex + 1 : toIndex + 2;
                doc.MovePage(from1, to1);
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    /// SEM gate (política única com MergeDocuments/SplitByRanges — ver decisão em Contract.cs,
    /// revisada pela Opus review pós-Task 2: a versão original dava gate a ExtractPages com uma
    /// justificativa de "pluralidade de entradas" que não resistiu à comparação com SplitByRanges,
    /// que também recebe 1 único `pdf` e nunca teve gate). Aceita fonte assinada de propósito —
    /// `HasSignatures(resultado) == false` é assertado por teste
    /// (ExtractPages_FromSignedDocument_ProducesUnsignedResult), com o mesmo mecanismo de rede de
    /// segurança defensiva (`StripSignatures` no resultado) que MergeDocuments já usava.
    public byte[] ExtractPages(byte[] pdf, IReadOnlyList<int> pageIndexes)
    {
        // Checagem de INPUT pura (não abre o PDF) — lista vazia não é um pedido de extração válido.
        if (pageIndexes.Count == 0)
            throw new ArgumentException("ExtractPages requer ao menos 1 índice de página.", nameof(pageIndexes));

        // C1 (revisão final pré-merge, Plano 3b): `CopyPagesTo` copia a APARÊNCIA do carimbo de
        // assinatura (o widget visual /AP na página) mas NUNCA o `/AcroForm`/campo `/FT /Sig` que o
        // sustenta (achado empírico, ver XML doc de MergeDocuments abaixo — mesmo mecanismo) — o
        // `StripSignatures(output...)` no FIM deste método (rede de segurança "defensiva") já não
        // encontra NADA pra remover depois da cópia (`SignatureUtil` vê 0 campos no destino), então o
        // carimbo "Assinado digitalmente por…" sobrevive na página copiada, pixel-idêntico ao original,
        // enquanto `HasSignatures(resultado) == false` mente sobre isso — medido: 0 px de diferença
        // entre o render do resultado e o render do original ANTES deste fix, texto do carimbo intacto.
        // Fix: `StripSignatures` na ORIGEM, ANTES de copiar — `PdfAcroForm.RemoveField` remove o campo
        // E o widget associado NA MESMA chamada (ver XML doc de StripSignatures), então a página que
        // `CopyPagesTo` copia já sai sem o carimbo. `HasSignatures` primeiro evita o custo de um 2º
        // PdfReader->PdfWriter em todo documento NÃO assinado (o caso comum).
        var source = HasSignatures(pdf) ? StripSignatures(pdf) : pdf;

        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        try
        {
            // `srcDoc` só precisa de PdfReader (nunca é reescrito — "original untouched" é uma
            // consequência estrutural de nunca abrir um PdfWriter sobre ele, não uma checagem extra).
            using (var srcDoc = new PdfDocument(new PdfReader(input)))
            {
                foreach (var idx in pageIndexes) ValidatePageIndex(srcDoc, idx);

                using (var destDoc = new PdfDocument(new PdfWriter(output)))
                {
                    // `// HIPÓTESE:` iText copyPagesTo — reconciliada por sonda empírica:
                    // `PdfDocument.CopyPagesTo(IList<int>, PdfDocument)` copia na ORDEM EXATA da
                    // lista dada (não reordena por número crescente) — permite ExtractPages devolver
                    // as páginas fora de ordem se o chamador pedir assim.
                    var pageNumbers = pageIndexes.Select(i => i + 1).ToList();
                    srcDoc.CopyPagesTo(pageNumbers, destDoc);
                }
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return StripSignatures(output.ToArray()); // rede de segurança defensiva (ver Contract.cs)
    }

    public byte[] InsertPages(byte[] pdf, byte[] source, int atIndex)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var targetDoc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                GuardAgainstSignedDocument(targetDoc); // gate no ALVO só (ver decisão em Contract.cs)
                ValidateInsertionIndex(targetDoc, atIndex);

                // SEM guard na origem, DE PROPÓSITO: inserir páginas de um PDF assinado é uma
                // LEITURA da origem (nunca uma edição dela) — só o alvo precisa estar livre de
                // assinatura. Coberto por teste (InsertPages_SignedSource_Works). C1 (revisão final
                // pré-merge): mesmo mecanismo de ExtractPages acima — `StripSignatures` na ORIGEM
                // ANTES de copiar, pra que o widget visual do carimbo não sobreviva órfão no ALVO
                // (o alvo em si nunca teve assinatura — `GuardAgainstSignedDocument` acima já garante
                // isso — então não há "resultado" pra passar por StripSignatures depois; só a ORIGEM
                // precisa do tratamento).
                var sourceBytes = HasSignatures(source) ? StripSignatures(source) : source;
                using var sourceInput = new MemoryStream(sourceBytes);
                using (var sourceDoc = new PdfDocument(new PdfReader(sourceInput)))
                {
                    // `// HIPÓTESE:` iText copyPagesTo com posição de inserção — reconciliada por
                    // sonda empírica: o overload de 3 argumentos `CopyPagesTo(IList<int>, PdfDocument,
                    // int insertBeforePage)` insere ANTES da página 1-based `insertBeforePage` do
                    // documento ALVO — `atIndex+1` direto (sem o ajuste que MovePage precisou, porque
                    // aqui não há remoção prévia deslocando nada: é inserção pura). `insertBeforePage
                    // == pageCount+1` insere no FIM, testado e funciona sem caso especial.
                    var pageNumbers = Enumerable.Range(1, sourceDoc.GetNumberOfPages()).ToList();
                    sourceDoc.CopyPagesTo(pageNumbers, targetDoc, atIndex + 1);
                }
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    /// SEM gate (ver decisão em Contract.cs) — aceita fontes assinadas de propósito. Empírico
    /// (probe project isolado, ver task-2-report.md): `PdfMerger.Merge(PdfDocument, int, int)` NÃO
    /// preserva o AcroForm/campo de assinatura da fonte — testado com fixture-carimbo (1 assinatura
    /// PAdES real de verdade) como uma das entradas: o resultado mesclado sai SEM AcroForm nenhum
    /// (`SignatureUtil` já vê 0 assinaturas logo após o merge, nenhum passo extra necessário) — mas
    /// (C1, revisão final pré-merge — achado que a v1 acima NÃO tinha: medido por render-diff, não só
    /// por `SignatureUtil`) o WIDGET visual (a aparência do carimbo "Assinado digitalmente por…")
    /// sobrevive como anotação órfã/inerte na página, pixel-idêntico ao original — não é cosmético
    /// inofensivo, é o pior caso possível pra um escritório que gera um PDF "sem assinatura" que ainda
    /// PARECE assinado. Fix: `StripSignatures` PER FONTE, antes de `merger.Merge` — mesmo mecanismo de
    /// `ExtractPages`/`InsertPages` acima (remove o campo E o widget associado numa única chamada, ver
    /// XML doc de `StripSignatures`), então o widget nunca chega a ser copiado. O resultado ainda passa
    /// por `StripSignatures` como REDE DE SEGURANÇA defensiva antes de retornar (agora genuinamente
    /// no-op, já que nenhuma fonte assinada sobrevive até aqui) — garante o invariante
    /// `HasSignatures(MergeDocuments(...)) == false` mesmo se uma versão futura do iText passar a
    /// preservar o AcroForm da fonte.
    public byte[] MergeDocuments(IReadOnlyList<byte[]> pdfs)
    {
        // Checagem de INPUT pura (não abre nenhum PDF) — lista vazia não produz um documento válido.
        if (pdfs.Count == 0)
            throw new ArgumentException("MergeDocuments requer ao menos 1 documento.", nameof(pdfs));

        using var output = new MemoryStream();
        try
        {
            using (var destDoc = new PdfDocument(new PdfWriter(output)))
            {
                var merger = new PdfMerger(destDoc);
                foreach (var pdf in pdfs)
                {
                    var sourceBytes = HasSignatures(pdf) ? StripSignatures(pdf) : pdf;
                    using var srcInput = new MemoryStream(sourceBytes);
                    using var srcDoc = new PdfDocument(new PdfReader(srcInput));
                    merger.Merge(srcDoc, 1, srcDoc.GetNumberOfPages());
                }
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return StripSignatures(output.ToArray()); // rede de segurança defensiva (ver XML doc acima)
    }

    /// SEM gate (política única com ExtractPages/MergeDocuments — ver decisão em Contract.cs) —
    /// leitura pura, `pdf` nunca é reescrito, mesmo se estiver assinado (só abre `srcDoc` com
    /// PdfReader, nunca com um PdfWriter por cima dele); `HasSignatures` de CADA saída `== false` é
    /// assertado por teste (SplitByRanges_FromSignedDocument_ProducesUnsignedResults), com o mesmo
    /// mecanismo de rede de segurança defensiva (`StripSignatures`) que MergeDocuments/ExtractPages
    /// já usam.
    public IReadOnlyList<byte[]> SplitByRanges(byte[] pdf, IReadOnlyList<(int from, int to)> ranges)
    {
        var results = new List<byte[]>();
        try
        {
            // C1 (revisão final pré-merge): mesmo mecanismo de ExtractPages/MergeDocuments/InsertPages
            // acima — `StripSignatures` na ORIGEM ANTES de copiar QUALQUER range, pra que o widget
            // visual do carimbo não sobreviva órfão em NENHUMA das partes geradas (1 strip serve todos
            // os ranges, já que é a MESMA origem pra todos eles).
            var source = HasSignatures(pdf) ? StripSignatures(pdf) : pdf;
            using var srcInput = new MemoryStream(source);
            using (var srcDoc = new PdfDocument(new PdfReader(srcInput)))
            {
                // TODOS os ranges validados (índices + `to >= from`) ANTES de construir QUALQUER
                // saída — um range inválido no MEIO da lista nunca deixa resultados parciais no
                // `IReadOnlyList` devolvido (mesmo espírito de RotatePages validar todos os índices
                // antes de girar qualquer página).
                foreach (var (from, to) in ranges)
                {
                    ValidatePageIndex(srcDoc, from);
                    ValidatePageIndex(srcDoc, to);
                    if (to < from)
                        throw new ArgumentException(
                            $"Intervalo inválido: fim ({to}) antes do início ({from}).", nameof(ranges));
                }

                foreach (var (from, to) in ranges)
                {
                    using var output = new MemoryStream();
                    using (var destDoc = new PdfDocument(new PdfWriter(output)))
                    {
                        // `// HIPÓTESE:` iText copyPagesTo por intervalo — reconciliada por sonda
                        // empírica: `CopyPagesTo(int pageFrom, int pageTo, PdfDocument)` (1-based,
                        // INCLUSIVO nos 2 extremos) copia o intervalo inteiro numa chamada.
                        srcDoc.CopyPagesTo(from + 1, to + 1, destDoc);
                    }
                    results.Add(StripSignatures(output.ToArray())); // rede de segurança defensiva
                }
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return results;
    }

    /// Defesa em profundidade (revisão pós-M11): a Task 5 vai adicionar o mesmo gate na camada de
    /// App, mas o módulo de edição não deve confiar que todo chamador presente e futuro sempre checa
    /// antes de editar. Reutiliza o `doc` JÁ aberto (evita abrir o PDF 2x por chamada). Tipo dedicado
    /// (revisão pós-M11, rodada 2 — item 1): `PdfSignedDocumentException`, não mais a
    /// `InvalidOperationException` genérica — a Task 5 precisa capturar ESTE caso especificamente
    /// (para oferecer "Editar uma cópia") sem confundir com outras causas de InvalidOperationException.
    private static void GuardAgainstSignedDocument(PdfDocument doc)
    {
        if (CountSignatures(doc) > 0)
            throw new PdfSignedDocumentException(
                "Documento contém assinaturas — edição bloqueada (spec §5.2). Use 'Editar uma cópia'.");
    }

    /// REVIEW (Task 2/Plano 3c, Important 2) — ACHADO REAL: `new SignatureUtil(doc)` aciona
    /// `PdfAcroForm.GetAcroForm` internamente, a MESMA falha empírica documentada em `HasXfa`
    /// (`PdfException: Root element is missing` ao parsear um `/XFA` malformado/dummy) — significa que
    /// `HasSignatures`/`GuardAgainstSignedDocument` (e portanto TODO mutador gateado deste contrato:
    /// `RotatePages`/`DeletePages`/`MovePage`/`InsertPages`/`AddAnnotation`/`RemoveAnnotation`/
    /// `SetFormFields`/`FlattenForm`) lançavam `PdfEditingException` genérica em QUALQUER documento com
    /// `/XFA`, mesmo um SEM assinatura nenhuma. Fix: quando `HasXfaKey(doc)` é verdadeiro, conta
    /// assinaturas via `CountSignaturesRaw` (varredura crua de `/AcroForm/Fields`, sem instanciar
    /// `PdfAcroForm`/`SignatureUtil`); documento SEM `/XFA` continua no caminho ORIGINAL, inalterado
    /// (`SignatureUtil`, nunca tocado por este fix).
    private static int CountSignatures(PdfDocument doc) =>
        HasXfaKey(doc) ? CountSignaturesRaw(doc) : new SignatureUtil(doc).GetSignatureNames().Count;

    /// Varredura CRUA de `/AcroForm/Fields` (recursiva via `/Kids` — grupos de campo aninhados) por
    /// `/FT /Sig` com `/V` presente — `/V` ausente é um placeholder VAZIO (não conta, mesma semântica de
    /// `SignatureUtil.GetSignatureNames()`, que só lista campos efetivamente assinados). Nunca instancia
    /// `PdfAcroForm`/`XfaForm` — só acessa o dicionário CRU, mesmo padrão de `HasXfaKey`.
    private static int CountSignaturesRaw(PdfDocument doc)
    {
        var fields = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm)?.GetAsArray(PdfName.Fields);
        if (fields is null) return 0;
        int count = 0;
        CountSignaturesInFieldsArray(fields, ref count);
        return count;
    }

    private static void CountSignaturesInFieldsArray(PdfArray fields, ref int count)
    {
        for (int i = 0; i < fields.Size(); i++)
        {
            var field = fields.GetAsDictionary(i);
            if (field is null) continue;
            if (PdfName.Sig.Equals(field.GetAsName(PdfName.FT)) && field.ContainsKey(PdfName.V)) count++;

            var kids = field.GetAsArray(PdfName.Kids);
            if (kids is not null) CountSignaturesInFieldsArray(kids, ref count);
        }
    }

    /// Detector de PRESENÇA da chave `/XFA` no dicionário CRU do AcroForm — extraído (Important 2,
    /// revisão) porque agora tem 2 chamadores (`HasXfa` público + `CountSignatures` acima). NUNCA
    /// instancia `PdfAcroForm` (ver ACHADO EMPÍRICO em Contract.cs).
    private static bool HasXfaKey(PdfDocument doc) =>
        doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm)?.ContainsKey(PdfName.XFA) ?? false;

    /// Unicidade de Id (item 2a, revisão pós-M11 rodada 2): um /NM que já exista em QUALQUER
    /// anotação do documento (qualquer subtype, não só as que ReadAnnotations devolveria — um widget
    /// também tem /NM e colidir com ele seria igualmente ruim) faz AddAnnotation recusar ANTES de
    /// escrever. Ids gerados automaticamente (GUID) nunca passam por aqui — unicidade garantida por
    /// construção.
    private static void GuardAgainstDuplicateId(PdfDocument doc, string id)
    {
        for (int pageNumber = 1; pageNumber <= doc.GetNumberOfPages(); pageNumber++)
        {
            foreach (var annot in doc.GetPage(pageNumber).GetAnnotations())
            {
                if (annot.GetName()?.ToUnicodeString() == id)
                    throw new ArgumentException($"Id de anotação já existe no documento: {id}");
            }
        }
    }

    /// ArgumentOutOfRangeException com mensagem pt-BR — antes de qualquer manipulação de página/
    /// anotação (só depois de abrir o doc, que é o mínimo necessário para saber quantas páginas ele
    /// tem; ver task-2-report.md, seção do fix, para a justificativa dessa leitura de "antes de tocar
    /// o iText").
    private static void ValidatePageIndex(PdfDocument doc, int pageIndex)
    {
        int pageCount = doc.GetNumberOfPages();
        if (pageIndex < 0 || pageIndex >= pageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                $"Índice de página {pageIndex} fora do intervalo válido (0 a {pageCount - 1}).");
    }

    /// Índice de INSERÇÃO (InsertPages, Task 2 do Plano 3b): ao contrário de ValidatePageIndex (que
    /// exige um índice de página JÁ EXISTENTE, 0..pageCount-1), aqui o intervalo válido é
    /// 0..pageCount INCLUSIVE — o valor `pageCount` é um caso de uso legítimo ("inserir no FIM",
    /// depois da última página), não um erro.
    private static void ValidateInsertionIndex(PdfDocument doc, int atIndex)
    {
        int pageCount = doc.GetNumberOfPages();
        if (atIndex < 0 || atIndex > pageCount)
            throw new ArgumentOutOfRangeException(nameof(atIndex), atIndex,
                $"Índice de inserção {atIndex} fora do intervalo válido (0 a {pageCount}).");
    }

    private static PdfPasswordRequiredException WrapPassword(BadPasswordException ex) =>
        new("PDF protegido por senha — não é possível editar sem a senha correta.", ex);

    /// HIPÓTESE original do rider (I4): "outro PdfException -> PdfEditingException". Reconciliado
    /// empiricamente (teste AddAnnotation_CorruptBytes_ThrowsPdfEditingException): bytes corrompidos
    /// fazem o PdfReader lançar `iText.IO.Exceptions.IOException` ("PDF header not found"), que é
    /// IRMÃ de `PdfException` (ambas derivam de `iText.Commons.Exceptions.ITextException`), não uma
    /// subclasse dela — `catch (PdfException)` não pegava esse caso. Catch alargado para
    /// `ITextException` (a raiz comum real de qualquer falha do iText), mantendo `BadPasswordException`
    /// capturada primeiro (mais específica: `BadPasswordException : PdfException : ITextException`).
    /// Mensagem (revisão pós-M11, rodada 2 — Minor 3): "corrompido ou inválido" superafirmava a causa
    /// — nem toda `ITextException` significa arquivo corrompido (podia ser um limite de memória, uma
    /// referência cruzada cíclica, etc.); "Não foi possível processar o PDF." é honesta sobre o que
    /// de fato sabemos, e a causa real sobrevive em `InnerException` para quem precisar dela.
    private static PdfEditingException WrapGeneric(ITextException ex) =>
        new("Não foi possível processar o PDF.", ex);

    /// Task 8 (Plano 3a): Ink/Rectangle/Line entraram no mapeamento (Square -> Rectangle, mesma
    /// convenção de nome já usada por Highlight/Underline/etc.). Arrow é o caso especial — o spec PDF
    /// NÃO define um subtype `/Arrow` próprio; é um `/Line` cujo `/LE` (line ending styles) termina
    /// numa seta (ver `BuildAnnotation`, que só ESCREVE `[/None /OpenArrow]`). `IsArrowLine` abaixo
    /// decide a leitura: recebe o `PdfAnnotation` inteiro (não só o subtype) porque precisa inspecionar
    /// `/LE`, por isso a assinatura ganhou o 2º parâmetro nesta task.
    private static AnnotationKind? MapKind(PdfName subtype, PdfAnnotation annot)
    {
        if (PdfName.Highlight.Equals(subtype)) return AnnotationKind.Highlight;
        if (PdfName.Underline.Equals(subtype)) return AnnotationKind.Underline;
        if (PdfName.StrikeOut.Equals(subtype)) return AnnotationKind.Strikeout;
        if (PdfName.Text.Equals(subtype)) return AnnotationKind.StickyNote;
        if (PdfName.FreeText.Equals(subtype)) return AnnotationKind.FreeText;
        if (PdfName.Ink.Equals(subtype)) return AnnotationKind.Ink;
        if (PdfName.Square.Equals(subtype)) return AnnotationKind.Rectangle;
        if (PdfName.Line.Equals(subtype)) return IsArrowLine(annot) ? AnnotationKind.Arrow : AnnotationKind.Line;
        if (PdfName.Stamp.Equals(subtype)) return AnnotationKind.ImageStamp;
        return null;
    }

    /// Um `/Line` conta como Arrow quando seu `/LE` (line ending styles, array de 2 `PdfName` — [start,
    /// end]) tem, no elemento FINAL (índice 1 — "end", onde a ponta da seta mora na convenção que
    /// `BuildAnnotation` escreve), um dos 4 estilos de seta que o spec PDF define
    /// (OpenArrow/ClosedArrow/ROpenArrow/RClosedArrow — confirmado por reflexão contra itext.kernel.dll
    /// 9.7.0, ver task-8-report.md). v1 desta task só ESCREVE `OpenArrow`, mas o LEITOR reconhece os 4
    /// pra não overfit no único par que este módulo produz — um PDF de origem EXTERNA (ex.: Acrobat)
    /// com `ClosedArrow` também deve voltar como Arrow, não Line.
    private static bool IsArrowLine(PdfAnnotation annot)
    {
        if (annot is not PdfLineAnnotation line) return false;
        var le = line.GetLineEndingStyles();
        if (le is null || le.Size() == 0) return false;
        var end = le.Size() > 1 ? le.Get(1) : le.Get(0);
        return PdfName.OpenArrow.Equals(end) || PdfName.ClosedArrow.Equals(end)
            || PdfName.ROpenArrow.Equals(end) || PdfName.RClosedArrow.Equals(end);
    }

    /// `// HIPÓTESE:` do brief da Task 6 — "CreateUnderline/CreateStrikeout, mesma mecânica de
    /// QuadPoints de CreateHighLight" — reconciliada por leitura do XML doc de itext.kernel.dll 9.7.0
    /// (NuGet cache): as 3 fábricas estáticas de `PdfTextMarkupAnnotation` têm a MESMA assinatura
    /// `(Rectangle, float[])` — `CreateHighLight`, `CreateUnderline`, `CreateStrikeout` — só o subtype
    /// PDF resultante muda (`/Highlight`, `/Underline`, `/StrikeOut`).
    ///
    /// Task 7 (Plano 3a) ampliou este método (renomeado de `BuildMarkupAnnotation`) para StickyNote/
    /// FreeText — `// HIPÓTESE:` do brief ("PdfTextAnnotation — SetContents/popup"; "PdfFreeTextAnnotation
    /// — appearance/DefaultAppearance"), reconciliada por REFLEXÃO em runtime contra itext.kernel.dll
    /// 9.7.0 (não só XML doc desta vez — ver task-7-report.md): as 3 classes (`PdfTextMarkupAnnotation`,
    /// `PdfTextAnnotation`, `PdfFreeTextAnnotation`) derivam TODAS de `PdfMarkupAnnotation` (confirmado
    /// via `Type.BaseType`), então o tipo de retorno pôde alargar de `PdfTextMarkupAnnotation` para
    /// `PdfMarkupAnnotation` sem perder `SetColor`/`SetName`/`SetContents`/`SetTitle` (todos herdados) —
    /// o resto de AddAnnotation (bbox, cor, /NM, /T) continua idêntico pros 5 kinds.
    /// `PdfTextAnnotation` (StickyNote): ctor `(Rectangle)` só — sem Quads, sem contents no ctor (mesmo
    /// padrão de Highlight: Content é setado depois, só se não-nulo).
    /// `PdfFreeTextAnnotation` (FreeText): ctor `(Rectangle, PdfString contents)` EXIGE conteúdo — ao
    /// contrário dos outros 4 kinds, não há overload sem `contents`. Resíduo aceito (documentado, não
    /// testado): `Content == null` vira uma string VAZIA no /Contents (não a ausência do campo que os
    /// outros kinds preservam) — nenhum teste depende de `Content` nulo para FreeText especificamente.
    /// Task 8 (Plano 3a) ampliou o switch para Ink/Rectangle/Line/Arrow — `// HIPÓTESE:` do brief
    /// ("InkList: array de arrays [x1 y1 x2 y2...]"; "PdfSquareAnnotation"; "PdfLineAnnotation.SetLine?";
    /// "Arrow = Line + line-ending OpenArrow") reconciliada EMPIRICAMENTE (não só XML doc, que não
    /// documenta o ctor de 2 argumentos de `PdfInkAnnotation` — ver task-8-report.md): escrevi um PDF
    /// real com os 4 kinds e reli o dicionário CRU. `PdfInkAnnotation(Rectangle, PdfArray)` grava o
    /// `PdfArray` recebido DIRETO como `/InkList`, sem transformação — então PRECISA já estar no formato
    /// do spec (array de arrays, um sub-array de floats intercalados x,y por traço), ver `BuildInkList`.
    /// `PdfSquareAnnotation(Rectangle)`: geometria é só o `/Rect`, sem array próprio (confirmado: o
    /// dicionário resultante não tem NENHUMA chave além de `/Rect`/`/Subtype`/`/NM`) — por isso Rectangle
    /// não precisou de campo novo no contrato (`AnnotationData` já tem Left/Bottom/Right/Top). Não há
    /// `SetLine` em `PdfLineAnnotation` — o ctor `(Rectangle, float[4] {x1,y1,x2,y2})` já recebe a linha
    /// (não existe overload sem ela, ao contrário de Highlight/StickyNote). Arrow reusa o MESMO ctor de
    /// Line, só acrescentando `SetLineEndingStyles([None, OpenArrow])` — confirmado que isso grava
    /// `/LE [/None /OpenArrow]` sem alterar `/L`.
    private static PdfMarkupAnnotation BuildAnnotation(AnnotationData a, Rectangle bbox) =>
        a.Kind switch
        {
            AnnotationKind.Highlight => PdfTextMarkupAnnotation.CreateHighLight(bbox, BuildQuadPoints(a)),
            AnnotationKind.Underline => PdfTextMarkupAnnotation.CreateUnderline(bbox, BuildQuadPoints(a)),
            AnnotationKind.Strikeout => PdfTextMarkupAnnotation.CreateStrikeout(bbox, BuildQuadPoints(a)),
            AnnotationKind.StickyNote => new PdfTextAnnotation(bbox),
            AnnotationKind.FreeText => new PdfFreeTextAnnotation(bbox, new PdfString(a.Content ?? string.Empty)),
            AnnotationKind.Ink => new PdfInkAnnotation(bbox, BuildInkList(a)),
            AnnotationKind.Rectangle => new PdfSquareAnnotation(bbox),
            AnnotationKind.Line => new PdfLineAnnotation(bbox, BuildLineArray(a)),
            AnnotationKind.Arrow => BuildArrowAnnotation(bbox, a),
            // ImageStamp (Task 9, Plano 3a): `// HIPÓTESE:` do brief ("PdfStampAnnotation com uma
            // aparência de imagem") reconciliada EMPIRICAMENTE via probe project isolado (referenciando
            // itext 9.7.0 direto, mesmo método da Task 8 — ver task-9-report.md): `PdfStampAnnotation
            // (Rectangle)` É um ctor real (não documentado no XML doc — mesmo padrão do ctor de 2 args
            // de PdfInkAnnotation na Task 8), cria uma anotação NOVA sem nenhum ícone padrão (`/Name`
            // nunca é setado — este módulo nunca usa `SetIconName`). A aparência da IMAGEM em si é
            // escrita GRAVANDO uma appearance stream custom logo abaixo (bloco `if (markup is
            // PdfStampAnnotation stamp)`), não pelo construtor.
            AnnotationKind.ImageStamp => new PdfStampAnnotation(bbox),
            _ => throw new NotSupportedException(
                $"BuildAnnotation: tipo '{a.Kind}' sem fábrica de anotação — a checagem em " +
                "AddAnnotation deveria ter recusado este kind antes de chegar aqui."),
        };

    /// `a.InkStrokes` já foi validado NÃO-nulo/NÃO-vazio no topo de AddAnnotation — o `!` aqui é seguro
    /// por construção, não uma suposição nova. Um sub-array por traço, floats intercalados x,y (mesmo
    /// formato que `ReadInkStrokes` decodifica de volta).
    private static PdfArray BuildInkList(AnnotationData a)
    {
        var inkList = new PdfArray();
        foreach (var stroke in a.InkStrokes!)
        {
            var floats = new float[stroke.Count * 2];
            for (int i = 0; i < stroke.Count; i++)
            {
                floats[i * 2] = (float)stroke[i].XPt;
                floats[i * 2 + 1] = (float)stroke[i].YPt;
            }
            inkList.Add(new PdfArray(floats));
        }
        return inkList;
    }

    /// `a.LineStartPt`/`a.LineEndPt` já validados NÃO-nulos no topo de AddAnnotation p/ Kind Line/Arrow.
    private static float[] BuildLineArray(AnnotationData a)
    {
        var start = a.LineStartPt!.Value;
        var end = a.LineEndPt!.Value;
        return new[] { (float)start.XPt, (float)start.YPt, (float)end.XPt, (float)end.YPt };
    }

    private static PdfLineAnnotation BuildArrowAnnotation(Rectangle bbox, AnnotationData a)
    {
        var line = new PdfLineAnnotation(bbox, BuildLineArray(a));
        var le = new PdfArray();
        le.Add(PdfName.None);
        le.Add(PdfName.OpenArrow);
        line.SetLineEndingStyles(le);
        return line;
    }

    /// HIPÓTESE reconciliada por reflexão contra itext.kernel.dll 9.7.0: QuadPoints de markup
    /// annotation é um float[8*N] (N quads), ordem por quad = 4 pontos (x,y). A convenção documentada
    /// do PDF é TL,TR,BL,BR a partir do vértice superior-esquerdo. A ordem exata não é crítica aqui:
    /// ReadQuads reconstrói o quad via min/max dos 4 pontos (bounding box), então qualquer permutação
    /// dos 4 vértices dá o mesmo round-trip — confirmado pelo teste AddAnnotation_Highlight_RoundTrips
    /// (1 quad) e AddAnnotation_MultipleQuads_RoundTrips (2 quads, pós-M6).
    private static float[] BuildQuadPoints(AnnotationData a)
    {
        var quads = a.Quads is { Count: > 0 } q
            ? q
            : new[] { new PdfQuad(a.LeftPt, a.BottomPt, a.RightPt, a.TopPt) };

        var points = new List<float>(quads.Count * 8);
        foreach (var quad in quads)
        {
            points.Add((float)quad.LeftPt); points.Add((float)quad.TopPt);     // TL
            points.Add((float)quad.RightPt); points.Add((float)quad.TopPt);    // TR
            points.Add((float)quad.LeftPt); points.Add((float)quad.BottomPt);  // BL
            points.Add((float)quad.RightPt); points.Add((float)quad.BottomPt); // BR
        }
        return points.ToArray();
    }

    private static IReadOnlyList<PdfQuad>? ReadQuads(PdfAnnotation annot)
    {
        if (annot is not PdfTextMarkupAnnotation markup) return null;
        var qp = markup.GetQuadPoints();
        if (qp is null || qp.Size() == 0) return null;

        var floats = qp.ToFloatArray();
        var quads = new List<PdfQuad>();
        for (int i = 0; i + 7 < floats.Length; i += 8)
        {
            double xMin = Math.Min(Math.Min(floats[i], floats[i + 2]), Math.Min(floats[i + 4], floats[i + 6]));
            double xMax = Math.Max(Math.Max(floats[i], floats[i + 2]), Math.Max(floats[i + 4], floats[i + 6]));
            double yMin = Math.Min(Math.Min(floats[i + 1], floats[i + 3]), Math.Min(floats[i + 5], floats[i + 7]));
            double yMax = Math.Max(Math.Max(floats[i + 1], floats[i + 3]), Math.Max(floats[i + 5], floats[i + 7]));
            quads.Add(new PdfQuad(xMin, yMin, xMax, yMax));
        }
        return quads;
    }

    /// Task 8 (Plano 3a): não há um getter dedicado pra `/InkList` em `PdfInkAnnotation` (confirmado —
    /// 0 ocorrências de "InkList" no XML doc de itext.kernel.dll 9.7.0, ao contrário de QuadPoints
    /// acima) — lê o array CRU via `GetPdfObject()` (herdado de `PdfObjectWrapper`, público), mesmo
    /// espírito de `ReadLineEndpoints` abaixo. Formato espelha exatamente `BuildInkList`: um sub-array
    /// de floats intercalados x,y por traço.
    private static IReadOnlyList<IReadOnlyList<PdfPoint>>? ReadInkStrokes(PdfAnnotation annot)
    {
        if (annot is not PdfInkAnnotation) return null;
        var inkList = annot.GetPdfObject().GetAsArray(PdfName.InkList);
        if (inkList is null || inkList.Size() == 0) return null;

        var strokes = new List<IReadOnlyList<PdfPoint>>();
        for (int i = 0; i < inkList.Size(); i++)
        {
            var strokeArr = inkList.GetAsArray(i);
            if (strokeArr is null) continue;
            var floats = strokeArr.ToFloatArray();
            var pts = new List<PdfPoint>(floats.Length / 2);
            for (int j = 0; j + 1 < floats.Length; j += 2)
                pts.Add(new PdfPoint(floats[j], floats[j + 1]));
            strokes.Add(pts);
        }
        return strokes;
    }

    /// Task 8 (Plano 3a): `/L` de `PdfLineAnnotation` (via `GetLine()`, que ESTE tem — ao contrário de
    /// InkList acima) é `[x1 y1 x2 y2]`. Serve Line E Arrow (Arrow também é um `PdfLineAnnotation` por
    /// baixo — ver `IsArrowLine`).
    private static (PdfPoint Start, PdfPoint End)? ReadLineEndpoints(PdfAnnotation annot)
    {
        if (annot is not PdfLineAnnotation line) return null;
        var l = line.GetLine();
        if (l is null || l.Size() < 4) return null;
        var f = l.ToFloatArray();
        return (new PdfPoint(f[0], f[1]), new PdfPoint(f[2], f[3]));
    }

    private static (int r, int g, int b) ArgbToRgb(uint argb) =>
        ((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));

    /// Sentinela (pós-M7): `null` quando `/C` está ausente OU vazio (0 componentes — spec PDF usa
    /// array vazio para "sem cor"/transparente), em vez do preto opaco presumido antes da revisão.
    /// Só RGB (3 componentes) é interpretado; Gray (1) e CMYK (4) também caem em `null` por ora —
    /// fora do escopo desta task (só Highlight/RGB é escrito por este módulo).
    private static uint? ReadColorArgb(PdfAnnotation annot)
    {
        var color = annot.GetColorObject();
        if (color is null || color.Size() != 3) return null;
        var f = color.ToFloatArray();
        byte r = (byte)Math.Round(Math.Clamp(f[0], 0f, 1f) * 255);
        byte g = (byte)Math.Round(Math.Clamp(f[1], 0f, 1f) * 255);
        byte b = (byte)Math.Round(Math.Clamp(f[2], 0f, 1f) * 255);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    // --- Task 1 (Plano 3c): motor de formulários AcroForm ---------------------------------------
    // Decisões de contrato (gate por operação, HIPÓTESE + reconciliação empírica) registradas no XML
    // doc de IPdfEditor em Contract.cs — não repetidas aqui.

    /// Leitura pura, sem gate (ver Contract.cs). `GetAcroForm(doc, false)` nulo (documento sem
    /// AcroForm nenhum) -> lista vazia, mesmo padrão de `ReadOutline`/`GetOutlines(false)` nulo.
    public IReadOnlyList<FormFieldData> ReadFormFields(byte[] pdf)
    {
        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            var acroForm = PdfAcroForm.GetAcroForm(doc, false);
            if (acroForm is null) return Array.Empty<FormFieldData>();

            var result = new List<FormFieldData>();
            foreach (var pair in acroForm.GetAllFormFields())
                result.Add(BuildFormFieldData(pair.Key, pair.Value, doc));
            return result;
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
    }

    /// Detector de PRESENÇA da chave `/XFA` no dicionário CRU do AcroForm — NUNCA instancia
    /// `PdfAcroForm` (ver ACHADO EMPÍRICO em Contract.cs: `PdfAcroForm.GetAcroForm` relendo um `/XFA`
    /// mesmo dummy LANÇA, porque tenta parsear como XML incondicionalmente). Leitura pura, sem gate.
    public bool HasXfa(byte[] pdf)
    {
        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            return HasXfaKey(doc); // extraído (Important 2, revisão) — mesmo detector usado por CountSignatures
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
    }

    public byte[] SetFormFields(byte[] pdf, IReadOnlyDictionary<string, string> values)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                GuardAgainstSignedDocument(doc);
                var acroForm = PdfAcroForm.GetAcroForm(doc, false);

                // TODAS as entradas validadas (existe + não é readonly + valor pertence às opções pra
                // Radio/Combo/ListBox) ANTES de escrever QUALQUER campo — mesmo espírito de
                // RotatePages/DeletePages/SplitByRanges (nunca aplica metade de um pedido).
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
                    // Review (Task 1 fix): FormFieldType.Other cobre push button e campo de
                    // assinatura — os 2 são ALCANÇÁVEIS aqui (nenhum outro guard os bloqueia antes:
                    // um botão não é readonly por padrão, e um placeholder de assinatura AINDA NÃO
                    // assinado não tem `/V` de assinatura real, então `HasSignatures`/o gate de
                    // documento assinado não pega — sonda ao vivo confirmou os 2, ver task-1-report.md
                    // "## Fix"). Sem esta recusa, `field.SetValue(string)` (herdado de PdfFormField)
                    // NÃO lança pra nenhum dos 2: no push button o valor é silenciosamente descartado
                    // (`GetValueAsString()` continua vazio depois — perda silenciosa, sem sinal pro
                    // chamador); no campo de assinatura a string crua é gravada em `/V` como
                    // `PdfString` — um `/V` poluído nesse campo é um risco real porque o Plano 4 vai
                    // ASSINAR esses mesmos placeholders depois (a chave que deveria só existir com
                    // conteúdo de assinatura de verdade, nunca com lixo de formulário).
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

                // SetValue(string) de 1 argumento regenera a aparência por default (sonda ao vivo —
                // ver Contract.cs) — nenhum SetGenerateAppearance explícito é necessário aqui.
                foreach (var (field, value) in toSet)
                    field.SetValue(value);
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    /// `FlattenFields()` (sem argumentos) — sonda ao vivo confirmou que remove o AcroForm inteiro E
    /// os widgets das páginas quando não sobra nenhum campo (ver Contract.cs). Documento sem AcroForm
    /// -> no-op (mesmo espírito de StripSignatures), nunca lança.
    public byte[] FlattenForm(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                GuardAgainstSignedDocument(doc);
                var acroForm = PdfAcroForm.GetAcroForm(doc, false);
                acroForm?.FlattenFields();
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    /// Checkbox E radio são AMBOS `PdfButtonFormField` (`/FT /Btn`) — distinguidos por
    /// `IsRadio()`/`IsPushButton()` (nenhum dos 2 -> Checkbox, ver reconciliação em Contract.cs);
    /// Combo/ListBox são AMBOS `PdfChoiceFormField` (`/FT /Ch`) — distinguidos por `IsCombo()`.
    /// `PdfSignatureFormField` e qualquer outro tipo não reconhecido caem em `Other`.
    private static FormFieldType MapFormFieldType(PdfFormField field) => field switch
    {
        PdfTextFormField => FormFieldType.Text,
        PdfChoiceFormField choice => choice.IsCombo() ? FormFieldType.Combo : FormFieldType.ListBox,
        PdfButtonFormField button => button.IsRadio() ? FormFieldType.Radio
            : button.IsPushButton() ? FormFieldType.Other
            : FormFieldType.Checkbox,
        _ => FormFieldType.Other,
    };

    /// Ver XML doc de `FormFieldData.Options` em Contract.cs: Combo/ListBox -> valores do menu (na
    /// ordem do PDF); Checkbox/Radio -> estados "on" possíveis, SEM "Off"; Text/Other -> vazia.
    /// `GetAppearanceStates()` no campo PAI já devolve a UNIÃO dos estados de TODOS os widgets-filho
    /// pra Radio (sonda ao vivo — ver Contract.cs), não precisa iterar `GetKids()` manualmente.
    private static IReadOnlyList<string> ReadOptions(PdfFormField field, FormFieldType type)
    {
        if (type is FormFieldType.Combo or FormFieldType.ListBox && field is PdfChoiceFormField choice)
        {
            var arr = choice.GetOptions();
            if (arr is null) return Array.Empty<string>();
            var list = new List<string>(arr.Size());
            for (int i = 0; i < arr.Size(); i++)
            {
                // Cada opção pode ser um PdfString simples OU um PdfArray [exportValue, displayText]
                // (a forma que `ChoiceFormFieldBuilder.SetOptions(string[][])` produziria) — este
                // módulo só ESCREVE a forma simples (flat), mas a LEITURA trata as 2 formas pra não
                // quebrar num PDF de origem externa com opções [valor, rótulo]: usa o PRIMEIRO
                // elemento (export value) nesse caso.
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

    /// `WidgetRect`/`PageIndex`: retângulo e página do PRIMEIRO widget do campo (`GetWidgets()[0]`),
    /// no frame NÃO-ROTACIONADO — ver decisão de contrato em `FormFieldData.WidgetRect`
    /// (Contract.cs). `GetWidgets()` cobre tanto o caso "campo terminal = o próprio widget" (Text/
    /// Checkbox/Combo — `GetKids()` nulo) quanto "campo com kids-widget" (Radio — `GetKids()` tem os
    /// botões) numa só chamada, mesmo pra Radio onde `GetKids()` sozinho não bastaria.
    private static FormFieldData BuildFormFieldData(string name, PdfFormField field, PdfDocument doc)
    {
        var type = MapFormFieldType(field);
        var options = ReadOptions(field, type);

        var widgets = field.GetWidgets();
        PdfQuad? widgetRect = null;
        int pageIndex = 0;
        if (widgets is { Count: > 0 })
        {
            var widget = widgets[0];
            var rect = widget.GetRectangle()?.ToRectangle();
            if (rect is not null)
                widgetRect = new PdfQuad(rect.GetLeft(), rect.GetBottom(), rect.GetRight(), rect.GetTop());
            var page = widget.GetPage();
            if (page is not null)
            {
                int pageNumber = doc.GetPageNumber(page);
                if (pageNumber > 0) pageIndex = pageNumber - 1;
            }
        }

        return new FormFieldData(name, type, field.GetValueAsString(), options, pageIndex, widgetRect, field.IsReadOnly());
    }

    // --- Task 1 (Plano 7): motor ImageToPdf ------------------------------------------------------
    // Decisões de contrato (arquitetura, HIPÓTESE + reconciliação empírica via probe project) registradas
    // no XML doc de IPdfEditor.ImageToPdf/IsSupportedImage em Contract.cs — não repetidas aqui.

    /// Mensagem COMPARTILHADA (decisão do brief) pra "não é um JPG/PNG utilizável" — cobre magic bytes
    /// desconhecidos (BMP/GIF/etc.) E bytes corrompidos com magic bytes válidos que o iText não
    /// decodifica: o usuário não precisa saber a causa exata, só o que fazer (usar JPG ou PNG).
    private const string UnsupportedImageMessage = "Formato de imagem não suportado. Use JPG ou PNG.";

    /// Teto de pixels suportado por `ImageToPdf` — ver justificativa MEDIDA no comentário do call site.
    private const long MaxImagePixels = 50_000_000; // 50 megapixels

    private static PdfEditingException OversizedImageException() => new(
        $"Imagem excede o limite de {MaxImagePixels / 1_000_000}MP suportado. Use uma imagem menor.");

    /// Sniff por magic bytes — ver XML doc em Contract.cs. Puro, sem I/O, sem iText.
    public bool IsSupportedImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4) return false;
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return true; // JPEG: SOI
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true; // PNG
        return false;
    }

    /// Task 3 (Plano 7) — ver XML doc em Contract.cs. Reusa TryReadJpegSofInfo/TryReadPngDimensions
    /// (mesmos parsers overflow-safe de ImageToPdf, NUNCA reimplementados aqui) — nenhuma mudança no
    /// código de ImageToPdf em si (zero risco de regressão no teto já revisado/hardenizado lá).
    public bool IsWithinImagePixelLimit(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 4) return true; // sem opinião — IsSupportedImage decide formato
        bool isJpeg = bytes[0] == 0xFF && bytes[1] == 0xD8;
        if (isJpeg)
        {
            // SOF ilegível/truncado -> sem opinião (mesmo fail-open de ImageToPdf: segue pro decode real).
            return !TryReadJpegSofInfo(bytes, out int width, out int height, out _) || (long)width * height <= MaxImagePixels;
        }
        if (TryReadPngDimensions(bytes, out long pngWidth, out long pngHeight))
            // Checagem por DIMENSÃO INDIVIDUAL antes do produto — mesma proteção contra overflow de 32
            // bits (C3, task-1-report.md) que ImageToPdf já aplica pro mesmo par de valores.
            return pngWidth <= MaxImagePixels && pngHeight <= MaxImagePixels && pngWidth * pngHeight <= MaxImagePixels;
        return true; // IHDR ilegível/truncado -> sem opinião
    }

    /// Task 3 (Plano 7) — ver XML doc em Contract.cs. Reusa ReadJpegExifOrientationRotation (mesmo
    /// parser TIFF/IFD0 de ImageToPdf, nunca reimplementado aqui).
    public int ReadJpegExifOrientation(byte[] image) =>
        image is { Length: >= 2 } && image[0] == 0xFF && image[1] == 0xD8
            ? ReadJpegExifOrientationRotation(image)
            : 0;

    public byte[] ImageToPdf(byte[] image)
    {
        if (!IsSupportedImage(image))
            throw new PdfEditingException(UnsupportedImageMessage);

        bool isJpeg = image[0] == 0xFF && image[1] == 0xD8;

        // C2 (revisão pré-merge, task-1-report.md "## Fix"): CMYK e teto de pixels são decididos por
        // VARREDURA DE HEADER (sem iText, sem decodificar) ANTES de `ImageDataFactory.Create` — medido
        // que o decode (sobretudo PNG com alpha, que o iText separa em SMask) já é o trabalho CARO que
        // o teto existe pra evitar; checar DEPOIS de já ter pago esse custo derrota o propósito.
        //
        // JPEG: `sofWidth`/`sofHeight` vêm de campos de 2 bytes do SOF (0..65535 cada, ver
        // `TryReadJpegSofInfo`) — o PRODUTO máximo possível é 65535*65535 ≈ 4.29e9, MUITO abaixo de
        // `long.MaxValue` (~9.22e18): estruturalmente IMUNE ao overflow que atingiu o ramo PNG abaixo
        // (não precisa do mesmo tratamento — não há combinação de bytes de SOF que overflowe `long`).
        if (isJpeg)
        {
            if (TryReadJpegSofInfo(image, out int sofWidth, out int sofHeight, out int sofComponents))
            {
                if (sofComponents == 4)
                    throw new PdfEditingException("JPEG CMYK não é suportado.");
                if ((long)sofWidth * sofHeight > MaxImagePixels)
                    throw OversizedImageException();
            }
            // SOF ilegível/truncado: segue pro Create() abaixo — recusa por corrupção, ou (se decodificar
            // mesmo assim) cai na rede de segurança pós-Create abaixo.
        }
        else if (TryReadPngDimensions(image, out long pngWidth, out long pngHeight))
        {
            // C3 (revisão pós-merge, task-1-report.md "## Fix" — CRÍTICO, achado real do revisor): PNG
            // IHDR é campo de 4 bytes cada (width/height até 0xFFFFFFFF = 4294967295) — o produto de
            // dois valores nesse tamanho (~1.84e19) EXCEDE `long.MaxValue` (~9.22e18) e OVERFLOWA pra
            // NEGATIVO em aritmética `long` padrão (unchecked); `pngWidth*pngHeight > MaxImagePixels`
            // comparado contra um produto negativo nunca dispara — um PNG hostil com as duas dimensões
            // no máximo de 32 bits atravessava o teto inteiro. Fix: comparar CADA dimensão
            // INDIVIDUALMENTE contra o teto ANTES de multiplicar — por construção, se os dois checks
            // individuais passam (nenhuma dimensão sozinha excede `MaxImagePixels`=50_000_000), o
            // produto máximo possível é 50_000_000² = 2.5e15, muito abaixo de `long.MaxValue`, então a
            // multiplicação seguinte NUNCA overflowa.
            if (pngWidth > MaxImagePixels || pngHeight > MaxImagePixels || pngWidth * pngHeight > MaxImagePixels)
                throw OversizedImageException();
        }

        ImageData imageData;
        try { imageData = ImageDataFactory.Create(image); }
        catch (ITextException ex) { throw new PdfEditingException(UnsupportedImageMessage, ex); }

        // C3 (revisão pós-merge — mesmo achado acima): um PNG com IHDR 0xFFFFFFFF×0xFFFFFFFF SEM IDAT
        // sobrevive à checagem de header acima (bloqueada pelo overflow) E o iText devolve `GetWidth()/
        // GetHeight() == -1` pra essa imagem degenerada (lê o campo de 4 bytes como `int` SINALIZADO —
        // 0xFFFFFFFF vira -1) — sem esta checagem, `pixelCount = (-1)*(-1) = 1` passaria a rede de
        // segurança abaixo TAMBÉM, produzindo silenciosamente um PDF com `/MediaBox [0 0 -0.75 -0.75]`
        // (dimensões NEGATIVAS, documento inválido). Dimensão não-positiva nunca é uma imagem válida —
        // recusa tipada ANTES de calcular qualquer coisa a partir dela.
        if (imageData.GetWidth() <= 0 || imageData.GetHeight() <= 0)
            throw new PdfEditingException("Imagem inválida (dimensões inconsistentes).");

        // Rede de segurança (defesa em profundidade — mesmo espírito do StripSignatures "defensivo" em
        // ExtractPages/MergeDocuments): só dispara se a varredura de header acima não conseguiu ler as
        // dimensões (arquivo malformado que o iText ainda assim decodificou).
        long pixelCount = (long)imageData.GetWidth() * (long)imageData.GetHeight();
        if (pixelCount > MaxImagePixels)
            throw OversizedImageException();

        double dpiX = imageData.GetDpiX() > 0 ? imageData.GetDpiX() : 96;
        double dpiY = imageData.GetDpiY() > 0 ? imageData.GetDpiY() : 96;
        double rawWidthPt = imageData.GetWidth() * 72.0 / dpiX;
        double rawHeightPt = imageData.GetHeight() * 72.0 / dpiY;

        // Minor (revisão pré-merge, task-1-report.md "## Fix"): PISO de tamanho de página — achado
        // REAL do teste `ImageToPdf_OnePixelImage_...` (imagem 1x1 @ fallback 96dpi = 0.75x0.75pt):
        // `Docnet.Core.Readers.IPageReader.GetPageWidth()/GetPageHeight()` (confirmado via reflexão
        // sobre Docnet.Core.dll) devolvem `int` — um MediaBox de 0.75pt, escrito CORRETAMENTE pelo
        // iText (confirmado inspecionando o PDF bruto: `/MediaBox[0 0 0.75 0.75]`), TRUNCA pra 0 no
        // motor de render que este app usa de verdade (`mPdf.Rendering`/PDFium), produzindo uma
        // página tecnicamente válida mas INUTILIZÁVEL (invisível/degenerada em qualquer tela do app).
        // `MinPageDimensionPt` escala as 2 dimensões PROPORCIONALMENTE (nunca distorce, nunca reduz)
        // só o suficiente pra garantir que nenhuma fique abaixo do que o motor consegue reportar —
        // margem de 2pt (não 1pt cravado) contra imprecisão de ponto flutuante na trilha DPI->pt.
        const double MinPageDimensionPt = 2.0;
        double growScale = Math.Max(1.0, Math.Max(MinPageDimensionPt / rawWidthPt, MinPageDimensionPt / rawHeightPt));
        if (growScale > 1.0) { rawWidthPt *= growScale; rawHeightPt *= growScale; }

        int rotation = isJpeg ? ReadJpegExifOrientationRotation(image) : 0;

        // I3 (revisão pré-merge, task-1-report.md "## Fix" — "o /Rotate collision"): a página do PDF
        // resultante NUNCA gira (/Rotate fica 0 SEMPRE) — GetPageRotations(resultado)[0] == 0 é
        // invariante ASSERTADO por teste. Girar a página (abordagem anterior, `PdfPage.SetRotation`)
        // fazia o gate de rotação do Plano 3b (ver XML doc de `GetPageRotations`/`IPdfEditor` em
        // Contract.cs: "interação de anotação fica DESLIGADA em página girada") bloquear anotação/
        // assinatura em QUALQUER foto de celular convertida com EXIF != 1 — quebrando exatamente o
        // fluxo "foto do WhatsApp -> assinar" que este motor existe pra habilitar. Fix por CONSTRUÇÃO:
        // a correção de EXIF é aplicada via MATRIZ DE TRANSFORMAÇÃO no desenho da imagem
        // (`PdfCanvas.ConcatMatrix`), nunca via `PdfPage.SetRotation` — ver `ComputeRotationMatrix` pra
        // derivação explícita. A página nasce DIRETO no tamanho CORRIGIDO (upright); só o desenho da
        // imagem dentro dela é rotacionado.
        bool swapDims = rotation is 90 or 270;
        double correctedWidthPt = swapDims ? rawHeightPt : rawWidthPt;
        double correctedHeightPt = swapDims ? rawWidthPt : rawHeightPt;

        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfWriter(output)))
            {
                var page = doc.AddNewPage(new PageSize((float)correctedWidthPt, (float)correctedHeightPt));
                var canvas = new PdfCanvas(page);
                var localRect = new Rectangle(0, 0, (float)rawWidthPt, (float)rawHeightPt);
                if (rotation == 0)
                {
                    canvas.AddImageFittedIntoRectangle(imageData, localRect, false);
                }
                else
                {
                    var m = ComputeRotationMatrix(rotation, rawWidthPt, rawHeightPt);
                    canvas.SaveState();
                    canvas.ConcatMatrix(m.A, m.B, m.C, m.D, m.E, m.F);
                    canvas.AddImageFittedIntoRectangle(imageData, localRect, false);
                    canvas.RestoreState();
                }
            }
        }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    /// Matriz `[a b c d e f]` (convenção PDF/`PdfCanvas.ConcatMatrix` — confirmado via XML doc de
    /// itext.kernel.dll 9.7.0: `x' = a*x + c*y + e`, `y' = b*x + d*y + f`) que rotaciona `rotation`
    /// graus HORÁRIOS o retângulo LOCAL (0,0)-(w0,h0) — onde `w0`/`h0` = `rawWidthPt`/`rawHeightPt`,
    /// o tamanho NATURAL (pré-correção) da imagem, o mesmo retângulo que `AddImageFittedIntoRectangle`
    /// sempre recebeu — e traduz o resultado pra caber exatamente em (0,0)-(w0,h0) [180°] ou
    /// (0,0)-(h0,w0) [90°/270°, dimensões trocadas]. Derivação (mapeamento dos 4 cantos do retângulo
    /// local pro retângulo final, espaço PDF y-para-cima):
    ///   90°:  (x,y) -> (y, w0-x)     => a=0  b=-1 c=1  d=0  e=0  f=w0   (span final: x'∈[0,h0] y'∈[0,w0])
    ///   180°: (x,y) -> (w0-x, h0-y)  => a=-1 b=0  c=0  d=-1 e=w0 f=h0   (span final: x'∈[0,w0] y'∈[0,h0])
    ///   270°: (x,y) -> (h0-y, x)     => a=0  b=1  c=-1 d=0  e=h0 f=0    (span final: x'∈[0,h0] y'∈[0,w0])
    /// Verificado empiricamente (mesma fixture Orientation=6 usada pra reconciliar a HIPÓTESE original,
    /// ver Contract.cs): o render via PDFium desta matriz bate cor-a-cor, pixel a pixel, com o que
    /// `PdfPage.SetRotation` produzia antes do fix I3 — só que agora `/Rotate` da página fica 0.
    private static (double A, double B, double C, double D, double E, double F) ComputeRotationMatrix(
        int rotation, double w0, double h0) => rotation switch
    {
        90 => (0, -1, 1, 0, 0, w0),
        180 => (-1, 0, 0, -1, w0, h0),
        270 => (0, 1, -1, 0, h0, 0),
        _ => (1, 0, 0, 1, 0, 0), // 0° — ImageToPdf nunca chama ComputeRotationMatrix pra este caso; identidade defensiva
    };

    /// EXIF Orientation (tag 0x0112) de um JPEG — parser COMPACTO do segmento APP1/TIFF, byte a byte,
    /// sem pacote novo (achado empírico via probe, ver Contract.cs: iText não lê este tag sozinho).
    /// Devolve os graus de rotação HORÁRIA que `ComputeRotationMatrix` precisa aplicar pra corrigir:
    /// Orientation 1 (normal) ou ausente -> 0; 3 -> 180; 6 -> 90; 8 -> 270. Valores com espelhamento
    /// (2/4/5/7) -> 0 (residual documentado, fora do escopo v1 — o brief só exige os 4 casos de
    /// rotação pura, que cobrem o cenário real de foto de celular). Qualquer segmento malformado
    /// devolve 0 (defesa em profundidade — nunca lança; um EXIF quebrado não pode derrubar a
    /// conversão inteira, mesmo espírito de `ResolvePageIndex` em ReadOutline).
    private static int ReadJpegExifOrientationRotation(byte[] jpeg)
    {
        int i = 2; // após SOI (FF D8)
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            byte marker = jpeg[i + 1];
            if (marker == 0xD9 || marker == 0xDA) break; // EOI ou SOS (dados de scan) — EXIF sempre vem antes
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; } // TEM/RSTn: sem tamanho
            // (a checagem `i+4>jpeg.Length` que existia aqui era CÓDIGO MORTO — revisão pré-merge: a
            // condição do `while` acima já garante `i+4<=jpeg.Length` neste ponto, sempre; removida.)
            int segLen = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (segLen < 2) break; // segmento malformado — nunca deveria acontecer num JPEG válido
            if (marker == 0xE1) // APP1 — candidato a EXIF ("Exif\0\0" + TIFF)
            {
                int payloadStart = i + 4;
                if (payloadStart + 6 <= jpeg.Length &&
                    jpeg[payloadStart] == 'E' && jpeg[payloadStart + 1] == 'x' && jpeg[payloadStart + 2] == 'i' &&
                    jpeg[payloadStart + 3] == 'f' && jpeg[payloadStart + 4] == 0 && jpeg[payloadStart + 5] == 0)
                {
                    int rotation = ParseTiffOrientationRotation(jpeg, payloadStart + 6);
                    if (rotation != 0) return rotation;
                }
            }
            i += 2 + segLen;
        }
        return 0;
    }

    /// TIFF/IFD0 dentro do payload EXIF — procura o tag Orientation (0x0112, tipo SHORT) e devolve a
    /// rotação horária correspondente (ver mapeamento em `ReadJpegExifOrientationRotation`).
    ///
    /// C1 (revisão pré-merge, task-1-report.md "## Fix" — CRÍTICO, achado real de fuzzing): o offset
    /// de IFD0 (`ifd0Offset`) vem de 4 bytes NÃO CONFIÁVEIS do arquivo — qualquer valor de 32 bits é
    /// "válido" sintaticamente. A versão anterior fazia `int ifd0Start = tiffStart + ifd0Offset;` em
    /// aritmética `int`: um `ifd0Offset` grande o bastante (próximo de `int.MaxValue`) faz
    /// `ifd0Start` OVERFLOWAR pra um valor ainda positivo e pequeno (passa despercebido pelo guard
    /// `ifd0Start < 0`), mas a checagem SEGUINTE (`ifd0Start + 2 > jpeg.Length`) overflowava DE NOVO
    /// pra um número bem negativo — `negativo > jpeg.Length` é sempre falso, então os DOIS guards
    /// davam falso positivo de "dentro dos limites" e `jpeg[ifd0Start]` lançava `IndexOutOfRangeException`
    /// CRUA, escapando de `ImageToPdf` sem nunca virar `PdfEditingException` (reproduzido pelo revisor
    /// com um JPEG hostil artesanal). Fix: TODA a aritmética de offset/limite desta função roda em
    /// `long` (`ReadUInt32` abaixo devolve `long`, nunca `int`, especificamente pra este caso —
    /// carrega o valor UNSIGNED de 32 bits completo sem jamais virar negativo por overflow de sinal;
    /// `long` tem margem de sobra pra somar qualquer combinação de valores de 32 bits sem overflowar).
    /// Só depois de VALIDAR (`entryStart + 12 <= jpeg.Length`, em `long`) um offset converte de volta
    /// pra `int` — nesse ponto já é seguro por construção (bounded por `jpeg.Length`, que é `int`).
    private static int ParseTiffOrientationRotation(byte[] jpeg, int tiffStart)
    {
        if (tiffStart + 8 > jpeg.Length) return 0;
        bool bigEndian = jpeg[tiffStart] == 'M' && jpeg[tiffStart + 1] == 'M';
        bool littleEndian = jpeg[tiffStart] == 'I' && jpeg[tiffStart + 1] == 'I';
        if (!bigEndian && !littleEndian) return 0;

        long ifd0Offset = ReadUInt32(jpeg, tiffStart + 4, bigEndian); // 0..4294967295 — NUNCA negativo
        long ifd0Start = tiffStart + ifd0Offset; // aritmética em `long`: sem risco de overflow (ver acima)
        if (ifd0Start + 2 > jpeg.Length) return 0; // ifd0Start já é sempre >= tiffStart >= 0

        int entryCount = ReadUInt16(jpeg, (int)ifd0Start, bigEndian); // seguro: ifd0Start+2<=jpeg.Length validado acima
        for (int e = 0; e < entryCount; e++)
        {
            long entryStart = ifd0Start + 2 + (long)e * 12; // long: e*12 sozinho não overflowaria, mas
                                                              // somado a um ifd0Start hostil poderia.
            if (entryStart + 12 > jpeg.Length) break;
            int entryStartInt = (int)entryStart; // seguro agora: validado < jpeg.Length (que é int)
            int tag = ReadUInt16(jpeg, entryStartInt, bigEndian);
            if (tag != 0x0112) continue;
            int type = ReadUInt16(jpeg, entryStartInt + 2, bigEndian);
            if (type != 3) return 0; // esperado SHORT — outro tipo é malformado, sem correção
            int orientation = ReadUInt16(jpeg, entryStartInt + 8, bigEndian); // cabe nos 2 primeiros bytes do campo de 4
            return orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 }; // 1=normal; 2/4/5/7=espelhado, fora de escopo v1
        }
        return 0;
    }

    /// Varredura crua (sem iText) do marcador SOF (Start Of Frame — 0xC0-0xCF, exceto 0xC4/0xC8/0xCC
    /// que não são SOF de verdade: DHT/JPG/DAC) de um JPEG — devolve largura/altura/nº de componentes
    /// de cor DIRETO do header, sem decodificar nada. Usado tanto pelo detector de CMYK (nº de
    /// componentes==4, ver `IsCmykJpeg`) quanto pelo teto de pixels em `ImageToPdf` (C2 da revisão —
    /// precisa saber o tamanho ANTES de chamar `ImageDataFactory.Create`, o trabalho caro que o teto
    /// existe pra evitar). `false` quando o SOF não é encontrado ou o header está truncado — chamador
    /// trata como "sem informação confiável", nunca lança.
    private static bool TryReadJpegSofInfo(byte[] jpeg, out int width, out int height, out int numComponents)
    {
        width = 0; height = 0; numComponents = 0;
        int i = 2; // após SOI (FF D8)
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            byte marker = jpeg[i + 1];
            if (marker == 0xD9 || marker == 0xDA) break; // EOI ou SOS — SOF sempre vem antes
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; } // TEM/RSTn: sem tamanho
            int segLen = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (segLen < 2) break;
            bool isSof = marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                int payloadStart = i + 4; // precisão(1) + altura(2) + largura(2) + nComponentes(1)
                if (payloadStart + 6 > jpeg.Length) return false; // header truncado — sem dado confiável
                height = (jpeg[payloadStart + 1] << 8) | jpeg[payloadStart + 2];
                width = (jpeg[payloadStart + 3] << 8) | jpeg[payloadStart + 4];
                numComponents = jpeg[payloadStart + 5];
                return true;
            }
            i += 2 + segLen;
        }
        return false;
    }

    /// FIX (revisão pós-merge da Task 3, Plano 7) — ver XML doc em Contract.cs. Era `private static`,
    /// NUNCA chamado por nada (`ImageToPdf` sempre checou `sofComponents == 4` inline, direto, sem
    /// passar por este helper) — promovido a instância pública pra virar a implementação de
    /// `IPdfEditor.IsCmykJpeg`, reusando `TryReadJpegSofInfo` (nunca reimplementado). Null/curto demais
    /// -> `false` (mesmo fail-safe de `IsWithinImagePixelLimit`) — sem essa guarda, `bytes.Length`
    /// dentro de `TryReadJpegSofInfo` lançaria `NullReferenceException` crua pra um `bytes` nulo.
    public bool IsCmykJpeg(byte[] bytes)
    {
        if (bytes is not { Length: >= 4 } || bytes[0] != 0xFF || bytes[1] != 0xD8) return false; // não é JPEG -> não pode ser CMYK
        return TryReadJpegSofInfo(bytes, out _, out _, out int numComponents) && numComponents == 4;
    }

    // --- Task 3 (Plano 15): camada de texto invisível de OCR ------------------------------------
    // Decisões de contrato (gate, mapeamento px→pt, rotação, invisível) registradas no XML doc de
    // IPdfEditor.ApplyOcrTextLayer em Contract.cs — não repetidas aqui.

    /// Grava a camada de texto invisível (render mode 3) por página. GATE de assinatura + validação
    /// de TODOS os `PageIndex` ANTES de gravar qualquer coisa (mesmo espírito de RotatePages/
    /// SetFormFields — nunca aplica metade de um pedido). O MAPEAMENTO px→pt e a rotação estão em
    /// `ComputeOcrTextMatrix` (ver derivação lá).
    public byte[] ApplyOcrTextLayer(byte[] pdf, IReadOnlyList<OcrTextLayer> layers)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                GuardAgainstSignedDocument(doc);
                // TODOS os índices validados ANTES de gravar QUALQUER texto — nunca grava metade das
                // páginas e falha na outra metade (mesma disciplina de RotatePages/DeletePages).
                foreach (var layer in layers) ValidatePageIndex(doc, layer.PageIndex);

                // Fonte padrão Helvetica (uma das 14 fontes-base do PDF — sem embutir bytes de fonte).
                // Basta pra busca/seleção: o glifo nunca é PINTADO (render mode 3 INVISIBLE), só a
                // string e a posição importam. Criada 1x e reusada em todas as páginas deste doc.
                var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

                foreach (var layer in layers)
                {
                    if (layer.Boxes is null || layer.Boxes.Count == 0) continue;
                    // Sem dimensão-fonte confiável não há como calcular a escala px→pt — pula a página
                    // inteira (defesa em profundidade; o App sempre preenche estes valores).
                    if (layer.SourceWidthPx <= 0 || layer.SourceHeightPx <= 0) continue;

                    var page = doc.GetPage(layer.PageIndex + 1); // iText 1-based; contrato 0-based
                    var mediaBox = page.GetMediaBox();
                    double llx = mediaBox.GetLeft(), lly = mediaBox.GetBottom();
                    double wPt = mediaBox.GetWidth(), hPt = mediaBox.GetHeight();
                    // `/Rotate` normalizado 0/90/180/270 (mesma normalização de GetPageRotations —
                    // defesa contra um `/Rotate` de origem externa negativo/fora de faixa).
                    int rotation = ((page.GetRotation() % 360) + 360) % 360;

                    // O bitmap de OCR foi rasterizado NA orientação EXIBIDA (PDFium já aplica /Rotate):
                    // para 90/270 a largura-exibida corresponde à ALTURA do MediaBox e vice-versa.
                    bool swap = rotation is 90 or 270;
                    double dispWpt = swap ? hPt : wPt;
                    double dispHpt = swap ? wPt : hPt;
                    double fatorX = dispWpt / layer.SourceWidthPx;
                    double fatorY = dispHpt / layer.SourceHeightPx;

                    var canvas = new PdfCanvas(page);
                    foreach (var box in layer.Boxes)
                    {
                        if (string.IsNullOrWhiteSpace(box.Text)) continue; // texto vazio -> nada a gravar

                        // Baseline da caixa no espaço EXIBIDO (y para CIMA, origem inferior-esquerda do
                        // frame exibido): canto INFERIOR-esquerdo da caixa (a origem px é topo-esquerda,
                        // y para BAIXO — o "fundo" da caixa em px é Top+Height).
                        double xbDisp = box.LeftPx * fatorX;
                        double ybDisp = dispHpt - (box.TopPx + box.HeightPx) * fatorY;
                        double fontSize = box.HeightPx * fatorY; // altura da caixa em pt (exibido)
                        if (fontSize <= 0) continue;

                        var m = ComputeOcrTextMatrix(rotation, wPt, hPt, xbDisp, ybDisp, llx, lly);

                        canvas.BeginText();
                        canvas.SetFontAndSize(font, (float)fontSize);
                        // Render mode 3 (INVISIBLE) — o glifo NÃO pinta tinta nenhuma (prova de pixel
                        // ~zero no render), mas a string continua extraível/pesquisável/copiável.
                        canvas.SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.INVISIBLE);
                        canvas.SetTextMatrix((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F);
                        canvas.ShowText(box.Text);
                        canvas.EndText();
                    }
                }
            }
        }
        catch (BadPasswordException ex) { throw WrapPassword(ex); }
        catch (ITextException ex) { throw WrapGeneric(ex); }
        return output.ToArray();
    }

    /// Matriz de TEXTO (`Tm` — `SetTextMatrix`, convenção PDF `[a b c d e f]`: `x' = a*x + c*y + e`,
    /// `y' = b*x + d*y + f`) que posiciona a baseline da palavra no frame NÃO-ROTACIONADO do MediaBox,
    /// aplicando a rotação INVERSA de `/Rotate` para o texto aparecer alinhado quando o leitor exibe a
    /// página girada. Mesma disciplina de frame do carimbo do Plano 3b/4 (`/Rotate` é EXIBIÇÃO; o
    /// conteúdo é gravado não-rotacionado). A escala do glifo vem SÓ do `SetFontAndSize` (fontSize) —
    /// esta matriz carrega apenas rotação + translação (parte linear com determinante ±1).
    ///
    /// Derivação. Seja a transformação de EXIBIÇÃO `T_θ` que leva um ponto não-rotacionado `(x,y)`
    /// (y para cima, origem 0) ao ponto exibido `(X,Y)` (idem), para uma página de dimensões
    /// não-rotacionadas `W×H` (`W=wPt`, `H=hPt`):
    ///   θ=0:   (X,Y) = (x,          y)           [exibida W×H]
    ///   θ=90:  (X,Y) = (y,          W - x)        [exibida H×W]  (giro horário de 90°)
    ///   θ=180: (X,Y) = (W - x,      H - y)        [exibida W×H]
    ///   θ=270: (X,Y) = (H - y,      x)            [exibida H×W]
    /// A translação `(e,f)` é a IMAGEM INVERSA (`T_θ⁻¹`) da baseline exibida `(xbDisp, ybDisp)`, mais o
    /// deslocamento da origem do MediaBox `(llx,lly)`:
    ///   θ=0:   (xbDisp,        ybDisp)
    ///   θ=90:  (W - ybDisp,    xbDisp)
    ///   θ=180: (W - xbDisp,    H - ybDisp)
    ///   θ=270: (ybDisp,        H - xbDisp)
    /// A parte linear `(a,b,c,d)` é a imagem, por `T_θ⁻¹`, dos vetores de direção do texto (avanço
    /// `+X`, subida `+Y` no espaço exibido) — o que rotaciona o glifo em `-θ`:
    ///   θ=0:   (1, 0, 0, 1)      θ=90:  (0, 1, -1, 0)
    ///   θ=180: (-1,0, 0,-1)      θ=270: (0,-1,  1, 0)
    /// Prova end-to-end nas 4 rotações (extração da baseline real via iText + reaplicação independente
    /// de `T_θ` no teste, batendo na baseline exibida esperada): ver `ApplyOcrTextLayerTests`.
    private static (double A, double B, double C, double D, double E, double F) ComputeOcrTextMatrix(
        int rotation, double wPt, double hPt, double xbDisp, double ybDisp, double llx, double lly)
    {
        (double a, double b, double c, double d) = rotation switch
        {
            90 => (0, 1, -1, 0),
            180 => (-1, 0, 0, -1),
            270 => (0, -1, 1, 0),
            _ => (1, 0, 0, 1),
        };
        (double tx, double ty) = rotation switch
        {
            90 => (wPt - ybDisp, xbDisp),
            180 => (wPt - xbDisp, hPt - ybDisp),
            270 => (ybDisp, hPt - xbDisp),
            _ => (xbDisp, ybDisp),
        };
        return (a, b, c, d, tx + llx, ty + lly);
    }

    /// Lê largura/altura direto do chunk IHDR de um PNG — offsets FIXOS pelo spec (assinatura de 8
    /// bytes; depois `length`(4, ignorado aqui) + `"IHDR"`(4) + `width`(4, big-endian) +
    /// `height`(4, big-endian), sempre o PRIMEIRO chunk de um PNG válido) — sem decodificar nada.
    /// Usado pelo teto de pixels em `ImageToPdf` ANTES de `ImageDataFactory.Create` (C2 da revisão).
    /// `long` de propósito (mesma disciplina do fix C1 acima): width/height vêm de 4 bytes NÃO
    /// CONFIÁVEIS do arquivo — um PNG hostil pode declarar até 4294967295 em cada campo; `long`
    /// carrega esse valor inteiro sem qualquer risco de overflow no produto `width*height` do chamador.
    private static bool TryReadPngDimensions(byte[] png, out long width, out long height)
    {
        width = 0; height = 0;
        if (png.Length < 24) return false;
        if (!(png[12] == 'I' && png[13] == 'H' && png[14] == 'D' && png[15] == 'R')) return false;
        width = ((long)png[16] << 24) | ((long)png[17] << 16) | ((long)png[18] << 8) | png[19];
        height = ((long)png[20] << 24) | ((long)png[21] << 16) | ((long)png[22] << 8) | png[23];
        return true;
    }

    private static int ReadUInt16(byte[] b, int offset, bool bigEndian) =>
        bigEndian ? (b[offset] << 8) | b[offset + 1] : (b[offset + 1] << 8) | b[offset];

    /// `long` de propósito (C1 da revisão, ver XML doc de `ParseTiffOrientationRotation` acima) —
    /// devolve o valor UNSIGNED de 32 bits completo (0..4294967295); um `int` sofreria overflow de
    /// SINAL pra qualquer valor >= 0x80000000, virando negativo silenciosamente.
    private static long ReadUInt32(byte[] b, int offset, bool bigEndian) =>
        bigEndian
            ? ((long)b[offset] << 24) | ((long)b[offset + 1] << 16) | ((long)b[offset + 2] << 8) | b[offset + 3]
            : ((long)b[offset + 3] << 24) | ((long)b[offset + 2] << 16) | ((long)b[offset + 1] << 8) | b[offset];
}
