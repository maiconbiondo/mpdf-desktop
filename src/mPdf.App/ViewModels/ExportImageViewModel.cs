using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mPdf.App.Rendering;
using mPdf.Documents;
using mPdf.Rendering;

namespace mPdf.App.ViewModels;

/// Fase do fluxo do diálogo "Exportar página como imagem" (Task 4, Plano 7) — mesmo espírito de
/// `BatchSignPhase`: Options (coletar formato/alcance/resolução/destino) -> Running (progresso +
/// cancelamento, exemplar: lote) -> Done (resultado). Só 3 fases (sem uma 4ª "Editing" com lista
/// editável — nada aqui é uma lista de itens, é um único documento com N páginas).
public enum ExportImagePhase { Options, Running, Done }

public enum ExportImageFormat { Png, Jpg }

public enum ExportImageRange { CurrentPage, AllPages }

/// VM da janela "Exportar página como imagem" (Task 4, Plano 7) — mesma razão estrutural de
/// `BatchSignViewModel` (Task 5, Plano 4): o fluxo inclui execução em BACKGROUND com progresso e
/// cancelamento, precisa ser testável sem abrir janela nenhuma. A janela (`Views.ExportImageDialog`) só
/// hospeda este VM como `DataContext`.
///
/// LEITURA PURA (diferente de todo comando de EDIÇÃO deste app): só consome `mPdf.Rendering`
/// (`PdfDocumentRenderer.RenderPage`) — nunca `mPdf.Editing`, nunca `Session.TryBeginEdit`/`ApplyEdit`.
/// Por isso funciona em documento ASSINADO sem gate nenhum (mesma política uniforme de leitura já
/// registrada em `mPdf.Editing.Contract` para `ExtractPages`/`MergeDocuments`/`SplitByRanges`/
/// `GetPageRotations`/`ReadOutline`/`ReadFormFields`: nenhuma dessas opera MUTANDO o PDF assinado, só
/// LEEM ele para produzir algo novo — exportar como imagem é a MESMA classe de operação, um passo
/// adiante: produz pixels, não bytes de PDF).
///
/// SNAPSHOT-COERÊNCIA (brief): `_snapshot` é passado pelo CHAMADOR (`DocumentViewModel.ExportImage`),
/// capturado UMA VEZ ali (`Session.Snapshot`, cópia imutável por referência — nunca mutada in-place, ver
/// doc XML de `DocumentSession.Snapshot`) ANTES deste VM existir — uma edição concorrente que aterrisse
/// em `Session` DEPOIS que o diálogo já abriu exporta a versão CAPTURADA, nunca a nova (mesma leitura
/// "instante congelado" já aceita por `MainViewModel.Split`/`OrganizerViewModel.ExtractSelected`).
///
/// RENDERER DEDICADO (exemplar: `PdfPrintPaginator`): um `PdfDocumentRenderer` PRÓPRIO é criado sobre
/// `_snapshot` dentro de `RunExport`, nunca o `Session.Renderer` da aba ativa (esse é o cache de
/// ESCALA ÚNICA do visualizador — ver doc XML de `PdfDocumentRenderer`; uma exportação martelando o
/// MESMO renderer serializaria com o scroll da UI). UM ÚNICO renderer é reusado por TODAS as páginas do
/// lote (ao contrário de `BatchSignViewModel`, que abre um renderer POR ARQUIVO — aqui é sempre o MESMO
/// documento, então reabrir o PDF a cada página reparsearia à toa; risco nomeado no plano: "exportar 500
/// páginas a 300dpi é pesado" — reusar o renderer half evita boa parte disso). `PdfRenderLock` é
/// respeitado internamente por `RenderPage` (1 página por vez, processo inteiro) — nenhum lock extra
/// necessário aqui.
public sealed partial class ExportImageViewModel : ObservableObject
{
    private readonly byte[] _snapshot;
    private readonly int _pageCount;
    private readonly int _currentPageIndex; // 0-based
    private readonly string _baseFileName;  // nome do documento sem extensão (base p/ "nome-p001.png")

