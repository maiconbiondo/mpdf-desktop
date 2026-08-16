using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mPdf.App.Services;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Rendering;

namespace mPdf.App.ViewModels;

/// Organizador de páginas (Task 3, Plano 3b) — grade de miniaturas GRANDES com multi-seleção e girar/
/// excluir/mover, tudo pelo motor de página do Task 2 (`IPdfEditor.RotatePages`/`DeletePages`/
/// `MovePage`) + `DocumentSession.ApplyEdit` (undo/redo de graça).
///
/// RENDERER PRÓPRIO (decisão registrada no relatório da task): NÃO reusa o renderer/scheduler de
/// miniaturas do painel lateral (`DocumentViewModel._thumbnailRenderer`, escala 0.2 FIXA) nem cria um
/// segundo scheduler sobre ele — o cache de render-reader de `PdfDocumentRenderer` é de ESCALA ÚNICA
/// (mesmo contrato já documentado na classe), então uma escala nova (0.35) exige um `PdfDocumentRenderer`
/// PRÓPRIO, exemplar direto do trio miniaturas em `DocumentViewModel` (renderer dedicado + scheduler
/// dedicado + rebuild em `Session.Applied`). CICLO DE VIDA: criado quando o organizador ABRE
/// (`DocumentViewModel.IsOrganizerOpen = true`) e descartado quando FECHA (`Dispose` abaixo, via
/// `PendingDisposals` — mesmo padrão de descarte serial do resto do app) — ao contrário do renderer de
/// miniaturas (vive pela vida inteira do documento), o organizador é um modo OCASIONAL: manter um
/// 3º reader nativo do PDFium aberto o tempo todo, mesmo quando o organizador nunca é aberto, seria
/// custo sem benefício.
///
/// DRAG-REORDER: v1 usa BOTÕES (`MoveSelectionLeftCommand`/`MoveSelectionRightCommand`), não
/// arrasto — decisão registrada no relatório (o próprio plano já previa este fallback: "se a grade
/// brigar, botões mover ↑↓ na v1 com drag como melhoria"). `MovePage` ainda é exercitado via
/// `ApplyEdit`, só o GATILHO na UI é botão em vez de gesto de arrasto.
public sealed partial class OrganizerViewModel : ObservableObject, IDisposable
{
    private readonly DocumentSession _session;
    private readonly IPdfEditor _editor;
    private readonly Action<string> _notifyError;
    private readonly Func<bool> _canEdit;
    // Task 4 (Plano 3b): Extrair/Inserir precisam de diálogos de arquivo (Salvar como/Abrir) e de um
    // canal de SUCESSO (distinto de _notifyError) — mesmo padrão de injeção dos campos acima, ambos
    // opcionais com default de produção pra não quebrar `OrganizerViewModelTests` pré-existentes (só 4
    // argumentos, sem estes 2).
    private readonly IFileDialogService _dialogs;
    private readonly Action<string> _notifyInfo;
    private readonly RenderScheduler _scheduler;
    private PdfDocumentRenderer _renderer;
    private bool _disposed;

    // Rodada 2 (revisão pós-branch, "R1 refutado" — o pino LOCAL da Rodada 1 não era exclusão mútua
    // de verdade, ver DocumentSession.IsEditInFlight/TryBeginEdit/EndEdit): o pino agora vive em
    // `_session` (compartilhado com `DocumentViewModel`, que edita o MESMO documento via anotações/
    // carimbos enquanto o organizador pode estar aberto) — este VM só CONSOME `_session.IsEditInFlight`
    // nas `CanExecute` abaixo e arma/solta via `_session.TryBeginEdit()`/`_session.EndEdit()` ao redor
    // de CADA operação mutadora, SINCRONAMENTE antes do 1º `await` (Insert: logo após o diálogo de
    // arquivo, ANTES da leitura — furo (b) do relatório da revisão, ver `Insert` abaixo).

