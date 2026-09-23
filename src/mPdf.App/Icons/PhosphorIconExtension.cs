using System;
using System.Windows.Markup;

namespace mPdf.App.Icons;

/// Plano 14 (Task 1) — MarkupExtension que resolve um NOME de ícone Phosphor no seu glifo, pra usar
/// direto no XAML: `<TextBlock FontFamily="{DynamicResource Fonte.Phosphor}" Text="{icons:PhosphorIcon
/// folder-open}"/>`. A VARIANTE (regular/fill/bold) é escolhida pela FontFamily do elemento
/// (Fonte.Phosphor / Fonte.PhosphorFill / Fonte.PhosphorBold), não por esta extensão — o glifo (code
/// point) é o mesmo nas 3. Retorna `string` (o caractere), então serve em qualquer propriedade string
/// (TextBlock.Text, Run.Text, Button.Content...). Nome inválido -> `KeyNotFoundException` de `Ph.Glyph`
/// (falha alta/cedo, ver Ph.cs), nunca um tofu silencioso.
[MarkupExtensionReturnType(typeof(string))]
public sealed class PhosphorIconExtension : MarkupExtension
{
    public string Name { get; set; } = "";

    public PhosphorIconExtension() { }

    public PhosphorIconExtension(string name) => Name = name;

    public override object ProvideValue(IServiceProvider serviceProvider) => Ph.Glyph(Name);
}
