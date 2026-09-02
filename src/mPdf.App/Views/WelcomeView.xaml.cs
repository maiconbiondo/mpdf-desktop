using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using mPdf.App.ViewModels;

namespace mPdf.App.Views;

/// Plano 14 (Task 3) — tela de boas-vindas / estado vazio. Só VISUAL + fiação ao existente: os comandos
/// (Abrir/Juntar/Assinar em lote/Recentes) vêm do MainViewModel herdado no DataContext. O único
/// code-behind é o arrastar-e-soltar de um PDF na drop zone, que chama o MESMO `MainViewModel.OpenPath`
/// usado por Abrir/Recentes (nenhuma lógica de abertura nova).
public partial class WelcomeView : UserControl
{
    public WelcomeView() => InitializeComponent();

    private void Welcome_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TemPdf(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Welcome_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (CaminhoPdf(e) is not { } pdf) return;
        await vm.OpenPath(pdf);
    }

    private static bool TemPdf(DragEventArgs e) => CaminhoPdf(e) is not null;

    private static string? CaminhoPdf(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop)
        && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.FirstOrDefault(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            : null;
}
