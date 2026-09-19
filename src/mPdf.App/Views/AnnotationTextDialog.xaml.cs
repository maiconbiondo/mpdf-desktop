using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using mPdf.App.Services;

namespace mPdf.App.Views;

/// Janelinha pt-BR de texto livre (Task 7, Plano 3a) — usada tanto pra CRIAR (Content vazio) quanto
/// EDITAR (Content pré-preenchido) uma nota adesiva/caixa de texto. Sem mediação de VM: o serviço
/// (`Services.AnnotationTextDialogService`) lê `ResultText`/`ResultFormat` direto depois de
/// `ShowDialog()`. `showFormatting` liga o painel de formatação (só na CAIXA DE TEXTO / FreeText).
public partial class AnnotationTextDialog : Window
{
    /// Texto (nota adesiva e caixa de texto). `null` se cancelou.
    public string? ResultText { get; private set; }
    /// Texto + formatação (caixa de texto). `null` se cancelou ou se `showFormatting` era false.
    public AnnotationTextResult? ResultFormat { get; private set; }

    private readonly bool _showFormatting;
    private uint _selectedColor = 0xFF000000; // preto default
    private Border? _selectedSwatch;

    // Paleta pt-BR da cor do texto (nome no tooltip).
    private static readonly (string nome, uint argb)[] Palette =
    {
        ("Preto", 0xFF000000), ("Vermelho", 0xFFD32F2F), ("Azul", 0xFF1D4E89),
        ("Verde", 0xFF2E7D32), ("Laranja", 0xFFEF6C00),
    };

    public AnnotationTextDialog(string title, string? initialText, bool showFormatting = false)
    {
        InitializeComponent();
        Title = title;
        HeaderTitle.Text = title;
        // Subtítulo curto conforme o tipo — a janela cresce sozinha (SizeToContent="Height") pra caber o
        // painel de formatação, sem Height mágico.
        HeaderSubtitle.Text = showFormatting
            ? "Digite o texto e ajuste fonte, tamanho, estilo e cor."
            : "Digite o texto da anotação.";
        _showFormatting = showFormatting;
        ContentTextBox.Text = initialText ?? string.Empty;

        if (showFormatting)
        {
            FormatPanel.Visibility = Visibility.Visible;
            BuildColorSwatches();
        }

        Loaded += (_, _) => { ContentTextBox.Focus(); ContentTextBox.SelectAll(); };
    }

    // Janela sem chrome (WindowStyle=None): arrastar pelo header move a janela, como nos demais diálogos.
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void BuildColorSwatches()
    {
        foreach (var (nome, argb) in Palette)
        {
            var swatch = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand, ToolTip = nome,
            };
            swatch.MouseLeftButtonDown += (_, _) => SelectColor(swatch, argb);
            ColorPanel.Children.Add(swatch);
            if (argb == _selectedColor) SelectColor(swatch, argb);
        }
    }

    private void SelectColor(Border swatch, uint argb)
    {
        _selectedColor = argb;
        if (_selectedSwatch is not null) _selectedSwatch.BorderBrush = Brushes.Transparent;
        swatch.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0x89)); // realce de seleção
        _selectedSwatch = swatch;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultText = ContentTextBox.Text;
        if (_showFormatting)
            ResultFormat = new AnnotationTextResult(
                ContentTextBox.Text, ReadFontSize(), BoldToggle.IsChecked == true,
                ItalicToggle.IsChecked == true, ReadFamily(), _selectedColor);
        DialogResult = true;
    }

    private double ReadFontSize()
    {
        var raw = (SizeCombo.Text ?? "").Trim();
        // aceita "12" digitado ou o Content de um ComboBoxItem selecionado.
        if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v is > 0 and <= 200)
            return v;
        return 12;
    }

    private string ReadFamily() =>
        (FamilyCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Helvetica";

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ResultText = null;
        ResultFormat = null;
        DialogResult = false;
    }
}
