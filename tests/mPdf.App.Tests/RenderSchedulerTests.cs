using mPdf.App.Services;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

public class RenderSchedulerTests
{
    private static RenderedPage FakePage(int i) => new(1, 1, new byte[4]);

    [Fact] // pedido é atendido e o callback recebe página/escala corretos
    public void Request_InvokesCallback()
    {
        using var done = new ManualResetEventSlim();
        (int page, double scale) got = (-1, 0);
        using var s = new RenderScheduler((i, sc) => FakePage(i));
        s.Request(3, 1.5, (i, sc, p) => { got = (i, sc); done.Set(); });
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal((3, 1.5), got);
    }

    [Fact] // CancelPending descarta pedidos ainda não processados
    public void CancelPending_DropsQueuedRequests()
    {
        using var gate = new ManualResetEventSlim();      // segura o worker no 1º render
        using var first = new ManualResetEventSlim();
        int rendered = 0;
        using var s = new RenderScheduler((i, sc) => { first.Set(); gate.Wait(); return FakePage(i); });
        s.Request(0, 1.0, (_, _, _) => Interlocked.Increment(ref rendered));
        Assert.True(first.Wait(TimeSpan.FromSeconds(5))); // worker ocupado no page 0
        s.Request(1, 1.0, (_, _, _) => Interlocked.Increment(ref rendered));
        s.Request(2, 1.0, (_, _, _) => Interlocked.Increment(ref rendered));
        s.CancelPending();                                 // derruba 1 e 2 (0 já está em curso)
        gate.Set();
        Thread.Sleep(300);
        Assert.True(rendered <= 1, $"esperado <=1 render, houve {rendered}");
    }

    [Fact] // segundo pedido da MESMA página substitui o primeiro (última escala vence)
    public void Request_SamePageTwice_LastWins()
    {
        using var gate = new ManualResetEventSlim();
        using var first = new ManualResetEventSlim();
        var scales = new List<double>();
        using var s = new RenderScheduler((i, sc) => { first.Set(); gate.Wait(); return FakePage(i); });
        s.Request(0, 1.0, (_, _, _) => { });               // ocupa o worker
        Assert.True(first.Wait(TimeSpan.FromSeconds(5)));
        s.Request(7, 1.0, (_, sc, _) => { lock (scales) scales.Add(sc); });
        s.Request(7, 2.0, (_, sc, _) => { lock (scales) scales.Add(sc); });
        gate.Set();
        Thread.Sleep(300);
        lock (scales) Assert.Equal([2.0], scales);
    }

    [Fact] // Cancel(page) remove pedido enfileirado daquela página, preserva os demais
    public void Cancel_RemovesOnlyThatPage()
    {
        using var gate = new ManualResetEventSlim();
        using var first = new ManualResetEventSlim();
        var rendered = new List<int>();
        using var doneAll = new CountdownEvent(2);         // esperamos exatamente 0 e 2
        using var s = new RenderScheduler((i, sc) => { if (i == 0) { first.Set(); gate.Wait(); } return FakePage(i); });
        s.Request(0, 1.0, (i, _, _) => { lock (rendered) rendered.Add(i); if (doneAll.CurrentCount > 0) doneAll.Signal(); });
        Assert.True(first.Wait(TimeSpan.FromSeconds(5)));   // worker preso no page 0
        s.Request(1, 1.0, (i, _, _) => { lock (rendered) rendered.Add(i); if (doneAll.CurrentCount > 0) doneAll.Signal(); });
        s.Request(2, 1.0, (i, _, _) => { lock (rendered) rendered.Add(i); if (doneAll.CurrentCount > 0) doneAll.Signal(); });
        s.Cancel(1);
        gate.Set();
        Assert.True(doneAll.Wait(TimeSpan.FromSeconds(5)), "esperava 0 e 2 renderizarem");
        Thread.Sleep(200); // folga para garantir que a página 1 (cancelada) não chegue atrasada
        lock (rendered) Assert.Equal([0, 2], rendered);
    }

    [Fact] // ordem FIFO por primeira solicitação
    public void Requests_ServedInFifoOrder()
    {
        using var gate = new ManualResetEventSlim();
        using var first = new ManualResetEventSlim();
        var order = new List<int>();
        using var doneAll = new CountdownEvent(4);          // 0, 5, 3, 9
        using var s = new RenderScheduler((i, sc) => { if (i == 0) { first.Set(); gate.Wait(); } return FakePage(i); });
        s.Request(0, 1.0, (i, _, _) => { lock (order) order.Add(i); doneAll.Signal(); });
        Assert.True(first.Wait(TimeSpan.FromSeconds(5)));   // worker preso no page 0
        s.Request(5, 1.0, (i, _, _) => { lock (order) order.Add(i); doneAll.Signal(); });
        s.Request(3, 1.0, (i, _, _) => { lock (order) order.Add(i); doneAll.Signal(); });
        s.Request(9, 1.0, (i, _, _) => { lock (order) order.Add(i); doneAll.Signal(); });
        gate.Set();
        Assert.True(doneAll.Wait(TimeSpan.FromSeconds(5)), "esperava 4 callbacks");
        lock (order) Assert.Equal([0, 5, 3, 9], order);
    }
}
