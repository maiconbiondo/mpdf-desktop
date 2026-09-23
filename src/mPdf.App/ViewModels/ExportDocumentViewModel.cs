using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mPdf.App.Rendering;
using mPdf.App.Services;
using mPdf.Documents;
using mPdf.Export;
using mPdf.Rendering;

namespace mPdf.App.ViewModels;

/// Fase do fluxo do diálogo "Exportar como Word/Excel" (Task 3, Plano 16) — mesmo espírito de
/// `ExportImagePhase`: Options (coletar alcance/destino) -> Running (progresso + cancelamento) ->
/// Done (resultado).
public enum ExportDocumentPhase { Options, Running, Done }

/// Qual exportador o diálogo aciona — Word (.docx, `IDocxExporter`) ou Excel (.xlsx, `IXlsxExporter`).
public enum ExportDocumentKind { Word, Excel }

/// Alcance da exportação: documento inteiro OU um intervalo digitado ("1-5, 8") parseado pelo mesmo
/// `PageRangeParser` do diálogo Dividir/Exportar-imagem.
public enum ExportDocumentRange { AllPages, Custom }

/// VM da janela "Exportar como Word (.docx)"/"Exportar como Excel (.xlsx)" (Task 3, Plano 16) — mesma
/// razão estrutural de `ExportImageViewModel` (Task 4, Plano 7): o fluxo inclui execução em BACKGROUND
/// com progresso e cancelamento, precisa ser testável sem abrir janela nenhuma. A janela
/// (`Views.ExportDocumentDialog`) só hospeda este VM como `DataContext`.
///
/// LEITURA PURA (mesma classe de `ExportImageViewModel`): só consome `mPdf.Rendering`
/// (`PdfDocumentRenderer.GetTextPage`/`GetPageSize` — a MESMA extração de texto+posições da
/// seleção/busca) e os exportadores neutros de `mPdf.Export`. NUNCA `mPdf.Editing`, NUNCA
/// `Session.TryBeginEdit`/`ApplyEdit`. Por isso funciona em documento ASSINADO sem gate nenhum, e o PDF
/// de origem fica BYTE-IDÊNTICO (nada é escrito de volta nele; só um arquivo .docx/.xlsx NOVO é gerado).
///
/// SNAPSHOT-COERÊNCIA (exemplar: `ExportImageViewModel`): `_snapshot` é capturado UMA VEZ pelo chamador
/// (`DocumentViewModel.ExportDocumentCoreAsync`, `Session.Snapshot`) ANTES deste VM existir — uma edição
/// concorrente que aterrisse em `Session` DEPOIS que o diálogo já abriu exporta a versão CAPTURADA.
///
/// RENDERER DEDICADO (exemplar: `ExportImageViewModel`/`PdfPrintPaginator`): um `PdfDocumentRenderer`
/// PRÓPRIO é criado sobre `_snapshot` dentro de `RunExport`, nunca o `Session.Renderer` da aba ativa
/// (esse é o cache de ESCALA ÚNICA do visualizador). Descartado via `PendingDisposals` (mesmo contrato
/// de teardown nativo serial dos outros consumidores de renderer dedicado).
///
/// "GRAVA SÓ AO CONCLUIR" (brief): ao contrário da exportação de imagem (1 arquivo por página, gravados
/// incrementalmente), aqui o texto de TODAS as páginas do alcance é ACUMULADO em memória, o exportador
/// produz UM `byte[]` único, e só então `File.WriteAllBytes` grava o destino de uma vez. Consequência
/// direta: CANCELAR em qualquer ponto antes da gravação final = NENHUM arquivo (nem parcial) — não há
/// arquivo meio-escrito possível.
public sealed partial class ExportDocumentViewModel : ObservableObject
{
    private readonly byte[] _snapshot;
    private readonly int _pageCount;
    private readonly string _baseFileName; // nome do documento sem extensão (sugestão do SaveFileDialog)
    private readonly IDocxExporter _docxExporter;
    private readonly IXlsxExporter _xlsxExporter;

    // Cancelamento: `Cancel()` roda na UI thread; `RunExport` roda OFF da UI thread (Task.Run) —
    // `volatile` garante visibilidade da escrita pro loop em background sem lock (mesmo padrão/
    // justificativa de `ExportImageViewModel._cancelRequested`).
    private volatile bool _cancelRequested;

    [ObservableProperty] private ExportDocumentRange range = ExportDocumentRange.AllPages;
    [ObservableProperty] private string rangeText = "";
    [ObservableProperty] private string? destination;
    [ObservableProperty] private ExportDocumentPhase phase = ExportDocumentPhase.Options;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string? rangeError;   // erro de validação do intervalo (fase Options)
    [ObservableProperty] private bool wasCancelled;
    [ObservableProperty] private bool noTextInRange;    // alcance sem nenhum caractere -> nenhum arquivo
    [ObservableProperty] private bool succeeded;
    [ObservableProperty] private int exportedPageCount;
    [ObservableProperty] private string? errorMessage;

    /// Word ou Excel — imutável (decidido pelo comando que abriu o diálogo). O XAML usa para título/
    /// filtro/ícone; `RunExport` escolhe o exportador.
    public ExportDocumentKind Kind { get; }

