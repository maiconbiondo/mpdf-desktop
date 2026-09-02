using System.IO;
using System.Windows;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

/// Rodada 2 da revisão pós-branch (plano3b-paginas-sumario, Task 6) — "R1 refutado": o pino LOCAL de
/// `OrganizerViewModel` da Rodada 1 não era exclusão mútua de verdade (3 furos provados pelo painel:
/// Undo/Redo não gateados, `Insert` armava o pino DEPOIS do 1º `await`, e um `bool` local não protege
/// contra sobreposição entre organizador e leitor). Fix: o pino sobe pra `DocumentSession`
/// (`IsEditInFlight`/`TryBeginEdit`/`EndEdit`, ver doc XML lá) — compartilhado entre `OrganizerViewModel`
/// e `DocumentViewModel`, as DUAS UIs que podem editar a MESMA sessão.
///
/// MATRIZ DE PARES (cada teste abaixo): A genuinamente bloqueada em voo (fake gated via
/// `TaskCompletionSource`) -> B tentada via seu PONTO DE ENTRADA REAL (chamada DIRETA a
/// `Execute`/`ExecuteAsync`, IGNORANDO `CanExecute` de propósito — prova que o FUNIL em si bloqueia,
/// não só o gate de UI que uma corrida poderia contornar) -> B recusada/bloqueada -> A completa -> B
/// funciona normalmente.
file sealed class FakeFileDialogService(string? openResult) : IFileDialogService
{
    public string? PickPdfToOpen() => openResult;
    public string? PickPdfToSaveAs(string currentPath) => null;
    public string? PickImageToImport() => null;
    public string? PickPdfToSave(string suggestedName) => null;
}

// Task 2 (Plano 5): satisfaz o construtor de MainViewModel pro par novo salvar×organizador abaixo —
// nenhum dos 2 testes fecha um documento sujo, então nunca é de fato chamado (mesma disciplina de
// FakeConfirmCloseService(CloseConfirmation.Cancel) usado em MainViewModelTests como "satisfaz o
// construtor, nunca exercitado").
file sealed class FakeMatrixConfirmCloseService : IConfirmCloseService
{
    public CloseConfirmation Confirm(string documentTitle) => CloseConfirmation.Cancel;
}

public class EditInFlightMatrixTests
{
    private static void SelectSomeText(PageViewModel page)
    {
        page.BeginSelection(new Point(10, 10));
        page.UpdateSelection(new Point(300, 20));
    }

    // ---- Par 1: organizer-op × organizer-op ---------------------------------------------------------