    // Cancelamento: `Cancel()` roda na UI thread (RelayCommand), `RunExport` roda OFF da UI thread
    // (dentro de Task.Run, ver Start) -- `volatile` garante que a escrita seja visível pro loop em
    // background sem depender de um lock (mesmo espírito de BatchSignViewModel._cancelRequested, que
    // dispensa `volatile` só porque LÁ o loop inteiro roda na UI thread -- aqui não, ver doc XML de Start).
    private volatile bool _cancelRequested;

    [ObservableProperty] private ExportImageFormat format = ExportImageFormat.Png;
    [ObservableProperty] private ExportImageRange range = ExportImageRange.CurrentPage;
    [ObservableProperty] private int dpi = 150;
    [ObservableProperty] private string? destination;
    [ObservableProperty] private ExportImagePhase phase = ExportImagePhase.Options;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private bool wasCancelled;
    [ObservableProperty] private int exportedCount;
    [ObservableProperty] private string? errorMessage;

    public int PageCount => _pageCount;

    /// Índice 0-based da página "atual" recebida no construtor (revisão: era só um detalhe interno de
    /// `Start`, sem forma de um teste no nível de `DocumentViewModel` confirmar que `CurrentPage - 1` foi
    /// mesmo o valor passado adiante — ver `ExportImageCommand_Execute_ShowsDialogWithSessionSnapshotAnd
    /// CurrentPage`, `DocumentViewModelTests.cs`). Só leitura, nunca usado pra decidir nada FORA de `Start`.
    public int CurrentPageIndex => _currentPageIndex;

    /// Conveniência de binding (exemplar: `BatchSignViewModel.CanEditList`/`IsRunning`/`IsDone`) —
    /// `Views.ExportImageDialog` alterna 3 painéis via estes bools em vez de comparar o enum no XAML.
    public bool CanEditOptions => Phase == ExportImagePhase.Options;
    public bool IsRunning => Phase == ExportImagePhase.Running;
    public bool IsDone => Phase == ExportImagePhase.Done;

    /// Hook de TESTE — `null` em produção (nenhum custo, um único `if` por página). Se setado, o loop de
    /// `RunExport` BLOQUEIA (fora da UI thread, seguro) logo depois de gravar a 1ª página do lote,
    /// liberado quando o teste chama `SetResult`. Existe porque renderizar/gravar as páginas pequenas das
    /// fixtures de teste é RÁPIDO DEMAIS pra uma corrida "cancelar no meio" confiável sem esta pausa
    /// determinística — mesmo PROPÓSITO do `Gate` de `FakeBatchSigningEngine`
    /// (BatchSignViewModelTests), adaptado aqui porque `RunExport` não tem um "motor" injetável separado
    /// (a própria função de render+encode+gravação É o corpo do loop, não uma dependência substituível).
    internal TaskCompletionSource<bool>? TestGateAfterFirstPage { get; set; }

    public ExportImageViewModel(byte[] snapshot, int pageCount, int currentPageIndex, string baseFileName)
    {
        _snapshot = snapshot;
        _pageCount = pageCount;
        _currentPageIndex = currentPageIndex;
        _baseFileName = baseFileName;
    }

