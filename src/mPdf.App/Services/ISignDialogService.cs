using System.Security.Cryptography.X509Certificates;
using mPdf.Signing;

namespace mPdf.App.Services;

/// Resultado do diálogo "Assinar" (Task 3, Plano 4) preenchido pelo usuário — `null` (nunca este tipo)
/// quando o usuário cancelou, ver `ISignDialogService.PromptForSignature`.
/// `ApplyDocMdp`: só pode ser `true` quando `SignDialogRequest.AllowDocMdp` também era `true` (o motor
/// RECUSA `CertificationLevel != None` num documento já assinado — ver `PadesSigningEngine.Sign`); a
/// View (`Views.SignDialog`) já esconde o checkbox nesse caso, mas o CONTRATO desta record não impede
/// um fake de teste montar uma combinação inválida de propósito (útil pra testar que o VM não confia
/// cegamente e monta o `SignRequest` certo mesmo assim).
/// `PlaceStamp`: `true` = "Posicionar carimbo na página" (o VM entra em modo de colocação, clique na
/// página); `false` = "Sem carimbo visível" (assina direto, sem `VisibleStampSpec`).
/// `RubricaBytes` (Plano 22): quando NÃO-null, a aparência do carimbo posicionado é ESSA imagem (a
/// rubrica escolhida na GALERIA do próprio diálogo), não o selo/texto padrão. `null` = carimbo padrão. Só
/// faz sentido com `PlaceStamp = true` (o VM ignora no caminho sem carimbo). A galeria de rubricas é
/// gerida NO diálogo (escolher/adicionar/remover) — não mais em Configurações.
public sealed record SignDialogResult(
    X509Certificate2 Certificate,
    string? Reason,
    string? Location,
    bool ApplyDocMdp,
    bool PlaceStamp,
    byte[]? RubricaBytes = null);

/// Coleta os dados necessários para assinar: certificado (da lista do catálogo — ver
/// `SigningCertificateInfo`), motivo/local opcionais, DocMDP (só quando `allowDocMdp`) e a escolha de
/// carimbo visível. Mesmo padrão de injeção de `IMergeDialogService`/`IAnnotationTextDialogService`:
/// produção abre uma janela WPF real (`Views.SignDialog`), testes injetam um fake que devolve um
/// `SignDialogResult` fixo (ou `null`), sem travar a sessão de teste esperando uma janela real.
public interface ISignDialogService
{
    /// `certificates`: lista COMPLETA do catálogo, incluindo certificados ECC — a View desabilita a
    /// opção com a explicação pt-BR, NUNCA filtra da lista (o usuário precisa VER que o certificado
    /// existe e por que não pode ser usado, não uma lista vazia sem explicação; ver
    /// `CertificateCatalog.SigningCertificateInfo.IsRsa`). `allowDocMdp`: só `true` quando o documento
    /// AINDA não tem nenhuma assinatura (1ª assinatura) — a View só mostra o checkbox DocMDP nesse caso.
    /// `rubricas` (Plano 22): a galeria de rubricas — o diálogo LISTA/CARREGA/ADICIONA/REMOVE direto nela
    /// quando o usuário clica em "Minha rubrica" (escolher uma miniatura ou "+" pra adicionar). `pickRubrica`:
    /// abre o seletor de imagem e VALIDA (PNG/JPG/pixels/CMYK), devolvendo os bytes a adicionar (ou `null`
    /// se cancelado/rejeitado — a produção já notificou). Devolve `null` se o usuário cancelou o diálogo.
    SignDialogResult? PromptForSignature(
        IReadOnlyList<SigningCertificateInfo> certificates, bool allowDocMdp,
        RubricaGallery rubricas, Func<byte[]?> pickRubrica);
}

/// Implementação de produção — abre `Views.SignDialog` como filha da janela principal. Mesmo precedente
/// de `MergeDialogService`/`AnnotationTextDialogService`: nenhuma mediação além de mostrar a janela e
/// ler o resultado.
public sealed class SignDialogService : ISignDialogService
{
    public SignDialogResult? PromptForSignature(
        IReadOnlyList<SigningCertificateInfo> certificates, bool allowDocMdp,
        RubricaGallery rubricas, Func<byte[]?> pickRubrica)
    {
        var dialog = new Views.SignDialog(certificates, allowDocMdp, rubricas, pickRubrica)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
