using System;
using System.IO;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

public class ImpressaoContextoTests
{
    [Fact] // "/print <pdf>" -> Silencioso + caminho
    public void Parse_Print_Silencioso()
    {
        var r = ImpressaoContextoService.Parse(new[] { "/print", @"C:\x.pdf" });
        Assert.Equal(ModoImpressaoContexto.Silencioso, r.Modo);
        Assert.Equal(@"C:\x.pdf", r.Caminho);
    }

    [Fact] // "/printadv <pdf>" -> Avancado + caminho
    public void Parse_PrintAdv_Avancado()
    {
        var r = ImpressaoContextoService.Parse(new[] { "/printadv", @"C:\x.pdf" });
        Assert.Equal(ModoImpressaoContexto.Avancado, r.Modo);
        Assert.Equal(@"C:\x.pdf", r.Caminho);
    }

    [Fact] // flag case-insensitive
    public void Parse_CaseInsensitive()
        => Assert.Equal(ModoImpressaoContexto.Silencioso, ImpressaoContextoService.Parse(new[] { "/PRINT", "a.pdf" }).Modo);

    [Fact] // "/print" sem caminho -> Silencioso, Caminho null
    public void Parse_PrintSemCaminho()
    {
        var r = ImpressaoContextoService.Parse(new[] { "/print" });
        Assert.Equal(ModoImpressaoContexto.Silencioso, r.Modo);
        Assert.Null(r.Caminho);
    }

    [Fact] // caminho sem flag (abrir normal) -> Nenhum
    public void Parse_CaminhoSemFlag_Nenhum()
        => Assert.Equal(ModoImpressaoContexto.Nenhum, ImpressaoContextoService.Parse(new[] { @"C:\x.pdf" }).Modo);

    [Fact] // sem args -> Nenhum
    public void Parse_SemArgs_Nenhum()
        => Assert.Equal(ModoImpressaoContexto.Nenhum, ImpressaoContextoService.Parse(Array.Empty<string>()).Modo);

    private sealed class FakeImpressora : IImpressoraPadrao
    {
        public bool ExisteResult = true;
        public mPdf.Documents.DocumentSession? SessaoRecebida;
        public string? TituloRecebido;
        public bool Existe() => ExisteResult;
        public void Imprimir(mPdf.Documents.DocumentSession session, string titulo) { SessaoRecebida = session; TituloRecebido = titulo; }
    }

    [Fact] // PDF valido + impressora existe -> Imprimir chamado com a sessao do arquivo certo
    public void Silencioso_ImprimeNaFilaPadrao()
    {
        var fake = new FakeImpressora();
        string? erro = null;
        var pdf = Path.Combine(Fixtures.Root, "fixture-30p.pdf");
        ImpressaoContextoService.ImprimirSilencioso(pdf, fake, e => erro = e);
        Assert.Null(erro);
        Assert.NotNull(fake.SessaoRecebida);
        Assert.Equal("fixture-30p", fake.TituloRecebido);
    }

    [Fact] // sem impressora padrao -> erro, nada impresso
    public void Silencioso_SemImpressora_Erro()
    {
        var fake = new FakeImpressora { ExisteResult = false };
        string? erro = null;
        ImpressaoContextoService.ImprimirSilencioso(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), fake, e => erro = e);
        Assert.NotNull(erro);
        Assert.Null(fake.SessaoRecebida);
    }

    [Fact] // caminho inexistente -> erro, nada aberto
    public void Silencioso_CaminhoInvalido_Erro()
    {
        var fake = new FakeImpressora();
        string? erro = null;
        ImpressaoContextoService.ImprimirSilencioso(@"C:\nao-existe-xyz.pdf", fake, e => erro = e);
        Assert.NotNull(erro);
        Assert.Null(fake.SessaoRecebida);
    }

    // Observação (skill-observations, "seam de serviço de diálogo"): toda seam que substitui um
    // componente real em teste cria um lado real que NUNCA roda em teste — sem isto,
    // `ImpressoraPadraoReal` poderia ter um construtor/`Existe()` quebrado (ex.: exceção não
    // capturada em `LocalPrintServer`) e nenhum teste jamais acusaria. Deliberadamente NÃO chama
    // `Imprimir` aqui: isso enviaria um job real para a fila padrão da máquina (sem `ShowDialog`,
    // vai direto pro spooler) — inadequado num teste automatizado/CI. `Existe()` é só leitura
    // (consulta o spooler local), seguro de executar de verdade em qualquer máquina, com ou sem
    // impressora instalada.
    [Fact]
    public void ImpressoraPadraoReal_Existe_NaoLancaEDevolveBool()
    {
        IImpressoraPadrao impressora = new ImpressoraPadraoReal();
        var existe = impressora.Existe(); // não deve lançar em nenhuma máquina (com ou sem impressora padrão)
        Assert.IsType<bool>(existe);
    }
}
