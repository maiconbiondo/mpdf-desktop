using System.Security.Cryptography;
using mPdf.Rendering;

namespace mPdf.Documents;

/// Resultado da decisão de limpeza tomada depois que a TROCA atômica (fase 2 de `AtomicWrite`) falha
/// no meio do caminho — ver `DocumentSession.HandleReplaceFailure` (C1, revisão pós-Task 3).
internal enum WriteFailureOutcome
{
    /// `destPath` sobreviveu à falha (ela aconteceu ANTES de qualquer coisa ser trocada, ex.: sharing
    /// violation detectada de cara) — o temporário foi removido, nada digno de recuperação sobrou.
    OriginalIntact,
    /// `destPath` tinha SUMIDO (o rename final falhou DEPOIS que o destino original já tinha sido
    /// consumido), mas o temporário foi movido de volta pro lugar certo com sucesso — dados restaurados.
    Recovered,
    /// `destPath` sumiu E a restauração TAMBÉM falhou — o temporário foi DEIXADO no disco de propósito:
    /// é a ÚLTIMA cópia sobrevivente dos dados do usuário; apagá-lo seria perda total.
    DataPreservedInTemp,
    /// Item 3 (revisão final pré-merge): `destPath` sumiu, a restauração falhou, E `temp` TAMBÉM sumiu
    /// (o SO/disco perdeu as DUAS cópias — o mesmo problema que derrubou o `File.Move` de resgate pode
    /// muito bem já ter destruído o próprio temporário antes). Diferente de `DataPreservedInTemp` (onde
    /// `temp` sobrevive e a mensagem pode apontar pra ele com segurança — ver `BuildFailureMessage`),
    /// aqui NÃO HÁ NADA pra apontar: prometer um caminho de resgate que não existe seria pior que não
    /// prometer nada — o usuário perderia tempo procurando um arquivo fantasma em vez de já saber que
    /// precisa recuperar de outra fonte (backup externo, .bak antigo).
    DataLost,
}

public sealed class DocumentSession : IDisposable
{
    public string FilePath { get; private set; }
    public string FileName => Path.GetFileName(FilePath);
    /// IMUTÁVEL por contrato: edições geram um NOVO snapshot (substituir, nunca mutar) — o renderer
    /// relê este buffer a cada recriação de reader; mutação in-place corrompe a renderização. O
    /// setter é privado (Task 3, Plano 3a): só `Apply` pode trocar a REFERÊNCIA (por um array NOVO,
    /// nunca por mutação do array antigo — a imutabilidade do conteúdo em si continua valendo).
    public byte[] Snapshot { get; private set; }
    /// Renderer ATUAL da sessão — troca de instância a cada `Apply` (Task 3, Plano 3a). Consumidores
    /// que precisam sobreviver a essa troca (schedulers de render) NÃO podem capturar
    /// `session.Renderer.RenderPage` como delegate uma única vez (isso prenderia a instância ANTIGA
    /// pra sempre); devem ler `session.Renderer` de novo a cada chamada — ver
    /// `DocumentViewModel` (seam documentada lá).
    public PdfDocumentRenderer Renderer { get; private set; }

    /// Tamanho (em pontos) de cada página, na ordem do documento — materializada AQUI (item c da
    /// Task 1, Plano 3a) em vez de deixar o consumidor (DocumentViewModel) chamar
    /// `Renderer.GetPageSize(i)` N vezes na thread de UI. `Open` (síncrono) e `OpenAsync` passam
    /// pelo MESMO construtor privado abaixo — para `OpenAsync` isso cai DENTRO do `Task.Run` que já
    /// existe (parse do PDFium), então o laço abaixo já sai da thread de UI de graça, sem precisar de
    /// um segundo `Task.Run` dedicado; para `Open` continua síncrono (compat com quem chama fora da
    /// UI, ex.: testes/PoC). Recalculada a cada `Apply` (Task 3, Plano 3a) — o documento novo pode ter
    /// contagem/tamanho de página diferentes do antigo.
    public IReadOnlyList<PdfPageSize> PageSizes { get; private set; }

    // IsDirty (Task 3, Plano 3a): hash SHA-256 do snapshot ATUAL vs hash do último snapshot GRAVADO em
    // disco (ou do snapshot de ABERTURA, se a sessão nunca foi salva) — calculado só em Apply/Save/
    // SaveAs (nunca a cada leitura de IsDirty, que é O(1): só devolve o campo cacheado). O CUSTO desse
    // cálculo é proporcional ao tamanho do documento (SHA-256 de um PDF grande, dezenas de MB, pode
    // levar alguns ms) — aceitável porque roda em resposta a uma ação explícita do usuário (aplicar
    // uma edição, salvar), nunca em binding/polling de UI. Comparar HASH (não os bytes crus) é o que
    // permite a Task 4 (undo/redo) um dia desfazer uma edição de volta ao estado EXATAMENTE salvo e
    // ver IsDirty voltar a false, sem precisar de lógica especial — a mesma UpdateDirty já cobre isso.
    private byte[] _lastSavedHash;
    public bool IsDirty { get; private set; }

    /// Dispara sempre que IsDirty MUDA de valor (não a cada Apply/Save — só quando o booleano vira).
    /// Consumido pela VM para atualizar o "•" no título da aba/janela.
    public event EventHandler? DirtyChanged;

    /// Dispara sempre que `Apply` troca Snapshot/Renderer/PageSizes por instâncias NOVAS — mesmo que
    /// IsDirty não mude de valor (ex.: aplicar uma segunda edição enquanto já está sujo). Consumido
    /// pela VM para reconstruir Pages/Thumbnails (a contagem/tamanho de página pode ter mudado) e para
    /// trocar o renderer dedicado de miniaturas — ver seam documentada em `DocumentViewModel`.
    public event EventHandler? Applied;

    /// Dispara quando `SaveAs` muda `FilePath` (nunca por `Save`, que grava no MESMO caminho).
    /// Consumido pela VM para atualizar o título da aba (`FileName` mudou) — separado de
    /// `DirtyChanged` de propósito: um "Salvar como" num documento já LIMPO não dispararia
    /// `DirtyChanged` (o booleano não muda), mas o nome do arquivo exibido ainda precisa atualizar.
    public event EventHandler? FilePathChanged;

