using System.IO;
using System.Runtime.CompilerServices;
using mPdf.App.Services;
using mPdf.Signing;

namespace mPdf.App.Tests;

/// <summary>
/// Task 0 (Plano 3c) — troca as 8 fábricas/delegates de <see cref="UiPrompts"/> por versões que LANÇAM,
/// assim que este assembly de TESTE carrega. `[ModuleInitializer]` roda no CARREGAMENTO do módulo — uma
/// garantia do runtime .NET (ECMA-335 §II.10.5.3 / documentação de `ModuleInitializerAttribute`): o
/// método marcado executa antes de QUALQUER outro código do assembly, inclusive antes da descoberta de
/// testes do xunit (que só acontece depois do assembly já estar carregado). Não depende de qual `[Fact]`
/// roda primeiro nem da ordem de execução — é uma propriedade do CARREGAMENTO do assembly, não da
/// suíte. `UiPromptsGuardTests.ModuleInitializer_SwappedAllSeamMembers_BeforeThisTestRan` prova (via
/// reflexão, sem nunca invocar um diálogo real) que a troca já estava em vigor quando aquele teste
/// específico rodou — evidência empírica de que a garantia realmente se sustenta neste runtime/versão do
/// xunit (2.9.3), não só a garantia documentada do CLR.
///
/// FALLBACK declarado no plano (não usado aqui): se `[ModuleInitializer]` alguma vez se mostrar
/// não-confiável neste projeto, o plano B é uma `ICollectionFixture` do xunit instalando a mesma troca
/// no `IDisposable.Dispose`-menos setup de uma collection — mais verboso (precisa de um
/// `[CollectionDefinition]` + toda classe de teste precisar entrar na collection), mesmo efeito. Não
/// adotado porque a prova acima (`ModuleInitializer_SwappedAllSeamMembers_...`) confirma que
/// `[ModuleInitializer]` já é suficiente.
///
/// Converte "teste headless alcança um diálogo/MessageBox real e trava a suíte até o timeout do CI" em
/// "InvalidOperationException nomeada, imediata, apontando pro membro da seam e o fake a injetar".
/// NENHUM `[ModuleInitializer]` equivalente existe no assembly de PRODUÇÃO (mPdf.App) — só aqui, no
/// assembly de teste (`UiPromptsCoverageTests.NoModuleInitializer_ExistsInProductionAssembly` prova isso
/// via reflexão sobre o assembly de produção).
///
/// Plano 18: este MESMO `[ModuleInitializer]` também seta `MPDF_CONFIG_DIR` (ver
/// `AppConfig.DefaultDirectory`/`RecentFilesStore.DefaultDirectory`) pra um diretório TEMPORÁRIO fresco,
/// criado uma vez por PROCESSO de teste — garante que `ShellTests`/`Task5Tests` (que constroem a
/// `MainWindow` de produção via construtor sem parâmetros, sem receber um `AppConfig`/`RecentFilesStore`
/// injetado) nunca leiam o `%AppData%\mPDF\config.json`/`recentes.json` REAIS da máquina onde os testes
/// rodam. Sem isto, um `config.json` real com valores NÃO-default (ex.: `NitidezExtra:true`,
/// `PosicaoMenuAnotacao:BarraLateral` — o usuário dogfooda o app) fazia esses testes falharem de forma
/// "misteriosa" só nesta máquina, já que eles esperam o comportamento DEFAULT do app novo. Mesma garantia
/// de ordenação do runtime que já sustenta a troca de seams de `UiPrompts` acima: roda antes de QUALQUER
/// `[Fact]`, incluindo os que constroem a `MainWindow` primeiro.
/// </summary>
internal static class UiPromptsTestGuard
{
    [ModuleInitializer]
    internal static void Install()
    {
        InstalarConfigDirIsolado();

        UiPrompts.NotifyInfo = _ => throw Guard(nameof(UiPrompts.NotifyInfo), "um Action<string> fake (ex.: lista.Add ou msg => {})");
        UiPrompts.MainNotifyError = _ => throw Guard(nameof(UiPrompts.MainNotifyError), "um Action<string> fake");
        UiPrompts.DocumentNotifyError = _ => throw Guard(nameof(UiPrompts.DocumentNotifyError), "um Action<string> fake");
        UiPrompts.CreateFileDialog = () => new ThrowingFileDialogService();
        UiPrompts.CreateAnnotationDialog = () => new ThrowingAnnotationTextDialogService();
        UiPrompts.CreateMergeDialog = () => new ThrowingMergeDialogService();
        UiPrompts.CreateSplitDialog = () => new ThrowingSplitDialogService();
        UiPrompts.CreateConfirmClose = () => new ThrowingConfirmCloseService();
        UiPrompts.CreateConfirmFlatten = () => new ThrowingConfirmFlattenService();
        UiPrompts.CreateConfirmSaveBeforeSign = () => new ThrowingConfirmSaveBeforeSignService();
        UiPrompts.CreateSignDialog = () => new ThrowingSignDialogService();
        UiPrompts.CreateBatchSignDialog = () => new ThrowingBatchSignDialogService();
        UiPrompts.CreateConfirmOrganizerScale = () => new ThrowingConfirmOrganizerScaleService();
        UiPrompts.CreateExportImageDialog = () => new ThrowingExportImageDialogService();
        // Task 3 (Plano 16):
        UiPrompts.CreateExportDocumentDialog = () => new ThrowingExportDocumentDialogService();
        // Task 2 (Plano 11):
        UiPrompts.CreateSobreDialog = () => new ThrowingSobreDialogService();
        // Task 2 (Plano 17):
        UiPrompts.CreateConfiguracoesDialog = () => new ThrowingConfiguracoesDialogService();
        UiPrompts.CreateUpdateSource = () => new ThrowingUpdateSource();
        UiPrompts.CreateConfirmInstallUpdate = () => new ThrowingConfirmInstallUpdateService();
        // Task 4 (Plano 15):
        UiPrompts.CreateOcrProgress = () => new ThrowingOcrProgressService();
    }

