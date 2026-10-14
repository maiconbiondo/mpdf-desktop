using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Xobject;

namespace mPdf.Editing.Tests;

/// Revisão crítica F1, item E1: UMA anotação malformada de outro programa não pode derrubar `ReadAnnotations` do
/// documento INTEIRO (no App: nenhuma anotação selecionável/movível/excluível, sem aviso). Cada leitura opcional
/// degrada localmente (perde só o próprio dado); se nem a geometria é legível, só AQUELA anotação some da lista.
public class LeituraToleranteTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    /// Página A4 com um realce íntegro ("ok") + a anotação `ruim` montada por `montar`.
    private static byte[] ComRuim(Func<PdfDocument, PdfAnnotation> montar, int rot = 0)
    {
        using var ms = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(ms)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            if (rot != 0) page.SetRotation(rot);
            var hl = PdfTextMarkupAnnotation.CreateHighLight(new Rectangle(60, 700, 100, 15),
                new float[] { 60, 715, 160, 715, 60, 700, 160, 700 });
            hl.SetName(new PdfString("ok"));
            hl.SetColor(new float[] { 1, 1, 0 });
            page.AddAnnotation(hl);
            var ruim = montar(doc);
            ruim.SetName(new PdfString("ruim"));
            page.AddAnnotation(ruim);
        }
        return ms.ToArray();
    }

    private static PdfArray Arr(params PdfObject[] itens) { var a = new PdfArray(); foreach (var i in itens) a.Add(i); return a; }
    private static PdfNumber N(double v) => new(v);

    private static PdfAnnotation CarimboComMatriz(PdfDocument doc, PdfArray matriz)
    {
        var st = new PdfStampAnnotation(new Rectangle(200, 400, 100, 100));
        var ap = new PdfFormXObject(new Rectangle(0, 0, 100, 100));
        new iText.Kernel.Pdf.Canvas.PdfCanvas(ap, doc).SetFillColor(new iText.Kernel.Colors.DeviceRgb(255, 0, 0))
            .Rectangle(0, 0, 100, 100).Fill();
        ap.GetPdfObject().Put(PdfName.Matrix, matriz);
        st.SetNormalAppearance(ap.GetPdfObject());
        return st;
    }

    [Theory] // o caso do revisor (/Matrix com null) + outros elementos não numéricos, em página normal e girada
    [InlineData(0)]
    [InlineData(90)]
    public void MatrizDoApMalformada_NaoDerrubaALeitura(int rot)
    {
        foreach (var matriz in new[]
        {
            Arr(N(1), N(0), PdfNull.PDF_NULL, N(1), N(0), N(0)),
            Arr(N(1), new PdfName("x"), N(0), N(1), N(0), N(0)),
            Arr(N(1), N(0), N(0), new PdfString("1"), N(0), N(0)),
        })
        {
            var lidas = Ed.ReadAnnotations(ComRuim(d => CarimboComMatriz(d, matriz), rot));
            Assert.Equal(new[] { "ok", "ruim" }, lidas.Select(a => a.Id).OrderBy(x => x));
            var st = lidas.Single(a => a.Id == "ruim");
            Assert.Equal(AnnotationKind.ImageStamp, st.Kind);
            Assert.Null(st.ConteudoRotacaoGraus); // orientação ilegível = em pé
            Assert.True(st.RightPt > st.LeftPt && st.TopPt > st.BottomPt);
        }
    }

    public static IEnumerable<object[]> Malformadas() => new (string, Func<PdfDocument, PdfAnnotation>)[]
    {
        ("QuadPoints não numérico", _ =>
        {
            var u = PdfTextMarkupAnnotation.CreateUnderline(new Rectangle(60, 600, 100, 15), new float[] { 60, 615, 160, 615, 60, 600, 160, 600 });
            u.GetPdfObject().Put(PdfName.QuadPoints, Arr(N(60), N(615), PdfNull.PDF_NULL, N(615), N(60), N(600), N(160), N(600)));
            return u;
        }),
        ("/C não numérico", _ =>
        {
            var u = PdfTextMarkupAnnotation.CreateHighLight(new Rectangle(60, 600, 100, 15), new float[] { 60, 615, 160, 615, 60, 600, 160, 600 });
            u.GetPdfObject().Put(PdfName.C, Arr(N(1), new PdfName("a"), N(0)));
            return u;
        }),
        ("/L não numérico", _ =>
        {
            var l = new PdfLineAnnotation(new Rectangle(60, 500, 100, 50), new float[] { 60, 500, 160, 550 });
            l.GetPdfObject().Put(PdfName.L, Arr(N(60), PdfNull.PDF_NULL, N(160), N(550)));
            return l;
        }),
        ("/InkList não numérico", _ =>
        {
            var traco = Arr(N(60), N(500), new PdfName("y"), N(550));
            return new PdfInkAnnotation(new Rectangle(60, 500, 100, 50), Arr(traco));
        }),
        ("/InkList com elemento que não é array", _ =>
            new PdfInkAnnotation(new Rectangle(60, 500, 100, 50), Arr(N(3)))),
        ("/DA e /AP de FreeText malformados", d =>
        {
            var ft = new PdfFreeTextAnnotation(new Rectangle(60, 500, 200, 40), new PdfString("x"));
            ft.GetPdfObject().Put(PdfName.DA, new PdfString("/Helv nao Tf 1 x 0 rg Tf rg g"));
            var ap = new PdfFormXObject(new Rectangle(0, 0, 200, 40));
            var fontes = new PdfDictionary();
            fontes.Put(new PdfName("F1"), N(7)); // fonte que não é dicionário
            var res = new PdfDictionary(); res.Put(PdfName.Font, fontes);
            ap.GetPdfObject().Put(PdfName.Resources, res);
            ap.GetPdfObject().Put(PdfName.Matrix, Arr(N(1), N(0), N(0)));
            ft.SetNormalAppearance(ap.GetPdfObject());
            return ft;
        }),
        ("/Border e /BS malformados", _ =>
        {
            var sq = new PdfSquareAnnotation(new Rectangle(60, 500, 100, 50));
            sq.GetPdfObject().Put(PdfName.Border, Arr(N(0), N(0), new PdfName("w")));
            sq.GetPdfObject().Put(PdfName.IC, Arr(PdfNull.PDF_NULL, N(0), N(0)));
            return sq;
        }),
        ("/Rotate da anotação não numérico", _ =>
        {
            var ft = new PdfFreeTextAnnotation(new Rectangle(60, 500, 200, 40), new PdfString("x"));
            ft.GetPdfObject().Put(PdfName.Rotate, new PdfName("noventa"));
            return ft;
        }),
        ("/Rect malformado", _ =>
        {
            var sq = new PdfSquareAnnotation(new Rectangle(60, 500, 100, 50));
            sq.GetPdfObject().Put(PdfName.Rect, Arr(N(60), PdfNull.PDF_NULL, N(160)));
            return sq;
        }),
    }.Select(c => new object[] { c.Item1, c.Item2 });

    [Theory, MemberData(nameof(Malformadas))]
    public void UmaAnotacaoMalformada_SoElaPerdeDados(string caso, Func<PdfDocument, PdfAnnotation> montar)
    {
        foreach (int rot in new[] { 0, 90 })
        {
            IReadOnlyList<AnnotationData> lidas;
            try { lidas = Ed.ReadAnnotations(ComRuim(montar, rot)); }
            catch (Exception ex) { throw new Xunit.Sdk.XunitException($"{caso} (rot {rot}): ReadAnnotations lançou {ex.GetType().Name}: {ex.Message}"); }
            var ok = lidas.SingleOrDefault(a => a.Id == "ok");
            Assert.True(ok is not null, $"{caso} (rot {rot}): a anotação íntegra sumiu");
            Assert.Equal(0xFFFFFF00u, ok!.ColorArgb);
        }
    }
}
