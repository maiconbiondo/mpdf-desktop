using mPdf.Rendering;

namespace mPdf.Editing.Tests;

public class PdfEditorTests
{
    private static IPdfEditor Editor => PdfEditorFactory.Create();

    // --- HasSignatures ---------------------------------------------------

    [Fact] // fixture-carimbo tem 1 assinatura PAdES de verdade (ver PadesSigner no PoC)
    public void HasSignatures_FixtureCarimbo_IsTrue()
    {
        Assert.True(Editor.HasSignatures(Fixtures.Carimbo()));
    }

    [Fact] // fixture-a4 nunca foi assinada
    public void HasSignatures_FixtureA4_IsFalse()
    {
        Assert.False(Editor.HasSignatures(Fixtures.A4()));
    }

    // Important 2 (revisão do Task 2, Plano 3c) — ACHADO REAL: HasSignatures (via SignatureUtil, que
    // aciona PdfAcroForm.GetAcroForm internamente) lançava PdfEditingException em QUALQUER documento
    // com /XFA (mesma falha empírica documentada em HasXfa/Contract.cs — "Root element is missing" ao
    // parsear /XFA malformado/dummy) — 5 call sites diferentes no App dependiam disso indiretamente
    // (OpenPath, Merge, Split, ExtractSelected, Insert), não só a checagem de assinatura na abertura.
    // Fix: quando o dicionário CRU do AcroForm tem /XFA, HasSignatures (via CountSignatures) faz uma
    // varredura RAW de /Fields por /FT /Sig com /V presente, SEM instanciar PdfAcroForm/SignatureUtil.

    [Fact] // doc XFA SEM assinatura nenhuma -> false, SEM lançar (antes do fix: lançava sempre).
    public void HasSignatures_FixtureXfa_IsFalse_DoesNotThrow()
    {
        Assert.False(Editor.HasSignatures(Fixtures.Xfa()));
    }

    [Fact] // doc XFA COM 1 campo /FT /Sig e /V presente -> true, SEM lançar — a varredura RAW encontra
    // a assinatura sem precisar de SignatureUtil/PdfAcroForm.
    public void HasSignatures_FixtureXfaAssinado_IsTrue_DoesNotThrow()
    {
        Assert.True(Editor.HasSignatures(Fixtures.XfaAssinado()));
    }

    [Fact] // GuardAgainstSignedDocument (usado por RotatePages/DeletePages/MovePage/InsertPages/
    // AddAnnotation/RemoveAnnotation/SetFormFields/FlattenForm — TODO mutador gateado) também usa
    // CountSignatures por baixo — recusa um doc XFA-e-assinado com o MESMO PdfSignedDocumentException
    // tipado de qualquer outro doc assinado, sem lançar por causa do /XFA no meio do caminho.
    public void RotatePages_XfaAssinado_ThrowsPdfSignedDocumentException_NotGenericXfaFailure()
    {
        Assert.Throws<PdfSignedDocumentException>(() => Editor.RotatePages(Fixtures.XfaAssinado(), new[] { 0 }, 90));
    }

    [Fact] // Important 2, item (d) — VEREDITO pra ExtractPages (SEM GATE, "ler pra compor"): num doc XFA
    // SEM assinatura, HasSignatures agora devolve false SEM lançar -> ExtractPages pula StripSignatures
    // na origem (curto-circuito já existente, `HasSignatures(pdf) ? StripSignatures(pdf) : pdf`) ->
    // CopyPagesTo nunca carrega /AcroForm/XFA pro destino (mesmo achado do Task 1/relatório da Task 2) ->
    // FUNCIONA de ponta a ponta, sem lançar.
    public void ExtractPages_UnsignedXfaFixture_WorksEndToEnd_NoThrow()
    {
        var result = Editor.ExtractPages(Fixtures.Xfa(), new[] { 0 });

        Assert.False(Editor.HasSignatures(result));
        Assert.False(Editor.HasXfa(result)); // saída não carrega /XFA (AcroForm não é copiado por página)
    }

    [Fact] // Important 2, item (d) — VEREDITO pra ExtractPages num doc XFA-E-ASSINADO: `HasSignatures`
    // (já corrigido) devolve TRUE sem lançar, então ExtractPages TENTA `StripSignatures(pdf)` na
    // ORIGEM antes de copiar — `StripSignatures` (gap RESIDUAL, fora do escopo deste fix: instancia
    // `SignatureUtil` DIRETO, sem passar pelo `CountSignatures`/`HasXfaKey` corrigido) ainda lança nesse
    // caso específico. Documentado, não escondido — nenhum teste deste Task 2 promete resolver
    // XFA-E-assinado além de HasSignatures/GuardAgainstSignedDocument em si (que já ficam corretos,
    // ver RotatePages_XfaAssinado acima); achatamento/extração de um doc XFA JÁ assinado permanece um
    // caso não coberto, registrado aqui como conhecido.
    public void ExtractPages_XfaAssinadoFixture_StillThrows_KnownResidualGap()
    {
        Assert.Throws<PdfEditingException>(() => Editor.ExtractPages(Fixtures.XfaAssinado(), new[] { 0 }));
    }

    // --- ReadAnnotations ---------------------------------------------------

    [Fact] // o carimbo visível é um WIDGET (campo de assinatura) — EXCLUÍDO: não é anotação de usuário
    public void ReadAnnotations_FixtureCarimbo_ExcludesWidget()
    {
        Assert.Empty(Editor.ReadAnnotations(Fixtures.Carimbo()));
    }

    [Fact] // fixture-anotada tem 1 highlight de usuário (NM=anotacao-fixture-1, autor Fixture, amarelo)
    public void ReadAnnotations_FixtureAnotada_ReturnsHighlight()
    {
        var annotations = Editor.ReadAnnotations(Fixtures.Anotada());

        var a = Assert.Single(annotations);
        Assert.Equal("anotacao-fixture-1", a.Id);
        Assert.Equal(AnnotationKind.Highlight, a.Kind);
        Assert.Equal(0, a.PageIndex);
        Assert.Equal("Fixture", a.Author);
        Assert.Equal(0xFFFFFF00u, a.ColorArgb); // amarelo opaco: A=FF R=FF G=FF B=00
        Assert.NotNull(a.Quads);
        Assert.Single(a.Quads!);
    }

    [Fact] // PDF sem nenhuma anotação -> lista vazia (não deve lançar)
    public void ReadAnnotations_FixtureA4_ReturnsEmpty()
    {
        Assert.Empty(Editor.ReadAnnotations(Fixtures.A4()));
    }

    // --- AddAnnotation (Highlight) ------------------------------------------

    [Fact]
    public void AddAnnotation_Highlight_RoundTrips()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Highlight,
            PageIndex = 0,
            LeftPt = 100, BottomPt = 700, RightPt = 250, TopPt = 715,
            ColorArgb = 0xFFFFFF00, // amarelo
            Content = "conteudo de teste",
            Author = "Autor de Teste",
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var read = Editor.ReadAnnotations(result);

