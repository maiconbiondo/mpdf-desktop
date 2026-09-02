using System.IO;
using System.Linq;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Editing;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

/// Task 6 (Plano 4): roteamento de `ApplyFormValuesCommand` entre o motor normal (`mPdf.Editing`, doc
/// não assinado) e o motor incremental (`mPdf.Signing`, doc assinado) + o gate `CanFillForms`/aviso do
/// painel + a verificação central de design "o preenchimento passa pelo MESMO `Session.ApplyEdit` de
/// qualquer edição — undo restaura os bytes pré-preenchimento com a assinatura íntegra". As 2 primeiras
/// seções usam `FakeSigningEngine`/`FakePdfEditor` (roteamento é uma questão de "qual delegate foi
/// chamado", não precisa de iText de verdade); a última seção usa o motor REAL (a única forma de provar
/// que a assinatura sobrevive ao ciclo completo Undo/Redo).
public class FormFillIncrementalRoutingTests : IDisposable
{
    private readonly List<string> _tempFilesToDelete = [];

    public void Dispose()
    {
        foreach (var f in _tempFilesToDelete) { try { if (File.Exists(f)) File.Delete(f); } catch { /* melhor esforço */ } }
    }

    private static FormFieldData TextField(string name = "nome", string value = "Fulano de Tal") =>
        new(name, FormFieldType.Text, value, Array.Empty<string>(), 0, null, IsReadOnly: false);

    private static (DocumentViewModel doc, FakePdfEditor editor, FakeSigningEngine engine, List<string> errors, List<string> infos)
        BuildForRouting()
    {
        var editor = new FakePdfEditor();
        var engine = new FakeSigningEngine();
        var errors = new List<string>();
        var infos = new List<string>();
        var doc = new DocumentViewModel(
            DocumentSession.Open(Path.Combine(Fixtures.Root, "fixture-a4.pdf")),
            editor: editor,
            signingEngine: engine,
            notifyError: errors.Add,
            notifyInfo: infos.Add);
        return (doc, editor, engine, errors, infos);
    }

    // ---- Roteamento: qual motor é chamado --------------------------------------------------------

    [Fact]
    public async Task ApplyFormValues_UnsignedDocument_CallsEditorSetFormFields_NeverSigningEngine()
    {
        var (doc, editor, engine, _, _) = BuildForRouting();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        d.FormFieldEditors[0].EditedValue = "Digitado";

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.Equal(1, editor.SetFormFieldsCallCount);
        Assert.Equal(0, engine.SetFormFieldsIncrementalCallCount);
        Assert.Equal("Digitado", editor.LastSetFormFieldsValues!["nome"]);
    }

