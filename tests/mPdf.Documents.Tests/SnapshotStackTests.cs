using mPdf.Documents;
using Xunit;

namespace mPdf.Documents.Tests;

// Task 4 (Plano 3a): SnapshotStack é PURA — não conhece PDF/DocumentSession, opera sobre byte[]
// opacos. Testada isolada com arrays minúsculos (nenhum fixture de PDF necessário aqui) + limite de
// RAM injetável (o ctor recebe maxInMemory), exatamente como o brief pede.
public class SnapshotStackTests
{
    private static string NewSpillDir() => Path.Combine(Path.GetTempPath(), $"mpdf-snap-{Guid.NewGuid():N}");

    [Fact]
    public void NewStack_CanUndoAndCanRedo_AreFalse()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir);

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact] // pilha vazia: Undo/Redo devolvem null, nunca lançam — quem chama (DocumentSession) usa o
    // null como "nada a fazer" (ver DocumentSessionTests: Undo_NothingToUndo_IsNoOp).
    public void Undo_EmptyStack_ReturnsNull_DoesNotThrow()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir);

        Assert.Null(stack.Undo([1, 2, 3]));
    }

    [Fact]
    public void Redo_EmptyStack_ReturnsNull_DoesNotThrow()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir);

        Assert.Null(stack.Redo([1, 2, 3]));
    }

    [Fact] // Push empilha o snapshot PRÉ-edição; Undo(current) desempilha e devolve a MESMA referência
    // (sem cópia defensiva — contrato de Apply, Task 3), empurrando `current` pro redo.
    public void Push_ThenUndo_ReturnsPushedSnapshot_AndFlipsCanUndoCanRedo()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir);
        byte[] pre = [1], current = [2];

        stack.Push(pre);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);

        var result = stack.Undo(current);

        Assert.Same(pre, result);
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
    }

    [Fact] // sequência completa: 2 edições, desfaz as 2, refaz as 2 — round-trip exato (mesmas
    // referências de volta, LIFO respeitado nos dois sentidos).
    public void Sequence_PushPushUndoUndoRedoRedo_RoundTripsExactly()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir);
        byte[] s0 = [0], s1 = [1], s2 = [2];

        stack.Push(s0); // edição 1: s0 -> s1
        stack.Push(s1); // edição 2: s1 -> s2 (current agora é s2)

        Assert.Same(s1, stack.Undo(s2)); // volta pra s1
        Assert.Same(s0, stack.Undo(s1)); // volta pra s0
        Assert.False(stack.CanUndo);

        Assert.Same(s1, stack.Redo(s0)); // avança pra s1
        Assert.Same(s2, stack.Redo(s1)); // avança pra s2
        Assert.False(stack.CanRedo);
    }

    [Fact] // MUTATION-PROVÁVEL (regra do brief): Push de uma edição NOVA limpa qualquer redo pendente
    // — comentar a chamada de Clear() dentro de SnapshotStack.Push faz este teste falhar (prova de
    // disparo documentada no relatório da Task 4).
    public void Push_ClearsRedoStack()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir);
        byte[] s0 = [0], s1 = [1];
        stack.Push(s0);
        stack.Undo(s1); // agora há 1 entrada no redo
        Assert.True(stack.CanRedo);

        stack.Push(s1); // nova edição — deve limpar o redo

        Assert.False(stack.CanRedo);
    }

    [Fact] // teto=2 injetado, 4 pushes SEM pop no meio -> as 2 entradas mais ANTIGAS (as que caíram
    // fora da janela dos 2 mais recentes) viram arquivo em disco; exatamente o cenário do brief.
    public void Spill_BeyondMaxInMemory_MovesOldestEntriesToDisk()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(2, dir);

        stack.Push([1]);
        stack.Push([2]);
        stack.Push([3]);
        stack.Push([4]);

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length);
    }

    [Fact] // restauração byte-a-byte idêntica mesmo vindo do disco — conteúdos DISTINGUÍVEIS em cada
    // posição provam que não há mistura/corrupção entre entradas espalhadas.
    public void Spill_RestoreIsByteIdentical_EvenFromDisk()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(2, dir);
        byte[] s0 = [10], s1 = [20], s2 = [30], s3 = [40], s4 = [50];
        stack.Push(s0);
        stack.Push(s1);
        stack.Push(s2);
        stack.Push(s3); // agora s0 e s1 estão em disco; s2/s3 em RAM

        byte[] current = s4;
        current = stack.Undo(current)!; Assert.Equal(s3, current);
        current = stack.Undo(current)!; Assert.Equal(s2, current);
        current = stack.Undo(current)!; Assert.Equal(s1, current); // vem do disco
        current = stack.Undo(current)!; Assert.Equal(s0, current); // vem do disco
        Assert.False(stack.CanUndo);
    }

    [Fact] // Dispose apaga a pasta de spill inteira (mesmo sem nada nela ainda) — nenhum arquivo
    // temporário sobra no %TEMP% do usuário depois que a sessão fecha.
    public void Dispose_DeletesSpillDirectory()
    {
        var dir = NewSpillDir();
        var stack = new SnapshotStack(2, dir);
        stack.Push([1]);
        stack.Push([2]);
        stack.Push([3]); // força pelo menos 1 spill de verdade
        Assert.True(Directory.Exists(dir));

        stack.Dispose();

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Push_NullSnapshot_Throws()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir);

        Assert.Throws<ArgumentNullException>(() => stack.Push(null!));
    }

    [Fact]
    public void Ctor_MaxInMemoryLessThanOne_Throws()
    {
        var dir = NewSpillDir();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotStack(0, dir));
    }

    // ---- Task 1 (Plano 5): teto de RAM/disco POR CIMA do mecanismo de spill acima (prova) ------------
    //
    // Novo ctor de 4 argumentos: maxRamBytes/maxSpillBytes ADICIONAM 2 janelas novas em cima da janela
    // por CONTAGEM já provada acima (maxInMemory) — o ctor de 2 argumentos continua existindo e se
    // comporta EXATAMENTE como antes (delega pra maxRamBytes/maxSpillBytes efetivamente ilimitados),
    // por isso nenhum teste acima precisou mudar.

    [Fact] // janela por BYTES mais apertada que a janela por CONTAGEM: mesmo com maxInMemory folgado
    // (20), um teto de RAM pequeno já força o spill das entradas mais ANTIGAS, mantendo sempre a do
    // TOPO (a mais recente) em RAM.
    public void Push_BeyondMaxRamBytes_SpillsOldestEntries_KeepingTopInMemory()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir, maxRamBytes: 5, maxSpillBytes: long.MaxValue);

        stack.Push([1, 1, 1]); // 3 bytes -- RAM: 3 (<=5)
        stack.Push([2, 2, 2]); // RAM subiria pra 6 (>5) -> espalha a mais antiga (a de push 1)
        stack.Push([3, 3, 3]); // RAM subiria pra 6 de novo -> espalha a mais antiga AINDA em RAM (push 2)

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length); // as 2 mais antigas espalhadas; só a do topo (push 3) ficou em RAM
    }

    [Fact] // mesmo um ÚNICO snapshot maior que o teto de RAM NUNCA é espalhado imediatamente -- é
    // sempre o do TOPO (o mais recente, o que .Undo() precisaria devolver primeiro) -- espalhar o topo
    // de cara não ganharia nada (ele seria relido do disco na hora seguinte) e complicaria o contrato.
    public void Push_SingleEntryExceedingRamBudget_StaysInMemory_NeverSpilled()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir, maxRamBytes: 1, maxSpillBytes: long.MaxValue);

        stack.Push([1, 2, 3, 4, 5]); // 5 bytes > teto de 1 byte

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert.Empty(files);
        Assert.True(stack.CanUndo);
    }

    [Fact] // restauração continua byte-a-byte idêntica quando o spill foi disparado pelo teto de BYTES
    // (não só pela CONTAGEM, já provado em Spill_RestoreIsByteIdentical_EvenFromDisk acima).
    public void Push_BeyondMaxRamBytes_RestoreIsByteIdentical()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(20, dir, maxRamBytes: 5, maxSpillBytes: long.MaxValue);
        byte[] s0 = [1, 1, 1], s1 = [2, 2, 2], s2 = [3, 3, 3];
        stack.Push(s0);
        stack.Push(s1);
        stack.Push(s2);

        byte[] current = [9, 9, 9];
        current = stack.Undo(current)!; Assert.Equal(s2, current); // topo, nunca saiu da RAM
        current = stack.Undo(current)!; Assert.Equal(s1, current); // veio do disco (espalhado no push 3)
        current = stack.Undo(current)!; Assert.Equal(s0, current); // veio do disco (espalhado no push 2)
        Assert.False(stack.CanUndo);
    }

    [Fact] // teto de DISCO (maxSpillBytes): além dele, a entrada espalhada mais ANTIGA é DESCARTADA de
    // vez (arquivo apagado, entrada removida da pilha) -- não só "não cresce mais", perde-se
    // PERMANENTEMENTE a capacidade de desfazer até aquele ponto. maxInMemory=1 força quase toda entrada
    // a espalhar cedo (deixa só o teto de disco como variável em jogo); snapshots de 3 bytes, teto de
    // disco 10 bytes -> cabem 3 espalhadas (9 bytes), a 4ª espalhada estouraria pra 12 -> descarta a
    // mais antiga das 3 já espalhadas.
    public void Push_BeyondMaxSpillBytes_DiscardsOldestSpilledEntry_DeletingItsFile()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(1, dir, maxRamBytes: long.MaxValue, maxSpillBytes: 10);
        byte[] v0 = [10, 10, 10], v1 = [20, 20, 20], v2 = [30, 30, 30], v3 = [40, 40, 40], v4 = [50, 50, 50];

        stack.Push(v0); // RAM: v0
        stack.Push(v1); // espalha v0 (9 bytes até aqui: 3)
        stack.Push(v2); // espalha v1 (spilled: 6)
        stack.Push(v3); // espalha v2 (spilled: 9, ainda <= 10)
        stack.Push(v4); // espalharia v3 -> spilled subiria pra 12 (> 10) -> descarta v0 (a mais antiga)

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert.Equal(3, files.Length); // v1,v2,v3 espalhadas; v0 descartada (arquivo apagado); v4 em RAM

        byte[] current = [99, 99, 99];
        current = stack.Undo(current)!; Assert.Equal(v4, current); // topo, RAM
        current = stack.Undo(current)!; Assert.Equal(v3, current); // disco
        current = stack.Undo(current)!; Assert.Equal(v2, current); // disco
        current = stack.Undo(current)!; Assert.Equal(v1, current); // disco
        Assert.False(stack.CanUndo); // v0 foi descartada -- não dá mais pra desfazer até lá
    }

    [Fact] // notificação do descarte: dispara EXATAMENTE 1x pra ESTE cenário (1 descarte genuíno) --
    // MUTATION-PROVÁVEL (mesma disciplina do brief): comentar o disparo de HistoryLimitReached faz
    // este teste falhar sem quebrar nenhum outro (nada mais observa o evento).
    public void Push_BeyondMaxSpillBytes_RaisesHistoryLimitReached_Once()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(1, dir, maxRamBytes: long.MaxValue, maxSpillBytes: 10);
        int raised = 0;
        stack.HistoryLimitReached += (_, _) => raised++;

        stack.Push([10, 10, 10]);
        stack.Push([20, 20, 20]);
        stack.Push([30, 30, 30]);
        stack.Push([40, 40, 40]);
        Assert.Equal(0, raised); // ainda dentro do teto (9 <= 10) -- nenhum descarte ainda
        stack.Push([50, 50, 50]); // estoura -> 1 descarte

        Assert.Equal(1, raised);
    }

    [Fact] // 2 descartes genuínos (teto bem apertado) -> o evento dispara 2x AQUI (SnapshotStack é PURA
    // e mecânica -- a disciplina "1x por DOCUMENTO" é responsabilidade de DocumentSession, que consome
    // este evento e faz seu próprio latch, ver DocumentSessionTests).
    public void Push_MultipleDiscards_RaisesHistoryLimitReached_OncePerDiscard()
    {
        var dir = NewSpillDir();
        using var stack = new SnapshotStack(1, dir, maxRamBytes: long.MaxValue, maxSpillBytes: 3);
        int raised = 0;
        stack.HistoryLimitReached += (_, _) => raised++;

        stack.Push([1, 1, 1]); // RAM
        stack.Push([2, 2, 2]); // espalha v0 (spilled=3, <=3 ok)
        stack.Push([3, 3, 3]); // espalharia v1 -> spilled=6 (>3) -> descarta v0 (1º descarte)
        stack.Push([4, 4, 4]); // espalharia v2 -> spilled sobe -> descarta a mais antiga espalhada ainda viva (2º descarte)

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Ctor_MaxRamBytesLessThanOne_Throws()
    {
        var dir = NewSpillDir();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotStack(20, dir, maxRamBytes: 0, maxSpillBytes: long.MaxValue));
    }

    [Fact]
    public void Ctor_MaxSpillBytesLessThanOne_Throws()
    {
        var dir = NewSpillDir();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotStack(20, dir, maxRamBytes: long.MaxValue, maxSpillBytes: 0));
    }
}
