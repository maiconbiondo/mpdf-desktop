using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// Plano 14 (Task 5) — organizador escuro, barra de anotação flutuante e busca escura. Sondas STA
/// (mesmo padrão de Task3PaineisTests/ShellTests) + verificação de FIAÇÃO (cada botão liga ao comando de
/// anotação/organizador EXISTENTE). Nada toca o caminho de render/overlay (fronteira SAGRADA): a barra
/// flutuante e a de busca são CHROME sobre o viewer.
public class Task5Tests
{
    private static void RunSta(Action scenario)
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try { scenario(); }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        bool joined = thread.Join(TimeSpan.FromSeconds(45));
        Assert.True(joined, "thread STA não terminou dentro de 45s (BLOCKED: possível deadlock/hang do WPF)");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }

    private static void AdicionarTokens(Window w) =>
        w.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/mPdf.App;component/Themes/Tokens.Escuro.xaml", UriKind.Absolute) });

    private static IEnumerable<T> Descendentes<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var f in Descendentes<T>(child)) yield return f;
        }
    }

    private static void Pump(Func<bool> cond, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!cond() && DateTime.UtcNow < end)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static void SalvarPng(Window w, string caminho)
    {
        int cw = Math.Min(1200, (int)Math.Ceiling(w.ActualWidth));
        int ch = Math.Min(800, (int)Math.Ceiling(w.ActualHeight));
        if (cw <= 0 || ch <= 0) return;
        var rtb = new RenderTargetBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = new FileStream(caminho, FileMode.Create);
        enc.Save(fs);
    }

    private static void Fechar(mPdf.App.MainWindow? w)
    {
        if (w is not null) w.ViewModel.Documents.Clear();
        w?.Close();
        try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* já desligando */ }
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    private static DocumentViewModel AbrirDoc(mPdf.App.MainWindow w, string fixture = "fixture-30p.pdf")
    {
        var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, fixture)));
        w.ViewModel.Documents.Add(doc);
        w.ViewModel.SelectedDocument = doc;
        w.UpdateLayout();
        Pump(() => doc.Pages.Count > 0, TimeSpan.FromSeconds(10));
        return doc;
    }

    public static readonly string PngOrganizador = Path.Combine(Path.GetTempPath(), "mpdf-t5-organizador.png");
    public static readonly string PngBusca = Path.Combine(Path.GetTempPath(), "mpdf-t5-busca.png");
    public static readonly string PngAnotacao = Path.Combine(Path.GetTempPath(), "mpdf-t5-anotacao.png");

    // ───────────────────────────────── ORGANIZADOR ─────────────────────────────────

    [Fact]
    public void Fidelidade_Organizador_Renderiza()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Width = 1200; w.Height = 800;
                w.Show();
                var doc = AbrirDoc(w);
                doc.IsOrganizerOpen = true;
                w.UpdateLayout();

                var org = Descendentes<PageOrganizerView>(w).FirstOrDefault();
                Assert.NotNull(org);
                Assert.Equal(Visibility.Visible, org!.Visibility);

                // Seleciona a 1ª página pra exibir a borda azul + atualizar o rótulo de contagem.
                doc.Organizer!.ToggleSelect(0, ctrl: false);
                w.UpdateLayout();
                Pump(() => doc.Organizer.Pages.Take(3).Any(p => p.ImageSource is not null), TimeSpan.FromSeconds(12));
                w.UpdateLayout();

                SalvarPng(w, PngOrganizador);
                Assert.True(new FileInfo(PngOrganizador).Length > 0);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void Organizador_BotoesLigadosAosComandosExistentes()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Show();
                var doc = AbrirDoc(w);
                doc.IsOrganizerOpen = true;
                w.UpdateLayout();

                var org = Descendentes<PageOrganizerView>(w).First();
                var botoes = Descendentes<Button>(org).ToList();
                // Cada ação da barra resolve o comando REAL do OrganizerViewModel (nenhum handler novo).
                Assert.Contains(botoes, b => b.Command == doc.Organizer!.RotateSelectedCommand);
                Assert.Contains(botoes, b => b.Command == doc.Organizer!.DeleteSelectedCommand);
                Assert.Contains(botoes, b => b.Command == doc.Organizer!.MoveSelectionLeftCommand);
                Assert.Contains(botoes, b => b.Command == doc.Organizer!.MoveSelectionRightCommand);
                Assert.Contains(botoes, b => b.Command == doc.Organizer!.ExtractSelectedCommand);
                Assert.Contains(botoes, b => b.Command == doc.Organizer!.InsertCommand);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void Organizador_ConcluirFechaOModo()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Show();
                var doc = AbrirDoc(w);
                doc.IsOrganizerOpen = true;
                w.UpdateLayout();

                var org = Descendentes<PageOrganizerView>(w).First();
                // "Concluir" = o botão com o estilo primário azul (sem x:Name); dispara o Click handler
                // que seta IsOrganizerOpen=false (mesmo efeito do ToggleButton "Organizar" da command bar).
                var concluir = Descendentes<Button>(org).First(b => (b.ToolTip as string) == "Concluir e fechar o organizador");
                concluir.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                w.UpdateLayout();
                Assert.False(doc.IsOrganizerOpen);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void Organizador_SelectionCountLabel_Atualiza()
    {
        var doc = new DocumentViewModel(DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf")));
        try
        {
            doc.IsOrganizerOpen = true;
            var org = doc.Organizer!;
            Assert.Equal("Nenhuma página selecionada", org.SelectionCountLabel);
            org.ToggleSelect(0, ctrl: false);
            Assert.Equal("1 página selecionada", org.SelectionCountLabel);
            org.ToggleSelect(1, ctrl: true);
            Assert.Equal("2 páginas selecionadas", org.SelectionCountLabel);
        }
        finally
        {
            doc.IsOrganizerOpen = false;
            try { PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }

    // ───────────────────────────────── BUSCA ─────────────────────────────────

    [Fact]
    public void Fidelidade_Busca_Renderiza()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Width = 1200; w.Height = 800;
                w.Show();
                var doc = AbrirDoc(w);
                doc.Search.IsOpen = true;
                doc.Search.Query = "contrato";
                w.UpdateLayout();

                var barra = Descendentes<SearchBar>(w).FirstOrDefault();
                Assert.NotNull(barra);
                // O Border interno (com Visibility ligada a IsOpen) deve estar visível.
                var borda = Descendentes<Border>(barra!).First();
                Assert.Equal(Visibility.Visible, borda.Visibility);
                w.UpdateLayout();
                SalvarPng(w, PngBusca);
                Assert.True(new FileInfo(PngBusca).Length > 0);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void Busca_BotoesLigadosAosComandosExistentes()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Show();
                var doc = AbrirDoc(w);
                doc.Search.IsOpen = true;
                w.UpdateLayout();

                var barra = Descendentes<SearchBar>(w).First();
                var botoes = Descendentes<Button>(barra).ToList();
                Assert.Contains(botoes, b => b.Command == doc.Search.PreviousCommand);
                Assert.Contains(botoes, b => b.Command == doc.Search.NextCommand);
                Assert.Contains(botoes, b => b.Command == doc.Search.CloseCommand);
            }
            finally { Fechar(w); }
        });
    }

    // ───────────────────────── BARRA DE ANOTAÇÃO FLUTUANTE ─────────────────────────

    [Fact]
    public void Fidelidade_BarraAnotacao_Renderiza()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Width = 1200; w.Height = 800;
                w.Show();
                var doc = AbrirDoc(w, "fixture-a4.pdf"); // não assinado -> ferramentas habilitadas
                w.UpdateLayout();

                var pilula = AcharPilula(w, doc);
                Assert.Equal(Visibility.Visible, pilula.Visibility);
                w.UpdateLayout();
                SalvarPng(w, PngAnotacao);
                Assert.True(new FileInfo(PngAnotacao).Length > 0);
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void BarraAnotacao_FerramentasLigadasAosComandosExistentes()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Show();
                var doc = AbrirDoc(w, "fixture-a4.pdf");
                w.UpdateLayout();

                var pilula = AcharPilula(w, doc);
                var botoes = Descendentes<Button>(pilula).ToList();
                // Marcação (ApplyMarkup) — 3 botões, mesmo comando, parâmetros AnnotationKind distintos.
                var markup = botoes.Where(b => b.Command == doc.ApplyMarkupCommand).ToList();
                Assert.Equal(3, markup.Count);
                Assert.Contains(markup, b => Equals(b.CommandParameter, AnnotationKind.Highlight));
                Assert.Contains(markup, b => Equals(b.CommandParameter, AnnotationKind.Underline));
                Assert.Contains(markup, b => Equals(b.CommandParameter, AnnotationKind.Strikeout));
                // Ferramentas de colocação/desenho.
                Assert.Contains(botoes, b => b.Command == doc.ToggleStickyNoteToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleFreeTextToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleInkToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleRectangleToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleLineToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleArrowToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleImageToolCommand);
                // Swatches de cor (comandos de cor de anotação existentes).
                Assert.Contains(botoes, b => b.Command == doc.SelectColorAmareloCommand);
                Assert.Contains(botoes, b => b.Command == doc.SelectColorVerdeCommand);
                Assert.Contains(botoes, b => b.Command == doc.SelectColorVermelhoCommand);
                // Galeria de carimbos: o gatilho é um ToggleButton (abre o Popup — cujos botões
                // AddStamp/SelectStamp ficam numa árvore visual PRÓPRIA do Popup, com DataContext
                // reancorado no MainViewModel via PlacementTarget.DataContext).
                Assert.Contains(Descendentes<ToggleButton>(pilula), t => t.Name == "StampGalleryToggle");
            }
            finally { Fechar(w); }
        });
    }

    [Fact]
    public void BarraAnotacao_ColapsaSemDocumentoENoOrganizador()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Show();
                w.UpdateLayout();
                // Sem documento: a pílula fica colapsada (NullToVis não casa "Visible").
                var pilulaVazia = AcharPilulaOuNull(w);
                if (pilulaVazia is not null) Assert.Equal(Visibility.Collapsed, pilulaVazia.Visibility);

                var doc = AbrirDoc(w, "fixture-a4.pdf");
                w.UpdateLayout();
                var pilula = AcharPilula(w, doc);
                Assert.Equal(Visibility.Visible, pilula.Visibility);

                // No modo organizador a pílula some (não deve sobrepor o organizador).
                doc.IsOrganizerOpen = true;
                w.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, pilula.Visibility);
            }
            finally { Fechar(w); }
        });
    }

    // ───────────────────── POSIÇÃO DO MENU DE ANOTAÇÃO (Plano 17 T3) ─────────────────────

    [Fact] // default: pílula flutuante VISÍVEL, tira do rail AUSENTE (comportamento de hoje preservado).
    public void MenuAnotacao_DefaultFlutuante_PilulaVisivel_TiraDoRailAusente()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Width = 1200; w.Height = 800;
                w.Show();
                var doc = AbrirDoc(w, "fixture-a4.pdf");
                w.UpdateLayout();

                Assert.False(w.ViewModel.MenuAnotacaoNaBarraLateral); // default -> flutuante

                var pilula = AcharPilula(w, doc);
                Assert.Equal(Visibility.Visible, pilula.Visibility);

                var tira = (ScrollViewer)w.FindName("AnotacaoRailStrip")!;
                Assert.Equal(Visibility.Collapsed, tira.Visibility);
            }
            finally { Fechar(w); }
        });
    }

    [Fact] // barra lateral: a tira vertical no rail APARECE (com as ferramentas ligadas aos MESMOS
    // comandos do documento) e a pílula flutuante SOME.
    public void MenuAnotacao_BarraLateral_TiraVisivel_PilulaColapsada_MesmosComandos()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Width = 1200; w.Height = 800;
                w.Show();
                var doc = AbrirDoc(w, "fixture-a4.pdf");
                w.UpdateLayout();

                // Troca AO VIVO pra barra lateral (mesma flag que a opção do diálogo Configurações seta).
                w.ViewModel.MenuAnotacaoNaBarraLateral = true;
                w.UpdateLayout();

                var pilula = AcharPilula(w, doc);
                Assert.Equal(Visibility.Collapsed, pilula.Visibility); // pílula flutuante SOME

                var tira = (ScrollViewer)w.FindName("AnotacaoRailStrip")!;
                Assert.Equal(Visibility.Visible, tira.Visibility); // tira vertical no rail APARECE

                // As ferramentas da tira ligam aos MESMOS comandos do documento (mesma fiação da pílula).
                var botoes = Descendentes<Button>(tira).ToList();
                var markup = botoes.Where(b => b.Command == doc.ApplyMarkupCommand).ToList();
                Assert.Equal(3, markup.Count);
                Assert.Contains(markup, b => Equals(b.CommandParameter, AnnotationKind.Highlight));
                Assert.Contains(markup, b => Equals(b.CommandParameter, AnnotationKind.Underline));
                Assert.Contains(markup, b => Equals(b.CommandParameter, AnnotationKind.Strikeout));
                Assert.Contains(botoes, b => b.Command == doc.ToggleStickyNoteToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleFreeTextToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleInkToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleRectangleToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleLineToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleArrowToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.ToggleImageToolCommand);
                Assert.Contains(botoes, b => b.Command == doc.SelectColorAmareloCommand);
                Assert.Contains(botoes, b => b.Command == doc.SelectColorVerdeCommand);
                Assert.Contains(botoes, b => b.Command == doc.SelectColorVermelhoCommand);
                Assert.Contains(Descendentes<ToggleButton>(tira), t => t.Name == "StampGalleryToggle");

                // Prova direta "MESMO ICommand" (Assert.Same): o marca-texto da tira e o da pílula apontam
                // pra a MESMÍSSIMA instância de comando do documento (não uma cópia/handler paralelo).
                var pilulaHighlight = Descendentes<Button>(pilula)
                    .First(b => b.Command == doc.ApplyMarkupCommand && Equals(b.CommandParameter, AnnotationKind.Highlight));
                var tiraHighlight = markup.First(b => Equals(b.CommandParameter, AnnotationKind.Highlight));
                Assert.Same(pilulaHighlight.Command, tiraHighlight.Command);
            }
            finally { Fechar(w); }
        });
    }

    [Fact] // aplica AO VIVO nos dois sentidos, na MESMA janela (só a flag muda, sem recriar nada).
    public void MenuAnotacao_TrocaAoVivo_AlternaVisibilidadeSemRecriarJanela()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? w = null;
            try
            {
                w = new mPdf.App.MainWindow();
                AdicionarTokens(w);
                w.Width = 1200; w.Height = 800;
                w.Show();
                var doc = AbrirDoc(w, "fixture-a4.pdf");
                w.UpdateLayout();

                var pilula = AcharPilula(w, doc);
                var tira = (ScrollViewer)w.FindName("AnotacaoRailStrip")!;

                // Estado inicial (flutuante).
                Assert.Equal(Visibility.Visible, pilula.Visibility);
                Assert.Equal(Visibility.Collapsed, tira.Visibility);

                // -> barra lateral
                w.ViewModel.MenuAnotacaoNaBarraLateral = true;
                w.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, pilula.Visibility);
                Assert.Equal(Visibility.Visible, tira.Visibility);

                // -> volta pra flutuante
                w.ViewModel.MenuAnotacaoNaBarraLateral = false;
                w.UpdateLayout();
                Assert.Equal(Visibility.Visible, pilula.Visibility);
                Assert.Equal(Visibility.Collapsed, tira.Visibility);
            }
            finally { Fechar(w); }
        });
    }

    /// A pílula flutuante tem x:Name="AnotacaoBar" (Border direto no Grid da área de documentos, fora de
    /// qualquer template) — resolvido pelo FindName da própria janela.
    private static Border AcharPilula(mPdf.App.MainWindow w, DocumentViewModel doc) =>
        AcharPilulaOuNull(w) ?? throw new Xunit.Sdk.XunitException("pílula de anotação (AnotacaoBar) não encontrada");

    private static Border? AcharPilulaOuNull(mPdf.App.MainWindow w, DocumentViewModel? doc = null) =>
        w.FindName("AnotacaoBar") as Border;
}