    partial void OnPhaseChanged(ExportImagePhase value)
    {
        OnPropertyChanged(nameof(CanEditOptions));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnDestinationChanged(string? value) => StartCommand.NotifyCanExecuteChanged();

    private bool CanStart() => Phase == ExportImagePhase.Options && !string.IsNullOrWhiteSpace(Destination);

    /// Exemplar: `BatchSignViewModel.Start` (progresso ANTES de cada item, cancelamento checado ANTES do
    /// próximo item, nenhum item "em voo" é interrompido no meio). DIFERENÇA proposital: lá o LOOP inteiro
    /// roda na UI thread (só o trabalho pesado de CADA arquivo vai pro `Task.Run`, porque cada arquivo tem
    /// seu PRÓPRIO renderer/motor descartável). Aqui o LOOP INTEIRO roda dentro de UM `Task.Run` (para que
    /// um ÚNICO `PdfDocumentRenderer` sobreviva por todas as páginas — ver doc XML da classe) — por isso
    /// `ProgressText` é atualizado via `IProgress&lt;string&gt;` (captura o `SynchronizationContext` da UI
    /// NA CRIAÇÃO, marshaling automático e seguro de volta pra UI thread a cada `Report`) em vez de
    /// atribuição direta: `[ObservableProperty]` gerado pelo CommunityToolkit levanta `PropertyChanged`
    /// direto na thread chamadora — sem isto, o WPF lançaria ao tentar atualizar o binding a partir de uma
    /// thread do pool. Pelo MESMO motivo, `WasCancelled`/`ExportedCount`/`ErrorMessage` só são setados
    /// DEPOIS do `await Task.Run(...)` retornar (de volta na UI thread), nunca de dentro do loop.
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        int[] pagesToExport = Range == ExportImageRange.CurrentPage
            ? [_currentPageIndex]
            : Enumerable.Range(0, _pageCount).ToArray();

        string destination = Destination!;
        ExportImageRange range = Range;
        ExportImageFormat format = Format;
        int dpi = Dpi;

        Phase = ExportImagePhase.Running;
        _cancelRequested = false;

        var progress = new Progress<string>(text => ProgressText = text);
        var result = await Task.Run(() => RunExport(pagesToExport, range, destination, format, dpi, progress));

        ExportedCount = result.Count;
        WasCancelled = result.Cancelled;
        ErrorMessage = result.Error;
        Phase = ExportImagePhase.Done;
    }

    private bool CanCancel() => Phase == ExportImagePhase.Running;

    /// Só sinaliza (mesmo contrato de `BatchSignViewModel.Cancel`) — quem observa a flag é `RunExport`,
    /// ANTES de cada página. Nenhum `CancellationToken`: nada dentro de uma página precisa ser
    /// interrompido NO MEIO (sempre grava por completo ou não grava nada), só o LOOP entre páginas
    /// precisa parar.
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancelRequested = true;

    /// Roda OFF da UI thread (dentro do `Task.Run` de `Start`). Um único `PdfDocumentRenderer` sobre
    /// `_snapshot` serve TODAS as páginas (ver doc XML da classe); descartado via `PendingDisposals`
    /// (exemplar: `PrintService.Print`/`BatchSignViewModel.SignOneFile` — "no máximo 1 teardown nativo em
    /// voo no processo inteiro"), nunca `using` direto. "Nenhum arquivo parcial no nome final" (brief):
    /// cada página é renderizada -> codificada em MEMÓRIA -> `File.WriteAllBytes` de uma vez só (nunca um
    /// stream parcialmente escrito); um erro de I/O no MEIO do lote preserva as páginas JÁ gravadas (não
    /// há rollback — mesma semântica "arquivos completos ficam" já aceita para cancelamento, estendida
    /// aqui pra erro).
    private (int Count, bool Cancelled, string? Error) RunExport(
        int[] pagesToExport, ExportImageRange range, string destination, ExportImageFormat format, int dpi,
        IProgress<string> progress)
    {
        var renderer = new PdfDocumentRenderer(_snapshot);
        int count = 0;
        // Hoisted (revisão): precisa sobreviver pro `catch` FORA do laço nomear qual arquivo falhou --
        // `outputPath` declarado dentro do `for` não seria visível lá.
        string? currentOutputPath = null;
        try
        {
            double scale = dpi / 72.0; // mesma unidade de PdfPrintPaginator._scale (dpi/72.0)
            int digits = Math.Max(1, _pageCount.ToString().Length); // zero-pad à LARGURA da contagem de páginas

            for (int i = 0; i < pagesToExport.Length; i++)
            {
                if (_cancelRequested) return (count, true, null);

                int pageIndex = pagesToExport[i];
                progress.Report($"Exportando página {i + 1} de {pagesToExport.Length}…");

                currentOutputPath = range == ExportImageRange.CurrentPage
                    ? destination
                    : BuildPagePath(destination, _baseFileName, pageIndex + 1, digits, format);

                var rendered = renderer.RenderPage(pageIndex, scale);
                // MESMA conversão usada pelo visualizador/impressão (BitmapConverter.ToBitmapSource) --
                // pixels exportados == pixels exibidos/impressos, mesma fonte de verdade. 96 fixo aqui
                // (Task 2, Plano 9): a tag de DPI deste `bmp` INTERMEDIÁRIO é irrelevante -- `EncodeImage`
                // logo abaixo reembala o MESMO buffer de pixels com o `dpi` REALMENTE escolhido (150/300)
                // na metadata do arquivo final (ver doc XML de EncodeImage), então 96 aqui nunca vaza pro
                // arquivo exportado.
                var bmp = BitmapConverter.ToBitmapSource(rendered, 96, 96);
                byte[] bytes = EncodeImage(bmp, format, dpi);
                File.WriteAllBytes(currentOutputPath, bytes);
                count++;

                if (i == 0 && TestGateAfterFirstPage is { } gate) gate.Task.GetAwaiter().GetResult();
            }
            return (count, false, null);
        }
        catch (Exception ex)
        {
            // Revisão: mensagem PREFIXADA em pt-BR (mesmo espírito de `BatchSignFileResult`, "nome:
            // mensagem") -- `ex.Message` sozinho (IOException nativa de `File.WriteAllBytes`, ex.: arquivo
            // travado por outro processo) costuma vir no idioma do SO, não confiável como pt-BR sozinho.
            string fileLabel = currentOutputPath is { } p ? Path.GetFileName(p) : "arquivo";
            return (count, false, $"Falha ao gravar '{fileLabel}': {ex.Message}");
        }
        finally { PendingDisposals.Enqueue(renderer.Dispose); }
    }

