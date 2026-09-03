using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Rendering;
using mPdf.Signing;

namespace mPdf.App.ViewModels;

/// Fase do fluxo do diálogo "Assinar em lote" (Task 5, Plano 4) — governa quais comandos estão
/// habilitados e o que a View mostra (lista editável vs. progresso vs. resultados).
public enum BatchSignPhase { Editing, Running, Done }

/// Resultado de UM arquivo do lote — texto EXATO do brief: sucesso é "`nome.pdf` assinado", falha é
/// "`nome.pdf`: &lt;mensagem tipada&gt;" (a mensagem do motor/I-O, já pt-BR — ver `SignOneFile`).
public sealed record BatchSignFileResult(string FileName, bool Succeeded, string Message);

/// Item de exibição do `ListBox` de certificados — MESMO formato de `Views.SignDialog.CertificateItem`
/// (Task 3), mas exposto aqui (não `private` dentro de uma janela) porque `BatchSignViewModel` precisa
/// dele em `SelectedCertificate`/`CanStart`, testável sem abrir janela nenhuma. Certificados ECC
/// continuam LISTADOS (nunca filtrados) — só desabilitados com a explicação pt-BR (mesma decisão da
/// Task 3: o usuário precisa VER por que não pode usar aquele certificado).
public sealed class BatchCertificateItem(SigningCertificateInfo info)
{
    public SigningCertificateInfo Info { get; } = info;

    public string DisplayText => Info.IsRsa
        ? Info.DisplayName
        : $"{Info.DisplayName} — assinatura ECDSA não suportada nesta versão";

    public bool IsEnabled => Info.IsRsa;

    public string? DisabledReason => Info.IsRsa
        ? null
        : "Este certificado usa criptografia ECDSA — esta versão do mPDF assina somente com certificados RSA.";
}

/// VM da janela "Assinar em lote" (Task 5, Plano 4) — DIFERENTE dos demais diálogos deste app
/// (SignDialog/MergeFilesDialog: coletam dados e fecham), este é um VM real, com estado e comandos,
/// porque o fluxo inclui execução em BACKGROUND com progresso/cancelamento — precisa ser testável sem
/// abrir janela nenhuma (ver BatchSignViewModelTests). A janela (`Views.BatchSignDialog`) só hospeda
/// este VM como `DataContext`; toda a lógica vive aqui.
///
/// OPERA SOBRE ARQUIVOS EXTERNOS (decisão do plano) — nunca a sessão aberta de nenhuma aba, por isso
/// NENHUM funil (`Session.TryBeginEdit`/`EndEdit`) é necessário: cada arquivo é lido do disco, assinado
/// em memória e gravado como um NOVO arquivo "nome (assinado).pdf" ao lado do original — o original
/// nunca é reaberto para escrita.
///
/// RECUSA DE ARQUIVO ABERTO (risco do plano): um caminho que já está aberto numa aba do app é recusado
/// da lista, nunca adicionado — `MainViewModel.BatchSign` passa o predicado `isPathOpen` (a MESMA
/// comparação de caminho, OrdinalIgnoreCase, do dedupe de `OpenPath`). A recusa acontece só no momento
/// de ADICIONAR (`AddFiles`) — como o diálogo é MODAL (`ShowDialog`), o conjunto de abas abertas não
/// pode mudar enquanto o diálogo está aberto, então checar de novo em `Start` seria redundante.
///
/// DocMDP NUNCA oferecido (decisão registrada do plano): uma lista de lote pode misturar arquivos já
/// assinados e não assinados — uma única escolha de DocMDP pra todos seria incoerente (o motor RECUSA
/// `CertificationLevel != None` num doc já assinado, então "certificar todos" quebraria pra metade da
/// lista silenciosamente). `SignOneFile` sempre monta `CertificationLevel: null` — cada arquivo é
/// assinado de forma INCREMENTAL (aprovação), com ou sem assinatura prévia, sem exceção.
///
/// SEAM: `pickFiles`/`isPathOpen` são parâmetros OBRIGATÓRIOS (sem default `??`) — ao contrário do
/// padrão `UiPrompts` usado pelos demais diálogos deste app, uma omissão aqui não pode silenciosamente
/// cair num `OpenFileDialog`/predicado de produção: o COMPILADOR já recusa qualquer chamador (teste ou
/// produção) que esqueça de passá-los, então não existe risco de uma suíte headless travar num diálogo
/// nativo real (a mesma classe de risco que a seam `UiPrompts` resolve para os outros diálogos, mas
/// resolvida aqui por TIPO em vez de por um `ModuleInitializer` — nenhum dos dois é "canal de UI
/// compartilhado" que precise de um switch estático global). `signingEngine` continua opcional (`??
/// SigningEngineFactory.Create()`) — mesmo precedente de `DocumentViewModel._signingEngine`: o motor de
/// produção não mostra UI nenhuma, só faz cálculo puro, então esquecer de injetar um fake não trava
/// suíte nenhuma (só assinaria com o motor real, um risco de ISOLAMENTO de teste, não de HANG).
public sealed partial class BatchSignViewModel : ObservableObject
{
    // Tamanho/margem do carimbo do LOTE (brief: "~180×60pt", posição fixa v1 — SEM posicionamento por
    // arquivo, backlog). Mesmos valores de DocumentViewModel.DefaultStampWidthPt/HeightPt, duplicados
    // aqui porque são `private` lá e não existe utilitário compartilhado de constantes/clamp de carimbo
    // neste codebase (mesmo precedente de duplicação: BuildEditableCopyPath/BuildSplitPartPath em
    // MainViewModel). Margem de 20pt: sem precedente no codebase, escolha desta task (afasta o carimbo
    // da quina exata da página, mesmo espírito visual de qualquer carimbo/rodapé impresso comum).
    private const double StampWidthPt = 180.0, StampHeightPt = 60.0, StampMarginPt = 20.0;

