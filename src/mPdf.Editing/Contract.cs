namespace mPdf.Editing;

/// Tipos de anotação suportados pelo contrato. Highlight (Task 2) + Underline/Strikeout (Task 6,
/// Plano 3a) + StickyNote/FreeText (Task 7, Plano 3a) + Ink/Rectangle/Line/Arrow (Task 8, Plano 3a) +
/// ImageStamp (Task 9, Plano 3a) têm implementação em AddAnnotation — todos os 10 kinds do enum.
/// Arrow (Task 8) não tem subtype PRÓPRIO no spec PDF: é gravado como um `/Line` com `/LE` (line ending
/// styles) terminando numa seta — ver `PdfEditor.BuildAnnotation`/`MapKind`.
public enum AnnotationKind
{
    Highlight,
    Underline,
    Strikeout,
    StickyNote,
    FreeText,
    Ink,
    Rectangle,
    Line,
    Arrow,
    ImageStamp,
}

/// Ponto em coordenadas de página, em pontos PDF (origem inferior-esquerda) — usado para traços de
/// tinta (Ink). Neutro: nenhum tipo iText ou WPF (ex.: System.Windows.Point) atravessa esta fronteira.
public readonly record struct PdfPoint(double XPt, double YPt);

/// Quadrilátero alinhado aos eixos, em pontos PDF — usado para QuadPoints de anotações de marcação de
/// texto (Highlight/Underline/Strikeout). Substitui o `System.Windows.Rect` cogitado no brief: WPF não
/// pode vazar para este projeto nem para o contrato que o App consome.
public readonly record struct PdfQuad(double LeftPt, double BottomPt, double RightPt, double TopPt);

/// Nó da árvore de sumário/bookmarks de um PDF (Task 5, Plano 3b) — neutro, nenhum tipo iText
/// (PdfOutline, PdfDestination, etc.) atravessa esta fronteira, mesmo espírito de AnnotationData/
/// PdfQuad acima. `PageIndex` é 0-based (convenção do resto do contrato — ver AnnotationData.PageIndex/
/// GetPageRotations) — `null` quando o nó não aponta pra nenhuma página: nó puramente organizacional
/// (sem `/Dest`/`/A` no PDF) OU um destino presente que não resolveu pra uma página válida do
/// documento (ver HIPÓTESE em `IPdfEditor.ReadOutline`) — as duas causas colapsam no mesmo `null`,
/// por design: o consumidor (TreeView do App) só precisa saber "clicável ou não", nunca precisa
/// distinguir a causa. `Children` é SEMPRE não-nulo (lista vazia pra nó-folha, nunca `null`) —
/// dispensa checagem de nulidade em quem consome.
public sealed record OutlineNode(string Title, int? PageIndex, IReadOnlyList<OutlineNode> Children)
{
    /// Conveniência de UI (TreeView do App, Task 5 Plano 3b) — `true` quando `PageIndex` não é nulo.
    /// Propriedade CALCULADA (sem backing field, corpo `=>`) — não participa da igualdade gerada pelo
    /// compilador pra este record (que só compara os 3 parâmetros posicionais acima), então não muda
    /// nenhum comportamento de comparação/round-trip já coberto pelos testes de ReadOutline.
    public bool HasPage => PageIndex is not null;
}

/// Representação neutra de uma anotação de PDF — nenhum tipo do iText (PdfAnnotation, PdfName, etc.)
/// aparece nesta assinatura pública; é o contrato que src/mPdf.App consome sem nunca referenciar iText.
public sealed record AnnotationData
{
    /// Nome/NM da anotação no PDF. Ao CRIAR: se informado, AddAnnotation o usa como `/NM` do PDF
    /// (revisão pós-M11 — "honrar o Id informado", Plano 3a Task 2 fix); se nulo, AddAnnotation gera
    /// um GUID. Em ambos os casos, o Id final (informado ou gerado) volta preenchido ao ler de novo
    /// via ReadAnnotations — estabilidade de Id entre chamadas é o que a Task 7 (undo/redo) precisa.
    /// Validação (revisão pós-M11, rodada 2 — "unicidade de Id"): um Id informado vazio/só espaços,
    /// ou que já exista em outra anotação do MESMO documento, faz `AddAnnotation` lançar
    /// `ArgumentException` ANTES de qualquer escrita — nunca silenciosamente sobrescreve/duplica um
    /// `/NM`. Não se aplica ao Id gerado automaticamente (GUID, unicidade garantida por construção).
    public string? Id { get; init; }
    public required AnnotationKind Kind { get; init; }
    /// Índice de página, base 0 (convenção do resto do codebase — ver PdfDocumentRenderer/DocumentSession).
    public required int PageIndex { get; init; }
    public double LeftPt { get; init; }
    public double BottomPt { get; init; }
    public double RightPt { get; init; }
    public double TopPt { get; init; }
    /// Cor ARGB. `null` é um valor de SENTINELA distinto de qualquer cor, inclusive preto — significa
    /// "sem `/C` no PDF" (anotação sem cor definida, ex.: transparente por spec). Ao LER, `null`
    /// reflete a ausência real de `/C` (nunca um palpite de preto opaco). Ao CRIAR com `null`,
    /// AddAnnotation NÃO escreve `/C` na anotação (revisão pós-M7 — "sentinela de cor").
    public uint? ColorArgb { get; init; }
    public string? Content { get; init; }
    public string? Author { get; init; }
    /// Quadriláteros de marcação de texto (Highlight/Underline/Strikeout). Se nulo/vazio ao criar,
    /// AddAnnotation usa o retângulo Left/Bottom/Right/Top acima como quad único.
    public IReadOnlyList<PdfQuad>? Quads { get; init; }
    /// Traços de tinta (Ink, Task 8, Plano 3a) — cada traço é uma polilinha de pontos, em pontos de
    /// página. v1 (brief): AddAnnotation aceita N traços (o /InkList do spec é sempre "array de
    /// arrays"), mas a ferramenta de desenho do App só produz 1 traço por gesto — múltiplos traços por
    /// anotação é um caso de uso fora de escopo desta task, não uma limitação do contrato/editor.
    /// AddAnnotation exige ao menos 1 traço não-vazio ao criar Kind=Ink (ArgumentException senão).
    public IReadOnlyList<IReadOnlyList<PdfPoint>>? InkStrokes { get; init; }
    /// Ponto inicial/final de uma anotação de Linha ou Seta (Task 8, Plano 3a), em pontos de página —
    /// nulo para os demais kinds. AddAnnotation exige os 2 preenchidos ao criar Kind=Line/Arrow
    /// (ArgumentException senão); Left/Bottom/Right/Top acima continuam sendo o BBOX (envelope
    /// min/max dos 2 pontos), igual ao papel que já cumprem para Highlight/Quads.
    public PdfPoint? LineStartPt { get; init; }
    public PdfPoint? LineEndPt { get; init; }
    /// Bytes de imagem PNG/JPG (ImageStamp, Task 9, Plano 3a) — exigido ao CRIAR (`ArgumentException`
    /// se nulo/vazio, mesmo espírito de Ink/Line/Arrow). DECISÃO DE DESIGN (v1): `ReadAnnotations`
    /// SEMPRE devolve `null` aqui, mesmo para um `/Stamp` recém-criado por este módulo — extrair a
    /// imagem de volta da appearance stream (`/AP /N /Resources /XObject`) é possível mas fora de
    /// escopo v1 (bbox/autor/cor/Id continuam preservados no round-trip, só a imagem em si não volta).
    /// CONSEQUÊNCIA DIRETA (documentada em `DocumentViewModel.MoveSelectedAnnotationAsync`): o lift
    /// (Remove+Add) que Editar/Mover usam para Highlight/StickyNote/Ink/etc. não tem como reconstruir
    /// a appearance de um ImageStamp sem os bytes originais — por isso ImageStamp é NÃO-liftável nesta
    /// v1 (só colocar e excluir; mover/editar ficam desabilitados para este Kind, ver CanExecute lá).
    public byte[]? ImageBytes { get; init; }
}

