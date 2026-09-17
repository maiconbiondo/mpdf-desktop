using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace mPdf.App.Tests;

/// Plano 12 (Task 1) + Plano 14 (Task 1) — recomputo INDEPENDENTE (WCAG 2.x) de cada par texto/fundo que
/// os estilos usam, agora nos DOIS temas (Tokens.Escuro.xaml default + Tokens.Claro.xaml). Duas mudanças
/// desta task sobre o débito conhecido do teste original:
///
/// 1. LÊ OS TOKENS DE VERDADE. Em vez de hexes hardcoded (que podiam divergir do dicionário sem o teste
///    perceber), lê os DOIS arquivos de tokens (Themes/Tokens.{Escuro,Claro}.xaml — a própria fonte da
///    verdade que o app compila) e extrai a cor REAL de cada `<SolidColorBrush x:Key="Cor.*" Color=.../>`.
///    Se um token mudar de valor, o recomputo acompanha automaticamente. (Parse de TEXTO, não via WPF, de
///    propósito: nenhuma thread STA / nenhum ResourceDictionary por-caso — determinístico e sem a corrida
///    de inicialização estática do WPF que 27 threads STA concorrentes provocavam.)
///
/// 2. COMPÕE O ALPHA. Vários tokens do tema escuro são véus de branco (alpha < FF) sobre o escuro
///    (TextoSecundario .70, SuperficieHover .07, ...). O olho vê a cor RESULTANTE da composição sobre o
///    fundo real embaixo — então cada fundo é montado compondo suas camadas (base opaca + overlays) e
///    cada texto com alpha é composto sobre o fundo antes de medir. No tema claro (tudo opaco) a
///    composição é no-op e os pares batem os mesmos valores do Plano 12.
///
/// Os pares são SEMÂNTICOS (chaves de token + a pilha de fundo), não hexes — o MESMO conjunto vale pros
/// dois temas. Piso AA de texto normal = 4.5:1. TextoDesabilitado NÃO figura aqui (texto desabilitado é
/// isento do AA por WCAG 1.4.3).
public class ThemeContrastTests
{
    private readonly record struct Rgba(byte A, byte R, byte G, byte B);

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

