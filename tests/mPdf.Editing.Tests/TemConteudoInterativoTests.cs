namespace mPdf.Editing.Tests;

/// Revisão F2 (A9): `IPdfEditor.TemConteudoInterativo` — o que `ImporEmFolhas` (CopyAsFormXObject) não leva para o PDF
/// imposto: qualquer anotação (exceto Link/Popup), widgets de formulário e assinaturas.
public class TemConteudoInterativoTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    [Fact] public void PdfSemNada_False() => Assert.False(Ed.TemConteudoInterativo(Fixtures.ThirtyPages()));
    [Fact] public void Anotada_True() => Assert.True(Ed.TemConteudoInterativo(Fixtures.Anotada()));
    [Fact] public void Formulario_True() => Assert.True(Ed.TemConteudoInterativo(Fixtures.Formulario()));
    [Fact] public void Assinado_True() => Assert.True(Ed.TemConteudoInterativo(Fixtures.Carimbo()));
    [Fact] public void Xfa_NaoLanca() => Ed.TemConteudoInterativo(Fixtures.Xfa());
    [Fact] public void Ilegivel_PdfEditingException() => Assert.ThrowsAny<PdfEditingException>(() => Ed.TemConteudoInterativo(new byte[] { 1, 2, 3 }));
}
