using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Fix round 2 da revisão final (defeito ANTIGO, já na v2.16): `ReadAnnotations` não devolvia a formatação do
/// FreeText (tamanho, negrito, itálico, família) — todo lift do App (mover, redimensionar, colar, replicar)
/// reescrevia a caixa em 12 pt Helvetica normal. Agora a leitura devolve a formatação (a fonte do /AP que nós
/// desenhamos; o /DA como reserva — inclusive de outros programas), e o lift faz round-trip exato.
public class FreeTextFormatoRoundTripTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();
    private const uint Vermelho = 0xFFD32F2F;

    private static AnnotationData Formatada() => new()
    {
        Id = "ft", Kind = AnnotationKind.FreeText, PageIndex = 0, Content = "Formatado",
        FontSizePt = 14, Bold = true, Italic = true, FontFamily = "Times", ColorArgb = Vermelho,
        LeftPt = 100, BottomPt = 300, RightPt = 300, TopPt = 340,
    };

    private static AnnotationData Ler(byte[] pdf) => Ed.ReadAnnotations(pdf).Single(a => a.Id == "ft");

    private static void Igual(AnnotationData esperado, AnnotationData lida, string ctx)
    {
        Assert.True(esperado.FontSizePt == lida.FontSizePt, $"{ctx}: tamanho {lida.FontSizePt}");
        Assert.True(esperado.Bold == lida.Bold, $"{ctx}: negrito {lida.Bold}");
        Assert.True(esperado.Italic == lida.Italic, $"{ctx}: itálico {lida.Italic}");
        Assert.Equal(esperado.FontFamily, lida.FontFamily);
        Assert.Equal(esperado.ColorArgb, lida.ColorArgb);
    }

    /// Caixa (pt) da tinta vermelha.
    private static Blob Tinta(byte[] pdf) => Find(Render(pdf), (r, g, b) => r > 150 && r - g > 60 && r - b > 60);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void Formatada_MoverDuasVezesRedimensionarCor_RoundTripExato(int rot)
    {
        var pdf = Ed.AddAnnotation(Page(rot, false), Formatada());
        Igual(Formatada(), Ler(pdf), $"rot {rot}: lida");
        var t0 = Tinta(pdf);
        Assert.True(t0.Count > 0);

        for (int k = 1; k <= 2; k++)
        {
            pdf = AnnotationContentRotationTests.Lift(pdf, "ft", a => AnnotationContentRotationTests.Mover(a, 20, -15));
            Igual(Formatada(), Ler(pdf), $"rot {rot}: movida {k}×");
            var t = Tinta(pdf);
            Assert.Equal(t0.T - t0.B, t.T - t.B, 1); // altura dos glifos (14 pt negrito ≠ 12 pt normal)
            Assert.Equal(t0.R - t0.L, t.R - t.L, 1);
        }

        pdf = AnnotationContentRotationTests.Lift(pdf, "ft", a => a with { RightPt = a.RightPt + 60, BottomPt = a.BottomPt - 30 });
        Igual(Formatada(), Ler(pdf), $"rot {rot}: redimensionada");
        Assert.Equal(t0.T - t0.B, Tinta(pdf).T - Tinta(pdf).B, 1);

        pdf = AnnotationContentRotationTests.Lift(pdf, "ft", a => a with { ColorArgb = 0xFF1D4E89 });
        Igual(Formatada() with { ColorArgb = 0xFF1D4E89 }, Ler(pdf), $"rot {rot}: cor nova");
    }

    public static IEnumerable<object?[]> Combinacoes() =>
        from fam in new[] { "Helvetica", "Times", "Courier" }
        from b in new[] { false, true }
        from i in new[] { false, true }
        select new object?[] { fam, b, i };

    [Theory, MemberData(nameof(Combinacoes))] // inclui Helvetica itálico: o /DA do iText grava "/Courier-Oblique" nesse caso
    public void TodasAsFontes_RoundTrip(string fam, bool bold, bool italic)
    {
        var a = Formatada() with { FontFamily = fam, Bold = bold, Italic = italic, FontSizePt = 9.5, ColorArgb = null };
        var lida = Ler(Ed.AddAnnotation(Page(0, false), a));
        Assert.Equal((fam, bold, italic, (double?)9.5), (lida.FontFamily, lida.Bold, lida.Italic, lida.FontSizePt));
        Assert.Null(lida.ColorArgb); // sem cor = texto preto default, continua null
    }

    [Fact] // sem formatação informada: lê os defaults explícitos (12 pt Helvetica) — o lift desenha igual
    public void SemFormatacao_LeDefaults()
    {
        var lida = Ler(Ed.AddAnnotation(Page(0, false), Formatada() with { FontSizePt = null, Bold = false, Italic = false, FontFamily = null, ColorArgb = null }));
        Assert.Equal(("Helvetica", false, false, (double?)12), (lida.FontFamily, lida.Bold, lida.Italic, lida.FontSizePt));
        Assert.Null(lida.ColorArgb);
    }

    [Fact] // documento salvo pela v2.16 com Helvetica itálico: /DA "/Courier-Oblique" (iText), /AP em Helvetica-Oblique
    public void DocumentoAntigo_DaErrado_ApManda()
    {
        var pdf = Ed.AddAnnotation(Page(0, false), Formatada() with { FontFamily = "Helvetica", Bold = false, Italic = true });
        using var o = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), new PdfWriter(o)))
            doc.GetPage(1).GetAnnotations()[0].GetPdfObject().Put(PdfName.DA, new PdfString("/Courier-Oblique 14 Tf 0.827451 0.18431373 0.18431373 rg"));
        var lida = Ler(o.ToArray());
        Assert.Equal(("Helvetica", false, true), (lida.FontFamily, lida.Bold, lida.Italic));
    }

    private static byte[] Terceiro(string? da, bool comC = false)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            var ft = new PdfFreeTextAnnotation(new Rectangle(100, 100, 200, 50), new PdfString("x"));
            ft.SetName(new PdfString("ft"));
            if (da is not null) ft.GetPdfObject().Put(PdfName.DA, new PdfString(da));
            if (comC) ft.SetColor(new float[] { 0, 1, 0 });
            page.AddAnnotation(ft);
        }
        return ms.ToArray();
    }

    [Theory] // outros programas: melhor esforço pelo /DA (nomes curtos do Acrobat, rg/g); senão os defaults (null)
    [InlineData("/Helv 10 Tf 0 0 1 rg", "Helvetica", false, false, 10.0, 0xFF0000FFu)]
    [InlineData("/TiBo 9 Tf 1 0 0 rg", "Times", true, false, 9.0, 0xFFFF0000u)]
    [InlineData("/CoBO 11.5 Tf 0 g", "Courier", true, true, 11.5, null)]
    [InlineData("0 0 0 rg /HeOb 8 Tf", "Helvetica", false, true, 8.0, null)]
    [InlineData("/Arial,Bold 12 Tf 0.5 g", "Helvetica", true, false, 12.0, 0xFF808080u)]
    public void Terceiros_MelhorEsforcoPeloDa(string da, string fam, bool bold, bool italic, double size, uint? cor)
    {
        var lida = Ler(Terceiro(da));
        Assert.Equal((fam, bold, italic, (double?)size, cor), (lida.FontFamily, lida.Bold, lida.Italic, lida.FontSizePt, lida.ColorArgb));
    }

    [Fact]
    public void Terceiros_SemDaOuIlegivel_Defaults()
    {
        foreach (var da in new[] { null, "lixo", "/F1 Tf" })
        {
            var lida = Ler(Terceiro(da));
            Assert.Equal(((string?)null, false, false, (double?)null), (lida.FontFamily, lida.Bold, lida.Italic, lida.FontSizePt));
        }
    }

    [Fact] // revisão F1 (E4): /C diferente da cor do /DA = FUNDO da caixa (Acrobat/PDFBox); o texto é a cor do /DA
    public void Terceiros_CDiferenteDoDa_EFundo()
    {
        var lida = Ler(Terceiro("/Helv 10 Tf 0 0 1 rg", comC: true));
        Assert.Equal((0xFF0000FFu, (uint?)0xFF00FF00u), (lida.ColorArgb!.Value, lida.FillArgb));
    }
}