    // Extrai key->cor de um Tokens.*.xaml lendo os <SolidColorBrush x:Key="..." Color="#..."/>.
    private static Dictionary<string, Rgba> LerTokens(string tema)
    {
        var caminho = Path.Combine(RepoRoot, "src", "mPdf.App", "Themes", $"Tokens.{tema}.xaml");
        var texto = File.ReadAllText(caminho);
        var mapa = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(texto, "x:Key=\"(Cor\\.[^\"]+)\"\\s+Color=\"(#[0-9A-Fa-f]+)\""))
            mapa[m.Groups[1].Value] = ParseHex(m.Groups[2].Value);
        Assert.True(mapa.Count >= 16, $"Tokens.{tema}.xaml: esperava >=16 tokens Cor.*, achei {mapa.Count}");
        return mapa;
    }

    // #RRGGBB (alpha FF implícito) ou #AARRGGBB.
    private static Rgba ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
            return new Rgba(0xFF, Byte(hex, 0), Byte(hex, 2), Byte(hex, 4));
        if (hex.Length == 8)
            return new Rgba(Byte(hex, 0), Byte(hex, 2), Byte(hex, 4), Byte(hex, 6));
        throw new FormatException($"hex de cor inesperado: #{hex}");
        static byte Byte(string s, int i) => Convert.ToByte(s.Substring(i, 2), 16);
    }

    private static double RelativeLuminance(Rgba c)
    {
        double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linearize(c.R) + 0.7152 * Linearize(c.G) + 0.0722 * Linearize(c.B);
    }

    private static double ContrastRatio(Rgba texto, Rgba fundo)
    {
        double l1 = RelativeLuminance(texto), l2 = RelativeLuminance(fundo);
        double lighter = Math.Max(l1, l2), darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    // "source-over": compõe `over` (com seu alpha) sobre `baixo` (tratado como opaco).
    private static Rgba Compor(Rgba over, Rgba baixo)
    {
        double a = over.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return new Rgba(0xFF, Mix(over.R, baixo.R), Mix(over.G, baixo.G), Mix(over.B, baixo.B));
    }

    private static Rgba FundoEfetivo(IReadOnlyDictionary<string, Rgba> tokens, string[] camadas)
    {
        var baseCor = tokens[camadas[0]];
        var cor = new Rgba(0xFF, baseCor.R, baseCor.G, baseCor.B); // base opaca
        for (int i = 1; i < camadas.Length; i++)
            cor = Compor(tokens[camadas[i]], cor);
        return cor;
    }

    private static readonly (string Rotulo, string TextoKey, string[] Fundo)[] Pares =
    [
        ("TextoPrimario / Superficie", "Cor.TextoPrimario", ["Cor.Superficie"]),
        ("TextoSecundario / Superficie", "Cor.TextoSecundario", ["Cor.Superficie"]),
        ("TextoPrimario / hover>Superficie (Button hover)", "Cor.TextoPrimario", ["Cor.Superficie", "Cor.SuperficieHover"]),
        ("TextoPrimario / pressed>Superficie (Button pressed)", "Cor.TextoPrimario", ["Cor.Superficie", "Cor.SuperficiePressionada"]),
        ("TextoSecundario / hover>Superficie (TabItem inativo hover)", "Cor.TextoSecundario", ["Cor.Superficie", "Cor.SuperficieHover"]),
        ("Sucesso / Superficie (SignaturePanel íntegra check)", "Cor.Sucesso", ["Cor.Superficie"]),
        ("Erro / Superficie (SignaturePanel íntegra x)", "Cor.Erro", ["Cor.Superficie"]),
        ("TextoPrimario / FundoPainel", "Cor.TextoPrimario", ["Cor.FundoPainel"]),
        ("TextoSecundario / FundoPainel", "Cor.TextoSecundario", ["Cor.FundoPainel"]),
        ("TextoPrimario / FundoJanela", "Cor.TextoPrimario", ["Cor.FundoJanela"]),
        ("TextoPrimario / FundoTitulo", "Cor.TextoPrimario", ["Cor.FundoTitulo"]),
        ("TextoPrimario / FundoRail", "Cor.TextoPrimario", ["Cor.FundoRail"]),
        ("TextoPrimario / FundoViewer", "Cor.TextoPrimario", ["Cor.FundoViewer"]),
    ];

    public static IEnumerable<object[]> ParesPorTema()
    {
        foreach (var (rotulo, textoKey, fundo) in Pares)
        {
            yield return ["Escuro", rotulo, textoKey, fundo];
            yield return ["Claro", rotulo, textoKey, fundo];
        }
    }

    [Theory]
    [MemberData(nameof(ParesPorTema))]
    public void ParTextoFundo_AtendeContrasteMinimoAA(string tema, string rotulo, string textoKey, string[] fundo)
    {
        var tokens = LerTokens(tema);
        var corFundo = FundoEfetivo(tokens, fundo);
        var corTexto = tokens[textoKey];
        if (corTexto.A < 255) corTexto = Compor(corTexto, corFundo); // texto com alpha compõe sobre o fundo
        double razao = ContrastRatio(corTexto, corFundo);
        Assert.True(razao >= 4.5,
            $"[{tema}] {rotulo}: razão {razao:F2}:1 abaixo do piso AA (4.5:1) — ajustar o TOKEN de cor no " +
            $"dicionário do tema (Themes/Tokens.{tema}.xaml), nunca ignorar este teste.");
    }

    // Achado documentado (Plano 12): branco sobre Cor.Acento (verde-água) FALHA (2.24:1) — por isso o
    // acento nunca é FUNDO atrás de texto branco. Lido do token REAL (Acento é igual nos dois temas).
    [Fact]
    public void Branco_SobreAcento_FalhaContraste_MotivoDaFaixaSerDecorativa()
    {
        var tokens = LerTokens("Escuro");
        double razao = ContrastRatio(new Rgba(0xFF, 0xFF, 0xFF, 0xFF), tokens["Cor.Acento"]);
        Assert.True(razao < 4.5,
            "Branco sobre Cor.Acento passou a atender 4.5:1 — se o token do acento mudou, revisar se algum " +
            "Setter passou a usar Cor.Acento como fundo atrás de texto branco (não deveria).");
    }
}
