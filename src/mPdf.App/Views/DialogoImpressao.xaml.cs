using System.IO;
using System.Linq;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using mPdf.App.Services;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.App.Views;

/// Plano 25 (Task 2b): diálogo de impressão avançada estilo Adobe — N-up, livreto, escala, orientação,
/// borda, intervalo, cópias, com PRÉ-VISUALIZAÇÃO ao vivo da folha imposta. Toda a matemática/desenho
/// vive em `Imposicao` (Task 1) + `ImposedPrintPaginator` (Task 2a); este arquivo é só a UI: lê os
/// controles -> `LayoutImpressao`, rende a folha atual pra prévia (dpi baixo) e imprime na impressora
/// escolhida (dpi da impressora). O picker de impressora reusa o `PrintDialog` nativo (bem estilizado),
/// evitando um ComboBox sem estilo no tema.
public partial class DialogoImpressao : Window
{
    private readonly DocumentSession _session;
    private readonly string _titulo;
    private readonly DispatcherTimer _debounce;
    private PrintQueue? _queue;
    private PrintTicket _ticket;
    private Size _papelRetrato; // papel do dispositivo em DIPs, normalizado retrato (L <= A)
    private int _folha;
    private int _totalPaginas = 1;
    private bool _carregado;
    private double _zoom = 1;          // multiplicador de exibição da prévia (1 = 1 DIP por DIP)
    private bool _ajustarZoom = true;  // true = recalcula p/ caber na janela a cada mudança de tamanho
    private Size _folhaAtualSize;      // tamanho (DIPs) da folha atualmente na prévia
    private readonly List<(double w, double h)> _pageSizesPt = new(); // tamanho (pt) de cada página do PDF