    /// <summary>Plano 18 — seta `MPDF_CONFIG_DIR` pra uma pasta nova sob `Path.GetTempPath()`, única por
    /// PROCESSO de teste (sufixo com o PID, nunca colide entre execuções concorrentes/anteriores que não
    /// limparam). `AppConfig.DefaultDirectory`/`RecentFilesStore.DefaultDirectory` checam essa variável
    /// ANTES de cair em `%AppData%\mPDF` — ver doc XML delas. Nunca setada em produção (só aqui, no
    /// `[ModuleInitializer]` do assembly de TESTE), então o comportamento de produção não muda.</summary>
    private static void InstalarConfigDirIsolado()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mPdf-test-config-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("MPDF_CONFIG_DIR", dir);
    }

    /// <summary>Mensagem PADRONIZADA — nomeia o membro da seam alcançado E o tipo de fake a injetar no
    /// VM, pra que a falha (nunca mais um hang) já aponte pro fix em uma linha.</summary>
    internal static InvalidOperationException Guard(string seamMember, string fakeHint) => new(
        $"Teste headless alcançou diálogo real via UiPrompts.{seamMember} — injete {fakeHint} " +
        "no construtor do VM em vez de deixar o parâmetro no default de produção.");
}

// ---- Fakes que LANÇAM (não implementações "silenciosas") — cada método nomeia, na mensagem da
// exceção, exatamente qual membro de UiPrompts foi alcançado, mesmo quando várias interfaces
// compartilham o mesmo "formato" de fake (ex.: os 4 métodos de IFileDialogService).

internal sealed class ThrowingFileDialogService : IFileDialogService
{
    public string? PickPdfToOpen() => throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateFileDialog), "um IFileDialogService fake");
    public string? PickPdfToSaveAs(string currentPath) => throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateFileDialog), "um IFileDialogService fake");
    public string? PickImageToImport() => throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateFileDialog), "um IFileDialogService fake");
    public string? PickPdfToSave(string suggestedName) => throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateFileDialog), "um IFileDialogService fake");
}

