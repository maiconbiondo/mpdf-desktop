using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.App.Views;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// Task 7 (SDD "Salvar como PDF/A") — smoke/wiring do diálogo real, mesmo padrão de
/// `ExportImageDialogTests`/`DialogConstructionSmokeTests` (construção de `Window` real precisa rodar
/// fora da thread de teste do xUnit) + verificação de que o botão da barra (Task 7) está de fato ligado a
/// `DocumentViewModel.SalvarComoPdfACommand` (mesmo padrão de busca por controle na árvore visual de
/// `ShellTests.BotaoPorTooltip`).
public class PdfADialogTests
{
    [Fact] // constrói o PdfADialog sem lançar (InitializeComponent + tokens do tema)
    public void Constroi_SemLancar()
    {
        RunSta(() =>
        {
            var dialog = new PdfADialog();
            try
            {
                dialog.Show();
                Assert.NotNull(dialog);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact] // padrão: PDF/A-1b selecionado e "manter pesquisável" marcado
    public void Padroes_A1b_E_OcrMarcado()
    {
        RunSta(() =>
        {
            var dialog = new PdfADialog();
            try
            {
                dialog.Show();

                Assert.Equal(PdfANivel.A1B, dialog.Escolha.Nivel);
                Assert.True(dialog.Escolha.ManterPesquisavel);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact] // MainWindow expõe um controle cujo Command é o SalvarComoPdfACommand do documento selecionado
    public void Botao_LigadoAoComando()
    {
        RunSta(() =>
        {
            mPdf.App.MainWindow? window = null;
            try
            {
                window = new mPdf.App.MainWindow();
                window.Show();

                var doc = new DocumentViewModel(
                    DocumentSession.Open(System.IO.Path.Combine(Fixtures.Root, "fixture-a4.pdf")));
                window.ViewModel.Documents.Add(doc);
                window.ViewModel.SelectedDocument = doc;
                window.UpdateLayout();

                var achado = Descendentes<ButtonBase>(window)
                    .FirstOrDefault(b => ReferenceEquals(b.Command, doc.SalvarComoPdfACommand));

                Assert.NotNull(achado);
            }
            finally { window?.Close(); }
        });
    }

    private static System.Collections.Generic.IEnumerable<T> Descendentes<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var f in Descendentes<T>(child)) yield return f;
        }
    }

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

        bool joined = thread.Join(TimeSpan.FromSeconds(15));
        Assert.True(joined, "thread STA não terminou dentro de 15s (BLOCKED: possível deadlock/hang do WPF)");
        if (threadEx is not null) ExceptionDispatchInfo.Capture(threadEx).Throw();
    }
}