    private readonly Func<string, bool> _isPathOpen;
    private readonly Func<IReadOnlyList<string>?> _pickFiles;
    private readonly ISigningEngine _signingEngine;
    // Revisão (achado crítico): precisa de /Rotate da última página pra transformar o retângulo do
    // carimbo do frame de EXIBIÇÃO pro frame de CONTEÚDO — ver ComputeStampRect. Mesmo padrão de
    // DocumentViewModel/MainViewModel._editor: não mostra UI (leitura pura), fica de fora da seam UiPrompts.
    private readonly IPdfEditor _editor;

    private bool _cancelRequested;

    public IReadOnlyList<BatchCertificateItem> Certificates { get; }
    public ObservableCollection<string> Files { get; } = [];

    [ObservableProperty] private BatchCertificateItem? selectedCertificate;
    [ObservableProperty] private string? reason;
    [ObservableProperty] private string? location;
    /// `false` = "Sem carimbo" (default — mesmo default de `SignDialog.NoStampRadio`, `IsChecked="True"`).
    [ObservableProperty] private bool placeStamp;
    [ObservableProperty] private BatchSignPhase phase = BatchSignPhase.Editing;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private IReadOnlyList<BatchSignFileResult> results = Array.Empty<BatchSignFileResult>();
    [ObservableProperty] private bool wasCancelled;
    /// Aviso pt-BR de arquivos RECUSADOS na última chamada de `AddFiles` (arquivo já aberto numa aba) —
    /// `null` quando a última chamada não recusou nada. Vive AQUI (não um `_notifyError` externo) porque
    /// é estado do PRÓPRIO diálogo (a View mostra um `TextBlock` condicional), não uma notificação
    /// separada por cima do diálogo modal já aberto.
    [ObservableProperty] private string? addFilesNotice;

    /// Habilita Adicionar/Remover — `false` durante Running/Done (mutar a lista no meio/depois da
    /// execução não faz sentido; `Start` já tirou um SNAPSHOT de `Files` antes de começar, então mesmo
    /// que a View permitisse, não afetaria o lote em andamento — mas a View não deveria nem oferecer).
    public bool CanEditList => Phase == BatchSignPhase.Editing;

