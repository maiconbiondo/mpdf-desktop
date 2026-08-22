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

    // Task 1 (Plano 10 — hotfix híbrido): doc HÍBRIDO sintético (1 página, 1 campo de texto "campo1")
    // — rev1 é uma revisão clássica normal (offsets reais, iText puro) com um stream de xref construído
    // À MÃO logo em seguida, cobrindo os MESMOS objetos que a tabela clássica (é isso que faz rev1 ser
    // híbrida DE VERDADE — os mesmos dados alcançáveis por 2 caminhos, não só uma ponte vazia); rev2 é
    // uma ponte clássica de 0 objetos novos cujo trailer carrega `/Prev` (-> tabela clássica de rev1,
    // texto "xref" de verdade) E `/XRefStm` (-> o stream à mão, offset DIFERENTE) — mesma forma de
    // 2-ponteiros-distintos do contrato real do usuário (gerador temporário deletado, ver git log da
    // task-1; descoberta ao vivo documentada em task-1-report.md: uma 1ª tentativa com a ponte carregando
    // SÓ /XRefStm, sem /Prev, também reproduzia 0% no PDFium, mas por um motivo ERRADO — beco sem saída
    // pra qualquer leitor que não entenda /XRefStm, não a mesma classe de bug do contrato real).
    public static byte[] Hibrido() => File.ReadAllBytes(Path.Combine(Root, "fixture-hibrido.pdf"));

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

    // Task 1 (Plano 10, review — lacuna de corpus que escondeu o achado C1): doc xref-STREAM PURO
    // (SetFullCompressionMode), NUNCA híbrido — o formato "moderno comum" mais frequente na prática
    // (qualquer PDF escrito com compressão total, sem ponte clássica nenhuma por trás). Gerado EM
    // MEMÓRIA, mesmo padrão de PasswordProtected acima — nenhuma fixture existente cobria este caso
    // sendo assinado através do ISigningEngine completo, o que deixou passar a 1ª versão do fix desta
    // task (zerava `xrefStm` incondicionalmente, corrompendo exatamente este formato — ver
    // HybridXrefSafePdfReader.cs). 1 campo de texto "campoFC" — usado pelo teste de
    // SetFormFieldsIncremental.
    public static byte[] FullCompression()
    {
        using var ms = new MemoryStream();
        var writerProperties = new WriterProperties().SetFullCompressionMode(true);
        using (var pdf = new PdfDocument(new PdfWriter(ms, writerProperties)))
        {
            var page = pdf.AddNewPage();
            var field = new iText.Forms.Fields.TextFormFieldBuilder(pdf, "campoFC")
                .SetWidgetRectangle(new iText.Kernel.Geom.Rectangle(50, 600, 150, 20))
                .CreateText();
            field.SetValue("");
            iText.Forms.PdfAcroForm.GetAcroForm(pdf, true).AddField(field, page);
        }
        return ms.ToArray();
    }
}