    public ObservableCollection<OrganizerPageViewModel> Pages { get; } = [];

    /// Índices (0-based) das páginas atualmente selecionadas, na ordem crescente — fonte única de
    /// verdade é `OrganizerPageViewModel.IsSelected` (não uma 2ª coleção paralela que pudesse
    /// dessincronizar); esta propriedade só FILTRA `Pages` a cada leitura, O(n) mas n é o nº de páginas
    /// do documento, nunca alto o bastante pra importar num clique de usuário.
    public IReadOnlyList<int> SelectedIndexes =>
        Pages.Where(p => p.IsSelected).Select(p => p.Index).ToList();

    public bool HasSelection => SelectedIndexes.Count > 0;

    public OrganizerViewModel(
        DocumentSession session,
        IPdfEditor editor,
        Action<string> notifyError,
        Func<bool> canEdit,
        IFileDialogService? dialogs = null,
        Action<string>? notifyInfo = null)
    {
        _session = session;
        _editor = editor;
        _notifyError = notifyError;
        _canEdit = canEdit;
        // Task 0 (Plano 3c): defaults vêm do seam `UiPrompts` — ver doc XML de UiPrompts.
        _dialogs = dialogs ?? UiPrompts.CreateFileDialog();
        _notifyInfo = notifyInfo ?? UiPrompts.NotifyInfo;
        _renderer = new PdfDocumentRenderer(_session.Snapshot);
        _scheduler = new RenderScheduler((pageIndex, scale) => _renderer.RenderPage(pageIndex, scale));
        BuildPages();
        // Refresh em Applied (brief: "o organizador deve refletir edições") — cobre TANTO edições
        // feitas pelo PRÓPRIO organizador (Rotate/Delete/Move abaixo) quanto qualquer edição feita
        // pelo leitor (anotação) enquanto o organizador está aberto, e Undo/Redo alheio. Desinscrito
        // em Dispose — ver lá.
        _session.Applied += OnSessionApplied;
        // Rodada 2: o pino agora é da SESSÃO (compartilhado com DocumentViewModel) — uma edição
        // ARMADA pelo LEITOR (ex.: colocar um carimbo) precisa desabilitar os comandos do organizador
        // tanto quanto uma armada aqui mesmo. Desinscrito em Dispose.
        _session.EditInFlightChanged += OnEditInFlightChanged;
    }

    private void BuildPages()
    {
        for (int i = 0; i < _session.Renderer.PageCount; i++)
            Pages.Add(new OrganizerPageViewModel(i, _session.PageSizes[i], _scheduler));
    }

    private void OnSessionApplied(object? sender, EventArgs e)
    {
        _scheduler.CancelPending();
        var oldRenderer = _renderer;
        _renderer = new PdfDocumentRenderer(_session.Snapshot);
        PendingDisposals.Enqueue(() => oldRenderer.Dispose());

        Pages.Clear();
        BuildPages();
        // Seleção limpa como CONSEQUÊNCIA do rebuild (novos OrganizerPageViewModel nascem com
        // IsSelected=false por padrão) — NENHUMA chamada explícita de "limpar seleção" aqui de
        // propósito (mesmo ACHADO já documentado em DocumentViewModel.ApplyMarkup: uma chamada extra
        // seria código MORTO, sempre redundante com este handler). É esta mesma redundância que a
        // PROVA DE MUTAÇÃO do relatório explora: comentar o `_session.ApplyEdit` dentro de
        // `TryApplyEdit` faz `Session.Applied` nunca disparar -> este handler nunca roda -> Pages
        // nunca reconstrói -> a seleção antiga sobrevive -> o teste de "seleção limpa pós-op" falha.
        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        RotateSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        MoveSelectionLeftCommand.NotifyCanExecuteChanged();
        MoveSelectionRightCommand.NotifyCanExecuteChanged();
        // Task 4 (Plano 3b): CanExtractSelection depende de HasSelection (não de _canEdit — ver doc XML
        // de ExtractSelected) — precisa reavaliar no MESMO ponto que os 4 comandos acima. InsertCommand
        // NÃO entra aqui: seu CanExecute (CanInsert) só depende de _canEdit, nunca de seleção.
        ExtractSelectedCommand.NotifyCanExecuteChanged();
    }

