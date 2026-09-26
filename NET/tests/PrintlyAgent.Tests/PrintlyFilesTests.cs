using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Credentials;
using PrintlyAgent.Db;
using PrintlyAgent.Jobs;
using PrintlyAgent.Net;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// An order's files arriving on the shop PC before its student does.
///
/// Run against a stub backend that speaks the real device API, so what is
/// pinned is the whole prefetch: the files land in PrintlyFiles under names a
/// person can read, and the backend is told they are there - the report that
/// moves the student's timeline to "Order accepted", which was never sent.
/// </summary>
public class PrintlyFilesTests : IDisposable
{
    private const string ShopId = "shop-1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printly-files-tests", Guid.NewGuid().ToString("N"));
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly string _baseUrl;

    public PrintlyFilesTests()
    {
        Directory.CreateDirectory(_root);
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(_baseUrl + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }

            var path = context.Request.Url!.AbsolutePath;
            _requests.Enqueue($"{context.Request.HttpMethod} {path}");
            var (type, body) = path switch
            {
                "/api/v1/print-agent/jobs/job-1" or "/api/v1/print-agent/jobs/job-1/cached" => ("application/json", Detail),
                "/api/v1/print-agent/jobs/job-1/download-url" => ("application/json", Urls),
                "/doc/1" or "/doc/2" => ("application/pdf", "%PDF-1.4 " + path),
                _ => ("text/plain", "not found"),
            };
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = body == "not found" ? 404 : 200;
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private const string Detail = """
        {"jobId":"job-1","orderId":"order-uuid-1","orderCode":"PPP01-000252","status":"CREATED","items":[
          {"itemId":"item-a","documentId":"doc-a","fileName":"Screenshot_20260918.jpg","paperSize":"A4","colorMode":"BLACK_AND_WHITE","duplexMode":"SINGLE_SIDED","orientation":"PORTRAIT","copies":1,"pageRange":null,"documentPageCount":1},
          {"itemId":"item-b","documentId":"doc-b","fileName":"Notes: week 3?.pdf","paperSize":"A4","colorMode":"COLOR","duplexMode":"SINGLE_SIDED","orientation":"PORTRAIT","copies":1,"pageRange":null,"documentPageCount":2}
        ]}
        """;

    private string Urls => $$"""
        {"items":[
          {"documentId":"doc-a","url":"{{_baseUrl}}/doc/1","expiresAt":"2030-01-01T00:00:00Z","fileName":"a"},
          {"documentId":"doc-b","url":"{{_baseUrl}}/doc/2","expiresAt":"2030-01-01T00:00:00Z","fileName":"b"}
        ]}
        """;

    public void Dispose()
    {
        _listener.Stop();
        ((IDisposable)_listener).Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact(DisplayName = "a waiting order's files land in PrintlyFiles, readable, and the backend is told")]
    public async Task PrefetchFillsPrintlyFilesAndReportsCached()
    {
        var filesDir = Path.Combine(_root, "PrintlyFiles");
        Directory.CreateDirectory(filesDir);
        using var db = new Database(Path.Combine(_root, "agent.db"));
        using var api = new PrintlyApiClient(_baseUrl);
        var ctx = new JobContext(
            Api: api, Db: db, TempDir: Path.Combine(_root, "tmp"), FilesDir: filesDir,
            MaxRetryAttempts: 3, DownloadTimeoutSeconds: 30, JobStallSeconds: 300.0,
            Credential: new AgentCredential("agent-1", ShopId, "secret"));

        await JobPipeline.PrefetchOrderFilesAsync(ctx, "job-1", "order-uuid-1", CancellationToken.None);

        var folder = Path.Combine(filesDir, "PPP01-000252 (order-uu)");
        Assert.True(Directory.Exists(folder), "the order's folder is named by its number");
        Assert.True(File.Exists(Path.Combine(folder, "1 - Screenshot_20260918.pdf")));
        Assert.True(File.Exists(Path.Combine(folder, "2 - Notes_ week 3_.pdf")), "characters Windows refuses are replaced");
        Assert.Equal(2, db.HeldFilesForOrder("order-uuid-1").Count);
        Assert.Contains("POST /api/v1/print-agent/jobs/job-1/cached", _requests);

        // Printed: the folder goes, and so do the rows.
        JobPipeline.DeleteHeldOrder(db, filesDir, "order-uuid-1");
        Assert.False(Directory.Exists(folder));
        Assert.Empty(db.HeldFilesForOrder("order-uuid-1"));
        Assert.True(Directory.Exists(filesDir), "PrintlyFiles itself is never removed");
    }

    [Fact(DisplayName = "files an older version held elsewhere are moved into PrintlyFiles on start")]
    public void OldHeldFilesAreMovedIntoPrintlyFiles()
    {
        var filesDir = Path.Combine(_root, "PrintlyFiles");
        var legacy = Path.Combine(_root, "appdata", "files", "order-uuid-9");
        Directory.CreateDirectory(legacy);
        var oldPath = Path.Combine(legacy, "item-a.pdf");
        File.WriteAllText(oldPath, "%PDF-1.4 old");

        using var db = new Database(Path.Combine(_root, "agent-move.db"));
        db.InsertJobReference("job-9", "order-uuid-9", "PPP01-000191", shopId: ShopId);
        db.UpsertHeldFile("order-uuid-9", "item-a", ShopId, "Screenshot_20260914.png", oldPath, 12);
        var settings = new PrintlyAgent.Core.Settings(
            BackendBaseUrl: "http://127.0.0.1:1", Msg91WidgetId: "", AppDataDir: _root,
            DbPath: Path.Combine(_root, "x.db"), LogDir: _root, TempDir: Path.Combine(_root, "tmp"), FilesDir: filesDir);

        var moved = PrintlyAgent.Core.AgentCore.MoveHeldFilesIntoPrintlyFiles(settings, db, NullLogger.Instance);

        var expected = Path.Combine(filesDir, "PPP01-000191 (order-uu)", "1 - Screenshot_20260914.pdf");
        Assert.Equal(1, moved);
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(oldPath));
        Assert.Equal(expected, db.HeldFilesForOrder("order-uuid-9").Single().LocalPath);
        Assert.Equal(0, PrintlyAgent.Core.AgentCore.MoveHeldFilesIntoPrintlyFiles(settings, db, NullLogger.Instance));
    }

    [Fact(DisplayName = "a file name that is nothing but punctuation still gets a name")]
    public void AnUnusableNameFallsBack()
    {
        Assert.Equal("item-9", JobPipeline.ReadableFileStem("???.pdf".Replace('?', '.'), "item-9"));
        Assert.Equal("thesis final", JobPipeline.ReadableFileStem("thesis final.docx", "x"));
    }
}