    // Task 3 (Plano 3a): controla a criação do .bak — só na 1ª gravação (`Save`) da SESSÃO, mesmo que
    // o usuário salve várias vezes depois. `SaveAs` não mexe nesta flag (contrato: escrita simples,
    // sem lógica de backup — ver doc XML de SaveAs).
    private bool _hasSavedOnce;

    // ---- Task 4 (Plano 3a): Undo/Redo -------------------------------------------------------------

    // Teto do brief: 20 snapshots em RAM por sessão; além disso, SnapshotStack espalha os mais antigos
    // em disco (ver doc XML da classe). Não é configurável via AppConfig — é um detalhe de
    // implementação da sessão, não uma preferência do usuário. INTOCADO pela Task 1 (Plano 5, "count
    // cap 20 stays") — os 2 tetos NOVOS abaixo (`maxRamBytes`/`maxSpillBytes`) são janelas ADICIONAIS,
    // mais apertadas, por CIMA desta (ver doc XML de `SnapshotStack` pro mecanismo completo).
    private const int MaxSnapshotsInMemory = 20;

    /// Teto de RAM padrão (Task 1, Plano 5) — 256 MB. Justificativa do brief: o ledger mediu 525 MB no
    /// PIOR CASO de um snapshot único (scan de 510 páginas) — um teto por CONTAGEM sozinho (os 20
    /// snapshots atuais) não protege nada nesse cenário (1 snapshot já estoura qualquer teto de RAM
    /// razoável). A POLÍTICA correta (ver doc XML de `SnapshotStack`) é manter em RAM só os snapshots
    /// mais recentes cujo TOTAL caiba neste teto, derramando o resto pro spill em disco JÁ EXISTENTE
    /// (mecanismo provado na Task 4, Plano 3a) — nunca alocar sem limite. `AppConfig.MaxUndoRamBytes`
    /// espelha este valor como default persistido (não exposto na UI v1, ver doc XML lá).
    public const long DefaultMaxUndoRamBytes = 256L * 1024 * 1024;

    /// Teto de disco padrão (Task 1, Plano 5) — 2 GB. Acima disso, `SnapshotStack` descarta
    /// PERMANENTEMENTE (não só "para de crescer") as entradas espalhadas mais antigas — o usuário perde
    /// a capacidade de desfazer até aquele ponto, avisado 1x por documento (`UndoHistoryLimitReached`
    /// abaixo) com o texto pt-BR do brief. `AppConfig.MaxUndoSpillBytes` espelha este valor.
    public const long DefaultMaxUndoSpillBytes = 2L * 1024 * 1024 * 1024;

    private readonly SnapshotStack _undoRedo;

    // Task 1 (Plano 5): latch "1x por documento" — mesma disciplina de `_hasSavedOnce` (Task 3, Plano
    // 3a): um `bool` simples que, uma vez `true`, nunca mais deixa `UndoHistoryLimitReached` disparar de
    // novo NESTA sessão, mesmo que `SnapshotStack.HistoryLimitReached` (mecânico, sem gate — ver doc XML
    // lá) dispare várias vezes ao longo da vida do documento.
    private bool _historyLimitNoticeShown;

    /// Dispara na PRIMEIRA vez que o teto de disco (`maxSpillBytes`) força o descarte permanente de uma
    /// entrada de undo/redo NESTA sessão — nunca mais depois disso (ver `_historyLimitNoticeShown`
    /// acima). Consumido por `DocumentViewModel` (mesmo exemplar de como `CanUndoRedoChanged`/`Applied`
    /// já fluem de `DocumentSession` até lá) pra rotear o aviso pt-BR do brief ("Limite de histórico
    /// atingido; as edições mais antigas não podem mais ser desfeitas.") pro seam de notificação
    /// (`_notifyInfo`/`UiPrompts.NotifyInfo`).
    public event EventHandler? UndoHistoryLimitReached;

    // Cache só para decidir o DISPARO de CanUndoRedoChanged (flip-only — mesma disciplina de
    // UpdateDirty/DirtyChanged, Task 3): CanUndo/CanRedo em si sempre leem _undoRedo diretamente
    // (nunca ficam desatualizados), estes dois campos só guardam "qual foi o último valor notificado".
    private bool _lastCanUndo, _lastCanRedo;

    private static string NewUndoSpillDirectory() =>
        Path.Combine(Path.GetTempPath(), "mPDF", $"undo-{Guid.NewGuid():N}");

    /// `internal` só para teste (`Dispose_DisposesUndoRedoStack...` precisa confirmar que a pasta de
    /// spill DESTA sessão some no Dispose) — via `InternalsVisibleTo("mPdf.Documents.Tests")`.
    internal string UndoRedoSpillDirectory => _undoRedo.SpillDirectory;

    /// Composto com `!_editInFlight` (Rodada 2 da revisão — ver seção "funil único" abaixo): desfazer
    /// durante uma edição em voo (organizador OU leitor) aplicaria `Apply(previous)` sobre um estado
    /// que a edição em voo ainda vai sobrescrever — o snapshot restaurado seria perdido em silêncio
    /// assim que a edição em voo terminasse (achado empírico do painel de revisão: Ctrl+Z durante um
    /// Girar em voo desfazia a rotação ERRADA e ainda apagava o slot de Refazer).
    public bool CanUndo => _undoRedo.CanUndo && !_editInFlight;
    public bool CanRedo => _undoRedo.CanRedo && !_editInFlight;

    /// Dispara quando CanUndo OU CanRedo mudam de valor (flip-only, nunca a cada ApplyEdit/Undo/Redo)
    /// — consumido pela VM pra reavaliar CanExecute dos comandos de Desfazer/Refazer, mesmo padrão de
    /// DirtyChanged pro "•" do título. Rodada 2: também dispara quando `_editInFlight` muda e isso
    /// flipa o valor COMPOSTO de CanUndo/CanRedo (ver `RaiseCanUndoRedoChangedIfFlipped`, que agora lê
    /// as propriedades COMPOSTAS, não `_undoRedo` cru).
    public event EventHandler? CanUndoRedoChanged;

