using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using mPdf.App.ViewModels;
using mPdf.Documents;

namespace mPdf.App.Services;

/// Impressão (Task 8): rasteriza cada página na RESOLUÇÃO DA IMPRESSORA (não na de tela) através de
/// `PdfPrintPaginator` — um SEGUNDO `PdfDocumentRenderer` dedicado sobre `Session.Snapshot`, mesmo
/// contrato de "segundo renderer por escala" já usado pelas miniaturas (Task 6, ver doc XML de
/// `DocumentViewModel`/`PdfDocumentRenderer`).
public static class PrintService
{
    /// Monta um `DocumentPaginator` testável SEM impressora: só precisa de uma `DocumentSession` real
    /// (fixture) + dpi/range diretos. `range` é 1-based (mesmo contrato de
    /// `System.Windows.Controls.PageRange` usado pelo `PrintDialog`) — `null` imprime o documento
    /// inteiro. Cada página só é renderizada quando `GetPage` é chamado (sob demanda, dentro do
    /// paginator) — nunca todas de uma vez.
    ///
    /// NÃO seta `PageSize` — quem chama diretamente (ex.: os testes de `PageCount`, que nunca
    /// chamam `GetPage`) não precisa de papel nenhum. `Print` (caminho de produção) usa
    /// `PreparePaginator` abaixo, que ENVOLVE este método e completa a fiação que falta aqui.
    public static DocumentPaginator BuildPaginator(DocumentSession session, double dpi, PageRange? range) =>
        new PdfPrintPaginator(session, dpi, range);

    /// FIX (revisão pós-Task 8, C1): `Print` chamava `BuildPaginator` e ia direto pro
    /// `PrintDocument` SEM setar `PageSize` — `GetPage` lia `PageSize.Width/Height` (default
    /// `Size(0,0)`, nunca setado por ninguém) e `ComputePlacement` caía na guarda degenerada
    /// (`paperW <= 0`), devolvendo `Rect(0,0,0,0)` — TODO job de impressão real saía com páginas de
    /// tamanho zero. Os testes originais mascararam isso porque setavam `paginator.PageSize`
    /// manualmente antes de `GetPage`, um passo que o código de produção nunca executava.
    ///
    /// `PreparePaginator` é o que extrai essa fiação pro caminho TESTÁVEL: constrói via
    /// `BuildPaginator` e seta `PageSize = paperSize` — a MESMA sequência que `Print` executa
    /// depois de `ShowDialog`, só que aqui parametrizada (sem precisar de um `PrintDialog` real).
    /// `internal` (não parte do contrato público do brief) — exposto aos testes via
    /// `InternalsVisibleTo("mPdf.App.Tests")` já existente.
    internal static PdfPrintPaginator PreparePaginator(DocumentSession session, double dpi, PageRange? range, Size paperSize)
    {
        var paginator = (PdfPrintPaginator)BuildPaginator(session, dpi, range);
        paginator.PageSize = paperSize;
        return paginator;
    }

    /// Calcula onde o bitmap de `pageWpt`×`pageHpt` cai dentro do papel `paperW`×`paperH` (mesma
    /// unidade nos dois lados de cada par — normalmente pontos vs. DIPs de `DocumentPaginator.PageSize`,
    /// mas o cálculo em si é agnóstico de unidade): CONTIDO (nunca maior que o papel), proporção
    /// preservada (NUNCA esticado) e CENTRADO nas duas dimensões — o fator de escala é o MENOR entre
    /// largura e altura (limitado pelo lado que "bate" primeiro), mesma lógica de `FitPage` em
    /// `DocumentViewModel` (lá pra tela, aqui pro papel).
    public static Rect ComputePlacement(double pageWpt, double pageHpt, double paperW, double paperH)
    {
        if (pageWpt <= 0 || pageHpt <= 0 || paperW <= 0 || paperH <= 0) return new Rect(0, 0, 0, 0);
        double scale = Math.Min(paperW / pageWpt, paperH / pageHpt);
        double w = pageWpt * scale, h = pageHpt * scale;
        return new Rect((paperW - w) / 2.0, (paperH - h) / 2.0, w, h);
    }

