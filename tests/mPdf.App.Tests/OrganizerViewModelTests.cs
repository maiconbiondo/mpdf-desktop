using System.IO;
using System.Linq;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

// Task 4 (Plano 3b): fake de IFileDialogService pros testes de Extrair/Inserir abaixo — mesmo padrão de
// FakeDialog (MainViewModelTests): caminhos FIXOS (ou null = cancelado), registra a última chamada.
file sealed class FakeFileDialogService(string? openResult = null, string? saveResult = null) : IFileDialogService
{
    public int PickPdfToSaveCallCount { get; private set; }
    public string? LastSuggestedName { get; private set; }

    public string? PickPdfToOpen() => openResult;
    public string? PickPdfToSaveAs(string currentPath) => null;
    public string? PickImageToImport() => null;

    public string? PickPdfToSave(string suggestedName)
    {
        PickPdfToSaveCallCount++;
        LastSuggestedName = suggestedName;
        return saveResult;
    }
}

// Task 3 (Plano 3b): OrganizerViewModel — grade de miniaturas grandes + girar/excluir/mover pelo motor
// de página do Task 2 + ApplyEdit. Testes com FakePdfEditor (mesmo tipo usado por DocumentViewModelTests
// — Rotate/Delete/Move ganharam implementação real lá, ver comentário no próprio fake) + DocumentSession
// REAL (undo/redo é da sessão de verdade, não dá pra fakear sem perder a garantia que o brief pede).
public class OrganizerViewModelTests
{
    private static (OrganizerViewModel vm, FakePdfEditor fake, DocumentSession session, List<string> errors) Build(
        string fixture = "fixture-30p.pdf", Func<bool>? canEdit = null,
        IFileDialogService? dialogs = null, Action<string>? notifyInfo = null)
    {
        var fake = new FakePdfEditor();
        var session = DocumentSession.Open(Path.Combine(Fixtures.Root, fixture));
        var errors = new List<string>();
        // notifyInfo SEMPRE recebe um no-op default aqui (nunca deixa null fluir pro construtor do VM,
        // que cairia no DefaultNotifyInfo de PRODUÇÃO — um `MessageBox.Show` real que TRAVA a sessão de
        // teste esperando um clique que nunca vem, headless). Achado ao vivo: `ExtractSelectedCommand`
        // sem `notifyInfo` explícito travou a suíte por completo até ser morto manualmente — mesma
        // disciplina que `MainViewModelTests.VmFull` já aplicava pra `notifyError`.
        var vm = new OrganizerViewModel(session, fake, errors.Add, canEdit ?? (() => true), dialogs, notifyInfo ?? (_ => { }));
        return (vm, fake, session, errors);
    }

    // ---- construção / refresh -------------------------------------------------------------------