    /// Rodada 2: reavalia `CanExecute` dos 6 comandos mutadores sempre que `_session.IsEditInFlight`
    /// muda (armado ou solto — pelo PRÓPRIO organizador OU pelo leitor, `DocumentViewModel`, que agora
    /// compartilha o MESMO pino) — os 5 já cobertos por `NotifySelectionChanged` (Rotate/Delete/
    /// Move×2/Extract) + `InsertCommand` (não depende de seleção, por isso `NotifySelectionChanged`
    /// sozinho não o cobre).
    private void OnEditInFlightChanged(object? sender, EventArgs e)
    {
        NotifySelectionChanged();
        InsertCommand.NotifyCanExecuteChanged();
    }

    /// Clique/Ctrl+clique (brief) numa miniatura do organizador, chamado pela View (`PageOrganizerView`).
    /// Clique simples: seleção vira EXATAMENTE {index} (substitui, mesmo padrão de seleção de ícone sem
    /// modificador em qualquer explorador de arquivos). Ctrl+clique: alterna a MEMBRESIA de `index` na
    /// seleção corrente, preservando o resto — multi-seleção incremental.
    public void ToggleSelect(int index, bool ctrl)
    {
        if (index < 0 || index >= Pages.Count) return;
        if (ctrl)
        {
            Pages[index].IsSelected = !Pages[index].IsSelected;
        }
        else
        {
            foreach (var p in Pages) p.IsSelected = false;
            Pages[index].IsSelected = true;
        }
        NotifySelectionChanged();
    }

    // Rodada 2: `!_session.IsEditInFlight` composto em TODAS as CanExecute de comandos mutadores — o
    // pino agora é COMPARTILHADO com DocumentViewModel (ver doc XML do ctor).
    private bool CanOperateOnSelection() => _canEdit() && HasSelection && !_session.IsEditInFlight;

    /// ↻ Girar 90° (brief) — gira TODA a seleção de uma vez, uma única chamada a `RotatePages` (o
    /// motor do Task 2 já deduplica/valida todos os índices antes de mutar qualquer página).
    ///
    /// `_session.TryBeginEdit()` (Rodada 2) é a 1ª linha do corpo — antes de QUALQUER `await` — e
    /// `false` faz o método retornar imediatamente, sem tocar nada: defesa em profundidade contra o
    /// MESMO cenário que `CanOperateOnSelection` já deveria ter barrado (chamada direta ao comando,
    /// contornando `CanExecute` — é exatamente assim que os testes da Rodada 2 provam o funil, não só
    /// o gate de UI). `try`/`finally` garante que `EndEdit()` sempre solta o pino.
    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    private async Task RotateSelected()
    {
        if (!_session.TryBeginEdit()) return;
        try
        {
            var indexes = SelectedIndexes;
            byte[] pdfAntes = _session.Snapshot;
            byte[]? pdfDepois = await TryRunEditAsync(() => _editor.RotatePages(pdfAntes, indexes, 90));
            if (pdfDepois is null) return;
            TryApplyEdit(pdfDepois);
        }
        finally { _session.EndEdit(); }
    }

