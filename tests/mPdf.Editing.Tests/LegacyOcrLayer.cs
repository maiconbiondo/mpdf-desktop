using System.IO; // explícito: o arquivo também compila no App.Tests (WPF), cujos ImplicitUsings não trazem System.IO
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using mPdf.Editing;

namespace mPdf.Editing.Tests;

/// Camada OCR LEGADA (mPDF até v2.14.1): Helvetica invisível de tamanho = altura da caixa, SEM ajuste de
/// largura (sem `Tz`) — a palavra invisível passa da tinta (ou fica aquém dela) conforme a fonte escaneada.
/// PDFs reconhecidos antes de feat/ocr-largura continuam assim para sempre (o comando OCR pula páginas que
/// já têm texto), então o "Corrigir texto" tem de seguir funcionando sobre esta camada. CÓPIA CONGELADA do
/// laço de `PdfEditor.ApplyOcrTextLayer` em c19bc99 (rotação, frame da CropBox e gate de assinatura
/// omitidos — os fixtures de teste são páginas não giradas). Compartilhado com mPdf.App.Tests (Compile Link).
/// NÃO "conserte" este arquivo: ele é o documento antigo.
public static class LegacyOcrLayer
{
    public static byte[] Apply(byte[] pdf, IReadOnlyList<OcrTextLayer> layers)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
        {
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            foreach (var layer in layers)
            {
                if (layer.Boxes is null || layer.Boxes.Count == 0) continue;
                if (layer.SourceWidthPx <= 0 || layer.SourceHeightPx <= 0) continue;
                var page = doc.GetPage(layer.PageIndex + 1);
                if (((page.GetRotation() % 360) + 360) % 360 != 0)
                    throw new NotSupportedException("LegacyOcrLayer: só páginas sem /Rotate (fixtures de teste).");
                var box0 = page.GetCropBox().GetIntersection(page.GetMediaBox()) ?? page.GetMediaBox();
                double llx = box0.GetLeft(), lly = box0.GetBottom();
                double wPt = box0.GetWidth(), hPt = box0.GetHeight();
                double fatorX = wPt / layer.SourceWidthPx;
                double fatorY = hPt / layer.SourceHeightPx;

                var canvas = new PdfCanvas(page);
                foreach (var box in layer.Boxes)
                {
                    if (string.IsNullOrWhiteSpace(box.Text)) continue;
                    double xbDisp = box.LeftPx * fatorX;
                    double ybDisp = hPt - (box.TopPx + box.HeightPx) * fatorY;
                    double fontSize = box.HeightPx * fatorY;
                    if (fontSize <= 0) continue;

                    canvas.BeginText();
                    canvas.SetFontAndSize(font, (float)fontSize);
                    canvas.SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.INVISIBLE);
                    canvas.SetTextMatrix(1, 0, 0, 1, (float)(xbDisp + llx), (float)(ybDisp + lly));
                    canvas.ShowText(box.Text);
                    canvas.EndText();
                }
            }
        }
        return output.ToArray();
    }
}
