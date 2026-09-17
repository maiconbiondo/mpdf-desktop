using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace mPdf.App.Services;

public enum ResultadoUpdateCli { JaNaUltima, AtualizacaoDisponivel, AtualizacaoIniciada, Erro }

public sealed record StatusUpdateCli(
    ResultadoUpdateCli Resultado, string? VersaoInstalada, string? VersaoDisponivel, string? MensagemErro);

/// Orquestra a atualizacao SILENCIOSA (sem UI/confirmacao) para os modos CLI /checkupdate e /update.
/// Reusa UpdateService (unica rede) + SilentUpdateInstaller. Constroi o UpdateService (2o sitio
/// deliberado — ver UpdateNetworkConfinementTests).
public sealed class SilentUpdateRunner
{
    private readonly Func<IUpdateSource> _sourceFactory;
    private readonly Action<string> _lancarInstalador;

    public SilentUpdateRunner(Func<IUpdateSource>? sourceFactory = null, Action<string>? lancarInstalador = null)
    {
        _sourceFactory = sourceFactory ?? UiPrompts.CreateUpdateSource;
        _lancarInstalador = lancarInstalador ?? (p => Process.Start(SilentUpdateInstaller.BuildStartInfo(p)));
    }

    // UNICO ponto de `new UpdateService(` neste arquivo (a guarda conta 2 no repo: aqui + ConfiguracoesViewModel).
    private UpdateService CriarService() => new UpdateService(_sourceFactory());

    public async Task<StatusUpdateCli> VerificarAsync(CancellationToken ct)
    {
        var instalada = UpdateService.CurrentVersionText();
        try
        {
            using var service = CriarService();
            var r = await service.VerificarAsync(ct);
            return r.Status switch
            {
                UpdateCheckStatus.Atualizado => new(ResultadoUpdateCli.JaNaUltima, instalada, instalada, null),
                UpdateCheckStatus.Disponivel => new(ResultadoUpdateCli.AtualizacaoDisponivel, instalada, r.Info!.TagVersao, null),
                _ => new(ResultadoUpdateCli.Erro, instalada, null, r.MensagemErro),
            };
        }
        catch (Exception ex) { return new(ResultadoUpdateCli.Erro, instalada, null, ex.Message); }
    }

    public async Task<StatusUpdateCli> AtualizarAsync(CancellationToken ct)
    {
        var instalada = UpdateService.CurrentVersionText();
        try
        {
            using var service = CriarService();
            var r = await service.VerificarAsync(ct);
            if (r.Status == UpdateCheckStatus.Atualizado)
                return new(ResultadoUpdateCli.JaNaUltima, instalada, instalada, null);
            if (r.Status != UpdateCheckStatus.Disponivel)
                return new(ResultadoUpdateCli.Erro, instalada, null, r.MensagemErro);

            var dr = await service.BaixarEVerificarAsync(r.Info!, null, ct);
            // DownloadResult(DownloadStatus Status, VerifiedUpdateFile? Arquivo, string? MensagemErro):
            //  Verificado = sucesso (Arquivo != null), Recusado = falha (MensagemErro).
            if (dr.Status != DownloadStatus.Verificado || dr.Arquivo is null)
                return new(ResultadoUpdateCli.Erro, instalada, r.Info!.TagVersao, dr.MensagemErro);
            return await InstalarVerificadoAsync(dr.Arquivo, instalada, r.Info!.TagVersao);
        }
        catch (Exception ex) { return new(ResultadoUpdateCli.Erro, instalada, null, ex.Message); }
    }

    internal Task<StatusUpdateCli> InstalarVerificadoAsync(
        UpdateService.VerifiedUpdateFile arquivo, string? versaoInstalada, string? versaoDisponivel)
    {
        try
        {
            _lancarInstalador(arquivo.CaminhoArquivo);
            return Task.FromResult(new StatusUpdateCli(ResultadoUpdateCli.AtualizacaoIniciada, versaoInstalada, versaoDisponivel, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new StatusUpdateCli(ResultadoUpdateCli.Erro, versaoInstalada, versaoDisponivel, ex.Message));
        }
    }

    public static int MapearExitCode(ResultadoUpdateCli r) => r switch
    {
        ResultadoUpdateCli.JaNaUltima => 0,
        ResultadoUpdateCli.AtualizacaoDisponivel => 10,
        ResultadoUpdateCli.AtualizacaoIniciada => 10,
        _ => 20,
    };

    public static string FormatarStatus(string comando, StatusUpdateCli s)
    {
        // ASCII apenas. Ex.: "mPDF /checkupdate: instalada=2.9.0 disponivel=2.10.0 atualizar=sim"
        var inst = s.VersaoInstalada ?? "-";
        return s.Resultado switch
        {
            ResultadoUpdateCli.Erro => $"mPDF {comando}: erro={s.MensagemErro ?? "desconhecido"}",
            ResultadoUpdateCli.AtualizacaoIniciada => $"mPDF {comando}: instalada={inst} disponivel={s.VersaoDisponivel ?? "-"} acao=instalando",
            ResultadoUpdateCli.AtualizacaoDisponivel => $"mPDF {comando}: instalada={inst} disponivel={s.VersaoDisponivel ?? "-"} atualizar=sim",
            _ => $"mPDF {comando}: instalada={inst} disponivel={inst} atualizar=nao",
        };
    }
}
