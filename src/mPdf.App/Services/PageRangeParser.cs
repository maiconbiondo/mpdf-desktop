namespace mPdf.App.Services;

/// Parser PURO (sem WPF, sem IPdfEditor) da string de intervalos digitada no diálogo de Dividir (Task 4,
/// Plano 3b) — ex.: `"1-5, 8, 10-12"`. Convenção de índice: a STRING é 1-based (o que o usuário vê na
/// UI, mesma numeração de `PageCountLabel`/`OrganizerPageViewModel.PageNumber`); o resultado é 0-based
/// INCLUSIVO nos dois extremos, pronto pra alimentar direto `IPdfEditor.SplitByRanges` (mesma convenção
/// de `(from, to)` do motor — ver doc XML lá). Testável sem WPF/PDF real de propósito: toda a lógica de
/// validação vive aqui, não espalhada pelo VM.
public static class PageRangeParser
{
    /// OVERLAPS PERMITIDOS (decisão de design, Task 4): nada no contrato de `SplitByRanges` proíbe a
    /// mesma página aparecer em 2 saídas diferentes, e o caso de uso é legítimo (ex.: repetir a capa em
    /// duas partes) — este parser não detecta nem bloqueia sobreposição entre tokens.
    ///
    /// INTERVALO INVERTIDO ("10-5") É ERRO (não normalizado silenciosamente pra "5-10"): reordenar sem
    /// avisar arriscaria mascarar um erro de digitação real do usuário — mesmo espírito de
    /// `SplitByRanges` já exigir `to &gt;= from` no motor (ver `Contract.cs`), só que aqui a mensagem
    /// nomeia o TOKEN pt-BR antes de qualquer chamada ao motor.
    public static IReadOnlyList<(int from, int to)> Parse(string input, int pageCount)
    {
        if (pageCount <= 0)
            throw new ArgumentException("O documento não tem páginas.", nameof(pageCount));
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Informe ao menos um intervalo de páginas.", nameof(input));

        var result = new List<(int from, int to)>();
        foreach (var rawToken in input.Split(','))
        {
            var token = rawToken.Trim();
            // Vírgula duplicada/sobrando ("1-5,,8" ou um "," no fim) produz um token vazio — tolerado
            // como ruído inofensivo, não um erro (mesmo espírito de tolerar espaços em volta dos números).
            if (token.Length == 0) continue;
            result.Add(ParseToken(token, pageCount));
        }

        if (result.Count == 0)
            throw new ArgumentException("Informe ao menos um intervalo de páginas.", nameof(input));

        return result;
    }

    private static (int from, int to) ParseToken(string token, int pageCount)
    {
        int from1, to1;
        int dashIdx = token.IndexOf('-');
        // dashIdx > 0 (não no início) e não o último caractere: um '-' de verdade separando "N-M", não
        // um número negativo nem um token terminado em '-' sem continuação (ambos caem no ramo de baixo
        // e falham no TryParse de um único inteiro, virando "Intervalo inválido").
        if (dashIdx > 0 && dashIdx < token.Length - 1)
        {
            var left = token[..dashIdx].Trim();
            var right = token[(dashIdx + 1)..].Trim();
            if (!int.TryParse(left, out from1) || !int.TryParse(right, out to1))
                throw new ArgumentException($"Intervalo inválido: '{token}'.", nameof(token));
        }
        else
        {
            if (!int.TryParse(token, out from1))
                throw new ArgumentException($"Intervalo inválido: '{token}'.", nameof(token));
            to1 = from1;
        }

        if (to1 < from1)
            throw new ArgumentException(
                $"Intervalo inválido (início maior que o fim): '{token}'.", nameof(token));

        if (from1 < 1 || to1 > pageCount)
            throw new ArgumentException(
                $"Intervalo fora dos limites do documento (1-{pageCount}): '{token}'.", nameof(token));

        return (from1 - 1, to1 - 1); // 1-based (UI) -> 0-based (motor)
    }
}
