using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using mPdf.App.Services;
using mPdf.Editing;

namespace mPdf.App.Views;

/// Corrigir texto (2026-10-09): janela pt-BR que troca o texto de um trecho de UMA linha. Chega
/// pré-preenchida com o estilo detectado (`CorrigirTextoPedido`) e mostra uma PRÉVIA renderizada a cada
/// mudança. Sem mediação de VM: o serviço (`CorrigirTextoDialogService`) lê `Resultado` depois de
/// `ShowDialog()`.
///
/// Prévia: o callback `previa` roda FORA da UI thread (Task.Run), nunca lança, mas NÃO é serializado
/// por quem o fornece — por isso esta janela (1) espera 300 ms de calmaria (debounce), (2) nunca dispara
/// uma chamada enquanto outra está em voo (se algo mudou nesse meio-tempo, roda EXATAMENTE mais uma
/// depois) e (3) descarta resposta velha pelo contador de geração (campos mudaram desde o pedido).
public partial class CorrigirTextoDialog : Window
{
    private readonly CorrigirTextoPedido _pedido;
    private readonly Func<CorrigirTextoResultado, CorrigirTextoPrevia> _previa;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _geracao;          // sobe a cada mudança de campo; resposta de geração antiga é descartada
    private bool _previaEmVoo;
    private bool _previaPendente;  // mudou algo com uma prévia em voo -> roda mais uma ao terminar
    private bool _fechada;
    private uint _corArgb;
    private Border? _swatchSelecionado;

    /// O que o usuário confirmou (Aplicar). null se cancelou.
    public CorrigirTextoResultado? Resultado { get; private set; }

