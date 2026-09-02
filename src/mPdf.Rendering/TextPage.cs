namespace mPdf.Rendering;

public sealed record TextPage(int PageIndex, IReadOnlyList<PdfCharacter> Characters)
{
    // Cópia defensiva: sem isso, `Characters` guardaria a MESMA referência de lista que o
    // chamador possui — se o chamador mutar essa lista depois (ex.: Task 4 monta TextPages
    // sintéticos "à mão"), page.Characters dessincronizaria de page.Text (que já é uma string
    // imutável, congelada na construção). O spread cria um snapshot independente.
    public IReadOnlyList<PdfCharacter> Characters { get; } = [.. Characters];

    /// Concatenação dos caracteres na ordem devolvida pelo PDF (ordem de extração do PDFium).
    ///
    /// I-5b (revisão final): construído via char[] em vez de `string.Concat(Characters.Select(c =>
    /// c.Char))` — a versão antiga alocava UMA string de 1 caractere por `PdfCharacter` (o `Select`
    /// converte cada `char` numa `string` implicitamente antes do `Concat` juntar tudo); num
    /// documento grande, a busca de texto materializa o `Text` de TODAS as páginas, então isso é da
    /// ordem de ~1 milhão de alocações de string descartáveis por documento. Preencher um `char[]` do
    /// tamanho exato e devolver via `new string(char[])` produz o MESMO conteúdo com uma única
    /// alocação de string (a final).
    public string Text { get; } = BuildText(Characters);

    private static string BuildText(IReadOnlyList<PdfCharacter> characters)
    {
        var buffer = new char[characters.Count];
        for (int i = 0; i < characters.Count; i++) buffer[i] = characters[i].Char;
        return new string(buffer);
    }
}
