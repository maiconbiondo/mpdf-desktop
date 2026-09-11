using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using mPdf.App.ViewModels;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

// SearchViewModel (Task 5): fake searcher injetado no delegate (sem PDFium, sem UI) — mesma
// disciplina de testabilidade de TextSelectionTests/ZoomAnchorTests. O debounce (DispatcherTimer,
// wiring real na Task 5) NÃO é testado por relógio de parede aqui: RunSearchAsync é o gatilho
// extraído que o Tick chama em produção e que os testes chamam DIRETO, sem esperar 300ms reais.
//
// Revisão (I6a): os 3 testes abaixo agora CAPTURAM os argumentos do callback de resultados
// (_onResultsChanged) em vez de descartá-los com `(_, _) => { }` — o rótulo pt-BR já provava o
// índice corrente INDIRETAMENTE (via ResultCountLabel), mas não provava que o callback (consumido de
// verdade por DocumentViewModel.ApplySearchResults em produção pra distribuir destaques/rolar) recebe
// o ÍNDICE CERTO em cada navegação.
public class SearchViewModelTests
{
    private static SearchHit Hit(int pageIndex) => new(pageIndex, 0, 1, []);

    private static SearchViewModel.Searcher FakeSearcher(IReadOnlyList<SearchHit> hits) =>
        (_, _) => Task.FromResult(hits);

    [Fact] // rótulo pt-BR "N de Total", e navegação PRA FRENTE avança o índice corrente
    public async Task RunSearchAsync_WithHits_LabelIsPtBr_AndNextAdvancesIndex()
    {
        var hits = new List<SearchHit>();
        for (int i = 0; i < 17; i++) hits.Add(Hit(0));
        IReadOnlyList<SearchHit>? lastHits = null;
        int lastIndex = -99;
        var vm = new SearchViewModel(FakeSearcher(hits), (h, idx) => { lastHits = h; lastIndex = idx; });
        vm.Query = "pagina";

        await vm.RunSearchAsync();
        Assert.Equal("1 de 17", vm.ResultCountLabel);
        Assert.Same(hits, lastHits);   // callback recebe a LISTA de hits de verdade, não só o rótulo derivado
        Assert.Equal(0, lastIndex);    // índice 0-based do 1º hit

        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Equal("3 de 17", vm.ResultCountLabel);
        Assert.Equal(2, lastIndex);    // índice 0-based do 3º hit — prova que o callback acompanha a navegação
    }

    [Fact] // navegação CIRCULAR: Anterior a partir do primeiro hit vai pro ÚLTIMO; Próximo a partir
    // do último volta pro PRIMEIRO — nunca lança (nem trava) nas duas bordas.
    public async Task Navigation_WrapsAroundBothEnds_Circularly()
    {
        var hits = new List<SearchHit> { Hit(0), Hit(1), Hit(2) };
        int lastIndex = -99;
        var vm = new SearchViewModel(FakeSearcher(hits), (_, idx) => lastIndex = idx);
        vm.Query = "xy"; // >= 2 caracteres não-espaço (I-5a) — FakeSearcher ignora o texto da query
        await vm.RunSearchAsync();
        Assert.Equal("1 de 3", vm.ResultCountLabel);   // índice corrente começa no primeiro hit
        Assert.Equal(0, lastIndex);

        vm.PreviousCommand.Execute(null);
        Assert.Equal("3 de 3", vm.ResultCountLabel);   // Anterior do 1º -> último (wrap)
        Assert.Equal(2, lastIndex);                    // índice 0-based do último (3 hits: 0,1,2)

        vm.NextCommand.Execute(null);
        Assert.Equal("1 de 3", vm.ResultCountLabel);   // Próximo do último -> 1º (wrap)
        Assert.Equal(0, lastIndex);
    }

