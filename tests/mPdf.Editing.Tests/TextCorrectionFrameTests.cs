using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.Editing.Tests;

/// Revisão final (2026-10-09): frame de coordenadas (CropBox com origem != 0,0) e anotações que
/// tocam a área limpa. Dados sintéticos.
public class TextCorrectionFrameTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();
    private const uint White = 0xFFFFFFFF;
    private const string Line1 = "Endereco: RUA XV DE NOVEMBRO, 2023";
    private const string Line2 = "Responsavel: JOSE MARIA DA SILVA";
    private const float Base1 = 689f, Base2 = 678f; // linhas a 11 pt de distância (relatório tipo Crystal)

    /// Página com MediaBox maior e CropBox deslocada (ox, oy); o texto é desenhado em
    /// (x+ox, y+oy), então no frame EXIBIDO (relativo à CropBox) ele aparece em (x, y).
    private static byte[] CropOffsetPage(float ox, float oy)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(595 + 2 * ox, 842 + 2 * oy));
            page.SetCropBox(new Rectangle(ox, oy, 595, 842));
            var times = PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN);
            var canvas = new PdfCanvas(page);
            canvas.BeginText().SetFontAndSize(times, 9).MoveText(110 + ox, Base1 + oy).ShowText(Line1).EndText();
            canvas.BeginText().SetFontAndSize(times, 9).MoveText(110 + ox, Base2 + oy).ShowText(Line2).EndText();
        }
        return output.ToArray();
    }

    /// Área da palavra como o App a obtém: união das caixas de caractere do PDFium (frame da CropBox).
    private static PdfQuad PdfiumWordArea(byte[] pdf, string word)
    {
        using var r = new PdfDocumentRenderer(pdf);
        var tp = r.GetTextPage(0);
        int i = tp.Text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{word}' não está no texto do PDFium: {tp.Text}");
        var cs = tp.Characters.Skip(i).Take(word.Length).ToList();
        return new PdfQuad(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt), cs.Max(c => c.RightPt), cs.Max(c => c.TopPt));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, 11f)]
    [InlineData(30f, 50f)]
    public void ProbeTextStyle_CropBoxOffset_FindsWordInDisplayedFrame(float ox, float oy)
    {
        var pdf = CropOffsetPage(ox, oy);
        var area = PdfiumWordArea(pdf, "NOVEMBRO");

        var p = Editor.ProbeTextStyle(pdf, 0, area);

        Assert.NotNull(p);
        Assert.Contains("NOVEMBRO", p!.Text);
        Assert.DoesNotContain("MARIA", p.Text);
        Assert.Equal(Base1, p.BaselineYPt, 1);            // frame da CropBox (o mesmo do PDFium)
        Assert.InRange(p.LeftXPt, area.LeftPt - 3, area.LeftPt + 1); // pode incluir o espaço antes da palavra
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, 11f)]
    [InlineData(30f, 50f)]
    public void ApplyTextCorrection_CropBoxOffset_ReplacesWordOnTheRightLine(float ox, float oy)
    {
        var pdf = CropOffsetPage(ox, oy);
        var area = PdfiumWordArea(pdf, "NOVEMBRO");
        var p = Editor.ProbeTextStyle(pdf, 0, area);
        Assert.NotNull(p);

        var after = Editor.ApplyTextCorrection(pdf, new TextCorrection(0, area, "DEZEMBRO", p!.Family, p.Bold, p.Italic,
            p.SizePt, p.ColorArgb, p.BaselineYPt, p.LeftXPt, White));

        using var r = new PdfDocumentRenderer(after);
        string text = r.GetTextPage(0).Text;
        Assert.DoesNotContain("NOVEMBRO", text);
        Assert.Contains("DEZEMBRO", text);
        Assert.Contains("JOSE MARIA DA SILVA", text); // linha vizinha (11 pt abaixo) intacta
        Assert.Contains("Endereco: RUA XV DE", text);
        // O texto novo caiu na MESMA linha (frame exibido) da palavra antiga.
        var novo = PdfiumWordArea(after, "DEZEMBRO");
        Assert.InRange(novo.BottomPt, area.BottomPt - 1, area.BottomPt + 1);
        Assert.InRange(novo.LeftPt, area.LeftPt - 3, area.LeftPt + 3);
    }

    [Fact] // pdfSweep não pode apagar anotações que tocam a área (nem as distantes)
    public void ApplyTextCorrection_KeepsAnnotations()
    {
        byte[] pdf;
        using (var o = new MemoryStream())
        {
            using (var d = new PdfDocument(new PdfReader(new MemoryStream(TextFixtures.TimesOverImage())), new PdfWriter(o)))
            {
                var pg = d.GetPage(1);
                var a = TextFixtures.OldWordArea();
                pg.AddAnnotation(new PdfSquareAnnotation(new Rectangle((float)a.LeftPt - 20, (float)a.BottomPt - 10, 200, 40)).SetContents("caixa"));
                pg.AddAnnotation(new PdfTextAnnotation(new Rectangle(300, 780, 20, 20)).SetContents("nota longe"));
            }
            pdf = o.ToArray();
        }
        var c = new TextCorrection(0, TextFixtures.OldWordArea(), "NOVA", TextFontFamily.Serif, false, false,
            TextFixtures.FontSize, 0xFF000000, TextFixtures.LineBaseline, TextFixtures.OldWordArea().LeftPt, White);

        var after = Editor.ApplyTextCorrection(pdf, c);

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(after)));
        var contents = doc.GetPage(1).GetAnnotations().Select(x => x.GetContents()?.ToString()).ToList();
        Assert.Equal(2, contents.Count);
        Assert.Contains("caixa", contents);
        Assert.Contains("nota longe", contents);
    }
    // ---- Rodada 2 (I-1): camada OCR com linhas coladas -------------------------------------------

    /// Duas palavras OCR em linhas coladas (3 px de folga entre as caixas): a descida da Helvetica
    /// invisível de "gqpjy" invade a caixa de "Joaquim" logo abaixo.
    private static (byte[] Pdf, PdfQuad JoaquimBox) OcrLinhasColadas()
    {
        byte[] pdf = PdfEditorFactory.Create().ApplyOcrTextLayer(Fixtures.NoText(), new[]
        {
            new OcrTextLayer(0, 800, 1131, new[]
            {
                new OcrTextBox("gqpjy", LeftPx: 100, TopPx: 100, WidthPx: 200, HeightPx: 40),
                new OcrTextBox("Joaquim", LeftPx: 100, TopPx: 143, WidthPx: 260, HeightPx: 40),
                new OcrTextBox("lhkbdf", LeftPx: 100, TopPx: 186, WidthPx: 220, HeightPx: 40),
            }),
        });
        double sx = 595.0 / 800, sy = 842.0 / 1131;
        // Área como o App monta na camada OCR: a caixa da palavra (+0,5 pt na vertical).
        return (pdf, new PdfQuad(100 * sx, 842 - 183 * sy - 0.5, 360 * sx, 842 - 143 * sy + 0.5));
    }

    [Fact] // com MinOverlapRatio, só a palavra da área sai; as linhas vizinhas continuam pesquisáveis
    public void ApplyTextCorrection_OcrMinOverlap_KeepsNeighbourLinesInvisibleText()
    {
        var (pdf, box) = OcrLinhasColadas();
        var c = new TextCorrection(0, box, "Antonio", TextFontFamily.SansSerif, false, false, 20, 0xFF000000,
            box.BottomPt + 0.5, box.LeftPt, White, MinOverlapRatio: 0.4);

        using var r = new PdfDocumentRenderer(Editor.ApplyTextCorrection(pdf, c));
        string text = r.GetTextPage(0).Text;
        Assert.DoesNotContain("Joaquim", text);
        Assert.Contains("Antonio", text);
        Assert.Contains("gqpjy", text);
        Assert.Contains("lhkbdf", text);
    }

    [Fact] // controle: sem MinOverlapRatio (caminho digital, inalterado) a descida vizinha é apagada junto
    public void ApplyTextCorrection_NoMinOverlap_TouchingGlyphsRemoved()
    {
        var (pdf, box) = OcrLinhasColadas();
        var c = new TextCorrection(0, box, "Antonio", TextFontFamily.SansSerif, false, false, 20, 0xFF000000,
            box.BottomPt + 0.5, box.LeftPt, White);
        using var r = new PdfDocumentRenderer(Editor.ApplyTextCorrection(pdf, c));
        Assert.DoesNotContain("gqpjy", r.GetTextPage(0).Text);
    }
    // ---- Rodada 2 (I-2/I-3): tokens OCR inteiros no probe ------------------------------------------

    [Fact] // tokens OCR colados (a Helvetica de "Joaquim" encosta em "Pereira,"): seleção parcial -> token inteiro
    public void ProbeTextStyle_OcrPartialSelection_ReturnsWholeTokenWithPunctuation()
    {
        double sx = 595.0 / 800, sy = 842.0 / 1131;
        byte[] pdf = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[]
        {
            new OcrTextLayer(0, 800, 1131, new[]
            {
                new OcrTextBox("Joaquim", LeftPx: 100, TopPx: 100, WidthPx: 120, HeightPx: 40),
                new OcrTextBox("Pereira,", LeftPx: 230, TopPx: 100, WidthPx: 120, HeightPx: 40),
            }),
        });
        double baseline = 842 - 140 * sy, left = 230 * sx;
        // Só o miolo de "Pereira," (sem o "P" nem a vírgula).
        var p = Editor.ProbeTextStyle(pdf, 0, new PdfQuad(left + 25, baseline - 2, left + 60, baseline + 15));

        Assert.NotNull(p);
        Assert.True(p!.Invisible);
        Assert.Equal("Pereira,", p.TokenText);
        Assert.NotNull(p.TokenAreaPt);
        var ta = p.TokenAreaPt!.Value;
        Assert.Equal(left, ta.LeftPt, 0);
        Assert.True(ta.BottomPt < baseline && ta.TopPt > baseline + 20);
    }

    [Fact] // OCR de outro programa que grava a LINHA inteira num Tj invisível: o espaço separa os tokens
    public void ProbeTextStyle_OcrWholeLineRun_ReturnsTokenBetweenSpaces()
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            new PdfCanvas(page).BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 12)
                .SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.INVISIBLE)
                .MoveText(72, 700).ShowText("RUA XV DE NOVEMBRO, 2023").EndText();
        }
        var helv = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        double x0 = 72 + helv.GetWidth("RUA XV DE NO", 12), x1 = 72 + helv.GetWidth("RUA XV DE NOVEMB", 12);

        var p = Editor.ProbeTextStyle(output.ToArray(), 0, new PdfQuad(x0, 698, x1, 710));

        Assert.NotNull(p);
        Assert.Equal("NOVEMBRO,", p!.TokenText);
        Assert.Equal(72 + helv.GetWidth("RUA XV DE ", 12), p.TokenAreaPt!.Value.LeftPt, 1);
        Assert.Equal(72 + helv.GetWidth("RUA XV DE NOVEMBRO,", 12), p.TokenAreaPt!.Value.RightPt, 1);
    }

    [Fact] // texto visível (digital): sem tokens (o encaixe é só da camada OCR)
    public void ProbeTextStyle_VisibleText_NoTokens()
    {
        var p = Editor.ProbeTextStyle(TextFixtures.TimesOverImage(), 0, TextFixtures.OldWordArea());
        Assert.NotNull(p);
        Assert.Null(p!.TokenText);
        Assert.Null(p.TokenAreaPt);
    }
    // ---- Rodada 3 (I-B): fonte estreita — a Helvetica invisível do token anterior invade a seleção ----

    /// "pagamento" (com descendente, caixa mais baixa) numa caixa ESTREITA: a Helvetica invisível dele
    /// passa da caixa e invade a de "jogo", logo à direita.
    private static (byte[] Pdf, PdfQuad JogoBox, double JogoBase, double JogoTop) OcrEstreito()
    {
        double sx = 595.0 / 800, sy = 842.0 / 1131;
        byte[] pdf = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[]
        {
            new OcrTextLayer(0, 800, 1131, new[]
            {
                new OcrTextBox("pagamento", LeftPx: 100, TopPx: 100, WidthPx: 160, HeightPx: 40),
                new OcrTextBox("jogo", LeftPx: 270, TopPx: 100, WidthPx: 70, HeightPx: 36),
                new OcrTextBox("quinta-feira", LeftPx: 350, TopPx: 100, WidthPx: 160, HeightPx: 40),
            }),
        });
        double jb = 842 - 136 * sy, jt = 842 - 100 * sy;
        return (pdf, new PdfQuad(270 * sx, jb, 340 * sx, jt), jb, jt);
    }

    [Fact] // seleção = só "jogo": o "o" final de "pagamento" que invade a área NÃO puxa o token vizinho
    public void ProbeTextStyle_OcrNarrow_NeighbourOverrunNotPulled()
    {
        var (pdf, box, jb, jt) = OcrEstreito();
        // sanity: a Helvetica de "pagamento" (tamanho 40 px de caixa) passa mesmo do início de "jogo"
        var helv = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        Assert.True(100 * 595.0 / 800 + helv.GetWidth("pagamento", (float)(40 * 842.0 / 1131)) > box.LeftPt + 3);

        // Área como a do PDFium: caixas de glifo da Helvetica descem abaixo da base (g, j).
        var p = Editor.ProbeTextStyle(pdf, 0, new PdfQuad(box.LeftPt, box.BottomPt - 6, box.RightPt, box.TopPt));

        Assert.NotNull(p);
        Assert.Equal("jogo", p!.TokenText);
        Assert.Equal(box.LeftPt, p.TokenAreaPt!.Value.LeftPt, 0);
        Assert.Equal(jb, p.BaselineYPt, 1);     // caixa OCR de "jogo", não a de "pagamento"
        Assert.Equal(jt - jb, p.SizePt, 1);
    }

    [Fact] // seleção parcial que pega 2 letras de "Pereira" ainda puxa o token inteiro; várias palavras inteiras também
    public void ProbeTextStyle_OcrTokenRule_PartialAndMultiWord()
    {
        var (pdf, box, _, _) = OcrEstreito();
        double sx = 595.0 / 800;
        var helv = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        float size = (float)(36 * 842.0 / 1131);
        double x0 = 270 * sx;
        // Só "jo" de "jogo".
        var parcial = Editor.ProbeTextStyle(pdf, 0, new PdfQuad(x0, box.BottomPt - 6, x0 + helv.GetWidth("jo", size), box.TopPt));
        Assert.Equal("jogo", parcial!.TokenText);
        // "jogo" + "quinta-feira" inteiros.
        var duas = Editor.ProbeTextStyle(pdf, 0, new PdfQuad(x0, box.BottomPt - 6, 510 * sx, box.TopPt));
        Assert.Equal("jogo quinta-feira", duas!.TokenText);
    }
    // ---- Rodada 4: linhas coladas com acento — a caixa de "MÚSICA" sobe até a linha de cima ---------

    [Fact] // a base da linha de cima ("ação") cai dentro da área de "MÚSICA": vale a linha com MAIS glifos tocados
    public void ProbeTextStyle_OcrAccentedLineBelow_PicksLineWithMostTouchedGlyphs()
    {
        double sx = 595.0 / 800, sy = 842.0 / 1131;
        byte[] pdf = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[]
        {
            new OcrTextLayer(0, 800, 1131, new[]
            {
                new OcrTextBox("ação", LeftPx: 90, TopPx: 100, WidthPx: 60, HeightPx: 30),
                new OcrTextBox("MÚSICA", LeftPx: 100, TopPx: 126, WidthPx: 200, HeightPx: 42),
            }),
        });
        double mb = 842 - 168 * sy, mt = 842 - 126 * sy;

        var p = Editor.ProbeTextStyle(pdf, 0, new PdfQuad(100 * sx, mb, 300 * sx, mt));

        Assert.NotNull(p);
        Assert.Equal("MÚSICA", p!.TokenText);
        Assert.Equal(mb, p.BaselineYPt, 1);
        Assert.Equal(mt - mb, p.SizePt, 1);
    }
    [Fact] // empate de glifos tocados (palavra longa em cima cobrindo a seleção): vence a linha cuja altura de
    // glifos fica mais DENTRO da área (a de cima só encosta a descida)
    public void ProbeTextStyle_OcrAccentedLineBelow_TieBrokenByVerticalOverlap()
    {
        double sx = 595.0 / 800, sy = 842.0 / 1131;
        byte[] pdf = Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[]
        {
            new OcrTextLayer(0, 800, 1131, new[]
            {
                new OcrTextBox("GONÇALVESGONÇALVES", LeftPx: 40, TopPx: 96, WidthPx: 400, HeightPx: 34),
                new OcrTextBox("MÚSICA", LeftPx: 100, TopPx: 126, WidthPx: 200, HeightPx: 42),
            }),
        });
        double mb = 842 - 168 * sy, mt = 842 - 126 * sy;

        var p = Editor.ProbeTextStyle(pdf, 0, new PdfQuad(100 * sx, mb, 300 * sx, mt));

        Assert.NotNull(p);
        Assert.Equal("MÚSICA", p!.TokenText);
        Assert.Equal(mb, p.BaselineYPt, 1);
    }
    // ---- Rodada 5: geometria da camada OCR (próximo token da linha, caixas das linhas vizinhas) ----

    private static (byte[] Pdf, double Sx, double Sy) OcrTresLinhas() =>
        (Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[]
        {
            new OcrTextLayer(0, 800, 1131, new[]
            {
                new OcrTextBox("GONÇALVES", LeftPx: 100, TopPx: 100, WidthPx: 200, HeightPx: 34),
                new OcrTextBox("MOURA", LeftPx: 320, TopPx: 100, WidthPx: 120, HeightPx: 28),
                new OcrTextBox("ANTONIO", LeftPx: 100, TopPx: 136, WidthPx: 150, HeightPx: 30),
                new OcrTextBox("PEREIRA", LeftPx: 270, TopPx: 137, WidthPx: 150, HeightPx: 29),
                new OcrTextBox("LIMA", LeftPx: 440, TopPx: 136, WidthPx: 90, HeightPx: 30),
                new OcrTextBox("LONGE", LeftPx: 100, TopPx: 400, WidthPx: 120, HeightPx: 30),
            }),
        }), 595.0 / 800, 842.0 / 1131);

    private static PdfQuad Caixa(double l, double t, double w, double h, double sx, double sy) =>
        new(l * sx, 842 - (t + h) * sy, (l + w) * sx, 842 - t * sy);

    [Fact] // palavra no meio da linha: próximo token = caixa de LIMA; vizinhos = caixas da linha de cima (não LONGE)
    public void ProbeTextStyle_Ocr_ReturnsNextTokenLeftAndNeighbourBoxes()
    {
        var (pdf, sx, sy) = OcrTresLinhas();
        var p = Editor.ProbeTextStyle(pdf, 0, Caixa(270, 137, 150, 29, sx, sy));

        Assert.NotNull(p);
        Assert.Equal("PEREIRA", p!.TokenText);
        Assert.InRange(p.NextTokenLeftPt!.Value, 440 * sx - 0.1, 440 * sx + 0.1);
        var viz = p.NeighbourBoxesPt!;
        Assert.Equal(2, viz.Count); // GONÇALVES e MOURA (linha de cima); LONGE está longe; ANTONIO/LIMA são da mesma linha
        var gon = viz.Single(q => Math.Abs(q.LeftPt - 100 * sx) < 0.5);
        Assert.InRange(gon.BottomPt, 842 - 134 * sy - 0.1, 842 - 134 * sy + 0.1);  // fundo da caixa OCR de GONÇALVES
        Assert.InRange(gon.TopPt, 842 - 100 * sy - 0.1, 842 - 100 * sy + 0.1);
        Assert.InRange(gon.RightPt, 320 * sx - 0.1, 320 * sx + 0.1); // até o início de MOURA (próximo da linha dele)
    }

    [Fact] // última palavra da linha: sem próximo token
    public void ProbeTextStyle_Ocr_LastWordHasNoNextToken()
    {
        var (pdf, sx, sy) = OcrTresLinhas();
        var p = Editor.ProbeTextStyle(pdf, 0, Caixa(440, 136, 90, 30, sx, sy));
        Assert.Equal("LIMA", p!.TokenText);
        Assert.Null(p.NextTokenLeftPt);
    }

    [Fact] // texto visível: nada da geometria OCR
    public void ProbeTextStyle_Visible_NoOcrGeometry()
    {
        var p = Editor.ProbeTextStyle(TextFixtures.TimesOverImage(), 0, TextFixtures.OldWordArea());
        Assert.Null(p!.NextTokenLeftPt);
        Assert.Null(p.NeighbourBoxesPt);
    }

    [Fact] // AreasPt: só os retângulos dados são limpos (o vão entre eles fica)
    public void ApplyTextCorrection_AreasPt_CleansOnlyGivenRectangles()
    {
        byte[] pdf = TextFixtures.TimesOverImage();
        var a = TextFixtures.OldWordArea();
        double meio = (a.LeftPt + a.RightPt) / 2;
        var esquerda = a with { RightPt = meio - 4 };
        var c = new TextCorrection(0, a, "", TextFontFamily.Serif, false, false, 11, 0xFF000000, 700, a.LeftPt,
            0xFFFF0000u, AreasPt: new[] { esquerda });

        var after = Editor.ApplyTextCorrection(pdf, c);

        using var r = new PdfDocumentRenderer(after);
        var pg = r.RenderPage(0, 1.0);
        double h = r.GetPageSize(0).HeightPt;
        uint Px(double x, double y) { int i = ((int)(h - y) * pg.WidthPx + (int)x) * 4; return 0xFF000000u | ((uint)pg.Bgra[i + 2] << 16) | ((uint)pg.Bgra[i + 1] << 8) | pg.Bgra[i]; }
        double ym = (a.BottomPt + a.TopPt) / 2;
        Assert.Equal(0xFFFF0000u, Px((esquerda.LeftPt + esquerda.RightPt) / 2, ym)); // pintado (limpo)
        Assert.NotEqual(0xFFFF0000u, Px(a.RightPt - 2, ym));                         // fora dos retângulos: intacto
    }
    // ---- Rodada 6: caixa OCR alta e fonte estreita — a Helvetica de "CONCEIÇÃO" invade "gqpy" ----------

    /// "CONCEIÇÃO gqpy AÇÃO" como num scan Arial Narrow: a caixa OCR de CONCEIÇÃO é alta (acentos + cedilha)
    /// e estreita, então a Helvetica invisível dela (tamanho = altura da caixa) passa por cima de gqpy.
    /// feat/ocr-largura: esse vazamento só existe na camada LEGADA (sem Tz) — PDFs reconhecidos até a
    /// v2.14.1 — e é nela que estes 3 testes rodam agora (`LegacyOcrLayer`). A camada nova: ver
    /// `ProbeTextStyle_OcrNewLayer_TallNarrowBox_EachWordAlone`.
    private static readonly OcrTextLayer[] ConceicaoBoxes =
    {
        new OcrTextLayer(0, 800, 1131, new[]
        {
            new OcrTextBox("CONCEIÇÃO", LeftPx: 100, TopPx: 100, WidthPx: 170, HeightPx: 46),
            new OcrTextBox("gqpy", LeftPx: 280, TopPx: 112, WidthPx: 70, HeightPx: 34),
            new OcrTextBox("AÇÃO", LeftPx: 360, TopPx: 100, WidthPx: 90, HeightPx: 46),
        }),
    };

    private static (byte[] Pdf, double Sx, double Sy, PdfFont Helv) OcrConceicao() =>
        (LegacyOcrLayer.Apply(Fixtures.NoText(), ConceicaoBoxes),
         595.0 / 800, 842.0 / 1131, PdfFontFactory.CreateFont(StandardFonts.HELVETICA));

    /// Área como o App a monta: caixas de glifo do PDFium da Helvetica invisível do token (largura da
    /// Helvetica a partir do início da caixa OCR), na altura da linha.
    private static PdfQuad AreaHelvetica(double leftPx, double topPx, double hPx, string texto, double sx, double sy, PdfFont helv)
    {
        double size = hPx * sy, l = leftPx * sx, b = 842 - (topPx + hPx) * sy;
        return new PdfQuad(l, b - 0.2 * size, l + helv.GetWidth(texto, (float)size), b + 0.75 * size);
    }

    [Fact] // seleção só de CONCEIÇÃO: gqpy (invadido pela Helvetica dela) não vem junto
    public void ProbeTextStyle_OcrTallBoxOverrun_SelectingFirstWordDoesNotPullNext()
    {
        var (pdf, sx, sy, helv) = OcrConceicao();
        var area = AreaHelvetica(100, 100, 46, "CONCEIÇÃO", sx, sy, helv);
        // sanity: a Helvetica de CONCEIÇÃO cobre mais da metade de gqpy
        Assert.True(area.RightPt > 280 * sx + 0.6 * helv.GetWidth("gqpy", (float)(34 * sy)), $"{area.RightPt} vs {280 * sx}");

        var p = Editor.ProbeTextStyle(pdf, 0, area);

        Assert.Equal("CONCEIÇÃO", p!.TokenText);
    }

    [Fact] // seleção só de gqpy: CONCEIÇÃO (que invade gqpy) não vem junto
    public void ProbeTextStyle_OcrTallBoxOverrun_SelectingSecondWordOnly()
    {
        var (pdf, sx, sy, helv) = OcrConceicao();
        var p = Editor.ProbeTextStyle(pdf, 0, AreaHelvetica(280, 112, 34, "gqpy", sx, sy, helv));
        Assert.Equal("gqpy", p!.TokenText);
    }

    [Fact] // controle: seleção que atravessa de propósito as duas palavras (passa do fim da Helvetica de CONCEIÇÃO)
    public void ProbeTextStyle_OcrTallBoxOverrun_SelectingAcrossBothPullsBoth()
    {
        var (pdf, sx, sy, helv) = OcrConceicao();
        var a1 = AreaHelvetica(100, 100, 46, "CONCEIÇÃO", sx, sy, helv);
        var a2 = AreaHelvetica(280, 112, 34, "gqpy", sx, sy, helv);
        var p = Editor.ProbeTextStyle(pdf, 0, new PdfQuad(a1.LeftPt, Math.Min(a1.BottomPt, a2.BottomPt), Math.Max(a1.RightPt, a2.RightPt) + 4, a1.TopPt));
        Assert.Equal("CONCEIÇÃO gqpy", p!.TokenText);
    }

    [Fact] // camada NOVA (Tz): a palavra invisível tem a largura da caixa OCR — selecionar cada palavra pelas
    // caixas de glifo do PDFium (como o App faz) traz só ela, e as caixas não se sobrepõem.
    public void ProbeTextStyle_OcrNewLayer_TallNarrowBox_EachWordAlone()
    {
        byte[] pdf = Editor.ApplyOcrTextLayer(Fixtures.NoText(), ConceicaoBoxes);
        using var r = new PdfDocumentRenderer(pdf);
        var tp = r.GetTextPage(0);
        PdfQuad Area(string w)
        {
            int i = tp.Text.IndexOf(w, StringComparison.Ordinal);
            Assert.True(i >= 0, $"'{w}' ausente: {tp.Text}");
            var cs = tp.Characters.Skip(i).Take(w.Length).ToList();
            return new PdfQuad(cs.Min(c => c.LeftPt), cs.Min(c => c.BottomPt), cs.Max(c => c.RightPt), cs.Max(c => c.TopPt));
        }
        var conceicao = Area("CONCEIÇÃO");
        var gqpy = Area("gqpy");
        Assert.True(conceicao.RightPt < gqpy.LeftPt, $"CONCEIÇÃO até {conceicao.RightPt}, gqpy de {gqpy.LeftPt}");
        Assert.Equal("CONCEIÇÃO", Editor.ProbeTextStyle(pdf, 0, conceicao)!.TokenText);
        Assert.Equal("gqpy", Editor.ProbeTextStyle(pdf, 0, gqpy)!.TokenText);
    }
}
