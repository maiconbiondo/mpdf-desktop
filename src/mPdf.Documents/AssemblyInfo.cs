using System.Runtime.CompilerServices;

// Expõe DocumentSession.HandleReplaceFailure (internal) a DocumentSessionTests — a decisão de
// limpeza/resgate pós-falha de troca atômica (C1, revisão pós-Task 3 do Plano 3a) é testada direto
// contra arquivos reais em disco, sem precisar forçar uma falha genuína de I/O do SO pra exercitá-la.
[assembly: InternalsVisibleTo("mPdf.Documents.Tests")]
