using System.IO;
using System.Text.RegularExpressions;

namespace mPdf.App.Tests;

/// Task 2 (Plano 11) — REDE SÓ POR CLIQUE (restrição estrutural do plano, ZERO rede em background): a
/// ÚNICA chamada de rede do app inteiro vive atrás do clique explícito em "Verificar atualização" no
/// `SobreDialog`. Duas guardas textuais aqui, MESMO padrão de varredura de
/// `mPdf.Editing.Tests.AgplGuardTests` (linha a linha, arquivo por arquivo — checagem pragmática,
/// documentada como tal, não um parser de C# completo):
///
///   1. nenhum token da família de rede do BCL existe em `src/mPdf.App` fora de
///      `Services/UpdateService.cs` — o ÚNICO arquivo autorizado a tocar rede (`GitHubUpdateSource`, a
///      implementação REAL de `IUpdateSource`, mora no MESMO arquivo por design — ver doc XML do
///      cabeçalho de `UpdateService.cs`, "toda a rede vive aqui"). Revisão de segurança (achado ao
///      vivo do revisor): a lista original (`HttpClient`/`WebRequest`) deixava passar `WebClient`,
///      `Socket`/`TcpClient` (namespace `System.Net.Sockets`) e `Dns` — QUALQUER um desses também abre
///      uma conexão de rede fora da fronteira pretendida. Lista ampliada pra família REALISTA do BCL:
///      `HttpClient`, `WebRequest` (cobre `HttpWebRequest` como substring), `WebClient`, `Socket`,
///      `TcpClient`, `Dns`. `Contains` simples de propósito (não uma âncora "new "): qualquer MENÇÃO ao
///      tipo (não só construção) já é suspeita fora do arquivo autorizado — inclusive pega
///      automaticamente formas qualificadas (`System.Net.Http.HttpClient` contém "HttpClient").
///      RESÍDUO ACEITO, documentado (não perseguido pela varredura): `Activator.CreateInstance`/
///      reflexão pura sobre esses tipos, ou uma DLL nativa P/Invoke fazendo I/O de rede sem usar
///      nenhum tipo gerenciado do BCL — exóticos o bastante pra não valer a complexidade de detectar
///      por texto (mesma disciplina de resíduo aceito já registrada em `AgplGuardTests`: "checagem
///      pragmática por linha, não um parser de C#/XML completo").
///   2. o construtor de `UpdateService` aparece em EXATAMENTE 1 lugar em todo `src/mPdf.App` —
///      `SobreViewModel.VerificarAtualizacao` (o comando "Verificar atualização"), o ÚNICO ponto que
///      constrói o serviço; o comando de download subsequente REUSA essa mesma instância via campo,
///      nunca reconstrói. Revisão de segurança (2º achado ao vivo do revisor): um `using U =
///      mPdf.App.Services.UpdateService;` seguido de `new U(...)` escapava da regex original (que só
///      reconhecia o NOME LITERAL `UpdateService`, qualificado ou não — nunca um ALIAS arbitrário).
///      `DetectUpdateServiceAliasNames` faz uma 1ª passada sobre TODOS os arquivos coletando qualquer
///      diretiva `using X = [...]UpdateService;`/`global using X = [...]UpdateService;` — o conjunto de
///      nomes resultante ({"UpdateService"} ∪ aliases encontrados) alimenta a MESMA regex ancorada em
///      "new" + qualificador opcional + (um dos nomes). Um `Contains` ingênuo teria falsos NEGATIVOS
///      (outros métodos ESTÁTICOS legítimos como `UpdateService.CurrentVersionText()`/
///      `UpdateService.VerifyAndFinalize(...)` são chamados de fora — não podem disparar a guarda) e
///      falsos POSITIVOS (`BatchUpdateService` conteria "UpdateService" como substring) — por isso
///      continua sendo uma REGEX ancorada, nunca um `Contains` cru (ver prova de CAPACIDADE abaixo,
///      fixture literal). RESÍDUO ACEITO, documentado: `Activator.CreateInstance(typeof(UpdateService), ...)`
///      via reflexão — exótico o bastante (ninguém escreve isso por acidente; é um esforço deliberado
///      de ofuscação) pra não valer complicar o detector além do que uma varredura textual consegue.
///
/// PROVA DE DISPARO AO VIVO (task-2-report.md documenta a íntegra, incl. os 2 achados do revisor que
/// motivaram os reforços acima): plantar `new System.Net.Http.HttpClient()` dentro de
/// `MainViewModel.Sobre()` -> guarda 1 reprova nomeando arquivo:linha; restaurado. Plantar
/// `new mPdf.App.Services.UpdateService(null!)` num método novo de `MainWindow.xaml.cs` -> guarda 2
/// reprova nomeando os 2 arquivos (2 ocorrências); restaurado. Replantios pós-revisão: `new
/// System.Net.WebClient()` -> guarda 1 (ampliada) reprova; `using U = ...UpdateService; new U(source)`
/// -> guarda 2 (com detecção de alias) reprova; ambos restaurados.
///
/// Complementado por `SobreViewModelTests` (prova COMPORTAMENTAL, não textual): um spy no lugar da
/// fábrica `Func&lt;IUpdateSource&gt;` prova que ZERO chamadas acontecem só de construir o VM/abrir o
/// diálogo — só o comando "Verificar atualização" invoca a fábrica.
public class UpdateNetworkConfinementTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static string AppSrcRoot => Path.Combine(RepoRoot, "src", "mPdf.App");

    private static bool IsBuildArtifact(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("obj") || parts.Contains("bin");
    }

    private static IEnumerable<string> ScannableFiles() =>
        Directory.EnumerateFiles(AppSrcRoot, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildArtifact(f));

    [Fact]
    public void ScanIsNotEmpty() // piso de varredura vazia — mesmo exemplar de AgplGuardTests.SrcTree_ScanIsNotEmpty
    {
        int count = ScannableFiles().Count();
        Assert.True(count >= 15, $"varredura vazia ou quase vazia ({count} arquivo(s)) = guarda morta");
    }

    // ---- guarda 1: família de rede do BCL confinada a UpdateService.cs --------------------------------

    // Revisão de segurança: ampliada de {"HttpClient","WebRequest"} pra família realista — WebClient
    // (classe legada mas ainda funcional), Socket/TcpClient (System.Net.Sockets, rede de baixo nível),
    // Dns (resolução de nome já é, em si, uma operação de rede). "WebRequest" já cobre "HttpWebRequest"
    // como substring (nenhuma entrada dedicada precisa).
    private static readonly string[] NetworkTokens = { "HttpClient", "WebRequest", "WebClient", "Socket", "TcpClient", "Dns" };

    [Fact]
    public void NoNetworkTokenOutsideUpdateServiceFile()
    {
        string allowedFile = Path.Combine(AppSrcRoot, "Services", "UpdateService.cs");
        var offenders = new List<string>();

        foreach (var file in ScannableFiles())
        {
            if (string.Equals(file, allowedFile, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var (lineNumber, line) in File.ReadLines(file).Select((l, i) => (i + 1, l)))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//")) continue; // comentário de linha inteira: resíduo aceito
                string? hit = NetworkTokens.FirstOrDefault(trimmed.Contains);
                if (hit is not null)
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{lineNumber}: [{hit}] {line.Trim()}");
                    break; // 1 ocorrência já basta para nomear o arquivo
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Rede fora da fronteira: token de rede do BCL (" + string.Join("/", NetworkTokens) +
            ") encontrado fora de Services/UpdateService.cs (a rede só pode ser tocada de dentro do " +
            "arquivo que hospeda UpdateService/GitHubUpdateSource):\n" + string.Join("\n", offenders));
    }

    // ---- guarda 1b (Plano 15): mPdf.Ocr — OFFLINE TOTAL, sem arquivo autorizado ----------------------

    // O assembly novo mPdf.Ocr (motor Tesseract) precisa ser VARRIDO por esta guarda também, senão um
    // projeto novo escapa da confinação de rede só por não estar sob src/mPdf.App. Diferente de
    // mPdf.App (que tem UpdateService.cs como ÚNICO arquivo autorizado), mPdf.Ocr NÃO tem nenhum
    // arquivo autorizado a tocar rede — o Tesseract é nativo/local; qualquer token de rede aqui é
    // ofensor. Prova de disparo (task-1-report.md): plantar `new System.Net.Http.HttpClient()` em
    // TesseractOcrEngine.cs -> NoNetworkTokenInOcrModule reprova nomeando arquivo:linha; restaurado.
    private static string OcrSrcRoot => Path.Combine(RepoRoot, "src", "mPdf.Ocr");

    private static IEnumerable<string> OcrScannableFiles() =>
        Directory.EnumerateFiles(OcrSrcRoot, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildArtifact(f));

    [Fact]
    public void OcrScanIsNotEmpty() // piso de varredura vazia do assembly novo — capability vs coverage
    {
        int count = OcrScannableFiles().Count();
        Assert.True(count >= 3, $"varredura de mPdf.Ocr vazia ou quase vazia ({count} arquivo(s)) = guarda morta");
    }

    [Fact]
    public void NoNetworkTokenInOcrModule()
    {
        var offenders = new List<string>();

        foreach (var file in OcrScannableFiles())
        {
            foreach (var (lineNumber, line) in File.ReadLines(file).Select((l, i) => (i + 1, l)))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//")) continue; // comentário de linha inteira: resíduo aceito
                string? hit = NetworkTokens.FirstOrDefault(trimmed.Contains);
                if (hit is not null)
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{lineNumber}: [{hit}] {line.Trim()}");
                    break; // 1 ocorrência já basta para nomear o arquivo
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "OFFLINE (Plano 15): mPdf.Ocr NÃO pode tocar rede — o motor Tesseract é nativo/local, " +
            "nenhum arquivo é autorizado. Token de rede do BCL (" + string.Join("/", NetworkTokens) +
            ") encontrado em mPdf.Ocr:\n" + string.Join("\n", offenders));
    }

    // ---- guarda 2: UpdateService construído em exatamente 1 lugar (com detecção de alias) -------------

    // Diretiva de alias — `using X = [global::][ns.]*UpdateService;` OU `global using X = ...;` (C# 10+,
    // válido em qualquer arquivo, efeito no PROJETO INTEIRO — por isso a varredura real, abaixo, coleta
    // aliases de TODOS os arquivos antes de escanear qualquer um).
    private static readonly Regex UpdateServiceAliasDirectiveRegex = new(
        @"^\s*(?:global\s+)?using\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*UpdateService\s*;",
        RegexOptions.Compiled);

    private static HashSet<string> DetectAliasNames(IEnumerable<string> lines)
    {
        var names = new HashSet<string> { "UpdateService" };
        foreach (var line in lines)
        {
            var m = UpdateServiceAliasDirectiveRegex.Match(line);
            if (m.Success) names.Add(m.Groups[1].Value);
        }
        return names;
    }

    // Ancorado em "new" + qualificador de namespace OPCIONAL (0+ segmentos "Identificador.") + um dos
    // NOMES conhecidos (base + aliases detectados) + "(": reconhece a forma canônica, a qualificada
    // (`new mPdf.App.Services.UpdateService(`, `new global::mPdf.App.Services.UpdateService(`) E
    // qualquer alias detectado (`new U(` quando `using U = ...UpdateService;` existe em algum arquivo),
    // sem disparar em tipos NÃO relacionados que só compartilham o sufixo "UpdateService"
    // (`BatchUpdateService`) nem em MENÇÕES que não são construção (`UpdateService.CurrentVersionText()`).
    private static int CountConstructions(IEnumerable<string> aliasNames, string line)
    {
        string alternation = string.Join("|", aliasNames.Select(Regex.Escape));
        var regex = new Regex($@"(?<![\w.])new\s+(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*(?:{alternation})\s*\(");
        return regex.Matches(line).Count;
    }

    private static int ConstructsUpdateServiceMatchCount(string line) => CountConstructions(new[] { "UpdateService" }, line);

    [Fact]
    public void UpdateServiceConstruction_OccursInExactlyOnePlace()
    {
        var allFiles = ScannableFiles().ToList();

        // 1ª passada: coleta TODO alias `UpdateService` de TODOS os arquivos (um `global using` em
        // QUALQUER arquivo vale pro projeto inteiro — nunca assuma que o alias só importa no arquivo
        // que o declara).
        var aliasNames = new HashSet<string> { "UpdateService" };
        foreach (var file in allFiles)
            foreach (var alias in DetectAliasNames(File.ReadLines(file)))
                aliasNames.Add(alias);

        var offenders = new List<string>();
        int totalOccurrences = 0;

        foreach (var file in allFiles)
        {
            foreach (var (lineNumber, line) in File.ReadLines(file).Select((l, i) => (i + 1, l)))
            {
                if (line.TrimStart().StartsWith("//")) continue;
                int count = CountConstructions(aliasNames, line);
                if (count == 0) continue;
                totalOccurrences += count;
                offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{lineNumber}: {line.Trim()}");
            }
        }

        Assert.True(totalOccurrences == 1,
            "UpdateService deveria ser construído em EXATAMENTE 1 lugar (dentro do comando 'Verificar " +
            "atualização' de SobreViewModel — o comando de download reusa a mesma instância via campo, " +
            $"nunca reconstrói; aliases considerados: {string.Join(", ", aliasNames)}) — encontrado " +
            $"{totalOccurrences}x:\n" + string.Join("\n", offenders));
        Assert.Contains(offenders, o => o.Contains("SobreViewModel.cs"));
    }

    // --- prova de CAPACIDADE dos detectores (fixture literal, sem tocar arquivo nenhum em src/) --------

    [Theory]
    [InlineData("var x = new UpdateService(source);")]                              // canônica
    [InlineData("var x = new  UpdateService(source);")]                             // espaço extra
    [InlineData("var x = new mPdf.App.Services.UpdateService(source);")]            // qualificada
    [InlineData("var x = new global::mPdf.App.Services.UpdateService(source);")]    // qualificada + global::
    public void ConstructsUpdateServiceMatchCount_RecognizesEquivalentSpellings(string line)
    {
        Assert.True(ConstructsUpdateServiceMatchCount(line) > 0, $"detector cego para a grafia: {line}");
    }

    [Theory]
    [InlineData("var x = new BatchUpdateService(source);")]        // tipo NÃO relacionado, mesmo sufixo
    [InlineData("var x = SomeHelper.UpdateService(source);")]      // menção sem "new" — chamada estática
    [InlineData("var text = UpdateService.CurrentVersionText();")] // menção de tipo, não construção
    public void ConstructsUpdateServiceMatchCount_DoesNotFlagUnrelatedTypesOrMentions(string line)
    {
        Assert.Equal(0, ConstructsUpdateServiceMatchCount(line));
    }

    // Comentário de linha inteira: o detector PURO (acima) não entende comentários — quem filtra é a
    // varredura real (`if (line.TrimStart().StartsWith("//")) continue;`, ANTES de chamar o detector).
    [Fact]
    public void ConstructsUpdateServiceMatchCount_AloneDoesNotUnderstandComments_ScanLoopFiltersInstead()
    {
        Assert.True(ConstructsUpdateServiceMatchCount("// var x = new UpdateService(source);") > 0,
            "o detector puro NÃO filtra comentário — é a varredura real (TrimStart().StartsWith(\"//\")) que filtra");
    }

    // --- prova de CAPACIDADE: detecção de ALIAS (achado ao vivo do revisor) ----------------------------

    [Theory]
    [InlineData("using U = mPdf.App.Services.UpdateService;", "U")]
    [InlineData("using Foo = global::mPdf.App.Services.UpdateService;", "Foo")]
    [InlineData("global using Bar = mPdf.App.Services.UpdateService;", "Bar")]
    [InlineData("    using Baz = UpdateService;", "Baz")] // indentado, sem qualificação de namespace
    public void DetectAliasNames_RecognizesAliasDirective(string directiveLine, string expectedAlias)
    {
        Assert.Contains(expectedAlias, DetectAliasNames(new[] { directiveLine }));
    }

    [Fact]
    public void DetectAliasNames_UnrelatedAliasOrNoDirective_OnlyBaseNamePresent()
    {
        Assert.Equal(new HashSet<string> { "UpdateService" }, DetectAliasNames(new[] { "var x = 1;" }));
        Assert.Equal(new HashSet<string> { "UpdateService" }, DetectAliasNames(new[] { "using Foo = System.String;" }));
    }

    [Fact] // fixture end-to-end: diretiva de alias + construção via alias, mesmo "arquivo" (lista de linhas)
    public void CountConstructions_WithDetectedAlias_CatchesAliasedConstruction()
    {
        var lines = new[] { "using U = mPdf.App.Services.UpdateService;", "var x = new U(source);" };
        var aliases = DetectAliasNames(lines);

        int total = lines.Sum(l => CountConstructions(aliases, l));

        Assert.Equal(1, total);
    }

    [Fact] // controle negativo: alias pra um tipo NÃO relacionado não deveria fazer "new Foo()" disparar
    public void CountConstructions_UnrelatedAlias_DoesNotFlagConstruction()
    {
        var lines = new[] { "using Foo = System.String;", "var x = new Foo();" };
        var aliases = DetectAliasNames(lines);

        int total = lines.Sum(l => CountConstructions(aliases, l));

        Assert.Equal(0, total);
    }
}
