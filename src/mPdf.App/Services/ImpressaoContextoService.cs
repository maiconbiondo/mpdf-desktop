using System;
using System.IO;
using mPdf.Documents;

namespace mPdf.App.Services;

public enum ModoImpressaoContexto { Nenhum, Silencioso, Avancado }

public readonly record struct ImpressaoContextoArgs(ModoImpressaoContexto Modo, string? Caminho);

/// Seam da impressora padrao do Windows (Task 1): quem CONHECE a fila padrao (DPI, tamanho de folha)
/// e monta o paginator e quem imprime, NUNCA o serviço acima — isso mantem `ImprimirSilencioso`
/// testavel com um fake sem fila de impressao nenhuma (ver `ImpressoraPadraoReal`, a unica
/// implementacao de producao).
public interface IImpressoraPadrao
{
    bool Existe();
    void Imprimir(DocumentSession session, string titulo);
}

/// Impressao pelo menu de contexto do Windows (verbos /print e /printadv). O App.OnStartup roteia
/// ANTES da instancia unica; a logica testavel (parsing + impressao silenciosa) mora aqui.
public static class ImpressaoContextoService
{
    public static ImpressaoContextoArgs Parse(string[] args)
    {
        if (args.Length == 0) return new(ModoImpressaoContexto.Nenhum, null);
        var flag = args[0];
        var caminho = args.Length > 1 ? args[1] : null;
        if (string.Equals(flag, "/print", StringComparison.OrdinalIgnoreCase))
            return new(ModoImpressaoContexto.Silencioso, caminho);
        if (string.Equals(flag, "/printadv", StringComparison.OrdinalIgnoreCase))
            return new(ModoImpressaoContexto.Avancado, caminho);
        return new(ModoImpressaoContexto.Nenhum, null);
    }

    /// Orquestracao pura (validar -> abrir -> despachar): NUNCA lanca pra fora — todo erro (arquivo
    /// invalido, sem impressora padrao, falha ao abrir/imprimir) vira `reportarErro`. Quem monta o
    /// paginator (DPI/tamanho de folha da fila padrao) e a `impressora` (seam), nao este metodo — e
    /// isso que permite testar a orquestracao inteira com um fake sem PrintQueue real.
    public static void ImprimirSilencioso(string? caminho, IImpressoraPadrao impressora, Action<string> reportarErro)
    {
        if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho)
            || !caminho.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            reportarErro("Arquivo PDF nao encontrado ou invalido.");
            return;
        }
        if (!impressora.Existe())
        {
            reportarErro("Nenhuma impressora padrao configurada no Windows.");
            return;
        }
        try
        {
            var session = DocumentSession.Open(caminho);
            impressora.Imprimir(session, Path.GetFileNameWithoutExtension(caminho));
        }
        catch (Exception ex) { reportarErro($"Nao foi possivel imprimir: {ex.Message}"); }
    }
}

/// Producao: fila PADRAO do Windows + `PrintDialog.PrintDocument` SEM `ShowDialog` (imprime direto,
/// sem abrir janela nenhuma) — mesmo mecanismo de `DialogoImpressao.Imprimir_Click`, so que aqui a
/// fila/ticket vem de `LocalPrintServer().DefaultPrintQueue` em vez de um picker de UI.
public sealed class ImpressoraPadraoReal : IImpressoraPadrao
{
    public bool Existe()
    {
        try { return new System.Printing.LocalPrintServer().DefaultPrintQueue is not null; }
        catch { return false; }
    }

    public void Imprimir(DocumentSession session, string titulo)
    {
        var fila = new System.Printing.LocalPrintServer().DefaultPrintQueue;
        var ticket = fila.DefaultPrintTicket;
        double dpi = PrintService.ResolveDpi(ticket);                 // exemplar: DialogoImpressao.Imprimir_Click
        var pag = new PdfPrintPaginator(session, dpi, range: null);   // todas as paginas
        // PageMediaSize.Width/Height vem em DIPs (1/96") no System.Printing — mesma unidade de
        // PageSize (confirmado em DialogoImpressao.AtualizarPapelDoTicket). Se a fila nao declarar
        // folha (media nula ou com dimensao <= 0 — comum em tickets minimalistas de "Microsoft Print
        // to PDF"/"XPS Document Writer"), o default do paginator e Size(0,0) -> paginas em branco/
        // tamanho zero (mesma classe de regressao que o comentario "C1" em PrintService.cs e o
        // fallback A4 de DialogoImpressao existem pra evitar). Mesma disciplina do caminho
        // interativo: usa a folha do ticket so quando as duas dimensoes sao > 0, senao cai pra A4.
        var media = ticket.PageMediaSize;
        pag.PageSize = (media?.Width is double w and > 0 && media?.Height is double h and > 0)
            ? new System.Windows.Size(w, h)
            : new System.Windows.Size(794, 1123); // A4 em DIPs — mesmo fallback de DialogoImpressao
        try
        {
            var pd = new System.Windows.Controls.PrintDialog { PrintQueue = fila, PrintTicket = ticket };
            pd.PrintDocument(pag, titulo);   // sem ShowDialog -> sem janela, imprime direto
        }
        finally { PendingDisposals.Enqueue(pag.Dispose); } // mesma disciplina de DialogoImpressao.Imprimir_Click
    }
}
