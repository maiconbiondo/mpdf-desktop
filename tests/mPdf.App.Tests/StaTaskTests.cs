using System;
using System.Threading;
using System.Threading.Tasks;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

public class StaTaskTests
{
    [Fact] // o trabalho roda numa thread STA (exigência do diálogo de PIN do token/A3 — ver StaTask)
    public async Task Run_ExecutaEmThreadSta()
    {
        var ap = await StaTask.Run(() => Thread.CurrentThread.GetApartmentState());
        Assert.Equal(ApartmentState.STA, ap);
    }

    [Fact] // devolve o valor produzido pelo func
    public async Task Run_DevolveResultado()
    {
        var r = await StaTask.Run(() => 40 + 2);
        Assert.Equal(42, r);
    }

    [Fact] // exceção do func propaga pelo Task (não some, não crasha a thread) — a UI trata a mensagem
    public async Task Run_PropagaExcecao()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StaTask.Run<int>(() => throw new InvalidOperationException("falha-de-teste")));
        Assert.Equal("falha-de-teste", ex.Message);
    }

    [Fact] // é uma thread de fundo (não segura o processo aberto)
    public async Task Run_ThreadEhDeFundo()
    {
        var ehBackground = await StaTask.Run(() => Thread.CurrentThread.IsBackground);
        Assert.True(ehBackground);
    }
}
