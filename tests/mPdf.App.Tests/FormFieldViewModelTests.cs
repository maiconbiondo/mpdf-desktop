using mPdf.App.ViewModels;
using mPdf.Editing;
using Xunit;

namespace mPdf.App.Tests;

/// Task 2 (Plano 3c): wrapper editável em torno de `FormFieldData` (record IMUTÁVEL do contrato neutro
/// de mPdf.Editing) — o painel de Campos precisa de um `EditedValue` MUTÁVEL/observável pra bindar nos
/// editores (TextBox/CheckBox/ComboBox/RadioButtons/ListBox), sem alterar o registro original (que
/// continua sendo a fonte-da-verdade do que veio do PDF, usada por `IsDirty` como comparação).
public class FormFieldViewModelTests
{
    private static FormFieldData TextField(string value = "Fulano de Tal") =>
        new("nome", FormFieldType.Text, value, Array.Empty<string>(), 0, null, IsReadOnly: false);

    private static FormFieldData CheckboxField(string value = "Off") =>
        new("aceito", FormFieldType.Checkbox, value, new[] { "Yes" }, 0, null, IsReadOnly: false);

    [Fact]
    public void Ctor_EditedValueStartsEqualToDataValue()
    {
        var vm = new FormFieldViewModel(TextField());
        Assert.Equal("Fulano de Tal", vm.EditedValue);
        Assert.Equal("nome", vm.Name);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void EditedValueChanged_MarksDirty()
    {
        var vm = new FormFieldViewModel(TextField());
        vm.EditedValue = "Outro Nome";
        Assert.True(vm.IsDirty);
    }

    [Fact] // digitar e depois voltar pro valor ORIGINAL não é mais "alterado" (dicionário de Aplicar não deve incluir)
    public void EditedValueChangedBackToOriginal_IsNotDirty()
    {
        var vm = new FormFieldViewModel(TextField());
        vm.EditedValue = "Outro Nome";
        vm.EditedValue = "Fulano de Tal";
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void IsDirty_RaisesPropertyChanged_WhenEditedValueChanges()
    {
        var vm = new FormFieldViewModel(TextField());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.EditedValue = "Outro";
        Assert.Contains(nameof(FormFieldViewModel.IsDirty), raised);
    }

    [Fact] // Checkbox desmarcado: Options=["Yes"], Value="Off" -> IsCheckboxOn false
    public void IsCheckboxOn_ReflectsCurrentValue_Off()
    {
        var vm = new FormFieldViewModel(CheckboxField("Off"));
        Assert.False(vm.IsCheckboxOn);
    }

    [Fact]
    public void IsCheckboxOn_ReflectsCurrentValue_On()
    {
        var vm = new FormFieldViewModel(CheckboxField("Yes"));
        Assert.True(vm.IsCheckboxOn);
    }

    [Fact] // setter: marcar -> EditedValue vira o export value real (Options[0]), nunca um "Yes" fixo
    public void IsCheckboxOn_SetTrue_SetsEditedValueToFirstOption()
    {
        var vm = new FormFieldViewModel(CheckboxField("Off"));
        vm.IsCheckboxOn = true;
        Assert.Equal("Yes", vm.EditedValue);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void IsCheckboxOn_SetFalse_SetsEditedValueToOff()
    {
        var vm = new FormFieldViewModel(CheckboxField("Yes"));
        vm.IsCheckboxOn = false;
        Assert.Equal("Off", vm.EditedValue);
        Assert.True(vm.IsDirty);
    }

    [Fact] // painel binda IsEnabled do editor nisto (Task 2, brief: "IsReadOnly fields: display value,
    // editor disabled") — mantém a lógica em C# testável em vez de um conversor XAML.
    public void IsEditable_FalseWhenReadOnly()
    {
        var vm = new FormFieldViewModel(TextField() with { IsReadOnly = true });
        Assert.False(vm.IsEditable);
    }

    [Fact]
    public void IsEditable_TrueWhenNotReadOnly()
    {
        var vm = new FormFieldViewModel(TextField());
        Assert.True(vm.IsEditable);
    }

    [Fact] // Important 1 (revisão, efeito colateral necessário): depois que ApplyFormValues manda um
    // valor pro documento com sucesso, o campo deixa de ser "edição PENDENTE" — MarkApplied atualiza o
    // baseline (Data.Value) pro valor que acabou de ser enviado, desligando IsDirty. Sem isto, a
    // PRESERVAÇÃO de dirty (RefreshFormFieldsAsync) confundiria "valor recém-aplicado" com "edição
    // alheia não-relacionada que precisa sobreviver" — um Undo logo depois reveria o valor JÁ desfeito.
    public void MarkApplied_UpdatesBaselineToCurrentEditedValue_TurnsOffDirty()
    {
        var vm = new FormFieldViewModel(TextField(value: "Original"));
        vm.EditedValue = "Novo Valor";
        Assert.True(vm.IsDirty);

        vm.MarkApplied();

        Assert.False(vm.IsDirty);
        Assert.Equal("Novo Valor", vm.Data.Value);
        Assert.Equal("Novo Valor", vm.EditedValue);
    }

    [Fact]
    public void Data_ExposesOriginalRecordUnchanged_AfterEdits()
    {
        var original = TextField();
        var vm = new FormFieldViewModel(original);
        vm.EditedValue = "Mudou";
        Assert.Same(original, vm.Data);
        Assert.Equal("Fulano de Tal", vm.Data.Value); // registro original nunca muda
    }
}
