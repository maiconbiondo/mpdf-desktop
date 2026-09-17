using System;
using System.Runtime.InteropServices;

namespace mPdf.App.Services;

/// Escreve UMA linha no console do processo PAI (ex.: o PowerShell do agente que rodou
/// `& mPdf.App.exe /update`). mPdf.App e WinExe (sem console proprio); AttachConsole(ATTACH_PARENT_PROCESS)
/// pega o console do pai. Se nao houver console pai (lancado sem console), falha silenciosa — o exit code
/// (setado pelo chamador) continua sendo o sinal confiavel.
public static class ConsoleStatus
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();

    public static void Escrever(string linha)
    {
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return; // sem console pai — segue so com exit code
            try { Console.Out.WriteLine(linha); Console.Out.Flush(); }
            finally { FreeConsole(); }
        }
        catch { /* best-effort: nunca deixa a linha de status derrubar o processo de update */ }
    }
}