/// Tipo de campo de formulário (AcroForm), Task 1 do Plano 3c — neutro, nenhum tipo iText
/// (PdfFormField/PdfTextFormField/PdfButtonFormField/PdfChoiceFormField, etc.) atravessa esta
/// fronteira, mesmo espírito de AnnotationKind acima. `Other` cobre botão de ação (push button, que
/// também é um `/Btn` no spec — distinto de checkbox/radio por flag, mas sem uso de "definir valor"
/// para este contrato) e campo de assinatura (`/Sig`) — nenhum dos 2 é lido/escrito por
/// SetFormFields/FlattenForm nesta v1, só aparecem em ReadFormFields pra não quebrar a leitura de um
/// PDF que os contenha.
public enum FormFieldType { Text, Checkbox, Radio, Combo, ListBox, Other }

/// Representação neutra de 1 campo de formulário — nenhum tipo do iText (PdfFormField, PdfAcroForm,
/// etc.) aparece nesta assinatura pública (mesmo espírito de AnnotationData acima).
/// `Options`: SEMPRE não-nulo (lista vazia quando não se aplica) — dispensa checagem de nulidade em
/// quem consome, mesmo padrão de `OutlineNode.Children`. Para Combo/ListBox são os valores do menu
/// (na ordem do PDF); para Checkbox/Radio são os "estados on" possíveis (nome do estado /AS que
/// representa "marcado" — ex.: `["Yes"]` pra um checkbox simples, `["M","F"]` pro grupo de radio),
/// SEM incluir "Off" (que não é uma opção "marcável", é a ausência de marcação); para Text/Other,
/// vazia.
/// `Value`: string devolvida por `PdfFormField.GetValueAsString()` — pra Checkbox/Radio é o nome do
/// estado ATUALMENTE selecionado ("Off" quando nada está marcado, nunca `null`); pra Combo/ListBox é
/// o valor selecionado; pra Text é o conteúdo digitado.
/// `WidgetRect`: retângulo do PRIMEIRO widget do campo (`PdfFormField.GetWidgets()[0]`), no frame
/// NÃO-ROTACIONADO da página — mesma convenção/mesma costura de rotação documentada em
/// `AnnotationData` (Task 3, Plano 3b: `/Rotate` é atributo de EXIBIÇÃO, o `/Rect` do widget nunca
/// muda quando a página gira). DECISÃO DE CONTRATO: um campo Radio tem >=1 widget (1 por opção,
/// potencialmente em páginas diferentes), mas este contrato expõe só 1 `FormFieldData` por CAMPO
/// (não por widget) — o retângulo/página do PRIMEIRO widget (ordem de `GetWidgets()`, que reflete a
/// ordem de `AddKid` na criação) representam o campo como um todo; um consumidor que precise da
/// posição de CADA botão de rádio individualmente não é um caso de uso desta v1. `null` só no caso
/// residual de um campo sem nenhum widget associado (não deveria ocorrer em um PDF bem formado, mas
/// `ReadFormFields` nunca lança por causa disso).
public sealed record FormFieldData(string Name, FormFieldType Type, string? Value,
    IReadOnlyList<string> Options, int PageIndex, PdfQuad? WidgetRect, bool IsReadOnly);

/// Camada de texto invisível de OCR (Task 3, Plano 15) — tipo NEUTRO de ENTRADA de
/// `IPdfEditor.ApplyOcrTextLayer`. Nenhum tipo do Tesseract (`OcrWord`/`OcrEngineResult` de
/// `mPdf.Ocr`) nem do iText atravessa esta fronteira: é puro dado. O App (Task 4) é quem mapeia o
/// `OcrEngineResult` do motor para estes tipos, preenchendo `PageIndex` e as dimensões-fonte.
///
/// `PageIndex` 0-based (convenção do resto do contrato — ver `AnnotationData.PageIndex`).
/// `SourceWidthPx`/`SourceHeightPx`: dimensões, EM PIXELS, do bitmap que gerou este OCR — na
/// orientação EXIBIDA/rasterizada da página (o `PdfDocumentRenderer` de `mPdf.Rendering` já aplica
/// `/Rotate` ao rasterizar, então para uma página com `/Rotate` 90/270 estas dimensões vêm
/// TROCADAS em relação ao `MediaBox` não-rotacionado — `ApplyOcrTextLayer` reconcilia isso). As
/// caixas de `Boxes` estão no MESMO espaço de pixels (origem TOPO-esquerda) que estas dimensões.
public sealed record OcrTextLayer(int PageIndex, int SourceWidthPx, int SourceHeightPx,
    IReadOnlyList<OcrTextBox> Boxes);

/// Uma palavra/caixa reconhecida pelo OCR, em PIXELS na origem TOPO-ESQUERDA (x cresce à direita,
/// y cresce para BAIXO), na resolução/orientação do bitmap declarado em `OcrTextLayer.Source*Px`.
/// `ApplyOcrTextLayer` mapeia px→pt (fator = dimensão-exibida_pt / dimensão-fonte_px), converte a
/// origem topo-esquerda (px) para a origem inferior-esquerda do PDF (pt) e aplica a transformação
/// de `/Rotate` para gravar o texto no frame NÃO-rotacionado do `MediaBox`. Texto vazio/só-espaços
/// é IGNORADO (nenhum operador de texto é gravado para ele).
public sealed record OcrTextBox(string Text, double LeftPx, double TopPx, double WidthPx, double HeightPx);

