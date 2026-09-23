using System.Collections.Generic;
using System.Linq;
using iText.Kernel.Pdf;
using mPdf.Editing;
using mPdf.Rendering;
using Xunit;

namespace mPdf.Editing.Tests;

// Task 3 (SDD "Salvar como PDF/A"): CriarPdfADeImagens — PDF/A só com imagens (sem texto ainda,
// ver Task 4). Fixture: Fixtures.PaginaSolida() (850x1100 RGB, retângulo escuro no canto
// superior-esquerdo — ver comentário em Fixtures.cs).
public class PdfAEditorTests
{
    private static readonly IPdfEditor Editor = PdfEditorFactory.Create();

    private static PdfAPaginaImagem Pagina() =>
        new(Fixtures.PaginaSolida(), 612, 792, 850, 1100, Texto: null); // Carta em pt (72dpi), bitmap 850x1100

    [Theory] // gera cada nivel "b" sem lancar e produz PDF nao-vazio
    [InlineData(PdfANivel.A1B)]
    [InlineData(PdfANivel.A2B)]
    [InlineData(PdfANivel.A3B)]
    public void Gera_CadaNivel_NaoVazio(PdfANivel nivel)
    {
        var outp = Editor.CriarPdfADeImagens(new[] { Pagina() }, nivel, new PdfAMetadados("Doc", "mPDF"), null);
        Assert.True(outp.Length > 1000, $"PDF/A {nivel} pequeno demais");
    }

    [Fact] // o PDF/A gerado reabre e renderiza (fidelidade: retangulo escuro no canto esquerdo-superior)
    public void Renderiza_ComFidelidade()
    {
        var outp = Editor.CriarPdfADeImagens(new[] { Pagina() }, PdfANivel.A1B, new PdfAMetadados("Doc", null), null);
        using var r = new PdfDocumentRenderer(outp);
        Assert.Equal(1, r.PageCount);
        var pg = r.RenderPage(0, 1.0);
        // canto sup-esquerdo (2%x2% a 12%x12%) tem tinta escura; o resto do fundo eh claro.
        Assert.True(NaoBranco(pg, 0.02, 0.02, 0.12, 0.12) > 0, "retangulo escuro sumiu");
    }

    [Fact] // reabre como PdfDocument sem erro e tem OutputIntent
    public void Estrutura_TemOutputIntent()
    {
        var outp = Editor.CriarPdfADeImagens(new[] { Pagina() }, PdfANivel.A2B, new PdfAMetadados("Doc", null), null);
        Assert.True(TemOutputIntent(outp), "sem OutputIntent no PDF/A");
    }

    [Fact] // lista vazia lanca ArgumentException (sem paginas nao ha documento)
    public void SemPaginas_Lanca() =>
        Assert.Throws<System.ArgumentException>(
            () => Editor.CriarPdfADeImagens(new List<PdfAPaginaImagem>(), PdfANivel.A1B, new PdfAMetadados(null, null), null));

    // Task 4 (SDD "Salvar como PDF/A"): camada de texto invisivel (OCR) embutida no PDF/A.

    [Fact] // com Texto + fonte embutida, o PDF/A fica pesquisavel (texto extraivel bate)
    public void ComOcr_TextoExtraivel()
    {
        var boxes = new List<OcrTextBox> { new("documento", 40, 40, 300, 60) }; // px topo-esquerda no bitmap 850x1100
        var pag = new PdfAPaginaImagem(Fixtures.PaginaSolida(), 612, 792, 850, 1100, boxes);
        var outp = Editor.CriarPdfADeImagens(new[] { pag }, PdfANivel.A2B, new PdfAMetadados("Doc", null), Fixtures.InterTtf());
        using var r = new PdfDocumentRenderer(outp);
        var texto = r.GetTextPage(0).Text; // mesma extracao do Ctrl+F
        Assert.Contains("documento", texto);
    }

    [Fact] // a fonte OCR (Type0/Identity-H) carrega o PROGRAMA da fonte embutido no FontDescriptor,
           // nao so uma referencia -- extrair o mesmo texto (ComOcr_TextoExtraivel) nao prova isso:
           // uma fonte-base nao-embutida com os mesmos glifos extrairia o texto igualmente. PDF/A
           // exige fontes embutidas; aqui caminhamos ate o FontDescriptor e conferimos /FontFile2
           // (TrueType) ou /FontFile3 (CFF/OpenType) diretamente na arvore de recursos da pagina.
    public void Estrutura_FonteOcrEmbutida()
    {
        var boxes = new List<OcrTextBox> { new("documento", 40, 40, 300, 60) };
        var pag = new PdfAPaginaImagem(Fixtures.PaginaSolida(), 612, 792, 850, 1100, boxes);
        var outp = Editor.CriarPdfADeImagens(new[] { pag }, PdfANivel.A1B, new PdfAMetadados("Doc", null), Fixtures.InterTtf());
        using var doc = new PdfDocument(new PdfReader(new System.IO.MemoryStream(outp)));
        var recursos = doc.GetPage(1).GetPdfObject().GetAsDictionary(PdfName.Resources);
        var fontes = recursos?.GetAsDictionary(PdfName.Font);
        Assert.NotNull(fontes);

        bool achouFonteEmbutida = false;
        foreach (var chave in fontes!.KeySet())
        {
            var fonteDict = fontes.GetAsDictionary(chave);
            var descendentes = fonteDict?.GetAsArray(PdfName.DescendantFonts);
            var descendente = descendentes is not null && descendentes.Size() > 0 ? descendentes.GetAsDictionary(0) : null;
            var descritor = descendente?.GetAsDictionary(PdfName.FontDescriptor);
            if (descritor is null) continue;
            if (descritor.Get(PdfName.FontFile2) is not null || descritor.Get(PdfName.FontFile3) is not null)
                achouFonteEmbutida = true;
        }
        Assert.True(achouFonteEmbutida, "fonte OCR (Type0) sem /FontFile2 ou /FontFile3 no FontDescriptor -- nao esta embutida");
    }

    [Fact] // texto presente mas fonte null -> ArgumentException (precondicao do contrato)
    public void ComTextoSemFonte_Lanca()
    {
        var boxes = new List<OcrTextBox> { new("x", 10, 10, 50, 20) };
        var pag = new PdfAPaginaImagem(Fixtures.PaginaSolida(), 612, 792, 850, 1100, boxes);
        Assert.Throws<System.ArgumentException>(
            () => Editor.CriarPdfADeImagens(new[] { pag }, PdfANivel.A1B, new PdfAMetadados(null, null), null));
    }

    // Conta pixels NAO-brancos numa regiao fracionaria (0..1) da pagina renderizada (BGRA) — copia
    // de ImposicaoPdfTests.NaoBranco.
    private static int NaoBranco(RenderedPage p, double fx0, double fy0, double fx1, double fy1)
    {
        int x0 = (int)(fx0 * p.WidthPx), x1 = (int)(fx1 * p.WidthPx);
        int y0 = (int)(fy0 * p.HeightPx), y1 = (int)(fy1 * p.HeightPx);
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * p.WidthPx + x) * 4;
                if (p.Bgra[i] < 240 || p.Bgra[i + 1] < 240 || p.Bgra[i + 2] < 240) n++;
            }
        return n;
    }

    // Reabre com iText puro e le /OutputIntents do catalogo.
    private static bool TemOutputIntent(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new System.IO.MemoryStream(pdf)));
        var arr = doc.GetCatalog().GetPdfObject().GetAsArray(PdfName.OutputIntents);
        return arr is not null && arr.Size() > 0;
    }
}