    [Fact] // sem hits -> "Nenhum resultado"; Fechar limpa query, fecha a barra e limpa o rótulo
    public async Task NoHits_ShowsNenhumResultado_AndCloseClearsState()
    {
        IReadOnlyList<SearchHit>? lastHits = null;
        int lastIndex = -99;
        var vm = new SearchViewModel(FakeSearcher([]), (h, idx) => { lastHits = h; lastIndex = idx; });
        vm.IsOpen = true;
        vm.Query = "inexistente";

        await vm.RunSearchAsync();
        Assert.Equal("Nenhum resultado", vm.ResultCountLabel);
        Assert.Empty(lastHits!);
        Assert.Equal(-1, lastIndex);   // sem hits -> nenhum índice corrente

        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsOpen);
        Assert.Equal(string.Empty, vm.Query);
        Assert.Equal(string.Empty, vm.ResultCountLabel);
        Assert.Equal(-1, lastIndex);   // Close() também notifica o callback com índice -1
    }

    [Fact] // I-5a (revisão final): consulta com menos de 2 caracteres não-espaço é tratada como
    // vazia — nenhuma busca real é disparada (o searcher injetado registra as chamadas e prova 0),
    // hits limpos e rótulo VAZIO (não "Nenhum resultado", que só se aplica a uma busca de verdade que
    // não achou nada).
    public async Task RunSearchAsync_QueryBelowMinimumLength_DoesNotInvokeSearcherAndClearsHits()
    {
        int callCount = 0;
        SearchViewModel.Searcher countingSearcher = (_, _) =>
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<SearchHit>>([Hit(0)]);
        };
        IReadOnlyList<SearchHit>? lastHits = null;
        int lastIndex = -99;
        var vm = new SearchViewModel(countingSearcher, (h, idx) => { lastHits = h; lastIndex = idx; });

        vm.Query = "a"; // 1 caractere não-espaço -> abaixo do mínimo de 2
        await vm.RunSearchAsync();

        Assert.Equal(0, callCount);                       // searcher NUNCA invocado
        Assert.Equal(string.Empty, vm.ResultCountLabel);   // rótulo vazio, não "Nenhum resultado"
        Assert.NotNull(lastHits);
        Assert.Empty(lastHits!);
        Assert.Equal(-1, lastIndex);

        vm.Query = " a "; // só 1 caractere não-espaço (os 2 espaços não contam) -> ainda abaixo do mínimo
        await vm.RunSearchAsync();
        Assert.Equal(0, callCount);
        Assert.Equal(string.Empty, vm.ResultCountLabel);
    }

    // Item (a) da Task 1 (Plano 3a): 0 hits + sonda dizendo "documento sem texto algum" -> rótulo
    // distinto ("digitalizado"), não o "Nenhum resultado" genérico. Sem sonda nenhuma (os 4 testes
    // acima, todos com o ctor de 2 parâmetros), o comportamento ANTIGO continua intacto — prova que a
    // mudança é aditiva, não uma regressão no caminho já coberto.
    [Fact]
    public async Task RunSearchAsync_ZeroHits_DocumentHasNoText_ShowsScannedMessage()
    {
        var vm = new SearchViewModel(FakeSearcher([]), (_, _) => { },
            hasTextProbe: _ => Task.FromResult(false)); // "documento não tem texto algum"
        vm.Query = "xy";

        await vm.RunSearchAsync();

        Assert.Equal("Documento sem texto pesquisável (digitalizado)", vm.ResultCountLabel);
    }

    // Contraste direto: mesma sonda, mas devolvendo "documento TEM texto" (só não achou a query) ->
    // rótulo genérico de sempre, não o de digitalizado.
    [Fact]
    public async Task RunSearchAsync_ZeroHits_DocumentHasText_ShowsNenhumResultado()
    {
        var vm = new SearchViewModel(FakeSearcher([]), (_, _) => { },
            hasTextProbe: _ => Task.FromResult(true)); // "documento tem texto"
        vm.Query = "xy";

        await vm.RunSearchAsync();

        Assert.Equal("Nenhum resultado", vm.ResultCountLabel);
    }

    // Cache (Task 1, item a): a sonda só é chamada 1x por VM — a 2ª busca com 0 hits reaproveita o
    // resultado da 1ª, sem tocar o PDFium de novo (custo pago só uma vez por documento).
    [Fact]
    public async Task RunSearchAsync_ZeroHits_ProbesOnlyOnce_ThenCachesResult()
    {
        int probeCalls = 0;
        var vm = new SearchViewModel(FakeSearcher([]), (_, _) => { }, hasTextProbe: _ =>
        {
            probeCalls++;
            return Task.FromResult(false);
        });

        vm.Query = "primeira";
        await vm.RunSearchAsync();
        Assert.Equal("Documento sem texto pesquisável (digitalizado)", vm.ResultCountLabel);
        Assert.Equal(1, probeCalls);

        vm.Query = "segunda";
        await vm.RunSearchAsync();
        Assert.Equal("Documento sem texto pesquisável (digitalizado)", vm.ResultCountLabel);
        Assert.Equal(1, probeCalls); // NÃO chamou de novo — resultado já estava cacheado
    }

    // Uma vez que uma busca ACHA algo (prova positiva mais barata que qualquer sonda), uma busca
    // seguinte com 0 hits (query diferente) volta ao rótulo genérico — o documento provou ter texto,
    // não é mais candidato a "digitalizado" — e a sonda nem precisa ser chamada de novo.
    [Fact]
    public async Task RunSearchAsync_AfterHitsFound_LaterZeroHits_NeverShowsScannedMessage()
    {
        var hits = new List<SearchHit> { Hit(0) };
        int probeCalls = 0;
        SearchViewModel.Searcher searcher = (q, _) =>
            Task.FromResult<IReadOnlyList<SearchHit>>(q == "acha" ? hits : []);
        var vm = new SearchViewModel(searcher, (_, _) => { }, hasTextProbe: _ =>
        {
            probeCalls++;
            return Task.FromResult(false);
        });

        vm.Query = "acha";
        await vm.RunSearchAsync();
        Assert.Equal("1 de 1", vm.ResultCountLabel);

        vm.Query = "naoacha";
        await vm.RunSearchAsync();
        Assert.Equal("Nenhum resultado", vm.ResultCountLabel);
        Assert.Equal(0, probeCalls); // hits>0 já provou "tem texto" — sonda nunca precisou ser chamada
    }
}
