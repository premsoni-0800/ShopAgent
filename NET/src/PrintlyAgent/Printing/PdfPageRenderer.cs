using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace PrintlyAgent.Printing;

/// <summary>
/// Rasterises PDF pages, one at a time, at a chosen DPI.
///
/// This is what PDFBox's PDFRenderer.renderImageWithDPI does on the Kotlin side.
/// .NET has no PDF renderer of its own, so this wraps PDFium (through Docnet) -
/// the same engine Chrome and Edge use. Shelling the document out to a viewer
/// was the alternative and was rejected: it cannot honour a page range, cannot
/// choose a DPI, and would have made the whole print path untestable.
///
/// <para>
/// One page is held at a time, deliberately. A4 at 150 DPI is roughly 8MB, and
/// pages are asked for in order, so keeping the last is all that is ever needed.
/// Keeping more would be a way to run a 3,000-page job out of memory.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PdfPageRenderer : IDisposable
{
    /// <summary>
    /// The DPI pages are rasterised at.
    ///
    /// 150 rather than 300, measured rather than guessed: on the Kotlin side 300
    /// DPI was tried and came out 19% slower with no visible difference on
    /// ordinary text, because the printer driver is scaling the bitmap to the
    /// sheet either way. Going lower starts to show on small type.
    /// </summary>
    public const int RenderDpi = 150;

    private readonly IDocLib _lib;
    private readonly IDocReader _reader;
    private readonly int _dpi;

    private int _cachedPage = -1;
    private Bitmap? _cached;

    public PdfPageRenderer(string pdfPath, int dpi = RenderDpi)
    {
        _dpi = dpi;
        _lib = DocLib.Instance;
        // Docnet wants the page dimensions up front; passing the DPI as both
        // axes asks it to scale from the page's own size, which is what
        // renderImageWithDPI does.
        _reader = _lib.GetDocReader(File.ReadAllBytes(pdfPath), new PageDimensions(dpi / 72.0));
    }

    public int PageCount => _reader.GetPageCount();

    /// <summary>
    /// The page at <paramref name="pageIndex"/> (0-based) as a bitmap.
    ///
    /// Cached by index because the print pipeline asks for every page at least
    /// twice - once to measure what is on it and once to draw it - and
    /// rasterising costs about as much as everything else put together. Doing it
    /// once per page rather than twice is close to halving the render time for
    /// nothing.
    /// </summary>
    public Bitmap RenderPage(int pageIndex)
    {
        if (_cachedPage == pageIndex && _cached is not null) return _cached;

        using var page = _reader.GetPageReader(pageIndex);
        var width = page.GetPageWidth();
        var height = page.GetPageHeight();
        var raw = page.GetImage();

        // PDFium hands back BGRA in which anything the page did not paint is
        // fully TRANSPARENT, not white. Copied straight to the printer that is
        // not a blank margin - it is undefined, and composites as black on a
        // driver that flattens alpha. PDFBox's renderImageWithDPI returns an RGB
        // image already composited onto white, so the port has to do the same
        // compositing itself or every page arrives with a black background.
        //
        // Caught by a test that rendered a deliberately empty page and measured
        // its ink coverage; it came back 100% covered.
        using var argb = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = argb.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(raw, 0, data.Scan0, raw.Length);
        }
        finally
        {
            argb.UnlockBits(data);
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(argb, 0, 0);
        }
        bitmap.SetResolution(_dpi, _dpi);

        _cached?.Dispose();
        _cached = bitmap;
        _cachedPage = pageIndex;
        return bitmap;
    }

    public void Dispose()
    {
        _cached?.Dispose();
        _reader.Dispose();
        // DocLib.Instance is a process-wide singleton; disposing it here would
        // pull PDFium out from under any other job still rendering.
    }
}