/// Fronteira ÚNICA por onde iText pode ser referenciado (via a implementação interna) — ver
/// AgplGuardTests. Consumido pelo App só através desta interface e dos tipos neutros acima.
///
/// PRECONDIÇÃO DE ASSINATURA (revisão pós-M11, defesa em profundidade — a Task 5 vai adicionar o
/// mesmo gate na camada de App, mas o módulo de edição não pode CONFIAR que o chamador sempre checa
/// antes): `AddAnnotation` e `RemoveAnnotation` recusam qualquer PDF que já tenha assinatura(s)
/// (`HasSignatures` internamente) com `PdfSignedDocumentException` — editar um documento assinado
/// invalidaria a assinatura (spec ICP-Brasil §5.2); o fluxo correto é "Editar uma cópia" (sem
/// assinaturas), nunca editar o assinado. Exceção TIPADA (revisão pós-M11, rodada 2) justamente para
/// a Task 5 poder capturá-la e oferecer "Editar uma cópia" sem precisar inspecionar mensagem.
public interface IPdfEditor
{
    byte[] AddAnnotation(byte[] pdf, AnnotationData annotation);
    /// Remove EXATAMENTE 1 anotação — a primeira encontrada (páginas em ordem, depois ordem da lista
    /// de anotações da página) cujo `/NM` bate com `annotationId`. `AddAnnotation` garante Id único
    /// por construção (ver `AnnotationData.Id`), então duplicidade nunca nasce por este módulo; mas
    /// um PDF de origem EXTERNA pode chegar com `/NM` duplicado — nesse caso residual, "remover 1 e
    /// parar" é o comportamento previsível (nunca remover todas de uma vez por engano).
    byte[] RemoveAnnotation(byte[] pdf, string annotationId);
    /// Anotações de USUÁRIO apenas — widgets (campos de formulário/assinatura) são EXCLUÍDOS por
    /// decisão de design: um widget não é uma anotação que o usuário adicionou, é a materialização
    /// visual de um campo (assinatura, texto, etc.). Ver PdfEditor.ReadAnnotations.
    IReadOnlyList<AnnotationData> ReadAnnotations(byte[] pdf);
    /// Leitura pura, sem gate. SEGURO EM XFA (Important 2, revisão do Task 2/Plano 3c — diferente de
    /// `ReadFormFields`/`SetFormFields`/`FlattenForm`, que continuam lançando `PdfEditingException` em
    /// documento XFA, contrato pinado no Task 1 fix): documento com `/XFA` nunca instancia
    /// `PdfAcroForm`/`SignatureUtil` internamente — usa uma varredura crua do dicionário `/AcroForm/
    /// Fields` por `/FT /Sig` com `/V` presente. Um doc XFA-E-assinado devolve `true` normalmente.
    bool HasSignatures(byte[] pdf);
    /// Remove os CAMPOS de assinatura (e os widgets visuais correspondentes) do PDF — a base de
    /// "Editar uma cópia" (Plano 3a, Task 5): a precondição acima recusa editar um documento assinado,
    /// então o único jeito de editar é sobre uma CÓPIA sem assinatura nenhuma. Ao contrário de
    /// AddAnnotation/RemoveAnnotation, NÃO recusa doc assinado — é o oposto, EXISTE justamente para
    /// produzir um doc que deixa de ser assinado. Doc sem nenhuma assinatura: no-op (devolve um PDF
    /// equivalente, sem lançar) — chamar isto num doc já editável não é um erro. Páginas e contagem são
    /// preservadas; conteúdo não-assinatura não é alterado pela remoção dos campos (fix pós-revisão,
    /// M: a formulação anterior enumerava "anotações de usuário, texto, imagens" — mais forte do que os
    /// testes atuais provam; StripSignatures só é testado quanto a PageCount/HasSignatures).
    byte[] StripSignatures(byte[] pdf);

    // --- Task 2 (Plano 3b): motor de organização de páginas ------------------------------------
    //
    // DECISÃO DE CONTRATO (extensão de IPdfEditor vs interface nova `IPageEditor` — brief oferecia
    // as duas opções): ESTENDER IPdfEditor, não criar uma 2ª interface. Rationale: 1 editor, 1
    // factory (`PdfEditorFactory.Create()`) — introduzir `IPageEditor` obrigaria (a) uma 2ª factory
    // OU um construtor que devolve as duas interfaces da mesma instância (acoplamento sem benefício:
    // as page-ops abrem o MESMO `PdfDocument` da MESMA classe `PdfEditor`, reusam OS MESMOS helpers
    // privados — `GuardAgainstSignedDocument`/`ValidatePageIndex`/`WrapPassword`/`WrapGeneric` — não
    // há fronteira arquitetural real entre "editar anotações" e "editar páginas", as duas são
    // "edição segura de PDF" pelo mesmo pipeline `ApplyEdit`); (b) o App precisaria injetar/resolver
    // 2 interfaces em vez de 1 em cada ViewModel que precisar de ambas as capacidades (o organizador,
    // Task 3/4 do Plano 3b, PRECISA de anotações E páginas na mesma sessão de edição). Nenhuma das
    // 7 operações novas quebra a fronteira neutra existente (nenhum tipo iText atravessa a
    // assinatura pública) — o único motivo master para separar interfaces (Segregation) seria um
    // consumidor que só precisa de uma metade, e não existe tal consumidor neste codebase.
    //
    // ÍNDICES: 0-based em TODA a superfície nova (pageIndexes, fromIndex/toIndex, atIndex, ranges) —
    // mesma convenção de `AnnotationData.PageIndex` (já 0-based) e do resto do contrato neutro; a UI
    // (Organizador, Task 3/4) converte para 1-based só na apresentação ao usuário, nunca no contrato.
    //
    // GATE DE ASSINATURA (GuardAgainstSignedDocument) — decisão por operação, registrada aqui para
    // o revisor validar (self-review do plano exige isto explicitamente). REVISADO (Opus review,
    // pós-Task 2 — adjudicação de um Important levantado no próprio relatório da task): a 1ª versão
    // desta doc dava gate a ExtractPages com uma justificativa de "pluralidade de entradas" que era
    // FACTUALMENTE ERRADA — SplitByRanges também recebe UM ÚNICO `pdf` de entrada (não uma lista) e
    // continua sendo, na prática, "extrair N vezes" da mesma fonte; não havia diferença estrutural
    // real entre Extract e Split que justificasse gates diferentes. Corrigido abaixo.
    //   - RotatePages/DeletePages/MovePage/InsertPages: GATE SIM. Todas mutam o `pdf` recebido como
    //     PARÂMETRO ALVO (o documento que o usuário está editando no organizador) — mesmo raciocínio
    //     de defesa em profundidade de AddAnnotation/RemoveAnnotation: o módulo não confia que todo
    //     chamador presente e futuro sempre checa antes. Para InsertPages, o gate é SÓ no alvo
    //     (`pdf`) — a ORIGEM (`source`) pode estar assinada sem problema (inserir páginas de um PDF
    //     assinado é uma LEITURA da origem, nunca uma edição dela; só o documento que seria
    //     RESSALVO precisa estar livre de assinatura — ver PdfEditor.InsertPages; o invariante
    //     `HasSignatures(resultado) == false` é ASSERTADO por teste, não só presumido — confirmado
    //     empiricamente que `CopyPagesTo` já não carrega o AcroForm da origem para o alvo).
    //   - ExtractPages/MergeDocuments/SplitByRanges: SEM GATE — POLÍTICA ÚNICA para as 3 (revisor
    //     DEVE validar): nenhuma das 3 EDITA/reescreve uma entrada sobre si mesma — cada uma LÊ 1+
    //     fontes e produz 1+ documentos NOVOS. ExtractPages e SplitByRanges são a MESMA operação sob
    //     duas formas (recorte de páginas de UMA fonte em N saídas — Split é literalmente "Extract
    //     repetido por intervalo"); MergeDocuments é a operação inversa (N fontes em 1 saída). A
    //     inviolabilidade de um PDF assinado (spec §5.2) protege contra EDITAR o documento assinado,
    //     não contra LÊ-LO para compor/recortar outra coisa — aceitam fontes assinadas DE PROPÓSITO.
    //     Em troca da ausência de gate, a HIGIENE DE ASSINATURA DA SAÍDA é um INVARIANTE ASSERTADO
    //     POR TESTE nas 3 (não só presumido): `HasSignatures(saída) == false` sempre, mesmo quando a
    //     entrada estava assinada. Empírico (probe project isolado, ver task-2-report.md, seção
    //     "## Fix"): tanto `PdfMerger.Merge` quanto `PdfDocument.CopyPagesTo` (os 2 mecanismos do
    //     iText usados pelas 3 operações) já NÃO carregam o AcroForm/campo de assinatura da fonte —
    //     `SignatureUtil` vê 0 assinaturas na saída sem nenhum passo extra, nas 3 operações. Mesmo
    //     assim, as 3 passam o resultado por `StripSignatures` como REDE DE SEGURANÇA defensiva antes
    //     de retornar (custa pouco, confirmado no-op hoje) — garante o invariante mesmo se uma versão
    //     futura do iText passar a preservar o AcroForm em algum desses caminhos.
    byte[] RotatePages(byte[] pdf, IReadOnlyList<int> pageIndexes, int degreesClockwise);
    /// Recusa excluir TODAS as páginas do documento (`ArgumentException` pt-BR) — um PDF sem
    /// nenhuma página não é um documento válido; o organizador (Task 3, Plano 3b) nunca deve deixar
    /// o usuário chegar a este estado.
    byte[] DeletePages(byte[] pdf, IReadOnlyList<int> pageIndexes);
    byte[] MovePage(byte[] pdf, int fromIndex, int toIndex);
    /// Novo documento contendo SÓ as páginas pedidas, NA ORDEM dada por `pageIndexes` (não
    /// necessariamente crescente — o chamador pode reordenar ao extrair). O documento original
    /// (`pdf`) nunca é alterado, mesmo se estiver assinado (sem gate — ver bloco de decisão acima);
    /// `HasSignatures(resultado) == false` é um invariante assertado por teste. `pageIndexes` vazio
    /// -> `ArgumentException` pt-BR, antes de abrir o PDF.
    byte[] ExtractPages(byte[] pdf, IReadOnlyList<int> pageIndexes);
    /// TODAS as páginas de `source`, na ORDEM em que aparecem nele, inseridas em `pdf` a partir de
    /// `atIndex` (0-based; `atIndex == pageCount(pdf)` insere no FIM). Gate de assinatura só em
    /// `pdf` — `source` pode estar assinado (ver bloco de decisão acima); `HasSignatures(resultado)
    /// == false` mesmo com `source` assinado é um invariante assertado por teste.
    byte[] InsertPages(byte[] pdf, byte[] source, int atIndex);
    /// Concatena os documentos NA ORDEM da lista, produzindo 1 documento novo. Aceita fontes
    /// assinadas (sem gate — ver bloco de decisão acima); `HasSignatures(resultado) == false` é um
    /// invariante assertado por teste. Lista vazia -> `ArgumentException` pt-BR, antes de abrir
    /// qualquer PDF.
    byte[] MergeDocuments(IReadOnlyList<byte[]> pdfs);
    /// 1 documento novo por intervalo de `ranges` — cada `(from, to)` é 0-based INCLUSIVO nos dois
    /// extremos (ex.: `(0, 2)` = as 3 primeiras páginas). Sem gate — leitura pura, `pdf` nunca é
    /// alterado, mesmo se estiver assinado; `HasSignatures` de CADA saída `== false` é um invariante
    /// assertado por teste. TODOS os ranges são validados (índices + `to >= from`) ANTES de
    /// construir qualquer saída — um range inválido no meio da lista nunca deixa resultados
    /// parciais no `IReadOnlyList` devolvido.
    IReadOnlyList<byte[]> SplitByRanges(byte[] pdf, IReadOnlyList<(int from, int to)> ranges);