    /// FIX (revisão final, C-1): deriva o dpi de um `PrintTicket` (fallback 300 — HIPÓTESE do brief),
    /// aplicando o teto de I-2. Extraído da `Print` abaixo para ser testável SEM `PrintDialog` real
    /// (`internal`, exposto aos testes via `InternalsVisibleTo("mPdf.App.Tests")` já existente).
    ///
    /// CORREÇÃO de um comentário anterior aqui que alegava "`PageResolution` é uma STRUCT (não nula),
    /// confirmado por reflexão + compilação" — essa alegação era FALSA e foi provada errada por uma
    /// probe compilada contra a API real do WPF em net10.0-windows: `System.Printing.PageResolution` é
    /// uma CLASSE (`typeof(PageResolution).IsValueType == false`), e `new PrintTicket().PageResolution`
    /// devolve `null`. Um driver cujo ticket não define resolução (comum em impressoras XPS/PDF
    /// minimalistas, ou tickets parcialmente preenchidos) deixa `PageResolution` nulo — sem o `?.`
    /// extra antes de `.X`, isso lançava `NullReferenceException` na thread de UI logo após o usuário
    /// confirmar a impressão.
    internal static double ResolveDpi(PrintTicket? ticket)
    {
        const double Fallback = 300;
        // I-2: teto de 600dpi — um ticket relatando 1200dpi rasterizaria uma página A4 inteira em
        // ~560MB de bitmap (dpi² cresce a memória quadraticamente); 600 já excede qualquer necessidade
        // prática de impressão e evita OutOfMemoryException com drivers que relatam valores exagerados.
        const double MaxDpi = 600;
        if (ticket?.PageResolution?.X is int x && x > 0) return Math.Min(x, MaxDpi);
        return Fallback;
    }

    /// Abre o `PrintDialog` nativo do WPF; se o usuário confirmar, deriva a resolução (dpi) do
    /// `PrintTicket` escolhido via `ResolveDpi` e honra o `PageRange` do próprio diálogo. Não testável
    /// automaticamente (dialog nativo bloqueia por um clique real) — coberto por smoke manual; a
    /// lógica testável (dpi, paginator, posicionamento) já está isolada em `ResolveDpi`/
    /// `BuildPaginator`/`ComputePlacement` acima.
    public static void Print(DocumentViewModel document)
    {
        var dlg = new PrintDialog();
        // FIX (revisão pós-Task 8, C2): sem isto, a opção "Páginas" fica ACINZENTADA no diálogo real
        // — `dlg.PageRangeSelection` nunca vira `UserPages` porque o usuário não consegue nem
        // escolher essa opção, deixando o código de range (linha abaixo) morto em produção mesmo
        // estando correto. `UserPageRangeEnabled` é o flag que HABILITA a opção no diálogo nativo.
        dlg.UserPageRangeEnabled = true;
        if (dlg.ShowDialog() != true) return;

        double dpi = ResolveDpi(dlg.PrintTicket);

        // `PageRangeSelection.AllPages` é o padrão quando o usuário não mexe no diálogo — só honra
        // `PageRange` quando o usuário EXPLICITAMENTE escolheu "Páginas" (`UserPages`); nos outros
        // casos (`AllPages`/`CurrentPage`/`SelectedPages` — os dois últimos fora de escopo desta v1,
        // que não expõe seleção nem "página atual" no diálogo) imprime o documento inteiro.
        PageRange? range = dlg.PageRangeSelection == PageRangeSelection.UserPages ? dlg.PageRange : null;

        // FIX (revisão pós-Task 8, C1): `PrintableAreaWidth`/`Height` (DIPs, 1/96") refletem o papel
        // REALMENTE selecionado no diálogo (tamanho + orientação escolhidos pelo usuário) — é a
        // mesma unidade que `DocumentPaginator.PageSize` espera. `PreparePaginator` seta isso ANTES
        // de qualquer `GetPage` rodar (o pipeline de impressão só chama `GetPage` dentro de
        // `PrintDocument`, abaixo), fechando o caminho que ficava com papel `(0,0)` antes deste fix.
        var paginator = PreparePaginator(document.Session, dpi, range, new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight));
        try
        {
            // PrintDocument é SÍNCRONO (serializa pro spooler chamando GetPage conforme necessário) —
            // ao retornar, todas as páginas já foram lidas; seguro descartar o renderer logo depois.
            //
            // I-1 (revisão final): guarda contra falha do spooler/driver — a falha de campo mais
            // provável com impressoras de rede (fila cheia, driver travado, impressora offline no
            // meio do job). Sem isto, uma `PrintException`/`Win32Exception` do WPF derrubava o app
            // inteiro bem depois do usuário já ter confirmado a impressão. Mesma convenção do
            // `MessageBox.Show` de erro de abertura em `MainViewModel.DefaultNotifyError`.
            dlg.PrintDocument(paginator, document.Title);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível imprimir:\n{ex.Message}", "mPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            // Mesmo contrato de descarte nativo do PDFium usado por `DocumentViewModel.Dispose` (Task
            // 6, fila SERIAL pós-revisão): nunca `Task.Run` direto, sempre via `PendingDisposals` —
            // garante no máximo 1 teardown nativo em voo no processo inteiro, mesmo que o usuário abra
            // o diálogo de impressão de novo (outro documento, outra aba) antes deste descarte terminar.
            PendingDisposals.Enqueue(paginator.Dispose);
        }
    }
}
