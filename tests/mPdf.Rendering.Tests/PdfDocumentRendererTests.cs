using mPdf.Rendering;
using Xunit;

namespace mPdf.Rendering.Tests;

public class PdfDocumentRendererTests
{
    [Fact] // fixture de 1 página abre e conta 1
    public void PageCount_SinglePageFixture_IsOne()
    {
        using var r = new PdfDocumentRenderer(Fixtures.A4());
        Assert.Equal(1, r.PageCount);
    }

    [Fact] // fixture de 30 páginas conta 30
    public void PageCount_ThirtyPageFixture_IsThirty()
    {
        using var r = new PdfDocumentRenderer(Fixtures.ThirtyPages());
        Assert.Equal(30, r.PageCount);
    }

    [Fact] // A4 do iText = 595x842 pontos (tolerância 1pt)
    public void GetPageSize_A4Fixture_Is595x842Points()
    {
        using var r = new PdfDocumentRenderer(Fixtures.A4());
        var s = r.GetPageSize(0);
        Assert.Equal(595, s.WidthPt, 1.0);
        Assert.Equal(842, s.HeightPt, 1.0);
    }

    [Fact] // bytes que não são PDF -> ArgumentException clara
    public void Ctor_InvalidBytes_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new PdfDocumentRenderer(new byte[] { 1, 2, 3 }));
    }

    [Fact] // prova de disparo da guarda de dispose (recusa observada, não inferida)
    public void RenderPage_AfterDispose_ThrowsObjectDisposed()
    {
        var r = new PdfDocumentRenderer(Fixtures.A4());
        r.Dispose();
        Assert.Throws<ObjectDisposedException>(() => r.RenderPage(0, 1.0));
    }

    [Fact]
    public void GetPageSize_AfterDispose_ThrowsObjectDisposed()
    {
        var r = new PdfDocumentRenderer(Fixtures.A4());
        r.Dispose();
        Assert.Throws<ObjectDisposedException>(() => r.GetPageSize(0));
    }
}
