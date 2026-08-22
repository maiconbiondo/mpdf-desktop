using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using mPdf.App.Views;
using mPdf.Signing;

namespace mPdf.App.Tests;

/// Plano 9 (Task 3, brief): "Posicionar carimbo na página" passa a vir PRÉ-SELECIONADO por padrão --
/// pedido do usuário (assinar com carimbo visível é o caminho mais comum na prática); "Sem carimbo
/// visível" continua 100% disponível, só deixa de ser o default (RadioButton, GroupName="Stamp",
/// SignDialog.xaml). NENHUM teste anterior fixava o default ANTIGO (grep confirmado: `PlaceStampRadio`/
/// `NoStampRadio` não apareciam em teste nenhum antes desta task -- os testes existentes só montam
/// `SignDialogResult` diretamente, via fakes de `ISignDialogService`, nunca a `Window` real) -- este é
/// o 1º teste a construir `Views.SignDialog` de verdade, mesmo padrão STA manual de
/// ViewerIntegrationTests/PrintServiceTests (construção de tipos WPF pode exigir STA nesta suíte).
/// `FindName` (não o campo `x:Name` gerado, `private` por padrão): API pública do WPF, resolve pelo
/// NameScope registrado por `InitializeComponent()` independente da visibilidade CLR do campo.
public class SignDialogTests
{
    [Fact]
    public void SignDialog_Constructed_PlaceStampRadioIsCheckedByDefault_NoStampIsNot()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(15));
        Assert.True(joined, "thread STA não terminou dentro de 15s (BLOCKED: possível deadlock/hang do WPF)");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunScenario()
    {
        var dialog = new SignDialog(Array.Empty<SigningCertificateInfo>(), allowDocMdp: true);
        try
        {
            var placeStamp = (RadioButton)dialog.FindName("PlaceStampRadio")!;
            var noStamp = (RadioButton)dialog.FindName("NoStampRadio")!;

            Assert.True(placeStamp.IsChecked == true, "\"Posicionar carimbo na página\" deveria vir marcado por padrão");
            Assert.False(noStamp.IsChecked == true, "\"Sem carimbo visível\" não deveria mais ser o default");
        }
        finally { dialog.Close(); }
    }
}