    // ---- Rodada 2 (revisão pós-branch, "R1 refutado"): funil único de exclusão mútua ("edição em
    // voo") ---------------------------------------------------------------------------------------
    //
    // O pino LOCAL que `OrganizerViewModel` usava na Rodada 1 (`_editInFlight` própria da VM) só
    // compunha os PRÓPRIOS 6 comandos do organizador — Desfazer/Refazer (`MainWindow.xaml.cs`, atalhos
    // Ctrl+Z/Ctrl+Y) e os pontos de `Session.ApplyEdit` do LEITOR (`DocumentViewModel` — anotações/
    // carimbos, incl. pontos SEM comando nenhum, chamados direto de handlers de mouse da View)
    // continuavam LIVRES pra rodar por cima de uma edição em voo — 3 furos EMPIRICAMENTE reproduzidos
    // pelo painel de revisão: (a) Ctrl+Z durante um Girar em voo desfaz a rotação ERRADA e apaga o
    // slot de Refazer; (b) `OrganizerViewModel.Insert` armava o pino DEPOIS do `await
    // File.ReadAllBytesAsync` — Girar/Excluir/Mover/Extrair ficavam livres durante a leitura (podendo
    // levar segundos num PDF grande/de rede); (c) um `bool` LOCAL não é exclusão mútua nenhuma quando
    // 2 operações se sobrepõem (possível via (b)): o `finally` da PRIMEIRA que termina reabilita TUDO
    // com a segunda ainda em voo.
    //
    // FIX: o pino sobe pra CÁ. `DocumentSession` é o ÚNICO objeto compartilhado entre
    // `OrganizerViewModel` e `DocumentViewModel` (as duas UIs operam sobre a MESMA sessão quando o
    // organizador está aberto sobre um documento) — é o funil correto pra exclusão mútua entre
    // QUALQUER combinação de operação que algum dia vá chamar `Apply`/`ApplyEdit`/`Undo`/`Redo`.
    private bool _editInFlight;

    /// Verdadeiro entre um `TryBeginEdit()` bem-sucedido e o `EndEdit()` correspondente. Composto em
    /// `CanUndo`/`CanRedo` acima; consumido por `DocumentViewModel`/`OrganizerViewModel` pra compor em
    /// TODO `CanExecute` de comando mutador de QUALQUER uma das duas VMs (via `EditInFlightChanged`
    /// abaixo) e checado diretamente pelos pontos de entrada SEM comando (mouse-handlers da View, ex.:
    /// `DocumentViewModel.PlaceAnnotationAtAsync`).
    public bool IsEditInFlight => _editInFlight;

    /// Dispara sempre que `TryBeginEdit`/`EndEdit` mudam `IsEditInFlight` — consumido pelas 2 VMs pra
    /// reavaliar `CanExecute` dos PRÓPRIOS comandos mutadores (organizador E leitor), já que agora as
    /// duas podem armar/soltar o MESMO pino — uma edição do leitor precisa desabilitar os botões do
    /// organizador tanto quanto uma edição do organizador precisa desabilitar as ferramentas do leitor.
    public event EventHandler? EditInFlightChanged;

