using System.Linq;
using mPdf.App.Services;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// Pedido do usuário: "a caixa de texto está quebrando linha, ela deveria me permitir digitar a linha do tamanho da
/// página". Caminho REAL do App (diálogo fake → PlaceAnnotationAtAsync / EditSelectedAnnotation / Move): criar e
/// editar alargam a caixa até caber a linha; mover mantém a largura (não re-alarga nem estreita).
public class CaixaTextoLarguraVmTests
{
    private const string Linha = "Esta linha inteira precisa caber sem quebrar nada"; // ~270 pt em 12 pt > 200 pt padrão

    private static AnnotationTextResult Texto(string t) => new(t, 12, false, false, "Helvetica", 0xFF000000);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task Criar_LinhaLonga_AlargaACaixa_MoverMantemLargura_EditarAlarga(int rot)
    {
        var ed = PdfEditorFactory.Create();
        byte[] pdf = ed.ExtractPages(Fixtures.ThirtyPages(), new[] { 0 });
        if (rot != 0) pdf = ed.RotatePages(pdf, new[] { 0 }, rot);
        using var vm = VmTemporaria.Abrir(pdf, "ft-largura", annotationDialog: new FakeAnnotationTextDialogService { FormatResult = Texto(Linha) });
        var d = vm.D;
        double larguraPagina = d.Pages[0].WidthPt;

        d.ToggleFreeTextToolCommand.Execute(null);
        await d.PlaceAnnotationAtAsync(0, 60, d.Pages[0].HeightPt - 150);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var criada = Assert.Single(d.AnnotationsByPage[0]);
        double largura = criada.RightPt - criada.LeftPt;
        Assert.True(largura > 200.5, $"rot {rot}: a caixa padrão (200 pt) devia alargar; veio {largura:F1}");
        Assert.True(criada.RightPt <= larguraPagina - 18 + 0.5, $"rot {rot}: passou da margem");
        Assert.Equal(60, criada.TopPt - criada.BottomPt, 0.5); // uma linha: altura padrão intacta

        // mover: mesma largura (nem re-alarga, nem estreita)
        d.SelectedAnnotation = criada;
        await d.MoveSelectedAnnotationAsync(criada.LeftPt + 20, criada.BottomPt - 30);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var movida = Assert.Single(d.AnnotationsByPage[0]);
        Assert.Equal(largura, movida.RightPt - movida.LeftPt, 0.5);

        // editar com uma linha ainda maior: alarga de novo (até a margem)
        vm.Dialog.FormatResult = Texto(Linha + " e mais um bom pedaço de texto aqui no fim");
        d.SelectedAnnotation = movida;
        await d.EditSelectedAnnotationCommand.ExecuteAsync(null);
        Assert.Empty(vm.Errors);
        await d.RefreshAnnotationsByPageAsync();
        var editada = Assert.Single(d.AnnotationsByPage[0]);
        Assert.True(editada.RightPt - editada.LeftPt > largura + 50, $"rot {rot}: edição não alargou");
        Assert.Equal(movida.LeftPt, editada.LeftPt, 0.5);
    }

