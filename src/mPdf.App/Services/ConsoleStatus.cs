using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace mPdf.App.Services;

/// Escreve UMA linha de status para quem chamou `mPdf.App.exe /update|/checkupdate`. mPdf.App e WinExe
/// (sem console proprio). Dois caminhos, nesta ordem:
///   1. STDOUT HERDADO — o agente do mTI roda `powershell -NonInteractive -Command "& '...\mPdf.App.exe'
///      /update"` com a saida REDIRECIONADA (captura Output); o mPDF herda esse handle de stdout, entao
///      escrever nele faz a linha aparecer no Output que o mTI coleta. Tambem cobre o PowerShell
///      interativo (stdout = buffer do console). E o caminho que faz o Output funcionar de verdade.
///   2. AttachConsole(ATTACH_PARENT_PROCESS) — fallback para quando nao ha stdout herdado mas ha um
///      console pai anexavel.
/// Sem nenhum dos dois (lancado pelo Explorer), falha em silencio — o EXIT CODE (setado pelo chamador)
/// continua sendo o sinal confiavel/primario. Best-effort: nunca deixa a linha derrubar o processo.
public static class ConsoleStatus
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();

    public static void Escrever(string linha)
    {
        if (EscreverNoStdoutHerdado(linha)) return; // agente mTI / stdout redirecionado -> ja capturado
        EscreverViaAttachConsole(linha);            // fallback: console do processo pai
    }

    private static bool EscreverNoStdoutHerdado(string linha)
    {
        try
        {
            using var stdout = Console.OpenStandardOutput();
            if (stdout == Stream.Null) return false; // sem stdout herdado
            var bytes = Encoding.ASCII.GetBytes(linha + "\r\n");
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            return true;
        }
        catch { return false; } // handle invalido (ex.: lancado pelo Explorer) — tenta o fallback
    }

    private static void EscreverViaAttachConsole(string linha)
    {
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return; // sem console pai — segue so com exit code
            try { Console.Out.WriteLine(linha); Console.Out.Flush(); }
            finally { FreeConsole(); }
        }
        catch { /* best-effort */ }
    }
}
