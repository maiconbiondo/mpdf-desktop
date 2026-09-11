using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace mPdf.App.Converters;

/// Plano 14 (Task 2) — conversores usados só pelo SHELL escuro (title bar / activity rail / status bar).
/// Nenhum toca o caminho de render/overlay (fronteira SAGRADA) — são utilitários de binding de chrome.

/// Visível quando o valor NÃO é nulo (ex.: activity rail e a tira de abas só aparecem com documento
/// aberto — `SelectedDocument != null`). `Collapsed` quando nulo (não ocupa layout no estado vazio).
/// Espelha o padrão do `BooleanToVisibilityConverter` já usado no app, só que sobre presença de objeto.
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Plano 14 (Task 3) — INVERSO de `NullToVisibilityConverter`: Visível quando o valor É nulo. Usado
/// pela tela de boas-vindas/estado vazio (`WelcomeView`), que aparece SÓ quando não há documento aberto
/// (`SelectedDocument == null`). Espelho exato do de cima, sinal trocado.
public sealed class NuloParaVisibilidadeInversoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Plano 14 (Task 3) — caminho completo -> nome do arquivo (para a lista "Recentes" da tela de boas-
/// vindas: nome em destaque, caminho embaixo). `Path.GetFileName` nunca lança pra uma string qualquer.
public sealed class CaminhoParaNomeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string p && p.Length > 0 ? Path.GetFileName(p) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Plano 14 (Task 3) — caminho completo -> pasta (diretório), o subtítulo mudo de cada item de
/// "Recentes". `Path.GetDirectoryName` devolve `null` pra um caminho sem pasta (vira "").
public sealed class CaminhoParaPastaConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string p && p.Length > 0 ? (Path.GetDirectoryName(p) ?? "") : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Plano 14 (Task 3) — `OutlineNode.PageIndex` (0-based, `int?`) -> número de página 1-based mudo do
/// item do Sumário; `null` (nó sem página, ex.: "Anexos") -> "" (sem número, mesma semântica de
/// `HasPage`). StringFormat não faz +1, por isso um converter.
public sealed class PaginaMaisUmConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? (i + 1).ToString(CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Plano 14 (Task 3) — contagem de assinaturas -> rótulo pt-BR pluralizado da FAIXA de validação
/// ("1 assinatura válida" / "N assinaturas válidas"). A contagem vem de
/// `SelectedDocument.SignatureRows.Count` (todas as linhas do painel; a faixa só aparece quando o
/// documento está assinado e íntegro, ver `IsSignedDocument`). 0 (transiente, antes do refresh
/// assíncrono) cai no plural neutro sem quebrar.
public sealed class ContagemAssinaturasParaRotuloConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int n = value is int i ? i : 0;
        return n == 1 ? "1 assinatura válida" : $"{n} assinaturas válidas";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Radio-como-int: um `RadioButton` do activity rail fica marcado (`true`) quando o índice do painel
/// ativo (`TabControl.SelectedIndex`) é IGUAL ao parâmetro do botão; ao marcar, escreve esse índice de
/// volta (two-way). Ao desmarcar (outro rail selecionado), devolve `Binding.DoNothing` — só o botão que
/// virou `true` escreve, nunca o que virou `false` (padrão idiomático de radio/enum em WPF). É assim que
/// os 4 ícones do rail dirigem qual painel aparece (Miniaturas/Sumário/Campos/Assinaturas), sem
/// code-behind.
public sealed class IndiceIgualConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && parameter is not null && int.TryParse(parameter.ToString(), out var alvo) && i == alvo;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null && int.TryParse(parameter.ToString(), out var alvo)
            ? alvo
            : Binding.DoNothing;
}

/// Fix painel recolhível: Visibility do painel esquerdo de 238px = "há documento aberto" E "painel não
/// foi recolhido" (`ThumbnailsVisible`). MultiBinding com 2 entradas: [0] `SelectedDocument` (object?),
/// [1] `ThumbnailsVisible` (bool). Visível só quando AMBAS são verdadeiras — documento nulo OU painel
/// recolhido colapsam o Border; o activity rail de 58px (Visibility separada, só em `SelectedDocument`)
/// continua visível pra reabrir.
public sealed class DocumentoEPainelVisivelConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool temDocumento = values.Length > 0 && values[0] is not null;
        bool painelVisivel = values.Length > 1 && values[1] is true;
        return temDocumento && painelVisivel ? Visibility.Visible : Visibility.Collapsed;
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Índice do painel ativo (PanelTabs.SelectedIndex) -> título exibido no cabeçalho do painel esquerdo.
/// (O `TabControl` do painel usa um template sem tira de abas nativa — o activity rail é quem seleciona
/// — então `SelectedItem` não popula; o cabeçalho segue o SelectedIndex, que popula normalmente.)
public sealed class IndiceParaTituloConverter : IValueConverter
{
    private static readonly string[] Titulos = { "Miniaturas", "Sumário", "Campos", "Assinaturas" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && i >= 0 && i < Titulos.Length ? Titulos[i] : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
