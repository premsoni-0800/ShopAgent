using System.Drawing;
using System.Runtime.Versioning;
using PrintlyAgent.Models;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// What size a page comes out of the printer.
///
/// Printed for real, through the same PrintSubmission the queue uses, to
/// "Microsoft Print to PDF" - and the paper it produced is read back and
/// measured. A page that is solid ink from edge to edge must come out solid
/// ink from edge to edge: when pages were fitted into one-inch margins the
/// same page covered about 63% of the sheet, which is the "small print" the
/// shop saw.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(RealPrinting.Name)]
public class PageLayoutTests : IDisposable
{
    private const string PdfPrinter = "Microsoft Print to PDF";
    private readonly string _tempDir;

    public PageLayoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "printly-layout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    private static bool HasPdfPrinter() =>
        System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>()
            .Any(p => string.Equals(p, PdfPrinter, StringComparison.OrdinalIgnoreCase));

    /// <summary>One page of the given size in points, painted black all over.</summary>
    private string SolidPage(int width, int height)
    {
        var content = $"0 0 0 rg 0 0 {width} {height} re f";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefAt = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++) pdf.Append($"{offsets[i]:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefAt}\n%%EOF\n");
        var path = Path.Combine(_tempDir, $"solid-{width}x{height}.pdf");
        File.WriteAllText(path, pdf.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    /// <summary>Prints it and returns the sheet the "printer" produced, rendered.</summary>
    private static Bitmap PrintAndReadBack(string pdf, Orientation orientation)
    {
        var token = PrintSubmission.PrintPdf(
            PdfPrinter, pdf,
            new PrintOptions(ColorMode.COLOR, DuplexMode.SINGLE_SIDED, PaperSize.A4, 1, null, orientation));
        var output = Path.Combine(PrintSubmission.VirtualPrinterOutputDir(), $"{token}.pdf");

        // The driver finishes writing after Print() returns.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        long lastSize = -1;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(output))
            {
                var size = new FileInfo(output).Length;
                if (size > 0 && size == lastSize) break;
                lastSize = size;
            }
            Thread.Sleep(500);
        }
        Assert.True(File.Exists(output), "Microsoft Print to PDF wrote nothing");

        using var renderer = new PdfPageRenderer(output, 50);
        var sheet = (Bitmap)renderer.RenderPage(0).Clone();
        try { File.Delete(output); } catch (IOException) { }
        return sheet;
    }

    private static double InkCoverage(Bitmap bitmap)
    {
        var inked = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).GetBrightness() < 0.5f) inked++;
        return (double)inked / (bitmap.Width * bitmap.Height);
    }

    [Fact(DisplayName = "an A4 page prints at full size, not shrunk into margins")]
    public void AnA4PagePrintsAtFullSize()
    {
        if (!HasPdfPrinter()) return;

        using var sheet = PrintAndReadBack(SolidPage(595, 842), Orientation.PORTRAIT);

        Assert.True(sheet.Height > sheet.Width, "an upright page should come out on an upright sheet");
        var coverage = InkCoverage(sheet);
        Assert.True(coverage > 0.95, $"the page covered {coverage:P0} of the sheet");
    }

    [Fact(DisplayName = "a landscape order puts a landscape page on a landscape sheet, filling it")]
    public void ALandscapeOrderFillsALandscapeSheet()
    {
        if (!HasPdfPrinter()) return;

        using var sheet = PrintAndReadBack(SolidPage(842, 595), Orientation.LANDSCAPE);

        Assert.True(sheet.Width > sheet.Height, "a landscape order should come out on a landscape sheet");
        var coverage = InkCoverage(sheet);
        Assert.True(coverage > 0.95, $"the page covered {coverage:P0} of the sheet");
    }

    [Fact(DisplayName = "the order's orientation decides the sheet, as the preview draws it")]
    public void TheOrdersOrientationDecidesTheSheet()
    {
        if (!HasPdfPrinter()) return;

        // A wide page on an order left upright. The preview shows it fitted to
        // the width of an upright sheet, so that is what comes out: about half
        // the sheet, edge to edge across it.
        using var sheet = PrintAndReadBack(SolidPage(842, 595), Orientation.PORTRAIT);

        Assert.True(sheet.Height > sheet.Width, "a portrait order should come out on a portrait sheet");
        var coverage = InkCoverage(sheet);
        Assert.InRange(coverage, 0.45, 0.55);
        Assert.True(RowCoverage(sheet, sheet.Height / 2) > 0.95, "the page should span the sheet's width");
    }

    [Fact(DisplayName = "a photo-shaped page spans the sheet like the preview's fit")]
    public void APhotoShapedPageSpansTheSheet()
    {
        if (!HasPdfPrinter()) return;

        // What the backend makes of a 16:9 photo: a page exactly the photo's shape.
        using var sheet = PrintAndReadBack(SolidPage(842, 474), Orientation.PORTRAIT);

        Assert.True(RowCoverage(sheet, sheet.Height / 2) > 0.95, "the photo should span the sheet's width");
        var expected = (474.0 / 842.0) * (595.0 / 842.0);
        Assert.InRange(InkCoverage(sheet), expected - 0.05, expected + 0.05);
    }

    private static double RowCoverage(Bitmap bitmap, int y)
    {
        var inked = 0;
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).GetBrightness() < 0.5f) inked++;
        return (double)inked / bitmap.Width;
    }
}

/// <summary>
/// Real printing and rasterising is heavy enough to starve the suite's
/// wall-clock tests (queue and download timing) when run beside them, so it
/// runs alone.
/// </summary>
[CollectionDefinition(RealPrinting.Name, DisableParallelization = true)]
public sealed class RealPrinting
{
    public const string Name = "Real printing";
}
