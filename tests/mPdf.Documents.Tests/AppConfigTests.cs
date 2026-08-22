using mPdf.Documents;
using Xunit;

namespace mPdf.Documents.Tests;

// Task 3 (Plano 3a): exemplar de RecentFilesStoreTests (mPdf.App.Tests) — diretório injetável no
// construtor, nunca toca %AppData%\mPDF real durante os testes.
public class AppConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-cfg-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact] // default seguro: CriarBackup=true mesmo sem config.json existente ainda (1º uso do app)
    public void CriarBackup_DefaultsToTrue_WhenNoConfigFileExists()
    {
        var config = new AppConfig(_dir);
        Assert.True(config.CriarBackup);
    }

    [Fact] // set persiste entre INSTÂNCIAS diferentes (mesmo padrão de RecentFilesStore.Add/Load)
    public void CriarBackup_SetPersistsAcrossInstances()
    {
        var c1 = new AppConfig(_dir);
        c1.CriarBackup = false;

        var c2 = new AppConfig(_dir);
        Assert.False(c2.CriarBackup);
    }

    [Fact] // set true -> false -> true de novo (garante que o valor não fica "preso" na 1ª mudança)
    public void CriarBackup_CanBeToggledBackAndForth()
    {
        var config = new AppConfig(_dir);
        config.CriarBackup = false;
        Assert.False(config.CriarBackup);
        config.CriarBackup = true;
        Assert.True(config.CriarBackup);
    }

    [Fact] // arquivo corrompido -> default seguro (true), nunca lança (mesmo tratamento de RecentFilesStore.Load)
    public void CriarBackup_CorruptedFile_ReturnsDefaultTrue_NeverThrows()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ isto não é json válido");

        var config = new AppConfig(_dir);

        Assert.True(config.CriarBackup);
    }

    // ---- Task 6 (Plano 3a): Autor -----------------------------------------------------------------

    [Fact] // default: sem config.json ainda -> Environment.UserName (nunca um literal fixo/vazio)
    public void Autor_DefaultsToEnvironmentUserName_WhenNoConfigFileExists()
    {
        var config = new AppConfig(_dir);
        Assert.Equal(Environment.UserName, config.Autor);
    }

    [Fact] // set persiste entre INSTÂNCIAS diferentes (mesmo padrão de CriarBackup acima)
    public void Autor_SetPersistsAcrossInstances()
    {
        var c1 = new AppConfig(_dir);
        c1.Autor = "Fulano de Tal";

        var c2 = new AppConfig(_dir);
        Assert.Equal("Fulano de Tal", c2.Autor);
    }

    [Fact] // arquivo corrompido -> default seguro (Environment.UserName), nunca lança
    public void Autor_CorruptedFile_ReturnsDefaultUserName_NeverThrows()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ isto não é json válido");

        var config = new AppConfig(_dir);

        Assert.Equal(Environment.UserName, config.Autor);
    }

    // ---- Task 1 (Plano 5): tetos de undo (RAM/disco) -- NÃO exposto na UI v1 (brief), mas persistido
    // como qualquer outra config, MESMO exemplar de CriarBackup/Autor acima.

    [Fact] // default: sem config.json ainda -> DocumentSession.DefaultMaxUndoRamBytes (256 MB)
    public void MaxUndoRamBytes_DefaultsToDocumentSessionDefault_WhenNoConfigFileExists()
    {
        var config = new AppConfig(_dir);
        Assert.Equal(DocumentSession.DefaultMaxUndoRamBytes, config.MaxUndoRamBytes);
    }

    [Fact]
    public void MaxUndoRamBytes_SetPersistsAcrossInstances()
    {
        var c1 = new AppConfig(_dir);
        c1.MaxUndoRamBytes = 123_456;

        var c2 = new AppConfig(_dir);
        Assert.Equal(123_456, c2.MaxUndoRamBytes);
    }

    [Fact] // default: sem config.json ainda -> DocumentSession.DefaultMaxUndoSpillBytes (2 GB)
    public void MaxUndoSpillBytes_DefaultsToDocumentSessionDefault_WhenNoConfigFileExists()
    {
        var config = new AppConfig(_dir);
        Assert.Equal(DocumentSession.DefaultMaxUndoSpillBytes, config.MaxUndoSpillBytes);
    }

    [Fact]
    public void MaxUndoSpillBytes_SetPersistsAcrossInstances()
    {
        var c1 = new AppConfig(_dir);
        c1.MaxUndoSpillBytes = 987_654_321;

        var c2 = new AppConfig(_dir);
        Assert.Equal(987_654_321, c2.MaxUndoSpillBytes);
    }

    [Fact] // arquivo corrompido -> defaults seguros, nunca lança (mesmo tratamento de CriarBackup/Autor)
    public void MaxUndoCeilings_CorruptedFile_ReturnDefaults_NeverThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ isto não é json válido");

        var config = new AppConfig(_dir);

        Assert.Equal(DocumentSession.DefaultMaxUndoRamBytes, config.MaxUndoRamBytes);
        Assert.Equal(DocumentSession.DefaultMaxUndoSpillBytes, config.MaxUndoSpillBytes);
    }

    [Fact] // config.json ANTIGO (de antes desta task, só CriarBackup/Autor) continua legível -- os 2
    // campos NOVOS caem no default (System.Text.Json usa o valor default do record pra propriedade
    // ausente no JSON), sem exigir migração nem quebrar a leitura do que já existia.
    public void MaxUndoCeilings_OldConfigFileWithoutNewFields_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"CriarBackup":false,"Autor":"Fulano"}""");

        var config = new AppConfig(_dir);

        Assert.False(config.CriarBackup); // campo antigo continua lendo certo
        Assert.Equal("Fulano", config.Autor);
        Assert.Equal(DocumentSession.DefaultMaxUndoRamBytes, config.MaxUndoRamBytes); // campo novo -> default
        Assert.Equal(DocumentSession.DefaultMaxUndoSpillBytes, config.MaxUndoSpillBytes);
    }

    // ---- Rider (revisão da Task 1, Plano 5): clamp Math.Max(1, ...) nos getters -----------------------
    //
    // Achado real (não hipotético): um config.json editado à mão com um teto 0 (ou negativo) fazia TODA
    // abertura de documento lançar `ArgumentOutOfRangeException` dentro de `SnapshotStack`/`SpillableStack`
    // (ver doc XML de `SnapshotStack` — "É preciso... pelo menos 1 byte") — `MainViewModel.OpenPath` passa
    // `config.MaxUndoRamBytes`/`MaxUndoSpillBytes` direto pra `DocumentSession.OpenAsync`, sem nenhum
    // gate no meio. Os testes abaixo gravam o JSON diretamente (mesmo padrão de
    // `MaxUndoCeilings_OldConfigFileWithoutNewFields_FallsBackToDefaults` acima) — `AppConfig` não tem
    // setter capaz de gravar um valor inválido de propósito (o próprio setter aceita qualquer long, mas
    // nenhum teste de produção jamais chamaria `config.MaxUndoRamBytes = 0`; o cenário real é edição
    // manual do arquivo).

    [Fact] // 0 no arquivo -> getter devolve 1 (nunca o valor cru que faria SnapshotStack lançar)
    public void MaxUndoRamBytes_ZeroInFile_ClampsToOne()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"MaxUndoRamBytes":0}""");

        var config = new AppConfig(_dir);

        Assert.Equal(1, config.MaxUndoRamBytes);
    }

    [Fact] // negativo no arquivo -> mesmo clamp pra 1 (não só o caso 0)
    public void MaxUndoRamBytes_NegativeInFile_ClampsToOne()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"MaxUndoRamBytes":-500}""");

        var config = new AppConfig(_dir);

        Assert.Equal(1, config.MaxUndoRamBytes);
    }

    [Fact] // mesmo clamp pro teto de disco (0 no arquivo -> 1)
    public void MaxUndoSpillBytes_ZeroInFile_ClampsToOne()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"MaxUndoSpillBytes":0}""");

        var config = new AppConfig(_dir);

        Assert.Equal(1, config.MaxUndoSpillBytes);
    }

    [Fact] // Rider (revisão desta task): faltava o espelho do caso negativo pro teto de disco -- mesmo
    // par que MaxUndoRamBytes já tinha (Zero + Negativo), agora simétrico pros 2 campos.
    public void MaxUndoSpillBytes_NegativeInFile_ClampsToOne()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"MaxUndoSpillBytes":-500}""");

        var config = new AppConfig(_dir);

        Assert.Equal(1, config.MaxUndoSpillBytes);
    }

    [Fact] // valor válido (> 1) no arquivo passa direto -- o clamp não interfere no caminho normal
    public void MaxUndoRamBytes_ValidValueInFile_PassesThroughUnclamped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"MaxUndoRamBytes":999}""");

        var config = new AppConfig(_dir);

        Assert.Equal(999, config.MaxUndoRamBytes);
    }

    [Fact] // Rider (revisão desta task): mesmo espelho pro teto de disco -- fechando o trio completo
    // (Zero/Negativo/Válido) pros 2 campos, não só pra MaxUndoRamBytes.
    public void MaxUndoSpillBytes_ValidValueInFile_PassesThroughUnclamped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"MaxUndoSpillBytes":999}""");

        var config = new AppConfig(_dir);

        Assert.Equal(999, config.MaxUndoSpillBytes);
    }

    // ---- Task 2 (Plano 13): NitidezExtra -- mesmo trio de provas de CriarBackup/Autor acima ------------

    [Fact] // default seguro: DESLIGADO mesmo sem config.json existente ainda (1º uso do app) -- ao
    // contrário de CriarBackup (default true), aqui o default seguro é NÃO gastar memória/CPU extra pra
    // ninguém (ver doc XML de AppConfig.NitidezExtra).
    public void NitidezExtra_DefaultsToFalse_WhenNoConfigFileExists()
    {
        var config = new AppConfig(_dir);
        Assert.False(config.NitidezExtra);
    }

    [Fact] // set persiste entre INSTÂNCIAS diferentes (mesmo padrão de CriarBackup/Autor)
    public void NitidezExtra_SetPersistsAcrossInstances()
    {
        var c1 = new AppConfig(_dir);
        c1.NitidezExtra = true;

        var c2 = new AppConfig(_dir);
        Assert.True(c2.NitidezExtra);
    }

    [Fact] // true -> false -> true de novo (garante que o valor não fica "preso" na 1ª mudança)
    public void NitidezExtra_CanBeToggledBackAndForth()
    {
        var config = new AppConfig(_dir);
        config.NitidezExtra = true;
        Assert.True(config.NitidezExtra);
        config.NitidezExtra = false;
        Assert.False(config.NitidezExtra);
    }

    [Fact] // arquivo corrompido -> default seguro (false), nunca lança (mesmo tratamento de CriarBackup/Autor)
    public void NitidezExtra_CorruptedFile_ReturnsDefaultFalse_NeverThrows()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ isto não é json válido");

        var config = new AppConfig(_dir);

        Assert.False(config.NitidezExtra);
    }

    [Fact] // config.json ANTIGO (de antes desta task, sem o campo NitidezExtra) continua legível -- o
    // campo NOVO cai no default `false` (System.Text.Json usa o default do parâmetro do record pra
    // propriedade AUSENTE no JSON), sem exigir migração nem quebrar a leitura do que já existia -- mesmo
    // mecanismo já provado por MaxUndoCeilings_OldConfigFileWithoutNewFields_FallsBackToDefaults.
    public void NitidezExtra_OldConfigFileWithoutField_FallsBackToFalse()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"CriarBackup":false,"Autor":"Fulano"}""");

        var config = new AppConfig(_dir);

        Assert.False(config.NitidezExtra);
        Assert.False(config.CriarBackup); // campos antigos continuam lendo certo
        Assert.Equal("Fulano", config.Autor);
    }
}
