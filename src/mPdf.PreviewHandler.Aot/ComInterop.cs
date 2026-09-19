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

// ---- Task 1 (menu Win11): interfaces do IExplorerCommand (comando "mPDF" no menu de contexto novo do
// Explorer). Mesma disciplina do preview handler: ponteiros de item-array/LPWSTR/subcomando entram/saem
// como `nint` cru (em vez dos tipos de interface do brief original) — evita depender de uma ComWrappers
// "default" implicita pro marshalling automatico de interface-em-interface no COM source-gen; a leitura
// do IShellItem e a criacao do CCW dos subcomandos usam a MESMA StrategyBasedComWrappers (Exports.Cw) que
// o ClassFactory ja usa. IIDs verificados contra shobjidl_core.h.

[GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
public partial interface IShellItem
{
    void BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
    void GetParent(out nint ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out nint ppszName); // SIGDN_FILESYSPATH = 0x80058000; ppszName = LPWSTR (CoTaskMem) -> Marshal.PtrToStringUni + Marshal.FreeCoTaskMem
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(nint psi, uint hint, out int piOrder);
}

// IShellItemArray — só os métodos que usamos, na ORDEM da vtable real (não pode pular nenhum).
[GeneratedComInterface, Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
public partial interface IShellItemArray
{
    void BindToHandler(nint pbc, in Guid rbhid, in Guid riid, out nint ppvOut);
    void GetPropertyStore(uint flags, in Guid riid, out nint ppv);
    void GetPropertyDescriptionList(nint keyType, in Guid riid, out nint ppv);
    void GetAttributes(int dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out nint ppsi);
    void EnumItems(out nint ppenumShellItems);
}

[GeneratedComInterface, Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8")]
public partial interface IEnumExplorerCommand
{
    [PreserveSig] int Next(uint celt, out nint pUICommand, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out nint ppenum);
}

[GeneratedComInterface, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
public partial interface IExplorerCommand
{
    [PreserveSig] int GetTitle(nint psiItemArray, out nint ppszName);   // ppszName = LPWSTR via Marshal.StringToCoTaskMemUni
    [PreserveSig] int GetIcon(nint psiItemArray, out nint ppszIcon);    // idem (ou devolva E_NOTIMPL=0x80004001 e ppszIcon=0)
    [PreserveSig] int GetToolTip(nint psiItemArray, out nint ppszInfotip); // E_NOTIMPL + 0
    [PreserveSig] int GetCanonicalName(out Guid pguidCommandName);      // devolva Guid.Empty + S_OK
    [PreserveSig] int GetState(nint psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCmdState); // ECS_ENABLED=0
    [PreserveSig] int Invoke(nint psiItemArray, nint pbc);
    [PreserveSig] int GetFlags(out uint pFlags);                        // ECF_DEFAULT=0; pai: ECF_HASSUBCOMMANDS=0x10
    [PreserveSig] int EnumSubCommands(out nint ppEnum);
}
