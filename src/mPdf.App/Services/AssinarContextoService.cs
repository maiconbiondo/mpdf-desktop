using System;
using System.IO;

namespace mPdf.App.Services;

public readonly record struct AssinarContextoArgs(bool Assinar, string? Caminho);

/// Verbo de linha de comando `/assinar <pdf>` (v2.13.0, pedido do mOffice: exporta o PDF e chama
/// `mPdf.App.exe /assinar "<pdf>"`). Diferente de `/print`/`/printadv` (processo efemero), aqui o PDF
/// abre NUMA ABA do editor e o comando de assinar do documento roda em seguida — com o mPDF fechado,
/// esta instancia vira a primaria; com ele aberto, o pedido vai pelo pipe da instancia unica (linha
/// `?assinar|<caminho>`, ver `ProtocoloInstanciaUnica`) e abre na janela existente. O App.OnStartup
/// roteia; a logica testavel (parsing) mora aqui.
public static class AssinarContextoService
{
    public const string Verbo = "/assinar";

    /// `/assinar` so e reconhecido em args[0], sem diferenca de caixa. Verbo ausente -> `Assinar=false`
    /// (inclusive um caminho puro, que segue o fluxo normal de abrir). `/assinar` sem caminho (ou com
    /// caminho em branco) -> `Assinar=true, Caminho=null` (o App mostra erro e sai).
    public static AssinarContextoArgs Parse(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], Verbo, StringComparison.OrdinalIgnoreCase))
            return new(false, null);
        var caminho = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : null;
        return new(true, caminho);
    }

    /// Caminho ABSOLUTO pro encaminhamento pelo pipe (a primaria so aceita caminho absoluto — o
    /// diretorio de trabalho desta instancia secundaria nao significa nada pra ela). Best-effort: um
    /// caminho que `GetFullPath` recusa volta como veio (a validacao de verdade — existe? e PDF? — e de
    /// `MainViewModel.AbrirEAssinarAsync`, com mensagem ao usuario).
    public static string CaminhoAbsoluto(string caminho)
    {
        try { return Path.GetFullPath(caminho); }
        catch (Exception) { return caminho; }
    }
}
