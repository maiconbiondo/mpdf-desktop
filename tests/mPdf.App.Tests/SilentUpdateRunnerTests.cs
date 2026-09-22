using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

// IUpdateSource fake local (file-scoped, mesma disciplina de UpdateServiceTests).
file sealed class FakeSourceRunner(LatestRelease? result, Exception? ex = null) : IUpdateSource
{
    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct)
        => ex is not null ? Task.FromException<LatestRelease?>(ex) : Task.FromResult(result);
}

public class SilentUpdateRunnerTests
{
    // LatestRelease(TagName, Body, AssetName, AssetUrl, AssetSize) — confirmado em UpdateService.cs:337.
    // Asset em example.invalid: o download falha DETERMINISTICO (NXDOMAIN), sem rede real; body tem o SHA
    // (contrato do release) so pra passar da checagem "tem hash".
    private static LatestRelease NovaVersao(string tag = "999.0.0")
        => new(tag, "corpo\nSHA256: " + new string('a', 64),
               "mPDF-Setup-999.0.0.exe", "https://example.invalid/mPDF-Setup-999.0.0.exe", 123);

    // "sem versao nova": UpdateService.VerificarAsync trata release NULO como ERRO (SemRede), nao como
    // "ja atualizado" — confirmado contra UpdateService.cs:92-97 e o exemplar UpdateServiceTests.cs:45
    // (`CurrentTag => "v" + UpdateService.CurrentVersionText()`). Por isso o cenario "ja na ultima" usa
    // uma release com a MESMA tag da versao instalada (IsNewerThan compara e da false), nunca null.
    private static LatestRelease VersaoAtual()
        => new("v" + UpdateService.CurrentVersionText(), "notas", "x.exe", "https://x.invalid/x.exe", 1);

    private sealed class LancadorFake { public string? Caminho; public bool Lancado; public void Lancar(string p){ Caminho=p; Lancado=true; } }

    [Fact] // VerificarAsync: sem versao nova -> JaNaUltima
    public async Task Verificar_JaNaUltima()
    {
        var runner = new SilentUpdateRunner(() => new FakeSourceRunner(VersaoAtual()));
        var s = await runner.VerificarAsync(CancellationToken.None);
        Assert.Equal(ResultadoUpdateCli.JaNaUltima, s.Resultado);
    }

    [Fact] // VerificarAsync: versao nova -> AtualizacaoDisponivel + versao
    public async Task Verificar_Disponivel()
    {
        var runner = new SilentUpdateRunner(() => new FakeSourceRunner(NovaVersao()));
        var s = await runner.VerificarAsync(CancellationToken.None);
        Assert.Equal(ResultadoUpdateCli.AtualizacaoDisponivel, s.Resultado);
        Assert.Equal("999.0.0", s.VersaoDisponivel);
    }

    [Fact] // VerificarAsync: source lanca -> Erro
    public async Task Verificar_Erro()
    {
        var runner = new SilentUpdateRunner(() => new FakeSourceRunner(null, new System.Net.Http.HttpRequestException("dns")));
        var s = await runner.VerificarAsync(CancellationToken.None);
        Assert.Equal(ResultadoUpdateCli.Erro, s.Resultado);
    }

    [Fact] // AtualizarAsync: ja na ultima -> JaNaUltima, NAO lanca instalador
    public async Task Atualizar_JaNaUltima_NaoLanca()
    {
        var lan = new LancadorFake();
        var runner = new SilentUpdateRunner(() => new FakeSourceRunner(VersaoAtual()), lan.Lancar);
        var s = await runner.AtualizarAsync(CancellationToken.None);
        Assert.Equal(ResultadoUpdateCli.JaNaUltima, s.Resultado);
        Assert.False(lan.Lancado);
    }

    [Fact] // AtualizarAsync: versao nova mas download falha (example.invalid) -> Erro, NAO lanca
    public async Task Atualizar_DownloadFalha_Erro()
    {
        var lan = new LancadorFake();
        var runner = new SilentUpdateRunner(() => new FakeSourceRunner(NovaVersao()), lan.Lancar);
        var s = await runner.AtualizarAsync(CancellationToken.None);
        Assert.Equal(ResultadoUpdateCli.Erro, s.Resultado);
        Assert.False(lan.Lancado);
    }

    [Fact] // InstalarVerificadoAsync: com VerifiedUpdateFile REAL -> lanca + AtualizacaoIniciada
    public async Task InstalarVerificado_Lanca()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-fake-inst-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "conteudo");
        try
        {
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tmp)));
            var dr = UpdateService.VerifyAndFinalize(tmp, sha); // DownloadResult.Ok com VerifiedUpdateFile real
            var arquivo = ObterVerified(dr);                    // helper: extrai o VerifiedUpdateFile do DownloadResult
            var lan = new LancadorFake();
            var runner = new SilentUpdateRunner(() => new FakeSourceRunner(null), lan.Lancar);
            var s = await runner.InstalarVerificadoAsync(arquivo, "1.0.0", "999.0.0");
            Assert.Equal(ResultadoUpdateCli.AtualizacaoIniciada, s.Resultado);
            Assert.True(lan.Lancado);
            Assert.Equal(tmp, lan.Caminho);
        }
        finally { File.Delete(tmp); }
    }

    [Theory] // MapearExitCode: tabela do contrato (0/10/10/20)
    [InlineData(ResultadoUpdateCli.JaNaUltima, 0)]
    [InlineData(ResultadoUpdateCli.AtualizacaoDisponivel, 10)]
    [InlineData(ResultadoUpdateCli.AtualizacaoIniciada, 10)]
    [InlineData(ResultadoUpdateCli.Erro, 20)]
    public void ExitCodes(ResultadoUpdateCli r, int esperado)
        => Assert.Equal(esperado, SilentUpdateRunner.MapearExitCode(r));

    [Fact] // FormatarStatus: linha ASCII do contrato p/ "disponivel"
    public void FormatarStatus_Disponivel()
    {
        var linha = SilentUpdateRunner.FormatarStatus("/checkupdate",
            new StatusUpdateCli(ResultadoUpdateCli.AtualizacaoDisponivel, "2.9.0", "2.10.0", null));
        Assert.Equal("mPDF /checkupdate: instalada=2.9.0 disponivel=2.10.0 atualizar=sim", linha);
        Assert.All(linha, c => Assert.True(c < 128, "linha de status deve ser ASCII"));
    }

    // DownloadResult(Status, Arquivo, MensagemErro) — o VerifiedUpdateFile do sucesso e dr.Arquivo (UpdateService.cs:417).
    private static UpdateService.VerifiedUpdateFile ObterVerified(DownloadResult dr) => dr.Arquivo!;
}