    /// UI-THREAD-ONLY POR CONVENÇÃO (mesmo contrato de `Apply` acima — ver doc XML lá): sem lock
    /// interno, porque nunca há 2 threads chamando isto concorrentemente — é sempre a UI thread
    /// decidindo, SINCRONAMENTE, "posso começar uma edição agora?" ANTES do 1º `await` de cada
    /// operação (organizador: início de cada comando mutador, logo após qualquer diálogo síncrono e
    /// ANTES de qualquer leitura de arquivo/Task.Run; leitor: início de cada método que produz uma
    /// anotação/carimbo, ANTES de `EnsureRotationCacheFreshAsync`/diálogo/Task.Run).
    ///
    /// `false` quando outra edição já está em voo — o chamador deve desistir GRACIOSAMENTE (no-op,
    /// nunca sobrescrever nada; "a anotação simplesmente não é colocada, o usuário tenta de novo" —
    /// visível e honesto, nunca um apply silencioso por cima). Este é o mecanismo PRIMÁRIO, testado
    /// via chamada DIRETA ao ponto de entrada real (comando `Execute`/`ExecuteAsync`, ou o método
    /// mouse-handler em si) enquanto outra operação está genuinamente bloqueada em voo — NUNCA lança
    /// nessa contenção legítima (ver `EndEdit` abaixo pro "belt" que SIM lança, num cenário diferente
    /// e inequívoco: pareamento quebrado, não contenção).
    public bool TryBeginEdit()
    {
        if (_editInFlight) return false;
        _editInFlight = true;
        RaiseCanUndoRedoChangedIfFlipped();
        EditInFlightChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// Solta o pino — SEMPRE num `finally` no chamador (mesmo se a operação lançar), simétrico com um
    /// `TryBeginEdit()` que devolveu `true`. `InvalidOperationException` se chamado sem um
    /// `TryBeginEdit` bem-sucedido correspondente — BELT (Rodada 2, "add a debug assertion that
    /// arming twice throws"): ao contrário de `TryBeginEdit` devolver `false` (a CONTENÇÃO legítima,
    /// mecanismo primário, exercitada o tempo todo pelos testes de bloqueio), `EndEdit` sem um
    /// `TryBeginEdit` correspondente é uma violação de PAREAMENTO — nunca um cenário legítimo (todo
    /// chamador correto só chega no `finally { EndEdit(); }` depois de um `TryBeginEdit()` que
    /// devolveu `true`; um chamador BLOQUEADO nunca entra no `try`, então nunca chega no `finally`) —
    /// por isso este lado É seguro pra falhar alto (não guardado por `#if DEBUG`: o custo de checar é
    /// desprezível e o bug que isto pega — um novo call site que chama `EndEdit` sem ter armado —
    /// merece quebrar em QUALQUER build, não só em desenvolvimento).
    public void EndEdit()
    {
        if (!_editInFlight)
            throw new InvalidOperationException(
                "EndEdit chamado sem um TryBeginEdit() correspondente — pareamento quebrado.");
        _editInFlight = false;
        RaiseCanUndoRedoChangedIfFlipped();
        EditInFlightChanged?.Invoke(this, EventArgs.Empty);
    }

    private DocumentSession(string path, byte[] snapshot, long maxRamBytes, long maxSpillBytes)
    {
        FilePath = path;
        Snapshot = snapshot;
        Renderer = new PdfDocumentRenderer(snapshot);
        PageSizes = BuildPageSizes(Renderer);
        _lastSavedHash = ComputeHash(snapshot);
        IsDirty = false;
        _undoRedo = new SnapshotStack(MaxSnapshotsInMemory, NewUndoSpillDirectory(), maxRamBytes, maxSpillBytes);
        // Task 1 (Plano 5): mesmo exemplar de fiação de evento que o resto da classe (CanUndoRedoChanged
        // compondo _undoRedo.CanUndo/CanRedo) — aqui o evento MECÂNICO de SnapshotStack (dispara a CADA
        // descarte genuíno) vira o evento PÚBLICO com latch "1x por documento" (ver doc XML acima).
        _undoRedo.HistoryLimitReached += OnUndoRedoHistoryLimitReached;
    }

    private void OnUndoRedoHistoryLimitReached(object? sender, EventArgs e)
    {
        if (_historyLimitNoticeShown) return; // já avisado nesta sessão — ver doc XML do campo
        _historyLimitNoticeShown = true;
        UndoHistoryLimitReached?.Invoke(this, EventArgs.Empty);
    }

    private static PdfPageSize[] BuildPageSizes(PdfDocumentRenderer renderer)
    {
        var sizes = new PdfPageSize[renderer.PageCount];
        for (int i = 0; i < sizes.Length; i++) sizes[i] = renderer.GetPageSize(i);
        return sizes;
    }

    private static byte[] ComputeHash(byte[] data) => SHA256.HashData(data);

    /// `maxRamBytes`/`maxSpillBytes` (Task 1, Plano 5) — OPCIONAIS, default = `DefaultMaxUndoRamBytes`/
    /// `DefaultMaxUndoSpillBytes` (256 MB / 2 GB): todo call site PRÉ-EXISTENTE (produção e os ~800
    /// testes deste repo) continua chamando `Open(path)` sem tocar nestes 2 parâmetros — o
    /// comportamento pré-Task-1 é preservado byte-a-byte pra qualquer sessão real (nenhum documento de
    /// teste/uso normal chega perto de 256 MB de histórico). Testes DESTA task passam valores pequenos
    /// explicitamente (ver `DocumentSessionTests`, seção "Task 1, Plano 5") — nunca alocação sintética
    /// gigante, sempre fixtures reais pequenas com teto proporcionalmente pequeno.
    public static DocumentSession Open(
        string path, long maxRamBytes = DefaultMaxUndoRamBytes, long maxSpillBytes = DefaultMaxUndoSpillBytes)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Arquivo não encontrado.", path);
        // ReadAllBytes não deixa handle aberto
        return new DocumentSession(path, File.ReadAllBytes(path), maxRamBytes, maxSpillBytes);
    }

    /// Caminho usado pelo app (Task 7): leitura assíncrona do arquivo + construção do renderer (parse
    /// PDFium) dentro de Task.Run — o parse é CPU-bound e síncrono por natureza (Docnet.Core não expõe
    /// versão async), então rodá-lo direto no await da UI thread ainda travaria a UI; Task.Run é o que
    /// de fato tira o trabalho de lá. `Open` (síncrono) continua existindo pros testes/PoC que não
    /// precisam de UI responsiva. `maxRamBytes`/`maxSpillBytes` — mesmo contrato de `Open` acima (Task
    /// 1, Plano 5); `MainViewModel.OpenPath` passa `AppConfig.MaxUndoRamBytes`/`MaxUndoSpillBytes` aqui
    /// (o caminho REAL de produção — `Open` síncrono é só teste/PoC).
    public static async Task<DocumentSession> OpenAsync(
        string path,
        long maxRamBytes = DefaultMaxUndoRamBytes,
        long maxSpillBytes = DefaultMaxUndoSpillBytes,
        CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Arquivo não encontrado.", path);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return await Task.Run(() => new DocumentSession(path, bytes, maxRamBytes, maxSpillBytes), ct).ConfigureAwait(false);
    }

    /// CROSS-REF (rider, revisão pós-Task 4): edições de USUÁRIO devem usar `ApplyEdit` (logo abaixo),
    /// nunca `Apply` direto — `Apply` é o mecanismo de baixo nível que `ApplyEdit` e `Undo`/`Redo`
    /// compartilham; chamado direto a partir de uma ação do usuário, ele NÃO registra undo (a edição
    /// "gruda" sem jeito de desfazer, e a pilha de refazer não é limpa como o contrato de uma edição
    /// nova exige).
    ///
    /// Aplica um NOVO snapshot (resultado de uma edição, ex.: `mPdf.Editing`) — troca Snapshot/
    /// Renderer/PageSizes por instâncias NOVAS de uma vez só e descarta o renderer ANTIGO via
    /// `PendingDisposals.Enqueue` (fila serial — mesmo padrão de `DocumentViewModel.Dispose`, ver doc
    /// XML lá para o histórico completo do porquê da fila ser serial em vez de Task.Run direto).
    ///
    /// UI-THREAD-ONLY POR CONVENÇÃO: este método NÃO tem lock interno — não é seguro chamar de threads
    /// concorrentes (duas chamadas simultâneas podem correr uma sobre a outra e perder um swap, ou
    /// enfileirar o renderer errado para dispose). Isso é aceitável porque toda a cadeia de chamada
    /// prevista (edição via UI -> `mPdf.Editing` -> `Apply`) já roda na thread de UI por natureza (é
    /// ela quem decide "aplicar esta edição agora"); um único chamador nunca concorrente.
    ///
    /// O NOVO renderer é construído ANTES de qualquer mutação de estado: se `novoSnapshot` não for um
    /// PDF válido, `PdfDocumentRenderer` lança (mesma `ArgumentException` do construtor da própria
    /// sessão) e a sessão permanece INTACTA no estado anterior — nenhuma edição malformada pode deixar
    /// a sessão pela metade.
    public void Apply(byte[] novoSnapshot)
    {
        ArgumentNullException.ThrowIfNull(novoSnapshot);

        var newRenderer = new PdfDocumentRenderer(novoSnapshot); // pode lançar; nada mutado ainda
        var newSizes = BuildPageSizes(newRenderer);
        var oldRenderer = Renderer;

        Snapshot = novoSnapshot;
        Renderer = newRenderer;
        PageSizes = newSizes;

        PendingDisposals.Enqueue(() => oldRenderer.Dispose());

        UpdateDirty(ComputeHash(novoSnapshot));
        Applied?.Invoke(this, EventArgs.Empty);
    }

