using System.IO;

namespace mPdf.PreviewHandler.Tests;

/// Trava o IID de `IInitializeWithStream` no módulo AOT por varredura de código-fonte. O bug da 1a
/// abordagem foi declarar essa interface com o IID de IInitializeWithFile (b7d14566-...) — o Explorer
/// chamava Initialize passando uma STRING (caminho) onde o método esperava um IStream* → E_HANDLE/crash.
/// O IID correto (verificado contra propsys.h de 2 versões do SDK) é b824b49d-....
public class PdfPreviewHandlerComContractTests
{
    private const string IID_IInitializeWithStream = "b824b49d-22ac-4161-ac8a-9916e8fa3f7f";
    private const string IID_IInitializeWithFile = "b7d14566-0509-4cce-a71f-0a554233bd9b";

    private static string ComInteropPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "mPdf.PreviewHandler.Aot", "ComInterop.cs");
    }

    [Fact]
    public void IInitializeWithStream_UsaOIidCorreto_NaoODeFile()
    {
        var src = File.ReadAllText(ComInteropPath());
        // A declaração de IInitializeWithStream tem que estar com o IID de Stream, não o de File.
        var idxStream = src.IndexOf("interface IInitializeWithStream", StringComparison.Ordinal);
        Assert.True(idxStream > 0, "IInitializeWithStream não encontrada em ComInterop.cs");
        // O Guid correto aparece imediatamente antes da declaração (atributo [GeneratedComInterface, Guid(...)]).
        var trecho = src[..idxStream];
        var idxGuid = trecho.LastIndexOf("Guid(\"", StringComparison.Ordinal);
        Assert.True(idxGuid > 0, "atributo Guid não encontrado antes de IInitializeWithStream");
        var guid = src.Substring(idxGuid + 6, 36);
        Assert.Equal(IID_IInitializeWithStream, guid, ignoreCase: true);
        Assert.NotEqual(IID_IInitializeWithFile, guid.ToLowerInvariant());
    }
}
