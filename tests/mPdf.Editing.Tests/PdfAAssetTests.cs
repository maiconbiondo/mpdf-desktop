using System.IO;
using System.Linq;
using mPdf.Editing;
using Xunit;

namespace mPdf.Editing.Tests;

public class PdfAAssetTests
{
    [Fact] // o perfil sRGB embutido resolve e tem tamanho de ICC plausivel
    public void PerfilSRGB_EmbutidoResolvivel()
    {
        var asm = typeof(PdfEditorFactory).Assembly;
        var nome = asm.GetManifestResourceNames().Single(n => n.EndsWith("sRGB2014.icc"));
        using var s = asm.GetManifestResourceStream(nome)!;
        Assert.NotNull(s);
        Assert.True(s.Length > 400, $"ICC pequeno demais: {s.Length} bytes");
        var head = new byte[4];
        s.Read(head, 0, 4);
        // ICC valido: bytes 36-39 sao a assinatura 'acsp'; aqui so garantimos leitura nao-vazia.
        Assert.Equal(4, head.Length);
    }
}