    // --- Task 3 (Plano 3b): costura de rotação (requisito de 1ª ordem, ledger da revisão da Task 2) ---
    //
    // ACHADO (provado por RotatePages_PageWithAnnotation_AnnotationSurvivesAndPageRendersNonBlank, Task
    // 2): `/Rotate` é um atributo de EXIBIÇÃO — o `/Rect` de uma anotação nunca muda quando a página
    // gira; é o LEITOR de PDF (PDFium, via PdfDocumentRenderer) que aplica a rotação a TODO o conteúdo
    // (texto + anotações) na hora de desenhar. Isso significa `Session.PageSizes`/hit-tests/drags no App
    // (que só conhecem o quadro ROTACIONADO do PDFium) e os retângulos de `AnnotationData` (que o iText
    // grava/lê no quadro NÃO-ROTACIONADO) deixam de bater assim que uma página tem `/Rotate != 0`.
    //
    // DECISÃO (registrada no relatório da Task 3, Plano 3b): v1 restringe — interação de anotação fica
    // DESLIGADA em página girada (hit-test nulo, ferramentas de colocação viram no-op com aviso pt-BR),
    // em vez de compor a transformação de rotação nos 2 primitivos de conversão do App
    // (TextSelection.ScreenToPagePoint/PageViewModel.PointRectToScreenRect) e no clamp — a correção
    // completa fica registrada como item de backlog nomeado (não implementada nesta task). Este método
    // é a SUPERFÍCIE DE LEITURA mínima que a v1 precisa: o App (DocumentViewModel) usa isto para saber
    // QUAL página está girada e recusar a interação — nunca escreve nada, sem gate de assinatura (mesma
    // classe de "leitura pura" que ReadAnnotations/HasSignatures já são).
    /// Rotação atual (`/Rotate`) de cada página, normalizada para 0/90/180/270, na ordem do documento.
    /// Documento sem nenhuma página girada -> lista de zeros. Leitura pura, sem gate de assinatura.
    IReadOnlyList<int> GetPageRotations(byte[] pdf);