    /// 🗑 Excluir (brief) — "excluir TODAS bloqueado com aviso pt-BR" não precisa de checagem própria
    /// aqui: `PdfEditor.DeletePages` já recusa com `ArgumentException` pt-BR
    /// ("Não é possível excluir todas as páginas do documento.") quando a seleção cobre o documento
    /// inteiro — `TryRunEditAsync` abaixo captura essa exceção e notifica a MESMA mensagem, sem
    /// duplicar a regra no App (single source of truth: o motor de edição).
    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    private async Task DeleteSelected()
    {
        if (!_session.TryBeginEdit()) return; // Rodada 2 — ver doc XML de RotateSelected
        try
        {
            var indexes = SelectedIndexes;
            byte[] pdfAntes = _session.Snapshot;
            byte[]? pdfDepois = await TryRunEditAsync(() => _editor.DeletePages(pdfAntes, indexes));
            if (pdfDepois is null) return;
            TryApplyEdit(pdfDepois);
        }
        finally { _session.EndEdit(); }
    }

    // ---- Task 4 (Plano 3b): Extrair (SEM gate CanEdit — ExtractPages é leitura pura, funciona mesmo em
    // documento ASSINADO, ver decisão em Contract.cs) e Inserir (COM gate CanEdit — InsertPages muta o
    // documento ALVO) --------------------------------------------------------------------------------

    /// SEM `_canEdit()`, DE PROPÓSITO (assimetria com `CanOperateOnSelection` acima): `ExtractPages` não
    /// tem gate de assinatura no motor (política única com Merge/Split — ver `Contract.cs`) — o
    /// documento de ORIGEM nunca é mutado, só lido pra produzir um arquivo NOVO ao lado. Bloquear o
    /// botão num documento assinado seria uma restrição sem correspondência no motor. `!IsEditInFlight`
    /// (Rodada 2) SIM entra aqui, apesar de Extrair não mutar `_session`: ela LÊ `_session.Snapshot` no
    /// mesmo instante que Rotate/Delete/Move/Insert (E agora qualquer edição do LEITOR) mutam — rodar
    /// em paralelo arriscaria extrair páginas do snapshot ERRADO (pré-edição).
    private bool CanExtractSelection() => HasSelection && !_session.IsEditInFlight;

    /// 📤 Extrair (brief) — grava as páginas selecionadas como um documento NOVO ao lado (SaveFileDialog),
    /// sem abrir aba nova (decisão do brief: "não abre aba"). Sufixo do nome sugerido nomeia a ação
    /// ("(extraído)") — mesmo espírito de `MainViewModel.BuildEditableCopyPath` (nome derivado do
    /// original), mas SEM lógica de colisão própria aqui: `SaveFileDialog` já pergunta antes de
    /// sobrescrever um arquivo existente (comportamento nativo do Win32), diferente de EditCopy/
    /// StampGallery (que gravam SEM diálogo, por isso precisam de sufixo automático).
    ///
    /// C1 (revisão final pré-merge): `ExtractPages` agora tira o widget visual da assinatura da
    /// ORIGEM antes de copiar (ver `PdfEditor.ExtractPages`) — o arquivo gerado NUNCA fica com um
    /// carimbo "assinado" órfão, mas se a origem ESTAVA assinada, o resultado sai genuinamente SEM
    /// assinatura nenhuma (diferente do documento original) e o usuário precisa saber disso pra não
    /// levar o arquivo errado pro protocolo/PJe/e-SAJ pensando que ainda está assinado.
    [RelayCommand(CanExecute = nameof(CanExtractSelection))]
    private async Task ExtractSelected()
    {
        var indexes = SelectedIndexes;
        if (_dialogs.PickPdfToSave(SuggestExtractFileName()) is not { } path) return; // diálogo é SÍNCRONO — nenhum await antes do arm abaixo

        if (!_session.TryBeginEdit()) return; // Rodada 2 — ver doc XML de RotateSelected
        try
        {
            byte[] pdfAntes = _session.Snapshot;
            bool sourceWasSigned;
            try { sourceWasSigned = await Task.Run(() => _editor.HasSignatures(pdfAntes)); }
            catch (PdfEditingException ex) { _notifyError(ex.Message); return; }

            byte[]? extracted = await TryRunEditAsync(() => _editor.ExtractPages(pdfAntes, indexes));
            if (extracted is null) return;

            try { await Task.Run(() => DocumentSession.WriteNewFile(path, extracted)); }
            catch (Exception ex) { _notifyError(ex.Message); return; }

            string countText = indexes.Count == 1 ? "1 página extraída" : $"{indexes.Count} páginas extraídas";
            string message = $"{countText} para {Path.GetFileName(path)}.";
            if (sourceWasSigned)
                message += " Atenção: o documento de origem estava assinado. O arquivo gerado NÃO está assinado.";
            _notifyInfo(message);
        }
        finally { _session.EndEdit(); }
    }

