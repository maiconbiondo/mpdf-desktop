using System.IO;

namespace mPdf.PreviewHandler.Aot;

/// Log de diagnóstico best-effort. Escreve em %USERPROFILE%\AppData\LocalLow\mPDF — LocalLow é gravável
/// por processos de BAIXA INTEGRIDADE (o prevhost de pré-visualização roda assim; %LOCALAPPDATA% normal
/// NÃO é gravável de lá — foi por isso que a 1a abordagem não deixava log). NUNCA lança.
internal static class Diag
{
    public static void Log(Exception ex) => Escrever(ex.ToString());
    public static void Trace(string msg) => Escrever(msg);

    private static void Escrever(string texto)
    {
        try
        {
            var perfil = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(perfil)) return;
            var dir = Path.Combine(perfil, "AppData", "LocalLow", "mPDF");
            Directory.CreateDirectory(dir);
            var arquivo = Path.Combine(dir, "preview-handler-aot.log");
            // Cap simples: o handler loga a cada pré-visualização; evita crescimento sem limite.
            try { if (new FileInfo(arquivo) is { Exists: true, Length: > 262144 }) File.WriteAllText(arquivo, ""); }
            catch { /* ignora */ }
            File.AppendAllText(arquivo,
                $"{DateTime.Now:o}  [pid{Environment.ProcessId} t{Environment.CurrentManagedThreadId}]  {texto}{Environment.NewLine}");
        }
        catch { /* best-effort */ }
    }
}
