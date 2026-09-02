namespace mPdf.Documents;

/// Pilha de desfazer/refazer (Task 4, Plano 3a) — PURA e testável: não conhece PDF, `DocumentSession`
/// nem nada além de `byte[]` opacos (é por isso que os testes usam arrays de 1 byte, sem nenhum
/// fixture). Quem sabe "o que é o snapshot atual" é o CHAMADOR (`DocumentSession`) — `Undo`/`Redo`
/// recebem esse valor como parâmetro em vez de guardá-lo internamente, então esta classe nunca precisa
/// saber quando o chamador troca de documento.
///
/// Duas pilhas internas (desfazer/refazer), cada uma um `SpillableStack` (ver classe aninhada abaixo):
/// mantém no máximo `maxInMemory` entradas em RAM; entradas mais antigas (mais distantes do topo)
/// migram para arquivos numerados dentro de `spillDirectory` conforme a pilha cresce além do teto —
/// restaurado byte-a-byte idêntico, tanto para entradas em RAM quanto para as que vieram do disco.
///
/// Contrato de `Push` (empilha o snapshot PRÉ-edição de uma edição nova): SEMPRE limpa a pilha de
/// refazer — uma edição nova invalida qualquer "refazer" pendente (mesmo contrato de qualquer editor
/// com undo/redo: você não pode "refazer" uma ramificação da história que acabou de ser substituída).
public sealed class SnapshotStack : IDisposable
{
    private readonly SpillableStack _undo;
    private readonly SpillableStack _redo;

    /// `internal` só para teste (`DocumentSessionTests.Dispose_DisposesUndoRedoStack...` precisa
    /// provar que `DocumentSession.Dispose` descarta ESTA pasta, não uma cópia) — via o mesmo
    /// `InternalsVisibleTo("mPdf.Documents.Tests")` já usado por `DocumentSession.HandleReplaceFailure`.
    internal string SpillDirectory { get; }

    /// `spillDirectory` é criado (se ainda não existir) já no construtor — mesmo que a sessão nunca
    /// empilhe além de `maxInMemory` e a pasta fique vazia a vida inteira, ela precisa existir desde já
    /// para `Dispose` ter algo determinístico para apagar (testado: `Dispose_DeletesSpillDirectory`
    /// não depende de spill ter ocorrido).
    ///
    /// Ctor de 2 argumentos PRESERVADO byte-a-byte (Task 1, Plano 5): delega pro ctor de 4 argumentos
    /// abaixo com `maxRamBytes`/`maxSpillBytes` efetivamente ILIMITADOS (`long.MaxValue`) — as 2 janelas
    /// NOVAS (ver doc XML do ctor de 4 argumentos) nunca binding, então todo o comportamento provado
    /// pelos testes ORIGINAIS deste arquivo (janela por CONTAGEM) continua idêntico.
    public SnapshotStack(int maxInMemory, string spillDirectory)
        : this(maxInMemory, spillDirectory, long.MaxValue, long.MaxValue) { }

