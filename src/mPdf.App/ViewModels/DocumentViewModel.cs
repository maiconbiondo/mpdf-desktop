using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mPdf.App.Services;
using mPdf.Documents;
// Task 6 (Plano 3a): 2º uso de mPdf.Editing dentro de src/mPdf.App (1º foi MainViewModel, Task 5) — só
// o CONTRATO neutro (IPdfEditor/PdfEditorFactory/AnnotationData/AnnotationKind/PdfQuad/exceções),
// nunca um tipo iText (guardado por AgplGuardTests + PrivateAssets=compile em mPdf.Editing.csproj).
using mPdf.Editing;
// Task 4 (Plano 15): 1º uso de mPdf.Ocr dentro de src/mPdf.App — só o contrato NEUTRO (IOcrEngine/
// OcrEngineResult/OcrWord/TesseractOcrEngine). O App é a raiz de composição do OCR: renderiza via
// mPdf.Rendering (o OcrPageRasterizer da T2), chama IOcrEngine (T1) e mapeia OcrEngineResult->OcrTextLayer
// (tipo neutro de mPdf.Editing, T3) — nenhum tipo de Tesseract cruza pra Editing. Guardado por AgplGuardTests.
using mPdf.Ocr;
using mPdf.Rendering;
using System.Threading;
// Task 3 (Plano 4): 1º uso de mPdf.Signing dentro de DocumentViewModel — só o contrato NEUTRO
// (ISigningEngine/SignRequest/SignatureInfo/DocMdpLevel/VisibleStampSpec/SigningCertificateInfo/
// exceções tipadas), nunca um tipo iText (mPdf.Signing.csproj usa PrivateAssets=compile — ver
// mPdf.App.csproj).
using mPdf.Signing;
using System.Security.Cryptography.X509Certificates;

namespace mPdf.App.ViewModels;

/// Ferramenta de colocação ativa na toolbar (Task 7, Plano 3a) — mutuamente exclusiva por construção
/// (só um campo `ActiveTool` no VM, nunca 2 bools independentes que pudessem ficar os 2 `true`).
/// `None` = nenhuma ferramenta ligada (clique na página faz SELEÇÃO/hit-test de anotação existente, ou
/// seleção de texto se não houver nenhuma anotação sob o clique — comportamento pré-Task 7 preservado).
/// Ink/Rectangle/Line/Arrow (Task 8, Plano 3a) são ferramentas de ARRASTO (não clique único como
/// StickyNote/FreeText) — a View decide qual fluxo de mouse usar olhando este mesmo campo (ver
/// `PdfViewerControl.Page_MouseLeftButtonDown`), mas a exclusividade mútua é a MESMA garantia de
/// construção pros valores não-`None`. ImageStamp (Task 9, Plano 3a) é clique único como StickyNote/
/// FreeText, mas sem diálogo — os bytes da imagem a colocar vêm da galeria (ver `ToggleStampTool`/
/// `PlaceStampAtAsync`), não de um prompt de texto. SignatureStamp (Task 3, Plano 4) ativa o modo de
/// colocação do carimbo de assinatura, mas NÃO é uma anotação: o mouse-down na página só decide ONDE a
/// caixa ajustável do carimbo começa a ser desenhada (Task 1/2, Plano 8 —
/// `BeginStampBoxPlacementAsync`/`ConfirmSignatureStampAsync`); o commit em si nunca passa por
/// `ApplyEdit`/`_editor`, vai direto pro motor de assinatura (`mPdf.Signing`) via `Session.CommitSigned`.
public enum AnnotationTool
{
    None,
    StickyNote,
    FreeText,
    Ink,
    Rectangle,
    Line,
    Arrow,
    ImageStamp,
    SignatureStamp,
}

/// Fase da caixa ajustável do carimbo de assinatura (Task 1, Plano 8) — máquina de estados PARALELA a
/// `AnnotationTool`: `ActiveTool == SignatureStamp` continua sendo o gate de "modo de colocação". `None`
/// = nenhuma caixa em andamento (o mouse ainda não desceu na página — ver `BeginStampBoxPlacementAsync`,
/// Task 2). `Drawing` = usuário arrastando o retângulo inicial (mouse ainda pressionado). `Adjusting` =
/// retângulo válido (≥ `MinStampBoxWidthPt`x`MinStampBoxHeightPt`) solto — alças de redimensionar/mover
/// E os botões flutuantes "Assinar aqui"/"Cancelar" ficam disponíveis (Task 2: "Assinar aqui" dispara o
/// motor de verdade via `ConfirmSignatureStampAsync`).
public enum StampPlacementPhase
{
    None,
    Drawing,
    Adjusting,
}

/// As 8 alças de redimensionar da caixa ajustável (Task 1, Plano 8) — 4 cantos (redimensionam 2 eixos)
/// + 4 bordas (redimensionam 1 eixo só). Ver `DocumentViewModel.ResizeBoxByHandle` pro mapeamento
/// fixo alça->eixo bruto (nunca recalculado a partir do retângulo NORMALIZADO corrente — é exatamente
/// esse mapeamento fixo que sustenta a inversão ao cruzar, ver doc XML de lá).
public enum StampBoxHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// Resultado de `DocumentViewModel.ConfirmStampBox()` — página + retângulo final (em pontos de página,
/// mesma convenção de `PdfQuad`/`VisibleStampSpec`) escolhidos pelo usuário. Task 1 (Plano 8) só expõe
/// isto pra quem chamar `ConfirmStampBox()` diretamente (teste, ou a View no futuro); o consumo real
/// (montar um `VisibleStampSpec` e disparar o motor de assinatura) é do Task 2 — ver doc XML de
/// `ConfirmStampBox`.
public readonly record struct StampBoxPlacement(int PageIndex, PdfQuad Rect);

public sealed partial class DocumentViewModel : ObservableObject, IDisposable
{
    private const double MinZoom = 0.25, MaxZoom = 4.0;
    private const double FitMarginPx = 24;   // folga visual nas laterais
    private const double PageMarginPx = 12;  // Border Margin="0,6" -> 6 acima + 6 abaixo
    /// Task 8 (Plano 3a, brief): arrasto menor que isto (medido em px de TELA, não pt de página — o
    /// zoom varia) não commita NADA — protege contra "pontinhos"/tremor de clique quase-parado com uma
    /// ferramenta de desenho ativa. Mesmo espírito de `PdfViewerControl.DragThresholdPx` (Task 3/7), só
    /// que este vive no VM de propósito: o brief pede um teste de VM pra este guard especificamente
    /// (`CommitDrawingAsync_DragBelowMinGesture_DoesNotCommit`), o que só é possível medindo em algo que
    /// não depende de uma janela WPF real.
    private const double MinGestureDragPx = 3.0;
    /// Largura MÁXIMA (pontos) de um carimbo de imagem recém-colocado (Task 9, Plano 3a, brief: "natural
    /// image size scaled to max 150pt width, keep aspect"). Ver `NaturalStampSize`.
    private const double MaxStampWidthPt = 150.0;
    /// Largura MÁXIMA (pontos) de uma imagem colocada via "🖼 Imagem" (Task 3, Plano 7, brief: "~200pt
    /// wide, height by aspect") — MAIOR que `MaxStampWidthPt` acima de propósito (decisão do brief:
    /// tamanho default distinto do carimbo de galeria, Task 9/Plano 3a, que fica INALTERADO). Ver
    /// `_pendingStampMaxWidthPt`/`ToggleImageTool`/`NaturalStampSize`.
    private const double MaxPickedImageWidthPt = 200.0;
    private readonly RenderScheduler _scheduler;

    // ---- Task 6 (Plano 3a): Marca-texto/Sublinhado/Riscado -----------------------------------------

    // Cores default do brief — opacas (FF de alfa): a translucência do highlight vem do PRÓPRIO tipo
    // de anotação (herança visual do subtype /Highlight no leitor de PDF), não de um /CA fracionário
    // que este módulo precisasse escrever — ver doc XML de AnnotationData.ColorArgb.
    public const uint ColorAmarelo = 0xFFFFFF00;
    public const uint ColorVerde = 0xFF00FF66;
    public const uint ColorVermelho = 0xFFFF5555;

    private readonly IPdfEditor _editor;
    private readonly AppConfig _config;
    // Mesmo padrão de MainViewModel._notifyError (Task 3): delegate injetável — produção usa MessageBox
    // de verdade, testes injetam um Action<string> que só captura a mensagem, sem travar esperando clique.
    private readonly Action<string> _notifyError;
    // Task 7 (Plano 3a): mesmo padrão de injeção de IConfirmCloseService (Task 3) — produção abre uma
    // janelinha WPF real (Views.AnnotationTextDialog), testes injetam um fake que devolve um texto fixo.
    private readonly IAnnotationTextDialogService _annotationDialog;
    // Task 4 (Plano 3b): propagados pro OrganizerViewModel (Extrair/Inserir precisam de diálogo de
    // arquivo + canal de sucesso) — mesmo padrão de propagação de _config/_notifyError já usado desde a
    // Task 6 (Plano 3a), só que a ORIGEM aqui é MainViewModel.OpenPath (que já tem _dialogs), não este
    // próprio VM (que não abre diálogo de arquivo nenhum sozinho).
    private readonly IFileDialogService _dialogs;
    private readonly Action<string> _notifyInfo;
    // Task 3 (Plano 3c): mesmo padrão de injeção de IConfirmCloseService acima — produção abre um
    // MessageBox real (Sim/Não), testes injetam um fake que devolve confirmado/cancelado fixo, sem
    // travar a sessão de teste esperando um clique. Consultado ANTES do funil (`Session.TryBeginEdit`)
    // em `FlattenForm` — ver doc XML lá pro porquê da ordem (contrato do brief: cancelar não arma nada).
    private readonly IConfirmFlattenService _confirmFlatten;
    // Task 1 (Plano 5): mesmo padrão de injeção de _confirmFlatten acima — produção consulta UiPrompts
    // (MessageBox real Sim/Não), testes injetam um fake. Consultado ANTES de `OpenOrganizer` quando o
    // documento tem mais de `OrganizerScaleWarningPageCount` páginas — ver `OnIsOrganizerOpenChanged`.
    private readonly IConfirmOrganizerScaleService _confirmOrganizerScale;
    // ---- Task 3 (Plano 4): Assinar --------------------------------------------------------------
    // Mesmo padrão de injeção de _confirmFlatten acima — produção consulta UiPrompts (MessageBox/janela
    // real), testes injetam fakes. _listSigningCertificates NÃO é um serviço de UI (não mostra nada,
    // só enumera o repositório do Windows) — por isso não passa pela seam UiPrompts (mesmo espírito de
    // `editor`/PdfEditorFactory.Create() acima: default é a fábrica de PRODUÇÃO do módulo correspondente,
    // não uma janela); testes de VM injetam uma lista fixa sem tocar o repositório real da máquina.
    private readonly IConfirmSaveBeforeSignService _confirmSaveBeforeSign;
    private readonly ISignDialogService _signDialog;
    private readonly ISigningEngine _signingEngine;
    private readonly Func<IReadOnlyList<SigningCertificateInfo>> _listSigningCertificates;
    // Task 4 (Plano 7): "Exportar página como imagem" -- mesmo padrão de injeção via UiPrompts dos
    // diálogos acima (produção abre Views.ExportImageDialog, testes injetam um fake).
    private readonly IExportImageDialogService _exportImageDialog;
    // Task 3 (Plano 16): "Exportar como Word/Excel" -- mesmo padrão de injeção via UiPrompts do diálogo
    // de exportar imagem acima (produção abre Views.ExportDocumentDialog, testes injetam um fake).
    private readonly IExportDocumentDialogService _exportDocumentDialog;
    // Seam de "o documento tem texto pesquisável?" (Task 3, Plano 16). `Func<byte[], bool>` (namespace
    // System, fora da varredura de UiPromptsCoverageTests, mesma isenção estrutural de `rasterizerFactory`):
    // não mostra UI (só render/leitura de texto), sem risco de hang. Default abre um `PdfDocumentRenderer`
    // PRÓPRIO sobre os bytes e reusa `PdfTextSearch.DocumentHasText` (o mesmo sinal do Ctrl+F/OCR); testes
    // injetam um fake determinístico. Usado para o aviso "sem texto -> rode o OCR" ANTES de abrir o diálogo.
    private readonly Func<byte[], bool> _documentHasText;

    // ---- Task 4 (Plano 15): OCR ("Reconhecer texto") -----------------------------------------------
    /// Motor de OCR (contrato neutro `IOcrEngine`, mPdf.Ocr). `null` até o 1º OCR quando NÃO injetado:
    /// `GetOcrEngine` cria um `TesseractOcrEngine` sob demanda e marca `_ownsOcrEngine` (só o que ESTE VM
    /// criou é descartado em `Dispose` — um motor INJETADO pertence a quem o injetou).
    private IOcrEngine? _ocrEngine;
    private bool _ownsOcrEngine;
    /// Fábrica do rasterizador de OCR (T2) por bytes de PDF — default cria um `OcrPageRasterizer` real
    /// (renderer próprio, seam separado do viewer); testes injetam um fake determinístico.
    private readonly Func<byte[], IOcrPageRasterizer> _rasterizerFactory;
    private readonly IOcrProgressService _ocrProgress;
    /// Idiomas do Tesseract (default do produto — por+eng). Constante: v1 não expõe escolha de idioma.
    private const string OcrLanguages = TesseractOcrEngine.DefaultLanguages;

    private const double DefaultStampWidthPt = 180.0, DefaultStampHeightPt = 60.0;
    /// Tamanho MÍNIMO da caixa ajustável do carimbo (Task 1, Plano 8, brief: "60×20pt — legibilidade do
    /// carimbo"). Aplicado nas 2 fases: soltar o arrasto inicial (`EndStampDraw`) abaixo do mínimo NÃO
    /// cancela (fica em Drawing, aviso sutil); redimensionar (`ResizeBoxByHandle`) abaixo do mínimo é
    /// clampado (nunca produz uma caixa menor que isto).
    private const double MinStampBoxWidthPt = 60.0, MinStampBoxHeightPt = 20.0;

    /// Capturado no construtor (exemplar: `PageViewModel._dispatcher`) — ACHADO real (revisão Opus, não
    /// hipotético): disparar `RefreshAnnotationsByPageAsync` como fire-and-forget CRU (`_ =
    /// RefreshAnnotationsByPageAsync();`) fazia `AnnotationsByPage` (um `[ObservableProperty]`, logo
    /// `PropertyChanged`) mudar numa THREAD DE POOL ARBITRÁRIA sempre que o construtor/`OnSessionApplied`
    /// rodava fora de um `SynchronizationContext` de UI ativo — em PRODUÇÃO isso nunca era um problema
    /// (o `await Task.Run` de dentro do método resume automaticamente no `SynchronizationContext` do
    /// Dispatcher da UI, mesmo mecanismo já documentado em `ApplyMarkup`), mas em TESTE xUnit comum
    /// (`[Fact]` sem `Dispatcher.Run()` — só `ViewerIntegrationTests`/`PrintServiceTests` rodam numa
    /// thread STA dedicada) não existe esse contexto: a leitura terminava numa thread QUALQUER, correndo
    /// contra as asserções de OUTRO teste que só tinha a infelicidade de estar `PropertyChanged`-
    /// -inscrito quando o refresh em voo terminava — capturado ao vivo como
    /// `System.InvalidOperationException: Collection was modified` num teste (`IsSignedDocument_...`)
    /// que nem toca `AnnotationsByPage`. Fix: os 2 SITES fire-and-forget (construtor/`OnSessionApplied`)
    /// despacham através deste `Dispatcher` — em produção o comportamento é o MESMO de antes (roda na UI
    /// thread, só que agora explícito em vez de depender do `SynchronizationContext` ambiente); em
    /// teste, sem `Dispatcher.Run()` bombeando a fila, o `BeginInvoke` simplesmente NUNCA dispara —
    /// nenhuma corrida possível, e os testes que precisam do cache atualizado já chamam `await
    /// RefreshAnnotationsByPageAsync()` DIRETO (não passando por este wrapper), que continua 100%
    /// síncrono/determinístico como sempre foi.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    [ObservableProperty] private uint selectedMarkupColorArgb = ColorAmarelo;

    // ---- Task 7 (Plano 3a): Nota adesiva / caixa de texto (criar/editar/mover/excluir + lift) --------

    /// Ferramenta de colocação ativa na toolbar — ver doc XML de `AnnotationTool`.
    [ObservableProperty] private AnnotationTool activeTool = AnnotationTool.None;

    /// Bytes do carimbo escolhido na galeria (Task 9, Plano 3a) OU da imagem escolhida via "🖼 Imagem"
    /// (Task 3, Plano 7, ver `ToggleImageTool`) — só relevante enquanto `ActiveTool ==
    /// AnnotationTool.ImageStamp` (ver `ToggleStampTool`/`ToggleImageTool`/`PlaceStampAtAsync`). Trocar
    /// de ferramenta para outra deixa este campo obsoleto sem limpar (resíduo aceito, inofensivo:
    /// `PlaceStampAtAsync` só o lê quando `ActiveTool` ainda é `ImageStamp`).
    private byte[]? _pendingStampBytes;

    /// Largura MÁXIMA a aplicar em `NaturalStampSize` no PRÓXIMO `PlaceStampAtAsync` — `MaxStampWidthPt`
    /// (galeria) ou `MaxPickedImageWidthPt` ("🖼 Imagem"), conforme QUEM ativou `_pendingStampBytes`
    /// por último (`ToggleStampTool`/`ToggleImageTool`, cada um seta este campo explicitamente ao
    /// ativar — Task 3, Plano 7). Mesmo resíduo aceito de `_pendingStampBytes` acima: só importa
    /// enquanto `ActiveTool == ImageStamp`, nunca precisa de reset em desativação.
    private double _pendingStampMaxWidthPt = MaxStampWidthPt;

    /// Anotação atualmente selecionada (clique, sem ferramenta ativa, num retângulo de
    /// `AnnotationsByPage` — ver `HitTestAnnotation`/`SelectAnnotationAt`). `null` = nada selecionado.
    /// Limpa em QUALQUER `Apply` (`OnSessionApplied` abaixo) — mesmo espírito de `ClearSelection()` já
    /// aplicado à seleção de TEXTO desde a Task 3: depois de uma edição (lift de editar/mover, Del, ou
    /// até um Undo/Redo alheio), os dados por trás do objeto antigo podem ter mudado ou sumido.
    [ObservableProperty] private AnnotationData? selectedAnnotation;

    /// Cache de `ReadAnnotations(Session.Snapshot)`, indexado por página (`AnnotationsByPage[pageIndex]`)
    /// — renovado em BACKGROUND (`Task.Run`, ver `RefreshAnnotationsByPageAsync`) a cada `Session.
    /// Applied` (e uma vez no construtor, pro hit-test funcionar em documentos que JÁ chegam com
    /// anotações). Usado pelo hit-test de seleção (`HitTestAnnotation`) — nunca lido diretamente do
    /// `IPdfEditor` na thread de UI (parse iText é CPU-bound, mesmo motivo de todo `Task.Run` deste VM).
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<AnnotationData>> annotationsByPage = Array.Empty<IReadOnlyList<AnnotationData>>();

    /// Cache de `ReadOutline(Session.Snapshot)` — árvore de sumário/bookmarks (Task 5, Plano 3b),
    /// renovada em BACKGROUND (`Task.Run`, ver `RefreshOutlineAsync`) no construtor e a cada `Session.
    /// Applied`, mesmo exemplar de `AnnotationsByPage` acima (e de `GetPageRotations`/`_pageRotations`).
    /// DIFERENÇA DE PROPÓSITO (decisão registrada no brief, "read-gated or safely-stale?"): SEM gate de
    /// leitura tipo `_annotationsCacheSnapshot` — um clique num nó do sumário só NAVEGA
    /// (`ScrollToPageRequested` com um índice, ver `NavigateToOutlineNode` abaixo), nunca seleciona/edita
    /// nada; o pior caso de clicar durante a janela entre um `Apply` e este refresh terminar é rolar pra
    /// uma página "aproximadamente certa" (a árvore de ANTES da edição mais recente) — SEVERIDADE BAIXA,
    /// aceita por design, nunca um crash (ao contrário do hit-test fantasma que motivou o gate de
    /// `AnnotationsByPage`). `RefreshOutlineAsync` ainda descarta um resultado OBSOLETO por
    /// `ReferenceEquals(Session.Snapshot, snapshot)` — não é um gate de leitura, é só higiene de
    /// concorrência (2 `Task.Run` em voo, o mais lento nunca deve sobrescrever o mais rápido).
    [ObservableProperty] private IReadOnlyList<OutlineNode> outline = Array.Empty<OutlineNode>();

    /// Sumário vazio (documento sem `/Outlines`, ou o 1º refresh ainda em voo) -> estado vazio pt-BR na
    /// aba Sumário ("Este documento não tem sumário.", ver `OutlineView.xaml`). `false` no intervalo
    /// entre o construtor e o 1º `RefreshOutlineAsync` resolver é aceito (mesmo espírito de
    /// `IsPageRotated`/`_pageRotations` default vazio) — a UI mostra o estado vazio por uma fração de
    /// segundo até o cache real chegar, nunca um erro.
    public bool HasOutline => Outline.Count > 0;

    partial void OnOutlineChanged(IReadOnlyList<OutlineNode> value) => OnPropertyChanged(nameof(HasOutline));

    /// GATE DE LEITURA (revisão Opus, C1a — fecha um crash real): o snapshot (`Session.Snapshot`, por
    /// REFERÊNCIA) do qual `AnnotationsByPage` foi construído por ÚLTIMA VEZ. `HitTestAnnotation`
    /// abaixo só serve um resultado quando este campo ainda é o snapshot CORRENTE — enquanto uma
    /// atualização está em voo (ou falhou, ver `RefreshAnnotationsByPageAsync`), o cache é tratado como
    /// "não sabe" (hit-test sempre `null`) em vez de arriscar apontar pra uma anotação que já mudou de
    /// posição ou nem existe mais no PDF vivo. Sem isto: `Del`/mover num clique bem cedo depois de uma
    /// edição (a atualização assíncrona do cache ainda não chegou) podia selecionar uma anotação
    /// FANTASMA — `RemoveAnnotation` então lançava `InvalidOperationException` ("não encontrada"), que
    /// NENHUM catch deste VM capturava; o `AsyncRelayCommand` gerado relança isso em cima do
    /// `Dispatcher` (sem handler em `src/`) -> CRASH do processo, documento sujo perdido. Também fecha
    /// I3 (mover em silêncio pra posição errada): sem seleção não há o que mover.
    private byte[]? _annotationsCacheSnapshot;

    /// COSTURA DE ROTAÇÃO (Task 3, Plano 3b — requisito de 1ª ordem, ledger da revisão da Task 2; ver
    /// XML doc de `IPdfEditor.GetPageRotations`): PDFium relata o quadro ROTACIONADO (PageSizes,
    /// hit-tests, drags) enquanto os retângulos de `AnnotationData` do iText estão SEMPRE no quadro
    /// NÃO-ROTACIONADO (provado empiricamente por `RotatePages_PageWithAnnotation_..." no Task 2 —
    /// `/Rotate` é um atributo de EXIBIÇÃO, o `/Rect` da anotação nunca muda quando a página gira).
    /// DECISÃO v1 (registrada no relatório da Task 3): em vez de compor a transformação de rotação nos
    /// primitivos de conversão (`TextSelection.ScreenToPagePoint`/`PageViewModel.PointRectToScreenRect`)
    /// e no clamp — a correção completa, de alto risco tão tarde no plano —, a interação de anotação
    /// fica DESLIGADA em página girada: `IsPageRotated` abaixo gateia hit-test (`HitTestAnnotation`) e
    /// os 4 pontos de escrita (`PlaceAnnotationAtAsync`/`PlaceStampAtAsync`/`CommitDrawingAsync`/
    /// `ApplyMarkup`). Renovado JUNTO com `AnnotationsByPage`/`_annotationsCacheSnapshot` (mesmo
    /// refresh, mesmo gate) — ver `RefreshAnnotationsByPageAsync`. Backlog nomeado para a transformação
    /// completa: task-3-report.md.
    private IReadOnlyList<int> _pageRotations = Array.Empty<int>();

    /// pt-BR (brief): mostrado quando o usuário tenta colocar/comitar/aplicar uma anotação numa página
    /// com `/Rotate != 0` — ver `IsPageRotated` acima e os 4 chamadores.
    private const string RotatedPageNotice =
        "Página girada — anotações apenas em páginas sem rotação nesta versão.";

    /// `false` (não girada) é o padrão quando `_pageRotations` está vazio (nenhum refresh terminou
    /// ainda — só acontece na fração de segundo entre o construtor e o 1º `RefreshAnnotationsByPageAsync`
    /// resolver). ACHADO (revisão Opus, I1): isto SOZINHO não bastava pros 4 pontos de ESCRITA — ao
    /// contrário de `HitTestAnnotation` (que já checava `ReferenceEquals(_annotationsCacheSnapshot,
    /// Session.Snapshot)` antes de consultar este método), os 4 chamadores de escrita liam
    /// `_pageRotations` DIRETO, sem checar se ainda correspondia ao `Session.Snapshot` CORRENTE.
    /// Delete/Move RE-INDEXAM páginas (a identidade de cada índice muda) — na janela entre
    /// `OnSessionApplied` reconstruir `Pages`/`Thumbnails` (síncrono) e o refresh assíncrono do cache de
    /// rotação ainda não ter chegado, `_pageRotations[novoÍndice]` podia refletir a rotação da página
    /// ANTIGA que ocupava aquele slot — ex.: excluir a página 0 (não girada) faz a antiga página 1
    /// (girada) virar a nova página 0; ler o slot 0 do cache VELHO (ainda "não girada") sub-bloqueava a
    /// escrita -> commit no quadro ERRADO, silencioso, persistido. Fix: os 4 chamadores de escrita
    /// chamam `EnsureRotationCacheFreshAsync` (abaixo) ANTES deste método — refresca o cache se estiver
    /// obsoleto, then gateia com dados CORRENTES. `HitTestAnnotation` (leitura) mantém o comportamento
    /// ANTIGO (devolve `null` sem refrescar) de propósito: bloquear um clique por um instante é barato;
    /// um `await` no meio de um hit-test síncrono mudaria a assinatura de um método usado em caminhos
    /// síncronos da View.
    internal bool IsPageRotated(int pageIndex) =>
        pageIndex >= 0 && pageIndex < _pageRotations.Count && _pageRotations[pageIndex] != 0;

    /// Garante que `_pageRotations`/`AnnotationsByPage` reflitam o `Session.Snapshot` CORRENTE antes de
    /// uma escrita confiar em `IsPageRotated` — ver ACHADO (revisão Opus, I1) no doc XML acima. No-op
    /// (retorna já completo, sem `await` de verdade) quando o cache já está fresco — o caminho COMUM
    /// (nenhum Apply aconteceu desde o último refresh) não paga custo extra além de uma comparação de
    /// referência. `RefreshAnnotationsByPageAsync` já busca as DUAS coisas (anotações + rotações) no
    /// MESMO `Task.Run` — reusado aqui, não duplicado.
    private Task EnsureRotationCacheFreshAsync() =>
        ReferenceEquals(_annotationsCacheSnapshot, Session.Snapshot) ? Task.CompletedTask : RefreshAnnotationsByPageAsync();

    partial void OnSelectedAnnotationChanged(AnnotationData? oldValue, AnnotationData? newValue)
    {
        DeleteSelectedAnnotationCommand.NotifyCanExecuteChanged();
        EditSelectedAnnotationCommand.NotifyCanExecuteChanged();
        UpdateAnnotationSelectionOverlay(oldValue, newValue);
    }

    // Overlay de seleção (exemplar: overlay de seleção de TEXTO, Task 3) — só a página ANTIGA (se
    // houver) e a página NOVA (se houver) são tocadas; as outras nunca tiveram HasAnnotationSelection
    // ligado, então não há nada pra limpar nelas.
    private void UpdateAnnotationSelectionOverlay(AnnotationData? oldValue, AnnotationData? newValue)
    {
        if (oldValue is { } o && o.PageIndex >= 0 && o.PageIndex < Pages.Count)
            Pages[o.PageIndex].HasAnnotationSelection = false;
        if (newValue is { } n)
        {
            SetAnnotationSelectionRectFor(n);
            if (n.PageIndex >= 0 && n.PageIndex < Pages.Count) Pages[n.PageIndex].HasAnnotationSelection = true;
        }
    }

    /// Escreve o retângulo de overlay (px de tela) da página de `a` a partir da geometria REAL de `a`
    /// (Left/Bottom/Right/TopPt) — extraído (revisão Opus, I4) porque agora tem 2 chamadores:
    /// `UpdateAnnotationSelectionOverlay` acima (seleção mudou) e `MoveSelectedAnnotationAsync` abaixo
    /// (restaura o overlay pra posição REAL quando um arrasto NÃO termina em commit — ver lá). No-op
    /// se `a` for nulo ou a página não existir mais (documento pode ter encolhido).
    private void SetAnnotationSelectionRectFor(AnnotationData? a)
    {
        if (a is null || a.PageIndex < 0 || a.PageIndex >= Pages.Count) return;
        var page = Pages[a.PageIndex];
        page.AnnotationSelectionRect = PageViewModel.PointRectToScreenRect(
            a.LeftPt, a.BottomPt, a.RightPt, a.TopPt, Zoom, page.HeightPt);
    }

    // Miniaturas (Task 6): SEGUNDO PdfDocumentRenderer sobre o MESMO Session.Snapshot + SEGUNDO
    // RenderScheduler dedicado, escala fixa (ThumbnailViewModel.Scale) — o cache de render-reader do
    // renderer principal é de escala ÚNICA (ver doc XML de PdfDocumentRenderer), então miniaturas não
    // podem compartilhar renderer/scheduler com as páginas (escala de zoom, variável) sem invalidar o
    // cache uma da outra a cada troca. PdfRenderLock.Gate (global) torna operar os dois seguro.
    //
    // NÃO É MAIS readonly (Task 3, Plano 3a): Session.Apply troca o snapshot por baixo — este renderer
    // dedicado precisa ser RECRIADO sobre o snapshot NOVO (ver OnSessionApplied). _thumbnailScheduler
    // continua readonly de propósito: seu delegate de render é uma LAMBDA que lê este campo a cada
    // chamada (late-bound — mesma técnica de _scheduler abaixo), então trocar só o campo já basta; não
    // precisa recriar o scheduler em si.
    private PdfDocumentRenderer _thumbnailRenderer;
    private readonly RenderScheduler _thumbnailScheduler;

    /// Exposto só pra teste (Dispose_DisposesThumbnailRendererToo) provar que o Dispose fecha o
    /// SEGUNDO renderer — internal via InternalsVisibleTo, mesmo padrão de mPdf.Rendering.Tests.
    internal PdfDocumentRenderer ThumbnailRenderer => _thumbnailRenderer;

    public DocumentSession Session { get; }
    // "•" quando suja (Task 3, Plano 3a) + "(não salvo)" quando NeedsSaveAs (Task 2, Plano 7, rider da
    // revisão — pista visível de que este documento está temp-backed) — um único binding
    // ({Binding Title}) já cobre tab header E, por encadeamento de PropertyPath do WPF
    // (SelectedDocument.Title), o título da janela também, sem precisar duplicar a lógica em
    // MainViewModel. OnSessionDirtyChanged/OnSessionFilePathChanged (abaixo) + OnNeedsSaveAsChanged
    // (junto da declaração de NeedsSaveAs) mantêm isto vivo.
    public string Title
    {
        get
        {
            string title = Session.FileName;
            if (NeedsSaveAs) title += " (não salvo)";
            if (IsDirty) title += " •";
            return title;
        }
    }
    /// Espelha `Session.IsDirty` como propriedade observável do VM (Task 3, Plano 3a) — permite ao
    /// MainViewModel assinar `PropertyChanged` (via INotifyPropertyChanged já herdado de
    /// ObservableObject) para reavaliar `SaveCommand.CanExecute` sem precisar conhecer `DocumentSession`.
    public bool IsDirty => Session.IsDirty;
    /// Espelha `Session.CanUndo`/`CanRedo` (Task 4, Plano 3a) — mesmo padrão de `IsDirty`: propriedade
    /// simples mantida em sincronia por `OnSessionCanUndoRedoChanged` abaixo, nunca um
    /// `[ObservableProperty]` (o dono do valor real é a sessão, não o VM).
    public bool CanUndo => Session.CanUndo;
    public bool CanRedo => Session.CanRedo;

    /// Indicador "Salvando…" (Task 2, Plano 5) — DIFERENTE de `IsDirty`/`CanUndo`/`CanRedo` acima: não
    /// espelha nada de `Session` (a sessão não sabe que está sendo salva, só executa `Save`/`SaveAs`
    /// quando chamada — ver doc XML lá, ainda 100% síncrona e alheia ao funil). Quem ARMA e DESARMA este
    /// flag é `MainViewModel.Save`/`SaveAs` (as duas únicas donas do comando de salvar — ver doc XML lá),
    /// diretamente via o setter público gerado por `[ObservableProperty]`, MESMO precedente de
    /// `IsSignedDocument` (setado de FORA, por quem tem a informação, não calculado aqui). Vive AQUI
    /// (não em `MainViewModel`) porque salvar é uma operação POR DOCUMENTO — cada aba tem seu próprio
    /// indicador, mesmo espírito de `IsOrganizerOpen`/`Zoom`/o pino `Session.IsEditInFlight` (que já é
    /// compartilhado por sessão, não por janela). `[ObservableProperty]`, não uma propriedade calculada:
    /// não existe nenhum evento de `Session` pra espelhar aqui (ao contrário de `IsDirty`).
    [ObservableProperty] private bool isSaving;