    /// Revisão N1 (decisão do produto, substitui a regra do I3): na EDIÇÃO só alarga se a caixa NÃO estava quebrando
    /// linha antes (a linha mais longa do texto ANTIGO, na formatação antiga, cabia na largura atual). Coluna que já
    /// quebrava (estreitada à mão) mantém a largura em qualquer edição — letra que faltava, negrito, fonte.
    private static async Task<(VmTemporaria Vm, AnnotationData Caixa)> CaixaCom(string texto, double? larguraManual)
    {
        var ed = PdfEditorFactory.Create();
        var vm = VmTemporaria.Abrir(ed.ExtractPages(Fixtures.ThirtyPages(), new[] { 0 }), "ft-coluna",
            annotationDialog: new FakeAnnotationTextDialogService { FormatResult = Texto(texto) });
        var d = vm.D;
        d.ToggleFreeTextToolCommand.Execute(null);
        await d.PlaceAnnotationAtAsync(0, 60, d.Pages[0].HeightPt - 150);
        await d.RefreshAnnotationsByPageAsync();
        var criada = Assert.Single(d.AnnotationsByPage[0]);
        if (larguraManual is { } w)
        {
            // "estreitar à mão" (o motor sem o pedido mantém a largura — é o que um redimensionar faz)
            Assert.True(d.Session.TryBeginEdit());
            try
            {
                var snap = d.Session.Snapshot;
                d.Session.ApplyEdit(ed.AddAnnotation(ed.RemoveAnnotation(snap, criada.Id!), criada with { RightPt = criada.LeftPt + w }));
            }
            finally { d.Session.EndEdit(); }
            await d.RefreshAnnotationsByPageAsync();
        }
        var caixa = Assert.Single(d.AnnotationsByPage[0]);
        if (larguraManual is { } w2) Assert.Equal(w2, caixa.RightPt - caixa.LeftPt, 0.5);
        return (vm, caixa);
    }

    private static async Task<AnnotationData> Editar(VmTemporaria vm, AnnotationData caixa, AnnotationTextResult novo)
    {
        vm.Dialog.FormatResult = novo;
        vm.D.SelectedAnnotation = caixa;
        await vm.D.EditSelectedAnnotationCommand.ExecuteAsync(null);
        Assert.Empty(vm.Errors);
        await vm.D.RefreshAnnotationsByPageAsync();
        return Assert.Single(vm.D.AnnotationsByPage[0]);
    }

    [Fact] // coluna estreita que quebra + letra que faltava ("lnha" -> "linha"): mantém
    public async Task ColunaQueQuebra_LetraQueFaltava_MantemLargura()
    {
        var (vm, coluna) = await CaixaCom("Esta lnha inteira precisa caber sem quebrar nada", 90);
        using var _ = vm;
        var editada = await Editar(vm, coluna, Texto(Linha));
        Assert.Equal(Linha, editada.Content);
        Assert.Equal(90, editada.RightPt - editada.LeftPt, 0.5);
    }

    [Fact] // coluna estreita que quebra + negrito: mantém
    public async Task ColunaQueQuebra_Negrito_MantemLargura()
    {
        var (vm, coluna) = await CaixaCom(Linha, 90);
        using var _ = vm;
        var editada = await Editar(vm, coluna, Texto(Linha) with { Bold = true });
        Assert.True(editada.Bold);
        Assert.Equal(90, editada.RightPt - editada.LeftPt, 0.5);
    }

    [Fact] // caixa de uma linha (não quebrava) + palavras acrescentadas: alarga
    public async Task CaixaDeUmaLinha_AcrescentarPalavras_Alarga()
    {
        var (vm, caixa) = await CaixaCom(Linha, null);
        using var _ = vm;
        double antes = caixa.RightPt - caixa.LeftPt;
        var editada = await Editar(vm, caixa, Texto(Linha + " com mais palavras"));
        Assert.True(editada.RightPt - editada.LeftPt > antes + 50, $"devia alargar; {antes:F1} -> {editada.RightPt - editada.LeftPt:F1}");
    }

    [Fact] // caixa de uma linha + fonte 12 -> 14: alarga
    public async Task CaixaDeUmaLinha_FonteMaior_Alarga()
    {
        var (vm, caixa) = await CaixaCom(Linha, null);
        using var _ = vm;
        double antes = caixa.RightPt - caixa.LeftPt;
        var editada = await Editar(vm, caixa, Texto(Linha) with { FontSizePt = 14 });
        Assert.True(editada.RightPt - editada.LeftPt > antes + 20, $"devia alargar; {antes:F1} -> {editada.RightPt - editada.LeftPt:F1}");
    }
}
