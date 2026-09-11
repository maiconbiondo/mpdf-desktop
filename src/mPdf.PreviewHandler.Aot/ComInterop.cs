using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace mPdf.PreviewHandler.Aot;

// Plano 24 (2a abordagem, Native AOT): interfaces COM do Preview Handler declaradas com COM SOURCE-GEN
// (`[GeneratedComInterface]`) — o gerador produz os stubs de marshalling em tempo de compilacao, sem
// reflection, compativel com Native AOT. IIDs sao o contrato do Windows (verificados contra propsys.h):
//   IInitializeWithStream = b824b49d-...  (NAO b7d14566 = IInitializeWithFile — bug da 1a abordagem)
//   IPreviewHandler       = 8895b1c6-...
//   IObjectWithSite       = fc4801a3-...
//   IOleWindow            = 00000114-...
// Ponteiros de interface entram/saem como `nint` (cru) pra evitar surpresas de marshalling — a leitura do
// IStream e feita direto pela vtable (ver PdfPreviewHandler.LerStreamCompleto).

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int left, top, right, bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public int pt_x, pt_y;
}

[GeneratedComInterface, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
public partial interface IInitializeWithStream
{
    void Initialize(nint pstream, uint grfMode);
}

[GeneratedComInterface, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
public partial interface IPreviewHandler
{
    void SetWindow(nint hwnd, in RECT prc);
    void SetRect(in RECT prc);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out nint phwnd);
    [PreserveSig] int TranslateAccelerator(in MSG pmsg);
}

[GeneratedComInterface, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
public partial interface IObjectWithSite
{
    void SetSite(nint pUnkSite);
    void GetSite(in Guid riid, out nint ppvSite);
}

[GeneratedComInterface, Guid("00000114-0000-0000-C000-000000000046")]
public partial interface IOleWindow
{
    void GetWindow(out nint phwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
}

// IClassFactory — o Explorer/COM chama DllGetClassObject -> devolvemos uma IClassFactory; o COM chama
// CreateInstance pra criar o PdfPreviewHandler. IID padrao do Windows: 00000001-0000-0000-C000-...46.
[GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
public partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}