    /// Task 1 (Plano 5) — teto de BYTES por cima do mecanismo de spill acima (a POLÍTICA que este brief
    /// pede; o MECANISMO de spill/restauração já estava provado na Task 4 do Plano 3a, ver doc XML da
    /// classe). Duas janelas NOVAS, cada uma aplicada INDEPENDENTEMENTE às 2 pilhas (desfazer/refazer —
    /// mesma simetria que `maxInMemory` já tinha):
    ///
    /// `maxRamBytes` — some as entradas ainda em RAM (do topo pra baixo, as mais RECENTES primeiro) e
    /// espalha as mais ANTIGAS pro disco (mesmo mecanismo de `maxInMemory`, só que o gatilho agora é
    /// BYTES em vez de CONTAGEM) enquanto a soma exceder o teto — EXCETO a entrada do TOPO (a mais
    /// recente), que NUNCA é espalhada por este motivo, mesmo sozinha excedendo o teto (ver
    /// `Push_SingleEntryExceedingRamBudget_StaysInMemory_NeverSpilled`): espalhar o topo de cara não
    /// economizaria nada (`Undo` o releria do disco na hora seguinte) e complicaria o contrato à toa.
    ///
    /// `maxSpillBytes` — some os bytes das entradas JÁ espalhadas (arquivos em disco); ao exceder,
    /// DESCARTA PERMANENTEMENTE (apaga o arquivo, remove a entrada da pilha — não é mais "só espalhada",
    /// deixa de existir) a entrada espalhada mais ANTIGA, repetindo até voltar dentro do teto. Dispara
    /// `HistoryLimitReached` a CADA descarte genuíno (mecânico, sem "1x" aqui — ver doc XML do evento:
    /// a disciplina "1x por documento" é responsabilidade de quem consome este evento, não desta
    /// classe PURA).
    ///
    /// DECISÃO REGISTRADA (task-1-report.md, Plano 5): os 2 tetos são aplicados por PILHA (desfazer E
    /// refazer, cada uma com seu PRÓPRIO orçamento de `maxRamBytes`/`maxSpillBytes`), não um total
    /// COMBINADO entre as duas — extensão mínima da simetria já existente (`maxInMemory` já era por
    /// pilha desde a Task 4 do Plano 3a). Pior caso teórico de RAM é `2×maxRamBytes` (desfazer + refazer
    /// cheios ao mesmo tempo), mas `ApplyEdit` limpa `_redo` a CADA edição nova (`SnapshotStack.Push`,
    /// doc XML acima) — na prática `_redo` raramente acumula bytes, o caso combinado é o passageiro, não
    /// o estacionário.
    public SnapshotStack(int maxInMemory, string spillDirectory, long maxRamBytes, long maxSpillBytes)
    {
        if (maxInMemory < 1)
            throw new ArgumentOutOfRangeException(nameof(maxInMemory), "É preciso manter pelo menos 1 snapshot em RAM.");
        if (maxRamBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRamBytes), "O teto de RAM precisa ser de pelo menos 1 byte.");
        if (maxSpillBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxSpillBytes), "O teto de disco precisa ser de pelo menos 1 byte.");
        ArgumentException.ThrowIfNullOrWhiteSpace(spillDirectory);

        SpillDirectory = spillDirectory;
        Directory.CreateDirectory(spillDirectory);
        _undo = new SpillableStack(maxInMemory, spillDirectory, "undo", maxRamBytes, maxSpillBytes);
        _redo = new SpillableStack(maxInMemory, spillDirectory, "redo", maxRamBytes, maxSpillBytes);
        _undo.EntryDiscarded += OnEntryDiscarded;
        _redo.EntryDiscarded += OnEntryDiscarded;
    }

    /// Dispara sempre que `maxSpillBytes` força o descarte PERMANENTE da entrada espalhada mais antiga
    /// de QUALQUER uma das 2 pilhas (desfazer OU refazer) — mecânico, sem gate de "1x" (ver doc XML do
    /// ctor de 4 argumentos). `DocumentSession` (Task 1, Plano 5) consome este evento e aplica seu
    /// PRÓPRIO latch "1x por documento", mesmo exemplar de como `CanUndoRedoChanged`/`Applied` já fluem
    /// de `SnapshotStack`/`DocumentSession` até `DocumentViewModel`.
    public event EventHandler? HistoryLimitReached;

    private void OnEntryDiscarded(object? sender, EventArgs e) => HistoryLimitReached?.Invoke(this, EventArgs.Empty);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// Empilha `snapshot` (o estado PRÉ-edição) na pilha de desfazer e limpa a de refazer.
    public void Push(byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _undo.Push(snapshot);
        _redo.Clear(); // MUTAÇÃO-PROVÁVEL: comentar esta linha faz Push_ClearsRedoStack falhar (ver relatório da Task 4)
    }

    /// Desempilha o snapshot anterior (ou `null` se não há nada a desfazer) e empurra `current` para a
    /// pilha de refazer — espelho exato de `Redo`.
    public byte[]? Undo(byte[] current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!_undo.TryPop(out var previous)) return null;
        _redo.Push(current);
        return previous;
    }

    /// Desempilha o próximo snapshot (ou `null` se não há nada a refazer) e empurra `current` de volta
    /// para a pilha de desfazer.
    public byte[]? Redo(byte[] current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!_redo.TryPop(out var next)) return null;
        _undo.Push(current);
        return next;
    }

    /// Descarta TODAS as entradas das duas pilhas (best-effort: apaga também as espalhadas em disco via
    /// `SpillableStack.Clear`), MANTENDO a sessão viva e a pasta de spill existindo — usado por
    /// `DocumentSession.CommitSigned` (Task 3, Plano 4; decisão registrada: assinar invalida qualquer
    /// histórico de desfazer/refazer anterior — desfazer uma assinatura seria juridicamente confuso, o
    /// arquivo assinado É o novo estado). Diferente de `Dispose` (abaixo): não apaga a PASTA de spill em
    /// si — o documento continua aberto depois de assinar, uma edição futura pode voltar a empilhar aqui.
    public void ClearAll()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// Descarta as duas pilhas (best-effort: fecha arquivos ainda espalhados) e remove a pasta de
    /// spill inteira — nenhum resíduo de undo/redo sobra em `%TEMP%` depois que a sessão fecha.
    public void Dispose()
    {
        _undo.Dispose();
        _redo.Dispose();
        try { if (Directory.Exists(SpillDirectory)) Directory.Delete(SpillDirectory, recursive: true); }
        catch { /* melhor esforço — Dispose nunca pode lançar por causa de um arquivo temporário preso */ }
    }

    /// Uma pilha LIFO de `byte[]` com teto de RAM: as `maxInMemory` entradas mais PRÓXIMAS do topo
    /// ficam em memória; entradas mais antigas (mais fundas) migram para arquivo conforme `Push` as
    /// empurra pra fora da janela. Como `Push` só adiciona 1 entrada por chamada, no máximo 1 entrada
    /// precisa mudar de estado (RAM -> disco) a cada chamada — o índice exato é sempre
    /// `Count - maxInMemory - 1` (a entrada que acabou de cair fora da janela).
    ///
    /// `TryPop` só remove do TOPO, que é sempre a entrada mais RECENTE — mas depois de vários pops sem
    /// pushes novos, o topo pode eventualmente ser uma entrada que já foi espalhada em algum momento
    /// anterior (a janela "ideal" de RAM não é reajustada proativamente a cada pop, só o suficiente
    /// pra nunca perder dado); nesse caso `TryPop` lê o arquivo, apaga-o e devolve os bytes — a
    /// restauração é sempre byte-a-byte idêntica, seja a entrada de RAM ou de disco. Isso é uma
    /// simplificação deliberada: aceitar que uma entrada fique espalhada um pouco além do estritamente
    /// necessário (até ser desempilhada) é mais simples e não corrompe nada — só usa um pouco mais de
    /// disco temporariamente, nunca memória.
    ///
    /// INVARIANTE (Task 1, Plano 5, base de toda a lógica nova abaixo): as entradas AINDA EM RAM formam
    /// sempre um SUFIXO CONTÍGUO de `_entries` — `[_firstInMemoryIndex, Count)`. Isso vale porque
    /// `Push` só espalha (RAM -> disco) a entrada de índice `_firstInMemoryIndex` (nunca "no meio"), e
    /// `TryPop` só remove do TOPO (`Count - 1`), nunca do meio. `TryPop` pode, porém, fazer
    /// `_firstInMemoryIndex` "sobrar" além do novo `Count` (quando o topo removido era a ÚLTIMA entrada
    /// ainda em RAM) — por isso todo `TryPop` reancora `_firstInMemoryIndex = Min(_firstInMemoryIndex,
    /// Count)` no final, senão um Push FUTURO (depois de esvaziar e voltar a empilhar) classificaria a
    /// entrada nova como "já espalhada" por engano.
    private sealed class SpillableStack : IDisposable
    {
        private readonly int _maxInMemory;
        private readonly string _directory;
        private readonly string _filePrefix;
        private readonly long _maxRamBytes;
        private readonly long _maxSpillBytes;
        private readonly List<Entry> _entries = [];
        private int _nextFileId;
        private int _firstInMemoryIndex; // ver INVARIANTE no doc XML da classe
        private long _inMemoryBytes;
        private long _spilledBytes;

        /// Dispara sempre que `maxSpillBytes` força o descarte PERMANENTE da entrada espalhada mais
        /// antiga desta pilha — ver doc XML de `SnapshotStack.HistoryLimitReached` (o evento público que
        /// agrega os 2 `EntryDiscarded`, de `_undo` e `_redo`).
        public event EventHandler? EntryDiscarded;

        public SpillableStack(int maxInMemory, string directory, string filePrefix, long maxRamBytes, long maxSpillBytes)
        {
            _maxInMemory = maxInMemory;
            _directory = directory;
            _filePrefix = filePrefix;
            _maxRamBytes = maxRamBytes;
            _maxSpillBytes = maxSpillBytes;
        }

        public int Count => _entries.Count;

        public void Push(byte[] snapshot)
        {
            _entries.Add(Entry.InMemory(snapshot));
            _inMemoryBytes += snapshot.LongLength;

            // 1) janela por CONTAGEM (mecanismo ORIGINAL, Task 4 do Plano 3a, teto = _maxInMemory,
            //    COMPORTAMENTO INTOCADO — só reescrito em termos de _firstInMemoryIndex em vez do
            //    índice cru, ver INVARIANTE no doc XML da classe: os dois SEMPRE coincidem quando só
            //    esta janela está em jogo, então nenhum teste pré-existente muda de resultado).
            int countOverflowIndex = _entries.Count - _maxInMemory - 1;
            if (countOverflowIndex >= _firstInMemoryIndex) SpillOldestInMemory();

            // 2) janela por BYTES (Task 1, Plano 5, NOVA): espalha as entradas mais ANTIGAS AINDA em
            //    RAM enquanto a soma exceder _maxRamBytes — SEMPRE mantendo ao menos a do TOPO (a que
            //    acabou de ser empilhada agora) em RAM, mesmo que ela sozinha já exceda o teto (ver
            //    Push_SingleEntryExceedingRamBudget_StaysInMemory_NeverSpilled).
            while (_inMemoryBytes > _maxRamBytes && _firstInMemoryIndex < _entries.Count - 1)
                SpillOldestInMemory();

            // 3) teto de DISCO (Task 1, Plano 5, NOVO): descarta PERMANENTEMENTE (apaga o arquivo,
            //    remove a entrada da pilha) a entrada espalhada mais ANTIGA enquanto o total em disco
            //    exceder _maxSpillBytes. Só toca entradas JÁ espalhadas (nunca uma ainda em RAM) —
            //    `_firstInMemoryIndex > 0` é exatamente "existe pelo menos 1 entrada espalhada".
            while (_spilledBytes > _maxSpillBytes && _firstInMemoryIndex > 0)
                DiscardOldestSpilled();
        }

        /// Move a entrada mais antiga AINDA em RAM (índice `_firstInMemoryIndex`) para um arquivo novo —
        /// extraído (Task 1, Plano 5) porque agora tem 2 chamadores (janela por contagem E por bytes,
        /// ver `Push` acima), mecanismo idêntico ao `overflowIndex`/`File.WriteAllBytes` original.
        private void SpillOldestInMemory()
        {
            int index = _firstInMemoryIndex;
            var entry = _entries[index];
            string path = Path.Combine(_directory, $"{_filePrefix}-{_nextFileId++}.bin");
            File.WriteAllBytes(path, entry.Bytes!);
            long length = entry.Bytes!.LongLength;
            _entries[index] = Entry.OnDisk(path, length);
            _inMemoryBytes -= length;
            _spilledBytes += length;
            _firstInMemoryIndex++;
        }

        /// Descarta de vez a entrada espalhada mais antiga (sempre índice 0 — a mais antiga já
        /// espalhada, por construção do invariante de sufixo contíguo): apaga o arquivo, REMOVE a
        /// entrada da lista (não é mais "só espalhada", deixa de existir — `Undo`/`Redo` nunca mais vão
        /// alcançá-la) e dispara `EntryDiscarded`.
        private void DiscardOldestSpilled()
        {
            var entry = _entries[0];
            TryDeleteSilently(entry.Path!);
            _spilledBytes -= entry.Length;
            _entries.RemoveAt(0);
            _firstInMemoryIndex--;
            EntryDiscarded?.Invoke(this, EventArgs.Empty);
        }

        public bool TryPop(out byte[] value)
        {
            if (_entries.Count == 0) { value = null!; return false; }

            int lastIndex = _entries.Count - 1;
            var entry = _entries[lastIndex];
            _entries.RemoveAt(lastIndex);

            if (entry.IsSpilled)
            {
                value = File.ReadAllBytes(entry.Path!);
                TryDeleteSilently(entry.Path!);
                _spilledBytes -= entry.Length;
            }
            else
            {
                value = entry.Bytes!;
                _inMemoryBytes -= entry.Length;
            }
            // Reancora o invariante — ver doc XML da classe: só importa quando o TryPop acima removeu a
            // última entrada ainda em RAM (_firstInMemoryIndex ficaria > Count sem este ajuste).
            if (_firstInMemoryIndex > _entries.Count) _firstInMemoryIndex = _entries.Count;
            return true;
        }

        public void Clear()
        {
            foreach (var entry in _entries)
                if (entry.IsSpilled) TryDeleteSilently(entry.Path!);
            _entries.Clear();
            _firstInMemoryIndex = 0;
            _inMemoryBytes = 0;
            _spilledBytes = 0;
        }

        public void Dispose() => Clear();

        private static void TryDeleteSilently(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* melhor esforço */ }
        }

        private readonly struct Entry
        {
            public byte[]? Bytes { get; }
            public string? Path { get; }
            public long Length { get; }
            public bool IsSpilled => Path is not null;

            private Entry(byte[]? bytes, string? path, long length) { Bytes = bytes; Path = path; Length = length; }

            public static Entry InMemory(byte[] bytes) => new(bytes, null, bytes.LongLength);
            public static Entry OnDisk(string path, long length) => new(null, path, length);
        }
    }
}