    [Fact]
    public async Task ApplyFormValues_SignedDocumentAllowed_CallsSigningEngineIncremental_NeverEditor()
    {
        var (doc, editor, engine, _, _) = BuildForRouting();
        using var d = doc;
        d.IsSignedDocument = true;
        d.SignedFillPermission = FillPermission.Allowed;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        d.FormFieldEditors[0].EditedValue = "Preenchido Apos Assinar";

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.SetFormFieldsIncrementalCallCount);
        Assert.Equal(0, editor.SetFormFieldsCallCount);
        Assert.Equal("Preenchido Apos Assinar", engine.LastSetFormFieldsIncrementalValues!["nome"]);
    }

    [Fact] // resultado do motor incremental ENTRA na sessão pelo MESMO ApplyEdit (undo/redo funcionam
    // de graça) — prova rápida com fake (a prova com assinatura de VERDADE vem na seção de integração).
    public async Task ApplyFormValues_SignedDocumentAllowed_ResultEntersSessionViaApplyEdit_UndoRestoresPrevious()
    {
        var (doc, _, engine, _, _) = BuildForRouting();
        using var d = doc;
        d.IsSignedDocument = true;
        d.SignedFillPermission = FillPermission.Allowed;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        d.FormFieldEditors[0].EditedValue = "Novo Valor";
        var before = d.Session.Snapshot;
        engine.SetFormFieldsIncrementalResult = Fixtures.ThirtyPages(); // "bytes-marcador" bem distintos

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.NotSame(before, d.Session.Snapshot);
        Assert.Equal(30, d.Session.PageSizes.Count);
        Assert.True(d.Session.CanUndo);

        d.UndoCommand.Execute(null);

        Assert.Same(before, d.Session.Snapshot); // desfazer restaura EXATAMENTE o buffer pré-preenchimento
    }

    [Fact] // ThrowOnSetFormFieldsIncremental (mPdf.Signing.PdfSigningException) — mesmo canal de erro
    // que ApplyFormValues já trata pro motor normal (PdfEditingException), agora pro motor incremental.
    public async Task ApplyFormValues_SigningEngineThrowsPdfSigningException_NotifiesErrorAndDoesNotApply()
    {
        var (doc, _, engine, errors, _) = BuildForRouting();
        using var d = doc;
        d.IsSignedDocument = true;
        d.SignedFillPermission = FillPermission.Allowed;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        d.FormFieldEditors[0].EditedValue = "Novo Valor";
        engine.ThrowOnSetFormFieldsIncremental = new PdfSigningException("Documento certificado não permite alterações.");
        var before = d.Session.Snapshot;

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        Assert.Same(before, d.Session.Snapshot); // nada aplicado
        Assert.Single(errors);
        Assert.Contains("não permite alterações", errors[0]);
        Assert.False(d.Session.IsEditInFlight); // pino solto mesmo na recusa
    }

    // ---- CanFillForms / aviso do painel --------------------------------------------------------

    [Fact]
    public void CanFillForms_UnsignedDocument_TrueByDefault()
    {
        var (doc, _, _, _, _) = BuildForRouting();
        using var d = doc;

        Assert.True(d.CanFillForms);
        Assert.False(d.ShowSignedFormFillNotice);
    }

    [Fact]
    public void CanFillForms_SignedAllowed_True_AndNoticeShown()
    {
        var (doc, _, _, _, _) = BuildForRouting();
        using var d = doc;

        d.IsSignedDocument = true;
        d.SignedFillPermission = FillPermission.Allowed;

        Assert.True(d.CanFillForms);
        Assert.True(d.ShowSignedFormFillNotice);
    }

    [Fact] // M2 (revisão): o aviso de "preenchimento liberado" some, mas o de "DocMDP proibindo" (a
    // razão específica, ver ShowDocMdpDeniedNotice abaixo) aparece — nunca os 2 juntos.
    public void CanFillForms_SignedDeniedByDocMdp_False_ShowsDocMdpDeniedNotice_NotFillNotice()
    {
        var (doc, _, _, _, _) = BuildForRouting();
        using var d = doc;

        d.IsSignedDocument = true;
        d.SignedFillPermission = FillPermission.DeniedByDocMdp;

        Assert.False(d.CanFillForms);
        Assert.False(d.ShowSignedFormFillNotice);
        Assert.True(d.ShowDocMdpDeniedNotice);
    }

    [Fact] // M2: os 2 avisos são MUTUAMENTE EXCLUSIVOS — nunca os 2 true ao mesmo tempo, em nenhum
    // dos 3 estados relevantes (não assinado, assinado-permitido, assinado-negado).
    public void ShowDocMdpDeniedNotice_MutuallyExclusiveWithFillNotice_AcrossStates()
    {
        foreach (var (isSigned, permission, expectFillNotice, expectDeniedNotice) in new[]
        {
            (false, FillPermission.NotSigned, false, false),
            (true, FillPermission.Allowed, true, false),
            (true, FillPermission.DeniedByDocMdp, false, true),
        })
        {
            var (doc, _, _, _, _) = BuildForRouting();
            using var d = doc;
            d.IsSignedDocument = isSigned;
            d.SignedFillPermission = permission;

            Assert.Equal(expectFillNotice, d.ShowSignedFormFillNotice);
            Assert.Equal(expectDeniedNotice, d.ShowDocMdpDeniedNotice);
            Assert.False(d.ShowSignedFormFillNotice && d.ShowDocMdpDeniedNotice); // nunca os 2 juntos
        }
    }

    [Fact] // documento certificado sem permissão continua "somente leitura" como hoje — CanApplyFormValues
    // reflete o gate composto (CanFillForms && HasFormFields).
    public void CanApplyFormValues_SignedDeniedByDocMdp_False()
    {
        var (doc, _, _, _, _) = BuildForRouting();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });

        d.IsSignedDocument = true;
        d.SignedFillPermission = FillPermission.DeniedByDocMdp;

        Assert.False(d.ApplyFormValuesCommand.CanExecute(null));
    }

    [Fact]
    public void CanApplyFormValues_SignedAllowed_True()
    {
        var (doc, _, _, _, _) = BuildForRouting();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });

        d.IsSignedDocument = true;
        d.SignedFillPermission = FillPermission.Allowed;

        Assert.True(d.ApplyFormValuesCommand.CanExecute(null));
    }

    [Fact] // XFA vence a permissão DocMDP — nenhum documento XFA é preenchível, assinado ou não (mesmo
    // raciocínio de CanEdit).
    public void CanFillForms_XfaForm_FalseEvenWhenSignedFillPermissionAllowed()
    {
        var (doc, _, _, _, _) = BuildForRouting();
        using var d = doc;
        d.IsSignedDocument = true;
        d.SignedFillPermission = FillPermission.Allowed;
        Assert.True(d.CanFillForms); // sanity

        d.IsXfaForm = true;

        Assert.False(d.CanFillForms);
    }

    [Fact] // achatar continua gated por CanEdit (não CanFillForms) — a exceção do Plano 4 é só pro
    // preenchimento, nunca pro achatamento de um documento assinado.
    public void CanFlattenForm_SignedAllowed_StaysFalse()
    {
        var (doc, _, _, _, _) = BuildForRouting();
        using var d = doc;
        d.SeedFormFieldsCache(false, new[] { TextField() });
        d.IsSignedDocument = true;
        d.SignedFillPermission = FillPermission.Allowed;

        Assert.True(d.ApplyFormValuesCommand.CanExecute(null)); // preencher: liberado
        Assert.False(d.FlattenFormCommand.CanExecute(null));    // achatar: continua bloqueado
    }

    // ---- Integração com motor REAL: a prova central de design (ApplyEdit/undo/redo + assinatura) ---

    private string WriteToTemp(byte[] bytes, string prefix)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mpdf-{prefix}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(tmp, bytes);
        _tempFilesToDelete.Add(tmp);
        return tmp;
    }

    [Fact] // A VERIFICAÇÃO CENTRAL DE DESIGN (brief): "aplicar um preenchimento incremental através de
    // Session.ApplyEdit guarda o snapshot PRÉ-preenchimento pro undo; undo restaura os bytes pré-fill
    // (assinatura AINDA intacta ali — nunca precisou deixar de estar); redo reaplica." Motor REAL
    // (SigningEngineFactory.Create()) — nenhum fake nesta seção, é a única forma de provar que a
    // assinatura sobrevive ao ciclo completo. Fixture discipline: assina EM MEMÓRIA (nunca sobrescreve
    // o arquivo compartilhado) e grava o resultado num arquivo TEMPORÁRIO próprio antes de abrir a sessão.
    public async Task ApplyFormValues_SignedDocument_UndoRestoresPreFillBytes_SignatureValidOnBothSides()
    {
        using var cert = SignCommandTests.CreateEphemeralRsaCertificate();
        var signingEngine = SigningEngineFactory.Create();
        var signed = signingEngine.Sign(new SignRequest(
            Fixtures.Formulario(), cert, null, null, null, DocMdpLevel.FormsAndSignatures));

        var tmp = WriteToTemp(signed, "form-signed-undo");
        using var session = DocumentSession.Open(tmp);
        using var d = new DocumentViewModel(session, notifyError: _ => { }, notifyInfo: _ => { })
        {
            IsSignedDocument = true,
            SignedFillPermission = FillPermission.Allowed,
        };

        await d.RefreshFormFieldsAsync();
        var nome = d.FormFieldEditors.Single(f => f.Name == "nome");
        Assert.Equal("Fulano de Tal", nome.EditedValue); // sanity: valor original da fixture
        nome.EditedValue = "Preenchido Depois de Assinar";

        var preFillSnapshot = d.Session.Snapshot;
        var preFillInfo = Assert.Single(signingEngine.ReadSignatures(preFillSnapshot));
        Assert.True(preFillInfo.IntegrityValid); // sanity: já íntegra antes de preencher

        await d.ApplyFormValuesCommand.ExecuteAsync(null);

        // ---- pós-preenchimento: valor mudou, assinatura CONTINUA íntegra (a prova central do engine,
        // reafirmada aqui através da VM/Session real, não só do motor isolado) ----
        Assert.NotSame(preFillSnapshot, d.Session.Snapshot);
        Assert.True(d.Session.IsDirty);
        var postFillInfo = Assert.Single(signingEngine.ReadSignatures(d.Session.Snapshot));
        Assert.True(postFillInfo.IntegrityValid, "assinatura deveria continuar íntegra após o preenchimento, mesmo através da VM");
        Assert.Equal("Preenchido Depois de Assinar",
            PdfEditorFactory.Create().ReadFormFields(d.Session.Snapshot).Single(f => f.Name == "nome").Value);

        // ---- UNDO: restaura os bytes PRÉ-preenchimento por REFERÊNCIA — Session.ApplyEdit nunca
        // reescreve o PDF, só troca qual snapshot está "current"; o snapshot pré-fill retido na pilha de
        // desfazer é literalmente o MESMO array que continha a assinatura intacta ANTES de preencher. ----
        Assert.True(d.Session.CanUndo);
        d.UndoCommand.Execute(null);

        Assert.Same(preFillSnapshot, d.Session.Snapshot);
        var afterUndoInfo = Assert.Single(signingEngine.ReadSignatures(d.Session.Snapshot));
        Assert.True(afterUndoInfo.IntegrityValid, "assinatura deveria continuar íntegra depois do UNDO");
        Assert.Equal("Fulano de Tal",
            PdfEditorFactory.Create().ReadFormFields(d.Session.Snapshot).Single(f => f.Name == "nome").Value);

        // ---- REDO: reaplica o preenchimento — assinatura continua íntegra dos DOIS lados do ciclo ----
        Assert.True(d.Session.CanRedo);
        d.RedoCommand.Execute(null);

        var afterRedoInfo = Assert.Single(signingEngine.ReadSignatures(d.Session.Snapshot));
        Assert.True(afterRedoInfo.IntegrityValid, "assinatura deveria continuar íntegra depois do REDO");
        Assert.Equal("Preenchido Depois de Assinar",
            PdfEditorFactory.Create().ReadFormFields(d.Session.Snapshot).Single(f => f.Name == "nome").Value);
    }
}
