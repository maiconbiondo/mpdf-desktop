using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using Xunit;

namespace mPdf.App.Tests;

/// <summary>
/// Task 0 (Plano 3c) — guarda de COBERTURA (designing-guard-rails: "capability-vs-coverage assertions"),
/// não de comportamento. `UiPromptsGuardTests` prova que os defaults CONHECIDOS disparam; este arquivo
/// prova que a LISTA de defaults conhecidos é EXAUSTIVA — reflete sobre os parâmetros OPCIONAIS dos
/// construtores públicos de `DocumentViewModel`/`OrganizerViewModel`/`MainViewModel` cujo TIPO é
/// `Action&lt;string&gt;` (convenção notify*) OU qualquer tipo do namespace `mPdf.App.Services` (onde
/// vivem TODOS os serviços de diálogo desta seam — `IFileDialogService`/`IAnnotationTextDialogService`/
/// `IMergeDialogService`/`ISplitDialogService`/`IConfirmCloseService`/`StampGallery`), e FALHA se
/// encontrar um parâmetro fora do manifesto abaixo. Um default NOVO desse formato, adicionado a
/// qualquer um dos 3 ctors no futuro, quebra ESTE teste (build vermelho) em vez de silenciosamente
/// reabrir o hang — "um guard que cobre 4 de 6 caminhos é um guard com um buraco" (achado da revisão
/// final do 3b que motivou esta task).
///
/// ESCOPO DELIBERADAMENTE NÃO COBERTO por esta reflexão (documentado, não esquecido): os overloads de
/// conveniência de 2/3 argumentos de `MainViewModel` encadeiam `UiPrompts.MainNotifyError`/
/// `UiPrompts.CreateConfirmClose()` como ARGUMENTOS FIXOS de uma chamada `: this(...)` — não são
/// PARÂMETROS opcionais de ctor nenhum (não têm `ParameterInfo.IsOptional`), então reflexão sobre
/// parâmetros não os alcança estruturalmente. Cobertos por prova de disparo DEDICADA em
/// `UiPromptsGuardTests` (`MainViewModel_TwoArgCtor_.../ThreeArgCtor_...`) em vez de por este manifesto.
/// </summary>
public class UiPromptsCoverageTests
{
    /// (Nome do tipo do VM, nome do parâmetro) -> onde a cobertura mora ("UiPrompts.X" = roteado pela
    /// seam, provado em UiPromptsGuardTests) OU "ISENTO -- <motivo>" (não mostra UI, sem risco de hang).
    private static readonly Dictionary<(string Type, string Param), string> KnownOptionalServiceParams = new()
    {
        [("DocumentViewModel", "notifyError")] = "UiPrompts.DocumentNotifyError",
        [("DocumentViewModel", "annotationDialog")] = "UiPrompts.CreateAnnotationDialog",
        [("DocumentViewModel", "dialogs")] = "UiPrompts.CreateFileDialog",
        [("DocumentViewModel", "notifyInfo")] = "UiPrompts.NotifyInfo",
        [("DocumentViewModel", "confirmFlatten")] = "UiPrompts.CreateConfirmFlatten",
        [("DocumentViewModel", "confirmSaveBeforeSign")] = "UiPrompts.CreateConfirmSaveBeforeSign",
        [("DocumentViewModel", "signDialog")] = "UiPrompts.CreateSignDialog",
        [("DocumentViewModel", "confirmOrganizerScale")] = "UiPrompts.CreateConfirmOrganizerScale",
        // Task 4 (Plano 7): "Exportar página como imagem" -- mesma disciplina de injeção via UiPrompts.
        [("DocumentViewModel", "exportImageDialog")] = "UiPrompts.CreateExportImageDialog",
        // Task 4 (Plano 15): faixa de progresso do OCR -- mesma disciplina de injeção via UiPrompts.
        // `ocrEngine` (Func-less, tipo IOcrEngine em mPdf.Ocr) e `rasterizerFactory` (Func<...>, namespace
        // System) NÃO entram nesta varredura: a primeira não mora em mPdf.App.Services, a segunda é uma
        // fábrica Func (mesma isenção estrutural de `Func<IUpdateSource>` de SobreViewModel) — e nenhuma
        // das duas mostra UI (motor de OCR/renderer, sem risco de hang).
        [("DocumentViewModel", "ocrProgress")] = "UiPrompts.CreateOcrProgress",
        [("OrganizerViewModel", "dialogs")] = "UiPrompts.CreateFileDialog",
        [("OrganizerViewModel", "notifyInfo")] = "UiPrompts.NotifyInfo",
        [("MainViewModel", "annotationDialog")] = "UiPrompts.CreateAnnotationDialog",
        [("MainViewModel", "notifyInfo")] = "UiPrompts.NotifyInfo",
        [("MainViewModel", "mergeDialog")] = "UiPrompts.CreateMergeDialog",
        [("MainViewModel", "splitDialog")] = "UiPrompts.CreateSplitDialog",
        [("MainViewModel", "batchSignDialog")] = "UiPrompts.CreateBatchSignDialog",
        // Task 2 (Plano 11): diálogo "Sobre".
        [("MainViewModel", "sobreDialog")] = "UiPrompts.CreateSobreDialog",
        // Task 2 (Plano 11): prompt "fechar e instalar agora?" do fluxo de atualização de SobreViewModel.
        // `createSource` (Func<IUpdateSource>) NÃO entra aqui de propósito — é um parâmetro FUNC (fábrica
        // deferida), não um tipo de serviço direto; a reflexão abaixo só varre parâmetros cujo PRÓPRIO
        // tipo mora em mPdf.App.Services (Namespace de Func<IUpdateSource> é "System", não
        // "mPdf.App.Services" — a forma Func é DELIBERADA, ver doc XML de SobreViewModel: precisa ser
        // deferida até o clique em "Verificar atualização", não resolvida no momento da construção do
        // VM/abertura do diálogo, diferente de todo outro default `?? UiPrompts.CreateXxx()` desta
        // classe). Cobertura equivalente vem de `UiPromptsGuardTests` (fire/negative-control) +
        // `SobreViewModelTests`/`UpdateNetworkConfinementTests` (prova comportamental/textual de
        // "rede só por clique").
        [("SobreViewModel", "confirmInstall")] = "UiPrompts.CreateConfirmInstallUpdate",
        // ISENÇÃO DELIBERADA: StampGallery só toca disco (Directory.CreateDirectory/File.Copy) — nenhum
        // ShowDialog/MessageBox.Show no caminho, mesmo exemplar de AppConfig/RecentFilesStore (que nem
        // entram nesta varredura por não estarem em mPdf.App.Services). Ver doc XML de UiPrompts.
        [("MainViewModel", "stampGallery")] = "ISENTO -- StampGallery não mostra UI (só filesystem)",
    };

