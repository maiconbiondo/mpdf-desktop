using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using mPdf.App.Services;
using mPdf.Signing;

namespace mPdf.App.Views;

/// Janela "Assinar" (Task 3, Plano 4) — coleta certificado (ECC visível mas desabilitado), motivo/local,
/// DocMDP (só na 1ª assinatura) e a escolha de carimbo visível. Plano 22: "Minha rubrica" abre uma
/// GALERIA de rubricas (miniaturas selecionáveis + "+" pra adicionar + remover), gerida aqui no diálogo
/// (não mais em Configurações). Sem VM (mesmo precedente de `AnnotationTextDialog`/`MergeFilesDialog`):
/// o serviço lê `Result` direto depois de `ShowDialog()`.
public partial class SignDialog : Window
{
    private sealed class CertificateItem(SigningCertificateInfo info)
    {
        public SigningCertificateInfo Info { get; } = info;
        public string DisplayText => Info.IsRsa
            ? Info.DisplayName
            : $"{Info.DisplayName} — assinatura ECDSA não suportada nesta versão";
        public bool IsEnabled => Info.IsRsa;
        public string? DisabledReason => Info.IsRsa
            ? null
            : "Este certificado usa criptografia ECDSA — esta versão do mPDF assina somente com certificados RSA.";
    }

    /// Preenchido só quando o usuário confirma (`DialogResult = true`); permanece `null` se cancelado.
    public SignDialogResult? Result { get; private set; }

    private readonly RubricaGallery _rubricas;
    private readonly Func<byte[]?> _pickRubrica;
    private string? _selectedRubricaId;
    private byte[]? _selectedRubricaBytes;

    public SignDialog(
        IReadOnlyList<SigningCertificateInfo> certificates, bool allowDocMdp,
        RubricaGallery rubricas, Func<byte[]?> pickRubrica)
    {
        _rubricas = rubricas;
        _pickRubrica = pickRubrica;
        InitializeComponent();
        CertificateListBox.ItemsSource = certificates.Select(c => new CertificateItem(c)).ToList();
        DocMdpCheckBox.Visibility = allowDocMdp ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => CertificateListBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (CertificateListBox.SelectedItem is not CertificateItem { IsEnabled: true } item) return;

        bool useRubrica = RubricaStampRadio.IsChecked == true;
        if (useRubrica && _selectedRubricaBytes is null)
        {
            // "Minha rubrica" escolhido mas nenhuma rubrica selecionada/adicionada -> não fecha; orienta.
            RubricaHintText.Text = "Adicione uma rubrica no + e selecione-a antes de assinar.";
            return;
        }

        Result = new SignDialogResult(
            item.Info.Certificate,
            string.IsNullOrWhiteSpace(ReasonTextBox.Text) ? null : ReasonTextBox.Text,
            string.IsNullOrWhiteSpace(LocationTextBox.Text) ? null : LocationTextBox.Text,
            ApplyDocMdp: DocMdpCheckBox.Visibility == Visibility.Visible && DocMdpCheckBox.IsChecked == true,
            // "Carimbo padrão" OU "Minha rubrica" = posicionar na página; só "Sem carimbo" fica false.
            PlaceStamp: PlaceStampRadio.IsChecked == true || useRubrica,
            RubricaBytes: useRubrica ? _selectedRubricaBytes : null);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    // ---- Plano 22: galeria de rubricas -------------------------------------------------------------

    private void RubricaStampRadio_Checked(object sender, RoutedEventArgs e)
    {
        RubricaGaleriaPanel.Visibility = Visibility.Visible;
        RenderRubricaGaleria();
    }

    private void RubricaStampRadio_Unchecked(object sender, RoutedEventArgs e)
    {
        RubricaGaleriaPanel.Visibility = Visibility.Collapsed;
    }

    /// Reconstrói as miniaturas da galeria + o "+" a partir da `RubricaGallery`. Pequeno N -> rebuild
    /// imperativo simples (sem binding). Auto-seleciona a 1ª rubrica se nenhuma estiver selecionada.
    private void RenderRubricaGaleria()
    {
        RubricaTilesPanel.Children.Clear();
        var ids = _rubricas.Load();

        // se a seleção sumiu (rubrica removida) ou nunca houve, escolhe a 1ª disponível
        if ((_selectedRubricaId is null || !ids.Contains(_selectedRubricaId)) && ids.Count > 0)
            SelectRubrica(ids[0]);
        else if (ids.Count == 0)
        {
            _selectedRubricaId = null;
            _selectedRubricaBytes = null;
        }

        foreach (var id in ids)
            RubricaTilesPanel.Children.Add(BuildRubricaTile(id));
        RubricaTilesPanel.Children.Add(BuildAddTile());

        RubricaHintText.Text = ids.Count == 0
            ? "Nenhuma rubrica salva ainda. Toque no + para adicionar uma imagem (PNG ou JPG) da sua rubrica."
            : "Toque numa rubrica para selecioná-la, ou no + para adicionar outra.";
    }

    private UIElement BuildRubricaTile(string id)
    {
        bool selected = id == _selectedRubricaId;
        var border = new Border
        {
            Width = 92, Height = 48, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6),
            Background = Brushes.White, BorderThickness = new Thickness(selected ? 2 : 1, selected ? 2 : 1, selected ? 2 : 1, selected ? 2 : 1),
            BorderBrush = selected ? BrandBrush("Cor.Primaria", Color.FromRgb(0x1D, 0x4E, 0x89))
                                   : BrandBrush("Cor.Borda", Color.FromRgb(0xC7, 0xD1, 0xDC)),
            Cursor = Cursors.Hand,
        };
        var grid = new Grid();
        var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(5) };
        try { img.Source = DecodeFrozen(_rubricas.LoadBytes(id)); } catch { /* miniatura ilegível -> vazia */ }
        grid.Children.Add(img);

