using System.Globalization;
using System.Security.Cryptography;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using mPdf.Editing;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 4: PIN TESTS do "Corrigir texto" em ROTAÇÃO 0 — valores-ouro capturados no código
/// ANTERIOR à Task 4 (probe serializado com "R" e SHA-256 do conteúdo da página + imagens depois do Apply). A
/// Task 4 não pode mudar nada em página não girada: estes valores ficam fixos.
public class TextCorrectionRotationPinTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();
    private const uint White = 0xFFFFFFFF;

    private static void Pin(string expected, string actual) => Assert.True(expected == actual, $"valor atual: {actual}");

    internal static string Describe(TextStyleProbe? p)
    {
        if (p is null) return "null";
        string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        string Q(PdfQuad q) => $"[{D(q.LeftPt)} {D(q.BottomPt)} {D(q.RightPt)} {D(q.TopPt)}]";
        return $"{p.Text}|{p.Family}|{p.Bold}|{p.Italic}|{D(p.SizePt)}|{p.ColorArgb:X8}|{D(p.BaselineYPt)}|{D(p.LeftXPt)}|{D(p.RightXPt)}|{p.Invisible}"
             + $"|{p.TokenText}|{(p.TokenAreaPt is { } t ? Q(t) : "-")}|{(p.NextTokenLeftPt is { } n ? D(n) : "-")}"
             + $"|{(p.NeighbourBoxesPt is { } v ? string.Join(",", v.Select(Q)) : "-")}";
    }

    /// SHA-256 (8 primeiros hex) do conteúdo da página 1 + bytes crus de cada imagem XObject (o pdfSweep pinta a
    /// área na imagem). O /ID e as datas do trailer ficam de fora (mudam a cada gravação).
    internal static string PageHash(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var page = doc.GetPage(1);
        using var ms = new MemoryStream();
        ms.Write(page.GetContentBytes());
        var xobjs = page.GetResources().GetResource(PdfName.XObject);
        if (xobjs is not null)
            foreach (var name in xobjs.KeySet().OrderBy(n => n.GetValue(), StringComparer.Ordinal))
                if (xobjs.GetAsStream(name) is { } s) ms.Write(s.GetBytes(false));
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()))[..16];
    }

    private static byte[] CropOffsetPage(float ox, float oy)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(595 + 2 * ox, 842 + 2 * oy));
            page.SetCropBox(new Rectangle(ox, oy, 595, 842));
            var times = PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN);
            var canvas = new PdfCanvas(page);
            canvas.BeginText().SetFontAndSize(times, 9).MoveText(110 + ox, 689 + oy).ShowText("Endereco: RUA XV DE NOVEMBRO, 2023").EndText();
            canvas.BeginText().SetFontAndSize(times, 9).MoveText(110 + ox, 678 + oy).ShowText("Responsavel: JOSE MARIA DA SILVA").EndText();
        }
        return output.ToArray();
    }

    private static byte[] OcrColadas() => Editor.ApplyOcrTextLayer(Fixtures.NoText(), new[]
    {
        new OcrTextLayer(0, 800, 1131, new[]
        {
            new OcrTextBox("gqpjy", LeftPx: 100, TopPx: 100, WidthPx: 200, HeightPx: 40),
            new OcrTextBox("Joaquim", LeftPx: 100, TopPx: 143, WidthPx: 260, HeightPx: 40),
            new OcrTextBox("Pereira", LeftPx: 370, TopPx: 143, WidthPx: 200, HeightPx: 40),
            new OcrTextBox("lhkbdf", LeftPx: 100, TopPx: 186, WidthPx: 220, HeightPx: 40),
        }),
    });

    private static readonly PdfQuad OcrJoaquim = new(100 * 595.0 / 800, 842 - 183 * 842.0 / 1131 - 0.5, 360 * 595.0 / 800, 842 - 143 * 842.0 / 1131 + 0.5);

    [Fact]
    public void Probe_Digital_Pin()
    {
        Pin(PinProbeDigital, Describe(Editor.ProbeTextStyle(TextFixtures.TimesOverImage(), 0, TextFixtures.OldWordArea())));
    }

    [Fact]
    public void Probe_CropBox_Pin()
    {
        Pin(PinProbeCrop, Describe(Editor.ProbeTextStyle(CropOffsetPage(30, 50), 0, new PdfQuad(200, 686, 260, 698))));
    }

    [Fact]
    public void Probe_Ocr_Pin()
    {
        Pin(PinProbeOcr, Describe(Editor.ProbeTextStyle(OcrColadas(), 0, OcrJoaquim)));
    }

    [Fact]
    public void Apply_Digital_Pin()
    {
        var a = TextFixtures.OldWordArea();
        var after = Editor.ApplyTextCorrection(TextFixtures.TimesOverImage(), new TextCorrection(0, a, "NOVA", TextFontFamily.Serif,
            false, false, TextFixtures.FontSize, 0xFF000000, TextFixtures.LineBaseline, a.LeftPt, White));
        Pin(PinApplyDigital, PageHash(after));
    }

    [Fact]
    public void Apply_CropBox_Pin()
    {
        var after = Editor.ApplyTextCorrection(CropOffsetPage(30, 50), new TextCorrection(0, new PdfQuad(200, 686, 260, 698), "DEZEMBRO",
            TextFontFamily.Serif, true, false, 9, 0xFF203040, 689, 201.5, 0xFFF0F0F0));
        Pin(PinApplyCrop, PageHash(after));
    }

    [Fact]
    public void Apply_Ocr_Pin()
    {
        var b = OcrJoaquim;
        var areas = new[] { new PdfQuad(b.LeftPt, b.BottomPt, b.RightPt, b.BottomPt + 10), new PdfQuad(b.LeftPt + 5, b.BottomPt + 10, b.RightPt - 5, b.TopPt) };
        var after = Editor.ApplyTextCorrection(OcrColadas(), new TextCorrection(0, b, "Antonio", TextFontFamily.SansSerif, false, false,
            20, 0xFF000000, b.BottomPt + 0.5, b.LeftPt, White, MinOverlapRatio: 0.4, AreasPt: areas));
        Pin(PinApplyOcr, PageHash(after));
    }

    // Valores-ouro capturados no código ANTERIOR à Task 4 (head d0e766d).
    private const string PinProbeDigital =
        "ANTIGA|Serif|False|False|11|FF000000|700|145.31500244140625|187.46701049804688|False||-|-|-";
    private const string PinProbeCrop =
        "NOVEMBRO, 2|Serif|False|False|9|FF000000|689|199.47799682617188|259.97601318359375|False||-|-|-";
    private const string PinProbeOcr =
        "Joaquim|SansSerif|False|False|29.780000686645508|FF000000|705.760009765625|74.37999725341797|186.91860961914062|True|Joaquim|[74.37999725341797 699.5955200195312 186.91860961914062 727.1420288085938]|275.19000244140625|[74.37999725341797 737.77001953125 154.48819942474364 767.5500202178955],[74.37999725341797 673.75 162.76703853607177 703.5300006866455]";
    private const string PinApplyDigital = "EA4E42EFED68C91D";
    private const string PinApplyCrop = "4513B3A20FB14E25";
    private const string PinApplyOcr = "FA001980EEBCA557";
}
