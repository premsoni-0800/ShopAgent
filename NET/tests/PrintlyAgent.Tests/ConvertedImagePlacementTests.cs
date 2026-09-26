using System.Drawing;
using System.Runtime.Versioning;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// A photo the backend framed on an A4 page prints as the photo, not as a
/// smaller photo inside a white frame - checked against files the backend's
/// real converter produced (Fixtures/converter: solid dark images of the
/// named shapes, converted with its 24pt margin).
/// </summary>
[SupportedOSPlatform("windows")]
public class ConvertedImagePlacementTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "converter", name);

    [Theory(DisplayName = "a framed photo is found where the converter put it")]
    [InlineData("tall.pdf", 900, 1600)]
    [InlineData("wide.pdf", 1600, 900)]
    [InlineData("square.pdf", 1000, 1000)]
    public void AFramedPhotoIsFound(string file, int w, int h)
    {
        var found = ConvertedImagePlacement.Find(File.ReadAllBytes(Fixture(file)));
        Assert.NotNull(found);
        Assert.Equal((double)w / h, found!.Value.W / found.Value.H, 2);
    }

    [Theory(DisplayName = "a document that is not a framed photo is left alone")]
    [InlineData("edited_portrait.pdf")] // an edited A4 sheet: no frame to remove
    [InlineData("doc.pdf")]              // a real PDF
    public void OtherDocumentsAreLeftAlone(string file)
    {
        Assert.Null(ConvertedImagePlacement.Find(File.ReadAllBytes(Fixture(file))));
    }

    [Fact(DisplayName = "the page renders as the photo alone, edge to edge")]
    public void ThePageRendersAsThePhotoAlone()
    {
        using var renderer = new PdfPageRenderer(Fixture("tall.pdf"), 72);
        var page = renderer.RenderPage(0);

        Assert.Equal(900.0 / 1600, (double)page.Width / page.Height, 2);
        // Dark to the very edges: no white frame left.
        foreach (var (x, y) in new[] { (1, 1), (page.Width - 2, 1), (1, page.Height - 2), (page.Width - 2, page.Height - 2) })
        {
            Assert.True(page.GetPixel(x, y).GetBrightness() < 0.3f, $"({x},{y}) should be the photo, not a margin");
        }
    }

    [Fact(DisplayName = "on the printed sheet the tall photo fills the sheet's height, like the student's preview")]
    public void TheSheetMatchesTheStudentsPreview()
    {
        using var renderer = new PdfPageRenderer(Fixture("tall.pdf"), 100);
        var page = renderer.RenderPage(0);
        var (_, y, _, h) = PageLayout.Placement(8.27, 11.69, page.Width / 100.0, page.Height / 100.0);
        Assert.Equal(0.0, y, 2);
        Assert.Equal(11.69, h, 2);
    }
}
