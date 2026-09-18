using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using mPdf.Rendering;

namespace mPdf.PreviewHandler.Aot;

/// Janela-filha Win32 (GDI) criada DENTRO do painel do Explorer, onde a página do PDF é desenhada. O
/// render (PDFium) roda em THREAD DE FUNDO pra não travar a bomba de mensagens (a janela-filha fica
/// parentada no painel do Explorer — filas de input anexadas entre processos; render síncrono congelaria
/// o Explorer). Mostra "Carregando…" na hora e repinta quando o bitmap fica pronto. Idêntico em lógica à
/// 1a abordagem (provado no harness) — só muda o hosting (Native AOT).
internal sealed partial class PreviewWindow
{
    private nint _hwnd;
    private IReadOnlyList<(double, double)>? _tamanhos;
    private IReadOnlyList<PaginaPreviewLayout> _layout = Array.Empty<PaginaPreviewLayout>();
    private int _alturaTotal;
    private int _larguraLayout;
    private LruCache<int, RenderedPage> _cache = new(TetoCache);
    private readonly HashSet<int> _emFila = new();
    private int _pageCount = 1;
    private int _scrollY;
    private string? _mensagem;

    private readonly object _lockBmp = new();
    private volatile int _geracao;
    private static readonly object _renderLock = new();

    private static readonly ConcurrentDictionary<nint, PreviewWindow> Instancias = new();
    private static readonly WndProcDelegate ProcDelegate = WndProc;
    private static ushort _atomClasse;
    private const string NomeClasse = "mPdfPreviewWndAot";

    public byte[]? PendingPdf { get; set; }
    public nint Hwnd => _hwnd;

    private const int MargemPx = 8, GapPx = 10, TetoCache = 12;

    public nint Criar(nint parent, RECT rect)
    {
        GarantirClasseRegistrada();
        int w = Math.Max(0, rect.right - rect.left);
        int h = Math.Max(0, rect.bottom - rect.top);
        _hwnd = CreateWindowEx(0, _atomClasse, null, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            rect.left, rect.top, w, h, parent, 0, GetModuleHandle(null), 0);
        if (_hwnd == 0) { Diag.Trace($"Criar: CreateWindowEx falhou err={Marshal.GetLastWin32Error()}"); return 0; }
        Instancias[_hwnd] = this;

        lock (_lockBmp) { _mensagem = "Carregando pré-visualização…"; }
        InvalidateRect(_hwnd, 0, false);
        IniciarLayoutEmFundo(w);
        return _hwnd;
    }

    public void Mover(RECT rect)
    {
        if (_hwnd == 0) return;
        int w = Math.Max(0, rect.right - rect.left);
        int h = Math.Max(0, rect.bottom - rect.top);
        MoveWindow(_hwnd, rect.left, rect.top, w, h, true);
        _scrollY = 0;
        IniciarLayoutEmFundo(w);
        InvalidateRect(_hwnd, 0, true);
    }

    public void Destruir()
    {
        if (_hwnd == 0) return;
        _geracao++;
        Instancias.TryRemove(_hwnd, out _);
        DestroyWindow(_hwnd);
        _hwnd = 0;
        lock (_lockBmp) { _cache = new LruCache<int, RenderedPage>(TetoCache); _emFila.Clear(); }
    }

