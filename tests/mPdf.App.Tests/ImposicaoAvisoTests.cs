using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using mPdf.App.Services;
using mPdf.App.Views;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// Revisão final das páginas giradas (P2, defeito antigo): o "Salvar PDF" da impressão avançada (ImporEmFolhas via
/// CopyAsFormXObject) leva só o CONTEÚDO das páginas — anotações e assinaturas (inclusive o carimbo visível) ficam
/// de fora. Nesta versão o diálogo AVISA quando o documento tem anotações ou assinaturas.
public class ImposicaoAvisoTests
{
    private static IPdfEditor Ed => PdfEditorFactory.Create();

    private static byte[] Anotado() => Ed.AddAnnotation(Fixtures.A4(), new AnnotationData
    {
        Kind = AnnotationKind.Rectangle, PageIndex = 0, LeftPt = 100, BottomPt = 100, RightPt = 200, TopPt = 200, ColorArgb = 0xFFFF0000,
    });

    [Fact]
    public void SemAnotacaoNemAssinatura_SemAviso() => Assert.Null(ImposicaoAviso.Para(Ed, Fixtures.A4()));

    [Fact]
    public void ComAnotacao_Avisa() => Assert.Equal(ImposicaoAviso.Texto, ImposicaoAviso.Para(Ed, Anotado()));

    [Fact] // fixture-carimbo: assinatura PAdES com carimbo visível (widget), nenhuma anotação de usuário
    public void ComAssinatura_Avisa() =>
        Assert.Equal(ImposicaoAviso.Texto, ImposicaoAviso.Para(Ed, File.ReadAllBytes(Path.Combine(Fixtures.Root, "fixture-carimbo.pdf"))));

    [Fact]
    public void PdfIlegivel_SemAvisoNemExcecao() => Assert.Null(ImposicaoAviso.Para(Ed, new byte[] { 1, 2, 3 }));

    [Fact] // Revisão F2 (A9): o texto cita os campos de formulário (eles também não vão)
    public void Texto_ptBR() =>
        Assert.Equal("Atenção: anotações, campos de formulário e assinaturas não vão para o PDF gerado em \"Salvar PDF\" (a impressão leva).", ImposicaoAviso.Texto);

    // ---- Revisão F2 (A9): widgets e subtypes que ReadAnnotations não mapeia também se perdem na imposição ----

    [Fact] // formulário PREENCHIDO (fixture-formulario + SetFormFields): os valores somem do PDF imposto
    public void FormularioPreenchido_Avisa()
    {
        var preenchido = Ed.SetFormFields(Fixtures.Formulario(), new Dictionary<string, string> { ["nome"] = "Fulano de Tal" });
        Assert.Empty(Ed.ReadAnnotations(preenchido)); // por isso o detector antigo não avisava
        Assert.Equal(ImposicaoAviso.Texto, ImposicaoAviso.Para(Ed, preenchido));
    }

    /// A4 com UMA anotação do subtype dado, gravada crua (o mPDF não cria esses).
    private static byte[] ComSubtype(iText.Kernel.Pdf.PdfName subtype)
    {
        using var o = new MemoryStream();
        using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(new MemoryStream(Fixtures.A4())), new iText.Kernel.Pdf.PdfWriter(o)))
        {
            var d = new iText.Kernel.Pdf.PdfDictionary();
            d.Put(iText.Kernel.Pdf.PdfName.Type, iText.Kernel.Pdf.PdfName.Annot);
            d.Put(iText.Kernel.Pdf.PdfName.Subtype, subtype);
            d.Put(iText.Kernel.Pdf.PdfName.Rect, new iText.Kernel.Pdf.PdfArray(new float[] { 100, 100, 200, 200 }));
            d.Put(iText.Kernel.Pdf.PdfName.C, new iText.Kernel.Pdf.PdfArray(new float[] { 1, 0, 0 }));
            d.MakeIndirect(doc);
            var annots = new iText.Kernel.Pdf.PdfArray();
            annots.Add(d);
            doc.GetPage(1).GetPdfObject().Put(iText.Kernel.Pdf.PdfName.Annots, annots);
        }
        return o.ToArray();
    }

    [Theory]
    [InlineData("Circle")]
    [InlineData("Squiggly")]
    [InlineData("Polygon")]
    [InlineData("Caret")]
    [InlineData("FileAttachment")]
    public void SubtypeNaoMapeado_Avisa(string subtype)
    {
        var pdf = ComSubtype(new iText.Kernel.Pdf.PdfName(subtype));
        Assert.Empty(Ed.ReadAnnotations(pdf)); // o detector antigo não via
        Assert.Equal(ImposicaoAviso.Texto, ImposicaoAviso.Para(Ed, pdf));
    }

    [Theory] // DECISÃO: link (só navegação) e popup (balão de uma anotação-mãe) não disparam o aviso
    [InlineData("Link")]
    [InlineData("Popup")]
    public void LinkOuPopupSozinhos_SemAviso(string subtype) =>
        Assert.Null(ImposicaoAviso.Para(Ed, ComSubtype(new iText.Kernel.Pdf.PdfName(subtype))));

    [Fact] // PDF comum de várias páginas, sem nada: sem aviso
    public void PdfSemNada_SemAviso() => Assert.Null(ImposicaoAviso.Para(Ed, Fixtures.ThirtyPages()));

    [Fact] // async (o diálogo não espera o parse na UI thread)
    public async Task ParaAsync_MesmoResultado()
    {
        Assert.Equal(ImposicaoAviso.Texto, await ImposicaoAviso.ParaAsync(Ed, Anotado()));
        Assert.Null(await ImposicaoAviso.ParaAsync(Ed, Fixtures.A4()));
    }

    [Fact] // o diálogo mostra o aviso (documento anotado); smoke STA como DialogoImpressaoTests
    public void Dialogo_DocumentoAnotado_MostraOAviso()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mpdf-impos-aviso-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Anotado());
        Exception? erro = null;
        var t = new Thread(() =>
        {
            try
            {
                using var s = DocumentSession.Open(path);
                var dlg = new DialogoImpressao(s, "teste");
                // Revisão F2 (A9): o aviso é calculado FORA da UI thread ao abrir o diálogo — bombeia o Dispatcher até chegar.
                var frame = new System.Windows.Threading.DispatcherFrame();
                dlg.AvisoImposicaoCarregado.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Assert.Equal(Visibility.Visible, dlg.PainelAvisoImposicao.Visibility);
                Assert.Equal(ImposicaoAviso.Texto, dlg.TxtAvisoImposicao.Text);
            }
            catch (Exception ex) { erro = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(40)), "thread STA não terminou em 40s");
        try { File.Delete(path); } catch (IOException) { }
        if (erro is not null) ExceptionDispatchInfo.Capture(erro).Throw();
    }
}