    /// Verdadeiro quando o documento tem ao menos 1 assinatura (Plano 3a, Task 5) — checado OFF da UI
    /// thread por `MainViewModel.OpenPath` (via `mPdf.Editing.IPdfEditor.HasSignatures`, num Task.Run
    /// seguinte ao `DocumentSession.OpenAsync`) DURANTE a abertura, e só então atribuído aqui. Setter
    /// PÚBLICO de propósito: `DocumentViewModel` não referencia `mPdf.Editing` (só quem abre o
    /// documento — `MainViewModel` — precisa saber que esse módulo existe); este VM só guarda o
    /// resultado e o expõe como propriedade observável, mesmo padrão de espelhamento de
    /// `Session.IsDirty`/`CanUndo`/`CanRedo`, só que a origem do valor não é `Session` aqui. Default
    /// `false` até a checagem terminar — nunca bloqueia a UI achando que está "assinado" por engano
    /// enquanto o resultado real ainda está em voo.
    [ObservableProperty] private bool isSignedDocument;

    /// Task 2 (Plano 7): `true` quando este documento foi aberto a partir de uma imagem CONVERTIDA
    /// (JPG/PNG -> PDF pela conversão na fronteira, `MainViewModel.OpenImageAsNewDocument`) — o arquivo
    /// por trás de `Session.FilePath` é um PDF TEMPORÁRIO em `%TEMP%\mPDF\open-<guid>\`, um caminho que
    /// o usuário NUNCA escolheu. Setter PÚBLICO de propósito (mesmo padrão de `IsSignedDocument` acima):
    /// quem converteu a imagem é quem sabe disso; este VM só guarda o resultado, nunca referencia
    /// `mPdf.Editing`/conversão de imagem. Consumido por `MainViewModel.Save`/`TryResolveDirtyDocument`
    /// E por `Sign` (abaixo, fix CRÍTICO pós-revisão) pra desviar "Salvar"/"Assinar" pra "Salvar como" —
    /// nunca grava (nem uma edição comum, nem — o caso mais grave — uma ASSINATURA) silenciosamente de
    /// volta no temp (ver doc XML de `MainViewModel.Save`/`Sign` abaixo).
    /// `[ObservableProperty]` (fix pós-revisão, rider "visible cue" — deixou de ser uma propriedade
    /// simples): `Title` acima COMPÕE este valor pra mostrar "(não salvo)" na aba — sem a notificação
    /// gerada automaticamente por `[ObservableProperty]`, a aba recém-aberta (`MainViewModel.
    /// OpenImageAsNewDocument` seta isto DEPOIS que a aba já está visível/vinculada) ficaria com o
    /// título ANTIGO até algum evento NÃO RELACIONADO (IsDirty/FilePath) disparar um refresh por
    /// coincidência. `false` (default) preserva o comportamento de TODO documento aberto de um caminho
    /// real de disco — a esmagadora maioria.
    [ObservableProperty] private bool needsSaveAs;

    partial void OnNeedsSaveAsChanged(bool value) => OnPropertyChanged(nameof(Title));

    /// Documento assinado -> edição bloqueada (spec ICP-Brasil §5.2 — mesma precondição que
    /// `IPdfEditor.AddAnnotation`/`RemoveAnnotation` já aplicam como defesa em profundidade dentro de
    /// `mPdf.Editing`). Tasks 6-9 ligam o `CanExecute` dos comandos de anotação a esta propriedade.
    /// Task 2 (Plano 3c) — ACHADO REAL, não hipotético (sonda ao vivo contra `fixture-xfa.pdf`):
    /// `IPdfEditor.HasSignatures`/`GuardAgainstSignedDocument` (usados por `AddAnnotation`/
    /// `RotatePages`/`DeletePages`/etc. — TODO mutador deste contrato) instanciam `SignatureUtil(doc)`,
    /// que internamente aciona `PdfAcroForm.GetAcroForm` — a MESMA falha documentada em `HasXfa`
    /// (`PdfException: Root element is missing` ao parsear `/XFA` malformado/dummy). Ou seja: QUALQUER
    /// tentativa de mutar um documento XFA por QUALQUER caminho deste VM (anotação, desenho, organizador
    /// via `OrganizerViewModel`, preenchimento de formulário) lançaria `PdfEditingException` — não só o
    /// preenchimento. "XFA... o documento abre para leitura" (spec) só é seguro se NENHUM comando
    /// mutador tentar chegar no motor num doc XFA; por isso `CanEdit` compõe `IsXfaForm` também, não só
    /// `IsSignedDocument` — um ÚNICO gate que todo `CanX` deste VM (e do organizador, via `() =>
    /// CanEdit` passado a `OrganizerViewModel`) já herda de graça.
    public bool CanEdit => !IsSignedDocument && !IsXfaForm;

    /// Task 6 (Plano 4): resultado de `ISigningEngine.CanFillIncremental` para o documento ATUAL — só
    /// relevante quando `IsSignedDocument` é true (documento sem assinatura sempre usa o caminho normal
    /// de `mPdf.Editing`, gated por `CanEdit`). Calculado OFF da UI thread durante a abertura (mesmo
    /// exemplar de `IsSignedDocument` — Obs 17: computado no caller já-async de `MainViewModel.OpenPath`,
    /// nunca fire-and-forget num construtor), e reatribuído depois de assinar (`SignCoreAsync` abaixo) —
    /// assinar pode fazer este valor mudar (um documento `NotSigned` passa a `Allowed`/`DeniedByDocMdp`
    /// dependendo do nível de certificação escolhido no diálogo). Default `NotSigned` — seguro mesmo
    /// antes da checagem assíncrona terminar (nunca libera preenchimento achando que está permitido).
    [ObservableProperty] private FillPermission signedFillPermission = FillPermission.NotSigned;

    /// Gate de preenchimento de formulário (Task 6, Plano 4) — DISTINTO de `CanEdit` de propósito: um
    /// documento assinado com DocMDP permitindo (`SignedFillPermission == Allowed`) continua
    /// PREENCHÍVEL (a única exceção ao gate `CanEdit` neste app — spec §5.2/decisão do plano), mas
    /// anotações/páginas/achatar continuam bloqueados (nenhum outro `CanX` deste VM compõe esta
    /// propriedade, só `CanApplyFormValues`). Documento NÃO assinado: preenchível sempre que `CanEdit`
    /// já seria (mesma regra de sempre, sem mudança de comportamento). `!IsXfaForm` mesmo raciocínio de
    /// `CanEdit` — formulário XFA nunca é preenchível por este app, assinado ou não.
    public bool CanFillForms => !IsXfaForm && (IsSignedDocument ? SignedFillPermission == FillPermission.Allowed : true);

    /// Aviso do painel Campos (brief, texto EXATO na View) — visível só no caso "assinado E preenchível"
    /// (`CanFillForms` já reflete a permissão DocMDP quando `IsSignedDocument` é true; compor os dois
    /// aqui deixa explícito que não é o caso comum "documento sem assinatura nenhuma").
    public bool ShowSignedFormFillNotice => IsSignedDocument && CanFillForms;

    /// M2 (revisão, Task 6/Plano 4 — "trust-panel misinformation" complementar ao I2): quando o painel
    /// fica DESABILITADO especificamente por `DeniedByDocMdp` (documento CERTIFICADO com P=1 — nenhuma
    /// alteração é permitida), o banner GENÉRICO de "documento assinado" (MainWindow) não explica O
    /// PORQUÊ — o usuário só vê os campos cinzentos sem saber se é XFA, DocMDP, ou outro motivo. Este
    /// aviso é ESPECÍFICO desse caso (texto EXATO da revisão), mutuamente exclusivo com
    /// `ShowSignedFormFillNotice` acima (os 2 dependem de valores DIFERENTES de `SignedFillPermission`,
    /// nunca os 2 `true` ao mesmo tempo).
    public bool ShowDocMdpDeniedNotice => IsSignedDocument && SignedFillPermission == FillPermission.DeniedByDocMdp;

    partial void OnSignedFillPermissionChanged(FillPermission value)
    {
        OnPropertyChanged(nameof(CanFillForms));
        OnPropertyChanged(nameof(ShowSignedFormFillNotice));
        OnPropertyChanged(nameof(ShowDocMdpDeniedNotice));
        ApplyFormValuesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSignedDocumentChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        // Task 6 (Plano 3a): CanApplyMarkup também depende de CanEdit. IsSignedDocument começa false e
        // só vira true DEPOIS que a checagem assíncrona de assinatura termina (ver doc XML acima) — se
        // o usuário já tiver selecionado texto ANTES desse resultado chegar, os botões de marca-texto
        // ficariam presos "habilitados" num documento que na verdade está assinado, sem este disparo.
        ApplyMarkupCommand.NotifyCanExecuteChanged();
        // Task 7 (Plano 3a): mesmo gate CanEdit cobre as ferramentas de anotação e os comandos de
        // excluir/editar a anotação selecionada. Task 8 (Plano 3a): as 4 ferramentas de desenho novas
        // (Ink/Rectangle/Line/Arrow) compartilham o MESMO gate (`CanUseAnnotationTool`).
        ToggleStickyNoteToolCommand.NotifyCanExecuteChanged();
        ToggleFreeTextToolCommand.NotifyCanExecuteChanged();
        ToggleInkToolCommand.NotifyCanExecuteChanged();
        ToggleRectangleToolCommand.NotifyCanExecuteChanged();
        ToggleLineToolCommand.NotifyCanExecuteChanged();
        ToggleArrowToolCommand.NotifyCanExecuteChanged();
        DeleteSelectedAnnotationCommand.NotifyCanExecuteChanged();
        EditSelectedAnnotationCommand.NotifyCanExecuteChanged();
        // Task 2 (Plano 3c): ApplyFormValuesCommand também compõe CanEdit... na verdade compõe
        // CanFillForms (Task 6, Plano 4) — CanFillForms já reage a IsSignedDocument sozinho (fórmula
        // acima), mas precisa do MESMO disparo de PropertyChanged/NotifyCanExecuteChanged aqui.
        OnPropertyChanged(nameof(CanFillForms));
        OnPropertyChanged(nameof(ShowSignedFormFillNotice));
        OnPropertyChanged(nameof(ShowDocMdpDeniedNotice));
        ApplyFormValuesCommand.NotifyCanExecuteChanged();
        // Task 3 (Plano 3c): FlattenFormCommand — mesmo gate CanEdit, mesmo motivo.
        FlattenFormCommand.NotifyCanExecuteChanged();
        // Task 4 (Plano 15): RecognizeTextCommand — mesmo gate CanEdit (assinado -> "Editar uma cópia").
        RecognizeTextCommand.NotifyCanExecuteChanged();
    }

    /// Espelho exato de `OnIsSignedDocumentChanged` acima — mesmas notificações, mesmo motivo (`CanEdit`
    /// agora compõe `IsXfaForm` também, ver doc XML de `CanEdit`). `IsXfaForm` é setado por
    /// `SeedFormFieldsCache`/`RefreshFormFieldsAsync` (Task 2) — normalmente já `false` no momento em
    /// que este VM é usável (a carga inicial acontece ANTES do documento aparecer pro usuário, ver Obs
    /// 17), mas o disparo continua correto pro caso residual de um refresh mudar o valor.
    partial void OnIsXfaFormChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        ApplyMarkupCommand.NotifyCanExecuteChanged();
        ToggleStickyNoteToolCommand.NotifyCanExecuteChanged();
        ToggleFreeTextToolCommand.NotifyCanExecuteChanged();
        ToggleInkToolCommand.NotifyCanExecuteChanged();
        ToggleRectangleToolCommand.NotifyCanExecuteChanged();
        ToggleLineToolCommand.NotifyCanExecuteChanged();
        ToggleArrowToolCommand.NotifyCanExecuteChanged();
        DeleteSelectedAnnotationCommand.NotifyCanExecuteChanged();
        EditSelectedAnnotationCommand.NotifyCanExecuteChanged();
        // Task 6 (Plano 4): CanFillForms também compõe !IsXfaForm — mesmo motivo/mesmo disparo.
        OnPropertyChanged(nameof(CanFillForms));
        OnPropertyChanged(nameof(ShowSignedFormFillNotice));
        OnPropertyChanged(nameof(ShowDocMdpDeniedNotice));
        ApplyFormValuesCommand.NotifyCanExecuteChanged();
        FlattenFormCommand.NotifyCanExecuteChanged();
        // Task 3 (Plano 4): SignCommand também compõe !IsXfaForm (CanSign) — mesma disciplina, MAS
        // deliberadamente NÃO entra em OnIsSignedDocumentChanged (acima): CanSign não compõe
        // !IsSignedDocument (assinatura incremental precisa continuar habilitada num doc já assinado).
        SignCommand.NotifyCanExecuteChanged();
        // Task 4 (Plano 15): RecognizeTextCommand compõe CanEdit (que já compõe !IsXfaForm) — mesmo disparo.
        RecognizeTextCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<PageViewModel> Pages { get; } = [];
    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = [];

    [ObservableProperty] private double zoom = 1.0;
    [ObservableProperty] private int currentPage = 1;

    /// Task 2 (Plano 9): fator de DPI do MONITOR onde o viewer está sendo exibido (1.0 = 96dpi padrão;
    /// 1.5 = telas Windows escaladas a 150%, etc.) — SEAM testável de propósito (brief: "não estático").
    /// `PdfViewerControl` é quem escreve este valor (via `ApplyDpiFactor`, ela mesma lida por
    /// `VisualTreeHelper.GetDpi(this)` no Loaded/troca de aba, e por `OnDpiChanged` quando a janela migra
    /// pra um monitor de DPI diferente) — este VM nunca lê o SO diretamente, só expõe a propriedade;
    /// testes escrevem aqui direto (`doc.DpiFactor = 1.5`), sem precisar de nenhuma janela/monitor real.
    /// Multiplica a escala de RENDER (`PageViewModel.RequestRender`) — a escala LÓGICA (`ApplyZoom`,
    /// overlays/hit-testing) fica INTOCADA: só o BITMAP nasce mais denso, o layout na tela não muda um
    /// px (fronteira central desta task, ver doc XML de `PageViewModel.RequestRender`).
    [ObservableProperty] private double dpiFactor = 1.0;

    /// Task 1 (Plano 13): fator de SUPERSAMPLING do texto — mesmo padrão de seam testável do
    /// `DpiFactor` acima (1.0 = OFF = comportamento de hoje byte-idêntico; não escreve nada sozinho,
    /// um chamador real decide o valor). Multiplica a escala de RENDER (`PageViewModel.RequestRender`)
    /// exatamente como `DpiFactor` — mas só QUANDO a escala efetiva (`zoom * PtToPx * DpiFactor`) está
    /// abaixo do limiar `PageViewModel.SupersampleThreshold`: em zoom alto o render nativo já é denso o
    /// bastante (PDFium 1:1 a essas escalas já não sofre do "traço subpixel" que deixa o texto fino a
    /// ~100%), então aplicar o fator ali só gastaria memória/CPU sem ganho de nitidez perceptível — ver
    /// `PageViewModel.EffectiveSupersampleFactor`. A escala LÓGICA (`ApplyZoom`, overlays/hit-testing/
    /// seleção/caixa do carimbo) fica INTOCADA, mesma fronteira central de `DpiFactor`.
    [ObservableProperty] private double supersampleFactor = 1.0;

    /// Task 2 (Plano 13): fator usado quando "Nitidez extra do texto" está LIGADA (`AppConfig.
    /// NitidezExtra`/`SobreViewModel.NitidezExtra`). MEDIDO (Task 1, ver task-1-report.md): a 1.5x NÃO
    /// há ganho de nitidez perceptível nas fixtures testadas; só a 2.0x o oráculo de pixels escuros
    /// mostra diferença — por isso o fator de produção é 2.0, não 1.5 (que só aparece nos testes PUROS
    /// de `ComputeRenderScale`, herdados da Task 1, como fator arbitrário de fórmula, nunca como valor
    /// de produção). Custo medido: ~4x memória/tempo de render por página (2.0² = 4x pixels) — por isso
    /// o recurso é OPT-IN, default desligado (ver `AppConfig.NitidezExtra`).
    public const double NitidezExtraSupersampleFactor = 2.0;

    public string ZoomPercent => $"{Zoom * 100:0}%";
    public string PageCountLabel => $"Página {CurrentPage} de {Pages.Count}";

    // Seleção de texto (Task 3): só uma página por vez tem seleção ativa neste documento — abrir uma
    // seleção em outra página limpa a anterior automaticamente (multi-página é fora de escopo v1).
    private PageViewModel? _pageWithSelection;

    /// Texto selecionado no documento (da página com seleção ativa, se houver) — o que Ctrl+C copia.
    public string? SelectedText => _pageWithSelection?.SelectedText;

    // Busca de texto (Task 5): um SearchViewModel por documento, espelhando _pageWithSelection —
    // cada aba tem sua própria busca independente.
    public SearchViewModel Search { get; }

    /// Disparado quando a busca quer levar a visão até a página do hit CORRENTE — SEMPRE dispara,
    /// mesmo se for a mesma página de antes (ex.: Próximo dentro da mesma página com vários hits);
    /// por isso é um evento simples, não um ObservableProperty (que suprimiria valores repetidos).
    public event Action<int>? ScrollToPageRequested;

    /// Deferência (Task 2, Plano 5) — "FitWidth com organizador aberto deixa o viewport obsoleto":
    /// `FitWidth(viewportWidthPx)` computa `Zoom` a partir da largura de tela QUE A VIEW leu na hora do
    /// clique (ver `MainWindow.FitWidth_Click`) — mas `PdfViewerControl` fica `Visibility=Collapsed`
    /// enquanto o organizador está aberto (ver `MainWindow.xaml`), e o WPF NÃO relayouta elementos
    /// Collapsed: `PdfViewerControl.ViewportWidth` (`PageList.ActualWidth`) congela no último valor
    /// medido enquanto o leitor esteve visível pela última vez. Se a JANELA for redimensionada com o
    /// organizador aberto, o `Zoom` calculado por um "Ajustar à largura" anterior fica desalinhado da
    /// largura REAL assim que o leitor reaparece — a página não preenche mais a largura do jeito que o
    /// usuário pediu, e nada recalcula sozinho. Evento SEM parâmetro (ao contrário de
    /// `ScrollToPageRequested`, que carrega o índice): este VM não tem acesso à largura de tela — só a
    /// View sabe medir `PageList.ActualWidth` DEPOIS do layout Collapsed->Visible terminar (mesmo padrão
    /// de `PdfViewerControl.OnFitWidthRecalcRequested`, que adia com `Dispatcher.BeginInvoke` antes de
    /// chamar `FitWidth` de volta — ver doc XML lá). Disparado só de `CloseOrganizer` abaixo, e só
    /// quando o ÚLTIMO ajuste de zoom pedido pelo usuário foi de fato "Ajustar à largura"
    /// (`_lastFitWasWidth`, setado em `FitWidth`/limpo em `ZoomIn`/`ZoomOut`/`FitPage`) — nunca
    /// sobrescreve um zoom manual (Ctrl+scroll, +/-) só porque o usuário espiou o organizador.
    public event Action? FitWidthRecalcRequested;

    /// Ver doc XML de `FitWidthRecalcRequested` acima — `true` só entre o momento em que `FitWidth` roda
    /// e o próximo ajuste de zoom que NÃO seja `FitWidth` (`ZoomIn`/`ZoomOut`/`FitPage` limpam).
    private bool _lastFitWasWidth;

    /// Verdadeiro quando alguma página do documento tem uma seleção ativa (Task 6, Plano 3a) — gate de
    /// `CanApplyMarkup` junto com `CanEdit`. Notificado só nos DOIS pontos onde `_pageWithSelection`
    /// muda de fato (`SetSelectionOwner`/`ClearSelection` abaixo), nunca a cada `UpdateSelection`
    /// (arrasto em curso): por quando o usuário solta o botão do mouse e clica numa ferramenta da
    /// toolbar, o gesto já terminou e os retângulos já estão completos — não precisa reavaliar o
    /// CanExecute a cada pixel de arrasto.
    public bool HasActiveSelection => _pageWithSelection is not null;

    internal void SetSelectionOwner(PageViewModel page)
    {
        if (_pageWithSelection is { } prev && !ReferenceEquals(prev, page)) prev.ClearSelection();
        _pageWithSelection = page;
        NotifySelectionChanged();
    }

