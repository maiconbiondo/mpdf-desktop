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
///
/// `girada` (páginas giradas, Task 5 — opt-in): página com /Rotate 90/180/270 como a v2.14.1 gravava — bitmap na
/// orientação EXIBIDA (dims trocadas em 90/270) e a matriz de `ComputeOcrTextMatrix` da v2.14.1 (cópia literal
/// abaixo). Sem a flag, uma página girada continua recusada (os fixtures antigos são todos em pé).
public static class LegacyOcrLayer
{
    public static byte[] Apply(byte[] pdf, IReadOnlyList<OcrTextLayer> layers, bool girada = false)
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
                int rotation = ((page.GetRotation() % 360) + 360) % 360;
                if (rotation != 0 && !girada)
                    throw new NotSupportedException("LegacyOcrLayer: só páginas sem /Rotate (fixtures de teste).");
                var box0 = page.GetCropBox().GetIntersection(page.GetMediaBox()) ?? page.GetMediaBox();
                double llx = box0.GetLeft(), lly = box0.GetBottom();
                double wPt = box0.GetWidth(), hPt = box0.GetHeight();
                bool swap = rotation is 90 or 270;
                double dispWpt = swap ? hPt : wPt, dispHpt = swap ? wPt : hPt;
                double fatorX = dispWpt / layer.SourceWidthPx;
                double fatorY = dispHpt / layer.SourceHeightPx;

                var canvas = new PdfCanvas(page);
                foreach (var box in layer.Boxes)
                {
                    if (string.IsNullOrWhiteSpace(box.Text)) continue;
                    double xbDisp = box.LeftPx * fatorX;
                    double ybDisp = dispHpt - (box.TopPx + box.HeightPx) * fatorY;
                    double fontSize = box.HeightPx * fatorY;
                    if (fontSize <= 0) continue;

                    canvas.BeginText();
                    canvas.SetFontAndSize(font, (float)fontSize);
                    canvas.SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.INVISIBLE);
                    var m = ComputeOcrTextMatrixV2141(rotation, wPt, hPt, xbDisp, ybDisp, llx, lly);
                    canvas.SetTextMatrix((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F);
                    canvas.ShowText(box.Text);
                    canvas.EndText();
                }
            }
        }
        return output.ToArray();
    }

    /// Cópia literal de `PdfEditor.ComputeOcrTextMatrix` da v2.14.1 (rotação 0 = (1,0,0,1, x+llx, y+lly), a
    /// mesma conta de antes desta flag).
    private static (double A, double B, double C, double D, double E, double F) ComputeOcrTextMatrixV2141(
        int rotation, double wPt, double hPt, double xbDisp, double ybDisp, double llx, double lly)
    {
        (double a, double b, double c, double d) = rotation switch
        {
            90 => (0, 1, -1, 0),
            180 => (-1, 0, 0, -1),
            270 => (0, -1, 1, 0),
            _ => (1, 0, 0, 1),
        };
        (double tx, double ty) = rotation switch
        {
            90 => (wPt - ybDisp, xbDisp),
            180 => (wPt - xbDisp, hPt - ybDisp),
            270 => (ybDisp, hPt - xbDisp),
            _ => (xbDisp, ybDisp),
        };
        return (a, b, c, d, tx + llx, ty + lly);
    }
}
