using System.Windows;

namespace mPdf.App.Services;

/// <summary>
/// Task 0 (Plano 3c) — seam ESTÁTICO central pra todos os defaults de produção de diálogo/notificação
/// usados pelos construtores de <see cref="mPdf.App.ViewModels.DocumentViewModel"/>,
/// <see cref="mPdf.App.ViewModels.OrganizerViewModel"/> e <see cref="mPdf.App.ViewModels.MainViewModel"/>
/// quando o chamador OMITE o parâmetro opcional correspondente (`?? algumDefault`).
///
/// A DÍVIDA (registrada na revisão final do 3b): 3 vezes um teste xunit headless construiu um desses
/// VMs sem injetar um fake de diálogo, um caminho de código novo chamou o default de produção (um
/// `MessageBox.Show`/`OpenFileDialog` de verdade), e a suíte inteira TRAVOU esperando um clique que
/// nunca vem — até o timeout do CI, sem mensagem de erro nenhuma apontando pra causa.
///
/// PRODUÇÃO: cada propriedade abaixo é inicializada com a implementação REAL (mesmo texto/ícone/janela
/// que cada VM já mostrava antes desta seam existir — comportamento byte-a-byte preservado, só o LOCAL
/// de onde o default vem mudou). NENHUM `[ModuleInitializer]` existe neste assembly de produção — só o
/// assembly de TESTE (`mPdf.App.Tests`, via `UiPromptsTestGuard`) troca essas propriedades por versões
/// que LANÇAM `InvalidOperationException` nomeando o fake a injetar, convertendo um hang silencioso em
/// falha imediata e diagnosticável. As propriedades são `set`táveis de propósito: é exatamente essa
/// mutabilidade que permite tanto o `[ModuleInitializer]` de teste (troca global, 1x por assembly)
/// quanto um teste individual restaurar/trocar um valor localmente (ex.: prova de disparo, controle
/// negativo — ver `UiPromptsGuardTests`).
///
/// COBERTURA (mapeada grep-a-grep nos 3 ctors, revisão final 3b): `NotifyInfo` é o ÚNICO default
/// idêntico nos 3 VMs (mesmo texto/ícone) — unificado numa propriedade só. `NotifyError` tem DOIS textos
/// DIFERENTES (`MainViewModel` prefixa "Não foi possível abrir o arquivo:", `DocumentViewModel` não) —
/// mantidos SEPARADOS (`MainNotifyError`/`DocumentNotifyError`) pra preservar o texto exato de cada um.
/// `OrganizerViewModel` não tem default de NotifyError (parâmetro obrigatório, sem risco de hang aqui).
/// Os 4 diálogos de serviço (arquivo/anotação/juntar/dividir) + o prompt de fechar sujo
/// (`IConfirmCloseService`, default usado pelos overloads de conveniência de 2/3 argumentos de
/// `MainViewModel` — não é um PARÂMETRO opcional, é um argumento fixo encadeado, mas é a MESMA classe de
/// risco) + o prompt de achatar formulário (`IConfirmFlattenService`, Task 3 do Plano 3c — parâmetro
/// opcional `confirmFlatten` de `DocumentViewModel`, mesma classe de risco) + o prompt de escala do
/// organizador (`IConfirmOrganizerScaleService`, Task 1 do Plano 5 — parâmetro opcional
/// `confirmOrganizerScale` de `DocumentViewModel`, mesma classe de risco) + o diálogo "Exportar página
/// como imagem" (`IExportImageDialogService`, Task 4 do Plano 7 — parâmetro opcional `exportImageDialog`
/// de `DocumentViewModel`, mesma classe de risco, mesma FORMA de `IBatchSignDialogService`: hospeda um VM
/// já construído) completam a lista.
/// `StampGallery`/`AppConfig`/`IPdfEditor`/`RecentFilesStore` NÃO entram aqui: nenhum mostra UI (só tocam
/// disco/lógica), documentado como isenção deliberada em `UiPromptsCoverageTests`.
/// </summary>
public static class UiPrompts
{
    /// <summary>Canal de SUCESSO (ícone Information) — texto idêntico nos 3 VMs.</summary>
    public static Action<string> NotifyInfo { get; set; } = ProductionNotifyInfo;