internal sealed class ThrowingAnnotationTextDialogService : IAnnotationTextDialogService
{
    public string? PromptForText(string title, string? initialText = null) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateAnnotationDialog), "um IAnnotationTextDialogService fake");
}

internal sealed class ThrowingMergeDialogService : IMergeDialogService
{
    public IReadOnlyList<string>? PickFilesToMerge() =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateMergeDialog), "um IMergeDialogService fake");
}

internal sealed class ThrowingSplitDialogService : ISplitDialogService
{
    public (string Ranges, string DestinationFolder)? PickSplitOptions() =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateSplitDialog), "um ISplitDialogService fake");
}

internal sealed class ThrowingConfirmCloseService : IConfirmCloseService
{
    public CloseConfirmation Confirm(string documentTitle) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateConfirmClose), "um IConfirmCloseService fake");
}

internal sealed class ThrowingConfirmFlattenService : IConfirmFlattenService
{
    public bool Confirm(string message) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateConfirmFlatten), "um IConfirmFlattenService fake");
}

internal sealed class ThrowingConfirmSaveBeforeSignService : IConfirmSaveBeforeSignService
{
    public bool Confirm(string message) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateConfirmSaveBeforeSign), "um IConfirmSaveBeforeSignService fake");
}

internal sealed class ThrowingSignDialogService : ISignDialogService
{
    public SignDialogResult? PromptForSignature(
        IReadOnlyList<SigningCertificateInfo> certificates, bool allowDocMdp,
        RubricaGallery rubricas, Func<byte[]?> pickRubrica) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateSignDialog), "um ISignDialogService fake");
}

internal sealed class ThrowingBatchSignDialogService : IBatchSignDialogService
{
    public void ShowBatchSignDialog(mPdf.App.ViewModels.BatchSignViewModel viewModel) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateBatchSignDialog), "um IBatchSignDialogService fake");
}

internal sealed class ThrowingConfirmOrganizerScaleService : IConfirmOrganizerScaleService
{
    public bool Confirm(string message) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateConfirmOrganizerScale), "um IConfirmOrganizerScaleService fake");
}

internal sealed class ThrowingExportImageDialogService : IExportImageDialogService
{
    public void ShowExportImageDialog(mPdf.App.ViewModels.ExportImageViewModel viewModel) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateExportImageDialog), "um IExportImageDialogService fake");
}

internal sealed class ThrowingExportDocumentDialogService : IExportDocumentDialogService
{
    public void ShowExportDocumentDialog(mPdf.App.ViewModels.ExportDocumentViewModel viewModel) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateExportDocumentDialog), "um IExportDocumentDialogService fake");
}

internal sealed class ThrowingSobreDialogService : ISobreDialogService
{
    public void ShowSobreDialog(mPdf.App.ViewModels.SobreViewModel viewModel) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateSobreDialog), "um ISobreDialogService fake");
}

internal sealed class ThrowingConfiguracoesDialogService : IConfiguracoesDialogService
{
    public void ShowConfiguracoesDialog(mPdf.App.ViewModels.ConfiguracoesViewModel viewModel) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateConfiguracoesDialog), "um IConfiguracoesDialogService fake");
}

internal sealed class ThrowingUpdateSource : IUpdateSource
{
    public Task<LatestRelease?> GetLatestAsync(CancellationToken ct) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateUpdateSource), "um IUpdateSource fake");
}

internal sealed class ThrowingConfirmInstallUpdateService : IConfirmInstallUpdateService
{
    public bool Confirm(string message) =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateConfirmInstallUpdate), "um IConfirmInstallUpdateService fake");
}

internal sealed class ThrowingOcrProgressService : IOcrProgressService
{
    public IOcrProgressSession Start() =>
        throw UiPromptsTestGuard.Guard(nameof(UiPrompts.CreateOcrProgress), "um IOcrProgressService fake");
}