    /// Aplica uma edição NOVA registrando o snapshot PRÉ-edição na pilha de desfazer (Task 4, Plano
    /// 3a) — SEPARADO de `Apply` de propósito: `Undo`/`Redo` (abaixo) chamam `Apply` DIRETAMENTE, nunca
    /// `ApplyEdit`, senão desfazer uma edição a empilharia de novo no undo (loop). `ApplyEdit` também é
    /// a única porta que LIMPA o redo (uma edição nova invalida qualquer "refazer" pendente — ver
    /// `SnapshotStack.Push`).
    ///
    /// Ordem deliberada — captura `pre` e chama `Apply` ANTES de empilhar: se `novoSnapshot` não for um
    /// PDF válido, `Apply` lança e a sessão fica intacta (mesmo contrato de `Apply`); empilhar `pre`
    /// SÓ DEPOIS que `Apply` teve sucesso evita poluir a pilha de desfazer com uma entrada "fantasma"
    /// de uma edição que nunca chegou a ser aplicada de verdade.
    ///
    /// IMUTABILIDADE (rider, revisão pós-Task 4): `pre` (o buffer que ESTAVA em `Snapshot`) fica retido
    /// na pilha de desfazer depois desta chamada — quem produziu esse array (uma chamada anterior a
    /// `ApplyEdit`/`Apply`, ou a leitura inicial do arquivo) NUNCA pode mutá-lo in-place depois de
    /// entregá-lo aqui; um `byte[]` editado por referência corromperia silenciosamente uma entrada do
    /// histórico de desfazer que parecia "só passado". O mesmo vale pro `novoSnapshot` recebido por
    /// este método: ele pode virar, mais tarde, o `pre` de uma chamada FUTURA — mesma regra.
    public void ApplyEdit(byte[] novoSnapshot)
    {
        ArgumentNullException.ThrowIfNull(novoSnapshot);
        var pre = Snapshot;
        Apply(novoSnapshot);
        _undoRedo.Push(pre);
        RaiseCanUndoRedoChangedIfFlipped();
    }

    /// Desfaz a última edição: aplica o snapshot anterior (via `Apply`, NUNCA `ApplyEdit` — ver doc XML
    /// acima) e empurra o estado atual pra pilha de refazer. Sem nada a desfazer: no-op silencioso
    /// (nenhum evento dispara, `Apply` nunca é chamado) — mesmo espírito de um botão desabilitado que
    /// não faz nada se clicado por engano.
    public void Undo()
    {
        // Rodada 2 (revisão — furo (a)): defesa em profundidade — `CanUndo` já compõe `!_editInFlight`
        // (deveria ter barrado `UndoCommand.CanExecute` antes de chegar aqui), mas este método NUNCA
        // deve confiar que todo chamador presente e futuro sempre checa antes (mesmo espírito de
        // `PdfEditor.GuardAgainstSignedDocument`) — sem este guard, um `session.Undo()` chamado direto
        // (ou uma corrida onde `CanExecute` ainda não reavaliou) desfaria por baixo de uma edição em
        // voo cujo resultado ainda vai sobrescrever o snapshot restaurado.
        if (_editInFlight) return;
        if (_undoRedo.Undo(Snapshot) is not { } previous) return;
        Apply(previous);
        RaiseCanUndoRedoChangedIfFlipped();
    }

    /// Refaz a última edição desfeita — espelho exato de `Undo`.
    public void Redo()
    {
        if (_editInFlight) return; // Rodada 2 — mesmo guard de Undo acima
        if (_undoRedo.Redo(Snapshot) is not { } next) return;
        Apply(next);
        RaiseCanUndoRedoChangedIfFlipped();
    }

