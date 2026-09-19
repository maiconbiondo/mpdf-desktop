using mPdf.Editing;

namespace mPdf.Signing;

/// Costura de rotação do carimbo de assinatura (Plano: carimbo em página girada). O usuário desenha a
/// caixa do carimbo no FRAME EXIBIDO — o quadro que o PDFium (mPdf.Rendering) mostra, JÁ girado por
/// `/Rotate` (PDF 32000-1:2008 §7.7.3.3, "clockwise when displayed"). O iText, porém, posiciona o campo
/// de assinatura (`SignerProperties.SetPageRect`) no frame do MEDIABOX (não-rotacionado). Esta classe é
/// a única fonte de verdade dessa conversão — o resto do módulo/app trabalha sempre em coordenadas
/// EXIBIDAS (`VisibleStampSpec.Rect`), e a conversão acontece só aqui, dentro do motor.
///
/// Derivação (frames y-para-cima, origem no canto inferior-esquerdo de cada frame; MediaBox W×H):
/// rotação de EXIBIÇÃO leva o ponto do MediaBox `(x,y)` ao ponto exibido — 90° horário: `(x,y)->(y, W-x)`;
/// 180°: `(W-x, H-y)`; 270°: `(H-y, x)`. Verificado empiricamente com render/medição por rotação (ver
/// `StampRotationRenderTests`). As fórmulas abaixo são a INVERSA (exibido -> MediaBox), já normalizadas
/// (Left&lt;Right, Bottom&lt;Top) — a entrada é um `PdfQuad` normalizado, e cada rotação preserva a ordem.
internal static class StampRotation
{
    /// Converte um retângulo do frame EXIBIDO para o frame do MEDIABOX. `mediaW`/`mediaH` são as
    /// dimensões do MediaBox (NÃO-rotacionado, como `PdfPage.GetPageSize()` devolve). `rotation` é
    /// normalizado a {0,90,180,270}. rot 0 é identidade (documento comum — comportamento intocado).
    public static PdfQuad DisplayedToMediaBox(PdfQuad d, int rotation, double mediaW, double mediaH)
    {
        int rot = ((rotation % 360) + 360) % 360;
        return rot switch
        {
            90 => new PdfQuad(mediaW - d.TopPt, d.LeftPt, mediaW - d.BottomPt, d.RightPt),
            180 => new PdfQuad(mediaW - d.RightPt, mediaH - d.TopPt, mediaW - d.LeftPt, mediaH - d.BottomPt),
            270 => new PdfQuad(d.BottomPt, mediaH - d.RightPt, d.TopPt, mediaH - d.LeftPt),
            _ => d,
        };
    }

    /// Rotação girou o eixo do carimbo? Em 90/270 o retângulo do MediaBox tem largura/altura TROCADAS em
    /// relação ao retângulo EXIBIDO — a camada de aparência (texto/selo) precisa desenhar nas dimensões
    /// EXIBIDAS (o que o usuário vê), não nas do MediaBox. Em 0/180 os eixos coincidem.
    public static bool SwapsAxes(int rotation) => (((rotation % 360) + 360) % 360) is 90 or 270;
}
