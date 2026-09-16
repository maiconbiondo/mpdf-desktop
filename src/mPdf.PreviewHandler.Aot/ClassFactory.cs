using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace mPdf.PreviewHandler.Aot;

/// Fábrica COM + exports nativos da DLL. Substitui o `comhost` da 1a abordagem: como é Native AOT, a DLL
/// exporta ela mesma `DllGetClassObject`/`DllCanUnloadNow` (via `[UnmanagedCallersOnly]`). O Windows chama
/// DllGetClassObject(CLSID, IID) -> devolvemos uma IClassFactory; o COM chama CreateInstance -> criamos o
/// PdfPreviewHandler e devolvemos o CCW (COM-callable wrapper) via `StrategyBasedComWrappers`.
[GeneratedComClass]
internal partial class ClassFactory : IClassFactory
{
    public int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject)
    {
        ppvObject = 0;
        if (pUnkOuter != 0) return CLASS_E_NOAGGREGATION;
        try
        {
            var handler = new PdfPreviewHandler();
            nint unk = Exports.Cw.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
            var iid = riid;
            int hr = Marshal.QueryInterface(unk, ref iid, out ppvObject);
            Marshal.Release(unk);
            return hr;
        }
        catch (Exception ex) { Diag.Log(ex); return E_FAIL; }
    }

    public int LockServer(bool fLock) => S_OK;

    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int S_OK = 0;
}

/// Exports nativos da DLL (entrypoints do servidor COM in-proc).
internal static class Exports
{
    // Um único ComWrappers pro processo (cria CCWs dos nossos objetos gerenciados).
    internal static readonly StrategyBasedComWrappers Cw = new();

    private static readonly Guid ClsidGuid = new(PdfPreviewHandler.Clsid);

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static unsafe int DllGetClassObject(Guid* rclsid, Guid* riid, nint* ppv)
    {
        try
        {
            PdfiumLoader.Ensure(); // garante que o pdfium.dll resolva da nossa pasta antes de qualquer render
            if (ppv is null) return E_POINTER;
            *ppv = 0;
            if (rclsid is null || *rclsid != ClsidGuid) return CLASS_E_CLASSNOTAVAILABLE;

            var factory = new ClassFactory();
            nint unk = Cw.GetOrCreateComInterfaceForObject(factory, CreateComInterfaceFlags.None);
            var iid = *riid;
            int hr = Marshal.QueryInterface(unk, ref iid, out nint p);
            Marshal.Release(unk);
            *ppv = p;
            return hr;
        }
        catch (Exception ex) { Diag.Log(ex); return E_FAIL; }
    }

    // Native AOT não suporta descarregar o runtime -> nunca liberar a DLL (o surrogate encerra o processo
    // quando termina, o que limpa tudo). S_FALSE = "não descarregue".
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => S_FALSE;

    private const int S_FALSE = 1;
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
}
