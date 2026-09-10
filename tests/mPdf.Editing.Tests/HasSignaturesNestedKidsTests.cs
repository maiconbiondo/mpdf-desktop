using iText.Kernel.Pdf;

namespace mPdf.Editing.Tests;

/// <summary>
/// Rodada de fechamento final (Plano 3c) — promove a sonda da revisão final a teste permanente: o
/// ramo RECURSIVO de <c>CountSignaturesInFieldsArray</c> (PdfEditor.cs — alcançado só quando um item
/// de <c>/AcroForm/Fields</c> NÃO tem <c>/FT</c> mas TEM <c>/Kids</c>, isto é, um grupo de campo
/// aninhado, ex.: radio button com widgets-filhos) estava em 0% de cobertura: nenhum fixture
/// existente (fixture-xfa/fixture-xfa-assinado) tem essa forma — os campos de assinatura ali são
/// sempre TERMINAIS, direto no array raiz de <c>/Fields</c>.
///
/// Construído 100% EM MEMÓRIA via iText baixo nível (sem fixture nova em tests/fixtures) porque só um
/// AcroForm malformado de propósito exercita esse ramo. Mesma classe de risco de fixture-xfa.pdf/
/// fixture-xfa-assinado.pdf (ver Fixtures.cs): a entrada <c>/XFA</c> dummy (array vazio) força
/// <c>HasXfaKey(doc) == true</c>, o que desvia <c>CountSignatures</c> do caminho normal
/// (<c>SignatureUtil</c>) pro caminho CRU (<c>CountSignaturesRaw</c>/<c>CountSignaturesInFieldsArray</c>)
/// — sem isso, este teste exercitaria o caminho ERRADO (SignatureUtil, que já tem cobertura própria
/// via fixture-carimbo) e o ramo recursivo continuaria em 0%.
///
/// Este é o único arquivo de <c>mPdf.Editing.Tests</c> que referencia iText diretamente (ver
/// PackageReference dedicada em mPdf.Editing.Tests.csproj) — necessário só pra montar a estrutura de
/// dicionário CRUA que o teste precisa; a asserção em si passa exclusivamente pela API pública
/// <see cref="IPdfEditor.HasSignatures"/>, nunca por um tipo do iText.
/// </summary>
public class HasSignaturesNestedKidsTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    /// Constrói, em memória, 1 página + AcroForm com /XFA dummy (mesmo protocolo de fixture-xfa.pdf)
    /// e /Fields = [ campo PAI sem /FT, com /Kids = [ campo FILHO terminal com /FT /Sig (e /V só
    /// quando <paramref name="includeV"/>) ] ] — a forma exata que o brief pede: um nó de grupo SEM
    /// /FT cujo /Kids guarda o campo terminal de assinatura.
    private static byte[] BuildXfaWithNestedSignatureField(bool includeV)
    {
        using var ms = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(ms)))
        {
            pdf.AddNewPage();

            var child = new PdfDictionary(); // campo TERMINAL: /FT /Sig, /V presente só se includeV
            child.Put(PdfName.FT, PdfName.Sig);
            if (includeV) child.Put(PdfName.V, new PdfDictionary());

            var parent = new PdfDictionary(); // grupo de campo: SEM /FT — só /Kids
            var kids = new PdfArray();
            kids.Add(child);
            parent.Put(PdfName.Kids, kids);

            var fields = new PdfArray();
            fields.Add(parent);

            var acroForm = new PdfDictionary();
            acroForm.Put(PdfName.XFA, new PdfArray()); // dummy vazio — mesmo protocolo de fixture-xfa.pdf
            acroForm.Put(PdfName.Fields, fields);

            pdf.GetCatalog().GetPdfObject().Put(PdfName.AcroForm, acroForm);
        }
        return ms.ToArray();
    }

    [Fact] // filho terminal do /Kids aninhado TEM /V -> conta como assinatura -> HasSignatures ==
    // true, sem lançar (prova que a recursão desce até o filho e o encontra).
    public void HasSignatures_XfaWithNestedKidsSignatureField_WithV_IsTrueWithoutThrowing()
    {
        var pdf = BuildXfaWithNestedSignatureField(includeV: true);

        var ex = Record.Exception(() => Editor.HasSignatures(pdf));

        Assert.Null(ex);
        Assert.True(Editor.HasSignatures(pdf));
    }

    [Fact] // controle negativo: MESMA estrutura aninhada (/Kids desce até o mesmo campo /FT /Sig),
    // mas SEM /V (placeholder vazio, não assinado de fato) -> não conta -> HasSignatures == false.
    // Prova que é a presença de /V que decide, não a recursão em si.
    public void HasSignatures_XfaWithNestedKidsSignatureField_WithoutV_IsFalse()
    {
        var pdf = BuildXfaWithNestedSignatureField(includeV: false);

        Assert.False(Editor.HasSignatures(pdf));
    }
}
