using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.App.Services;

/// Corrigir texto (2026-10-09): o que a janela recebe pré-preenchido (estilo detectado no trecho).
public sealed record CorrigirTextoPedido(string TextoAtual, TextFontFamily Familia, bool Negrito, bool Italico, double TamanhoPt, uint CorArgb);

/// O que o usuário confirmou na janela.
public sealed record CorrigirTextoResultado(string TextoNovo, TextFontFamily Familia, bool Negrito, bool Italico, double TamanhoPt, uint CorArgb);

/// Prévia da correção: recorte renderizado (null = não foi possível gerar) + aviso não bloqueante.
public sealed record CorrigirTextoPrevia(RenderedPage? Recorte, string? Aviso);

/// Seam da janela "Corrigir texto" (roteada por `UiPrompts.CreateCorrigirTextoDialog`).
public interface ICorrigirTextoDialogService
{
    /// Abre a janela; `previa` é chamada FORA da UI thread (pela janela) a cada mudança. null = cancelou.
    CorrigirTextoResultado? Pedir(CorrigirTextoPedido pedido, Func<CorrigirTextoResultado, CorrigirTextoPrevia> previa);
}

/// Implementação de produção — abre `Views.CorrigirTextoDialog` (modal, pt-BR) como filha da janela
/// principal. Mesmo precedente de `AnnotationTextDialogService`: só mostra a janela e lê o resultado.
public sealed class CorrigirTextoDialogService : ICorrigirTextoDialogService
{
    public CorrigirTextoResultado? Pedir(CorrigirTextoPedido pedido, Func<CorrigirTextoResultado, CorrigirTextoPrevia> previa)
    {
        var dialog = new Views.CorrigirTextoDialog(pedido, previa)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.Resultado : null;
    }
}
