using iText.Commons.Exceptions;
using iText.Kernel.Colors;
using iText.Kernel.Exceptions;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.PdfCleanup;

namespace mPdf.Editing;

/// Corrigir texto: remoção real (pdfSweep) + escrita do texto novo com fonte padrão-14.
/// Único arquivo que toca o pdfSweep (itext.cleanup) — o App só vê os tipos neutros de Contract.cs.
internal static class TextCorrectionEngine
{
    public static byte[] Apply(byte[] pdf, TextCorrection c)
    {
        ArgumentNullException.ThrowIfNull(c);
        ValidateArea(c.AreaPt);
        if (c.AreasPt is { } areas)
        {
            if (areas.Count == 0) throw new ArgumentException("AreasPt vazia.", "correction");
            foreach (var q in areas) ValidateArea(q);
        }
        if (c.MinOverlapRatio is double r && !(r > 0 && r <= 1))
            throw new ArgumentException("MinOverlapRatio precisa estar entre 0 (exclusive) e 1.", "correction");
        // Falha rápida: recusa caracteres não codificáveis ANTES de rodar o pdfSweep.
        PdfFont? newFont = null;
        if (!string.IsNullOrEmpty(c.NewText))
        {
            newFont = PdfFontFactory.CreateFont(PdfEditor.StandardFontNameFor(FamilyName(c.Family), c.Bold, c.Italic));
            GuardEncodable(newFont, c.NewText);
        }
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try
        {
            using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                PdfEditor.GuardAgainstSignedDocument(doc);
                PdfEditor.ValidatePageIndex(doc, c.PageIndex);
                // Contrato: coordenadas relativas à origem da CropBox (frame exibido, o mesmo do PDFium);
                // iText/pdfSweep trabalham no espaço do usuário -> desloca pela origem da CropBox.
                var (ox, oy) = CropOrigin(doc.GetPage(c.PageIndex + 1));
                var a = c.AreaPt;
                var rect = new Rectangle((float)(a.LeftPt + ox), (float)(a.BottomPt + oy),
                    (float)(a.RightPt - a.LeftPt), (float)(a.TopPt - a.BottomPt));
                var bg = ToDeviceRgb(c.BackgroundArgb);
                // pdfSweep 5.0.7: PdfCleaner.CleanUp(PdfDocument, IList<PdfCleanUpLocation>, CleanUpProperties)
                // — remove glifos (visíveis ou não) e pixels de imagem da área; a área é pintada com `bg`.
                // SetProcessAnnotations(false): sem isso o pdfSweep APAGA anotações que tocam a área
                // (comentários, caixas, carimbos do usuário) — a correção só mexe no conteúdo da página.
                // MinOverlapRatio (só a camada OCR): glifo só sai se ficar >= essa fração dentro da área —
                // a descida da Helvetica invisível da linha vizinha só "encosta" e sobrevive. null = padrão
                // do pdfSweep (qualquer interseção remove), caminho digital inalterado.
                var props = new CleanUpProperties().SetProcessAnnotations(false);
                if (c.MinOverlapRatio is double ratio) props.SetOverlapRatio(ratio);
                var locais = (c.AreasPt ?? new[] { c.AreaPt })
                    .Select(q => new PdfCleanUpLocation(c.PageIndex + 1, new Rectangle((float)(q.LeftPt + ox), (float)(q.BottomPt + oy),
                        (float)(q.RightPt - q.LeftPt), (float)(q.TopPt - q.BottomPt)), bg))
                    .ToList();
                PdfCleaner.CleanUp(doc, locais, props);

                if (!string.IsNullOrEmpty(c.NewText))
                {
                    var page = doc.GetPage(c.PageIndex + 1);
                    var font = newFont!;
                    // wrapOldContent=true: o conteúdo original fica entre q/Q, então um `cm`/`Tr`/`Tc`/
                    // `Tz`/`Ts` que vaze dele (ex.: o `3 Tr` invisível da camada OCR) não atinge o texto
                    // novo. O estado de texto ainda é fixado explicitamente (defesa em profundidade).
                    var canvas = new PdfCanvas(page, true);
                    canvas.SaveState()
                          .SetFillColor(ToDeviceRgb(c.ColorArgb))
                          .BeginText().SetFontAndSize(font, (float)c.SizePt)
                          .SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.FILL)
                          .SetCharacterSpacing(0).SetWordSpacing(0).SetHorizontalScaling(100).SetTextRise(0)
                          .MoveText(c.LeftXPt + ox, c.BaselineYPt + oy)
                          .ShowText(c.NewText)
                          .EndText().RestoreState();
                }
            }
        }
        catch (BadPasswordException ex) { throw PdfEditor.WrapPassword(ex); }
        catch (ITextException ex) { throw PdfEditor.WrapGeneric(ex); }
        // Caminho de imagem do pdfSweep usa System.Drawing (GDI+): falhas de plataforma não são
        // ITextException — mesma mensagem honesta de WrapGeneric.
        catch (System.Runtime.InteropServices.ExternalException ex) { throw new PdfEditingException("Não foi possível processar o PDF.", ex); }
        catch (PlatformNotSupportedException ex) { throw new PdfEditingException("Não foi possível processar o PDF.", ex); }
        return output.ToArray();
    }

    /// Origem (canto inferior-esquerdo) da CropBox no espaço do usuário. Sem /CropBox o iText devolve
    /// a MediaBox — mesmo critério do PDFium para o frame exibido.
    /// Origem do frame exibido (CropBox ∩ MediaBox) — ver `PageFrame`.
    private static (double X, double Y) CropOrigin(PdfPage page) => PageFrame.DisplayedOrigin(page);

    private static void ValidateArea(PdfQuad a)
    {
        bool finite = double.IsFinite(a.LeftPt) && double.IsFinite(a.BottomPt)
                      && double.IsFinite(a.RightPt) && double.IsFinite(a.TopPt);
        if (!finite || a.RightPt <= a.LeftPt || a.TopPt <= a.BottomPt)
            throw new ArgumentException(
                "Área de correção inválida: coordenadas precisam ser finitas, com direita > esquerda e topo > base.",
                "correction");
    }

    /// Fonte padrão-14 (WinAnsi): caractere fora da codificação seria descartado em silêncio pelo
    /// ShowText — recusa antes de escrever, listando os caracteres.
    private static void GuardEncodable(PdfFont font, string text)
    {
        var bad = new List<string>();
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            // Surrogate solto não é texto válido: entra na lista como U+FFFD em vez de lançar crú.
            int cp = char.IsSurrogate(text, i) && !char.IsSurrogatePair(text, i) ? 0xFFFD : char.ConvertToUtf32(text, i);
            if (!font.ContainsGlyph(cp))
            {
                string s = char.ConvertFromUtf32(cp);
                if (!bad.Contains(s)) bad.Add(s);
            }
        }
        if (bad.Count > 0)
            throw new PdfEditingException($"O texto novo tem caracteres que esta fonte não suporta: {string.Concat(bad)}.");
    }

    /// Rótulo aceito por PdfEditor.NormalizeFamily: "serif"->Times, "mono"->Courier, qualquer outro ->
    /// Helvetica (comparação em minúsculas).
    internal static string FamilyName(TextFontFamily f) => f switch
    {
        TextFontFamily.Serif => "Serif",
        TextFontFamily.Monospace => "Mono",
        _ => "Sans",
    };

    internal static DeviceRgb ToDeviceRgb(uint argb) =>
        new((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));

    internal static (TextFontFamily Family, bool Bold, bool Italic) ClassifyFontName(string baseFont)
    {
        string n = baseFont.Contains('+') ? baseFont[(baseFont.IndexOf('+') + 1)..] : baseFont;
        string l = n.ToLowerInvariant();
        var fam = l.Contains("courier") || l.Contains("mono") || l.Contains("consolas") ? TextFontFamily.Monospace
                : l.Contains("times") || l.Contains("roman") || l.Contains("serif") && !l.Contains("sans")
                  || l.Contains("georgia") || l.Contains("garamond") || l.Contains("cambria") ? TextFontFamily.Serif
                : TextFontFamily.SansSerif;
        bool bold = l.Contains("bold") || l.Contains("black") || l.Contains("heavy") || l.Contains("semibold");
        bool italic = l.Contains("italic") || l.Contains("oblique");
        return (fam, bold, italic);
    }

    public static double Measure(TextFontFamily family, bool bold, bool italic, double sizePt, string text)
    {
        var font = PdfFontFactory.CreateFont(PdfEditor.StandardFontNameFor(FamilyName(family), bold, italic));
        return font.GetWidth(text ?? string.Empty, (float)sizePt);
    }

    public static TextStyleProbe? Probe(byte[] pdf, int pageIndex, PdfQuad areaPt)
    {
        ProbeListener listener;
        try
        {
            using var input = new MemoryStream(pdf);
            using var doc = new PdfDocument(new PdfReader(input));
            PdfEditor.ValidatePageIndex(doc, pageIndex);
            var page = doc.GetPage(pageIndex + 1);
            var (ox, oy) = CropOrigin(page);
            // Área chega no frame da CropBox; o listener compara no espaço do usuário e o resultado
            // volta para o frame da CropBox (ver Contract.cs).
            listener = new ProbeListener(new PdfQuad(areaPt.LeftPt + ox, areaPt.BottomPt + oy,
                areaPt.RightPt + ox, areaPt.TopPt + oy), ox, oy);
            new PdfCanvasProcessor(listener).ProcessPageContent(page);
        }
        catch (BadPasswordException ex) { throw PdfEditor.WrapPassword(ex); }
        catch (ITextException ex) { throw PdfEditor.WrapGeneric(ex); }
        return listener.Result(); // só valores simples: não toca o documento já fechado
    }

    private sealed record Hit(string Text, double X0, double X1, double Y, double Size,
        string FontName, uint Argb, bool Invisible);

    private sealed class ProbeListener : IEventListener
    {
        private readonly PdfQuad _a;
        private readonly double _ox, _oy;
        private readonly List<Hit> _hits = new();
        /// `a` no espaço do usuário; (ox, oy) = origem da CropBox, subtraída do resultado.
        public ProbeListener(PdfQuad a, double ox, double oy) { _a = a; _ox = ox; _oy = oy; }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo tri) return;
            // O evento vem por operador de texto (linha inteira ou, na camada OCR, um token): filtra por glifo.
            var chars = tri.GetCharacterRenderInfos().ToList();
            RegistrarTokensInvisiveis(chars);
            var dentro = new List<int>();
            for (int i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                var b = ch.GetBaseline();
                var s = b.GetStartPoint(); var e = b.GetEndPoint();
                double mx = (s.Get(0) + e.Get(0)) / 2, my = (s.Get(1) + e.Get(1)) / 2;
                if (mx < _a.LeftPt - 1 || mx > _a.RightPt + 1 || my < _a.BottomPt - 1 || my > _a.TopPt + 1) continue;
                // Tamanho efetivo = fonte x comprimento do vetor vertical (c,d) de TextMatrix x CTM.
                var m = ch.GetTextMatrix().Multiply(ch.GetGraphicsState().GetCtm());
                double size = ch.GetFontSize() * Math.Sqrt(m.Get(3) * m.Get(3) + m.Get(4) * m.Get(4));
                _hits.Add(new Hit(ch.GetText(), s.Get(0), e.Get(0), s.Get(1), size,
                    ch.GetFont().GetFontProgram().GetFontNames().GetFontName() ?? "",
                    ToArgb(ch.GetFillColor()), ch.GetTextRenderMode() == 3));
                // Token OCR: só conta glifo com o ponto médio ESTRITAMENTE dentro da área (sem a tolerância de
                // 1 pt acima) — na horizontal, a Helvetica invisível de um token vizinho que "vaza" para a seleção
                // (fonte escaneada estreita) não pode puxar o vizinho; na vertical, vale o CENTRO da altura do
                // glifo (entre as linhas de descida e subida), não a base — com linhas coladas e acento embaixo
                // (rodada 4) a área sobe até a BASE da linha de cima, mas o miolo dos glifos dela fica fora.
                double cy = (ch.GetAscentLine().GetStartPoint().Get(1) + ch.GetDescentLine().GetStartPoint().Get(1)) / 2;
                bool estrito = mx > _a.LeftPt && mx < _a.RightPt && cy > _a.BottomPt && cy < _a.TopPt;
                if (estrito && ch.GetTextRenderMode() == 3 && !string.IsNullOrWhiteSpace(ch.GetText())) dentro.Add(i);
            }
            // Camada OCR (invisível): cada glifo tocado puxa o TOKEN inteiro do mesmo operador de texto
            // (sequência sem espaço — pontuação colada inclusa). Funciona tanto para OCR que grava 1 token
            // por Tj (mPDF) quanto para OCR que grava a linha inteira num Tj (o espaço separa os tokens).
            bool Branco(int i) => string.IsNullOrWhiteSpace(chars[i].GetText());
            int ultimoFim = -1;
            foreach (int i in dentro)
            {
                if (i <= ultimoFim) continue;
                int a = i, z = i;
                while (a > 0 && !Branco(a - 1)) a--;
                while (z < chars.Count - 1 && !Branco(z + 1)) z++;
                ultimoFim = z;
                double y0 = double.MaxValue, y1 = double.MinValue;
                for (int k = a; k <= z; k++)
                {
                    y0 = Math.Min(y0, chars[k].GetDescentLine().GetStartPoint().Get(1));
                    y1 = Math.Max(y1, chars[k].GetAscentLine().GetStartPoint().Get(1));
                }
                int total = 0, tocados = 0;
                for (int k = a; k <= z; k++) if (!Branco(k)) { total++; if (dentro.Contains(k)) tocados++; }
                var mt = chars[a].GetTextMatrix().Multiply(chars[a].GetGraphicsState().GetCtm());
                double tam = chars[a].GetFontSize() * Math.Sqrt(mt.Get(3) * mt.Get(3) + mt.Get(4) * mt.Get(4));
                _tokens.Add(new Token(string.Concat(chars.Skip(a).Take(z - a + 1).Select(c => c.GetText())),
                    chars[a].GetBaseline().GetStartPoint().Get(0), chars[z].GetBaseline().GetEndPoint().Get(0),
                    chars[a].GetBaseline().GetStartPoint().Get(1), y0, y1, tam, tocados, total));
            }
        }

        /// Geometria de TODO token invisível (camada OCR) da página — a caixa OCR do token é
        /// [X0, ?] x [Y, Y + Size] (base no fundo da caixa, tamanho = altura); X1 é o fim da Helvetica invisível
        /// (não a borda direita da caixa, que o PDF não guarda).
        private sealed record Geo(double X0, double X1, double Y, double Size);
        private readonly List<Geo> _todos = new();

        private void RegistrarTokensInvisiveis(List<TextRenderInfo> chars)
        {
            int a = -1;
            for (int i = 0; i <= chars.Count; i++)
            {
                bool fim = i == chars.Count || chars[i].GetTextRenderMode() != 3 || string.IsNullOrWhiteSpace(chars[i].GetText());
                if (!fim) { if (a < 0) a = i; continue; }
                if (a < 0) continue;
                var m = chars[a].GetTextMatrix().Multiply(chars[a].GetGraphicsState().GetCtm());
                double tam = chars[a].GetFontSize() * Math.Sqrt(m.Get(3) * m.Get(3) + m.Get(4) * m.Get(4));
                _todos.Add(new Geo(chars[a].GetBaseline().GetStartPoint().Get(0), chars[i - 1].GetBaseline().GetEndPoint().Get(0),
                    chars[a].GetBaseline().GetStartPoint().Get(1), tam));
                a = -1;
            }
        }

        /// Mesma linha = as faixas verticais [Y, Y+Size] se sobrepõem em pelo menos metade da menor.
        private static bool MesmaLinha(double y1, double s1, double y2, double s2) =>
            Math.Min(y1 + s1, y2 + s2) - Math.Max(y1, y2) >= 0.5 * Math.Min(s1, s2);

        /// Borda direita da caixa OCR de `g` até onde se pode afirmar: o início do próximo token da MESMA linha
        /// (a caixa dele começa na tinta dele); sem próximo, o fim da Helvetica invisível + 0,3 x tamanho.
        private double DireitaDaCaixa(Geo g)
        {
            var prox = _todos.Where(u => u.X0 > g.X0 + 0.1 && MesmaLinha(u.Y, u.Size, g.Y, g.Size)).Select(u => u.X0).DefaultIfEmpty(double.NaN).Min();
            return double.IsNaN(prox) ? Math.Max(g.X1, g.X0) + 0.3 * g.Size : prox;
        }

        /// `Tocados` = glifos do token com o ponto médio estritamente dentro da área; `Total` = glifos do token.
        private sealed record Token(string Text, double X0, double X1, double Y, double Bottom, double Top,
            double Size, int Tocados, int Total);
        private readonly List<Token> _tokens = new();

        public ICollection<EventType> GetSupportedEvents() => new HashSet<EventType> { EventType.RENDER_TEXT };

        public TextStyleProbe? Result()
        {
            if (_hits.Count == 0) return null;
            var first = _hits.OrderBy(h => h.X0).First();
            // Só a linha do acerto mais à esquerda (várias linhas podem tocar a área).
            var line = _hits.Where(h => Math.Abs(h.Y - first.Y) <= 0.5 * first.Size).OrderBy(h => h.X0).ToList();
            var (fam, bold, italic) = ClassifyFontName(first.FontName);
            string? tokenText = null; PdfQuad? tokenArea = null;
            double? proximo = null; List<PdfQuad>? vizinhos = null;
            double size = first.Size, baseY = first.Y, leftX = line.Min(h => h.X0);
            // Tokens puxados (camada OCR): os que têm metade ou mais dos PRÓPRIOS glifos dentro da área
            // (seleção parcial de uma palavra) ou MAIS da metade de todos os glifos tocados (seleção de 1-2
            // letras) — um vizinho que só vaza 1-2 glifos para a seleção fica de fora.
            int tocadosTotal = _tokens.Sum(t => t.Tocados);
            var toks = first.Invisible
                ? _tokens.Where(t => t.Tocados > 0 && (2 * t.Tocados >= t.Total || 2 * t.Tocados > tocadosTotal)).OrderBy(t => t.X0).ToList()
                : new List<Token>();
            if (toks.Count > 0)
            {
                // Mesma linha: a que tem MAIS glifos tocados (rodada 4) — com linhas coladas e acento na linha
                // de baixo, a área de "MÚSICA" sobe até a base da linha de cima e puxaria "ação" (mais à
                // esquerda) como se fosse a linha certa.
                var linhaDe = toks.Select(t => toks.Where(u => Math.Abs(u.Y - t.Y) <= 0.5 * Math.Max(t.Size, u.Size)).ToList());
                toks = linhaDe.OrderByDescending(g => g.Sum(u => u.Tocados)).ThenBy(g => g.Min(u => u.X0)).First()
                              .OrderBy(t => t.X0).ToList();
                // Rodada 6: a Helvetica invisível de um token com caixa OCR alta (acentos + cedilha) e fonte
                // estreita passa por cima do token seguinte, e a seleção (caixas do PDFium dessa Helvetica)
                // "toca" mais da metade dele. A partir do token com mais glifos tocados, um token à DIREITA só
                // entra se a seleção passa do FIM da Helvetica do anterior (o usuário arrastou até ele de
                // propósito) e um à ESQUERDA só se a seleção começa antes do início (caixa OCR) do seguinte.
                // (1 pt de folga: o Docnet arredonda as caixas para inteiro.)
                int ancora = toks.IndexOf(toks.OrderByDescending(t => t.Tocados).ThenBy(t => t.X0).First());
                int fim = ancora, ini = ancora;
                while (fim + 1 < toks.Count && _a.RightPt > toks[fim].X1 + 1) fim++;
                while (ini - 1 >= 0 && _a.LeftPt < toks[ini].X0 - 1) ini--;
                toks = toks.GetRange(ini, fim - ini + 1);
                var t0 = toks[0];
                tokenText = string.Join(" ", toks.Select(t => t.Text));
                tokenArea = new PdfQuad(toks.Min(t => t.X0) - _ox, toks.Min(t => t.Bottom) - _oy,
                    toks.Max(t => t.X1) - _ox, toks.Max(t => t.Top) - _oy);
                // Base/tamanho = união das caixas OCR dos tokens (base no fundo da caixa, tamanho = altura),
                // não os de um glifo vizinho que vazou para a área.
                baseY = toks.Min(t => t.Y);
                size = toks.Max(t => t.Y + t.Size) - baseY;
                leftX = t0.X0;
                // Rodada 5: geometria da camada OCR em volta — o início do PRÓXIMO token da mesma linha (borda
                // direita estrutural da palavra: a caixa dele começa na tinta dele) e as caixas OCR dos tokens
                // das linhas vizinhas que tocam a faixa vertical da palavra (área que NÃO pode ser limpa).
                double ultimo = toks.Max(t => t.X0), by = baseY, bs = size;
                var prox = _todos.Where(u => u.X0 > ultimo + 0.1 && MesmaLinha(u.Y, u.Size, by, bs)).Select(u => u.X0).DefaultIfEmpty(double.NaN).Min();
                if (!double.IsNaN(prox)) proximo = prox - _ox;
                double esq = toks.Min(t => t.X0), dir = double.IsNaN(prox) ? toks.Max(t => t.X1) + 0.5 * bs : prox;
                vizinhos = _todos
                    .Where(u => !MesmaLinha(u.Y, u.Size, by, bs) && u.Y < by + 1.5 * bs && u.Y + u.Size > by - 0.5 * bs)
                    .Select(u => new PdfQuad(u.X0 - _ox, u.Y - _oy, DireitaDaCaixa(u) - _ox, u.Y + u.Size - _oy))
                    .Where(q => q.RightPt > esq - _ox - bs && q.LeftPt < dir - _ox + bs)
                    .ToList();
            }
            return new TextStyleProbe(string.Concat(line.Select(h => h.Text)), fam, bold, italic, size,
                first.Argb, baseY - _oy, leftX - _ox, line.Max(h => h.X1) - _ox, first.Invisible,
                tokenText, tokenArea, proximo, vizinhos);
        }

        // DeviceRgb/DeviceGray/DeviceCmyk convertidos; outros espaços de cor (ICC, Lab, padrão...)
        // caem em preto opaco.
        private static uint ToArgb(Color? c)
        {
            float[] v = c?.GetColorValue() ?? new float[] { 0f };
            int R(double f) => (int)Math.Round(Math.Clamp(f, 0.0, 1.0) * 255);
            uint Pack(int r, int g, int b) => 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
            return c switch
            {
                DeviceRgb when v.Length >= 3 => Pack(R(v[0]), R(v[1]), R(v[2])),
                DeviceGray when v.Length >= 1 => Pack(R(v[0]), R(v[0]), R(v[0])),
                DeviceCmyk when v.Length >= 4 => Pack(R((1 - v[0]) * (1 - v[3])), R((1 - v[1]) * (1 - v[3])), R((1 - v[2]) * (1 - v[3]))),
                _ => 0xFF000000u,
            };
        }
    }
}