    /// Conveniência de binding (exemplar: `DocumentViewModel.HasSignatures`) — `Views.BatchSignDialog`
    /// alterna 3 painéis (edição/progresso/resultados) via `Style`/`DataTrigger` sobre estes 2 bools em
    /// vez de comparar o enum `Phase` direto no XAML (nenhum `IValueConverter` novo precisou existir).
    public bool IsRunning => Phase == BatchSignPhase.Running;
    public bool IsDone => Phase == BatchSignPhase.Done;

    public BatchSignViewModel(
        IReadOnlyList<SigningCertificateInfo> certificates,
        Func<string, bool> isPathOpen,
        Func<IReadOnlyList<string>?> pickFiles,
        ISigningEngine? signingEngine = null,
        IPdfEditor? editor = null)
    {
        Certificates = certificates.Select(c => new BatchCertificateItem(c)).ToList();
        _isPathOpen = isPathOpen;
        _pickFiles = pickFiles;
        _signingEngine = signingEngine ?? SigningEngineFactory.Create();
        _editor = editor ?? PdfEditorFactory.Create();

        Files.CollectionChanged += (_, _) => StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCertificateChanged(BatchCertificateItem? value) => StartCommand.NotifyCanExecuteChanged();

    partial void OnPhaseChanged(BatchSignPhase value)
    {
        OnPropertyChanged(nameof(CanEditList));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        AddFilesCommand.NotifyCanExecuteChanged();
        RemoveFileCommand.NotifyCanExecuteChanged();
    }

    // ---- Adicionar / remover arquivos -----------------------------------------------------------------

    /// `_pickFiles` devolve `null` (cancelado) ou a lista ESCOLHIDA (produção: multi-seleção de
    /// `OpenFileDialog`, mesmo filtro/título de `MergeFilesDialog.Add_Click`). Cada caminho aberto numa
    /// aba (`_isPathOpen`) é RECUSADO — nunca entra em `Files` — e listado em `AddFilesNotice` (singular/
    /// plural pt-BR). Arquivos não-recusados são adicionados SEM dedupe (mesmo comportamento simples de
    /// `MergeFilesDialog`: escolher o mesmo arquivo duas vezes o processa duas vezes, gerando uma 2ª
    /// saída com sufixo de colisão — inofensivo, não um bug).
    [RelayCommand(CanExecute = nameof(CanEditList))]
    private void AddFiles()
    {
        if (_pickFiles() is not { Count: > 0 } picked) return;

        var refused = new List<string>();
        foreach (var path in picked)
        {
            if (_isPathOpen(path)) { refused.Add(Path.GetFileName(path)); continue; }
            Files.Add(path);
        }

        AddFilesNotice = refused.Count switch
        {
            0 => null,
            1 => $"\"{refused[0]}\" não foi adicionado: o arquivo está aberto no aplicativo. Feche a aba antes de incluí-lo no lote.",
            _ => $"{refused.Count} arquivos não foram adicionados: estão abertos no aplicativo. Feche as abas antes de incluí-los no lote.",
        };
    }

    [RelayCommand(CanExecute = nameof(CanEditList))]
    private void RemoveFile(string path) => Files.Remove(path);

    // ---- Assinar em lote (execução em background, cancelável) ------------------------------------------

    private bool CanStart() => Phase == BatchSignPhase.Editing && Files.Count > 0 && SelectedCertificate is { IsEnabled: true };

    /// SNAPSHOT de `Files`/certificado/motivo/local/carimbo ANTES de mudar `Phase` — desacopla o loop de
    /// qualquer mutação futura de `Files` (defesa em profundidade; a View não deveria permitir mutar
    /// durante Running, ver `CanEditList`, mas o loop nunca depende disso pra estar correto). Progresso
    /// reportado ANTES de cada arquivo ("Assinando arquivo N de M…", brief). Cancelamento: checado ANTES
    /// de iniciar CADA arquivo (nunca no meio) — o arquivo em voo sempre TERMINA (grava ou falha
    /// normalmente); nenhum arquivo parcial é gravado (SignOneFile só grava em sucesso, via
    /// `DocumentSession.WriteNewFile`, que já é atômica — ver doc XML lá). Erro em UM arquivo NUNCA
    /// aborta o lote (`SignOneFile` captura toda exceção esperada e devolve um resultado de FALHA, nunca
    /// deixa a exceção escapar do loop).
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        var cert = SelectedCertificate!.Info.Certificate;
        string? reasonValue = string.IsNullOrWhiteSpace(Reason) ? null : Reason;
        string? locationValue = string.IsNullOrWhiteSpace(Location) ? null : Location;
        bool placeStampValue = PlaceStamp;
        var toProcess = Files.ToList();

        Phase = BatchSignPhase.Running;
        _cancelRequested = false;
        WasCancelled = false;
        var collected = new List<BatchSignFileResult>();

        for (int i = 0; i < toProcess.Count; i++)
        {
            if (_cancelRequested) { WasCancelled = true; break; }
            string path = toProcess[i];
            ProgressText = $"Assinando arquivo {i + 1} de {toProcess.Count}…";
            var result = await Task.Run(() => SignOneFile(path, cert, reasonValue, locationValue, placeStampValue));
            collected.Add(result);
        }

        Results = collected;
        Phase = BatchSignPhase.Done;
    }

