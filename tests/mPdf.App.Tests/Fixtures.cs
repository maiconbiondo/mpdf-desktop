using System.IO;

namespace mPdf.App.Tests;

public static class Fixtures
{
    // sobe da pasta bin até a raiz do repo (onde está mPdf.slnx) e entra em tests/fixtures
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mPdf.slnx")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "tests", "fixtures");
        }
    }
    public static byte[] A4() => File.ReadAllBytes(Path.Combine(Root, "fixture-a4.pdf"));
    public static byte[] ThirtyPages() => File.ReadAllBytes(Path.Combine(Root, "fixture-30p.pdf"));
    // 1 página A4, SEM nenhum texto (nem invisível) — ver mesmo comentário em mPdf.Rendering.Tests.Fixtures.
    public static byte[] NoText() => File.ReadAllBytes(Path.Combine(Root, "fixture-sem-texto.pdf"));

    // 1x1 PNG vermelho hardcoded em base64 (Task 9, Plano 3a, brief) — mesmo arquivo do exemplar em
    // mPdf.Editing.Tests.Fixtures.OnePixelPng (verificado empiricamente: BitmapDecoder decodifica sem
    // erro, PixelWidth=PixelHeight=1).
    public static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    // Formulário AcroForm (Task 1, Plano 3c) — mesma fixture/mesmo shape de mPdf.Editing.Tests.Fixtures.Formulario():
    // texto/multilinha/checkbox (página 0), radio/combo/readonly (página 1) + push button/assinatura (Other).
    public static byte[] Formulario() => File.ReadAllBytes(Path.Combine(Root, "fixture-formulario.pdf"));
    // AcroForm com /XFA dummy (Task 1, Plano 3c) — só pra HasXfa detectar a CHAVE (ver mPdf.Editing.Tests.Fixtures.Xfa()).
    public static byte[] Xfa() => File.ReadAllBytes(Path.Combine(Root, "fixture-xfa.pdf"));

    // Foto/FotoExif90 (Task 3, Plano 7 — "🖼 Imagem", px integration): MESMOS arquivos de
    // mPdf.Editing.Tests.Fixtures.Foto()/FotoExif90() (tests/fixtures/ é compartilhado entre projetos
    // de teste) — cantos de cor conhecida (TL=vermelho, TR=verde/lime, BL=azul, BR=amarelo);
    // FotoExif90 tem os MESMOS pixels pré-rotacionados 90° CCW + EXIF Orientation=6, usada pra provar
    // que ToggleImageTool corrige a rotação ANTES de colocar (renderiza EM PÉ, mesmas cores de canto).
    public static byte[] Foto() => File.ReadAllBytes(Path.Combine(Root, "fixture-foto.jpg"));
    public static byte[] FotoExif90() => File.ReadAllBytes(Path.Combine(Root, "fixture-foto-exif90.jpg"));
    // XFA dummy + 1 campo /FT /Sig com /V presente (Important 2, revisão do Task 2, Plano 3c) — ver
    // mPdf.Editing.Tests.Fixtures.XfaAssinado(): prova que HasSignatures reconhece o doc como assinado
    // (banner de assinado) mesmo sendo XFA, sem lançar — CanEdit continua falso via o gate IsXfaForm.
    public static byte[] XfaAssinado() => File.ReadAllBytes(Path.Combine(Root, "fixture-xfa-assinado.pdf"));
}
