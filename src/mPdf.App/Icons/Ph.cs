using System;
using System.Collections.Generic;

namespace mPdf.App.Icons;

/// Plano 14 (Task 1) - mapa NOME->GLIFO dos icones Phosphor usados no redesenho. Um icone e renderizado
/// como texto (TextBlock/Run) com FontFamily = {DynamicResource Fonte.Phosphor} (ou .PhosphorFill/
/// .PhosphorBold) e Text = o caractere aqui. As 3 variantes (regular/fill/bold) compartilham o MESMO
/// code point por nome - a VARIANTE e escolhida pela FAMILIA da fonte, nao por um glifo diferente; por
/// isso um unico mapa nome->glifo basta (ver Assets/Fonts + as familias Fonte.* em mPdfTheme.xaml).
///
/// Fonte da verdade: docs/redesign-ref/fonts/codepoints-{regular,fill,bold}.txt (os ~65 icones do
/// mockup; uniao de nomes distintos = 55). Os valores sao code points da Private Use Area (U+Exxx),
/// escritos como escapes uXXXX. Consumido em XAML via a MarkupExtension {icons:PhosphorIcon folder-open}
/// (ver PhosphorIconExtension) ou em code-behind via Ph.Glyph("folder-open").
public static class Ph
{
    /// Nome do icone (kebab-case) -> o caractere do code point na fonte.
    public static readonly IReadOnlyDictionary<string, string> Glifos = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["folder-open"] = "\uE256",
        ["floppy-disk"] = "\uE248",
        ["arrow-counter-clockwise"] = "\uE038",
        ["arrow-clockwise"] = "\uE036",
        ["printer"] = "\uE3DC",
        ["export"] = "\uEAF0",
        ["grid-nine"] = "\uEC8C",
        ["arrows-merge"] = "\uED3E",
        ["scissors"] = "\uEAE0",
        ["stack"] = "\uE466",
        ["magnifying-glass"] = "\uE30C",
        ["clock-counter-clockwise"] = "\uE1A0",
        ["info"] = "\uE2CE",
        ["file-text"] = "\uE23A",
        ["x"] = "\uE4F6",
        ["plus"] = "\uE3D4",
        ["minus"] = "\uE32A",
        ["square"] = "\uE45E",
        ["check"] = "\uE182",
        ["check-circle"] = "\uE184",
        ["shield-check"] = "\uE40C",
        ["trash"] = "\uE4A6",
        ["caret-left"] = "\uE138",
        ["caret-right"] = "\uE13A",
        ["caret-up"] = "\uE13C",
        ["caret-down"] = "\uE136",
        ["eye-slash"] = "\uE224",
        ["seal"] = "\uE604",
        ["dots-six-vertical"] = "\uEAE2",
        ["arrow-up"] = "\uE08E",
        ["arrow-down"] = "\uE03E",
        ["image"] = "\uE2CA",
        ["stamp"] = "\uEA48",
        ["gear-six"] = "\uE272",
        ["arrows-horizontal"] = "\uEB06",
        ["corners-out"] = "\uE1D0",
        ["file-arrow-up"] = "\uE61E",
        ["highlighter"] = "\uEC76",
        ["note"] = "\uE348",
        ["textbox"] = "\uEB0A",
        ["line-segment"] = "\uE6D2",
        ["rectangle"] = "\uE3F0",
        ["scribble-loop"] = "\uE662",
        ["text-t"] = "\uE48A",
        ["text-underline"] = "\uE5C4",
        ["text-strikethrough"] = "\uE5C2",
        ["list-bullets"] = "\uE2F2",
        ["squares-four"] = "\uE464",
        ["book-open"] = "\uE0E6",
        ["prohibit"] = "\uE3DE",
        ["arrow-up-right"] = "\uE092",
        ["pen-nib"] = "\uE3AC",
        ["seal-check"] = "\uE606",
        ["user"] = "\uE4C2",
        ["buildings"] = "\uE102",
    };

    /// Glifo do icone nome. Lanca KeyNotFoundException (com o nome) se o icone nao esta no mapa - falha
    /// ALTA/cedo (um nome digitado errado no XAML vira erro claro, nao um quadrado tofu silencioso em
    /// runtime). Nomes novos: adicione aqui a partir dos codepoints-*.txt.
    public static string Glyph(string nome) =>
        Glifos.TryGetValue(nome, out var g)
            ? g
            : throw new KeyNotFoundException($"Icone Phosphor '{nome}' nao esta no mapa (Icons/Ph.cs). Adicione-o a partir de docs/redesign-ref/fonts/codepoints-*.txt.");
}
