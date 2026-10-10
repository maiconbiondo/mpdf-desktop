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

    // --- Largura certa (feat/pdfa-ocr-largura): o mesmo `Tz` da camada OCR (ApplyOcrTextLayer) no PDF/A ---
    // Oraculo: as caixas de caractere do PDFium (motor INDEPENDENTE do iText que escreveu), como em
    // ApplyOcrTextLayerTests. Largura natural medida com a fonte REALMENTE embutida (Inter, via iText).
    // Pagina Carta 612x792 pt com bitmap-fonte 1224x1584 px (fator px->pt = 0,5: prova que a largura usa fatorX).
    private const double Escala = 2.0;

    private static byte[] PdfAComCaixas(PdfANivel nivel, params OcrTextBox[] caixasPt)
    {
        var caixas = caixasPt.Select(c => new OcrTextBox(c.Text, c.LeftPx * Escala, c.TopPx * Escala, c.WidthPx * Escala, c.HeightPx * Escala)).ToList();
        var pag = new PdfAPaginaImagem(Fixtures.PaginaSolida(), 612, 792, (int)(612 * Escala), (int)(792 * Escala), caixas);
        return Editor.CriarPdfADeImagens(new[] { pag }, nivel, new PdfAMetadados("Doc", null), Fixtures.InterTtf());
    }

    private static double LarguraNaturalInter(string palavra, double tamanhoPt) =>
        iText.Kernel.Font.PdfFontFactory.CreateFont(Fixtures.InterTtf(), iText.IO.Font.PdfEncodings.IDENTITY_H,
            iText.Kernel.Font.PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED).GetWidth(palavra, (float)tamanhoPt);

    [Fact] // caixas ESTREITAS: a palavra invisivel e COMPRIMIDA ate a largura da caixa do OCR (+-2 pt), comecando
           // na borda esquerda. As paginas do PDF/A sao recem-criadas, SEMPRE rotacao 0 (sem /Rotate) — nao ha
           // rotacoes a cobrir aqui (as 4 rotacoes do Tz estao em ApplyOcrTextLayerTests).
    public void CriarPdfADeImagens_WordWidthMatchesOcrBox()
    {
        var pdf = PdfAComCaixas(PdfANivel.A2B,
            new OcrTextBox("Joaquim", 60, 200, 35, 14),
            new OcrTextBox("Avenida", 60, 300, 42, 14));

        foreach (var (word, caixa) in new[] { ("Joaquim", 35.0), ("Avenida", 42.0) })
        {
            Assert.True(LarguraNaturalInter(word, 14) > caixa + 5, $"sanity: '{word}' natural tem de passar da caixa");
            var w = WordExtentDisplayed(pdf, word);
            Assert.True(Math.Abs(w.Left - 60) <= 2, $"'{word}': esquerda {w.Left:F2} (esperado 60)");
            Assert.True(Math.Abs(w.Width - caixa) <= 2, $"'{word}': largura {w.Width:F2} (esperado {caixa})");
        }
    }

    [Fact] // "Joaquim" "Pereira" com caixas estreitas: a Inter natural da 1a passaria por cima da 2a
    public void CriarPdfADeImagens_AdjacentWordsExtractSeparately()
    {
        Assert.True(LarguraNaturalInter("Joaquim", 14) > 42 + 4, "sanity: natural invadiria a vizinha");
        var pdf = PdfAComCaixas(PdfANivel.A2B,
            new OcrTextBox("Joaquim", 100, 200, 38, 14),
            new OcrTextBox("Pereira", 142, 200, 36, 14));

        using var r = new PdfDocumentRenderer(pdf);
        string text = r.GetTextPage(0).Text;
        Assert.Contains("Joaquim Pereira", text);
        Assert.DoesNotContain("JoaquimPereira", text);
        var a = WordExtentDisplayed(pdf, "Joaquim");
        var b = WordExtentDisplayed(pdf, "Pereira");
        Assert.True(a.Left + a.Width < b.Left, $"as palavras se sobrepoem: Joaquim ate {a.Left + a.Width:F2}, Pereira de {b.Left:F2}");

        // iText (copiar/exportar texto): o limiar de espaco dele e metade da largura do ESPACO na fonte da pagina.
        // Num subset Type0/Identity-H o espaco so entra no /W (e no /ToUnicode) se for USADO — sem isso o leitor
        // cai no /DW 1000 (um "espaco" de 1 em) e "Joaquim Pereira" saia "JoaquimPereira".
        using var doc = new PdfDocument(new PdfReader(new System.IO.MemoryStream(pdf)));
        string itext = iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor.GetTextFromPage(doc.GetPage(1));
        Assert.Contains("Joaquim Pereira", itext);
    }

    [Fact] // caixa LARGA: NUNCA estica (teto 100% — esticar juntava palavras na extracao), fica na largura natural
    public void CriarPdfADeImagens_WideBoxKeepsNaturalWidth()
    {
        var pdf = PdfAComCaixas(PdfANivel.A2B,
            new OcrTextBox("Joaquim", 60, 200, 30, 14), // comprimida antes: prova que o Tz dela nao vaza
            new OcrTextBox("Gomes", 60, 300, 90, 14));

        Assert.True(WordExtentDisplayed(pdf, "Joaquim").Width < 33, "sanity: a 1a foi mesmo comprimida");
        double natural = LarguraNaturalInter("Gomes", 14);
        var w = WordExtentDisplayed(pdf, "Gomes");
        Assert.True(Math.Abs(w.Left - 60) <= 2, $"esquerda {w.Left:F2} (esperado 60)");
        Assert.True(Math.Abs(w.Width - natural) <= 2 && w.Width < 90, $"largura {w.Width:F2} (natural {natural:F2}, caixa 90)");
    }

    [Fact] // caixa sem largura (WidthPx <= 0) -> largura natural (100%), depois de uma comprimida (sem vazamento)
    public void CriarPdfADeImagens_ZeroWidthBox_FallsBackToNaturalWidth()
    {
        var pdf = PdfAComCaixas(PdfANivel.A2B,
            new OcrTextBox("Joaquim", 100, 200, 30, 14),
            new OcrTextBox("Pereira", 100, 300, 0, 14),
            new OcrTextBox("Gomes", 100, 400, -5, 14));

        Assert.True(WordExtentDisplayed(pdf, "Joaquim").Width < 33, "sanity: a 1a foi mesmo comprimida");
        foreach (var word in new[] { "Pereira", "Gomes" })
        {
            double natural = LarguraNaturalInter(word, 14);
            var w = WordExtentDisplayed(pdf, word);
            // +-2,5 pt: o PDFium mede a caixa do GLIFO, a largura natural e o AVANCO — os recuos laterais da Inter
            // ("P" e "a" de "Pereira" ~2 pt somados a 14 pt) ficam de fora; comprimida a 30 pt ficaria ~17 pt menor.
            Assert.True(Math.Abs(w.Width - natural) <= 2.5, $"'{word}': largura {w.Width:F2}, natural {natural:F2}");
        }
    }

    [Theory] // com Tz, o documento continua PDF/A em cada nivel: o PdfADocument (que RECUSA gravar conteudo
             // nao-conforme) gerou sem lancar; reaberto por PdfADocument em modo de edicao (o verificador do iText
             // roda no fechamento) le o MESMO nivel do XMP; a fonte continua embutida e o modo 3 (invisivel) e o
             // Tz estao no conteudo da pagina, isolados num q..Q.
    [InlineData(PdfANivel.A1B, "1", "B")]
    [InlineData(PdfANivel.A2B, "2", "B")]
    [InlineData(PdfANivel.A3B, "3", "B")]
    public void CriarPdfADeImagens_ComLarguraAjustada_ContinuaPdfA(PdfANivel nivel, string parte, string conformidade)
    {
        var pdf = PdfAComCaixas(nivel, new OcrTextBox("Joaquim", 100, 200, 30, 14), new OcrTextBox("Gomes", 100, 300, 90, 14));

        using (var reaberto = new iText.Pdfa.PdfADocument(new PdfReader(new System.IO.MemoryStream(pdf)), new PdfWriter(new System.IO.MemoryStream())))
        {
            var c = reaberto.GetConformance().GetAConformance();
            Assert.NotNull(c);
            Assert.Equal(parte, c!.GetPart());
            Assert.Equal(conformidade, c.GetLevel());
        }

        using var doc = new PdfDocument(new PdfReader(new System.IO.MemoryStream(pdf)));
        string conteudo = System.Text.Encoding.Latin1.GetString(doc.GetPage(1).GetContentBytes());
        Assert.Contains(" Tz", conteudo);
        Assert.Contains("3 Tr", conteudo);
        // A imagem vem antes no seu proprio q..Q (`q ... cm /Im Do Q`); a camada de texto abre o PROPRIO q logo
        // depois e fecha com Q apos o ultimo ET (Tz/Tr/Tf sao estado grafico — nao vazam para conteudo posterior).
        Assert.Matches(@"Do\s+Q\s+q\s+BT", conteudo);
        Assert.Matches(@"ET\s+Q\s*$", conteudo);
    }

    /// Extensao horizontal EXIBIDA da palavra pelas caixas de caractere do PDFium (copia de ApplyOcrTextLayerTests).
    private static (double Left, double Width) WordExtentDisplayed(byte[] pdf, string word)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var tp = r.GetTextPage(0);
        int i = tp.Text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{word}' nao extraido pelo PDFium: '{tp.Text}'");
        var cs = tp.Characters.Skip(i).Take(word.Length).ToList();
        double l = cs.Min(c => Math.Min(c.LeftPt, c.RightPt)), rr = cs.Max(c => Math.Max(c.LeftPt, c.RightPt));
        return (l, rr - l);
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
