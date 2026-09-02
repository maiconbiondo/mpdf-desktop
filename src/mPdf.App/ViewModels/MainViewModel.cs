using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mPdf.App.Services;
using mPdf.Documents;
// Task 5 (Plano 3a): 1º uso de mPdf.Editing dentro de src/mPdf.App — só o CONTRATO neutro
// (IPdfEditor/PdfEditorFactory/PdfSignedDocumentException), nunca um tipo iText (guardado por
// AgplGuardTests + PrivateAssets=compile em mPdf.Editing.csproj).
using mPdf.Editing;
// Task 5 (Plano 4): "Assinar em lote" — 1ª referência de MainViewModel a mPdf.Signing (o contrato
// neutro já usado por DocumentViewModel desde a Task 3, mesmo módulo, nenhuma referência de projeto
// nova — mPdf.App.csproj já referencia mPdf.Signing).
using mPdf.Signing;

namespace mPdf.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IFileDialogService _dialogs;
    private readonly RecentFilesStore _recent;
    // AppConfig (Task 3, Plano 3a): governa DocumentSession.Save (CriarBackup) — mesmo padrão de
    // injeção de RecentFilesStore (diretório configurável, testes nunca tocam %AppData% real).
    private readonly AppConfig _config;
    // Notificação de erro extraída num delegate injetável (Task 7): o único jeito de exercitar o
    // caminho "recente que falha ao abrir" num teste headless sem de fato abrir um MessageBox real
    // (que travaria a sessão de teste esperando um clique que nunca vem). Produção usa o default
    // (DefaultNotifyError, MessageBox de verdade); testes injetam um Action<string> que só captura
    // a mensagem. Escolha registrada: menor mudança possível — não virou uma interface/serviço
    // completo (IMessageService) porque nada mais no VM precisa notificar o usuário ainda.
    private readonly Action<string> _notifyError;
    // Prompt de fechar sujo (Task 3, Plano 3a): mesma disciplina de injeção de _notifyError —
    // produção usa MessageBoxConfirmCloseService (MessageBox de verdade), testes injetam um fake que
    // devolve uma CloseConfirmation fixa, sem travar esperando clique.
    private readonly IConfirmCloseService _confirmClose;
    // Prompt de texto de nota adesiva/caixa de texto (Task 7, Plano 3a): mesma disciplina — propagado
    // pra cada DocumentViewModel que este VM abre (1 janelinha de diálogo, 1 canal, mesmo espírito de
    // _config/_notifyError propagados em OpenPath abaixo).
    private readonly IAnnotationTextDialogService _annotationDialog;
    // Galeria de carimbos de imagem (Task 9, Plano 3a): mesma disciplina de injeção de RecentFilesStore
    // — diretório configurável, testes nunca tocam %AppData%\mPDF\carimbos real. Vive AQUI (não em
    // DocumentViewModel) porque é um recurso GLOBAL do app (compartilhado entre abas), mesmo espírito
    // de RecentFilesStore/AppConfig.
    private readonly StampGallery _stampGallery;
    // Canal de SUCESSO (Task 4, Plano 3b) — mesma disciplina de _notifyError, mas pra confirmações
    // (ex.: "N arquivos criados em X"), não falhas. Também propagado pra DocumentViewModel/
    // OrganizerViewModel (ver OpenPath abaixo) — Extrair vive no organizador, precisa do MESMO canal.
    private readonly Action<string> _notifyInfo;
    // Diálogo "Juntar documentos" (Task 4, Plano 3b) — mesma disciplina de injeção de
    // IConfirmCloseService/IAnnotationTextDialogService: produção abre uma janela WPF real
    // (Views.MergeFilesDialog), testes injetam um fake com uma lista fixa de caminhos.
    private readonly IMergeDialogService _mergeDialog;
    // Diálogo "Dividir documento" (Task 4, Plano 3b) — mesma disciplina, produção abre Views.SplitDialog.
    private readonly ISplitDialogService _splitDialog;
    // Diálogo "Assinar em lote" (Task 5, Plano 4) — mesma disciplina de injeção via UiPrompts.
    private readonly IBatchSignDialogService _batchSignDialog;
    // Catálogo de certificados (Task 5, Plano 4) — mesmo padrão de DocumentViewModel._listSigningCertificates:
    // NÃO passa pela seam UiPrompts (enumerar o repositório do Windows é read-only, sem PIN/senha, não
    // mostra UI nenhuma — ver doc XML de UiPrompts/decisão registrada na Task 3).
    private readonly Func<IReadOnlyList<SigningCertificateInfo>> _listSigningCertificates;
    // Diálogo "Sobre" (Task 2, Plano 11) — mesma disciplina de injeção via UiPrompts.
    private readonly ISobreDialogService _sobreDialog;
    // Diálogo "Configurações" (Task 2, Plano 17) — mesma disciplina de injeção via UiPrompts.
    private readonly IConfiguracoesDialogService _configuracoesDialog;
    // Injetável SÓ para Merge/Split (revisão pós-Task 4, achado "Important"): as demais chamadas a
    // IPdfEditor deste VM (EditCopy) continuam usando `PdfEditorFactory.Create()` INLINE (precedente
    // original, não mexido aqui) — este campo existe especificamente para permitir testar o catch de
    // `ArgumentException` de Merge/Split com um `FakePdfEditor.ThrowOnMergeDocuments`/
    // `ThrowOnSplitByRanges`, sem precisar forçar o motor REAL a lançar (caminho hoje inalcançável em
    // uso normal — ver comentário em Merge/Split abaixo).
    private readonly IPdfEditor _editor;

    // Reentrância entre comandos (revisão pós-Task 7): OpenFileCommand e OpenRecentCommand podem
    // disparar pro MESMO caminho dentro da janela assíncrona de OpenAsync (ex.: usuário clica Abrir
    // e clica de novo no mesmo item de Recentes antes da 1ª abertura terminar) — o dedupe por
    // Documents só enxerga uma aba DEPOIS que ela existe; sem guarda, a 2ª chamada corre em paralelo
    // com a 1ª e as duas terminam adicionando uma aba cada. Guarda por CAMINHO em voo, não um lock
    // global: abrir dois arquivos DIFERENTES ao mesmo tempo continua permitido.
    private readonly HashSet<string> _opening = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    // Task 8: PrintCommand precisa reavaliar CanExecute (botão/atalho desabilitados sem documento)
    // toda vez que a aba ativa muda — NotifyCanExecuteChangedFor dispensa chamar
    // PrintCommand.NotifyCanExecuteChanged() manualmente a cada setter. Task 3 (Plano 3a): mesma
    // dispensa agora cobre também Save/SaveAs — CanSave/CanSaveAs dependem de SelectedDocument.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    // Task 5 (Plano 3a): EditCopyCommand.CanExecute depende de SelectedDocument.IsSignedDocument —
    // mesma dispensa de NotifyCanExecuteChanged manual usada pelos 3 comandos acima cobre a TROCA de
    // aba; OnSelectedDocumentPropertyChanged (abaixo) cobre o caso complementar (IsSignedDocument que
    // vira true DEPOIS que a aba já está selecionada, quando a checagem em background termina).
    [NotifyCanExecuteChangedFor(nameof(EditCopyCommand))]
    // Task 4 (Plano 3b): CanSplit também depende de SelectedDocument (precisa de um documento aberto).
    [NotifyCanExecuteChangedFor(nameof(SplitCommand))]
    private DocumentViewModel? selectedDocument;

    // Task 3 (Plano 3a): SaveCommand.CanExecute ("enabled when dirty") também precisa reavaliar
    // quando o documento JÁ selecionado fica sujo/limpo (Apply/Save dentro da MESMA aba) — o
    // [NotifyCanExecuteChangedFor] acima só cobre TROCA de aba. Assina/desassina o PropertyChanged
    // do DocumentViewModel corrente sempre que a seleção muda.
    partial void OnSelectedDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnSelectedDocumentPropertyChanged;
            // Task 2 (Plano 5): mesma disciplina de assinar/desassinar de PropertyChanged acima, mas pro
            // pino COMPARTILHADO (`Session.IsEditInFlight`) que CanSave/CanSaveAs agora compõem (ver doc
            // XML de CanSave abaixo) — um Rotate/Sign/etc. armado pelo ORGANIZADOR ou pelo leitor da aba
            // ATIVA precisa desabilitar Save/SaveAs aqui tanto quanto `OnSessionEditInFlightChanged` já
            // desabilita os comandos mutadores do próprio DocumentViewModel.
            oldValue.Session.EditInFlightChanged -= OnSelectedSessionEditInFlightChanged;
            // Task 2 (Plano 8): trocar de aba (ou fechar a aba ATIVA — CloseDocument reatribui
            // SelectedDocument, disparando este MESMO handler) com a caixa ajustável do carimbo em
            // Drawing/Adjusting na aba ANTIGA cancela a colocação — mesmo contrato de Esc/botão/troca de
            // ferramenta (CancelStampBox, Task 1/2); Task 1 deixou "troca de documento" registrado como
            // escopo futuro (o gesto de mouse só é alcançável na aba VISÍVEL, então uma colocação nunca
            // fica "presa" numa aba em segundo plano — reseta ANTES de a aba sair de vista, não depois).
            // CancelStampBox() é idempotente/seguro mesmo quando não há nada em andamento.
            oldValue.CancelStampBox();
        }
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSelectedDocumentPropertyChanged;
            newValue.Session.EditInFlightChanged += OnSelectedSessionEditInFlightChanged;
        }
        // Painel de miniaturas OCULTO por padrão (decisão do usuário 2026-09-01: mais espaço pra
        // página em projetos grandes; quem quiser ver as miniaturas clica no ícone do rail). Abrir um
        // documento NÃO revela mais o painel automaticamente — o estado só muda por ação explícita do
        // usuário (clicar no ícone do rail ou no botão de recolher do cabeçalho).
    }

    private void OnSelectedDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.IsDirty)) SaveCommand.NotifyCanExecuteChanged();
        // Task 5 (Plano 3a): IsSignedDocument começa false (checagem em voo, ver doc XML na VM) e vira
        // true um pouco DEPOIS que a aba já está selecionada — sem isto, o botão "Editar uma cópia"
        // (banner) ficaria desabilitado até o usuário trocar de aba e voltar.
        if (e.PropertyName == nameof(DocumentViewModel.IsSignedDocument)) EditCopyCommand.NotifyCanExecuteChanged();
    }

    /// Ver comentário em `OnSelectedDocumentChanged` acima — reavalia Save/SaveAs sempre que o pino
    /// compartilhado da aba ATIVA muda de estado (armado OU solto, por QUALQUER comando mutador: o
    /// próprio Save/SaveAs — ver abaixo —, o organizador, ou os comandos do leitor).
    private void OnSelectedSessionEditInFlightChanged(object? sender, EventArgs e)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
    }

    // Painel de miniaturas / recolhível (fix-painel-recolhivel): controla a Visibility do painel
    // esquerdo de 238px (ver MultiBinding + DocumentoEPainelVisivelConverter em MainWindow.xaml) —
    // recolhido por ícone ativo (toggle) ou pelo botão do cabeçalho do painel, reaberto pelo rail.
    // Default FALSE (Plano 17): o painel vem OCULTO por padrão (decisão do usuário — mais espaço pra
    // página); abrir um documento NÃO o revela, só a ação explícita do usuário (ícone do rail / botão
    // de recolher). Sem documento a Visibility também não depende disto (SelectedDocument==null já colapsa).
    [ObservableProperty]
    private bool thumbnailsVisible = false;

    // Posição do menu de anotação (Plano 17, Task 3) — true = tira vertical no rail de 58px; false = a
    // pílula flutuante do centro-inferior (padrão). Inicializado da config PERSISTIDA no construtor; a
    // opção do diálogo Configurações troca isto AO VIVO (via o callback aplicarPosicaoMenuAnotacao em
    // Configuracoes()) — os bindings de Visibility da pílula (AnotacaoBar) e da tira do rail
    // (AnotacaoRailStrip) em MainWindow.xaml reagem na hora, sem recriar a janela.
    [ObservableProperty]
    private bool menuAnotacaoNaBarraLateral;

    // Indicador de progresso (Task 7): true durante o await de DocumentSession.OpenAsync — a
    // abertura de um arquivo grande/SMB não é mais instantânea (parse PDFium em Task.Run), então a
    // status bar mostra "Abrindo documento…" pra não parecer que o app travou.
    [ObservableProperty]
    private bool isOpening = false;

    public IReadOnlyList<string> RecentFiles => _recent.Load();

    /// Itens da galeria de carimbos (Task 9, Plano 3a) — nome + miniatura já decodificada, prontos pro
    /// binding do popup da toolbar (ver MainWindow.xaml). Recalculado (`RefreshStampItems`) a cada
    /// Add/Remove — mesmo padrão de `RecentFiles`, só que aqui o VALOR é cacheado num campo (não relido
    /// do disco a cada acesso à propriedade): decodificar N miniaturas a cada binding seria caro à toa.
    [ObservableProperty]
    private ObservableCollection<StampGalleryItem> stampItems = [];

    public MainViewModel(IFileDialogService dialogs) : this(dialogs, new RecentFilesStore(RecentFilesStore.DefaultDirectory)) { }

    // Task 0 (Plano 3c): `DefaultNotifyError` (argumento fixo encadeado, não um parâmetro opcional) virou
    // `UiPrompts.MainNotifyError` — mesma classe de risco que os defaults `?? algumDefault` abaixo (um
    // teste que chama ESTE overload sem saber que ele encadeia num MessageBox real também travaria).
    public MainViewModel(IFileDialogService dialogs, RecentFilesStore recent) : this(dialogs, recent, UiPrompts.MainNotifyError) { }

    // Assinaturas de 1/2/3 argumentos PRESERVADAS byte-a-byte (Task 3, Plano 3a): testes existentes
    // (MainViewModelTests) chamam `new MainViewModel(dialogs, recent, notifyErrorLambda)` — inserir
    // um parâmetro NOVO antes de notifyError quebraria essa posição. AppConfig/IConfirmCloseService
    // entram só no overload de 5 argumentos abaixo; testes que precisam injetá-los chamam ele direto.
    // Task 0 (Plano 3c): `new MessageBoxConfirmCloseService()` encadeado virou `UiPrompts.CreateConfirmClose()`
    // — mesmo raciocínio do NotifyError acima (chamar este overload sem saber invocaria um MessageBox real
    // ao fechar um documento sujo).
    public MainViewModel(IFileDialogService dialogs, RecentFilesStore recent, Action<string> notifyError)
        : this(dialogs, recent, notifyError, new AppConfig(AppConfig.DefaultDirectory), UiPrompts.CreateConfirmClose()) { }

    public MainViewModel(
        IFileDialogService dialogs,
        RecentFilesStore recent,
        Action<string> notifyError,
        AppConfig config,
        IConfirmCloseService confirmClose,
        IAnnotationTextDialogService? annotationDialog = null,
        StampGallery? stampGallery = null,
        Action<string>? notifyInfo = null,
        IMergeDialogService? mergeDialog = null,
        ISplitDialogService? splitDialog = null,
        IPdfEditor? editor = null,
        IBatchSignDialogService? batchSignDialog = null,
        Func<IReadOnlyList<SigningCertificateInfo>>? listSigningCertificates = null,
        ISobreDialogService? sobreDialog = null,
        IConfiguracoesDialogService? configuracoesDialog = null)
    {
        _dialogs = dialogs;
        _recent = recent;
        _notifyError = notifyError;
        _config = config;
        _confirmClose = confirmClose;
        // Task 0 (Plano 3c): defaults vêm do seam `UiPrompts` — ver doc XML de UiPrompts.
        _annotationDialog = annotationDialog ?? UiPrompts.CreateAnnotationDialog();
        _stampGallery = stampGallery ?? new StampGallery(StampGallery.DefaultDirectory);
        _notifyInfo = notifyInfo ?? UiPrompts.NotifyInfo;
        _mergeDialog = mergeDialog ?? UiPrompts.CreateMergeDialog();
        _splitDialog = splitDialog ?? UiPrompts.CreateSplitDialog();
        _editor = editor ?? PdfEditorFactory.Create();
        // Task 5 (Plano 4): defaults do lote de assinatura.
        _batchSignDialog = batchSignDialog ?? UiPrompts.CreateBatchSignDialog();
        _listSigningCertificates = listSigningCertificates ?? CertificateCatalog.ListSigningCertificates;
        // Task 2 (Plano 11): diálogo "Sobre" — mesma disciplina de injeção via UiPrompts.
        _sobreDialog = sobreDialog ?? UiPrompts.CreateSobreDialog();
        // Task 2 (Plano 17): diálogo "Configurações" — mesma disciplina de injeção via UiPrompts.
        _configuracoesDialog = configuracoesDialog ?? UiPrompts.CreateConfiguracoesDialog();
        // Plano 17 (Task 3): estado inicial da posição do menu de anotação lido do config PERSISTIDO
        // (mesmo _config das outras preferências) -- setado no CAMPO (não na propriedade) pois é o estado
        // inicial, não uma mudança de UI (nenhum documento/janela existe ainda quando o VM é construído).
        menuAnotacaoNaBarraLateral = _config.PosicaoMenuAnotacao == PosicaoMenuAnotacao.BarraLateral;
        RefreshStampItems();
    }

    // Task 0 (Plano 3c): DefaultNotifyError/DefaultNotifyInfo mudaram de método estático local pra
    // UiPrompts.MainNotifyError/UiPrompts.NotifyInfo (ver ctors acima) — texto/ícone preservados.

    [RelayCommand]
    private async Task OpenFile()
    {
        if (_dialogs.PickPdfToOpen() is { } path) await OpenPath(path);
    }

    public async Task OpenPath(string path)
    {
        // Task 2 (Plano 7): caminho de IMAGEM (.jpg/.jpeg/.png) -> ramo SEPARADO, antes de qualquer
        // lógica de dedupe/abertura de PDF abaixo (ver OpenImageAsNewDocument). Roteado por AQUI (não um
        // método público novo) de propósito: os 3 chamadores existentes de OpenPath (OpenFileCommand,
        // OpenRecentCommand, App.xaml.cs — args-path e forwarding de instância única) precisam do MESMO
        // comportamento estendido sem precisar saber que imagem é um caso diferente de PDF.
        if (ImageImport.IsImagePath(path)) { await OpenImageAsNewDocument(path); return; }

        // Dedupe (Task 7): mesmo caminho completo já aberto numa aba -> só seleciona, sem duplicar.
        // ANTES de tocar no arquivo (nem File.Exists) — se já está aberto, a leitura é desnecessária.
        if (Documents.FirstOrDefault(d => string.Equals(d.Session.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            SelectedDocument = existing;
            return;
        }

        // Guarda de reentrância: se este caminho já está em voo (outra chamada de OpenPath ainda não
        // terminou), esta chamada não faz nada — a 1ª que terminar é quem adiciona a aba; o dedupe
        // acima cobre chamadas SEQUENCIAIS (aba já existe), este Add cobre as CONCORRENTES (aba ainda
        // não existe, mas já tem uma abertura andando).
        if (!_opening.Add(path)) return;

        IsOpening = true;
        try
        {
            // Task 1 (Plano 5): teto de bytes no undo -- lê os ceilings da MESMA AppConfig já usada pro
            // Autor/CriarBackup desta janela (não um literal hardcoded), em vez de deixar OpenAsync cair
            // nos defaults de produção sempre (que TAMBÉM são 256 MB/2 GB hoje, mas só por coincidência
            // — uma config.json editada manualmente/uma futura tela de Settings precisa ser respeitada).
            var session = await DocumentSession.OpenAsync(path, _config.MaxUndoRamBytes, _config.MaxUndoSpillBytes);

            // Task 2 (Plano 3c) — `HasXfa` primeiro: o ÚNICO dos métodos de formulário que NUNCA lança
            // por causa de XFA (detector puro de presença da chave) — decide se `ReadFormFields` (que
            // LANÇA em doc XFA, contrato pinado no Task 1 fix) pode ser chamado com segurança.
            var formEditor = PdfEditorFactory.Create();
            bool isXfaForm;
            try { isXfaForm = await Task.Run(() => formEditor.HasXfa(session.Snapshot)); }
            catch { session.Dispose(); throw; }

            // Task 5 (Plano 3a): checagem de assinatura OFF da UI thread — um Task.Run SEGUINTE ao de
            // OpenAsync (não dentro de DocumentSession/DocumentViewModel: nenhum dos dois referencia
            // mPdf.Editing; "durante a abertura" É este método). Se a checagem lançar (ex.: um PDF que
            // o PDFium tolera mas o iText rejeita), a sessão já aberta precisa ser descartada aqui —
            // sem isto, o renderer nativo e a pasta de spill de undo/redo dela vazariam, já que nenhum
            // DocumentViewModel chegaria a existir pra ficar dono do Dispose. O erro em si cai no MESMO
            // catch de qualquer outra falha de abertura logo abaixo — mesma UX, sem caminho especial.
            //
            // Important 2 (revisão do Task 2, Plano 3c) — `HasSignatures` chamado SEM condicionar a
            // `!isXfaForm`: o achado original (`SignatureUtil` lançando em doc XFA) foi corrigido no
            // MOTOR (`PdfEditor.CountSignatures`/`HasXfaKey` — ver mPdf.Editing/PdfEditor.cs), não mais
            // contornado aqui. Um doc XFA-E-assinado agora reporta `isSigned = true` de verdade (banner
            // de assinado aparece, "Editar uma cópia" fica disponível) — `CanEdit` continua falso de
            // qualquer forma pra doc XFA (compõe `IsXfaForm` também, ver doc XML de
            // `DocumentViewModel.CanEdit`), então os DOIS gates convivem sem conflito.
            bool isSigned;
            try { isSigned = await Task.Run(() => PdfEditorFactory.Create().HasSignatures(session.Snapshot)); }
            catch { session.Dispose(); throw; }

            // Task 6 (Plano 4): permissão de preenchimento incremental — só importa (e só custa abrir o
            // PDF de novo) quando o documento JÁ está assinado; `FillPermission.NotSigned` é o default
            // seguro pro caso comum (documento sem assinatura nenhuma, a maioria), mesmo espírito
            // condicional de `formFields` abaixo (só lê quando `!isXfaForm`). Mesma rede de descarte que
            // os 2 checks acima — uma falha aqui não pode vazar a sessão já aberta.
            var signedFillPermission = FillPermission.NotSigned;
            if (isSigned)
            {
                try { signedFillPermission = await Task.Run(() => SigningEngineFactory.Create().CanFillIncremental(session.Snapshot)); }
                catch { session.Dispose(); throw; }
            }

            // Task 2 (Plano 3c): cache de campos de formulário — computado AQUI, no MESMO fluxo já-async
            // de abertura (Obs 17: "cache de campos computado no caller já-async, NUNCA fire-and-forget
            // em construtor"). Documento XFA -> lista vazia por construção (nunca chama ReadFormFields,
            // que AINDA lança em XFA — contrato pinado, Task 1 fix, INTOCADO por este fix). Mesma rede
            // de descarte que os 2 checks acima: uma falha aqui não pode vazar a sessão já aberta.
            IReadOnlyList<FormFieldData> formFields = Array.Empty<FormFieldData>();
            if (!isXfaForm)
            {
                try { formFields = await Task.Run(() => formEditor.ReadFormFields(session.Snapshot)); }
                catch { session.Dispose(); throw; }
            }

            // Task 6 (Plano 3a): config/notifyError PROPAGADOS pra DocumentViewModel — mesmo AppConfig
            // (Autor das anotações) e mesmo canal de notificação de erro (PdfEditingException do
            // ApplyMarkupCommand) desta janela, em vez de cada documento criar o seu próprio default
            // (que funcionaria, mas duplicaria a leitura de config.json e poderia abrir um 2º
            // MessageBox com estilo diferente do resto do app).
            var doc = new DocumentViewModel(session, config: _config, notifyError: _notifyError, annotationDialog: _annotationDialog, dialogs: _dialogs, notifyInfo: _notifyInfo)
                { IsSignedDocument = isSigned, SignedFillPermission = signedFillPermission };
            // Task 2 (Plano 3c): semeia o cache de campos JÁ CALCULADO acima — ver doc XML de
            // DocumentViewModel.SeedFormFieldsCache (Obs 17).
            doc.SeedFormFieldsCache(isXfaForm, formFields);
            Documents.Add(doc);
            SelectedDocument = doc;
            _recent.Add(path);
            OnPropertyChanged(nameof(RecentFiles));
        }
        catch (Exception ex)
        {
            // Recente que aponta pra um arquivo que não abre mais (movido/apagado) sai da lista —
            // continuar oferecendo um caminho morto no menu só gera outro erro na próxima tentativa.
            // Isolado no seu próprio try/catch: um recentes.json travado/corrompido durante a limpeza
            // não pode engolir a notificação do erro ORIGINAL que trouxe o usuário até aqui.
            try
            {
                _recent.Remove(path);
                OnPropertyChanged(nameof(RecentFiles));
            }
            catch (Exception) { /* limpeza de recentes é best-effort; a notificação abaixo é o que importa */ }
            _notifyError(ex.Message);
        }
        finally
        {
            // IsOpening reflete se AINDA HÁ alguma abertura em voo (não necessariamente esta) — com a
            // guarda de reentrância acima, várias aberturas de caminhos DIFERENTES podem estar
            // concorrentes; o indicador só deve sumir quando a ÚLTIMA delas terminar.
            _opening.Remove(path);
            IsOpening = _opening.Count > 0;
        }
    }

    [RelayCommand]
    private async Task OpenRecent(string path) => await OpenPath(path);

    // ---- Task 2 (Plano 7): "Abrir" com uma IMAGEM (.jpg/.jpeg/.png) ----------------------------------
    //
    // DESIGN DO DOCUMENTO NÃO-SALVO (registrado no relatório): exemplar TRAÇADO é `EditCopy` acima —
    // "converte bytes -> DocumentSession.WriteNewFile (mesmo AtomicWrite de Save/SaveAs) -> await
    // OpenPath(novoCaminho)", a MESMA receita, escolhida por ser a MENOS invasiva consistente com um
    // precedente já existente no código (DocumentSession não precisou ganhar um construtor "pathless" —
    // continua abrindo de um caminho real de disco sempre, só que aqui o caminho é um arquivo TEMPORÁRIO
    // recém-escrito, nunca um caminho escolhido pelo usuário). A ALTERNATIVA cogitada (estender
    // DocumentSession para abrir sem nenhum arquivo em disco, "pathless open") foi rejeitada: exigiria
    // uma 2ª forma de construir a sessão só para este caso, quando o app já tem um jeito PROVADO de
    // materializar bytes recém-produzidos como um arquivo real e abrir uma sessão de verdade sobre ele.
    //
    // POR QUE TEMP (não um arquivo permanente ao lado do original, como EditCopy faz): a imagem
    // convertida NUNCA teve um "lar" escolhido pelo usuário — ele só pediu para ABRIR uma foto, não
    // decidiu ainda ONDE o PDF resultante deveria morar. Gravar ao lado do original poluiria a pasta do
    // usuário com um .pdf que ele não pediu explicitamente; gravar em %TEMP% deixa claro que é um estado
    // TRANSITÓRIO até o usuário decidir (Salvar como). `NeedsSaveAs = true` (setado logo abaixo) é a
    // salvaguarda que impede um "Salvar" (Ctrl+S) de gravar SILENCIOSAMENTE de volta nesse temp — ver
    // doc XML de `Save`/`TryResolveDirtyDocument`.
    //
    // SEM dedupe por caminho ORIGINAL de propósito (decisão registrada no relatório): o dedupe padrão de
    // `OpenPath` (mesmo `Session.FilePath`) nunca dispara aqui porque cada chamada gera um caminho temp
    // com um GUID novo (`BuildConvertedImageTempPath`) — abrir a MESMA imagem duas vezes produz DOIS
    // documentos. Aceito: um cache de conversões (dedupe por caminho de ORIGEM) adicionaria estado extra
    // sem um requisito real do brief; cada import é tratado como um rascunho novo, nunca uma referência
    // compartilhada a uma conversão anterior.
    private async Task OpenImageAsNewDocument(string imagePath)
    {
        IsOpening = true;
        try
        {
            byte[] pdfBytes;
            try { pdfBytes = await ImageImport.ConvertToPdfAsync(imagePath, _editor); }
            catch (Exception ex) { _notifyError(ex.Message); return; }

            string tempPath = BuildConvertedImageTempPath(imagePath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                await Task.Run(() => DocumentSession.WriteNewFile(tempPath, pdfBytes));
            }
            catch (Exception ex) { _notifyError(ex.Message); return; }

            await OpenPath(tempPath);
            // Task 2 (Plano 7): NeedsSaveAs setado DEPOIS que a aba já existe (não antes) — se a
            // abertura em si falhar (ex.: PDFium recusa o resultado por algum motivo), não há
            // DocumentViewModel nenhum para marcar; o guard de caminho abaixo garante que só marcamos o
            // documento que REALMENTE corresponde a esta conversão (defesa contra uma corrida
            // improvável onde SelectedDocument mudou por outro caminho entre o await acima e esta linha).
            if (SelectedDocument is { } doc && string.Equals(doc.Session.FilePath, tempPath, StringComparison.OrdinalIgnoreCase))
                doc.NeedsSaveAs = true;
        }
        finally { IsOpening = _opening.Count > 0; }
    }

    // "<nome-sem-extensão> (convertido).pdf" numa pasta TEMP própria por GUID (nunca reaproveitada —
    // mesmo raciocínio de `DocumentSession.NewUndoSpillDirectory`) — o GUID na pasta (não no nome do
    // arquivo) é o que garante caminhos diferentes em aberturas repetidas da MESMA imagem, mantendo o
    // NOME exibido na aba limpo ("foto (convertido).pdf", nunca "foto (convertido)-a1b2c3.pdf").
    private static string BuildConvertedImageTempPath(string imagePath)
    {
        string fileName = $"{Path.GetFileNameWithoutExtension(imagePath)} (convertido).pdf";
        return Path.Combine(Path.GetTempPath(), "mPDF", $"open-{Guid.NewGuid():N}", fileName);
    }

    // ---- Task 5 (Plano 3a): "Editar uma cópia" -------------------------------------------------------
    //
    // Único caminho de edição pra um documento assinado: a precondição em
    // IPdfEditor.AddAnnotation/RemoveAnnotation RECUSA editar o assinado (spec ICP-Brasil §5.2) — a
    // cópia SEM assinatura nenhuma é o que Tasks 6-9 vão realmente editar. StripSignatures roda em
    // Task.Run (parse iText é CPU-bound, não pode rodar na UI thread); a gravação em disco reaproveita
    // DocumentSession.WriteNewFile (a MESMA AtomicWrite de Save/SaveAs — ver doc XML lá pra por que NÃO
    // instanciamos uma DocumentSession descartável só pra gravar: vazaria um renderer nativo + uma
    // pasta de spill de undo/redo por clique). Termina abrindo a cópia numa aba NOVA via OpenPath (o
    // dedupe de lá já cobre clicar duas vezes rápido).
    [RelayCommand(CanExecute = nameof(CanEditCopy))]
    private async Task EditCopy()
    {
        if (SelectedDocument is not { IsSignedDocument: true } doc) return;

        byte[] snapshot = doc.Session.Snapshot;
        string originalPath = doc.Session.FilePath;
        string newPath;
        try
        {
            newPath = await Task.Run(() =>
            {
                var stripped = PdfEditorFactory.Create().StripSignatures(snapshot);
                var path = BuildEditableCopyPath(originalPath);
                DocumentSession.WriteNewFile(path, stripped);
                return path;
            });
        }
        catch (Exception ex) { _notifyError(ex.Message); return; }

        await OpenPath(newPath);
    }

    private bool CanEditCopy() => SelectedDocument is { IsSignedDocument: true };

    // "<nome> (cópia editável).pdf" AO LADO do original (brief, Task 5); colisão de nome -> " (2)",
    // " (3)"... Varredura simples por File.Exists — aceitável: sem concorrência real entre a checagem e
    // a escrita (mesma classe de aceitação já usada em DocumentSession.SweepOrphanTempFiles).
    private static string BuildEditableCopyPath(string originalPath)
    {
        string dir = Path.GetDirectoryName(originalPath)
            ?? throw new IOException($"Não foi possível determinar o diretório de '{originalPath}'.");
        string baseName = $"{Path.GetFileNameWithoutExtension(originalPath)} (cópia editável)";
        string ext = Path.GetExtension(originalPath);

        string candidate = Path.Combine(dir, baseName + ext);
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{baseName} ({n}){ext}");
        return candidate;
    }

    // Task 3 (Plano 3a): documento SUJO pede confirmação (Salvar/Descartar/Cancelar, pt-BR) antes de
    // fechar — via _confirmClose (injetável, ver doc XML de IConfirmCloseService). Documento LIMPO
    // fecha direto, sem prompt (comportamento antigo preservado). "Salvar" que FALHA (ex.: destino
    // travado) não fecha a aba — o usuário não pode perder a edição por um erro de I/O silencioso.
    [RelayCommand]
    private void CloseDocument(DocumentViewModel doc)
    {
        if (!TryResolveDirtyDocument(doc)) return;
        Documents.Remove(doc);
        if (SelectedDocument == doc) SelectedDocument = Documents.LastOrDefault();
        doc.Dispose();
    }

    // Extraído (I3, revisão pós-Task 3) de dentro de CloseDocument — a MESMA decisão (perguntar só se
    // sujo; Cancelar recusa; Salvar que falha recusa e notifica; Descartar aceita sem tocar o arquivo)
    // agora é reutilizada tanto pra fechar UMA aba (CloseDocument) quanto pra fechar a JANELA inteira
    // (ConfirmCloseAll, abaixo) — um único lugar decide "posso descartar este documento sem perder
    // dados do usuário?", nunca duas implementações que podem divergir com o tempo.
    private bool TryResolveDirtyDocument(DocumentViewModel doc)
    {
        if (!doc.IsDirty) return true;
        var choice = _confirmClose.Confirm(doc.Session.FileName);
        switch (choice)
        {
            case CloseConfirmation.Cancel:
                return false;
            case CloseConfirmation.Save:
                // Task 2 (Plano 7): mesma desvio de "Salvar" pra "Salvar como" que `MainViewModel.Save`
                // aplica no comando síncrono — sem isto, escolher "Salvar" aqui pra um documento
                // temp-backed gravaria silenciosamente de volta em %TEMP%, violando a MESMA garantia
                // pelo caminho de fechar aba em vez do Ctrl+S.
                return doc.NeedsSaveAs ? TrySaveAsSync(doc) : TrySaveSync(doc);
            case CloseConfirmation.Discard:
            default:
                return true;
        }
    }

    private bool TrySaveSync(DocumentViewModel doc)
    {
        try { doc.Session.Save(_config); return true; }
        catch (Exception ex) { _notifyError(ex.Message); return false; }
    }

    // Contraparte SÍNCRONA de `SaveAs` (o comando é `async` — arma `TryBeginEdit`/`Task.Run` — mas
    // `TryResolveDirtyDocument` é chamado de contextos síncronos, `CloseDocument`/`ConfirmCloseAll`; o
    // diálogo em si (`PickPdfToSaveAs`) já é síncrono, e a escrita de um único documento não justifica
    // sair pra uma Task só pra este caminho residual). Diálogo CANCELADO -> `false` (mesma semântica de
    // "Save que falha": a aba NÃO fecha, sem notificar erro nenhum — cancelar não é uma falha).
    private bool TrySaveAsSync(DocumentViewModel doc)
    {
        if (_dialogs.PickPdfToSaveAs(doc.Session.FilePath) is not { } path) return false;
        try
        {
            doc.Session.SaveAs(path);
            doc.NeedsSaveAs = false;
            _recent.Add(path);
            OnPropertyChanged(nameof(RecentFiles));
            return true;
        }
        catch (Exception ex) { _notifyError(ex.Message); return false; }
    }

    // I3 (revisão pós-Task 3): chamado por MainWindow.OnClosing ao fechar a JANELA inteira (✕ da
    // barra de título, Alt+F4) — sem isso, fechar a janela com abas sujas descartava tudo em silêncio
    // (só CloseDocument, por clique no ✕ da aba, perguntava). Um prompt POR documento sujo, na ORDEM
    // de Documents (mesma consistência de UX de CloseDocument — não um prompt combinado "existem N
    // documentos não salvos"). Devolve `false` na PRIMEIRA negativa (Cancelar, ou Salvar que falha) —
    // a janela NÃO deve fechar nesse caso; documentos que já foram resolvidos (salvos/descartados)
    // ANTES da negativa continuam com o efeito já aplicado (não há como "desfazer" um Save que já
    // escreveu no disco, e não faria sentido reverter uma decisão de Descartar já tomada pelo
    // usuário) — comportamento aceito, mesma limitação de qualquer app que processa uma lista
    // sequencialmente. NÃO faz Dispose de nada — quem descarta os DocumentViewModel continua sendo
    // MainWindow.OnClosed, chamado pelo WPF só depois que OnClosing deixa a janela fechar de fato.
    public bool ConfirmCloseAll()
    {
        foreach (var doc in Documents)
            if (!TryResolveDirtyDocument(doc)) return false;
        return true;
    }

    // Task 8: chama o WPF PrintDialog nativo diretamente do VM — mesmo precedente já aberto por
    // DefaultNotifyError (MessageBox.Show direto no VM); nada mais no app precisa mediar isso hoje.
    [RelayCommand(CanExecute = nameof(CanPrint))]
    private void Print() => PrintService.Print(SelectedDocument!);

    private bool CanPrint() => SelectedDocument is not null;

    // Task 3 (Plano 3a): 💾 Salvar (Ctrl+S) — só habilitado quando o documento ATIVO está sujo (não
    // faz sentido reescrever um arquivo idêntico ao que já está em disco). Reaproveita
    // DocumentSession.Save (temp + File.Replace + .bak condicional — ver doc XML lá); erro de I/O
    // (ex.: destino travado por outro processo) vira notificação pt-BR, documento continua sujo.
    //
    // Task 2 (Plano 5): ASSÍNCRONO — gravar um snapshot de centenas de MB em `Task.Run` congelava a UI
    // por segundos inteiros (ledger: gate #3 pré-rollout, medido em 525 MB). O pipeline atômico em si
    // (`DocumentSession.Save`, temp+`File.Replace`+`.bak`) continua INTOCADO — só passou a rodar fora da
    // UI thread. `TryBeginEdit()` arma o funil SÍNCRONO, ANTES do primeiro `await` (mesmo contrato de
    // `Sign`/`ConfirmSignatureStampAsync` em `DocumentViewModel` — ver doc XML de
    // `DocumentSession.TryBeginEdit`): salvar agora É uma operação exclusiva de sessão, então NENHUMA
    // edição (organizador OU leitor, o pino é compartilhado) pode aterrissar entre a leitura de
    // `Session.Snapshot` (dentro de `Save`, no Task.Run) e a escrita em disco — sem isso, um Rotate
    // concorrente com um Save em voo escreveria um arquivo cujo conteúdo diverge do que o `IsDirty`
    // (limpo por `MarkSaved`) afirma ter sido persistido. `CanSave` (abaixo) compõe
    // `!Session.IsEditInFlight` pelo MESMO motivo, E fecha o minor do 3b pela via inversa: uma edição
    // JÁ em voo agora desabilita Save também (antes, salvar durante uma edição em voo gravava o snapshot
    // PRÉ-edição e limpava o "•" por ~1s até a edição aplicar por cima, sujando de novo).
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (SelectedDocument is not { } doc) return;
        // Task 2 (Plano 7): documento TEMP-BACKED (aberto de uma imagem convertida — ver
        // OpenImageAsNewDocument) NUNCA pode ser salvo silenciosamente de volta no arquivo em %TEMP% —
        // "Salvar" vira "Salvar como" até o usuário escolher um destino real. Reusa o MESMO método
        // `SaveAs` (não uma cópia da lógica): `doc == SelectedDocument` aqui, então chamar o corpo do
        // comando direto (sem passar por `SaveAsCommand.ExecuteAsync`) já tem as mesmas precondições que
        // `CanSave` já garantiu (documento selecionado, sem edição em voo).
        if (doc.NeedsSaveAs) { await SaveAs(); return; }
        if (!doc.Session.TryBeginEdit()) return; // outra edição em voo — mesmo funil de qualquer outro comando
        doc.IsSaving = true;
        try { await Task.Run(() => doc.Session.Save(_config)); }
        catch (Exception ex) { _notifyError(ex.Message); }
        finally
        {
            doc.Session.EndEdit();
            doc.IsSaving = false;
        }
    }

    // Task 2 (Plano 5): agora também composto por `!Session.IsEditInFlight` — mesmo raciocínio de
    // `CanApplyMarkup`/`CanSign`/etc. em `DocumentViewModel` (o pino é COMPARTILHADO entre leitor,
    // organizador e, a partir de agora, o próprio Save/SaveAs). `OnSelectedSessionEditInFlightChanged`
    // (acima) e `OnSelectedDocumentPropertyChanged` (IsDirty) mantêm isto reavaliado.
    private bool CanSave() => SelectedDocument is { IsDirty: true } && !SelectedDocument.Session.IsEditInFlight;

    // Task 3 (Plano 3a): "Salvar como…" — sempre disponível havendo um documento (mesmo limpo,
    // diferente de Save). Escrita atômica simples (sem .bak — ver doc XML de DocumentSession.SaveAs);
    // sucesso adiciona o NOVO caminho aos recentes (decisão de camada: SaveAs em si não conhece
    // RecentFilesStore, só a VM).
    //
    // Task 2 (Plano 5): ASSÍNCRONO — mesmo motivo/contrato de `Save` acima. `PickPdfToSaveAs` (diálogo
    // modal, síncrono) roda ANTES de armar o funil — mesma ordem de `SaveAs`/`Sign` (UI bloqueante
    // primeiro, funil só imediatamente antes do trabalho de verdade), nunca o inverso (armar e SÓ DEPOIS
    // abrir um diálogo prenderia o funil pelo tempo inteiro que o usuário leva decidindo o caminho).
    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private async Task SaveAs()
    {
        if (SelectedDocument is not { } doc) return;
        if (_dialogs.PickPdfToSaveAs(doc.Session.FilePath) is not { } path) return;
        if (!doc.Session.TryBeginEdit()) return; // outra edição em voo — mesmo funil de qualquer outro comando
        doc.IsSaving = true;
        try
        {
            await Task.Run(() => doc.Session.SaveAs(path));
            // Task 2 (Plano 7): documento ganhou um "lar" definitivo — o próximo Save já pode gravar
            // direto ali (sem desviar de novo pra este mesmo diálogo). No-op para um documento que já
            // não era temp-backed (o caso comum, `NeedsSaveAs` já false).
            doc.NeedsSaveAs = false;
            _recent.Add(path);
            OnPropertyChanged(nameof(RecentFiles));
        }
        catch (Exception ex) { _notifyError(ex.Message); }
        finally
        {
            doc.Session.EndEdit();
            doc.IsSaving = false;
        }
    }

    private bool CanSaveAs() => SelectedDocument is not null && !SelectedDocument.Session.IsEditInFlight;

    // ---- Task 4 (Plano 3b): Juntar / Dividir documentos ----------------------------------------------
    //
    // Vivem AQUI (não em OrganizerViewModel, como Extrair/Inserir) por decisão do brief: Juntar funciona
    // SEM nenhum documento aberto (concatena arquivos escolhidos no diálogo, nada a ver com uma sessão
    // corrente) e Dividir opera sobre o documento da aba ATIVA, mas produz N arquivos NOVOS em disco —
    // nenhuma das duas MUTA `SelectedDocument.Session` (diferente de Rotate/Delete/Move/Inserir, que
    // vivem no organizador porque editam a sessão aberta ali). Usam `_editor` (injetável — ver doc XML
    // do campo), não `PdfEditorFactory.Create()` inline como `EditCopy` — só pra Merge/Split, adicionado
    // na revisão pós-Task 4 pra permitir testar o catch de `ArgumentException` abaixo sem o motor real.

    /// ➕ Juntar (brief) — SEMPRE habilitado (nenhum `CanExecute`), mesmo sem documento algum aberto.
    /// Diálogo devolve os caminhos JÁ na ordem de concatenação; lê os bytes (convertendo cada entrada de
    /// IMAGEM pra PDF na fronteira — Task 2, Plano 7, `ImageImport.ReadOrConvertToPdf`, ANTES de
    /// `MergeDocuments`; o motor só enxerga PDFs, nunca uma entrada de imagem crua), concatena via
    /// `MergeDocuments`, grava como arquivo NOVO (SaveFileDialog) e abre o resultado numa aba nova —
    /// `OpenPath` (mesmo caminho de abrir qualquer PDF, dedupe/seleção de aba inclusos de graça).
    ///
    /// ATÔMICO (decisão registrada no relatório, Task 2 Plano 7): a conversão de CADA imagem roda dentro
    /// do MESMO `Task.Run`/`try` que já lia os bytes brutos — se QUALQUER arquivo (imagem ou PDF) falhar
    /// (arquivo ausente, imagem corrompida/CMYK/acima do teto de pixels — `ImageImport.ConvertToPdf` já
    /// nomeia o arquivo na mensagem), o `Select(...).ToArray()` propaga a exceção do PRIMEIRO item que
    /// falhar e o `catch` abaixo aborta o comando INTEIRO antes de tocar `MergeDocuments`/o SaveFileDialog
    /// — nenhum arquivo parcial é escrito, nenhuma aba abre. Alternativa cogitada (juntar só os arquivos
    /// que converteram, pular os que falharam com um aviso) foi rejeitada: um "documento unificado" que
    /// silenciosamente perde páginas que o usuário pediu é uma corrupção de intenção pior do que só
    /// recusar a operação inteira e deixar o usuário corrigir a entrada problemática.
    [RelayCommand]
    private async Task Merge()
    {
        if (_mergeDialog.PickFilesToMerge() is not { Count: > 0 } paths) return;

        byte[][] inputs;
        try { inputs = await Task.Run(() => paths.Select(p => ImageImport.ReadOrConvertToPdf(p, _editor)).ToArray()); }
        catch (Exception ex) { _notifyError(ex.Message); return; }

        // C1 (revisão final pré-merge): `MergeDocuments` agora tira o widget visual de assinatura de
        // CADA fonte antes de mesclar (ver `PdfEditor.MergeDocuments`) — o arquivo unificado NUNCA fica
        // com um carimbo "assinado" órfão, mas se uma ou mais fontes ESTAVAM assinadas, o resultado sai
        // genuinamente SEM assinatura nenhuma; o usuário precisa saber disso (mesmo aviso de
        // `OrganizerViewModel.ExtractSelected`, plural porque aqui há VÁRIAS fontes possíveis).
        bool anySourceSigned;
        try { anySourceSigned = await Task.Run(() => inputs.Any(_editor.HasSignatures)); }
        catch (PdfEditingException ex) { _notifyError(ex.Message); return; }

        byte[] merged;
        // Revisão pós-Task 4 (achado "Important"): `ArgumentException` também é capturada aqui, mesmo
        // par de catches de `OrganizerViewModel.TryRunEditAsync` pra Rotate/Delete/Move — `MergeDocuments`
        // lança `ArgumentException` CRUA pra lista vazia (ver `Contract.cs`). HOJE esse caminho é
        // INALCANÇÁVEL em uso normal (o guard `{ Count: > 0 }` acima já impede `inputs` de chegar vazio
        // aqui) — mas essa é uma coincidência de acoplamento IMPLÍCITO entre o guard e o motor, não um
        // invariante reforçado pelo tipo; defesa em profundidade, mesmo espírito da precondição
        // redundante já documentada em `PdfEditor.GuardAgainstSignedDocument`.
        try { merged = await Task.Run(() => _editor.MergeDocuments(inputs)); }
        catch (PdfEditingException ex) { _notifyError(ex.Message); return; }
        catch (ArgumentException ex) { _notifyError(ex.Message); return; }

        if (_dialogs.PickPdfToSave("documento unificado.pdf") is not { } savePath) return;
        try { await Task.Run(() => DocumentSession.WriteNewFile(savePath, merged)); }
        catch (Exception ex) { _notifyError(ex.Message); return; }

        // R3 (revisão pós-branch, rider): ponto final ANTES de "Atenção:" — sem ele, as 2 frases
        // concatenavam sem separador ("...unificado.pdf Atenção: ...").
        string message = $"Documento unificado salvo em {Path.GetFileName(savePath)}.";
        if (anySourceSigned)
            message += " Atenção: um ou mais documentos de origem estavam assinados. O arquivo gerado NÃO está assinado.";
        _notifyInfo(message);
        await OpenPath(savePath);
    }

    /// 🔀 Dividir (brief) — precisa de um documento ABERTO (a fonte das páginas). Diálogo coleta a
    /// string de intervalos crua + pasta de destino; `PageRangeParser.Parse` (puro, testável sem WPF)
    /// converte pra 0-based e valida ANTES de tocar o motor — um erro de digitação vira notificação
    /// pt-BR sem sequer chamar `SplitByRanges`. Cada parte vira `nome (parte K).pdf` na pasta escolhida,
    /// com sufixo de colisão (exemplar: `BuildEditableCopyPath`/`StampGallery.Add`).
    [RelayCommand(CanExecute = nameof(CanSplit))]
    private async Task Split()
    {
        if (SelectedDocument is not { } doc) return;
        if (_splitDialog.PickSplitOptions() is not { } opts) return;

        IReadOnlyList<(int from, int to)> ranges;
        try { ranges = PageRangeParser.Parse(opts.Ranges, doc.Session.Renderer.PageCount); }
        catch (ArgumentException ex) { _notifyError(ex.Message); return; }

        // R3.2 (revisão pós-branch, Rodada 2 — achado registrado no relatório): `Split` NÃO precisa do
        // pino `Session.TryBeginEdit`/`IsEditInFlight` (ao contrário dos 6 mutadores do organizador e
        // dos 6 pontos de `ApplyEdit` do leitor) por DOIS motivos combinados: (1) esta linha —
        // `doc.Session.Snapshot` — é lida SINCRONAMENTE, ANTES de qualquer `await` deste método
        // (`PickSplitOptions`/`PageRangeParser.Parse` acima são ambos síncronos) — a captura é atômica
        // por construção, nunca pode "rasgar" um snapshot em transição (`Snapshot` só é TROCADO por
        // referência nova em `Apply`, nunca mutado in-place — ver doc XML de `DocumentSession.Snapshot`);
        // (2) `Split` NUNCA escreve de volta em `doc.Session` (só lê esta cópia local `snapshot` e grava
        // ARQUIVOS NOVOS em disco) — não há "sobrescrita silenciosa" possível, só o mesmo risco de
        // "snapshot desatualizado" que `OrganizerViewModel.ExtractSelected` também aceita implicitamente
        // (extrair/dividir a partir do estado de ANTES de uma edição concorrente é uma leitura VÁLIDA de
        // um instante real do documento, não um dado corrompido). `Merge` (acima) nem chega a ler
        // `Session` de um documento aberto — concatena arquivos do DISCO e abre o resultado numa aba
        // NOVA — dispensa a mesma análise por não tocar sessão nenhuma já existente.
        byte[] snapshot = doc.Session.Snapshot;

        // C1 (revisão final pré-merge): `SplitByRanges` agora tira o widget visual de assinatura da
        // ORIGEM antes de copiar QUALQUER parte (ver `PdfEditor.SplitByRanges`) — nenhuma parte gerada
        // fica com um carimbo "assinado" órfão, mas se a origem ESTAVA assinada, TODAS as partes saem
        // genuinamente SEM assinatura; mesmo aviso de `OrganizerViewModel.ExtractSelected` (1 fonte só,
        // singular).
        bool sourceWasSigned;
        try { sourceWasSigned = await Task.Run(() => _editor.HasSignatures(snapshot)); }
        catch (PdfEditingException ex) { _notifyError(ex.Message); return; }

        IReadOnlyList<byte[]> parts;
        // Revisão pós-Task 4 (mesmo achado "Important" de Merge acima): `ArgumentException` (índice
        // inválido, `to < from`) também é capturada — `PageRangeParser.Parse` já valida os DOIS antes de
        // chegar aqui usando a MESMA `doc.Session.Renderer.PageCount` que `SplitByRanges` valida
        // internamente (mesmos bytes, `snapshot` == origem de `PageCount`), então este caminho é
        // INALCANÇÁVEL hoje — mas é um acoplamento IMPLÍCITO (2 leituras separadas do mesmo estado),
        // não uma garantia de tipo; defesa em profundidade, mesmo par de catches de
        // `OrganizerViewModel.TryRunEditAsync`.
        try { parts = await Task.Run(() => _editor.SplitByRanges(snapshot, ranges)); }
        catch (PdfEditingException ex) { _notifyError(ex.Message); return; }
        catch (ArgumentException ex) { _notifyError(ex.Message); return; }

        string baseName = Path.GetFileNameWithoutExtension(doc.Session.FileName);
        string folder = opts.DestinationFolder;
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < parts.Count; i++)
                    DocumentSession.WriteNewFile(BuildSplitPartPath(folder, baseName, i + 1), parts[i]);
            });
        }
        catch (Exception ex) { _notifyError(ex.Message); return; }

        // Rider: singular quando só 1 arquivo (mesma disciplina de OrganizerViewModel.ExtractSelected).
        // R3 (revisão pós-branch): ponto final ANTES de "Atenção:" — ver mesmo fix em Merge acima.
        string countText = parts.Count == 1 ? "1 arquivo criado" : $"{parts.Count} arquivos criados";
        string message = $"{countText} em {folder}.";
        if (sourceWasSigned)
            message += parts.Count == 1
                ? " Atenção: o documento de origem estava assinado. O arquivo gerado NÃO está assinado."
                : " Atenção: o documento de origem estava assinado. Os arquivos gerados NÃO estão assinados.";
        _notifyInfo(message);
    }

    private bool CanSplit() => SelectedDocument is not null;

    // Colisão de nome -> " (2)", " (3)"... (mesma convenção de `BuildEditableCopyPath`/`StampGallery.Add`
    // acima/StampGallery — varredura simples por `File.Exists`, mesma aceitação de concorrência já
    // documentada nos dois exemplares).
    private static string BuildSplitPartPath(string folder, string baseName, int partNumber)
    {
        string candidate = Path.Combine(folder, $"{baseName} (parte {partNumber}).pdf");
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{baseName} (parte {partNumber}) ({n}).pdf");
        return candidate;
    }

    // ---- Task 5 (Plano 4): 🖊 Assinar em lote ---------------------------------------------------------
    //
    // Opera sobre ARQUIVOS EXTERNOS (mesmo espírito de Merge acima) — nenhuma mutação de Session nenhuma
    // aba, por isso SEM funil (`TryBeginEdit`/`EndEdit`) e SEMPRE habilitado (nenhum `CanExecute`), mesmo
    // sem documento algum aberto. Este comando só COORDENA: constrói o `BatchSignViewModel` (com o
    // catálogo de certificados e o predicado "este caminho está aberto numa aba?") e delega TODA a lógica
    // — adicionar/remover arquivos, assinar em background, progresso, cancelar, resultados — pro VM/
    // diálogo (ver `BatchSignViewModel`, testável sem esta VM/sem janela nenhuma).

    /// 🖊 Assinar em lote (brief) — mesmo padrão SEMPRE-habilitado de `Merge`.
    [RelayCommand]
    private void BatchSign()
    {
        var vm = new BatchSignViewModel(
            _listSigningCertificates(),
            isPathOpen: IsPathOpenInAnyTab,
            pickFiles: PickPdfFilesForBatch,
            editor: _editor); // MESMO IPdfEditor injetável já usado por Merge/Split (GetPageRotations
                               // pro carimbo do lote — ver revisão/doc XML de BatchSignViewModel).
        _batchSignDialog.ShowBatchSignDialog(vm);
    }

    /// Predicado "este caminho está aberto em alguma aba agora?" — MESMA comparação (caminho completo,
    /// `OrdinalIgnoreCase`) do dedupe de `OpenPath` acima. Risco do plano: assinar por baixo um arquivo
    /// que o usuário tem aberto (possivelmente com edições não salvas na aba) deixaria a aba/o disco
    /// divergentes — a recusa existe mesmo o lote NUNCA sobrescrevendo o original (sempre grava
    /// "nome (assinado).pdf" ao lado): o usuário provavelmente quer revisar a versão que está editando
    /// na aba antes de assiná-la, não uma cópia em disco que já pode estar desatualizada.
    /// `internal` (mesmo precedente de `ApplyEditToSelectedDocument`): testável direto contra `Documents`
    /// de verdade, sem precisar abrir a janela do diálogo de lote nem recorrer a reflexão.
    internal bool IsPathOpenInAnyTab(string path) =>
        Documents.Any(d => string.Equals(d.Session.FilePath, path, StringComparison.OrdinalIgnoreCase));

    /// Multi-seleção de PDFs (mesmo filtro/título de `MergeFilesDialog.Add_Click`) — vive AQUI (não
    /// atrás da seam `UiPrompts`) porque é passado como delegate OBRIGATÓRIO ao `BatchSignViewModel` (ver
    /// doc XML da classe: nenhum default implícito, o compilador recusa esquecer de injetar um fake em
    /// teste). `static` porque não depende de estado nenhum deste VM.
    private static IReadOnlyList<string>? PickPdfFilesForBatch()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Adicionar arquivos ao lote",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            Multiselect = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileNames : null;
    }

    // ---- Task 2 (Plano 11; reduzido na Task 2 do Plano 17): "ℹ Sobre" — só informações do app agora ----
    //
    // SEMPRE habilitado (nenhum CanExecute), mesmo espírito de Merge/BatchSign acima — não depende de
    // documento algum aberto. `SobreViewModel` não tem mais estado/comando nenhum (só `VersaoAtual`) —
    // Tema/Nitidez/Atualização migraram pro `ConfiguracoesCommand` abaixo.
    [RelayCommand]
    private void Sobre()
    {
        _sobreDialog.ShowSobreDialog(new SobreViewModel());
    }

    // ---- Task 2 (Plano 17): "⚙ Configurações" — Tema, Nitidez extra e Verificar atualização ------------
    //
    // MIGRADO de `Sobre()` (ver histórico acima) — mesma coordenação exata: constrói o
    // `ConfiguracoesViewModel` com os 3 delegates de produção do fluxo de instalação (fechar sujos pelo
    // MESMO `ConfirmCloseAll` que fechar a janela já usa, iniciar o instalador via `Process.Start`
    // interativo — UAC claro, nunca silencioso —, e encerrar via `Application.Current.Shutdown()`) e
    // delega toda a lógica pro VM/diálogo (ver `ConfiguracoesViewModel`, testável sem esta VM/sem janela
    // nenhuma). SEMPRE habilitado, mesmo espírito de `Sobre()`.
    [RelayCommand]
    private void Configuracoes()
    {
        var vm = new ConfiguracoesViewModel(
            confirmCloseAllDocuments: ConfirmCloseAll,
            startInstaller: path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }),
            shutdown: () => Application.Current.Shutdown(),
            // Task 2 (Plano 13): MESMA AppConfig desta janela (não uma nova instância) — o toggle lê/
            // grava o mesmo config.json que já governa CriarBackup/Autor/tetos de undo, e o callback
            // aplica o fator a TODO documento já aberto nesta janela (`DocumentViewModel.SupersampleFactor`
            // já tem seu próprio re-render via `OnSupersampleFactorChanged`, Task 1 — este VM só seta o
            // valor em cada aba aberta).
            config: _config,
            applySupersampleFactor: factor =>
            {
                foreach (var doc in Documents) doc.SupersampleFactor = factor;
            },
            // Plano 14 (Task 1): o toggle de tema aplica AO VIVO trocando o dicionário de tokens em
            // Application.Resources (ThemeService.AplicarNoApp) — os {DynamicResource Cor.*} re-pintam
            // toda a UI sem reiniciar. Este VM já usa Application.Current (shutdown acima), então
            // referenciar o ThemeService aqui é coerente com o padrão existente.
            aplicarTema: mPdf.App.Services.ThemeService.AplicarNoApp,
            // Plano 17 (Task 3): a opção de posição do menu de anotação aplica AO VIVO setando a flag
            // desta VM -- os bindings de Visibility da pílula flutuante e da tira do rail reagem na hora.
            aplicarPosicaoMenuAnotacao: pos => MenuAnotacaoNaBarraLateral = pos == PosicaoMenuAnotacao.BarraLateral);
        _configuracoesDialog.ShowConfiguracoesDialog(vm);
    }

    // ---- Task 9 (Plano 3a): galeria de carimbos de imagem --------------------------------------------

    // "➕ Adicionar carimbo…" (brief) — abre o diálogo injetável (exemplar: OpenFileCommand/PickPdfToOpen),
    // copia o arquivo escolhido pra dentro da galeria. Extensão fora de PNG/JPG -> StampGallery.Add
    // lança ArgumentException, notificada pt-BR (mesmo canal de qualquer outro erro deste VM); nenhuma
    // cópia acontece nesse caso.
    [RelayCommand]
    private void AddStamp()
    {
        if (_dialogs.PickImageToImport() is not { } path) return;
        try { _stampGallery.Add(path); }
        catch (ArgumentException ex) { _notifyError(ex.Message); return; }
        RefreshStampItems();
    }

    // "x"/remover (brief) — apaga o carimbo da galeria pelo nome (CommandParameter, mesmo padrão de
    // CloseDocumentCommand/OpenRecentCommand).
    [RelayCommand]
    private void RemoveStamp(string name)
    {
        _stampGallery.Remove(name);
        RefreshStampItems();
    }

    // Clique numa miniatura da galeria (brief: "click a stamp -> tool mode") — MainViewModel é quem lê
    // os bytes (StampGallery vive aqui, não em DocumentViewModel — ver doc XML do campo); a ferramenta
    // em si é ativada/desligada por DocumentViewModel.ToggleStampTool (mesmo desacoplamento de
    // ApplyEditToSelectedDocument acima: este VM decide O QUE, o documento decide COMO). Sem documento
    // selecionado: no-op (não há onde armar a ferramenta).
    [RelayCommand]
    private void SelectStamp(string name)
    {
        if (SelectedDocument is not { } doc) return;
        doc.ToggleStampTool(_stampGallery.LoadBytes(name));
    }

    // Miniatura DECODIFICADA em resolução BAIXA (DecodePixelWidth) — a galeria é só uma lista de nomes
    // pequena, decodificar em resolução total só pra um preview de ~32px seria desperdício. Congelado
    // (Freeze): construído aqui, consumido pelo binding na UI thread — mesmo padrão de
    // BitmapConverter.ToBitmapSource.
    private void RefreshStampItems()
    {
        var items = new ObservableCollection<StampGalleryItem>();
        foreach (var name in _stampGallery.Load())
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 40;
            bmp.StreamSource = new MemoryStream(_stampGallery.LoadBytes(name));
            bmp.EndInit();
            bmp.Freeze();
            items.Add(new StampGalleryItem(name, bmp));
        }
        StampItems = items;
    }
}

/// Item da galeria de carimbos pronto pro binding (Task 9, Plano 3a) — nome (identidade, usado como
/// CommandParameter de SelectStamp/RemoveStamp) + miniatura já decodificada. `sealed record`: só dados,
/// nenhum tipo do iText (ImageSource é WPF puro).
public sealed record StampGalleryItem(string Name, ImageSource Thumbnail);
