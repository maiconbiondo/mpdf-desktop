using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using mPdf.App.Services;

namespace mPdf.App.Tests;

// ---- fake local de IUpdateSource (mesmo padrão file-scoped de UiPromptsGuardTests/MainViewModelTests) ---

file sealed class FakeUpdateSource : IUpdateSource
{
    private readonly LatestRelease? _result;
    private readonly Exception? _exception;
    public int CallCount { get; private set; }

    public static FakeUpdateSource Returning(LatestRelease? result) => new(result, null);
    public static FakeUpdateSource Throwing(Exception ex) => new(null, ex);

    private FakeUpdateSource(LatestRelease? result, Exception? exception)
    {
        _result = result;
        _exception = exception;
    }

    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct)
    {
        CallCount++;
        if (_exception is not null) throw _exception;
        return Task.FromResult(_result);
    }
}

/// Task 2 (Plano 11) — testes puros de `UpdateService`: comparação de versão (System.Version, nunca
/// comparação de string — 1.10 vs 1.9), extração do SHA do corpo (contrato de `tools/release.ps1`,
/// último match vence), cenários de `VerificarAsync` com `IUpdateSource` fake (mais-nova/igual/erro/
/// rate-limit/sem-asset/sem-hash — lista exata do brief), e `VerifyAndFinalize` (verificação de hash
/// sobre um arquivo REAL em disco — hash adulterado recusa e apaga, hash batendo devolve o caminho
/// verificado). NENHUM destes testes toca rede — `IUpdateSource` fake cobre `VerificarAsync`;
/// `VerifyAndFinalize` é síncrono/local por design (só le o arquivo já baixado e compara hash).
public class UpdateServiceTests
{
    private const string ValidSha = "058cf405f778fc15284646e9d7ad8377171681366424a448b588a17bb4a1c813";

    private static string CurrentTag => "v" + UpdateService.CurrentVersionText();

    private static string NewerTag
    {
        get
        {
            var v = UpdateService.CurrentVersion();
            return $"v{v.Major}.{v.Minor}.{v.Build + 1}";
        }
    }

    private static LatestRelease NewerRelease(string? body = null, string? assetUrl = "https://example.invalid/mPDF-Setup-9.9.9.exe", string? assetName = "mPDF-Setup-9.9.9.exe") =>
        new(NewerTag, body ?? $"Notas da versão.\n\nSHA256: {ValidSha}", assetName, assetUrl, 123456);

    // ---- VerificarAsync — cenários do brief (fake source): mais-nova/igual/erro/rate-limit/sem-asset/sem-hash

    [Fact]
    public async Task VerificarAsync_NewerTag_ReturnsDisponivelComInfo()
    {
        var service = new UpdateService(FakeUpdateSource.Returning(NewerRelease()));

        var result = await service.VerificarAsync();

        Assert.Equal(UpdateCheckStatus.Disponivel, result.Status);
        Assert.NotNull(result.Info);
        Assert.Equal(NewerTag, result.Info!.TagVersao);
        Assert.Equal(ValidSha, result.Info.Sha256Esperado);
        Assert.Equal("https://example.invalid/mPDF-Setup-9.9.9.exe", result.Info.UrlAsset);
    }

    [Fact]
    public async Task VerificarAsync_EqualTag_ReturnsAtualizado()
    {
        var release = new LatestRelease(CurrentTag, "notas", "x.exe", "https://x.invalid/x.exe", 1);
        var service = new UpdateService(FakeUpdateSource.Returning(release));

        var result = await service.VerificarAsync();

        Assert.Equal(UpdateCheckStatus.Atualizado, result.Status);
        Assert.Null(result.Info);
    }

    [Fact]
    public async Task VerificarAsync_OlderTag_ReturnsAtualizado()
    {
        var v = UpdateService.CurrentVersion();
        var release = new LatestRelease($"v{v.Major}.{v.Minor}.0", "notas", "x.exe", "https://x.invalid/x.exe", 1);
        var service = new UpdateService(FakeUpdateSource.Returning(release));

        var result = await service.VerificarAsync();

        Assert.Equal(UpdateCheckStatus.Atualizado, result.Status);
    }