    private bool CanCancel() => Phase == BatchSignPhase.Running;

    /// Só sinaliza — o loop de `Start` é quem observa a flag ANTES do próximo arquivo (ver doc XML lá).
    /// Nenhum `CancellationToken` real: nada dentro de `SignOneFile` precisa ser interrompido NO MEIO
    /// (nunca há um estado "parcial" a abortar), só o LOOP entre arquivos precisa parar — uma flag
    /// simples é suficiente e mais simples que orquestrar um token através de `Task.Run`.
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancelRequested = true;

    /// Assina UM arquivo, do disco até o arquivo de saída gravado — nunca deixa uma exceção escapar
    /// (contrato central do brief: "erro em um arquivo não aborta o lote"). Mensagem de falha: EXATO
    /// formato do brief, "`nome.pdf`: &lt;mensagem tipada&gt;" — `ex.Message` já vem pt-BR de qualquer
    /// camada que a produziu (I/O, `PdfDocumentRenderer`, `ISigningEngine`).
    private BatchSignFileResult SignOneFile(
        string path, X509Certificate2 certificate, string? reason, string? location, bool placeStamp)
    {
        string fileName = Path.GetFileName(path);
        try
        {
            byte[] pdf = File.ReadAllBytes(path);

            VisibleStampSpec? stamp = null;
            if (placeStamp)
            {
                // PDFium em memória (mPdf.Rendering), NUNCA uma DocumentSession completa -- não
                // precisamos de renderer cacheado/spill de undo-redo só pra ler geometria da última
                // página; mesma razão registrada em DocumentSession.WriteNewFile pra não abrir uma
                // sessão descartável só pra gravar. SEM `using`: descarte nativo vai pra
                // `PendingDisposals` (fila SERIAL do processo inteiro, exemplar: PrintService.cs — "no
                // máximo 1 teardown nativo em voo", mesma garantia que TODO renderer avulso deste app
                // segue, nunca `Dispose`/`using` direto) -- `finally` garante o Enqueue mesmo se a
                // leitura de geometria/rotação lançar.
                var renderer = new PdfDocumentRenderer(pdf);
                try
                {
                    // Minor (revisão): 0 páginas -> GetPageSize(-1) lançaria de dentro do Docnet/PDFium
                    // (mensagem não-tipada) -- checado explicitamente ANTES, resultado pt-BR próprio.
                    // Confirmado que Docnet aceita um PDF sintaticamente válido com Pages/Kids vazio
                    // (PageCount==0) sem lançar na construção -- não é um caso hipotético descartável.
                    if (renderer.PageCount == 0)
                        return new BatchSignFileResult(fileName, Succeeded: false, $"{fileName}: arquivo sem páginas.");

                    int lastPageIndex = renderer.PageCount - 1;
                    // Frame de EXIBIÇÃO (PDFium já aplica /Rotate aqui — é o que o usuário VÊ).
                    var displaySize = renderer.GetPageSize(lastPageIndex);
                    // /Rotate da última página (0/90/180/270) -- só a mPdf.Editing (via IPdfEditor) sabe
                    // ler isso sem vazar iText pro App; NUNCA reimplementado aqui.
                    int rotation = _editor.GetPageRotations(pdf)[lastPageIndex];
                    stamp = new VisibleStampSpec(lastPageIndex,
                        ComputeStampRect(rotation, displaySize.WidthPt, displaySize.HeightPt));
                }
                finally { PendingDisposals.Enqueue(renderer.Dispose); }
            }

            // CertificationLevel SEMPRE null (v1: nunca DocMDP em lote -- ver doc XML da classe).
            var request = new SignRequest(pdf, certificate, reason, location, stamp, CertificationLevel: null);
            byte[] signed = _signingEngine.Sign(request);

            string outputPath = BuildSignedOutputPath(path);
            DocumentSession.WriteNewFile(outputPath, signed); // atômica (temp + move), original nunca tocado

            return new BatchSignFileResult(fileName, Succeeded: true, $"{fileName} assinado");
        }
        catch (Exception ex)
        {
            return new BatchSignFileResult(fileName, Succeeded: false, $"{fileName}: {ex.Message}");
        }
    }

