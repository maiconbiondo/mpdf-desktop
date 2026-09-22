using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime;
using mPdf.Rendering;

// Stress: reproduce the intermittent AV in FPDFDOC_ExitFormFillEnvironment.
// Theory: FPDF_FORMFILLINFO is a movable managed object whose pointer PDFium retains
// after FPDFDOC_InitFormFillEnvironment; a compacting GC between the first annotated
// render (lazy init) and Dispose (Exit) leaves PDFium dereferencing a dangling pointer.
// Sequence per iteration: create renderer -> render (init form env) -> force compacting
// GC + heap churn (reuse the old address) -> dispose (Exit reads stale memory).
//
// Ver tools/pdfium-stress/README.md (pt-BR) para o CONTRATO DE ACEITAÇÃO deste probe (deve travar
// hoje, em Docnet.Core 2.6.0; deve sobreviver 40+ iterações depois do fix pendente).

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
        dir = dir.Parent;
    return dir?.FullName
        ?? throw new InvalidOperationException("mPdf.slnx não encontrado a partir de " + AppContext.BaseDirectory);
}

string root = RepoRoot();
string fixture = args.Length > 1 && args[1] == "a4"
    ? Path.Combine(root, "tests", "fixtures", "fixture-a4.pdf")
    : Path.Combine(root, "poc", "samples", "teste-carimbo.pdf");
var bytes = File.ReadAllBytes(fixture);
int iterations = args.Length > 0 ? int.Parse(args[0]) : 300;
bool doRender = !args.Contains("norender");
bool doGc = !args.Contains("nogc");
Console.WriteLine($"fixture={Path.GetFileName(fixture)} render={doRender} gc={doGc}");

void Log(string msg) { Console.WriteLine(msg); Console.Out.Flush(); }

var junk = new List<byte[]>();
for (int i = 1; i <= iterations; i++)
{
    Log($"[{i}] create");
    var r = new PdfDocumentRenderer(bytes);
    if (doRender)
    {
        Log($"[{i}] render");
        r.RenderPage(0, 1.0); // RenderAnnotations -> lazy FPDFDOC_InitFormFillEnvironment
    }

    if (doGc)
    {
        Log($"[{i}] gc-churn");
        // Force object movement + address reuse between Init and Exit:
        junk.Clear();
        for (int j = 0; j < 20000; j++) junk.Add(new byte[64]); // churn gen0/gen1
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        for (int j = 0; j < 20000; j++) junk.Add(new byte[64]); // reuse freed space
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    Log($"[{i}] dispose");
    r.Dispose(); // FPDF_ExitFormFillEnvironment -> dereferences possibly-stale pointer
    Log($"[{i}] ok");
}
Log("SURVIVED all iterations — no AV reproduced");