    /// <summary>Canal de ERRO de <see cref="mPdf.App.ViewModels.MainViewModel"/> — prefixa "Não foi
    /// possível abrir o arquivo:" (usado no contexto de abrir arquivo/recente).</summary>
    public static Action<string> MainNotifyError { get; set; } = ProductionMainNotifyError;

    /// <summary>Canal de ERRO de <see cref="mPdf.App.ViewModels.DocumentViewModel"/> — mensagem crua,
    /// sem prefixo (usado em vários contextos: edição bloqueada, anotação inválida, etc.).</summary>
    public static Action<string> DocumentNotifyError { get; set; } = ProductionDocumentNotifyError;

    /// <summary>Diálogo "Abrir"/"Salvar como"/"Escolher imagem" (Win32 nativo via
    /// <see cref="FileDialogService"/>).</summary>
    public static Func<IFileDialogService> CreateFileDialog { get; set; } = () => new FileDialogService();

    /// <summary>Janela "nota adesiva/caixa de texto" (<see cref="AnnotationTextDialogService"/>).</summary>
    public static Func<IAnnotationTextDialogService> CreateAnnotationDialog { get; set; } =
        () => new AnnotationTextDialogService();

    /// <summary>Janela "Juntar documentos" (<see cref="MergeDialogService"/>).</summary>
    public static Func<IMergeDialogService> CreateMergeDialog { get; set; } = () => new MergeDialogService();

    /// <summary>Janela "Dividir documento" (<see cref="SplitDialogService"/>).</summary>
    public static Func<ISplitDialogService> CreateSplitDialog { get; set; } = () => new SplitDialogService();

    /// <summary>Prompt "salvar antes de fechar?" (<see cref="MessageBoxConfirmCloseService"/>) — default
    /// usado pelos overloads de conveniência de 2/3 argumentos de `MainViewModel`.</summary>
    public static Func<IConfirmCloseService> CreateConfirmClose { get; set; } =
        () => new MessageBoxConfirmCloseService();

    /// <summary>Prompt "achatar formulário?" (Task 3, Plano 3c — <see cref="MessageBoxConfirmFlattenService"/>)
    /// — default do parâmetro `confirmFlatten` de `DocumentViewModel`.</summary>
    public static Func<IConfirmFlattenService> CreateConfirmFlatten { get; set; } =
        () => new MessageBoxConfirmFlattenService();

    /// <summary>Prompt "salvar antes de assinar?" (Task 3, Plano 4 —
    /// <see cref="MessageBoxConfirmSaveBeforeSignService"/>) — default do parâmetro
    /// `confirmSaveBeforeSign` de `DocumentViewModel`.</summary>
    public static Func<IConfirmSaveBeforeSignService> CreateConfirmSaveBeforeSign { get; set; } =
        () => new MessageBoxConfirmSaveBeforeSignService();

    /// <summary>Janela "Assinar" (Task 3, Plano 4 — <see cref="SignDialogService"/>) — default do
    /// parâmetro `signDialog` de `DocumentViewModel`.</summary>
    public static Func<ISignDialogService> CreateSignDialog { get; set; } = () => new SignDialogService();

    /// <summary>Janela "Assinar em lote" (Task 5, Plano 4 — <see cref="BatchSignDialogService"/>) —
    /// default do parâmetro `batchSignDialog` de `MainViewModel`.</summary>
    public static Func<IBatchSignDialogService> CreateBatchSignDialog { get; set; } = () => new BatchSignDialogService();

