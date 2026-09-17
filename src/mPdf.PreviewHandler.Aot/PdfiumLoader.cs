using System.IO;
using System.Runtime.InteropServices;

namespace mPdf.PreviewHandler.Aot;

/// Faz o PDFium (`pdfium.dll`) carregar da PASTA DA NOSSA DLL, não do diretório do processo. Crítico no
/// prevhost: o host é `C:\Windows\System32\prevhost.exe`, então a busca padrão de DLL NÃO inclui a pasta
/// do handler. Delega para `mPdf.Rendering.PdfiumNativeResolver` (o Docnet fica confinado lá, na fronteira
/// de render) passando o diretório desta DLL nativa.
internal static partial class PdfiumLoader
{
    private static bool _feito;
    private static readonly object _lock = new();

    public static void Ensure()
    {
        lock (_lock)
        {
            if (_feito) return;
            _feito = true;
            try
            {
                var dir = DiretorioDestaDll();
                mPdf.Rendering.PdfiumNativeResolver.ResolveFrom(dir);
                Diag.Trace($"PdfiumLoader: resolver via mPdf.Rendering (dir='{dir}')");
            }
            catch (Exception ex) { Diag.Log(ex); }
        }
    }

    /// Diretório do MÓDULO nativo que contém este código (a nossa DLL AOT), obtido pelo endereço de uma
    /// função nossa — não do processo (prevhost) nem do CWD.
    private static unsafe string DiretorioDestaDll()
    {
        delegate*<void> marcador = &Marcador;
        GetModuleHandleEx(0x4 /*FROM_ADDRESS*/ | 0x2 /*UNCHANGED_REFCOUNT*/, (nint)marcador, out nint hmod);
        char* buf = stackalloc char[1024];
        uint n = GetModuleFileName(hmod, buf, 1024);
        var caminho = new string(buf, 0, (int)n);
        return Path.GetDirectoryName(caminho) ?? AppContext.BaseDirectory;
    }

    private static void Marcador() { }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleEx(uint flags, nint addr, out nint hModule);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    private static unsafe partial uint GetModuleFileName(nint hModule, char* filename, uint size);
}
