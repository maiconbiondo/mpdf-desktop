using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace mPdf.App.Services;

/// <summary>
/// Task 2 (Plano 11) — TODA a rede do app inteiro vive neste arquivo. Restrição estrutural do plano
/// ("ZERO monitoramento em background"): nenhum outro arquivo de <c>src/mPdf.App</c> pode conter o
/// token <c>HttpClient</c>/<c>WebRequest</c> — <see cref="UpdateNetworkConfinementTests"/>
/// (mPdf.App.Tests) varre o resto de <c>src/mPdf.App</c> por esses tokens e reprova nomeando o
/// arquivo/linha, mesmo padrão de varredura de <c>AgplGuardTests</c>. É por isso que
/// <see cref="GitHubUpdateSource"/> (a implementação REAL de <see cref="IUpdateSource"/>, a única que
/// de fato bate na API do GitHub) mora NESTE MESMO arquivo em vez de um arquivo próprio ao lado das
/// outras implementações de serviço deste projeto — a fronteira de rede é por ARQUIVO, não por pasta.
///
/// <see cref="UpdateService"/> em si só é CONSTRUÍDO em 1 lugar do app inteiro:
/// <c>SobreViewModel.VerificarAtualizacao</c> (o comando "Verificar atualização"), nunca no construtor
/// do diálogo/VM nem em qualquer caminho que rode sem o usuário clicar explicitamente — segunda guarda
/// estrutural (<see cref="UpdateNetworkConfinementTests.UpdateServiceConstruction_OccursInExactlyOnePlace"/>),
/// complementada pela prova COMPORTAMENTAL em <c>SobreViewModelTests</c> (fábrica <c>Func&lt;IUpdateSource&gt;</c>
/// injetada tem zero chamadas só de abrir o diálogo).
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly IUpdateSource _source;
    private readonly HttpClient _downloadClient;

    public UpdateService(IUpdateSource source) : this(source, CreateDownloadClient()) { }

    /// ctor interno — permite um teste (se algum dia precisar) injetar um `HttpClient` próprio pro
    /// download; hoje nenhum teste toca esse caminho (o download real não é exercitado sem rede —
    /// `VerifyAndFinalize`, abaixo, é a parte da verificação que É testada sem rede, com um arquivo
    /// real em disco).
    internal UpdateService(IUpdateSource source, HttpClient downloadClient)
    {
        _source = source;
        _downloadClient = downloadClient;
    }

    private static HttpClient CreateDownloadClient() => new() { Timeout = TimeSpan.FromMinutes(10) };

    // Revisão de segurança (achado do revisor, "minor"): `_source` (produção: `GitHubUpdateSource`,
    // dono do SEU PRÓPRIO `HttpClient` de checagem) NÃO era descartado — só `_downloadClient` — um
    // `HttpClient` vivo vazava a cada "Verificar atualização" clicado (o VM descarta o `_service`
    // ANTERIOR antes de trocar, mas `UpdateService.Dispose` nunca repassava pro `_source`).
    public void Dispose()
    {
        _downloadClient.Dispose();
        (_source as IDisposable)?.Dispose();
    }

    // ---- versão atual do app -----------------------------------------------------------------------

    /// Versão atual do app, normalizada (Major.Minor.Build — ver `NormalizeVersion`). Lida do assembly
    /// (o `<Version>` do csproj flui pro `AssemblyVersion` via SDK, sem código extra pra manter em
    /// sincronia com nenhuma outra fonte — mesma fonte única já documentada no cabeçalho de
    /// `mPdf.App.csproj`/consumida por `tools/release-functions.ps1:Get-ProductVersion`).
    public static Version CurrentVersion() => NormalizeVersion(typeof(UpdateService).Assembly.GetName().Version!);

    public static string CurrentVersionText() => CurrentVersion().ToString();

    // ---- verificar ----------------------------------------------------------------------------------

    /// Consulta `_source` (rede real em produção, fake em teste) e devolve um resultado TIPADO: nunca
    /// nova versão sem asset .exe e SHA presentes ao mesmo tempo — ambos exigidos ANTES de considerar
    /// "Disponível" (ver `UpdateErrorKind.SemAsset`/`SemHash`). Erros de rede/limite de taxa são
    /// diferenciados pelo `HttpStatusCode` já populado por `HttpRequestException` desde .NET 5
    /// (`EnsureSuccessStatusCode`) — sem precisar de um tipo de exceção próprio.
    public async Task<UpdateCheckResult> VerificarAsync(CancellationToken ct = default)
    {
        LatestRelease? release;
        try
        {
            release = await _source.GetLatestAsync(ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return UpdateCheckResult.Erro(UpdateErrorKind.LimiteTaxa,
                "Muitas verificações recentes. Tente novamente em alguns minutos.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or System.Net.Sockets.SocketException or JsonException)
        {
            return UpdateCheckResult.Erro(UpdateErrorKind.SemRede,
                "Não foi possível verificar a atualização. Confira a conexão com a internet.");
        }

        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            return UpdateCheckResult.Erro(UpdateErrorKind.SemRede,
                "Não foi possível verificar a atualização. Confira a conexão com a internet.");

        if (!IsNewerThan(release.TagName, CurrentVersionText()))
            return UpdateCheckResult.Atualizado();

        if (string.IsNullOrWhiteSpace(release.AssetUrl))
            return UpdateCheckResult.Erro(UpdateErrorKind.SemAsset,
                "A versão publicada não tem um instalador (.exe) disponível.");

        string? sha = ExtractSha256(release.Body);
        if (sha is null)
            return UpdateCheckResult.Erro(UpdateErrorKind.SemHash,
                "A versão publicada não informa o hash de verificação (release sem hash).");

        var info = new UpdateInfo(
            release.TagName, release.Body, release.AssetUrl,
            release.AssetName ?? "instalador.exe", release.AssetSize, sha);
        return UpdateCheckResult.Disponivel(info);
    }

    // ---- comparação de versão (robusta: 1.9 vs 1.10 — System.Version, nunca comparação de string) ----

    /// `tagVersion`/`currentVersion` aceitam prefixo "v" opcional (tag do GitHub). `false` se qualquer
    /// um dos dois não parsear (defensivo — nunca oferece atualização a partir de um dado inválido).
    internal static bool IsNewerThan(string tagVersion, string currentVersion)
    {
        var tag = ParseVersion(tagVersion);
        var current = ParseVersion(currentVersion);
        if (tag is null || current is null) return false;
        return NormalizeVersion(tag) > NormalizeVersion(current);
    }

    private static Version? ParseVersion(string text)
    {
        string s = text.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? text[1..] : text;
        return Version.TryParse(s, out var v) ? v : null;
    }

    /// Sempre 3 componentes (Major.Minor.Build) — descarta Revision. Pitfall real (documentado em
    /// `UpdateServiceTests.IsNewerThan_EqualIgnoringRevisionComponent_IsNotNewer`): o `AssemblyVersion`
    /// derivado do `<Version>` do csproj sempre tem 4 componentes com Revision=0 EXPLÍCITO, enquanto uma
    /// tag "1.4.1" (3 componentes) tem Revision=-1 ("não especificado") internamente no `System.Version`
    /// — comparar os `Version` crus faria uma versão IGUAL parecer mais nova/mais velha só por causa
    /// dessa 4ª componente.
    private static Version NormalizeVersion(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // ---- extração do SHA do corpo da release (contrato de tools/release.ps1) -------------------------

    // Âncora de LINHA completa (não multiline) — mesmo contrato documentado no cabeçalho de
    // tools/release.ps1: "SHA256: <64 hex minúsculos>" é sempre a ÚLTIMA linha do corpo, sem exceção.
    private static readonly Regex ShaLineRegex = new(@"^SHA256:\s*([0-9a-fA-F]{64})$", RegexOptions.Compiled);

    /// Varre TODAS as linhas do corpo; a ÚLTIMA linha que bate vence (defensivo contra notas de texto
    /// livre que mencionem a string em outro contexto) — contrato documentado no cabeçalho de
    /// `tools/release.ps1`. `null` se nenhuma linha bater (release sem hash).
    internal static string? ExtractSha256(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        string? found = null;
        foreach (var line in body.Split('\n'))
        {
            var m = ShaLineRegex.Match(line.TrimEnd('\r'));
            if (m.Success) found = m.Groups[1].Value.ToLowerInvariant();
        }
        return found;
    }

    // ---- baixar + verificar --------------------------------------------------------------------------

    /// Baixa `info.UrlAsset` para `%TEMP%\mPDF\update-<guid>\<NomeAsset>` (pasta nova por chamada — nunca
    /// reaproveita um diretório de uma tentativa anterior), reportando progresso via `progresso`
    /// (bytes lidos, total — de `Content-Length` ou `info.TamanhoBytes` como fallback). Cancelamento
    /// GENUÍNO (via `ct`) propaga `OperationCanceledException` normalmente (o chamador decide o que
    /// fazer — nunca convertido silenciosamente num `DownloadResult`); qualquer OUTRA falha de
    /// rede/I-O vira um `DownloadResult.Falhou` tipado pt-BR. Em AMBOS os casos de falha, o arquivo
    /// parcial é apagado (nunca deixa lixo pra trás). Ao final, delega a verificação de hash pra
    /// `VerifyAndFinalize` — o ÚNICO método que devolve um `VerifiedUpdateFile` (ver doc XML da
    /// classe aninhada).
    public async Task<DownloadResult> BaixarEVerificarAsync(
        UpdateInfo info, IProgress<(long Baixados, long Total)>? progresso, CancellationToken ct)
    {
        // C1 (CRÍTICO, revisão de segurança — provado ao vivo pelo revisor): `info.NomeAsset`/
        // `info.UrlAsset` vêm VERBATIM da API do GitHub (campos `name`/`browser_download_url` de um
        // release QUALQUER — não necessariamente o nosso `tools/release.ps1`; um release comprometido/
        // malicioso controla os DOIS, incluindo o hash no corpo que "confere" contra o próprio arquivo
        // adulterado). AS DUAS validações abaixo rodam ANTES de qualquer I/O — nome/host inválidos
        // recusam sem criar diretório nenhum, sem tocar rede nenhuma (ver
        // `UpdateServiceTests.BaixarEVerificarAsync_MaliciousAssetName_RefusesWithoutTouchingDisk`, que
        // confere que NENHUM diretório novo aparece em `%TEMP%\mPDF`).
        string? nomeArquivo = SanitizeAssetFileName(info.NomeAsset);
        if (nomeArquivo is null)
            return DownloadResult.Falhou("Nome de arquivo inválido no release.");

        // I4: allowlist de host do asset — só baixa de domínios do GitHub, nunca de onde quer que o
        // corpo/os assets da release apontem (ver `IsAllowedAssetHost`).
        if (!IsAllowedAssetHost(info.UrlAsset))
            return DownloadResult.Falhou("Origem do instalador não é confiável (fora da lista de domínios permitidos).");

        string dir = Path.Combine(Path.GetTempPath(), "mPDF", $"update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, nomeArquivo);

        try
        {
            using var response = await _downloadClient.GetAsync(info.UrlAsset, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.TamanhoBytes;

            await using (var httpStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = File.Create(path))
            {
                var buffer = new byte[81920];
                long lidos = 0;
                int n;
                while ((n = await httpStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, n), ct);
                    lidos += n;
                    progresso?.Report((lidos, total));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(path);
            throw; // cancelamento genuíno do usuário -- nunca mascarado como uma falha "recusada"
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            TryDelete(path);
            return DownloadResult.Falhou("Não foi possível baixar a atualização. Confira a conexão com a internet.");
        }

        return VerifyAndFinalize(path, info.Sha256Esperado);
    }

    /// Compara o SHA-256 do arquivo JÁ EM DISCO contra `expectedSha256Hex` (comparação case-insensitive
    /// — o corpo da release/o valor calculado podem vir em maiúsculas ou minúsculas). Hash divergente:
    /// APAGA o arquivo e devolve uma recusa tipada (nunca deixa um arquivo adulterado/corrompido pra
    /// trás). Hash batendo: devolve o ÚNICO objeto `VerifiedUpdateFile` possível. `static`/síncrono de
    /// propósito: não depende de rede, testável com um arquivo REAL em disco sem nenhum fake de
    /// `IUpdateSource` (ver `UpdateServiceTests.VerifyAndFinalize_*`). Encaminha (1 linha) pra
    /// `VerifiedUpdateFile.VerifyAndFinalize` — ver doc XML da classe aninhada pra por que o CÁLCULO em
    /// si mora LÁ (não aqui): é o único jeito de o construtor `private` do resultado ficar
    /// verdadeiramente inacessível a qualquer código FORA desta cadeia (C# só concede acesso a membro
    /// `private` de um tipo aninhado pra código escrito DENTRO desse mesmo tipo aninhado — a classe
    /// externa NÃO tem esse privilégio automaticamente, ao contrário do sentido inverso).
    public static DownloadResult VerifyAndFinalize(string downloadedFilePath, string expectedSha256Hex) =>
        VerifiedUpdateFile.VerifyAndFinalize(downloadedFilePath, expectedSha256Hex);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* melhor esforço */ }
    }

    /// C1 (CRÍTICO, revisão de segurança): reduz `nomeAsset` pro componente de ARQUIVO final
    /// (`Path.GetFileName`) e recusa (devolve `null`) se o resultado DIFERIR do original — significa que
    /// o nome continha um separador de diretório, prefixo de unidade (`C:\`) ou UNC (`\\host\`).
    /// `Path.Combine(dir, nomeAsset)` DESCARTA `dir` inteiro quando o 2º argumento é absoluto/UNC
    /// (comportamento documentado do .NET, não um bug) — sem esta checagem, um release malicioso (nome
    /// do asset controlado pelo publicador da release, JUNTO com o hash no corpo que "confere" contra o
    /// próprio arquivo adulterado) escrevia um arquivo VERIFICADO em qualquer caminho do disco, só de o
    /// usuário clicar "Baixar e instalar". Recusa o nome INTEIRO (nunca trunca silenciosamente pro
    /// componente "seguro" — um nome que tentou escapar é tratado como inválido, não como uma sugestão a
    /// corrigir). `Path.IsPathRooted` é defesa em profundidade redundante (não deveria disparar depois
    /// do check de igualdade acima, mas documenta a intenção explicitamente). `internal` — testável
    /// direto (fixture literal) sem precisar passar pelo método assíncrono inteiro.
    internal static string? SanitizeAssetFileName(string nomeAsset)
    {
        if (string.IsNullOrWhiteSpace(nomeAsset)) return null;
        string fileName = Path.GetFileName(nomeAsset);
        if (string.IsNullOrEmpty(fileName)) return null;
        if (!string.Equals(fileName, nomeAsset, StringComparison.Ordinal)) return null;
        if (Path.IsPathRooted(fileName)) return null; // defesa redundante, ver doc XML acima
        return fileName;
    }

    private static readonly string[] AllowedAssetHosts =
        { "github.com", "objects.githubusercontent.com", "api.github.com" };

    /// I4 (revisão de segurança): `info.UrlAsset` vem do campo `browser_download_url` da release — mesma
    /// fonte não-confiável de propósito que `NomeAsset` (ver `SanitizeAssetFileName`). Exige HTTPS e
    /// host dentro da allowlist ANTES de `_downloadClient.GetAsync` — nunca confia cegamente no dado
    /// externo antes de agir sobre ele, mesmo espírito do C1. Comparação por SUFIXO de DOMÍNIO (host ==
    /// permitido OU termina em "." + permitido) — nunca um `Contains` cru, que aceitaria
    /// "evilgithub.com"/"github.com.evil.com" só por conterem a substring "github.com" sem fronteira de
    /// domínio real. `internal` — testável direto (fixture literal), incluindo os casos POSITIVOS
    /// (hosts/subdomínios permitidos) sem precisar tentar uma conexão de rede de verdade.
    internal static bool IsAllowedAssetHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var allowed in AllowedAssetHosts)
        {
            if (string.Equals(uri.Host, allowed, StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.Host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// Caminho de um instalador BAIXADO e cujo SHA-256 CONFERE com o hash esperado — o construtor é
    /// `private` e o ÚNICO método capaz de chamá-lo é `VerifyAndFinalize`, logo abaixo, DECLARADO DENTRO
    /// desta própria classe aninhada (não em `UpdateService`) exatamente para isso: em C#, o acesso a um
    /// membro `private` de um tipo T só é concedido a código escrito DENTRO de T — um tipo ANINHADO tem
    /// acesso aos privados do tipo que o CONTÉM (`TryDelete`, acima, chamado logo abaixo), mas o INVERSO
    /// não vale automaticamente (achado ao vivo escrevendo esta classe — `UpdateService.VerifyAndFinalize`
    /// chamando `new VerifiedUpdateFile(...)` direto reprovava a compilação, CS0122). Resultado: NENHUM
    /// outro código deste app consegue montar um caminho "verificado" sem bater EXATAMENTE nesta
    /// verificação de hash. O passo de instalação (`SobreViewModel.ProsseguirComInstalacaoAsync`) só
    /// aceita ESTE tipo como parâmetro — nunca uma `string` crua — então estruturalmente não existe
    /// caminho de código que chame `Process.Start` sobre um arquivo que não passou por aqui.
    public sealed class VerifiedUpdateFile
    {
        public string CaminhoArquivo { get; }
        private VerifiedUpdateFile(string caminhoArquivo) => CaminhoArquivo = caminhoArquivo;

        internal static DownloadResult VerifyAndFinalize(string downloadedFilePath, string expectedSha256Hex)
        {
            string actual;
            using (var stream = File.OpenRead(downloadedFilePath))
            using (var sha = SHA256.Create())
                actual = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();

            if (!string.Equals(actual, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(downloadedFilePath);
                return DownloadResult.Falhou(
                    "O arquivo baixado não confere com o hash esperado — pode estar corrompido ou " +
                    "adulterado. A verificação recusou a instalação e removeu o arquivo.");
            }

            return DownloadResult.Ok(new VerifiedUpdateFile(downloadedFilePath));
        }
    }
}

// =======================================================================================================
// Seam fake-ável — GetLatestAsync é a ÚNICA operação de rede mediada por IUpdateSource (o download em si
// é feito diretamente por UpdateService.BaixarEVerificarAsync, mesmo arquivo, mesma fronteira).
// =======================================================================================================

/// Dados da release mais recente, já reduzidos ao que `UpdateService` precisa — `AssetName`/`AssetUrl`
/// `null` quando NENHUM asset `.exe` foi encontrado entre os assets da release (cenário "sem-asset").
public sealed record LatestRelease(string TagName, string? Body, string? AssetName, string? AssetUrl, long AssetSize);

public interface IUpdateSource
{
    Task<LatestRelease?> GetLatestAsync(CancellationToken ct);
}

/// Implementação REAL — a ÚNICA classe deste app que efetivamente bate na rede (ver restrição
/// estrutural no cabeçalho do arquivo). GET `https://api.github.com/repos/maiconbiondo/mpdf-desktop/
/// releases/latest`, timeout de 15s (checagem rápida, corpo JSON pequeno — bem menor que os 10 min
/// tolerados pro DOWNLOAD do instalador em si), `User-Agent` (exigido pela API do GitHub — requisições
/// sem esse header são recusadas com 403) e `Accept: application/vnd.github+json` (formato recomendado
/// pela documentação da API). `internal`: só `UpdateService`/`SobreViewModel` (mesmo assembly) e os
/// testes (via `InternalsVisibleTo`) precisam dela.
internal sealed class GitHubUpdateSource : IUpdateSource, IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/maiconbiondo/mpdf-desktop/releases/latest";
    private readonly HttpClient _http;

    public GitHubUpdateSource()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"mPDF-UpdateChecker/{UpdateService.CurrentVersionText()}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<LatestRelease?> GetLatestAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(ReleasesUrl, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        string tagName = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        string? body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;

        string? assetName = null, assetUrl = null;
        long assetSize = 0;
        // Primeiro asset cujo nome termina em ".exe" — o release.ps1 (Task 1) publica exatamente 1
        // (o instalador Inno Setup); se algum dia houver mais de um, o primeiro encontrado vence.
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                assetName = name;
                assetUrl = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                assetSize = asset.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
                break;
            }
        }

        return new LatestRelease(tagName, body, assetName, assetUrl, assetSize);
    }

    public void Dispose() => _http.Dispose();
}

// ---- resultados tipados ---------------------------------------------------------------------------------

public sealed record UpdateInfo(
    string TagVersao, string? Notas, string UrlAsset, string NomeAsset, long TamanhoBytes, string Sha256Esperado);

public enum UpdateCheckStatus { Disponivel, Atualizado, Erro }

public enum UpdateErrorKind { SemRede, LimiteTaxa, SemAsset, SemHash }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info, UpdateErrorKind? ErroTipo, string? MensagemErro)
{
    public static UpdateCheckResult Disponivel(UpdateInfo info) => new(UpdateCheckStatus.Disponivel, info, null, null);
    public static UpdateCheckResult Atualizado() => new(UpdateCheckStatus.Atualizado, null, null, null);
    public static UpdateCheckResult Erro(UpdateErrorKind tipo, string mensagem) => new(UpdateCheckStatus.Erro, null, tipo, mensagem);
}

public enum DownloadStatus { Verificado, Recusado }

public sealed record DownloadResult(DownloadStatus Status, UpdateService.VerifiedUpdateFile? Arquivo, string? MensagemErro)
{
    public static DownloadResult Ok(UpdateService.VerifiedUpdateFile arquivo) => new(DownloadStatus.Verificado, arquivo, null);
    public static DownloadResult Falhou(string mensagem) => new(DownloadStatus.Recusado, null, mensagem);
}
