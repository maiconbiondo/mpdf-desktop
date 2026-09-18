using CommunityToolkit.Mvvm.ComponentModel;
using mPdf.Editing;

namespace mPdf.App.ViewModels;

/// Wrapper EDITÁVEL em torno de `FormFieldData` (Task 2, Plano 3c) — `FormFieldData` é um `record`
/// imutável do contrato neutro de `mPdf.Editing` (fonte-da-verdade do que veio do PDF); o painel de
/// Campos precisa de um valor MUTÁVEL/observável pra bindar nos editores (TextBox/CheckBox/ComboBox/
/// RadioButtons/ListBox) sem tocar no registro original — `Data` continua intocado, usado só como base
/// de comparação por `IsDirty`. 1 instância por campo, RECRIADA (nunca reaproveitada por identidade) a
/// cada refresh do cache (`DocumentViewModel.RefreshFormFieldsAsync`/`SeedFormFieldsCache`) — mesmo
/// espírito de `AnnotationsByPage` ser uma lista nova a cada refresh, não uma coleção mutada in-place.
public sealed partial class FormFieldViewModel : ObservableObject
{
    /// Registro original — fonte-da-verdade do que veio da ÚLTIMA leitura (ou do último `MarkApplied`,
    /// ver abaixo). `IsDirty` compara `EditedValue` contra `Data.Value`. `private set` (não `init`/
    /// somente-construtor): `MarkApplied` precisa trocar por uma cópia (`with`) com `Value` atualizado —
    /// nunca MUTA o record em si (records continuam imutáveis; troca-se a REFERÊNCIA).
    public FormFieldData Data { get; private set; }

    public string Name => Data.Name;
    public FormFieldType Type => Data.Type;
    public IReadOnlyList<string> Options => Data.Options;
    public bool IsReadOnly => Data.IsReadOnly;

    /// Painel binda `IsEnabled` do editor nisto (brief: "IsReadOnly fields: display value, editor
    /// disabled") — o gate de documento ASSINADO/XFA vive um nível acima (CanEdit da
    /// DocumentViewModel, cascateado via IsEnabled do container pai); este é só o gate POR CAMPO.
    public bool IsEditable => !IsReadOnly;

    /// Valor CORRENTE no editor — inicializado com `Data.Value`, mutado pelos controles de edição
    /// (TextBox.Text/ComboBox.SelectedItem/RadioButton via `IsCheckboxOn`/seleção de rádio). `null` só
    /// no caso residual de `Data.Value` já ser `null` (não deveria ocorrer pelo contrato de
    /// `FormFieldData.Value`, que documenta "nunca null" pra Checkbox/Radio/Combo/Text — mas o TIPO é
    /// `string?`, então este wrapper segue o mesmo tipo em vez de assumir).
    [ObservableProperty] private string? editedValue;

    /// `true` quando `EditedValue` difere do valor ORIGINAL (`Data.Value`) — inclui o caso de digitar e
    /// voltar pro valor original (não fica "preso" dirty). Consumido por
    /// `DocumentViewModel.ApplyFormValues` pra montar o dicionário "só os ALTERADOS" (brief).
    public bool IsDirty => EditedValue != Data.Value;

    partial void OnEditedValueChanged(string? value)
    {
        OnPropertyChanged(nameof(IsDirty));
        // Checkbox (brief: editor tipo CheckBox) — IsCheckboxOn é DERIVADO de EditedValue; qualquer
        // troca de EditedValue (inclusive via IsCheckboxOn.set abaixo, que passa por aqui de qualquer
        // forma) precisa notificar de volta pro binding two-way do CheckBox ficar em sincronia.
        OnPropertyChanged(nameof(IsCheckboxOn));
    }

    /// Projeção booleana de `EditedValue` pra `FormFieldType.Checkbox` (brief: editor tipo CheckBox) —
    /// o nome do estado "ligado" é `Options[0]` (nunca uma constante fixa "Yes", ver XML doc de
    /// `FormFieldData.Options`/task-1-report.md: "o nome real, nunca assume Yes"); fallback pra "Yes"
    /// só no caso residual de `Options` vir vazio (não deveria acontecer pelo contrato — `ReadFormFields`
    /// sempre popula ao menos 1 opção pra Checkbox). Setter grava o export value REAL quando marcado,
    /// "Off" quando desmarcado — mesma convenção de `SetFormFields`/o próprio iText (Off = ausência de
    /// marcação, nunca uma opção "marcável" em si).
    public bool IsCheckboxOn
    {
        get => EditedValue == (Options.Count > 0 ? Options[0] : "Yes");
        set => EditedValue = value ? (Options.Count > 0 ? Options[0] : "Yes") : "Off";
    }

    public FormFieldViewModel(FormFieldData data)
    {
        Data = data;
        editedValue = data.Value;
    }

    /// Chamado por `DocumentViewModel.ApplyFormValues` (Important 1, revisão) IMEDIATAMENTE depois que
    /// `TryApplyEdit` confirma sucesso — ANTES de qualquer refresh (próprio ou alheio) rodar. Atualiza o
    /// baseline (`Data.Value`) pro valor CORRENTE de `EditedValue`, desligando `IsDirty`: o valor que
    /// acabou de ser mandado pro documento não é mais uma "edição pendente" — sem isto, a PRESERVAÇÃO de
    /// dirty em `RefreshFormFieldsAsync` confundiria "valor recém-aplicado" (que já está no documento)
    /// com "edição alheia não-relacionada que precisa sobreviver a um refresh" — um `Session.Undo()`
    /// logo em seguida reveria silenciosamente o valor JÁ desfeito de volta pro editor.
    internal void MarkApplied()
    {
        Data = Data with { Value = EditedValue };
        OnPropertyChanged(nameof(IsDirty)); // IsCheckboxOn não muda aqui — depende de EditedValue/Options, não de Data.Value
    }
}
