using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace mPdf.Rendering;

/// O cache de reader de renderização é de ESCALA ÚNICA: consumidores que precisam de outra escala
/// simultânea (miniaturas, impressão) devem criar um SEGUNDO PdfDocumentRenderer sobre o mesmo
/// snapshot — o lock global torna isso seguro.
public sealed class PdfDocumentRenderer : IDisposable
{
    private readonly byte[] _pdf;
    private IDocReader? _metrics;           // escala 1.0 -> dimensões em pontos
    private IDocReader? _renderReader;      // cacheado por escala (Task 3)
    private double _renderScale;
    private bool _disposed;

    public int PageCount { get; }

    public PdfDocumentRenderer(byte[] pdf)
    {
        _pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
        lock (PdfRenderLock.Gate)
        {
            try
            {
                // Confirmado via reflexão contra Docnet.Core 2.6.0 (NuGet cache):
                // DocLib.Instance.GetDocReader(byte[], PageDimensions) e PageDimensions(double scalingFactor)
                // existem exatamente como assumido na HIPÓTESE do brief.
                _metrics = DocLib.Instance.GetDocReader(_pdf, new PageDimensions(1.0));
                PageCount = _metrics.GetPageCount();
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                // _metrics pode já ter sido atribuído (GetDocReader ok, GetPageCount falhou) — não vazar o reader nativo.
                _metrics?.Dispose();
                _metrics = null;
                throw new ArgumentException(
                    "Os bytes não são um PDF válido ou o arquivo está protegido por senha.", nameof(pdf), ex);
            }
        }
    }

    public PdfPageSize GetPageSize(int pageIndex)
    {
        lock (PdfRenderLock.Gate)
        {
            // Guarda sob o MESMO lock que usa os handles: worker que passou o gate após Dispose recebe
            // ODE gerenciada (nunca AV nativa). O RenderScheduler engole a ODE por design — aba
            // fechando: página fica em placeholder, custo aceito; o que a guarda proíbe é o crash nativo.
            if (_disposed) throw new ObjectDisposedException(nameof(PdfDocumentRenderer));
            using var page = _metrics!.GetPageReader(pageIndex);
            return new PdfPageSize(page.GetPageWidth(), page.GetPageHeight());
        }
    }

    /// Extrai o texto da página com geometria por caractere, em pontos (escala 1.0 de _metrics).
    public TextPage GetTextPage(int pageIndex)
    {
        lock (PdfRenderLock.Gate)
        {
            // Guarda sob o MESMO lock que usa os handles: worker que passou o gate após Dispose recebe
            // ODE gerenciada (nunca AV nativa). O RenderScheduler engole a ODE por design — aba
            // fechando: página fica em placeholder, custo aceito; o que a guarda proíbe é o crash nativo.
            if (_disposed) throw new ObjectDisposedException(nameof(PdfDocumentRenderer));
            using var page = _metrics!.GetPageReader(pageIndex);
            double heightPt = page.GetPageHeight();

            // SONDA ao vivo (fixture-a4.pdf e fixture-carimbo.pdf, escala 1.0): Docnet.Core.Models.
            // Character.Box (BoundBox: Left/Top/Right/Bottom, int) já vem em PONTOS na escala 1.0 —
            // os valores de Left/Right ficam dentro de 0..GetPageWidth() e Top/Bottom dentro de
            // 0..GetPageHeight(). MAS a origem é TOPO-esquerda (y cresce para baixo): para o texto no
            // topo visual da página (A4, 842pt de altura) os valores observados foram Top≈44-55,
            // Bottom≈53-55 — pequenos e com Top < Bottom, o que só é consistente com origem no topo
            // (se a origem fosse inferior, texto no topo visual teria y≈787-798, não y≈44-55). O
            // CONTRATO exige origem PDF padrão (inferior-esquerda, y cresce para cima, BottomPt <
            // TopPt) — convertida aqui via heightPt - y, preservando Left/Right como estão (já batem
            // com a mesma escala em pontos, sem inversão horizontal).
            // Achado adicional: o espaço (' ') vem com Top==Bottom (caixa de altura zero — não há
            // tinta pra medir); glifos com tinta (mesmo finos, como '-') sempre vieram com altura > 0.
            // TextPage.Characters preserva o espaço mesmo assim (mantém a ordem/índices do PDF intactos
            // para os consumidores das Tasks 3-5); ver TextPageTests.GetTextPage_CharactersHaveSaneGeometry.
            var characters = new List<PdfCharacter>();
            foreach (var c in page.GetCharacters())
            {
                characters.Add(new PdfCharacter(
                    c.Char,
                    c.Box.Left,
                    heightPt - c.Box.Bottom,
                    c.Box.Right,
                    heightPt - c.Box.Top));
            }
            return new TextPage(pageIndex, characters);
        }
    }

