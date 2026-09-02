using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Documents;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

/// Task 2 (Plano 9): nitidez — a escala de RENDER (densidade de pixels do bitmap) ganha o fator de DPI
/// do monitor (`DocumentViewModel.DpiFactor`, seam testável — injetado DIRETO aqui, nenhum monitor/
/// `VisualTreeHelper` real precisa existir), enquanto a escala LÓGICA (`DisplayWidth`/`DisplayHeight`,
/// overlays, seleção de texto, caixa do carimbo) fica INTOCADA — essa fronteira é a pergunta central da
/// task (ver task-2-brief.md). Os testes de overlay/seleção/caixa do carimbo já existentes
/// (StampBoxPlacementTests, TextSelectionTests, ViewerIntegrationTests etc.) são a PROVA de regressão
/// dessa fronteira: nenhum deles foi tocado por esta task, e todos continuam verdes.
public class PageDpiTests
{
    // Config ISOLADO (Plano 17, Task 1): sem `config:` o ctor lê o %AppData%\mPDF\config.json REAL; com
    // NitidezExtra=true na máquina o SupersampleFactor vira 2.0 e os testes de dimensão de render saem em
    // 2× (bitmap dobrado). Isolar o config mantém o render no fator 1.0 esperado por estes testes.
    private static DocumentViewModel Doc() =>
        new(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")), config: Fixtures.IsolatedConfig());

    [Fact] // default (nenhuma View setou nada ainda) — comportamento de HOJE, byte a byte: factor 1.0.
    public void DpiFactor_DefaultsToOne()
    {
        using var doc = Doc();
        Assert.Equal(1.0, doc.DpiFactor);
    }

    [Fact] // ApplyDpiFactor é o SEAM que a View usa pra propagar o DPI do SO — testável sem monitor
    // real nenhum (VisualTreeHelper.GetDpi nunca entra em jogo aqui, brief: "propagado por seam
    // testável — não estático").
    public void PdfViewerControl_ApplyDpiFactor_WritesToBoundDocumentViewModel()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunApplyDpiFactorScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "thread STA não terminou a tempo");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunApplyDpiFactorScenario()
    {
        DocumentViewModel? doc = null;
        try
        {
            doc = Doc();
            var control = new PdfViewerControl { DataContext = doc };
            Assert.Equal(1.0, doc.DpiFactor); // sanity: default antes de qualquer ApplyDpiFactor

            control.ApplyDpiFactor(1.5);

            Assert.Equal(1.5, doc.DpiFactor);
        }
        finally
        {
            doc?.Dispose();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    /// STA ponta a ponta (brief: "real viewer at simulated high DPI"): injeta o fator via o MESMO seam
    /// que o SO usaria (`ApplyDpiFactor` — nenhum monitor real precisa estar a 150% pra este teste
    /// rodar) numa `PdfViewerControl` REAL dentro de uma `Window`, e prova as DUAS metades da fronteira
    /// ao mesmo tempo: (a) o BITMAP entregue nasce mais denso E com a tag de DPI correta; (b) o LAYOUT
    /// na tela (`Image`, dentro do `Border` bindado a `DisplayWidth`/`DisplayHeight`) não muda 1px.
    [Fact]
    public void Viewer_HighDpiFactor_RendersDenserTaggedBitmap_WithLogicalLayoutUnchanged()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { RunHighDpiScenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "thread STA não terminou a tempo");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void RunHighDpiScenario()
    {
        DocumentViewModel? doc = null;
        PdfViewerControl? control = null;
        Window? window = null;
        try
        {
            // Config ISOLADO (Plano 17, Task 1): baseline bmp100 espera DpiX=96 e dimensão a escala 1.0;
            // o config real com NitidezExtra=true daria SupersampleFactor 2.0 e reprovaria o baseline.
            doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")), config: Fixtures.IsolatedConfig());
            control = new PdfViewerControl { DataContext = doc };
            window = new Window { Width = 1000, Height = 800, Content = control };
            window.Show();

            Pump(() => doc.Pages[0].ImageSource is not null, TimeSpan.FromSeconds(20));
            var bmp100 = Assert.IsAssignableFrom<BitmapSource>(doc.Pages[0].ImageSource);

            // Oráculo independente (exemplar: ExportImageIntegrationTests) — nenhum número hand-
            // calculado; um SEGUNDO render real na MESMA escala é quem decide o pixel esperado.
            using (var independent = new PdfDocumentRenderer(Fixtures.A4()))
            {
                var expected100 = independent.RenderPage(0, doc.Zoom * PageViewModel.PtToPx);
                Assert.Equal(expected100.WidthPx, bmp100.PixelWidth);
                Assert.Equal(expected100.HeightPx, bmp100.PixelHeight);
            }
            Assert.Equal(96, bmp100.DpiX, 3);
            Assert.Equal(96, bmp100.DpiY, 3);

            double logicalWidthBefore = doc.Pages[0].DisplayWidth;
            double logicalHeightBefore = doc.Pages[0].DisplayHeight;
            var image = FindDescendantByDataContext<Image>(window, doc.Pages[0]);
            Assert.NotNull(image); // DataTemplate real resolveu
            Pump(() => image!.ActualWidth > 0, TimeSpan.FromSeconds(5));
            Assert.Equal(logicalWidthBefore, image!.ActualWidth, 1);
            Assert.Equal(logicalHeightBefore, image.ActualHeight, 1);

            // ---- injeta o fator 1.5 (SEAM — nenhum monitor real a 150% precisa existir) -------------
            control.ApplyDpiFactor(1.5);
            Pump(() => doc.Pages[0].ImageSource is BitmapSource b && b.DpiX > 96, TimeSpan.FromSeconds(20));

            var bmp150 = Assert.IsAssignableFrom<BitmapSource>(doc.Pages[0].ImageSource);
            Assert.Equal(144, bmp150.DpiX, 3);
            Assert.Equal(144, bmp150.DpiY, 3);

            using (var independent = new PdfDocumentRenderer(Fixtures.A4()))
            {
                var expected150 = independent.RenderPage(0, doc.Zoom * PageViewModel.PtToPx * 1.5);
                Assert.Equal(expected150.WidthPx, bmp150.PixelWidth);
                Assert.Equal(expected150.HeightPx, bmp150.PixelHeight);
            }

            // fronteira central da task: mais pixels no bitmap, ZERO mudança no layout lógico.
            Assert.Equal(logicalWidthBefore, doc.Pages[0].DisplayWidth, 6);
            Assert.Equal(logicalHeightBefore, doc.Pages[0].DisplayHeight, 6);
            Assert.Equal(logicalWidthBefore, image.ActualWidth, 1);
            Assert.Equal(logicalHeightBefore, image.ActualHeight, 1);
        }
        finally
        {
            window?.Close();
            doc?.Dispose();
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* descarte faltoso não pode mascarar o encerramento/falha real; já estamos desligando */ }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    [Fact] // DpiFactor muda mas a página NÃO está realizada — RefreshDpi vira no-op (ImageSource
    // continua null), nenhum render é pedido à toa pra página fora de tela (mesma disciplina de
    // OnRealized: só páginas realizadas custam alguma coisa).
    public void DpiFactorChanged_UnrealizedPage_DoesNotTriggerRender()
    {
        using var doc = Doc();
        var page = doc.Pages[0];
        Assert.Null(page.ImageSource); // nunca realizada

        doc.DpiFactor = 1.5;

        Assert.Null(page.ImageSource);
    }

    private static void Pump(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(50);
        }
    }

    private static T? FindDescendantByDataContext<T>(DependencyObject root, object dataContext) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && ReferenceEquals(t.DataContext, dataContext)) return t;
            if (FindDescendantByDataContext<T>(child, dataContext) is { } found) return found;
        }
        return null;
    }
}
