using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace PrintlyAgent.Printing;

/// <summary>
/// Where a rendered PDF page goes on the sheet.
///
/// <para>
/// Every page used to be fitted into <c>MarginBounds</c> - the sheet less the
/// one-inch margins <see cref="PageSettings"/> invents by default. An A4 page
/// on A4 paper came out at roughly three quarters of its size, centred in a
/// band of white, and a photo the student had laid out edge to edge on an A4
/// sheet in the app printed as a small picture in the middle of the paper.
/// Nobody asked for those margins: the document already has whatever margins
/// its author wanted.
/// </para>
///
/// <para>
/// So the page is fitted to the physical sheet instead - the rule the student's
/// preview uses in both the app and the web: the page (or photo) fitted edge to
/// edge onto an A4 sheet turned the way the order says, centred. A page that is
/// the sheet's size prints at actual size. The sheet's orientation is the
/// order's and never the page's own shape: the preview draws a wide page small
/// on an upright sheet when that is what was chosen, so the paper does too.
/// What falls in the printer's unprintable strip - a few millimetres at the
/// edges on most lasers - is lost exactly as it is from any viewer's "Actual
/// size"; no printer can put ink there.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PageLayout
{
    /// <summary>
    /// How far a page may differ from the sheet and still print at 100%.
    /// Rendering rounds a page to whole pixels, and an A4 page shrunk by 0.3%
    /// to "fit" A4 paper is the kind of drift that turns a ruled form's lines
    /// into a moiré.
    /// </summary>
    private const double ActualSizeTolerance = 0.015;

    /// <summary>
    /// Draws <paramref name="page"/>, rendered at <paramref name="dpi"/>, onto the
    /// sheet <paramref name="args"/> is printing.
    /// </summary>
    /// <summary>
    /// Where a page of <paramref name="pageWidth"/> x <paramref name="pageHeight"/>
    /// inches lands on a sheet of <paramref name="sheetWidth"/> x
    /// <paramref name="sheetHeight"/> inches: fitted, centred, and at actual
    /// size when it is the sheet's size. In inches from the sheet's top-left.
    ///
    /// The one rule both the printer and the owner's preview are drawn with
    /// (see <see cref="RenderSheet"/>), so the preview cannot show a sheet
    /// the printer would not produce.
    /// </summary>
    public static (double X, double Y, double Width, double Height) Placement(
        double sheetWidth, double sheetHeight, double pageWidth, double pageHeight)
    {
        var scale = Math.Min(sheetWidth / pageWidth, sheetHeight / pageHeight);
        if (Math.Abs(scale - 1.0) <= ActualSizeTolerance) scale = 1.0;
        var width = pageWidth * scale;
        var height = pageHeight * scale;
        return ((sheetWidth - width) / 2.0, (sheetHeight - height) / 2.0, width, height);
    }

    /// <summary>The paper a file prints on, in inches, turned the way the order says.</summary>
    public static (double Width, double Height) SheetInches(string? paperSize, string? orientation)
    {
        var (w, h) = (paperSize ?? "A4").ToUpperInvariant() switch
        {
            "A3" => (11.69, 16.54),
            "LETTER" => (8.5, 11.0),
            "LEGAL" => (8.5, 14.0),
            _ => (8.27, 11.69),
        };
        return string.Equals(orientation, "LANDSCAPE", StringComparison.OrdinalIgnoreCase) ? (h, w) : (w, h);
    }

    /// <summary>
    /// The printed sheet as an image <paramref name="pixelWidth"/> wide: white
    /// paper of the order's size and orientation, the page placed on it exactly
    /// as <see cref="DrawOnSheet"/> places it, in grey for a black-and-white
    /// order. This is what the dashboard shows the owner before they print.
    /// </summary>
    public static Bitmap RenderSheet(
        Image page, int pageDpi, double sheetWidth, double sheetHeight, int pixelWidth, bool blackAndWhite)
    {
        var pixelsPerInch = pixelWidth / sheetWidth;
        var pixelHeight = (int)Math.Round(sheetHeight * pixelsPerInch);
        var sheet = new Bitmap(pixelWidth, pixelHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        var (x, y, w, h) = Placement(sheetWidth, sheetHeight, (double)page.Width / pageDpi, (double)page.Height / pageDpi);
        var target = new RectangleF(
            (float)(x * pixelsPerInch), (float)(y * pixelsPerInch), (float)(w * pixelsPerInch), (float)(h * pixelsPerInch));

        if (!blackAndWhite)
        {
            graphics.DrawImage(page, target);
            return sheet;
        }

        // Luminance, the way a mono printer renders colour.
        var grey = new System.Drawing.Imaging.ColorMatrix(new[]
        {
            new[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
            new[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
            new[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { 0f, 0f, 0f, 0f, 1f },
        });
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetColorMatrix(grey);
        graphics.DrawImage(page, Rectangle.Round(target), 0, 0, page.Width, page.Height, GraphicsUnit.Pixel, attributes);
        return sheet;
    }

    public static void DrawOnSheet(PrintPageEventArgs args, Image page, int dpi)
    {
        var graphics = args.Graphics!;
        // Hundredths of an inch, the unit a printer's Graphics draws in.
        var sheet = args.PageBounds;
        var (x, y, width, height) = Placement(
            sheet.Width / 100.0, sheet.Height / 100.0, (double)page.Width / dpi, (double)page.Height / dpi);

        // The Graphics origin is the corner of the printable area, not of the
        // paper (OriginAtMargins is false), so the hard margin is taken back
        // off to place the page against the sheet's real edges.
        var left = x * 100.0 - args.PageSettings.HardMarginX;
        var top = y * 100.0 - args.PageSettings.HardMarginY;
        width *= 100.0;
        height *= 100.0;

        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.DrawImage(page, (float)left, (float)top, (float)width, (float)height);
    }
}
