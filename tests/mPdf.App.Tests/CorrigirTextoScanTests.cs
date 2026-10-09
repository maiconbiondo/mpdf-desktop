using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using mPdf.App.Services;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.App.Tests;

/// Revisão final C1 (2026-10-09) + rodada 2: página ESCANEADA (tinta real na IMAGEM) + camada OCR do
/// próprio mPDF. As caixas OCR são medidas na tinta (como o Tesseract faz), mas a camada grava Helvetica
/// invisível de tamanho = altura da caixa, sem ajuste de largura — as caixas do PDFium não batem com a
/// tinta. Dados sintéticos: texto desenhado pelo WPF num bitmap 200 dpi (Times New Roman 11 pt).
/// O pipeline exercitado é o MESMO do `DocumentViewModel.CorrigirTextoCoreAsync`:
/// `PrepararCorrecao.Sondar` -> `PlanejarCorrecao.Calcular` -> `ApplyTextCorrection`.
public class CorrigirTextoScanTests
{
    private const int W = 1100, H = 350, Dpi = 200, Topo = 100; // 396 x 126 pt: inteiros (o PDFium/Docnet arredonda o tamanho da página)
    private const double FontePt = 11; // tamanho real dos glifos escaneados
    private static readonly double FontePx = FontePt * Dpi / 72; // ~30,6 px

    /// `BasePx` = linha de base REAL (px da imagem, y para baixo) da linha de cada token.
    /// `Mascaras` (rodada 5): pixels (y*W+x, px da imagem) da tinta PRÓPRIA de cada token — desenhado sozinho,
    /// dilatado 1 px. Com entrelinha 1,0 a CAIXA de uma palavra contém tinta da vizinha (descendente/acento);
    /// as checagens de sobra e de dano usam a máscara, não a caixa.
    private sealed record Scan(byte[] Pdf, Dictionary<string, Int32Rect> TintaPx, Dictionary<string, double> BasePx,
        Dictionary<string, HashSet<int>> Mascaras);

    private static HashSet<int> Mascara(Func<int, int, bool> tinta, int x0, int x1)
    {
        var m = new HashSet<int>();
        for (int y = 0; y < H; y++) for (int x = Math.Max(0, x0); x < Math.Min(W, x1); x++)
            if (tinta(x, y))
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    if (x + dx >= 0 && x + dx < W && y + dy >= 0 && y + dy < H) m.Add((y + dy) * W + x + dx);
        return m;
    }

    private static readonly string[] Normal = { "Nome: Joaquim Pereira Gomes", "Rua: Antiga Lisboa" };
    private static readonly string[] Coladas = { "pagamento gqpjy jogo", "Joaquim Pereira Gomes", "lhkbdf Tilde baldt" };
    private static readonly string[] Virgula = { "RUA XV DE NOVEMBRO, 2023 - CENTRO", "Bairro Uvaranas, Ponta Grossa" };

