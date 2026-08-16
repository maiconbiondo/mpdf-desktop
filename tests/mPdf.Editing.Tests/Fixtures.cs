using System.IO;

namespace mPdf.Editing.Tests;

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
    // 1 página A4 com carimbo visível de assinatura PAdES (widget do campo de assinatura) — ver
    // poc/mPdf.Poc.Signer/Signing/PadesSigner.cs (SetSignatureAppearance). Tem assinatura de verdade
    // (SignatureUtil enxerga) e NENHUMA anotação de usuário (o widget não conta — ver ReadAnnotations).
    public static byte[] Carimbo() => File.ReadAllBytes(Path.Combine(Root, "fixture-carimbo.pdf"));
    // fixture-a4.pdf + 1 highlight amarelo (NM="anotacao-fixture-1", autor "Fixture") — gerada 1x via
    // teste temporário no PoC (ver task-2-report.md); sem assinatura nenhuma.
    public static byte[] Anotada() => File.ReadAllBytes(Path.Combine(Root, "fixture-anotada.pdf"));
    // 30 páginas + outline de 3 níveis (Capítulo 1 -> Seção 1.1 -> Item 1.1.1/1.1.2, Seção 1.2;
    // Capítulo 2 -> Seção 2.1 -> Item 2.1.1; Capítulo 3; "Anexos" SEM destino de página) — gerada 1x
    // via teste temporário no PoC (ver task-5-report.md); sem assinatura nenhuma. Árvore exata
    // documentada em PdfEditorTests (seção "ReadOutline").
    public static byte[] Sumario() => File.ReadAllBytes(Path.Combine(Root, "fixture-sumario.pdf"));
    // 1 página A4 + cadeia LINEAR de 100 níveis de outline aninhado (cada nó com exatamente 1 filho,
    // todos apontando pra página 0) — gerada 1x via teste temporário no PoC (ver task-5-report.md,
    // seção "## Fix"); prova "capability-style" que PdfEditor.MaxOutlineDepth (64) realmente para de
    // descender no CAMINHO DE RECURSÃO real, não só numa função de decisão isolada.
    public static byte[] OutlineProfundo() => File.ReadAllBytes(Path.Combine(Root, "fixture-outline-profundo.pdf"));

    // 1x1 PNG vermelho hardcoded em base64 (Task 9, Plano 3a, brief) — ~90 bytes, grande o bastante pra
    // pintar pixels distintos do fundo branco quando escalado pro bbox de um ImageStamp. Verificado
    // empiricamente (probe project, ver task-9-report.md): ImageDataFactory.Create decodifica sem erro.
    public static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    // 2 páginas com AcroForm (Task 1, Plano 3c): página 0 = texto "nome" ("Fulano de Tal"), texto
    // multilinha "observacoes" ("linha 1\nlinha 2"), checkbox "aceito" (desmarcado, Options=[Yes]);
    // página 1 = radio "genero" (2 opções M/F, valor M), combo "estado" (3 opções SP/RJ/MG, valor
    // RJ), texto readonly "protocolo" (valor "PROTO-12345"). Sem assinatura, sem /XFA. Gerada 1x via
    // teste temporário no PoC (ver task-1-report.md); mesmo protocolo de fixture-anotada/fixture-sumario.
    public static byte[] Formulario() => File.ReadAllBytes(Path.Combine(Root, "fixture-formulario.pdf"));
    // 1 página, AcroForm com entrada /XFA dummy (array vazio) — só pra HasXfa detectar a CHAVE, o
    // conteúdo não é XML válido de propósito (ver ACHADO EMPÍRICO em Contract.cs: PdfAcroForm.
    // GetAcroForm relendo esse doc via a API de forms LANÇA — HasXfa nunca instancia PdfAcroForm).
    public static byte[] Xfa() => File.ReadAllBytes(Path.Combine(Root, "fixture-xfa.pdf"));
    // 1 página, AcroForm com /XFA dummy (mesmo protocolo de Xfa() acima) + 1 campo /FT /Sig
    // ("assinatura1") com /V presente (dicionário mínimo, SEM criptografia real — não abre com um
    // leitor de assinatura de verdade, só exercita o detector RAW) — Important 2 (revisão do Task 2,
    // Plano 3c): prova que HasSignatures reconhece um doc XFA-e-assinado sem instanciar
    // PdfAcroForm/SignatureUtil (que lançariam, mesmo achado empírico de HasXfa). Gerada 1x via teste
    // temporário no PoC (mPdf.Poc.Signer.Tests/TempXfaSignedFixtureGenerator.cs, apagado depois).
    public static byte[] XfaAssinado() => File.ReadAllBytes(Path.Combine(Root, "fixture-xfa-assinado.pdf"));
}
