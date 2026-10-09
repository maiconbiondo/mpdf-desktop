using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using mPdf.Editing;

namespace mPdf.Editing.Tests;

/// PDFs sintéticos (dados fictícios) para Corrigir texto. Nada de documento real no repositório.
internal static class TextFixtures
{
    public const float FontSize = 11f;
    public const float LineX = 72f;
    public const float LineBaseline = 700f;
    public const string Prefix = "Endereco: RUA ";
    public const string OldWord = "ANTIGA";
    public const string Suffix = " 123";

    /// A4, imagem de fundo (pagina-solida.jpg) cobrindo a página inteira, uma linha Times-Roman
    /// (não embutida, como os relatórios Crystal Reports da prefeitura) e uma 2ª linha mais abaixo.
    public static byte[] TimesOverImage()
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            var img = ImageDataFactory.Create(Fixtures.PaginaSolida());
            canvas.AddImageFittedIntoRectangle(img, new Rectangle(0, 0, PageSize.A4.GetWidth(), PageSize.A4.GetHeight()), false);
            var times = PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN);
            canvas.BeginText().SetFontAndSize(times, FontSize).MoveText(LineX, LineBaseline)
                  .ShowText(Prefix + OldWord + Suffix).EndText();
            canvas.BeginText().SetFontAndSize(times, FontSize).MoveText(LineX, LineBaseline - 40)
                  .ShowText("Outra linha que nao deve mudar").EndText();
        }
        return output.ToArray();
    }

    /// Área (pontos, origem PDF) que cobre exatamente OldWord na linha de TimesOverImage.
    public static PdfQuad OldWordArea()
    {
        var times = PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN);
        float left = LineX + times.GetWidth(Prefix, FontSize);
        float right = left + times.GetWidth(OldWord, FontSize);
        return new PdfQuad(left, LineBaseline - 3, right, LineBaseline + FontSize);
    }

    /// Página "escaneada" (fixture-sem-texto.pdf) + camada OCR invisível com a palavra OldWord.
    /// Mesma construção de ApplyOcrTextLayerTests.ApplyOcrTextLayer_SingleBox_ProducesExtractableText.
    public static byte[] OcrPage() =>
        PdfEditorFactory.Create().ApplyOcrTextLayer(Fixtures.NoText(), new[]
        {
            new OcrTextLayer(0, 800, 1131, new[] { new OcrTextBox(OldWord, LeftPx: 100, TopPx: 100, WidthPx: 300, HeightPx: 40) }),
        });

    /// Como OcrPage, mas com uma 2ª palavra invisível FORA da área corrigida (mais abaixo). É o caso
    /// real (página OCR tem muitas palavras): o `3 Tr` sobrevive à limpeza do pdfSweep e vazaria
    /// para o texto novo se o conteúdo antigo não fosse isolado.
    public static byte[] OcrPageTwoWords() =>
        PdfEditorFactory.Create().ApplyOcrTextLayer(Fixtures.NoText(), new[]
        {
            new OcrTextLayer(0, 800, 1131, new[]
            {
                new OcrTextBox(OldWord, LeftPx: 100, TopPx: 100, WidthPx: 300, HeightPx: 40),
                new OcrTextBox("PERMANECE", LeftPx: 100, TopPx: 600, WidthPx: 300, HeightPx: 40),
            }),
        });

    /// Área em pontos da caixa OCR acima. fixture-sem-texto é A4 (595x842 pt) sem rotação; o
    /// fator px->pt é 595/800 na horizontal e 842/1131 na vertical (ver doc de OcrTextLayer).
    public static PdfQuad OcrWordArea()
    {
        double sx = 595.0 / 800, sy = 842.0 / 1131;
        double left = 100 * sx, right = 400 * sx;
        double top = 842 - 100 * sy, bottom = 842 - 140 * sy;
        return new PdfQuad(left, bottom, right, top);
    }

    /// Uma única linha com fonte TrueType EMBUTIDA (Inter, Identity-H). O texto CMYK vermelho fica em CmykRedLine.
    public static byte[] EmbeddedInterLine(string text, float size, float x, float y)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            var font = PdfFontFactory.CreateFont(Fixtures.InterTtf(), iText.IO.Font.PdfEncodings.IDENTITY_H,
                PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
            new PdfCanvas(page).BeginText().SetFontAndSize(font, size).MoveText(x, y).ShowText(text).EndText();
        }
        return output.ToArray();
    }

    public static byte[] CmykRedLine(string text, float size, float x, float y)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            new PdfCanvas(page).SetFillColor(new iText.Kernel.Colors.DeviceCmyk(0f, 1f, 1f, 0f))
                .BeginText().SetFontAndSize(font, size).MoveText(x, y).ShowText(text).EndText();
        }
        return output.ToArray();
    }
}
