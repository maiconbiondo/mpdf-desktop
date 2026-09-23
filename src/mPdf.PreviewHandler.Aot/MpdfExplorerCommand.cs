using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace mPdf.PreviewHandler.Aot;

/// Task 1 (menu Win11): comando de shell `IExplorerCommand` "mPDF" no menu de contexto novo do Explorer
/// (Win11), com 3 subcomandos que lançam o `mPdf.App.exe` com o verbo certo. Registrado sob um CLSID NOVO
/// (não reusa o CLSID do preview handler) e roteado pelo mesmo `ClassFactory`/`DllGetClassObject` da DLL
/// AOT — ver `ClassFactory.cs`. Mesmo contrato de robustez do preview handler: nenhuma exceção escapa de
/// um método COM (o Explorer cairia); tudo é try/catch com log best-effort via `Diag`.
///
/// Ponteiros de interface (item-array/item/subcomando) circulam como `nint` cru — o mesmo estilo já usado
/// em `ComInterop.cs`/`ClassFactory.cs` para IStream etc. — e o CCW dos objetos gerenciados (subcomandos,
/// enumerador) é criado manualmente com a MESMA `StrategyBasedComWrappers` (`Exports.Cw`) que o
/// `ClassFactory` já usa, via `GetOrCreateComInterfaceForObject` + `QueryInterface`.
[GeneratedComClass]
[Guid(Clsid)]
internal sealed partial class MpdfRootCommand : IExplorerCommand
{
    /// CLSID NOVO do comando de shell (gerado especificamente pra esta task — NÃO é o CLSID estável do
    /// preview handler, 9A675AC7-...).
    public const string Clsid = "ACB5F42F-419D-4B08-BDF4-217A69B4C7C6";

    private const string IID_IExplorerCommand = "a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9";
    private const string IID_IEnumExplorerCommand = "a88826f8-186f-4987-aade-ea0cef8fbfe8";

    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const uint ECS_ENABLED = 0;
    private const uint ECF_DEFAULT = 0;
    private const uint ECF_HASSUBCOMMANDS = 0x10;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    // ---- IExplorerCommand (comando-pai "mPDF") ----
    public int GetTitle(nint psiItemArray, out nint ppszName)
    {
        ppszName = Marshal.StringToCoTaskMemUni("mPDF");
        return S_OK;
    }

    public int GetIcon(nint psiItemArray, out nint ppszIcon)
    {
        try { ppszIcon = Marshal.StringToCoTaskMemUni(CaminhoIcone()); return S_OK; }
        catch (Exception ex) { Diag.Log(ex); ppszIcon = 0; return E_NOTIMPL; }
    }

    public int GetToolTip(nint psiItemArray, out nint ppszInfotip) { ppszInfotip = 0; return E_NOTIMPL; }

    public int GetCanonicalName(out Guid pguidCommandName) { pguidCommandName = Guid.Empty; return S_OK; }

    public int GetState(nint psiItemArray, bool fOkToBeSlow, out uint pCmdState) { pCmdState = ECS_ENABLED; return S_OK; }

    public int Invoke(nint psiItemArray, nint pbc) => S_OK; // o pai só agrupa subcomandos, não age sozinho

    public int GetFlags(out uint pFlags) { pFlags = ECF_HASSUBCOMMANDS; return S_OK; }

    public int EnumSubCommands(out nint ppEnum)
    {
        ppEnum = 0;
        try
        {
            var comandos = new IExplorerCommand[]
            {
                new LaunchCommand("Abrir", verbo: null),
                new LaunchCommand("Imprimir", verbo: "/print"),
                new LaunchCommand("Impressão avançada", verbo: "/printadv"),
            };
            return CriarCcw(new SubCommandEnum(comandos), IID_IEnumExplorerCommand, out ppEnum);
        }
        catch (Exception ex) { Diag.Log(ex); return E_FAIL; }
    }

    /// Resolve o `mPdf.App.exe`: layout instalado é `…\mPDF\mPdf.App.exe` (raiz) +
    /// `…\mPDF\previewhandler\mPdf.PreviewHandler.Aot.dll` (subpasta) — ver `tools/installer/mpdf.iss`.
    /// A DLL AOT roda a partir da subpasta `previewhandler`; o exe fica 1 nível acima.
    /// USA a pasta da PRÓPRIA DLL (`PdfiumLoader.DiretorioDestaDll`, via GetModuleHandleEx) — NÃO o
    /// diretório-base do processo: este comando é ativado sob o COM surrogate `dllhost.exe`, cujo
    /// diretório-base é System32, não a pasta do handler (mesma armadilha que o PdfiumLoader já trata).
    internal static string CaminhoExe()
        => Path.Combine(new DirectoryInfo(PdfiumLoader.DiretorioDestaDll()).Parent!.FullName, "mPdf.App.exe");

    private static string CaminhoIcone() => $"{CaminhoExe()},0";

    /// Cria o CCW (COM-callable wrapper) de um objeto gerenciado via `Exports.Cw` (a mesma
    /// `StrategyBasedComWrappers` do `ClassFactory`) e devolve a interface pedida por IID — mesmo padrão de
    /// `ClassFactory.CreateInstance`/`Exports.DllGetClassObject` (GetOrCreateComInterfaceForObject +
    /// QueryInterface + Release do IUnknown intermediário).
    internal static int CriarCcw(object alvo, string iid, out nint ppv)
    {
        nint unk = Exports.Cw.GetOrCreateComInterfaceForObject(alvo, CreateComInterfaceFlags.None);
        try
        {
            var guid = new Guid(iid);
            return Marshal.QueryInterface(unk, ref guid, out ppv);
        }
        finally { Marshal.Release(unk); }
    }

