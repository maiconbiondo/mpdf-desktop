using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using mPdf.App.Icons;
using Xunit;

namespace mPdf.App.Tests;

/// Plano 14 (Task 1) — o helper de ícones Phosphor (Icons/Ph.cs + PhosphorIconExtension) é a ponte
/// nome→glifo que T2–T5 vão usar. Estes testes provam que: (a) todo nome do mapa resolve num glifo de 1
/// caractere; (b) esse code point REALMENTE existe nas fontes embarcadas (nenhum "tofu"); (c) nome
/// inválido falha ALTO (KeyNotFoundException, não um quadrado silencioso); (d) a MarkupExtension devolve
/// o mesmo glifo; (e) o mapa tem os 55 ícones esperados (não encolheu por engano).
public class PhosphorIconTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }

    // União dos code points cobertos pelas 3 fontes embarcadas (regular/fill/bold) — um nome pode ser
    // exclusivo de uma variante (ex.: seal-check/user/buildings só existem no Fill).
    private static HashSet<int> CodePointsDasFontes()
    {
        var fontesDir = Path.Combine(RepoRoot, "src", "mPdf.App", "Assets", "Fonts");
        var uniao = new HashSet<int>();
        foreach (var arquivo in new[] { "Phosphor.ttf", "Phosphor-Fill.ttf", "Phosphor-Bold.ttf" })
        {
            var gt = new GlyphTypeface(new Uri(Path.Combine(fontesDir, arquivo)));
            foreach (var cp in gt.CharacterToGlyphMap.Keys) uniao.Add(cp);
        }
        return uniao;
    }

    [Fact]
    public void MapaTem55Icones()
    {
        Assert.Equal(55, Ph.Glifos.Count);
    }

    [Fact]
    public void TodoNome_ResolveGlifoDeUmCaractere()
    {
        foreach (var (nome, glifo) in Ph.Glifos)
        {
            Assert.False(string.IsNullOrEmpty(glifo), $"'{nome}' sem glifo");
            Assert.Equal(1, glifo.Length); // 1 caractere BMP (PUA U+Exxx)
            Assert.InRange((int)glifo[0], 0xE000, 0xF8FF); // Private Use Area
        }
    }

    [Fact]
    public void TodoGlifo_ExisteNasFontesEmbarcadas()
    {
        var disponiveis = CodePointsDasFontes();
        var faltando = new List<string>();
        foreach (var (nome, glifo) in Ph.Glifos)
            if (!disponiveis.Contains(glifo[0]))
                faltando.Add($"{nome} (U+{(int)glifo[0]:X4})");
        Assert.True(faltando.Count == 0, "ícones sem glifo nas fontes: " + string.Join(", ", faltando));
    }

    [Fact]
    public void Glyph_NomeConhecido_DevolveOMesmoDoMapa()
    {
        Assert.Equal(Ph.Glifos["folder-open"], Ph.Glyph("folder-open"));
        Assert.Equal(Ph.Glifos["shield-check"], Ph.Glyph("shield-check"));
    }

    [Fact]
    public void Glyph_NomeDesconhecido_LancaKeyNotFound()
    {
        Assert.Throws<KeyNotFoundException>(() => Ph.Glyph("nao-existe-esse-icone"));
    }

    [Fact]
    public void MarkupExtension_DevolveOGlifoDoNome()
    {
        var ext = new PhosphorIconExtension("folder-open");
        Assert.Equal(Ph.Glyph("folder-open"), (string)ext.ProvideValue(null!));
    }
}
