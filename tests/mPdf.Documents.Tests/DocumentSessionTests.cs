using mPdf.Documents;
using Xunit;

namespace mPdf.Documents.Tests;

public class DocumentSessionTests
{
    private static string CopyFixtureToTemp()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-test-{Guid.NewGuid():N}.pdf");
        File.Copy(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), tmp);
        return tmp;
    }

    [Fact] // abre, expõe nome/caminho e renderer funcional
    public void Open_ValidPdf_ExposesRendererAndMetadata()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.Equal(tmp, s.FilePath);
            Assert.Equal(Path.GetFileName(tmp), s.FileName);
            Assert.Equal(1, s.Renderer.PageCount);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // REQUISITO DO SPEC §5.1: o arquivo em disco fica LIVRE enquanto aberto
    public void Open_FileRemainsUnlocked_CanDeleteWhileSessionOpen()
    {
        var tmp = CopyFixtureToTemp();
        using var s = DocumentSession.Open(tmp);
        File.Delete(tmp);                       // não pode lançar
        Assert.False(File.Exists(tmp));
        Assert.Equal(1, s.Renderer.PageCount);  // sessão segue funcional (memória)
    }

    [Fact] // arquivo inexistente -> FileNotFoundException
    public void Open_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            DocumentSession.Open(Path.Combine(Path.GetTempPath(), "nao-existe-mpdf.pdf")));
    }

    [Fact] // Task 7: caminho async do app (ReadAllBytesAsync + ctor do renderer em Task.Run) — awaitable, sem travar a chamadora
    public async Task OpenAsync_ValidPdf_ExposesRendererAndMetadata()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = await DocumentSession.OpenAsync(tmp);
            Assert.Equal(tmp, s.FilePath);
            Assert.Equal(Path.GetFileName(tmp), s.FileName);
            Assert.Equal(1, s.Renderer.PageCount);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Item (c) da Task 1 (Plano 3a): Open (síncrono) já materializa PageSizes — 1 entrada por
    // página, batendo com Renderer.GetPageSize(i) chamado diretamente (mesmos bytes de PDF).
    public void Open_PageSizes_MatchesRendererGetPageSizePerPage()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.Equal(s.Renderer.PageCount, s.PageSizes.Count);
            for (int i = 0; i < s.Renderer.PageCount; i++)
                Assert.Equal(s.Renderer.GetPageSize(i), s.PageSizes[i]);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Mesma prova pelo caminho ASSÍNCRONO — PageSizes precisa estar pronta (não vazia/atrasada)
    // assim que o Task devolvido por OpenAsync completa, já que a coleta roda DENTRO do Task.Run que
    // constrói a sessão (mesmo construtor privado do caminho síncrono acima).
    public async Task OpenAsync_PageSizes_MatchesRendererGetPageSizePerPage()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = await DocumentSession.OpenAsync(tmp);
            Assert.Equal(s.Renderer.PageCount, s.PageSizes.Count);
            for (int i = 0; i < s.Renderer.PageCount; i++)
                Assert.Equal(s.Renderer.GetPageSize(i), s.PageSizes[i]);
        }
        finally { File.Delete(tmp); }
    }

    // ---- Task 3 (Plano 3a): Apply ----------------------------------------------------------------

    private static string TempConfigDir() => Path.Combine(Path.GetTempPath(), $"mpdf-cfg-{Guid.NewGuid():N}");

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDir(string dir) { try { Directory.Delete(dir, true); } catch { } }

    [Fact] // troca Snapshot/Renderer/PageSizes por instâncias NOVAS batendo com o documento aplicado —
    // fixture-a4 (1 página) -> fixture-30p (30 páginas), a prova mais direta de "documento realmente novo".
    public void Apply_SwapsSnapshotRendererAndPageSizes_ForTheNewDocument()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.Equal(1, s.Renderer.PageCount);
            Assert.Single(s.PageSizes);

            var bytes = Fixtures.ThirtyPages();
            s.Apply(bytes);

            Assert.Same(bytes, s.Snapshot); // sem cópia defensiva — troca de REFERÊNCIA, nunca mutação
            Assert.Equal(30, s.Renderer.PageCount);
            Assert.Equal(30, s.PageSizes.Count);
            for (int i = 0; i < s.Renderer.PageCount; i++)
                Assert.Equal(s.Renderer.GetPageSize(i), s.PageSizes[i]);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // o renderer ANTIGO é enfileirado em PendingDisposals (fila serial — Apply nunca descarta
    // sincronamente, mesmo padrão de DocumentViewModel.Dispose); depois de drenar a fila, o handle
    // antigo está mesmo descartado (ObjectDisposedException gerenciada, nunca AV nativa).
    public void Apply_EnqueuesOldRenderer_ForDisposalViaPendingDisposals()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            var oldRenderer = s.Renderer;

            s.Apply(Fixtures.ThirtyPages());
            Assert.NotSame(oldRenderer, s.Renderer);

            bool finished;
            try { finished = PendingDisposals.WaitAll(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { finished = true; }
            Assert.True(finished, "descarte do renderer antigo (offloaded) não terminou a tempo");

            Assert.Throws<ObjectDisposedException>(() => oldRenderer.RenderPage(0, 1.0));
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // bytes inválidos -> Apply lança e a sessão fica INTACTA (Renderer/Snapshot/IsDirty do
    // estado anterior) — o novo renderer é construído ANTES de qualquer mutação de campo.
    public void Apply_InvalidBytes_ThrowsAndLeavesSessionUnchanged()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            var originalRenderer = s.Renderer;
            var originalSnapshot = s.Snapshot;

            Assert.Throws<ArgumentException>(() => s.Apply([0x00, 0x01, 0x02, 0x03]));

            Assert.Same(originalRenderer, s.Renderer);
            Assert.Same(originalSnapshot, s.Snapshot);
            Assert.False(s.IsDirty);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // IsDirty: false na abertura, true depois de Apply com conteúdo DIFERENTE do salvo/aberto —
    // DirtyChanged dispara exatamente 1x (na virada de valor), não a cada Apply.
    public void Apply_MarksDirty_RaisesDirtyChangedOnlyOnValueFlip()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            int raised = 0;
            s.DirtyChanged += (_, _) => raised++;
            Assert.False(s.IsDirty);

            s.Apply(Fixtures.ThirtyPages());
            Assert.True(s.IsDirty);
            Assert.Equal(1, raised);

            // 2º Apply com o MESMO conteúdo (30 páginas de novo, ainda diferente do hash SALVO — que
            // continua sendo o do fixture-a4 original) -> IsDirty continua true, sem VIRAR de valor.
            // (Nota: reaplicar Fixtures.A4() aqui faria IsDirty voltar a false de propósito — o
            // snapshot ficaria byte-a-byte igual ao que foi ABERTO/salvo — prova útil do design de
            // hash mas NÃO o que este teste quer verificar; ver Apply_RoundTripBackToSavedContent_ClearsDirty.)
            s.Apply(Fixtures.ThirtyPages());
            Assert.True(s.IsDirty);
            Assert.Equal(1, raised); // não mudou de valor -> não deve disparar de novo
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Applied dispara em TODO Apply, mesmo quando IsDirty não muda de valor — é o que a VM usa
    // pra saber "reconstrua Pages/Thumbnails", que precisa acontecer sempre, não só na virada de dirty.
    public void Apply_RaisesAppliedEvent_EveryTime_RegardlessOfDirtyFlip()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            int applied = 0;
            s.Applied += (_, _) => applied++;

            s.Apply(Fixtures.ThirtyPages());
            Assert.Equal(1, applied);

            // 2º Apply — devolve o conteúdo ORIGINAL (fixture-a4, o mesmo hash da abertura), então
            // IsDirty na verdade VIRA para false aqui (ver Apply_RoundTripBackToSavedContent_ClearsDirty
            // logo abaixo) — mas Applied dispara de novo de qualquer forma: reconstrução de
            // Pages/Thumbnails tem que acontecer todo Apply, independente do valor de IsDirty.
            s.Apply(Fixtures.A4());
            Assert.Equal(2, applied);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Round-trip: aplicar de volta o EXATO conteúdo que foi aberto/salvo por último faz IsDirty
    // voltar a false — prova o design de hash (não um flag "foi editado alguma vez"); é o que vai
    // permitir a Task 4 (undo) desfazer uma edição até o estado salvo e ver o "•" sumir sozinho.
    public void Apply_RoundTripBackToSavedContent_ClearsDirty()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.False(s.IsDirty);

            s.Apply(Fixtures.ThirtyPages());
            Assert.True(s.IsDirty);

            s.Apply(Fixtures.A4()); // byte-a-byte igual ao conteúdo aberto/nunca-editado
            Assert.False(s.IsDirty);
        }
        finally { File.Delete(tmp); }
    }

    // ---- Task 3 (Plano 3a): Save / .bak / config -------------------------------------------------

    [Fact] // 1ª gravação com CriarBackup=true (default): grava o snapshot no arquivo E cria .bak com o
    // conteúdo ORIGINAL (pré-edição); 2ª gravação NÃO regrava o .bak (continua = original da 1ª vez).
    public void Save_FirstSave_CreatesBackupOfOriginal_SecondSaveDoesNotOverwriteIt()
    {
        var tmp = CopyFixtureToTemp();
        var configDir = TempConfigDir();
        var bak = tmp + ".bak";
        try
        {
            var originalBytes = File.ReadAllBytes(tmp);
            using var s = DocumentSession.Open(tmp);
            var config = new AppConfig(configDir);

            s.Apply(Fixtures.ThirtyPages());
            s.Save(config);

            Assert.True(File.Exists(bak));
            Assert.Equal(originalBytes, File.ReadAllBytes(bak));
            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(tmp));

            s.Apply(Fixtures.A4());
            s.Save(config); // 2ª gravação da SESSÃO

            Assert.Equal(originalBytes, File.ReadAllBytes(bak)); // .bak intocado
            Assert.Equal(Fixtures.A4(), File.ReadAllBytes(tmp)); // arquivo principal SEGUE atualizando
        }
        finally { File.Delete(tmp); TryDelete(bak); TryDeleteDir(configDir); }
    }

    [Fact] // CONTROLE NEGATIVO: config.CriarBackup=false -> nenhum .bak, nem na 1ª gravação.
    public void Save_CriarBackupFalse_NeverCreatesBackup()
    {
        var tmp = CopyFixtureToTemp();
        var configDir = TempConfigDir();
        var bak = tmp + ".bak";
        try
        {
            using var s = DocumentSession.Open(tmp);
            var config = new AppConfig(configDir) { CriarBackup = false };
            s.Apply(Fixtures.ThirtyPages());

            s.Save(config);

            Assert.False(File.Exists(bak));
            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(tmp));
        }
        finally { File.Delete(tmp); TryDelete(bak); TryDeleteDir(configDir); }
    }

    [Fact]
    public void Save_ClearsIsDirty()
    {
        var tmp = CopyFixtureToTemp();
        var configDir = TempConfigDir();
        var bak = tmp + ".bak";
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.Apply(Fixtures.ThirtyPages());
            Assert.True(s.IsDirty);

            s.Save(new AppConfig(configDir));

            Assert.False(s.IsDirty);
        }
        finally { File.Delete(tmp); TryDelete(bak); TryDeleteDir(configDir); }
    }

    [Fact] // CAMINHO DE ERRO: destino travado (aberto com FileShare.None por "outro processo") ->
    // exceção legível em pt-BR, arquivo ORIGINAL intacto (bytes inalterados), temp file removido —
    // nenhum lixo sobra no diretório do usuário mesmo quando o Save falha no meio do caminho.
    public void Save_DestinationLocked_ThrowsReadableException_OriginalIntact_TempCleanedUp()
    {
        var tmp = CopyFixtureToTemp();
        var configDir = TempConfigDir();
        try
        {
            var originalBytes = File.ReadAllBytes(tmp);
            using var s = DocumentSession.Open(tmp);
            s.Apply(Fixtures.ThirtyPages());

            using (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Assert.Throws<IOException>(() => s.Save(new AppConfig(configDir)));
                Assert.Contains("Não foi possível salvar", ex.Message);
                Assert.Contains(tmp, ex.Message);
            }

            Assert.Equal(originalBytes, File.ReadAllBytes(tmp)); // NUNCA tocado — Replace falhou antes de trocar nada
            Assert.True(s.IsDirty, "sessão deve CONTINUAR suja: o Save falhou, nada foi persistido");

            string dir = Path.GetDirectoryName(tmp)!;
            var leftovers = Directory.GetFiles(dir, $".{Path.GetFileName(tmp)}.*.tmp");
            Assert.Empty(leftovers);
        }
        finally { File.Delete(tmp); TryDeleteDir(configDir); }
    }

    // ---- Task 3 (Plano 3a): SaveAs -----------------------------------------------------------------

    [Fact] // escrita atômica simples pro NOVO caminho, sem .bak; FilePath atualiza; IsDirty limpa;
    // o arquivo ORIGINAL (caminho antigo) nunca é tocado.
    public void SaveAs_WritesToNewPath_UpdatesFilePath_ClearsDirty_LeavesOriginalUntouched()
    {
        var tmp = CopyFixtureToTemp();
        var originalBytes = File.ReadAllBytes(tmp);
        var newPath = Path.Combine(Path.GetTempPath(), $"mpdf-saveas-{Guid.NewGuid():N}.pdf");
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.Apply(Fixtures.ThirtyPages());

            s.SaveAs(newPath);

            Assert.Equal(newPath, s.FilePath);
            Assert.False(s.IsDirty);
            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(newPath));
            Assert.Equal(originalBytes, File.ReadAllBytes(tmp)); // caminho ANTIGO nunca escrito
            Assert.False(File.Exists(newPath + ".bak")); // SaveAs nunca cria .bak
        }
        finally { File.Delete(tmp); TryDelete(newPath); }
    }

    [Fact] // SaveAs sobre um caminho JÁ EXISTENTE também funciona (File.Replace, não só File.Move)
    public void SaveAs_ToExistingFile_Overwrites()
    {
        var tmp = CopyFixtureToTemp();
        var otherExisting = CopyFixtureToTemp(); // já existe, conteúdo A4 original
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.Apply(Fixtures.ThirtyPages());

            s.SaveAs(otherExisting);

            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(otherExisting));
        }
        finally { File.Delete(tmp); TryDelete(otherExisting); }
    }

    [Fact] // FilePathChanged dispara em SaveAs (mesmo sem IsDirty mudar de valor — ex.: sessão já limpa)
    public void SaveAs_RaisesFilePathChanged()
    {
        var tmp = CopyFixtureToTemp();
        var newPath = Path.Combine(Path.GetTempPath(), $"mpdf-saveas-{Guid.NewGuid():N}.pdf");
        try
        {
            using var s = DocumentSession.Open(tmp);
            int raised = 0;
            s.FilePathChanged += (_, _) => raised++;

            s.SaveAs(newPath); // sessão já estava LIMPA (IsDirty=false antes e depois) — ainda assim dispara

            Assert.Equal(1, raised);
        }
        finally { File.Delete(tmp); TryDelete(newPath); }
    }

    // ---- Fix pós-revisão (C1): HandleReplaceFailure — decisão de limpeza/resgate pós-falha ---------
    //
    // Win32 ReplaceFile documenta ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: o rename FINAL (temp -> destino)
    // pode falhar DEPOIS que o destino original já foi consumido (renomeado pro backup, ou apagado se
    // sem backup). O código ANTIGO apagava o temp incondicionalmente no catch de AtomicWrite — se essa
    // falha específica acontecer, isso apaga a ÚLTIMA CÓPIA dos dados, deixando NENHUM dos dois
    // arquivos no disco. `backupPath` é null em toda gravação após a 1ª da sessão, com
    // CriarBackup=false, e em todo SaveAs — não é um caso de canto raro.

    [Fact] // destPath PRESENTE -> a falha aconteceu ANTES da troca consumir o destino; original
    // intacto, temp é lixo seguro pra remover.
    public void HandleReplaceFailure_DestPathExists_DeletesTempAndReportsOriginalIntact()
    {
        var dir = TempConfigDir();
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "dest.pdf");
        var temp = Path.Combine(dir, ".dest.pdf.abc123.tmp");
        File.WriteAllBytes(dest, [1, 2, 3]);
        File.WriteAllBytes(temp, [9, 9, 9]);
        try
        {
            var outcome = DocumentSession.HandleReplaceFailure(temp, dest);

            Assert.Equal(WriteFailureOutcome.OriginalIntact, outcome);
            Assert.False(File.Exists(temp));
            Assert.True(File.Exists(dest));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dest));
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact] // destPath AUSENTE (o rename final falhou DEPOIS do destino original já ter sido
    // consumido) -> restaura movendo o temp de volta pro lugar certo. Os dados sobrevivem.
    public void HandleReplaceFailure_DestPathMissing_RestoresFromTemp()
    {
        var dir = TempConfigDir();
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "dest.pdf"); // NÃO existe — simula destino já consumido pelo Replace
        var temp = Path.Combine(dir, ".dest.pdf.abc123.tmp");
        File.WriteAllBytes(temp, [9, 9, 9]);
        try
        {
            var outcome = DocumentSession.HandleReplaceFailure(temp, dest);

            Assert.Equal(WriteFailureOutcome.Recovered, outcome);
            Assert.False(File.Exists(temp));
            Assert.True(File.Exists(dest));
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(dest));
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact] // CRÍTICO — a prova direta do bug C1: destPath ausente E a restauração TAMBÉM falha
    // (diretório do destino sumiu) -> o temp é PRESERVADO (nunca apagado), porque é a ÚLTIMA CÓPIA
    // sobrevivente dos dados do usuário. O código antigo apagava incondicionalmente aqui.
    public void HandleReplaceFailure_DestPathMissingAndRestoreFails_PreservesTemp()
    {
        var dir = TempConfigDir();
        Directory.CreateDirectory(dir);
        var missingDir = Path.Combine(dir, "sumiu"); // NUNCA criado -> o Move de resgate falha
        var dest = Path.Combine(missingDir, "dest.pdf");
        var temp = Path.Combine(dir, ".dest.pdf.abc123.tmp");
        File.WriteAllBytes(temp, [9, 9, 9]);
        try
        {
            var outcome = DocumentSession.HandleReplaceFailure(temp, dest);

            Assert.Equal(WriteFailureOutcome.DataPreservedInTemp, outcome);
            Assert.True(File.Exists(temp)); // NUNCA apagado
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(temp));
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact] // Item 3 (revisão final pré-merge) — honestidade: destPath ausente E a restauração falha E
    // `temp` TAMBÉM sumiu (as DUAS cópias perdidas) — outcome vira DataLost, NÃO DataPreservedInTemp
    // (que prometeria, na mensagem, um caminho de resgate que não existe).
    public void HandleReplaceFailure_DestPathAndTempBothMissing_ReportsDataLost()
    {
        var dir = TempConfigDir();
        Directory.CreateDirectory(dir);
        var missingDir = Path.Combine(dir, "sumiu"); // NUNCA criado -> o Move de resgate falha
        var dest = Path.Combine(missingDir, "dest.pdf");
        var temp = Path.Combine(dir, ".dest.pdf.abc123.tmp"); // NUNCA criado -> simula as duas cópias perdidas
        try
        {
            var outcome = DocumentSession.HandleReplaceFailure(temp, dest);

            Assert.Equal(WriteFailureOutcome.DataLost, outcome);
            Assert.False(File.Exists(temp));
            Assert.False(File.Exists(dest));
        }
        finally { TryDeleteDir(dir); }
    }

    // ---- Fix pós-revisão (I2): falha na ESCRITA do temporário --------------------------------------

    [Fact] // I2: falha ao ESCREVER o temporário (diretório de destino inexistente) -> exceção legível
    // em pt-BR, NENHUM temporário sobra (a falha acontece ANTES de qualquer tentativa de trocar o
    // destino — não há nem "original" nem "novo temp" para confundir).
    public void SaveAs_TempWriteFails_ThrowsReadableException_NoTempLeftBehind()
    {
        var tmp = CopyFixtureToTemp();
        var badDir = Path.Combine(Path.GetTempPath(), $"mpdf-sem-dir-{Guid.NewGuid():N}"); // nunca criado, de propósito
        var badPath = Path.Combine(badDir, "novo.pdf");
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.Apply(Fixtures.ThirtyPages());

            var ex = Assert.Throws<IOException>(() => s.SaveAs(badPath));
            Assert.Contains("Não foi possível salvar", ex.Message);
            Assert.Contains("original não foi alterado", ex.Message);

            Assert.False(Directory.Exists(badDir)); // nada foi criado — nenhum temp perdido por aí
        }
        finally { File.Delete(tmp); }
    }

    // ---- Riders (revisão pós-Task 3) ----------------------------------------------------------------

    [Fact] // um .bak PRÉ-EXISTENTE (de uma sessão de edição ANTERIOR sobre o mesmo arquivo) é
    // SUBSTITUÍDO pela 1ª gravação desta sessão — não falha, não é ignorado, não vira ".bak.bak".
    public void Save_FirstSave_ReplacesPreExistingBackup()
    {
        var tmp = CopyFixtureToTemp();
        var configDir = TempConfigDir();
        var bak = tmp + ".bak";
        try
        {
            File.WriteAllBytes(bak, [0xDE, 0xAD, 0xBE, 0xEF]); // "backup" de uma sessão anterior, lixo antigo
            var originalBytes = File.ReadAllBytes(tmp);

            using var s = DocumentSession.Open(tmp);
            s.Apply(Fixtures.ThirtyPages());
            s.Save(new AppConfig(configDir));

            Assert.Equal(originalBytes, File.ReadAllBytes(bak)); // .bak agora é o ORIGINAL desta sessão
        }
        finally { File.Delete(tmp); TryDelete(bak); TryDeleteDir(configDir); }
    }

    [Fact] // temporários ÓRFÃOS de tentativas ANTERIORES (mesmo padrão de nome, mas de uma sessão de
    // app passada que não completou) são varridos por um Save bem-sucedido — melhor esforço.
    public void Save_SweepsOrphanTempFiles_OnSuccess()
    {
        var tmp = CopyFixtureToTemp();
        var configDir = TempConfigDir();
        var dir = Path.GetDirectoryName(tmp)!;
        var orphan1 = Path.Combine(dir, $".{Path.GetFileName(tmp)}.{Guid.NewGuid():N}.tmp");
        var orphan2 = Path.Combine(dir, $".{Path.GetFileName(tmp)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(orphan1, [1, 2, 3]);
        File.WriteAllBytes(orphan2, [4, 5, 6]);
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.Apply(Fixtures.ThirtyPages());

            s.Save(new AppConfig(configDir));

            Assert.False(File.Exists(orphan1));
            Assert.False(File.Exists(orphan2));
        }
        finally { File.Delete(tmp); TryDelete(tmp + ".bak"); TryDelete(orphan1); TryDelete(orphan2); TryDeleteDir(configDir); }
    }

    // ---- Rider (revisão pós-Task 3, endereçado na Task 4): BuildFailureMessage internal -----------

    [Fact] // a mensagem de DataPreservedInTemp é a ÚNICA rota do usuário até os dados sobreviventes —
    // sem o caminho completo do temp NA MENSAGEM, o resgate do C1 vira proteção sem payoff prático.
    public void BuildFailureMessage_DataPreservedInTemp_ContainsTempPath()
    {
        string dest = Path.Combine(Path.GetTempPath(), "dest.pdf");
        string temp = Path.Combine(Path.GetTempPath(), ".dest.pdf.abc123.tmp");
        var ex = new IOException("falha simulada de I/O");

        string msg = DocumentSession.BuildFailureMessage(dest, temp, ex, WriteFailureOutcome.DataPreservedInTemp);

        Assert.Contains(temp, msg);
    }

    [Fact] // Item 3 (revisão final pré-merge) — a mensagem de DataLost NÃO promete um caminho de
    // resgate que não existe (diferente de DataPreservedInTemp acima): honestidade sobre perda total.
    public void BuildFailureMessage_DataLost_DoesNotMentionTempPath()
    {
        string dest = Path.Combine(Path.GetTempPath(), "dest.pdf");
        string temp = Path.Combine(Path.GetTempPath(), ".dest.pdf.abc123.tmp");
        var ex = new IOException("falha simulada de I/O");

        string msg = DocumentSession.BuildFailureMessage(dest, temp, ex, WriteFailureOutcome.DataLost);

        Assert.DoesNotContain(temp, msg);
        Assert.Contains("podem ter sido perdidos", msg);
    }

    // ---- Task 4 (Plano 3a): Undo/Redo ---------------------------------------------------------------

    [Fact] // ApplyEdit = captura o snapshot PRÉ-edição, Apply(novo), SÓ ENTÃO empilha — sem cópia (mesma
    // referência, contrato de Apply). CanUndo vira true; CanRedo continua false (nenhum Undo ainda).
    public void ApplyEdit_PushesPreEditSnapshot_ThenApplies()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.False(s.CanUndo);

            var novo = Fixtures.ThirtyPages();
            s.ApplyEdit(novo);

            Assert.Same(novo, s.Snapshot);
            Assert.True(s.CanUndo);
            Assert.False(s.CanRedo);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // bytes inválidos em ApplyEdit: Apply lança (sessão intacta, mesmo contrato de Apply) E a
    // pilha de desfazer NUNCA é tocada — sem entrada "fantasma" de uma edição que não aconteceu.
    public void ApplyEdit_InvalidBytes_ThrowsAndDoesNotPushToUndoStack()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.Throws<ArgumentException>(() => s.ApplyEdit([0x00, 0x01, 0x02, 0x03]));
            Assert.False(s.CanUndo);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Undo reverte pro snapshot PRÉ-edição (byte-a-byte igual ao aberto) — CanUndo vira false,
    // CanRedo vira true. Undo() chama Apply INTERNAMENTE (Applied dispara, mesmo contrato de Apply),
    // mas SEM re-empilhar (senão desfazer empurraria de volta pro undo — loop).
    public void Undo_RevertsToPreEditSnapshot()
    {
        var tmp = CopyFixtureToTemp();
        var original = File.ReadAllBytes(tmp);
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.ApplyEdit(Fixtures.ThirtyPages());

            s.Undo();

            Assert.Equal(original, s.Snapshot);
            Assert.False(s.CanUndo);
            Assert.True(s.CanRedo);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Redo reaplica o snapshot desfeito — espelho exato de Undo.
    public void Redo_ReappliesUndoneSnapshot()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            var novo = Fixtures.ThirtyPages();
            s.ApplyEdit(novo);
            s.Undo();

            s.Redo();

            Assert.Equal(novo, s.Snapshot);
            Assert.True(s.CanUndo);
            Assert.False(s.CanRedo);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Undo sem nada a desfazer: no-op silencioso — snapshot/estado intactos, sem lançar, sem
    // disparar CanUndoRedoChanged (o método retorna ANTES de tocar em Apply/no evento).
    public void Undo_NothingToUndo_IsNoOp()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            var snapshotBefore = s.Snapshot;
            int raised = 0;
            s.CanUndoRedoChanged += (_, _) => raised++;

            s.Undo();

            Assert.Same(snapshotBefore, s.Snapshot);
            Assert.False(s.CanUndo);
            Assert.False(s.CanRedo);
            Assert.Equal(0, raised);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Redo sem nada a refazer: no-op silencioso — espelho exato do teste acima.
    public void Redo_NothingToRedo_IsNoOp()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            var snapshotBefore = s.Snapshot;
            int raised = 0;
            s.CanUndoRedoChanged += (_, _) => raised++;

            s.Redo();

            Assert.Same(snapshotBefore, s.Snapshot);
            Assert.False(s.CanUndo);
            Assert.False(s.CanRedo);
            Assert.Equal(0, raised);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // ApplyEdit (edição NOVA) limpa qualquer redo pendente — mesmo contrato de SnapshotStack,
    // agora pela porta pública de DocumentSession.
    public void ApplyEdit_ClearsRedoStack()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.ApplyEdit(Fixtures.ThirtyPages());
            s.Undo();
            Assert.True(s.CanRedo);

            s.ApplyEdit(Fixtures.ThirtyPages());

            Assert.False(s.CanRedo);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // A REGRA do brief: editar -> salvar -> desfazer -> IsDirty volta a TRUE (o conteúdo agora
    // difere do que foi SALVO); refazer -> IsDirty volta a false de novo. Nenhuma lógica especial — a
    // MESMA UpdateDirty por hash (Task 3) já cobre isso, porque Undo/Redo chamam Apply internamente.
    public void UndoAfterSave_MakesDirtyTrueAgain_RedoClearsItAgain()
    {
        var tmp = CopyFixtureToTemp();
        var configDir = TempConfigDir();
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.ApplyEdit(Fixtures.ThirtyPages());
            s.Save(new AppConfig(configDir));
            Assert.False(s.IsDirty);

            s.Undo();
            Assert.True(s.IsDirty, "conteúdo pré-edição difere do que foi SALVO -> sujo de novo");

            s.Redo();
            Assert.False(s.IsDirty, "de volta ao conteúdo salvo -> limpo de novo");
        }
        finally { File.Delete(tmp); TryDelete(tmp + ".bak"); TryDeleteDir(configDir); }
    }

    [Fact] // CanUndoRedoChanged dispara na virada de qualquer um dos dois booleanos — mesma disciplina
    // de DirtyChanged (Task 3): não dispara em toda chamada, só quando CanUndo OU CanRedo de fato muda.
    public void CanUndoRedoChanged_RaisesOnFlip_NotOnEveryCall()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            int raised = 0;
            s.CanUndoRedoChanged += (_, _) => raised++;

            s.ApplyEdit(Fixtures.ThirtyPages()); // false,false -> true,false (CanUndo vira)
            Assert.Equal(1, raised);

            s.Undo(); // true,false -> false,true (CanUndo E CanRedo viram, ainda assim 1 disparo só)
            Assert.Equal(2, raised);

            s.Redo(); // false,true -> true,false (os dois viram de volta, 1 disparo)
            Assert.Equal(3, raised);
        }
        finally { File.Delete(tmp); }
    }

    // ---- Rodada 2 (revisão pós-branch): funil único "edição em voo" (TryBeginEdit/EndEdit) -----------

    [Fact] // TryBeginEdit arma o pino (devolve true), 2ª chamada enquanto armado devolve false — o
    // mecanismo PRIMÁRIO de exclusão mútua, nunca lança nessa contenção.
    public void TryBeginEdit_SecondCallWhileArmed_ReturnsFalse()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.False(s.IsEditInFlight);

            Assert.True(s.TryBeginEdit());
            Assert.True(s.IsEditInFlight);
            Assert.False(s.TryBeginEdit()); // já armado — contenção graciosa, sem lançar

            s.EndEdit();
            Assert.False(s.IsEditInFlight);
            Assert.True(s.TryBeginEdit()); // solto de novo -> arma normalmente
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // BELT (Rodada 2): EndEdit sem TryBeginEdit correspondente é pareamento QUEBRADO — lança,
    // ao contrário da contenção graciosa de TryBeginEdit acima (cenário diferente e inequívoco).
    public void EndEdit_WithoutMatchingTryBeginEdit_Throws()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.Throws<InvalidOperationException>(() => s.EndEdit());
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // furo (a) do relatório da revisão: CanUndo/CanRedo compõem !IsEditInFlight — Desfazer NÃO
    // pode ficar disponível enquanto uma edição está em voo, mesmo com pilha de undo não-vazia.
    public void CanUndoCanRedo_ComposeWithEditInFlight()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.ApplyEdit(Fixtures.ThirtyPages());
            s.Undo();
            Assert.True(s.CanRedo); // pilha tem redo pendente

            s.TryBeginEdit();
            Assert.False(s.CanUndo);
            Assert.False(s.CanRedo); // composto: pilha ainda tem redo, mas o pino bloqueia

            s.EndEdit();
            Assert.True(s.CanRedo); // solto -> volta a refletir a pilha crua
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // furo (a), a PROVA DIRETA: Undo()/Redo() (não só CanUndo/CanRedo) recusam graciosamente
    // enquanto o pino está armado — defesa em profundidade, mesmo sem depender de CanExecute nenhum.
    public void UndoRedo_NoOp_WhileEditInFlight()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.ApplyEdit(Fixtures.ThirtyPages());
            var beforeArm = s.Snapshot;

            s.TryBeginEdit();
            s.Undo(); // deveria desfazer, mas o pino bloqueia

            Assert.Same(beforeArm, s.Snapshot); // NADA mudou — Undo foi um no-op de verdade

            s.EndEdit();
            s.Redo(); // pilha nunca foi tocada -> Redo também é no-op (nada foi desfeito antes)
            Assert.Same(beforeArm, s.Snapshot);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // CanUndoRedoChanged dispara quando SÓ o pino muda (mesmo sem nenhum ApplyEdit/Undo/Redo no
    // meio) — RaiseCanUndoRedoChangedIfFlipped precisa ler as propriedades COMPOSTAS, não a pilha crua.
    public void EditInFlightTransition_RaisesCanUndoRedoChanged_WhenComposedValueFlips()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.ApplyEdit(Fixtures.ThirtyPages()); // CanUndo=true agora
            int raised = 0;
            s.CanUndoRedoChanged += (_, _) => raised++;

            s.TryBeginEdit(); // CanUndo true->false (só por causa do pino) -> deve disparar
            Assert.Equal(1, raised);

            s.EndEdit(); // CanUndo false->true de novo -> deve disparar de novo
            Assert.Equal(2, raised);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // EditInFlightChanged dispara em TryBeginEdit (sucesso) e EndEdit — consumido pelas 2 VMs
    // pra reavaliar CanExecute dos comandos mutadores.
    public void EditInFlightChanged_RaisesOnBeginAndEnd()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            int raised = 0;
            s.EditInFlightChanged += (_, _) => raised++;

            Assert.True(s.TryBeginEdit());
            Assert.Equal(1, raised);

            Assert.False(s.TryBeginEdit()); // contenção — NÃO dispara (nada mudou de estado)
            Assert.Equal(1, raised);

            s.EndEdit();
            Assert.Equal(2, raised);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Dispose da sessão descarta a pilha de undo/redo — a pasta de spill correspondente é
    // removida (o contrato de SnapshotStack.Dispose em si já é testado isolado em SnapshotStackTests).
    public void Dispose_DisposesUndoRedoStack_DeletingSpillDirectory()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            var s = DocumentSession.Open(tmp);
            string spillDir = s.UndoRedoSpillDirectory;
            Assert.True(Directory.Exists(spillDir));

            s.Dispose();

            Assert.False(Directory.Exists(spillDir));
        }
        finally { File.Delete(tmp); }
    }

    // ---- Task 1 (Plano 5): teto de bytes no undo (RAM + disco, POLÍTICA sobre o mecanismo já provado
    // acima) — Open/OpenAsync agora aceitam maxRamBytes/maxSpillBytes opcionais, threading pra dentro
    // do SnapshotStack (já provado isolado em SnapshotStackTests — aqui só prova que DocumentSession FIA
    // os parâmetros corretamente e que a notificação "1x por documento" funciona por CIMA do evento
    // mecânico/repetitivo de SnapshotStack.HistoryLimitReached).

    [Fact] // documenta os defaults do brief (256 MB / 2 GB) como um teste, não só um comentário — muda
    // se alguém trocar a constante sem querer.
    public void DefaultCeilings_MatchBriefConstants()
    {
        Assert.Equal(256L * 1024 * 1024, DocumentSession.DefaultMaxUndoRamBytes);
        Assert.Equal(2L * 1024 * 1024 * 1024, DocumentSession.DefaultMaxUndoSpillBytes);
    }

    [Fact] // ceilings pequenos (fixture-a4.pdf real, ~poucos KB — NUNCA um array sintético gigante,
    // ver brief) forçam o spill+descarte através do caminho de produção inteiro (ApplyEdit -> Apply ->
    // SnapshotStack.Push) — prova que Open FIA maxRamBytes/maxSpillBytes até o mecanismo, não só que o
    // mecanismo em si funciona isolado (já provado em SnapshotStackTests).
    public void ApplyEdit_BeyondSpillCap_DiscardsOldest_MakingItUnreachableViaUndo()
    {
        var tmp = CopyFixtureToTemp(); // conteúdo inicial = fixture-a4.pdf
        try
        {
            long unit = Fixtures.A4().LongLength;
            using var s = DocumentSession.Open(tmp, maxRamBytes: unit, maxSpillBytes: unit);

            // 3 ApplyEdit com o MESMO tamanho `unit` (fixture-a4.pdf) -- aritmética exata no relatório:
            // push0: RAM=u (ok) | push1: RAM=2u>u -> espalha a mais antiga -> RAM=u (ok), disco=u (ok)
            // | push2: RAM=2u>u -> espalha -> RAM=u (ok), disco=2u>u -> DESCARTA a mais antiga -> disco=u.
            // Resultado: só 2 dos 3 passos de desfazer sobrevivem (o 1º foi descartado de vez).
            for (int i = 0; i < 3; i++) s.ApplyEdit(Fixtures.A4());

            Assert.True(s.CanUndo);
            s.Undo(); // desempilha a 3ª pré-edição (RAM)
            Assert.True(s.CanUndo);
            s.Undo(); // desempilha a 2ª pré-edição (disco)
            Assert.False(s.CanUndo); // a 1ª foi DESCARTADA -- não sobra um 3º Undo
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // "descarte notifica 1x" (brief) NO NÍVEL DE DOCUMENTO: mesmo com 2 descartes GENUÍNOS
    // (SnapshotStack.HistoryLimitReached dispara 2x, mecânico -- ver SnapshotStackTests), o evento
    // PÚBLICO de DocumentSession só dispara 1x -- o latch "1x por documento" mora AQUI, não na pilha
    // pura. MUTATION-PROVÁVEL: remover o `if (_historyLimitNoticeShown) return;` faz este teste falhar
    // (raised viraria 2) sem quebrar o teste anterior.
    public void ApplyEdit_BeyondSpillCap_RaisesUndoHistoryLimitReached_OnceDespiteMultipleDiscards()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            long unit = Fixtures.A4().LongLength;
            using var s = DocumentSession.Open(tmp, maxRamBytes: unit, maxSpillBytes: unit * 2);
            int raised = 0;
            s.UndoHistoryLimitReached += (_, _) => raised++;

            // 5 pushes de `unit` bytes -> 2 descartes genuínos (aritmética completa no relatório: os
            // descartes acontecem no 4º e no 5º ApplyEdit desta sequência).
            for (int i = 0; i < 5; i++) s.ApplyEdit(Fixtures.A4());

            Assert.Equal(1, raised);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // controle negativo: com os defaults de produção (256 MB / 2 GB), snapshots de fixture (KB)
    // NUNCA disparam o aviso -- nenhuma sessão real de uso normal deveria ver isto na v1.
    public void ApplyEdit_ManyTimes_WithDefaultCeilings_NeverRaisesUndoHistoryLimitReached()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp); // ceilings OMITIDOS -- defaults de produção
            int raised = 0;
            s.UndoHistoryLimitReached += (_, _) => raised++;

            for (int i = 0; i < 25; i++) s.ApplyEdit(Fixtures.A4()); // > profundidade de RAM (20) à toa

            Assert.Equal(0, raised);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // mesma prova pelo caminho ASSÍNCRONO — OpenAsync também FIA maxRamBytes/maxSpillBytes
    // (Task 7, Plano 3a, é o caminho que a produção usa de verdade — ver MainViewModel.OpenPath).
    public async Task OpenAsync_ApplyEdit_BeyondSpillCap_DiscardsOldest()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            long unit = Fixtures.A4().LongLength;
            using var s = await DocumentSession.OpenAsync(tmp, maxRamBytes: unit, maxSpillBytes: unit);

            for (int i = 0; i < 3; i++) s.ApplyEdit(Fixtures.A4()); // mesma aritmética do teste síncrono acima

            s.Undo();
            s.Undo();
            Assert.False(s.CanUndo);
        }
        finally { File.Delete(tmp); }
    }

    // ---- Task 5 (Plano 3a): WriteNewFile ------------------------------------------------------------

    [Fact] // grava bytes NOVOS atomicamente num caminho que ainda não existe, sem exigir sessão nenhuma
    public void WriteNewFile_WritesContentAtomically_ToNewPath()
    {
        var dir = TempConfigDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "copia.pdf");
        try
        {
            DocumentSession.WriteNewFile(path, Fixtures.ThirtyPages());

            Assert.True(File.Exists(path));
            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(path));
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact] // mesma AtomicWrite de SaveAs -> também funciona sobre um caminho JÁ EXISTENTE (File.Replace)
    public void WriteNewFile_ToExistingPath_Overwrites()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            DocumentSession.WriteNewFile(tmp, Fixtures.ThirtyPages());

            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(tmp));
        }
        finally { File.Delete(tmp); }
    }

    // ---- Task 5 (Plano 3a): SweepOrphanUndoSpillDirectories -----------------------------------------

    [Fact] // só remove pastas "undo-*" cujo LastWriteTime é mais antigo que o limiar; as recentes ficam
    public void SweepOrphanUndoSpillDirectories_RemovesOnlyDirsOlderThanThreshold()
    {
        var root = TempConfigDir();
        Directory.CreateDirectory(root);
        var oldDir = Path.Combine(root, "undo-velha");
        var newDir = Path.Combine(root, "undo-nova");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        Directory.SetLastWriteTimeUtc(oldDir, DateTime.UtcNow.AddHours(-48));
        try
        {
            DocumentSession.SweepOrphanUndoSpillDirectories(root, TimeSpan.FromHours(24));

            Assert.False(Directory.Exists(oldDir));
            Assert.True(Directory.Exists(newDir));
        }
        finally { TryDeleteDir(root); }
    }

    [Fact] // raiz que nem existe ainda (app nunca criou nenhuma pasta de undo) -> não lança
    public void SweepOrphanUndoSpillDirectories_RootMissing_DoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mpdf-sweep-nao-existe-{Guid.NewGuid():N}");
        var ex = Record.Exception(() => DocumentSession.SweepOrphanUndoSpillDirectories(root, TimeSpan.FromHours(24)));
        Assert.Null(ex);
    }

    // ---- Task 3 (Plano 4): CommitSigned -------------------------------------------------------------

    [Fact] // BELT (mesmo espírito de EndEdit): chamado FORA do funil (TryBeginEdit nunca armado) lança
    // alto em vez de gravar por baixo de uma "edição" nunca anunciada — contrato do brief.
    public void CommitSigned_WithoutTryBeginEdit_Throws()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.Throws<InvalidOperationException>(() => s.CommitSigned(Fixtures.ThirtyPages()));
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // grava os bytes assinados ATOMICAMENTE no MESMO FilePath (mesma AtomicWrite de Save/SaveAs)
    // e troca Snapshot/Renderer/PageSizes pro documento novo — mesma prova de "documento realmente
    // trocou" usada em Apply_SwapsSnapshotRendererAndPageSizes_ForTheNewDocument (fixture-a4 -> 30 páginas).
    public void CommitSigned_WritesBytesAtomicallyToFilePath_AndSwapsSnapshot()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            Assert.True(s.TryBeginEdit());
            s.CommitSigned(Fixtures.ThirtyPages());

            Assert.Equal(Fixtures.ThirtyPages(), s.Snapshot);
            Assert.Equal(30, s.Renderer.PageCount);
            Assert.Equal(30, s.PageSizes.Count);
            Assert.Equal(Fixtures.ThirtyPages(), File.ReadAllBytes(tmp)); // o ARQUIVO em disco também mudou
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // DECISÃO REGISTRADA: assinar LIMPA o histórico de desfazer/refazer inteiro (desfazer uma
    // assinatura seria juridicamente confuso). Popula as DUAS pilhas antes (ApplyEdit -> Undo deixa algo
    // em CADA uma) e prova que as duas zeram.
    public void CommitSigned_ClearsUndoAndRedoStacks()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.ApplyEdit(Fixtures.ThirtyPages());
            s.Undo(); // undo agora vazio, redo tem 1 entrada
            s.ApplyEdit(Fixtures.ThirtyPages()); // undo tem 1 entrada de novo
            Assert.True(s.CanUndo);

            Assert.True(s.TryBeginEdit());
            s.CommitSigned(Fixtures.ThirtyPages());

            Assert.False(s.CanUndo);
            Assert.False(s.CanRedo);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // dirty=false depois de CommitSigned — mesmo contrato de Save/SaveAs (o arquivo em disco
    // agora bate com o Snapshot corrente).
    public void CommitSigned_SetsDirtyFalse()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.Apply(Fixtures.ThirtyPages()); // suja a sessão ANTES de assinar (Apply não passa pelo funil)
            Assert.True(s.IsDirty);

            Assert.True(s.TryBeginEdit());
            s.CommitSigned(Fixtures.ThirtyPages());

            Assert.False(s.IsDirty);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // Applied dispara (mesmo consumidor de Apply: a VM reconstrói Pages/Thumbnails a partir dele)
    public void CommitSigned_RaisesAppliedEvent()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            int raised = 0;
            s.Applied += (_, _) => raised++;

            Assert.True(s.TryBeginEdit());
            s.CommitSigned(Fixtures.ThirtyPages());

            Assert.Equal(1, raised);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // CanUndoRedoChanged dispara quando limpar as pilhas FLIPA CanUndo/CanRedo (mesma disciplina
    // flip-only de qualquer outro disparo do evento).
    public void CommitSigned_RaisesCanUndoRedoChanged_WhenStacksFlip()
    {
        var tmp = CopyFixtureToTemp();
        try
        {
            using var s = DocumentSession.Open(tmp);
            s.ApplyEdit(Fixtures.ThirtyPages());
            Assert.True(s.CanUndo);

            int raised = 0;
            s.CanUndoRedoChanged += (_, _) => raised++;

            Assert.True(s.TryBeginEdit());
            s.CommitSigned(Fixtures.ThirtyPages());

            Assert.False(s.CanUndo);
            Assert.True(raised >= 1);
        }
        finally { File.Delete(tmp); }
    }

    [Fact] // bytes inválidos (não é um PDF de verdade) -> ArgumentException, sessão E arquivo em disco
    // permanecem INTOCADOS (mesmo contrato de Apply_InvalidBytes_ThrowsAndLeavesSessionUnchanged) —
    // CommitSigned valida ANTES de gravar em disco/mutar qualquer estado.
    public void CommitSigned_InvalidBytes_ThrowsAndLeavesSessionAndFileUnchanged()
    {
        var tmp = CopyFixtureToTemp();
        var originalBytes = File.ReadAllBytes(tmp);
        try
        {
            using var s = DocumentSession.Open(tmp);
            var originalRenderer = s.Renderer;
            var originalSnapshot = s.Snapshot;
            Assert.True(s.TryBeginEdit());

            Assert.Throws<ArgumentException>(() => s.CommitSigned([0x00, 0x01, 0x02, 0x03]));

            Assert.Same(originalRenderer, s.Renderer);
            Assert.Same(originalSnapshot, s.Snapshot);
            Assert.Equal(originalBytes, File.ReadAllBytes(tmp));
            Assert.True(s.IsEditInFlight); // funil continua armado -- chamador ainda deve chamar EndEdit no finally
        }
        finally { File.Delete(tmp); }
    }
}