    private string SuggestExtractFileName() =>
        $"{Path.GetFileNameWithoutExtension(_session.FileName)} (extraído).pdf";

    /// COM `_canEdit()` (diferente de Extrair acima): `InsertPages` MUTA o documento ALVO (`_session`),
    /// mesmo gate de Rotate/Delete/Move. `!IsEditInFlight` (Rodada 2) idem.
    private bool CanInsert() => _canEdit() && !_session.IsEditInFlight;

    /// ➕ Inserir (brief) — OpenFileDialog escolhe o PDF de origem; TODAS as páginas dele entram no
    /// documento corrente a partir de `atIndex` = logo APÓS a última página selecionada, ou no FIM se
    /// nada estiver selecionado (mesma convenção 0-based de `IPdfEditor.InsertPages`: `atIndex ==
    /// pageCount` insere no fim). Passa por `TryApplyEdit` (não `TryRunEditAsync` sozinho) — Inserir
    /// PRECISA entrar no undo/redo da sessão (brief: "via ApplyEdit"), diferente de Extrair (que nunca
    /// muta `_session`, não tem o que desfazer).
    ///
    /// FURO (b) da Rodada 2 (achado do painel de revisão, PROVADO empiricamente): a Rodada 1 armava o
    /// pino só DEPOIS do `await Task.Run(() => File.ReadAllBytes(path))` — a leitura de um PDF grande/
    /// numa pasta de rede pode levar SEGUNDOS, e durante esse tempo Girar/Excluir/Mover/Extrair ficavam
    /// livres pra rodar (probe do painel: Insert + Rotate durante a leitura -> 31 páginas, rotação 0,
    /// nenhum erro). Fix: `TryBeginEdit()` logo APÓS `PickPdfToOpen()` (diálogo SÍNCRONO — sem `await`
    /// antes dele), ANTES do `await Task.Run(File.ReadAllBytes)` — cobre a leitura inteira, não só o
    /// `InsertPages` no fim.
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private async Task Insert()
    {
        if (_dialogs.PickPdfToOpen() is not { } path) return; // SÍNCRONO — nenhum await antes do arm abaixo

        if (!_session.TryBeginEdit()) return; // ARMADO AQUI — antes do 1º await (a leitura do arquivo)
        try
        {
            // Task 2 (Plano 7): `path` pode ser uma IMAGEM (o mesmo `PickPdfToOpen` agora aceita
            // *.jpg/*.jpeg/*.png — filtro compartilhado com `MainViewModel.OpenFile`) — converte pra PDF
            // ANTES de tudo abaixo (motor intocado, `InsertPages` só enxerga PDF). Aviso de assinatura de
            // origem (logo abaixo) continua UNIFORME de propósito: uma imagem convertida É um PDF válido
            // sem nenhuma assinatura, então `HasSignatures` sobre ela devolve `false` normalmente — sem
            // caso especial nenhum pra pular a checagem (decisão registrada no relatório).
            byte[] source;
            try
            {
                source = ImageImport.IsImagePath(path)
                    ? await ImageImport.ConvertToPdfAsync(path, _editor)
                    : await Task.Run(() => File.ReadAllBytes(path));
            }
            catch (Exception ex) { _notifyError(ex.Message); return; }

            // R2 (revisão pós-branch, "C1 wiring gap"): `InsertPages` já tira o widget visual de
            // assinatura da ORIGEM antes de copiar (ver `PdfEditor.InsertPages`) — igual a Extrair/
            // Juntar/Dividir, mas esta chamada NUNCA ganhou o aviso pt-BR correspondente. Fix: MESMO
            // padrão (checa `HasSignatures` na origem ANTES da operação, avisa se `true`) — só que aqui
            // não há um "arquivo gerado" (Inserir muta o documento ALVO já aberto), então a notificação
            // só dispara QUANDO há algo a avisar (Inserir normalmente é silencioso no sucesso — nenhuma
            // mudança de UX pro caso comum).
            bool sourceWasSigned;
            try { sourceWasSigned = await Task.Run(() => _editor.HasSignatures(source)); }
            catch (PdfEditingException ex) { _notifyError(ex.Message); return; }

            int atIndex = SelectedIndexes.Count > 0 ? SelectedIndexes.Max() + 1 : Pages.Count;
            byte[] pdfAntes = _session.Snapshot;
            byte[]? pdfDepois = await TryRunEditAsync(() => _editor.InsertPages(pdfAntes, source, atIndex));
            if (pdfDepois is null) return;
            if (!TryApplyEdit(pdfDepois)) return; // falha já notificada dentro — nenhum aviso extra sobre um insert que NÃO aconteceu

            if (sourceWasSigned)
                _notifyInfo("Atenção: o documento inserido estava assinado. As páginas inseridas NÃO estão assinadas.");
        }
        finally { _session.EndEdit(); }
    }

