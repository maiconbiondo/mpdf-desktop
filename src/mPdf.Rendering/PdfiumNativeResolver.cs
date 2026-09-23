using System.Runtime.InteropServices;

namespace mPdf.Rendering;

/// Faz o `pdfium.dll` (nativo, usado pelo Docnet) ser carregado de um DIRETÓRIO específico, em vez de
/// depender da busca de DLL padrão do processo. Necessário para o Preview Handler (Native AOT): ele roda
/// dentro do `prevhost.exe` (que fica em System32), então a busca padrão NÃO encontra o `pdfium.dll` que
/// está na pasta do handler — sem isto o render falha com DllNotFoundException.
///
/// Mora AQUI (e não no módulo do preview handler) de propósito: Docnet fica CONFINADO a mPdf.Rendering
/// (fronteira arquitetural — só este módulo referencia o motor de render). O handler chama `ResolveFrom`.
public static class PdfiumNativeResolver
{
    private static bool _registrado;
    private static readonly object _lock = new();

    /// Registra um resolvedor de import nativo (uma vez) que carrega `pdfium.dll` de `diretorio`.
    /// Idempotente e best-effort — nunca lança.
    public static void ResolveFrom(string diretorio)
    {
        lock (_lock)
        {
            if (_registrado) return;
            _registrado = true;
            try
            {
                var pdfium = System.IO.Path.Combine(diretorio, "pdfium.dll");
                NativeLibrary.SetDllImportResolver(typeof(Docnet.Core.DocLib).Assembly, (nome, asm, caminho) =>
                {
                    if (string.Equals(nome, "pdfium", StringComparison.OrdinalIgnoreCase)
                        && System.IO.File.Exists(pdfium) && NativeLibrary.TryLoad(pdfium, out var h))
                        return h;
                    return IntPtr.Zero;
                });
            }
            catch { /* best-effort: se falhar, cai na busca padrão de DLL */ }
        }
    }
}
