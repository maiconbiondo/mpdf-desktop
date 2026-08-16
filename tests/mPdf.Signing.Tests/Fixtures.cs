using System.IO;
using System.Text;
using iText.Kernel.Pdf;

namespace mPdf.Signing.Tests;

public static class Fixtures
{
    // sobe da pasta bin até a raiz do repo (onde está mPdf.slnx) e entra em tests/fixtures — mesmo
    // padrão de tests/mPdf.Editing.Tests/Fixtures.cs.
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

    // Task 6 (Plano 4): mesmas fixtures já usadas por mPdf.Editing.Tests/Fixtures.cs (mesmo diretório
    // tests/fixtures/, compartilhado por todos os projetos de teste) — expostas aqui pela 1ª vez porque
    // FormFillIncrementalEngineTests precisa assinar um documento COM formulário (Formulario) e provar a
    // recusa típica de XFA+assinado (XfaAssinado) sem reabrir StripSignatures (fora de escopo, ver
    // Contract.cs). "nome"/"observacoes"/"aceito" (página 0), "genero"/"estado"/"protocolo" (página 1,
    // "protocolo" é readonly), "botao" (push button, Other) e "assinatura1" (placeholder de assinatura
    // NÃO assinado, Other) — ver PdfEditorTests.ReadFormFields_FixtureFormulario_... (mPdf.Editing.Tests)
    // para o mapa completo dos 8 campos.
    public static byte[] Formulario() => File.ReadAllBytes(Path.Combine(Root, "fixture-formulario.pdf"));

    // 1 página, AcroForm com /XFA dummy + 1 campo /FT /Sig assinado de verdade — prova que
    // CanFillIncremental/SetFormFieldsIncremental checam HasXfa ANTES de tocar SignatureUtil/PdfAcroForm
    // (que lançariam PdfException: "Root element is missing", mesmo achado empírico de HasXfa/
    // GetAcroForm em mPdf.Editing/Contract.cs).
    public static byte[] XfaAssinado() => File.ReadAllBytes(Path.Combine(Root, "fixture-xfa-assinado.pdf"));

    // 1 página A4 com carimbo visível de assinatura PAdES self-signed antigo (ver
    // poc/mPdf.Poc.Signer/Signing/PadesSigner.cs) — usada para provar compat: ReadSignatures precisa
    // ler uma assinatura gerada pelo PoC, não só as que este motor gera.
    public static byte[] Carimbo() => File.ReadAllBytes(Path.Combine(Root, "fixture-carimbo.pdf"));

    // Revisão 2 (item 1): PDF criptografado (senha de USUÁRIO exigida pra abrir — não só senha de
    // dono), gerado EM MEMÓRIA via WriterProperties.SetStandardEncryption (mesma API que qualquer
    // documento real protegido por senha usaria) — não existe fixture pronta pra isso no repositório;
    // construir uma nova é mais simples e mais explícito que forjar bytes criptografados à mão. Único
    // uso do PackageReference itext direto deste projeto de teste (ver comentário no .csproj).
    public static byte[] PasswordProtected()
    {
        using var ms = new MemoryStream();
        var writerProperties = new WriterProperties().SetStandardEncryption(
            Encoding.ASCII.GetBytes("usuario123"), Encoding.ASCII.GetBytes("dono123"),
            EncryptionConstants.ALLOW_PRINTING, EncryptionConstants.ENCRYPTION_AES_256);
        using (var pdf = new PdfDocument(new PdfWriter(ms, writerProperties)))
        {
            pdf.AddNewPage();
        }
        return ms.ToArray();
    }
}
