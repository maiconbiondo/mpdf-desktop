using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;

namespace mPdf.Editing.Tests;

/// Páginas giradas, Task 1 — PIN TESTS da camada OCR (`ApplyOcrTextLayer` / `CriarPdfADeImagens`).
/// Valores-ouro capturados ANTES da refatoração que trocou `ComputeOcrTextMatrix` por `DisplayFrame.TextMatrix`
/// (a transformação única exibido↔usuário). A saída tem de continuar BYTE A BYTE idêntica nas 4 rotações,
/// com e sem CropBox deslocada: o conteúdo inteiro da página (SHA-256) e, legível, os operadores `Tm`.
public class RotationOcrPinTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    private static readonly OcrTextBox[] Boxes =
    {
        new("CONTRATO", LeftPx: 100, TopPx: 120, WidthPx: 300, HeightPx: 40),
        new("Joaquim", LeftPx: 433.5, TopPx: 517.25, WidthPx: 91, HeightPx: 23.5),
    };

    /// Página em branco com /Rotate `rot`; `crop` = CropBox (30,40)-(530,760) dentro da MediaBox A4.
    private static byte[] Blank(int rot, bool crop)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            page.SetRotation(rot);
            if (crop) page.SetCropBox(new Rectangle(30, 40, 500, 720));
        }
        return ms.ToArray();
    }

    private static byte[] PageContent(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        return doc.GetPage(1).GetContentBytes();
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static string TmLines(byte[] content) =>
        string.Join(" | ", Regex.Matches(Encoding.Latin1.GetString(content), @"[^\n]* Tm").Select(m => m.Value.Trim()));

    [Theory]
    [InlineData(0, false, "1 0 0 1 50 762 Tm | 1 0 0 1 216.75 571.63 Tm", "A7D4CA2F966D7EA31DBC49B0F88944FD077D5554A215CB210D463726F3A4E252")]
    [InlineData(90, false, "0 1 -1 0 80 50 Tm | 0 1 -1 0 270.38 216.75 Tm", "DCCB2B235AC252897E1EBB2E47D53819A70AD66113325B80625F66464B16BEB1")]
    [InlineData(180, false, "-1 0 0 -1 545 80 Tm | -1 0 0 -1 378.25 270.38 Tm", "E0589E39E620CBB821DBE77D9A9D38B9BAAEF92597566808E624AB852A238B1D")]
    [InlineData(270, false, "0 -1 1 0 515 792 Tm | 0 -1 1 0 324.63 625.25 Tm", "754A94ECE90C53F112F200167A2A5623AE99F5BF83A63905FAFC30BB199316D0")]
    [InlineData(0, true, "1 0 0 1 72.02 691.59 Tm | 1 0 0 1 212.14 528.8 Tm", "8C8EC41AFAF4D3A7D30580817DCC1330F38FBE416CBFDF0CF2AC537CDCE5F392")]
    [InlineData(90, true, "0 1 -1 0 97.23 82.76 Tm | 0 1 -1 0 257.21 225.34 Tm", "D1B174B17BDA1AC160B4BA984689E148D8A579707D3125C2E9C7696085945E75")]
    [InlineData(180, true, "-1 0 0 -1 487.98 108.41 Tm | -1 0 0 -1 347.86 271.2 Tm", "9F55E223AC39055F5E5A01B2C467EF62528B419E98E8D7DA23334A2F2A2A9BB9")]
    [InlineData(270, true, "0 -1 1 0 462.77 717.24 Tm | 0 -1 1 0 302.79 574.66 Tm", "25F8B4CA713AF9DE0E881B21D2FC733C4944DB72283BC34353D6A673FE105F1F")]
    public void ApplyOcrTextLayer_OutputIsPinned(int rot, bool crop, string goldenTm, string goldenSha)
    {
        // Bitmap-fonte com a orientação EXIBIDA (o App rasteriza a página já girada).
        bool swap = rot is 90 or 270;
        var layer = new OcrTextLayer(0, swap ? 1684 : 1190, swap ? 1190 : 1684, Boxes);
        var content = PageContent(Editor.ApplyOcrTextLayer(Blank(rot, crop), new[] { layer }));

        Assert.Equal(goldenTm, TmLines(content));
        Assert.Equal(goldenSha, Sha(content));
    }

    [Fact]
    public void CriarPdfADeImagens_OcrOutputIsPinned()
    {
        var pag = new PdfAPaginaImagem(Fixtures.PaginaSolida(), 612, 792, 1224, 1584, Boxes);
        var pdf = Editor.CriarPdfADeImagens(new[] { pag }, PdfANivel.A2B, new PdfAMetadados("Doc", null), Fixtures.InterTtf());
        var content = PageContent(pdf);

        Assert.Equal("1 0 0 1 50 712 Tm | 1 0 0 1 216.75 521.63 Tm", TmLines(content));
        Assert.Equal("AA012F278E60FF42D9F3C9D55EE4D4B095CFD252777F425DA9B6030C5E6B649B", Sha(content));
    }
}
