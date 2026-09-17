using System.IO;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

public class PdfAConversaoTests
{
    [Fact] // BGRA opaco vira JPEG valido (magic FF D8) nao-vazio
    public void BgraParaJpeg_ProduzJpegValido()
    {
        int w = 4, h = 4;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 200; bgra[i+1] = 210; bgra[i+2] = 220; bgra[i+3] = 255; }
        var jpg = PdfAConversao.BgraParaJpegRgb(bgra, w, h);
        Assert.True(jpg.Length > 2 && jpg[0] == 0xFF && jpg[1] == 0xD8, "nao e JPEG");
    }

    [Fact] // a fonte Inter empacotada resolve e comeca com a assinatura de TTF/OTF
    public void FonteInvisivel_ResolveTtf()
    {
        var ttf = PdfAConversao.FonteInvisivelTtf();
        Assert.True(ttf.Length > 1000, "fonte pequena demais");
        // TTF: 00 01 00 00 ; OTF: 'OTTO' — Inter estatico e TTF (00 01 00 00).
        Assert.True(ttf[0] == 0x00 && ttf[1] == 0x01 && ttf[2] == 0x00 && ttf[3] == 0x00
                    || (ttf[0]=='O'&&ttf[1]=='T'&&ttf[2]=='T'&&ttf[3]=='O'), "assinatura de fonte inesperada");
    }
}
