using System.Runtime.CompilerServices;
using System.Windows;

// Task 6: DocumentViewModel.ThumbnailRenderer é internal — só existe pra provar em teste que o
// Dispose fecha o SEGUNDO PdfDocumentRenderer (miniaturas), mesmo padrão já usado por
// mPdf.Rendering -> mPdf.Rendering.Tests.
[assembly: InternalsVisibleTo("mPdf.App.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
