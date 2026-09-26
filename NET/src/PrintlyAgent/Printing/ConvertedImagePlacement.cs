using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PrintlyAgent.Printing;

/// <summary>
/// Recognises a PDF the backend made out of an uploaded photo and says where
/// the photo sits on it.
///
/// <para>
/// The backend's image converter draws the photo, and nothing else, on an A4
/// page, centred, inside a 24pt margin. The student's own preview has no such
/// margin - it fits the photo to the sheet - so printing the page as it comes
/// put a white frame round every photo that nobody asked for. Cutting the photo
/// back out on this side makes the print the student's preview whether or not
/// the backend still adds the frame; once it does not, the photo fills its page
/// and this finds nothing to cut.
/// </para>
///
/// <para>
/// Deliberately narrow, because a student's own PDF must never be cropped: a
/// single content stream that is exactly <c>q w 0 0 h x y cm /Im Do Q</c>,
/// centred on the page, with a 24pt gap on its tightest side. Anything else
/// returns null and prints untouched.
/// </para>
/// </summary>
public static class ConvertedImagePlacement
{
    private const double ConverterMarginPt = 24.0;

    private static readonly Regex StreamHeader = new(
        @"<<(?<dict>(?:(?!>>).){0,400}?)>>\s*stream\r?\n", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex SoleImageDraw = new(
        @"^\s*q\s+(?<w>[\d.]+)\s+0\s+0\s+(?<h>[\d.]+)\s+(?<x>-?[\d.]+)\s+(?<y>-?[\d.]+)\s+cm\s+/\S+\s+Do\s+Q\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex MediaBox = new(
        @"/MediaBox\s*\[\s*(?<x0>-?[\d.]+)\s+(?<y0>-?[\d.]+)\s+(?<x1>-?[\d.]+)\s+(?<y1>-?[\d.]+)\s*\]",
        RegexOptions.CultureInvariant);

    /// <summary>(x, y, w, h) of the photo in points from the page's bottom-left, or null.</summary>
    public static (double X, double Y, double W, double H)? Find(byte[] pdf)
    {
        try
        {
            var text = Encoding.Latin1.GetString(pdf);
            (double X, double Y, double W, double H)? draw = null;
            var drawStreams = 0;
            (double W, double H)? page = null;

            foreach (Match header in StreamHeader.Matches(text))
            {
                var dict = header.Groups["dict"].Value;
                if (dict.Contains("/Subtype /Image") || dict.Contains("/Subtype/Image")) continue;
                var length = Regex.Match(dict, @"/Length\s+(\d+)\b");
                if (!length.Success) continue;
                var size = int.Parse(length.Groups[1].Value, CultureInfo.InvariantCulture);
                if (size <= 0 || size > 16_384 || header.Index + header.Length + size > pdf.Length) continue;

                var raw = new ReadOnlySpan<byte>(pdf, header.Index + header.Length, size).ToArray();
                var content = dict.Contains("/FlateDecode") ? Inflate(raw) : Encoding.Latin1.GetString(raw);
                if (content is null) continue;

                if (MediaBox.Match(content) is { Success: true } box) page = SizeOf(box);
                var match = SoleImageDraw.Match(content);
                if (match.Success)
                {
                    drawStreams++;
                    draw = (Num(match, "x"), Num(match, "y"), Num(match, "w"), Num(match, "h"));
                }
                else if (!dict.Contains("/Type /ObjStm") && !dict.Contains("/Type /XRef") && content.Contains(" Do"))
                {
                    return null; // some other drawing: a real document, leave it alone
                }
            }

            if (MediaBox.Match(text) is { Success: true } plainBox) page ??= SizeOf(plainBox);
            if (drawStreams != 1 || draw is not { } d || page is not { } p) return null;
            if (d.W <= 0 || d.H <= 0) return null;

            // Centred, inside a 24pt margin, touching it on the tighter side.
            var centred = Math.Abs(d.X - (p.W - d.W) / 2) < 1.0 && Math.Abs(d.Y - (p.H - d.H) / 2) < 1.0;
            var framed = Math.Abs(Math.Min(d.X, d.Y) - ConverterMarginPt) < 1.0;
            return centred && framed ? d : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (double W, double H) SizeOf(Match box) =>
        (Num(box, "x1") - Num(box, "x0"), Num(box, "y1") - Num(box, "y0"));

    private static double Num(Match match, string group) =>
        double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static string? Inflate(byte[] raw)
    {
        try
        {
            using var input = new MemoryStream(raw);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return Encoding.Latin1.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
