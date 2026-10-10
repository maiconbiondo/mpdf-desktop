using iText.IO.Image;
using iText.Kernel.Colors;

namespace mPdf.Signing;

/// PDF/A-1 (ISO 19005-1 §6.4) proíbe QUALQUER transparência: ExtGState com CA/ca != 1 e imagens com
/// /SMask. O carimbo visível usa as duas (selo translúcido 0.10/0.12; rubrica PNG com alfa). A2/A3
/// permitem — só a parte 1 passa por aqui (ver `PadesSigningEngine.InspectDocument`, `IsPdfA1`).
/// O PDF/A do mPDF é SEMPRE página escaneada (papel cinza/amarelado), então nada aqui pode pintar
/// fundo: o selo vira só traço (ver `DrawWatermarkSeal`) e a rubrica usa máscara STENCIL de 1 bit.
internal static class PdfA1Opaque
{
    /// Papel de REFERÊNCIA de um scan típico (cinza claro amarelado). Os tons opacos do selo são o
    /// alfa composto sobre ESTE papel, não sobre branco: composto sobre branco o traço ficava MAIS CLARO
    /// que o papel do scan e "brilhava" (traço esbranquiçado sobre cinza, visto no render). Sobre este
    /// papel o resultado coincide com o modo translúcido do A2B; sobre papel branco vira uma marca-d'água
    /// cinza-azulada discreta — nunca mais clara que o papel típico.
    public static readonly (int R, int G, int B) ScanPaper = (222, 220, 212);

    /// Cor de `fg` com alfa `alpha` composta (Porter-Duff "over") sobre `ScanPaper`.
    public static DeviceRgb OverScanPaper(int r, int g, int b, float alpha) =>
        new(Mix(r, alpha, ScanPaper.R), Mix(g, alpha, ScanPaper.G), Mix(b, alpha, ScanPaper.B));

    private static int Mix(int fg, float alpha, int bg) => (int)Math.Round(fg * alpha + bg * (1 - alpha));

    /// Rubrica em PDF/A-1. Sem alfa (JPEG, PNG opaco) -> imagem como está. Com alfa (o iText decodifica
    /// o canal em um /SMask de 8/16 bits, proibido) -> o alfa vira máscara STENCIL de 1 bit (limiar 50%:
    /// alfa &gt;= 128 pinta, &lt; 128 é recortado), gravada como /Mask com /ImageMask true — permitida em
    /// PDF/A-1. O papel continua visível onde a rubrica é transparente; o preço é borda serrilhada (sem
    /// meio-tom de alfa). Nunca degrada em silêncio: formato de alfa inesperado -> PdfSigningException.
    public static ImageData StencilFromAlpha(byte[] imageBytes)
    {
        var image = ImageDataFactory.Create(imageBytes);
        var soft = image.GetImageMask();
        if (soft is null || !soft.IsSoftMask()) return image;

        int w = (int)soft.GetWidth(), h = (int)soft.GetHeight(), bpc = soft.GetBpc();
        byte[] alpha = soft.GetData();
        int bytesPerSample = bpc / 8;
        if ((bpc != 8 && bpc != 16) || alpha is null || alpha.Length != w * h * bytesPerSample)
            throw new PdfSigningException(
                "A transparência desta rubrica não pôde ser convertida para PDF/A-1 — use um PNG de 8 " +
                "bits ou um JPG.");

        int stride = (w + 7) / 8;
        var bits = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // 16 bits: o byte ALTO (big-endian, como no PNG) decide.
                byte a = alpha[(y * w + x) * bytesPerSample];
                if (a < 128) bits[y * stride + x / 8] |= (byte)(0x80 >> (x % 8)); // 1 = recortado
            }

        var stencil = ImageDataFactory.Create(w, h, 1, 1, bits, null);
        stencil.MakeMask();
        image.SetImageMask(stencil);
        return image;
    }
}