        var a = Assert.Single(read);
        Assert.False(string.IsNullOrWhiteSpace(a.Id)); // Id não informado -> Add gera um GUID como NM
        Assert.Equal(AnnotationKind.Highlight, a.Kind);
        Assert.Equal(0, a.PageIndex);
        Assert.Equal(100, a.LeftPt, 0.5);
        Assert.Equal(700, a.BottomPt, 0.5);
        Assert.Equal(250, a.RightPt, 0.5);
        Assert.Equal(715, a.TopPt, 0.5);
        Assert.Equal(0xFFFFFF00u, a.ColorArgb);
        Assert.Equal("conteudo de teste", a.Content);
        Assert.Equal("Autor de Teste", a.Author);
    }

    [Fact] // pós-I5: Id informado pelo chamador vira o /NM — estabilidade de Id entre chamadas (Task 7)
    public void AddAnnotation_WithExplicitId_ReadReturnsSameId()
    {
        var data = new AnnotationData
        {
            Id = "id-escolhido-pelo-chamador",
            Kind = AnnotationKind.Highlight,
            PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 50, TopPt = 20,
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.Equal("id-escolhido-pelo-chamador", a.Id);
    }

    [Fact] // pós-M6: 2 quads na entrada -> 2 quads na saída, valores preservados (não só 1)
    public void AddAnnotation_MultipleQuads_RoundTrips()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Highlight,
            PageIndex = 0,
            // bbox precisa cobrir os 2 quads — CreateHighLight não recalcula a partir dos quads.
            LeftPt = 50, BottomPt = 690, RightPt = 300, TopPt = 715,
            ColorArgb = 0xFFFFFF00,
            Quads = new[]
            {
                new PdfQuad(50, 700, 150, 715),  // 1ª linha de texto "destacada"
                new PdfQuad(50, 690, 300, 705),  // 2ª linha, mais larga
            },
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.NotNull(a.Quads);
        Assert.Equal(2, a.Quads!.Count);
        var q1 = a.Quads.Single(q => q.RightPt < 200); // o quad(50,700,150,715): distingue pelo Right menor
        var q2 = a.Quads.Single(q => q.RightPt > 200); // o quad(50,690,300,705): Right maior
        Assert.Equal(50, q1.LeftPt, 0.5); Assert.Equal(700, q1.BottomPt, 0.5);
        Assert.Equal(150, q1.RightPt, 0.5); Assert.Equal(715, q1.TopPt, 0.5);
        Assert.Equal(50, q2.LeftPt, 0.5); Assert.Equal(690, q2.BottomPt, 0.5);
        Assert.Equal(300, q2.RightPt, 0.5); Assert.Equal(705, q2.TopPt, 0.5);
    }

    [Fact] // Task 6 (Plano 3a): mesma mecânica de QuadPoints de Highlight, subtype/fábrica diferente —
    // exemplar AddAnnotation_Highlight_RoundTrips, com Quads (não só o bbox) pra provar o caminho real
    // que DocumentViewModel.ApplyMarkupCommand usa (seleção -> quads em pontos, não um retângulo único).
    public void AddAnnotation_Underline_RoundTrips()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Underline,
            PageIndex = 0,
            LeftPt = 100, BottomPt = 700, RightPt = 250, TopPt = 715,
            ColorArgb = 0xFFFFFF00, // amarelo
            Author = "Autor de Teste",
            Quads = new[] { new PdfQuad(100, 700, 250, 715) },
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.Equal(AnnotationKind.Underline, a.Kind);
        Assert.Equal(0, a.PageIndex);
        Assert.Equal(0xFFFFFF00u, a.ColorArgb);
        Assert.Equal("Autor de Teste", a.Author);
        Assert.NotNull(a.Quads);
        var q = Assert.Single(a.Quads!);
        Assert.Equal(100, q.LeftPt, 0.5); Assert.Equal(700, q.BottomPt, 0.5);
        Assert.Equal(250, q.RightPt, 0.5); Assert.Equal(715, q.TopPt, 0.5);
    }

    [Fact] // Task 6 (Plano 3a): espelho exato do teste de Underline acima, subtype Strikeout.
    public void AddAnnotation_Strikeout_RoundTrips()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Strikeout,
            PageIndex = 0,
            LeftPt = 50, BottomPt = 600, RightPt = 300, TopPt = 620,
            ColorArgb = 0xFFFF5555, // vermelho
            Author = "Autor de Teste",
            Quads = new[] { new PdfQuad(50, 600, 300, 620) },
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.Equal(AnnotationKind.Strikeout, a.Kind);
        Assert.Equal(0, a.PageIndex);
        Assert.Equal(0xFFFF5555u, a.ColorArgb);
        Assert.Equal("Autor de Teste", a.Author);
        Assert.NotNull(a.Quads);
        var q = Assert.Single(a.Quads!);
        Assert.Equal(50, q.LeftPt, 0.5); Assert.Equal(600, q.BottomPt, 0.5);
        Assert.Equal(300, q.RightPt, 0.5); Assert.Equal(620, q.TopPt, 0.5);
    }

    [Fact] // pós-M7: ColorArgb=null -> NENHUM /C escrito -> lido de volta como null (não preto)
    public void AddAnnotation_NullColor_RoundTripsAsNoColor()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Highlight,
            PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 50, TopPt = 20,
            ColorArgb = null,
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.Null(a.ColorArgb);
    }

    // --- AddAnnotation (StickyNote/FreeText, Task 7, Plano 3a) --------------------------------------

    [Fact] // exemplar AddAnnotation_Highlight_RoundTrips — StickyNote é um /Text (PdfTextAnnotation),
    // sem Quads (subtype de "ícone posicionado num ponto", não de marcação de texto).
    public void AddAnnotation_StickyNote_RoundTrips()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.StickyNote,
            PageIndex = 0,
            LeftPt = 100, BottomPt = 700, RightPt = 120, TopPt = 720,
            ColorArgb = 0xFFFFFF00, // amarelo: cor do ÍCONE (brief)
            Content = "nota de teste",
            Author = "Autor de Teste",
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.False(string.IsNullOrWhiteSpace(a.Id));
        Assert.Equal(AnnotationKind.StickyNote, a.Kind);
        Assert.Equal(0, a.PageIndex);
        Assert.Equal(100, a.LeftPt, 0.5); Assert.Equal(700, a.BottomPt, 0.5);
        Assert.Equal(120, a.RightPt, 0.5); Assert.Equal(720, a.TopPt, 0.5);
        Assert.Equal(0xFFFFFF00u, a.ColorArgb);
        Assert.Equal("nota de teste", a.Content);
        Assert.Equal("Autor de Teste", a.Author);
    }

    [Fact] // mesmo sentinela de cor (pós-M7) aplicado a StickyNote: null -> nenhum /C -> lido como null
    public void AddAnnotation_StickyNote_NullColor_RoundTripsAsNoColor()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.StickyNote,
            PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30,
            ColorArgb = null,
            Content = "sem cor",
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.Null(a.ColorArgb);
        Assert.Equal("sem cor", a.Content);
    }

    [Fact] // FreeText: PdfFreeTextAnnotation — o ctor do iText EXIGE um PdfString de conteúdo (ver
    // reflexão em PdfEditor.BuildAnnotation), então este teste cobre o caminho "com Content" — o
    // caminho "Content nulo" tem um resíduo documentado ali (não é coberto aqui de propósito).
    public void AddAnnotation_FreeText_RoundTrips()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.FreeText,
            PageIndex = 0,
            LeftPt = 100, BottomPt = 600, RightPt = 300, TopPt = 660,
            ColorArgb = 0xFF3355FF, // azul: cor do TEXTO (brief)
            Content = "caixa de texto de teste",
            Author = "Autor de Teste",
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.False(string.IsNullOrWhiteSpace(a.Id));
        Assert.Equal(AnnotationKind.FreeText, a.Kind);
        Assert.Equal(0, a.PageIndex);
        Assert.Equal(100, a.LeftPt, 0.5); Assert.Equal(600, a.BottomPt, 0.5);
        Assert.Equal(300, a.RightPt, 0.5); Assert.Equal(660, a.TopPt, 0.5);
        Assert.Equal(0xFF3355FFu, a.ColorArgb);
        Assert.Equal("caixa de texto de teste", a.Content);
        Assert.Equal("Autor de Teste", a.Author);
    }

    [Fact] // mesmo sentinela de cor aplicado a FreeText: null -> nenhum /C -> lido como null (a caixa
    // ainda ganha um /DA com fonte/tamanho fixos, mas SEM operador de cor — ver BuildAnnotation)
    public void AddAnnotation_FreeText_NullColor_RoundTripsAsNoColor()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.FreeText,
            PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 210, TopPt = 70,
            ColorArgb = null,
            Content = "sem cor",
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.Null(a.ColorArgb);
        Assert.Equal("sem cor", a.Content);
    }

    [Fact] // pós-item 2a (Id explícito): mesma estabilidade de Id provada para Highlight já vale para
    // StickyNote/FreeText — o lift (Task 7: Remove+Add mesmo Id) depende disso para os 2 tipos novos.
    public void AddAnnotation_StickyNoteWithExplicitId_ReadReturnsSameId()
    {
        var data = new AnnotationData
        {
            Id = "nota-id-escolhido",
            Kind = AnnotationKind.StickyNote,
            PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 30, TopPt = 30,
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.Equal("nota-id-escolhido", a.Id);
    }

    [Fact] // honesto: só garante que um highlight num doc SEM assinatura não cria uma assinatura fantasma
    public void AddAnnotation_OnUnsignedDocument_DoesNotCreatePhantomSignature()
    {
        var data = new AnnotationData { Kind = AnnotationKind.Highlight, PageIndex = 0 };
        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        Assert.False(Editor.HasSignatures(result));
    }

    [Fact] // pós-I3, tipada na rodada 2: doc com assinatura -> PdfSignedDocumentException (não mais
           // InvalidOperationException genérica) — a Task 5 vai capturar ESTE tipo especificamente
           // para oferecer "Editar uma cópia"; o tipo já É o canal, sem precisar checar a mensagem.
    public void AddAnnotation_OnSignedDocument_Throws()
    {
        var data = new AnnotationData { Kind = AnnotationKind.Highlight, PageIndex = 0 };
        Assert.Throws<PdfSignedDocumentException>(() => Editor.AddAnnotation(Fixtures.Carimbo(), data));
    }

    // --- AddAnnotation (Ink/Rectangle/Line/Arrow, Task 8, Plano 3a) --------------------------------

    [Fact] // exemplar AddAnnotation_Highlight_RoundTrips — Ink é /InkList (array de arrays), sem Quads.
    // 2 traços (não só 1) pra provar que o round-trip preserva CADA traço, não só o primeiro.
    public void AddAnnotation_Ink_RoundTrips()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Ink,
            PageIndex = 0,
            LeftPt = 100, BottomPt = 100, RightPt = 250, TopPt = 260,
            ColorArgb = 0xFF3355FF,
            Author = "Autor de Teste",
            InkStrokes = new IReadOnlyList<PdfPoint>[]
            {
                new[] { new PdfPoint(100, 100), new PdfPoint(150, 200), new PdfPoint(200, 150) },
                new[] { new PdfPoint(220, 240), new PdfPoint(250, 260) },
            },
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.False(string.IsNullOrWhiteSpace(a.Id));
        Assert.Equal(AnnotationKind.Ink, a.Kind);
        Assert.Equal(0, a.PageIndex);
        Assert.Equal(100, a.LeftPt, 0.5); Assert.Equal(100, a.BottomPt, 0.5);
        Assert.Equal(250, a.RightPt, 0.5); Assert.Equal(260, a.TopPt, 0.5);
        Assert.Equal(0xFF3355FFu, a.ColorArgb);
        Assert.Equal("Autor de Teste", a.Author);

        Assert.NotNull(a.InkStrokes);
        Assert.Equal(2, a.InkStrokes!.Count);
        var s1 = a.InkStrokes[0];
        Assert.Equal(3, s1.Count);
        Assert.Equal(100, s1[0].XPt, 0.5); Assert.Equal(100, s1[0].YPt, 0.5);
        Assert.Equal(150, s1[1].XPt, 0.5); Assert.Equal(200, s1[1].YPt, 0.5);
        Assert.Equal(200, s1[2].XPt, 0.5); Assert.Equal(150, s1[2].YPt, 0.5);
        var s2 = a.InkStrokes[1];
        Assert.Equal(2, s2.Count);
        Assert.Equal(220, s2[0].XPt, 0.5); Assert.Equal(240, s2[0].YPt, 0.5);
        Assert.Equal(250, s2[1].XPt, 0.5); Assert.Equal(260, s2[1].YPt, 0.5);
    }

    [Fact] // Ink sem NENHUM traço -> ArgumentException, antes de tocar o PDF (mesmo espírito do Id vazio)
    public void AddAnnotation_Ink_NoStrokes_Throws()
    {
        var data = new AnnotationData { Kind = AnnotationKind.Ink, PageIndex = 0 };
        Assert.Throws<ArgumentException>(() => Editor.AddAnnotation(Fixtures.A4(), data));
    }

    [Fact] // Rectangle: /Square, geometria é só o bbox (Left/Bottom/Right/Top) — sem Quads, sem array próprio.
    public void AddAnnotation_Rectangle_RoundTrips()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Rectangle,
            PageIndex = 0,
            LeftPt = 50, BottomPt = 500, RightPt = 250, TopPt = 600,
            ColorArgb = 0xFFFF5555,
            Author = "Autor de Teste",
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.False(string.IsNullOrWhiteSpace(a.Id));
        Assert.Equal(AnnotationKind.Rectangle, a.Kind);
        Assert.Equal(0, a.PageIndex);
        Assert.Equal(50, a.LeftPt, 0.5); Assert.Equal(500, a.BottomPt, 0.5);
        Assert.Equal(250, a.RightPt, 0.5); Assert.Equal(600, a.TopPt, 0.5);
        Assert.Equal(0xFFFF5555u, a.ColorArgb);
        Assert.Equal("Autor de Teste", a.Author);
    }

    [Fact] // Line: /L [x1 y1 x2 y2] — lido de volta como LineStartPt/LineEndPt, kind continua Line (sem /LE).
    public void AddAnnotation_Line_RoundTrips()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Line,
            PageIndex = 0,
            LeftPt = 30, BottomPt = 40, RightPt = 300, TopPt = 320,
            LineStartPt = new PdfPoint(30, 40),
            LineEndPt = new PdfPoint(300, 320),
            ColorArgb = 0xFF000000,
            Author = "Autor de Teste",
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.Equal(AnnotationKind.Line, a.Kind); // NÃO Arrow — sem /LE
        Assert.NotNull(a.LineStartPt); Assert.NotNull(a.LineEndPt);
        Assert.Equal(30, a.LineStartPt!.Value.XPt, 0.5); Assert.Equal(40, a.LineStartPt.Value.YPt, 0.5);
        Assert.Equal(300, a.LineEndPt!.Value.XPt, 0.5); Assert.Equal(320, a.LineEndPt.Value.YPt, 0.5);
        Assert.Equal(0xFF000000u, a.ColorArgb);
    }

    [Fact] // Line sem os 2 pontos -> ArgumentException, antes de tocar o PDF.
    public void AddAnnotation_Line_MissingEndpoints_Throws()
    {
        var data = new AnnotationData { Kind = AnnotationKind.Line, PageIndex = 0 };
        Assert.Throws<ArgumentException>(() => Editor.AddAnnotation(Fixtures.A4(), data));
    }

    [Fact] // Arrow: mesmo /L de Line, mas com /LE=[None,OpenArrow] — MapKind lê de volta como Arrow, NÃO Line.
    public void AddAnnotation_Arrow_RoundTrips_AsArrowKind()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.Arrow,
            PageIndex = 0,
            LeftPt = 30, BottomPt = 40, RightPt = 300, TopPt = 320,
            LineStartPt = new PdfPoint(30, 40),
            LineEndPt = new PdfPoint(300, 320),
            ColorArgb = 0xFF00AA00,
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.Equal(AnnotationKind.Arrow, a.Kind);
        Assert.NotNull(a.LineStartPt); Assert.NotNull(a.LineEndPt);
        Assert.Equal(30, a.LineStartPt!.Value.XPt, 0.5); Assert.Equal(40, a.LineStartPt.Value.YPt, 0.5);
        Assert.Equal(300, a.LineEndPt!.Value.XPt, 0.5); Assert.Equal(320, a.LineEndPt.Value.YPt, 0.5);
    }

    [Fact] // Arrow sem os 2 pontos -> ArgumentException (mesma checagem de Line acima, mesmo kind por baixo).
    public void AddAnnotation_Arrow_MissingEndpoints_Throws()
    {
        var data = new AnnotationData { Kind = AnnotationKind.Arrow, PageIndex = 0 };
        Assert.Throws<ArgumentException>(() => Editor.AddAnnotation(Fixtures.A4(), data));
    }

    [Fact] // exemplar AddAnnotation_Highlight_RoundTrips — ImageStamp é um /Stamp com aparência CUSTOM
    // (a imagem), sem Quads/InkStrokes/LineStart-EndPt. DECISÃO DE DESIGN (ver doc XML de
    // AnnotationData.ImageBytes): ReadAnnotations SEMPRE devolve ImageBytes null, mesmo pra um /Stamp
    // recém-criado por este módulo — bbox/autor/cor/Id continuam preservados no round-trip.
    public void AddAnnotation_ImageStamp_RoundTrips_WithoutImageBytes()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.ImageStamp,
            PageIndex = 0,
            LeftPt = 100, BottomPt = 600, RightPt = 200, TopPt = 700,
            ImageBytes = Fixtures.OnePixelPng(),
            Author = "Autor de Teste",
        };

        var result = Editor.AddAnnotation(Fixtures.A4(), data);
        var a = Assert.Single(Editor.ReadAnnotations(result));

        Assert.False(string.IsNullOrWhiteSpace(a.Id));
        Assert.Equal(AnnotationKind.ImageStamp, a.Kind);
        Assert.Equal(0, a.PageIndex);
        Assert.Equal(100, a.LeftPt, 0.5); Assert.Equal(600, a.BottomPt, 0.5);
        Assert.Equal(200, a.RightPt, 0.5); Assert.Equal(700, a.TopPt, 0.5);
        Assert.Equal("Autor de Teste", a.Author);
        Assert.Null(a.ImageBytes); // DECISÃO v1 — ver doc XML do contrato
        Assert.Null(a.Quads); Assert.Null(a.InkStrokes); Assert.Null(a.LineStartPt); Assert.Null(a.LineEndPt);
    }

    [Fact] // ImageStamp sem NENHUM byte de imagem -> ArgumentException, antes de tocar o PDF (mesmo
    // espírito de Ink sem traços/Line sem pontos).
    public void AddAnnotation_ImageStamp_NoImageBytes_Throws()
    {
        var data = new AnnotationData { Kind = AnnotationKind.ImageStamp, PageIndex = 0 };
        Assert.Throws<ArgumentException>(() => Editor.AddAnnotation(Fixtures.A4(), data));
    }

    [Fact] // ImageStamp com array de bytes VAZIO (não nulo) -> mesma ArgumentException (checagem de
    // Length, não só de null).
    public void AddAnnotation_ImageStamp_EmptyImageBytes_Throws()
    {
        var data = new AnnotationData { Kind = AnnotationKind.ImageStamp, PageIndex = 0, ImageBytes = Array.Empty<byte>() };
        Assert.Throws<ArgumentException>(() => Editor.AddAnnotation(Fixtures.A4(), data));
    }

    [Fact] // Regressão de RENDERIZAÇÃO (exemplar: RenderPage_SignatureStampAnnotation_IsPainted em
    // mPdf.Rendering.Tests) — não basta o dicionário cru ter /AP/N; a imagem PRECISA aparecer pintada
    // quando o PDF resultante é renderizado de verdade (PDFium via mPdf.Rendering, motor INDEPENDENTE
    // do iText que escreveu). PNG 1x1 vermelho hardcoded, escalado pro bbox 100x100pt — grande o
    // bastante pra pintar centenas de pixels não-brancos na região.
    public void AddAnnotation_ImageStamp_RendersNonBlankInStampRegion()
    {
        var data = new AnnotationData
        {
            Kind = AnnotationKind.ImageStamp,
            PageIndex = 0,
            LeftPt = 100, BottomPt = 600, RightPt = 200, TopPt = 700,
            ImageBytes = Fixtures.OnePixelPng(),
        };
        var result = Editor.AddAnnotation(Fixtures.A4(), data);

        using var renderer = new PdfDocumentRenderer(result);
        var page = renderer.RenderPage(0, 1.0);
        int painted = 0;
        int h = page.HeightPx, w = page.WidthPx;
        for (int y = h - 700; y < h - 600; y++)
            for (int x = 100; x < 200; x++)
            {
                int i = (y * w + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) painted++;
            }
        Assert.True(painted > 100, $"carimbo não renderizado: só {painted} pixels pintados na região");
    }

    [Fact] // pós-item 2a: Id explícito que já existe no documento -> ArgumentException, antes de escrever
    public void AddAnnotation_DuplicateId_Throws()
    {
        var first = Editor.AddAnnotation(Fixtures.A4(), new AnnotationData
        {
            Id = "id-duplicado",
            Kind = AnnotationKind.Highlight,
            PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 50, TopPt = 20,
        });

        var duplicate = new AnnotationData
        {
            Id = "id-duplicado", // mesmo Id da anotação já presente em `first`
            Kind = AnnotationKind.Highlight,
            PageIndex = 0,
            LeftPt = 60, BottomPt = 10, RightPt = 100, TopPt = 20,
        };
        var ex = Assert.Throws<ArgumentException>(() => Editor.AddAnnotation(first, duplicate));
        Assert.Contains("id-duplicado", ex.Message);

        // e o PDF não foi alterado: continua só com a 1ª anotação (a rejeitada nunca foi escrita)
        Assert.Single(Editor.ReadAnnotations(first));
    }

    [Theory] // pós-item 2a: Id vazio ou só espaços -> ArgumentException (nulo continua sendo "gerar GUID")
    [InlineData("")]
    [InlineData("   ")]
    public void AddAnnotation_EmptyOrWhitespaceId_Throws(string emptyId)
    {
        var data = new AnnotationData { Id = emptyId, Kind = AnnotationKind.Highlight, PageIndex = 0 };
        Assert.Throws<ArgumentException>(() => Editor.AddAnnotation(Fixtures.A4(), data));
    }

    // AddAnnotation_UnsupportedKind_ThrowsNotSupportedException (a antiga theory "kinds ainda não
    // implementados") foi REMOVIDA nesta task: ImageStamp era o ÚLTIMO valor do enum que ainda não
    // tinha implementação (ver AddAnnotation_ImageStamp_RoundTrips_WithoutImageBytes acima) — todos os
    // 10 kinds de AnnotationKind têm implementação agora, não sobra nenhum caso "não suportado" pra
    // testar. O guard NotSupportedException em si continua no código (defesa em profundidade — ver
    // AddAnnotation), só não há mais input de teste que o alcance.

    [Fact] // pós-I4: PageIndex fora do intervalo -> ArgumentOutOfRangeException (não deixa o iText lançar o dele)
    public void AddAnnotation_PageIndexOutOfRange_ThrowsArgumentOutOfRange()
    {
        var data = new AnnotationData { Kind = AnnotationKind.Highlight, PageIndex = 99 };
        Assert.Throws<ArgumentOutOfRangeException>(() => Editor.AddAnnotation(Fixtures.A4(), data));
    }

    [Fact] // pós-I4: bytes corrompidos (não é um PDF) -> PdfEditingException neutra, nunca um tipo iText cru
    public void AddAnnotation_CorruptBytes_ThrowsPdfEditingException()
    {
        var corrupt = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        var data = new AnnotationData { Kind = AnnotationKind.Highlight, PageIndex = 0 };
        Assert.Throws<PdfEditingException>(() => Editor.AddAnnotation(corrupt, data));
    }

    // --- RemoveAnnotation ---------------------------------------------------

    [Fact]
    public void RemoveAnnotation_ById_RemovesIt()
    {
        var added = Editor.AddAnnotation(Fixtures.A4(), new AnnotationData
        {
            Kind = AnnotationKind.Highlight,
            PageIndex = 0,
            LeftPt = 10, BottomPt = 10, RightPt = 50, TopPt = 20,
        });
        var id = Assert.Single(Editor.ReadAnnotations(added)).Id!;

        var removed = Editor.RemoveAnnotation(added, id);

        Assert.Empty(Editor.ReadAnnotations(removed));
    }

    [Fact] // fixture-anotada tem o NM conhecido "anotacao-fixture-1" — remove e confere que sumiu
    public void RemoveAnnotation_KnownFixtureId_RemovesIt()
    {
        var removed = Editor.RemoveAnnotation(Fixtures.Anotada(), "anotacao-fixture-1");
        Assert.Empty(Editor.ReadAnnotations(removed));
    }

    [Fact]
    public void RemoveAnnotation_UnknownId_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Editor.RemoveAnnotation(Fixtures.A4(), "id-que-nao-existe"));
    }

    [Fact] // pós-I3, tipada na rodada 2: mesma recusa de RemoveAnnotation em doc assinado (Add/Remove simétricos)
    public void RemoveAnnotation_OnSignedDocument_Throws()
    {
        Assert.Throws<PdfSignedDocumentException>(
            () => Editor.RemoveAnnotation(Fixtures.Carimbo(), "qualquer-id"));
    }

    [Fact] // pós-item 2b: remove EXATAMENTE a anotação pedida, não todas — fixture-anotada já tem
           // "anotacao-fixture-1"; adiciono uma 2ª com Id distinto e removo só a 1ª. (O cenário de
           // /NM duplicado externo não é testável sem um PDF forjado à mão fora da API deste módulo
           // — que agora impede duplicidade por construção — e fica documentado como resíduo em vez
           // de fabricado só para o teste; ver task-2-report.md.)
    public void RemoveAnnotation_RemovesOnlyTheTargetedAnnotation()
    {
        var withSecond = Editor.AddAnnotation(Fixtures.Anotada(), new AnnotationData
        {
            Id = "segunda-anotacao",
            Kind = AnnotationKind.Highlight,
            PageIndex = 0,
            LeftPt = 60, BottomPt = 600, RightPt = 100, TopPt = 620,
        });
        Assert.Equal(2, Editor.ReadAnnotations(withSecond).Count); // sanity: as 2 estão lá antes de remover

        var result = Editor.RemoveAnnotation(withSecond, "anotacao-fixture-1");

        var remaining = Assert.Single(Editor.ReadAnnotations(result));
        Assert.Equal("segunda-anotacao", remaining.Id);
    }

    // --- StripSignatures (Plano 3a, Task 5) ---------------------------------

    [Fact] // fixture-carimbo tem 1 assinatura PAdES real -> some, PageCount preservado (verificado por
    // um motor INDEPENDENTE do iText — PdfDocumentRenderer/Docnet — não uma prova circular)
    public void StripSignatures_FixtureCarimbo_RemovesSignatures_PreservesPageCount()
    {
        var result = Editor.StripSignatures(Fixtures.Carimbo());

        Assert.False(Editor.HasSignatures(result));
        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(1, renderer.PageCount);
    }

    [Fact] // nunca muta o array recebido — mesmo contrato de AddAnnotation/RemoveAnnotation acima
    // (nenhum deles mexe no `pdf` de entrada, só lê via MemoryStream)
    public void StripSignatures_DoesNotMutateOriginalBytes()
    {
        var original = Fixtures.Carimbo();
        var untouchedCopy = (byte[])original.Clone();

        Editor.StripSignatures(original);

        Assert.Equal(untouchedCopy, original);
    }

    [Fact] // doc SEM assinatura -> no-op honesto: não lança, continua sem assinatura, PageCount preservado
    public void StripSignatures_UnsignedDocument_NoThrow_PreservesPageCount()
    {
        var result = Editor.StripSignatures(Fixtures.A4());

        Assert.False(Editor.HasSignatures(result));
        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(1, renderer.PageCount);
    }

    // --- Item 5(a) (revisão final pré-merge): probe ad-hoc da revisão promovida a teste PERMANENTE ---

    [Fact] // O CENÁRIO REAL do fluxo "Editar uma cópia" (Task 5, Plano 3a): fixture-carimbo tem uma
    // assinatura PAdES DE VERDADE (ver PadesSigner no PoC) -> StripSignatures (mesmo caminho que
    // MainViewModel.EditCopy usa) -> AddAnnotation (Highlight num retângulo CONHECIDO), como qualquer
    // edição de usuário faria em seguida. Prova, por um motor de verificação INDEPENDENTE do iText
    // (PdfDocumentRenderer/Docnet — mesmo padrão de StripSignatures_FixtureCarimbo_..._PreservesPageCount
    // acima) que a anotação NUNCA vaza pra fora de si mesma: contagem de página preservada, texto
    // extraído byte-a-byte idêntico, e render diff ZERO pixels diferentes FORA da região da anotação —
    // só DENTRO dela (o destaque em si) deveria diferir.
    public void RoundTrip_RealSignedFixture_PreservesEverythingOutsideAnnotation()
    {
        byte[] stripped = Editor.StripSignatures(Fixtures.Carimbo());

        // Retângulo de destaque LONGE da região do carimbo/widget original ((300,50)-(550,130), ver
        // RenderPage_SignatureStampAnnotation_IsPainted em mPdf.Rendering.Tests) — a posição em si é
        // irrelevante pra "nada vaza fora da anotação" (isso vale nem importa ONDE ela é colocada), mas
        // evitar sobreposição deixa a asserção "diff DENTRO > 100" inequívoca (destaque puro, sem
        // interferência de conteúdo pré-existente por baixo).
        const double Left = 50, Bottom = 750, Right = 200, Top = 800;
        var highlight = new AnnotationData
        {
            Kind = AnnotationKind.Highlight,
            PageIndex = 0,
            LeftPt = Left, BottomPt = Bottom, RightPt = Right, TopPt = Top,
            ColorArgb = 0xFFFFFF00, // amarelo — mesmo default de DocumentViewModel.ColorAmarelo
            Author = "Teste",
        };
        byte[] result = Editor.AddAnnotation(stripped, highlight);

        using var rendererBefore = new PdfDocumentRenderer(stripped);
        using var rendererAfter = new PdfDocumentRenderer(result);

        // page count preservado
        Assert.Equal(rendererBefore.PageCount, rendererAfter.PageCount);

        // texto extraído idêntico — uma anotação nunca deve alterar o CONTEÚDO textual da página
        // (motor de extração INDEPENDENTE do iText — PdfDocumentRenderer.GetTextPage via Docnet/PDFium).
        Assert.Equal(rendererBefore.GetTextPage(0).Text, rendererAfter.GetTextPage(0).Text);

        // render diff: FORA da região da anotação, ZERO pixels diferentes; DENTRO dela, >100.
        var pageBefore = rendererBefore.RenderPage(0, 1.0);
        var pageAfter = rendererAfter.RenderPage(0, 1.0);
        Assert.Equal(pageBefore.WidthPx, pageAfter.WidthPx);
        Assert.Equal(pageBefore.HeightPx, pageAfter.HeightPx);
        int w = pageBefore.WidthPx, h = pageBefore.HeightPx;

        // Anotação em px de imagem: X bate direto com pt (escala 1.0, mesma convenção de toda região
        // testada neste arquivo); Y invertido (origem PDF = canto INFERIOR esquerdo; origem da imagem =
        // canto SUPERIOR). `Margin`: banda de poucos pixels na BORDA do retângulo, ignorada pros dois
        // lados (nem exigida como prova de pintura, nem contada como violação) — tolerância de
        // antialiasing na borda de uma forma, não uma alegação de renderização pixel-perfeita; nenhum
        // teste deste arquivo assume isso (ver RenderPage_SignatureStampAnnotation_IsPainted, mesmo
        // espírito de folga).
        const int Margin = 4;
        int annLeft = (int)Left, annRight = (int)Right;
        int annTop = h - (int)Top, annBottom = h - (int)Bottom;

        int diffOutsidePadded = 0, diffInsideCore = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool differs = pageBefore.Bgra[i] != pageAfter.Bgra[i]
                    || pageBefore.Bgra[i + 1] != pageAfter.Bgra[i + 1]
                    || pageBefore.Bgra[i + 2] != pageAfter.Bgra[i + 2];
                if (!differs) continue;

                bool insideCore = x >= annLeft + Margin && x < annRight - Margin
                    && y >= annTop + Margin && y < annBottom - Margin;
                bool outsidePadded = x < annLeft - Margin || x >= annRight + Margin
                    || y < annTop - Margin || y >= annBottom + Margin;

                if (insideCore) diffInsideCore++;
                else if (outsidePadded) diffOutsidePadded++;
                // pixels na faixa de borda (nem núcleo nem fora-com-folga) são IGNORADOS de propósito
            }
        }

        Assert.Equal(0, diffOutsidePadded);
        Assert.True(diffInsideCore > 100,
            $"destaque não visível: só {diffInsideCore} pixels diferentes no núcleo da região");
    }

    // --- Task 2 (Plano 3b): motor de organização de páginas -----------------------------------
    // fixture-30p.pdf: pageIndex i (0-based) tem o texto "Fixture mPDF - pagina {i+1} de 30" (ver
    // Fixtures.cs / TempMakeFixtures em docs/superpowers/plans/2026-08-13-plano2a-nucleo-leitor.md).
    // Toda verificação de CONTEÚDO/ORDEM usa PdfDocumentRenderer (Docnet/PDFium) — motor INDEPENDENTE
    // do iText que escreveu, mesmo padrão do resto deste arquivo (nunca uma prova circular).

    // --- RotatePages ---------------------------------------------------

    [Fact] // motor INDEPENDENTE do iText (Docnet/PDFium, via PdfDocumentRenderer.GetPageSize):
    // 90/270 graus TROCAM largura/altura efetivas da página — confirmado empiricamente (probe
    // project isolado com Docnet.Core direto — ver task-2-report.md) antes de escrever este teste.
    public void RotatePages_Rotate90_SwapsPageDimensions()
    {
        using var before = new PdfDocumentRenderer(Fixtures.ThirtyPages());
        var sizeBefore = before.GetPageSize(0);

        var result = Editor.RotatePages(Fixtures.ThirtyPages(), new[] { 0 }, 90);

        using var after = new PdfDocumentRenderer(result);
        var sizeAfter = after.GetPageSize(0);
        Assert.Equal(sizeBefore.WidthPt, sizeAfter.HeightPt, 0.5);
        Assert.Equal(sizeBefore.HeightPt, sizeAfter.WidthPt, 0.5);
    }

    [Fact] // Prova que a rotação é ADITIVA-COM-NORMALIZAÇÃO na IMPLEMENTAÇÃO, não absoluta: 2
    // chamadas de 90° (em 2 saves separados) somam 180°, não "travam" em 90°. `// HIPÓTESE:` do
    // brief reconciliada empiricamente (probe project isolado): `PdfPage.SetRotation(int)` do iText
    // é ABSOLUTO por si só (chamar SetRotation(90) 2x seguidas grava /Rotate 90 as 2 vezes) — é
    // RotatePages que lê GetRotation() antes de escrever e soma. 180° NÃO troca largura/altura (ao
    // contrário de 90°/270°) — se a implementação tivesse um bug e "travasse" em 90 a cada chamada,
    // as dimensões continuariam trocadas aqui; só voltam ao original se 90+90 é realmente 180.
    public void RotatePages_DoubleRotate90Plus90_Is180()
    {
        using var before = new PdfDocumentRenderer(Fixtures.ThirtyPages());
        var sizeBefore = before.GetPageSize(0);

        var oncePlusOnce = Editor.RotatePages(
            Editor.RotatePages(Fixtures.ThirtyPages(), new[] { 0 }, 90),
            new[] { 0 }, 90);

        using var after = new PdfDocumentRenderer(oncePlusOnce);
        var sizeAfter = after.GetPageSize(0);
        Assert.Equal(sizeBefore.WidthPt, sizeAfter.WidthPt, 0.5);
        Assert.Equal(sizeBefore.HeightPt, sizeAfter.HeightPt, 0.5);
    }

    [Theory] // só 90/180/270 são ângulos válidos — checagem de INPUT pura, antes de abrir o PDF
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(-90)]
    [InlineData(360)]
    public void RotatePages_InvalidDegrees_ThrowsArgumentException(int degrees)
    {
        Assert.Throws<ArgumentException>(() => Editor.RotatePages(Fixtures.ThirtyPages(), new[] { 0 }, degrees));
    }

    [Fact]
    public void RotatePages_InvalidPageIndex_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Editor.RotatePages(Fixtures.ThirtyPages(), new[] { 99 }, 90));
    }

    [Fact] // Gate por op (self-review do plano 3b exige 1 teste por operação mutadora)
    public void RotatePages_OnSignedDocument_Throws()
    {
        Assert.Throws<PdfSignedDocumentException>(() => Editor.RotatePages(Fixtures.Carimbo(), new[] { 0 }, 90));
    }

    [Fact] // Opus review, rider: duplicata proposital ([0,0]) não pode girar a página 0 DUAS vezes
    // na mesma chamada (90+90=180) — RotatePages deduplica pageIndexes (mesmo raciocínio de
    // DeletePages abaixo). 180° NÃO troca largura/altura (ao contrário de 90°/270°) — se o dedup
    // não existisse, este teste falharia (dimensões continuariam iguais, não trocadas).
    public void RotatePages_DuplicateIndexes_RotatesOnce()
    {
        using var before = new PdfDocumentRenderer(Fixtures.ThirtyPages());
        var sizeBefore = before.GetPageSize(0);

        var result = Editor.RotatePages(Fixtures.ThirtyPages(), new[] { 0, 0 }, 90);

        using var after = new PdfDocumentRenderer(result);
        var sizeAfter = after.GetPageSize(0);
        Assert.Equal(sizeBefore.WidthPt, sizeAfter.HeightPt, 0.5);
        Assert.Equal(sizeBefore.HeightPt, sizeAfter.WidthPt, 0.5);
    }

    [Fact] // SUB-TESTE CRÍTICO (self-review do plano 3b, Task 2): girar uma página com anotação não
    // pode "perder" a anotação nem deixar a página em branco. fixture-anotada = fixture-a4 + 1
    // highlight (NM=anotacao-fixture-1, ver Fixtures.cs).
    public void RotatePages_PageWithAnnotation_AnnotationSurvivesAndPageRendersNonBlank()
    {
        var before = Editor.ReadAnnotations(Fixtures.Anotada());
        var annotationBefore = Assert.Single(before);

        var result = Editor.RotatePages(Fixtures.Anotada(), new[] { 0 }, 90);

        var after = Editor.ReadAnnotations(result);
        var annotationAfter = Assert.Single(after);
        Assert.Equal(annotationBefore.Id, annotationAfter.Id);
        Assert.Equal(annotationBefore.Kind, annotationAfter.Kind);
        // ACHADO EMPÍRICO (ver task-2-report.md): `/Rotate` é um atributo de EXIBIÇÃO da página — o
        // spec PDF não pede que ele altere o `/Rect` de nenhuma anotação; o retângulo da anotação
        // continua no sistema de coordenadas ORIGINAL (não-rotacionado) da página, e é o LEITOR de
        // PDF (aqui, PDFium via PdfDocumentRenderer abaixo) que aplica a rotação a TODO o conteúdo —
        // texto e anotações igualmente — na hora de desenhar. Por isso o Rect lido de volta é
        // IDÊNTICO ao de antes da rotação (confirmado por este teste, não uma hipótese não-verificada).
        Assert.Equal(annotationBefore.LeftPt, annotationAfter.LeftPt, 0.01);
        Assert.Equal(annotationBefore.BottomPt, annotationAfter.BottomPt, 0.01);
        Assert.Equal(annotationBefore.RightPt, annotationAfter.RightPt, 0.01);
        Assert.Equal(annotationBefore.TopPt, annotationAfter.TopPt, 0.01);

        // página renderizada não pode ficar em branco (motor INDEPENDENTE do iText — Docnet/PDFium —
        // mesmo padrão de AddAnnotation_ImageStamp_RendersNonBlankInStampRegion acima).
        using var renderer = new PdfDocumentRenderer(result);
        var page = renderer.RenderPage(0, 1.0);
        int nonWhite = 0;
        for (int i = 0; i < page.Bgra.Length; i += 4)
            if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) nonWhite++;
        Assert.True(nonWhite > 100,
            $"página rotacionada renderizou praticamente em branco ({nonWhite} pixels não-brancos)");
    }

    // --- GetPageRotations (Task 3, Plano 3b — costura de rotação, requisito de 1ª ordem) --------------

    [Fact]
    public void GetPageRotations_UnrotatedDocument_AllZero()
    {
        var rotations = Editor.GetPageRotations(Fixtures.ThirtyPages());
        Assert.Equal(30, rotations.Count);
        Assert.All(rotations, r => Assert.Equal(0, r));
    }

    [Fact] // lê de volta exatamente o que RotatePages escreveu — só a página girada muda, as demais
    // continuam 0 (prova que a leitura é POR PÁGINA, não um valor global).
    public void GetPageRotations_ReflectsRotatePages()
    {
        var rotated = Editor.RotatePages(Fixtures.ThirtyPages(), new[] { 2 }, 90);

        var rotations = Editor.GetPageRotations(rotated);

        Assert.Equal(90, rotations[2]);
        Assert.Equal(0, rotations[0]);
        Assert.Equal(0, rotations[1]);
        Assert.Equal(0, rotations[3]);
    }

    [Fact] // 2 rotações de 90° somam 180° na MESMA página (mesmo invariante aditivo-com-normalização
    // já provado por RotatePages_DoubleRotate90Plus90_Is180) — GetPageRotations reflete a SOMA, não a
    // última chamada isolada.
    public void GetPageRotations_DoubleRotate90Plus90_Is180()
    {
        var rotated = Editor.RotatePages(
            Editor.RotatePages(Fixtures.ThirtyPages(), new[] { 0 }, 90),
            new[] { 0 }, 90);

        Assert.Equal(180, Editor.GetPageRotations(rotated)[0]);
    }

    // --- ReadOutline (Task 5, Plano 3b — sumário/bookmarks) ---------------------------------------
    //
    // Árvore de fixture-sumario.pdf (30 páginas, gerada 1x via PoC — ver task-5-report.md):
    //   Capítulo 1 [0]
    //     Seção 1.1 [1]
    //       Item 1.1.1 [2]
    //       Item 1.1.2 [3]
    //     Seção 1.2 [4]
    //   Capítulo 2 [10]
    //     Seção 2.1 [11]
    //       Item 2.1.1 [12]
    //   Capítulo 3 [20]
    //   Anexos [sem página]

    [Fact] // forma da árvore: títulos, aninhamento e ORDEM (children preserva a ordem de inserção do
    // outline) — os 5 nós de TOPO, na ordem exata.
    public void ReadOutline_FixtureSumario_TopLevelTitlesInOrder()
    {
        var outline = Editor.ReadOutline(Fixtures.Sumario());

        Assert.Equal(
            new[] { "Capítulo 1", "Capítulo 2", "Capítulo 3", "Anexos" },
            outline.Select(n => n.Title));
    }

    [Fact] // aninhamento de 3 níveis sob "Capítulo 1": 2 filhos diretos (Seção 1.1/1.2), e Seção 1.1
    // tem 2 filhos próprios (Item 1.1.1/1.1.2) — prova a árvore RECURSIVA, não só uma lista achatada.
    public void ReadOutline_FixtureSumario_ThreeLevelNesting()
    {
        var outline = Editor.ReadOutline(Fixtures.Sumario());

        var cap1 = outline.Single(n => n.Title == "Capítulo 1");
        Assert.Equal(new[] { "Seção 1.1", "Seção 1.2" }, cap1.Children.Select(n => n.Title));

        var sec11 = cap1.Children.Single(n => n.Title == "Seção 1.1");
        Assert.Equal(new[] { "Item 1.1.1", "Item 1.1.2" }, sec11.Children.Select(n => n.Title));
        Assert.Empty(sec11.Children[0].Children); // nó-folha: Children vazio, nunca null
    }

    [Fact] // índices de página 0-based corretos em TODOS os níveis, inclusive nós profundamente
    // aninhados (Item 1.1.1) e o 2º capítulo (não-contíguo, página 10).
    public void ReadOutline_FixtureSumario_PageIndexesCorrect()
    {
        var outline = Editor.ReadOutline(Fixtures.Sumario());

        var cap1 = outline.Single(n => n.Title == "Capítulo 1");
        Assert.Equal(0, cap1.PageIndex);
        var sec11 = cap1.Children.Single(n => n.Title == "Seção 1.1");
        Assert.Equal(1, sec11.PageIndex);
        Assert.Equal(2, sec11.Children.Single(n => n.Title == "Item 1.1.1").PageIndex);
        Assert.Equal(3, sec11.Children.Single(n => n.Title == "Item 1.1.2").PageIndex);
        Assert.Equal(4, cap1.Children.Single(n => n.Title == "Seção 1.2").PageIndex);

        var cap2 = outline.Single(n => n.Title == "Capítulo 2");
        Assert.Equal(10, cap2.PageIndex);
        Assert.Equal(20, outline.Single(n => n.Title == "Capítulo 3").PageIndex);
    }

    [Fact] // "Anexos" foi gerado SEM AddDestination nenhum (nó organizacional puro) — PageIndex null,
    // nunca 0 ou uma exceção.
    public void ReadOutline_FixtureSumario_NodeWithoutDestination_PageIndexIsNull()
    {
        var outline = Editor.ReadOutline(Fixtures.Sumario());

        var anexos = outline.Single(n => n.Title == "Anexos");
        Assert.Null(anexos.PageIndex);
        Assert.Empty(anexos.Children);
    }

    [Fact] // documento sem NENHUM /Outlines (fixture-a4) -> lista vazia, nunca null nem exceção —
    // achado empírico: GetOutlines(false) devolve null nesse caso, ReadOutline trata como vazio.
    public void ReadOutline_FixtureA4_NoOutline_ReturnsEmptyList()
    {
        Assert.Empty(Editor.ReadOutline(Fixtures.A4()));
    }

    [Fact] // ReadOutline é leitura pura, SEM gate de assinatura (mesma política de ReadAnnotations/
    // HasSignatures/GetPageRotations) — lê fixture-carimbo (documento assinado) sem lançar.
    public void ReadOutline_FixtureCarimbo_SignedDocument_ReadsWithoutThrowing()
    {
        var outline = Editor.ReadOutline(Fixtures.Carimbo());
        Assert.NotNull(outline); // fixture-carimbo não tem outline -> lista vazia é o resultado esperado
    }

    [Fact] // ACHADO EMPÍRICO (sonda no PoC, task-5-report.md): excluir a página-alvo de um bookmark via
    // DeletePages (doc.RemovePage em modo stamping) faz o PRÓPRIO iText PODAR o nó de outline inteiro
    // da árvore — não sobra com PageIndex null. Item 1.1.1 mira a página 2 (0-based); excluí-la deve
    // fazer o nó "Item 1.1.1" desaparecer de baixo de "Seção 1.1", mas "Item 1.1.2" (página 3 antes,
    // vira 2 depois) sobrevive normalmente.
    public void ReadOutline_DeletePagesRemovesBookmarkTarget_NodeIsPruned()
    {
        var edited = Editor.DeletePages(Fixtures.Sumario(), new[] { 2 });

        var outline = Editor.ReadOutline(edited);

        var sec11 = outline.Single(n => n.Title == "Capítulo 1").Children.Single(n => n.Title == "Seção 1.1");
        Assert.DoesNotContain(sec11.Children, n => n.Title == "Item 1.1.1");
        var item112 = sec11.Children.Single(n => n.Title == "Item 1.1.2");
        Assert.Equal(2, item112.PageIndex); // página 3 original desloca pra 2 depois da exclusão
    }

    [Fact] // Review pós-Task 5 (Important): settla EMPIRICAMENTE a inferência não verificada do
    // relatório original ("MovePage não foi sondado; expectativa razoável é que o destino sobrevive").
    // Move "Capítulo 2" (índice 10, o alvo) pro índice 0 — mesma semântica de "list splice" já provada
    // pra CONTEÚDO de página em MovePage_BranchAndBoundaryCoverage_VerifiedByTextContent acima: old
    // 0..9 -> new 1..10 (Capítulo 1/Seção 1.1/Item 1.1.1/Item 1.1.2/Seção 1.2), old 10 -> new 0
    // (Capítulo 2 — o nó MOVIDO), old 11+ inalterado (Seção 2.1/Item 2.1.1/Capítulo 3). Confere a
    // árvore INTEIRA pós-move, não só o nó movido — se ISSO falhar, é porque MovePage (doc.MovePage do
    // iText, que reordena a árvore de páginas SEM remover o dicionário) quebrou os destinos de outline
    // de alguma forma — um bug real em MovePage, não neste teste.
    public void ReadOutline_MovePageRelocatesBookmarkTarget_IndexesShiftConsistently()
    {
        var moved = Editor.MovePage(Fixtures.Sumario(), fromIndex: 10, toIndex: 0);

        var outline = Editor.ReadOutline(moved);

        var cap1 = outline.Single(n => n.Title == "Capítulo 1");
        Assert.Equal(1, cap1.PageIndex);
        var sec11 = cap1.Children.Single(n => n.Title == "Seção 1.1");
        Assert.Equal(2, sec11.PageIndex);
        Assert.Equal(3, sec11.Children.Single(n => n.Title == "Item 1.1.1").PageIndex);
        Assert.Equal(4, sec11.Children.Single(n => n.Title == "Item 1.1.2").PageIndex);
        Assert.Equal(5, cap1.Children.Single(n => n.Title == "Seção 1.2").PageIndex);

        var cap2 = outline.Single(n => n.Title == "Capítulo 2");
        Assert.Equal(0, cap2.PageIndex); // o nó MOVIDO — a asserção central do review
        var sec21 = cap2.Children.Single(n => n.Title == "Seção 2.1");
        Assert.Equal(11, sec21.PageIndex); // inalterado — old 11 fica >= fromIndex original (10)
        Assert.Equal(12, sec21.Children.Single(n => n.Title == "Item 2.1.1").PageIndex);

        Assert.Equal(20, outline.Single(n => n.Title == "Capítulo 3").PageIndex); // inalterado
        Assert.Null(outline.Single(n => n.Title == "Anexos").PageIndex);
    }

    [Fact] // Review pós-Task 5 (Important item 2): PDFs de origem EXTERNA (o escritório abre PDFs de
    // terceiros diariamente) podem ter um /Outlines aninhado ALÉM do razoável (construível de propósito,
    // sem precisar de ciclo nenhum) — sem um limite, BuildOutlineNode recursaria até estourar a pilha
    // (StackOverflowException, INCAPTURÁVEL em .NET, derruba o processo inteiro). fixture-outline-
    // profundo.pdf tem 100 níveis reais (cadeia linear, 1 filho por nó) — prova CAPABILITY-STYLE que o
    // guard (PdfEditor.MaxOutlineDepth = 64) realmente para de descender no CAMINHO DE RECURSÃO de
    // verdade (o teste em si não estourar a pilha já é uma prova; a contagem exata de 64 confirma o
    // ponto de corte documentado). O NÓ no limite ainda é construído normalmente (Título/PageIndex),
    // só Children fica vazio — não é um "buraco" na árvore, é uma folha honesta.
    public void ReadOutline_DeeplyNestedOutline_StopsDescendingAtMaxDepth()
    {
        var outline = Editor.ReadOutline(Fixtures.OutlineProfundo());

        var node = outline.Single();
        Assert.Equal("Nivel 0", node.Title);
        int depth = 0;
        while (node.Children.Count > 0)
        {
            depth++;
            node = node.Children.Single();
        }

        Assert.Equal(64, depth); // MaxOutlineDepth — o nó "Nivel 64" é o último construído com filhos
        Assert.Equal($"Nivel {depth}", node.Title); // ainda construído corretamente, só sem descendência
        Assert.Empty(node.Children); // cortado pelo guard — a fixture TEM mais 35 níveis reais abaixo dele
    }

    // --- DeletePages ---------------------------------------------------

    [Fact]
    public void DeletePages_RemovesGivenPages_SurvivorsContentPreserved()
    {
        // remove os índices 1 e 3 (0-based) = "pagina 2" e "pagina 4" -> sobra
        // pagina1,pagina3,pagina5,pagina6,...,pagina30 (28 páginas).
        var result = Editor.DeletePages(Fixtures.ThirtyPages(), new[] { 1, 3 });

        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(28, renderer.PageCount);
        // substring EXATA "pagina 1 de 30" (não só "pagina 1"): "pagina 1" também é prefixo de
        // "pagina 10".."pagina 19" — um bug que pousasse a página errada aqui ainda passaria num
        // Contains("pagina 1") solto (Opus review, item riders).
        Assert.Contains("pagina 1 de 30", renderer.GetTextPage(0).Text);
        Assert.Contains("pagina 3", renderer.GetTextPage(1).Text); // pagina 2 foi removida
        Assert.Contains("pagina 5", renderer.GetTextPage(2).Text); // pagina 4 foi removida
    }

    [Fact] // duplicata proposital no índice 0 -> prova que a checagem "todas" usa Distinct(), não
    // conta a duplicata como se fossem 31 índices para um doc de 30 páginas.
    public void DeletePages_AllPages_ThrowsArgumentException()
    {
        var allIndexesWithDuplicate = Enumerable.Range(0, 30).Append(0).ToList();

        var ex = Assert.Throws<ArgumentException>(
            () => Editor.DeletePages(Fixtures.ThirtyPages(), allIndexesWithDuplicate));
        Assert.Contains("todas", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // fixture-carimbo tem só 1 página: SE o gate de assinatura não disparasse primeiro, [0]
    // cairia direto no caso "excluir todas" — este teste prova a ORDEM dos guards também (assinatura
    // antes de qualquer outra checagem).
    public void DeletePages_OnSignedDocument_Throws()
    {
        Assert.Throws<PdfSignedDocumentException>(() => Editor.DeletePages(Fixtures.Carimbo(), new[] { 0 }));
    }

    // --- MovePage ---------------------------------------------------

    [Fact]
    public void MovePage_ReordersPages_VerifiedByTextContent()
    {
        // move pagina1 (índice 0) para o índice 2 -> esperado: pagina2,pagina3,pagina1,pagina4,...
        var result = Editor.MovePage(Fixtures.ThirtyPages(), 0, 2);

        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(30, renderer.PageCount);
        Assert.Contains("pagina 2", renderer.GetTextPage(0).Text);
        Assert.Contains("pagina 3", renderer.GetTextPage(1).Text);
        // substring EXATA "pagina 1 de 30" (não só "pagina 1"): "pagina 1" também é prefixo de
        // "pagina 10".."pagina 19" — ver mesma nota em DeletePages_RemovesGivenPages_... acima
        // (Opus review, riders).
        Assert.Contains("pagina 1 de 30", renderer.GetTextPage(2).Text);
        Assert.Contains("pagina 4", renderer.GetTextPage(3).Text);
    }

    [Fact]
    public void MovePage_InvalidFromIndex_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Editor.MovePage(Fixtures.ThirtyPages(), 99, 0));
    }

    [Fact]
    public void MovePage_InvalidToIndex_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Editor.MovePage(Fixtures.ThirtyPages(), 0, 99));
    }

    [Fact]
    public void MovePage_OnSignedDocument_Throws()
    {
        Assert.Throws<PdfSignedDocumentException>(() => Editor.MovePage(Fixtures.Carimbo(), 0, 0));
    }

    [Theory] // Opus review, item 4: cobertura de RAMO da fórmula de MovePage
    // (`toIndex <= fromIndex ? toIndex+1 : toIndex+2`) + os 2 casos de fronteira mais arriscados
    // (mover pro início/fim do doc — onde o argumento nativo passa de pageCount) + o caso n->n
    // (no-op). Cada caso reconstrói a ordem ESPERADA INTEIRA via "list splice" de referência (não
    // só a página que se moveu) e confere TODAS as 30 páginas — prova de ordem completa, não só de
    // "a página X chegou lá".
    //
    // NOTA DE MUTAÇÃO (registrada em detalhe em task-2-report.md, seção "## Fix" — resultado
    // DIFERENTE do esperado pelo pedido original, reportado honestamente): trocar `<=` por `<`
    // aqui NÃO quebra nenhum destes testes — provado ALGEBRICAMENTE e depois confirmado
    // EMPIRICAMENTE (probe isolado, 6 valores de n incluindo os 2 extremos 0 e 29): no único ponto
    // onde `<=`/`<` divergem (toIndex==fromIndex), os 2 argumentos nativos resultantes
    // (`from+1` vs `from+2`) resolvem para a MESMA posição final via a semântica própria do
    // `insertBefore` do iText ("inserir antes de onde você mesmo ficaria de qualquer forma" é um
    // no-op nos 2 casos) — não é um teste fraco, é uma mutação genuinamente EQUIVALENTE nesta
    // fórmula específica; nenhuma asserção de ORDEM final pode distinguir as duas. A prova de
    // mutação REAL destes testes está no report: trocar os ramos entre si
    // (`toIndex+2 : toIndex+1`, invertido) derruba 5 dos 9 casos abaixo.
    [InlineData(0, 1)]   // adjacente, ramo toIndex > fromIndex ("+2")
    [InlineData(1, 0)]   // adjacente, ramo toIndex <= fromIndex ("+1")
    [InlineData(29, 0)]  // fronteira: do FIM (índice 29) pro INÍCIO
    [InlineData(0, 29)]  // fronteira: do INÍCIO pro FIM (toIndex+2 = 31, 1 além de pageCount)
    [InlineData(15, 15)] // no-op: from==toIndex
    public void MovePage_BranchAndBoundaryCoverage_VerifiedByTextContent(int fromIndex, int toIndex)
    {
        var result = Editor.MovePage(Fixtures.ThirtyPages(), fromIndex, toIndex);

        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(30, renderer.PageCount);

        // reconstrói a ordem esperada por "list splice" padrão (remover de fromIndex, inserir em
        // toIndex — mesma semântica de ObservableCollection.Move) e confere TODAS as páginas.
        var expected = Enumerable.Range(1, 30).ToList();
        int moved = expected[fromIndex];
        expected.RemoveAt(fromIndex);
        expected.Insert(toIndex, moved);

        for (int i = 0; i < expected.Count; i++)
            Assert.Contains($"pagina {expected[i]} de 30", renderer.GetTextPage(i).Text);
    }

    // --- ExtractPages ---------------------------------------------------

    [Fact]
    public void ExtractPages_NewDocWithOnlySelectedPages_InGivenOrder_OriginalUntouched()
    {
        var original = Fixtures.ThirtyPages();
        var untouchedCopy = (byte[])original.Clone();

        // fora de ordem, de propósito: prova que ExtractPages respeita a ORDEM pedida, não reordena
        // por número crescente.
        var result = Editor.ExtractPages(original, new[] { 4, 0, 2 });

        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(3, renderer.PageCount);
        Assert.Contains("pagina 5", renderer.GetTextPage(0).Text);
        // substring EXATA "pagina 1 de 30" (não só "pagina 1"): "pagina 1" também é prefixo de
        // "pagina 10".."pagina 19" — ver mesma nota em DeletePages_RemovesGivenPages_... acima
        // (Opus review, riders).
        Assert.Contains("pagina 1 de 30", renderer.GetTextPage(1).Text);
        Assert.Contains("pagina 3", renderer.GetTextPage(2).Text);

        Assert.Equal(untouchedCopy, original); // nunca muta os bytes de entrada
        using var rendererOriginal = new PdfDocumentRenderer(original);
        Assert.Equal(30, rendererOriginal.PageCount); // original com as 30 páginas ainda
    }

    [Fact] // pura checagem de INPUT, antes de abrir o PDF (mesmo espírito de MergeDocuments_EmptyList_...)
    public void ExtractPages_EmptyPageIndexes_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Editor.ExtractPages(Fixtures.ThirtyPages(), Array.Empty<int>()));
    }

    [Fact] // Opus review, adjudicação: ExtractPages foi UNGATED (política única com Merge/Split —
    // ver Contract.cs) — mesmo formato do teste de MergeDocuments_WithSignedInput_...: o INVARIANTE
    // fim-a-fim (HasSignatures do resultado) é o que importa, não o mecanismo interno.
    public void ExtractPages_FromSignedDocument_ProducesUnsignedResult()
    {
        var result = Editor.ExtractPages(Fixtures.Carimbo(), new[] { 0 });

        Assert.False(Editor.HasSignatures(result));
        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(1, renderer.PageCount);
    }

    // --- C1 (revisão final pré-merge, Plano 3b): stamp visual órfão em Extract/Insert/Merge/Split ---
    // `CopyPagesTo`/`PdfMerger` NUNCA copiam o AcroForm/campo de assinatura da fonte, mas copiam o
    // WIDGET visual (a aparência do carimbo) — sem o fix (StripSignatures na ORIGEM antes de copiar,
    // ver PdfEditor.ExtractPages/InsertPages/MergeDocuments/SplitByRanges), o carimbo "Assinado
    // digitalmente por…" sobrevivia PIXEL-IDÊNTICO ao original enquanto `HasSignatures(resultado)`
    // mentia "false" — exatamente o teste acima (`..._ProducesUnsignedResult`), que já passava MESMO
    // COM o bug (esse é o motivo do bug ter ficado invisível numa rodada de revisão inteira). Os
    // testes abaixo são LOAD-BEARING: usam um motor de verificação INDEPENDENTE do iText
    // (PdfDocumentRenderer/Docnet — mesmo padrão de RoundTrip_RealSignedFixture..., no topo deste
    // arquivo) pra medir pixels de verdade, não só perguntar pro `SignatureUtil` que escreveu o PDF.

    /// Conta pixels (RGB, ignora alfa) que diferem entre 2 renders do MESMO tamanho — mesma lógica
    /// inline já usada por `RoundTrip_RealSignedFixture_PreservesEverythingOutsideAnnotation` acima,
    /// extraída aqui pra ser reusada pelos 4 testes de C1 sem repetir o loop 4 vezes.
    private static int CountDifferingPixels(RenderedPage a, RenderedPage b)
    {
        Assert.Equal(a.WidthPx, b.WidthPx);
        Assert.Equal(a.HeightPx, b.HeightPx);
        int diff = 0;
        for (int i = 0; i + 2 < a.Bgra.Length; i += 4)
            if (a.Bgra[i] != b.Bgra[i] || a.Bgra[i + 1] != b.Bgra[i + 1] || a.Bgra[i + 2] != b.Bgra[i + 2])
                diff++;
        return diff;
    }

    [Fact] // TESTE LOAD-BEARING: página 0 do resultado de ExtractPages PRECISA diferir do render da
    // fixture-carimbo ORIGINAL (assinada) em MILHARES de pixels — o carimbo visual removido, não um
    // artefato de antialiasing. Compara também contra StripSignatures aplicado DIRETO na origem: os 2
    // caminhos usam o MESMO mecanismo agora (ver PdfEditor.ExtractPages), então o render deve ser
    // IDÊNTICO (0 pixels de diferença), não só "parecido".
    public void ExtractPages_FromSignedDocument_RemovesVisualStampFromRender()
    {
        var extracted = Editor.ExtractPages(Fixtures.Carimbo(), new[] { 0 });
        var strippedDirect = Editor.StripSignatures(Fixtures.Carimbo());

        using var rendererOriginal = new PdfDocumentRenderer(Fixtures.Carimbo());
        using var rendererExtracted = new PdfDocumentRenderer(extracted);
        using var rendererStrippedDirect = new PdfDocumentRenderer(strippedDirect);

        var pageOriginal = rendererOriginal.RenderPage(0, 1.0);
        var pageExtracted = rendererExtracted.RenderPage(0, 1.0);
        var pageStrippedDirect = rendererStrippedDirect.RenderPage(0, 1.0);

        int diffVsOriginal = CountDifferingPixels(pageOriginal, pageExtracted);
        Assert.True(diffVsOriginal > 1000,
            $"carimbo visual sobreviveu à extração: só {diffVsOriginal} pixels diferentes vs. o original assinado");

        Assert.Equal(0, CountDifferingPixels(pageStrippedDirect, pageExtracted));
    }

    // --- InsertPages ---------------------------------------------------

    [Theory] // início, meio e FIM (atIndex == pageCount do alvo, "inserir depois da última página")
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(30)]
    public void InsertPages_InsertsAllSourcePagesAtPosition(int atIndex)
    {
        var result = Editor.InsertPages(Fixtures.ThirtyPages(), Fixtures.A4(), atIndex);

        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(31, renderer.PageCount);
        Assert.Contains("Fixture A4", renderer.GetTextPage(atIndex).Text);
        if (atIndex > 0) // a página imediatamente ANTES continua sendo a original daquela posição
            Assert.Contains($"pagina {atIndex}", renderer.GetTextPage(atIndex - 1).Text);
        if (atIndex < 30) // a página imediatamente DEPOIS é a original que estava em atIndex antes
            Assert.Contains($"pagina {atIndex + 1}", renderer.GetTextPage(atIndex + 1).Text);
    }

    [Fact] // pageCount do alvo é 30; 31 já é 1 além do "fim" válido (30 == inserir no fim)
    public void InsertPages_InvalidAtIndex_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Editor.InsertPages(Fixtures.ThirtyPages(), Fixtures.A4(), 31));
    }

    [Fact]
    public void InsertPages_OnSignedDocument_Throws()
    {
        Assert.Throws<PdfSignedDocumentException>(() => Editor.InsertPages(Fixtures.Carimbo(), Fixtures.A4(), 0));
    }

    [Fact] // gate é só no ALVO — a ORIGEM pode estar assinada (decisão registrada em Contract.cs):
    // inserir páginas de um PDF assinado é uma LEITURA da origem, nunca uma edição dela. Opus
    // review, item 2 (potencialmente Critical): o invariante `HasSignatures(resultado) == false`
    // precisa ser ASSERTADO, não só presumido — confirmado empiricamente (probe project isolado,
    // ver task-2-report.md, seção "## Fix") que `CopyPagesTo` (o mecanismo de InsertPages) já não
    // carrega o AcroForm/campo de assinatura da origem pro alvo, então a asserção abaixo passa SEM
    // precisar de nenhuma rede StripSignatures adicional no código de produção — ver relatório para
    // qual caminho foi confirmado.
    public void InsertPages_SignedSource_Works()
    {
        var result = Editor.InsertPages(Fixtures.A4(), Fixtures.Carimbo(), 0);

        Assert.False(Editor.HasSignatures(result));
        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(2, renderer.PageCount);
    }

    [Fact] // C1: equivalência ENGINE-LEVEL em vez de pixel-diff próprio (mesmo mecanismo, ver comentário
    // da seção C1 acima) — a página inserida (índice 0, `atIndex=0`) precisa renderizar IDÊNTICA ao
    // resultado de `StripSignatures` aplicado direto na mesma origem assinada.
    public void InsertPages_SignedSource_RemovesVisualStampFromRender()
    {
        var result = Editor.InsertPages(Fixtures.A4(), Fixtures.Carimbo(), 0);
        var strippedDirect = Editor.StripSignatures(Fixtures.Carimbo());

        using var rendererResult = new PdfDocumentRenderer(result);
        using var rendererStrippedDirect = new PdfDocumentRenderer(strippedDirect);

        var pageResult = rendererResult.RenderPage(0, 1.0);
        var pageStrippedDirect = rendererStrippedDirect.RenderPage(0, 1.0);

        Assert.Equal(0, CountDifferingPixels(pageStrippedDirect, pageResult));
    }

    // --- MergeDocuments ---------------------------------------------------

    [Fact]
    public void MergeDocuments_OrderPreserved_ContentVerified()
    {
        var docB = Editor.ExtractPages(Fixtures.ThirtyPages(), new[] { 0, 1 }); // pagina1, pagina2
        var result = Editor.MergeDocuments(new[] { docB, Fixtures.A4() });

        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(3, renderer.PageCount);
        Assert.Contains("pagina 1", renderer.GetTextPage(0).Text);
        Assert.Contains("pagina 2", renderer.GetTextPage(1).Text);
        Assert.Contains("Fixture A4", renderer.GetTextPage(2).Text);
    }

    [Fact] // pura checagem de INPUT, antes de abrir qualquer PDF.
    public void MergeDocuments_EmptyList_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Editor.MergeDocuments(Array.Empty<byte[]>()));
    }

    [Fact] // decisão SEM gate (Contract.cs): MergeDocuments aceita fontes assinadas de propósito —
    // achado empírico (probe project isolado, ver task-2-report.md): PdfMerger não preserva o
    // AcroForm da fonte, o resultado já sai sem assinatura reconhecida por SignatureUtil; mesmo
    // assim o resultado passa por StripSignatures como rede de segurança defensiva antes de voltar
    // (ver PdfEditor.MergeDocuments) — este teste prova o INVARIANTE fim-a-fim, não o mecanismo
    // interno.
    public void MergeDocuments_WithSignedInput_ProducesUnsignedResult()
    {
        var result = Editor.MergeDocuments(new[] { Fixtures.Carimbo(), Fixtures.A4() });

        Assert.False(Editor.HasSignatures(result));
        using var renderer = new PdfDocumentRenderer(result);
        Assert.Equal(2, renderer.PageCount);
    }

    [Fact] // TESTE LOAD-BEARING (C1, mesmo raciocínio de ExtractPages_..._RemovesVisualStampFromRender
    // acima): fixture-carimbo como PRIMEIRA entrada -> página 0 do resultado mesclado É a página do
    // Carimbo; precisa diferir do original assinado em MILHARES de pixels, não só HasSignatures==false.
    public void MergeDocuments_WithSignedInput_RemovesVisualStampFromRender()
    {
        var merged = Editor.MergeDocuments(new[] { Fixtures.Carimbo(), Fixtures.A4() });

        using var rendererOriginal = new PdfDocumentRenderer(Fixtures.Carimbo());
        using var rendererMerged = new PdfDocumentRenderer(merged);

        var pageOriginal = rendererOriginal.RenderPage(0, 1.0);
        var pageMerged = rendererMerged.RenderPage(0, 1.0);

        int diff = CountDifferingPixels(pageOriginal, pageMerged);
        Assert.True(diff > 1000,
            $"carimbo visual sobreviveu ao merge: só {diff} pixels diferentes vs. o original assinado");
    }

    // --- SplitByRanges ---------------------------------------------------

    [Fact]
    public void SplitByRanges_EachOutputPageCountAndContentVerified()
    {
        var results = Editor.SplitByRanges(
            Fixtures.ThirtyPages(), new (int from, int to)[] { (0, 9), (10, 19), (20, 29) });

        Assert.Equal(3, results.Count);
        using var r0 = new PdfDocumentRenderer(results[0]);
        using var r1 = new PdfDocumentRenderer(results[1]);
        using var r2 = new PdfDocumentRenderer(results[2]);
        Assert.Equal(10, r0.PageCount);
        Assert.Equal(10, r1.PageCount);
        Assert.Equal(10, r2.PageCount);
        // substring EXATA "pagina 1 de 30" (não só "pagina 1") — risco REAL aqui, não só teórico:
        // r0 tem 10 páginas e a página índice9 É "pagina 10 de 30", então "pagina 1" solto passaria
        // mesmo se um bug pousasse o conteúdo errado no índice0 (Opus review, riders).
        Assert.Contains("pagina 1 de 30", r0.GetTextPage(0).Text);
        Assert.Contains("pagina 10", r0.GetTextPage(9).Text);
        Assert.Contains("pagina 11", r1.GetTextPage(0).Text);
        Assert.Contains("pagina 20", r1.GetTextPage(9).Text);
        Assert.Contains("pagina 21", r2.GetTextPage(0).Text);
        Assert.Contains("pagina 30", r2.GetTextPage(9).Text);
    }

    [Fact]
    public void SplitByRanges_FromGreaterThanTo_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => Editor.SplitByRanges(Fixtures.ThirtyPages(), new (int from, int to)[] { (5, 2) }));
    }

    [Fact]
    public void SplitByRanges_OutOfRangeIndex_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Editor.SplitByRanges(Fixtures.ThirtyPages(), new (int from, int to)[] { (0, 999) }));
    }

    [Fact] // Opus review, item 3: higiene de assinatura da saída é um invariante ASSERTADO, não só
    // presumido — TODA saída de SplitByRanges precisa ter HasSignatures == false, mesmo vindo de
    // uma fonte assinada de verdade (fixture-carimbo, 1 assinatura PAdES real, 1 página só ->
    // range único (0,0), mas a asserção é escrita para valer sobre CADA elemento da lista, não só
    // o primeiro, caso o fixture um dia ganhe mais páginas).
    public void SplitByRanges_FromSignedDocument_ProducesUnsignedResults()
    {
        var results = Editor.SplitByRanges(Fixtures.Carimbo(), new (int from, int to)[] { (0, 0) });

        Assert.NotEmpty(results);
        foreach (var result in results)
            Assert.False(Editor.HasSignatures(result));
    }

    [Fact] // C1: equivalência ENGINE-LEVEL (mesmo espírito de InsertPages_..._RemovesVisualStampFromRender
    // acima) — a única parte resultante (range (0,0) na fixture de 1 página) precisa renderizar
    // IDÊNTICA ao resultado de StripSignatures aplicado direto na mesma origem assinada.
    public void SplitByRanges_FromSignedDocument_RemovesVisualStampFromRender()
    {
        var results = Editor.SplitByRanges(Fixtures.Carimbo(), new (int from, int to)[] { (0, 0) });
        var strippedDirect = Editor.StripSignatures(Fixtures.Carimbo());

        using var rendererResult = new PdfDocumentRenderer(Assert.Single(results));
        using var rendererStrippedDirect = new PdfDocumentRenderer(strippedDirect);

        var pageResult = rendererResult.RenderPage(0, 1.0);
        var pageStrippedDirect = rendererStrippedDirect.RenderPage(0, 1.0);

        Assert.Equal(0, CountDifferingPixels(pageStrippedDirect, pageResult));
    }

    // --- Task 1 (Plano 3c): motor de formulários AcroForm ----------------------------------------
    // fixture-formulario: página 0 = texto "nome" ("Fulano de Tal"), multilinha "observacoes",
    // checkbox "aceito" (Off); página 1 = radio "genero" (M/F, valor M), combo "estado" (SP/RJ/MG,
    // valor RJ), texto readonly "protocolo" ("PROTO-12345"). Ver Fixtures.Formulario().

    // --- ReadFormFields ---------------------------------------------------

    [Fact] // doc sem AcroForm nenhum -> lista vazia, NUNCA null (contrato explícito em Contract.cs)
    public void ReadFormFields_NoAcroForm_ReturnsEmptyListNotNull()
    {
        var fields = Editor.ReadFormFields(Fixtures.A4());
        Assert.NotNull(fields);
        Assert.Empty(fields);
    }

    [Fact] // review (Task 1 fix): fixture regenerada com 2 campos a mais (push button "botao" +
    // placeholder de assinatura NÃO assinado "assinatura1") — os 2 precisam virar FormFieldType.Other.
    public void ReadFormFields_FixtureFormulario_ReturnsAllEightFieldsWithExpectedShape()
    {
        var fields = Editor.ReadFormFields(Fixtures.Formulario());
        Assert.Equal(8, fields.Count);

        var nome = fields.Single(f => f.Name == "nome");
        Assert.Equal(FormFieldType.Text, nome.Type);
        Assert.Equal("Fulano de Tal", nome.Value);
        Assert.Empty(nome.Options);
        Assert.Equal(0, nome.PageIndex);
        Assert.False(nome.IsReadOnly);
        Assert.NotNull(nome.WidgetRect);

        var obs = fields.Single(f => f.Name == "observacoes");
        Assert.Equal(FormFieldType.Text, obs.Type);
        Assert.Equal("linha 1\nlinha 2", obs.Value);
        Assert.Equal(0, obs.PageIndex);

        var aceito = fields.Single(f => f.Name == "aceito");
        Assert.Equal(FormFieldType.Checkbox, aceito.Type);
        Assert.Equal("Off", aceito.Value);
        Assert.Equal(new[] { "Yes" }, aceito.Options);
        Assert.Equal(0, aceito.PageIndex);

        var genero = fields.Single(f => f.Name == "genero");
        Assert.Equal(FormFieldType.Radio, genero.Type);
        Assert.Equal("M", genero.Value);
        Assert.Equal(new[] { "M", "F" }, genero.Options);
        Assert.Equal(1, genero.PageIndex); // página 1 — prova que PageIndex é exercitado além de 0

        var estado = fields.Single(f => f.Name == "estado");
        Assert.Equal(FormFieldType.Combo, estado.Type);
        Assert.Equal("RJ", estado.Value);
        Assert.Equal(new[] { "SP", "RJ", "MG" }, estado.Options);
        Assert.Equal(1, estado.PageIndex);

        var protocolo = fields.Single(f => f.Name == "protocolo");
        Assert.Equal(FormFieldType.Text, protocolo.Type);
        Assert.Equal("PROTO-12345", protocolo.Value);
        Assert.True(protocolo.IsReadOnly);
        Assert.Equal(1, protocolo.PageIndex);

        var botao = fields.Single(f => f.Name == "botao");
        Assert.Equal(FormFieldType.Other, botao.Type);
        Assert.Equal(1, botao.PageIndex);

        var assinatura = fields.Single(f => f.Name == "assinatura1");
        Assert.Equal(FormFieldType.Other, assinatura.Type);
        Assert.Equal(1, assinatura.PageIndex);
    }

    [Fact] // review (item 2): contrato XFA PINADO — ReadFormFields chama PdfAcroForm.GetAcroForm
    // internamente, que LANÇA PdfException ao tentar parsear /XFA como XML (ver HIPÓTESE de HasXfa em
    // Contract.cs); capturado pelo catch(ITextException) genérico e envolvido em PdfEditingException
    // — mesmo canal neutro de qualquer outra falha do iText. Chame HasXfa ANTES pra evitar isso.
    public void ReadFormFields_FixtureXfa_ThrowsPdfEditingException()
    {
        Assert.Throws<PdfEditingException>(() => Editor.ReadFormFields(Fixtures.Xfa()));
    }

    // --- HasXfa ---------------------------------------------------

    [Fact]
    public void HasXfa_FixtureXfa_IsTrue()
    {
        Assert.True(Editor.HasXfa(Fixtures.Xfa()));
    }

    [Theory] // false em QUALQUER doc sem entrada /XFA — inclusive um com AcroForm "normal" (formulario)
    // ou com campo de assinatura (carimbo), pra provar que o detector não dá falso-positivo em
    // qualquer AcroForm, só no que de fato tem a chave /XFA.
    [InlineData(nameof(Fixtures.A4))]
    [InlineData(nameof(Fixtures.Formulario))]
    [InlineData(nameof(Fixtures.Carimbo))]
    public void HasXfa_DocsWithoutXfaEntry_IsFalse(string fixtureName)
    {
        byte[] pdf = fixtureName switch
        {
            nameof(Fixtures.A4) => Fixtures.A4(),
            nameof(Fixtures.Formulario) => Fixtures.Formulario(),
            nameof(Fixtures.Carimbo) => Fixtures.Carimbo(),
            _ => throw new InvalidOperationException(),
        };
        Assert.False(Editor.HasXfa(pdf));
    }

    // --- SetFormFields: round-trip set->read, 1 teste por tipo ---------------------------------

    [Fact]
    public void SetFormFields_Text_RoundTrips()
    {
        var result = Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["nome"] = "Novo Nome Aqui" });
        var field = Editor.ReadFormFields(result).Single(f => f.Name == "nome");
        Assert.Equal("Novo Nome Aqui", field.Value);
    }

    [Fact]
    public void SetFormFields_Checkbox_RoundTrips()
    {
        var result = Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["aceito"] = "Yes" });
        var field = Editor.ReadFormFields(result).Single(f => f.Name == "aceito");
        Assert.Equal("Yes", field.Value);
    }

    [Fact] // "Off" continua sendo um valor legítimo de desmarcar (não está em Options, mas não é
    // validado contra Options — ver XML doc de FormFieldData.Options/SetFormFields em Contract.cs)
    public void SetFormFields_Checkbox_CanBeSetBackToOff()
    {
        var checkedResult = Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["aceito"] = "Yes" });
        var uncheckedResult = Editor.SetFormFields(checkedResult,
            new Dictionary<string, string> { ["aceito"] = "Off" });
        var field = Editor.ReadFormFields(uncheckedResult).Single(f => f.Name == "aceito");
        Assert.Equal("Off", field.Value);
    }

    [Fact] // review (rider): simetria com o checkbox (Off/Yes nos 2 sentidos, ver
    // SetFormFields_Checkbox_CanBeSetBackToOff) — radio também escreve os 2 export values através de
    // SetFormFields explicitamente ("M", mesmo já sendo o valor da fixture, prova que SetFormFields
    // consegue ESCREVER e não só herdar o default; depois "F"), com leitura de volta em CADA passo.
    public void SetFormFields_Radio_RoundTrips()
    {
        var setM = Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["genero"] = "M" });
        Assert.Equal("M", Editor.ReadFormFields(setM).Single(f => f.Name == "genero").Value);

        var setF = Editor.SetFormFields(setM,
            new Dictionary<string, string> { ["genero"] = "F" });
        Assert.Equal("F", Editor.ReadFormFields(setF).Single(f => f.Name == "genero").Value);
    }

    [Fact]
    public void SetFormFields_Combo_RoundTrips()
    {
        var result = Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["estado"] = "MG" });
        var field = Editor.ReadFormFields(result).Single(f => f.Name == "estado");
        Assert.Equal("MG", field.Value);
    }

    [Fact] // vários campos de tipos diferentes na MESMA chamada — prova que a validação/aplicação em
    // 2 passos (validar tudo, depois aplicar tudo) não perde nenhuma entrada
    public void SetFormFields_MultipleFieldsAtOnce_AllApply()
    {
        var result = Editor.SetFormFields(Fixtures.Formulario(), new Dictionary<string, string>
        {
            ["nome"] = "Ciclano",
            ["aceito"] = "Yes",
            ["genero"] = "F",
            ["estado"] = "SP",
        });
        var fields = Editor.ReadFormFields(result);
        Assert.Equal("Ciclano", fields.Single(f => f.Name == "nome").Value);
        Assert.Equal("Yes", fields.Single(f => f.Name == "aceito").Value);
        Assert.Equal("F", fields.Single(f => f.Name == "genero").Value);
        Assert.Equal("SP", fields.Single(f => f.Name == "estado").Value);
    }

    // --- SetFormFields: validação ---------------------------------------------------

    [Fact]
    public void SetFormFields_NonexistentField_ThrowsArgumentExceptionNamingField()
    {
        var ex = Assert.Throws<ArgumentException>(() => Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["campo-que-nao-existe"] = "valor" }));
        Assert.Contains("campo-que-nao-existe", ex.Message);
    }

    [Fact]
    public void SetFormFields_ReadOnlyField_ThrowsArgumentExceptionNamingField()
    {
        var ex = Assert.Throws<ArgumentException>(() => Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["protocolo"] = "outro valor" }));
        Assert.Contains("protocolo", ex.Message);
    }

    [Fact]
    public void SetFormFields_InvalidComboValue_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["estado"] = "XX" }));
        Assert.Contains("estado", ex.Message);
        Assert.Contains("XX", ex.Message);
    }

    [Fact]
    public void SetFormFields_InvalidRadioValue_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["genero"] = "X" }));
        Assert.Contains("genero", ex.Message);
    }

    [Fact] // review (Important, item 1): push button é FormFieldType.Other — SEM esta recusa,
    // field.SetValue(string) não lança, mas o valor é DESCARTADO silenciosamente (GetValueAsString()
    // continua vazio depois — perda silenciosa, sonda ao vivo confirmou, ver task-1-report.md "## Fix").
    public void SetFormFields_PushButton_ThrowsArgumentExceptionNamingField()
    {
        var ex = Assert.Throws<ArgumentException>(() => Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["botao"] = "qualquer valor" }));
        Assert.Contains("botao", ex.Message);
    }

    [Fact] // review (Important, item 1): placeholder de assinatura AINDA NÃO assinado também é
    // FormFieldType.Other e é ALCANÇÁVEL (HasSignatures/o gate de doc assinado não bloqueia — sonda
    // ao vivo: SignatureUtil.GetSignatureNames().Count == 0 pra um /Sig sem assinatura de verdade).
    // SEM esta recusa, field.SetValue(string) grava a string CRUA em /V — risco real porque o Plano 4
    // vai assinar esses mesmos placeholders depois.
    public void SetFormFields_UnsignedSignaturePlaceholder_ThrowsArgumentExceptionNamingField()
    {
        Assert.False(Editor.HasSignatures(Fixtures.Formulario())); // confirma que o gate de doc
        // assinado não intercepta antes — a recusa testada aqui É a defesa real, não redundante com o gate.
        var ex = Assert.Throws<ArgumentException>(() => Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["assinatura1"] = "valor-que-nao-deveria-ir-pro-/V" }));
        Assert.Contains("assinatura1", ex.Message);
    }

    [Fact] // mesmo espírito de RotatePages/DeletePages/SplitByRanges: TODAS as entradas validadas
    // ANTES de escrever qualquer campo — 1 entrada válida + 1 inválida na MESMA chamada não deixa a
    // válida aplicada (o documento nunca chega a ser devolvido, a exceção sobe antes do `return`).
    public void SetFormFields_OneValidOneInvalid_ThrowsAndAppliesNeither()
    {
        Assert.Throws<ArgumentException>(() => Editor.SetFormFields(Fixtures.Formulario(),
            new Dictionary<string, string> { ["nome"] = "Nao Deveria Aplicar", ["estado"] = "XX" }));

        // fixture original não foi alterada (SetFormFields recebe bytes, nunca muta in-place)
        var original = Editor.ReadFormFields(Fixtures.Formulario()).Single(f => f.Name == "nome");
        Assert.Equal("Fulano de Tal", original.Value);
    }

    [Fact] // Gate por op (mesmo padrão de RotatePages_OnSignedDocument_Throws etc.)
    public void SetFormFields_OnSignedDocument_Throws()
    {
        Assert.Throws<PdfSignedDocumentException>(() => Editor.SetFormFields(Fixtures.Carimbo(),
            new Dictionary<string, string> { ["qualquer"] = "valor" }));
    }

    [Fact] // review (item 2): contrato XFA pinado — mesmo achado de ReadFormFields_FixtureXfa_...
    // (PdfAcroForm.GetAcroForm lança ao parsear /XFA, PdfEditingException é o canal neutro).
    public void SetFormFields_FixtureXfa_ThrowsPdfEditingException()
    {
        Assert.Throws<PdfEditingException>(() => Editor.SetFormFields(Fixtures.Xfa(),
            new Dictionary<string, string> { ["qualquer"] = "valor" }));
    }

    // --- SetFormFields: prova de RENDERIZAÇÃO (exemplar: AddAnnotation_ImageStamp_RendersNonBlankInStampRegion) ---

    [Fact] // checkbox "aceito" começa DESMARCADO (Off, sem aparência visível) na fixture — depois de
    // SetFormFields("Yes"), a região do widget precisa ter pixels pintados de verdade (não basta o
    // dicionário cru ter /V=Yes — o valor precisa aparecer no render, motor INDEPENDENTE do iText que
    // escreveu, PDFium via mPdf.Rendering).
    public void SetFormFields_Checkbox_ValueAppearsInRender()
    {
        var before = Fixtures.Formulario();
        var after = Editor.SetFormFields(before, new Dictionary<string, string> { ["aceito"] = "Yes" });

        using var rendererBefore = new PdfDocumentRenderer(before);
        using var rendererAfter = new PdfDocumentRenderer(after);
        var pageBefore = rendererBefore.RenderPage(0, 1.0);
        var pageAfter = rendererAfter.RenderPage(0, 1.0);

        // região do widget "aceito": rect (50,600)-(70,620) em pontos, escala 1.0 -> px direto
        // (mesma convenção de AddAnnotation_ImageStamp_RendersNonBlankInStampRegion).
        int h = pageAfter.HeightPx, w = pageAfter.WidthPx;
        int paintedBefore = CountPaintedPixelsInRegion(pageBefore, h, 50, 70, 600, 620);
        int paintedAfter = CountPaintedPixelsInRegion(pageAfter, h, 50, 70, 600, 620);

        // Medido (ver task-1-report.md): antes=0 pixels pintados, depois=83 — limiar 20 folgado abaixo
        // do valor real, ainda longe o bastante de 0 pra não confundir com ruído de antialiasing.
        Assert.True(paintedBefore < 5, $"checkbox já aparecia pintado ANTES de SetFormFields: {paintedBefore} pixels");
        Assert.True(paintedAfter > 20,
            $"checkbox marcado não renderizou: só {paintedAfter} pixels pintados na região (antes: {paintedBefore})");
    }

    private static int CountPaintedPixelsInRegion(RenderedPage page, int heightPx, int xMin, int xMax, int yMinPt, int yMaxPt)
    {
        int painted = 0;
        for (int y = heightPx - yMaxPt; y < heightPx - yMinPt; y++)
            for (int x = xMin; x < xMax; x++)
            {
                int i = (y * page.WidthPx + x) * 4;
                if (page.Bgra[i] < 250 || page.Bgra[i + 1] < 250 || page.Bgra[i + 2] < 250) painted++;
            }
        return painted;
    }

    // --- FlattenForm ---------------------------------------------------

    [Fact]
    public void FlattenForm_RemovesAllFields_ReadFormFieldsIsEmptyAfter()
    {
        var result = Editor.FlattenForm(Fixtures.Formulario());
        Assert.Empty(Editor.ReadFormFields(result));
    }

    [Fact] // doc sem AcroForm nenhum -> no-op, nunca lança (mesmo espírito de StripSignatures)
    public void FlattenForm_NoAcroForm_IsNoOpAndDoesNotThrow()
    {
        var result = Editor.FlattenForm(Fixtures.A4());
        Assert.Empty(Editor.ReadFormFields(result));
    }

    [Fact] // Gate por op
    public void FlattenForm_OnSignedDocument_Throws()
    {
        Assert.Throws<PdfSignedDocumentException>(() => Editor.FlattenForm(Fixtures.Carimbo()));
    }

    [Fact] // review (item 2): contrato XFA pinado — mesmo achado de ReadFormFields/SetFormFields
    // contra fixture-xfa (PdfAcroForm.GetAcroForm lança ao parsear /XFA, PdfEditingException é o
    // canal neutro).
    public void FlattenForm_FixtureXfa_ThrowsPdfEditingException()
    {
        Assert.Throws<PdfEditingException>(() => Editor.FlattenForm(Fixtures.Xfa()));
    }

    [Fact] // flatten ≈ preenchido: MESMA aparência visual (o valor já estava renderizado antes de
    // achatar — flatten só "imprime" a appearance stream existente na página, não muda o desenho),
    // tolerância pequena e MEDIDA (não 0 cravado — achatar troca a ORIGEM do desenho de
    // annotation-appearance pra conteúdo de página, então alguma diferença de composição/anti-
    // aliasing residual é aceitável, mas precisa ser MEDIDA, não presumida).
    public void FlattenForm_PreservesAppearance_WithinSmallMeasuredPixelTolerance()
    {
        var filled = Editor.SetFormFields(Fixtures.Formulario(), new Dictionary<string, string>
        {
            ["nome"] = "Preenchido Assim",
            ["aceito"] = "Yes",
            ["genero"] = "F",
            ["estado"] = "MG",
        });
        var flattened = Editor.FlattenForm(filled);

        using var rendererFilled = new PdfDocumentRenderer(filled);
        using var rendererFlattened = new PdfDocumentRenderer(flattened);

        int diffPage0 = CountDifferingPixels(rendererFilled.RenderPage(0, 1.0), rendererFlattened.RenderPage(0, 1.0));
        int diffPage1 = CountDifferingPixels(rendererFilled.RenderPage(1, 1.0), rendererFlattened.RenderPage(1, 1.0));
        int totalDiff = diffPage0 + diffPage1;

        // Medido (ver task-1-report.md): 0 pixels de diferença nas 2 páginas (página0=0, página1=0)
        // — flatten reproduziu a aparência PIXEL-IDÊNTICA ao preenchido. Limiar 100 (não 0 cravado):
        // margem pequena e documentada pra não tornar o teste frágil a uma variação legítima de
        // antialiasing numa versão futura do iText/PDFium, sem deixar de detectar uma regressão real
        // (a diferença REAL de uma aparência quebrada seria de milhares de pixels, não dezenas).
        Assert.True(totalDiff < 100,
            $"flatten mudou a aparência mais do que a tolerância esperada: {totalDiff} pixels diferentes (página0={diffPage0}, página1={diffPage1})");
    }

    // --- Task 1 (Plano 7): IsSupportedImage --------------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]                                     // JPEG (SOI + APP0/JFIF)
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 })]                                     // JPEG (SOI + APP1/EXIF)
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]              // PNG (assinatura completa de 8 bytes)
    public void IsSupportedImage_JpegOrPngMagicBytes_IsTrue(byte[] bytes)
    {
        Assert.True(Editor.IsSupportedImage(bytes));
    }

    [Theory]
    [InlineData(new byte[] { 0x42, 0x4D, 0x00, 0x00 })]                     // BMP ("BM")
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38 })]                     // GIF ("GIF8")
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 })]                     // PDF ("%PDF") — não é imagem
    public void IsSupportedImage_OtherFormats_IsFalse(byte[] bytes)
    {
        Assert.False(Editor.IsSupportedImage(bytes));
    }

    [Fact] // nulo/vazio/curto demais pra ter magic bytes — nunca lança, só devolve false
    public void IsSupportedImage_NullOrEmptyOrTooShort_IsFalse()
    {
        Assert.False(Editor.IsSupportedImage(null));
        Assert.False(Editor.IsSupportedImage(Array.Empty<byte>()));
        Assert.False(Editor.IsSupportedImage(new byte[] { 0xFF }));
    }

    [Fact]
    public void IsSupportedImage_RealJpegFixture_IsTrue() => Assert.True(Editor.IsSupportedImage(Fixtures.Foto()));

    [Fact]
    public void IsSupportedImage_RealPngFixture_IsTrue() => Assert.True(Editor.IsSupportedImage(Fixtures.Transparente()));

    // --- Task 1 (Plano 7): ImageToPdf --------------------------------------------------------
    //
    // Cores de canto conhecidas das fixtures (ver Fixtures.Foto()/FotoExif90(), geradas via probe —
    // task-1-report.md): TL=vermelho puro, TR=verde/lime puro, BL=azul puro, BR=amarelo puro.
    // Tolerância MEDIDA (probe, JPEG qualidade 90): desvio real de 0-1 por canal nos cantos — 30 é
    // uma margem folgada (~12x o desvio real) que ainda reprova um bug real de canal trocado (~255).
    private const int JpegColorTolerance = 30;
    private static readonly (byte R, byte G, byte B) Red = (255, 0, 0);
    private static readonly (byte R, byte G, byte B) Lime = (0, 255, 0);
    private static readonly (byte R, byte G, byte B) Blue = (0, 0, 255);
    private static readonly (byte R, byte G, byte B) Yellow = (255, 255, 0);

    private static void AssertCornerColor(RenderedPage page, int x, int y, (byte R, byte G, byte B) expected, int tolerance)
    {
        x = Math.Clamp(x, 0, page.WidthPx - 1); y = Math.Clamp(y, 0, page.HeightPx - 1);
        int i = (y * page.WidthPx + x) * 4;
        byte b = page.Bgra[i], g = page.Bgra[i + 1], r = page.Bgra[i + 2];
        Assert.True(Math.Abs(r - expected.R) <= tolerance && Math.Abs(g - expected.G) <= tolerance && Math.Abs(b - expected.B) <= tolerance,
            $"cor em ({x},{y}) fora da tolerância: esperado ~({expected.R},{expected.G},{expected.B}), obtido ({r},{g},{b})");
    }

    [Fact] // 200x150px @ 96dpi -> pt = px*72/96 = 150x112.5pt (mesma fórmula documentada em IPdfEditor.ImageToPdf)
    public void ImageToPdf_Jpeg_ProducesOnePageOfCorrectSize()
    {
        var pdf = Editor.ImageToPdf(Fixtures.Foto());
        using var r = new PdfDocumentRenderer(pdf);
        Assert.Equal(1, r.PageCount);
        var size = r.GetPageSize(0);
        Assert.Equal(150.0, size.WidthPt, 0.5);
        Assert.Equal(112.5, size.HeightPt, 0.5);
    }

    [Fact] // px oracle: canto renderizado bate com a cor CONHECIDA da fixture (motor de render
    // INDEPENDENTE do iText que escreveu — mesmo padrão de AddAnnotation_ImageStamp_RendersNonBlankInStampRegion).
    public void ImageToPdf_Jpeg_RendersMatchingKnownCornerColors()
    {
        var pdf = Editor.ImageToPdf(Fixtures.Foto());
        using var r = new PdfDocumentRenderer(pdf);
        var page = r.RenderPage(0, 1.0);
        AssertCornerColor(page, 5, 5, Red, JpegColorTolerance);
        AssertCornerColor(page, page.WidthPx - 5, 5, Lime, JpegColorTolerance);
        AssertCornerColor(page, 5, page.HeightPx - 5, Blue, JpegColorTolerance);
        AssertCornerColor(page, page.WidthPx - 5, page.HeightPx - 5, Yellow, JpegColorTolerance);
    }

    [Fact] // TESTE CENTRAL DA ARMADILHA (brief, Plano 7 Task 1): fixture-foto-exif90 tem os MESMOS
    // pixels de fixture-foto, só que pré-rotacionados 90° CCW + EXIF Orientation=6. iText NÃO honra
    // o tag (achado empírico, ver task-1-report.md) — ImageToPdf precisa ler o tag e corrigir via
    // MATRIZ DE TRANSFORMAÇÃO no desenho da imagem (revisão pré-merge, I3 — NUNCA via
    // `PdfPage.SetRotation`, ver comentário de `GetPageRotations` abaixo), senão a foto abre DE LADO.
    // Cores de canto esperadas são as MESMAS de ImageToPdf_Jpeg_RendersMatchingKnownCornerColors
    // (TL=vermelho etc.) — provando que abriu EM PÉ.
    public void ImageToPdf_JpegWithExifOrientation6_OpensUpright()
    {
        var pdf = Editor.ImageToPdf(Fixtures.FotoExif90());
        using var r = new PdfDocumentRenderer(pdf);
        var page = r.RenderPage(0, 1.0);
        AssertCornerColor(page, 5, 5, Red, JpegColorTolerance);
        AssertCornerColor(page, page.WidthPx - 5, 5, Lime, JpegColorTolerance);
        AssertCornerColor(page, 5, page.HeightPx - 5, Blue, JpegColorTolerance);
        AssertCornerColor(page, page.WidthPx - 5, page.HeightPx - 5, Yellow, JpegColorTolerance);
    }

    [Fact] // dimensões finais (pós-correção) devem bater com a foto "em pé" (150x112.5pt), NÃO com os
    // pixels crus armazenados (que seriam 112.5x150pt, de lado) — a página NASCE direto no tamanho
    // corrigido (ver GetPageRotations abaixo: /Rotate nunca é usado pra chegar nesse tamanho).
    public void ImageToPdf_JpegWithExifOrientation6_FinalPageSizeMatchesUprightOrientation()
    {
        var pdf = Editor.ImageToPdf(Fixtures.FotoExif90());
        using var r = new PdfDocumentRenderer(pdf);
        var size = r.GetPageSize(0);
        Assert.Equal(150.0, size.WidthPt, 0.5);
        Assert.Equal(112.5, size.HeightPt, 0.5);
    }

    [Fact] // I3 CRÍTICO da revisão pré-merge (task-1-report.md "## Fix" — "o /Rotate collision", o
    // "flagship-breaking landmine"): a versão anterior corrigia o EXIF via `PdfPage.SetRotation`, o
    // que fazia CADA foto de celular convertida com Orientation != 1 nascer como "página girada" — e
    // o gate de rotação do Plano 3b (`GetPageRotations`/interação de anotação, ver Contract.cs)
    // DESLIGA anotação/assinatura em qualquer página girada. Isso quebraria o fluxo real "foto do
    // WhatsApp -> assinar". Fix por construção (matriz de transformação, não `/Rotate`): asserção
    // LOAD-BEARING — `GetPageRotations(convertido)[0] == 0` MESMO numa foto com EXIF Orientation=6,
    // provando que o gate do Plano 3b continua ABERTO (anotação/assinatura permanecem possíveis).
    public void ImageToPdf_JpegWithExifOrientation6_PageRotationStaysZero_AnnotationGateStaysOpen()
    {
        var pdf = Editor.ImageToPdf(Fixtures.FotoExif90());
        var rotations = Editor.GetPageRotations(pdf);
        Assert.Equal(0, Assert.Single(rotations));
    }

    [Fact] // 100x100px @ 96dpi -> 75x75pt
    public void ImageToPdf_Png_ProducesOnePageOfCorrectSize()
    {
        var pdf = Editor.ImageToPdf(Fixtures.Transparente());
        using var r = new PdfDocumentRenderer(pdf);
        Assert.Equal(1, r.PageCount);
        var size = r.GetPageSize(0);
        Assert.Equal(75.0, size.WidthPt, 0.5);
        Assert.Equal(75.0, size.HeightPt, 0.5);
    }

    [Fact] // fixture-transparente: "buraco" alpha=0 sobre RGB preto deliberado (0,0)-(50,50) — se o
    // SMask não fosse honrado, renderizaria PRETO; quadrado opaco verde-escuro (50,0)-(100,50) prova
    // que a imagem FOI desenhada (não é só "página em branco passando por acidente").
    public void ImageToPdf_TransparentPng_HoleShowsWhiteBackground_OpaqueRegionShowsColor()
    {
        var pdf = Editor.ImageToPdf(Fixtures.Transparente());
        using var r = new PdfDocumentRenderer(pdf);
        var page = r.RenderPage(0, 1.0);
        // buraco transparente: amostra em torno do centro da região (25,25) do PNG 100x100 -> mesma
        // escala 0.75 (96dpi->72pt) da página 75x75pt, então (25,25)px da imagem cai em ~(19,19)pt/px
        // renderizado — usar um ponto interno (18,18) evita a borda de antialiasing do quadrado.
        AssertCornerColor(page, 18, 18, (255, 255, 255), 5); // branco = fundo da página, SMask honrado
        // quadrado opaco verde-escuro (era (50,0)-(100,50) na imagem 100x100) -> ~(56,18) na página 75x75
        int i = (18 * page.WidthPx + 56) * 4;
        byte b = page.Bgra[i], g = page.Bgra[i + 1], red = page.Bgra[i + 2];
        Assert.True(g > red && g > b, $"região do quadrado opaco não saiu esverdeada: R={red} G={g} B={b}");
    }

    [Theory]
    [InlineData(new byte[] { 0x42, 0x4D, 0x00, 0x00 })] // BMP
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38 })] // GIF
    public void ImageToPdf_UnsupportedFormat_ThrowsNamingSupportedFormats(byte[] bytes)
    {
        var ex = Assert.Throws<PdfEditingException>(() => Editor.ImageToPdf(bytes));
        Assert.Contains("JPG", ex.Message);
        Assert.Contains("PNG", ex.Message);
    }

    [Fact]
    public void ImageToPdf_NullOrEmptyBytes_ThrowsNamingSupportedFormats()
    {
        var exNull = Assert.Throws<PdfEditingException>(() => Editor.ImageToPdf(null!));
        Assert.Contains("JPG", exNull.Message);
        var exEmpty = Assert.Throws<PdfEditingException>(() => Editor.ImageToPdf(Array.Empty<byte>()));
        Assert.Contains("JPG", exEmpty.Message);
    }

    [Fact] // magic bytes OK (JPEG SOI+APP0 válidos), mas o resto é lixo/truncado — iText não decodifica.
    // Mesma mensagem de "formato não suportado" (decisão do brief: corrupto/BMP/GIF convergem na
    // MESMA mensagem nomeando os formatos suportados — o usuário não precisa saber a causa exata).
    public void ImageToPdf_CorruptJpegBytes_ThrowsNamingSupportedFormats()
    {
        var truncated = Fixtures.Foto().Take(30).ToArray(); // header válido, sem SOF/SOS/dados de scan
        var ex = Assert.Throws<PdfEditingException>(() => Editor.ImageToPdf(truncated));
        Assert.Contains("JPG", ex.Message);
        Assert.Contains("PNG", ex.Message);
    }

    // --- CMYK JPEG: recusa defensiva (brief, Plano 7 Task 1 — fail-closed, não construível sinteticamente) ---
    //
    // `BuildMinimalSofJpeg` monta só o header SOF (SOI+SOF0+EOI, sem dados de scan reais) — achado
    // empírico: iText lê largura/altura/nº de componentes de cor DIRETO do SOF pra fins de embutir
    // (JPEG é passthrough do stream DCT, não precisa decodificar pixels), então este header mínimo
    // basta pra exercitar tanto o detector de CMYK quanto o teto de pixels abaixo — SEM precisar
    // construir um bitmap real (rápido, sem custo de memória de imagem gigante no teste).
    private static byte[] BuildMinimalSofJpeg(int numComponents, int width = 10, int height = 10)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 }; // SOI
        bytes.AddRange(new byte[] { 0xFF, 0xC0 }); // SOF0 (baseline)
        int len = 8 + 3 * numComponents; // 2(len)+1(precisão)+2(altura)+2(largura)+1(nComp)+3*nComp
        bytes.Add((byte)(len >> 8)); bytes.Add((byte)(len & 0xFF));
        bytes.Add(8); // precisão
        bytes.Add((byte)(height >> 8)); bytes.Add((byte)(height & 0xFF));
        bytes.Add((byte)(width >> 8)); bytes.Add((byte)(width & 0xFF));
        bytes.Add((byte)numComponents);
        for (int c = 0; c < numComponents; c++) { bytes.Add((byte)(c + 1)); bytes.Add(0x11); bytes.Add(0); }
        bytes.AddRange(new byte[] { 0xFF, 0xD9 }); // EOI
        return bytes.ToArray();
    }

    [Fact] // 4 componentes de cor no SOF = CMYK/YCCK -> recusa TIPADA nomeando o motivo, ANTES mesmo
    // de tentar decodificar via iText (varredura crua do marcador SOF, independente).
    public void ImageToPdf_CmykJpeg_ThrowsTypedRefusalNamingCmyk()
    {
        var ex = Assert.Throws<PdfEditingException>(() => Editor.ImageToPdf(BuildMinimalSofJpeg(4)));
        Assert.Contains("CMYK", ex.Message);
    }

    [Theory] // 1 (grayscale) e 3 (RGB/YCbCr) componentes NÃO disparam a recusa de CMYK — a conversão
    // segue adiante normalmente, provando que o detector é PRECISO (só dispara em ==4), não um
    // "recusa qualquer coisa que pareça estranha".
    [InlineData(1)]
    [InlineData(3)]
    public void ImageToPdf_NonCmykComponentCounts_DoesNotThrow(int numComponents)
    {
        var pdf = Editor.ImageToPdf(BuildMinimalSofJpeg(numComponents));
        Assert.NotEmpty(pdf);
    }

    // --- Teto de pixels: recusa de imagem "gigante" (brief, Plano 7 Task 1 — teto MEDIDO, ver task-1-report.md) ---

    [Fact] // header SOF declarando 10000x10000 (100MP, > teto de 50MP) — mesma técnica de BuildMinimalSofJpeg
    // acima: a varredura de header (TryReadJpegSofInfo) lê W/H direto do SOF, SEM precisar de
    // ImageDataFactory.Create nem de dados de scan reais.
    public void ImageToPdf_ImageAbovePixelCeiling_ThrowsNamingMegapixelLimit()
    {
        var huge = BuildMinimalSofJpeg(numComponents: 3, width: 10000, height: 10000);
        var ex = Assert.Throws<PdfEditingException>(() => Editor.ImageToPdf(huge));
        Assert.Contains("50", ex.Message);
        Assert.Contains("MP", ex.Message);
    }

    // --- C2 CRÍTICO (revisão pré-merge, task-1-report.md "## Fix"): teto ANTES do decode caro ---

    /// Constrói um PNG grayscale (1 byte/pixel — mantém o array gerenciado do teste pequeno, ~50MB pra
    /// 51MP em vez de ~200MB que RGBA exigiria) REAL e VÁLIDO — IDAT genuíno, zlib/deflate correto via
    /// `System.IO.Compression.DeflateStream` (SEM pacote novo, `System.IO.Compression` é BCL) — grande
    /// o bastante pra exigir trabalho de decodificação de verdade SE `ImageDataFactory.Create`
    /// chegasse a ser chamado. Achado empírico (ver task-1-report.md "## Fix", C2): PNG com IDAT
    /// AUSENTE ou com bytes zlib inválidos NÃO faz o iText lançar (decodificador tolerante — trata
    /// como imagem vazia/degenerada sem reclamar), então "a mensagem seria outra se Create() tivesse
    /// rodado" NÃO é uma prova confiável pra PNG (ao contrário de JPEG truncado, que falha cedo — ver
    /// ImageToPdf_CorruptJpegBytes_ThrowsNamingSupportedFormats). Por isso este PNG é REAL/decodificável
    /// de propósito — a prova de que o teto recusa ANTES de Create() vira TEMPO de execução (ver
    /// teste abaixo), comparado contra os números medidos do benchmark real (task-1-report.md: PNG com
    /// alpha em 36-64MP levou 1-1.8s pelo pipeline INTEIRO Create+desenho+escrita).
    private static byte[] BuildRealGrayscalePng(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // assinatura
        void WriteChunk(string type, byte[] data)
        {
            bytes.Add((byte)(data.Length >> 24)); bytes.Add((byte)(data.Length >> 16));
            bytes.Add((byte)(data.Length >> 8)); bytes.Add((byte)data.Length);
            var typeAndData = new List<byte>(System.Text.Encoding.ASCII.GetBytes(type));
            typeAndData.AddRange(data);
            bytes.AddRange(typeAndData);
            bytes.AddRange(Crc32(typeAndData.ToArray()));
        }

        var ihdr = new List<byte>();
        ihdr.AddRange(new byte[] { (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width });
        ihdr.AddRange(new byte[] { (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height });
        ihdr.AddRange(new byte[] { 8, 0, 0, 0, 0 }); // bitdepth=8, colortype=0 (grayscale, 1 byte/pixel)
        WriteChunk("IHDR", ihdr.ToArray());

        // scanlines cruas: 1 byte de filtro (0=None) + width bytes de pixel, por linha — padrão
        // repetitivo (barato de gerar/comprimir), mas GENUÍNO: se Create() decodificasse de verdade,
        // precisaria processar TODOS os width*height pixels, não um atalho de dado ausente.
        int rowStride = 1 + width;
        byte[] raw = new byte[(long)height * rowStride > int.MaxValue
            ? throw new InvalidOperationException("dimensão de teste grande demais pro array gerenciado")
            : height * rowStride];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowStride;
            raw[rowStart] = 0; // filtro None
            for (int x = 0; x < width; x++) raw[rowStart + 1 + x] = (byte)((x ^ y) & 0xFF);
        }
        byte[] zlibStream = ZlibCompress(raw);
        WriteChunk("IDAT", zlibStream);
        WriteChunk("IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C); // zlib header (compressão default)
        using (var deflate = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        uint adler = Adler32(raw);
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        const uint MOD = 65521;
        uint a = 1, b = 0;
        foreach (byte d in data) { a = (a + d) % MOD; b = (b + a) % MOD; }
        return (b << 16) | a;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();
    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static byte[] Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte d in data) crc = Crc32Table[(crc ^ d) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFF;
        return new byte[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc };
    }

    [Fact] // sanity check ANTES do teste principal: confirma que este PNG é REALMENTE decodificável
    // (não degenerado) — abaixo do teto, então o teto não pode interferir aqui. Prova que
    // BuildRealGrayscalePng produz um IDAT genuíno que o iText consegue ler (dimensão pequena só
    // pra este check ficar rápido; o teste de timing abaixo usa uma versão grande da MESMA construção).
    public void ImageToPdf_RealGrayscalePng_SmallDimensions_DecodesSuccessfully()
    {
        var png = BuildRealGrayscalePng(50, 50);
        var pdf = Editor.ImageToPdf(png);
        Assert.NotEmpty(pdf);
    }

    [Fact] // C2 CRÍTICO — prova por TEMPO DE EXECUÇÃO (achado empírico: PNG "sem dado real" não força
    // o iText a lançar — ver doc XML de BuildRealGrayscalePng acima, "mensagem discriminante" não é
    // confiável pra PNG) que o teto recusa ANTES de ImageDataFactory.Create. Este PNG (51MP, > teto de
    // 50MP) tem um IDAT REAL/válido (confirmado pelo teste anterior que a mesma construção decodifica
    // de verdade) — se Create() tivesse sido alcançado, precisaria decodificar 51 milhões de pixels
    // (medido no benchmark real, task-1-report.md: dezenas a centenas de ms só pra decodificar nessa
    // faixa, chegando a ~1-2s com o pipeline completo em 36-100MP). O teste passa com folga larga
    // (< 300ms) porque a checagem de header nunca deixa `Create()` ser chamado — só lê 24 bytes fixos
    // do IHDR.
    public void ImageToPdf_OversizedRealPng_RefusesFastBeforeReachingDecode()
    {
        var png = BuildRealGrayscalePng(7200, 7200); // 51.84MP > teto de 50MP, IDAT REAL e válido
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<PdfEditingException>(() => Editor.ImageToPdf(png));
        sw.Stop();
        Assert.Contains("50", ex.Message);
        Assert.Contains("MP", ex.Message);
        Assert.True(sw.ElapsedMilliseconds < 300,
            $"recusa levou {sw.ElapsedMilliseconds}ms — tempo alto demais pra uma checagem de HEADER " +
            "(24 bytes fixos do IHDR); sugere que ImageDataFactory.Create foi alcançado antes da recusa.");
    }

    // --- C3 CRÍTICO (revisão pós-merge, task-1-report.md "## Fix"): overflow no teto de pixels PNG ---

    /// Monta um PNG com IHDR customizado (width/height ATÉ 0xFFFFFFFF cada) e NENHUM chunk IDAT —
    /// mesma forma do probe EXATO do revisor. `long` nos parâmetros de propósito: precisa aceitar o
    /// valor MÁXIMO de 32 bits sem truncar (0xFFFFFFFF não cabe num `int`).
    private static byte[] BuildPngIhdrOnly(long width, long height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // assinatura
        void WriteChunk(string type, byte[] data)
        {
            bytes.Add((byte)(data.Length >> 24)); bytes.Add((byte)(data.Length >> 16));
            bytes.Add((byte)(data.Length >> 8)); bytes.Add((byte)data.Length);
            var typeAndData = new List<byte>(System.Text.Encoding.ASCII.GetBytes(type));
            typeAndData.AddRange(data);
            bytes.AddRange(typeAndData);
            bytes.AddRange(Crc32(typeAndData.ToArray()));
        }

        var ihdr = new List<byte>();
        ihdr.Add((byte)(width >> 24)); ihdr.Add((byte)(width >> 16)); ihdr.Add((byte)(width >> 8)); ihdr.Add((byte)width);
        ihdr.Add((byte)(height >> 24)); ihdr.Add((byte)(height >> 16)); ihdr.Add((byte)(height >> 8)); ihdr.Add((byte)height);
        ihdr.AddRange(new byte[] { 8, 6, 0, 0, 0 }); // bitdepth=8, colortype=6(RGBA)
        WriteChunk("IHDR", ihdr.ToArray());
        // SEM IDAT — mesma forma do probe do revisor.
        WriteChunk("IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    [Fact] // C3 CRÍTICO — probe EXATO do revisor: IHDR 0xFFFFFFFF x 0xFFFFFFFF (o produto, ~1.84e19,
    // overflowa `long` e volta NEGATIVO — `> MaxImagePixels` contra um número negativo nunca dispara,
    // então SEM o fix de C3 esse PNG atravessava o teto de header inteiro; a rede pós-Create também
    // era enganada, já que o iText devolve GetWidth()/GetHeight() == -1 pra essa imagem degenerada, e
    // (-1)*(-1)=1 também passaria pelo teto). Com o fix, qualquer uma das 2 camadas recusa — o
    // resultado observável é sempre PdfEditingException, NUNCA um PDF com MediaBox negativo.
    public void ImageToPdf_PngIhdrMaxUint32BothDimensions_ThrowsTypedRefusal_NoPdfProduced()
    {
        var png = BuildPngIhdrOnly(0xFFFFFFFFL, 0xFFFFFFFFL);
        var ex = Assert.Throws<PdfEditingException>(() => Editor.ImageToPdf(png));
        Assert.NotNull(ex.Message); // recusa TIPADA — nunca uma exceção crua (IndexOutOfRange, etc.)
    }

    [Fact] // caso extremo POR DIMENSÃO (não os dois iguais): uma dimensão no máximo de 32 bits, a
    // outra mínima (1) — o PRODUTO em si (0xFFFFFFFF*1 ≈ 4.29e9) NÃO overflowaria `long`, mas already
    // excede o teto (50M) MUITO antes de chegar perto do limite de overflow — prova que o check
    // individual por dimensão (`pngWidth > MaxImagePixels || pngHeight > MaxImagePixels`) pega esse
    // caso de qualquer forma, sem depender do produto.
    public void ImageToPdf_PngIhdrMaxUint32OneDimension_ThrowsTypedRefusal()
    {
        var png = BuildPngIhdrOnly(0xFFFFFFFFL, 1L);
        var ex = Assert.Throws<PdfEditingException>(() => Editor.ImageToPdf(png));
        Assert.Contains("MP", ex.Message); // pega na checagem de HEADER (dimensão individual), mensagem de teto
    }

    // --- C1 CRÍTICO (revisão pré-merge, task-1-report.md "## Fix"): parser EXIF blindado contra overflow ---

    /// Monta um JPEG DECODIFICÁVEL (SOF0 10x10, 3 componentes — mesmo formato de BuildMinimalSofJpeg,
    /// achado empírico de que iText decodifica sem dados de scan reais) com um segmento APP1/EXIF
    /// CUSTOMIZADO inserido ANTES do SOF — permite exercitar o parser de Orientation com payloads
    /// hostis/truncados através da API PÚBLICA (ImageToPdf), já que o parser é privado (sem
    /// InternalsVisibleTo, mesmo padrão do resto da suíte).
    private static byte[] BuildJpegWithCustomExifPayload(byte[] exifPayloadAfterSignature)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 }; // SOI
        var app1Payload = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 }; // "Exif\0\0"
        app1Payload.AddRange(exifPayloadAfterSignature);
        int app1Len = 2 + app1Payload.Count;
        bytes.AddRange(new byte[] { 0xFF, 0xE1, (byte)(app1Len >> 8), (byte)(app1Len & 0xFF) });
        bytes.AddRange(app1Payload);
        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 8, 0, 10, 0, 10, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1 }); // SOF0 10x10, 3 comp.
        bytes.AddRange(new byte[] { 0xFF, 0xD9 }); // EOI
        return bytes.ToArray();
    }

    [Fact] // C1 CRÍTICO da revisão pré-merge (achado REAL de fuzzing, reproduzido pelo revisor com um
    // JPEG hostil artesanal): offset de IFD0 craftado perto de int.MaxValue fazia a checagem de
    // limite ANTIGA (aritmética `int`) overflowar DUAS VEZES em sequência — a soma
    // `tiffStart+ifd0Offset` ficava positiva-mas-enorme (não caía no guard `<0`), então somar `+2`
    // overflowava DE NOVO pra um número bem negativo (`negativo > jpeg.Length` é sempre falso,
    // derrotando o segundo guard também) — `jpeg[ifd0Start]` lançava IndexOutOfRangeException CRUA,
    // escapando de ImageToPdf sem nunca virar PdfEditingException. Fix: aritmética em `long` (ver
    // ParseTiffOrientationRotation). Este teste usa o offset EXATO (0x7FFFFFE0) que provocava o duplo
    // overflow: a conversão precisa SUCEDER, com a orientação tratada como "sem correção" (parse
    // falhou silenciosamente = mesmo efeito de Orientation=1/ausente), nunca escapar como exceção crua.
    public void ImageToPdf_HostileExifIfd0Offset_NeverEscapesRawException_DefaultsToNoRotation()
    {
        var exifPayload = new byte[] { (byte)'M', (byte)'M', 0x00, 0x2A, 0x7F, 0xFF, 0xFF, 0xE0 };
        var jpeg = BuildJpegWithCustomExifPayload(exifPayload);

        var pdf = Editor.ImageToPdf(jpeg); // NÃO pode lançar IndexOutOfRangeException (nem nada crua)
        Assert.NotEmpty(pdf);

        // orientação "defaulted" (parse falhou) -> SEM rotação nenhuma, página no tamanho cru (10x10px
        // @ fallback 96dpi = 7.5x7.5pt), /Rotate sempre 0 (I3 acima).
        Assert.Equal(0, Assert.Single(Editor.GetPageRotations(pdf)));
        using var r = new PdfDocumentRenderer(pdf);
        var size = r.GetPageSize(0);
        Assert.Equal(7.5, size.WidthPt, 0.5);
        Assert.Equal(7.5, size.HeightPt, 0.5);
    }

    [Theory] // fuzz de truncamento/extremos — cada forma provoca uma checagem de limite DIFERENTE no
    // parser (header truncado; IFD0 aponta pra dentro do arquivo mas declara mais entradas do que
    // cabem; offset no valor MÁXIMO absoluto de 32 bits). Nenhuma pode escapar como exceção crua —
    // todas convergem pra "sem rotação", conversão segue normalmente.
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0x00 })]                                        // TIFF header truncado (falta metade do magic)
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0xFF, 0xFF })] // IFD0 logo após o header, declara 65535 entradas SEM nenhum dado de entrada (truncado)
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0x00, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF })]           // offset = 0xFFFFFFFF (máximo de 32 bits — o valor mais hostil possível)
    public void ImageToPdf_TruncatedOrExtremeExifPayloads_NeverThrowsUnexpectedException(byte[] exifPayload)
    {
        var jpeg = BuildJpegWithCustomExifPayload(exifPayload);
        var pdf = Editor.ImageToPdf(jpeg); // nunca lança — payload EXIF malformado não é "imagem corrompida"
        Assert.NotEmpty(pdf);
        Assert.Equal(0, Assert.Single(Editor.GetPageRotations(pdf)));
    }

    // --- Minor (revisão pré-merge): imagem 1x1 degenerada ---

    [Fact] // minor da revisão: imagem 1x1 (degenerada) não pode quebrar o pipeline de tamanho de página
    public void ImageToPdf_OnePixelImage_ProducesDegeneratePageWithoutThrowing()
    {
        var pdf = Editor.ImageToPdf(Fixtures.OnePixelPng());
        using var r = new PdfDocumentRenderer(pdf);
        Assert.Equal(1, r.PageCount);
        var size = r.GetPageSize(0);
        Assert.True(size.WidthPt > 0 && size.HeightPt > 0, $"tamanho de página degenerado: {size.WidthPt}x{size.HeightPt}");
    }

    // --- Task 3 (Plano 7): IsWithinImagePixelLimit — exposição do teto de header pro App -------------
    //
    // O caminho AddAnnotation/AnnotationKind.ImageStamp (consumido por DocumentViewModel.
    // PlaceStampAtAsync) nunca teve teto de pixels nenhum — foi implementado (Task 9, Plano 3a) ANTES
    // do teto de ImageToPdf existir (Task 1, Plano 7), e nunca foi retrofitado. Task 3 (Plano 7,
    // ferramenta "🖼 Imagem") decide aplicar o MESMO teto (50MP) ANTES do modo de colocação, do lado
    // do App — mas o App não pode reimplementar o parser SOF/IHDR (duplicar um parser resiliente a
    // overflow É o tipo de duplicação que diverge silenciosamente na 1ª mudança futura). Este método
    // reusa TryReadJpegSofInfo/TryReadPngDimensions (já testados via ImageToPdf acima), sem alterar o
    // código de ImageToPdf em si (risco zero de regressão no teto já revisado/hardenizado).

    [Fact]
    public void IsWithinImagePixelLimit_SmallJpeg_IsTrue() =>
        Assert.True(Editor.IsWithinImagePixelLimit(Fixtures.Foto()));

    [Fact] // mesma técnica de ImageToPdf_ImageAbovePixelCeiling_ThrowsNamingMegapixelLimit — header SOF
    // declarando 10000x10000 (100MP), sem decodificar nada.
    public void IsWithinImagePixelLimit_JpegAbovePixelCeiling_IsFalse() =>
        Assert.False(Editor.IsWithinImagePixelLimit(BuildMinimalSofJpeg(numComponents: 3, width: 10000, height: 10000)));

    [Fact]
    public void IsWithinImagePixelLimit_SmallPng_IsTrue() =>
        Assert.True(Editor.IsWithinImagePixelLimit(Fixtures.Transparente()));

    [Fact] // mesma técnica de ImageToPdf_OversizedRealPng_RefusesFastBeforeReachingDecode — IHDR
    // declarando 7200x7200 (51.84MP), sem IDAT nenhum (varredura de header não precisa de pixels reais).
    public void IsWithinImagePixelLimit_PngAbovePixelCeiling_IsFalse() =>
        Assert.False(Editor.IsWithinImagePixelLimit(BuildPngIhdrOnly(7200, 7200)));

    [Fact] // header ilegível/truncado -> FAIL-OPEN (true): este método só existe pra pegar "grande
    // demais" ANTES do decode caro; um header que não dá pra ler com confiança não é "grande demais",
    // é "sem opinião" — o decode real (WPF no App) decide o resto (corrupção vira outra mensagem).
    public void IsWithinImagePixelLimit_UnreadableHeader_IsTrue()
    {
        Assert.True(Editor.IsWithinImagePixelLimit(new byte[] { 0xFF, 0xD8, 0xFF })); // JPEG truncado antes do SOF
        Assert.True(Editor.IsWithinImagePixelLimit(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00 })); // PNG truncado antes do IHDR
    }

    [Fact]
    public void IsWithinImagePixelLimit_NullOrEmpty_IsTrue()
    {
        Assert.True(Editor.IsWithinImagePixelLimit(null!));
        Assert.True(Editor.IsWithinImagePixelLimit(Array.Empty<byte>()));
    }

    // --- Task 3 (Plano 7): ReadJpegExifOrientation — exposição do parser EXIF pro App ----------------
    //
    // AddAnnotation/AnnotationKind.ImageStamp nunca honrou EXIF (ao contrário de ImageToPdf, Task 1) —
    // uma foto de celular colocada como "🖼 Imagem" entraria de lado sem correção. A correção de
    // PIXELS em si é 100% App-side (WPF TransformedBitmap — WPF não pode entrar em mPdf.Editing, ver
    // AgplGuardTests/csproj); este método só expõe a LEITURA pura do ângulo (já testada indiretamente
    // via ImageToPdf_JpegWithExifOrientation6_OpensUpright acima) — reusa ReadJpegExifOrientationRotation
    // sem reimplementar o parser TIFF/IFD0 uma 2ª vez.

    [Fact]
    public void ReadJpegExifOrientation_FotoWithoutExifRotation_IsZero() =>
        Assert.Equal(0, Editor.ReadJpegExifOrientation(Fixtures.Foto()));

    [Fact] // mesma fixture/mesmo ângulo (90°) que ImageToPdf_JpegWithExifOrientation6_OpensUpright prova
    // visualmente via render — aqui só o NÚMERO devolvido pelo parser.
    public void ReadJpegExifOrientation_FotoExif90_Returns90() =>
        Assert.Equal(90, Editor.ReadJpegExifOrientation(Fixtures.FotoExif90()));

    [Fact] // PNG não tem EXIF (segmento APP1 é conceito JPEG) -> 0 sempre, sem tentar parsear nada.
    public void ReadJpegExifOrientation_Png_IsZero() =>
        Assert.Equal(0, Editor.ReadJpegExifOrientation(Fixtures.Transparente()));

    [Fact]
    public void ReadJpegExifOrientation_NullOrTooShort_IsZero()
    {
        Assert.Equal(0, Editor.ReadJpegExifOrientation(null!));
        Assert.Equal(0, Editor.ReadJpegExifOrientation(new byte[] { 0xFF }));
    }

    // --- Task 3 (Plano 7), fix pós-revisão — IsCmykJpeg: exposição do detector de CMYK pro App --------
    //
    // ACHADO (revisão pós-merge desta task): o caminho AddAnnotation/AnnotationKind.ImageStamp
    // (consumido por DocumentViewModel.ToggleImageTool/ToggleStampTool/PlaceStampAtAsync) nunca recusou
    // JPEG CMYK — só ImageToPdf (Task 1) tem essa recusa (varredura de SOF, componentes==4, decisão
    // fail-closed porque renderizar CMYK embutido via PDFium é intestável sem fixture real). Este
    // método expõe o MESMO detector (já existia como `private static IsCmykJpeg`, não estava sendo
    // usado por NADA — `ImageToPdf` sempre checou `sofComponents == 4` inline, direto — reaproveitado
    // aqui em vez de duplicar a leitura de SOF) pra `ToggleImageTool` recusar ANTES do modo de
    // colocação, mesma disciplina de `IsWithinImagePixelLimit`.

    [Fact]
    public void IsCmykJpeg_RgbJpeg_IsFalse() =>
        Assert.False(Editor.IsCmykJpeg(BuildMinimalSofJpeg(numComponents: 3)));

    [Fact] // técnica do Task 1 (BuildMinimalSofJpeg): só o header SOF, sem dados de scan reais — basta
    // pra exercitar o detector, sem precisar de uma fixture CMYK de verdade (não construível
    // sinteticamente sem pacote novo — ver task-1-report.md).
    public void IsCmykJpeg_CmykSofJpeg_IsTrue() =>
        Assert.True(Editor.IsCmykJpeg(BuildMinimalSofJpeg(numComponents: 4)));

    [Fact]
    public void IsCmykJpeg_GrayscaleJpeg_IsFalse() =>
        Assert.False(Editor.IsCmykJpeg(BuildMinimalSofJpeg(numComponents: 1)));

    [Fact]
    public void IsCmykJpeg_Png_IsFalse() =>
        Assert.False(Editor.IsCmykJpeg(Fixtures.Transparente()));

    [Fact]
    public void IsCmykJpeg_NullOrTooShort_IsFalse()
    {
        Assert.False(Editor.IsCmykJpeg(null!));
        Assert.False(Editor.IsCmykJpeg(new byte[] { 0xFF }));
    }
}
