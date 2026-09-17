namespace mPdf.Rendering;

/// Geometria em PONTOS, origem PDF padrão (inferior-esquerda, y cresce para cima): BottomPt < TopPt.
public readonly record struct PdfCharacter(char Char, double LeftPt, double BottomPt, double RightPt, double TopPt);
