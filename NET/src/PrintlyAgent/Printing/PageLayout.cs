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
    public static void DrawOnSheet(PrintPageEventArgs args, Image page, int dpi)
    {
        var graphics = args.Graphics!;
        // Hundredths of an inch, the unit a printer's Graphics draws in.
        var sheet = args.PageBounds;
        var pageWidth = page.Width * 100.0 / dpi;
        var pageHeight = page.Height * 100.0 / dpi;

        var scale = Math.Min(sheet.Width / pageWidth, sheet.Height / pageHeight);
        if (Math.Abs(scale - 1.0) <= ActualSizeTolerance) scale = 1.0;

        var width = pageWidth * scale;
        var height = pageHeight * scale;

        // The Graphics origin is the corner of the printable area, not of the
        // paper (OriginAtMargins is false), so the hard margin is taken back
        // off to place the page against the sheet's real edges.
        var hardX = args.PageSettings.HardMarginX;
        var hardY = args.PageSettings.HardMarginY;
        var left = (sheet.Width - width) / 2.0 - hardX;
        var top = (sheet.Height - height) / 2.0 - hardY;

        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.DrawImage(page, (float)left, (float)top, (float)width, (float)height);
    }
}
