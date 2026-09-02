using System.Collections.Generic;
using System.Linq;
using System.Windows;
using mPdf.App.Services;
using mPdf.Signing;

namespace mPdf.App.Views;

/// Janela "Assinar" (Task 3, Plano 4) — coleta certificado (da lista do catálogo, ECC visível mas
/// desabilitado), motivo/local opcionais, DocMDP (só na 1ª assinatura) e escolha de carimbo visível.
/// Sem mediação de VM (mesmo precedente de `AnnotationTextDialog`/`MergeFilesDialog`): o serviço
/// (`Services.SignDialogService`) lê `Result` direto depois de `ShowDialog()`.
public partial class SignDialog : Window
{
    /// Item de exibição do `CertificateListBox` — envolve `SigningCertificateInfo` com o texto/estado
    /// que a View precisa (mesmo espírito de não vazar lógica de classificação pro XAML).
    private sealed class CertificateItem(SigningCertificateInfo info)
    {
        public SigningCertificateInfo Info { get; } = info;

        /// RSA: nome de exibição puro do catálogo. ECC: mesmo nome + explicação pt-BR anexada — o item
        /// continua VISÍVEL (nunca filtrado), só desabilitado (ver `IsEnabled`/`ItemContainerStyle`).
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

    public SignDialog(IReadOnlyList<SigningCertificateInfo> certificates, bool allowDocMdp)
    {
        InitializeComponent();
        CertificateListBox.ItemsSource = certificates.Select(c => new CertificateItem(c)).ToList();
        // 2ª+ assinatura: o motor RECUSA CertificationLevel != None num doc já assinado — nem oferece o
        // checkbox (Collapsed, não só desabilitado: o brief é explícito "doc já assinado: sem checkbox").
        DocMdpCheckBox.Visibility = allowDocMdp ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => CertificateListBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (CertificateListBox.SelectedItem is not CertificateItem { IsEnabled: true } item) return;

        Result = new SignDialogResult(
            item.Info.Certificate,
            string.IsNullOrWhiteSpace(ReasonTextBox.Text) ? null : ReasonTextBox.Text,
            string.IsNullOrWhiteSpace(LocationTextBox.Text) ? null : LocationTextBox.Text,
            ApplyDocMdp: DocMdpCheckBox.Visibility == Visibility.Visible && DocMdpCheckBox.IsChecked == true,
            PlaceStamp: PlaceStampRadio.IsChecked == true);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    /// Plano 14 (Task 4): a janela é sem moldura (WindowStyle=None) pra dar a superfície escura rounded do
    /// redesenho — arrastar pelo header substitui o arraste da title bar nativa (só UX de janela, nenhuma
    /// lógica de assinatura). Guarda de botão pra não conflitar com cliques.
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