    /// `internal` (testável sem passar por `Start`/renderer nenhum) — WPF puro, zero dependência nova
    /// (brief). JPG qualidade 90 (mesma escolha/justificativa já registrada em
    /// `DocumentViewModel.NormalizeExifRotation`: "joelho" da curva tamanho×qualidade, sem precedente
    /// formal de benchmark, mas consistente com o ÚNICO outro uso de `JpegBitmapEncoder` no app).
    ///
    /// DPI da metadata (revisão): `bmp` vem de `BitmapConverter.ToBitmapSource`, que fixa 96/96 (correto
    /// pra EXIBIÇÃO em tela — nenhum consumidor de tela hoje olha essa tag). O ARQUIVO exportado precisa
    /// refletir o `dpi` REALMENTE escolhido (150/300) na metadata, senão um visualizador externo/"imprimir
    /// em tamanho real" calcularia o tamanho físico da imagem errado a partir de 96 fixo, mesmo os PIXELS
    /// estando corretos. Reempacota o MESMO buffer de pixels (nenhuma reconversão/reamostragem — só a tag
    /// de DPI muda) via `BitmapSource.Create` com `dpi`/`dpi` explícitos antes de entregar ao encoder.
    internal static byte[] EncodeImage(BitmapSource bmp, ExportImageFormat format, int dpi)
    {
        int stride = bmp.PixelWidth * ((bmp.Format.BitsPerPixel + 7) / 8);
        byte[] pixels = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(pixels, stride, 0);
        var dpiTagged = BitmapSource.Create(
            bmp.PixelWidth, bmp.PixelHeight, dpi, dpi, bmp.Format, bmp.Palette, pixels, stride);

        BitmapEncoder encoder = format == ExportImageFormat.Jpg
            ? new JpegBitmapEncoder { QualityLevel = 90 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(dpiTagged));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// "nome-pNNN.ext" (brief) zero-padded a `digits`; colisão de nome -> " (2)", " (3)"... MESMA
    /// convenção de `MainViewModel.BuildSplitPartPath`/`BatchSignViewModel.BuildSignedOutputPath`
    /// (varredura simples por `File.Exists`, mesma aceitação de concorrência já documentada nos dois
    /// exemplares). Só usado pro alcance "todas as páginas" -- "página atual" grava DIRETO no destino
    /// escolhido pelo próprio usuário via `SaveFileDialog` (que já confirma sobrescrita nativamente,
    /// nenhum sufixo surpresa sobre um caminho que o usuário JÁ escolheu explicitamente).
    internal static string BuildPagePath(string folder, string baseName, int pageNumber, int digits, ExportImageFormat format)
    {
        string ext = format == ExportImageFormat.Png ? "png" : "jpg";
        string stem = $"{baseName}-p{pageNumber.ToString().PadLeft(digits, '0')}";
        string candidate = Path.Combine(folder, $"{stem}.{ext}");
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem} ({n}).{ext}");
        return candidate;
    }
}