    // --- Task 5 (Plano 3b): sumário (bookmarks) -------------------------------------------------
    //
    // GATE DE ASSINATURA: SEM gate — mesma política de GetPageRotations/ReadAnnotations/HasSignatures
    // acima (leitura pura, `pdf` nunca é escrito). A precondição de assinatura (GuardAgainstSignedDocument)
    // só se aplica a operações que MUTAM o `pdf` recebido — ver bloco de decisão de gate no topo desta
    // interface. Confirmado por teste: `ReadOutline` lê `fixture-carimbo.pdf` (documento assinado) sem
    // lançar, mesmo padrão de `ReadAnnotations`/`HasSignatures` contra a mesma fixture.
    //
    // HIPÓTESE + RESOLUÇÃO DE DESTINO (reconciliado por reflection sobre itext.kernel.dll 9.7.0 + sonda
    // empírica num probe do PoC — ver task-5-report.md, seção "Reconciliação"):
    //   - `PdfDocument.GetOutlines(false)` devolve a raiz virtual da árvore (nunca ela mesma faz parte
    //     do resultado — só `GetAllChildren()` dela, recursivamente, vira `IReadOnlyList<OutlineNode>`),
    //     OU `null` quando o documento não tem NENHUM `/Outlines` — achado empírico (não documentado
    //     claramente na API): `ReadOutline` trata `null` como lista vazia.
    //   - `PdfOutline.GetDestination()` devolve `PdfDestination` (namespace `iText.Kernel.Pdf.Navigation`)
    //     ou `null` (nó sem `/Dest` nem `/A` — organizacional puro) -> `PageIndex = null` direto, sem
    //     tentar resolver nada.
    //   - `PdfDestination.GetDestinationPage(IPdfNameTreeAccess names)` resolve QUALQUER subtipo de
    //     destino pro `PdfObject` da página-alvo — inclusive destino NOMEADO (`PdfNamedDestination`/
    //     `PdfStringDestination`, via a árvore `/Dests` do catálogo): `names` vem de
    //     `doc.GetCatalog().GetNameTree(PdfName.Dests)`, CHEAP (mesmo `PdfDocument` já aberto pela
    //     leitura, sem I/O nem parse extra) — não havia motivo pra pular a resolução nomeada como o
    //     brief cogitava como alternativa ("else null + document"). Se o `PdfObject` resolvido não for
    //     um `PdfDictionary` que `doc.GetPage(dict)` reconhece, `PageIndex` fica `null` (destino
    //     quebrado/inesperado tratado como "sem página", nunca derruba a leitura da árvore inteira).
    //
    // ACHADO EMPÍRICO (sonda, task-5-report.md): página-alvo de um bookmark REMOVIDA via `DeletePages`
    // (que chama `doc.RemovePage` em modo stamping, reader+writer) — o PRÓPRIO iText já PODA da árvore,
    // na hora da remoção, o nó de outline cujo destino apontava pra aquela página; o nó inteiro
    // desaparece (não sobra com `PageIndex = null`). `ReadOutline` não precisa de nenhum tratamento
    // especial pra esse caso — a árvore que ele lê de um PDF pós-`DeletePages` já vem consistente por
    // conta do próprio iText, nunca com uma referência pendurada pra uma página que não existe mais.
    //
    // ACHADO EMPÍRICO 2 (revisão pós-Task 5, item Important — task-5-report.md, seção "## Fix"):
    // página-alvo de um bookmark MOVIDA via `MovePage` (que chama `doc.MovePage`, reordena a árvore de
    // páginas SEM remover/recriar o dicionário da página) — o destino sobrevive normalmente (o
    // `PdfDictionary` da página é o MESMO objeto, só reordenado; `GetPageNumber` reflete a posição
    // NOVA). `PageIndex` do nó movido passa a refletir o índice de destino de `MovePage`, e os DEMAIS
    // bookmarks deslocam pela mesma semântica de "list splice" já provada pro conteúdo de página em
    // `MovePage_BranchAndBoundaryCoverage_...` (Editing.Tests) — ver
    // `ReadOutline_MovePageRelocatesBookmarkTarget_IndexesShiftConsistently`.
    //
    // LIMITE DE PROFUNDIDADE (revisão pós-Task 5, item Important — defesa em profundidade): PDFs de
    // origem EXTERNA podem ter um `/Outlines` malformado (cíclico, ou simplesmente aninhado ALÉM do
    // razoável) — `ReadOutline` para de descender além de 64 níveis (`PdfEditor.MaxOutlineDepth`),
    // devolvendo o nó no limite com `Children` vazio em vez de recursar infinitamente
    // (`StackOverflowException` não é capturável em .NET — derrubaria o processo). 64 é generoso pra
    // qualquer sumário real; nenhum documento de uso comum passa de uma dezena de níveis.
    IReadOnlyList<OutlineNode> ReadOutline(byte[] pdf);

