using System.IO;
using System.Linq;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Rendering;
using Xunit;

namespace mPdf.App.Tests;

/// Task 3 (Plano 3c) — testes de INTEGRAÇÃO ponta a ponta do fluxo de formulário: motor REAL
/// (`PdfEditorFactory.Create()`, NUNCA `FakePdfEditor`), `DocumentSession` REAL, arquivos REAIS em disco
/// (cópia da fixture pra um diretório temporário — mesmo padrão de `DocumentSessionTests.
/// CopyFixtureToTemp`/`MainViewModelTests.CopyFixtureToTemp`, nunca escreve em cima do fixture
/// versionado no repo). Prova o CONTRATO fim a fim que nenhum teste de VM isolado (com `FakePdfEditor`)
/// alcança: bytes REALMENTE gravados em disco, reabertos por uma leitura NOVA, lidos/renderizados pelo
/// motor de verdade.
///
/// Item (c) do brief ("fill+flatten roundtrip leaves annotations intact") — OMITIDO de propósito:
/// `fixture-formulario.pdf` não tem NENHUMA anotação de marcação (só campos de formulário e seus
/// widgets), então não há nada pra provar "intacto" aqui; cobrir isso exigiria uma fixture nova
/// (formulário + anotação na mesma página), fora do escopo desta task.
public class FormFlattenIntegrationTests
{
    private static string CopyFormFixtureToTemp()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-flatten-{Guid.NewGuid():N}.pdf");
        File.Copy(Path.Combine(Fixtures.Root, "fixture-formulario.pdf"), tmp);
        return tmp;
    }

    private static void TryDeleteWithBak(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* melhor esforço */ }
        try { if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); } catch { /* melhor esforço */ }
    }

    [Fact] // (a) do brief: preencher -> salvar -> reabrir -> valores PERSISTEM — verificado com
    // ReadFormFields sobre os bytes REAIS gravados em disco (não sobre Session.Snapshot em RAM).
    public async Task Fill_Save_Reopen_ValuesPersist()
    {
        var tmp = CopyFormFixtureToTemp();
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-flatten-cfg-{Guid.NewGuid():N}");
        try
        {
            var editor = PdfEditorFactory.Create();
            {
                using var session = DocumentSession.Open(tmp);
                using var doc = new DocumentViewModel(session, editor: editor,
                    notifyError: _ => { }, notifyInfo: _ => { }, confirmFlatten: new FakeConfirmFlattenService(true));

                await doc.RefreshFormFieldsAsync();
                var nome = doc.FormFieldEditors.Single(f => f.Name == "nome");
                nome.EditedValue = "Ciclano Testado";

                await doc.ApplyFormValuesCommand.ExecuteAsync(null);
                Assert.True(session.IsDirty);

                session.Save(new AppConfig(configDir));
            }

            // Leitura NOVA, direto do arquivo em disco (não do Session.Snapshot em RAM) — prova que o
            // Save de fato persistiu, não só que a sessão em memória mudou.
            var reopened = editor.ReadFormFields(File.ReadAllBytes(tmp));
            Assert.Equal("Ciclano Testado", reopened.Single(f => f.Name == "nome").Value);
        }
        finally
        {
            TryDeleteWithBak(tmp);
            try { Directory.Delete(configDir, recursive: true); } catch { /* melhor esforço */ }
        }
    }

    [Fact] // (b) do brief: preencher -> achatar -> salvar -> reabrir -> SEM campos, aparência
    // PRESERVADA (prova de renderização em px — exemplar: PdfEditorTests.
    // SetFormFields_Checkbox_ValueAppearsInRender, MESMA região do widget "aceito": 50,600-70,620 pt,
    // escala 1.0, ver task-1-report.md).
    public async Task Fill_Flatten_Save_Reopen_NoFields_RendersFilledValue()
    {
        var tmp = CopyFormFixtureToTemp();
        var configDir = Path.Combine(Path.GetTempPath(), $"mpdf-flatten-cfg-{Guid.NewGuid():N}");
        try
        {
            var editor = PdfEditorFactory.Create();

            // Baseline: fixture ORIGINAL (nunca preenchida), MESMA região — prova que o que for pintado
            // depois não é um artefato pré-existente da fixture (checkbox "aceito" começa Off).
            int paintedBefore;
            using (var baselineRenderer = new PdfDocumentRenderer(Fixtures.Formulario()))
                paintedBefore = CountPaintedPixelsInRegion(baselineRenderer.RenderPage(0, 1.0), 50, 70, 600, 620);

            {
                using var session = DocumentSession.Open(tmp);
                using var doc = new DocumentViewModel(session, editor: editor,
                    notifyError: _ => { }, notifyInfo: _ => { }, confirmFlatten: new FakeConfirmFlattenService(true));

                await doc.RefreshFormFieldsAsync();
                var aceito = doc.FormFieldEditors.Single(f => f.Name == "aceito");
                aceito.EditedValue = "Yes";
                await doc.ApplyFormValuesCommand.ExecuteAsync(null);
                await doc.RefreshFormFieldsAsync();
                Assert.True(doc.HasFormFields); // preenchido, ainda NÃO achatado

                await doc.FlattenFormCommand.ExecuteAsync(null);
                await doc.RefreshFormFieldsAsync();
                Assert.False(doc.HasFormFields); // achatado — painel volta pro estado sem-formulário

                session.Save(new AppConfig(configDir));
            }

            var savedBytes = File.ReadAllBytes(tmp);
            Assert.Empty(editor.ReadFormFields(savedBytes)); // reaberto do disco: sem campos NENHUM

            int paintedAfter;
            using (var reopenedRenderer = new PdfDocumentRenderer(savedBytes))
                paintedAfter = CountPaintedPixelsInRegion(reopenedRenderer.RenderPage(0, 1.0), 50, 70, 600, 620);

            // Medido ao vivo (ver task-3-report.md): antes=0 pixels pintados, depois=83 — mesmo número
            // exato de PdfEditorTests.SetFormFields_Checkbox_ValueAppearsInRender (Task 1), esperado: o
            // achatamento "imprime" a MESMA appearance stream que SetFormFields já regenera, não muda o
            // desenho. Limiar 20 folgado abaixo do valor real, ainda longe o bastante de 0 pra não
            // confundir com ruído de antialiasing.
            Assert.True(paintedBefore < 5, $"checkbox já aparecia pintado ANTES de preencher: {paintedBefore} pixels");
            Assert.True(paintedAfter > 20,
                $"checkbox marcado não sobreviveu ao achatar+salvar+reabrir: só {paintedAfter} pixels pintados na região (antes: {paintedBefore})");
        }
        finally
        {
            TryDeleteWithBak(tmp);
            try { Directory.Delete(configDir, recursive: true); } catch { /* melhor esforço */ }
        }
    }

    // Mesma lógica de PdfEditorTests.CountPaintedPixelsInRegion (mPdf.Editing.Tests) — não
    // compartilhável entre assemblies de teste distintos, reimplementada aqui idêntica.
    private static int CountPaintedPixelsInRegion(RenderedPage page, int xMin, int xMax, int yMinPt, int yMaxPt)
    {
        int painted = 0;
        int heightPx = page.HeightPx;
        for (int y = heightPx - yMaxPt; y < heightPx - yMinPt; y++)
            for (int x = xMin; x < xMax; x++)
            {
                int i = (y * page.WidthPx + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) painted++;
            }
        return painted;
    }
}