    /// Limpa a seleção ativa (clique simples, início de um novo gesto de mouse-down, ou o fim bem-
    /// sucedido de ApplyMarkupCommand abaixo).
    public void ClearSelection()
    {
        _pageWithSelection?.ClearSelection();
        _pageWithSelection = null;
        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasActiveSelection));
        ApplyMarkupCommand.NotifyCanExecuteChanged();
    }

    public DocumentViewModel(
        DocumentSession session,
        IPdfEditor? editor = null,
        AppConfig? config = null,
        Action<string>? notifyError = null,
        IAnnotationTextDialogService? annotationDialog = null,
        IFileDialogService? dialogs = null,
        Action<string>? notifyInfo = null,
        IConfirmFlattenService? confirmFlatten = null,
        IConfirmSaveBeforeSignService? confirmSaveBeforeSign = null,
        ISignDialogService? signDialog = null,
        ISigningEngine? signingEngine = null,
        Func<IReadOnlyList<SigningCertificateInfo>>? listSigningCertificates = null,
        IConfirmOrganizerScaleService? confirmOrganizerScale = null,
        IExportImageDialogService? exportImageDialog = null,
        // Task 4 (Plano 15): OCR. `ocrEngine` default `null` -> criado PREGUIÇOSAMENTE (só no 1º OCR) via
        // `new TesseractOcrEngine()` — não onera cada documento aberto com a carga de nativos/tessdata, e
        // testes de orquestração injetam um fake determinístico. `rasterizerFactory` é `Func` (namespace
        // System, fora da varredura de UiPromptsCoverageTests, mesma isenção de `Func<IUpdateSource>`):
        // não mostra UI, só render; testes injetam um fake sem tocar o renderer nativo. `ocrProgress`
        // roteia pela seam `UiPrompts` (abre uma Window real) — na varredura da guarda de cobertura.
        IOcrEngine? ocrEngine = null,
        Func<byte[], IOcrPageRasterizer>? rasterizerFactory = null,
        IOcrProgressService? ocrProgress = null,
        // Task 3 (Plano 16): diálogo "Exportar como Word/Excel" (seam UiPrompts, hang-guard) +
        // detecção de texto (Func, isenta da varredura de cobertura — não mostra UI).
        IExportDocumentDialogService? exportDocumentDialog = null,
        Func<byte[], bool>? documentHasText = null)
    {
        Session = session;
        _editor = editor ?? PdfEditorFactory.Create();
        _config = config ?? new AppConfig(AppConfig.DefaultDirectory);
        // Task 0 (Plano 3c): defaults vêm do seam `UiPrompts` (não mais de um método estático local) —
        // ver doc XML de UiPrompts pro porquê (guarda de diálogo-em-teste-headless).
        _notifyError = notifyError ?? UiPrompts.DocumentNotifyError;
        _annotationDialog = annotationDialog ?? UiPrompts.CreateAnnotationDialog();
        _dialogs = dialogs ?? UiPrompts.CreateFileDialog();
        _notifyInfo = notifyInfo ?? UiPrompts.NotifyInfo;
        // Task 3 (Plano 3c): mesmo padrão dos 3 defaults acima.
        _confirmFlatten = confirmFlatten ?? UiPrompts.CreateConfirmFlatten();
        // Task 1 (Plano 5): mesmo padrão de _confirmFlatten acima.
        _confirmOrganizerScale = confirmOrganizerScale ?? UiPrompts.CreateConfirmOrganizerScale();
        // Task 3 (Plano 4): mesmo padrão dos 2 diálogos acima (seam UiPrompts) pros 2 novos prompts;
        // _signingEngine/_listSigningCertificates NÃO passam pela seam (não mostram UI — ver doc XML do
        // campo acima), mesmo precedente de `_editor ?? PdfEditorFactory.Create()`.
        _confirmSaveBeforeSign = confirmSaveBeforeSign ?? UiPrompts.CreateConfirmSaveBeforeSign();
        _signDialog = signDialog ?? UiPrompts.CreateSignDialog();
        _signingEngine = signingEngine ?? SigningEngineFactory.Create();
        _listSigningCertificates = listSigningCertificates ?? CertificateCatalog.ListSigningCertificates;
        // Task 4 (Plano 7): mesmo padrão dos diálogos acima (seam UiPrompts).
        _exportImageDialog = exportImageDialog ?? UiPrompts.CreateExportImageDialog();
        // Task 4 (Plano 15): OCR. `_ocrEngine` fica `null` quando não injetado (criado sob demanda em
        // `GetOcrEngine` — ver campo). `_rasterizerFactory` default é o `OcrPageRasterizer` real (T2).
        // `_ocrProgress` roteia pela seam UiPrompts (hang-guard), mesmo padrão dos diálogos acima.
        _ocrEngine = ocrEngine;
        _rasterizerFactory = rasterizerFactory ?? (pdf => new OcrPageRasterizer(pdf));
        _ocrProgress = ocrProgress ?? UiPrompts.CreateOcrProgress();
        // Task 3 (Plano 16): mesmo padrão dos diálogos acima (seam UiPrompts). `_documentHasText` default
        // abre um renderer próprio e reusa `PdfTextSearch.DocumentHasText` (leitura pura, sem UI).
        _exportDocumentDialog = exportDocumentDialog ?? UiPrompts.CreateExportDocumentDialog();
        _documentHasText = documentHasText ?? DefaultDocumentHasText;
        // SEAM (Task 3, Plano 3a — "o lugar mais provável de quebrar em silêncio" do Apply): a forma
        // ANTIGA, `new RenderScheduler(session.Renderer.RenderPage)`, é uma conversão de GRUPO DE
        // MÉTODO — ela avalia `session.Renderer` UMA VEZ, na hora desta linha, e produz um delegate
        // ligado PARA SEMPRE àquela instância específica de PdfDocumentRenderer. `Session.Apply` troca
        // `Renderer` por uma instância NOVA (e manda a antiga pro PendingDisposals) sempre que uma
        // edição é aplicada — sem este fix, todo render pedido DEPOIS de um Apply continuaria
        // silenciosamente usando o documento ANTIGO (ou lançaria ObjectDisposedException, engolida
        // pelo catch{} do RenderScheduler — a página ficaria em branco pra sempre, sem erro visível).
        // O fix é LATE-BINDING: uma lambda que lê `Session.Renderer` de novo a CADA chamada, então
        // sempre observa a instância CORRENTE.
        _scheduler = new RenderScheduler((pageIndex, scale) => Session.Renderer.RenderPage(pageIndex, scale));
        _thumbnailRenderer = new PdfDocumentRenderer(session.Snapshot);
        // Mesma técnica pro scheduler de miniaturas, mas OLHANDO O CAMPO `_thumbnailRenderer` (não
        // `Session.Renderer` — miniaturas usam um renderer DEDICADO, de escala fixa, por design da
        // Task 6/2b; apontar pro renderer principal aqui reintroduziria o thrashing de cache de
        // escala única que aquele renderer dedicado existe pra evitar). OnSessionApplied troca o VALOR
        // do campo quando o documento muda; como a lambda relê o campo a cada chamada, o scheduler em
        // si nunca precisa ser recriado.
        _thumbnailScheduler = new RenderScheduler((pageIndex, scale) => _thumbnailRenderer.RenderPage(pageIndex, scale));
        BuildPagesAndThumbnails();
        // Task 2 (Plano 13): fator de supersampling INICIAL lido da config persistida — 2.0 se o usuário
        // já ligou "Nitidez extra" no Sobre antes de abrir este documento, 1.0 (comportamento de hoje,
        // byte-idêntico) no default/config antiga. DEPOIS de `BuildPagesAndThumbnails` de propósito: o
        // setter deste `[ObservableProperty]` dispara `OnSupersampleFactorChanged` sempre que o valor
        // NOVO difere do default 1.0 (ex.: config já com NitidezExtra=true) — esse handler itera `Pages`
        // (`foreach (var p in Pages) p.RefreshDpi()`), que antes desta linha ainda é `null`
        // (NullReferenceException real, pego por `SupersampleFactor_ConfigNitidezExtraTrue_IsTwo`).
        // Nenhuma página está REALIZADA ainda nesta linha (`_realized` só liga quando a View pede — ver
        // `PageViewModel.Realize`), então `RefreshDpi()` é um no-op seguro aqui; é só o valor de PARTIDA.
        // Documentos JÁ abertos quando o usuário liga/desliga o toggle são re-renderizados por
        // `MainViewModel.Sobre` (ver lá), não por aqui.
        SupersampleFactor = _config.NitidezExtra ? NitidezExtraSupersampleFactor : 1.0;
        Search = new SearchViewModel(SearchInDocument, ApplySearchResults, ProbeDocumentHasText);
        // Task 3 (Plano 3a): reage a Apply (sempre — reconstrói Pages/Thumbnails pro documento NOVO,
        // troca o renderer dedicado de miniaturas), a mudanças de IsDirty (só o "•" do título) e a
        // SaveAs (nome do arquivo mudou, título precisa atualizar mesmo sem o "•" mudar). Desinscrito
        // em Dispose — ver lá.
        Session.Applied += OnSessionApplied;
        Session.DirtyChanged += OnSessionDirtyChanged;
        Session.FilePathChanged += OnSessionFilePathChanged;
        // Task 4 (Plano 3a): mantém CanUndo/CanRedo (e o CanExecute de UndoCommand/RedoCommand)
        // sincronizados com a pilha de desfazer/refazer da sessão. Desinscrito em Dispose — ver lá.
        Session.CanUndoRedoChanged += OnSessionCanUndoRedoChanged;
        // Task 1 (Plano 5): teto de disco do histórico de desfazer/refazer — 1x por documento (o latch
        // mora em DocumentSession, ver doc XML de UndoHistoryLimitReached lá), este VM só roteia pro
        // seam de notificação. Desinscrito em Dispose — ver lá.
        Session.UndoHistoryLimitReached += OnSessionUndoHistoryLimitReached;
        // Rodada 2 (revisão pós-branch, "R1 refutado" — funil único de exclusão mútua): o pino de
        // "edição em voo" agora vive em `Session` (compartilhado com `OrganizerViewModel`, quando o
        // organizador está aberto sobre o MESMO documento) — uma edição armada pelo ORGANIZADOR
        // (Girar/Excluir/Mover/Inserir/Extrair) precisa desabilitar os comandos de anotação/carimbo
        // deste VM tanto quanto uma armada AQUI mesmo. Desinscrito em Dispose — ver lá.
        Session.EditInFlightChanged += OnSessionEditInFlightChanged;
        // Task 7 (Plano 3a): carga INICIAL do cache de anotações (fire-and-forget, exemplar: prefetch
        // de TextPage em PageViewModel.OnRealized) — sem isto, um documento que já ABRE com anotações
        // (ex.: fixture-anotada.pdf) não teria nada pra hit-testar até a PRÓXIMA edição disparar
        // OnSessionApplied. Despachado via `_dispatcher` (revisão Opus — ver doc XML do campo): evita a
        // corrida de `PropertyChanged` numa thread de pool arbitrária achada ao vivo em teste.
        // Task 5 (Plano 3b): mesma disciplina pro cache de sumário — carga INICIAL, mesmo despacho.
        // Task 4 (Plano 4): mesma disciplina pro cache de assinaturas (ver doc XML de `SignatureRows`).
        _dispatcher.BeginInvoke(() => { _ = RefreshAnnotationsByPageAsync(); _ = RefreshOutlineAsync(); _ = RefreshSignaturesAsync(); });
    }

    // Preenche Pages/Thumbnails a partir do estado CORRENTE de Session (PageCount/PageSizes) — usado
    // no construtor E em OnSessionApplied (reconstrução pós-Apply). Extraído pra método próprio na
    // Task 3 (Plano 3a) porque agora roda duas vezes na vida do VM, não só na construção.
    private void BuildPagesAndThumbnails()
    {
        for (int i = 0; i < Session.Renderer.PageCount; i++)
        {
            // Item (c) da Task 1 (Plano 3a): lê de session.PageSizes (já pronta, materializada no
            // Open/OpenAsync da sessão) em vez de chamar Renderer.GetPageSize(i) aqui — isso tirou N
            // chamadas SÍNCRONAS ao PDFium (1 por página, cada uma sob o lock global) da thread de UI
            // que constrói este VM. Mesma medida (pontos) serve pras duas VMs abaixo: dimensões da
            // página independem de qual dos dois renderers foi usado pra lê-las (mesmos bytes de PDF).
            var size = Session.PageSizes[i];
            Pages.Add(new PageViewModel(i, size, _scheduler, this));
            Thumbnails.Add(new ThumbnailViewModel(i, size, _thumbnailScheduler));
        }
        if (Thumbnails.Count > 0) Thumbnails[0].IsCurrent = true; // CurrentPage inicial = 1
    }

    // Handler de Session.Applied (Task 3, Plano 3a) — a OUTRA metade da seam: mesmo com os schedulers
    // late-bound (acima), o RENDERER DEDICADO de miniaturas (_thumbnailRenderer) é uma instância
    // própria do DocumentViewModel, construída sobre os bytes do snapshot ANTIGO — ele não troca
    // sozinho só porque Session.Renderer trocou. E mesmo que trocasse, Pages/Thumbnails (as coleções
    // observáveis que a UI de fato mostra) continuariam com a contagem/tamanho do documento ANTIGO.
    // Este handler resolve as duas coisas de uma vez, sincronamente (Apply é UI-thread-only por
    // contrato — ver doc XML de DocumentSession.Apply — então mexer nas ObservableCollection aqui é
    // seguro, igual ao laço do construtor).
    private void OnSessionApplied(object? sender, EventArgs e)
    {
        // I4 (revisão pós-Task 3) — PRIMEIRA linha, de propósito: busca com hits aponta pra ÍNDICES DE
        // PÁGINA do documento ANTIGO (SearchHit.PageIndex), que podem não existir mais depois do
        // rebuild abaixo (documento novo pode ter MENOS páginas). Sem fechar a busca AQUI, um hit
        // antigo sobrevivia em Search._hits; navegar (Next/Previous) reaplicava esses hits contra as
        // Pages NOVAS via ApplySearchResults -> `Pages[group.Key]` com um índice fora do range ->
        // ArgumentOutOfRangeException, um crash real disparável pelo usuário (busca -> aplica edição
        // que encolhe o doc -> aperta "Próximo"). CloseCommand.Execute(null) é o mesmo caminho já usado
        // por Dispose() (ver comentário lá): fecha a barra, cancela o CTS em voo, zera hits/índice — a
        // via PÚBLICA mais barata de "limpar tudo", sem duplicar a lógica de ApplyHits([]) aqui.
        Search.CloseCommand.Execute(null);

        // Pedidos pendentes referem-se a índices/página do documento ANTIGO — sem valor no documento
        // NOVO (podem nem existir mais). Mesmo padrão de OnZoomChanged.
        _scheduler.CancelPending();
        _thumbnailScheduler.CancelPending();

        var oldThumbnailRenderer = _thumbnailRenderer;
        _thumbnailRenderer = new PdfDocumentRenderer(Session.Snapshot);
        // Mesma fila serial usada em Dispose() — descarte nativo nunca pode rodar concorrente com
        // outro (ver doc XML de PendingDisposals). Uma renderização de miniatura em voo NO renderer
        // antigo, se houver, recebe ObjectDisposedException gerenciada (nunca AV nativa) pelo mesmo
        // guard-sob-lock que já protege o Dispose de sessão — nenhum mecanismo novo precisou ser
        // inventado aqui.
        PendingDisposals.Enqueue(() => oldThumbnailRenderer.Dispose());

        ClearSelection(); // _pageWithSelection apontava pra um PageViewModel que está prestes a sumir
        // Task 7 (Plano 3a): mesma disciplina — SelectedAnnotation aponta pra um AnnotationData que
        // pode ter mudado de posição/conteúdo (lift de editar/mover) ou sumido (Del) nesta MESMA
        // edição; nunca sobrevive a um Apply, seja ele qual for (inclui Undo/Redo alheios).
        SelectedAnnotation = null;
        // Task 2 (Plano 3c): mesma disciplina para o campo de formulário selecionado — o WidgetRect
        // cacheado pode não refletir mais o documento vivo depois desta edição (qualquer edição,
        // inclusive uma alheia ao painel de Campos).
        SelectedFormField = null;
        // Task 4 (Plano 4): mesma disciplina para a assinatura selecionada — o StampRect cacheado pode
        // não refletir mais o documento vivo depois desta edição (qualquer edição, inclusive uma alheia
        // ao painel de Assinaturas — ou a PRÓPRIA assinatura que acabou de ser adicionada).
        SelectedSignature = null;
        // Task 1 (Plano 8): mesma disciplina — a caixa ajustável do carimbo (Drawing/Adjusting) referencia
        // um StampBoxPageIndex/rect que podem não existir/fazer sentido mais depois desta edição (o
        // MESMO "placement-window mutation gap" já documentado em DocumentChangedDuringPlacementNotice —
        // hoje sem funil armado durante Drawing/Adjusting, uma edição alheia PODE acontecer no meio;
        // Task 2 cobre o cinto no Confirmar, mas isto aqui evita até um ArgumentOutOfRangeException se a
        // CONTAGEM de página encolher). Chamado ANTES de Pages.Clear() abaixo de propósito — o overlay
        // que este cancelamento limpa (RefreshStampBoxOverlay, via OnStampPlacementPhaseChanged) ainda
        // precisa achar a PageViewModel antiga em Pages pra zerar HasStampBox/IsStampBoxAdjusting nela.
        // `dueToDocumentMutation: true` (fix pós-revisão, achado real do coordenador): ESTE é o ÚNICO
        // chamador de CancelStampBox onde o usuário NÃO iniciou o cancelamento — a mutação veio de
        // outro lugar (Undo/Redo, outra anotação, um Flatten) enquanto ele estava desenhando/ajustando;
        // sem aviso, a caixa some da tela sem explicação nenhuma. Ver doc XML do parâmetro em
        // CancelStampBox.
        CancelStampBox(dueToDocumentMutation: true);

        // Item 4 (revisão final pré-merge) — capturado ANTES do rebuild abaixo: Pages/Thumbnails trocam
        // TODAS as instâncias (nenhum PageViewModel sobrevive a um Apply), mas a CONTAGEM de página em
        // si normalmente não muda numa edição de anotação (Highlight/StickyNote/Ink/etc. nunca inserem
        // ou removem página) — sem isto, `CurrentPage = 1` (incondicional, linha abaixo) jogava a visão
        // de volta pro TOPO a cada edição, mesmo anotando a página 30 de um documento de 30 páginas: uma
        // fricção diária real, achado que a revisão anterior não tinha como enxergar (nenhum teste olhava
        // CurrentPage depois de um Apply em documento com mais de 1 página).
        int previousPage = CurrentPage;

        Pages.Clear();
        Thumbnails.Clear();
        BuildPagesAndThumbnails();

        // Restaura CurrentPage se a página capturada ainda existir no documento NOVO — e pede à View
        // pra rolar até ela (ScrollToPageRequested, 0-based, mesma convenção de ApplySearchResults/
        // hit.PageIndex acima: a View espera um ÍNDICE de página, não o número 1-based exibido).
        // `CurrentPage = previousPage` pode ser um NO-OP de notificação (SetProperty não dispara
        // OnCurrentPageChanged se o valor não mudou — ver comentário original abaixo, preservado), mas
        // Pages/Thumbnails acabaram de ser RECRIADOS: SyncCurrentThumbnail() garante o destaque de
        // miniatura correto de qualquer forma, sem depender dessa notificação.
        //
        // Página capturada NÃO existe mais (documento encolheu — ex.: Undo pra um snapshot mais curto)
        // -> cai pro comportamento ANTIGO (volta pro topo).
        if (previousPage >= 1 && previousPage <= Pages.Count)
        {
            CurrentPage = previousPage;
            ScrollToPageRequested?.Invoke(previousPage - 1);
        }
        else
        {
            CurrentPage = 1;
        }
        SyncCurrentThumbnail();
        // PageCountLabel depende de Pages.Count, que SEMPRE pode ter mudado; notifica direto em vez de
        // confiar no setter de CurrentPage (que pode não ter disparado — ver comentário acima).
        OnPropertyChanged(nameof(PageCountLabel));

        // Task 7 (Plano 3a): renova o cache de anotações pro documento NOVO — depois de
        // BuildPagesAndThumbnails (Pages.Count já reflete a contagem de página CORRETA pra agrupar).
        // Despachado via `_dispatcher` (mesmo motivo/fix do construtor, ver doc XML do campo).
        // Task 5 (Plano 3b): mesma disciplina pro cache de sumário — páginas movidas/excluídas mudam
        // (ou quebram, ver ACHADO EMPÍRICO em Contract.cs) os alvos dos bookmarks; a releitura do iText
        // devolve a árvore CORRENTE, que pode ter nós PODADOS em relação à árvore antiga.
        // Task 2 (Plano 3c): mesma disciplina pro cache de campos — ver bloco de comentário no topo da
        // seção "painel de Campos" (Obs 17: SÓ aqui, nunca no construtor — a carga inicial vem de
        // SeedFormFieldsCache via MainViewModel.OpenPath).
        // Task 4 (Plano 4): mesma disciplina pro cache de assinaturas — `Session.CommitSigned` (usado
        // por `SignCoreAsync`) dispara `Applied` por este MESMO caminho, então assinar já atualiza o
        // painel de graça, nenhum mecanismo novo.
        _dispatcher.BeginInvoke(() => { _ = RefreshAnnotationsByPageAsync(); _ = RefreshOutlineAsync(); _ = RefreshFormFieldsAsync(); _ = RefreshSignaturesAsync(); });
    }

    private void OnSessionDirtyChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    private void OnSessionFilePathChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(Title));

    /// Texto EXATO do brief (Task 1, Plano 5) — teto de disco do histórico de desfazer/refazer atingido.
    private const string UndoHistoryLimitReachedNotice =
        "Limite de histórico atingido; as edições mais antigas não podem mais ser desfeitas.";

    /// `Session.UndoHistoryLimitReached` já dispara 1x por documento (latch em `DocumentSession`, ver
    /// doc XML lá) — aqui só roteia pro seam de notificação (`_notifyInfo`), mesmo padrão de
    /// `FlattenForm`/`SignCoreAsync` (`_notifyInfo(...)` depois de uma operação concluída).
    private void OnSessionUndoHistoryLimitReached(object? sender, EventArgs e) => _notifyInfo(UndoHistoryLimitReachedNotice);

    // Task 4 (Plano 3a): CanUndoRedoChanged já dispara flip-only (ver doc XML em DocumentSession) —
    // aqui só precisa propagar pro VM (OnPropertyChanged pra qualquer binding futuro) e reavaliar os
    // dois RelayCommand gerados (NotifyCanExecuteChanged reavalia CanUndo/CanRedo — WPF desabilita os
    // botões ↶/↷ da toolbar automaticamente quando falso, mesmo mecanismo de SaveCommand/PrintCommand).
    private void OnSessionCanUndoRedoChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// Rodada 2 (revisão pós-branch): reavalia `CanExecute` dos comandos deste VM que agora compõem
    /// `!Session.IsEditInFlight` (`ApplyMarkupCommand`/`DeleteSelectedAnnotationCommand`/
    /// `EditSelectedAnnotationCommand` — ver os 3 `CanX` correspondentes) sempre que o pino COMPARTILHADO
    /// muda, armado por QUALQUER uma das duas VMs (este leitor OU o organizador). Task 2 (Plano 3c,
    /// rider da revisão): `ApplyFormValuesCommand` também compõe `!Session.IsEditInFlight`
    /// (`CanApplyFormValues`) — faltava aqui, mesmo lapso de omissão que os outros 3 já evitam.
    private void OnSessionEditInFlightChanged(object? sender, EventArgs e)
    {
        ApplyMarkupCommand.NotifyCanExecuteChanged();
        DeleteSelectedAnnotationCommand.NotifyCanExecuteChanged();
        EditSelectedAnnotationCommand.NotifyCanExecuteChanged();
        ApplyFormValuesCommand.NotifyCanExecuteChanged();
        // Task 3 (Plano 3c): FlattenFormCommand também compõe !Session.IsEditInFlight — mesmo lapso que
        // ApplyFormValuesCommand já corrigiu na Rodada 2 (ver doc XML acima), aplicado aqui de saída.
        FlattenFormCommand.NotifyCanExecuteChanged();
        // Task 3 (Plano 4): SignCommand também compõe !Session.IsEditInFlight (CanSign) — mesma disciplina.
        SignCommand.NotifyCanExecuteChanged();
        // Task 4 (Plano 15): RecognizeTextCommand compõe CanEdit && !Session.IsEditInFlight — mesma disciplina.
        RecognizeTextCommand.NotifyCanExecuteChanged();
    }

    // Task 4 (Plano 3a): ↶ Desfazer (Ctrl+Z) / ↷ Refazer (Ctrl+Y) — comandos por DOCUMENTO (cada aba
    // tem sua própria pilha de undo/redo, mesmo padrão de ZoomIn/ZoomOut abaixo), diferente de
    // Save/Print (que vivem no MainViewModel e operam sobre SelectedDocument porque precisam de
    // diálogos/config compartilhados). CanExecute aponta direto pras propriedades espelhadas acima —
    // CommunityToolkit.Mvvm aceita um nome de PROPRIEDADE bool aqui, não só método (mesmo mecanismo
    // usado por CanSave/CanPrint no MainViewModel, só que lá são métodos por convenção local).
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Session.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Session.Redo();

    // Task 6 (Plano 3a): 3 botões toggle-less da toolbar (🖍 Marca-texto, sublinhar, riscar) trocam a
    // cor SELECIONADA (não aplicam nada sozinhos) — cada botão de cor é um comando PARAMETERLESS
    // dedicado (em vez de 1 comando genérico recebendo o ARGB) pra evitar o problema clássico de
    // CommandParameter em XAML: um literal definido como atributo simples (`CommandParameter="..."`)
    // chega em ICommand.Execute como STRING crua, não como `uint` — RelayCommand<uint> lançaria
    // InvalidCastException tentando um cast direto. 3 comandos nomeados são a via mais simples e mais
    // testável (nenhuma conversão de tipo em voo), mesmo espírito de simplicidade dos outros comandos
    // pequenos deste VM (ZoomIn/ZoomOut).
    [RelayCommand] private void SelectColorAmarelo() => SelectedMarkupColorArgb = ColorAmarelo;
    [RelayCommand] private void SelectColorVerde() => SelectedMarkupColorArgb = ColorVerde;
    [RelayCommand] private void SelectColorVermelho() => SelectedMarkupColorArgb = ColorVermelho;

    // Rodada 2 (revisão pós-branch): `!Session.IsEditInFlight` composto aqui — o pino agora é
    // COMPARTILHADO com `OrganizerViewModel` (ver `OnSessionEditInFlightChanged`).
    private bool CanApplyMarkup() => CanEdit && HasActiveSelection && !Session.IsEditInFlight;

    /// Aplica marca-texto/sublinhado/riscado à seleção ATIVA (Task 6, Plano 3a) — o PRIMEIRO comando de
    /// edição de usuário de verdade ligado a `Session.ApplyEdit` (não `Apply` — edição de usuário
    /// precisa entrar no undo/redo, ver doc XML de `DocumentSession.ApplyEdit`). `kind` vem da toolbar
    /// via `{x:Static mPdf.Editing:AnnotationKind.Highlight}` (e Underline/Strikeout) — o MESMO truque
    /// de tipo forte que evita o problema de CommandParameter-como-string descrito acima, só que aqui
    /// `AnnotationKind` É o parâmetro do comando (`RelayCommand&lt;AnnotationKind&gt;`), não um valor
    /// fixo por botão — 1 método serve os 3 botões de ferramenta.
    ///
    /// Os retângulos da seleção ativa (`PageViewModel.SelectionPointRects`, já EM PONTOS — mesma fonte
    /// que desenha o overlay de seleção na tela) viram os `Quads` da anotação DIRETO, sem nenhuma
    /// síntese geométrica nova: a seleção de texto (Task 3) já produz exatamente os retângulos por
    /// linha que uma anotação de marcação de texto precisa. `LeftPt/BottomPt/RightPt/TopPt` (o bbox
    /// exigido por `AnnotationData`) é o envelope MIN/MAX de todos os quads — cobre a seleção inteira,
    /// mesmo multi-linha.
    ///
    /// `_editor.AddAnnotation` roda em `Task.Run` (o mesmo motivo de `EditCopy`/`StripSignatures` em
    /// `MainViewModel`: parse+reescrita iText é CPU-bound, nunca pode rodar na thread de UI que chama
    /// este comando). `Session.ApplyEdit` roda de volta na UI thread (o `await` retoma no
    /// `SynchronizationContext` capturado — mesmo mecanismo já usado por `Search.RunSearchAsync`),
    /// porque `Session.Apply`/`ApplyEdit` são UI-thread-only por contrato (ver doc XML lá).
    ///
    /// ACHADO (revisão própria, medido — não só lido no código): este método NÃO chama
    /// `ClearSelection()` explicitamente depois de `Session.ApplyEdit`, embora o brief da task pedisse
    /// isso. Testei via mutação (comentar uma chamada explícita aqui) e o teste de "seleção limpa" NÃO
    /// quebrou — `Session.ApplyEdit`/`Apply` já dispara `Session.Applied`, que `OnSessionApplied`
    /// (Task 3, infraestrutura pré-existente) já assina e responde chamando `ClearSelection()` ele
    /// mesmo (o `_pageWithSelection` de antes aponta pra um `PageViewModel` prestes a ser substituído
    /// pelo rebuild de `Pages`/`Thumbnails`). Uma chamada aqui seria código MORTO (sempre redundante
    /// com o handler), não uma segunda linha de defesa de verdade — diferente da redundância
    /// PROPOSITAL de `PdfSignedDocumentException` abaixo (que protege contra um `CanExecute`
    /// desatualizado, um cenário real). A prova de que a seleção realmente limpa continua no VM test
    /// deste comando (a garantia é real, só a MECÂNICA documentada no brief estava desatualizada) — e
    /// ganhou também um teste dedicado a `OnSessionApplied` provando que É ELE quem limpa (ver
    /// `SessionApply_ClearsActiveSelection_MutationProofOnOnSessionApplied` em DocumentViewModelTests).
    ///
    /// Erros: `PdfSignedDocumentException` — `CanApplyMarkup` já deveria ter barrado a chamada via
    /// `CanEdit` (documento assinado -> `IsSignedDocument=true` -> `CanEdit=false`), mas capturada
    /// mesmo assim como defesa em profundidade (mesmo espírito da precondição redundante que o próprio
    /// `mPdf.Editing.PdfEditor.GuardAgainstSignedDocument` já aplica — "o chamador não deve confiar que
    /// todo caminho presente e futuro sempre checa antes"). `PdfEditingException` (a base, cobre
    /// qualquer outra falha do iText, ex.: PDF corrompido) — notificada em pt-BR. Nenhum dos dois ramos
    /// chama `Session.ApplyEdit`: uma edição que FALHOU não deve tocar o snapshot nem apagar a seleção
    /// do usuário (ele pode querer tentar de novo, ou pelo menos ainda ver o que tinha selecionado).
    ///
    /// Rodada 2 (revisão pós-branch): `Session.TryBeginEdit()` — pino compartilhado, ver doc XML do
    /// campo em `DocumentSession` — arma SINCRONAMENTE aqui, logo após a defesa de `_pageWithSelection`
    /// e ANTES do 1º `await` (`EnsureRotationCacheFreshAsync`). `false` (outra edição em voo — do
    /// organizador OU deste mesmo VM) faz o método retornar sem tocar em NADA: mesmo espírito de
    /// "o marca-texto simplesmente não é aplicado, usuário tenta de novo" das outras 5 operações.
    [RelayCommand(CanExecute = nameof(CanApplyMarkup))]
    private async Task ApplyMarkup(AnnotationKind kind)
    {
        if (_pageWithSelection is not { } page) return; // defesa: CanExecute já deveria impedir chegar aqui
        if (!Session.TryBeginEdit()) return; // Rodada 2 — ver doc XML acima
        try
        {
            // COSTURA DE ROTAÇÃO (Task 3, Plano 3b) — ver doc XML de `IsPageRotated`: os retângulos de
            // `page.SelectionPointRects` vêm do quadro ROTACIONADO do PDFium (mesma TextPage que desenha
            // a seleção na tela); escrevê-los DIRETO como `Quads`/bbox de uma anotação (quadro
            // NÃO-ROTACIONADO do iText) posicionaria o marca-texto no lugar errado. No-op com aviso — a
            // seleção de TEXTO em si (Ctrl+C) continua funcionando normalmente em página girada, só a
            // ESCRITA de anotação fica bloqueada. I1 (revisão Opus): refresca o cache ANTES de confiar em
            // IsPageRotated — ver doc XML de `EnsureRotationCacheFreshAsync`.
            await EnsureRotationCacheFreshAsync();
            if (IsPageRotated(page.Index)) { _notifyError(RotatedPageNotice); return; }
            var quads = page.SelectionPointRects
                .Select(r => new PdfQuad(r.X, r.Y, r.X + r.Width, r.Y + r.Height))
                .ToList();
            if (quads.Count == 0) return; // idem — seleção vazia nunca deveria habilitar o comando

            var data = new AnnotationData
            {
                Kind = kind,
                PageIndex = page.Index,
                LeftPt = quads.Min(q => q.LeftPt),
                BottomPt = quads.Min(q => q.BottomPt),
                RightPt = quads.Max(q => q.RightPt),
                TopPt = quads.Max(q => q.TopPt),
                Quads = quads,
                ColorArgb = SelectedMarkupColorArgb,
                Author = _config.Autor,
            };

            byte[]? pdfDepois = await TryAddAnnotationAsync(data);
            if (pdfDepois is null) return; // falha tipada, já notificada dentro do helper

            // Session.Applied dispara aqui dentro (Apply -> Applied?.Invoke) -> OnSessionApplied já limpa
            // a seleção sozinho (ver ACHADO acima) — nenhuma chamada extra a ClearSelection() é necessária
            // nem correta (seria morta). TryApplyEdit (item 2, revisão final pré-merge): rede contra
            // ArgumentException do PDFium — nada mais a fazer aqui em qualquer dos dois desfechos (método
            // termina de qualquer forma, sucesso ou falha já notificada dentro do helper).
            TryApplyEdit(pdfDepois);
        }
        finally { Session.EndEdit(); }
    }

    /// Task 8 (Plano 3a, revisão): 3º call site do MESMO padrão "Task.Run(_editor.AddAnnotation) ->
    /// catch tipado -> notifica -> devolve" (`ApplyMarkup` acima, Task 6; `PlaceAnnotationAtAsync`,
    /// Task 7; agora `CommitDrawingAsync`, Task 8) — extraído pra não duplicar verbatim uma 3ª vez,
    /// mesma disciplina de "reuse, don't duplicate" já aplicada ao estado de arrasto da View (ver
    /// `PdfViewerControl.ResetGestureState`). Devolve os bytes resultantes em sucesso; `null` em
    /// qualquer falha TIPADA — já notificada AQUI DENTRO (mesmo texto/canal dos 3 chamadores) — pra
    /// que cada chamador só precise de 2 linhas (`if (pdfDepois is null) return;` + `Session.ApplyEdit`)
    /// em vez de repetir os 2 catches. O que cada chamador faz DEPOIS do sucesso (ApplyEdit sozinho,
    /// ou +one-shot deactivate) continua no PRÓPRIO chamador — só a parte IDÊNTICA foi extraída.
    private async Task<byte[]?> TryAddAnnotationAsync(AnnotationData data)
    {
        byte[] pdfAntes = Session.Snapshot;
        try
        {
            return await Task.Run(() => _editor.AddAnnotation(pdfAntes, data));
        }
        catch (PdfSignedDocumentException)
        {
            _notifyError("Este documento está assinado — a edição foi bloqueada para preservar a assinatura. Use \"Editar uma cópia\".");
            return null;
        }
        catch (PdfEditingException ex)
        {
            _notifyError(ex.Message);
            return null;
        }
    }

    /// REDE (revisão final pré-merge, item 2) — os 6 call sites de `Session.ApplyEdit` deste VM
    /// (`ApplyMarkup`, `PlaceAnnotationAtAsync`, `PlaceStampAtAsync`, `CommitDrawingAsync`,
    /// `DeleteSelectedAnnotation`, `LiftSelectedAnnotationAsync`) estavam DESPROTEGIDOS contra
    /// `ArgumentException`: `ApplyEdit` -> `Apply` constrói um `PdfDocumentRenderer` NOVO antes de
    /// mutar qualquer estado (ver doc XML de `DocumentSession.Apply`) — se `mPdf.Editing` produzir bytes
    /// que o PDFium rejeita (não deveria, mas é o iText escrevendo, não este módulo lendo), essa
    /// construção lança `ArgumentException` CRUA. Sem esta rede, a exceção escapava de um `AsyncRelayCommand`/
    /// `Task` gerado (`async void` por baixo, mesmo mecanismo já documentado nos catches tipados
    /// acima/em `DeleteSelectedAnnotation`) e relançava em cima do `Dispatcher` sem handler em `src/`
    /// (antes do item 1 desta revisão — agora `App.OnDispatcherUnhandledException` pegaria, mas mata a
    /// UX mesmo assim: melhor notificar em pt-BR ESPECÍFICO da ação que falhou do que cair na mensagem
    /// genérica de última linha).
    ///
    /// `Apply` já garante que a sessão permanece INTACTA nesse caso (o renderer novo é construído ANTES
    /// de qualquer swap de `Snapshot`/`Renderer`/`PageSizes`) — este helper só evita que a exceção suba,
    /// devolvendo `false` pra que o chamador NÃO execute os efeitos colaterais de sucesso (ex.:
    /// desativar a ferramenta one-shot, limpar `_pendingStampBytes`).
    private bool TryApplyEdit(byte[] novo)
    {
        try
        {
            Session.ApplyEdit(novo);
            return true;
        }
        catch (ArgumentException)
        {
            _notifyError("O resultado da edição não pôde ser aplicado — o PDF gerado é inválido. Nenhuma alteração foi salva.");
            return false;
        }
    }

    // ==== Task 7 (Plano 3a): Nota adesiva + caixa de texto ==========================================

    /// Renova `AnnotationsByPage` (e o gate `_annotationsCacheSnapshot`) a partir do snapshot CORRENTE —
    /// chamada no construtor (carga inicial) e a cada `Session.Applied` (`OnSessionApplied` acima),
    /// sempre fire-and-forget lá; exposta `internal` (via `InternalsVisibleTo`, mesmo padrão de
    /// `ThumbnailRenderer`/`SelectionPointRects`) só para os testes de VM poderem `await` um ponto
    /// DETERMINÍSTICO em vez de correr atrás de uma Task descartada em produção — e pra `DeleteSelectedAnnotation`/
    /// `LiftSelectedAnnotationAsync` poderem se AUTO-CURAR chamando de novo depois de um erro tipado
    /// (revisão Opus, C1b — ver lá).
    ///
    /// Exemplar: cálculo de `IsSignedDocument` (`MainViewModel.OpenPath`) — `Task.Run` porque
    /// `ReadAnnotations` faz parse iText (CPU-bound), nunca na thread de UI. Guarda de obsolescência
    /// (`ReferenceEquals(Session.Snapshot, snapshot)`): se OUTRO `Applied` disparou enquanto esta
    /// chamada estava em voo, o resultado (do snapshot ANTIGO) é descartado — o `Applied` mais novo já
    /// disparou (ou vai disparar) sua PRÓPRIA chamada, que é quem deve vencer.
    ///
    /// `retry` (revisão Opus, C1c — "un-freeze"): uma falha na leitura (ex.: `ObjectDisposedException`
    /// transitória) NUNCA podia travar o cache pra sempre no snapshot antigo — com o gate de leitura
    /// (C1a) isso significaria hit-test morto até a PRÓXIMA edição real do usuário disparar outro
    /// `Applied`. 1 retry (não um loop) depois de 500ms cobre uma falha transitória comum sem virar
    /// polling indefinido; se o retry TAMBÉM falhar, desiste — documentado: qualquer `Session.Applied`
    /// FUTURO (uma edição de verdade) já dispara `RefreshAnnotationsByPageAsync` de novo por conta
    /// própria (ver `OnSessionApplied`), então o cache nunca fica preso além do próximo evento real.
    internal async Task RefreshAnnotationsByPageAsync(bool retry = true)
    {
        byte[] snapshot = Session.Snapshot;
        int pageCount = Pages.Count;
        IReadOnlyList<AnnotationData> read;
        IReadOnlyList<int> rotations;
        try
        {
            read = await Task.Run(() => _editor.ReadAnnotations(snapshot));
            // Costura de rotação (Task 3, Plano 3b) — MESMO refresh/gate de AnnotationsByPage acima
            // (ver doc XML de `_pageRotations`): 2ª leitura iText separada (mesma granularidade "cada
            // método abre seu próprio PdfDocument" já usada por HasSignatures/ReadAnnotations em call
            // sites distintos deste VM), não uma 2ª fonte de obsolescência — as DUAS leituras
            // acontecem dentro do MESMO `try`, então uma falha em qualquer uma pula as DUAS (nenhum
            // estado parcial: ou o par inteiro avança, ou nenhum avança).
            rotations = await Task.Run(() => _editor.GetPageRotations(snapshot));
        }
        catch (Exception)
        {
            if (retry)
            {
                await Task.Delay(500);
                await RefreshAnnotationsByPageAsync(retry: false);
            }
            return;
        }
        if (!ReferenceEquals(Session.Snapshot, snapshot)) return; // obsoleto — ver doc XML acima

        var byPage = new List<AnnotationData>[pageCount];
        for (int i = 0; i < pageCount; i++) byPage[i] = new List<AnnotationData>();
        foreach (var a in read)
            if (a.PageIndex >= 0 && a.PageIndex < pageCount) byPage[a.PageIndex].Add(a);
        AnnotationsByPage = byPage.Select(l => (IReadOnlyList<AnnotationData>)l).ToArray();
        _pageRotations = rotations;
        _annotationsCacheSnapshot = snapshot; // C1a: só AQUI o gate avança — leitura + agrupamento OK
    }

    // ==== Task 5 (Plano 3b): Sumário (bookmarks) =====================================================

    /// Renova `Outline` a partir do snapshot CORRENTE — chamada no construtor (carga inicial) e a cada
    /// `Session.Applied` (ver os 2 sites de `_dispatcher.BeginInvoke`). SEM `retry`, ao contrário de
    /// `RefreshAnnotationsByPageAsync`: não há gate de leitura pra "un-freeze" aqui (ver doc XML de
    /// `Outline` acima) — uma falha transitória só deixa a árvore como estava (ou vazia, se ainda não
    /// tinha carregado nenhuma vez) até o PRÓXIMO `Session.Applied` real tentar de novo, exatamente como
    /// o comportamento de "desiste depois do retry" que `RefreshAnnotationsByPageAsync` já tem.
    internal async Task RefreshOutlineAsync()
    {
        byte[] snapshot = Session.Snapshot;
        IReadOnlyList<OutlineNode> read;
        try { read = await Task.Run(() => _editor.ReadOutline(snapshot)); }
        catch (Exception) { return; }
        if (!ReferenceEquals(Session.Snapshot, snapshot)) return; // obsoleto — mesma higiene de RefreshAnnotationsByPageAsync
        Outline = read;
    }

    /// Navega até a página de `node` (clique na aba Sumário, ver `OutlineView.xaml`) — no-op pra nó SEM
    /// página (organizacional puro, `PageIndex == null`, ex.: "Anexos" em fixture-sumario.pdf) e pra
    /// `node == null` (nada selecionado). Reusa `ScrollToPageRequested` — o MESMO evento que busca
    /// (`ApplySearchResults`) e a restauração de página pós-Apply (`OnSessionApplied`) já disparam;
    /// nenhum canal de navegação novo.
    [RelayCommand]
    private void NavigateToOutlineNode(OutlineNode? node)
    {
        if (node?.PageIndex is int pageIndex) ScrollToPageRequested?.Invoke(pageIndex);
    }

    /// Ponto-em-retângulo puro contra `AnnotationsByPage[pageIndex]` — topmost-last: percorre de trás
    /// pra frente, a PRIMEIRA que contém o ponto vence (a última da lista é a "mais em cima" — mesma
    /// convenção de z-order de qualquer pilha desenhada em ordem). `null` = nenhuma anotação sob o
    /// ponto.
    ///
    /// GATE DE LEITURA (revisão Opus, C1a): se `_annotationsCacheSnapshot` não é o `Session.Snapshot`
    /// CORRENTE (cache desatualizado — uma atualização está em voo, falhou, ou ainda nem rodou 1x),
    /// devolve `null` INCONDICIONALMENTE, mesmo que geometricamente o ponto caia dentro de um
    /// retângulo do cache velho — a View trata `null` como "nada sob o clique" e cai pro fallback de
    /// seleção de TEXTO (degradação aceita e documentada: melhor perder 1 clique de seleção de
    /// anotação por um instante do que apontar `SelectedAnnotation` pra algo que pode já não existir/ter
    /// mudado de posição no PDF vivo — ver doc XML de `_annotationsCacheSnapshot`).
    public AnnotationData? HitTestAnnotation(int pageIndex, double xPt, double yPt)
    {
        if (!ReferenceEquals(_annotationsCacheSnapshot, Session.Snapshot)) return null;
        // COSTURA DE ROTAÇÃO (Task 3, Plano 3b) — ver doc XML de `IsPageRotated`: hit-test SEMPRE nulo
        // numa página girada, mesmo que o ponto caia geometricamente dentro do retângulo (o retângulo
        // está no quadro NÃO-ROTACIONADO do iText; o clique chega em pt do quadro ROTACIONADO do
        // PDFium — comparar os dois sem transformação erraria o alvo).
        if (IsPageRotated(pageIndex)) return null;
        if (pageIndex < 0 || pageIndex >= AnnotationsByPage.Count) return null;
        var candidates = AnnotationsByPage[pageIndex];
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            var a = candidates[i];
            if (xPt >= a.LeftPt && xPt <= a.RightPt && yPt >= a.BottomPt && yPt <= a.TopPt)
                return a;
        }
        return null;
    }

    /// Chamado pela View num clique SIMPLES sem ferramenta ativa (`ActiveTool == None`) — seleciona a
    /// anotação sob o ponto, ou LIMPA a seleção se o clique caiu fora de qualquer retângulo (mesmo
    /// espírito de "clique simples limpa" já documentado para seleção de texto, Task 3).
    public void SelectAnnotationAt(int pageIndex, double xPt, double yPt) =>
        SelectedAnnotation = HitTestAnnotation(pageIndex, xPt, yPt);

    private bool CanUseAnnotationTool() => CanEdit;

    // Task 7 (Plano 3a): 2 botões toggle da toolbar (📝 Nota / 🅰 Texto) — MUTUAMENTE EXCLUSIVOS por
    // construção (um único campo `ActiveTool`, nunca 2 bools independentes): clicar no botão JÁ ativo
    // desliga; clicar no OUTRO troca (nunca acumula os 2 ligados).
    [RelayCommand(CanExecute = nameof(CanUseAnnotationTool))]
    private void ToggleStickyNoteTool() =>
        ActiveTool = ActiveTool == AnnotationTool.StickyNote ? AnnotationTool.None : AnnotationTool.StickyNote;

    [RelayCommand(CanExecute = nameof(CanUseAnnotationTool))]
    private void ToggleFreeTextTool() =>
        ActiveTool = ActiveTool == AnnotationTool.FreeText ? AnnotationTool.None : AnnotationTool.FreeText;

    // Task 8 (Plano 3a): 4 botões toggle da toolbar (✏ Desenho / ▢ Retângulo / ─ Linha / ↗ Seta) — MESMO
    // padrão de exclusividade mútua/CanUseAnnotationTool dos 2 acima; diferem deles só no fluxo de
    // mouse que a View arma (arrasto, não clique único — ver PdfViewerControl.Page_MouseLeftButtonDown/
    // CommitDrawingAsync abaixo).
    [RelayCommand(CanExecute = nameof(CanUseAnnotationTool))]
    private void ToggleInkTool() =>
        ActiveTool = ActiveTool == AnnotationTool.Ink ? AnnotationTool.None : AnnotationTool.Ink;

    [RelayCommand(CanExecute = nameof(CanUseAnnotationTool))]
    private void ToggleRectangleTool() =>
        ActiveTool = ActiveTool == AnnotationTool.Rectangle ? AnnotationTool.None : AnnotationTool.Rectangle;

    [RelayCommand(CanExecute = nameof(CanUseAnnotationTool))]
    private void ToggleLineTool() =>
        ActiveTool = ActiveTool == AnnotationTool.Line ? AnnotationTool.None : AnnotationTool.Line;

    [RelayCommand(CanExecute = nameof(CanUseAnnotationTool))]
    private void ToggleArrowTool() =>
        ActiveTool = ActiveTool == AnnotationTool.Arrow ? AnnotationTool.None : AnnotationTool.Arrow;

    /// Tamanho FIXO (brief, v1) do retângulo colocado por cada ferramenta — StickyNote: ícone 20x20pt;
    /// FreeText: caixa 200x60pt.
    private static (double w, double h) FixedSize(AnnotationKind kind) =>
        kind == AnnotationKind.StickyNote ? (20.0, 20.0) : (200.0, 60.0);

    /// Desloca (nunca encolhe) um retângulo de tamanho FIXO `w`x`h`, ancorado em `(x, y)` como canto
    /// inferior-esquerdo, pra dentro dos limites `[0, pageWidthPt] x [0, pageHeightPt]` — usado tanto
    /// na COLOCAÇÃO (`PlaceAnnotationAtAsync`, clique perto da borda) quanto no MOVER
    /// (`MoveSelectedAnnotationAsync`, arrastar pra fora da página). O `Math.Clamp` final cobre o caso
    /// degenerado (página menor que o próprio retângulo — não acontece com os tamanhos fixos de hoje
    /// contra qualquer página real, mas não deixa o retângulo escapar dos limites de qualquer jeito).
    private static (double left, double bottom, double right, double top) ClampToPage(
        double x, double y, double w, double h, double pageWidthPt, double pageHeightPt)
    {
        double left = x, bottom = y, right = x + w, top = y + h;
        if (right > pageWidthPt) { left -= right - pageWidthPt; right = pageWidthPt; }
        if (left < 0) { right -= left; left = 0; }
        if (top > pageHeightPt) { bottom -= top - pageHeightPt; top = pageHeightPt; }
        if (bottom < 0) { top -= bottom; bottom = 0; }
        left = Math.Clamp(left, 0, pageWidthPt);
        right = Math.Clamp(right, 0, pageWidthPt);
        bottom = Math.Clamp(bottom, 0, pageHeightPt);
        top = Math.Clamp(top, 0, pageHeightPt);
        return (left, bottom, right, top);
    }

    /// Chamado pela View no CLIQUE da página quando `ActiveTool != None` (brief: "clique posiciona").
    /// Fluxo: abre o diálogo pt-BR injetável (`_annotationDialog`, exemplar `IConfirmCloseService`) pra
    /// coletar o `Content`; cancelado (`null`) -> NADA acontece, a ferramenta continua ATIVA (usuário
    /// pode tentar de novo sem precisar reclicar no botão da toolbar). Texto confirmado -> monta o
    /// retângulo de tamanho FIXO no ponto clicado (clampado aos limites da página), `_editor.
    /// AddAnnotation` em `Task.Run` (mesmo motivo de `ApplyMarkup` acima — CPU-bound), `Session.
    /// ApplyEdit` na UI thread.
    ///
    /// ONE-SHOT (brief): `ActiveTool = AnnotationTool.None` SÓ depois de um `ApplyEdit` bem-sucedido —
    /// nunca no cancelamento nem numa falha do editor (mesmo espírito de `ApplyMarkup`: um erro não
    /// deve fazer o usuário perder o estado/contexto, aqui "a ferramenta que ele tinha escolhido").
    public async Task PlaceAnnotationAtAsync(int pageIndex, double xPt, double yPt)
    {
        if (ActiveTool == AnnotationTool.None) return;
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        // Rodada 2 (revisão pós-branch): PONTO DE ENTRADA SEM COMANDO — chamado direto de um
        // mouse-handler da View (`PdfViewerControl`), sem NENHUM `CanExecute` compondo o pino. O funil
        // de exclusão mútua precisa ser checado AQUI, sincronamente, ANTES do 1º `await`
        // (`EnsureRotationCacheFreshAsync` logo abaixo) — `false` (organizador ou outra operação deste
        // VM em voo) faz o clique virar NO-OP silencioso: "a anotação simplesmente não é colocada,
        // usuário tenta de novo" (ver doc XML de `DocumentSession.TryBeginEdit`).
        if (!Session.TryBeginEdit()) return;
        try
        {
            // COSTURA DE ROTAÇÃO (Task 3, Plano 3b) — ver doc XML de `IsPageRotated`. No-op com aviso;
            // ferramenta continua ativa (mesmo contrato de cancelamento/falha já documentado acima —
            // usuário pode tentar noutra página sem precisar reclicar o botão da toolbar). I1 (revisão
            // Opus): refresca o cache ANTES de confiar em IsPageRotated.
            await EnsureRotationCacheFreshAsync();
            if (IsPageRotated(pageIndex)) { _notifyError(RotatedPageNotice); return; }

            var kind = ActiveTool == AnnotationTool.StickyNote ? AnnotationKind.StickyNote : AnnotationKind.FreeText;
            string title = kind == AnnotationKind.StickyNote ? "Nova nota adesiva" : "Nova caixa de texto";
            string? text = _annotationDialog.PromptForText(title);
            if (text is null) return; // cancelado: ferramenta continua ativa

            var page = Pages[pageIndex];
            var (w, h) = FixedSize(kind);
            var (left, bottom, right, top) = ClampToPage(xPt, yPt, w, h, page.WidthPt, page.HeightPt);

            var data = new AnnotationData
            {
                Kind = kind,
                PageIndex = pageIndex,
                LeftPt = left, BottomPt = bottom, RightPt = right, TopPt = top,
                Content = text,
                ColorArgb = SelectedMarkupColorArgb,
                Author = _config.Autor,
            };

            byte[]? pdfDepois = await TryAddAnnotationAsync(data);
            if (pdfDepois is null) return; // falha tipada, já notificada dentro do helper

            // TryApplyEdit (item 2, revisão final pré-merge): `if (!...) return;` cobre o MESMO desfecho
            // "não desativa a ferramenta" que já valia pras 2 exceções tipadas notificadas dentro de
            // TryAddAnnotationAsync acima — falha aqui (bytes rejeitados pelo PDFium) merece o mesmo
            // tratamento (usuário pode tentar de novo, ferramenta continua ativa).
            if (!TryApplyEdit(pdfDepois)) return;
            // one-shot: só desativa DEPOIS do ApplyEdit ter sucesso (mutation proof: comentar esta linha
            // faz PlaceAnnotationAtAsync_Success_DeactivatesToolAfterPlacement_MutationProof falhar —
            // RED->GREEN verificado ao vivo, ver task-7-report.md).
            ActiveTool = AnnotationTool.None;
        }
        finally { Session.EndEdit(); }
    }

    // ==== Task 9 (Plano 3a): Carimbos de imagem (galeria) ============================================

    /// Ativa a ferramenta de carimbo com os bytes de UM carimbo escolhido na galeria (chamado por
    /// `MainViewModel` quando o usuário clica numa miniatura da galeria — `MainViewModel` é quem lê os
    /// bytes via `StampGallery.LoadBytes`; este VM não conhece a galeria, mesmo desacoplamento de
    /// `RecentFilesStore`/`AppConfig`). Clicar no MESMO carimbo já ativo DESLIGA a ferramenta — mesma
    /// semântica de "clicar de novo desliga" dos 6 botões `Toggle*Tool` acima, só que aqui o "botão" é
    /// um item de galeria (comparado pelo CONTEÚDO dos bytes, não por identidade de referência — a View
    /// nunca reaproveita o mesmo `byte[]` entre 2 cliques). Gated por `CanUseAnnotationTool` (mesmo
    /// `CanEdit` que os demais — documento assinado não ativa a ferramenta).
    public void ToggleStampTool(byte[] imageBytes)
    {
        if (!CanUseAnnotationTool()) return;

        bool sameStampAlreadyActive = ActiveTool == AnnotationTool.ImageStamp
            && _pendingStampBytes is { } current && current.AsSpan().SequenceEqual(imageBytes);
        if (sameStampAlreadyActive)
        {
            ActiveTool = AnnotationTool.None;
            _pendingStampBytes = null;
            return;
        }

        _pendingStampBytes = imageBytes;
        // Task 3 (Plano 7): reafirma o teto de largura DESTA origem (galeria) explicitamente — protege
        // contra um `ToggleImageTool` anterior ter deixado `_pendingStampMaxWidthPt` em 200pt (resíduo
        // aceito documentado no campo, mas cada ativação PRECISA setar o valor CORRETO pra sua própria
        // origem, nunca confiar no que sobrou de uma ativação alheia).
        _pendingStampMaxWidthPt = MaxStampWidthPt;
        ActiveTool = AnnotationTool.ImageStamp;
    }

    /// Lê o tamanho NATURAL (em pixels) de uma imagem PNG/JPG via os decoders nativos do WPF (não
    /// precisa de iText — decodificar cabeçalho de imagem não cruza a fronteira AGPL) e devolve o
    /// tamanho a COLOCAR na página, em pontos: DECISÃO v1 (brief: "natural image size scaled to max
    /// 150pt width, keep aspect") — sem metadado de DPI confiável num PNG/JPG qualquer, este módulo
    /// interpreta 1px = 1pt (tamanho "natural" = dimensão em pixels tomada diretamente como pontos),
    /// depois clampa a LARGURA a `maxWidthPt`, preservando a proporção — imagens menores que
    /// `maxWidthPt` de largura ficam no tamanho natural (nunca AUMENTADAS); maiores encolhem.
    /// `maxWidthPt` (Task 3, Plano 7 — antes uma constante fixa `MaxStampWidthPt`): parametrizado pra
    /// servir os 2 tetos distintos (galeria vs "🖼 Imagem") sem duplicar este método — ver
    /// `_pendingStampMaxWidthPt`/chamador em `PlaceStampAtAsync`.
    private static (double widthPt, double heightPt) NaturalStampSize(byte[] imageBytes, double maxWidthPt)
    {
        using var stream = new MemoryStream(imageBytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        double wPx = frame.PixelWidth, hPx = frame.PixelHeight;
        double widthPt = Math.Min(wPx, maxWidthPt);
        double heightPt = wPx > 0 ? widthPt * hPx / wPx : widthPt;
        return (widthPt, heightPt);
    }

    /// Chamado pela View no CLIQUE da página quando `ActiveTool == AnnotationTool.ImageStamp` (exemplar:
    /// `PlaceAnnotationAtAsync`, mesmo one-shot/clamp — mas SEM diálogo: os bytes já vieram da galeria
    /// via `ToggleStampTool`). O ponto clicado é o canto INFERIOR-ESQUERDO do carimbo (mesma convenção
    /// de `PlaceAnnotationAtAsync`), tamanho = `NaturalStampSize` clampado aos limites da página
    /// (`ClampToPage`, mesmo helper). Decodificação de imagem inválida (bytes corrompidos/não é uma
    /// imagem de verdade — não deveria acontecer com um arquivo já filtrado por `StampGallery.Add`, mas
    /// defesa em profundidade) vira notificação pt-BR em vez de deixar a exceção escapar do
    /// fire-and-forget da View. ONE-SHOT: `ActiveTool` só desativa DEPOIS de um `ApplyEdit` bem-sucedido
    /// (mesmo contrato de `PlaceAnnotationAtAsync`/`CommitDrawingAsync`).
    public async Task PlaceStampAtAsync(int pageIndex, double xPt, double yPt)
    {
        if (ActiveTool != AnnotationTool.ImageStamp) return;
        if (_pendingStampBytes is not { Length: > 0 } bytes) return;
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        // Rodada 2 — mesmo funil sem-comando de PlaceAnnotationAtAsync (ver doc XML lá).
        if (!Session.TryBeginEdit()) return;
        try
        {
            // COSTURA DE ROTAÇÃO (Task 3, Plano 3b) — ver doc XML de `IsPageRotated`. I1 (revisão Opus):
            // refresca o cache ANTES de confiar em IsPageRotated.
            await EnsureRotationCacheFreshAsync();
            if (IsPageRotated(pageIndex)) { _notifyError(RotatedPageNotice); return; }

            double widthPt, heightPt;
            try { (widthPt, heightPt) = NaturalStampSize(bytes, _pendingStampMaxWidthPt); }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
            {
                _notifyError("Não foi possível ler esta imagem de carimbo.");
                return;
            }

            var page = Pages[pageIndex];
            var (left, bottom, right, top) = ClampToPage(xPt, yPt, widthPt, heightPt, page.WidthPt, page.HeightPt);

            var data = new AnnotationData
            {
                Kind = AnnotationKind.ImageStamp,
                PageIndex = pageIndex,
                LeftPt = left, BottomPt = bottom, RightPt = right, TopPt = top,
                ImageBytes = bytes,
                Author = _config.Autor,
            };

            byte[]? pdfDepois = await TryAddAnnotationAsync(data);
            if (pdfDepois is null) return; // falha tipada, já notificada dentro do helper

            // TryApplyEdit (item 2, revisão final pré-merge) — mesmo contrato de PlaceAnnotationAtAsync:
            // falha não desativa a ferramenta nem limpa os bytes pendentes do carimbo.
            if (!TryApplyEdit(pdfDepois)) return;
            ActiveTool = AnnotationTool.None;
            _pendingStampBytes = null;
        }
        finally { Session.EndEdit(); }
    }

    // ==== Task 3 (Plano 7): "🖼 Imagem" — click-to-place a partir de OpenFileDialog (não-galeria) ======
    //
    // EXEMPLAR EXATO (brief): o mecanismo de carimbo de imagem acima (Task 9, Plano 3a) —
    // ToggleStampTool/PlaceStampAtAsync, REUSADOS sem alteração de contrato. A ÚNICA diferença real é
    // A ORIGEM dos bytes: em vez de vir já pronta da galeria (`StampGallery`/`MainViewModel.
    // SelectStamp`), este método ABRE o diálogo, valida (magic-bytes + teto de pixels, ANTES do modo de
    // colocação — a galeria não valida nada na ativação) e normaliza EXIF (WPF, App-side — o motor
    // nunca honrou EXIF neste caminho, ao contrário de `IPdfEditor.ImageToPdf`/Task 1), depois ativa o
    // MESMO `ActiveTool.ImageStamp`/`_pendingStampBytes` que `PlaceStampAtAsync` já sabe comitar.

    /// Corrige EXIF ANTES do modo de colocação — decisão registrada no brief desta task: o caminho
    /// AddAnnotation/AnnotationKind.ImageStamp (`PlaceStampAtAsync` acima) NUNCA aplicou a matriz de
    /// correção que `IPdfEditor.ImageToPdf` (Task 1, Plano 7) já tem pro OUTRO caminho de imagem (Abrir/
    /// Juntar/Inserir como página nova) — colocar uma foto de celular como "🖼 Imagem" faria a foto
    /// entrar de lado sem este fix (a MESMA armadilha, um caminho diferente). Opções cogitadas no brief:
    /// (a) normalizar pixels App-side via WPF, (b) estender o motor, (c) recusar fotos com EXIF
    /// rotacionado. Escolhida (a) — mais barata E correta: o motor só precisa expor a LEITURA pura do
    /// ângulo (`IPdfEditor.ReadJpegExifOrientation`, reusa o parser TIFF/IFD0 já testado de `ImageToPdf`
    /// SEM reimplementá-lo), a rotação de PIXELS em si é 100% WPF (`TransformedBitmap`+`RotateTransform`,
    /// mesmo sentido horário documentado no motor), nunca cruza pra dentro de iText/AGPL. PNG (sem EXIF)
    /// ou JPEG com Orientation 1/ausente -> `ReadJpegExifOrientation` devolve 0, devolve os bytes
    /// ORIGINAIS sem tocar (caminho quente, sem custo de reencode pra maioria dos arquivos — só fotos de
    /// celular fora de pé pagam o decode+reencode). Decodificação inválida propaga como uma das 3
    /// exceções que `ToggleImageTool` (único chamador) já trata, mesmo padrão de `NaturalStampSize`.
    private byte[] NormalizeExifRotation(byte[] bytes)
    {
        int rotation = _editor.ReadJpegExifOrientation(bytes);
        if (rotation == 0) return bytes;

        using var input = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var rotated = new TransformedBitmap(frame, new RotateTransform(rotation));

        using var output = new MemoryStream();
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(rotated));
        encoder.Save(output);
        return output.ToArray();
    }

    /// "🖼 Imagem" (toolbar, banda 2) — abre o diálogo pt-BR injetável (`_dialogs.PickImageToImport`,
    /// mesmo diálogo do "➕ Adicionar carimbo…" da galeria — Task 9, Plano 3a; a imagem escolhida aqui
    /// NUNCA é copiada pra dentro da `StampGallery`, é um carimbo AVULSO). "clicar de novo desliga"
    /// (mesma semântica dos demais `Toggle*Tool`) cobre QUALQUER `ImageStamp` já ativo, seja a origem a
    /// galeria ou este diálogo — um único botão "parar de colocar imagem" é mais previsível do que
    /// rastrear a origem só pra decidir se desliga. Cancelado (diálogo devolve `null`) -> NADA muda
    /// (brief: "cancel pick -> no mode"). Validação ANTES do modo de colocação (brief): magic-bytes
    /// (`IPdfEditor.IsSupportedImage`, mesma mensagem nomeando os formatos suportados de
    /// `ImageImport.ConvertToPdf`) primeiro (mais barato — sniff puro, sem decodificar nada), DEPOIS o
    /// teto de pixels (`IPdfEditor.IsWithinImagePixelLimit`, mesmo teto de 50MP de `ImageToPdf`/Task 1,
    /// aplicado aqui porque o caminho AddAnnotation/ImageStamp NUNCA teve teto nenhum — implementado
    /// ANTES do teto existir, nunca retrofitado), DEPOIS CMYK (`IPdfEditor.IsCmykJpeg`, FIX pós-revisão
    /// — mesmo detector que `ImageToPdf` já aplica, mesma lacuna histórica do teto de pixels: o caminho
    /// AddAnnotation/ImageStamp nunca recusou CMYK; render CMYK embutido via PDFium é intestável, ver
    /// Contract.cs — ACEITO: a galeria de carimbos [`ToggleStampTool`, Task 9/Plano 3a] continua sem
    /// este gate, ledgerado separadamente no relatório desta task, não corrigido aqui). EXIF normalizado
    /// por ÚLTIMO (`NormalizeExifRotation` acima) — só paga o custo de decode+reencode DEPOIS que os 3
    /// checks mais baratos (header-only, sem decodificar pixel nenhum) já aprovaram.
    /// Gated por `CanUseAnnotationTool` (mesmo `CanEdit` de todo `Toggle*ToolCommand`).
    [RelayCommand(CanExecute = nameof(CanUseAnnotationTool))]
    private void ToggleImageTool()
    {
        if (ActiveTool == AnnotationTool.ImageStamp)
        {
            ActiveTool = AnnotationTool.None;
            _pendingStampBytes = null;
            return;
        }

        if (_dialogs.PickImageToImport() is not { } path) return; // cancelado — brief: "no mode"

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException ex) { _notifyError(ex.Message); return; }

        if (!_editor.IsSupportedImage(bytes))
        {
            _notifyError($"'{Path.GetFileName(path)}' não é uma imagem JPG/PNG válida — formatos suportados: JPG, PNG.");
            return;
        }

        if (!_editor.IsWithinImagePixelLimit(bytes))
        {
            _notifyError("Imagem excede o limite de 50MP suportado. Use uma imagem menor.");
            return;
        }

        if (_editor.IsCmykJpeg(bytes))
        {
            _notifyError("JPEG CMYK não é suportado. Converta para RGB.");
            return;
        }

        try { bytes = NormalizeExifRotation(bytes); }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            _notifyError("Não foi possível ler esta imagem de carimbo.");
            return;
        }

        _pendingStampBytes = bytes;
        _pendingStampMaxWidthPt = MaxPickedImageWidthPt;
        ActiveTool = AnnotationTool.ImageStamp;
    }

    // ---- Task 4 (Plano 7): "📤 Exportar" (página como imagem, PNG/JPG) --------------------------------
    //
    // LEITURA PURA (mPdf.Rendering nunca muta nada) -- SEM `CanExecute`/gate nenhum: ao contrário de todo
    // `Toggle*ToolCommand`/`ApplyMarkupCommand` acima (gated por `CanEdit`/`CanUseAnnotationTool`), este
    // comando funciona em QUALQUER documento aberto, INCLUSIVE assinado -- mesma política uniforme de
    // leitura já registrada em `mPdf.Editing.Contract` para `ExtractPages`/`MergeDocuments`/
    // `SplitByRanges`/`GetPageRotations`/`ReadOutline`/`ReadFormFields` (nenhuma dessas MUTA o PDF
    // assinado, só o LEEM para produzir algo novo -- ver doc XML de `ExportImageViewModel`). NÃO arma o
    // funil (`Session.TryBeginEdit`/`ApplyEdit`) -- nenhuma mutação de `Session` acontece.
    //
    // `Session.Snapshot` é capturado UMA VEZ aqui (antes do diálogo abrir) -- coerência de instantâneo:
    // uma edição concorrente que aterrisse em `Session` DURANTE a exportação (diálogo NÃO-modal? não --
    // `ShowExportImageDialog` é modal, mas o brief pede o mesmo contrato de `PdfPrintPaginator` mesmo
    // assim, pela MESMA razão: o snapshot vira a fonte de verdade do que será exportado, documentado, não
    // implícito) exporta a versão CAPTURADA, nunca uma versão mais nova. `Session.Renderer.PageCount`
    // (cache já existente, ver `MainViewModel.Split`) só pra saber QUANTAS páginas existem -- o RENDER em
    // si usa um `PdfDocumentRenderer` DEDICADO sobre `Session.Snapshot`, criado dentro do próprio VM (
    // nunca o `Session.Renderer` da aba ativa, que é o cache de escala ÚNICA do visualizador).
    [RelayCommand]
    private void ExportImage()
    {
        var vm = new ExportImageViewModel(
            Session.Snapshot,
            Session.Renderer.PageCount,
            currentPageIndex: CurrentPage - 1,
            baseFileName: Path.GetFileNameWithoutExtension(Session.FileName));
        _exportImageDialog.ShowExportImageDialog(vm);
    }

    // ---- Task 3 (Plano 16): "Exportar como Word (.docx)" / "Exportar como Excel (.xlsx)" ------------
    //
    // LEITURA PURA (mesma classe de ExportImage acima): extrai texto+posições via mPdf.Rendering
    // (GetTextPage/GetPageSize), mapeia para os tipos neutros de mPdf.Export e gera um .docx/.xlsx NOVO —
    // o PDF de origem NÃO é tocado (nenhum funil/gate; funciona em assinado; o snapshot fica byte-idêntico).
    // Por isso NÃO há `CanExecute` gated por CanEdit: como os comandos são POR DOCUMENTO
    // (SelectedDocument.ExportWordCommand), já ficam desabilitados quando não há documento aberto, e
    // continuam habilitados em documento assinado (é leitura, não edição).
    //
    // AVISO SEM-TEXTO (brief): um escaneado sem OCR não tem texto extraível — exportar geraria um arquivo
    // VAZIO inútil. Antes de abrir o diálogo, verifica (fora da UI thread) se o documento tem QUALQUER
    // texto pesquisável (`_documentHasText`, o mesmo sinal do Ctrl+F/OCR). Se não tem, avisa via o seam de
    // prompt (`_notifyInfo`) sugerindo o OCR e NÃO abre o diálogo (nenhum arquivo é gerado).

    [RelayCommand]
    private Task ExportWord() => ExportDocumentCoreAsync(ExportDocumentKind.Word);

    [RelayCommand]
    private Task ExportExcel() => ExportDocumentCoreAsync(ExportDocumentKind.Excel);

    /// `internal` para os testes exercitarem a orquestração direto (mesmo precedente de
    /// `RecognizeTextCoreAsync`). Captura o snapshot/contagem/nome ANTES do 1º `await` (coerência de
    /// instantâneo — exemplar `ExportImage`). Pré-checagem de texto fora da UI thread; se vazio, aviso via
    /// prompt e retorna sem abrir o diálogo. Caso contrário, constrói o `ExportDocumentViewModel` (alcance/
    /// destino/progresso/cancelamento vivem nele) e manda a janela mostrá-lo.
    internal async Task ExportDocumentCoreAsync(ExportDocumentKind kind)
    {
        byte[] snapshot = Session.Snapshot;
        int pageCount = Session.Renderer.PageCount;
        string baseName = Path.GetFileNameWithoutExtension(Session.FileName);

        bool hasText;
        try { hasText = await Task.Run(() => _documentHasText(snapshot)); }
        catch (Exception ex)
        {
            _notifyError($"Não foi possível analisar o documento para exportação: {ex.Message}");
            return;
        }

        if (!hasText)
        {
            _notifyInfo("Este documento não tem texto pesquisável. Use \"Reconhecer texto (OCR)\" primeiro.");
            return;
        }

        var vm = new ExportDocumentViewModel(snapshot, pageCount, kind, baseName);
        _exportDocumentDialog.ShowExportDocumentDialog(vm);
    }

    /// Default de `_documentHasText`: abre um `PdfDocumentRenderer` PRÓPRIO sobre os bytes (renderer
    /// dedicado, seam separado do viewer) e reusa `PdfTextSearch.DocumentHasText` — o MESMO sinal barato
    /// que o Ctrl+F/OCR já usam ("alguma página tem 1+ caractere?"). Leitura pura, sem UI.
    private static bool DefaultDocumentHasText(byte[] pdf)
    {
        using var renderer = new PdfDocumentRenderer(pdf);
        return PdfTextSearch.DocumentHasText(renderer, CancellationToken.None);
    }

    // ==== Task 8 (Plano 3a): Desenho livre (Ink) + formas (Rectangle/Line/Arrow) =====================

    /// Commita o gesto de ARRASTO atual (Ink/Rectangle/Line/Arrow) — chamado pela View no MOUSE-UP de
    /// um arrasto com uma dessas 4 ferramentas ativas (`PdfViewerControl.Page_MouseLeftButtonUp`). A
    /// View já converteu TODO o caminho do arrasto pra pontos de página via `TextSelection.
    /// ScreenToPagePoint` (mesma conversão px->pt de sempre — nenhuma nova aqui); este método só decide
    /// a FORMA da anotação a partir do path e commita, exemplar `PlaceAnnotationAtAsync`.
    ///
    /// `pathPt`: pra Ink, TODOS os pontos amostrados do traço (uma polilinha só, v1 — brief: "ink = ONE
    /// stroke per gesture", multi-traço fica fora de escopo). Pra Rectangle/Line/Arrow, a View só
    /// precisa mandar 2 pontos (âncora do mouse-down + posição final do mouse-up) — o "rubber-band" da
    /// prévia ao vivo não muda essa regra: só o par início/fim FINAL define a forma commitada.
    ///
    /// MIN-GESTURE GUARD (brief): o BBOX do `pathPt` inteiro precisa ter uma diagonal de tela >=
    /// `MinGestureDragPx` — um clique quase-parado (bbox ~0x0) não cria uma anotação degenerada. Usa o
    /// MESMO bbox que a anotação vai carregar de qualquer forma (Left/Bottom/Right/Top), então não é
    /// uma 2ª passada pela geometria, só uma checagem a mais sobre um valor já calculado.
    ///
    /// CLAMP AOS LIMITES DA PÁGINA (achado da revisão do Task 8, item A1 — corrigido aqui por instrução
    /// do coordenador): diferente de `PlaceAnnotationAtAsync`/`MoveSelectedAnnotationAsync` (que sempre
    /// clampam via `ClampToPage`), a 1ª versão desta task deixava a geometria desenhada CRUA — WPF
    /// continua entregando `MouseMove` a um elemento com `CaptureMouse()` mesmo fora do Border da
    /// página, então um arrasto até a borda podia produzir um bbox que extrapola `[0,pageWidthPt] x
    /// [0,pageHeightPt]`. Fix: CLAMPA-ENTÃO-TRANSLADA — o bbox é clampado (`ClampToPage`, mesmo helper
    /// dos outros 2 métodos) e TODA a geometria do gesto (pontos do Ink, extremos de Line/Arrow) é
    /// deslocada pelo MESMO delta resultante (`TranslatePoints`/`Translate`, mesma família usada por
    /// `MoveSelectedAnnotationAsync`) — a FORMA nunca muda, só desliza pra dentro da página inteira.
    public async Task CommitDrawingAsync(int pageIndex, IReadOnlyList<PdfPoint> pathPt)
    {
        // As 4 ferramentas de desenho são enumeradas UMA VEZ só (revisão — o switch abaixo já devolve
        // `null` pra qualquer ActiveTool que não seja uma delas, então dobla como o guard de entrada:
        // sem essa 2ª enumeração/branch redundante, e sem um `default` morto precisando de comentário
        // "inatingível" pra se justificar).
        AnnotationKind? kind = ActiveTool switch
        {
            AnnotationTool.Ink => AnnotationKind.Ink,
            AnnotationTool.Rectangle => AnnotationKind.Rectangle,
            AnnotationTool.Line => AnnotationKind.Line,
            AnnotationTool.Arrow => AnnotationKind.Arrow,
            _ => null,
        };
        if (kind is null) return;
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        // Rodada 2 — mesmo funil sem-comando de PlaceAnnotationAtAsync (ver doc XML lá).
        if (!Session.TryBeginEdit()) return;
        try
        {
            // COSTURA DE ROTAÇÃO (Task 3, Plano 3b) — ver doc XML de `IsPageRotated`. Checado ANTES do
            // guard de gesto mínimo abaixo: um usuário que desenhou de verdade numa página girada merece
            // o aviso, não um retorno silencioso que pareceria "nada aconteceu por acaso". I1 (revisão
            // Opus): refresca o cache ANTES de confiar em IsPageRotated.
            await EnsureRotationCacheFreshAsync();
            if (IsPageRotated(pageIndex)) { _notifyError(RotatedPageNotice); return; }
            if (pathPt.Count < 2) return; // gesto degenerado (clique sem nenhum move) — nem bbox dá pra formar

            double left = pathPt.Min(p => p.XPt), right = pathPt.Max(p => p.XPt);
            double bottom = pathPt.Min(p => p.YPt), top = pathPt.Max(p => p.YPt);

            double scale = Zoom * PageViewModel.PtToPx;
            double diagPx = Math.Sqrt(Math.Pow((right - left) * scale, 2) + Math.Pow((top - bottom) * scale, 2));
            if (diagPx < MinGestureDragPx) return; // MIN-GESTURE GUARD — ver doc XML acima

            var page = Pages[pageIndex];
            var (clampedLeft, clampedBottom, clampedRight, clampedTop) =
                ClampToPage(left, bottom, right - left, top - bottom, page.WidthPt, page.HeightPt);
            double dx = clampedLeft - left, dy = clampedBottom - bottom;

            var data = new AnnotationData
            {
                Kind = kind.Value,
                PageIndex = pageIndex,
                LeftPt = clampedLeft, BottomPt = clampedBottom, RightPt = clampedRight, TopPt = clampedTop,
                InkStrokes = kind == AnnotationKind.Ink ? new[] { TranslatePoints(pathPt, dx, dy) } : null,
                LineStartPt = kind is AnnotationKind.Line or AnnotationKind.Arrow ? Translate(pathPt[0], dx, dy) : null,
                LineEndPt = kind is AnnotationKind.Line or AnnotationKind.Arrow ? Translate(pathPt[^1], dx, dy) : null,
                ColorArgb = SelectedMarkupColorArgb,
                Author = _config.Autor,
            };

            byte[]? pdfDepois = await TryAddAnnotationAsync(data);
            if (pdfDepois is null) return; // falha tipada, já notificada dentro do helper

            // TryApplyEdit (item 2, revisão final pré-merge) — mesmo contrato de PlaceAnnotationAtAsync.
            if (!TryApplyEdit(pdfDepois)) return;
            // one-shot: só desativa DEPOIS do ApplyEdit ter sucesso — mesmo contrato de PlaceAnnotationAtAsync
            // (mutation-proof: ver CommitDrawingAsync_Success_DeactivatesToolAfterCommit_MutationProof).
            ActiveTool = AnnotationTool.None;
        }
        finally { Session.EndEdit(); }
    }

    // Rodada 2: `!Session.IsEditInFlight` composto aqui também — ver doc XML de CanApplyMarkup.
    private bool CanDeleteSelectedAnnotation() => CanEdit && SelectedAnnotation is not null && !Session.IsEditInFlight;

    /// Task 8 (Plano 3a): só StickyNote/FreeText têm um `Content` TEXTUAL editável pelo diálogo — Ink/
    /// Rectangle/Line/Arrow (novos nesta task) não têm essa noção. Sem este filtro, um duplo-clique num
    /// retângulo recém-desenhado abriria "Editar caixa de texto" e o `Content` gravado nunca apareceria
    /// visualmente (só FreeText ganha um `/DA` de aparência de texto em `PdfEditor.BuildAnnotation`) —
    /// uma armadilha de UX que só passou a ser alcançável NESTA task (antes, só um `/Ink`/`/Square`/
    /// `/Line` de origem EXTERNA podia cair em `AnnotationsByPage` com um desses kinds). Delete continua
    /// valendo pra QUALQUER kind (`CanDeleteSelectedAnnotation` acima, sem este filtro extra) — apagar
    /// um desenho faz sentido pra todos. Mesmo filtro exclui ImageStamp (Task 9, Plano 3a) por construção
    /// (a lista permitida é um ALLOWLIST de 2 kinds, não um denylist) — ImageStamp também não é liftável
    /// (ver DECISÃO em `MoveSelectedAnnotationAsync`), então nunca deveria ter chegado aqui de qualquer
    /// forma, mas o filtro cobre os 2 casos com a MESMA linha.
    // Rodada 2: `!Session.IsEditInFlight` composto aqui também — ver doc XML de CanApplyMarkup.
    private bool CanEditSelectedAnnotation() =>
        CanEdit && SelectedAnnotation is { Kind: AnnotationKind.StickyNote or AnnotationKind.FreeText }
        && !Session.IsEditInFlight;

    /// Del (brief) — Remove via `ApplyEdit` a anotação atualmente selecionada. `SelectedAnnotation`
    /// sem `Id` (residual: um `/Text`/`/FreeText` de origem EXTERNA sem `/NM`, ver doc XML de
    /// `PdfEditor.ReadAnnotations`) não tem identidade estável pra remover — no-op defensivo.
    [RelayCommand(CanExecute = nameof(CanDeleteSelectedAnnotation))]
    private async Task DeleteSelectedAnnotation()
    {
        if (SelectedAnnotation is not { Id: not null } sel) return;
        if (!Session.TryBeginEdit()) return; // Rodada 2 — ver doc XML de ApplyMarkup/PlaceAnnotationAtAsync
        try
        {
            byte[] pdfAntes = Session.Snapshot;
            byte[] pdfDepois;
            try { pdfDepois = await Task.Run(() => _editor.RemoveAnnotation(pdfAntes, sel.Id)); }
            catch (PdfSignedDocumentException)
            {
                _notifyError("Este documento está assinado — a edição foi bloqueada para preservar a assinatura. Use \"Editar uma cópia\".");
                return;
            }
            catch (PdfEditingException ex)
            {
                _notifyError(ex.Message);
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // REDE TIPADA (revisão Opus, C1b — crash real fechado): `RemoveAnnotation` lança
                // `InvalidOperationException` CRUA quando o Id "não é encontrado" (ver doc XML de
                // `mPdf.Editing.PdfEditor.RemoveAnnotation`) — não é uma `PdfEditingException`, então os 2
                // catches acima NUNCA pegavam isso. Sem esta rede, a exceção escapava pro
                // `AsyncRelayCommand` gerado (`Execute(null)`, chamado pelo Del do teclado — ver
                // `PdfViewerControl.OnPreviewKeyDown`), que relança em cima do `Dispatcher` sem NENHUM
                // handler em `src/` -> processo morre, documento sujo perdido. `ArgumentException` (cobre
                // `ArgumentOutOfRangeException`, subtipo) é a mesma classe de risco residual (ex.: `PageIndex`
                // de uma anotação cujo cache ficou desatualizado depois que o documento encolheu).
                // Self-heal: a causa mais provável é o cache estar desatualizado — renova pra não repetir o
                // mesmo erro no próximo clique (o gate de leitura, C1a, já devia ter impedido a SELEÇÃO
                // chegar até aqui na maioria dos casos; esta rede cobre a janela residual entre o hit-test e
                // a chamada real, e qualquer outra causa que a C1a não cubra).
                _notifyError(ex.Message);
                await RefreshAnnotationsByPageAsync();
                return;
            }

            // Session.Applied -> OnSessionApplied já limpa SelectedAnnotation sozinho (mesmo ACHADO da
            // Task 6 pra seleção de texto — ver doc XML de ApplyMarkup) — nenhuma chamada extra aqui.
            // TryApplyEdit (item 2, revisão final pré-merge): rede contra ArgumentException do PDFium.
            TryApplyEdit(pdfDepois);
        }
        finally { Session.EndEdit(); }
    }

    /// LIFT (brief/contrato Task 2): Remove(Id) -> `_editor.AddAnnotation` com uma cópia MODIFICADA
    /// (mesmo Id) -> `ApplyEdit`. Compartilhado por `EditSelectedAnnotation` (muda `Content`) e
    /// `MoveSelectedAnnotationAsync` (muda geometria) — as DUAS operações são o MESMO pipeline, só o
    /// que muda no `with` é diferente; extrair evita duplicar o try/catch de exceções tipadas.
    ///
    /// Devolve `true` só quando `ApplyEdit` REALMENTE aconteceu (revisão Opus, I4) — `false` em
    /// qualquer falha tipada (já notificada aqui dentro). `MoveSelectedAnnotationAsync` usa o retorno
    /// pra saber se precisa restaurar o overlay de seleção pra posição REAL (um `false` significa que a
    /// posição de PREVIEW que a View já tinha desenhado durante o arrasto nunca vingou).
    ///
    /// Rodada 2 (revisão pós-branch): FUNIL ÚNICO pros 2 chamadores (`EditSelectedAnnotation` — tem
    /// `CanExecute` próprio; `MoveSelectedAnnotationAsync` — PONTO DE ENTRADA SEM COMANDO, mouse-handler
    /// de arrasto) — armar AQUI (1 vez, no topo deste método, antes do 1º `await`) cobre os DOIS sem
    /// duplicar o gate em cada chamador. `false` (outra edição em voo) reaproveita o MESMO `bool` de
    /// retorno que as 3 falhas tipadas abaixo já usam — `MoveSelectedAnnotationAsync` já sabe restaurar
    /// o overlay quando este método devolve `false`, nenhum caso novo necessário lá.
    private async Task<bool> LiftSelectedAnnotationAsync(AnnotationData lifted)
    {
        if (!Session.TryBeginEdit()) return false;
        try
        {
            byte[] pdfAntes = Session.Snapshot;
            byte[] pdfDepois;
            try
            {
                pdfDepois = await Task.Run(() =>
                {
                    var afterRemove = _editor.RemoveAnnotation(pdfAntes, lifted.Id!);
                    return _editor.AddAnnotation(afterRemove, lifted);
                });
            }
            catch (PdfSignedDocumentException)
            {
                _notifyError("Este documento está assinado — a edição foi bloqueada para preservar a assinatura. Use \"Editar uma cópia\".");
                return false;
            }
            catch (PdfEditingException ex)
            {
                _notifyError(ex.Message);
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Mesma rede/auto-cura de DeleteSelectedAnnotation acima (C1b) — o lift faz Remove+Add, os
                // 2 podem lançar `InvalidOperationException`/`ArgumentException` cruas (Id não encontrado no
                // Remove; Id duplicado ou PageIndex fora do intervalo no Add, se o cache estiver
                // desatualizado o bastante pra ter escapado do gate de leitura, C1a).
                _notifyError(ex.Message);
                await RefreshAnnotationsByPageAsync();
                return false;
            }

            // TryApplyEdit (item 2, revisão final pré-merge): reaproveita o MESMO `bool` de retorno já usado
            // pelos 3 desfechos de falha acima — MoveSelectedAnnotationAsync já sabe restaurar o overlay de
            // seleção pra posição REAL quando este método devolve false, então nenhum chamador precisa de
            // um caso novo.
            return TryApplyEdit(pdfDepois);
        }
        finally { Session.EndEdit(); }
    }

    /// Duplo-clique (ou um botão ✏, brief) — edita o `Content` da anotação selecionada via o MESMO
    /// diálogo de `PlaceAnnotationAtAsync`, pré-preenchido com o texto ATUAL (`initialText: sel.
    /// Content`). Cancelado -> nada muda. Confirmado -> lift preservando TODOS os demais campos
    /// (posição, cor — incl. `null`, ver `AnnotationData.ColorArgb` — autor) via `sel with { Content =
    /// text }`, que só o `Content` é diferente do original por construção do `record`.
    [RelayCommand(CanExecute = nameof(CanEditSelectedAnnotation))]
    private async Task EditSelectedAnnotation()
    {
        if (SelectedAnnotation is not { Id: not null } sel) return;

        string title = sel.Kind == AnnotationKind.StickyNote ? "Editar nota adesiva" : "Editar caixa de texto";
        string? text = _annotationDialog.PromptForText(title, sel.Content);
        if (text is null) return; // cancelado

        // Edição não guia nenhuma preview de arrasto na View (diferente de Mover, abaixo) — o `bool` de
        // retorno não tem nada extra pra fazer aqui além do que Session.Applied já dispara sozinho.
        await LiftSelectedAnnotationAsync(sel with { Content = text });
    }

    /// Arrastar (brief) — chamado pela View no MOUSE-UP de um arrasto da anotação selecionada (a
    /// PREVIEW ao vivo do overlay seguindo o mouse é responsabilidade só da View, ver
    /// `PdfViewerControl.xaml.cs`; este método só COMMITA a posição final). Mesmo lift de
    /// `EditSelectedAnnotation`, mudando geometria (`LeftPt/BottomPt/RightPt/TopPt`) em vez de
    /// `Content` — tamanho preservado (só a POSIÇÃO desloca), clampado aos limites da página (mesmo
    /// `ClampToPage` de `PlaceAnnotationAtAsync`).
    ///
    /// RESTAURAÇÃO DO OVERLAY (revisão Opus, I4): a View já deslocou `AnnotationSelectionRect` pra uma
    /// posição de PREVIEW durante o arrasto (`Page_MouseMove`), ANTES de saber se o commit vai vingar.
    /// TODA saída — precoce (`CanEdit` falso/sem Id/página fora do intervalo) OU falha do lift — precisa
    /// devolver o overlay pra onde a anotação REALMENTE está (`SetAnnotationSelectionRectFor`); só o
    /// caminho de SUCESSO não precisa (`Session.ApplyEdit` já disparou `OnSessionApplied`, que zera
    /// `SelectedAnnotation`/overlay sozinho — mesmo ACHADO já documentado em `ApplyMarkup`/`DeleteSelectedAnnotation`).
    ///
    /// GEOMETRIA PRÓPRIA: Highlight/Underline/Strikeout (`Quads`, Task 6) e Ink/Line/Arrow (`InkStrokes`/
    /// `LineStartPt`/`LineEndPt`, Task 8) carregam pontos INDEPENDENTES do bbox Left/Bottom/Right/Top —
    /// mover só o bbox (como sempre bastou pra StickyNote/FreeText, cuja aparência É o bbox) deixaria a
    /// marcação/tinta/linha PARA TRÁS, descolada do retângulo que deveria envolvê-la: PDFium sintetiza a
    /// aparência a partir do `/QuadPoints`/`/InkList`/`/L`, não do `/Rect` (ver `PdfEditor.
    /// BuildAnnotation`). FIX (revisão pós-Task 8 — achado durante a revisão do próprio Task 8, corrigido
    /// aqui por instrução do coordenador): `Quads` NUNCA foi traduzido — bug PRÉ-EXISTENTE desde a Task 7
    /// (mover ficou genérico pra QUALQUER kind selecionável, incl. Highlight/Underline/Strikeout, que já
    /// existiam desde a Task 6), só descoberto ao revisar a MESMA classe de bug pros kinds novos.
    /// `TranslateQuads` abaixo fecha o mesmo buraco pra Highlight/Underline/Strikeout. Por isso o delta
    /// usa o resultado CLAMPADO (`left/bottom` já ajustados pra dentro da página), não
    /// `newLeftPt/newBottomPt` crus — perto da borda o clamp desloca MENOS que o pedido, e usar o delta
    /// cru arrancaria a marcação/tinta do bbox que passou a envolvê-la. `TranslateStrokes`/`Translate`/
    /// `TranslateQuads` abaixo são no-ops (`null`) pros kinds sem a geometria correspondente — `with` já
    /// preserva os campos que não mudam.
    public async Task MoveSelectedAnnotationAsync(double newLeftPt, double newBottomPt)
    {
        var sel = SelectedAnnotation;
        if (!CanEdit) { SetAnnotationSelectionRectFor(sel); return; }
        if (sel is not { Id: not null } target) { SetAnnotationSelectionRectFor(sel); return; }
        if (target.PageIndex < 0 || target.PageIndex >= Pages.Count) { SetAnnotationSelectionRectFor(sel); return; }
        // M1 (revisão Opus): guarda AUTODEFENSIVA — `SelectedAnnotation` tem setter PÚBLICO, então
        // `HitTestAnnotation` (já gateado, nunca devolve uma anotação de página girada) não é a ÚNICA
        // forma de `target` apontar pra uma página girada aqui (um chamador poderia setar
        // `SelectedAnnotation` diretamente). Mesmo aviso pt-BR dos outros 4 pontos de escrita; overlay
        // volta pra posição REAL (mesmo padrão de falha dos guards logo abaixo).
        if (IsPageRotated(target.PageIndex))
        {
            SetAnnotationSelectionRectFor(sel);
            _notifyError(RotatedPageNotice);
            return;
        }
        // Task 9 (Plano 3a) — DECISÃO DE DESIGN: ImageStamp NÃO é liftável nesta v1 (ver doc XML de
        // AnnotationData.ImageBytes). `ReadAnnotations` sempre devolve ImageBytes null — um lift
        // (Remove+Add, ver LiftSelectedAnnotationAsync) não teria como reconstruir a appearance stream
        // da imagem sem os bytes originais. Mover fica desabilitado para este Kind (Excluir continua
        // funcionando — RemoveAnnotation só apaga por Id, nunca precisa reler a imagem, ver
        // DeleteSelectedAnnotation). Overlay volta pra posição REAL, nunca fica preso na prévia de
        // arrasto que a View já tinha desenhado.
        if (target.Kind == AnnotationKind.ImageStamp) { SetAnnotationSelectionRectFor(sel); return; }

        double w = target.RightPt - target.LeftPt, h = target.TopPt - target.BottomPt;
        var page = Pages[target.PageIndex];
        var (left, bottom, right, top) = ClampToPage(newLeftPt, newBottomPt, w, h, page.WidthPt, page.HeightPt);
        double dx = left - target.LeftPt, dy = bottom - target.BottomPt;

        bool applied = await LiftSelectedAnnotationAsync(target with
        {
            LeftPt = left, BottomPt = bottom, RightPt = right, TopPt = top,
            Quads = TranslateQuads(target.Quads, dx, dy),
            InkStrokes = TranslateStrokes(target.InkStrokes, dx, dy),
            LineStartPt = Translate(target.LineStartPt, dx, dy),
            LineEndPt = Translate(target.LineEndPt, dx, dy),
        });
        if (!applied) SetAnnotationSelectionRectFor(target); // lift falhou (já notificado lá) -> overlay volta pro real
    }

    private static PdfPoint? Translate(PdfPoint? p, double dx, double dy) =>
        p is { } pt ? new PdfPoint(pt.XPt + dx, pt.YPt + dy) : null;

    /// Traslada UM traço/polilinha (lista de pontos) — extraído (revisão do fix batch) porque agora tem
    /// 2 chamadores: `TranslateStrokes` abaixo (1 chamada por traço de um Ink já EXISTENTE, no mover) e
    /// `CommitDrawingAsync` (o path do gesto INTEIRO, ao clampar um Ink recém-desenhado aos limites da
    /// página — ver A1 no relatório da Task 8).
    private static IReadOnlyList<PdfPoint> TranslatePoints(IReadOnlyList<PdfPoint> pts, double dx, double dy) =>
        pts.Select(p => new PdfPoint(p.XPt + dx, p.YPt + dy)).ToList();

    private static IReadOnlyList<IReadOnlyList<PdfPoint>>? TranslateStrokes(
        IReadOnlyList<IReadOnlyList<PdfPoint>>? strokes, double dx, double dy) =>
        strokes?.Select(s => TranslatePoints(s, dx, dy)).ToList();

    /// Fix do fix batch (coordenador) — mesma disciplina de `TranslateStrokes`/`Translate`, só que pra
    /// `Quads` (Highlight/Underline/Strikeout, Task 6): cada quad é 4 cantos independentes, todos
    /// deslocados pelo MESMO delta — a FORMA do quad (largura/altura de cada linha marcada) nunca muda,
    /// só a posição.
    private static IReadOnlyList<PdfQuad>? TranslateQuads(IReadOnlyList<PdfQuad>? quads, double dx, double dy) =>
        quads?.Select(q => new PdfQuad(q.LeftPt + dx, q.BottomPt + dy, q.RightPt + dx, q.TopPt + dy)).ToList();

    // Task 0 (Plano 3c): DefaultNotifyError/DefaultNotifyInfo mudaram de método estático local pra
    // UiPrompts.DocumentNotifyError/UiPrompts.NotifyInfo (ver ctor acima) — texto/ícone preservados.

    // Busca (Task 5): SEMPRE via Task.Run — PdfTextSearch.FindAll espera o gate global do PDFium por
    // página (ver doc XML de FindAll); nunca pode rodar direto na thread de UI que chama RunSearchAsync.
    private Task<IReadOnlyList<SearchHit>> SearchInDocument(string query, CancellationToken ct) =>
        Task.Run(() => (IReadOnlyList<SearchHit>)PdfTextSearch.FindAll(Session.Renderer, query, ct), ct);

    // Item (a) da Task 1 (Plano 3a): sonda "documento sem texto algum" injetada no SearchViewModel —
    // mesma disciplina de SearchInDocument acima (Task.Run, nunca a thread de UI). O cache real (só
    // pergunta 1x) vive no PRÓPRIO SearchViewModel (ver HasTextProbe ali) — não precisa duplicar
    // estado aqui, já que cada DocumentViewModel tem exatamente 1 SearchViewModel.
    private Task<bool> ProbeDocumentHasText(CancellationToken ct) =>
        Task.Run(() => PdfTextSearch.DocumentHasText(Session.Renderer, ct), ct);

    // Aplica os resultados da busca às páginas: distribui os retângulos de TODOS os hits (uma página
    // pode ter vários hits — cada um vira um grupo de retângulos fundidos por BuildLineRects), marca
    // o hit CORRENTE com destaque distinto na sua página, e pede a rolagem até ela.
    private void ApplySearchResults(IReadOnlyList<SearchHit> hits, int currentIndex)
    {
        foreach (var p in Pages) p.ClearHighlights();

        foreach (var group in hits.GroupBy(h => h.PageIndex))
        {
            var rects = group.SelectMany(h => TextSelection.BuildLineRects(h.Chars)).ToList();
            Pages[group.Key].SetHighlights(rects);
        }

        if (currentIndex >= 0 && currentIndex < hits.Count)
        {
            var hit = hits[currentIndex];
            Pages[hit.PageIndex].SetCurrentHighlight(TextSelection.BuildLineRects(hit.Chars));
            ScrollToPageRequested?.Invoke(hit.PageIndex);
        }
    }

    // Task 2 (Plano 5): os 3 ajustes de zoom que NÃO são "Ajustar à largura" limpam `_lastFitWasWidth`
    // — ver doc XML de `FitWidthRecalcRequested`. Um zoom manual (+/-) ou "Página inteira" é uma escolha
    // EXPLÍCITA do usuário; fechar o organizador depois não deve sobrescrevê-la.
    [RelayCommand] private void ZoomIn() { _lastFitWasWidth = false; Zoom = Math.Min(MaxZoom, Math.Round(Zoom + 0.1, 2)); }
    [RelayCommand] private void ZoomOut() { _lastFitWasWidth = false; Zoom = Math.Max(MinZoom, Math.Round(Zoom - 0.1, 2)); }

    public void FitWidth(double viewportWidthPx)
    {
        _lastFitWasWidth = true;
        double maxPageWidthPt = Pages.Max(p => p.WidthPt);
        Zoom = Clamp((viewportWidthPx - FitMarginPx) / (maxPageWidthPt * PageViewModel.PtToPx));
    }

    public void FitPage(double viewportWidthPx, double viewportHeightPx)
    {
        _lastFitWasWidth = false;
        double wPt = Pages.Max(p => p.WidthPt), hPt = Pages.Max(p => p.HeightPt);
        double byWidth = (viewportWidthPx - FitMarginPx) / (wPt * PageViewModel.PtToPx);
        double byHeight = (viewportHeightPx - FitMarginPx) / (hPt * PageViewModel.PtToPx);
        Zoom = Clamp(Math.Min(byWidth, byHeight));
    }

    private static double Clamp(double z) => Math.Clamp(z, MinZoom, MaxZoom);

    // página atual = primeira cuja faixa vertical acumulada (altura + margem) contém o offset de rolagem
    public void UpdateCurrentPageFromScroll(double verticalOffsetPx)
    {
        double acc = 0;
        for (int i = 0; i < Pages.Count; i++)
        {
            acc += Pages[i].DisplayHeight + PageMarginPx;
            if (verticalOffsetPx < acc) { CurrentPage = i + 1; return; }
        }
        CurrentPage = Pages.Count;
    }

    /// Topo de `pageIndex` no conteúdo rolável do PageList, em PX DE TELA — soma das alturas (+
    /// margem) de todas as páginas ANTERIORES (inverso de UpdateCurrentPageFromScroll: lá vamos de
    /// offset -> página, aqui de página -> offset). APROXIMADA, não exata: não conta a margem
    /// superior de 6px do primeiro Border nem um eventual padding do próprio ListBox — aceitável
    /// porque quem usa isto (scroll-to-hit da busca, revisão da Task 5, I1) soma um termo -Viewport/3
    /// por cima, que DOMINA esses poucos pixels de desvio, e o critério de convergência de lá é "o
    /// hit está dentro do viewport" (I1-B), não bater num offset exato.
    public double PageTopOffsetPx(int pageIndex)
    {
        double acc = 0;
        for (int i = 0; i < pageIndex; i++) acc += Pages[i].DisplayHeight + PageMarginPx;
        return acc;
    }

    partial void OnZoomChanged(double value)
    {
        _scheduler.CancelPending();
        foreach (var p in Pages) p.ApplyZoom(value);
        OnPropertyChanged(nameof(ZoomPercent));
    }

    /// Task 2 (Plano 9): mudança de DPI ao vivo (janela migrou pra um monitor de escala diferente) —
    /// mesmo padrão de `OnZoomChanged` acima (CancelPending descarta renders em voo no fator ANTIGO,
    /// evitando entregar um bitmap na densidade errada por corrida), mas NUNCA reconverte os overlays/
    /// seleção/destaques/caixa do carimbo: eles são puramente LÓGICOS (`zoom * PtToPx`), o fator de DPI
    /// não entra na conta deles (fronteira do brief) — só o BITMAP de cada página realizada precisa
    /// nascer de novo, mais denso ou mais raro. `RefreshDpi` (não `ApplyZoom`) é de propósito: pedir um
    /// ApplyZoom aqui reconverteria overlays que não mudaram de nada, trabalho à toa.
    partial void OnDpiFactorChanged(double value)
    {
        _scheduler.CancelPending();
        foreach (var p in Pages) p.RefreshDpi();
    }

    /// Task 1 (Plano 13): mesmo padrão EXATO de `OnDpiFactorChanged` acima — `SupersampleFactor` entra
    /// na MESMA função de escala (`PageViewModel.ComputeRenderScale`), então uma mudança ao vivo precisa
    /// do MESMO reflow (`CancelPending` descarta renders em voo no fator antigo; `RefreshDpi` — não
    /// `ApplyZoom` — porque só o BITMAP muda, os overlays/seleção/caixa do carimbo continuam puramente
    /// LÓGICOS). Sem este hook, setar `SupersampleFactor` (ex.: `doc.SupersampleFactor = 1.5` num teste,
    /// ou uma futura tela de configurações) não fazia NADA até o próximo evento que já disparasse
    /// RequestRender por outro motivo (zoom, DPI) — bug real, pego pelo teste STA ponta a ponta.
    partial void OnSupersampleFactorChanged(double value)
    {
        _scheduler.CancelPending();
        foreach (var p in Pages) p.RefreshDpi();
    }

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(PageCountLabel));
        SyncCurrentThumbnail();
    }

    // Task 6: mantém IsCurrent (destaque na miniatura) sincronizado com a página atual — mesma
    // conversão 1-based -> 0-based de UpdateCurrentPageFromScroll. Extraído (item 4, revisão final
    // pré-merge) pra ter um 2º chamador: OnSessionApplied, onde `CurrentPage = previousPage` pode ser
    // um NO-OP de notificação (valor idêntico ao já presente no campo — CommunityToolkit não dispara
    // OnCurrentPageChanged nesse caso) mas as Thumbnails foram RECRIADAS mesmo assim e precisam do
    // destaque sincronizado independente de notificação ter disparado ou não.
    private void SyncCurrentThumbnail()
    {
        int idx = CurrentPage - 1;
        foreach (var t in Thumbnails) t.IsCurrent = t.Index == idx;
    }

    // ==== Task 3 (Plano 3b): Organizador de páginas (modo "📄 Páginas") ==============================

    /// `OrganizerViewModel` da sessão ATIVA de organização, ou `null` fora do modo organizador — CRIADO
    /// quando `IsOrganizerOpen` vira `true`, DESCARTADO (renderer próprio via PendingDisposals — ver
    /// `OrganizerViewModel.Dispose`) quando vira `false`. `private set`: só este VM decide quando o
    /// organizador existe, a View só o LÊ (binding `{Binding Organizer.Pages}` etc. em
    /// `PageOrganizerView`).
    public OrganizerViewModel? Organizer { get; private set; }

    /// Toggle da toolbar "📄 Páginas" (brief) — TwoWay via `ToggleButton.IsChecked`, mesmo padrão de
    /// `MainViewModel.ThumbnailsVisible` (bool simples, sem comando dedicado). Entrar/sair do
    /// organizador não depende de `CanEdit`: VER a estrutura de páginas é sempre permitido, mesmo num
    /// documento assinado — só as operações MUTADORAS dentro do organizador (Rotate/Delete/Move) são
    /// gated (ver `OrganizerViewModel.CanOperateOnSelection`/`CanMoveLeft`/`CanMoveRight`).
    [ObservableProperty] private bool isOrganizerOpen;

    /// Teto do brief (Task 1, Plano 5) — ledger: 14,7 s pra encher a grade de miniaturas a 510 páginas.
    /// Acima deste número de páginas, `OnIsOrganizerOpenChanged` consulta `_confirmOrganizerScale` ANTES
    /// de `OpenOrganizer` — fronteira EXATA testada: 200 não avisa, 201 avisa (`>`, não `>=`).
    private const int OrganizerScaleWarningPageCount = 200;

    partial void OnIsOrganizerOpenChanged(bool value)
    {
        if (!value) { CloseOrganizer(); return; }

        int pageCount = Session.Renderer.PageCount;
        if (pageCount > OrganizerScaleWarningPageCount && !_confirmOrganizerScale.Confirm(BuildOrganizerScaleWarningMessage(pageCount)))
        {
            // Recusa mantém o leitor (brief): reverte o campo GERADO diretamente (nunca `IsOrganizerOpen
            // = false`, que reentraria neste MESMO método via o setter gerado por `[ObservableProperty]`
            // e chamaria `CloseOrganizer()` — desnecessário e semanticamente errado aqui, já que
            // `OpenOrganizer` nunca chegou a rodar: não há nada pra "fechar") — só notifica a UI (o
            // `ToggleButton` TwoWay) que o valor voltou a `false`, mesma técnica de qualquer setter que
            // precisa recusar uma mudança sem reentrar no próprio changed-handler.
            isOrganizerOpen = false;
            OnPropertyChanged(nameof(IsOrganizerOpen));
            return;
        }

        OpenOrganizer();
    }

    /// Texto EXATO do brief (Task 1, Plano 5), com `N` = a contagem real de páginas do documento.
    private static string BuildOrganizerScaleWarningMessage(int pageCount) =>
        $"Este documento tem {pageCount} páginas; o organizador pode levar alguns segundos para carregar. Continuar?";

    private void OpenOrganizer()
    {
        Organizer = new OrganizerViewModel(Session, _editor, _notifyError, () => CanEdit, _dialogs, _notifyInfo);
        OnPropertyChanged(nameof(Organizer));
    }

    /// Fechar devolve o leitor "à página equivalente" (brief): a PRIMEIRA página selecionada no
    /// organizador no momento do fechamento, ou — sem nenhuma seleção — a página que já estava
    /// CORRENTE no leitor antes de abrir o organizador (nunca voltamos ao topo sem motivo, mesmo
    /// espírito de `OnSessionApplied` preservando `CurrentPage` através de uma edição). Lido ANTES do
    /// `Dispose()` do organizador (que não muda `SelectedIndexes`, mas por clareza/ordem: ler primeiro,
    /// descartar depois).
    private void CloseOrganizer()
    {
        if (Organizer is not { } open) return;
        var indexes = open.SelectedIndexes;
        int target = indexes.Count > 0 ? indexes.Min() : CurrentPage - 1;
        open.Dispose();
        Organizer = null;
        OnPropertyChanged(nameof(Organizer));
        if (target >= 0 && target < Pages.Count)
        {
            CurrentPage = target + 1;
            ScrollToPageRequested?.Invoke(target);
        }
        // Deferência (Task 2, Plano 5) — ver doc XML de FitWidthRecalcRequested: o leitor acabou de
        // voltar a Visible (PdfViewerControl.ViewportWidth só volta a refletir a largura REAL depois
        // deste layout), e só faz sentido recalcular se o ÚLTIMO ajuste pedido pelo usuário foi
        // "Ajustar à largura" — nunca sobrescreve um zoom manual.
        if (_lastFitWasWidth) FitWidthRecalcRequested?.Invoke();
    }

    // ==== Task 2 (Plano 3c): painel de Campos (formulário AcroForm) ================================
    //
    // CACHE — DUAS origens, de PROPÓSITO diferentes do padrão de AnnotationsByPage/Outline (que só têm
    // 1 origem: fire-and-forget no construtor, ver doc XML de `_dispatcher`):
    //   1) Carga INICIAL: `SeedFormFieldsCache` abaixo, chamada por `MainViewModel.OpenPath` — Obs 17
    //      ("cache de campos computado no caller já-async, NUNCA fire-and-forget em construtor"): a
    //      leitura (`HasXfa`+`ReadFormFields`) já acontece no MESMO Task.Run sequencial que já computa
    //      `IsSignedDocument` durante a abertura (fluxo já assíncrono existente), então NÃO precisa de
    //      um 2º mecanismo fire-and-forget-via-Dispatcher só pra esta carga — o resultado já pronto é
    //      só ATRIBUÍDO aqui, síncrono, depois que o VM já existe.
    //   2) Refresh em `Session.Applied`: `RefreshFormFieldsAsync` abaixo, mesmo exemplar de
    //      `RefreshAnnotationsByPageAsync` (fire-and-forget via `_dispatcher.BeginInvoke` dentro de
    //      `OnSessionApplied`) — aqui SIM é o padrão certo, porque `OnSessionApplied` é um handler de
    //      EVENTO síncrono, não um fluxo já-async como `OpenPath`.
    //
    // GATE DE LEITURA MANDATÓRIO (diferente de AnnotationsByPage, onde o gate existe pra não apontar
    // pra uma anotação fantasma): aqui `ApplyFormValues` ESCREVE com base nos NOMES cacheados — um
    // cache obsoleto nunca pode virar um `SetFormFields` "às cegas" contra um documento que já mudou
    // por baixo (outra edição em outro lugar). `_formFieldsCacheSnapshot` só avança quando a leitura
    // TERMINA e ainda corresponde ao `Session.Snapshot` CORRENTE — mesma higiene de obsolescência de
    // `_annotationsCacheSnapshot`.
    private byte[]? _formFieldsCacheSnapshot;

    private bool IsFormFieldsCacheFresh => ReferenceEquals(_formFieldsCacheSnapshot, Session.Snapshot);

    /// `true` quando o AcroForm do documento tem `/XFA` (`IPdfEditor.HasXfa`) — painel mostra o aviso
    /// pt-BR da spec ("Formulário XFA não é suportado; o documento abre para leitura.") e fica vazio
    /// (nunca chama `ReadFormFields`/`SetFormFields` num doc XFA — os 2 lançariam `PdfEditingException`,
    /// contrato pinado no Task 1 fix).
    [ObservableProperty] private bool isXfaForm;

    /// Lista editável — SEMPRE filtrada de `FormFieldType.Other` (botão/campo de assinatura): `SetFormFields`
    /// recusa esses campos com `ArgumentException` (Task 1 fix, nota de política explícita no relatório
    /// dessa task) — a UI nunca pode oferecê-los como "campo preenchível". Cada elemento é um
    /// `FormFieldViewModel` NOVO a cada refresh (nunca reaproveitado por identidade — mesmo espírito de
    /// `AnnotationsByPage` virar uma lista nova, não mutada in-place): qualquer edição pendente/não
    /// aplicada num campo é descartada se ALGUMA OUTRA edição (organizador, outra ferramenta) disparar
    /// um `Session.Applied` enquanto o painel está aberto — mesmo trade-off já aceito para `SelectedAnnotation`.
    [ObservableProperty] private IReadOnlyList<FormFieldViewModel> formFieldEditors = Array.Empty<FormFieldViewModel>();

    /// Documento sem AcroForm (ou só campos `Other`) -> "Este documento não tem formulário." (brief) —
    /// só quando `IsXfaForm` também é falso (XFA tem o PRÓPRIO aviso, mais específico).
    public bool HasFormFields => FormFieldEditors.Count > 0;

    /// Rider da revisão: `CanApplyFormValues` também compõe `HasFormFields` — faltava reavaliar o
    /// `CanExecute` do botão quando a lista vira vazia<->populada (ex.: achatar/undo, ou o próprio
    /// preencher-que-esvazia-nunca-acontece-mas-o-gate-precisa-refletir-o-estado-atual mesmo assim).
    partial void OnFormFieldEditorsChanged(IReadOnlyList<FormFieldViewModel> value)
    {
        OnPropertyChanged(nameof(HasFormFields));
        ApplyFormValuesCommand.NotifyCanExecuteChanged();
        // Task 3 (Plano 3c): FlattenFormCommand também compõe HasFormFields (mesmo motivo/mesmo lugar
        // de ApplyFormValuesCommand acima — achatar/undo pode fazer a lista virar vazia<->populada).
        FlattenFormCommand.NotifyCanExecuteChanged();
    }

    /// Campo atualmente selecionado na lista do painel (`null` = nada selecionado). Dispara o destaque
    /// do widget na página (`UpdateFormFieldHighlightOverlay` abaixo) — exemplar EXATO de
    /// `SelectedAnnotation`/`UpdateAnnotationSelectionOverlay` (Task 7, Plano 3a), só que a fonte
    /// geométrica é `FormFieldData.WidgetRect` em vez de `AnnotationData`.
    [ObservableProperty] private FormFieldViewModel? selectedFormField;

    partial void OnSelectedFormFieldChanged(FormFieldViewModel? oldValue, FormFieldViewModel? newValue) =>
        UpdateFormFieldHighlightOverlay(oldValue, newValue);

    /// Só a página ANTIGA (se houver) e a NOVA (se houver) são tocadas — mesma economia de
    /// `UpdateAnnotationSelectionOverlay`. GATE DE ROTAÇÃO (brief: "gate de rotação só no destaque,
    /// preencher continua livre") — igual a `HitTestAnnotation`, uma LEITURA: usa o cache CORRENTE de
    /// `_pageRotations` (populado junto com `AnnotationsByPage`, ver `RefreshAnnotationsByPageAsync`)
    /// SEM forçar um refresh (`EnsureRotationCacheFreshAsync` é só pros 4 pontos de ESCRITA de
    /// anotação) — bloquear uma seleção de campo por um instante por causa de rotação obsoleta é uma
    /// degradação aceitável e rara, mesmo raciocínio já documentado pra `HitTestAnnotation`.
    private void UpdateFormFieldHighlightOverlay(FormFieldViewModel? oldValue, FormFieldViewModel? newValue)
    {
        if (oldValue is { } o && o.Data.PageIndex >= 0 && o.Data.PageIndex < Pages.Count)
            Pages[o.Data.PageIndex].HasFormFieldHighlight = false;
        if (newValue is not { Data.WidgetRect: { } rect } n) return;
        if (n.Data.PageIndex < 0 || n.Data.PageIndex >= Pages.Count) return;
        if (IsPageRotated(n.Data.PageIndex)) return; // GATE DE ROTAÇÃO — ver doc XML acima
        var page = Pages[n.Data.PageIndex];
        page.FormFieldHighlightRect = PageViewModel.PointRectToScreenRect(
            rect.LeftPt, rect.BottomPt, rect.RightPt, rect.TopPt, Zoom, page.HeightPt);
        page.HasFormFieldHighlight = true;
    }

    /// Clique num campo do painel (brief: "campo selecionado -> ScrollToPage + destaque do widget") —
    /// `ScrollToPageRequested` dispara SEMPRE (mesma convenção do evento, ver doc XML dele: "SEMPRE
    /// dispara, mesmo se for a mesma página"), mesmo sem `WidgetRect` (campo residual sem widget, ver
    /// XML doc de `FormFieldData.WidgetRect`) — só o DESTAQUE depende de ter um retângulo (e da
    /// página não estar girada, ver `UpdateFormFieldHighlightOverlay`); a NAVEGAÇÃO não.
    [RelayCommand]
    private void SelectFormField(FormFieldViewModel? field)
    {
        SelectedFormField = field;
        if (field is not null) ScrollToPageRequested?.Invoke(field.Data.PageIndex);
    }

    // Other-filter (Task 1 fix, nota de política) — extraído porque tem 2 chamadores (SeedFormFieldsCache/
    // RefreshFormFieldsAsync), nenhum dos 2 pode duplicar a regra.
    private static IReadOnlyList<FormFieldViewModel> BuildFormFieldEditors(IReadOnlyList<FormFieldData> raw) =>
        raw.Where(f => f.Type != FormFieldType.Other).Select(f => new FormFieldViewModel(f)).ToArray();

    /// Carga INICIAL do cache (Obs 17 — ver bloco de comentário no topo desta seção): chamada por
    /// `MainViewModel.OpenPath` com o resultado JÁ CALCULADO no fluxo já-async de abertura (mesmo
    /// padrão de `IsSignedDocument`, exceto que aqui o VALOR chega via método em vez de um setter de
    /// `[ObservableProperty]` — precisa TAMBÉM avançar o gate `_formFieldsCacheSnapshot`, que um simples
    /// object-initializer não alcançaria). `internal` — só produção (MainViewModel) e testes de VM
    /// (`InternalsVisibleTo`) chamam isto; testes usam pra simular a carga inicial sem precisar
    /// construir um `MainViewModel` inteiro.
    internal void SeedFormFieldsCache(bool xfa, IReadOnlyList<FormFieldData> rawFields)
    {
        IsXfaForm = xfa;
        // Defensivo (mesmo contrato pinado de RefreshFormFieldsAsync): XFA nunca tem lista editável,
        // mesmo que o chamador (por engano) tenha passado campos crus junto com xfa=true.
        FormFieldEditors = xfa ? Array.Empty<FormFieldViewModel>() : BuildFormFieldEditors(rawFields);
        _formFieldsCacheSnapshot = Session.Snapshot;
    }

    /// Renova o cache a partir do snapshot CORRENTE — chamada a cada `Session.Applied` (despachada via
    /// `_dispatcher` em `OnSessionApplied`, mesmo padrão de `RefreshAnnotationsByPageAsync`), NUNCA no
    /// construtor (Obs 17 — a carga inicial vem de `SeedFormFieldsCache` acima). `HasXfa` é checado
    /// ANTES de `ReadFormFields` (contrato pinado, Task 1 fix: `ReadFormFields` LANÇA em documento XFA)
    /// — documento XFA nunca chama `ReadFormFields`, só marca `IsXfaForm=true` com lista vazia.
    /// `retry`: mesmo mecanismo "un-freeze" de `RefreshAnnotationsByPageAsync` (1 retry após 500ms;
    /// falha total deixa o cache como estava — o PRÓXIMO `Session.Applied` real tenta de novo).
    ///
    /// PRESERVAÇÃO DE EDIÇÃO EM CURSO (Important 1, revisão): um refresh BEM-SUCEDIDO (diferente do
    /// caminho de falha acima) substituía `FormFieldEditors` por instâncias NOVAS/limpas incondicionalmente
    /// — apagava SILENCIOSAMENTE qualquer valor que o usuário já tivesse digitado antes de uma edição
    /// alheia interseccionar (organizador, outra aba), virando `ApplyFormValues` num no-op indistinguível
    /// de "nada mudou" (`changed.Count == 0`). Fix: captura os campos DIRTY do cache ANTIGO por NOME
    /// antes de substituir; um campo que sobrevive editável na leitura NOVA recebe o valor digitado de
    /// volta (ainda dirty contra o `Data.Value` NOVO — se convergir pro mesmo valor, `IsDirty` desliga
    /// sozinho, comportamento correto e automático, não um caso especial); um campo que SUMIU (não está
    /// mais em `byName`, inclusive por ter virado `FormFieldType.Other` — filtrado por
    /// `BuildFormFieldEditors`) ou que virou READONLY não tem como preservar o valor — descarta e avisa
    /// pt-BR NOMEANDO o(s) campo(s) perdido(s) (`NotifyLostDirtyEdits`). A perda é POR CAMPO: os demais
    /// campos dirty que sobreviverem continuam preservados normalmente. O GATE DE LEITURA continua
    /// MANDATÓRIO (inalterado) — esta preservação acontece ATRAVÉS da releitura, nunca no lugar dela.
    internal async Task RefreshFormFieldsAsync(bool retry = true)
    {
        byte[] snapshot = Session.Snapshot;
        bool xfa;
        IReadOnlyList<FormFieldData> read;
        try
        {
            xfa = await Task.Run(() => _editor.HasXfa(snapshot));
            read = xfa ? Array.Empty<FormFieldData>() : await Task.Run(() => _editor.ReadFormFields(snapshot));
        }
        catch (Exception)
        {
            if (retry)
            {
                await Task.Delay(500);
                await RefreshFormFieldsAsync(retry: false);
            }
            return;
        }
        if (!ReferenceEquals(Session.Snapshot, snapshot)) return; // obsoleto — mesma higiene das outras 2 caches

        // Captura ANTES de descartar o cache antigo — só os DIRTY importam (um campo intocado que some
        // não é uma "perda de edição", é só "o documento mudou", sem aviso nenhum — ver teste
        // RefreshFormFieldsAsync_NonDirtyFieldDisappears_NoNotice).
        var dirtyBefore = FormFieldEditors.Where(f => f.IsDirty).ToDictionary(f => f.Name, f => f.EditedValue);

        IsXfaForm = xfa;
        var newEditors = BuildFormFieldEditors(read);
        if (dirtyBefore.Count > 0)
        {
            var byName = newEditors.ToDictionary(f => f.Name);
            List<string>? lost = null;
            foreach (var (name, value) in dirtyBefore)
            {
                if (byName.TryGetValue(name, out var field) && field.IsEditable)
                    field.EditedValue = value; // preserva — IsDirty reavalia sozinho contra o Data.Value NOVO
                else
                    (lost ??= new List<string>()).Add(name); // sumiu, virou Other (filtrado) ou virou readonly
            }
            if (lost is { Count: > 0 }) NotifyLostDirtyEdits(lost);
        }
        FormFieldEditors = newEditors;
        _formFieldsCacheSnapshot = snapshot;
    }

    /// pt-BR (Important 1, revisão) — nomeia TODOS os campos perdidos numa ÚNICA notificação (não N
    /// MessageBoxes sequenciais pra um refresh que perdeu vários de uma vez).
    private void NotifyLostDirtyEdits(IReadOnlyList<string> lostNames)
    {
        string message = lostNames.Count == 1
            ? $"O campo '{lostNames[0]}' mudou com a última edição; o valor digitado foi descartado."
            : $"Os campos {string.Join(", ", lostNames.Select(n => $"'{n}'"))} mudaram com a última edição; os valores digitados foram descartados.";
        _notifyError(message);
    }

    private Task EnsureFormFieldsCacheFreshAsync() =>
        IsFormFieldsCacheFresh ? Task.CompletedTask : RefreshFormFieldsAsync();

    private const string StaleFormFieldsNotice =
        "Os campos do formulário foram atualizados — tente aplicar novamente.";

    // Rodada 2 (mesmo funil de qualquer outro comando mutador deste VM): `!Session.IsEditInFlight`
    // composto aqui também. `HasFormFields` — sem campo nenhum não há o que aplicar. Task 6 (Plano 4):
    // `CanEdit` virou `CanFillForms` — a ÚNICA diferença de comportamento é o caso "assinado com DocMDP
    // permitindo" (`CanFillForms` true ali, `CanEdit` continua false) — todo o resto (documento comum,
    // XFA, assinado-mas-negado) se comporta EXATAMENTE como antes, porque `CanFillForms` reduz a
    // `CanEdit` nesses 3 casos (ver fórmula de `CanFillForms` acima).
    private bool CanApplyFormValues() => CanFillForms && HasFormFields && !Session.IsEditInFlight;

    /// "Aplicar alterações" (brief) — junta só os campos ALTERADOS (`FormFieldViewModel.IsDirty`) num
    /// dicionário nome->valor e passa pelo MESMO funil de qualquer outra escrita deste VM
    /// (`TryBeginEdit` -> `SetFormFields`/`SetFormFieldsIncremental` -> `ApplyEdit`; undo desfaz de
    /// graça via `Session.ApplyEdit`, nenhum mecanismo novo — MESMO num documento assinado: os bytes
    /// incrementados SÃO o que `ApplyEdit` guarda como snapshot atual, e o snapshot PRÉ-preenchimento
    /// (com a assinatura intacta e SEM o valor novo) fica retido na pilha de desfazer, ver doc XML de
    /// `DocumentSession.ApplyEdit` — `ApplyEdit` nunca REESCREVE o PDF, só troca a referência do
    /// snapshot pelos bytes que o CHAMADOR (este método) já produziu). GATE DE LEITURA MANDATÓRIO (ver
    /// bloco de comentário no topo desta seção): `EnsureFormFieldsCacheFreshAsync` RE-LÊ antes de montar
    /// o dicionário; se mesmo assim continuar obsoleto (leitura falhou persistentemente, ou o documento
    /// mudou de novo no meio do caminho), recusa com aviso pt-BR em vez de aplicar valores que já não
    /// correspondem ao campo vivo. Preencher é LIVRE de coordenadas (brief) — nenhuma checagem de
    /// rotação aqui, ao contrário de `ApplyMarkup`/`PlaceAnnotationAtAsync`/etc.: `SetFormFields`/
    /// `SetFormFieldsIncremental` operam por NOME de campo, nunca por posição geométrica.
    ///
    /// ROTEAMENTO (Task 6, Plano 4): `IsSignedDocument` decide o MOTOR — `false` -> caminho normal
    /// (`mPdf.Editing.IPdfEditor.SetFormFields`, inalterado); `true` -> `mPdf.Signing.ISigningEngine.
    /// SetFormFieldsIncremental` (append mode, preserva a assinatura — `CanFillForms`/`CanApplyFormValues`
    /// já garantem que só chega aqui quando `SignedFillPermission == Allowed`, mas o motor tem seu
    /// PRÓPRIO gate de defesa em profundidade também, mesmo espírito de `GuardAgainstSignedDocument`).
    [RelayCommand(CanExecute = nameof(CanApplyFormValues))]
    private async Task ApplyFormValues()
    {
        if (!Session.TryBeginEdit()) return;
        try
        {
            await EnsureFormFieldsCacheFreshAsync();
            if (!IsFormFieldsCacheFresh) { _notifyError(StaleFormFieldsNotice); return; }

            var changed = FormFieldEditors.Where(f => f.IsDirty)
                .ToDictionary(f => f.Name, f => f.EditedValue ?? string.Empty);
            if (changed.Count == 0) return; // nada mudou — no-op silencioso, mesmo espírito de quads.Count==0 em ApplyMarkup

            byte[] pdfAntes = Session.Snapshot;
            byte[] pdfDepois;
            try
            {
                pdfDepois = IsSignedDocument
                    ? await Task.Run(() => _signingEngine.SetFormFieldsIncremental(pdfAntes, changed))
                    : await Task.Run(() => _editor.SetFormFields(pdfAntes, changed));
            }
            catch (PdfSignedDocumentException)
            {
                _notifyError("Este documento está assinado — a edição foi bloqueada para preservar a assinatura. Use \"Editar uma cópia\".");
                return;
            }
            catch (PdfSigningException ex) { _notifyError(ex.Message); return; } // canal de mPdf.Signing (motor incremental)
            catch (PdfEditingException ex) { _notifyError(ex.Message); return; }
            catch (ArgumentException ex) { _notifyError(ex.Message); return; }

            if (TryApplyEdit(pdfDepois))
            {
                // Important 1 (revisão, efeito colateral necessário — ver doc XML de
                // FormFieldViewModel.MarkApplied): os campos que ACABARAM de ser enviados deixam de ser
                // "edição pendente" AQUI, ANTES de qualquer refresh (o próprio Session.Applied que
                // TryApplyEdit já disparou, síncrono, ou um posterior) rodar — sem isto, a preservação
                // de dirty em RefreshFormFieldsAsync confundiria "valor recém-aplicado" (já está no
                // documento) com "edição alheia não-relacionada" e um Undo logo em seguida reveria o
                // valor JÁ desfeito de volta pro editor.
                foreach (var field in FormFieldEditors)
                    if (changed.ContainsKey(field.Name)) field.MarkApplied();
            }
        }
        finally { Session.EndEdit(); }
    }

    // ---- Task 3 (Plano 3c): "Achatar formulário" ---------------------------------------------------

    /// pt-BR (brief) — mensagem EXATA do brief, explica a irreversibilidade em termos de usuário
    /// (Ctrl+Z desfaz ENQUANTO o documento estiver aberto — a mesma garantia que qualquer outra edição
    /// deste app já tem via `Session.ApplyEdit`/`_undoRedo`, nada de novo introduzido por este comando).
    private const string FlattenFormConfirmMessage =
        "Os campos serão convertidos em conteúdo fixo da página. Esta ação pode ser desfeita com Ctrl+Z " +
        "enquanto o documento estiver aberto.";

    /// Mesmo gate de `CanApplyFormValues` acima (`CanEdit && HasFormFields && !Session.IsEditInFlight`)
    /// — `HasFormFields` já implica `!IsXfaForm` POR CONSTRUÇÃO (`SeedFormFieldsCache`/
    /// `RefreshFormFieldsAsync` nunca populam `FormFieldEditors` num documento XFA, ver doc XML de
    /// `HasFormFields`), então não precisa compor `IsXfaForm` de novo aqui — seria redundante com o
    /// próprio `HasFormFields`, não uma checagem adicional de verdade.
    private bool CanFlattenForm() => CanEdit && HasFormFields && !Session.IsEditInFlight;

    /// "Achatar formulário" (brief) — confirma (diálogo INJETÁVEL, `_confirmFlatten`, seam `UiPrompts`)
    /// -> funil (`Session.TryBeginEdit`) -> `IPdfEditor.FlattenForm` -> `Session.ApplyEdit` -> notifica
    /// sucesso em pt-BR. Pós-achatar, `FormFieldEditors` fica vazio via o MESMO refresh de sempre
    /// (`OnSessionApplied` -> `RefreshFormFieldsAsync`, disparado por `TryApplyEdit`/`Session.ApplyEdit`
    /// já rodarem `Applied` — nenhum mecanismo novo) — o painel volta sozinho pro estado "sem
    /// formulário". Undo restaura os campos pela MESMA via (`Session.Undo` -> `Applied` -> refresh).
    ///
    /// ORDEM DELIBERADA (contrato do brief: "confirm-declined -> nothing happens, no funnel arm"): o
    /// diálogo é consultado ANTES de `Session.TryBeginEdit()`, não depois — cancelar nunca arma o pino
    /// compartilhado (nenhum efeito colateral pra outros comandos mutadores, nem uma reavaliação de
    /// `CanExecute` que ninguém pediu). O diálogo em si é SÍNCRONO (`MessageBox.Show` bloqueia;
    /// qualquer fake de teste devolve na hora) — arma-se o funil logo depois, ainda ANTES do 1º `await`
    /// de verdade (`Task.Run(FlattenForm)`), mesmo contrato "sincronamente antes do 1º await" que todo
    /// outro comando mutador deste VM já segue (ver doc XML de `ApplyMarkup`).
    ///
    /// Erros: mesmo par de `catch` de `ApplyFormValues` acima (`PdfSignedDocumentException` — defesa em
    /// profundidade, `CanFlattenForm` já deveria ter barrado via `CanEdit`; `PdfEditingException` — o
    /// canal neutro de qualquer outra falha do iText). Sem `catch (ArgumentException)` dedicado aqui:
    /// `FlattenForm` (ao contrário de `SetFormFields`) não valida nome/tipo/opção de campo nenhum — a
    /// única `ArgumentException` alcançável é a REDE de `TryApplyEdit` (PDF resultante inválido).
    [RelayCommand(CanExecute = nameof(CanFlattenForm))]
    private async Task FlattenForm()
    {
        if (!_confirmFlatten.Confirm(FlattenFormConfirmMessage)) return; // cancelado — funil NUNCA arma
        if (!Session.TryBeginEdit()) return; // outra edição em voo — mesmo funil de qualquer outro comando
        try
        {
            byte[] pdfAntes = Session.Snapshot;
            byte[] pdfDepois;
            try { pdfDepois = await Task.Run(() => _editor.FlattenForm(pdfAntes)); }
            catch (PdfSignedDocumentException)
            {
                _notifyError("Este documento está assinado — a edição foi bloqueada para preservar a assinatura. Use \"Editar uma cópia\".");
                return;
            }
            catch (PdfEditingException ex) { _notifyError(ex.Message); return; }

            if (TryApplyEdit(pdfDepois)) _notifyInfo("Formulário achatado com sucesso.");
        }
        finally { Session.EndEdit(); }
    }

    // ---- Task 4 (Plano 15): Reconhecer texto (OCR) --------------------------------------------------

    /// Mesmo gate dos outros comandos MUTADORES (`CanEdit && !Session.IsEditInFlight`): documento
    /// assinado/XFA -> desabilitado (o usuário usa "Editar uma cópia" na faixa de validação, como em
    /// FlattenForm/anotações); edição concorrente em voo -> desabilitado. `CanEdit`/`IsEditInFlight`
    /// disparam `NotifyCanExecuteChanged` deste comando em `OnIsSignedDocumentChanged`/
    /// `OnIsXfaFormChanged`/`OnSessionEditInFlightChanged`. Habilitado só com documento aberto vem de
    /// graça: o botão bindа `SelectedDocument.RecognizeTextCommand` (null -> desabilitado quando não há
    /// documento).
    private bool CanRecognizeText() => CanEdit && !Session.IsEditInFlight;

    /// "Reconhecer texto (OCR)" (brief) — reconhece as páginas-IMAGEM (sem texto) e grava uma camada de
    /// texto invisível (render mode 3, T3), tornando o PDF pesquisável/copiável. Fluxo:
    ///   Fase 1 (rápida, fora da UI): páginas-alvo = as SEM texto (`PaginaTemTexto`, T2). Nenhuma ->
    ///     "nada a reconhecer", NÃO altera o arquivo (nenhuma faixa de progresso é aberta).
    ///   Fase 2: para cada alvo (1 por vez, liberando o bitmap): rasteriza 300dpi (T2) -> `IOcrEngine.
    ///     Recognize` (T1) -> mapeia `OcrEngineResult`->`OcrTextLayer` (mesmos px). Progresso "página N de
    ///     M" e Cancelar sempre disponíveis (`IOcrProgressSession`). Falha numa página conta e SEGUE (não
    ///     aborta as outras). Cancelar interrompe limpo — nada gravado sem salvar.
    ///   Fim: acumula todos os layers e aplica num ÚNICO passo de edição no funil existente
    ///     (`TryBeginEdit`/`ApplyOcrTextLayer`/`ApplyEdit` -> undo/atômico; gate de assinatura via o mesmo
    ///     `catch (PdfSignedDocumentException)` das outras edições -> aviso "Editar uma cópia").
    /// O `RelayCommand` delega a `RecognizeTextCoreAsync` (internal) pra os testes exercitarem a
    /// orquestração direto, mesmo precedente de `PlaceAnnotationAtAsync` etc.
    [RelayCommand(CanExecute = nameof(CanRecognizeText))]
    private Task RecognizeText() => RecognizeTextCoreAsync();

    internal async Task RecognizeTextCoreAsync()
    {
        if (!Session.TryBeginEdit()) return; // outra edição em voo — mesmo funil de qualquer outro comando
        try
        {
            byte[] pdfAntes = Session.Snapshot;

            // Fase 1: páginas-alvo (as SEM texto) — leitura fora da UI thread, SEM abrir a faixa ainda.
            int[] alvos;
            try { alvos = await Task.Run(() => DetermineOcrTargetPages(pdfAntes)); }
            catch (Exception ex)
            {
                _notifyError($"Não foi possível analisar o documento para OCR: {ex.Message}");
                return;
            }

            if (alvos.Length == 0)
            {
                _notifyInfo("Nada a reconhecer (o documento já tem texto pesquisável).");
                return;
            }

            // Fase 2: render + OCR por página, com progresso e cancelamento.
            OcrRunResult resultado;
            CancellationToken token;
            using (var progresso = _ocrProgress.Start())
            {
                token = progresso.Token;
                var progress = progresso.Progress;
                resultado = await Task.Run(() => RunOcr(pdfAntes, alvos, progress, token));
            }

            if (token.IsCancellationRequested) return; // cancelado — nada gravado sem salvar

            if (resultado.Layers.Count == 0)
            {
                _notifyError(
                    $"O OCR não conseguiu reconhecer texto em nenhuma das {alvos.Length} página(s) analisada(s).");
                return;
            }

            byte[] pdfDepois;
            try { pdfDepois = await Task.Run(() => _editor.ApplyOcrTextLayer(pdfAntes, resultado.Layers)); }
            catch (PdfSignedDocumentException)
            {
                _notifyError("Este documento está assinado — a edição foi bloqueada para preservar a assinatura. Use \"Editar uma cópia\".");
                return;
            }
            catch (PdfEditingException ex) { _notifyError(ex.Message); return; }

            if (TryApplyEdit(pdfDepois))
            {
                string msg = $"Texto reconhecido em {resultado.Layers.Count} página(s).";
                if (resultado.Falhas > 0)
                    msg += $" {resultado.Falhas} página(s) não puderam ser reconhecidas.";
                _notifyInfo(msg);
            }
        }
        finally { Session.EndEdit(); }
    }

    /// Resultado do laço de OCR: os layers reconhecidos (px topo-esquerda, na resolução do bitmap) e a
    /// contagem de páginas que FALHARAM (não abortam as demais — informadas ao fim).
    private readonly record struct OcrRunResult(List<OcrTextLayer> Layers, int Falhas);

    /// Páginas-alvo = as SEM texto extraível (`PaginaTemTexto` falso, T2). Fora da UI thread. Abre e
    /// descarta um rasterizador PRÓPRIO (renderer separado do viewer — seam da T2).
    private int[] DetermineOcrTargetPages(byte[] pdf)
    {
        using var raster = _rasterizerFactory(pdf);
        var alvos = new List<int>();
        for (int i = 0; i < raster.PageCount; i++)
            if (!raster.PaginaTemTexto(i)) alvos.Add(i);
        return alvos.ToArray();
    }

    /// Laço de reconhecimento (fora da UI thread): 1 página por vez, liberando o bitmap antes da próxima.
    /// Reporta "página N de M" ANTES de processar cada página; checa cancelamento no topo (cancelar
    /// interrompe limpo, os layers parciais são DESCARTADOS pelo chamador via `token.IsCancellationRequested`).
    /// Falha numa página incrementa `Falhas` e SEGUE — nunca aborta o documento inteiro.
    private OcrRunResult RunOcr(byte[] pdf, int[] alvos, IProgress<OcrProgress> progress, CancellationToken token)
    {
        var engine = GetOcrEngine();
        var layers = new List<OcrTextLayer>();
        int falhas = 0;
        using var raster = _rasterizerFactory(pdf);
        for (int i = 0; i < alvos.Length; i++)
        {
            if (token.IsCancellationRequested) break;
            progress.Report(new OcrProgress(i + 1, alvos.Length));
            int pageIndex = alvos[i];
            try
            {
                var rendered = raster.RasterizeForOcr(pageIndex);
                var res = engine.Recognize(rendered.Bgra, rendered.WidthPx, rendered.HeightPx, OcrLanguages);
                var boxes = new List<OcrTextBox>();
                foreach (var w in res.Words)
                    if (!string.IsNullOrWhiteSpace(w.Text))
                        boxes.Add(new OcrTextBox(w.Text, w.LeftPx, w.TopPx, w.WidthPx, w.HeightPx));
                if (boxes.Count > 0)
                    layers.Add(new OcrTextLayer(pageIndex, rendered.WidthPx, rendered.HeightPx, boxes));
            }
            catch (Exception) { falhas++; } // uma página que falha não aborta as outras (brief)
        }
        return new OcrRunResult(layers, falhas);
    }

    /// Resolve o motor de OCR: o INJETADO (se houver) ou um `TesseractOcrEngine` criado sob demanda no 1º
    /// uso (marcado `_ownsOcrEngine` pra descarte em `Dispose`). Chamado dentro do `Task.Run` do OCR
    /// (fora da UI thread — a carga de nativos/tessdata é lenta). Serializado por construção: o funil
    /// (`TryBeginEdit`) impede dois OCRs concorrentes no mesmo documento.
    private IOcrEngine GetOcrEngine()
    {
        if (_ocrEngine is null)
        {
            _ocrEngine = new TesseractOcrEngine();
            _ownsOcrEngine = true;
        }
        return _ocrEngine;
    }

    // ---- Task 3 (Plano 4): Assinar ------------------------------------------------------------------

    private const string ConfirmSaveBeforeSignMessage =
        "O documento tem alterações não salvas. Salvar antes de assinar?";
    private const string SignedDocumentNotice =
        "Documento assinado e salvo. O histórico de desfazer foi limpo.";
    /// Revisão do coordenador (achado real, não hipotético) — "compound failure message": quando o
    /// salvamento FORÇADO pré-assinatura (doc sujo -> `Session.Save`) já aconteceu e o motor falha
    /// DEPOIS, o usuário via só o erro do motor — sem saber que o arquivo em disco JÁ foi sobrescrito
    /// (sem assinatura nenhuma). Anexado à mensagem de erro do motor SÓ quando `didForcedSave` é `true`
    /// (ver `ComposeSignFailureMessage`) — o caminho "doc já estava limpo" nunca precisa deste aviso.
    private const string SavedButNotSignedSuffix = " O documento foi salvo, mas NÃO está assinado.";
    /// BELT (revisão do coordenador, achado real: "placement-window mutation gap") — entre o OK do
    /// diálogo "Assinar" e o Confirmar da caixa ajustável, o funil NÃO está armado (só arma no
    /// Confirmar, ver `ConfirmSignatureStampAsync`, Task 2/Plano 8 — cobre a janela INTEIRA de
    /// Desenhar+Ajustar, não só o instante do clique como no fluxo antigo) — TODO mutador deste VM
    /// (FlattenForm/ApplyFormValues/ApplyMarkup/Undo/Redo/anotações) continua HABILITADO nessa janela.
    /// Uma edição ali entraria SILENCIOSAMENTE no documento que está prestes a virar "certificado" pela
    /// assinatura, sem nenhuma re-confirmação do usuário — ver `SignCoreAsync` (o cinto em si) e
    /// `ComposeSignFailureMessage`.
    private const string DocumentChangedDuringPlacementNotice =
        "O documento foi alterado durante o posicionamento do carimbo. A assinatura foi cancelada — assine novamente.";

    private static string ComposeSignFailureMessage(string engineMessage, bool didForcedSave) =>
        didForcedSave ? engineMessage + SavedButNotSignedSuffix : engineMessage;

    /// Estado retido ENQUANTO `ActiveTool == AnnotationTool.SignatureStamp` (mesmo espírito de
    /// `_pendingStampBytes`, Task 9/Plano 3a, só que carregando 3 coisas em vez de 1 — motivo: o BELT de
    /// `SignCoreAsync` e a mensagem composta precisam do mesmo contexto do momento em que o diálogo
    /// retornou, não só do resultado escolhido). `SnapshotAtDialogOk`: `Session.Snapshot` capturado
    /// logo que o diálogo devolveu `!= null` — comparado por REFERÊNCIA contra `Session.Snapshot`
    /// corrente dentro de `SignCoreAsync`; qualquer mutação (mesmo Undo/Redo pra um conteúdo IDÊNTICO)
    /// sempre produz uma referência NOVA via `Apply`, então a comparação nunca falha por engano.
    /// `DidForcedSave`: `true` quando `Sign()` salvou o documento sujo ANTES de mostrar o diálogo — vive
    /// aqui (não só numa variável local de `Sign()`) porque o caminho COM carimbo só alcança
    /// `SignCoreAsync` bem depois, no Confirmar da caixa ajustável (Task 2, Plano 8).
    private sealed record PendingSignPlacement(SignDialogResult Result, byte[] SnapshotAtDialogOk, bool DidForcedSave);
    private PendingSignPlacement? _pendingSignPlacement;

    /// Habilitado com qualquer documento aberto, desde que não seja XFA (o motor de assinatura passa
    /// por `SignatureUtil`/`PdfAcroForm` internamente — mesma falha documentada em `HasXfa`/`CanEdit`
    /// pra doc XFA malformado), nenhuma edição concorrente esteja em voo, e o modo de COLOCAÇÃO do
    /// carimbo não esteja ativo (evita reabrir o diálogo "Assinar" por cima de uma colocação já em
    /// andamento — clique duplo acidental no botão da toolbar). DELIBERADAMENTE NÃO compõe
    /// `CanEdit`/`!IsSignedDocument` (diferente de FlattenForm/ApplyFormValues/anotações): assinar um
    /// documento JÁ assinado é o caso de USO CENTRAL de assinatura incremental (2ª, 3ª assinatura) — o
    /// brief é explícito: "a SIGNED doc CAN be signed again — incremental! CanExecute must NOT gate on
    /// !IsSignedDocument".
    private bool CanSign() => !IsXfaForm && !Session.IsEditInFlight && ActiveTool != AnnotationTool.SignatureStamp;

    /// Reavalia `SignCommand.CanExecute` quando `ActiveTool` muda — cobre especificamente a entrada/
    /// saída do modo de colocação do carimbo de assinatura (`CanSign` acima compõe `ActiveTool`). As
    /// outras ferramentas (StickyNote/ImageStamp/etc.) não precisam de um handler simétrico: nenhuma
    /// delas aparece na composição de `CanX` de comando nenhum deste VM, só `SignCommand`.
    partial void OnActiveToolChanged(AnnotationTool value)
    {
        SignCommand.NotifyCanExecuteChanged();
        // Task 1 (Plano 8): "cancelamentos ... troca de ferramenta" — trocar PRA QUALQUER outra
        // ferramenta (incl. None) enquanto uma caixa está em Drawing/Adjusting cancela a caixa (mesmo
        // contrato de CancelStampBox chamado por Esc/botão — reseta TUDO, sem armar funil). Guard
        // `value != SignatureStamp`: ativar/reativar a PRÓPRIA ferramenta (Sign() faz isso hoje) nunca
        // deve se auto-cancelar.
        if (value != AnnotationTool.SignatureStamp && StampPlacementPhase != StampPlacementPhase.None)
            CancelStampBox();
    }

    /// "Assinar" (Task 3, Plano 4): documento TEMP-BACKED (`NeedsSaveAs`, Task 2 Plano 7) -> relocaliza
    /// PRIMEIRO (ver `TryRelocateBeforeSign` abaixo — fix CRÍTICO pós-revisão) -> doc sujo -> confirma
    /// salvar ANTES (recusa aborta o fluxo inteiro, SEM armar o funil — mesma ordem de `FlattenForm`:
    /// diálogo síncrono ANTES de `TryBeginEdit`) -> diálogo de assinatura (certificado/motivo/local/
    /// DocMDP/carimbo) -> se o usuário escolheu carimbo visível, entra em modo de COLOCAÇÃO na página
    /// (espelha `ToggleStampTool`/`PlaceStampAtAsync`) e devolve — o commit de verdade só acontece no
    /// CONFIRMAR da caixa ajustável (`ConfirmSignatureStampAsync`, Task 2/Plano 8), depois de
    /// desenhar+ajustar o retângulo (`BeginStampBoxPlacementAsync`/`UpdateDrawTo`/`EndStampDraw`/
    /// `MoveBoxBy`/`ResizeBoxByHandle`, Task 1/Plano 8); sem carimbo, assina direto aqui mesmo. Assinar NUNCA
    /// passa por `ApplyEdit`/`_editor` (append mode, sempre resolvido dentro de `mPdf.Signing`) — o
    /// funil (`Session.TryBeginEdit`) é armado só DEPOIS do diálogo, imediatamente antes de
    /// `SignCoreAsync` (mesmo contrato "sincronamente antes do 1º await de verdade" de todo outro
    /// comando mutador deste VM).
    [RelayCommand(CanExecute = nameof(CanSign))]
    private async Task Sign()
    {
        // CRÍTICO (fix pós-revisão, achado end-to-end confirmado): um documento TEMP-BACKED (aberto de
        // uma imagem convertida — `MainViewModel.OpenImageAsNewDocument`) abre LIMPO (`IsDirty=false`,
        // o temp já bate com o snapshot) — o dirty-check/forced-save logo abaixo NUNCA dispararia pra
        // ele, então SEM esta linha `SignCoreAsync`/`Session.CommitSigned` gravaria a assinatura de
        // volta no MESMO arquivo em `%TEMP%\mPDF\open-<guid>\`, `MarkSaved` limparia `IsDirty` de novo
        // (documento CONTINUA "limpo" do ponto de vista de Save/fechar aba), e a assinatura — um
        // documento LEGAL, não um rascunho — desapareceria em silêncio na próxima limpeza do SO. Roda
        // ANTES de QUALQUER outra coisa neste método (antes até do dirty-check/forced-save PRÉ-existente
        // e do diálogo de assinatura) — mesma disciplina "diálogo síncrono ANTES de qualquer funil" que
        // o resto do método já segue. Recusa (diálogo de "Salvar como" cancelado) aborta o fluxo INTEIRO
        // aqui mesmo, SEM tocar `_confirmSaveBeforeSign`/o diálogo de assinatura/o motor — mesmo
        // contrato de "recusa aborta sem armar o funil" que a linha seguinte já tinha pro dirty-check.
        if (NeedsSaveAs && !TryRelocateBeforeSign()) return;

        bool didForcedSave = false;
        if (Session.IsDirty)
        {
            if (!_confirmSaveBeforeSign.Confirm(ConfirmSaveBeforeSignMessage)) return; // recusado -> funil NUNCA arma
            // INVARIANTE (Task 2, Plano 5 — MainViewModel.SaveCommand virou assíncrono, TryBeginEdit
            // ANTES do 1º await, ver doc XML lá): este Session.Save é SÍNCRONO e roda ANTES de
            // Session.TryBeginEdit ser armado (o funil de Sign só arma bem mais abaixo, imediatamente
            // antes de SignCoreAsync — ver doc XML da assinatura do método). Como a UI thread é única e
            // este trecho não tem NENHUM `await`, nada mais pode rodar concorrentemente enquanto ele
            // executa — um SaveCommand.ExecuteAsync concorrente só alcançaria este `Sign()` ignorando
            // CanExecute/CanSign de propósito (CanSign já compõe !Session.IsEditInFlight — a UI de
            // produção nunca oferece essa janela). Qualquer refactor que torne ESTE save assíncrono, ou
            // que adicione um caminho de entrada pra Sign() que não passe por SignCommand (logo, que não
            // passe por CanSign), precisa RE-VERIFICAR esta invariante — ela é o que hoje garante que
            // este Save forçado nunca corre em paralelo com um Save/edição armados pelo funil.
            try { Session.Save(_config); didForcedSave = true; }
            catch (IOException ex) { _notifyError(ex.Message); return; }
        }

        // 1ª assinatura (nenhuma ainda) -> DocMDP disponível; 2ª+ -> motor RECUSA CertificationLevel !=
        // None num doc já assinado (ArgumentException), então o diálogo nem oferece o checkbox.
        bool hasSignatures;
        try { hasSignatures = await Task.Run(() => _editor.HasSignatures(Session.Snapshot)); }
        catch (PdfEditingException ex) { _notifyError(ex.Message); return; }

        var certificates = _listSigningCertificates();
        var result = _signDialog.PromptForSignature(certificates, allowDocMdp: !hasSignatures);
        if (result is null) return; // cancelado

        // Capturado NO INSTANTE em que o diálogo devolveu ("dialog-OK", revisão do coordenador) — o
        // BELT de SignCoreAsync compara esta referência contra Session.Snapshot corrente.
        byte[] snapshotAtDialogOk = Session.Snapshot;

        if (result.PlaceStamp)
        {
            // Modo de colocação (exemplar: ToggleStampTool/PlaceStampAtAsync) — o commit de verdade
            // acontece no Confirmar da caixa ajustável (ConfirmSignatureStampAsync, Task 2/Plano 8).
            // NADA de motor/funil ainda aqui.
            _pendingSignPlacement = new PendingSignPlacement(result, snapshotAtDialogOk, didForcedSave);
            ActiveTool = AnnotationTool.SignatureStamp;
            return;
        }

        if (!Session.TryBeginEdit()) return; // outra edição em voo — mesmo funil de qualquer outro comando
        try { await SignCoreAsync(result, stamp: null, snapshotAtDialogOk, didForcedSave); }
        finally { Session.EndEdit(); }
    }

    /// Relocaliza um documento TEMP-BACKED (`NeedsSaveAs`) ANTES de assinar — ver doc XML de `Sign`
    /// acima (fix CRÍTICO pós-revisão). MESMA FORMA de `MainViewModel.TrySaveAsSync` (diálogo síncrono
    /// -> `Session.SaveAs` -> zera a flag), implementada AQUI (não delegada a `MainViewModel`) porque
    /// `Sign` já vive neste VM e `_dialogs`/`Session` já são acessíveis — não precisa de uma 2ª
    /// dependência cruzando VMs só pra este caminho. DIFERENÇA aceita (registrada no relatório): não
    /// adiciona `path` a `RecentFilesStore` — esse serviço só existe em `MainViewModel` (compartilhado
    /// entre abas), `DocumentViewModel` nunca teve acesso a ele; a lista de recentes é UX, não uma
    /// garantia de correção (a garantia de correção É o documento nunca ficar preso no temp — essa
    /// continua valendo sem recentes). Diálogo CANCELADO -> `false`, mesma semântica de "recusa aborta
    /// sem notificar erro" que o dirty-check logo acima em `Sign` já usa (cancelar não é uma falha).
    private bool TryRelocateBeforeSign()
    {
        if (_dialogs.PickPdfToSaveAs(Session.FilePath) is not { } path) return false;
        try
        {
            Session.SaveAs(path);
            NeedsSaveAs = false;
            return true;
        }
        catch (Exception ex) { _notifyError(ex.Message); return false; }
    }

    /// Task 2 (Plano 8): gatilho REAL do arrasto-para-desenhar da caixa ajustável — chamado pela View no
    /// MOUSE-DOWN da página (`PdfViewerControl.Page_MouseLeftButtonDown`) quando `ActiveTool ==
    /// AnnotationTool.SignatureStamp` e a máquina ainda não está em Adjusting. Substitui
    /// `PlaceSignatureStampAtAsync` (clique único, Task 3/Plano 4 — deletado nesta task) como o ponto de
    /// entrada de produção: mesma guarda `ActiveTool != SignatureStamp -> no-op`, mesmo GATE DE ROTAÇÃO
    /// (`EnsureRotationCacheFreshAsync`/`IsPageRotated`, mesmo aviso pt-BR, ferramenta continua ativa —
    /// só que agora recusado ANTES de sequer entrar em Drawing, não mais no momento do commit). O CN do
    /// certificado (prévia "fiel o suficiente" do carimbo, ver `StampBoxCertificateCn`) é resolvido AQUI
    /// a partir do certificado ESCOLHIDO no diálogo "Assinar" (`_pendingSignPlacement.Result.Certificate`),
    /// via `X509Certificate2.GetNameInfo(SimpleName, forIssuer: false)` — mesmo extrator já usado por
    /// `CertificateCatalog`/`PadesSigningEngine`, sem depender de nenhum parsing novo — a View não
    /// precisa saber nada sobre certificados. O commit de verdade (motor/funil) só acontece no
    /// CONFIRMAR — ver `ConfirmSignatureStampAsync` abaixo.
    public async Task BeginStampBoxPlacementAsync(int pageIndex, PdfPoint startPt)
    {
        if (ActiveTool != AnnotationTool.SignatureStamp) return;
        if (_pendingSignPlacement is not { } pending) return;
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;

        // COSTURA DE ROTAÇÃO (exemplar: PlaceSignatureStampAtAsync original) — refresca o cache ANTES
        // de confiar em IsPageRotated; no-op com aviso, ferramenta continua ativa (usuário tenta outra
        // página). RefreshAnnotationsByPageAsync já engole qualquer exceção internamente (retry + desiste
        // — ver doc XML lá), então este `await` nunca lança.
        await EnsureRotationCacheFreshAsync();
        if (IsPageRotated(pageIndex)) { _notifyError(RotatedPageNotice); return; }

        string cn = pending.Result.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        BeginStampBoxPlacement(pageIndex, startPt, cn);
    }

    /// Task 2 (Plano 8): gatilho REAL do botão "✔ Assinar aqui" (`PdfViewerControl.StampBoxConfirm_Click`)
    /// — troca `PlaceSignatureStampAtAsync` (clique único, deletado nesta task) como o ponto onde o
    /// motor de assinatura de fato dispara. `ConfirmStampBox()` (Task 1) devolve o rect FINAL — já
    /// ajustado (mover/redimensionar) pelo usuário, em pontos de página, mesma convenção de
    /// `VisibleStampSpec`/`PdfQuad` — sem conversão nenhuma (não há matemática nova aqui, conforme o
    /// brief). O CINTO (`ReferenceEquals(snapshotAtDialogOk, Session.Snapshot)`, dentro de
    /// `SignCoreAsync`) agora é checado NO CONFIRMAR — cobre a janela INTEIRA de Desenhar+Ajustar, não
    /// só entre o diálogo e o clique como antes. Mesmo funil/try-catch/finally de
    /// `PlaceSignatureStampAtAsync` original (ver doc XML I2 removido daqui — mesma disciplina: exceção
    /// não antecipada não pode escapar como Task não observada, já que este método também é invocado
    /// fire-and-forget pela View).
    public async Task ConfirmSignatureStampAsync()
    {
        if (_pendingSignPlacement is not { } pending) return;
        if (ConfirmStampBox() is not { } placement) return; // fora de Adjusting -- nada a confirmar

        if (!Session.TryBeginEdit()) return; // mesmo funil sem-comando de PlaceSignatureStampAtAsync original
        try
        {
            var stamp = new VisibleStampSpec(placement.PageIndex, placement.Rect);
            bool committed = await SignCoreAsync(pending.Result, stamp, pending.SnapshotAtDialogOk, pending.DidForcedSave);
            if (committed)
            {
                ActiveTool = AnnotationTool.None;
                _pendingSignPlacement = null;
            }
        }
        catch (Exception ex)
        {
            _notifyError(ComposeSignFailureMessage(ex.Message, pending.DidForcedSave));
        }
        finally { Session.EndEdit(); }
    }

    /// Assume o funil JÁ armado pelo chamador (`Sign` ou `ConfirmSignatureStampAsync`) — roda o motor
    /// (`ISigningEngine.Sign`) em `Task.Run` e comita via `Session.CommitSigned` (NUNCA `ApplyEdit` —
    /// append mode). Devolve `true` em sucesso; o chamador decide o que fazer com `ActiveTool`/
    /// `_pendingSignPlacement` a partir disso (o caminho "sem carimbo" de `Sign` nem usa o retorno).
    ///
    /// BELT (revisão do coordenador — ver doc XML de `DocumentChangedDuringPlacementNotice`): primeira
    /// coisa que este método faz, ANTES de tocar o motor. `snapshotAtDialogOk` é o que `Sign()` capturou
    /// quando o diálogo devolveu; se `Session.Snapshot` mudou de REFERÊNCIA desde então, alguma edição
    /// aconteceu na janela sem funil (entre o OK e o Confirmar da caixa ajustável, Task 2/Plano 8 — cobre
    /// Desenhar+Ajustar inteiros) — aborta, notifica em pt-BR, e força o RESET completo do modo de
    /// colocação (`ActiveTool`/`_pendingSignPlacement`): o `PendingSignPlacement` escolhido (cert/motivo/
    /// local/DocMDP) foi decidido contra o snapshot ANTIGO, continuar tentando desenhar OUTRA caixa com
    /// ele agora seria assinar por cima de um documento diferente do que o usuário viu no diálogo. NA
    /// PRÁTICA (Task 2): `OnSessionApplied`/`CancelStampBox(dueToDocumentMutation: true)` já reseta
    /// `StampPlacementPhase` — E JÁ NOTIFICA (fix pós-revisão do coordenador, mesma mensagem
    /// `DocumentChangedDuringPlacementNotice` deste belt) — ANTES desta checagem alcançar o caminho COM
    /// carimbo (single-threaded, `Session.Applied` fires síncrono dentro do próprio
    /// `Apply`/`ApplyEdit`/`CommitSigned`) — `ConfirmStampBox()` já devolve `null` nesse caso, então
    /// `ConfirmSignatureStampAsync` nem chega a chamar este método. Este checagem continua aqui como
    /// REDE DE SEGURANÇA estrutural (mesmo espírito do parágrafo seguinte, sobre o caminho sem carimbo).
    ///
    /// POLÍTICA (verificada, não hipotética): o caminho SEM carimbo (`Sign()`, `stamp: null`) chama este
    /// método SINCRONAMENTE logo após capturar `snapshotAtDialogOk` — sem NENHUM `await` de verdade no
    /// meio (o `Task.Run` do motor, abaixo, É o 1º `await`; a thread de UI nunca cede controle entre a
    /// captura e esta checagem). Não existe janela nenhuma nesse caminho: a checagem abaixo passa
    /// SEMPRE (mesma referência) — não é código morto, é a mesma garantia estrutural que protege o
    /// caminho COM carimbo, só que trivialmente satisfeita aqui; continua correta se `Sign()` algum dia
    /// ganhar um `await` genuíno antes desta chamada.
    private async Task<bool> SignCoreAsync(SignDialogResult result, VisibleStampSpec? stamp, byte[] snapshotAtDialogOk, bool didForcedSave)
    {
        if (!ReferenceEquals(snapshotAtDialogOk, Session.Snapshot))
        {
            _notifyError(DocumentChangedDuringPlacementNotice);
            ActiveTool = AnnotationTool.None;
            _pendingSignPlacement = null;
            return false;
        }

        var request = new SignRequest(
            Session.Snapshot, result.Certificate, result.Reason, result.Location, stamp,
            result.ApplyDocMdp ? DocMdpLevel.FormsAndSignatures : null);

        byte[] signed;
        try { signed = await Task.Run(() => _signingEngine.Sign(request)); }
        catch (PdfSigningException ex) { _notifyError(ComposeSignFailureMessage(ex.Message, didForcedSave)); return false; }
        catch (ArgumentException ex) { _notifyError(ComposeSignFailureMessage(ex.Message, didForcedSave)); return false; }

        // I2 (revisão final, achado do revisor): `Session.CommitSigned` grava em disco de verdade
        // (`AtomicWrite` no `FilePath` da sessão) — DIFERENTE de qualquer outro mutador deste VM
        // (`ApplyEdit`/`FlattenForm`/etc. só mutam `Snapshot` em memória, gravação fica pra `Save`
        // manual). Uma falha de I/O aqui (arquivo travado por outro processo, disco cheio) é real e
        // ATÉ AGORA escapava SEM catch nenhum: no caminho SEM carimbo, virava uma exceção não tratada
        // subindo pelo `AsyncRelayCommand`; no caminho COM carimbo (`ConfirmSignatureStampAsync`, Task 2/
        // Plano 8, invocado fire-and-forget via `_ = doc.ConfirmSignatureStampAsync()` em
        // `PdfViewerControl.StampBoxConfirm_Click`), virava uma Task NÃO OBSERVADA —
        // `TaskScheduler.UnobservedTaskException` (App.xaml.cs) só REGISTRA em `CrashLog`, nunca mostra
        // nada ao usuário: silêncio TOTAL do ponto de vista de quem assinou. Mesma disciplina de
        // `ComposeSignFailureMessage`/`didForcedSave` que os 2 catches acima já usam — se o salvamento
        // forçado pré-assinatura já aconteceu, o usuário PRECISA saber que o arquivo em disco foi
        // sobrescrito sem a assinatura.
        try { Session.CommitSigned(signed); }
        catch (IOException ex) { _notifyError(ComposeSignFailureMessage(ex.Message, didForcedSave)); return false; }

        IsSignedDocument = true; // acabou de assinar -- reflete de imediato (banner/CanEdit reagem via OnIsSignedDocumentChanged)
        // Task 6 (Plano 4): SignedFillPermission também precisa refletir de imediato — CanFillForms/o
        // painel de Campos usam esse valor pra decidir se o preenchimento continua liberado logo após
        // assinar. Recalculado de VERDADE via o motor (não hardcoded como "sempre Allowed" — mesmo
        // que nenhuma assinatura que ESTE app produza hoje resulte em outra coisa, P2/aprovação, nunca
        // P1/P3 — o oráculo continua sendo o motor, não uma suposição sobre o que ele deveria produzir).
        try { SignedFillPermission = await Task.Run(() => _signingEngine.CanFillIncremental(signed)); }
        catch (PdfSigningException) { /* leitura auxiliar best-effort — não desfaz a assinatura já commitada */ }
        _notifyInfo(SignedDocumentNotice);
        return true;
    }

    // ==== Task 1/2 (Plano 8): caixa ajustável do carimbo de assinatura ===============================
    //
    // Máquina de estados construída pela Task 1 (headless, testável sem UI) e conectada ao fluxo de
    // assinar de verdade pela Task 2: `Sign()` continua ativando `ActiveTool = SignatureStamp` (Task 3,
    // Plano 4) e devolvendo; a View (`PdfViewerControl.Page_MouseLeftButtonDown`) agora inicia um
    // ARRASTO no mouse-down (`BeginStampBoxPlacementAsync`/`UpdateDrawTo`/`EndStampDraw`), e o botão
    // "✔ Assinar aqui" chama `ConfirmSignatureStampAsync` (que usa `ConfirmStampBox` abaixo pra obter o
    // rect final e dispara o motor de assinatura de verdade — ver doc XML de `ConfirmSignatureStampAsync`
    // acima). Os métodos desta seção continuam public/testáveis direto (headless), como a Task 1 deixou.
    //
    // REPRESENTAÇÃO DO RETÂNGULO (a parte não-óbvia): 4 escalares BRUTOS e NÃO-ORDENADOS, não um
    // left/bottom/right/top já normalizado — `_boxXa`/`_boxXb` (eixo X) e `_boxYa`/`_boxYb` (eixo Y).
    // `StampBoxRect` (público, ObservableProperty) é SEMPRE `min/max` desses 4 valores — nunca escrito
    // direto, só via `RecomputeStampBoxRect()`. Por quê brutos: durante Drawing, `_boxXa/_boxYa` é a
    // ÂNCORA (ponto do mouse-down, fixo) e `_boxXb/_boxYb` é o ponto CORRENTE do arrasto — um arrasto
    // pra CIMA-ESQUERDA deixa a âncora MAIOR que o corrente (min/max resolve a normalização de graça,
    // mesmo truque de `new Rect(anchorPx, currentPx)` da ferramenta Retângulo, Task 8/Plano 3a). Ao
    // entrar em Adjusting (`EndStampDraw`, quando o retângulo já é válido), os 4 brutos são
    // CANONICALIZADOS (Xa=Left, Xb=Right, Ya=Bottom, Yb=Top) — a partir daí, cada ALÇA sempre move o
    // MESMO escalar bruto pelo resto da fase Adjusting (mapeamento fixo, ver `ResizeBoxByHandle`),
    // mesmo depois de um cruzamento inverter qual É o Left/Right visualmente: é essa PERMANÊNCIA do
    // mapeamento (nunca re-derivada a partir do retângulo normalizado corrente, que já perdeu a
    // informação de qual bruto é qual) que faz a alça continuar "grudada" no mesmo canto físico do
    // mouse do usuário através de uma inversão, em vez de saltar pro canto errado no frame seguinte.

    private double _boxXa, _boxXb, _boxYa, _boxYb;
    /// Última página pra qual `RefreshStampBoxOverlay` empurrou HasStampBox=true — usada só pra saber
    /// QUAL PageViewModel limpar quando a fase volta a None (ou a página muda, hipoteticamente). Mesmo
    /// papel de `oldValue` em `UpdateFormFieldHighlightOverlay`, só que guardado como campo (esta
    /// máquina não recebe old/new de um SetProperty — os OnXChanged aqui não carregam o valor antigo
    /// preciso que precisaríamos, um índice de página).
    private int _stampBoxOverlayPageIndex = -1;

    [ObservableProperty] private StampPlacementPhase stampPlacementPhase = StampPlacementPhase.None;
    [ObservableProperty] private PdfQuad stampBoxRect;
    /// Aviso sutil de barra de status (brief: "soltar com rect < mínimo -> permanece Drawing, NÃO
    /// cancela") — `null`/vazio quando não há nada a avisar. `HasStampBoxNotice` (abaixo) é derivado
    /// automaticamente no OnChanged, mesmo padrão de bool+texto já usado por IsOpening/IsSaving na
    /// StatusBar de MainWindow.xaml (ver doc XML de lá).
    [ObservableProperty] private string? stampBoxNotice;
    [ObservableProperty] private bool hasStampBoxNotice;

    partial void OnStampBoxNoticeChanged(string? value) => HasStampBoxNotice = !string.IsNullOrEmpty(value);
    partial void OnStampPlacementPhaseChanged(StampPlacementPhase value) => RefreshStampBoxOverlay();
    partial void OnStampBoxRectChanged(PdfQuad value) => RefreshStampBoxOverlay();

    /// Página dona da caixa corrente (-1 = nenhuma). `private set`: só os métodos desta seção mudam isto
    /// — não é `[ObservableProperty]` de propósito (nada no XAML faz bind direto nele; só
    /// `PageViewModel.ApplyZoom`, no mesmo assembly, lê pra saber se É a página dona antes de
    /// reconverter — mesmo padrão de leitura direta que `SelectedFormField`/`SelectedSignature` já
    /// expõem pro mesmo consumidor).
    public int StampBoxPageIndex { get; private set; } = -1;
    /// CN do certificado escolhido no diálogo "Assinar" (Task 2 vai passar `result.Certificate` — ver
    /// `SignDialogResult.Certificate` — convertido via `X509Certificate2.GetNameInfo(SimpleName,
    /// forIssuer: false)`, mesmo extrator já usado por `CertificateCatalog`/`PadesSigningEngine`, sem
    /// depender de nenhum parsing novo). Só texto de PRÉVIA (brief: "fiel o suficiente, não idêntico ao
    /// appearance do motor") — o motor decide o texto final de verdade.
    public string? StampBoxCertificateCn { get; private set; }
    public string? StampBoxDateLabel { get; private set; }

    /// Chamado no mouse-down da página (via `BeginStampBoxPlacementAsync`, Task 2/Plano 8 — que resolve
    /// o CN do certificado e aplica o gate de rotação antes de chegar aqui). Mesmo guard do clique único
    /// original (`ActiveTool != SignatureStamp -> no-op`) — evita a máquina rodar fora do modo de
    /// colocação de assinatura. `startPt`: ponto de PÁGINA do mouse-down (mesma
    /// convenção pt de página de todo o resto do arquivo — conversão screen->page é responsabilidade da
    /// View, via `TextSelection.ScreenToPagePoint`, nunca duplicada aqui). Retângulo inicial é
    /// DEGENERADO (largura/altura zero, os 4 brutos = o mesmo ponto) — só vira algo visível no primeiro
    /// `UpdateDrawTo`.
    public void BeginStampBoxPlacement(int pageIndex, PdfPoint startPt, string certificateCn)
    {
        if (ActiveTool != AnnotationTool.SignatureStamp) return;
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;

        var page = Pages[pageIndex];
        double x = Math.Clamp(startPt.XPt, 0, page.WidthPt);
        double y = Math.Clamp(startPt.YPt, 0, page.HeightPt);

        StampBoxPageIndex = pageIndex;
        StampBoxCertificateCn = certificateCn;
        StampBoxDateLabel = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
        _boxXa = _boxXb = x;
        _boxYa = _boxYb = y;
        StampBoxNotice = null;
        RecomputeStampBoxRect();
        StampPlacementPhase = StampPlacementPhase.Drawing;
    }

    /// Atualiza o retângulo em curso durante Drawing pro ponto de página atual do arrasto (mouse-move) —
    /// exemplar: `new Rect(anchorPx, currentPx)` da ferramenta Retângulo (Task 8, Plano 3a) — só que em
    /// pontos de página (não px de tela transiente) porque este overlay PRECISA sobreviver a
    /// zoom/reciclagem (ver doc XML da seção "caixa ajustável" acima). Clampa o ponto corrente à página
    /// (nunca deixa a âncora — o lado que o usuário já soltou — sair da página; só o lado que ele ainda
    /// está arrastando).
    public void UpdateDrawTo(PdfPoint currentPt)
    {
        if (StampPlacementPhase != StampPlacementPhase.Drawing) return;
        if (StampBoxPageIndex < 0 || StampBoxPageIndex >= Pages.Count) return;

        var page = Pages[StampBoxPageIndex];
        _boxXb = Math.Clamp(currentPt.XPt, 0, page.WidthPt);
        _boxYb = Math.Clamp(currentPt.YPt, 0, page.HeightPt);
        RecomputeStampBoxRect();
    }

    /// Solta o arrasto inicial (mouse-up durante Drawing). Retângulo abaixo do mínimo (brief: 60x20pt)
    /// -> NÃO cancela, fica em Drawing com um aviso sutil (o usuário pode continuar arrastando a partir
    /// de onde parou — próximos `UpdateDrawTo` continuam funcionando normalmente); retângulo válido ->
    /// CANONICALIZA os 4 brutos (ver `CanonicalizeRawScalars`/doc XML da seção acima) e avança pra
    /// Adjusting.
    public void EndStampDraw()
    {
        if (StampPlacementPhase != StampPlacementPhase.Drawing) return;

        double width = Math.Abs(_boxXb - _boxXa);
        double height = Math.Abs(_boxYb - _boxYa);
        if (width < MinStampBoxWidthPt || height < MinStampBoxHeightPt)
        {
            StampBoxNotice =
                $"Caixa pequena demais — arraste uma área de ao menos {MinStampBoxWidthPt:0}×{MinStampBoxHeightPt:0}pt.";
            return;
        }

        StampBoxNotice = null;
        CanonicalizeRawScalars();
        StampPlacementPhase = StampPlacementPhase.Adjusting;
    }

    /// FIX (revisão final da branch, achado real reproduzido pelo revisor — I1/I2, a mesma raiz): os 4
    /// escalares brutos ficam LEGITIMAMENTE invertidos durante um cruzamento de `ResizeBoxByHandle`
    /// (contrato da alça — ver doc XML de lá), mas só até o usuário SOLTAR o mouse. Sem re-canonicalizar
    /// na FRONTEIRA do gesto, o PRÓXIMO gesto herdava os brutos invertidos assumindo ordem canônica — 2
    /// bugs reais: (a) `MoveBoxBy` computava `width = Xb-Xa` NEGATIVA e passava pro `ClampToPage`
    /// (que assume `w>=0`), colapsando a caixa a zero perto da borda (repro do revisor: Adjusting
    /// 100,100–300,200 → Resize(Right,−280) [inverte] → MoveBoxBy(−200,0) → largura 0); (b) depois de um
    /// cruzamento, um resize NOVO pela alça `Left` (que por CONTRATO sempre escreve em `_boxXa`) passava
    /// a mover a borda DIREITA visual, porque `_boxXa` tinha parado de ser o lado esquerdo (repro: flip
    /// pra [20,100] → Resize(Left,−10) → borda direita 100→90). Fix NA RAIZ: canonicalizar SEMPRE que um
    /// GESTO termina (não só na entrada em Adjusting) — chamado pela View no mouse-up/LostMouseCapture
    /// de QUALQUER gesto de mover/redimensionar (`Page_MouseLeftButtonUp`/`ResetGestureState`, ver
    /// `PdfViewerControl.xaml.cs`). Dentro de um MESMO gesto contínuo (mouse pressionado, vários
    /// MouseMove) a inversão continua permitida — é exatamente o que sustenta "a alça gruda no dedo do
    /// usuário através do cruzamento" (ver `ResizeBoxByHandle`); só a FRONTEIRA entre gestos precisa
    /// estar sempre canônica. Idempotente/seguro fora de Adjusting (no-op).
    public void EndAdjustGesture()
    {
        if (StampPlacementPhase != StampPlacementPhase.Adjusting) return;
        CanonicalizeRawScalars();
    }

    /// `_boxXa/_boxYa` = min, `_boxXb/_boxYb` = max — mesma normalização que `EndStampDraw` sempre
    /// aplicou ao entrar em Adjusting, extraída pra ser reusada por `EndAdjustGesture` (fronteira de
    /// CADA gesto, não só o primeiro) e pelo cinto defensivo de `MoveBoxBy` abaixo.
    private void CanonicalizeRawScalars()
    {
        double left = Math.Min(_boxXa, _boxXb), right = Math.Max(_boxXa, _boxXb);
        double bottom = Math.Min(_boxYa, _boxYb), top = Math.Max(_boxYa, _boxYb);
        _boxXa = left; _boxXb = right; _boxYa = bottom; _boxYb = top;
    }

    /// Move a caixa inteira (delta em pontos de página) durante Adjusting, preservando o TAMANHO —
    /// exemplar: `ClampToPage(x,y,w,h,...)` (o MESMO helper compartilhado por
    /// PlaceStampAtAsync/CommitDrawingAsync) — desloca, nunca encolhe, salvo
    /// se a própria página for menor que a caixa (mesmo limite físico que todo outro chamador de
    /// ClampToPage já aceita).
    public void MoveBoxBy(PdfPoint deltaPt)
    {
        if (StampPlacementPhase != StampPlacementPhase.Adjusting) return;
        if (StampBoxPageIndex < 0 || StampBoxPageIndex >= Pages.Count) return;

        // CINTO (revisão final, achado I1 do revisor — ver doc XML de EndAdjustGesture pro porquê):
        // canonicaliza aqui TAMBÉM, defensivo — a View já chama EndAdjustGesture no fim de todo gesto de
        // resize antes de um novo gesto de mover poder começar, mas MoveBoxBy nunca deve CONFIAR nisso
        // silenciosamente: computar uma largura/altura NEGATIVA quebraria ClampToPage (que assume
        // w/h>=0) mesmo que a causa fosse um caminho futuro que esqueça de canonicalizar.
        CanonicalizeRawScalars();

        var page = Pages[StampBoxPageIndex];
        double width = _boxXb - _boxXa, height = _boxYb - _boxYa; // canônico pelo CINTO acima
        var (left, bottom, right, top) = ClampToPage(
            _boxXa + deltaPt.XPt, _boxYa + deltaPt.YPt, width, height, page.WidthPt, page.HeightPt);
        _boxXa = left; _boxXb = right; _boxYa = bottom; _boxYb = top;
        RecomputeStampBoxRect();
    }

    /// Redimensiona a partir de UMA alça (delta em pontos de página desde a última chamada — não desde
    /// o início do gesto) durante Adjusting. Mapeamento alça->escalar bruto É FIXO (ver doc XML da seção
    /// acima) — cada alça sempre escreve no MESMO campo (`_boxXa`/`_boxXb`/`_boxYa`/`_boxYb`) pelo resto
    /// da fase Adjusting, nunca redescoberto a partir do retângulo NORMALIZADO corrente. `INVERSÃO AO
    /// CRUZAR`: quando o escalar que a alça move ultrapassa o OPOSTO (ex.: arrastar a alça Right além da
    /// borda Left), `RecomputeStampBoxRect` (min/max) automaticamente trata o cruzado como o novo
    /// Left/Right/Bottom/Top — nenhum caso especial: é a MESMA normalização de sempre, só que agora um
    /// valor que já foi "right" ficou numericamente menor que "left". A alça continua fisicamente presa
    /// ao MESMO canto do mouse do usuário através do cruzamento (não salta pro canto errado), porque o
    /// escalar que ela escreve nunca muda de identidade.
    public void ResizeBoxByHandle(StampBoxHandle handle, PdfPoint deltaPt)
    {
        if (StampPlacementPhase != StampPlacementPhase.Adjusting) return;
        if (StampBoxPageIndex < 0 || StampBoxPageIndex >= Pages.Count) return;

        var page = Pages[StampBoxPageIndex];
        bool movesXa = handle is StampBoxHandle.TopLeft or StampBoxHandle.Left or StampBoxHandle.BottomLeft;
        bool movesXb = handle is StampBoxHandle.TopRight or StampBoxHandle.Right or StampBoxHandle.BottomRight;
        bool movesYa = handle is StampBoxHandle.BottomLeft or StampBoxHandle.Bottom or StampBoxHandle.BottomRight;
        bool movesYb = handle is StampBoxHandle.TopLeft or StampBoxHandle.Top or StampBoxHandle.TopRight;

        if (movesXa) _boxXa = ResizeAxis(_boxXa, _boxXb, deltaPt.XPt, page.WidthPt, MinStampBoxWidthPt);
        if (movesXb) _boxXb = ResizeAxis(_boxXb, _boxXa, deltaPt.XPt, page.WidthPt, MinStampBoxWidthPt);
        if (movesYa) _boxYa = ResizeAxis(_boxYa, _boxYb, deltaPt.YPt, page.HeightPt, MinStampBoxHeightPt);
        if (movesYb) _boxYb = ResizeAxis(_boxYb, _boxYa, deltaPt.YPt, page.HeightPt, MinStampBoxHeightPt);

        RecomputeStampBoxRect();
    }

    /// FIX (revisão pós-Task-1, achado real reproduzido pelo revisor): a versão anterior clampava
    /// `moving+delta` à PÁGINA primeiro e só DEPOIS tentava empurrar o resultado pra `fixedEdge ±
    /// minSizePt` — quando esse alvo ideal também caía fora da página (o `fixedEdge` está a MENOS de
    /// `minSizePt` da borda que a alça está sendo arrastada em direção), o 2º clamp reintroduzia o
    /// próprio estouro de página que o alvo ideal existia pra evitar, produzindo silenciosamente uma
    /// largura/altura ABAIXO do mínimo (reproduzido: caixa Left=10, arrastar a alça Right por -1000pt
    /// dava largura 10pt; caixa Right=590 numa página ~595pt, arrastar Left por +1000pt dava largura
    /// 5pt). Dois clamps sequenciais SEM verificar se as 2 restrições (mínimo, página) ainda cabem
    /// JUNTAS é o defeito — a matemática certa precisa das 2 ao mesmo tempo, não em sequência.
    ///
    /// MATEMÁTICA (derivada explicitamente, não só corrigida por tentativa): a faixa de valores de
    /// `next` que satisfaz o mínimo em relação a `fixedEdge` é a UNIÃO de 2 intervalos disjuntos (o
    /// "vão morto" `(fixedEdge-minSizePt, fixedEdge+minSizePt)` nunca é válido pra nenhum lado) —
    /// `NEG = [0, fixedEdge-minSizePt]` (lado onde `next <= fixedEdge-minSizePt`) e `POS =
    /// [fixedEdge+minSizePt, pageMaxPt]` (lado onde `next >= fixedEdge+minSizePt`) — cada um só
    /// NÃO-VAZIO se a página tiver espaço pro mínimo DAQUELE lado (`NEG` vazio se `fixedEdge <
    /// minSizePt`; `POS` vazio se `fixedEdge + minSizePt > pageMaxPt` — exatamente o caso reproduzido:
    /// `fixedEdge=10` deixa `NEG` vazio, `fixedEdge=590` numa página de ~595pt deixa `POS` vazio). O
    /// alvo bruto (`moving+delta`, SEM clamp nenhum ainda) decide qual lado o usuário está pedindo; se
    /// esse lado for válido, o resultado é o alvo bruto CLAMPADO DIRETO à faixa daquele lado (1 único
    /// clamp, já respeitando as 2 restrições ao mesmo tempo — nunca um 2º clamp por cima de um 1º já
    /// corrompido). Se o alvo bruto cair no vão morto OU mirar um lado sem espaço nenhum: usa o outro
    /// lado se ele for válido (a alça "não consegue" cruzar pra um lado sem cabimento — gruda na
    /// fronteira do mínimo do lado que ainda existe, ao invés de produzir uma largura abaixo do
    /// mínimo); se NENHUM dos 2 lados cabe o mínimo (página menor que 2×minSizePt a partir de
    /// `fixedEdge` — documento minúsculo, caso degenerado JÁ aceito em outro lugar, ver `MoveBoxBy`/
    /// `ClampToPage`), clampa só à página, sem fingir que o mínimo foi respeitado (fisicamente
    /// impossível ali).
    private static double ResizeAxis(double moving, double fixedEdge, double delta, double pageMaxPt, double minSizePt)
    {
        double desired = moving + delta; // alvo BRUTO, sem clamp nenhum ainda -- decide o lado pedido

        double negLo = 0, negHi = fixedEdge - minSizePt;
        double posLo = fixedEdge + minSizePt, posHi = pageMaxPt;
        bool negValid = negHi >= negLo; // NEG não-vazio: a página comporta o mínimo deste lado
        bool posValid = posHi >= posLo; // POS não-vazio: idem, do outro lado

        if (negValid && desired <= negHi) return Math.Clamp(desired, negLo, negHi);
        if (posValid && desired >= posLo) return Math.Clamp(desired, posLo, posHi);

        // `desired` caiu no vão morto (ou mirou um lado inválido) -- gruda na fronteira de mínimo mais
        // perto do alvo pedido, entre as que existem (cobre tanto "só 1 lado cabe" -- os 2 casos
        // reproduzidos pelo revisor -- quanto "nenhum delta chegou perto de nenhum lado de propósito").
        if (negValid && posValid) return Math.Abs(desired - negHi) <= Math.Abs(desired - posLo) ? negHi : posLo;
        if (negValid) return negHi;
        if (posValid) return posLo;
        return Math.Clamp(desired, 0, pageMaxPt); // nenhum lado cabe o mínimo -- caso degenerado aceito
    }

    private void RecomputeStampBoxRect() =>
        StampBoxRect = new PdfQuad(Math.Min(_boxXa, _boxXb), Math.Min(_boxYa, _boxYb), Math.Max(_boxXa, _boxXb), Math.Max(_boxYa, _boxYb));

    /// Cancela a caixa em QUALQUER fase (None incluído — idempotente/seguro de chamar sempre) — chamado
    /// por Esc (`PdfViewerControl.OnPreviewKeyDown`), o botão "✖ Cancelar" do adorner, uma troca de
    /// ferramenta (`OnActiveToolChanged` acima), uma troca/fechamento de documento
    /// (`MainViewModel.OnSelectedDocumentChanged`) e QUALQUER edição aplicada por baixo
    /// (`OnSessionApplied` acima — janela sem funil, mesmo "mutation gap" de
    /// `DocumentChangedDuringPlacementNotice`). Reseta TUDO (brief: "sem armar o funil") — nunca toca
    /// Session/`_editor`/motor de assinatura, só este VM. `wasActive` protege o caminho antigo de
    /// clique único: só força `ActiveTool = None` se ESTA máquina de fato tinha algo em andamento —
    /// chamar isto com a fase já None (ex.: o clique único de hoje, que nunca sai de None) nunca deve
    /// mexer no `ActiveTool` de outro fluxo.
    ///
    /// `dueToDocumentMutation` (fix pós-revisão, achado real do coordenador — "silent path": um
    /// Ctrl+Z/edição alheia acidental no MEIO de Drawing/Adjusting fazia a caixa sumir da tela SEM
    /// NENHUMA explicação, achado que só o usuário não-técnico sentiria — "cadê meu carimbo?"): `true`
    /// APENAS no chamador de `OnSessionApplied` (a MESMA notificação pt-BR que o cinto de
    /// `SignCoreAsync` já usa pro caminho "entre o diálogo e o Confirmar" — `DocumentChangedDuringPlacementNotice`
    /// — agora cobre TAMBÉM o caminho "durante Desenhar/Ajustar", que é justamente onde
    /// `OnSessionApplied` intercepta ANTES do cinto de `SignCoreAsync` sequer ser alcançado, ver doc XML
    /// de `SignCoreAsync`). Os outros 4 chamadores (Esc/botão/troca de ferramenta/troca de documento)
    /// deixam o parâmetro no default `false` DE PROPÓSITO: o usuário INICIOU esses 4 caminhos, ele já
    /// sabe que cancelou — um aviso ali seria ruído, não informação.
    public void CancelStampBox(bool dueToDocumentMutation = false)
    {
        bool wasActive = StampPlacementPhase != StampPlacementPhase.None;
        StampPlacementPhase = StampPlacementPhase.None; // dispara RefreshStampBoxOverlay -> limpa a página dona
        StampBoxPageIndex = -1;
        StampBoxCertificateCn = null;
        StampBoxDateLabel = null;
        StampBoxNotice = null;
        _boxXa = _boxXb = _boxYa = _boxYb = 0;
        StampBoxRect = default;
        // Fix pós-revisão: notifica em pt-BR SÓ quando o cancelamento veio de uma mutação alheia (ver
        // doc XML do parâmetro acima) — MESMA mensagem/seam do cinto de SignCoreAsync
        // (DocumentChangedDuringPlacementNotice/_notifyError), gateado por wasActive (nunca notifica se
        // não havia nada em andamento pra cancelar).
        if (wasActive && dueToDocumentMutation) _notifyError(DocumentChangedDuringPlacementNotice);
        // Task 2 (Plano 8): cancelar uma colocação REALMENTE em andamento (mesmo guard de ActiveTool
        // acima) também limpa o PendingSignPlacement armado por Sign() -- todo caminho de cancelamento
        // (Esc/botão/troca de ferramenta/troca de documento, ver PdfViewerControl.OnPreviewKeyDown,
        // StampBoxCancel_Click, OnActiveToolChanged acima, MainViewModel.OnSelectedDocumentChanged) passa
        // por aqui; sem isto, um ConfirmSignatureStampAsync tardio poderia reusar um contexto
        // (certificado/motivo/local) obsoleto -- mesma disciplina de reset completo que a falha do BELT
        // dentro de SignCoreAsync já aplica.
        if (wasActive && ActiveTool == AnnotationTool.SignatureStamp)
        {
            ActiveTool = AnnotationTool.None;
            _pendingSignPlacement = null;
        }
    }

    /// Confirma a caixa (botão "✔ Assinar aqui") — só produz resultado partindo de Adjusting (não dá pra
    /// confirmar um retângulo ainda sendo desenhado, sem alças pra saber que terminou). Este método só
    /// expõe o rect final pra quem chamar; NÃO dispara o motor de assinatura nem arma o funil — isso é
    /// `ConfirmSignatureStampAsync` (Task 2/Plano 8, acima), que consome este resultado pra montar o
    /// `VisibleStampSpec` e disparar `SignCoreAsync`. Reseta a máquina pra None de qualquer forma
    /// (sucesso "sai do modo").
    public StampBoxPlacement? ConfirmStampBox()
    {
        if (StampPlacementPhase != StampPlacementPhase.Adjusting) return null;
        var result = new StampBoxPlacement(StampBoxPageIndex, StampBoxRect);
        StampPlacementPhase = StampPlacementPhase.None;
        StampBoxPageIndex = -1;
        StampBoxCertificateCn = null;
        StampBoxDateLabel = null;
        StampBoxNotice = null;
        _boxXa = _boxXb = _boxYa = _boxYb = 0;
        StampBoxRect = default;
        return result;
    }

    /// Empurra o estado da caixa (bool + Rect de tela, mesma mecânica de
    /// HasFormFieldHighlight/FormFieldHighlightRect — ver doc XML de PageViewModel) pra a PageViewModel
    /// dona, e limpa a PageViewModel ANTERIOR se a fase voltou a None (ou, hipoteticamente, se a página
    /// dona mudasse no meio — não acontece hoje, nenhum método reatribui StampBoxPageIndex fora de
    /// Begin/Cancel/Confirm, mas o guard cobre o caso por simetria com o exemplar de FormField). Chamado
    /// pelos OnXChanged de StampPlacementPhase/StampBoxRect acima — nunca direto por um dos métodos de
    /// gesto (eles só mexem nos 4 brutos + StampBoxRect, que já dispara isto sozinho).
    private void RefreshStampBoxOverlay()
    {
        int newPage = StampPlacementPhase == StampPlacementPhase.None ? -1 : StampBoxPageIndex;

        if (_stampBoxOverlayPageIndex >= 0 && _stampBoxOverlayPageIndex != newPage && _stampBoxOverlayPageIndex < Pages.Count)
        {
            Pages[_stampBoxOverlayPageIndex].HasStampBox = false;
            Pages[_stampBoxOverlayPageIndex].IsStampBoxAdjusting = false;
            Pages[_stampBoxOverlayPageIndex].StampBoxHandlePoints.Clear();
        }

        if (newPage < 0 || newPage >= Pages.Count)
        {
            _stampBoxOverlayPageIndex = -1;
            return;
        }

        var page = Pages[newPage];
        bool isAdjusting = StampPlacementPhase == StampPlacementPhase.Adjusting;
        page.HasStampBox = true;
        page.IsStampBoxAdjusting = isAdjusting;
        page.StampBoxPreviewText = BuildStampBoxPreviewText();
        page.StampBoxScreenRect = PageViewModel.PointRectToScreenRect(
            StampBoxRect.LeftPt, StampBoxRect.BottomPt, StampBoxRect.RightPt, StampBoxRect.TopPt, Zoom, page.HeightPt);
        page.StampBoxButtonsPos = ComputeStampBoxButtonsPos(page.StampBoxScreenRect, page.DisplayWidth, page.DisplayHeight);
        // FIX (revisão final da branch, achado I3 do revisor): as 8 alças só fazem sentido em Adjusting
        // (a UX/o brief pede alças só depois de soltar um retângulo válido, nunca durante o arrasto
        // inicial) — preenchê-las incondicionalmente aqui deixava 8 Rectangles hit-testáveis
        // sobrepostos à página DURANTE Drawing, que engoliam o clique que deveria CONTINUAR o gesto de
        // desenho (achado real: um clique que caísse sobre a posição de uma alça "fantasma" interceptava
        // o mouse-down em vez de chegar em Page_MouseLeftButtonDown). Invariante mantido aqui em toda
        // chamada (não só quando o estado MUDA): `StampBoxHandlePoints` só é não-vazio quando
        // `IsStampBoxAdjusting` é `true` NESTA MESMA chamada — `Clear()` explícito no `else`, nunca
        // "deixa como estava".
        if (isAdjusting) FillStampBoxHandlePoints(page.StampBoxHandlePoints, page.StampBoxScreenRect);
        else page.StampBoxHandlePoints.Clear();
        _stampBoxOverlayPageIndex = newPage;
    }

    /// Preenche `target` com as 8 alças (posição + cursor) do retângulo de tela `r` — cantos primeiro
    /// (diagonal), depois as 4 bordas (NS/WE), mesma ordem de `StampBoxHandle` (não que a ORDEM importe
    /// pro binding, é só convenção de leitura). `internal` (não `private`): chamado tanto por
    /// `RefreshStampBoxOverlay` (mudança de fase/rect) quanto por `PageViewModel.ApplyZoom` (mudança de
    /// zoom) — os 2 únicos eventos que invalidam a posição das alças.
    internal static void FillStampBoxHandlePoints(ObservableCollection<StampBoxHandlePoint> target, Rect r)
    {
        target.Clear();
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        target.Add(new StampBoxHandlePoint(new Point(r.Left, r.Top), Cursors.SizeNWSE, StampBoxHandle.TopLeft));
        target.Add(new StampBoxHandlePoint(new Point(cx, r.Top), Cursors.SizeNS, StampBoxHandle.Top));
        target.Add(new StampBoxHandlePoint(new Point(r.Right, r.Top), Cursors.SizeNESW, StampBoxHandle.TopRight));
        target.Add(new StampBoxHandlePoint(new Point(r.Right, cy), Cursors.SizeWE, StampBoxHandle.Right));
        target.Add(new StampBoxHandlePoint(new Point(r.Right, r.Bottom), Cursors.SizeNWSE, StampBoxHandle.BottomRight));
        target.Add(new StampBoxHandlePoint(new Point(cx, r.Bottom), Cursors.SizeNS, StampBoxHandle.Bottom));
        target.Add(new StampBoxHandlePoint(new Point(r.Left, r.Bottom), Cursors.SizeNESW, StampBoxHandle.BottomLeft));
        target.Add(new StampBoxHandlePoint(new Point(r.Left, cy), Cursors.SizeWE, StampBoxHandle.Left));
    }

    /// Plano 9 (Task 3, brief): a prévia ecoa o layout NOVO do carimbo — "Assinado digitalmente por\n
    /// &lt;CN&gt;\n&lt;data&gt;" (PadesSigningEngine.ApplyVisibleStamp desenha o texto de verdade nesse
    /// mesmo espírito, ver StampAppearanceRenderer) — "fiel o suficiente, não idêntico ao appearance do
    /// motor" (ver doc XML de StampBoxCertificateCn): nome+data ao menos, sem replicar a régua de
    /// prioridade/CPF/motivo/local/emissor do motor aqui.
    private string BuildStampBoxPreviewText() =>
        string.IsNullOrEmpty(StampBoxCertificateCn)
            ? StampBoxDateLabel ?? ""
            : $"Assinado digitalmente por\n{StampBoxCertificateCn}\n{StampBoxDateLabel}";

    /// Posição (Canvas.Left/Top, px de tela local à página) do grupo de botões flutuantes "✔ Assinar
    /// aqui"/"✖ Cancelar" — LOGO ABAIXO da caixa por padrão; se não couber (caixa encostada na borda de
    /// baixo da página), sobe pra ACIMA da caixa (brief: "dentro da página se a caixa encostar na
    /// borda"). Horizontal: alinhado à borda esquerda da caixa, clampado pra nunca vazar a largura da
    /// página. `rowWidth`/`rowHeight` são um tamanho ASSUMIDO (o VM não mede o layout real dos 2
    /// botões) — mesma tolerância que o brief já concede pro texto de prévia ("fiel o suficiente, não
    /// idêntico"): o clamp fica aproximado, nunca pixel-perfeito, mas nunca deixa os botões viverem fora
    /// da página visível.
    internal static Point ComputeStampBoxButtonsPos(Rect boxScreenRect, double pageDisplayWidthPx, double pageDisplayHeightPx)
    {
        const double rowWidth = 190, rowHeight = 30, margin = 6;
        double below = boxScreenRect.Bottom + margin;
        double top = below + rowHeight <= pageDisplayHeightPx ? below : Math.Max(0, boxScreenRect.Top - rowHeight - margin);
        double left = Math.Clamp(boxScreenRect.Left, 0, Math.Max(0, pageDisplayWidthPx - rowWidth));
        return new Point(left, top);
    }

    // ==== Task 4 (Plano 4): painel de Assinaturas (validação) ========================================
    //
    // CACHE — mesmo exemplar de `Outline` (não de `FormFieldEditors`/`_formFieldsCacheSnapshot`):
    // puramente de EXIBIÇÃO, nenhuma escrita depende deste cache (ao contrário do painel de Campos, que
    // usa o cache pra montar `SetFormFields` "só os alterados") — por isso SEM gate de leitura
    // mandatório, mesma razão registrada no XML doc de `Outline` acima: o pior caso de um clique durante
    // a janela entre um `Apply`/`CommitSigned` e este refresh terminar é navegar/destacar com dados
    // "aproximadamente certos" (a lista de ANTES da mudança mais recente) — severidade baixa, aceita por
    // design, nunca um crash. Renovado em BACKGROUND (`Task.Run`, ver `RefreshSignaturesAsync`) no
    // construtor e a cada `Session.Applied` (mesmos 2 sites de `_dispatcher.BeginInvoke` que já disparam
    // `RefreshOutlineAsync`/`RefreshAnnotationsByPageAsync`/`RefreshFormFieldsAsync` — nenhum mecanismo
    // novo). `Session.CommitSigned` (usado por `SignCoreAsync` acima) dispara `Applied` DIRETO — assinar
    // atualiza o painel automaticamente, de graça. Obs 17 (evitar fire-and-forget CRU em construtor):
    // coberto pelo MESMO `_dispatcher` que já resolveu esse problema pros 3 caches acima — em teste
    // xUnit puro (sem `Dispatcher.Run()` bombeando a fila), o `BeginInvoke` simplesmente nunca dispara,
    // sem corrida possível; em produção, roda na UI thread via o `SynchronizationContext` do Dispatcher.
    [ObservableProperty] private IReadOnlyList<SignatureRowViewModel> signatureRows = Array.Empty<SignatureRowViewModel>();

    /// Documento sem assinatura nenhuma (ou o 1º refresh ainda em voo) -> "Este documento não tem
    /// assinaturas." (brief, texto EXATO — ver `SignaturePanel.xaml`).
    public bool HasSignatures => SignatureRows.Count > 0;

    partial void OnSignatureRowsChanged(IReadOnlyList<SignatureRowViewModel> value) => OnPropertyChanged(nameof(HasSignatures));

    /// Renova `SignatureRows` a partir do snapshot CORRENTE — chamada no construtor (carga inicial) e a
    /// cada `Session.Applied`. SEM `retry` (mesmo motivo de `RefreshOutlineAsync`: sem gate de leitura
    /// pra "un-freeze" aqui — uma falha transitória só deixa a lista como estava até o PRÓXIMO `Applied`
    /// real tentar de novo). `i + 1`/`read.Count`: ordinal (1-based) e total, só pra
    /// `SignatureRowViewModel.CoverageLabel` — ver XML doc lá.
    internal async Task RefreshSignaturesAsync()
    {
        byte[] snapshot = Session.Snapshot;
        IReadOnlyList<SignatureInfo> read;
        try { read = await Task.Run(() => _signingEngine.ReadSignatures(snapshot)); }
        catch (Exception) { return; }
        if (!ReferenceEquals(Session.Snapshot, snapshot)) return; // obsoleto — mesma higiene de RefreshOutlineAsync
        SignatureRows = read.Select((info, i) => new SignatureRowViewModel(info, i + 1, read.Count)).ToArray();
    }

    /// Assinatura selecionada no painel (`null` = nada selecionado) — dispara o destaque do carimbo na
    /// página (`UpdateSignatureHighlightOverlay` abaixo), exemplar EXATO de `SelectedFormField`/
    /// `UpdateFormFieldHighlightOverlay` (Task 2, Plano 3c), só que a fonte geométrica é
    /// `SignatureInfo.StampRect` em vez de `FormFieldData.WidgetRect`.
    [ObservableProperty] private SignatureRowViewModel? selectedSignature;

    partial void OnSelectedSignatureChanged(SignatureRowViewModel? oldValue, SignatureRowViewModel? newValue) =>
        UpdateSignatureHighlightOverlay(oldValue, newValue);

    /// Só a página ANTIGA (se houver) e a NOVA (se houver) são tocadas — mesma economia de
    /// `UpdateFormFieldHighlightOverlay`. GATE DE ROTAÇÃO (revisão da Task 4): navegação é livre de
    /// coordenadas (`ScrollToPageRequested` só usa o ÍNDICE da página, nunca um retângulo); só o
    /// DESTAQUE depende do frame não-rotacionado (`PointRectToScreenRect` assume página sem `/Rotate`,
    /// mesma ressalva de `IsPageRotated`) — mesma política de `SelectFormField`/
    /// `UpdateFormFieldHighlightOverlay` (Task 2, Plano 3c): "preencher/navegar continua livre", só o
    /// retângulo desenhado é que ficaria geometricamente errado numa página girada.
    private void UpdateSignatureHighlightOverlay(SignatureRowViewModel? oldValue, SignatureRowViewModel? newValue)
    {
        if (oldValue?.Data.StampPageIndex is int o && o >= 0 && o < Pages.Count)
            Pages[o].HasSignatureStampHighlight = false;
        if (newValue?.Data is not { StampPageIndex: int pageIndex, StampRect: { } rect }) return;
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        if (IsPageRotated(pageIndex)) return; // GATE DE ROTAÇÃO — ver doc XML acima
        var page = Pages[pageIndex];
        page.SignatureStampHighlightRect = PageViewModel.PointRectToScreenRect(
            rect.LeftPt, rect.BottomPt, rect.RightPt, rect.TopPt, Zoom, page.HeightPt);
        page.HasSignatureStampHighlight = true;
    }

    /// Clique num signatário do painel (`SignaturePanel.xaml`) -> seleciona, navega (se houver carimbo)
    /// e destaca (se a página não estiver girada) — exemplar EXATO de `SelectFormField`:
    /// `ScrollToPageRequested` dispara sempre que há `StampPageIndex` (navegação é livre de
    /// coordenadas), o gate de rotação vive só em `UpdateSignatureHighlightOverlay` (acima), nunca aqui.
    [RelayCommand]
    private void SelectSignature(SignatureRowViewModel? row)
    {
        SelectedSignature = row; // dispara OnSelectedSignatureChanged -> UpdateSignatureHighlightOverlay
        if (row?.Data.StampPageIndex is int pageIndex) ScrollToPageRequested?.Invoke(pageIndex);
    }

    public void Dispose()
    {
        // Task 3 (Plano 3a): desinscreve dos eventos da sessão ANTES de qualquer outra coisa — Session
        // sobrevive um pouco além deste VM (vai pro PendingDisposals), então sem isso um Apply/Save
        // que corresse nesse intervalo chamaria handlers de um VM já descartado (mexendo em
        // ObservableCollection sem ninguém observando — inofensivo, mas desnecessário e ruidoso).
        Session.Applied -= OnSessionApplied;
        Session.DirtyChanged -= OnSessionDirtyChanged;
        Session.FilePathChanged -= OnSessionFilePathChanged;
        Session.CanUndoRedoChanged -= OnSessionCanUndoRedoChanged;
        Session.UndoHistoryLimitReached -= OnSessionUndoHistoryLimitReached;
        Session.EditInFlightChanged -= OnSessionEditInFlightChanged;

        // Task 3 (Plano 3b): fechar a ABA com o organizador ainda aberto (usuário nunca desligou o
        // toggle) não pode vazar o renderer PRÓPRIO do organizador — mesmo padrão dos outros dois
        // renderers deste VM, só que aqui via `OrganizerViewModel.Dispose` (que já enfileira o SEU
        // renderer em PendingDisposals) em vez de repetir o Enqueue aqui.
        Organizer?.Dispose();

        // PRIMEIRO: cancela o CTS da busca em voo e para o debounce. Sem isso, digitar e fechar a
        // aba dentro dos 300ms de debounce deixava o Tick pendente disparar depois — RunSearchAsync
        // chamando SearchInDocument (Task.Run sobre Session.Renderer) já descartado logo abaixo.
        // Close() em si é privado (só o CloseCommand gerado é público, mesmo padrão de RelayCommand
        // usado em todo o VM) — Execute(null) é a forma pública de disparar o mesmo efeito.
        Search.CloseCommand.Execute(null);
        _scheduler.Dispose();
        _thumbnailScheduler.Dispose();
        // O gate global do PDFium (PdfRenderLock) pode estar sendo segurado pelo render de OUTRO
        // documento nesse instante; bloquear a thread de UI aqui travava o fechamento da aba por
        // segundos em digitalizações pesadas. Descarta em background em vez disso — via
        // PendingDisposals (fila SERIAL, não Task.Run direto — ver correção abaixo), para que o
        // encerramento do processo possa esperar o teardown nativo do PDFium terminar antes de matar
        // a thread-pool (0xC0000005 na saída).
        //
        // CORREÇÃO (revisão pós-Task 6, achado do revisor via decompilação do IL da Docnet.Core): a
        // minha hipótese original — "duas Task.Run concorrentes causavam o 0xC0000005 por falta de
        // exclusão mútua" — estava ERRADA. Tanto PdfRenderLock.Gate (nosso lock) quanto o lock
        // INTERNO da própria Docnet.Core (Monitor.Enter num lock estático da DocLib, confirmado por
        // decompilação) já serializavam TODAS as chamadas nativas de qualquer renderer, em qualquer
        // thread — concorrência ENTRE disposes nunca foi o mecanismo do crash; consolidar duas
        // Task.Run numa só só estreitou uma janela de µs pra ns, não removeu nenhuma concorrência de
        // fato (ela já não existia).
        //
        // O que realmente muda com PendingDisposals virar uma fila SERIAL (um Enqueue por renderer,
        // executados em sequência garantida pelo TPL — ver doc XML da classe) é a INVARIANTE do
        // processo: no máximo 1 teardown nativo em voo a qualquer momento, processado numa cadência
        // previsível em vez de picos de N threads de pool terminando quase juntas. Isso fecha um
        // buraco que a versão "1 Task.Run por Dispose()" ainda deixava aberto: fechar VÁRIAS abas de
        // uma vez (MainWindow.OnClosed itera Documents; MainViewModel.CloseDocument por clique no ✕)
        // continuava gerando N Task.Runs concorrentes — a mesma forma que mediu ~3 crashes em 5
        // rodadas antes desta correção.
        //
        // Causa mais provável do crash em si (não "resolvida" por exclusão mútua — isso já existia —
        // e sim por REDUZIR o volume/cadência): RenderPage usa RenderFlags.RenderAnnotations (preciso
        // pra mostrar carimbos de assinatura, ver doc XML de PdfDocumentRenderer.RenderPage), que faz
        // cada render-reader criar um form-fill environment de forma PREGUIÇOSA na primeira
        // renderização. Desde a Task 6, cada DocumentViewModel tem DOIS render-readers (principal +
        // miniaturas), dobrando o churn de FPDFDOC_InitFormFillEnvironment/ExitFormFillEnvironment por
        // documento aberto/fechado — exatamente o frame nativo onde o access violation acontecia. A
        // fila serial não elimina esse churn, mas garante que ele nunca se acumula em picos.
        var session = Session;
        var thumbnailRenderer = _thumbnailRenderer;
        PendingDisposals.Enqueue(() => session.Dispose());
        PendingDisposals.Enqueue(() => thumbnailRenderer.Dispose());

        // Task 4 (Plano 15): descarta o motor de OCR SÓ se ESTE VM o criou (`_ownsOcrEngine` — um motor
        // INJETADO pertence a quem o injetou). `TesseractOcrEngine` segura recursos nativos (Leptonica/
        // Tesseract) — enfileirado como os demais teardowns nativos, não descartado na thread de UI.
        if (_ownsOcrEngine && _ocrEngine is IDisposable ocrDisposable)
            PendingDisposals.Enqueue(() => ocrDisposable.Dispose());
    }
}
