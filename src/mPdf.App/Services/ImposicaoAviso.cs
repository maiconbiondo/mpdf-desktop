using mPdf.Editing;

namespace mPdf.App.Services;

/// Aviso do "Salvar PDF" da impressão avançada (revisão final das páginas giradas, defeito antigo P2):
/// `IPdfEditor.ImporEmFolhas` copia cada página com `CopyAsFormXObject`, que leva só o CONTEÚDO — anotações, campos
/// de formulário (inclusive os valores preenchidos) e assinaturas (inclusive o carimbo visível) não vão para o PDF
/// imposto. A impressão leva (o PDFium desenha anotações e widgets na folha). Até o PDF imposto levar esse conteúdo,
/// o diálogo avisa quando o documento tem algum.
///
/// Revisão F2 (A9): a detecção era `ReadAnnotations().Count > 0 || HasSignatures`, mas `ReadAnnotations` exclui os
/// widgets e todo subtype que não mapeia (Circle, Squiggly, Polygon, Caret, FileAttachment...) — um formulário
/// preenchido ou um círculo do Acrobat sumiam sem aviso. Agora pergunta ao motor (`TemConteudoInterativo`: qualquer
/// /Annots exceto Link/Popup, ou assinatura) — e o diálogo faz isso FORA da UI thread (`ParaAsync`), porque o parse
/// é do documento inteiro (o revisor mediu 134 ms com 3000 páginas).
public static class ImposicaoAviso
{
    public const string Texto = "Atenção: anotações, campos de formulário e assinaturas não vão para o PDF gerado em \"Salvar PDF\" (a impressão leva).";

    /// `Texto` se o documento tem conteúdo interativo (anotação de qualquer tipo, exceto link/popup; campo de
    /// formulário; assinatura); `null` se não tem (ou se não deu para ler — o aviso é informativo, nunca impede o
    /// diálogo de abrir).
    public static string? Para(IPdfEditor editor, byte[] pdf)
    {
        try { return editor.TemConteudoInterativo(pdf) ? Texto : null; }
        catch { return null; }
    }

    /// `Para` fora da UI thread.
    public static Task<string?> ParaAsync(IPdfEditor editor, byte[] pdf) => Task.Run(() => Para(editor, pdf));
}
