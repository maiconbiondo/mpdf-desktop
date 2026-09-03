using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Janela "Configurações" (Task 2, Plano 17) — mesmo padrão de `SobreDialog`: `DataContext` é um
/// `ConfiguracoesViewModel` REAL, já construído pelo chamador (`Services.ConfiguracoesDialogService`) —
/// toda a lógica vive lá, testável sem esta janela (ver `ConfiguracoesViewModelTests`). Este code-behind
/// não tem NENHUMA lógica de estado — só hospeda o VM (bindings puros no XAML cobrem toda a troca de
/// painel por `ConfiguracoesEstado`).
public partial class ConfiguracoesDialog : Window
{
    private readonly ConfiguracoesViewModel _viewModel;

    public ConfiguracoesDialog(ConfiguracoesViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // Plano 21: a prévia da rubrica é DADO puro no VM (bytes) — o code-behind converte pra BitmapImage.
        // Reagimos a mudanças de RubricaPreviewBytes (escolher/trocar/remover) além do estado inicial.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        AtualizarPreviaRubrica();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConfiguracoesViewModel.RubricaPreviewBytes)
            or nameof(ConfiguracoesViewModel.TemRubrica))
            AtualizarPreviaRubrica();
    }

    /// Converte os bytes da rubrica (ou nada) numa `BitmapImage` congelada e sem cache de arquivo
    /// (`OnLoad` a partir de um `MemoryStream` — o mesmo caminho pode ser reescrito e a prévia precisa
    /// refletir a imagem NOVA, nunca a cacheada). Falha silenciosa (imagem ilegível) limpa a prévia.
    private void AtualizarPreviaRubrica()
    {
        var bytes = _viewModel.RubricaPreviewBytes;
        if (bytes is null || bytes.Length == 0) { RubricaPreviewImage.Source = null; return; }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            RubricaPreviewImage.Source = bmp;
        }
        catch { RubricaPreviewImage.Source = null; }
    }

    /// Janela sem moldura (superfície escura rounded) — arrastar pelo cabeçalho substitui o arraste da
    /// title bar nativa (só UX de janela, nenhuma lógica tocada). Mesmo padrão de `SobreDialog`.
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