    // --- Task 1 (Plano 3c): motor de formulários AcroForm --------------------------------------
    //
    // GATE DE ASSINATURA: `ReadFormFields`/`HasXfa` SEM gate — leitura pura, mesma política uniforme
    // de `ReadAnnotations`/`HasSignatures`/`GetPageRotations`/`ReadOutline` acima (`pdf` nunca é
    // escrito). `SetFormFields`/`FlattenForm` COM gate (`GuardAgainstSignedDocument`) — mutam o `pdf`
    // recebido como parâmetro alvo, mesmo raciocínio de defesa em profundidade de
    // AddAnnotation/RotatePages/etc.: editar valores ou achatar campos de um documento assinado
    // invalidaria a assinatura (spec ICP-Brasil §5.2). Assinatura digital em si é um CAMPO de
    // formulário (`/FT /Sig`) — por isso este bloco vem depois de `HasSignatures`/`StripSignatures`
    // acima, que já tratam esse caso especial; preencher/achatar um documento assinado fica DEFERIDO
    // ao Plano 4 (decisão de escopo registrada no brief desta task) — o gate aqui é só a recusa +
    // mensagem pt-BR, não uma tentativa de suportar o caso.
    //
    // HIPÓTESE + RECONCILIAÇÃO (reflexão sobre itext.forms.dll 9.7.0 do cache NuGet + sonda ao vivo —
    // ver task-1-report.md):
    //   - `PdfAcroForm.GetAcroForm(doc, false)` -> `null` quando o documento não tem NENHUM AcroForm
    //     (confirmado empiricamente) -> `ReadFormFields` trata como lista vazia, mesmo padrão de
    //     `ReadOutline`/`GetOutlines(false)` nulo.
    //   - `GetAllFormFields()` devolve `IDictionary<string, PdfFormField>` (`LinkedDictionary`,
    //     ordem de inserção preservada) — cada valor é a subclasse concreta real
    //     (`PdfTextFormField`/`PdfButtonFormField`/`PdfChoiceFormField`/`PdfSignatureFormField`).
    //     Checkbox E radio são AMBOS `PdfButtonFormField` (`/FT /Btn`) — distinguidos por
    //     `IsRadio()`/`IsPushButton()` (nenhum dos 2 -> Checkbox); Combo/ListBox são AMBOS
    //     `PdfChoiceFormField` (`/FT /Ch`) — distinguidos por `IsCombo()`.
    //   - Nome do estado "on" do checkbox: NÃO é uma constante fixa ("Yes") — é o que quer que
    //     `SetValue` grave (sonda ao vivo: `SetValue("Yes")` produziu `/AP/N` com chaves
    //     `[Off, Yes]`, e `GetAppearanceStates()` devolve exatamente isso). `ReadFormFields` lê o
    //     nome real via `GetAppearanceStates()`, nunca assume "Yes".
    //   - Export values do radio: `GetAppearanceStates()` no campo PAI (grupo, não cada kid)
    //     devolve a UNIÃO dos estados de TODOS os widgets-filho (sonda ao vivo, grupo com kids "M"/
    //     "F": `[M, Off, F]`) — não precisa iterar `GetKids()` manualmente para montar `Options`.
    //   - `SetValue(string)` de 1 argumento REGENERA a aparência por DEFAULT (sonda ao vivo:
    //     `/AP`/`/AP/N` presentes após `SetValue` sem o 2º argumento `generateAppearance`) — ao
    //     contrário do que o brief cogitava como precisando de um `SetGenerateAppearance` explícito;
    //     `SetFormFields` não liga nada explicitamente, confia no default.
    //   - `FlattenFields()` (sem argumentos) remove o AcroForm inteiro quando não sobra nenhum campo
    //     (sonda ao vivo: `PdfAcroForm.GetAcroForm(doc, false)` pós-flatten devolve `null`) E remove
    //     o(s) widget(s) da(s) página(s) (contagem de anotações da página cai a 0) — `ReadFormFields`
    //     pós-flatten fica vazia por CONSTRUÇÃO (mesmo caminho "AcroForm nulo -> lista vazia" acima),
    //     não por um caso especial.
    //   - Detecção XFA: **NÃO** usa `PdfAcroForm.GetXfaForm()`/`HasXfaForm()`/`XfaForm.IsXfaPresent()`
    //     — ACHADO EMPÍRICO (surpresa, sonda ao vivo): `PdfAcroForm.GetAcroForm(doc, false)` relendo
    //     um documento cujo AcroForm já tem QUALQUER valor sob `/XFA` (mesmo um array vazio, dummy)
    //     LANÇA `PdfException` ("Root element is missing") — o construtor de `PdfAcroForm` tenta
    //     parsear `/XFA` como XML incondicionalmente sempre que a chave existe, então até `HasXfa`
    //     ficaria inutilizável para o próprio caso que deveria detectar (um PDF com XFA malformado/
    //     incompleto, que é exatamente o tipo de arquivo real que existe por aí). `HasXfa` inspeciona
    //     o dicionário CRU (`doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm)?.
    //     ContainsKey(PdfName.XFA)`), nunca instancia `PdfAcroForm` — detector puro de PRESENÇA da
    //     chave, sem depender do conteúdo ser XML válido.
    //   - Validação de valor (combo/radio): iText NÃO valida — `SetValue`/`SetListSelected` aceitam
    //     qualquer string, mesmo fora das opções definidas (sonda ao vivo, confirmado para os 2
    //     caminhos) — a checagem "`ArgumentException` pt-BR pra valor fora das opções" em
    //     `SetFormFields` é responsabilidade DESTE módulo, não algo que o iText garante de graça.
    //
    // ÍNDICES: `PageIndex` 0-based, mesma convenção do resto do contrato.
    //
    // REVIEW (Task 1 fix) — CONTRATO XFA PINADO nos 3 métodos abaixo que NÃO são o detector: um
    // documento com formulário XFA (`HasXfa` verdadeiro) faz `ReadFormFields`/`SetFormFields`/
    // `FlattenForm` lançarem `PdfEditingException` — achado empírico (não uma decisão deliberada de
    // design nova, mas um comportamento REAL que precisa estar documentado e testado, não só
    // incidental): os 3 chamam `PdfAcroForm.GetAcroForm(doc, false)` internamente, que LANÇA
    // `PdfException` ao tentar parsear `/XFA` como XML (ver HIPÓTESE de `HasXfa` acima) — capturado
    // pelo `catch (ITextException)` genérico de cada método e envolvido em `PdfEditingException`
    // (mesmo canal neutro de qualquer outra falha do iText, `WrapGeneric`). Chame `HasXfa` ANTES de
    // qualquer um dos 3 pra decidir se o documento é editável por este módulo (formulário XFA nunca
    // é — só o AcroForm tradicional).
    /// Leitura pura de TODOS os campos de formulário (AcroForm) do documento, sem gate de assinatura
    /// (política uniforme — ver bloco de decisão acima). Documento sem NENHUM AcroForm -> lista
    /// vazia (nunca `null`). Documento XFA (`HasXfa` verdadeiro) -> `PdfEditingException` (ver bloco
    /// XFA acima) — chame `HasXfa` antes.
    IReadOnlyList<FormFieldData> ReadFormFields(byte[] pdf);
    /// `true` quando o AcroForm do documento tem uma entrada `/XFA` (formulário dinâmico XFA,
    /// incompatível com o preenchimento AcroForm tradicional que este módulo escreve) — detector de
    /// PRESENÇA da chave, não de validade do conteúdo (ver HIPÓTESE acima). Leitura pura, sem gate.
    /// Único dos 4 métodos desta seção que NUNCA lança por causa de XFA (é o próprio detector — ver
    /// bloco XFA acima).
    bool HasXfa(byte[] pdf);
    /// Define o valor de 1+ campos pelo nome (`values`: nome -> valor). GATE de assinatura. Campo
    /// citado em `values` que não existe no documento -> `ArgumentException` pt-BR NOMEANDO o campo;
    /// campo `IsReadOnly` -> `ArgumentException` pt-BR recusando NOMEANDO o campo; campo
    /// `FormFieldType.Other` (push button ou campo de assinatura) -> `ArgumentException` pt-BR
    /// NOMEANDO o campo (review, Task 1 fix: nenhum dos 2 é preenchível por este contrato — sem esta
    /// recusa, `SetValue` num push button descarta o valor SILENCIOSAMENTE, e num campo de assinatura
    /// grava a string crua em `/V`, um risco real porque o Plano 4 vai assinar esses mesmos
    /// placeholders); valor fora das opções válidas de um campo Radio/Combo/ListBox ->
    /// `ArgumentException` pt-BR (Checkbox/Text não são validados contra `Options` — ver XML doc de
    /// `FormFieldData.Options`, que para Checkbox exclui "Off" de propósito, e "Off" continua sendo
    /// um valor legítimo de desmarcar). TODAS as entradas de `values` são validadas (existência +
    /// readonly + tipo preenchível + opção) ANTES de escrever QUALQUER campo — mesmo espírito de
    /// `RotatePages`/`DeletePages`/`SplitByRanges` (nunca aplica metade de um pedido e falha na
    /// outra metade). Documento XFA (`HasXfa` verdadeiro) -> `PdfEditingException` (ver bloco XFA
    /// acima) — chame `HasXfa` antes.
    byte[] SetFormFields(byte[] pdf, IReadOnlyDictionary<string, string> values);
    /// Achata (flatten) todos os campos de formulário do documento — os campos deixam de ser
    /// editáveis, seus valores viram conteúdo estático da página (mesma aparência visual — appearance
    /// stream do widget "impressa" na página), e os widgets/o AcroForm são removidos
    /// (`ReadFormFields` pós-flatten devolve lista vazia). GATE de assinatura. Documento sem NENHUM
    /// AcroForm -> no-op (mesmo espírito de `StripSignatures`), nunca lança. Documento XFA (`HasXfa`
    /// verdadeiro) -> `PdfEditingException` (ver bloco XFA acima) — chame `HasXfa` antes.
    byte[] FlattenForm(byte[] pdf);

