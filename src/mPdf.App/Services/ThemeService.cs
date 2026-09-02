using System;
using System.Collections.ObjectModel;
using System.Windows;
using mPdf.Documents;
// .NET 10 introduziu System.Windows.ThemeMode (tema Fluent do WPF) — colide com o nosso
// mPdf.Documents.ThemeMode. Alias resolve a ambiguidade sem renomear o enum do domínio.
using ThemeMode = mPdf.Documents.ThemeMode;

namespace mPdf.App.Services;

/// Plano 14 (Task 1) — troca o TEMA (escuro/claro) AO VIVO, sem reiniciar, substituindo o dicionário de
/// tokens de cor ATIVO mesclado numa coleção de MergedDictionaries. A estrutura (mPdfTheme.xaml)
/// referencia cada cor por {DynamicResource Cor.*}; trocar o dicionário de tokens re-dispara essas
/// referências e o WPF re-pinta tudo que as usa (o mesmo mecanismo que faz o tema escuro ser o default
/// e o toggle de Sobre alternar pro claro e voltar).
///
/// TESTÁVEL sem `Application` (estado ESTÁTICO de processo, que xUnit paralelizaria — ver
/// ThemeVisualProbeTests): o construtor recebe a coleção-ALVO de MergedDictionaries (em produção, a de
/// `Application.Current.Resources`; numa sonda STA, a de uma Window). `AplicarNoApp` é o atalho de
/// produção. `Aplicar` é idempotente: sempre remove QUALQUER `Tokens.*` presente antes de inserir o
/// novo, então chamar duas vezes o mesmo tema não acumula dicionários.
public sealed class ThemeService
{
    // Pack URIs dos dois dicionários de tokens (compilados como recursos de mPdf.App). Absolutos pra
    // resolver independentemente de qual assembly (app vs. teste) dispara o merge.
    private const string UriEscuro = "pack://application:,,,/mPdf.App;component/Themes/Tokens.Escuro.xaml";
    private const string UriClaro = "pack://application:,,,/mPdf.App;component/Themes/Tokens.Claro.xaml";

    // Marca que identifica um dicionário de tokens NESTA coleção (pelo Source), pra remover o antigo sem
    // tocar em mPdfTheme.xaml nem em nenhum outro merge. "Tokens." casa Tokens.Escuro/Tokens.Claro e
    // nada mais (nenhum outro dicionário do app tem "Tokens." no caminho).
    private const string MarcaTokens = "Tokens.";

    private readonly Collection<ResourceDictionary> _alvo;

    public ThemeService(Collection<ResourceDictionary> mergedDictionariesAlvo)
    {
        _alvo = mergedDictionariesAlvo ?? throw new ArgumentNullException(nameof(mergedDictionariesAlvo));
    }

    /// Atalho de produção: opera sobre `Application.Current.Resources.MergedDictionaries`.
    public static void AplicarNoApp(ThemeMode modo) =>
        new ThemeService(Application.Current.Resources.MergedDictionaries).Aplicar(modo);

    /// Remove o dicionário de tokens atual (qualquer `Tokens.*`) e insere o do `modo` no ÍNDICE 0 (antes
    /// de mPdfTheme.xaml, mesma posição que App.xaml usa) — as chaves `Cor.*` são disjuntas de
    /// mPdfTheme, então a posição é apenas por clareza; o que importa é o dicionário ativo existir na
    /// coleção pros {DynamicResource Cor.*} resolverem.
    public void Aplicar(ThemeMode modo)
    {
        for (int i = _alvo.Count - 1; i >= 0; i--)
        {
            var src = _alvo[i].Source;
            if (src is not null && src.OriginalString.Contains(MarcaTokens))
                _alvo.RemoveAt(i);
        }

        var uri = modo == ThemeMode.Claro ? UriClaro : UriEscuro;
        _alvo.Insert(0, new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
    }
}