    /// Renderiza a página em BGRA opaco (fundo branco). scale 1.0 = 72dpi (1px por ponto).
    public RenderedPage RenderPage(int pageIndex, double scale)
    {
        lock (PdfRenderLock.Gate)
        {
            // Guarda sob o MESMO lock que usa os handles: worker que passou o gate após Dispose recebe
            // ODE gerenciada (nunca AV nativa). O RenderScheduler engole a ODE por design — aba
            // fechando: página fica em placeholder, custo aceito; o que a guarda proíbe é o crash nativo.
            if (_disposed) throw new ObjectDisposedException(nameof(PdfDocumentRenderer));
            // reader de renderização cacheado por escala (recriar a cada página seria re-parsear o doc)
            if (_renderReader is null || Math.Abs(_renderScale - scale) > 0.001)
            {
                _renderReader?.Dispose();
                _renderReader = DocLib.Instance.GetDocReader(_pdf, new PageDimensions(scale));
                _renderScale = scale;
            }
            using var page = _renderReader.GetPageReader(pageIndex);
            int w = page.GetPageWidth();
            int h = page.GetPageHeight();
            // Confirmado via reflexão contra Docnet.Core 2.6.0 (NuGet cache): RenderFlags vive em
            // Docnet.Core.Models (não em Docnet.Core.Converters como cogitado) e RenderAnnotations=1.
            // Sem essa flag o PDFium omite TODAS as anotações (carimbos de assinatura, realces, notas)
            // do bitmap renderizado — usuário via página "em branco" onde deveria ver o carimbo.
            //
            // INVESTIGAÇÃO DE NITIDEZ (Plano 10, Task 2 — usuário comparou mPDF x Foxit lado a lado
            // a 100% de zoom, texto pareceu mais fino/apagado). RenderFlags 2.7.0-alpha.1 expõe 10
            // membros (reflexão ao vivo): None, RenderAnnotations, OptimizeTextForLcd (=FPDF_LCD_TEXT),
            // NoNativeText, Grayscale, LimitImageCacheSize, ForceHalftone, RenderForPrinting,
            // DisableTextAntialiasing/ImageAntialiasing/PathAntialiasing. Testados via harness A/B
            // (crops + métrica de contraste de borda + inspeção visual, ver task-2-report.md):
            //   - OptimizeTextForLcd, Grayscale, ForceHalftone, RenderForPrinting (isolados e
            //     combinados): bytes IDÊNTICOS ao baseline (0 de 2.003.960 bytes diferentes,
            //     verificado por diff direto) em fixture com texto 8-12pt. Decompilação de
            //     Docnet.Core.dll confirma que o int de RenderFlags chega intacto em
            //     FPDF_RenderPageBitmapWithMatrix — não é um bug do wrapper C#; é o binário nativo
            //     pdfium.dll empacotado por este pacote que ignora esses flags (build sem suporte a
            //     subpixel/LCD — hipótese mais provável: FreeType compilado sem
            //     FT_CONFIG_OPTION_SUBPIXEL_RENDERING, comum em builds "headless"). NENHUM flag
            //     testado melhora a nitidez nesta build.
            //   - DisableTextAntialiasing MUDA o resultado (65.244 bytes diferentes) mas para PIOR:
            //     glifos ficam serrilhados/blocudos (prova visual em task-2-report.md) — confirma que
            //     o AA em tons de cinza já está LIGADO por padrão (sem flag nenhuma) e é a escolha
            //     certa; não desligar.
            //   - Causa mecanística real do "fino/apagado" a 8-10pt/escala 1.0: o traço de uma fonte
            //     pequena é sub-pixel (< 1px de largura em 72 "px por ponto") — NENHUM pixel individual
            //     chega a cobertura total, então o traço nunca fica preto puro (medido: valor mais
            //     escuro = 53/255 em texto 8pt; a 12pt já cai pra 2/255). Renderizar nativamente em
            //     1.5x/2x/3x FAZ o traço saturar (medido: 53→3→0→0), PORÉM reamostrar de volta pro
            //     grid final de 1x com um filtro que preserva área (bicúbico de alta qualidade)
            //     reconverge pra quase a MESMA cobertura média por pixel final — matematicamente
            //     esperado (a soma de tinta por pixel final é ~invariante à resolução intermediária).
            //     Comparação lado a lado (crops "COMPOSITE_*" no report) confirma: supersampling 1.5x/
            //     2x reamostrado fica visualmente quase idêntico ao nativo 1x (às vezes até
            //     ligeiramente mais suave/pior — 1.5x teve a MENOR nitidez de borda medida das 4
            //     variantes). CONCLUSÃO: supersampling avaliado e REJEITADO (sem ganho decisivo que
            //     justifique 1.5-2.25x de memória); a lacuna residual contra o Foxit é, com alta
            //     probabilidade, o algoritmo proprietário de stem-darkening/hinting do renderer deles
            //     (escolha de blending não-linear pra realçar traços finos), não uma flag do PDFium
            //     nem falta de resolução — fora do alcance do que RenderFlags do PDFium expõe.
            //     Ver RenderPageTests.RenderPage_SmallText_HasIntermediateGrayAntialiasing (guarda de
            //     regressão pra este AA padrão).
            var raw = page.GetImage(RenderFlags.RenderAnnotations);

            // HIPÓTESE do brief (parcialmente confirmada por sonda ao vivo com fixture-a4.pdf):
            // Docnet 2.6.0 devolve BGRA NÃO pré-multiplicado sobre fundo transparente.
            // - Pixels sem tinta: (B,G,R,A) = (0,0,0,0) -> alfa 0 em 99,78% dos pixels da fixture.
            // - Pixels com tinta: cor original (preto, B=G=R=0) com alfa PARCIAL representando a
            //   cobertura de anti-aliasing (valores observados de 2 a 254; nenhum pixel chega a
            //   alfa=255 nesta fixture). A hipótese do brief — tratar só alfa==0 como caso especial
            //   e deixar o resto como está — deixaria pixels de tinta parcialmente transparentes,
            //   violando a pós-condição "buffer opaco". Por isso a normalização foi generalizada
            //   para compor TODO pixel sobre fundo branco opaco (fórmula "over"), não só alfa==0.
            for (int i = 0; i < raw.Length; i += 4)
            {
                byte a = raw[i + 3];
                if (a == 255) continue; // já opaco: cor final, nada a compor
                int inv = 255 - a;
                raw[i] = (byte)((raw[i] * a + 255 * inv) / 255);
                raw[i + 1] = (byte)((raw[i + 1] * a + 255 * inv) / 255);
                raw[i + 2] = (byte)((raw[i + 2] * a + 255 * inv) / 255);
                raw[i + 3] = 255;
            }

            return new RenderedPage(w, h, raw);
        }
    }

    public void Dispose()
    {
        lock (PdfRenderLock.Gate)
        {
            if (_disposed) return;
            _disposed = true;
            _renderReader?.Dispose();
            _metrics?.Dispose();
            _renderReader = null;
            _metrics = null;
        }
    }
}
