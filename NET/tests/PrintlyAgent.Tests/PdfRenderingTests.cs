using System.Drawing;
using System.Runtime.Versioning;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Page ranges, and that PDFium genuinely rasterises a page on this machine.
///
/// The rendering test is the .NET equivalent of the Kotlin suite's ink-coverage
/// checks: it builds a PDF by hand, renders it, and looks at the pixels. A
/// renderer that loads and returns a blank bitmap would pass every test that
/// only checked for "no exception", and blank pages are exactly the failure a
/// print shop cannot afford.
/// </summary>
[SupportedOSPlatform("windows")]
public class PdfRenderingTests : IDisposable
{
    private readonly string _tempDir;

    public PdfRenderingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "printly-pdf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    // --- page ranges ---------------------------------------------------------

    [Fact(DisplayName = "no range means every page")]
    public void NoRangeMeansEveryPage()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, PageRange.Resolve(null, 5));
        Assert.Equal(new[] { 1, 2, 3 }, PageRange.Resolve("", 3));
        Assert.Equal(new[] { 1, 2, 3 }, PageRange.Resolve("   ", 3));
    }

    [Fact(DisplayName = "a span and a single page combine, in the order written")]
    public void ASpanAndASinglePageCombine()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 8 }, PageRange.Resolve("1-5,8", 10));
        Assert.Equal(new[] { 8, 1, 2 }, PageRange.Resolve("8,1-2", 10));
    }

    [Fact(DisplayName = "pages outside the document are dropped, not clamped")]
    public void PagesOutsideTheDocumentAreDropped()
    {
        // Clamping would silently print page 3 twice for "3-99" on a 3-page
        // document; dropping prints what exists and nothing else.
        Assert.Equal(new[] { 2, 3 }, PageRange.Resolve("2-99", 3));
        Assert.Equal(new[] { 1, 2, 3 }, PageRange.Resolve("0-3", 3));
    }

    /// <summary>
    /// The fallback direction matters. Printing everything when the range could
    /// not be understood is wrong in a way the shop can see and fix; printing
    /// nothing looks exactly like a job that succeeded, and the student collects
    /// blank air.
    /// </summary>
    [Fact(DisplayName = "an unreadable range prints the whole document rather than nothing")]
    public void AnUnreadableRangePrintsTheWholeDocument()
    {
        Assert.Equal(new[] { 1, 2, 3 }, PageRange.Resolve("not-a-range", 3));
        Assert.Equal(new[] { 1, 2, 3 }, PageRange.Resolve("99-200", 3));
        Assert.Equal(new[] { 1, 2, 3 }, PageRange.Resolve(",,,", 3));
    }

    [Fact(DisplayName = "whitespace around numbers is tolerated")]
    public void WhitespaceAroundNumbersIsTolerated()
    {
        Assert.Equal(new[] { 1, 2, 5 }, PageRange.Resolve(" 1 - 2 , 5 ", 10));
    }

    // --- real rasterising ----------------------------------------------------

    /// <summary>
    /// A minimal but genuine PDF: two pages, the first carrying a large filled
    /// rectangle, the second left blank. Written by hand rather than pulled from
    /// a fixture so the test knows exactly what should be on each page.
    /// </summary>
    private string WriteTwoPagePdf()
    {
        const string content = "0 0 0 rg 100 400 400 300 re f";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };

        var pdf = new System.Text.StringBuilder();
        var offsets = new List<int> { 0 };
        pdf.Append("%PDF-1.4\n");
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefAt = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++) pdf.Append($"{offsets[i]:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefAt}\n%%EOF\n");

        var path = Path.Combine(_tempDir, "two-page.pdf");
        File.WriteAllText(path, pdf.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    [Fact(DisplayName = "PDFium really rasterises a page, and the ink is where it was drawn")]
    public void PdfiumReallyRasterisesAPage()
    {
        using var renderer = new PdfPageRenderer(WriteTwoPagePdf());

        Assert.Equal(2, renderer.PageCount);

        var inked = renderer.RenderPage(0);
        Assert.True(inked.Width > 0 && inked.Height > 0);
        Assert.True(NonWhiteFraction(inked) > 0.05,
            "page 1 carries a large filled rectangle - a blank bitmap here means the renderer loaded but drew nothing");

        var blank = renderer.RenderPage(1);
        Assert.True(NonWhiteFraction(blank) < 0.01, "page 2 is empty and should render empty");
    }

    /// <summary>
    /// The cache is the whole reason rendering is not done twice per page. Same
    /// index must hand back the very same object, not an equal one.
    /// </summary>
    [Fact(DisplayName = "asking for the same page twice renders it once")]
    public void AskingForTheSamePageTwiceRendersItOnce()
    {
        using var renderer = new PdfPageRenderer(WriteTwoPagePdf());

        var first = renderer.RenderPage(0);
        var second = renderer.RenderPage(0);

        Assert.Same(first, second);
    }

    [Fact(DisplayName = "pages are rasterised at the DPI asked for")]
    public void PagesAreRasterisedAtTheDpiAskedFor()
    {
        // A4 is 595x842 points; at 150 DPI that is about 1240x1754 pixels. The
        // exact numbers are PDFium's business, but the scale has to follow the
        // DPI or the driver receives a bitmap far too small and prints it soft.
        using var low = new PdfPageRenderer(WriteTwoPagePdf(), dpi: 72);
        using var high = new PdfPageRenderer(WriteTwoPagePdf(), dpi: 150);

        var lowPage = low.RenderPage(0);
        var highPage = high.RenderPage(0);

        Assert.True(highPage.Width > lowPage.Width,
            $"150 DPI should be wider than 72 DPI (got {highPage.Width} vs {lowPage.Width})");
    }

    private static double NonWhiteFraction(Bitmap bitmap)
    {
        var nonWhite = 0;
        var total = 0;
        // Sampled rather than every pixel: this is a coverage question, and a
        // full A4 bitmap at 150 DPI is two million pixels per assertion.
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                var pixel = bitmap.GetPixel(x, y);
                total++;
                if (pixel.R < 200 || pixel.G < 200 || pixel.B < 200) nonWhite++;
            }
        }
        return total == 0 ? 0 : (double)nonWhite / total;
    }
}
