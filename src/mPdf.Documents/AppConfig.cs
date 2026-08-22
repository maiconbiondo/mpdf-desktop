using System.Text.Json;

namespace mPdf.Documents;

/// Configuração persistida do app (Task 3, Plano 3a) — JSON em `%AppData%\mPDF\config.json`, mesmo
/// exemplar de `RecentFilesStore` (mPdf.App.Services): diretório injetável no construtor (testes usam
/// um `TemporaryDirectory` próprio, nunca tocam no config real da máquina), `DefaultDirectory` estático
/// para produção. Mora em `mPdf.Documents` (não em `mPdf.App`) porque `DocumentSession.Save` — que
/// consulta `CriarBackup` — mora aqui e `mPdf.Documents` não pode referenciar `mPdf.App` (seria
/// circular: `mPdf.App` já referencia `mPdf.Documents`).
public sealed class AppConfig
{
    private readonly string _file;

    public AppConfig(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "config.json");
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mPDF");

    /// Cria um `.bak` (`FilePath + ".bak"`) na primeira gravação de cada sessão de documento — default
    /// TRUE (comportamento seguro por padrão: preferir sobrar um backup a nunca ter um). Lido do disco
    /// a cada acesso (arquivo pequeno, poucas leituras por sessão de app — sem cache, para refletir uma
    /// mudança de configuração feita enquanto o app está aberto, se um dia houver UI de settings).
    public bool CriarBackup
    {
        get => Load().CriarBackup;
        set => Save(Load() with { CriarBackup = value });
    }

    /// Autor gravado no `/T` das anotações de marcação criadas pelo usuário (Task 6, Plano 3a — mesmo
    /// campo que `PdfEditor.AddAnnotation`/`ReadAnnotations` já tratam como "autor" via
    /// `PdfMarkupAnnotation.SetTitle`/`GetTitle`, ver doc XML lá). `null` persistido (nunca configurado
    /// pelo usuário ainda) cai no default `Environment.UserName` — mesmo espírito de "default seguro"
    /// de `CriarBackup`, só que aqui o default depende do AMBIENTE (não é um literal fixo), então não
    /// pode virar o valor default do parâmetro do record (seria congelado no momento errado, e mudaria
    /// o "usuário atual" de sessão em sessão sem refletir isso pra quem já salvou um Autor explícito).
    public string Autor
    {
        get => Load().Autor ?? Environment.UserName;
        set => Save(Load() with { Autor = value });
    }

    /// Teto de RAM do histórico de desfazer/refazer (Task 1, Plano 5) — espelha
    /// `DocumentSession.DefaultMaxUndoRamBytes` (256 MB) como default persistido. NÃO exposto na UI v1
    /// (brief) — nenhuma tela de configurações lê/grava isto ainda, mesmo estado de `CriarBackup`/
    /// `Autor` antes de existir uma tela de Settings; existe pra permitir ajuste futuro sem migração de
    /// schema (e pra `MainViewModel.OpenPath` já poder consultar um valor real hoje, em vez de um
    /// literal hardcoded, ao chamar `DocumentSession.OpenAsync`).
    /// Rider (revisão da Task 1, Plano 5): `Math.Max(1, ...)` no getter — `SnapshotStack`/`SpillableStack`
    /// (ver doc XML lá) já REJEITAM `< 1` com `ArgumentOutOfRangeException`. Sem o clamp aqui, um
    /// `config.json` editado à mão com `0` (ou um valor negativo) fazia TODA abertura de documento
    /// lançar dentro de `DocumentSession.OpenAsync` — nenhuma tela de Settings valida a entrada ainda
    /// (v1), então o único portão contra um valor inválido É este getter. Clampar aqui (não no setter)
    /// preserva o valor CRU gravado no disco (`Save`/`Load` continuam simétricos, útil se uma futura UI
    /// de Settings quiser mostrar/corrigir o valor bruto) — só a LEITURA usada por `OpenPath` nunca
    /// devolve um teto inválido.
    public long MaxUndoRamBytes
    {
        get => Math.Max(1, Load().MaxUndoRamBytes);
        set => Save(Load() with { MaxUndoRamBytes = value });
    }

    /// Teto de disco do histórico de desfazer/refazer (Task 1, Plano 5) — espelha
    /// `DocumentSession.DefaultMaxUndoSpillBytes` (2 GB). Mesma justificativa de não-exposição na UI v1
    /// que `MaxUndoRamBytes` acima. Rider (revisão da Task 1, Plano 5): mesmo clamp `Math.Max(1, ...)`
    /// no getter, mesmo motivo — ver doc XML de `MaxUndoRamBytes`.
    public long MaxUndoSpillBytes
    {
        get => Math.Max(1, Load().MaxUndoSpillBytes);
        set => Save(Load() with { MaxUndoSpillBytes = value });
    }

    /// Nitidez extra do texto (Task 2, Plano 13) — liga o SUPERSAMPLING de render (fator 2.0, ver
    /// `PageViewModel.ComputeRenderScale`/`DocumentViewModel.SupersampleFactor`) quando `true`. Default
    /// FALSE (mesmo espírito de "default seguro" de `CriarBackup`, mas na direção OPOSTA: aqui o default
    /// seguro é NÃO gastar memória/CPU extra pra ninguém — a MEDIÇÃO da Task 1 achou só um ganho PEQUENO
    /// de nitidez, e só no fator 2.0 — nunca 1.5 —, contra um custo real de ~4x memória/tempo de render
    /// por página; ligar isto é uma escolha explícita do usuário, não um "quanto mais opções ligadas,
    /// melhor"). Um `config.json` ANTIGO (gravado antes desta task) cai no default `false` do parâmetro
    /// do record — mesmo mecanismo que já cobre `MaxUndoRamBytes`/`MaxUndoSpillBytes` pra configs
    /// pré-Plano-5 (ver doc XML de `ConfigData` abaixo).
    public bool NitidezExtra
    {
        get => Load().NitidezExtra;
        set => Save(Load() with { NitidezExtra = value });
    }

    // Task 1 (Plano 5): 2 campos NOVOS com valor default = as constantes de DocumentSession — um
    // config.json ANTIGO (gravado antes desta task, só com CriarBackup/Autor) continua desserializando
    // normalmente: System.Text.Json usa o default do PARÂMETRO do record pra qualquer propriedade
    // AUSENTE no JSON (testado: MaxUndoCeilings_OldConfigFileWithoutNewFields_FallsBackToDefaults).
    // Task 2 (Plano 13): mesmo mecanismo pro campo `NitidezExtra` novo — default `false` do parâmetro
    // cobre qualquer config.json gravado antes desta task.
    private sealed record ConfigData(
        bool CriarBackup = true,
        string? Autor = null,
        long MaxUndoRamBytes = DocumentSession.DefaultMaxUndoRamBytes,
        long MaxUndoSpillBytes = DocumentSession.DefaultMaxUndoSpillBytes,
        bool NitidezExtra = false);

    private ConfigData Load()
    {
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(_file)) ?? new ConfigData()
                : new ConfigData();
        }
        catch (JsonException) { return new ConfigData(); } // arquivo corrompido = default seguro, nunca crash
    }

    private void Save(ConfigData data) => File.WriteAllText(_file, JsonSerializer.Serialize(data));
}