    /// Exatamente 1 página selecionada (mover múltiplas de uma vez não tem uma semântica óbvia de
    /// ordem — fora de escopo v1) e dentro dos limites (não pode mover a 1ª pra trás nem a última pra
    /// frente) — CanExecute cobre os 2 casos, então os botões ficam genuinamente desabilitados nos
    /// extremos (não é um no-op silencioso escondido atrás de um botão clicável).
    // `!IsEditInFlight` (Rodada 2) composto aqui também — ver doc XML do ctor.
    private bool CanMoveLeft() => _canEdit() && !_session.IsEditInFlight && SelectedIndexes is [var idx] && idx > 0;
    private bool CanMoveRight() => _canEdit() && !_session.IsEditInFlight && SelectedIndexes is [var idx] && idx < Pages.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveLeft))]
    private async Task MoveSelectionLeft()
    {
        int idx = SelectedIndexes[0];
        await MoveAsync(idx, idx - 1);
    }

    [RelayCommand(CanExecute = nameof(CanMoveRight))]
    private async Task MoveSelectionRight()
    {
        int idx = SelectedIndexes[0];
        await MoveAsync(idx, idx + 1);
    }

    private async Task MoveAsync(int fromIndex, int toIndex)
    {
        if (!_session.TryBeginEdit()) return; // Rodada 2 — ver doc XML de RotateSelected
        try
        {
            byte[] pdfAntes = _session.Snapshot;
            byte[]? pdfDepois = await TryRunEditAsync(() => _editor.MovePage(pdfAntes, fromIndex, toIndex));
            if (pdfDepois is null) return;
            if (!TryApplyEdit(pdfDepois)) return;
            // I3 (revisão Opus) — ASSIMETRIA DELIBERADA com Rotate/Delete: aquelas são operações TERMINAIS
            // (nada mais a fazer com a seleção depois de girar/excluir — limpar é o comportamento certo,
            // consequência natural do rebuild em `OnSessionApplied`). Mover é um GESTO DE POSICIONAMENTO —
            // o caso de uso real é repetir o clique várias vezes seguidas pra deslocar a MESMA página N
            // posições (ex.: página 3 -> 20 = 17 cliques em "Mover ▶"). Sem re-selecionar aqui, cada clique
            // exigiria reabrir a seleção manualmente entre um clique e outro (17 cliques + 17 re-seleções).
            // `TryApplyEdit` -> `_session.ApplyEdit` já disparou `Session.Applied` -> `OnSessionApplied`
            // (síncrono, já rodou por baixo desta chamada) -> `Pages` já é a coleção NOVA; `toIndex` é a
            // posição onde a página movida ATERRISSOU (contrato de `IPdfEditor.MovePage` — índice 0-based
            // FINAL, não a numeração intermediária que a implementação do motor precisou pra chegar lá).
            // `ToggleSelect` (não escrita direta em `IsSelected`) reusa a MESMA notificação de seleção que
            // mantém `CanMoveLeft`/`CanMoveRight` vivos pro PRÓXIMO clique. Roda DENTRO do `try` (antes do
            // `finally` soltar o pino) — mesmo raciocínio de RotateSelected: soltar ANTES arriscaria
            // reavaliar `CanMoveLeft`/`CanMoveRight` contra a seleção AINDA velha por 1 frame.
            if (toIndex >= 0 && toIndex < Pages.Count) ToggleSelect(toIndex, ctrl: false);
        }
        finally { _session.EndEdit(); }
    }

    /// Roda `op` (uma chamada a RotatePages/DeletePages/MovePage) fora da UI thread (CPU-bound, mesmo
    /// motivo de `DocumentViewModel.TryAddAnnotationAsync`) e traduz as 3 famílias de falha que o
    /// motor de página pode lançar em notificação pt-BR: `PdfSignedDocumentException` (gate de
    /// assinatura — defesa em profundidade, `CanOperateOnSelection`/`CanMoveLeft`/`CanMoveRight` já
    /// deveriam ter barrado via `CanEdit`), `PdfEditingException` (base — PDF corrompido, etc.) e
    /// `ArgumentException` CRUA (excluir todas as páginas, índice inválido — o motor lança isso direto,
    /// sem embrulhar; ver `PdfEditor.DeletePages`/`ValidatePageIndex`). `null` = falha já notificada,
    /// chamador não deve prosseguir pro `ApplyEdit`.
    private async Task<byte[]?> TryRunEditAsync(Func<byte[]> op)
    {
        try { return await Task.Run(op); }
        catch (PdfSignedDocumentException)
        {
            _notifyError("Este documento está assinado — a edição foi bloqueada para preservar a assinatura. Use \"Editar uma cópia\".");
            return null;
        }
        catch (PdfEditingException ex)
        {
            _notifyError(ex.Message);
            return null;
        }
        catch (ArgumentException ex)
        {
            _notifyError(ex.Message);
            return null;
        }
    }

    /// Rede contra `ArgumentException` do PDFium ao reconstruir o renderer sobre o resultado (mesmo
    /// papel de `DocumentViewModel.TryApplyEdit` — não reusa aquele método de propósito: duplicar esta
    /// meia-dúzia de linhas mantém `OrganizerViewModel` testável sozinho, com só `DocumentSession` +
    /// `IPdfEditor` fake, sem precisar construir um `DocumentViewModel` inteiro só pra emprestar o
    /// wrapper).
    private bool TryApplyEdit(byte[] novo)
    {
        try
        {
            _session.ApplyEdit(novo);
            return true;
        }
        catch (ArgumentException)
        {
            _notifyError("O resultado da edição não pôde ser aplicado — o PDF gerado é inválido. Nenhuma alteração foi salva.");
            return false;
        }
    }

    // Task 0 (Plano 3c): DefaultNotifyInfo mudou de método estático local pra UiPrompts.NotifyInfo (ver
    // ctor acima) — texto/ícone preservados (canal de SUCESSO, usado por ExtractSelected).

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Applied -= OnSessionApplied;
        _session.EditInFlightChanged -= OnEditInFlightChanged;
        _scheduler.Dispose();
        var renderer = _renderer;
        PendingDisposals.Enqueue(() => renderer.Dispose());
    }
}
