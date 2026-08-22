using System.IO;
using System.Text.RegularExpressions;

namespace mPdf.App.Tests;

/// Plano 12 (Task 1) — guarda estrutural PERMANENTE contra efeitos caros entrando no tema de chrome.
/// O usuário elogiou a velocidade do app ("super rápido"); este teste é o portão que impede QUALQUER
/// mudança futura em Themes/*.xaml de reintroduzir sombra/blur/animação contínua/opacidade fracionária
/// — os 4 jeitos mais comuns de um estilo WPF ficar caro de repintar. Varre só `Themes/*.xaml` (o
/// dicionário NOVO desta task), nunca o app inteiro: `Views/PdfViewerControl.xaml` (fora do escopo —
/// "SÓ CHROME", nenhum arquivo de render/overlay tocado) já tem um `DropShadowEffect` PRÉ-EXISTENTE
/// na sombra da página (Border.Effect, linha ~60) — varrer o app inteiro faria este teste vermelho
/// desde o primeiro commit, por um efeito que não é desta task e que o brief explicitamente não pede
/// pra remover (só pede que o TEMA NOVO não adicione mais nenhum).
public class ThemeStructuralTests
{
    // Mesmo padrão de Fixtures.Root: sobe da pasta bin até achar mPdf.slnx, entra em src/mPdf.App/Themes.
    private static string ThemesDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "src", "mPdf.App", "Themes");
        }
    }

    // Os 5 tokens proibidos, exatamente como o brief especifica (Plano 12, Task 1): sombra, blur,
    // qualquer elemento de propriedade "*.Effect" (Border.Effect etc.) ou tag terminando em "Effect"
    // (DropShadowEffect/BlurEffect isolados também cairiam aqui, redundante de propósito — 2 formas de
    // pegar o mesmo problema), Storyboard disparado (animação) e Opacity cujo valor COMEÇA em "0" — isto
    // é deliberadamente amplo: "Opacity=\"0" bate tanto em "Opacity=\"0\"" quanto em qualquer fração
    // como "Opacity=\"0.4\"" (o valor exato já usado no DropShadowEffect pré-existente do overlay,
    // citado acima) — a intenção do brief é banir QUALQUER opacidade < 1, não só zero.
    private static readonly (string Rotulo, Regex Padrao)[] TokensProibidos =
    [
        ("DropShadow", new Regex("DropShadow", RegexOptions.Compiled)),
        ("BlurEffect", new Regex("BlurEffect", RegexOptions.Compiled)),
        ("<*.Effect (elemento de propriedade .Effect ou tag *Effect)", new Regex("<.*Effect", RegexOptions.Compiled)),
        ("BeginStoryboard", new Regex("BeginStoryboard", RegexOptions.Compiled)),
        ("Opacity=\"0...\" (fracionário, <1)", new Regex("Opacity=\"0", RegexOptions.Compiled)),
    ];

    // Comentário XAML `<!-- ... -->` pode atravessar várias linhas (todo o cabeçalho de
    // documentação deste próprio tema é um comentário multi-linha) — Singleline faz "." casar
    // quebra de linha também, senão um comentário de 3 linhas só teria a 1ª e a última reconhecidas
    // como delimitador e o MEIO ficaria de fora da remoção.
    private static readonly Regex ComentarioXaml = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void ThemeHasNoExpensiveEffects()
    {
        var themeFiles = Directory.GetFiles(ThemesDir, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(themeFiles); // sanity: a pasta Themes/ tem que existir e conter pelo menos mPdfTheme.xaml

        var achados = new List<string>();
        foreach (var file in themeFiles)
        {
            // Remove comentários ANTES de varrer — achado ao vivo escrevendo este teste: a própria
            // documentação em pt-BR deste arquivo CITA os tokens proibidos pelo nome (pra explicar a
            // regra), o que faria um grep ingênuo sobre o texto bruto reprovar por causa da explicação,
            // não do código. Comentários são documentação, não XAML executável — não fazem parte da
            // superfície que este teste precisa proteger.
            var semComentarios = ComentarioXaml.Replace(File.ReadAllText(file), m =>
                string.Concat(m.Value.Select(c => c == '\n' ? '\n' : ' '))); // preserva quebras de linha (números de linha corretos no relato de falha), apaga o resto
            var linhas = semComentarios.Split('\n');
            for (int i = 0; i < linhas.Length; i++)
            {
                foreach (var (rotulo, padrao) in TokensProibidos)
                {
                    if (padrao.IsMatch(linhas[i]))
                        achados.Add($"{Path.GetFileName(file)}:{i + 1}: [{rotulo}] {linhas[i].Trim()}");
                }
            }
        }

        Assert.True(achados.Count == 0,
            "Tema de chrome contém token(s) de efeito caro proibido(s) (sombra/blur/Effect/Storyboard/" +
            "opacidade fracionária) — a velocidade elogiada do app depende de repintura barata " +
            "(troca de Background sólido), não disto:\n" + string.Join("\n", achados));
    }
}
