using System.Linq;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

// Task 4 (Plano 3b): PageRangeParser — parser PURO (sem WPF/PDF) da string de intervalos do diálogo de
// Dividir. Table-driven: casos válidos codificam o resultado esperado como "from-to;from-to..." (já
// 0-based) para comparação direta, sem precisar de Assert.Equal em cima de tuplas.
public class PageRangeParserTests
{
    [Theory]
    [InlineData("1-5", 10, "0-4")]
    [InlineData("1-5, 8, 10-12", 12, "0-4;7-7;9-11")]
    [InlineData("1", 5, "0-0")]
    [InlineData("1,2,3", 3, "0-0;1-1;2-2")]
    [InlineData(" 1 - 5 , 8 ", 10, "0-4;7-7")] // espaços em volta de números/traço/vírgulas tolerados
    [InlineData("1-3,,5", 5, "0-2;4-4")] // vírgula duplicada tolerada (token vazio ignorado)
    [InlineData("1-10", 10, "0-9")] // intervalo cobrindo o documento inteiro (limite superior exato)
    // Fix pós-revisão (minor 2): OVERLAPS PERMITIDOS (decisão registrada no doc XML de Parse) — prova
    // POSITIVA de que nenhuma lógica de dedup/rejeição de sobreposição existe: os dois ranges saem
    // intactos, mesmo cobrindo a página 3 duas vezes ("1-5" e "3-8" compartilham 3-5).
    [InlineData("1-5,3-8", 10, "0-4;2-7")]
    public void Parse_ValidInputs_ReturnsExpectedZeroBasedRanges(string input, int pageCount, string expected)
    {
        var result = PageRangeParser.Parse(input, pageCount);
        var actual = string.Join(";", result.Select(r => $"{r.from}-{r.to}"));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Parse_EmptyInput_ThrowsPortugueseMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => PageRangeParser.Parse("", 10));
        Assert.Contains("intervalo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_WhitespaceOnlyInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => PageRangeParser.Parse("   ", 10));
    }

    [Fact]
    public void Parse_OnlyCommas_ThrowsSameEmptyMessage() // "1,,,\n" - todos os tokens vazios, nenhum válido sobra
    {
        var ex = Assert.Throws<ArgumentException>(() => PageRangeParser.Parse(",,,", 10));
        Assert.Contains("intervalo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0-5", 10)] // 0 não existe (página 1 é a primeira)
    [InlineData("1-11", 10)] // fim além do total de páginas
    [InlineData("15", 10)]  // token único fora dos limites
    public void Parse_OutOfBounds_ThrowsNamingBadTokenAndLimits(string input, int pageCount)
    {
        var ex = Assert.Throws<ArgumentException>(() => PageRangeParser.Parse(input, pageCount));
        Assert.Contains($"1-{pageCount}", ex.Message); // limites citados na mensagem pt-BR
    }

    [Fact]
    public void Parse_ReversedRange_ThrowsNamingToken()
    {
        var ex = Assert.Throws<ArgumentException>(() => PageRangeParser.Parse("10-5", 12));
        Assert.Contains("10-5", ex.Message);
    }

    [Theory]
    [InlineData("abc", 10)]
    [InlineData("1-abc", 10)]
    [InlineData("abc-5", 10)]
    [InlineData("1--5", 10)]
    [InlineData("5-", 10)]
    public void Parse_MalformedToken_ThrowsNamingToken(string input, int pageCount)
    {
        var ex = Assert.Throws<ArgumentException>(() => PageRangeParser.Parse(input, pageCount));
        Assert.Contains(input.Trim(), ex.Message);
    }

    [Fact]
    public void Parse_ZeroPageCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => PageRangeParser.Parse("1", 0));
    }

    // Prova de mutação (brief): comentar a checagem `to1 > pageCount` faria este teste falhar — o limite
    // exato (pageCount) continua válido, mas pageCount+1 (1 além) precisa continuar lançando.
    [Fact]
    public void Parse_UpperBoundCheck_MutationProof_ExactLimitPassesOneBeyondFails()
    {
        var ok = PageRangeParser.Parse("1-10", 10);
        Assert.Single(ok);
        Assert.Equal((0, 9), ok[0]);

        Assert.Throws<ArgumentException>(() => PageRangeParser.Parse("1-11", 10));
    }
}
