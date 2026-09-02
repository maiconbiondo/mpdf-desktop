using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mPdf.Rendering;

namespace mPdf.App.ViewModels;

/// VM da barra de busca (Ctrl+F, Task 5): navegação circular sobre os SearchHit encontrados,
/// rótulo pt-BR, debounce de digitação. PURO o bastante pra testar com um Searcher fake (sem
/// PDFium, sem UI) — a única dependência de infraestrutura WPF é o DispatcherTimer do debounce, que
/// nunca dispara sozinho em teste (nada bombeia a fila do Dispatcher lá); por isso RunSearchAsync é
/// extraído como o GATILHO que o Tick do timer chama em produção e que os testes chamam DIRETO, sem
/// depender de relógio de parede.
public sealed partial class SearchViewModel : ObservableObject
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(300);

    /// Busca injetada — produção envolve PdfTextSearch.FindAll em Task.Run (NUNCA a thread de UI:
    /// FindAll espera o gate global do PDFium por página, ver mPdf.Rendering.PdfTextSearch); testes
    /// injetam um fake síncrono via Task.FromResult.
    public delegate Task<IReadOnlyList<SearchHit>> Searcher(string query, CancellationToken ct);

    /// Sonda opcional (Task 1, Plano 3a, item a): "este documento tem ALGUM texto extraível?" —
    /// separada do delegate `Searcher` de propósito, pra não quebrar o contrato existente (e os
    /// testes que já constroem `Searcher`s fake com só 2 parâmetros). `null` = comportamento antigo
    /// (rótulo "Nenhum resultado" pra toda busca com 0 hits, nunca distingue documento digitalizado).
    /// Produção injeta `PdfTextSearch.DocumentHasText` (via Task.Run, ver DocumentViewModel).
    public delegate Task<bool> HasTextProbe(CancellationToken ct);

    private readonly Searcher _search;
    private readonly HasTextProbe? _hasTextProbe;
    // Cache de "documento sem texto algum" — calculado no máximo 1x por VM (== 1x por documento,
    // já que cada DocumentViewModel tem exatamente 1 SearchViewModel): a 1ª busca com 0 hits chama
    // _hasTextProbe; buscas seguintes com 0 hits reaproveitam este valor sem tocar o PDFium de novo.
    // Vira `false` definitivamente assim que QUALQUER busca encontra >=1 hit (prova positiva mais
    // barata que qualquer sonda) — nunca mais precisa perguntar depois disso.
    private bool _documentHasNoSearchableText;
    private bool _hasTextProbed;
    // Chamado toda vez que os resultados OU o índice corrente mudam (busca nova, navegação, fechar)
    // — quem constrói o VM (DocumentViewModel) usa isso pra distribuir os retângulos de destaque nas
    // páginas e pedir a rolagem até o hit corrente; o VM em si não conhece PageViewModel/UI.
    private readonly Action<IReadOnlyList<SearchHit>, int> _onResultsChanged;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;

    private IReadOnlyList<SearchHit> _hits = [];
    private int _currentIndex = -1;

    // NotifyPropertyChangedFor: sem isso, o rótulo ficava com o valor ANTIGO entre uma tecla digitada
    // e a busca debounced (300ms depois) terminar — a query mudou (ex.: virou vazia) mas o rótulo só
    // era renotificado quando os resultados chegavam. Agora cada tecla também renotifica o rótulo.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultCountLabel))]
    private string query = string.Empty;
    [ObservableProperty] private bool isOpen;

    public SearchViewModel(Searcher search, Action<IReadOnlyList<SearchHit>, int> onResultsChanged,
        HasTextProbe? hasTextProbe = null)
    {
        _search = search;
        _hasTextProbe = hasTextProbe;
        _onResultsChanged = onResultsChanged;
        _debounce = new DispatcherTimer { Interval = DebounceInterval };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RunSearchAsync(); };
    }

    // Cada tecla reinicia o debounce de 300ms (cancela o Tick pendente e reagenda) — só o ÚLTIMO
    // Tick depois de uma pausa na digitação dispara a busca de verdade.
    partial void OnQueryChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public string ResultCountLabel =>
        !MeetsMinimumQueryLength(Query) ? string.Empty :
        _hits.Count == 0 && _documentHasNoSearchableText ? "Documento sem texto pesquisável (digitalizado)" :
        _hits.Count == 0 ? "Nenhum resultado" :
        $"{_currentIndex + 1} de {_hits.Count}";

    private bool HasHits => _hits.Count > 0;

    // I-5a (revisão final): menos de 2 caracteres não-espaço é tratado como consulta vazia — sem
    // isso, cada tecla isolada (ex.: usuário ainda digitando a 1ª letra) disparava um FindAll completo
    // no documento inteiro (PdfTextSearch varre TODAS as páginas), com centenas de hits inúteis (toda
    // ocorrência da letra) por um custo de PDFium desproporcional ao valor do resultado. Conta
    // caracteres não-espaço (não `Query.Length` puro) para que " a " (com espaços) também caia aqui.
    private const int MinQueryLength = 2;

    private static bool MeetsMinimumQueryLength(string query)
    {
        int nonWhitespace = 0;
        foreach (char c in query)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (++nonWhitespace >= MinQueryLength) return true;
        }
        return false;
    }

    /// Gatilho extraído do Tick do debounce (ver comentário de classe) — cancela qualquer busca
    /// anterior ainda em voo antes de iniciar a nova (só o resultado mais recente importa).
    public async Task RunSearchAsync()
    {
        // I-5a: consulta abaixo do mínimo -> cancela qualquer busca anterior em voo (evita que um
        // resultado tardio e obsoleto reapareça) e trata como vazia (limpa hits, rótulo vazio via
        // MeetsMinimumQueryLength acima) — SEM invocar o searcher injetado.
        if (!MeetsMinimumQueryLength(Query))
        {
            _cts?.Cancel();
            ApplyHits([]);
            return;
        }

        var previous = _cts;
        previous?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        previous?.Dispose(); // supersedida (já cancelada, ninguém mais referencia) — não vaza o handle

        IReadOnlyList<SearchHit> hits;
        try { hits = await _search(Query, cts.Token); }
        catch (OperationCanceledException) { return; } // uma busca mais nova já assumiu, ou Close() cancelou
        catch (ObjectDisposedException) { return; } // documento fechado com a busca em voo — nada a atualizar
        catch (Exception)
        {
            // Falha de busca (ex.: erro do PDFium) não pode derrubar o app NEM deixar o rótulo mudo
            // mostrando um resultado antigo como se a busca nova tivesse funcionado — mesma convenção
            // do WorkerLoop em RenderScheduler.cs:76-79 ("erro não derruba o worker, vira placeholder"):
            // engole e segue, mas aqui o "placeholder" é limpar os resultados.
            // M-1 (revisão 2): só limpa se ESTA busca ainda for a atual (cts não cancelado nesse meio
            // tempo) — uma busca SUPERSEDIDA que falhe por outro motivo (não OperationCanceledException)
            // não pode apagar os resultados VÁLIDOS já aplicados pela busca sucessora só por terminar
            // fora de ordem depois dela.
            if (!cts.IsCancellationRequested) ApplyHits([]);
            return;
        }

        if (cts.IsCancellationRequested) return; // corrida: resultado obsoleto chegou após cancelar

        // Item (a): distingue "0 hits, mas o documento tem texto (só não achou a query)" de
        // "0 hits porque o documento não tem texto ALGUM (digitalizado)". Só sonda quando ainda não
        // sabemos a resposta (_hasTextProbed) — hits>0 já prova "tem texto" sem precisar perguntar.
        if (hits.Count > 0)
        {
            _documentHasNoSearchableText = false;
            _hasTextProbed = true;
        }
        else if (!_hasTextProbed && _hasTextProbe is not null)
        {
            try { _documentHasNoSearchableText = !await _hasTextProbe(cts.Token); }
            catch (OperationCanceledException) { return; } // busca (e a sonda) supersedida ou Close()
            catch (ObjectDisposedException) { return; }     // documento fechado com a sonda em voo
            catch (Exception) { _documentHasNoSearchableText = false; } // sonda falhou: não afirma "digitalizado" sem certeza — cai no rótulo "Nenhum resultado" normal
            _hasTextProbed = true;

            if (cts.IsCancellationRequested) return; // corrida: uma busca mais nova assumiu enquanto a sonda rodava
        }

        ApplyHits(hits);
    }

    private void ApplyHits(IReadOnlyList<SearchHit> hits)
    {
        _hits = hits;
        _currentIndex = hits.Count > 0 ? 0 : -1;
        NotifyResultsChanged();
    }

    private void NotifyResultsChanged()
    {
        OnPropertyChanged(nameof(ResultCountLabel));
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        _onResultsChanged(_hits, _currentIndex);
    }

    [RelayCommand(CanExecute = nameof(HasHits))]
    private void Next()
    {
        if (_hits.Count == 0) return; // guarda contra Execute(null) direto, que ignora CanExecute
        _currentIndex = (_currentIndex + 1) % _hits.Count;
        NotifyResultsChanged();
    }

    [RelayCommand(CanExecute = nameof(HasHits))]
    private void Previous()
    {
        if (_hits.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _hits.Count) % _hits.Count;
        NotifyResultsChanged();
    }

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        Query = string.Empty;  // dispara OnQueryChanged, que reinicia o debounce...
        _debounce.Stop();      // ...por isso para ele DEPOIS, cancelando esse reagendamento
        IsOpen = false;
        _hits = [];
        _currentIndex = -1;
        NotifyResultsChanged();
    }
}