    /// Lê o caminho do 1º item selecionado a partir do `IShellItemArray*` (cru) recebido em `Invoke`.
    internal static string? LerPrimeiroCaminho(nint psiItemArray)
    {
        if (psiItemArray == 0) return null;
        nint itemPtr = 0;
        nint pName = 0;
        try
        {
            var arrayObj = Exports.Cw.GetOrCreateObjectForComInstance(psiItemArray, CreateObjectFlags.None);
            if (arrayObj is not IShellItemArray array) return null;
            if (array.GetItemAt(0, out itemPtr) != S_OK || itemPtr == 0) return null;

            var itemObj = Exports.Cw.GetOrCreateObjectForComInstance(itemPtr, CreateObjectFlags.None);
            if (itemObj is not IShellItem item) return null;
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out pName) != S_OK || pName == 0) return null;

            return Marshal.PtrToStringUni(pName);
        }
        finally
        {
            if (pName != 0) Marshal.FreeCoTaskMem(pName);
            if (itemPtr != 0) Marshal.Release(itemPtr);
        }
    }
}

/// Subcomando-folha do menu "mPDF": "Abrir" (verbo=null -> abre no visualizador), "Imprimir" (`/print`) e
/// "Impressão avançada" (`/printadv`). `Invoke` lê o caminho do arquivo selecionado e lança o
/// `mPdf.App.exe` com o verbo — nunca deixa exceção escapar pro shell.
[GeneratedComClass]
internal sealed partial class LaunchCommand(string titulo, string? verbo) : IExplorerCommand
{
    private const int S_OK = 0;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const uint ECS_ENABLED = 0;
    private const uint ECF_DEFAULT = 0;

    private readonly string _titulo = titulo;
    private readonly string? _verbo = verbo;

    public int GetTitle(nint psiItemArray, out nint ppszName) { ppszName = Marshal.StringToCoTaskMemUni(_titulo); return S_OK; }
    public int GetIcon(nint psiItemArray, out nint ppszIcon) { ppszIcon = 0; return E_NOTIMPL; }
    public int GetToolTip(nint psiItemArray, out nint ppszInfotip) { ppszInfotip = 0; return E_NOTIMPL; }
    public int GetCanonicalName(out Guid pguidCommandName) { pguidCommandName = Guid.Empty; return S_OK; }
    public int GetState(nint psiItemArray, bool fOkToBeSlow, out uint pCmdState) { pCmdState = ECS_ENABLED; return S_OK; }
    public int GetFlags(out uint pFlags) { pFlags = ECF_DEFAULT; return S_OK; }
    public int EnumSubCommands(out nint ppEnum) { ppEnum = 0; return E_NOTIMPL; } // folha: sem subcomandos

    public int Invoke(nint psiItemArray, nint pbc)
    {
        try
        {
            var caminho = MpdfRootCommand.LerPrimeiroCaminho(psiItemArray);
            if (string.IsNullOrEmpty(caminho))
            {
                Diag.Trace($"LaunchCommand({_titulo}).Invoke: nenhum item selecionado");
                return S_OK;
            }

            var psi = new ProcessStartInfo(MpdfRootCommand.CaminhoExe()) { UseShellExecute = true };
            if (_verbo is not null) psi.ArgumentList.Add(_verbo);
            psi.ArgumentList.Add(caminho);
            Process.Start(psi);
            return S_OK;
        }
        catch (Exception ex) { Diag.Log(ex); return S_OK; } // nunca propaga pro shell
    }
}

/// Cursor simples de `IEnumExplorerCommand` sobre um array fixo de subcomandos.
[GeneratedComClass]
internal sealed partial class SubCommandEnum : IEnumExplorerCommand
{
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_FAIL = unchecked((int)0x80004005);
    private const string IID_IExplorerCommand = "a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9";
    private const string IID_IEnumExplorerCommand = "a88826f8-186f-4987-aade-ea0cef8fbfe8";

    private readonly IExplorerCommand[] _comandos;
    private int _indice;

    public SubCommandEnum(IExplorerCommand[] comandos) : this(comandos, 0) { }
    private SubCommandEnum(IExplorerCommand[] comandos, int indice) { _comandos = comandos; _indice = indice; }

    public int Next(uint celt, out nint pUICommand, out uint pceltFetched)
    {
        pUICommand = 0;
        pceltFetched = 0;
        if (_indice >= _comandos.Length) return S_FALSE; // acabou
        try
        {
            var comando = _comandos[_indice];
            int hr = MpdfRootCommand.CriarCcw(comando, IID_IExplorerCommand, out pUICommand);
            if (hr != S_OK) return hr;
            _indice++;
            pceltFetched = 1;
            return S_OK;
        }
        catch (Exception ex) { Diag.Log(ex); return E_FAIL; }
    }

    public int Skip(uint celt) { _indice = Math.Min(_indice + (int)celt, _comandos.Length); return S_OK; }
    public int Reset() { _indice = 0; return S_OK; }

    public int Clone(out nint ppenum)
    {
        ppenum = 0;
        try { return MpdfRootCommand.CriarCcw(new SubCommandEnum(_comandos, _indice), IID_IEnumExplorerCommand, out ppenum); }
        catch (Exception ex) { Diag.Log(ex); return E_FAIL; }
    }
}