    [Fact]
    public async Task VerificarAsync_GenericNetworkFailure_ReturnsErroSemRede()
    {
        var service = new UpdateService(FakeUpdateSource.Throwing(new HttpRequestException("falha de dns")));

        var result = await service.VerificarAsync();

        Assert.Equal(UpdateCheckStatus.Erro, result.Status);
        Assert.Equal(UpdateErrorKind.SemRede, result.ErroTipo);
        Assert.Contains("conexão", result.MensagemErro);
    }

    [Fact]
    public async Task VerificarAsync_403Forbidden_ReturnsErroLimiteTaxa()
    {
        var ex = new HttpRequestException("rate limited", null, HttpStatusCode.Forbidden);
        var service = new UpdateService(FakeUpdateSource.Throwing(ex));

        var result = await service.VerificarAsync();

        Assert.Equal(UpdateCheckStatus.Erro, result.Status);
        Assert.Equal(UpdateErrorKind.LimiteTaxa, result.ErroTipo);
        Assert.Contains("Tente novamente", result.MensagemErro);
    }

    [Fact]
    public async Task VerificarAsync_NoExeAsset_ReturnsErroSemAsset()
    {
        var service = new UpdateService(FakeUpdateSource.Returning(NewerRelease(assetUrl: null, assetName: null)));

        var result = await service.VerificarAsync();

        Assert.Equal(UpdateCheckStatus.Erro, result.Status);
        Assert.Equal(UpdateErrorKind.SemAsset, result.ErroTipo);
    }

    [Fact]
    public async Task VerificarAsync_NoShaLineInBody_ReturnsErroSemHash()
    {
        var service = new UpdateService(FakeUpdateSource.Returning(NewerRelease(body: "Notas sem hash nenhum.")));

        var result = await service.VerificarAsync();

        Assert.Equal(UpdateCheckStatus.Erro, result.Status);
        Assert.Equal(UpdateErrorKind.SemHash, result.ErroTipo);
    }

    // ---- comparação de versão — robustez 1.9 vs 1.10 (nunca comparação de string) --------------------

