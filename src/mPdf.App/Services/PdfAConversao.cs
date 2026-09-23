using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace mPdf.App.Services;

/// Helpers da conversao "Salvar como PDF/A": rasterizacao BGRA->JPEG RGB (sem alfa) e leitura dos
/// bytes da fonte Inter embarcada (para embutir no PDF/A). iText fica em mPdf.Editing — aqui so
/// preparamos imagens/fonte que atravessam o contrato NEUTRO.
/// Escolha do usuário no diálogo "Salvar como PDF/A" (Task 6, SDD "Salvar como PDF/A") — nível de
/// conformidade + se deve manter/gerar a camada pesquisável (OCR) nas páginas sem texto. Definido aqui
/// (não em Task 7, que só troca o default de `UiPrompts.CreatePdfADialog` e implementa a view) pra que
/// a orquestração do comando (`DocumentViewModel.SalvarComoPdfA`) não tenha referência pra frente.
public readonly record struct PdfAEscolha(mPdf.Editing.PdfANivel Nivel, bool ManterPesquisavel);

/// Seam do diálogo "Salvar como PDF/A" — produção (Task 7) abre uma janela real pedindo nível +
/// "manter pesquisável"; `null` = usuário cancelou (mesmo contrato dos demais diálogos desta seam,
/// ver `UiPrompts`).
public interface IPdfADialogService
{
    PdfAEscolha? Perguntar();
}

public static class PdfAConversao
{
    public static byte[] BgraParaJpegRgb(byte[] bgra, int larguraPx, int alturaPx, int qualidade = 85)
    {
        var bmp = BitmapSource.Create(larguraPx, alturaPx, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, bgra, larguraPx * 4);
        var enc = new JpegBitmapEncoder { QualityLevel = qualidade };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms); // JPEG e sempre RGB, sem canal alfa
        return ms.ToArray();
    }

    public static byte[] FonteInvisivelTtf()
    {
        if (Application.Current is null) _ = new Application(); // host de teste sem App
        // Forma "component" com o nome curto do assembly: independe de Application.ResourceAssembly,
        // que no host de teste aponta para o testhost, nao para mPdf.App.
        var uri = new Uri("pack://application:,,,/mPdf.App;component/Assets/Fonts/Inter-400.ttf", UriKind.Absolute);
        using var s = Application.GetResourceStream(uri)!.Stream;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
