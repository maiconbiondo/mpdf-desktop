using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Signing;

namespace mPdf.App.Tests;

/// Plano 14 (Task 4) — sonda de FIDELIDADE dos 6 diálogos escuros (Assinar, Assinar em lote, Juntar,
/// Dividir, Exportar, Sobre). Constrói cada janela REAL, mescla os tokens ESCUROS na própria janela (sem
/// Application, o DynamicResource dos estilos resolve subindo até Window.Resources) e RENDERIZA pra PNG,
/// pra comparação A OLHO com os screenshots-alvo 05–10 (docs/redesign-ref/screenshots). Também guarda que
/// (a) construir cada diálogo não lança (mesma classe da DialogConstructionSmokeTests) e (b) o render tem
/// o canto ESCURO da superfície de diálogo (#1C1E2C), provando que a linguagem escura foi aplicada.
///
/// Mesma disciplina STA manual das outras sondas de janela desta suíte (SignDialogTests/ExportImageDialog
/// Tests): construir Window WPF precisa rodar fora da thread do xUnit.
public class Task4DialogosTests
{
    private static readonly string OutDir =
        Path.Combine(Path.GetTempPath(), "mpdf-t4-dialogos");

    private static ResourceDictionary Tokens() => new()
    {
        Source = new Uri("pack://application:,,,/mPdf.App;component/Themes/Tokens.Escuro.xaml"),
    };

    // Certificados EFÊMEROS (em memória — nunca tocam o repositório real). Um RSA e-CPF (habilitado), um
    // RSA e-CNPJ (habilitado) e um ECC (listado mas desabilitado) — pra exercitar os 3 estados do cartão.
    private static SigningCertificateInfo RsaCert(string cn, bool personal, bool company)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var cert = X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        return new SigningCertificateInfo(cert, IsRsa: true, DisplayName: cn, personal, company);
    }

    private static SigningCertificateInfo EccCert(string cn)
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", ec, HashAlgorithmName.SHA256);
        var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var cert = X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        return new SigningCertificateInfo(cert, IsRsa: false, DisplayName: cn, IsIcpBrasilPersonal: true, IsIcpBrasilCompany: false);
    }

    private static SigningCertificateInfo[] Certs() =>
    [
        RsaCert("Maria Silva Santos", personal: true, company: false),
        RsaCert("Oliveira Servicos LTDA", personal: false, company: true),
        EccCert("João Pereira (ECC)"),
    ];

    [Fact]
    public void Assinar_Render() => RenderDialog("05-assinar", () =>
    {
        var d = new SignDialog(Certs(), allowDocMdp: true,
            new mPdf.App.Services.RubricaGallery(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpdf-rub-{System.Guid.NewGuid():N}")),
            () => null);
        var lb = (System.Windows.Controls.ListBox)d.FindName("CertificateListBox")!;
        lb.SelectedIndex = 0;
        return d;
    });

    [Fact]
    public void AssinarLote_Render() => RenderDialog("09-assinar-lote", () =>
    {
        var vm = new BatchSignViewModel(Certs(), isPathOpen: _ => false, pickFiles: () => null);
        vm.SelectedCertificate = vm.Certificates[0];
        return new BatchSignDialog(vm);
    });

    [Fact]
    public void Juntar_Render() => RenderDialog("06-juntar", () =>
    {
        var d = new MergeFilesDialog();
        d.Files.Add(@"C:\Documentos\Contrato de prestação.pdf");
        d.Files.Add(@"C:\Documentos\Anexo I.pdf");
        d.Files.Add(@"C:\Documentos\Assinatura digitalizada.png");
        return d;
    });

    [Fact]
    public void Dividir_Render() => RenderDialog("07-dividir", () =>
    {
        var d = new SplitDialog();
        ((System.Windows.Controls.TextBox)d.FindName("RangesTextBox")!).Text = "1-3, 4-5";
        ((System.Windows.Controls.TextBox)d.FindName("FolderTextBox")!).Text = @"C:\Users\maria\Documentos\Dividido";
        return d;
    });

    [Fact]
    public void Exportar_Render() => RenderDialog("08-exportar", () =>
        new ExportImageDialog(new ExportImageViewModel(Fixtures.A4(), pageCount: 1, currentPageIndex: 0, baseFileName: "documento")));

    [Fact]
    public void Sobre_Render() => RenderDialog("10-sobre", () =>
        new SobreDialog(new SobreViewModel()));

    // Task 2 (Plano 17): sonda de fidelidade do novo diálogo "Configurações" (Tema/Nitidez/Atualização,
    // migrados do Sobre) — mesma disciplina das demais.
    [Fact]
    public void Configuracoes_Render() => RenderDialog("11-configuracoes", () =>
        new ConfiguracoesDialog(new ConfiguracoesViewModel(
            confirmCloseAllDocuments: () => true,
            startInstaller: _ => { },
            shutdown: () => { })));

    private static void RenderDialog(string nome, Func<Window> build)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(OutDir);
                var w = build();
                w.Resources.MergedDictionaries.Add(Tokens());
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -10000; w.Top = -10000;
                try
                {
                    w.Show();
                    w.UpdateLayout();

                    var root = (FrameworkElement)w.Content;
                    root.UpdateLayout();
                    int width = (int)Math.Ceiling(root.ActualWidth);
                    int height = (int)Math.Ceiling(root.ActualHeight);
                    Assert.True(width > 100 && height > 100, $"{nome}: render vazio ({width}x{height})");

                    var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(root);

                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    var path = Path.Combine(OutDir, nome + ".png");
                    using (var fs = File.Create(path)) enc.Save(fs);

                    // A superfície de diálogo escura (#1C1E2C) deve dominar o miolo do render — amostra um
                    // bloco 24x24 no centro e confirma que a MÉDIA é ESCURA (canais < 100), provando que a
                    // linguagem escura foi aplicada (não um diálogo claro/sistema). Média (não 1 pixel)
                    // tolera um glifo/texto claro que caia exatamente no centro.
                    const int blk = 24;
                    var px = new byte[blk * blk * 4];
                    var cropped = new CroppedBitmap(rtb, new Int32Rect(width / 2 - blk / 2, height / 2 - blk / 2, blk, blk));
                    cropped.CopyPixels(px, blk * 4, 0);
                    long b = 0, g = 0, r = 0;
                    for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
                    int n = blk * blk;
                    Assert.True(b / n < 100 && g / n < 100 && r / n < 100,
                        $"{nome}: centro não é escuro (B={b / n} G={g / n} R={r / n}) — tokens escuros não aplicados?");
                }
                finally { w.Close(); }
            }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        bool joined = thread.Join(TimeSpan.FromSeconds(20));
        Assert.True(joined, $"{nome}: thread STA não terminou em 20s (possível hang do WPF)");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }
}
