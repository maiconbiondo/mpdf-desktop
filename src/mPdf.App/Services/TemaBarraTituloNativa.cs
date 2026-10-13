using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace mPdf.App.Services;

/// Barra de título NATIVA da janela principal: a cor do caption (claro/escuro) segue o MODO DE APP do
/// Windows (Configurações > Personalização > Cores > "Escolha o modo de aplicativo padrão"), lido em
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme (0 = escuro;
/// ausente/1/qualquer outra coisa = claro). É INDEPENDENTE do tema Claro/Escuro do próprio mPDF
/// (ThemeService), por decisão do usuário.
///
/// Aplicação: DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE) no SourceInitialized (antes
/// da 1ª pintura, sem "piscar" claro) e de novo, AO VIVO, quando o Windows avisa troca de tema
/// (WM_SETTINGCHANGE com lParam "ImmersiveColorSet"). Atributo 20 a partir do build 18985; 19 (não
/// documentado) nos Windows 10 anteriores. Tudo o que é nativo é melhor-esforço: qualquer falha
/// (Windows antigo sem o atributo, sessão RDP, DWM indisponível, registro ilegível) é IGNORADA — a
/// janela abre com a barra padrão, nunca derruba o app.
public static class TemaBarraTituloNativa
{
    internal const string ChavePersonalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    internal const string ValorAppsUseLightTheme = "AppsUseLightTheme";
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_ANTES_20H1 = 19;
    internal const int PrimeiroBuildComAtributo20 = 18985;

    private const int WM_SETTINGCHANGE = 0x001A;

    // ───── decisão pura (testável) ─────

    /// `valorRegistro` = o que `RegistryKey.GetValue` devolveu (DWORD chega como int). Só 0 é escuro.
    public static bool DeveSerEscuro(object? valorRegistro) => valorRegistro is int v && v == 0;

    public static int AtributoModoEscuro(int buildWindows) =>
        buildWindows >= PrimeiroBuildComAtributo20 ? DWMWA_USE_IMMERSIVE_DARK_MODE : DWMWA_USE_IMMERSIVE_DARK_MODE_ANTES_20H1;

    /// WM_SETTINGCHANGE chega com lParam = nome da área alterada; "ImmersiveColorSet" = modo claro/escuro.
    public static bool EhMudancaDeTema(string? area) => string.Equals(area, "ImmersiveColorSet", StringComparison.Ordinal);

    // ───── leitura do Windows ─────

    public static object? LerAppsUseLightTheme()
    {
        try
        {
            using var chave = Registry.CurrentUser.OpenSubKey(ChavePersonalize, writable: false);
            return chave?.GetValue(ValorAppsUseLightTheme);
        }
        catch { return null; } // ilegível = claro (padrão do Windows)
    }

    public static bool SistemaEmModoEscuro() => DeveSerEscuro(LerAppsUseLightTheme());

    // ───── aplicação nativa ─────

    /// Liga/desliga o caption escuro de `hwnd`. Devolve true se o DWM aceitou; false (sem lançar) em
    /// qualquer falha. Tenta o atributo do build atual e, se recusado, o outro (builds de transição).
    public static bool Aplicar(IntPtr hwnd, bool escuro)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            int valor = escuro ? 1 : 0;
            int principal = AtributoModoEscuro(Environment.OSVersion.Version.Build);
            if (DwmSetWindowAttribute(hwnd, principal, ref valor, sizeof(int)) == 0) return true;
            int alternativo = principal == DWMWA_USE_IMMERSIVE_DARK_MODE
                ? DWMWA_USE_IMMERSIVE_DARK_MODE_ANTES_20H1
                : DWMWA_USE_IMMERSIVE_DARK_MODE;
            return DwmSetWindowAttribute(hwnd, alternativo, ref valor, sizeof(int)) == 0;
        }
        catch { return false; } // dwmapi ausente/EntryPoint etc.: ignora
    }

    /// Conecta `janela`: aplica no SourceInitialized e reaplica a cada troca de tema do Windows.
    /// `modoEscuro` é injetável só pros testes (produção: lê o registro a cada vez).
    public static void Conectar(Window janela, Func<bool>? modoEscuro = null)
    {
        var ler = modoEscuro ?? SistemaEmModoEscuro;
        janela.SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(janela).Handle;
                Aplicar(hwnd, LerSeguro(ler));
                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero)
                    {
                        try
                        {
                            if (EhMudancaDeTema(Marshal.PtrToStringUni(lParam)) && Aplicar(h, LerSeguro(ler)))
                                RedesenharMoldura(h);
                        }
                        catch { /* melhor esforço */ }
                    }
                    return IntPtr.Zero; // nunca marca handled: o WPF também precisa ver WM_SETTINGCHANGE
                });
            }
            catch { /* melhor esforço: a janela abre com a barra padrão */ }
        };
    }

    private static bool LerSeguro(Func<bool> ler)
    {
        try { return ler(); }
        catch { return false; }
    }

    /// No Windows 10 o caption só repinta com a cor nova na próxima ativação; SWP_FRAMECHANGED força agora.
    private static void RedesenharMoldura(IntPtr hwnd)
    {
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
        try { SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED); }
        catch { /* melhor esforço */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