        // "×" de remover (canto superior-direito)
        var remove = new TextBlock
        {
            Text = "✕", FontSize = 10, Padding = new Thickness(3, 0, 3, 1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Foreground = BrandBrush("Cor.TextoSecundario", Color.FromRgb(0x82, 0x82, 0x8C)),
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), Cursor = Cursors.Hand,
            ToolTip = "Remover esta rubrica",
        };
        remove.MouseLeftButtonDown += (_, e) => { e.Handled = true; RemoveRubrica(id); };
        grid.Children.Add(remove);

        border.Child = grid;
        border.MouseLeftButtonDown += (_, _) => { SelectRubrica(id); RenderRubricaGaleria(); };
        return border;
    }

    private UIElement BuildAddTile()
    {
        var border = new Border
        {
            Width = 92, Height = 48, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), BorderBrush = BrandBrush("Cor.Borda", Color.FromRgb(0xC7, 0xD1, 0xDC)),
            Background = BrandBrush("Cor.Superficie", Color.FromRgb(0x24, 0x27, 0x36)), Cursor = Cursors.Hand,
            ToolTip = "Adicionar rubrica",
        };
        border.Child = new TextBlock
        {
            Text = "+", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrandBrush("Cor.TextoSecundario", Color.FromRgb(0x82, 0x82, 0x8C)),
        };
        border.MouseLeftButtonDown += (_, _) => AddRubrica();
        return border;
    }

    private void SelectRubrica(string id)
    {
        _selectedRubricaId = id;
        try { _selectedRubricaBytes = _rubricas.LoadBytes(id); }
        catch (IOException) { _selectedRubricaBytes = null; }
    }

    private void AddRubrica()
    {
        if (_pickRubrica() is not { } bytes) return; // cancelado/rejeitado (produção já notificou)
        string id = _rubricas.Add(bytes);
        SelectRubrica(id);
        RenderRubricaGaleria();
    }

    private void RemoveRubrica(string id)
    {
        _rubricas.Remove(id);
        if (id == _selectedRubricaId) { _selectedRubricaId = null; _selectedRubricaBytes = null; }
        RenderRubricaGaleria();
    }

    private static ImageSource DecodeFrozen(byte[] bytes)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private Brush BrandBrush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    /// Plano 14 (Task 4): janela sem moldura — arrastar pelo cabeçalho substitui a title bar nativa.
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
