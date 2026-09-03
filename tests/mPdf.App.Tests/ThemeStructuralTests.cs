using System.IO;
using System.Text.RegularExpressions;

namespace mPdf.App.Tests;

/// Plano 12 (Task 1) → Plano 14 (Task 1) — guarda estrutural RECONCILIADA contra efeitos caros.
///
/// MUDANÇA DE ESCOPO (documentada): o Plano 12 baniu Effect/sombra/animação/opacidade fracionária no
/// TEMA DE CHROME (Themes/*.xaml), porque naquele momento o chrome era plano e a única sombra do app era
/// a da página (pré-existente, em Views/PdfViewerControl.xaml). O redesenho do Plano 14 USA elevação no
/// chrome (diálogos/cards/title bar ganham sombra em T2–T5) — então proibir sombra no chrome deixou de
/// fazer sentido. O que continua importando pra PERFORMANCE (a velocidade elogiada) é o CAMINHO DE
/// RENDER por página não ganhar efeito NOVO: cada página é materializada/repintada na lista virtualizada,
/// e um Effect/blur/animação a mais ALI multiplica por página. Então a proibição MIGRA de "todo o tema de
/// chrome" para "o caminho de render por página" (Views/PdfViewerControl.xaml, que hospeda o
/// ItemTemplate/Border de CADA página).
///
/// BASELINE PRÉ-EXISTENTE, PERMITIDO E FIXADO: o PageBorder de cada página já tem UMA
/// DropShadowEffect (a sombra sutil do papel, BlurRadius=6/Opacity=0.4) desde antes do Plano 12 — o
/// brief nunca pediu pra removê-la, só que nada NOVO se some a ela. A guarda abaixo prova exatamente
/// isso: EXATAMENTE 1 DropShadowEffect e 1 elemento-de-propriedade `.Effect` no arquivo (a sombra da
/// página), ZERO BlurEffect e ZERO BeginStoryboard. Adicionar uma 2ª sombra, um blur, uma animação ou
/// qualquer outro `*.Effect` no caminho de render faz a contagem divergir do baseline e o teste QUEBRA —
/// é a rede que garante que a virada visual não vaze custo pro hot path por página.
public class ThemeStructuralTests
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

    // Comentário XAML pode ser multi-linha — removido antes de contar (senão esta doc, ou os comentários
    // do próprio PdfViewerControl.xaml que citam "DropShadowEffect" pelo nome, contariam).
    private static readonly Regex ComentarioXaml = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string SemComentarios(string caminho) =>
        ComentarioXaml.Replace(File.ReadAllText(caminho), "");

    [Fact]
    public void RenderPath_PdfViewerControl_NaoGanhaEfeitoNovoAlemDaSombraDaPagina()
    {
        var arquivo = Path.Combine(RepoRoot, "src", "mPdf.App", "Views", "PdfViewerControl.xaml");
        Assert.True(File.Exists(arquivo), $"caminho de render não encontrado: {arquivo}");
        var xaml = SemComentarios(arquivo);

        int dropShadows = Regex.Matches(xaml, "DropShadowEffect").Count;
        int blurs = Regex.Matches(xaml, "BlurEffect").Count;
        int storyboards = Regex.Matches(xaml, "BeginStoryboard").Count;
        // Elemento-de-propriedade `<Algo.Effect>` (ex.: <Border.Effect>) — o "gancho" por onde um Effect
        // entra na árvore por página. `<.*Effect` isolado também pegaria a tag <DropShadowEffect>, então
        // ancoro num ponto ANTES de "Effect" pra contar só os elementos-de-propriedade `.Effect`.
        int propEffects = Regex.Matches(xaml, "<[A-Za-z0-9]+\\.Effect\\b").Count;

        // Baseline documentado: 1 sombra de página (1 DropShadowEffect dentro de 1 <Border.Effect>).
        Assert.Equal(1, dropShadows);
        Assert.Equal(1, propEffects);
        // Nada NOVO além dela.
        Assert.Equal(0, blurs);
        Assert.Equal(0, storyboards);
    }

    // Sanidade da própria rede: o caminho de render existe e realmente contém o baseline que afirmamos —
    // se alguém REMOVER a sombra da página (baseline vira 0), este teste também chama atenção (a
    // contagem esperada mudaria e o teste acima quebraria), forçando uma atualização CONSCIENTE do
    // baseline em vez de um drift silencioso.
    [Fact]
    public void RenderPath_Existe_ComOBaselineEsperado()
    {
        var arquivo = Path.Combine(RepoRoot, "src", "mPdf.App", "Views", "PdfViewerControl.xaml");
        Assert.True(File.Exists(arquivo));
        Assert.Contains("DropShadowEffect", SemComentarios(arquivo));
    }
}
