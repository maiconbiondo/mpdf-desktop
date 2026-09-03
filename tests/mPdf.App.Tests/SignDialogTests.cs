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

    // 1x1 PNG vermelho (bytes válidos p/ a galeria/miniatura).
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    private static mPdf.App.Services.RubricaGallery NewGallery(int count)
    {
        var g = new mPdf.App.Services.RubricaGallery(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpdf-rub-{Guid.NewGuid():N}"));
        for (int i = 0; i < count; i++) g.Add(Png);
        return g;
    }

    private static void RunScenario()
    {
        var dialog = new SignDialog(Array.Empty<SigningCertificateInfo>(), allowDocMdp: true, NewGallery(0), () => null);
        try
        {
            var placeStamp = (RadioButton)dialog.FindName("PlaceStampRadio")!;
            var noStamp = (RadioButton)dialog.FindName("NoStampRadio")!;

            Assert.True(placeStamp.IsChecked == true, "\"Posicionar carimbo na página\" deveria vir marcado por padrão");
            Assert.False(noStamp.IsChecked == true, "\"Sem carimbo visível\" não deveria mais ser o default");
        }
        finally { dialog.Close(); }
    }

    // Plano 22: "Minha rubrica" abre a galeria (escondida até selecionar); com rubricas salvas mostra as
    // miniaturas + o "+".
    [Fact]
    public void SignDialog_Rubrica_ShowsGalleryWithTilesAndAddButton()
    {
        RunOnSta(() =>
        {
            var dialog = new SignDialog(Array.Empty<SigningCertificateInfo>(), allowDocMdp: true, NewGallery(2), () => null);
            try
            {
                var radio = (RadioButton)dialog.FindName("RubricaStampRadio")!;
                var panel = (System.Windows.Controls.StackPanel)dialog.FindName("RubricaGaleriaPanel")!;
                var tiles = (System.Windows.Controls.WrapPanel)dialog.FindName("RubricaTilesPanel")!;

                Assert.True(radio.IsEnabled); // sempre habilitado (a galeria vive dentro do diálogo)
                Assert.Equal(System.Windows.Visibility.Collapsed, panel.Visibility); // escondida até escolher

                radio.IsChecked = true; // dispara Checked -> mostra + renderiza

                Assert.Equal(System.Windows.Visibility.Visible, panel.Visibility);
                Assert.Equal(3, tiles.Children.Count); // 2 miniaturas + o "+"
            }
            finally { dialog.Close(); }
        });
    }

    private static void RunOnSta(Action scenario)
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