    public int PageCount => _pageCount;

    /// Extensão de arquivo do destino (para o `SaveFileDialog` no code-behind).
    public string FileExtension => Kind == ExportDocumentKind.Word ? "docx" : "xlsx";

    /// Nome-base sugerido para o arquivo de destino ("documento.docx").
    public string SuggestedFileName => $"{_baseFileName}.{FileExtension}";

    /// Título/subtítulo do cabeçalho da janela (dependem do `Kind`).
    public string DialogTitle => Kind == ExportDocumentKind.Word
        ? "Exportar como Word (.docx)"
        : "Exportar como Excel (.xlsx)";
    public string DialogSubtitle => Kind == ExportDocumentKind.Word
        ? "Texto editável em parágrafos"
        : "Melhor-esforço em tabelas alinhadas";

    /// Nota HONESTA discreta (brief): deixa claro que Word não é cópia do layout e Excel é melhor-esforço.
    public string HonestNote =>
        "Word: texto editável, não cópia do layout. Excel: melhor-esforço em tabelas alinhadas.";

    /// Conveniência de binding (exemplar: `ExportImageViewModel`) — a janela alterna os 3 painéis via
    /// estes bools em vez de comparar o enum no XAML.
    public bool CanEditOptions => Phase == ExportDocumentPhase.Options;
    public bool IsRunning => Phase == ExportDocumentPhase.Running;
    public bool IsDone => Phase == ExportDocumentPhase.Done;

    /// Hook de TESTE — `null` em produção. Se setado, o loop de `RunExport` BLOQUEIA (fora da UI thread)
    /// logo depois de extrair a 1ª página do alcance, liberado quando o teste chama `SetResult`. Mesmo
    /// PROPÓSITO/uso do `TestGateAfterFirstPage` de `ExportImageViewModel` (torna a corrida
    /// "cancelar no meio" determinística sem depender de timing).
    internal TaskCompletionSource<bool>? TestGateAfterFirstPage { get; set; }

    /// Hook de TESTE (companheiro de `TestGateAfterFirstPage`) — `null` em produção. `RunExport` o
    /// SINALIZA (`TrySetResult`) imediatamente ANTES de bloquear no gate, para que a corrida de "cancelar
    /// no meio" seja determinística: o teste aguarda este sinal (página 1 extraída, laço pausado), então
    /// cancela e libera o gate — sem depender de timing/polling. Existe porque, ao contrário da
    /// exportação de imagem (que grava a página 1 e o teste espera o ARQUIVO surgir), aqui nada é gravado
    /// até o fim, então não há artefato observável para sincronizar a pausa.
    internal TaskCompletionSource<bool>? TestGateReachedAfterFirstPage { get; set; }

    public ExportDocumentViewModel(byte[] snapshot, int pageCount, ExportDocumentKind kind,
        string baseFileName, IDocxExporter? docxExporter = null, IXlsxExporter? xlsxExporter = null)
    {
        _snapshot = snapshot;
        _pageCount = pageCount;
        Kind = kind;
        _baseFileName = string.IsNullOrWhiteSpace(baseFileName) ? "documento" : baseFileName;
        // Exportadores REAIS por default (mesmo precedente de `_editor ?? PdfEditorFactory.Create()`):
        // não mostram UI e não têm risco de hang, então NÃO passam pela seam UiPrompts. Testes injetam
        // fakes ou os reais conforme o caso.
        _docxExporter = docxExporter ?? new DocxExporter();
        _xlsxExporter = xlsxExporter ?? new XlsxExporter();
    }