    // Mesma paleta do AnnotationTextDialog (nome no tooltip).
    private static readonly (string nome, uint argb)[] Paleta =
    {
        ("Preto", 0xFF000000), ("Vermelho", 0xFFD32F2F), ("Azul", 0xFF1D4E89),
        ("Verde", 0xFF2E7D32), ("Laranja", 0xFFEF6C00),
    };

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public CorrigirTextoDialog(CorrigirTextoPedido pedido, Func<CorrigirTextoResultado, CorrigirTextoPrevia> previa)
    {
        InitializeComponent();
        _pedido = pedido;
        _previa = previa;

        // Preenche ANTES de ligar os eventos (não agenda prévia por causa do preenchimento).
        TextoAtualBox.Text = pedido.TextoAtual;
        TextoNovoBox.Text = pedido.TextoAtual;
        FamiliaCombo.SelectedIndex = pedido.Familia switch
        {
            TextFontFamily.Serif => 0,
            TextFontFamily.SansSerif => 1,
            _ => 2,
        };
        NegritoToggle.IsChecked = pedido.Negrito;
        ItalicoToggle.IsChecked = pedido.Italico;
        TamanhoBox.Text = pedido.TamanhoPt.ToString("0.##", PtBr);
        _corArgb = pedido.CorArgb;
        MontarCores();
        AtualizarAplicar();

        TextoNovoBox.TextChanged += (_, _) => { AtualizarAplicar(); AgendarPrevia(); };
        TamanhoBox.TextChanged += (_, _) => AgendarPrevia();
        FamiliaCombo.SelectionChanged += (_, _) => AgendarPrevia();
        NegritoToggle.Checked += (_, _) => AgendarPrevia();
        NegritoToggle.Unchecked += (_, _) => AgendarPrevia();
        ItalicoToggle.Checked += (_, _) => AgendarPrevia();
        ItalicoToggle.Unchecked += (_, _) => AgendarPrevia();

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await AtualizarPreviaAsync(); };
        Loaded += async (_, _) => { TextoNovoBox.Focus(); TextoNovoBox.SelectAll(); await AtualizarPreviaAsync(); };
        Closed += (_, _) => { _fechada = true; _debounce.Stop(); };
    }

    public CorrigirTextoResultado ResultadoAtual() => new(TextoNovoBox.Text, FamiliaSelecionada(),
        NegritoToggle.IsChecked == true, ItalicoToggle.IsChecked == true, TamanhoValido(), _corArgb);

    private TextFontFamily FamiliaSelecionada() => FamiliaCombo.SelectedIndex switch
    {
        0 => TextFontFamily.Serif,
        1 => TextFontFamily.SansSerif,
        2 => TextFontFamily.Monospace,
        _ => _pedido.Familia,
    };

    /// Aceita "10,5" (pt-BR) ou "10.5"; clamp 4..144; inválido -> tamanho do pedido.
    private double TamanhoValido()
    {
        var raw = (TamanhoBox.Text ?? "").Trim();
        if (!double.TryParse(raw, NumberStyles.Float, PtBr, out var v) &&
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return _pedido.TamanhoPt;
        // NumberStyles.Float não inclui AllowThousands: "12.5" FALHA em pt-BR e cai no Invariant (12,5),
        // nunca vira 125.
        if (double.IsNaN(v) || double.IsInfinity(v)) return _pedido.TamanhoPt;
        return Math.Clamp(v, 4, 144);
    }

    private void AtualizarAplicar() => AplicarButton.IsEnabled = !string.IsNullOrWhiteSpace(TextoNovoBox.Text);

    private void AgendarPrevia()
    {
        _geracao++; // resposta em voo (se houver) passa a ser velha
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task AtualizarPreviaAsync()
    {
        if (_fechada) return;
        if (_previaEmVoo) { _previaPendente = true; return; }
        _previaEmVoo = true;
        try
        {
            do
            {
                _previaPendente = false;
                int g = _geracao;
                var pedido = ResultadoAtual();
                var p = await Task.Run(() => _previa(pedido));
                if (_fechada) return;
                if (g != _geracao) continue; // campos mudaram desde o pedido: resposta velha
                MostrarPrevia(p);
            } while (_previaPendente && !_fechada);
        }
        finally { _previaEmVoo = false; }
    }

    private void MostrarPrevia(CorrigirTextoPrevia p)
    {
        // Recortes vêm em escala 2.0 (144 dpi): 144 aqui faz o recorte aparecer ~1:1 com a página a 100%.
        PreviaImage.Source = p.Recorte is { WidthPx: > 0, HeightPx: > 0 } r && r.Bgra.Length >= r.WidthPx * r.HeightPx * 4
            ? BitmapSource.Create(r.WidthPx, r.HeightPx, 144, 144, PixelFormats.Bgra32, null, r.Bgra, r.WidthPx * 4)
            : null;
        AvisoText.Text = p.Aviso ?? "";
        AvisoText.Visibility = p.Aviso is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MontarCores()
    {
        var cores = Paleta.ToList();
        // Cor detectada fora da paleta entra primeiro, pra não "trocar a cor" sem o usuário pedir.
        if (!cores.Any(c => c.argb == _corArgb)) cores.Insert(0, ("Cor original", _corArgb));
        foreach (var (nome, argb) in cores)
        {
            var swatch = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand, ToolTip = nome,
            };
            swatch.MouseLeftButtonDown += (_, _) => { SelecionarCor(swatch, argb); AgendarPrevia(); };
            CorPanel.Children.Add(swatch);
            if (argb == _corArgb) SelecionarCor(swatch, argb);
        }
    }

    private void SelecionarCor(Border swatch, uint argb)
    {
        _corArgb = argb;
        if (_swatchSelecionado is not null) _swatchSelecionado.BorderBrush = Brushes.Transparent;
        swatch.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0x89)); // realce de seleção
        _swatchSelecionado = swatch;
    }

    // Janela sem chrome (WindowStyle=None): arrastar pelo header move a janela, como nos demais diálogos.
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Aplicar_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextoNovoBox.Text)) return;
        Resultado = ResultadoAtual();
        DialogResult = true;
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e)
    {
        Resultado = null;
        DialogResult = false;
    }
}
