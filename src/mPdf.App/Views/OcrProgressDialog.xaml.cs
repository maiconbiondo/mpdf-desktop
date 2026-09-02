using System;
using System.Windows;
using mPdf.App.Services;

namespace mPdf.App.Views;

/// Faixa/diálogo de progresso do OCR (Task 4, Plano 15) — janela escura MODELESS (não `ShowDialog`: o
/// comando de OCR roda `Task.Run` e precisa que a thread de UI continue bombeando a fila pra o botão
/// Cancelar responder e a barra atualizar). Sem VM: `OcrProgressDialogService` cria e opera esta janela
/// diretamente (atualiza `MensagemText`/`Barra` via o `IProgress<OcrProgress>` criado na thread de UI, e
/// dispara `Cancelamento` no clique de Cancelar). O code-behind não tem lógica de OCR nenhuma — só UI.
public partial class OcrProgressDialog : Window
{
    /// Disparado quando o usuário clica em "Cancelar" (o serviço cancela o `CancellationTokenSource`).
    public event Action? Cancelamento;

    public OcrProgressDialog() => InitializeComponent();

    /// Atualiza a faixa "Reconhecendo página N de M…" e a barra de progresso. Chamado na thread de UI
    /// (o `Progress<OcrProgress>` do serviço marshala os reports do `Task.Run` de volta pra cá).
    public void Atualizar(OcrProgress progresso)
    {
        MensagemText.Text = $"Reconhecendo página {progresso.PaginaAtual} de {progresso.TotalPaginas}…";
        Barra.Maximum = Math.Max(1, progresso.TotalPaginas);
        Barra.Value = progresso.PaginaAtual;
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e)
    {
        CancelarButton.IsEnabled = false;
        MensagemText.Text = "Cancelando…";
        Cancelamento?.Invoke();
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
