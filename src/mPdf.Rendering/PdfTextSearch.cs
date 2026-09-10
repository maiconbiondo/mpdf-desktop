using System.Globalization;

namespace mPdf.Rendering;

/// Um trecho encontrado por PdfTextSearch.FindAll: [CharStart, CharStart+Length) em TextPage.Text
/// da página PageIndex, com os PdfCharacter correspondentes (mesma geometria de GetTextPage) para
/// quem for desenhar o realce.
public sealed record SearchHit(int PageIndex, int CharStart, int Length, IReadOnlyList<PdfCharacter> Chars);

/// Busca de texto pura sobre as páginas de um PdfDocumentRenderer — caso- E acento-insensível
/// (pt-BR: "pagina" encontra "página", "acoes" encontra "AÇÕES").
public static class PdfTextSearch
{
    /// Percorre TODAS as páginas de `renderer` via GetTextPage (Task 2). CHAME FORA DA THREAD DE UI:
    /// cada página espera brevemente o gate global do PDFium (PdfRenderLock.Gate) — a espera em si é
    /// curta (~0.5ms de execução), mas pode ficar atrás de uma renderização pesada em andamento; em
    /// um documento de muitas páginas a soma é perceptível. `ct` é checado ENTRE páginas, nunca
    /// dentro do lock de GetTextPage (para não segurar o gate além do necessário) — um token já
    /// cancelado lança OperationCanceledException antes de tocar a primeira página, sem devolver
    /// resultado parcial. Query vazia/só-espaços devolve lista vazia SEM iterar página alguma (nem
    /// chama GetTextPage, logo nem abre o lock).
    public static IReadOnlyList<SearchHit> FindAll(PdfDocumentRenderer renderer, string query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;

        for (int pageIndex = 0; pageIndex < renderer.PageCount; pageIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var page = renderer.GetTextPage(pageIndex);
            foreach (var (start, len) in FindInText(page.Text, query))
            {
                var chars = new List<PdfCharacter>(len);
                for (int i = 0; i < len; i++) chars.Add(page.Characters[start + i]);
                hits.Add(new SearchHit(pageIndex, start, len, chars));
            }
        }
        return hits;
    }

    /// Detecta se o documento tem QUALQUER texto extraível (Task 1, Plano 3a, item a — distingue
    /// "0 hits porque não achou a query" de "0 hits porque o documento é digitalizado/sem texto
    /// algum" na UI). CHAME FORA DA THREAD DE UI, mesma disciplina de FindAll (ct verificado ENTRE
    /// páginas, nunca durante o lock de GetTextPage).
    ///
    /// Sinal MAIS BARATO possível sem inventar contrato novo: reaproveita o mesmo GetTextPage que
    /// FindAll já usa (nenhuma API nova no renderer) e PARA na primeira página com >=1 caractere —
    /// um documento normal tem texto já na página 1, então o caso comum devolve `true` sem nem olhar
    /// as páginas seguintes; só um documento REALMENTE todo digitalizado (nenhuma página com texto)
    /// paga o custo de percorrer todas.
    public static bool DocumentHasText(PdfDocumentRenderer renderer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        for (int pageIndex = 0; pageIndex < renderer.PageCount; pageIndex++)
        {
            ct.ThrowIfCancellationRequested();
            if (renderer.GetTextPage(pageIndex).Characters.Count > 0) return true;
        }
        return false;
    }

    // Núcleo da comparação, extraído para ser testável com strings sintéticas — capacidade de
    // acento-insensibilidade testada aqui direto, separada da cobertura via fixture real (nenhuma
    // fixture PDF atual contém acento), conforme a disciplina de guard-rails do projeto.
    //
    // ACHADO (verificado por compilação contra net10.0, via reflexão sobre CompareInfo): a
    // sobrecarga esperada pelo brief — IndexOf(string, string, int startIndex, CompareOptions, out
    // int matchLength) — NÃO EXISTE. A única sobrecarga com `out matchLength` opera sobre
    // ReadOnlySpan<char> e NÃO tem parâmetro startIndex:
    //   IndexOf(ReadOnlySpan<char> source, ReadOnlySpan<char> value, CompareOptions, out int)
    // Adaptado: cada iteração fatia `text` a partir de `offset` via AsSpan(offset) e soma `offset`
    // de volta ao índice devolvido (que é relativo à fatia). matchLength continua necessário: com
    // IgnoreNonSpace, o trecho casado no texto-fonte pode ter comprimento diferente de
    // query.Length (ex.: marca de combinação decomposta ocupa mais de um char-fonte por 1 char de
    // query) — usar query.Length como Length do hit seria uma correção sutilmente errada.
    internal static IEnumerable<(int start, int len)> FindInText(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) yield break;

        int offset = 0;
        while (offset <= text.Length)
        {
            int foundInSlice = CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                text.AsSpan(offset), query.AsSpan(),
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out int matchLength);
            if (foundInSlice < 0) yield break;
            int start = offset + foundInSlice;
            yield return (start, matchLength);
            // avança pelo menos 1 char mesmo se matchLength viesse 0 (não deveria, para query
            // não-vazia, mas evita loop infinito em vez de confiar cegamente na API externa)
            offset = start + Math.Max(matchLength, 1);
        }
    }
}
