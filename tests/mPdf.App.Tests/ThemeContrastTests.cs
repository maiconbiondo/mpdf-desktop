using System.Windows.Media;

namespace mPdf.App.Tests;

/// Plano 12 (Task 1) — recomputo INDEPENDENTE (WCAG 2.x relative luminance / contrast ratio) de cada
/// par texto/fundo que os estilos de Themes/mPdfTheme.xaml realmente usam. "Independente" aqui quer
/// dizer: implementação PRÓPRIA da fórmula (não reaproveita nenhum helper/script usado pra escolher a
/// paleta durante o design) — se um erro de aritmética tivesse entrado na escolha dos tokens, este
/// teste usa um caminho de cálculo separado pra pegá-lo, e vira guarda PERMANENTE (se algum token de
/// cor mudar depois e cair abaixo de 4.5:1, a suíte quebra em vez de precisar de outra rodada manual).
/// Cada par abaixo corresponde a um Foreground/Background REAL usado em algum Setter/Trigger do tema
/// (ver comentário de cada [InlineData]) — não é uma lista arbitrária de combinações da paleta.
public class ThemeContrastTests
{
    // Fórmula padrão WCAG 2.x (https://www.w3.org/TR/WCAG21/#dfn-relative-luminance), reimplementada
    // aqui direto sobre System.Windows.Media.Color (não reusa nenhum código de Themes/*.xaml nem do
    // script usado pra escolher a paleta).
    private static double RelativeLuminance(Color c)
    {
        double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linearize(c.R) + 0.7152 * Linearize(c.G) + 0.0722 * Linearize(c.B);
    }

    private static double ContrastRatio(string hex1, string hex2)
    {
        var c1 = (Color)ColorConverter.ConvertFromString(hex1)!;
        var c2 = (Color)ColorConverter.ConvertFromString(hex2)!;
        double l1 = RelativeLuminance(c1), l2 = RelativeLuminance(c2);
        double lighter = Math.Max(l1, l2), darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    // Par (rótulo, hexTexto, hexFundo) — hex exatamente como gravado em Themes/mPdfTheme.xaml.
    // AAA de texto normal é 7:1, AA (o piso exigido pelo brief) é 4.5:1 — todos os pares abaixo
    // precisam bater >= 4.5:1.
    public static IEnumerable<object[]> ParesUsadosNoTema =>
    [
        // TabItem inativo (ocioso) e MenuItem/Menu — Foreground=Cor.TextoSecundario, fundo=Cor.Superficie
        ["TextoSecundario/Superficie (TabItem inativo, Menu)", "#FF52606D", "#FFF5F7FA"],
        // TabItem selecionado — Foreground=Cor.TextoPrimario, fundo vira White (Border "Corpo" trigger IsSelected)
        ["TextoPrimario/White (TabItem selecionado)", "#FF0F2E52", "#FFFFFFFF"],
        // Menu/MenuItem/GroupBox — Foreground=Cor.TextoPrimario, fundo=Cor.Superficie (Menu.Background)
        ["TextoPrimario/Superficie (Menu, MenuItem, GroupBox)", "#FF0F2E52", "#FFF5F7FA"],
        // Button.Toolbar/Dialog em hover — Foreground=Cor.TextoPrimario (herda), fundo troca pra Cor.SuperficieHover
        ["TextoPrimario/SuperficieHover (Button hover)", "#FF0F2E52", "#FFE3ECF5"],
        // Button.Toolbar/Dialog pressionado — fundo troca pra Cor.SuperficiePressionada
        ["TextoPrimario/SuperficiePressionada (Button pressed)", "#FF0F2E52", "#FFCFE0F0"],
        // TabItem inativo com mouse em cima (hover, ainda não selecionado) — mesmo Foreground=TextoSecundario, fundo=SuperficieHover
        ["TextoSecundario/SuperficieHover (TabItem inativo hover)", "#FF52606D", "#FFE3ECF5"],
        // Button.Dialog ocioso — Foreground=Cor.TextoPrimario (herdado), fundo=Cor.Superficie
        ["TextoPrimario/Superficie (Button.Dialog ocioso)", "#FF0F2E52", "#FFF5F7FA"],
        // Plano 12 (Task 2, rider): SignaturePanel.IntegrityLabel (✔ Íntegra) — Foreground=Cor.Sucesso,
        // fundo=Cor.Superficie (painel interior agora claro, era #FF3C3F41 escuro antes do rider).
        ["Sucesso/Superficie (SignaturePanel IntegrityLabel válido)", "#FF2E7D32", "#FFF5F7FA"],
        // Plano 12 (Task 2, rider): SignaturePanel.IntegrityLabel (✖ Íntegra) — Foreground=Cor.Erro,
        // fundo=Cor.Superficie.
        ["Erro/Superficie (SignaturePanel IntegrityLabel inválido)", "#FFB00020", "#FFF5F7FA"],
    ];

    [Theory]
    [MemberData(nameof(ParesUsadosNoTema))]
    public void ParTextoFundo_AtendeContrasteMinimoAA(string rotulo, string hexTexto, string hexFundo)
    {
        double razao = ContrastRatio(hexTexto, hexFundo);
        Assert.True(razao >= 4.5,
            $"{rotulo}: razão de contraste {razao:F2}:1 abaixo do piso AA (4.5:1) — ajustar o TOKEN " +
            "de cor no tema (Themes/mPdfTheme.xaml), nunca ignorar este teste.");
    }

    // Achado documentado durante o design (Task 1): branco sobre Cor.Acento (#FF3EC1A7, verde-água)
    // FALHA feio (2.24:1) — por isso o acento nunca aparece como FUNDO atrás de texto branco no tema
    // (a aba ativa usa o acento só como faixa decorativa fina embaixo, nunca como preenchimento com
    // texto por cima). Este teste é o NEGATIVO que prova a decisão de design: se algum dia um Setter
    // futuro tentar "Foreground=White, Background=Cor.Acento", o par abaixo confirma que ele FALHARIA
    // — documentação viva do motivo de a faixa ser decorativa, não preenchimento com texto.
    [Fact]
    public void Branco_SobreAcento_FalhaContraste_MotivoDaFaixaSerDecorativa()
    {
        double razao = ContrastRatio("#FFFFFFFF", "#FF3EC1A7");
        Assert.True(razao < 4.5,
            "Branco sobre Cor.Acento passou a atender 4.5:1 — se o token do acento mudou, revisar se " +
            "algum Setter novo passou a usar Cor.Acento como fundo atrás de texto branco (não deveria).");
    }
}
