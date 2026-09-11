using System.IO;

namespace mPdf.Editing.Tests;

/// GUARDA AGPL (Plano 3a, Task 2 — a task que traz iText para dentro do codebase da app pela primeira
/// vez, isolado em src/mPdf.Editing/; revisão pós-M11/I1-I2/M12 adicionou a varredura de .csproj, o
/// piso de varredura vazia e a prova de capacidade separada). Varre `src/**/*.cs` E `src/**/*.csproj`
/// e reprova nomeando o arquivo:linha se encontrar `using iText`/`iText.` (ou, em .csproj,
/// `<PackageReference>`/`<Using>` citando "iText") fora de `src/mPdf.Editing/`, ou o mesmo para
/// `Docnet` fora de `src/mPdf.Rendering/` — as duas dependências com implicação de licenciamento
/// (AGPL) deste projeto precisam ficar cada uma atrás de UMA fronteira só, nunca espalhadas.
///
/// Esta é a camada de FONTE da fronteira AGPL. A camada de ARTEFATO/compilador é o
/// `PrivateAssets=compile` em `src/mPdf.Editing/mPdf.Editing.csproj` (I1): o compilador recusa
/// `using iText` em src/mPdf.App antes mesmo desta varredura rodar — ver prova de disparo em
/// task-2-report.md ("## Fix"). As duas camadas são complementares: a do compilador é mais forte
/// (impede o bind do tipo), mas só cobre `src/mPdf.App` (o único projeto com a referência de
/// projeto para mPdf.Editing); esta varredura cobre TODO `src/` e pega qualquer outro projeto novo
/// que um dia ganhe a mesma referência sem o PrivateAssets equivalente.
///
/// Escopo: só src/ (poc/ é permitido pelo spec — é onde o iText foi originalmente reconciliado, fora
/// do produto). Resíduo aceito: linhas de comentário inteiras (`//` em .cs, `<!--` em .csproj)
/// mencionando os tokens em texto livre NÃO disparam a guarda — só disparamos em uso de código/
/// declaração real. Checagem pragmática por linha, não um parser de C#/XML completo: uma linha de
/// CÓDIGO que também tivesse um comentário à direita mencionando o token escaparia da checagem de
/// "// no início"; não existe hoje nenhuma linha assim no repo (checado à mão) e a prova de disparo
/// (compilador + varredura) cobre o caso real (using-directive plantada em arquivo do App).
public class AgplGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    // Plano 4, Task 1: mPdf.Signing entra no allowlist ao lado de mPdf.Editing — 2º módulo que
    // referencia iText de propósito (assinatura PAdES), mesma fronteira (interfaces neutras em
    // Contract.cs, PrivateAssets=compile no .csproj). Prova de disparo NAS DUAS direções (plantar fora
    // de Editing/Signing -> reprova nomeando; plantar em Signing -> passa) no relatório da task.
    public void SrcTree_NoStrayItextOutsideEditingOrSigningProjects()
    {
        AssertNoStrayToken("iText",
            Path.Combine("src", "mPdf.Editing"), Path.Combine("src", "mPdf.Signing"));
    }

    [Fact]
    public void SrcTree_NoStrayDocnetOutsideRenderingProject()
    {
        AssertNoStrayToken("Docnet", Path.Combine("src", "mPdf.Rendering"));
    }

    /// Piso de varredura vazia (I2, guard-rails law: "lista vazia tratada como caminho feliz" é o
    /// antipadrão clássico de guarda morta). SEPARADO da asserção de "sem ofensores" acima de
    /// propósito: as duas juntas não distinguem "nada de errado encontrado" de "a varredura não
    /// encontrou NADA para examinar" — um bug que fizesse `Directory.EnumerateFiles` devolver vazio
    /// (raiz errada, extensão errada, etc.) passaria pelas 2 asserções de offenders com verde falso.
    /// 62 arquivos existem hoje (57 .cs + 5 .csproj em src/ — medição refeita no Plano 4 Task 1, que
    /// somou src/mPdf.Signing/; o comentário anterior citava 52 = 48+4, já defasado pelo crescimento
    /// do código); 15 é uma margem folgada que ainda reprova se a varredura degenerar para "quase nada".
    [Fact]
    public void SrcTree_ScanIsNotEmpty()
    {
        var srcRoot = Path.Combine(RepoRoot, "src");
        int scanned = CountScannableFiles(srcRoot);
        Assert.True(scanned >= 15, $"varredura vazia ou quase vazia ({scanned} arquivo(s)) = guarda morta");
    }

    private static int CountScannableFiles(string srcRoot) =>
        Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories).Count(f => !IsBuildArtifact(f)) +
        Directory.EnumerateFiles(srcRoot, "*.csproj", SearchOption.AllDirectories).Count(f => !IsBuildArtifact(f));

    /// `allowedRelativeDirs`: 1+ diretórios (relativos à raiz do repo) onde o token é permitido — um
    /// arquivo dentro de QUALQUER um deles é pulado; fora de todos, é varrido normalmente. `params`
    /// pra manter a chamada de `Docnet` (1 diretório) idêntica de antes sem overload separado.
    private static void AssertNoStrayToken(string token, params string[] allowedRelativeDirs)
    {
        var srcRoot = Path.Combine(RepoRoot, "src");
        var allowedDirs = allowedRelativeDirs
            .Select(d => Path.Combine(RepoRoot, d) + Path.DirectorySeparatorChar)
            .ToArray();
        var offenders = new List<string>();

        bool IsAllowed(string file) => allowedDirs.Any(d => file.StartsWith(d, StringComparison.OrdinalIgnoreCase));

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file)) continue;
            if (IsAllowed(file)) continue;
            CheckCsFile(file, token, offenders);
        }

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file)) continue;
            if (IsAllowed(file)) continue;
            CheckCsprojFile(file, token, offenders);
        }

        Assert.True(offenders.Count == 0,
            $"Guarda AGPL: '{token}' encontrado fora de {string.Join(", ", allowedRelativeDirs)} " +
            $"(dependência AGPL vazando para fora da fronteira isolada):\n" + string.Join("\n", offenders));
    }

    private static void CheckCsFile(string file, string token, List<string> offenders)
    {
        foreach (var (lineNumber, line) in File.ReadLines(file).Select((l, i) => (i + 1, l)))
        {
            if (LineUsesToken(line.TrimStart(), token))
            {
                offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{lineNumber}: {line.Trim()}");
                break; // 1 ocorrência já basta para nomear o arquivo
            }
        }
    }

    private static void CheckCsprojFile(string file, string token, List<string> offenders)
    {
        foreach (var (lineNumber, line) in File.ReadLines(file).Select((l, i) => (i + 1, l)))
        {
            if (ProjectFileLineUsesToken(line.TrimStart(), token))
            {
                offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{lineNumber}: {line.Trim()}");
                break;
            }
        }
    }

    /// Detector puro por linha de C# (já pré-trimzada à esquerda) — extraído para separar prova de
    /// CAPACIDADE (M12, ver LineUsesToken_RecognizesEquivalentSpellings/_DoesNotFlagAcceptedResidue
    /// abaixo — fixture LITERAL, nenhum arquivo tocado) de prova de COBERTURA (SrcTree_ScanIsNotEmpty
    /// acima — medida de volume sobre os arquivos reais). Reconhece a forma canônica
    /// (`using iText.X;`) e as equivalentes que só aparecem qualificadas com ponto em algum ponto da
    /// linha — alias (`using Foo = iText.X;`), qualificação no meio da linha
    /// (`var x = iText.X.Y();`), `using static iText.X;`, `global using iText.X;`: todas contêm
    /// `"iText."` como substring mesmo sem conter `"using iText"` contíguo, então o segundo `Contains`
    /// já cobre as 4 sem tratamento especial por forma.
    private static bool LineUsesToken(string trimmedLine, string token)
    {
        if (trimmedLine.StartsWith("//")) return false; // comentário de linha inteira: resíduo aceito
        return trimmedLine.Contains($"using {token}") || trimmedLine.Contains($"{token}.");
    }

    /// Mesma ideia para .csproj: XML não tem `using`, então olhamos dentro de declarações
    /// `<PackageReference .../>`/`<Using .../>` — a única forma real de um projeto puxar o pacote
    /// iText/Docnet ou reexportar o namespace via `<Using Include="iText..." />` implícito do SDK.
    /// Case-insensitive: o id do pacote NuGet é "itext" minúsculo (`Include="itext"`), diferente do
    /// namespace C# "iText" usado na varredura de .cs.
    private static bool ProjectFileLineUsesToken(string trimmedLine, string token)
    {
        if (trimmedLine.StartsWith("<!--")) return false; // comentário XML de linha inteira: resíduo aceito
        bool isDeclaration = trimmedLine.Contains("<PackageReference") || trimmedLine.Contains("<Using");
        return isDeclaration && trimmedLine.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuildArtifact(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("obj") || parts.Contains("bin");
    }

    // --- M12: prova de CAPACIDADE do detector (fixture literal, sem tocar arquivo nenhum em src/) ---

    [Theory]
    [InlineData("using iText.Kernel.Pdf;")]                             // canônica
    [InlineData("using Foo = iText.Kernel.Pdf.PdfDocument;")]           // alias
    [InlineData("var doc = iText.Kernel.Pdf.PdfDocument.Something();")] // qualificado no meio da linha
    [InlineData("using static iText.Kernel.Pdf.PdfName;")]              // using static
    [InlineData("global using iText.Kernel.Pdf;")]                      // global using
    public void LineUsesToken_RecognizesEquivalentSpellings(string line)
    {
        Assert.True(LineUsesToken(line.TrimStart(), "iText"), $"detector cego para a grafia: {line}");
    }

    [Theory]
    // comentário de linha inteira: resíduo aceito por design (documentado na classe acima)
    [InlineData("// fala sobre iText no meio do texto, é só comentário")]
    // identificador que só COMEÇA com o token, sem "using iText" nem "iText." — prova que o
    // detector não é um `Contains(token)` ingênuo que dispararia em qualquer substring
    [InlineData("var iTextHelperNaoRelacionado = 1;")]
    public void LineUsesToken_DoesNotFlagAcceptedResidueOrUnrelatedIdentifiers(string line)
    {
        Assert.False(LineUsesToken(line.TrimStart(), "iText"), $"falso positivo em: {line}");
    }

    [Theory]
    [InlineData("<PackageReference Include=\"itext\" Version=\"9.7.0\" />")] // id real do pacote NuGet (minúsculo)
    [InlineData("<Using Include=\"iText.Kernel.Pdf\" />")]
    public void ProjectFileLineUsesToken_RecognizesPackageAndUsingDeclarations(string line)
    {
        Assert.True(ProjectFileLineUsesToken(line.TrimStart(), "iText"), $"detector cego para: {line}");
    }

    [Theory]
    [InlineData("<!-- referenciamos iText em mPdf.Editing, não aqui -->")] // comentário XML: resíduo aceito
    [InlineData("<PackageReference Include=\"CommunityToolkit.Mvvm\" Version=\"8.4.2\" />")] // sem o token
    public void ProjectFileLineUsesToken_DoesNotFlagAcceptedResidue(string line)
    {
        Assert.False(ProjectFileLineUsesToken(line.TrimStart(), "iText"), $"falso positivo em: {line}");
    }
}