    private void IniciarLayoutEmFundo(int larguraPainelPx)
    {
        int geracao = ++_geracao;
        int alvo = Math.Max(1, larguraPainelPx);
        var pdf = PendingPdf;
        var t = new Thread(() =>
        {
            IReadOnlyList<(double, double)>? tamanhos = null; int count = 1; string? msg = null;
            try
            {
                if (pdf is not { Length: > 0 }) { msg = "Não foi possível pré-visualizar este PDF."; }
                else
                {
                    lock (_renderLock)
                    {
                        using var r = new PdfDocumentRenderer(pdf);
                        count = r.PageCount;
                        var lst = new List<(double, double)>(count);
                        for (int i = 0; i < count; i++) { var s = r.GetPageSize(i); lst.Add((s.WidthPt, s.HeightPt)); }
                        tamanhos = lst;
                    }
                    Diag.Trace($"layout(fundo): OK {count} pag");
                }
            }
            catch (Exception ex) { Diag.Log(ex); msg = "Não foi possível pré-visualizar este PDF."; }

            if (geracao != _geracao) return;
            var h = _hwnd; if (h == 0) return;
            lock (_lockBmp)
            {
                _tamanhos = tamanhos; _pageCount = count; _mensagem = tamanhos is null ? msg : null;
                _larguraLayout = alvo;
                (_layout, _alturaTotal) = tamanhos is null
                    ? (Array.Empty<PaginaPreviewLayout>(), 0)
                    : PreviewLayout.Calcular(tamanhos, alvo, MargemPx, GapPx);
                _cache = new LruCache<int, RenderedPage>(TetoCache);
                _emFila.Clear();
            }
            InvalidateRect(h, 0, false);
        })
        { IsBackground = true, Name = "mPdfPreviewLayoutAot" };
        t.Start();
    }

    private void EnfileirarRender(int indice, int larguraPagPx)
    {
        var pdf = PendingPdf;
        int geracao = _geracao;
        lock (_lockBmp)
        {
            if (pdf is null || _cache.Contem(indice) || _emFila.Contains(indice)) return;
            _emFila.Add(indice);
        }
        var t = new Thread(() =>
        {
            RenderedPage? page = null;
            try { lock (_renderLock) { (page, _) = PreviewRenderer.RenderFitWidth(pdf!, indice, larguraPagPx); } }
            catch (Exception ex) { Diag.Log(ex); }

            var h = _hwnd;
            lock (_lockBmp)
            {
                _emFila.Remove(indice);
                if (geracao != _geracao) return;      // layout mudou (resize/novo doc) -> descarta
                if (page is not null) _cache.Adicionar(indice, page);
            }
            if (geracao == _geracao && h != 0) InvalidateRect(h, 0, false);
        })
        { IsBackground = true, Name = $"mPdfPreviewPag{indice}" };
        t.Start();
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (!Instancias.TryGetValue(hwnd, out var self))
                return DefWindowProc(hwnd, msg, wParam, lParam);
            switch (msg)
            {
                case WM_PAINT: self.OnPaint(); return 0;
                case WM_ERASEBKGND: return 1;
                case WM_MOUSEWHEEL: self.OnWheel(wParam); return 0;
                case WM_DESTROY: Instancias.TryRemove(hwnd, out _); return 0;
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex) { Diag.Log(ex); return DefWindowProc(hwnd, msg, wParam, lParam); }
    }

    private void OnWheel(nint wParam)
    {
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        GetClientRect(_hwnd, out var rc);
        int visivel = rc.bottom - rc.top;
        int total; lock (_lockBmp) { total = _alturaTotal; }
        int maxScroll = Math.Max(0, total - visivel);
        _scrollY = Math.Clamp(_scrollY - (delta / 120) * 60, 0, maxScroll);
        InvalidateRect(_hwnd, 0, false);
    }

