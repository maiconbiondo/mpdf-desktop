using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

// Obs 21 (revisão final pré-merge) — helper PURO/testável extraído de App.OnDispatcherUnhandledException
// (mesmo exemplar de CrashLogTests: o handler em si não é testável headless, a DECISÃO que ele delega é).
// 3 eixos documentados no XML de CrashDialogGate: TAXA (teto por janela deslizante), REENTRÂNCIA (2ª
// exceção com a caixa "aberta"), RESÍDUO (nada sobrevive além do necessário pra decidir a PRÓXIMA
// chamada). Tempo sempre passado EXPLICITAMENTE (DateTimeOffset) — nenhum Task.Delay/sleep nesta suíte.
public class CrashDialogGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- eixo TAXA -----------------------------------------------------------------------------------

    [Fact] // as constantes documentadas no XML da classe (>3 em 10s) são o CONTRATO testado abaixo —
    // travadas aqui como medição executável (designing-guard-rails: "medições que deveriam virar teste").
    public void Constants_MatchDocumentedContract()
    {
        Assert.Equal(3, CrashDialogGate.MaxDialogsPerWindow);
        Assert.Equal(TimeSpan.FromSeconds(10), CrashDialogGate.Window);
    }

    [Fact] // até o teto (3), cada TryEnter/Exit sequencial (sem reentrância) é aprovado.
    public void TryEnter_UpToMax_WithinWindow_AllApproved()
    {
        var gate = new CrashDialogGate();

        for (int i = 0; i < CrashDialogGate.MaxDialogsPerWindow; i++)
        {
            Assert.True(gate.TryEnter(T0.AddSeconds(i)), $"chamada {i + 1} deveria ser aprovada (dentro do teto)");
            gate.Exit();
        }
    }

    [Fact] // a (MaxDialogsPerWindow + 1)-ésima chamada DENTRO da mesma janela de 10s é recusada — um
    // timer/render-loop que falha em CADA tick não pode virar um loop infinito de modais.
    public void TryEnter_ExceedsMaxWithinWindow_Rejected()
    {
        var gate = new CrashDialogGate();
        for (int i = 0; i < CrashDialogGate.MaxDialogsPerWindow; i++)
        {
            Assert.True(gate.TryEnter(T0.AddSeconds(i)));
            gate.Exit();
        }

        bool approved = gate.TryEnter(T0.AddSeconds(CrashDialogGate.MaxDialogsPerWindow)); // ainda dentro de 10s do 1º timestamp

        Assert.False(approved);
    }

    [Fact] // RESÍDUO: passado o tamanho da janela, os timestamps antigos são PODADOS — o teto de taxa
    // não fica preso permanentemente, só durante a janela deslizante de 10s.
    public void TryEnter_AfterWindowElapses_OldTimestampsPruned_ApprovedAgain()
    {
        var gate = new CrashDialogGate();
        for (int i = 0; i < CrashDialogGate.MaxDialogsPerWindow; i++)
        {
            Assert.True(gate.TryEnter(T0.AddSeconds(i)));
            gate.Exit();
        }
        Assert.False(gate.TryEnter(T0.AddSeconds(CrashDialogGate.MaxDialogsPerWindow))); // sanity: teto ainda vale aqui

        bool approvedAfterWindow = gate.TryEnter(T0 + CrashDialogGate.Window + TimeSpan.FromSeconds(1));

        Assert.True(approvedAfterWindow);
    }

    // ---- eixo REENTRÂNCIA ------------------------------------------------------------------------------

    [Fact] // uma 2ª chamada enquanto a 1ª ainda não chamou Exit() (caixa modal "aberta") é recusada —
    // MESMO havendo espaço na janela de taxa (só a 1ª entrada foi registrada).
    public void TryEnter_WhileStillShowing_Rejected_RegardlessOfRateWindow()
    {
        var gate = new CrashDialogGate();
        Assert.True(gate.TryEnter(T0)); // 1ª — aprovada, NÃO chama Exit (simula caixa ainda aberta)

        bool secondApproved = gate.TryEnter(T0); // 2ª exceção chega "durante" a modal da 1ª

        Assert.False(secondApproved);
    }

    [Fact] // Exit() libera a seção crítica — uma chamada SEGUINTE (após a caixa fechar) volta a ser aprovada.
    public void TryEnter_AfterExit_ApprovedAgain()
    {
        var gate = new CrashDialogGate();
        Assert.True(gate.TryEnter(T0));
        gate.Exit();

        bool approved = gate.TryEnter(T0);

        Assert.True(approved);
    }

    // ---- eixo RESÍDUO (Exit chamado mesmo após uma "exceção" simulada no caminho do chamador) ---------

    [Fact] // contrato do chamador: Exit() SEMPRE roda num finally, mesmo se o "MessageBox.Show" (aqui,
    // simulado por uma exceção síncrona qualquer) lançar — o gate nunca pode ficar preso em "mostrando".
    public void Exit_CalledFromFinally_EvenAfterSimulatedShowFailure_ReleasesGate()
    {
        var gate = new CrashDialogGate();
        Assert.True(gate.TryEnter(T0));
        try
        {
            try { throw new InvalidOperationException("MessageBox.Show falhou (simulado)"); }
            finally { gate.Exit(); } // mesmo padrão do handler real (App.OnDispatcherUnhandledException)
        }
        catch (InvalidOperationException) { /* esperado — só provando que Exit() ainda rodou */ }

        bool approvedAfter = gate.TryEnter(T0);

        Assert.True(approvedAfter);
    }
}
