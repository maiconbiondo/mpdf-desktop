using mPdf.App.Services;

namespace mPdf.App.Tests;

/// Task 2 (Plano 11) — integração REAL, opcional-skippable: GET real contra a release v1.4.1 publicada
/// de verdade na Task 1 (`maiconbiondo/mpdf-desktop` — ver `task-1-report.md` §4/§6: tag_name "v1.4.1",
/// 1 asset "mPDF-Setup-1.4.1.exe", corpo terminando em "SHA256: 058cf4...") — prova que
/// `GitHubUpdateSource.GetLatestAsync` bate na API real do GitHub e que `UpdateService.ExtractSha256`
/// parseia o corpo REAL corretamente, contra o CONTRATO real (não uma simulação). Gate: env var
/// `MPDF_TESTE_REDE=1` — ausente, `Skip.If` pula de verdade (nunca um early-return silencioso contado
/// como "passou" — `Xunit.SkippableFact`, mesmo pacote/padrão já usado em
/// `mPdf.Signing.Tests.HybridXrefRegressionTests`). A suíte normal (CI, `dotnet test` sem a env var)
/// NUNCA bate rede — reforça, na prática, a MESMA garantia que `UpdateNetworkConfinementTests` prova
/// estruturalmente (rede só por ação explícita, nunca em background/CI).
public class UpdateServiceRealNetworkTests
{
    [SkippableFact]
    public async Task GetLatestAsync_RealGitHubApi_ParsesTagAndSha()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("MPDF_TESTE_REDE") == "1",
            "MPDF_TESTE_REDE não setada — integração de rede real pulada neste ambiente.");

        var source = new GitHubUpdateSource();
        try
        {
            var release = await source.GetLatestAsync(CancellationToken.None);

            Assert.NotNull(release);
            Assert.False(string.IsNullOrWhiteSpace(release!.TagName));
            Assert.False(string.IsNullOrWhiteSpace(release.AssetUrl));
            Assert.NotNull(release.AssetName);
            Assert.True(release.AssetName!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                $"esperava um asset .exe, achou '{release.AssetName}'");

            string? sha = UpdateService.ExtractSha256(release.Body);
            Assert.False(string.IsNullOrWhiteSpace(sha));
            Assert.Equal(64, sha!.Length);
        }
        finally { source.Dispose(); }
    }
}
