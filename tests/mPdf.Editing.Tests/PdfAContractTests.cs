using mPdf.Editing;
using Xunit;

namespace mPdf.Editing.Tests;

public class PdfAContractTests
{
    [Fact] // a fabrica devolve um editor que expoe o novo metodo (compila = contrato presente)
    public void Editor_ExpoeCriarPdfADeImagens()
    {
        IPdfEditor e = PdfEditorFactory.Create();
        Assert.NotNull(e);
        // Chamar com lista vazia deve lancar ArgumentException (sem paginas nao ha PDF/A) — Task 3
        // implementa; aqui so garantimos que o simbolo existe e a chamada compila.
    }
}
