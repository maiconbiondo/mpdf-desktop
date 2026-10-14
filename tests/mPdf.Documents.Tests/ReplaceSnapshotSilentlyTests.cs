using mPdf.Documents;
using Xunit;

namespace mPdf.Documents.Tests;

/// Revisão F2 (A7): normalização de ABERTURA (dar /AP às linhas gravadas até a v2.16) troca os bytes da sessão sem
/// marcar o documento como modificado e sem entrada de desfazer — `DocumentSession.ReplaceSnapshotSilently`.
public class ReplaceSnapshotSilentlyTests
{
    private static string Temp()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-silent-{Guid.NewGuid():N}.pdf");
        File.Copy(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), tmp);
        return tmp;
    }

    [Fact]
    public void TrocaOsBytes_SemSujarSemDesfazer_EDisparaApplied()
    {
        var tmp = Temp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            var novo = Fixtures.ThirtyPages();
            int applied = 0, dirty = 0, undo = 0;
            s.Applied += (_, _) => applied++;
            s.DirtyChanged += (_, _) => dirty++;
            s.CanUndoRedoChanged += (_, _) => undo++;

            s.ReplaceSnapshotSilently(novo);

            Assert.Same(novo, s.Snapshot);
            Assert.Equal(30, s.Renderer.PageCount);
            Assert.Equal(30, s.PageSizes.Count);
            Assert.False(s.IsDirty);
            Assert.False(s.CanUndo);
            Assert.False(s.CanRedo);
            Assert.Equal(1, applied);
            Assert.Equal(0, dirty);
            Assert.Equal(0, undo);
            Assert.Equal(new FileInfo(tmp).Length, Fixtures.A4().Length); // o disco não é tocado

            // uma edição depois suja; desfazê-la volta ao estado normalizado = não modificado
            s.ApplyEdit(Fixtures.A4());
            Assert.True(s.IsDirty);
            s.Undo();
            Assert.False(s.IsDirty);
            Assert.Same(novo, s.Snapshot);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // só na abertura: com histórico, documento modificado ou edição em voo, recusa sem mexer em nada
    public void ForaDaAbertura_Recusa()
    {
        var tmp = Temp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.ApplyEdit(Fixtures.ThirtyPages());
            var antes = s.Snapshot;
            Assert.Throws<InvalidOperationException>(() => s.ReplaceSnapshotSilently(Fixtures.A4()));
            Assert.Same(antes, s.Snapshot);

            using var s2 = DocumentSession.Open(tmp);
            Assert.True(s2.TryBeginEdit());
            Assert.Throws<InvalidOperationException>(() => s2.ReplaceSnapshotSilently(Fixtures.ThirtyPages()));
            s2.EndEdit();
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // bytes inválidos: lança e a sessão fica intacta (mesmo contrato de Apply)
    public void BytesInvalidos_SessaoIntacta()
    {
        var tmp = Temp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            var antes = s.Snapshot;
            Assert.ThrowsAny<Exception>(() => s.ReplaceSnapshotSilently(new byte[] { 1, 2, 3 }));
            Assert.Same(antes, s.Snapshot);
            Assert.False(s.IsDirty);
        }
        finally { File.Delete(tmp); }
    }
}