    private static readonly Type[] TargetVmTypes =
    [
        typeof(DocumentViewModel), typeof(OrganizerViewModel), typeof(MainViewModel), typeof(SobreViewModel),
    ];

    [Fact]
    public void AllOptionalServiceShapedCtorParameters_AreAccountedForInManifest()
    {
        var found = new HashSet<(string Type, string Param)>();
        var unaccounted = new List<string>();

        foreach (var vmType in TargetVmTypes)
        {
            foreach (var ctor in vmType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var p in ctor.GetParameters())
                {
                    if (!p.IsOptional) continue;
                    bool isServiceShaped = p.ParameterType == typeof(Action<string>)
                        || p.ParameterType.Namespace == "mPdf.App.Services";
                    if (!isServiceShaped) continue;

                    var key = (vmType.Name, p.Name!);
                    found.Add(key);
                    if (!KnownOptionalServiceParams.ContainsKey(key))
                    {
                        unaccounted.Add(
                            $"{vmType.Name}.{p.Name} ({p.ParameterType.Name}) -- NÃO está no manifesto de " +
                            "UiPromptsCoverageTests. Se é um diálogo/notificação novo: roteie o default pela " +
                            "seam UiPrompts e adicione uma entrada aqui + uma prova de disparo em " +
                            "UiPromptsGuardTests. Se genuinamente não mostra UI: adicione como ISENTO com o motivo.");
                    }
                }
            }
        }

        Assert.True(unaccounted.Count == 0, string.Join("\n", unaccounted));

        // Simétrico: uma entrada do manifesto que a reflexão NÃO encontra mais é tão perigosa quanto uma
        // faltando -- mascara cobertura que já não existe (assinatura mudou, parâmetro removido/renomeado
        // sem atualizar aqui).
        var stale = KnownOptionalServiceParams.Keys.Where(k => !found.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "Entradas OBSOLETAS no manifesto (parâmetro não existe mais via reflexão, atualize o manifesto): " +
            string.Join(", ", stale.Select(k => $"{k.Type}.{k.Param}")));
    }

    /// <summary>Item explícito do brief: "nenhum ModuleInitializer no assembly de produção". Prova
    /// executável (não só uma alegação no relatório) -- varre TODOS os métodos de TODOS os tipos do
    /// assembly `mPdf.App` (produção, onde `UiPrompts` mora) por `[ModuleInitializer]`.</summary>
    [Fact]
    public void NoModuleInitializer_ExistsInProductionAssembly()
    {
        var productionAssembly = typeof(UiPrompts).Assembly;
        var offenders = productionAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes(typeof(ModuleInitializerAttribute), false).Length > 0)
            .Select(m => $"{m.DeclaringType}.{m.Name}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "ModuleInitializer encontrado no assembly de PRODUÇÃO (não deveria existir nenhum): " + string.Join(", ", offenders));
    }
}