    [Fact] // Rotate em voo (gated) -> Delete tentado via ExecuteAsync DIRETO (bypassando CanExecute,
    // que já foi provado false na Rodada 1) -> DeletePages NUNCA chamado (o funil recusa antes de
    // sequer tentar) -> solta -> Delete funciona normalmente.
    public async Task RotateInFlight_BlocksDeleteViaRealExecutePath()
    {
        var fake = new FakePdfEditor { RotatePagesGate = new TaskCompletionSource<bool>() };
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        var vm = new OrganizerViewModel(session, fake, _ => { }, () => true);
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            var rotateTask = vm.RotateSelectedCommand.ExecuteAsync(null);

            await vm.DeleteSelectedCommand.ExecuteAsync(null); // REAL entry, CanExecute já é false — ignorado de propósito
            Assert.Equal(0, fake.DeletePagesCallCount); // o FUNIL recusou, não só a UI

            fake.RotatePagesGate!.SetResult(true);
            await rotateTask;

            vm.ToggleSelect(0, ctrl: false); // Rotate é terminal — seleção foi limpa pelo Applied
            await vm.DeleteSelectedCommand.ExecuteAsync(null);
            Assert.Equal(1, fake.DeletePagesCallCount); // solto -> funciona normalmente
        }
    }

    // ---- Par 2/3: organizer-op × Undo/Redo (furo (a)) -----------------------------------------------

    [Fact] // furo (a): Rotate em voo bloqueia Desfazer — CanExecute reflete E o comando REAL (Execute
    // direto, ignorando CanExecute) vira no-op de verdade (defesa em profundidade em
    // DocumentSession.Undo — ver doc XML lá).
    public async Task RotateInFlight_BlocksUndoViaRealEntry()
    {
        var fake = new FakePdfEditor { RotatePagesGate = new TaskCompletionSource<bool>() };
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        session.ApplyEdit(Fixtures.ThirtyPages()); // cria histórico de desfazer
        var snapshotBeforeArm = session.Snapshot;
        var organizer = new OrganizerViewModel(session, fake, _ => { }, () => true);
        using var doc = new DocumentViewModel(session, notifyError: _ => { });
        using (session) using (organizer)
        {
            organizer.ToggleSelect(0, ctrl: false);
            Assert.True(doc.UndoCommand.CanExecute(null)); // sanity: sem nada em voo, Desfazer disponível

            var rotateTask = organizer.RotateSelectedCommand.ExecuteAsync(null);

            Assert.False(doc.UndoCommand.CanExecute(null)); // CanExecute reflete o pino compartilhado

            doc.UndoCommand.Execute(null); // REAL entry — MainWindow.Undo() faz exatamente esta chamada
            Assert.Same(snapshotBeforeArm, session.Snapshot); // NADA mudou — Undo foi um no-op de verdade

            fake.RotatePagesGate!.SetResult(true);
            await rotateTask;

            Assert.True(doc.UndoCommand.CanExecute(null)); // solto -> reabilitado
        }
    }

    [Fact] // espelho exato do teste acima, pro sentido Refazer.
    public async Task RotateInFlight_BlocksRedoViaRealEntry()
    {
        var fake = new FakePdfEditor { RotatePagesGate = new TaskCompletionSource<bool>() };
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        session.ApplyEdit(Fixtures.ThirtyPages());
        session.Undo(); // cria histórico de refazer
        var snapshotBeforeArm = session.Snapshot;
        var organizer = new OrganizerViewModel(session, fake, _ => { }, () => true);
        using var doc = new DocumentViewModel(session, notifyError: _ => { });
        using (session) using (organizer)
        {
            organizer.ToggleSelect(0, ctrl: false);
            Assert.True(doc.RedoCommand.CanExecute(null));

            var rotateTask = organizer.RotateSelectedCommand.ExecuteAsync(null);

            Assert.False(doc.RedoCommand.CanExecute(null));

            doc.RedoCommand.Execute(null); // REAL entry
            Assert.Same(snapshotBeforeArm, session.Snapshot); // no-op de verdade

            fake.RotatePagesGate!.SetResult(true);
            await rotateTask;

            Assert.False(session.IsEditInFlight); // pino solto
            // O PRÓPRIO Rotate (uma edição REAL, via ApplyEdit) empilhou no desfazer e limpou o
            // refazer ANTIGO — comportamento ESPERADO de ApplyEdit (ver ApplyEdit_ClearsRedoStack em
            // DocumentSessionTests), não o pino. Estabelece um cenário de refazer NOVO pra provar que
            // Refazer FUNCIONA de verdade agora, não só que CanExecute mudou de valor.
            session.Undo();
            Assert.True(doc.RedoCommand.CanExecute(null));
            var beforeRedo = session.Snapshot;
            doc.RedoCommand.Execute(null);
            Assert.NotSame(beforeRedo, session.Snapshot); // Refazer realmente aconteceu
        }
    }

    // ---- Par 4: organizer-op × anotação do leitor (o furo "annotating with the organizer open") ------

    [Fact] // Rotate em voo (organizador) bloqueia ApplyMarkup (leitor) — o MESMO pino compartilhado
    // fecha exatamente a classe de defeito que o painel apontou: "annotating with the organizer open
    // during an in-flight op". REAL entry: ExecuteAsync direto no comando de anotação.
    public async Task RotateInFlight_BlocksReaderAnnotationViaRealEntry()
    {
        var organizerFake = new FakePdfEditor { RotatePagesGate = new TaskCompletionSource<bool>() };
        var docFake = new FakePdfEditor();
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        var organizer = new OrganizerViewModel(session, organizerFake, _ => { }, () => true);
        using var doc = new DocumentViewModel(session, editor: docFake, notifyError: _ => { });
        using (session) using (organizer)
        {
            SelectSomeText(doc.Pages[0]);
            Assert.True(doc.ApplyMarkupCommand.CanExecute(AnnotationKind.Highlight)); // sanity

            organizer.ToggleSelect(0, ctrl: false);
            var rotateTask = organizer.RotateSelectedCommand.ExecuteAsync(null);

            Assert.False(doc.ApplyMarkupCommand.CanExecute(AnnotationKind.Highlight));

            await doc.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight); // REAL entry, ignora CanExecute
            Assert.Equal(0, docFake.AddAnnotationCallCount); // o FUNIL recusou ANTES de computar a anotação

            organizerFake.RotatePagesGate!.SetResult(true);
            await rotateTask;

            Assert.False(session.IsEditInFlight); // pino solto
            // O PRÓPRIO Rotate (edição REAL do organizador) disparou Session.Applied -> OnSessionApplied
            // do LEITOR reconstruiu Pages e limpou a seleção de texto (mesmo ACHADO já documentado em
            // ApplyMarkup) — precisa re-selecionar pra provar que ApplyMarkup FUNCIONA de verdade
            // agora, não só que CanExecute mudou de valor.
            SelectSomeText(doc.Pages[0]);
            Assert.True(doc.ApplyMarkupCommand.CanExecute(AnnotationKind.Highlight));
            await doc.ApplyMarkupCommand.ExecuteAsync(AnnotationKind.Highlight);
            Assert.Equal(1, docFake.AddAnnotationCallCount); // funcionou de verdade
        }
    }

    // ---- Par novo (Task 2, Plano 3c): organizer-op × preencher formulário (painel Campos) ------------

    [Fact] // Rotate em voo (organizador) bloqueia ApplyFormValues (painel Campos) — o MESMO pino
    // compartilhado que fecha "annotating with the organizer open" (Par 4 acima) agora cobre TAMBÉM o
    // preenchimento de formulário. REAL entry: ExecuteAsync direto no comando do painel, ignorando
    // CanExecute de propósito.
    public async Task RotateInFlight_BlocksApplyFormValuesViaRealEntry()
    {
        var organizerFake = new FakePdfEditor { RotatePagesGate = new TaskCompletionSource<bool>() };
        var docFake = new FakePdfEditor
        {
            ReadFormFieldsResult = new[]
            {
                new FormFieldData("nome", FormFieldType.Text, "Original", Array.Empty<string>(), 0, null, IsReadOnly: false),
            },
        };
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        var organizer = new OrganizerViewModel(session, organizerFake, _ => { }, () => true);
        using var doc = new DocumentViewModel(session, editor: docFake, notifyError: _ => { });
        using (session) using (organizer)
        {
            doc.SeedFormFieldsCache(false, docFake.ReadFormFieldsResult!);
            doc.FormFieldEditors[0].EditedValue = "Novo Nome"; // dirty
            Assert.True(doc.ApplyFormValuesCommand.CanExecute(null)); // sanity

            organizer.ToggleSelect(0, ctrl: false);
            var rotateTask = organizer.RotateSelectedCommand.ExecuteAsync(null);

            Assert.False(doc.ApplyFormValuesCommand.CanExecute(null));

            await doc.ApplyFormValuesCommand.ExecuteAsync(null); // REAL entry, ignora CanExecute
            Assert.Equal(0, docFake.SetFormFieldsCallCount); // o FUNIL recusou ANTES de montar o dicionário

            organizerFake.RotatePagesGate!.SetResult(true);
            await rotateTask;

            Assert.False(session.IsEditInFlight); // pino solto
            // O PRÓPRIO Rotate (edição REAL do organizador) disparou Session.Applied -> o cache de
            // campos do painel ficou OBSOLETO por baixo (mesmo GATE DE LEITURA MANDATÓRIO documentado em
            // DocumentViewModel.ApplyFormValues) — renova explicitamente (mesmo espírito de re-selecionar
            // texto em RotateInFlight_BlocksReaderAnnotationViaRealEntry acima) antes de editar de novo,
            // pra provar que ApplyFormValues FUNCIONA de verdade agora, não só que CanExecute mudou.
            await doc.RefreshFormFieldsAsync();
            doc.FormFieldEditors[0].EditedValue = "Outro Nome";
            Assert.True(doc.ApplyFormValuesCommand.CanExecute(null));
            await doc.ApplyFormValuesCommand.ExecuteAsync(null);
            Assert.Equal(1, docFake.SetFormFieldsCallCount); // funcionou de verdade
        }
    }

    // ---- Par novo (Task 3, Plano 3c): organizer-op × achatar formulário (painel Campos) --------------

    [Fact] // Rotate em voo (organizador) bloqueia FlattenForm (painel Campos) — mesmo pino compartilhado,
    // mesma classe de furo do Par acima ("annotating with the organizer open during an in-flight op"),
    // agora cobrindo TAMBÉM achatar. REAL entry: ExecuteAsync direto no comando, ignorando CanExecute de
    // propósito. O diálogo de confirmação É consultado (síncrono, ANTES do funil — contrato do brief,
    // ver doc XML de DocumentViewModel.FlattenForm), mas o MOTOR nunca é alcançado enquanto Rotate
    // segura o pino — o funil recusa DEPOIS do diálogo, ANTES de `_editor.FlattenForm`.
    public async Task RotateInFlight_BlocksFlattenFormViaRealEntry()
    {
        var organizerFake = new FakePdfEditor { RotatePagesGate = new TaskCompletionSource<bool>() };
        var docFake = new FakePdfEditor
        {
            ReadFormFieldsResult = new[]
            {
                new FormFieldData("nome", FormFieldType.Text, "Original", Array.Empty<string>(), 0, null, IsReadOnly: false),
            },
        };
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        var organizer = new OrganizerViewModel(session, organizerFake, _ => { }, () => true);
        var confirmFlatten = new FakeConfirmFlattenService(true);
        using var doc = new DocumentViewModel(session, editor: docFake, notifyError: _ => { },
            notifyInfo: _ => { }, confirmFlatten: confirmFlatten);
        using (session) using (organizer)
        {
            doc.SeedFormFieldsCache(false, docFake.ReadFormFieldsResult!);
            Assert.True(doc.FlattenFormCommand.CanExecute(null)); // sanity

            organizer.ToggleSelect(0, ctrl: false);
            var rotateTask = organizer.RotateSelectedCommand.ExecuteAsync(null);

            Assert.False(doc.FlattenFormCommand.CanExecute(null));

            await doc.FlattenFormCommand.ExecuteAsync(null); // REAL entry, ignora CanExecute
            Assert.Equal(0, docFake.FlattenFormCallCount); // o FUNIL recusou ANTES de alcançar o motor

            organizerFake.RotatePagesGate!.SetResult(true);
            await rotateTask;

            Assert.False(session.IsEditInFlight); // pino solto
            // O PRÓPRIO Rotate (edição REAL do organizador) disparou Session.Applied -> o cache de
            // campos do painel ficou OBSOLETO por baixo — renova explicitamente (mesmo espírito do Par
            // acima) antes de achatar de novo, pra provar que FlattenForm FUNCIONA de verdade agora, não
            // só que CanExecute mudou.
            await doc.RefreshFormFieldsAsync();
            Assert.True(doc.FlattenFormCommand.CanExecute(null));
            await doc.FlattenFormCommand.ExecuteAsync(null);
            Assert.Equal(1, docFake.FlattenFormCallCount); // funcionou de verdade
        }
    }

    // ---- Par novo (Task 3, Plano 4): organizer-op × assinar --------------------------------------------

    [Fact] // Rotate em voo (organizador) bloqueia Sign (Task 3, Plano 4) — mesmo pino compartilhado, um
    // comando mutador NOVO (assinar) tem que respeitar o MESMO funil que FlattenForm/ApplyFormValues já
    // respeitam (par acima). REAL entry: ExecuteAsync direto no comando, ignorando CanExecute de
    // propósito. Doc não está sujo (sem prompt de salvar) e o diálogo de assinatura já tem uma resposta
    // FIXA — o funil recusa DEPOIS da consulta a HasSignatures/diálogo, ANTES de alcançar
    // FakeSigningEngine.Sign. CRÍTICO (achado ao vivo, ver doc XML de SignCommandTests.CopyFixtureToTemp):
    // Sign pode escrever em disco de verdade (Session.CommitSigned) — por isso, DIFERENTE do par de
    // FlattenForm acima, este teste abre uma CÓPIA temporária da fixture, nunca o arquivo compartilhado.
    public async Task RotateInFlight_BlocksSignViaRealEntry()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-matrix-sign-{Guid.NewGuid():N}.pdf");
        File.Copy(Path.Combine(Fixtures.Root, "fixture-30p.pdf"), tmp);
        // config TEMPORÁRIA de propósito (revisão do coordenador, item "temp litter"): a 2ª chamada de
        // Sign abaixo assina depois de Rotate deixar a sessão SUJA -> Sign() salva antes (fluxo normal)
        // -> sem uma AppConfig injetada, o default seria AppConfig.DefaultDirectory (%AppData%\mPDF, a
        // pasta REAL da máquina) -- nenhum teste deveria tocar aí. Mesmo padrão de SignCommandTests.
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-matrix-sign-cfg-{Guid.NewGuid():N}");
        try
        {
            var organizerFake = new FakePdfEditor { RotatePagesGate = new TaskCompletionSource<bool>() };
            var docEditorFake = new FakePdfEditor { HasSignaturesResult = false };
            var signEngine = new FakeSigningEngine();
            using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
            var dialog = new FakeSignDialogService(new SignDialogResult(cert, null, null, ApplyDocMdp: true, PlaceStamp: false));
            var session = DocumentSession.Open(tmp);
            var organizer = new OrganizerViewModel(session, organizerFake, _ => { }, () => true);
            using var doc = new DocumentViewModel(session, editor: docEditorFake, config: new AppConfig(configDir),
                notifyError: _ => { }, notifyInfo: _ => { }, signDialog: dialog, signingEngine: signEngine,
                confirmSaveBeforeSign: new FakeConfirmSaveBeforeSignService(true),
                listSigningCertificates: () => Array.Empty<SigningCertificateInfo>());
            using (session) using (organizer)
            {
                Assert.True(doc.SignCommand.CanExecute(null)); // sanity

                organizer.ToggleSelect(0, ctrl: false);
                var rotateTask = organizer.RotateSelectedCommand.ExecuteAsync(null);

                Assert.False(doc.SignCommand.CanExecute(null));

                await doc.SignCommand.ExecuteAsync(null); // REAL entry, ignora CanExecute
                Assert.Equal(0, signEngine.SignCallCount); // o FUNIL recusou ANTES de alcançar o motor

                organizerFake.RotatePagesGate!.SetResult(true);
                await rotateTask;

                Assert.False(session.IsEditInFlight); // pino solto
                Assert.True(doc.SignCommand.CanExecute(null));
                await doc.SignCommand.ExecuteAsync(null);
                Assert.Equal(1, signEngine.SignCallCount); // funcionou de verdade
            }
        }
        finally
        {
            File.Delete(tmp);
            try { if (File.Exists(tmp + ".bak")) File.Delete(tmp + ".bak"); } catch { } // Rotate suja -> Sign salva -> 1ª gravação cria .bak
            try { if (Directory.Exists(configDir)) Directory.Delete(configDir, recursive: true); } catch { }
        }
    }

    // ---- Par novo (Task 2, Plano 5): organizer-op × Salvar (MainViewModel.SaveCommand) ----------------
    //
    // Save virou async nesta task (Task.Run em torno do pipeline atômico existente — ver doc XML de
    // MainViewModel.Save) e passou a armar o MESMO pino compartilhado (`TryBeginEdit`/`EndEdit`) — este
    // é o par que prova as DUAS direções, mesmo espírito do par organizer-op×Sign acima (o outro
    // mutador que também grava em disco de verdade).

    [Fact] // Rotate em voo (organizador) bloqueia Save (MainViewModel) — REAL entry: ExecuteAsync
    // direto no comando, ignorando CanExecute de propósito. Save grava em disco de verdade
    // (Session.Save -> AtomicWrite) — mesma disciplina do par de Sign acima: copia a fixture pra um
    // arquivo TEMPORÁRIO, nunca o compartilhado do repo.
    public async Task RotateInFlight_BlocksSaveViaRealEntry()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-matrix-save-{Guid.NewGuid():N}.pdf");
        // Base = fixture-a4.pdf (1 página) — DIFERENTE de Fixtures.ThirtyPages() (30 páginas, conteúdo
        // distinto), ao contrário de fixture-30p.pdf: abrir fixture-30p.pdf e "sujar" com
        // Fixtures.ThirtyPages() seria um Apply de conteúdo IDÊNTICO ao já salvo — UpdateDirty (hash vs
        // _lastSavedHash, ver DocumentSession.Apply) NUNCA marcaria IsDirty, e a sanity abaixo falharia
        // (achado ao vivo escrevendo este teste). O Apply roda ANTES de construir organizer/doc, pra
        // ambos já nascerem vendo as 30 páginas (organizador seleciona o índice 0 de um documento que
        // já existe nesse estado, sem depender de reagir a um Applied posterior).
        File.Copy(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), tmp);
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-matrix-save-cfg-{Guid.NewGuid():N}");
        try
        {
            var organizerFake = new FakePdfEditor { RotatePagesGate = new TaskCompletionSource<bool>() };
            var session = DocumentSession.Open(tmp);
            session.Apply(Fixtures.ThirtyPages()); // suja -> Save teria algo real a fazer
            var organizer = new OrganizerViewModel(session, organizerFake, _ => { }, () => true);
            using var doc = new DocumentViewModel(session, notifyError: _ => { });
            var vm = new MainViewModel(new FakeFileDialogService(null), new RecentFilesStore(configDir),
                _ => { }, new AppConfig(configDir), new FakeMatrixConfirmCloseService());
            vm.Documents.Add(doc);
            vm.SelectedDocument = doc;
            using (session) using (organizer)
            {
                Assert.True(vm.SaveCommand.CanExecute(null)); // sanity

                organizer.ToggleSelect(0, ctrl: false);
                var rotateTask = organizer.RotateSelectedCommand.ExecuteAsync(null);

                Assert.False(vm.SaveCommand.CanExecute(null));

                await vm.SaveCommand.ExecuteAsync(null); // REAL entry, ignora CanExecute
                Assert.True(doc.IsDirty); // o FUNIL recusou ANTES de alcançar Session.Save

                organizerFake.RotatePagesGate!.SetResult(true);
                await rotateTask;

                Assert.False(session.IsEditInFlight); // pino solto
                Assert.True(vm.SaveCommand.CanExecute(null));
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.False(doc.IsDirty); // funcionou de verdade
            }
        }
        finally
        {
            File.Delete(tmp);
            try { if (File.Exists(tmp + ".bak")) File.Delete(tmp + ".bak"); } catch { }
            try { if (Directory.Exists(configDir)) Directory.Delete(configDir, recursive: true); } catch { }
        }
    }

    [Fact] // Direção espelhada: Save em voo (MainViewModel) bloqueia Rotate (organizador). Save arma o
    // pino SINCRONAMENTE — `TryBeginEdit()` roda ANTES do 1º `await` (dentro de `Task.Run`) — não
    // precisa de nenhum gate/TaskCompletionSource pra observar o estado "em voo": a linha seguinte a
    // `ExecuteAsync(null)` (não aguardada) já roda com o pino armado, mesmo raciocínio determinístico
    // já usado no Par 5 (furo b) abaixo, só que aqui a garantia vem da ORDEM do método (arma antes de
    // qualquer await), não de um arquivo grande forçando uma janela de tempo.
    public async Task SaveInFlight_BlocksRotateViaRealEntry()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-matrix-save2-{Guid.NewGuid():N}.pdf");
        // Base = fixture-a4.pdf + Apply(ThirtyPages) ANTES de construir organizer/doc — mesmo motivo
        // registrado em RotateInFlight_BlocksSaveViaRealEntry acima (fixture-30p.pdf + Apply(ThirtyPages)
        // seria um no-op de conteúdo; aqui não quebraria nenhuma asserção, mas o doc não estaria
        // genuinamente sujo, o que contradiria o cenário que o teste descreve).
        File.Copy(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), tmp);
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-matrix-save2-cfg-{Guid.NewGuid():N}");
        try
        {
            var organizerFake = new FakePdfEditor();
            var session = DocumentSession.Open(tmp);
            session.Apply(Fixtures.ThirtyPages());
            var organizer = new OrganizerViewModel(session, organizerFake, _ => { }, () => true);
            using var doc = new DocumentViewModel(session, notifyError: _ => { });
            var vm = new MainViewModel(new FakeFileDialogService(null), new RecentFilesStore(configDir),
                _ => { }, new AppConfig(configDir), new FakeMatrixConfirmCloseService());
            vm.Documents.Add(doc);
            vm.SelectedDocument = doc;
            using (session) using (organizer)
            {
                organizer.ToggleSelect(0, ctrl: false);
                Assert.True(organizer.RotateSelectedCommand.CanExecute(null)); // sanity

                var saveTask = vm.SaveCommand.ExecuteAsync(null); // não aguardada -- funil já armado ao voltar

                Assert.False(organizer.RotateSelectedCommand.CanExecute(null));

                await organizer.RotateSelectedCommand.ExecuteAsync(null); // REAL entry, ignora CanExecute
                Assert.Equal(0, organizerFake.RotatePagesCallCount); // o FUNIL recusou

                await saveTask;

                Assert.False(session.IsEditInFlight); // pino solto
                Assert.True(organizer.RotateSelectedCommand.CanExecute(null));
                await organizer.RotateSelectedCommand.ExecuteAsync(null);
                Assert.Equal(1, organizerFake.RotatePagesCallCount); // funcionou de verdade
            }
        }
        finally
        {
            File.Delete(tmp);
            try { if (File.Exists(tmp + ".bak")) File.Delete(tmp + ".bak"); } catch { }
            try { if (Directory.Exists(configDir)) Directory.Delete(configDir, recursive: true); } catch { }
        }
    }

    // ---- Par 5: furo (b) — Insert-durante-leitura-do-arquivo × Rotate --------------------------------

    [Fact] // furo (b), PROVA DIRETA: `Insert` arma o pino LOGO APÓS o diálogo (síncrono), ANTES do 1º
    // `await` (a leitura do arquivo, `File.ReadAllBytes`) — Rotate fica bloqueado durante TODA a
    // leitura, não só durante a chamada final a `InsertPages`. `Task.Run` NUNCA completa inline (sempre
    // agenda no ThreadPool) — a linha seguinte a `ExecuteAsync(null)` sem `await` roda ANTES da leitura
    // terminar, determinístico o bastante pra um teste (mesmo raciocínio já usado pelo restante desta
    // suíte pra provar estados intermediários de operações assíncronas).
    public async Task InsertDuringFileRead_BlocksRotateViaRealEntry()
    {
        // Arquivo GRANDE de propósito (300MB) — NÃO precisa ser um PDF válido (FakePdfEditor.InsertPages
        // não lê o CONTEÚDO de `source`, só registra a referência). Uma fixture pequena (poucos KB) some
        // rápido demais: o ThreadPool consegue despachar+concluir a leitura ANTES da linha de asserção
        // seguinte rodar em máquinas rápidas/com threads do pool já aquecidas (medido ao vivo: um teste
        // com fixture-a4.pdf de ~4KB NÃO distinguia a mutação "arma DEPOIS da leitura" — passava mesmo
        // assim, porque a leitura de 4KB e o `HasSignatures` seguinte terminavam rápido demais pra
        // corrida importar). 300MB garante uma janela de leitura MENSURÁVEL (ms), tempo o bastante pra
        // este teste observar de forma confiável o estado "ainda lendo" — mesmo espírito do cenário REAL
        // do furo (b) no relatório da revisão ("PDF grande/de rede").
        var sourcePath = Path.Combine(Path.GetTempPath(), $"mpdf-insert-during-read-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(sourcePath, new byte[300 * 1024 * 1024]);
        // `insertTask` declarado FORA do try — se uma asserção falhar antes de `await insertTask` rodar,
        // o `finally` ainda precisa ESPERAR essa Task terminar (bom-esforço, exceção engolida de
        // propósito) ANTES de apagar o arquivo, senão `File.Delete` mascara a falha de ASSERÇÃO real
        // com um `IOException` de "arquivo em uso" (achado ao vivo: aconteceu exatamente assim rodando
        // a mutação de controle negativo desta prova).
        Task insertTask = Task.CompletedTask;
        try
        {
            var dialogs = new FakeFileDialogService(sourcePath);
            var fake = new FakePdfEditor();
            var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
            var vm = new OrganizerViewModel(session, fake, _ => { }, () => true, dialogs);
            using (session) using (vm)
            {
                vm.ToggleSelect(0, ctrl: false);

                insertTask = vm.InsertCommand.ExecuteAsync(null); // NÃO aguardado — ainda no meio da leitura do arquivo

                Assert.False(vm.RotateSelectedCommand.CanExecute(null)); // bloqueado DURANTE a leitura

                await vm.RotateSelectedCommand.ExecuteAsync(null); // REAL entry, ignora CanExecute
                Assert.Equal(0, fake.RotatePagesCallCount); // o FUNIL recusou

                await insertTask;

                Assert.False(session.IsEditInFlight); // pino solto
                // O PRÓPRIO Insert (edição REAL) disparou Applied -> OnSessionApplied reconstruiu Pages
                // e limpou a seleção — precisa re-selecionar pra provar que Rotate FUNCIONA de verdade
                // agora, não só que CanExecute mudou de valor.
                vm.ToggleSelect(0, ctrl: false);
                Assert.True(vm.RotateSelectedCommand.CanExecute(null));
                await vm.RotateSelectedCommand.ExecuteAsync(null);
                Assert.Equal(1, fake.RotatePagesCallCount); // funcionou de verdade
            }
        }
        finally
        {
            try { await insertTask; } catch { /* já reportada (ou irrelevante) — só drenando pra soltar o handle do arquivo */ }
            File.Delete(sourcePath);
        }
    }

    // ---- "A lança -> pino solta" (belt do funil) ------------------------------------------------------

    [Fact] // uma exceção NÃO capturada por `TryRunEditAsync` (só PdfSignedDocumentException/
    // PdfEditingException/ArgumentException são tratadas ali) escapa pro AsyncRelayCommand como Task
    // FALTADA — mas o `finally { Session.EndEdit(); }` do funil AINDA solta o pino, senão um erro
    // inesperado travaria TODOS os comandos mutadores pra sempre.
    public async Task RotateThrowsUnhandledException_StillReleasesPin()
    {
        var fake = new FakePdfEditor { ThrowOnRotatePages = new InvalidOperationException("falha não tipada pelo TryRunEditAsync") };
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-30p.pdf"));
        var vm = new OrganizerViewModel(session, fake, _ => { }, () => true);
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);

            var ex = await Record.ExceptionAsync(() => vm.RotateSelectedCommand.ExecuteAsync(null));

            Assert.NotNull(ex); // confirma que este É o caminho NÃO capturado por TryRunEditAsync
            Assert.False(session.IsEditInFlight); // mas o pino AINDA solta — o finally rodou

            vm.ToggleSelect(0, ctrl: false);
            Assert.True(vm.DeleteSelectedCommand.CanExecute(null)); // outro comando reabilitado
        }
    }
}
