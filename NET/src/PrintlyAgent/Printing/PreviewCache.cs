using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PrintlyAgent.Printing;

/// <summary>
/// The owner's preview of each held file, drawn once and kept on this disk.
///
/// <para>
/// Drawing a page is the slow part - PDFium renders it, the sheet is composed,
/// the image encoded - and it used to happen on the click, so the preview
/// opened on a blank sheet for a second or more even though the file itself
/// had been on this PC for hours. Now every page is drawn as soon as its file
/// arrives (and at start-up for anything already held), and a click only reads
/// a small JPEG back off the disk.
/// </para>
///
/// <para>
/// One width for every preview: <see cref="PreviewWidth"/> pixels is sharp on a
/// large monitor and small enough to draw quickly and fetch instantly. The key
/// includes the file's size and time and the sheet it prints on, so a replaced
/// file or corrected settings draw afresh rather than showing a stale page.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class PreviewCache
{
    /// <summary>A4 at about 170 dpi.</summary>
    public const int PreviewWidth = 1400;

    /// <summary>
    /// Bumped whenever the way a sheet is drawn changes, so previews drawn the
    /// old way are not served again - 2: the backend's frame round a photo is
    /// cut away (ConvertedImagePlacement).
    /// </summary>
    private const int DrawingVersion = 2;

    /// <summary>Pages drawn ahead of time per file. Later pages are drawn when asked for.</summary>
    private const int WarmPages = 12;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> PageCounts = new(StringComparer.Ordinal);

    /// <summary>Set once at start-up; empty turns the cache off (tests).</summary>
    public static string Root { get; set; } = "";

    public static ILogger? Log { get; set; }

    /// <summary>How many pages the file has, remembered per version of the file.</summary>
    public static int PageCount(string pdfPath)
    {
        var info = new FileInfo(pdfPath);
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return PageCounts.GetOrAdd(key, _ =>
        {
            using var renderer = new PdfPageRenderer(pdfPath, 36);
            return renderer.PageCount;
        });
    }

    /// <summary>
    /// Page <paramref name="pageNumber"/> (1-based) as the printed sheet, as a
    /// JPEG: from the disk when already drawn, drawn and kept otherwise.
    /// </summary>
    public static byte[] SheetJpeg(
        string pdfPath, int pageNumber, string? orientation, string? colorMode, string? paperSize)
    {
        var file = CachePath(pdfPath, pageNumber, orientation, colorMode, paperSize);
        if (file is not null && File.Exists(file)) return File.ReadAllBytes(file);

        var gate = Locks.GetOrAdd(file ?? pdfPath + pageNumber, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            // Drawn by whoever held the gate while this one waited.
            if (file is not null && File.Exists(file)) return File.ReadAllBytes(file);

            var bytes = Draw(pdfPath, pageNumber, orientation, colorMode, paperSize);
            if (file is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    var temp = file + ".tmp";
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, file, overwrite: true);
                }
                catch (IOException) { /* served anyway; drawn again next time */ }
            }
            return bytes;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Draws the first pages of every file given, in the background, so the
    /// first click on any of them is instant. Never throws.
    /// </summary>
    public static void WarmInBackground(
        IEnumerable<(string Path, string? Orientation, string? ColorMode, string? PaperSize)> files)
    {
        var list = files.ToList();
        if (list.Count == 0 || string.IsNullOrEmpty(Root)) return;
        _ = Task.Run(() =>
        {
            foreach (var (path, orientation, colorMode, paperSize) in list)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var pages = Math.Min(PageCount(path), WarmPages);
                    for (var page = 1; page <= pages; page++) SheetJpeg(path, page, orientation, colorMode, paperSize);
                }
                catch (Exception exc)
                {
                    if (Log is not null) Log.LogDebug(exc, "preview_warm_failed path={Path}", path);
                }
            }
        });
    }

    /// <summary>Removes previews nobody has needed for a week - their files have long gone.</summary>
    public static void Sweep()
    {
        if (string.IsNullOrEmpty(Root) || !Directory.Exists(Root)) return;
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var file in Directory.EnumerateFiles(Root))
        {
            try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] Draw(string pdfPath, int pageNumber, string? orientation, string? colorMode, string? paperSize)
    {
        var (sheetW, sheetH) = PageLayout.SheetInches(paperSize, orientation);
        var dpi = Math.Clamp((int)Math.Ceiling(PreviewWidth / sheetW), 36, 200);
        using var renderer = new PdfPageRenderer(pdfPath, dpi);
        if (pageNumber < 1 || pageNumber > renderer.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var page = renderer.RenderPage(pageNumber - 1);
        var blackAndWhite = string.Equals(colorMode, "BLACK_AND_WHITE", StringComparison.OrdinalIgnoreCase);
        using var sheet = PageLayout.RenderSheet(page, dpi, sheetW, sheetH, PreviewWidth, blackAndWhite);

        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        using var buffer = new MemoryStream();
        sheet.Save(buffer, encoder, parameters);
        return buffer.ToArray();
    }

    private static string? CachePath(string pdfPath, int page, string? orientation, string? colorMode, string? paperSize)
    {
        if (string.IsNullOrEmpty(Root)) return null;
        var info = new FileInfo(pdfPath);
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{page}|" +
                  $"{orientation ?? "PORTRAIT"}|{colorMode ?? "COLOR"}|{paperSize ?? "A4"}|{PreviewWidth}|v{DrawingVersion}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(Root, hash + ".jpg");
    }
}
