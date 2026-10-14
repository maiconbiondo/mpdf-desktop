using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using static mPdf.Editing.Tests.AnnotationRotationRenderTests;

namespace mPdf.Editing.Tests;

/// Revisão crítica F1, item E5: anotação com a flag NoRotate (/F bit 5 — a nota do Acrobat grava /F 28 =
/// Print|NoZoom|NoRotate) numa página girada. O PDFium a desenha EM PÉ girando o /AP em torno do canto
/// sup.-esq. do /Rect (no espaço do usuário), então ela aparece noutro lugar que não o /Rect girado:
/// `ReadAnnotations` devolvia o /Rect girado (hit-test fora do ícone; Del apagava o vazio) e o lift descartava
/// /F (o ícone pulava 20 pt). Agora: bbox = onde o PDFium desenha; /F faz round-trip (`AnnotationData.Flags`).
/// Oráculo: render PDFium.
public class NoRotateTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();
    private const int F28 = PdfAnnotation.PRINT | PdfAnnotation.NO_ZOOM | PdfAnnotation.NO_ROTATE;

    public static IEnumerable<object[]> Casos() =>
        from rot in new[] { 0, 90, 180, 270 }
        from comAp in new[] { false, true }
        select new object[] { rot, comAp };

    /// Página A4 com /Rotate `rot` e uma nota do Acrobat (/F 28) em [300,500,320,520] (usuário).
    private static byte[] Nota(int rot, bool comAp, Rectangle? r = null)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            if (rot != 0) page.SetRotation(rot);
            var rect = r ?? new Rectangle(300, 500, 20, 20);
            var nt = new PdfTextAnnotation(rect);
            nt.SetName(new PdfString("nt"));
            nt.SetContents("nota");
            nt.SetColor(new float[] { 1, 1, 0 });
            nt.SetFlags(F28);
            if (comAp)
            {
                var ap = new PdfFormXObject(new Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()));
                new PdfCanvas(ap, doc).SetFillColor(new DeviceRgb(255, 255, 0)).Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()).Fill();
                nt.SetNormalAppearance(ap.GetPdfObject());
            }
            page.AddAnnotation(nt);
        }
        return ms.ToArray();
    }

    private static AnnotationData Ler(byte[] pdf, string id = "nt") => Ed.ReadAnnotations(pdf).Single(a => a.Id == id);

    private static void BateCom(Blob tinta, AnnotationData a, double tol, string ctx)
    {
        Assert.True(tinta.Count > 0, ctx + ": nada desenhado");
        Assert.True(Math.Abs(tinta.L - a.LeftPt) <= tol && Math.Abs(tinta.R - a.RightPt) <= tol
                    && Math.Abs(tinta.B - a.BottomPt) <= tol && Math.Abs(tinta.T - a.TopPt) <= tol,
            $"{ctx}: lida ({a.LeftPt},{a.BottomPt})-({a.RightPt},{a.TopPt}), desenhada ({tinta.L},{tinta.B})-({tinta.R},{tinta.T})");
    }

    [Theory, MemberData(nameof(Casos))]
    public void NotaNoRotate_BboxLidaEOndeOPdfiumDesenha(int rot, bool comAp)
    {
        var pdf = Nota(rot, comAp);
        var lida = Ler(pdf);
        Assert.Equal(F28, lida.Flags);
        Assert.Null(lida.ConteudoRotacaoGraus);
        BateCom(Find(Render(pdf), NonWhite), lida, 1.5, $"rot {rot} ap {comAp}");
    }

    [Theory, MemberData(nameof(Casos))]
    public void NotaNoRotate_LiftPreservaFlagsEPosicao_EMoverDeslocaNaTela(int rot, bool comAp)
    {
        var pdf = Nota(rot, comAp);
        var antes = Find(Render(pdf), NonWhite);

        var editada = AnnotationContentRotationTests.Lift(pdf, "nt", a => a with { Content = "outra" });
        var lida = Ler(editada);
        Assert.Equal(F28, lida.Flags);
        var depois = Find(Render(editada), NonWhite);
        Assert.True(Math.Abs(depois.L - antes.L) <= 1.5 && Math.Abs(depois.T - antes.T) <= 1.5,
            $"rot {rot}: ícone pulou de ({antes.L},{antes.T}) para ({depois.L},{depois.T})");
        BateCom(depois, lida, 1.5, $"rot {rot} editada");

        var movida = AnnotationContentRotationTests.Lift(editada, "nt", a => AnnotationContentRotationTests.Mover(a, 30, -40));
        var m = Find(Render(movida), NonWhite);
        Assert.True(Math.Abs(m.L - (antes.L + 30)) <= 1.5 && Math.Abs(m.T - (antes.T - 40)) <= 1.5,
            $"rot {rot}: movida para ({m.L},{m.T}), esperado ({antes.L + 30},{antes.T - 40})");
        BateCom(m, Ler(movida), 1.5, $"rot {rot} movida");
    }

    [Theory] // FreeText/carimbo de outro programa com NoRotate (caixa não quadrada: o canto de giro importa)
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void CaixaECarimboNoRotate_BboxLidaEOndeOPdfiumDesenha_ELiftMantem(int rot)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            page.SetRotation(rot);
            var r = new Rectangle(200, 400, 120, 40);
            var ft = new PdfFreeTextAnnotation(r, new PdfString("Caixa"));
            ft.SetName(new PdfString("ft"));
            ft.SetColor(new float[] { 1, 1, 0 }); // fundo (Acrobat)
            ft.GetPdfObject().Put(PdfName.DA, new PdfString("/Helv 12 Tf 0 0 1 rg"));
            ft.SetFlags(PdfAnnotation.PRINT | PdfAnnotation.NO_ROTATE);
            var ap = new PdfFormXObject(new Rectangle(0, 0, 120, 40));
            new PdfCanvas(ap, doc).SetFillColor(new DeviceRgb(255, 255, 0)).Rectangle(0, 0, 120, 40).Fill();
            ft.SetNormalAppearance(ap.GetPdfObject());
            page.AddAnnotation(ft);

            var st = new PdfStampAnnotation(new Rectangle(100, 150, 80, 40));
            st.SetName(new PdfString("st"));
            st.SetFlags(PdfAnnotation.PRINT | PdfAnnotation.NO_ROTATE);
            var sap = new PdfFormXObject(new Rectangle(0, 0, 80, 40));
            new PdfCanvas(sap, doc).SetFillColor(new DeviceRgb(0, 200, 0)).Rectangle(0, 0, 80, 40).Fill();
            st.SetNormalAppearance(sap.GetPdfObject());
            page.AddAnnotation(st);
        }
        var pdf = ms.ToArray();
        var p = Render(pdf);
        BateCom(Find(p, Yellow), Ler(pdf, "ft"), 1.5, $"rot {rot} caixa");
        BateCom(Find(p, Green), Ler(pdf, "st"), 1.5, $"rot {rot} carimbo");

        var liftado = AnnotationContentRotationTests.Lift(pdf, "ft", a => a);
        liftado = AnnotationContentRotationTests.Lift(liftado, "st", a => a, MarkerPng);
        var q = Render(liftado);
        BateCom(Find(q, Yellow), Ler(liftado, "ft"), 1.5, $"rot {rot} caixa liftada");
        var ft2 = Ler(liftado, "ft");
        Assert.Equal(PdfAnnotation.PRINT | PdfAnnotation.NO_ROTATE, ft2.Flags);
        Assert.Equal(Ler(pdf, "ft").LeftPt, ft2.LeftPt, 1);
        Assert.Equal(Ler(pdf, "ft").TopPt, ft2.TopPt, 1);
        var st2 = Ler(liftado, "st");
        Assert.Equal((Ler(pdf, "st").LeftPt, Ler(pdf, "st").TopPt), (Math.Round(st2.LeftPt, 1), Math.Round(st2.TopPt, 1)));
        // o carimbo liftado (MarkerPng: faixa vermelha no topo) sai EM PÉ
        var vermelho = Find(q, Red);
        Assert.True(vermelho.Count > 0 && vermelho.Cy > st2.BottomPt + (st2.TopPt - st2.BottomPt) / 2, $"rot {rot}: carimbo não está em pé");
    }

    [Fact] // flags quaisquer (sem NoRotate) também fazem round-trip; anotação do mPDF continua sem /F
    public void Flags_RoundTrip_ESemFlagsContinuaNull()
    {
        var pdf = Ed.AddAnnotation(Page(0, false), new AnnotationData
        {
            Id = "rc", Kind = AnnotationKind.Rectangle, PageIndex = 0, ColorArgb = 0xFFFF0000,
            LeftPt = 100, BottomPt = 100, RightPt = 200, TopPt = 200,
        });
        Assert.Null(Ler(pdf, "rc").Flags);
        var com = AnnotationContentRotationTests.Lift(pdf, "rc", a => a with { Flags = PdfAnnotation.PRINT | PdfAnnotation.LOCKED });
        Assert.Equal(PdfAnnotation.PRINT | PdfAnnotation.LOCKED, Ler(com, "rc").Flags);
    }
}
