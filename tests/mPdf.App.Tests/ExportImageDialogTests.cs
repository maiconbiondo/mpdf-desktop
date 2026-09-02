using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Signing;

namespace mPdf.App.Tests;

/// Task 3 (Plano 12, fixes finais) — pré-existente desde Plano 7/v1.3, reproduzido deterministicamente
/// pela revisão em 60476eb E 8b358b4: `ExportImageDialog` SEMPRE lançava `NullReferenceException` dentro
/// de `InitializeComponent`, porque `CurrentPageRadio`/`AllPagesRadio` têm `IsChecked="True"`/`Checked=
/// "...Checked"` no XAML (grupo "Alcance") e o handler `CurrentPageRadio_Checked` toca `DestinationText
/// Box` — um `x:Name` declarado DEPOIS no mesmo arquivo (seção "Destino:"), ainda não conectado pelo BAML
/// no momento em que o RadioButton dispara `Checked` ao ser marcado durante o próprio parse. Nenhum teste
/// anterior construía o `Views.ExportImageDialog` REAL (o seam de serviço, `ExportImageDialogService`,
/// sempre foi trocado por um fake nos testes existentes — grep confirmado, `new ExportImageDialog(`
/// não aparecia em teste nenhum antes desta task) — por isso ficou invisível por 3 releases: o botão
/// "📤 Exportar" sempre caía no diálogo global de crash em produção.
///
/// Mesmo padrão STA manual de `SignDialogTests`/`SobreDialogTests` (construção de `Window` real precisa
/// rodar fora da thread de teste do xUnit nesta suíte).
public class ExportImageDialogTests
{
    private static ExportImageViewModel BuildVm() =>
        new(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "documento");

    /// Prova de RED (achado da revisão) -> prova de GREEN (pós-fix): construir o diálogo real não deve
    /// lançar. `Assert.Null` no lugar de um `try/catch` mudo -- se `RunScenario` lançar, `RunSta` relança
    /// a mesma exceção (via `ExceptionDispatchInfo`) e o teste falha com o stack trace original intacto.
    [Fact]
    public void ExportImageDialog_Constructed_DoesNotThrow()
    {
        RunSta(() =>
        {
            var dialog = new ExportImageDialog(BuildVm());
            try { dialog.Show(); }
            finally { dialog.Close(); }
        });
    }

    /// Intenção ORIGINAL do handler (preservada pelo fix -- guarda de nulidade, não remoção de
    /// comportamento): trocar o Alcance invalida um destino já escolhido, tanto no VM quanto no
    /// `TextBox` visível. Simula a escolha de um destino (sem abrir `SaveFileDialog` nenhum -- não há
    /// seam pra isso neste code-behind, então o teste seta o VM + o TextBox direto, o mesmo par que
    /// `PickDestination_Click` setaria) e confirma que marcar "Todas as páginas" limpa os dois.
    [Fact]
    public void ExportImageDialog_ToggleRangeRadio_ClearsChosenDestination()
    {
        RunSta(() =>
        {
            var vm = BuildVm();
            var dialog = new ExportImageDialog(vm);
            try
            {
                dialog.Show();

                var destinationBox = (TextBox)dialog.FindName("DestinationTextBox")!;
                var allPagesRadio = (RadioButton)dialog.FindName("AllPagesRadio")!;
                var currentPageRadio = (RadioButton)dialog.FindName("CurrentPageRadio")!;

                // Simula um destino já escolhido (mesmo par que PickDestination_Click atribuiria).
                vm.Destination = @"C:\tmp\pagina.png";
                destinationBox.Text = @"C:\tmp\pagina.png";

                allPagesRadio.IsChecked = true;

                Assert.Null(vm.Destination);
                Assert.Equal("", destinationBox.Text);
                Assert.Equal(ExportImageRange.AllPages, vm.Range);

                // E o caminho inverso: voltar pra "Página atual" também limpa (mesmo par de handlers).
                vm.Destination = @"C:\tmp\pasta";
                destinationBox.Text = @"C:\tmp\pasta";

                currentPageRadio.IsChecked = true;

                Assert.Null(vm.Destination);
                Assert.Equal("", destinationBox.Text);
                Assert.Equal(ExportImageRange.CurrentPage, vm.Range);
            }
            finally { dialog.Close(); }
        });
    }

    private static void RunSta(Action scenario)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { scenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(15));
        Assert.True(joined, "thread STA não terminou dentro de 15s (BLOCKED: possível deadlock/hang do WPF)");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }
}
