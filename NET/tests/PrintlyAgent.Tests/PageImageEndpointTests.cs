using System.Drawing;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Ui;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// A held file served as sheet images - what the dashboard shows instead of a
/// PDF viewer, so a page looks like the paper and cannot be zoomed.
/// </summary>
[SupportedOSPlatform("windows")]
public class PageImageEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "printly-page-images", Guid.NewGuid().ToString("N"));

    public PageImageEndpointTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string TwoPagePdf()
    {
        const string content = "0 0 0 rg 0 0 595 842 re f";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] >>",
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
        var path = Path.Combine(_dir, "doc.pdf");
        File.WriteAllText(path, pdf.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact(DisplayName = "a held file is previewed as the sheet it prints on")]
    public async Task AHeldFileIsPreviewedAsItsPrintedSheet()
    {
        var pdf = TwoPagePdf();
        var settings = ("LANDSCAPE", "BLACK_AND_WHITE", "A4");
        using var server = new WebUiServer(NullLogger.Instance, "http://127.0.0.1:1", _dir, FreePort())
        {
            HeldFileResolver = (order, item) => order == "o1" && item == "i1" ? pdf : null,
            HeldSettingsResolver = (_, _) => settings,
        };
        using var http = new HttpClient();

        // Every page is described as the order's sheet: A4 turned landscape.
        var list = JsonDocument.Parse(await http.GetStringAsync(server.BaseUrl + "local/files/o1/i1/pages")).RootElement;
        var pages = list.GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal(2, pages.Count);
        Assert.All(pages, p => Assert.True(p.GetProperty("width").GetInt32() > p.GetProperty("height").GetInt32()));

        // Page 1 is an upright A4 page, solid black, on that landscape sheet: it
        // is fitted to the sheet's height and centred - exactly where
        // PageLayout.DrawOnSheet puts it on paper - with white either side.
        var response = await http.GetAsync(server.BaseUrl + "local/files/o1/i1/page/1?w=842");
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        using var sheet = new Bitmap(await response.Content.ReadAsStreamAsync());
        // One preview size, drawn ahead of time - see PreviewCache.
        Assert.Equal(PrintlyAgent.Printing.PreviewCache.PreviewWidth, sheet.Width);
        Assert.InRange(sheet.Height, 985, 995);

        var (x, _, w, _) = PrintlyAgent.Printing.PageLayout.Placement(11.69, 8.27, 8.27, 11.69);
        var inkLeft = (int)(x / 11.69 * sheet.Width);
        var inkRight = (int)((x + w) / 11.69 * sheet.Width);
        var mid = sheet.Height / 2;
        Assert.True(sheet.GetPixel(inkLeft - 6, mid).GetBrightness() > 0.9f, "white left of the page");
        Assert.True(sheet.GetPixel((inkLeft + inkRight) / 2, mid).GetBrightness() < 0.2f, "the page's ink");
        Assert.True(sheet.GetPixel(inkRight + 6, mid).GetBrightness() > 0.9f, "white right of the page");

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.BaseUrl + "local/files/o1/i1/page/3")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.BaseUrl + "local/files/other/i1/pages")).StatusCode);
    }
}
