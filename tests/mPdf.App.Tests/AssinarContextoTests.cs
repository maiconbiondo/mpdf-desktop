using System;
using System.IO;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

// v2.13.0: verbo /assinar <pdf> (parsing puro) + formato da linha de comando do pipe.
public class AssinarContextoTests
{
    [Fact] // "/assinar <pdf>" -> (true, caminho)
    public void Parse_Assinar_ComCaminho()
    {
        var r = AssinarContextoService.Parse(new[] { "/assinar", @"C:\x\a.pdf" });
        Assert.Equal(new AssinarContextoArgs(true, @"C:\x\a.pdf"), r);
    }

    [Fact] // verbo sem diferenca de caixa
    public void Parse_Assinar_Maiusculas()
    {
        var r = AssinarContextoService.Parse(new[] { "/ASSINAR", @"C:\x\a.pdf" });
        Assert.True(r.Assinar);
        Assert.Equal(@"C:\x\a.pdf", r.Caminho);
    }

    [Fact] // "/assinar" sem caminho (ou caminho em branco) -> (true, null): o App mostra erro e sai
    public void Parse_Assinar_SemCaminho()
    {
        Assert.Equal(new AssinarContextoArgs(true, null), AssinarContextoService.Parse(new[] { "/assinar" }));
        Assert.Equal(new AssinarContextoArgs(true, null), AssinarContextoService.Parse(new[] { "/assinar", "  " }));
    }

    [Fact] // sem verbo -> (false, null); caminho puro tambem (segue o fluxo normal de abrir)
    public void Parse_SemVerbo()
    {
        Assert.Equal(new AssinarContextoArgs(false, null), AssinarContextoService.Parse(Array.Empty<string>()));
        Assert.Equal(new AssinarContextoArgs(false, null), AssinarContextoService.Parse(new[] { @"C:\x\a.pdf" }));
        // outros verbos nao sao /assinar
        Assert.Equal(new AssinarContextoArgs(false, null), AssinarContextoService.Parse(new[] { "/print", @"C:\x\a.pdf" }));
    }

    [Fact] // caminho relativo vira absoluto antes de ir pro pipe (a primaria so aceita absoluto)
    public void CaminhoAbsoluto_RelativoViraAbsoluto()
    {
        var abs = AssinarContextoService.CaminhoAbsoluto("a.pdf");
        Assert.True(Path.IsPathRooted(abs));
        Assert.Equal(Path.GetFullPath("a.pdf"), abs);
        Assert.Equal(@"C:\x\a.pdf", AssinarContextoService.CaminhoAbsoluto(@"C:\x\a.pdf"));
    }

    // ---- protocolo do pipe (linha de comando) ------------------------------------------------------

    [Fact] // formato exato da linha: ?assinar|<caminho>
    public void Protocolo_MontarLinha_FormatoExato()
        => Assert.Equal(@"?assinar|C:\x\a.pdf", ProtocoloInstanciaUnica.MontarLinha(VerboInstancia.Assinar, @"C:\x\a.pdf"));

    [Fact] // ida e volta, inclusive com espacos/acentos no caminho e verbo em maiusculas
    public void Protocolo_TryParse_IdaEVolta()
    {
        var caminho = @"C:\Usuários\Fulano\Relatório (2026) — final.pdf";
        Assert.True(ProtocoloInstanciaUnica.TryParse(ProtocoloInstanciaUnica.MontarLinha(VerboInstancia.Assinar, caminho), out var c));
        Assert.Equal(new ComandoInstancia(VerboInstancia.Assinar, caminho), c);
        Assert.True(ProtocoloInstanciaUnica.TryParse(@"?ASSINAR|\\servidor\share\a.pdf", out var c2));
        Assert.Equal(@"\\servidor\share\a.pdf", c2.Caminho);
    }

    [Theory] // linhas de comando malformadas/desconhecidas -> falso (ignoradas pela primaria)
    [InlineData(@"?qualquercoisa|C:\x.pdf")] // verbo desconhecido (ex.: de uma versao futura)
    [InlineData("?qualquercoisa|x")]
    [InlineData(@"?assinar")]                // sem separador
    [InlineData(@"?assinar|")]               // sem caminho
    [InlineData(@"?assinar|relativo.pdf")]   // caminho nao-absoluto
    [InlineData(@"?|C:\x.pdf")]              // verbo vazio
    [InlineData(@"C:\x.pdf")]                // caminho puro nao e comando
    public void Protocolo_TryParse_Invalida_Falso(string linha)
        => Assert.False(ProtocoloInstanciaUnica.TryParse(linha, out _));

    [Theory] // nenhum caminho absoluto do Windows comeca com o prefixo de comando — um caminho puro
             // nunca e confundido com comando
    [InlineData(@"C:\x.pdf")]
    [InlineData(@"\\servidor\share\x.pdf")]
    [InlineData(@"\\?\C:\x.pdf")]
    [InlineData(@"\x.pdf")]
    public void Protocolo_CaminhoAbsoluto_NuncaEhComando(string caminho)
    {
        Assert.True(Path.IsPathRooted(caminho));
        Assert.False(ProtocoloInstanciaUnica.EhComando(caminho));
    }

    [Fact] // COMPATIBILIDADE: a primaria 2.12.x so repassa linhas com Path.IsPathRooted verdadeiro
           // (SingleInstanceService.ListenLoopAsync ate a v2.12.2) — a linha nova e falsa ali, entao
           // cai no ramo "malformada, ignorada" sem excecao
    public void Protocolo_LinhaNova_NaoEhRaizParaPrimariaAntiga()
    {
        var linha = ProtocoloInstanciaUnica.MontarLinha(VerboInstancia.Assinar, @"C:\x\a.pdf");
        Assert.False(Path.IsPathRooted(linha));
        Assert.False(Path.IsPathRooted(ProtocoloInstanciaUnica.MontarLinha(VerboInstancia.Assinar, @"\\servidor\share\a.pdf")));
    }
}