    /// Canto INFERIOR DIREITO da página, com margem, clampado no frame de EXIBIÇÃO (brief: "posição
    /// fixa v1... clamped"), depois transformado pro frame de CONTEÚDO (`TransformVisualRectToContentFrame`
    /// — ver doc XML lá pro ACHADO CRÍTICO da revisão e a álgebra completa). `internal` (não `private`):
    /// testável direto (`BatchSignViewModelTests`), sem precisar renderizar pixels pra cada asserção de
    /// número — a prova PIXEL-A-PIXEL (oráculo mandatório da revisão) vive à parte, na integração.
    internal static PdfQuad ComputeStampRect(int rotation, double displayWidthPt, double displayHeightPt)
    {
        double x = displayWidthPt - StampMarginPt - StampWidthPt;
        double y = StampMarginPt;
        var visual = ClampToPage(x, y, StampWidthPt, StampHeightPt, displayWidthPt, displayHeightPt);
        return TransformVisualRectToContentFrame(rotation, visual, displayWidthPt, displayHeightPt);
    }

    /// EXEMPLAR: `DocumentViewModel.ClampToPage` (mesmo algoritmo/mesma assinatura, duplicado aqui pelo
    /// mesmo motivo de `StampWidthPt`/`StampHeightPt` acima) — desloca (nunca encolhe) um retângulo de
    /// tamanho fixo, ancorado em `(x,y)` como canto inferior-esquerdo, pra dentro dos limites
    /// `[0,pageWidthPt]x[0,pageHeightPt]`; cobre o caso degenerado de uma página menor que o carimbo.
    /// Opera SEMPRE no frame de EXIBIÇÃO aqui (`ComputeStampRect` chama isto com `displayWidthPt`/
    /// `displayHeightPt`, nunca com dimensões de conteúdo) — o clamp precisa acontecer no frame que o
    /// usuário efetivamente VÊ, antes da transformação pro frame de conteúdo.
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