    // --- Task 1 (Plano 7): motor ImageToPdf ------------------------------------------------------
    //
    // ARQUITETURA (decisão de vínculo, brief do Plano 7): imagens convertem pra PDF NA FRONTEIRA —
    // este método é a ÚNICA costura de conversão do app; tudo rio abaixo (visualizador/sessão/gates)
    // só enxerga PDFs depois disso. iText confinado aqui, mesma fronteira AGPL de sempre (AgplGuardTests).
    //
    // HIPÓTESE + RECONCILIAÇÃO EMPÍRICA (probe project isolado — mesmo método das Tasks 8/9 do Plano
    // 3a — ver task-1-report.md, Plano 7, para o trilho completo):
    //   - EXIF Orientation: `ImageDataFactory.Create` NÃO honra o tag Orientation — achado empírico:
    //     uma fixture com Orientation=6 (pixels pré-rotacionados 90° CCW) reportou W=150/H=200 (os
    //     pixels CRUS armazenados), NUNCA W=200/H=150 (que seria "já corrigido"). `ImageToPdf` lê o
    //     tag Orientation com um parser compacto de APP1/TIFF (sem pacote novo — só bytes crus) e
    //     aplica a correção via MATRIZ DE TRANSFORMAÇÃO no desenho da imagem (`PdfCanvas.ConcatMatrix`
    //     — ver `PdfEditor.ComputeRotationMatrix`), NUNCA via `PdfPage.SetRotation`. REVISÃO PRÉ-MERGE
    //     (I3, "o /Rotate collision"): a 1ª versão usava `SetRotation`, que fazia CADA foto convertida
    //     com EXIF != 1 nascer como "página girada" — e o gate de rotação do Plano 3b (ver
    //     `GetPageRotations` abaixo: "interação de anotação fica DESLIGADA em página girada") passava a
    //     bloquear anotação/assinatura em qualquer foto de celular convertida, quebrando o fluxo real
    //     "foto do WhatsApp -> assinar". Fix por construção: a página nasce DIRETO no tamanho CORRIGIDO
    //     (upright) com `/Rotate` SEMPRE 0 — `GetPageRotations(ImageToPdf(...))[0] == 0` é invariante
    //     assertado por teste, MESMO com EXIF Orientation=6. Confirmado empiricamente (render via
    //     PDFium, motor independente do iText que escreveu) que a matriz produz cores de canto
    //     IDÊNTICAS ao render da MESMA foto sem EXIF nenhum — a correção visual continua funcionando,
    //     só o MECANISMO mudou.
    //   - DPI: `ImageData.GetDpiX()/GetDpiY()` devolvem 0 quando o arquivo não tem segmento de
    //     densidade (JFIF APP0 pro JPEG) — confirmado removendo o APP0 de um JPEG real. Fallback pra
    //     96 quando <= 0 (ausente ou explicitamente zero).
    //   - PNG com canal alpha: `ImageDataFactory.Create` + `PdfCanvas.AddImageFittedIntoRectangle`
    //     gera `/SMask` automaticamente no XObject de imagem resultante (confirmado inspecionando o
    //     dicionário cru) — PDFium compõe o SMask corretamente: região TOTALMENTE transparente
    //     (alpha=0, mesmo sobre RGB preto de propósito) renderiza BRANCA (fundo da página), nunca preta.
    //   - JPEG CMYK: não construível sinteticamente sem pacote novo (o encoder JPEG do GDI+/System.
    //     Drawing só escreve YCbCr/RGB). Reflexão sobre itext.io.dll 9.7.0 mostra que `JpegImageData`
    //     TEM campos internos de suporte a CMYK (`colorEncodingComponentsNumber`, `colorTransform`,
    //     `inverted`, `decode`) — sugere que o caminho de EMBUTIR provavelmente funciona — mas a
    //     RENDERIZAÇÃO via PDFium/Docnet de um `/DeviceCMYK`+`/Decode` embutido num JPEG é INTESTÁVEL
    //     sem essa fixture real. Decisão FAIL-CLOSED (brief): `ImageToPdf` detecta JPEG CMYK por
    //     varredura CRUA do marcador SOF (nº de componentes de cor == 4), independente do iText, e
    //     RECUSA tipado — nunca arrisca produzir um PDF que renderize errado silenciosamente.
    //
    /// Converte 1 imagem (JPG ou PNG) num PDF de 1 página do TAMANHO DA IMAGEM
    /// (pt = px × 72 / DPI; DPI ausente/zero no metadado → fallback 96, com um PISO mínimo de tamanho
    /// de página — ver `PdfEditor` — pra imagens degeneradas tipo 1x1px). Foto JPEG com EXIF
    /// Orientation != 1 é corrigida via MATRIZ DE TRANSFORMAÇÃO no desenho da imagem (ver bloco acima)
    /// — abre UPRIGHT, e a página resultante NUNCA gira (`/Rotate` sempre 0 — `GetPageRotations` do
    /// resultado é sempre `[0]`, mesmo para fotos com EXIF Orientation != 1: o gate de rotação do
    /// Plano 3b nunca bloqueia anotação/assinatura numa foto convertida). PNG com canal alpha preserva
    /// transparência (SMask). `bytes` que não é JPEG/PNG por magic bytes, corrompido (magic bytes ok
    /// mas iText não decodifica), JPEG CMYK, ou imagem além do teto de pixels suportado (ver
    /// `PdfEditor.MaxImagePixels` — decidido por varredura de HEADER, ANTES de decodificar, pra nunca
    /// pagar o custo do decode numa imagem que vai ser recusada) → `PdfEditingException` pt-BR
    /// nomeando o motivo — a mensagem de "não é JPG/PNG válido" sempre NOMEIA os formatos suportados.
    byte[] ImageToPdf(byte[] image);

    /// Sniff por magic bytes (JPEG: `FF D8`; PNG: `89 50 4E 47`) — SEM decodificar a imagem, sem
    /// tocar iText — usado pela camada de App (Tasks 2-4 do Plano 7) pra filtrar/validar seleção de
    /// arquivo sem enxergar iText (mesmo espírito de `HasXfa`: detector de PRESENÇA da assinatura de
    /// formato, não de validade do conteúdo — um JPEG corrompido ainda começa com `FF D8` e volta
    /// `true` aqui; só `ImageToPdf` decodifica de verdade e pode recusar por corrupção). `false` pra
    /// bytes nulos/vazios/curtos demais ou qualquer assinatura que não seja JPEG/PNG.
    bool IsSupportedImage(byte[]? bytes);

    // --- Task 3 (Plano 7): imagem-como-anotação ("🖼 Imagem" — click-to-place sobre a página) --------
    //
    // ACHADO (brief desta task): o caminho AddAnnotation/AnnotationKind.ImageStamp (Task 9, Plano 3a —
    // consumido por DocumentViewModel.PlaceStampAtAsync, o MESMO mecanismo que "🖼 Imagem" reusa) foi
    // implementado ANTES do teto de pixels e da correção de EXIF existirem (Task 1, Plano 7,
    // `ImageToPdf`) — nunca ganhou nenhum dos dois retroativamente. Os 2 métodos abaixo expõem, pro
    // App decidir ANTES do modo de colocação, exatamente a MESMA lógica que `ImageToPdf` já usa
    // internamente — sem duplicar os parsers (JPEG SOF/PNG IHDR resilientes a overflow; TIFF/IFD0 de
    // EXIF) numa 2ª cópia do lado do App, que divergiria silenciosamente na 1ª correção futura de
    // qualquer um dos dois.

    /// `true` quando `bytes` está DENTRO do mesmo teto de pixels que `ImageToPdf` aplica
    /// (`PdfEditor.MaxImagePixels`, 50MP) — decidido por VARREDURA DE HEADER (JPEG: marcador SOF; PNG:
    /// IHDR), sem decodificar a imagem, mesma técnica/mesmos parsers do teto de `ImageToPdf` (nunca
    /// reimplementados aqui — reusados). Usado pela camada de App (Task 3, Plano 7 — "🖼 Imagem") pra
    /// recusar uma imagem grande demais ANTES do modo de colocação, sem pagar o custo de decodificar
    /// (WPF, do lado do App) uma imagem que vai ser recusada de qualquer forma. FAIL-OPEN: bytes nulos/
    /// vazios/com header ilegível ou truncado (não dá pra ler dimensões com confiança) -> `true` — este
    /// método só existe pra pegar "grande demais SABIDO"; um formato desconhecido ou corrompido é
    /// responsabilidade de `IsSupportedImage`/do decode real, não deste teto.
    bool IsWithinImagePixelLimit(byte[] bytes);

