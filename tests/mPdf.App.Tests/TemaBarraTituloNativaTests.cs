using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using mPdf.App.Services;
using Xunit;

namespace mPdf.App.Tests;

/// Barra de título NATIVA: a cor do caption segue o tema do WINDOWS (modo de app claro/escuro em
/// HKCU\...\Themes\Personalize\AppsUseLightTheme), independente do tema Claro/Escuro do próprio mPDF.
/// A decisão ("deve ser escuro?", qual atributo DWM, se a mensagem é troca de tema) é pura e testada
/// aqui; a parte nativa (DwmSetWindowAttribute) é sondada numa janela real numa thread STA, com o
/// leitor do tema INJETADO — o teste nunca altera a configuração real do Windows.
public class TemaBarraTituloNativaTests
{
    // ───── decisão pura ─────

    [Theory]
    [InlineData(0, true)]   // AppsUseLightTheme = 0 -> modo escuro
    [InlineData(1, false)]  // 1 -> claro
    [InlineData(2, false)]  // valor inesperado -> claro (padrão do Windows)
    public void DeveSerEscuro_PorValorDword(int valor, bool esperado) =>
        Assert.Equal(esperado, TemaBarraTituloNativa.DeveSerEscuro(valor));

    [Fact] // valor ausente (chave/valor inexistente) -> claro
    public void DeveSerEscuro_Ausente_EhClaro() => Assert.False(TemaBarraTituloNativa.DeveSerEscuro(null));

    [Fact] // tipo inesperado no registro (ex.: string) -> claro, nunca lança
    public void DeveSerEscuro_TipoInesperado_EhClaro() => Assert.False(TemaBarraTituloNativa.DeveSerEscuro("0"));

    [Theory]
    [InlineData(22631, 20)] // Win11
    [InlineData(19045, 20)] // Win10 22H2
    [InlineData(18985, 20)] // 1º build com o atributo 20
    [InlineData(18362, 19)] // Win10 1903: atributo não documentado 19
    [InlineData(17763, 19)] // Win10 1809
    public void AtributoModoEscuro_PorBuild(int build, int esperado) =>
        Assert.Equal(esperado, TemaBarraTituloNativa.AtributoModoEscuro(build));

    [Theory]
    [InlineData("ImmersiveColorSet", true)]
    [InlineData("WindowsThemeElement", false)]
    [InlineData("Policy", false)]
    [InlineData(null, false)]
    public void EhMudancaDeTema(string? area, bool esperado) =>
        Assert.Equal(esperado, TemaBarraTituloNativa.EhMudancaDeTema(area));

    [Fact] // leitura real do registro: nunca lança (pode ser null/0/1 conforme a máquina)
    public void SistemaEmModoEscuro_NaoLanca()
    {
        var ex = Record.Exception(() => TemaBarraTituloNativa.SistemaEmModoEscuro());
        Assert.Null(ex);
    }

    [Fact] // hwnd inválido: falha é IGNORADA (devolve false), nunca derruba o app
    public void Aplicar_HwndZero_NaoLanca_DevolveFalse() =>
        Assert.False(TemaBarraTituloNativa.Aplicar(IntPtr.Zero, escuro: true));

    // ───── sonda nativa (janela real, STA) ─────

    [Fact]
    public void Conectar_AplicaNoSourceInitialized_EReaplicaNoImmersiveColorSet()
    {
        RunSta(() =>
        {
            bool escuro = true;
            var w = new Window { Width = 200, Height = 120, ShowActivated = false, WindowStyle = WindowStyle.SingleBorderWindow };
            try
            {
                TemaBarraTituloNativa.Conectar(w, () => escuro);
                w.Show();
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                Assert.Equal(1, LerAtributo(hwnd));

                escuro = false;
                // mensagem irrelevante: não reaplica
                SendMessage(hwnd, WM_SETTINGCHANGE, IntPtr.Zero, "Policy");
                Assert.Equal(1, LerAtributo(hwnd));
                // troca de tema do Windows: reaplica ao vivo
                SendMessage(hwnd, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet");
                Assert.Equal(0, LerAtributo(hwnd));

                escuro = true;
                SendMessage(hwnd, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet");
                Assert.Equal(1, LerAtributo(hwnd));
            }
            finally { w.Close(); }
        });
    }

    [Fact] // leitor que LANÇA: a janela abre normalmente (falha = ignorar)
    public void Conectar_LeitorQueLanca_NaoDerrubaAJanela()
    {
        RunSta(() =>
        {
            var w = new Window { Width = 200, Height = 120, ShowActivated = false };
            try
            {
                TemaBarraTituloNativa.Conectar(w, () => throw new InvalidOperationException("registro quebrado"));
                w.Show();
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                SendMessage(hwnd, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet");
                Assert.True(w.IsVisible);
            }
            finally { w.Close(); }
        });
    }

    private const int WM_SETTINGCHANGE = 0x001A;

    private static int LerAtributo(IntPtr hwnd)
    {
        int atributo = TemaBarraTituloNativa.AtributoModoEscuro(Environment.OSVersion.Version.Build);
        int hr = DwmGetWindowAttribute(hwnd, atributo, out int valor, sizeof(int));
        Assert.True(hr == 0, $"DwmGetWindowAttribute({atributo}) falhou: 0x{hr:X8}");
        return valor;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

    private static void RunSta(Action scenario)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { scenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "thread STA não terminou dentro de 40s");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }
}