    [Theory]
    [InlineData("1.10.0", "1.9.0", true)]  // 1.10 > 1.9 numericamente — string ordenaria ao contrário
    [InlineData("1.9.0", "1.10.0", false)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.4.1", "1.4.1", false)]  // igual não é "mais nova"
    [InlineData("1.4.0", "1.4.1", false)]
    [InlineData("v1.5.0", "1.4.1", true)]  // prefixo "v" na tag
    public void IsNewerThan_ComparesNumericallyNotLexicographically(string tagVersion, string currentVersion, bool expectNewer)
    {
        Assert.Equal(expectNewer, UpdateService.IsNewerThan(tagVersion, currentVersion));
    }

    [Fact]
    // Pitfall real (documentado): a versão do assembly (System.Version lido de
    // typeof(UpdateService).Assembly.GetName().Version) sempre tem 4 componentes com Revision=0
    // EXPLÍCITO (o SDK completa "1.4.1" -> AssemblyVersion "1.4.1.0"); uma tag "1.4.1" (3 componentes)
    // tem Revision=-1 ("não especificado") internamente no System.Version. Comparar os Version CRUS sem
    // normalizar faria uma versão IGUAL parecer mais nova/mais velha só por causa da 4ª componente —
    // NormalizeVersion (Major.Minor.Build, Revision descartado) existe especificamente para isto.
    public void IsNewerThan_EqualIgnoringRevisionComponent_IsNotNewer()
    {
        Assert.False(UpdateService.IsNewerThan("1.4.1", "1.4.1.0"));
        Assert.False(UpdateService.IsNewerThan("1.4.1.0", "1.4.1"));
    }

    [Fact]
    public void IsNewerThan_UnparsableTag_ReturnsFalse()
    {
        Assert.False(UpdateService.IsNewerThan("não-é-uma-versão", "1.4.1"));
    }

    // ---- extração do SHA do corpo (contrato de tools/release.ps1) -------------------------------------

    [Fact]
    public void ExtractSha256_LastMatchingLineWins()
    {
        string body = $"SHA256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n\nSHA256: {ValidSha}";
        Assert.Equal(ValidSha, UpdateService.ExtractSha256(body));
    }

    [Fact]
    public void ExtractSha256_UppercaseHex_NormalizedToLowercase()
    {
        Assert.Equal(ValidSha, UpdateService.ExtractSha256($"SHA256: {ValidSha.ToUpperInvariant()}"));
    }

    [Fact]
    public void ExtractSha256_NoMatchingLine_ReturnsNull() =>
        Assert.Null(UpdateService.ExtractSha256("notas sem nada relacionado ao hash."));

    [Fact]
    public void ExtractSha256_NullOrEmptyBody_ReturnsNull()
    {
        Assert.Null(UpdateService.ExtractSha256(null));
        Assert.Null(UpdateService.ExtractSha256(""));
    }

    [Fact]
    public void ExtractSha256_WrongLength_DoesNotMatch() =>
        Assert.Null(UpdateService.ExtractSha256("SHA256: abc123"));

    [Fact] // âncora de linha completa — nunca um prefixo solto no meio de outra frase
    public void ExtractSha256_TrailingTextOnSameLine_DoesNotMatch() =>
        Assert.Null(UpdateService.ExtractSha256($"SHA256: {ValidSha} (verificado)"));

    // ---- BaixarEVerificarAsync / VerifyAndFinalize — hash tampered (arquivo REAL em temp) -------------

    [Fact]
    public void VerifyAndFinalize_TamperedFile_RefusesAndDeletesFile()
    {
        string path = WriteTempFile("conteúdo adulterado, o hash não vai bater com o esperado");
        try
        {
            var result = UpdateService.VerifyAndFinalize(path, ValidSha);

            Assert.Equal(DownloadStatus.Recusado, result.Status);
            Assert.Null(result.Arquivo);
            Assert.False(string.IsNullOrEmpty(result.MensagemErro));
            Assert.False(File.Exists(path), "arquivo com hash adulterado deveria ter sido apagado");
        }
        finally { TryDeleteIfExists(path); }
    }

    [Fact]
    public void VerifyAndFinalize_MatchingHash_ReturnsVerifiedPathAndKeepsFile()
    {
        string path = WriteTempFile("conteúdo real do instalador (simulado)");
        string realHash = Sha256Hex(File.ReadAllBytes(path));
        try
        {
            var result = UpdateService.VerifyAndFinalize(path, realHash);

            Assert.Equal(DownloadStatus.Verificado, result.Status);
            Assert.NotNull(result.Arquivo);
            Assert.Equal(path, result.Arquivo!.CaminhoArquivo);
            Assert.True(File.Exists(path), "arquivo com hash verificado não deveria ser apagado");
        }
        finally { TryDeleteIfExists(path); }
    }

    [Fact]
    public void VerifyAndFinalize_HashComparisonIsCaseInsensitive()
    {
        string path = WriteTempFile("conteúdo case insensitive");
        string realHashUpper = Sha256Hex(File.ReadAllBytes(path)).ToUpperInvariant();
        try
        {
            var result = UpdateService.VerifyAndFinalize(path, realHashUpper);
            Assert.Equal(DownloadStatus.Verificado, result.Status);
        }
        finally { TryDeleteIfExists(path); }
    }

    // ---- BaixarEVerificarAsync — cancelamento (token JÁ cancelado, sem tocar rede nenhuma) -------------

    [Fact]
    public async Task BaixarEVerificarAsync_AlreadyCancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Host PRECISA estar na allowlist (I4) — senão a recusa de host dispara ANTES do cancelamento
        // ser sequer checado, e este teste deixaria de exercitar o caminho que quer provar.
        var info = new UpdateInfo("v9.9.9", null, "https://github.com/x/x/releases/download/v9.9.9/x.exe", "x.exe", 10, ValidSha);
        var service = new UpdateService(FakeUpdateSource.Returning(null));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.BaixarEVerificarAsync(info, null, cts.Token));
    }

    // ---- C1 (CRÍTICO, revisão de segurança) — nome de asset malicioso: path traversal / escrita fora --
    // ---- do diretório temp por chamada. Path.Combine(dir, nomeAsset) DESCARTA `dir` quando nomeAsset --
    // ---- é absoluto/UNC; "../" escapa mesmo sem ser absoluto. Provado ao vivo pelo revisor. ------------

    [Theory]
    [InlineData(@"C:\evil\x.exe")]   // absoluto — Path.Combine descartaria "dir" inteiro
    [InlineData(@"\\host\x.exe")]    // UNC
    [InlineData(@"..\..\x.exe")]     // traversal relativo
    [InlineData("a/b.exe")]          // separador embutido (forward slash)
    public async Task BaixarEVerificarAsync_MaliciousAssetName_RefusesWithoutTouchingDisk(string nomeMalicioso)
    {
        var before = SnapshotUpdateTempDirs();
        var info = new UpdateInfo("v9.9.9", null,
            "https://github.com/x/x/releases/download/v9.9.9/x.exe", nomeMalicioso, 10, ValidSha);
        var service = new UpdateService(FakeUpdateSource.Returning(null));

        var result = await service.BaixarEVerificarAsync(info, null, CancellationToken.None);

        Assert.Equal(DownloadStatus.Recusado, result.Status);
        Assert.Null(result.Arquivo);
        Assert.False(string.IsNullOrEmpty(result.MensagemErro));
        // nada escreve fora (nem DENTRO) do diretório temp por chamada — a validação recusa ANTES de
        // qualquer I/O, então NENHUM diretório novo em %TEMP%\mPDF deveria ter sido criado.
        Assert.Equal(before, SnapshotUpdateTempDirs());
        Assert.False(File.Exists(nomeMalicioso), $"não deveria existir um arquivo em '{nomeMalicioso}'");
    }

    private static HashSet<string> SnapshotUpdateTempDirs()
    {
        string root = Path.Combine(Path.GetTempPath(), "mPDF");
        return Directory.Exists(root)
            ? new HashSet<string>(Directory.GetDirectories(root, "update-*").Select(Path.GetFileName)!)
            : new HashSet<string>();
    }

    [Theory]
    [InlineData("mPDF-Setup-1.4.1.exe", "mPDF-Setup-1.4.1.exe")]
    [InlineData("a.exe", "a.exe")]
    public void SanitizeAssetFileName_ValidName_ReturnsUnchanged(string input, string expected) =>
        Assert.Equal(expected, UpdateService.SanitizeAssetFileName(input));

    [Theory]
    [InlineData(@"C:\evil\x.exe")]
    [InlineData(@"\\host\x.exe")]
    [InlineData(@"..\..\x.exe")]
    [InlineData("a/b.exe")]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeAssetFileName_MaliciousOrInvalid_ReturnsNull(string input) =>
        Assert.Null(UpdateService.SanitizeAssetFileName(input));

    // ---- I4 (revisão de segurança) — allowlist de host do asset ----------------------------------------

    [Theory]
    [InlineData("https://github.com/x/x/releases/download/v1/x.exe")]
    [InlineData("https://objects.githubusercontent.com/some/asset")]
    [InlineData("https://api.github.com/x")]
    [InlineData("https://sub.objects.githubusercontent.com/asset")] // subdomínio legítimo
    public void IsAllowedAssetHost_AllowsGitHubHostsAndSubdomains(string url) =>
        Assert.True(UpdateService.IsAllowedAssetHost(url));

    [Theory]
    [InlineData("https://evil.example.com/x.exe")]
    [InlineData("http://github.com/x/x.exe")]           // http, não https
    [InlineData("https://evilgithub.com/x.exe")]         // substring crua, sem fronteira de domínio
    [InlineData("https://github.com.evil.com/x.exe")]    // prefixo, não sufixo de domínio
    [InlineData("not a url")]
    public void IsAllowedAssetHost_RejectsNonGitHubOrNonHttps(string url) =>
        Assert.False(UpdateService.IsAllowedAssetHost(url));

    [Fact]
    public async Task BaixarEVerificarAsync_SpoofedAssetHost_RefusesViaFakeSource()
    {
        var info = new UpdateInfo("v9.9.9", null, "https://evil.example.com/x.exe", "x.exe", 10, ValidSha);
        var service = new UpdateService(FakeUpdateSource.Returning(null));

        var result = await service.BaixarEVerificarAsync(info, null, CancellationToken.None);

        Assert.Equal(DownloadStatus.Recusado, result.Status);
        Assert.Null(result.Arquivo);
    }

    private static string WriteTempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-update-test-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TryDeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* melhor esforço */ }
    }
}