    /// Variante GDI (harness do revisor, rodada 5): a página inteira é desenhada com `Graphics.DrawString` por
    /// linha; a caixa OCR de cada palavra = contorno da TINTA dentro da faixa horizontal que o GDI mede para ela
    /// (`MeasureCharacterRanges`), medido num bitmap só com a linha dela (inclui acentos e cedilha, como o
    /// Tesseract, sem misturar as linhas vizinhas).
    private static Scan BuildScanGdi(string[] linhas, double entrelinha, string fonte)
    {
        int passo = (int)Math.Round(FontePx * entrelinha);
        var tinta = new Dictionary<string, Int32Rect>();
        var bases = new Dictionary<string, double>();
        var boxes = new List<OcrTextBox>();
        using var f = new System.Drawing.Font(fonte, (float)FontePx, System.Drawing.GraphicsUnit.Pixel);
        double ascPx;
        using (var ff = new System.Drawing.FontFamily(fonte))
            ascPx = FontePx * ff.GetCellAscent(System.Drawing.FontStyle.Regular) / ff.GetEmHeight(System.Drawing.FontStyle.Regular);
        var mascaras = new Dictionary<string, HashSet<int>>();
        using var pagina = new System.Drawing.Bitmap(W, H);
        pagina.SetResolution(Dpi, Dpi); // PNG a 200 dpi: página de 396 x 126 pt, como a do WPF (tamanho inteiro)
        using (var g = System.Drawing.Graphics.FromImage(pagina))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            for (int i = 0; i < linhas.Length; i++) g.DrawString(linhas[i], f, System.Drawing.Brushes.Black, 100, Topo + i * passo);
        }
        for (int li = 0; li < linhas.Length; li++)
        {
            using var so = new System.Drawing.Bitmap(W, H);
            var faixas = new List<(string W, System.Drawing.RectangleF R)>();
            using (var g = System.Drawing.Graphics.FromImage(so))
            {
                g.Clear(System.Drawing.Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.DrawString(linhas[li], f, System.Drawing.Brushes.Black, 100, Topo + li * passo);
                int pos = 0;
                foreach (var w in linhas[li].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    pos = linhas[li].IndexOf(w, pos, StringComparison.Ordinal);
                    using var sf = new System.Drawing.StringFormat();
                    sf.SetMeasurableCharacterRanges(new[] { new System.Drawing.CharacterRange(pos, w.Length) });
                    var reg = g.MeasureCharacterRanges(linhas[li], f, new System.Drawing.RectangleF(100, Topo + li * passo, 3000, 500), sf)[0];
                    faixas.Add((w, reg.GetBounds(g)));
                    pos += w.Length;
                }
            }
            var dados = so.LockBits(new System.Drawing.Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new byte[W * H * 4];
            System.Runtime.InteropServices.Marshal.Copy(dados.Scan0, px, 0, px.Length);
            so.UnlockBits(dados);
            foreach (var (w, r) in faixas)
            {
                int x0 = Math.Max(0, (int)r.Left - 1), x1 = Math.Min(W, (int)Math.Ceiling(r.Right) + 1);
                int l = int.MaxValue, rr = -1, t = int.MaxValue, b = -1;
                for (int y = 0; y < H; y++) for (int x = x0; x < x1; x++)
                    if (px[(y * W + x) * 4 + 2] < 128) { l = Math.Min(l, x); rr = Math.Max(rr, x); t = Math.Min(t, y); b = Math.Max(b, y); }
                Assert.True(rr >= 0, $"sem tinta para '{w}'");
                Assert.False(tinta.ContainsKey(w), $"token repetido no fixture: {w}");
                tinta[w] = new Int32Rect(l, t, rr - l + 1, b - t + 1);
                mascaras[w] = Mascara((x, y) => px[(y * W + x) * 4 + 2] < 200, x0, x1);
                bases[w] = Topo + li * passo + ascPx;
                boxes.Add(new OcrTextBox(w, l, t, rr - l + 1, b - t + 1));
            }
        }
        byte[] png;
        using (var ms = new MemoryStream()) { pagina.Save(ms, System.Drawing.Imaging.ImageFormat.Png); png = ms.ToArray(); }
        var editor = PdfEditorFactory.Create();
        byte[] pdf = editor.ApplyOcrTextLayer(editor.ImageToPdf(png), new[] { new OcrTextLayer(0, W, H, boxes) });
        return new Scan(pdf, tinta, bases, mascaras);
    }

    /// `entrelinha` em múltiplos do corpo da fonte (1,0 = linhas coladas: descendentes de uma quase tocam
    /// as ascendentes da outra).
    private static Scan BuildScan(string[] linhas, double entrelinha, string fonte = "Times New Roman")
    {
        int passo = (int)Math.Round(FontePx * entrelinha);
        byte[] png = null!;
        // Caixas OCR = contorno EXATO da tinta de cada token (bounds da geometria dos glifos do WPF — como
        // o Tesseract, inclui acentos, cedilha e pontuação colada). Cada token é desenhado sozinho na
        // posição de avanço da linha, então caixa e pixels batem e nenhum vizinho contamina a medida
        // (com entrelinha 1,0 o acento de uma maiúscula invade as linhas de pixels da linha de cima).
        var tinta = new Dictionary<string, Int32Rect>();
        var bases = new Dictionary<string, double>();
        var boxes = new List<OcrTextBox>();
        var mascaras = new Dictionary<string, HashSet<int>>();
        RunOnSta(() =>
        {
            var sozinhos = new List<(string Tok, FormattedText Ft, Point O)>();
            double dip = 96.0 / Dpi; // px -> DIP
            var tf = new Typeface(fonte);
            double em = FontePt * 96 / 72;
            FormattedText Ft(string t) => new(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, em, Brushes.Black, 1.0);
            double basePxDaLinha = Ft("H").Baseline / dip; // topo da linha -> base, em px
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W * dip, H * dip));
                for (int i = 0; i < linhas.Length; i++)
                {
                    double topoDip = (Topo + i * passo) * dip;
                    int pos = 0;
                    foreach (var tok in linhas[i].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        pos = linhas[i].IndexOf(tok, pos, StringComparison.Ordinal);
                        double a = pos == 0 ? 0 : Ft(linhas[i][..pos]).WidthIncludingTrailingWhitespace;
                        var origem = new Point(100 * dip + a, topoDip);
                        var ft = Ft(tok);
                        dc.DrawText(ft, origem);
                        sozinhos.Add((tok, ft, origem));
                        var b = ft.BuildGeometry(origem).Bounds;
                        int l = (int)Math.Floor(b.Left / dip), t = (int)Math.Floor(b.Top / dip);
                        int r = (int)Math.Ceiling(b.Right / dip), bo = (int)Math.Ceiling(b.Bottom / dip);
                        Assert.False(tinta.ContainsKey(tok), $"token repetido no fixture: {tok}");
                        tinta[tok] = new Int32Rect(l, t, r - l, bo - t);
                        bases[tok] = Topo + i * passo + basePxDaLinha;
                        boxes.Add(new OcrTextBox(tok, l, t, r - l, bo - t));
                        pos += tok.Length;
                    }
                }
            }
            var rtb = new RenderTargetBitmap(W, H, Dpi, Dpi, PixelFormats.Pbgra32);
            rtb.Render(dv);
            foreach (var (tok, ft, o) in sozinhos)
            {
                var so = new DrawingVisual();
                using (var dc2 = so.RenderOpen()) { dc2.DrawRectangle(Brushes.White, null, new Rect(0, 0, W * dip, H * dip)); dc2.DrawText(ft, o); }
                var r1 = new RenderTargetBitmap(W, H, Dpi, Dpi, PixelFormats.Pbgra32);
                r1.Render(so);
                var px = new byte[W * H * 4];
                r1.CopyPixels(px, W * 4, 0);
                mascaras[tok] = Mascara((x, y) => px[(y * W + x) * 4 + 2] < 200, 0, W);
            }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream(); enc.Save(ms); png = ms.ToArray();
        });
        var editor = PdfEditorFactory.Create();
        byte[] pdf = editor.ApplyOcrTextLayer(editor.ImageToPdf(png), new[] { new OcrTextLayer(0, W, H, boxes) });
        return new Scan(pdf, tinta, bases, mascaras);
    }

    /// Pixels escuros (luminância < 128) do render num retângulo em PIXELS DE RENDER, opcionalmente
    /// excluindo outro retângulo (pixels de render).
    private static int Escuros(RenderedPage p, int x0, int y0, int x1, int y1, params (int X0, int Y0, int X1, int Y1)[] fora)
    {
        int n = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(p.HeightPx, y1); y++)
        for (int x = Math.Max(0, x0); x < Math.Min(p.WidthPx, x1); x++)
        {
            if (fora.Any(f => x >= f.X0 && x < f.X1 && y >= f.Y0 && y < f.Y1)) continue;
            int i = (y * p.WidthPx + x) * 4;
            if (0.114 * p.Bgra[i] + 0.587 * p.Bgra[i + 1] + 0.299 * p.Bgra[i + 2] < 128) n++;
        }
        return n;
    }

    private static PdfQuad Quad(IReadOnlyList<PdfCharacter> cs)
    {
        var r = TextSelection.BuildLineRects(cs).Single();
        return new PdfQuad(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
    }

    public static IEnumerable<object[]> Casos() => new[]
    {
        // caso, linhas, fonte, entrelinha, trecho selecionado, token OCR esperado, troca (antigo -> novo)
        new object[] { "normal", "Normal", Times, 1.2, "Pereira", "Pereira", "Silva" },
        new object[] { "normal", "Normal", Times, 1.2, "Joaquim", "Joaquim", "Antonio" },
        new object[] { "coladas", "Coladas", Times, 1.0, "Joaquim", "Joaquim", "Antonio" },
        new object[] { "coladas", "Coladas", Times, 1.0, "Pereira", "Pereira", "Silva" },
        new object[] { "virgula", "Virgula", Times, 1.2, "NOVEMBRO", "NOVEMBRO,", "DEZEMBRO," },
        new object[] { "parcial", "Normal", Times, 1.2, "reira", "Pereira", "Silva" },
        // Rodada 3 (I-A): maiúsculas com T/P/F/V (pé de coluna acima da base) em Arial, Times e Arial Narrow.
        new object[] { "caixa-alta", "Caps", Arial, 1.2, "PONTA", "PONTA", "VILA" },
        new object[] { "caixa-alta", "Caps", Arial, 1.2, "TAXA", "TAXA", "TOLA" },
        new object[] { "caixa-alta", "Caps", Arial, 1.2, "FEITO", "FEITO", "FEIRA" },
        new object[] { "caixa-alta", "Caps", Times, 1.2, "PONTA", "PONTA", "VILA" },
        new object[] { "caixa-alta", "Caps", Times, 1.2, "TAXA", "TAXA", "TOLA" },
        new object[] { "caixa-alta", "Caps", Times, 1.2, "FEITO", "FEITO", "FEIRA" },
        new object[] { "caixa-alta", "Caps", Narrow, 1.2, "PONTA", "PONTA", "VILA" },
        // Rodada 3 (I-B): fonte estreita — a Helvetica invisível de "pagamento" invade "jogo".
        new object[] { "estreita", "Estreita", Narrow, 1.2, "jogo", "jogo", "dia" },
        new object[] { "estreita", "Estreita", Arial, 1.2, "jogo", "jogo", "dia" },
        // Rodada 4 (C-1/I-1): acentos sobre MAIÚSCULAS e Ç maiúsculo, entrelinha 1,5 e 1,0.
        new object[] { "acentos", "Acentos", Times, 1.5, "JOÃO", "JOÃO", "PEDRO" },
        new object[] { "acentos", "Acentos", Times, 1.5, "SÃO", "SÃO", "RIO" },
        new object[] { "acentos", "Acentos", Times, 1.5, "MÚSICA", "MÚSICA", "TEATRO" },
        new object[] { "acentos", "Acentos", Times, 1.5, "ÁVILA", "ÁVILA", "BELEM" },
        new object[] { "acentos", "Acentos", Times, 1.5, "CONCEIÇÃO", "CONCEIÇÃO", "CARMO" },
        new object[] { "acentos", "Acentos", Times, 1.5, "AÇÃO", "AÇÃO", "OBRA" },
        new object[] { "acentos", "Acentos", Times, 1.5, "GONÇALVES", "GONÇALVES", "MOREIRA" },
        new object[] { "acentos", "Acentos", Arial, 1.5, "JOÃO", "JOÃO", "PEDRO" },
        new object[] { "acentos", "Acentos", Arial, 1.5, "SÃO", "SÃO", "RIO" },
        new object[] { "acentos", "Acentos", Arial, 1.5, "MÚSICA", "MÚSICA", "TEATRO" },
        new object[] { "acentos", "Acentos", Arial, 1.5, "ÁVILA", "ÁVILA", "BELEM" },
        new object[] { "acentos", "Acentos", Arial, 1.5, "CONCEIÇÃO", "CONCEIÇÃO", "CARMO" },
        new object[] { "acentos", "Acentos", Arial, 1.5, "AÇÃO", "AÇÃO", "OBRA" },
        new object[] { "acentos", "Acentos", Arial, 1.5, "GONÇALVES", "GONÇALVES", "MOREIRA" },
        new object[] { "acentos-coladas", "Acentos", Times, 1.0, "JOÃO", "JOÃO", "PEDRO" },
        new object[] { "acentos-coladas", "Acentos", Times, 1.0, "MÚSICA", "MÚSICA", "TEATRO" },
        new object[] { "acentos-coladas", "Acentos", Times, 1.0, "CONCEIÇÃO", "CONCEIÇÃO", "CARMO" },
        new object[] { "acentos-coladas", "Acentos", Times, 1.0, "AÇÃO", "AÇÃO", "OBRA" },
        new object[] { "acentos-coladas", "Acentos", Arial, 1.0, "JOÃO", "JOÃO", "PEDRO" },
        new object[] { "acentos-coladas", "Acentos", Arial, 1.0, "MÚSICA", "MÚSICA", "TEATRO" },
        new object[] { "acentos-coladas", "Acentos", Arial, 1.0, "CONCEIÇÃO", "CONCEIÇÃO", "CARMO" },
        new object[] { "acentos-coladas", "Acentos", Arial, 1.0, "AÇÃO", "AÇÃO", "OBRA" },
        // Rodada 4: maiúscula acentuada + minúsculas ("Ávila": o acento quase encosta no "A").
        new object[] { "misto", "Misto", Times, 1.5, "Ávila", "Ávila", "Belém" },
        new object[] { "misto", "Misto", Times, 1.5, "Música", "Música", "Teatro" },
        new object[] { "misto", "Misto", Times, 1.5, "Conceição", "Conceição", "Carmo" },
        new object[] { "misto", "Misto", Arial, 1.5, "Ávila", "Ávila", "Belém" },
        new object[] { "misto", "Misto", Arial, 1.5, "Música", "Música", "Teatro" },
        new object[] { "misto", "Misto", Arial, 1.5, "Conceição", "Conceição", "Carmo" },
        new object[] { "misto-coladas", "Misto", Times, 1.0, "Ávila", "Ávila", "Belém" },
        new object[] { "misto-coladas", "Misto", Arial, 1.0, "Ávila", "Ávila", "Belém" },
        // Rodada 5 (X-1): Arial — caixa alta e minúsculas curtas, no MEIO e no FIM da linha (a Helvetica
        // invisível não tem a largura da tinta; pares de maiúsculas do Arial abrem lacunas maiores que o
        // limiar de "palavra" da tinta).
        new object[] { "meio-linha", "MeioLinha", Arial, 1.5, "ANTONIO", "ANTONIO", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Arial, 1.5, "ANTONIO", "ANTONIO", "XYZ" },
        new object[] { "meio-linha", "MeioLinha", Arial, 1.5, "PEREIRA", "PEREIRA", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Arial, 1.5, "PEREIRA", "PEREIRA", "XYZ" },
        new object[] { "meio-linha", "MeioLinha", Arial, 1.5, "NOVEMBRO", "NOVEMBRO", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Arial, 1.5, "NOVEMBRO", "NOVEMBRO", "XYZ" },
        new object[] { "meio-linha", "MeioLinha", Arial, 1.5, "PAGAMENTO", "PAGAMENTO", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Arial, 1.5, "PAGAMENTO", "PAGAMENTO", "XYZ" },
        new object[] { "meio-linha", "MeioLinha", Arial, 1.5, "uma", "uma", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Arial, 1.5, "uma", "uma", "XYZ" },
        new object[] { "meio-linha", "MeioLinha", Times, 1.5, "ANTONIO", "ANTONIO", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Times, 1.5, "ANTONIO", "ANTONIO", "XYZ" },
        new object[] { "meio-linha", "MeioLinha", Times, 1.5, "PEREIRA", "PEREIRA", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Times, 1.5, "PEREIRA", "PEREIRA", "XYZ" },
        new object[] { "meio-linha", "MeioLinha", Times, 1.5, "NOVEMBRO", "NOVEMBRO", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Times, 1.5, "NOVEMBRO", "NOVEMBRO", "XYZ" },
        new object[] { "meio-linha", "MeioLinha", Times, 1.5, "PAGAMENTO", "PAGAMENTO", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Times, 1.5, "PAGAMENTO", "PAGAMENTO", "XYZ" },
        new object[] { "meio-linha", "MeioLinha", Times, 1.5, "uma", "uma", "XYZ" },
        new object[] { "fim-linha", "FimLinha", Times, 1.5, "uma", "uma", "XYZ" },
        // Rodada 6: Arial Narrow — a Helvetica da caixa alta de CONCEIÇÃO invade gqpy; selecionar só uma palavra.
        new object[] { "conceicao", "Conceicao", Narrow, 1.5, "CONCEIÇÃO", "CONCEIÇÃO", "XYZ" },
        // (selecionar só "gqpy" fica no teste do motor: palavra só de descendentes tem a base pelo descendente —
        //  limitação conhecida desde a rodada 3, fora do escopo desta rodada)
        new object[] { "conceicao", "Conceicao", Arial, 1.5, "CONCEIÇÃO", "CONCEIÇÃO", "XYZ" },
        // Rodada 5: nomes com uma só letra alta (M de Times com o V no meio; b com a haste na borda da fatia).
        new object[] { "nomes", "Nomes", Times, 1.5, "Maria", "Maria", "XYZ" },
        new object[] { "nomes", "Nomes", Arial, 1.5, "Maria", "Maria", "XYZ" },
        new object[] { "nomes", "Nomes", Times, 1.5, "Ribeiro", "Ribeiro", "XYZ" },
        new object[] { "nomes", "Nomes", Arial, 1.5, "Ribeiro", "Ribeiro", "XYZ" },
        // Rodada 5 (I-1): cedilha da linha de cima x til da de baixo, entrelinha 1,17 e 1,0, nos dois sentidos.
        new object[] { "vizinhas", "Vizinhas", Arial, 1.17, "GONÇALVES", "GONÇALVES", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Arial, 1.17, "JOÃO", "JOÃO", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Arial, 1.17, "SÃO", "SÃO", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Arial, 1.0, "GONÇALVES", "GONÇALVES", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Arial, 1.0, "JOÃO", "JOÃO", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Arial, 1.0, "SÃO", "SÃO", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Times, 1.17, "GONÇALVES", "GONÇALVES", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Times, 1.17, "JOÃO", "JOÃO", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Times, 1.17, "SÃO", "SÃO", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Times, 1.0, "GONÇALVES", "GONÇALVES", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Times, 1.0, "JOÃO", "JOÃO", "XYZ" },
        new object[] { "vizinhas", "Vizinhas", Times, 1.0, "SÃO", "SÃO", "XYZ" },
    };

    private const string Times = "Times New Roman", Arial = "Arial", Narrow = "Arial Narrow";
    private static readonly string[] Caps = { "MUNICIPIO PONTA GROSSA", "TAXA FEITO PAGO" };
    private static readonly string[] Estreita = { "pagamento jogo quinta-feira", "MUNICIPIO PONTA GROSSA" };
    private static readonly string[] MeioLinha = { "ANTONIO PEREIRA NOVEMBRO PAGAMENTO uma FIM" };
    private static readonly string[] FimLinha = { "A ANTONIO", "B PEREIRA", "C NOVEMBRO", "D PAGAMENTO", "E uma" };
    private static readonly string[] Conceicao = { "CONCEIÇÃO gqpy AÇÃO" };
    private static readonly string[] Nomes = { "Maria Aparecida Ribeiro" };
    private static readonly string[] Vizinhas = { "GONÇALVES MOURA", "JOÃO SÃO PEDRO" };
    private static readonly string[] Misto = { "Rua das Flores", "Conceição Ávila Música", "Bairro Centro Oeste" };
    private static readonly string[] Acentos = { "RUA DAS FLORES NORTE", "JOÃO SÃO MÚSICA ÁVILA", "CONCEIÇÃO AÇÃO GONÇALVES", "BAIRRO CENTRO OESTE" };

    [SkippableTheory]
    [MemberData(nameof(Casos))]
    public void Escaneado_TrocaPalavra_ApagaSoOTokenEPreservaVizinhos(string caso, string conjunto, string fonte, double entrelinha,
        string selecionado, string tokenEsperado, string novo)
        => Executar(caso, conjunto, fonte, entrelinha, selecionado, tokenEsperado, novo, gdi: false);

    /// Rodada 5: os MESMOS casos com o "scan" desenhado pelo GDI (System.Drawing), como o harness do revisor —
    /// o GDI do Arial abre lacunas maiores entre maiúsculas (E|I, M|E, B|R) que o WPF.
    [SkippableTheory]
    [MemberData(nameof(Casos))]
    public void EscaneadoGdi_TrocaPalavra_ApagaSoOTokenEPreservaVizinhos(string caso, string conjunto, string fonte, double entrelinha,
        string selecionado, string tokenEsperado, string novo)
        => Executar(caso, conjunto, fonte, entrelinha, selecionado, tokenEsperado, novo, gdi: true);

    private static void Executar(string caso, string conjunto, string fonte, double entrelinha,
        string selecionado, string tokenEsperado, string novo, bool gdi)
    {
        _ = caso;
        // Arial Narrow vem com o Office, não com o Windows: sem ela o WPF trocaria a fonte em silêncio.
        Skip.If(fonte == Narrow && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "ARIALN.TTF")),
            "Arial Narrow não instalada");
        var linhas = conjunto switch { "Coladas" => Coladas, "Virgula" => Virgula, "Caps" => Caps, "Estreita" => Estreita, "Acentos" => Acentos, "Misto" => Misto, "MeioLinha" => MeioLinha, "FimLinha" => FimLinha, "Vizinhas" => Vizinhas, "Nomes" => Nomes, "Conceicao" => Conceicao, _ => Normal };
        var scan = gdi ? BuildScanGdi(linhas, entrelinha, fonte) : BuildScan(linhas, entrelinha, fonte);
        var editor = PdfEditorFactory.Create();
        using var r0 = new PdfDocumentRenderer(scan.Pdf);
        var size = r0.GetPageSize(0);
        const double s = 2.0;
        double pxToPt = size.WidthPt / W; // imagem cobre a página inteira
        var tp0 = r0.GetTextPage(0);
        int ini = tp0.Text.IndexOf(selecionado, StringComparison.Ordinal);
        Assert.True(ini >= 0, $"'{selecionado}' não está no texto do PDFium: {tp0.Text}");
        var areaSel = Quad(tp0.Characters.Skip(ini).Take(selecionado.Length).ToList());

        // Mesmo pipeline do VM.
        var (estilo, area, texto) = PrepararCorrecao.Sondar(editor, scan.Pdf, 0, areaSel, selecionado);
        Assert.NotNull(estilo);
        Assert.True(estilo!.Invisible); // sanity: é a camada OCR
        Assert.Equal(tokenEsperado, texto); // seleção encaixada no token OCR inteiro (pontuação inclusa)
        var antes = r0.RenderPage(0, s);
        var plano = PlanejarCorrecao.Calcular(estilo, area, antes, size.HeightPt, s);
        byte[] depois = editor.ApplyTextCorrection(scan.Pdf, new TextCorrection(0, plano.AreaPt, novo, TextFontFamily.SansSerif,
            false, false, plano.TamanhoPt, 0xFF000000u, plano.BaselineYPt, plano.LeftXPt, plano.FundoArgb, plano.MinOverlapRatio, plano.AreasPt));

        using var r1 = new PdfDocumentRenderer(depois);
        var apos = r1.RenderPage(0, s);
        var tp1 = r1.GetTextPage(0);
        int ni = tp1.Text.IndexOf(novo, StringComparison.Ordinal);
        Assert.True(ni >= 0, $"texto novo ausente: {tp1.Text}");
        var novoPt = Quad(tp1.Characters.Skip(ni).Take(novo.Length).ToList());
        // Caixa do texto novo em px de render, com folga de 1 pt.
        var novoPx = ((int)((novoPt.LeftPt - 1) * s), (int)((size.HeightPt - novoPt.TopPt - 1) * s),
                      (int)Math.Ceiling((novoPt.RightPt + 1) * s), (int)Math.Ceiling((size.HeightPt - novoPt.BottomPt + 1) * s));
        int Px(double imgPx) => (int)Math.Round(imgPx * pxToPt * s);
        var t = scan.TintaPx[tokenEsperado];

        // Checagens por MÁSCARA de tinta (rodada 5), num render na resolução da imagem (1 px = 1 px do scan).
        var a200 = r0.RenderPage(0, Dpi / 72.0); var d200 = r1.RenderPage(0, Dpi / 72.0);
        double s2 = Dpi / 72.0;
        (int X0, int Y0, int X1, int Y1) novo200 = ((int)((novoPt.LeftPt - 1) * s2), (int)((size.HeightPt - novoPt.TopPt - 1) * s2),
            (int)Math.Ceiling((novoPt.RightPt + 1) * s2), (int)Math.Ceiling((size.HeightPt - novoPt.BottomPt + 1) * s2));
        bool NoNovo(int k) => k % W >= novo200.X0 && k % W < novo200.X1 && k / W >= novo200.Y0 && k / W < novo200.Y1;
        bool Escuro(RenderedPage pg, int k)
        {
            int x = k % W, y = k / W;
            if (x >= pg.WidthPx || y >= pg.HeightPx) return false;
            int i = (y * pg.WidthPx + x) * 4;
            return 0.114 * pg.Bgra[i] + 0.587 * pg.Bgra[i + 1] + 0.299 * pg.Bgra[i + 2] < 128;
        }
        var propria = scan.Mascaras[tokenEsperado];
        var alheias = new HashSet<int>(scan.Mascaras.Where(kv => kv.Key != tokenEsperado).SelectMany(kv => kv.Value));
        // 1) Nenhum pixel escuro da tinta do token antigo (inteiro, com acentos/cedilha/pontuação) sobra fora da
        //    caixa do texto novo (pixels que também são de outra palavra não contam).
        int sobra = propria.Count(k => !alheias.Contains(k) && !NoNovo(k) && Escuro(d200, k));
        Assert.True(sobra == 0, $"[{plano}] sobrou tinta do token antigo: {sobra} px (antes havia {propria.Count(k => Escuro(a200, k))}) em: {string.Join(" ", propria.Where(k => !alheias.Contains(k) && !NoNovo(k) && Escuro(d200, k)).Take(8).Select(k => $"({k % W},{k / W})"))}; areas: {string.Join(" | ", plano.AreasPt ?? new List<PdfQuad>())}");
        // 2) Texto novo visível.
        Assert.True(Escuros(apos, novoPx.Item1, novoPx.Item2, novoPx.Item3, novoPx.Item4) > 30, "texto novo não aparece");
        // 3) Tamanho derivado da tinta: dentro de ±15% do tamanho real dos glifos escaneados.
        double fontePtReal = FontePx * pxToPt;
        double baseRealDbg = size.HeightPt - scan.BasePx[tokenEsperado] * pxToPt;
        Assert.True(plano.TamanhoPt >= fontePtReal * 0.85 && plano.TamanhoPt <= fontePtReal * 1.15,
            $"tamanho {plano.TamanhoPt} (real {fontePtReal:F1}); base {plano.BaselineYPt:F2} vs real {baseRealDbg:F2}; caixa OCR {estilo.BaselineYPt:F2}+{estilo.SizePt:F2}");
        // 3b) Linha de base do texto novo = a REAL da linha escaneada (±0,5 pt).
        double baseReal = size.HeightPt - scan.BasePx[tokenEsperado] * pxToPt;
        Assert.True(Math.Abs(plano.BaselineYPt - baseReal) <= 0.5, $"base {plano.BaselineYPt:F2} vs real {baseReal:F2} (caixa OCR {estilo.BaselineYPt:F2})");
        // 4) Tokens vizinhos (mesma linha e linhas coladas acima/abaixo): tinta intacta E texto OCR ainda
        //    pesquisável no PDFium.
        foreach (var (w, tv) in scan.TintaPx.Where(kv => kv.Key != tokenEsperado))
        {
            // Tinta da vizinha (máscara dela, fora da nossa e da caixa do texto novo) que era escura e clareou.
            var mv = scan.Mascaras[w];
            int a = mv.Count(k => !propria.Contains(k) && !NoNovo(k) && Escuro(a200, k));
            int perdidos = mv.Count(k => !propria.Contains(k) && !NoNovo(k) && Escuro(a200, k) && !Escuro(d200, k));
            Assert.True(perdidos <= Math.Max(3, a * 0.02), $"[{plano}] tinta do vizinho '{w}' perdeu {perdidos} de {a} px escuros; areas: {string.Join(" | ", plano.AreasPt ?? new List<PdfQuad>())}; viz: {string.Join(" | ", estilo.NeighbourBoxesPt ?? new List<PdfQuad>())}; perdidos em: {string.Join(" ", mv.Where(k => !propria.Contains(k) && !NoNovo(k) && Escuro(a200, k) && !Escuro(d200, k)).Take(8).Select(k => $"({k % W},{k / W})"))}");
            // LIMITAÇÃO conhecida (rodada 3, fonte estreita): se a Helvetica invisível do vizinho (tamanho =
            // altura da caixa, sem ajuste de largura) passa por cima da tinta da palavra corrigida, os glifos
            // dele que ficam DENTRO da área limpa saem junto (ex.: "pagamento" -> "pagament"); a tinta do
            // vizinho fica intacta (checada acima). Só esse caso é dispensado da checagem do texto OCR.
            double vizL = tv.X * pxToPt, vizR = vizL + editor.MeasureTextWidth(TextFontFamily.SansSerif, false, false, tv.Height * pxToPt, w);
            bool helveticaInvadeArea = vizL < plano.AreaPt.LeftPt && vizR > plano.AreaPt.LeftPt + 1;
            if (!helveticaInvadeArea)
                Assert.True(tp1.Text.Contains(w, StringComparison.Ordinal), $"texto OCR do vizinho '{w}' sumiu: {tp1.Text}");
        }
    }

    private static void RunOnSta(Action scenario)
    {
        Exception? ex = null;
        var th = new Thread(() => { try { scenario(); } catch (Exception e) { ex = e; } });
        th.SetApartmentState(ApartmentState.STA);
        th.IsBackground = true;
        th.Start();
        Assert.True(th.Join(TimeSpan.FromSeconds(30)), "thread STA travou");
        if (ex is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
    }
}