    /// ACHADO CRÍTICO DA REVISÃO (confirmado ao vivo): a 1ª versão desta task alimentava
    /// `PdfDocumentRenderer.GetPageSize` (frame de EXIBIÇÃO — PDFium já compõe `/Rotate` ao reportar
    /// dimensões, mesma convenção de `RotatePages_Rotate90_SwapsPageDimensions` em
    /// `mPdf.Editing.Tests`) DIRETO em `VisibleStampSpec.Rect`, que o motor (`PadesSigningEngine.
    /// ApplyVisibleStamp`/`SetPageRect`) consome no frame de CONTEÚDO NÃO-ROTACIONADO (mesma convenção
    /// de `AnnotationData`/`FormFieldData.WidgetRect` — `/Rotate` é atributo de EXIBIÇÃO, o `/Rect`
    /// gravado no PDF nunca muda quando a página gira). Numa página `/Rotate=90`, o retângulo calculado
    /// pro canto inferior-direito do frame EXIBIDO (642pt–822pt de X, medido ao vivo pelo revisor contra
    /// uma página de 595pt de largura NÃO-rotacionada) caía INTEIRAMENTE fora da página real quando
    /// interpretado como coordenada de conteúdo — o carimbo saía do PDF assinado sem NENHUM erro
    /// (`PadesSigningEngine.ValidateStamp` só valida índice de página + retângulo não-degenerado, nunca
    /// que o retângulo caiba dentro da página).
    ///
    /// DERIVAÇÃO (composição de frames, não pattern-matching — álgebra completa também em
    /// task-5-report.md): `/Rotate=r` pede ao visualizador pra girar a página `r` graus no sentido
    /// HORÁRIO ao exibir. Rastreando os 4 cantos do retângulo unitário (BL/BR/TR/TL) do frame de
    /// CONTEÚDO (origem inferior-esquerda, X direita, Y cima, extensão `Wu`x`Hu`) pro frame de EXIBIÇÃO
    /// (mesma convenção, extensão `Wd`x`Hd` — `Wd=Hu`/`Hd=Wu` quando r=90/270, `Wd=Wu`/`Hd=Hu` quando
    /// r=0/180) sob uma rotação física horária de `r` graus, e resolvendo o mapa linear consistente com
    /// os 4 pontos, dá o mapa direto CONTEÚDO->EXIBIÇÃO; a fórmula abaixo é o INVERSO dele (EXIBIÇÃO->
    /// CONTEÚDO, o que este método precisa), aplicado aos 4 cantos do retângulo de entrada e depois
    /// reduzido a min/max (a transformação pode inverter qual canto vira "esquerda"/"direita"):
    ///   r=0:   identidade.
    ///   r=90:  Left'=Hd-dyTop, Right'=Hd-dyBottom, Bottom'=dxLeft, Top'=dxRight.
    ///   r=180: Left'=Wd-dxRight, Right'=Wd-dxLeft, Bottom'=Hd-dyTop, Top'=Hd-dyBottom.
    ///   r=270: Left'=dyBottom, Right'=dyTop, Bottom'=Wd-dxRight, Top'=Wd-dxLeft.
    /// (dxLeft/dxRight/dyBottom/dyTop = retângulo de ENTRADA, no frame de exibição; Wd/Hd = dimensões
    /// de EXIBIÇÃO da página, mesmas que `GetPageSize` devolve.) Cada fórmula foi verificada mapeando os
    /// 4 cantos do retângulo unitário nos dois frames e conferindo que width'/height' resultantes batem
    /// com a troca de eixos esperada (90/270 trocam largura&lt;-&gt;altura; 0/180 preservam).
    internal static PdfQuad TransformVisualRectToContentFrame(
        int rotation, (double left, double bottom, double right, double top) visual,
        double displayWidthPt, double displayHeightPt)
    {
        double dxL = visual.left, dxR = visual.right, dyB = visual.bottom, dyT = visual.top;
        double wd = displayWidthPt, hd = displayHeightPt;

        return rotation switch
        {
            0 => new PdfQuad(dxL, dyB, dxR, dyT),
            90 => new PdfQuad(hd - dyT, dxL, hd - dyB, dxR),
            180 => new PdfQuad(wd - dxR, hd - dyT, wd - dxL, hd - dyB),
            270 => new PdfQuad(dyB, wd - dxR, dyT, wd - dxL),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation,
                "Rotação de página inesperada — esperado 0, 90, 180 ou 270."),
        };
    }

    /// "nome (assinado).pdf" AO LADO do original (brief); colisão de nome -> " (2)", " (3)"... MESMA
    /// convenção de `MainViewModel.BuildEditableCopyPath`/`BuildSplitPartPath` (varredura simples por
    /// `File.Exists`, mesma aceitação de concorrência já documentada nos dois exemplares).
    private static string BuildSignedOutputPath(string originalPath)
    {
        string dir = Path.GetDirectoryName(originalPath)
            ?? throw new IOException($"Não foi possível determinar o diretório de '{originalPath}'.");
        string baseName = $"{Path.GetFileNameWithoutExtension(originalPath)} (assinado)";
        string ext = Path.GetExtension(originalPath);

        string candidate = Path.Combine(dir, baseName + ext);
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{baseName} ({n}){ext}");
        return candidate;
    }
}
