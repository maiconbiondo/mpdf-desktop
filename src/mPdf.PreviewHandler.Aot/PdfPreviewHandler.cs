using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace mPdf.PreviewHandler.Aot;

/// Preview Handler de PDF do mPDF — versão NATIVE AOT (COM source-gen). Roda no surrogate prevhost.exe
/// (64-bit, baixa integridade) SEM CoreCLR, o que evita o hang da 1a abordagem. Contrato de robustez:
/// nenhuma exceção pode escapar de um método COM (derrubaria o prevhost) — todos são try/catch, com log
/// best-effort (Diag, em LocalLow). Lê o IStream direto pela vtable (sem marshalling gerenciado do stream).
[GeneratedComClass]
[Guid(Clsid)]
public partial class PdfPreviewHandler : IPreviewHandler, IInitializeWithStream, IObjectWithSite, IOleWindow
{
    /// CLSID ESTÁVEL do handler (o mesmo da 1a abordagem — identidade registrada no Explorer).
    public const string Clsid = "9A675AC7-E3B9-492B-A94C-9669CAD164BF";

    private byte[]? _pdfBytes;
    private nint _parentHwnd;
    private RECT _rect;
    private PreviewWindow? _window;
    private nint _site;

    public PdfPreviewHandler() => Diag.Trace("PdfPreviewHandler(AOT): instanciado");

    // ---- IInitializeWithStream ----
    public void Initialize(nint pstream, uint grfMode)
    {
        Diag.Trace($"Initialize: entrou (grfMode={grfMode})");
        try { _pdfBytes = LerStreamCompleto(pstream); Diag.Trace($"Initialize: leu {_pdfBytes.Length} bytes"); }
        catch (Exception ex) { Diag.Log(ex); _pdfBytes = null; }
    }

    // ---- IPreviewHandler ----
    public void SetWindow(nint hwnd, in RECT prc)
    {
        try { _parentHwnd = hwnd; _rect = prc; _window?.Mover(prc); }
        catch (Exception ex) { Diag.Log(ex); }
    }

    public void SetRect(in RECT prc)
    {
        try { _rect = prc; _window?.Mover(prc); }
        catch (Exception ex) { Diag.Log(ex); }
    }

    public void DoPreview()
    {
        try
        {
            Diag.Trace($"DoPreview: entrou (parent=0x{_parentHwnd:X}, pdf={_pdfBytes?.Length ?? -1})");
            if (_parentHwnd == 0) return;
            _window?.Destruir();
            _window = new PreviewWindow { PendingPdf = _pdfBytes };
            _window.Criar(_parentHwnd, _rect);
        }
        catch (Exception ex) { Diag.Log(ex); }
    }

    public void Unload()
    {
        try
        {
            _pdfBytes = null;
            _window?.Destruir();
            _window = null;
            _parentHwnd = 0;
            if (_site != 0) { Marshal.Release(_site); _site = 0; }
        }
        catch (Exception ex) { Diag.Log(ex); }
    }

    public void SetFocus()
    {
        try { if (_window?.Hwnd is { } h && h != 0) SetFocusWin(h); }
        catch (Exception ex) { Diag.Log(ex); }
    }

    public void QueryFocus(out nint phwnd)
    {
        phwnd = 0;
        try { phwnd = GetFocusWin(); }
        catch (Exception ex) { Diag.Log(ex); }
    }

    public int TranslateAccelerator(in MSG pmsg) => S_FALSE;

    // ---- IObjectWithSite ----
    public void SetSite(nint pUnkSite)
    {
        try
        {
            if (_site != 0) { Marshal.Release(_site); _site = 0; }
            _site = pUnkSite;
            if (_site != 0) Marshal.AddRef(_site);
        }
        catch (Exception ex) { Diag.Log(ex); }
    }

    public void GetSite(in Guid riid, out nint ppvSite)
    {
        ppvSite = 0;
        try { if (_site != 0) Marshal.QueryInterface(_site, riid, out ppvSite); }
        catch (Exception ex) { Diag.Log(ex); }
    }

    // ---- IOleWindow ----
    public void GetWindow(out nint phwnd) => phwnd = _window?.Hwnd ?? 0;
    public void ContextSensitiveHelp(bool fEnterMode) { }

    // ---- leitura do IStream via vtable (sem marshalling gerenciado do stream) ----
    private static unsafe byte[] LerStreamCompleto(nint pstream)
    {
        if (pstream == 0) return [];
        // vtable[3] = ISequentialStream::Read(this, void* pv, ULONG cb, ULONG* pcbRead) -> HRESULT
        nint vtbl = *(nint*)pstream;
        var read = (delegate* unmanaged<nint, byte*, uint, uint*, int>)(*(nint*)(vtbl + 3 * sizeof(nint)));

        using var ms = new MemoryStream();
        const int TAM = 64 * 1024;
        var buffer = new byte[TAM];
        uint lidos;
        fixed (byte* p = buffer)
        {
            while (true)
            {
                lidos = 0;
                int hr = read(pstream, p, TAM, &lidos);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr) ?? new IOException($"IStream.Read HRESULT 0x{hr:X8}");
                if (lidos == 0) break;
                ms.Write(buffer, 0, (int)lidos);
            }
        }
        return ms.ToArray();
    }

    private const int S_FALSE = 1;

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "SetFocus")]
    private static partial nint SetFocusWin(nint hwnd);
    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "GetFocus")]
    private static partial nint GetFocusWin();
}