    [Fact]
    public void Ctor_PopulatesPagesFromSession()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            Assert.Equal(30, vm.Pages.Count);
            Assert.Equal(1, vm.Pages[0].PageNumber);
            Assert.Equal(session.PageSizes[0].WidthPt * OrganizerPageViewModel.Scale, vm.Pages[0].DisplayWidth, 0.01);
        }
    }

    [Fact] // "o organizador deve refletir edições" (brief) — mesmo uma edição que não passou pelos
    // comandos do PRÓPRIO organizador (aqui, Session.Apply direto, simulando outra origem) dispara o
    // rebuild via Session.Applied.
    public void SessionApplied_RebuildsPagesFromNewSnapshot()
    {
        var (vm, _, session, _) = Build("fixture-a4.pdf"); // 1 página
        using (session) using (vm)
        {
            Assert.Single(vm.Pages);

            session.Apply(Fixtures.ThirtyPages());

            Assert.Equal(30, vm.Pages.Count);
        }
    }

    // ---- seleção ----------------------------------------------------------------------------------

    [Fact]
    public void ToggleSelect_PlainClick_ReplacesSelectionWithSinglePage()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            vm.ToggleSelect(2, ctrl: false);
            vm.ToggleSelect(5, ctrl: false);

            Assert.Equal(new[] { 5 }, vm.SelectedIndexes);
        }
    }

    [Fact]
    public void ToggleSelect_CtrlClick_TogglesMembershipPreservingRest()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            vm.ToggleSelect(2, ctrl: false);
            vm.ToggleSelect(5, ctrl: true);
            vm.ToggleSelect(9, ctrl: true);
            Assert.Equal(new[] { 2, 5, 9 }, vm.SelectedIndexes.OrderBy(i => i));

            vm.ToggleSelect(5, ctrl: true); // remove da seleção

            Assert.Equal(new[] { 2, 9 }, vm.SelectedIndexes.OrderBy(i => i));
        }
    }

    [Fact]
    public void HasSelection_ReflectsCurrentSelection()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            Assert.False(vm.HasSelection);
            vm.ToggleSelect(0, ctrl: false);
            Assert.True(vm.HasSelection);
        }
    }

    // ---- RotateSelected -----------------------------------------------------------------------------

    [Fact]
    public async Task RotateSelectedCommand_SendsSelectedIndexesAndDegrees90_ThenApplyEdit()
    {
        var (vm, fake, session, _) = Build();
        using (session) using (vm)
        {
            vm.ToggleSelect(2, ctrl: false);
            vm.ToggleSelect(5, ctrl: true);
            vm.ToggleSelect(9, ctrl: true);
            var before = session.Snapshot;

            await vm.RotateSelectedCommand.ExecuteAsync(null);

            Assert.Equal(new[] { 2, 5, 9 }, fake.LastRotatePageIndexes!.OrderBy(i => i));
            Assert.Equal(90, fake.LastRotateDegrees);
            Assert.Equal(1, fake.RotatePagesCallCount);
            Assert.NotSame(before, session.Snapshot); // ApplyEdit realmente trocou o snapshot
        }
    }

    [Fact]
    public void RotateSelectedCommand_CanExecute_FalseWithoutSelection()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            Assert.False(vm.RotateSelectedCommand.CanExecute(null));
        }
    }

    [Fact]
    public void RotateSelectedCommand_CanExecute_FalseWhenCanEditFalse()
    {
        var (vm, _, session, _) = Build(canEdit: () => false);
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            Assert.False(vm.RotateSelectedCommand.CanExecute(null));
        }
    }

    [Fact] // M4 (revisão Opus, paridade com Delete) — "seleção limpa pós-op" (brief) também vale pra
    // Rotate: OPERAÇÃO TERMINAL (ao contrário de Mover — ver I3/ClearsSelection... abaixo, que RE-
    // SELECIONA na posição nova de propósito), consequência do rebuild em OnSessionApplied.
    public async Task RotateSelectedCommand_ClearsSelectionAfterSuccess()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            Assert.True(vm.HasSelection);

            await vm.RotateSelectedCommand.ExecuteAsync(null);

            Assert.False(vm.HasSelection);
            Assert.Empty(vm.SelectedIndexes);
        }
    }

    // ---- I1 (revisão final pré-merge, Plano 3b): pino de "edição em voo" ------------------------------
    //
    // PROVA DA MUTAÇÃO do relatório (rodada final da revisão): clicar Excluir enquanto Girar ainda
    // está no Task.Run capturava `pdfAntes` de CADA comando no momento do clique — se a exclusão
    // terminasse ANTES da rotação (ou vice-versa), `_session.ApplyEdit` do que terminasse DEPOIS
    // sobrescrevia o resultado do outro por cima, sem exceção nem aviso (0 páginas giradas, 0 erros,
    // exatamente como medido pelo relatório). Verificado ao vivo (RED->GREEN): comentar as 2 linhas
    // `!_editInFlight` em `CanOperateOnSelection` (`OrganizerViewModel.cs`) faz o teste abaixo falhar —
    // `DeleteSelectedCommand.CanExecute(null)` volta a `true` com Rotate ainda em voo (mutação NÃO
    // commitada, só rodada manualmente durante a implementação — ver task-6-fixes-report.md).
    [Fact]
    public async Task RotateSelectedCommand_InFlight_BlocksOtherMutatorCommands()
    {
        var (vm, fake, session, _) = Build(); // 30 páginas
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            fake.RotatePagesGate = new TaskCompletionSource<bool>();

            var rotateTask = vm.RotateSelectedCommand.ExecuteAsync(null); // NÃO aguardado ainda — trava no gate

            // `SetEditInFlight(true)` roda SÍNCRONO, antes do 1º `await` dentro de RotateSelected — por
            // isso as checagens abaixo são determinísticas, sem precisar de Pump/sleep (mesmo raciocínio
            // documentado no comentário do teste).
            Assert.False(vm.DeleteSelectedCommand.CanExecute(null)); // ESTE é o cenário exato do relatório
            Assert.False(vm.RotateSelectedCommand.CanExecute(null)); // AsyncRelayCommand já bloqueava a SI MESMO — continua bloqueado
            Assert.False(vm.InsertCommand.CanExecute(null));
            Assert.False(vm.MoveSelectionRightCommand.CanExecute(null)); // só 1 selecionada, dentro dos limites — SÓ o pino bloqueia
            Assert.False(vm.ExtractSelectedCommand.CanExecute(null)); // lê o MESMO snapshot em voo — também bloqueado (ver doc XML)

            fake.RotatePagesGate.SetResult(true);
            await rotateTask;

            // pino solto -> tudo reabilitado (Rotate/Delete/Move terminais limpam a seleção — ver
            // RotateSelectedCommand_ClearsSelectionAfterSuccess acima — então re-seleciono pra medir
            // Delete/Insert/Extract com HasSelection=true de novo).
            Assert.True(vm.InsertCommand.CanExecute(null)); // não depende de seleção
            vm.ToggleSelect(0, ctrl: false);
            Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
            Assert.True(vm.ExtractSelectedCommand.CanExecute(null));
        }
    }

    // ---- DeleteSelected -----------------------------------------------------------------------------

    [Fact] // MUTAÇÃO (relatório): comentar `_session.ApplyEdit(pdfDepois)` dentro de `TryApplyEdit` faz
    // este teste falhar (snapshot nunca troca, PageCount nunca muda) — verificado ao vivo durante a
    // implementação (RED->GREEN), ver task-3-report.md.
    public async Task DeleteSelectedCommand_SendsIndexes_AppliesEdit_ChangesSnapshot()
    {
        var (vm, fake, session, _) = Build();
        fake.DeletePagesResult = Fixtures.A4(); // "bytes-marcador": 30 -> 1 página, contraste bem distinto
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            vm.ToggleSelect(1, ctrl: true);
            vm.ToggleSelect(2, ctrl: true);
            var before = session.Snapshot;

            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            Assert.Equal(new[] { 0, 1, 2 }, fake.LastDeletePageIndexes!.OrderBy(i => i));
            Assert.NotSame(before, session.Snapshot);
            Assert.Equal(1, session.Renderer.PageCount);
        }
    }

    [Fact] // "seleção limpa pós-op" (brief) — consequência do rebuild em OnSessionApplied, não uma
    // chamada explícita (ver doc XML de OrganizerViewModel.OnSessionApplied).
    public async Task DeleteSelectedCommand_ClearsSelectionAfterSuccess()
    {
        var (vm, fake, session, _) = Build();
        fake.DeletePagesResult = Fixtures.A4();
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            Assert.True(vm.HasSelection);

            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            Assert.False(vm.HasSelection);
            Assert.Empty(vm.SelectedIndexes);
        }
    }

    [Fact] // "excluir TODAS bloqueado com aviso pt-BR" (brief) — o motor (PdfEditor.DeletePages) já
    // recusa; o VM só precisa REPASSAR a mensagem e não avançar pro ApplyEdit. Simulado via
    // ThrowOnDeletePages (não precisa do editor REAL — esse caminho já tem teste dedicado no Task 2).
    public async Task DeleteSelectedCommand_AllPagesSelected_BlockedWithNotice_SnapshotUnchanged()
    {
        var (vm, fake, session, errors) = Build("fixture-a4.pdf"); // 1 página = fácil selecionar TODAS
        fake.ThrowOnDeletePages = new ArgumentException("Não é possível excluir todas as páginas do documento.");
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            var before = session.Snapshot;

            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            Assert.Contains(errors, e => e.Contains("excluir todas"));
            Assert.Same(before, session.Snapshot); // ApplyEdit NUNCA rodou
        }
    }

    [Fact] // "undo funciona" (brief): delete -> CanUndo -> Undo restaura a contagem de páginas.
    public async Task DeleteSelectedCommand_ThenUndo_RestoresPageCount()
    {
        var (vm, fake, session, _) = Build();
        fake.DeletePagesResult = Fixtures.A4();
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            vm.ToggleSelect(1, ctrl: true);
            vm.ToggleSelect(2, ctrl: true);

            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            Assert.Equal(1, session.Renderer.PageCount);
            Assert.True(session.CanUndo);

            session.Undo();

            Assert.Equal(30, session.Renderer.PageCount);
        }
    }

    [Fact]
    public void DeleteSelectedCommand_CanExecute_FalseWhenCanEditFalse()
    {
        var (vm, _, session, _) = Build(canEdit: () => false);
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
        }
    }

    // ---- Mover (botões ◀/▶ — decisão v1, ver doc XML da classe) --------------------------------------

    [Fact]
    public async Task MoveSelectionRightCommand_SendsFromAndToPlusOne_AppliesEdit()
    {
        var (vm, fake, session, _) = Build();
        using (session) using (vm)
        {
            vm.ToggleSelect(3, ctrl: false);
            var before = session.Snapshot;

            await vm.MoveSelectionRightCommand.ExecuteAsync(null);

            Assert.Equal(3, fake.LastMoveFromIndex);
            Assert.Equal(4, fake.LastMoveToIndex);
            Assert.Equal(1, fake.MovePageCallCount);
            Assert.NotSame(before, session.Snapshot);
        }
    }

    [Fact]
    public async Task MoveSelectionLeftCommand_SendsFromAndToMinusOne_AppliesEdit()
    {
        var (vm, fake, session, _) = Build();
        using (session) using (vm)
        {
            vm.ToggleSelect(3, ctrl: false);

            await vm.MoveSelectionLeftCommand.ExecuteAsync(null);

            Assert.Equal(3, fake.LastMoveFromIndex);
            Assert.Equal(2, fake.LastMoveToIndex);
        }
    }

    [Fact]
    public void MoveCommands_CanExecute_FalseWithoutExactlyOneSelected()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            Assert.False(vm.MoveSelectionLeftCommand.CanExecute(null));
            Assert.False(vm.MoveSelectionRightCommand.CanExecute(null));

            vm.ToggleSelect(3, ctrl: false);
            vm.ToggleSelect(5, ctrl: true); // 2 selecionadas agora

            Assert.False(vm.MoveSelectionLeftCommand.CanExecute(null));
            Assert.False(vm.MoveSelectionRightCommand.CanExecute(null));
        }
    }

    [Fact] // limites do documento: 1ª página não pode mover pra esquerda, última não pode mover pra direita.
    public void MoveCommands_CanExecute_FalseAtDocumentBoundaries()
    {
        var (vm, _, session, _) = Build(); // 30 páginas, índices 0..29
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            Assert.False(vm.MoveSelectionLeftCommand.CanExecute(null));
            Assert.True(vm.MoveSelectionRightCommand.CanExecute(null));

            vm.ToggleSelect(29, ctrl: false);
            Assert.True(vm.MoveSelectionLeftCommand.CanExecute(null));
            Assert.False(vm.MoveSelectionRightCommand.CanExecute(null));
        }
    }

    [Fact] // I3 (revisão Opus) — ASSIMETRIA DELIBERADA com Rotate/Delete (que LIMPAM a seleção — ver
    // RotateSelectedCommand_ClearsSelectionAfterSuccess/DeleteSelectedCommand_ClearsSelectionAfterSuccess
    // acima): Mover é um GESTO DE POSICIONAMENTO, não uma operação terminal — o caso de uso real é
    // repetir o clique várias vezes seguidas (ex.: página 3 -> 20 = 17 cliques em "Mover ▶"). Sem
    // re-selecionar a página na posição NOVA depois de cada clique, o usuário precisaria re-selecionar
    // manualmente entre um clique e outro (17 cliques + 17 re-seleções). A ANTIGA versão deste teste
    // afirmava "seleção limpa" — comportamento CORRIGIDO pela revisão (era a UX-quebrante que o
    // relatório do fix documenta).
    public async Task MoveSelectionRightCommand_ReSelectsPageAtNewIndexAfterSuccess()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            vm.ToggleSelect(3, ctrl: false);

            await vm.MoveSelectionRightCommand.ExecuteAsync(null);

            Assert.Equal(new[] { 4 }, vm.SelectedIndexes);
            Assert.True(vm.HasSelection);
        }
    }

    [Fact] // mesma asserção pro sentido contrário — prova que os DOIS botões re-selecionam (não só um).
    public async Task MoveSelectionLeftCommand_ReSelectsPageAtNewIndexAfterSuccess()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            vm.ToggleSelect(3, ctrl: false);

            await vm.MoveSelectionLeftCommand.ExecuteAsync(null);

            Assert.Equal(new[] { 2 }, vm.SelectedIndexes);
        }
    }

    [Fact] // prova o caso de uso REAL que motivou I3: repetir "Mover ▶" várias vezes seguidas, sem
    // re-selecionar manualmente entre um clique e outro, desloca a MESMA página N posições.
    public async Task MoveSelectionRightCommand_RepeatedClicks_KeepsMovingSamePageWithoutManualReselect()
    {
        var (vm, _, session, _) = Build(); // 30 páginas
        using (session) using (vm)
        {
            vm.ToggleSelect(3, ctrl: false);

            for (int i = 0; i < 3; i++)
            {
                Assert.True(vm.MoveSelectionRightCommand.CanExecute(null)); // CanExecute continua vivo
                await vm.MoveSelectionRightCommand.ExecuteAsync(null);
            }

            Assert.Equal(new[] { 6 }, vm.SelectedIndexes); // 3 -> 4 -> 5 -> 6, sem nenhum ToggleSelect manual
        }
    }

    // ---- falhas tipadas -----------------------------------------------------------------------------

    [Fact]
    public async Task RotateSelectedCommand_SignedDocument_NotifiesAndDoesNotApplyEdit()
    {
        var (vm, fake, session, errors) = Build();
        fake.ThrowOnRotatePages = new PdfSignedDocumentException("Documento contém assinaturas — edição bloqueada.");
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            var before = session.Snapshot;

            await vm.RotateSelectedCommand.ExecuteAsync(null);

            Assert.Contains(errors, e => e.Contains("assinado") || e.Contains("assinatura"));
            Assert.Same(before, session.Snapshot);
        }
    }

    // ---- Extrair (Task 4, Plano 3b) -------------------------------------------------------------------

    [Fact] // índices certos enviados ao motor + arquivo escrito no disco é um PDF REAL (via renderer,
    // não só bytes crus) com a contagem de página do "bytes-marcador" configurado no fake.
    public async Task ExtractSelectedCommand_WritesRealPdfFile_WithSelectedIndexes()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-organizer-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var savePath = Path.Combine(tmpDir, "extraido.pdf");
        var dialogs = new FakeFileDialogService(saveResult: savePath);
        var infos = new List<string>();
        var (vm, fake, session, _) = Build(dialogs: dialogs, notifyInfo: infos.Add);
        try
        {
            using (session) using (vm)
            {
                fake.ExtractPagesResult = Fixtures.A4(); // 1 página — contraste distinto do doc de 30
                vm.ToggleSelect(2, ctrl: false);
                vm.ToggleSelect(5, ctrl: true);
                vm.ToggleSelect(9, ctrl: true);
                var before = session.Snapshot;

                await vm.ExtractSelectedCommand.ExecuteAsync(null);

                Assert.Equal(new[] { 2, 5, 9 }, fake.LastExtractPageIndexes!.OrderBy(i => i));
                Assert.Equal(1, fake.ExtractPagesCallCount);
                Assert.True(File.Exists(savePath));

                using (var written = DocumentSession.Open(savePath))
                    Assert.Equal(1, written.Renderer.PageCount); // é um PDF REAL, decodificável

                Assert.Same(before, session.Snapshot); // Extrair NÃO muta a sessão (não abre aba, não edita)
                Assert.Single(infos);
                Assert.Contains("3", infos[0]); // "3 páginas extraídas para extraido.pdf"
                Assert.Contains("extraido.pdf", infos[0]);
            }
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact] // seleção PRESERVADA pós-Extrair (assimetria com Rotate/Delete: não há rebuild de Pages, já
    // que a sessão nunca muda — nada de errado em continuar com a mesma seleção depois).
    public async Task ExtractSelectedCommand_DoesNotClearSelection()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-organizer-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var savePath = Path.Combine(tmpDir, "extraido.pdf");
        var dialogs = new FakeFileDialogService(saveResult: savePath);
        var (vm, _, session, _) = Build(dialogs: dialogs);
        try
        {
            using (session) using (vm)
            {
                vm.ToggleSelect(0, ctrl: false);

                await vm.ExtractSelectedCommand.ExecuteAsync(null);

                Assert.Equal(new[] { 0 }, vm.SelectedIndexes);
            }
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact] // diálogo CANCELADO (null) -> nenhuma chamada ao motor, nenhum arquivo escrito
    public async Task ExtractSelectedCommand_DialogCancelled_DoesNothing()
    {
        var dialogs = new FakeFileDialogService(saveResult: null);
        var (vm, fake, session, _) = Build(dialogs: dialogs);
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);

            await vm.ExtractSelectedCommand.ExecuteAsync(null);

            Assert.Equal(0, fake.ExtractPagesCallCount);
        }
    }

    [Fact]
    public void ExtractSelectedCommand_CanExecute_FalseWithoutSelection()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            Assert.False(vm.ExtractSelectedCommand.CanExecute(null));
        }
    }

    [Fact] // ExtractPages é leitura pura, SEM gate de assinatura no motor (ver Contract.cs) — diferente
    // de Rotate/Delete/Move/Inserir, continua habilitado mesmo com CanEdit=false (documento assinado).
    public void ExtractSelectedCommand_CanExecute_TrueEvenWhenCanEditFalse()
    {
        var (vm, _, session, _) = Build(canEdit: () => false);
        using (session) using (vm)
        {
            vm.ToggleSelect(0, ctrl: false);
            Assert.True(vm.ExtractSelectedCommand.CanExecute(null));
        }
    }

    // ---- C1 (revisão final pré-merge, Plano 3b): aviso "documento de origem assinado" ------------------

    [Fact] // fake reporta HasSignatures==true pra origem -> notificação de sucesso ganha o aviso extra.
    public async Task ExtractSelectedCommand_SourceSigned_AppendsUnsignedWarningToNotice()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-organizer-extract-sig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var savePath = Path.Combine(tmpDir, "extraido.pdf");
        var dialogs = new FakeFileDialogService(saveResult: savePath);
        var infos = new List<string>();
        var (vm, fake, session, _) = Build(dialogs: dialogs, notifyInfo: infos.Add);
        try
        {
            using (session) using (vm)
            {
                fake.HasSignaturesResult = true;
                vm.ToggleSelect(0, ctrl: false);

                await vm.ExtractSelectedCommand.ExecuteAsync(null);

                Assert.Single(infos);
                Assert.Contains("assinado", infos[0]);
                Assert.Contains("NÃO está assinado", infos[0]);
            }
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact] // fake reporta HasSignatures==false (default) -> notificação SEM o aviso extra.
    public async Task ExtractSelectedCommand_SourceUnsigned_DoesNotAppendWarningToNotice()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-organizer-extract-unsig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var savePath = Path.Combine(tmpDir, "extraido.pdf");
        var dialogs = new FakeFileDialogService(saveResult: savePath);
        var infos = new List<string>();
        var (vm, _, session, _) = Build(dialogs: dialogs, notifyInfo: infos.Add);
        try
        {
            using (session) using (vm)
            {
                vm.ToggleSelect(0, ctrl: false);

                await vm.ExtractSelectedCommand.ExecuteAsync(null);

                Assert.Single(infos);
                Assert.DoesNotContain("assinado", infos[0]);
            }
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact] // Rider: singular quando exatamente 1 página é extraída ("1 página extraída", não "1 páginas").
    public async Task ExtractSelectedCommand_SinglePage_UsesSingularNoticeText()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-organizer-extract-singular-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var savePath = Path.Combine(tmpDir, "extraido.pdf");
        var dialogs = new FakeFileDialogService(saveResult: savePath);
        var infos = new List<string>();
        var (vm, _, session, _) = Build(dialogs: dialogs, notifyInfo: infos.Add);
        try
        {
            using (session) using (vm)
            {
                vm.ToggleSelect(0, ctrl: false);

                await vm.ExtractSelectedCommand.ExecuteAsync(null);

                Assert.Single(infos);
                Assert.StartsWith("1 página extraída", infos[0]);
                Assert.DoesNotContain("1 páginas", infos[0]);
            }
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    // ---- Inserir (Task 4, Plano 3b) -------------------------------------------------------------------

    [Fact] // atIndex = logo APÓS a última página selecionada (não a primeira, não a contagem de itens)
    public async Task InsertCommand_WithSelection_AtIndexIsAfterLastSelected()
    {
        var sourcePath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var dialogs = new FakeFileDialogService(openResult: sourcePath);
        var (vm, fake, session, _) = Build(dialogs: dialogs); // 30 páginas
        using (session) using (vm)
        {
            vm.ToggleSelect(2, ctrl: false);
            vm.ToggleSelect(5, ctrl: true);
            vm.ToggleSelect(9, ctrl: true); // maior índice selecionado = 9

            await vm.InsertCommand.ExecuteAsync(null);

            Assert.Equal(10, fake.LastInsertAtIndex);
            Assert.Equal(1, fake.InsertPagesCallCount);
            Assert.Equal(Fixtures.A4(), fake.LastInsertSource);
        }
    }

    [Fact] // SEM seleção -> atIndex = FIM do documento (Pages.Count, 0-based == pageCount insere no fim)
    public async Task InsertCommand_NoSelection_AtIndexIsEnd()
    {
        var sourcePath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var dialogs = new FakeFileDialogService(openResult: sourcePath);
        var (vm, fake, session, _) = Build(dialogs: dialogs); // 30 páginas
        using (session) using (vm)
        {
            await vm.InsertCommand.ExecuteAsync(null);

            Assert.Equal(30, fake.LastInsertAtIndex);
        }
    }

    [Fact] // Inserir passa por ApplyEdit (não Apply direto) — undo funciona (brief: "via ApplyEdit").
    public async Task InsertCommand_ThenUndo_RestoresOriginalPageCount()
    {
        var sourcePath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var dialogs = new FakeFileDialogService(openResult: sourcePath);
        var (vm, fake, session, _) = Build("fixture-a4.pdf", dialogs: dialogs); // 1 página
        fake.InsertPagesResult = Fixtures.ThirtyPages(); // "bytes-marcador" contrastante (1 -> 30)
        using (session) using (vm)
        {
            await vm.InsertCommand.ExecuteAsync(null);

            Assert.Equal(30, session.Renderer.PageCount);
            Assert.True(session.CanUndo);

            session.Undo();

            Assert.Equal(1, session.Renderer.PageCount);
        }
    }

    [Fact] // diálogo CANCELADO (null) -> nenhuma chamada ao motor, sessão intacta
    public async Task InsertCommand_DialogCancelled_DoesNothing()
    {
        var dialogs = new FakeFileDialogService(openResult: null);
        var (vm, fake, session, _) = Build(dialogs: dialogs);
        using (session) using (vm)
        {
            var before = session.Snapshot;

            await vm.InsertCommand.ExecuteAsync(null);

            Assert.Equal(0, fake.InsertPagesCallCount);
            Assert.Same(before, session.Snapshot);
        }
    }

    [Fact]
    public void InsertCommand_CanExecute_FalseWhenCanEditFalse()
    {
        var (vm, _, session, _) = Build(canEdit: () => false);
        using (session) using (vm)
        {
            Assert.False(vm.InsertCommand.CanExecute(null));
        }
    }

    [Fact] // Inserir NÃO depende de seleção (diferente de Extrair) — habilitado mesmo sem nada selecionado
    public void InsertCommand_CanExecute_TrueWithoutSelection_WhenCanEditTrue()
    {
        var (vm, _, session, _) = Build();
        using (session) using (vm)
        {
            Assert.True(vm.InsertCommand.CanExecute(null));
        }
    }

    [Fact] // gate de assinatura do ALVO (defesa em profundidade — CanInsert já deveria ter barrado)
    public async Task InsertCommand_SignedDocument_NotifiesAndDoesNotApplyEdit()
    {
        var sourcePath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var dialogs = new FakeFileDialogService(openResult: sourcePath);
        var (vm, fake, session, errors) = Build(dialogs: dialogs);
        fake.ThrowOnInsertPages = new PdfSignedDocumentException("Documento contém assinaturas — edição bloqueada.");
        using (session) using (vm)
        {
            var before = session.Snapshot;

            await vm.InsertCommand.ExecuteAsync(null);

            Assert.Contains(errors, e => e.Contains("assinado") || e.Contains("assinatura"));
            Assert.Same(before, session.Snapshot);
        }
    }

    // ---- R2 (Rodada 2 da revisão pós-branch): "C1 wiring gap" — Inserir nunca avisava sobre ORIGEM
    // assinada, ao contrário de Extrair/Juntar/Dividir. `InsertPages` já tirava o widget visual da
    // origem (C1, Rodada 1) — só faltava o aviso pt-BR.

    [Fact] // fake reporta HasSignatures==true pra ORIGEM (o arquivo inserido) -> aviso pt-BR notificado.
    public async Task InsertCommand_SourceSigned_NotifiesUnsignedWarning()
    {
        var sourcePath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var dialogs = new FakeFileDialogService(openResult: sourcePath);
        var infos = new List<string>();
        var (vm, fake, session, _) = Build(dialogs: dialogs, notifyInfo: infos.Add);
        fake.HasSignaturesResult = true;
        using (session) using (vm)
        {
            await vm.InsertCommand.ExecuteAsync(null);

            Assert.Single(infos);
            Assert.Contains("assinado", infos[0]);
            Assert.Contains("NÃO estão assinadas", infos[0]);
        }
    }

    [Fact] // fake reporta HasSignatures==false (default) -> Inserir continua SILENCIOSO no sucesso
    // (nenhuma mudança de UX pro caso comum — Inserir nunca teve notificação de sucesso).
    public async Task InsertCommand_SourceUnsigned_StaysSilentOnSuccess()
    {
        var sourcePath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var dialogs = new FakeFileDialogService(openResult: sourcePath);
        var infos = new List<string>();
        var (vm, _, session, _) = Build(dialogs: dialogs, notifyInfo: infos.Add);
        using (session) using (vm)
        {
            await vm.InsertCommand.ExecuteAsync(null);

            Assert.Empty(infos);
        }
    }

    // ---- Task 2 (Plano 7): Inserir aceita imagens -- conversão ANTES de InsertPages (motor intocado) --
    //
    // Cada teste cria/apaga sua PRÓPRIA pasta temp (mesmo padrão try/finally de
    // ExtractSelectedCommand_SinglePage_UsesSingularNoticeText acima) -- nunca uma pasta compartilhada
    // entre testes, e nunca deixa lixo em %TEMP% entre execuções.

    private static string WriteTempImageFile(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF });
        return path;
    }

    [Fact] // caminho de imagem -- convertido via ImageToPdf ANTES de InsertPages; InsertPages recebe o
    // PDF resultante da conversão, NUNCA os bytes crus da imagem.
    public async Task InsertCommand_ImagePath_ConvertsBeforeInsertPages()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-organizer-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var imgPath = WriteTempImageFile(tmpDir, "pagina.png");
            var dialogs = new FakeFileDialogService(openResult: imgPath);
            var (vm, fake, session, _) = Build(dialogs: dialogs);
            fake.ImageToPdfResult = Fixtures.A4();
            using (session) using (vm)
            {
                await vm.InsertCommand.ExecuteAsync(null);

                Assert.Equal(1, fake.ImageToPdfCallCount);
                Assert.Equal(File.ReadAllBytes(imgPath), fake.ImageToPdfInputs[0]);
                Assert.Equal(1, fake.InsertPagesCallCount);
                Assert.Equal(Fixtures.A4(), fake.LastInsertSource); // convertido, não os bytes crus da imagem
            }
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact] // caminho .pdf continua o fluxo normal -- NENHUMA chamada a ImageToPdf (invariante preservado)
    public async Task InsertCommand_PdfPath_DoesNotCallImageConversion()
    {
        var sourcePath = Path.Combine(Fixtures.Root, "fixture-a4.pdf");
        var dialogs = new FakeFileDialogService(openResult: sourcePath);
        var (vm, fake, session, _) = Build(dialogs: dialogs);
        using (session) using (vm)
        {
            await vm.InsertCommand.ExecuteAsync(null);

            Assert.Equal(0, fake.ImageToPdfCallCount);
            Assert.Equal(1, fake.InsertPagesCallCount);
        }
    }

    [Fact] // conversão FALHA -- notificado pt-BR nomeando o arquivo, InsertPages NUNCA chamado, sessão intacta
    public async Task InsertCommand_ImageConversionFails_NotifiesError_DoesNotInsert()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-organizer-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var imgPath = WriteTempImageFile(tmpDir, "quebrada.jpg");
            var dialogs = new FakeFileDialogService(openResult: imgPath);
            var (vm, fake, session, errors) = Build(dialogs: dialogs);
            fake.ThrowOnImageToPdf = new PdfEditingException("Imagem corrompida.");
            using (session) using (vm)
            {
                var before = session.Snapshot;

                await vm.InsertCommand.ExecuteAsync(null);

                Assert.Equal(0, fake.InsertPagesCallCount);
                Assert.Same(before, session.Snapshot);
                Assert.Contains(errors, e => e.Contains("quebrada.jpg"));
            }
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact] // HasSignatures roda normalmente sobre o PDF CONVERTIDO (fluxo uniforme, sem caso especial) --
    // nunca lança, nunca é pulado; uma imagem convertida não tem assinatura -> sem aviso.
    public async Task InsertCommand_ImagePath_HasSignaturesRunsUniformlyOnConvertedBytes_NoWarning()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mpdf-organizer-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var imgPath = WriteTempImageFile(tmpDir, "pagina.jpg");
            var dialogs = new FakeFileDialogService(openResult: imgPath);
            var infos = new List<string>();
            var (vm, fake, session, _) = Build(dialogs: dialogs, notifyInfo: infos.Add);
            fake.ImageToPdfResult = Fixtures.A4();
            using (session) using (vm)
            {
                await vm.InsertCommand.ExecuteAsync(null);

                Assert.Equal(1, fake.HasSignaturesCallCount);
                Assert.Empty(infos);
            }
        }
        finally { Directory.Delete(tmpDir, true); }
    }
}