    private void OnPaint()
    {
        var ps = new PAINTSTRUCT();
        nint hdc = BeginPaint(_hwnd, ref ps);
        try
        {
            GetClientRect(_hwnd, out var rc);
            int cw = rc.right - rc.left, ch = rc.bottom - rc.top;
            nint brush = CreateSolidBrush(0x00F0F0F0);
            FillRect(hdc, ref rc, brush);
            DeleteObject(brush);

            IReadOnlyList<PaginaPreviewLayout> layout; string? msg; int scrollY;
            lock (_lockBmp) { layout = _layout; msg = _mensagem; scrollY = _scrollY; }

            if (msg is not null) { DesenharTexto(hdc, msg, 12, 12); return; }

            foreach (var p in layout)
            {
                int yTela = p.YTopo - scrollY;
                if (yTela + p.Altura < 0 || yTela > ch) continue; // fora da viewport
                int x = Math.Max(MargemPx, (cw - p.Largura) / 2);

                RenderedPage? page = null;
                lock (_lockBmp) { if (_cache.TryGet(p.Indice, out var cached)) page = cached; }
                if (page is not null && page.Bgra is { Length: > 0 })
                    DesenharBitmap(hdc, page.Bgra, page.WidthPx, page.HeightPx, x, yTela);
                else
                {
                    // placeholder (retangulo claro) enquanto renderiza
                    var rPag = new RECT { left = x, top = yTela, right = x + p.Largura, bottom = yTela + p.Altura };
                    nint b2 = CreateSolidBrush(0x00FFFFFF); FillRect(hdc, ref rPag, b2); DeleteObject(b2);
                    EnfileirarRender(p.Indice, p.Largura);
                }
            }
        }
        catch (Exception ex) { Diag.Log(ex); }
        finally { EndPaint(_hwnd, ref ps); }
    }

    private static void DesenharBitmap(nint hdc, byte[] bgra, int w, int h, int x, int y)
    {
        var bi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            StretchDIBits(hdc, x, y, w, h, 0, 0, w, h, handle.AddrOfPinnedObject(), ref bi, 0, 0x00CC0020);
        }
        finally { handle.Free(); }
    }

    private static void DesenharTexto(nint hdc, string texto, int x, int y)
    {
        SetBkMode(hdc, 1);
        SetTextColor(hdc, 0x00404040);
        TextOut(hdc, x, y, texto, texto.Length);
    }

    private static readonly object _lockClasse = new();
    private static void GarantirClasseRegistrada()
    {
        lock (_lockClasse)
        {
            if (_atomClasse != 0) return;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0x0002 | 0x0001,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ProcDelegate),
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(0, 32512),
                lpszClassName = Marshal.StringToHGlobalUni(NomeClasse), // alocado uma vez, vive pela sessão
            };
            _atomClasse = RegisterClassEx(ref wc);
        }
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
    private const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_MOUSEWHEEL = 0x020A, WM_DESTROY = 0x0002;

    // Blittável (ponteiros crus) — exigência do LibraryImport (source-gen P/Invoke). O nome da classe é
    // alocado uma vez em memória não-gerenciada (ver GarantirClasseRegistrada).
    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public nint lpfnWndProc; public int cbClsExtra;
        public int cbWndExtra; public nint hInstance; public nint hIcon; public nint hCursor;
        public nint hbrBackground; public nint lpszMenuName; public nint lpszClassName; public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PAINTSTRUCT
    {
        public nint hdc; public int fErase; public RECT rcPaint; public int fRestore;
        public int fIncUpdate; public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes;
        public ushort biBitCount; public uint biCompression; public uint biSizeImage;
        public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
    }

    // LibraryImport NÃO acrescenta o sufixo W automaticamente (diferente do DllImport clássico) — os APIs
    // Unicode precisam do EntryPoint explícito "...W".
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static partial ushort RegisterClassEx(ref WNDCLASSEX wc);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint exStyle, ushort classAtom, string? name, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint hInstance, nint param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] private static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool MoveWindow(nint hwnd, int x, int y, int w, int h, [MarshalAs(UnmanagedType.Bool)] bool repaint);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint hwnd, out RECT rc);
    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint hwnd, ref PAINTSTRUCT ps);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);
    [LibraryImport("user32.dll")] private static partial int FillRect(nint hdc, ref RECT rc, nint brush);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")] private static partial nint LoadCursor(nint hInstance, nint cursor);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandle(string? name);
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport("gdi32.dll", EntryPoint = "TextOutW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TextOut(nint hdc, int x, int y, string s, int len);
    [LibraryImport("gdi32.dll")] private static partial int StretchDIBits(nint hdc, int xDest, int yDest, int wDest,
        int hDest, int xSrc, int ySrc, int wSrc, int hSrc, nint bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);
}
