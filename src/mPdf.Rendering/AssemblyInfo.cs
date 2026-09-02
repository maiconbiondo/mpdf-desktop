using System.Runtime.CompilerServices;

// Expõe FindInText (internal) a PdfTextSearchTests: acento-insensibilidade é testada direto contra
// o núcleo do comparador com strings sintéticas, já que nenhuma fixture PDF real tem acento
// (ver PdfTextSearch.FindInText e o teste FindInText_AccentInsensitive_PaginaMatchesPaginaAcentuada).
[assembly: InternalsVisibleTo("mPdf.Rendering.Tests")]