    /// Lê as propriedades COMPOSTAS (`CanUndo`/`CanRedo`, não `_undoRedo.CanUndo`/`_undoRedo.CanRedo`
    /// crus) — Rodada 2: desde que `CanUndo`/`CanRedo` passaram a compor `!_editInFlight`, ler os
    /// campos crus aqui perderia o disparo de `CanUndoRedoChanged` quando SÓ `_editInFlight` muda (a
    /// pilha de undo/redo em si não mudou, mas o valor COMPOSTO que a VM enxerga sim) — `TryBeginEdit`/
    /// `EndEdit` chamam este método justamente pra cobrir esse caso.
    private void RaiseCanUndoRedoChangedIfFlipped()
    {
        bool canUndo = CanUndo, canRedo = CanRedo;
        if (canUndo == _lastCanUndo && canRedo == _lastCanRedo) return;
        _lastCanUndo = canUndo;
        _lastCanRedo = canRedo;
        CanUndoRedoChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDirty(byte[] currentHash)
    {
        bool dirty = !currentHash.AsSpan().SequenceEqual(_lastSavedHash);
        if (dirty == IsDirty) return;
        IsDirty = dirty;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// Grava o snapshot ATUAL de volta em `FilePath` (sobrescreve o arquivo original), atomicamente
    /// (temp + `File.Replace`/`File.Move` — ver `AtomicWrite`). Cria um `.bak` (`FilePath + ".bak"`)
    /// SÓ na primeira gravação desta SESSÃO (`_hasSavedOnce`), e só se `config.CriarBackup` estiver
    /// ligado (default: true) — gravações seguintes não regravam o `.bak` (o backup continua sendo o
    /// conteúdo de ANTES de qualquer edição desta sessão, não o penúltimo save). Um `.bak`
    /// PRÉ-EXISTENTE (de uma sessão de edição anterior sobre o mesmo arquivo) é substituído
    /// normalmente pela 1ª gravação desta sessão — `File.Replace` sobrescreve o backup antigo.
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool createBackup = config.CriarBackup && !_hasSavedOnce;
        AtomicWrite(FilePath, Snapshot, createBackup ? FilePath + ".bak" : null);
        _hasSavedOnce = true;
        MarkSaved();
    }

    /// Grava o snapshot ATUAL em `path` (destino NOVO ou existente) — escrita atômica SIMPLES, sem
    /// lógica de `.bak` (contrato: "Salvar como" não cria backup do destino; se `path` já existir, seu
    /// conteúdo anterior é perdido, igual a qualquer "Salvar como" convencional). Atualiza `FilePath`
    /// para `path` e limpa o estado sujo (o arquivo em disco agora bate com `Snapshot`). Não mexe em
    /// `_hasSavedOnce` — essa flag é exclusiva da lógica de backup de `Save` e é ortogonal a `SaveAs`.
    public void SaveAs(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AtomicWrite(path, Snapshot, null);
        FilePath = path;
        FilePathChanged?.Invoke(this, EventArgs.Empty);
        MarkSaved();
    }

    /// Comita uma assinatura recém-produzida (Task 3, Plano 4) — o ÚNICO caminho pelo qual um documento
    /// assinado entra na sessão. Assinar NUNCA passa por `ApplyEdit`/`Apply` direto: o motor
    /// (`mPdf.Signing`) sempre opera em modo INCREMENTAL (append) sobre o arquivo já salvo, então o
    /// resultado precisa entrar como um COMMIT (troca de estado + gravação em disco) atômico, não como
    /// mais uma edição em memória que o usuário ainda precisaria salvar depois.
    ///
    /// CONVENÇÃO DE USO (documentada aqui, não imposta por tipo): o comando "Assinar" arma o funil
    /// (`Session.TryBeginEdit()`) ANTES de rodar o motor de assinatura (`Task.Run`), chama este método
    /// DEPOIS que a assinatura termina — AINDA dentro do mesmo par `TryBeginEdit`/`EndEdit` — e só então
    /// solta o pino no `finally` do comando (mesmo par de responsabilidade que `ApplyEdit`/Undo/Redo já
    /// seguem por convenção, só que aqui o "commit" tem uma API própria em vez de reusar `ApplyEdit`).
    /// BELT (mesmo espírito do `EndEdit` acima): chamado FORA do funil — um call site novo que esqueceu
    /// o `TryBeginEdit` — lança `InvalidOperationException` alto, em vez de gravar por baixo de uma
    /// "edição" nunca anunciada aos outros consumidores do pino compartilhado.
    ///
    /// DECISÃO REGISTRADA (plano): assinar LIMPA o histórico de desfazer/refazer INTEIRO (as duas
    /// pilhas, via `SnapshotStack.ClearAll`) — desfazer uma assinatura seria juridicamente confuso (o
    /// arquivo assinado É o novo estado; um "Ctrl+Z" que reaparecesse com o documento SEM a assinatura,
    /// enquanto o arquivo em disco JÁ está assinado, deixaria sessão e disco divergentes).
    ///
    /// UI-THREAD-ONLY POR CONVENÇÃO (mesmo contrato de `Apply`/`TryBeginEdit` acima — ver doc XML lá):
    /// sem lock interno, porque o único chamador previsto (o comando "Assinar" da UI) já roda
    /// serializado pelo próprio funil `TryBeginEdit`.
    ///
    /// ORDEM DELIBERADA (mesma disciplina de `Apply`: nada muta até que o novo estado esteja VALIDADO):
    /// 1) constrói o renderer NOVO sobre `signedBytes` (valida que é um PDF de verdade — `ArgumentException`
    ///    se não for, sessão E arquivo em disco continuam INTOCADOS); 2) grava `signedBytes` atomicamente
    ///    em `FilePath` (a MESMA `AtomicWrite` de `Save`/`SaveAs`, SEM `.bak` — este método não recebe
    ///    `AppConfig`, então não há como consultar `CriarBackup`; o `.bak` de `Save` continua protegendo
    ///    o conteúdo de ANTES de qualquer edição desta sessão, essa garantia não muda aqui) — se a
    ///    gravação falhar (disco cheio, arquivo locked), a sessão AINDA está intacta (nada mutado em
    ///    memória ainda); só DEPOIS que o disco confirma é que 3) troca Snapshot/Renderer/PageSizes,
    ///    limpa undo/redo, marca salvo e dispara os eventos.
    public void CommitSigned(byte[] signedBytes)
    {
        ArgumentNullException.ThrowIfNull(signedBytes);
        if (!_editInFlight)
            throw new InvalidOperationException(
                "CommitSigned chamado fora do funil TryBeginEdit — o comando de assinar precisa armar " +
                "Session.TryBeginEdit() ANTES de assinar e chamar CommitSigned ainda dentro do mesmo par " +
                "TryBeginEdit/EndEdit (mesmo contrato documentado em TryBeginEdit/EndEdit acima).");

        var newRenderer = new PdfDocumentRenderer(signedBytes); // valida o PDF; nada mutado ainda (mesmo contrato de Apply)
        var newSizes = BuildPageSizes(newRenderer);

        try
        {
            // Grava em disco ANTES de mutar qualquer estado em memória — uma falha de I/O aqui (disco
            // cheio, arquivo aberto por outro processo) deixa a sessão exatamente como estava.
            AtomicWrite(FilePath, signedBytes, null);
        }
        catch
        {
            newRenderer.Dispose();
            throw;
        }

        var oldRenderer = Renderer;
        Snapshot = signedBytes;
        Renderer = newRenderer;
        PageSizes = newSizes;
        PendingDisposals.Enqueue(() => oldRenderer.Dispose());

        _undoRedo.ClearAll();
        MarkSaved();
        RaiseCanUndoRedoChangedIfFlipped(); // undo/redo acabaram de zerar -- CanUndo/CanRedo podem ter flipado
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void MarkSaved()
    {
        _lastSavedHash = ComputeHash(Snapshot);
        if (!IsDirty) return;
        IsDirty = false;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// Escreve `content` em `destPath` em DUAS FASES separadas (revisão pós-Task 3 — I2 e C1 exigiram
    /// separar o que antes era um bloco só):
    ///
    /// FASE 1 — grava num arquivo TEMPORÁRIO no MESMO DIRETÓRIO de `destPath` (nunca em
    /// `Path.GetTempPath()` ou outro volume), de forma DURÁVEL (`WriteTempDurably` — write-through +
    /// `Flush(true)`, I5). `destPath` NUNCA é tocado nesta fase — se ela falhar (a causa mais provável
    /// na prática: disco cheio, escrevendo um PDF grande), o temp (talvez parcial) é limpo e a exceção
    /// vira um `IOException` legível; o original permanece garantidamente intacto.
    ///
    /// FASE 2 — troca atômica: `File.Replace` (destino já existe — permite `backupPath` opcional) ou
    /// `File.Move(..., overwrite: true)` (destino novo). ATÔMICA quanto ao RENAME em si — quando
    /// origem e destino compartilham volume (por isso o temp DEVE estar no mesmo diretório: o .NET
    /// degrada silenciosamente para copiar+apagar entre volumes, e uma falha no meio da cópia deixaria
    /// o destino com conteúdo PARCIAL — a corrupção que este pipeline existe para evitar). NÃO É,
    /// PORÉM, garantidamente "tudo ou nada" a nível de Win32: `ReplaceFile` documenta
    /// `ERROR_UNABLE_TO_MOVE_REPLACEMENT_2`, uma falha que pode acontecer DEPOIS do destino original já
    /// ter sido consumido — ver `HandleReplaceFailure` (C1) para como esse resíduo é tratado sem jamais
    /// apagar a única cópia sobrevivente dos dados. A janela residual real (perda de energia bem no
    /// meio do rename do próprio SO) é responsabilidade do sistema operacional/hardware, fora do nosso
    /// controle — o que ESTE código garante é que, do lado de cá, nunca escolhemos apagar dados que só
    /// existem numa cópia.
    ///
    /// Sucesso: varre o diretório por temporários ÓRFÃOS de tentativas anteriores desta mesma sessão
    /// de app (crash/queda de energia no meio de um Save passado) e os remove — melhor esforço, nunca
    /// derruba um Save que já teve sucesso.
    private static void AtomicWrite(string destPath, byte[] content, string? backupPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(destPath))
            ?? throw new IOException($"Não foi possível determinar o diretório de '{destPath}'.");
        string temp = Path.Combine(dir, $".{Path.GetFileName(destPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            WriteTempDurably(temp, content);
        }
        catch (Exception ex)
        {
            TryDeleteSilently(temp);
            throw new IOException(
                $"Não foi possível salvar o arquivo '{destPath}': {ex.Message} O arquivo original não foi alterado.", ex);
        }

        try
        {
            if (File.Exists(destPath))
                File.Replace(temp, destPath, backupPath);
            else
                File.Move(temp, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            var outcome = HandleReplaceFailure(temp, destPath);
            throw new IOException(BuildFailureMessage(destPath, temp, ex, outcome), ex);
        }

        SweepOrphanTempFiles(destPath);
    }

    /// I5 (revisão pós-Task 3): `FileOptions.WriteThrough` pede ao SO pra não reter os bytes só em
    /// cache de escrita; `Flush(true)` (== `FlushFileBuffers` no Windows) força a persistência em
    /// disco antes deste método retornar — sem isso, um Save "bem-sucedido" podia na verdade estar só
    /// na RAM do SO, perdido numa queda de energia mesmo com o rename subsequente sendo atômico.
    private static void WriteTempDurably(string path, byte[] content)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        fs.Write(content, 0, content.Length);
        fs.Flush(true);
    }

    /// Decide o destino do temporário depois que a FASE 2 de `AtomicWrite` (a troca em si) falha —
    /// extraído como método próprio, `internal` e testável direto contra arquivos reais em disco, sem
    /// precisar forçar uma falha genuína de I/O do SO pra exercitar a lógica.
    ///
    /// CRÍTICO (C1): ver o parágrafo dedicado no XML doc de `AtomicWrite`. Resumo da decisão:
    /// - `destPath` existe: a falha aconteceu ANTES da troca consumir o destino — original intacto,
    ///   `temp` é lixo seguro pra remover.
    /// - `destPath` NÃO existe: tenta mover `temp` de volta pro lugar certo (restaura a continuidade).
    ///   Se a restauração TAMBÉM falhar, `temp` é DEIXADO no disco de propósito — é a última cópia.
    internal static WriteFailureOutcome HandleReplaceFailure(string temp, string destPath)
    {
        if (File.Exists(destPath))
        {
            TryDeleteSilently(temp);
            return WriteFailureOutcome.OriginalIntact;
        }

        try
        {
            File.Move(temp, destPath);
            return WriteFailureOutcome.Recovered;
        }
        catch
        {
            // NUNCA apagar `temp` aqui — SE ele sobreviveu, é a única cópia sobrevivente dos dados.
            // Item 3 (revisão final pré-merge): o `File.Move` acima pode falhar por MUITOS motivos,
            // incluindo o próprio `temp` já ter sumido (mesma causa raiz que derrubou o Move: disco/SO
            // perdeu as duas cópias) — checa a existência REAL antes de prometer, na mensagem que
            // `BuildFailureMessage` monta a partir deste retorno, um caminho de resgate que pode não
            // existir mais.
            return File.Exists(temp) ? WriteFailureOutcome.DataPreservedInTemp : WriteFailureOutcome.DataLost;
        }
    }

    /// `internal` (rider da revisão pós-Task 3, endereçado na Task 4): a mensagem de
    /// `DataPreservedInTemp` é a ÚNICA rota do usuário até os dados sobreviventes quando o rename final
    /// falha depois do original já ter sido consumido (C1) — sem o caminho do `temp` NA MENSAGEM, o
    /// resgate vira proteção sem payoff prático (dado preservado em disco, mas ninguém sabe onde). Ver
    /// `BuildFailureMessage_DataPreservedInTemp_ContainsTempPath` em `DocumentSessionTests`.
    internal static string BuildFailureMessage(string destPath, string temp, Exception ex, WriteFailureOutcome outcome) =>
        outcome switch
        {
            WriteFailureOutcome.OriginalIntact =>
                $"Não foi possível salvar o arquivo '{destPath}': {ex.Message} O arquivo original não foi alterado.",
            WriteFailureOutcome.Recovered =>
                $"Falha ao salvar '{destPath}' foi detectada e corrigida automaticamente ({ex.Message}) — " +
                "o arquivo no disco já contém os dados mais recentes; recomendamos salvar de novo para confirmar.",
            WriteFailureOutcome.DataPreservedInTemp =>
                $"Falha crítica ao salvar '{destPath}': {ex.Message} O arquivo original não pôde ser restaurado, " +
                $"mas os dados estão preservados em: {temp}",
            WriteFailureOutcome.DataLost =>
                $"Falha crítica ao salvar '{destPath}': {ex.Message} O arquivo original não pôde ser restaurado " +
                "e a cópia temporária também não foi encontrada — os dados desta edição podem ter sido perdidos.",
            _ => $"Não foi possível salvar o arquivo '{destPath}': {ex.Message}",
        };

    private static void TryDeleteSilently(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* melhor esforço */ }
    }

    // Rider (revisão pós-Task 3): temporários de tentativas de Save ANTERIORES que não completaram
    // (crash/queda de energia entre a Fase 1 e a Fase 2) ficam órfãos no diretório do usuário — varre
    // e remove por padrão de nome (`.{arquivo}.*.tmp`) toda vez que um Save/SaveAs TERMINA com sucesso.
    // Melhor esforço: uma falha aqui (permissão, etc.) não pode mascarar um Save que já funcionou.
    private static void SweepOrphanTempFiles(string destPath)
    {
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(destPath))!;
            string pattern = $".{Path.GetFileName(destPath)}.*.tmp";
            foreach (var orphan in Directory.EnumerateFiles(dir, pattern))
                TryDeleteSilently(orphan);
        }
        catch { /* melhor esforço */ }
    }

    public void Dispose()
    {
        // try/finally (rider, revisão pós-Task 4): se Renderer.Dispose() lançar, _undoRedo.Dispose()
        // TEM que rodar mesmo assim — sem isso, a pasta de spill de undo/redo desta sessão (que pode
        // ter dezenas de MB espalhados em disco, ver SnapshotStack) vazava pra sempre em %TEMP%\mPDF.
        try { Renderer.Dispose(); }
        finally { _undoRedo.Dispose(); } // Task 4 (Plano 3a): apaga a pasta de spill do undo/redo desta sessão
    }

    /// Escreve `content` como um arquivo NOVO em `path`, atomicamente (a MESMA `AtomicWrite` de
    /// `Save`/`SaveAs`, sem `.bak`) — SEM exigir uma `DocumentSession` completa por trás. Existe pra
    /// fluxos que precisam materializar bytes recém-produzidos em disco ANTES de haver uma sessão de
    /// edição sobre eles (Task 5, Plano 3a: "Editar uma cópia" grava a cópia sem assinaturas assim, e
    /// SÓ DEPOIS abre uma sessão de verdade sobre o arquivo já gravado, via `OpenAsync`).
    ///
    /// ESCOLHA REGISTRADA (task-5-report.md): a alternativa cogitada — instanciar uma `DocumentSession`
    /// descartável só pra chamar `SaveAs` — foi rejeitada porque uma `DocumentSession` sempre abre um
    /// `PdfDocumentRenderer` nativo (Docnet/PDFium) E uma pasta de spill de undo/redo (`SnapshotStack`)
    /// que SÓ são liberados por `Dispose()`; uma sessão "de uso único" criada só pra escrever e depois
    /// jogada fora vazaria os dois a cada "Editar uma cópia". Este método reaproveita a MESMA
    /// `AtomicWrite` sem carregar nada disso — não abre PDF nenhum, só grava bytes.
    public static void WriteNewFile(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        AtomicWrite(path, content, null);
    }

    /// Varredura de pastas ÓRFÃS de undo/redo (rider, revisão pós-Task 4) — cada `DocumentSession` cria
    /// sua PRÓPRIA pasta de spill (`%TEMP%\mPDF\undo-<guid>`, ver `NewUndoSpillDirectory`) e a apaga em
    /// `Dispose`; uma queda de energia/crash no meio de uma sessão deixa essa pasta pra trás pra
    /// sempre, sem ninguém pra limpar depois. Chamada 1x na inicialização do app (`MainWindow` ctor) —
    /// ANTES de qualquer sessão desta execução existir, então nunca corre o risco de apagar a pasta de
    /// uma sessão ATIVA da PRÓPRIA execução. `maxAge` (default 24h) é a defesa contra apagar a pasta de
    /// OUTRA instância do app rodando ao mesmo tempo (sessão de verdade, só velha o bastante pra ser
    /// improvável que ainda esteja em uso). Melhor esforço: cada pasta que falhar ao apagar (em uso,
    /// permissão, etc.) é ignorada silenciosamente — uma varredura de limpeza nunca pode impedir o app
    /// de abrir.
    public static void SweepOrphanUndoSpillDirectories(TimeSpan? maxAge = null) =>
        SweepOrphanUndoSpillDirectories(
            Path.Combine(Path.GetTempPath(), "mPDF"), maxAge ?? TimeSpan.FromHours(24));

    /// `internal`, testável direto contra uma raiz TEMPORÁRIA própria (não a `%TEMP%\mPDF` real) — mesmo
    /// padrão de `HandleReplaceFailure`/`BuildFailureMessage` acima.
    internal static void SweepOrphanUndoSpillDirectories(string root, TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root, "undo-*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < maxAge) continue;
                    Directory.Delete(dir, recursive: true);
                }
                catch { /* melhor esforço por pasta — uma travada não pode impedir varrer as demais */ }
            }
        }
        catch { /* melhor esforço — nunca pode impedir o app de abrir */ }
    }
}
