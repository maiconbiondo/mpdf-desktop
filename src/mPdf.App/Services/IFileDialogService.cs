namespace mPdf.App.Services;

public interface IFileDialogService
{
    string? PickPdfToOpen();

    /// Diálogo "Salvar como…" (Task 3, Plano 3a) — `currentPath` sugere diretório/nome iniciais.
    /// Devolve null se o usuário cancelar (mesmo contrato de PickPdfToOpen).
    string? PickPdfToSaveAs(string currentPath);

    /// Diálogo "Escolher imagem" (Task 9, Plano 3a) — escolhe UMA imagem PNG/JPG. 2 chamadores (Task 3,
    /// Plano 7): `MainViewModel.AddStamp` (copia pra dentro da `StampGallery`, carimbo persistente) e
    /// `DocumentViewModel.ToggleImageTool` ("🖼 Imagem" — carimbo AVULSO, nunca entra na galeria). Mesmo
    /// contrato de PickPdfToOpen (null = cancelado).
    string? PickImageToImport();

    /// Diálogo "Salvar como…" SEM um caminho de origem (Task 4, Plano 3b) — usado por Extrair/Juntar,
    /// que produzem um documento NOVO sem "arquivo atual" nenhum pra derivar diretório inicial (diferente
    /// de `PickPdfToSaveAs`, que sempre parte de um documento já aberto). `suggestedName` pré-preenche o
    /// nome do arquivo (extensão incluída); diretório inicial é o padrão do sistema (última pasta usada).
    /// Mesmo contrato dos outros 3 diálogos (null = cancelado).
    string? PickPdfToSave(string suggestedName);
}