    public DialogoImpressao(DocumentSession session, string titulo)
    {
        _session = session;
        _titulo = titulo;
        _ticket = new PrintTicket();
        _papelRetrato = new Size(794, 1123); // A4 em DIPs (fallback)
        InitializeComponent();

        try
        {
            using var r = new PdfDocumentRenderer(_session.Snapshot);
            _totalPaginas = Math.Max(1, r.PageCount);
            for (int i = 0; i < r.PageCount; i++) { var s = r.GetPageSize(i); _pageSizesPt.Add((s.WidthPt, s.HeightPt)); }
        }
        catch { _totalPaginas = 1; }
        TxtAte.Text = _totalPaginas.ToString();

        InicializarImpressora();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); AtualizarPrevia(); };

        _carregado = true;
        AtualizarPrevia();
    }

    private void InicializarImpressora()
    {
        try
        {
            using var server = new LocalPrintServer();
            _queue = server.DefaultPrintQueue;
            if (_queue is not null)
            {
                LblImpressora.Text = _queue.FullName;
                var t = _queue.DefaultPrintTicket;
                if (t is not null) { _ticket = t; AtualizarPapelDoTicket(t); }
            }
            else { LblImpressora.Text = "(impressora padrão do Windows)"; }
        }
        catch { LblImpressora.Text = "(impressora padrão do Windows)"; }
    }

    private void AtualizarPapelDoTicket(PrintTicket t)
    {
        // PageMediaSize.Width/Height vêm em DIPs (1/96"). Normaliza retrato (L <= A).
        if (t.PageMediaSize?.Width is double w and > 0 && t.PageMediaSize?.Height is double h and > 0)
            _papelRetrato = new Size(Math.Min(w, h), Math.Max(w, h));
    }

    // ---- eventos dos controles -> reprograma a prévia ----
    private void Opcao_Alterada(object sender, RoutedEventArgs e) => AgendarPrevia();
    private void Opcao_Alterada(object sender, TextChangedEventArgs e) => AgendarPrevia();

    private void Livreto_Alterado(object sender, RoutedEventArgs e)
    {
        bool livreto = ChkLivreto.IsChecked == true;
        PainelNUp.IsEnabled = !livreto; // livreto é sempre 2-up
        PainelOrdem.IsEnabled = !livreto;
        AgendarPrevia();
    }

    private void Escala_Alterada(object sender, RoutedEventArgs e)
    {
        if (PainelPercent is not null) PainelPercent.Visibility = EscPercent.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AgendarPrevia();
    }

    private void Range_Alterado(object sender, RoutedEventArgs e)
    {
        if (PainelIntervalo is not null) PainelIntervalo.Visibility = RadIntervalo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AgendarPrevia();
    }

    private void AgendarPrevia()
    {
        if (!_carregado) return;
        _folha = 0; // qualquer mudança de opção volta pra 1ª folha
        _debounce.Stop();
        _debounce.Start();
    }

    // ---- leitura dos controles ----
    private LayoutImpressao LerLayout()
    {
        bool livreto = ChkLivreto.IsChecked == true;
        int nUp = NUp16.IsChecked == true ? 16 : NUp9.IsChecked == true ? 9 : NUp6.IsChecked == true ? 6
                : NUp4.IsChecked == true ? 4 : NUp2.IsChecked == true ? 2 : 1;
        var escala = EscReal.IsChecked == true ? EscalaModo.TamanhoReal
                   : EscReduzir.IsChecked == true ? EscalaModo.Reduzir
                   : EscPercent.IsChecked == true ? EscalaModo.Percentual
                   : EscalaModo.Ajustar;
        double pct = double.TryParse(TxtPercent.Text, out var p) ? Math.Clamp(p, 1, 1000) : 100;
        bool multiPorFolha = livreto || nUp > 1;
        return new LayoutImpressao
        {
            NUp = nUp,
            Ordem = OrdemV.IsChecked == true ? OrdemNup.Vertical : OrdemNup.Horizontal,
            Livreto = livreto,
            Escala = escala,
            Percentual = pct,
            Orientacao = OrientacaoModo.Auto, // rotação de página dentro da célula é sempre automática
            EspacamentoPt = multiPorFolha ? 8 : 0, // gutter (em DIPs, mesma unidade da folha)
            Borda = ChkBorda.IsChecked == true,
        };
    }

    private bool FolhaPaisagem()
    {
        if (ChkLivreto.IsChecked == true) return true;      // livreto é sempre paisagem (2-up)
        if (OriPaisagem.IsChecked == true) return true;
        return false;                                        // Retrato e Automática = retrato
    }

    private Size TamanhoFolha()
    {
        double menor = _papelRetrato.Width, maior = _papelRetrato.Height;
        return FolhaPaisagem() ? new Size(maior, menor) : new Size(menor, maior);
    }

    private PageRange? LerRange()
    {
        if (RadTodas.IsChecked == true) return null;
        int de = int.TryParse(TxtDe.Text, out var d) ? Math.Max(1, d) : 1;
        int ate = int.TryParse(TxtAte.Text, out var a) ? a : _totalPaginas;
        return new PageRange(de, Math.Max(de, ate));
    }

    // ---- prévia ----
    private void AtualizarPrevia()
    {
        var layout = LerLayout();
        var folhaSize = TamanhoFolha();
        var range = LerRange();

        var pag = new ImposedPrintPaginator(_session, layout, 96, range) { PageSize = folhaSize };
        try
        {
            int total = pag.PageCount;
            if (total <= 0) { ImgPreview.Source = null; LblFolha.Text = "Nada a imprimir"; BtnAnterior.IsEnabled = BtnProxima.IsEnabled = false; return; }
            _folha = Math.Clamp(_folha, 0, total - 1);

            var page = pag.GetPage(_folha);
            var rtb = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(folhaSize.Width)),
                Math.Max(1, (int)Math.Ceiling(folhaSize.Height)), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(page.Visual);
            rtb.Freeze();
            ImgPreview.Source = rtb;
            _folhaAtualSize = folhaSize;
            AplicarZoom();

            LblFolha.Text = $"Folha {_folha + 1} de {total}";
            BtnAnterior.IsEnabled = _folha > 0;
            BtnProxima.IsEnabled = _folha < total - 1;
        }
        catch (Exception ex) { Diag(ex); }
        finally { PendingDisposals.Enqueue(pag.Dispose); }
    }

    private void FolhaAnterior_Click(object sender, RoutedEventArgs e) { if (_folha > 0) { _folha--; AtualizarPrevia(); } }
    private void FolhaProxima_Click(object sender, RoutedEventArgs e) { _folha++; AtualizarPrevia(); }

    // ---- zoom da prévia ----
    private void AplicarZoom()
    {
        if (_folhaAtualSize.Width <= 0) return;
        if (_ajustarZoom) _zoom = CalcularFit();
        ImgPreview.Width = _folhaAtualSize.Width * _zoom;
        ImgPreview.Height = _folhaAtualSize.Height * _zoom;
        LblZoom.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private double CalcularFit()
    {
        double vw = PreviewScroll.ViewportWidth, vh = PreviewScroll.ViewportHeight;
        if (vw <= 1 || vh <= 1 || _folhaAtualSize.Width <= 0 || _folhaAtualSize.Height <= 0) return _zoom > 0 ? _zoom : 0.5;
        double margem = 28; // respiro (margem do papel + barras de rolagem)
        double z = Math.Min((vw - margem) / _folhaAtualSize.Width, (vh - margem) / _folhaAtualSize.Height);
        return Math.Clamp(z, 0.05, 5);
    }

    private void AplicarZoomManual(double novo)
    {
        _ajustarZoom = false;
        _zoom = Math.Clamp(novo, 0.05, 5);
        AplicarZoom();
    }

    private void ZoomMenos_Click(object sender, RoutedEventArgs e) => AplicarZoomManual(_zoom / 1.2);
    private void ZoomMais_Click(object sender, RoutedEventArgs e) => AplicarZoomManual(_zoom * 1.2);
    private void ZoomAjustar_Click(object sender, RoutedEventArgs e) { _ajustarZoom = true; AplicarZoom(); }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return; // sem Ctrl: rolagem normal do ScrollViewer
        e.Handled = true;
        AplicarZoomManual(e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15);
    }

    private void PreviewScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ajustarZoom) AplicarZoom();
    }

    private void TrocarImpressora_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PrintDialog();
        if (_queue is not null) dlg.PrintQueue = _queue;
        if (dlg.ShowDialog() != true) return;
        _queue = dlg.PrintQueue;
        _ticket = dlg.PrintTicket ?? _ticket;
        LblImpressora.Text = _queue?.FullName ?? "(impressora padrão do Windows)";
        AtualizarPapelDoTicket(_ticket);
        _folha = 0;
        AtualizarPrevia();
    }

    private void Imprimir_Click(object sender, RoutedEventArgs e)
    {
        var layout = LerLayout();
        var folhaSize = TamanhoFolha();
        var range = LerRange();
        int copias = int.TryParse(TxtCopias.Text, out var c) ? Math.Clamp(c, 1, 999) : 1;

        try
        {
            var pd = new PrintDialog();
            if (_queue is not null) pd.PrintQueue = _queue;
            var ticket = _ticket;
            ticket.CopyCount = copias;
            ticket.PageOrientation = FolhaPaisagem() ? PageOrientation.Landscape : PageOrientation.Portrait;
            pd.PrintTicket = ticket;

            double dpi = PrintService.ResolveDpi(ticket);
            var pag = new ImposedPrintPaginator(_session, layout, dpi, range) { PageSize = folhaSize };
            try { pd.PrintDocument(pag, _titulo); }
            finally { PendingDisposals.Enqueue(pag.Dispose); }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível imprimir:\n{ex.Message}", "mPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- salvar como PDF (imposição aplicada ao documento, não só impressão) ----
    private (List<ColocacaoFolha> col, double fw, double fh) CalcularColocacoesPdf()
    {
        var layout = LerLayout();
        var folhaDip = TamanhoFolha();
        double fw = folhaDip.Width * 72.0 / 96.0, fh = folhaDip.Height * 72.0 / 96.0; // DIP -> pt (unidade do PDF)
        var range = LerRange();

        var indices = new List<int>();
        if (range is { } r) { int de = Math.Max(1, r.PageFrom) - 1, ate = Math.Min(_totalPaginas, r.PageTo) - 1; for (int i = de; i <= ate; i++) indices.Add(i); }
        else for (int i = 0; i < _totalPaginas; i++) indices.Add(i);
        var indicesValidos = indices.Where(i => i >= 0 && i < _pageSizesPt.Count).ToList();
        if (indicesValidos.Count == 0) return (new List<ColocacaoFolha>(), fw, fh);

        var pagesPt = indicesValidos.Select(i => new TamanhoPt(_pageSizesPt[i].w, _pageSizesPt[i].h)).ToList();
        var col = Imposicao.Calcular(new TamanhoPt(fw, fh), pagesPt, layout); // top-left, pt
        var mapped = col.Select(c => new ColocacaoFolha(
            c.Folha, c.PaginaFonte < 0 ? -1 : indicesValidos[c.PaginaFonte],
            c.X, c.Y, c.Largura, c.Altura, c.Rotacao)).ToList();
        return (mapped, fw, fh);
    }

    private void SalvarPdf_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (col, fw, fh) = CalcularColocacoesPdf();
            if (col.Count == 0) { MessageBox.Show("Não há páginas para impor.", "mPDF", MessageBoxButton.OK, MessageBoxImage.Information); return; }

            IPdfEditor editor = PdfEditorFactory.Create();
            var bytes = editor.ImporEmFolhas(_session.Snapshot, col, fw, fh);

            var nome = _session.FileName;
            var baseNome = string.IsNullOrEmpty(nome) ? "documento" : Path.GetFileNameWithoutExtension(nome);
            var caminho = new FileDialogService().PickPdfToSave($"{baseNome} (imposto).pdf");
            if (caminho is null) return;
            File.WriteAllBytes(caminho, bytes);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível gerar o PDF:\n{ex.Message}", "mPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static void Diag(Exception _) { /* prévia é best-effort: uma falha de render não derruba o diálogo */ }
}