    /// Rotação EXIF (tag Orientation, 0x0112) de um JPEG, em graus HORÁRIOS normalizados (0/90/180/270)
    /// — mesmo parser puro (sem iText, sem decodificar pixels) que `ImageToPdf` já usa internamente pra
    /// corrigir fotos de celular via matriz de transformação (nunca reimplementado aqui). Exposto pro
    /// App (Task 3, Plano 7 — "🖼 Imagem") pré-normalizar os PIXELS de uma foto ANTES de embutir como
    /// anotação: `AddAnnotation`/`AnnotationKind.ImageStamp` nunca aplicou nenhuma correção de EXIF
    /// (diferente de `ImageToPdf`) — a correção de pixels em si é 100% App-side (WPF, que não pode
    /// entrar em `mPdf.Editing` — ver `AgplGuardTests`/`mPdf.Editing.csproj`); este método só expõe a
    /// LEITURA do ângulo. PNG (sem EXIF), JPEG com Orientation 1/ausente, ou `bytes` nulo/curto/
    /// malformado demais pra ler com confiança -> `0` (nunca lança — mesma defesa em profundidade do
    /// parser interno). 2/4/5/7 (espelhamento) -> `0`, mesmo escopo v1 de `ImageToPdf`.
    int ReadJpegExifOrientation(byte[] image);

    /// FIX (revisão pós-merge da Task 3, Plano 7 — "🖼 Imagem"): `true` quando `bytes` é um JPEG com 4
    /// componentes de cor no marcador SOF (CMYK/YCCK) — mesmo detector que `ImageToPdf` já aplica
    /// inline (varredura de SOF, decisão fail-closed do Task 1: render CMYK embutido via PDFium é
    /// intestável sem fixture real), agora exposto pro App recusar ANTES do modo de colocação no
    /// caminho `AddAnnotation`/`AnnotationKind.ImageStamp` — esse caminho (Task 9, Plano 3a, reusado
    /// por `DocumentViewModel.ToggleStampTool`/`ToggleImageTool`) nunca teve recusa de CMYK nenhuma
    /// (implementado ANTES do detector existir, nunca retrofitado — mesma lacuna histórica do teto de
    /// pixels, ver `IsWithinImagePixelLimit`). Varredura de HEADER pura (mesmo parser
    /// `TryReadJpegSofInfo`, nunca reimplementado aqui), sem decodificar nada. `bytes` que não é JPEG
    /// por magic bytes, nulo/curto demais, ou com SOF ilegível/truncado -> `false` (sem opinião — nunca
    /// "detecta" CMYK sem confiança nenhuma sobre o header). ACEITO (revisão): a GALERIA de carimbos
    /// (`ToggleStampTool`, Task 9/Plano 3a) continua sem este gate — só `ToggleImageTool` (Task 3,
    /// Plano 7) o consulta; a mesma correção pra galeria fica ledgerada, não implementada aqui.
    bool IsCmykJpeg(byte[] bytes);

    // --- Task 3 (Plano 15): camada de texto invisível de OCR ------------------------------------
    //
    // GATE DE ASSINATURA: COM gate (`GuardAgainstSignedDocument`) — grava conteúdo novo em páginas
    // do `pdf` recebido como alvo, mesmo raciocínio de defesa em profundidade de
    // AddAnnotation/RotatePages/SetFormFields (editar um documento assinado invalidaria a assinatura,
    // spec ICP-Brasil §5.2); o App (Task 4) captura `PdfSignedDocumentException` e oferece o mesmo
    // fluxo "cópia não-assinada" das outras edições.
    //
    // MAPEAMENTO px→pt + ROTAÇÃO (mesma disciplina de frame do carimbo do Plano 3b/4 —
    // `/Rotate` é atributo de EXIBIÇÃO, o conteúdo é gravado no frame NÃO-rotacionado do MediaBox;
    // ver `PdfEditor.ApplyOcrTextLayer`/`ComputeOcrTextMatrix`): o bitmap de OCR foi rasterizado NA
    // orientação exibida (o `PdfDocumentRenderer`/PDFium já aplica `/Rotate`), então para uma página
    // com `/Rotate` 90/270 a largura-fonte px corresponde à ALTURA do MediaBox e vice-versa. Cada
    // caixa (px, topo-esquerda) é convertida para um ponto do MediaBox (pt, inferior-esquerda) e o
    // texto é gravado com uma matriz de texto que aplica a rotação INVERSA (-`/Rotate`) — assim,
    // quando o leitor exibe a página girada, o texto aparece alinhado sobre a imagem original.
    //
    // INVISÍVEL: cada palavra é gravada com `PdfCanvas.BeginText()` +
    // `SetTextRenderingMode(TextRenderMode.INVISIBLE)` (render mode 3 — não pinta tinta nenhuma),
    // fonte padrão Helvetica, tamanho ajustado à ALTURA da caixa em pt. Extraível (busca/cópia/
    // Ctrl+F) mas invisível no render (diff de pixel ~zero — provado por teste).
    /// Grava uma camada de texto INVISÍVEL (render mode 3) por página a partir de `layers`, tornando
    /// um PDF-imagem pesquisável/copiável SEM alterar sua aparência. Caixa de texto vazio/só-espaços
    /// é ignorada. `layers` vazio -> devolve um PDF equivalente (reprocessado, nunca os bytes
    /// originais intocados), sem lançar. `PageIndex` fora do intervalo -> `ArgumentOutOfRangeException`
    /// pt-BR (mesma disciplina de `ValidatePageIndex`), antes de gravar qualquer coisa. Documento
    /// assinado -> `PdfSignedDocumentException` (ver bloco acima).
    byte[] ApplyOcrTextLayer(byte[] pdf, IReadOnlyList<OcrTextLayer> layers);
}

public static class PdfEditorFactory
{
    public static IPdfEditor Create() => new PdfEditor();
}

/// Canal de erro NEUTRO (revisão pós-M11): qualquer falha do iText ao processar o PDF (arquivo
/// corrompido, xref inválido, etc.) chega ao chamador como uma exceção deste namespace — nunca como
/// um tipo `iText.*` cru, que quebraria a fronteira neutra do contrato tão quanto um `using iText`
/// vazando para o App. A exceção original do iText é preservada em `InnerException`.
public class PdfEditingException : Exception
{
    public PdfEditingException(string message, Exception? inner = null) : base(message, inner) { }
}

/// PDF protegido por senha — caso mais específico e mais ACIONÁVEL de `PdfEditingException` (o
/// chamador pode pedir a senha ao usuário em vez de só reportar falha genérica).
public sealed class PdfPasswordRequiredException : PdfEditingException
{
    public PdfPasswordRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}

/// Documento já contém assinatura(s) — `AddAnnotation`/`RemoveAnnotation` recusam editar (ver
/// precondição documentada em `IPdfEditor`). Tipada (revisão pós-M11, rodada 2) para a Task 5
/// capturar especificamente e oferecer "Editar uma cópia", sem precisar inspecionar a mensagem de
/// uma `InvalidOperationException` genérica (que outras falhas também poderiam lançar).
public sealed class PdfSignedDocumentException : PdfEditingException
{
    public PdfSignedDocumentException(string message, Exception? inner = null) : base(message, inner) { }
}