    partial void OnPhaseChanged(ExportDocumentPhase value)
    {
        OnPropertyChanged(nameof(CanEditOptions));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnDestinationChanged(string? value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnRangeChanged(ExportDocumentRange value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnRangeTextChanged(string value) => StartCommand.NotifyCanExecuteChanged();

    private bool CanStart() => Phase == ExportDocumentPhase.Options
        && !string.IsNullOrWhiteSpace(Destination)
        && (Range == ExportDocumentRange.AllPages || !string.IsNullOrWhiteSpace(RangeText));

    /// Exemplar: `ExportImageViewModel.Start`. Valida o alcance ANTES de mudar de fase (parse do
    /// intervalo pode falhar — mostra `RangeError` e fica em Options). O loop inteiro roda dentro de UM
    /// `Task.Run` (um único `PdfDocumentRenderer` sobrevive por todas as páginas), então `ProgressText`
    /// é atualizado via `IProgress<string>` (captura o `SynchronizationContext` da UI NA CRIAÇÃO) e os
    /// campos de resultado só são setados DEPOIS do `await` retornar (de volta na UI thread).
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        int[] pages;
        try { pages = ResolvePages(); }
        catch (ArgumentException ex) { RangeError = ex.Message; return; }
        RangeError = null;

        string destination = Destination!;
        ExportDocumentKind kind = Kind;

        Phase = ExportDocumentPhase.Running;
        _cancelRequested = false;

        var progress = new Progress<string>(text => ProgressText = text);
        var result = await Task.Run(() => RunExport(pages, destination, kind, progress));

        ExportedPageCount = result.Pages;
        WasCancelled = result.Cancelled;
        NoTextInRange = result.NoText;
        Succeeded = result.Succeeded;
        ErrorMessage = result.Error;
        Phase = ExportDocumentPhase.Done;
    }

    private bool CanCancel() => Phase == ExportDocumentPhase.Running;

    /// Só sinaliza (mesmo contrato de `ExportImageViewModel.Cancel`) — quem observa a flag é `RunExport`,
    /// ANTES de cada página e ANTES da gravação. Como só se grava ao concluir, cancelar = nenhum arquivo.
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancelRequested = true;

    /// Expande o alcance escolhido em índices de página 0-based DISTINTOS e ORDENADOS. Documento inteiro
    /// = 0..PageCount-1; intervalo = `PageRangeParser.Parse` (reuso do parser de "1-5, 8" do diálogo
    /// Dividir — 1-based na UI, devolve pares 0-based inclusivos). `PageRangeParser` lança
    /// `ArgumentException` pt-BR com o token inválido para os intervalos fora dos limites/invertidos.
    private int[] ResolvePages()
    {
        if (Range == ExportDocumentRange.AllPages)
            return Enumerable.Range(0, _pageCount).ToArray();

        var ranges = PageRangeParser.Parse(RangeText, _pageCount);
        var set = new SortedSet<int>();
        foreach (var (from, to) in ranges)
            for (int p = from; p <= to; p++)
                set.Add(p);
        return set.ToArray();
    }

    private readonly record struct ExportResult(
        int Pages, bool Cancelled, bool NoText, bool Succeeded, string? Error);

    /// Roda OFF da UI thread (dentro do `Task.Run` de `Start`). Um único `PdfDocumentRenderer` sobre
    /// `_snapshot` serve TODAS as páginas do alcance (extração de texto+dimensões); descartado via
    /// `PendingDisposals` (nunca `using` direto — mesmo contrato de teardown nativo serial dos outros
    /// consumidores de renderer dedicado). Acumula os `ExportPage`s, e SÓ ao concluir chama o exportador
    /// e grava o `byte[]` de uma vez (cancelar/erro antes disso = nenhum arquivo).
    private ExportResult RunExport(
        int[] pages, string destination, ExportDocumentKind kind, IProgress<string> progress)
    {
        var renderer = new PdfDocumentRenderer(_snapshot);
        try
        {
            var exportPages = new List<ExportPage>(pages.Length);
            long totalChars = 0;

            for (int i = 0; i < pages.Length; i++)
            {
                if (_cancelRequested) return new ExportResult(0, true, false, false, null);

                int pageIndex = pages[i];
                progress.Report($"Exportando página {i + 1} de {pages.Length}…");

                // MESMA extração de texto+posições da seleção/busca (GetTextPage) e MESMAS dimensões da
                // página em pontos (GetPageSize) — o mapeamento PdfCharacter->ExportChar preserva os
                // valores campo a campo (Char/LeftPt/BottomPt/RightPt/TopPt), origem PDF inferior-esquerda.
                var textPage = renderer.GetTextPage(pageIndex);
                var size = renderer.GetPageSize(pageIndex);
                var chars = new List<ExportChar>(textPage.Characters.Count);
                foreach (var c in textPage.Characters)
                    chars.Add(new ExportChar(c.Char, c.LeftPt, c.BottomPt, c.RightPt, c.TopPt));
                totalChars += chars.Count;
                exportPages.Add(new ExportPage(pageIndex, size.WidthPt, size.HeightPt, chars));

                if (i == 0 && TestGateAfterFirstPage is { } gate)
                {
                    TestGateReachedAfterFirstPage?.TrySetResult(true);
                    gate.Task.GetAwaiter().GetResult();
                }
            }

            if (_cancelRequested) return new ExportResult(0, true, false, false, null);

            // Alcance sem NENHUM caractere extraível (ex.: intervalo caiu só em páginas escaneadas sem
            // OCR): NÃO gera arquivo. O caminho comum (documento inteiro sem texto) já é barrado ANTES do
            // diálogo por `DocumentViewModel.ExportDocumentCoreAsync` (aviso via o seam de prompt); esta é
            // a guarda defensiva para o alcance parcial.
            if (totalChars == 0) return new ExportResult(0, false, true, false, null);

            byte[] bytes;
            try
            {
                bytes = kind == ExportDocumentKind.Word
                    ? _docxExporter.Export(exportPages)
                    : _xlsxExporter.Export(exportPages);
            }
            catch (Exception ex)
            {
                return new ExportResult(0, false, false, false, $"Falha ao gerar o arquivo: {ex.Message}");
            }

            try
            {
                File.WriteAllBytes(destination, bytes);
            }
            catch (Exception ex)
            {
                string fileLabel = Path.GetFileName(destination);
                return new ExportResult(0, false, false, false, $"Falha ao gravar '{fileLabel}': {ex.Message}");
            }

            return new ExportResult(exportPages.Count, false, false, true, null);
        }
        finally { PendingDisposals.Enqueue(renderer.Dispose); }
    }
}