    /// <summary>Prompt "documento grande, o organizador pode demorar?" (Task 1, Plano 5 —
    /// <see cref="MessageBoxConfirmOrganizerScaleService"/>) — default do parâmetro
    /// `confirmOrganizerScale` de `DocumentViewModel`.</summary>
    public static Func<IConfirmOrganizerScaleService> CreateConfirmOrganizerScale { get; set; } =
        () => new MessageBoxConfirmOrganizerScaleService();

    /// <summary>Janela "Exportar página como imagem" (Task 4, Plano 7 —
    /// <see cref="ExportImageDialogService"/>) — default do parâmetro `exportImageDialog` de
    /// `DocumentViewModel`.</summary>
    public static Func<IExportImageDialogService> CreateExportImageDialog { get; set; } = () => new ExportImageDialogService();

    /// <summary>Janela "Exportar como Word/Excel" (Task 3, Plano 16 —
    /// <see cref="ExportDocumentDialogService"/>) — default do parâmetro `exportDocumentDialog` de
    /// `DocumentViewModel`. Mesma classe de risco/forma de `CreateExportImageDialog`: hospeda um VM já
    /// construído e abre uma `Window` real.</summary>
    public static Func<IExportDocumentDialogService> CreateExportDocumentDialog { get; set; } = () => new ExportDocumentDialogService();

    /// <summary>Janela "Sobre" (Task 2, Plano 11 — <see cref="SobreDialogService"/>) — default do
    /// parâmetro `sobreDialog` de `MainViewModel`.</summary>
    public static Func<ISobreDialogService> CreateSobreDialog { get; set; } = () => new SobreDialogService();

    /// <summary>Fonte de dados de atualização (Task 2, Plano 11 — <see cref="GitHubUpdateSource"/>, a
    /// ÚNICA implementação que bate na rede de verdade) — default do parâmetro `createSource` de
    /// `SobreViewModel`. DIFERENTE dos demais membros desta classe (que produzem um SERVIÇO DE DIÁLOGO):
    /// este produz a fonte de RESULTADO de rede consumida por `UpdateService.VerificarAsync` — mesma
    /// classe de risco que um `MessageBox`/`OpenFileDialog` real (uma chamada de rede não mockada numa
    /// suíte supostamente hermética é tão indesejável quanto travar num diálogo), por isso a MESMA
    /// disciplina: `UiPromptsTestGuard` troca por uma versão que LANÇA.</summary>
    public static Func<IUpdateSource> CreateUpdateSource { get; set; } = () => new GitHubUpdateSource();

    /// <summary>Prompt "Fechar o mPDF e instalar a atualização agora?" (Task 2, Plano 11 —
    /// <see cref="MessageBoxConfirmInstallUpdateService"/>) — default do parâmetro `confirmInstall` de
    /// `SobreViewModel`.</summary>
    public static Func<IConfirmInstallUpdateService> CreateConfirmInstallUpdate { get; set; } =
        () => new MessageBoxConfirmInstallUpdateService();

    /// <summary>Faixa/diálogo de progresso do OCR (Task 4, Plano 15 —
    /// <see cref="OcrProgressDialogService"/>) — default do parâmetro `ocrProgress` de
    /// `DocumentViewModel`. Mesma classe de risco dos demais diálogos: abre uma `Window` real (só que
    /// MODELESS), que um teste headless não pode alcançar sem travar/estourar fora de uma thread STA.</summary>
    public static Func<IOcrProgressService> CreateOcrProgress { get; set; } = () => new OcrProgressDialogService();

    // ---- Implementações de PRODUÇÃO (texto/ícone preservados byte-a-byte dos VMs originais) ---------

    private static void ProductionNotifyInfo(string message) =>
        MessageBox.Show(message, "mPDF", MessageBoxButton.OK, MessageBoxImage.Information);

    private static void ProductionMainNotifyError(string message) =>
        MessageBox.Show($"Não foi possível abrir o arquivo:\n{message}",
            "mPDF", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void ProductionDocumentNotifyError(string message) =>
        MessageBox.Show(message, "mPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
}
