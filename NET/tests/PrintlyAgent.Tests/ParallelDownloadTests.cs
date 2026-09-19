using System.Diagnostics;
using System.Net;
using System.Text;
using PrintlyAgent.Credentials;
using PrintlyAgent.Db;
using PrintlyAgent.Jobs;
using PrintlyAgent.Models;
using PrintlyAgent.Net;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// An order's documents are fetched together, not one after another.
///
/// This time is spent between the shop being told the order is being prepared
/// and anything reaching a printer, so on a multi-document order it was the
/// difference between waiting for the slowest file and waiting for all of them
/// added up.
///
/// Served from a real loopback listener rather than a stubbed client, because
/// the claim being made here is about wall-clock overlap of actual sockets - a
/// fake that returns instantly could not tell the two shapes apart.
/// </summary>
public class ParallelDownloadTests : IDisposable
{
    private const int DocumentCount = 4;
    private static readonly TimeSpan PerDocument = TimeSpan.FromMilliseconds(400);

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"printly-parallel-{Guid.NewGuid():N}");
    private readonly HttpListener _listener = new();
    private readonly List<IDisposable> _disposables = new();
    private readonly string _origin;

    /// <summary>Documents the listener was actually asked for.</summary>
    private int _documentsRequested;

    public ParallelDownloadTests()
    {
        Directory.CreateDirectory(_tempDir);

        var port = FreePort();
        _origin = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_origin}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    [Fact(DisplayName = "an order's documents are fetched together, not one after another")]
    public async Task DocumentsDownloadInParallel()
    {
        var ctx = NewContext("agent.db", "job-parallel", "order-1", "AA-000001");

        var detail = new PrintJobDetail(
            "job-parallel", "order-1", "AA-000001", PrintJobStatus.CLAIMED,
            Enumerable.Range(0, DocumentCount).Select(i => Item($"item-{i}", $"doc-{i}")).ToList());

        var clock = Stopwatch.StartNew();
        var downloaded = await JobPipeline.DownloadAllAsync(ctx, "job-parallel", detail, CancellationToken.None);
        clock.Stop();

        Assert.Equal(DocumentCount, downloaded.Count);
        foreach (var item in detail.Items)
        {
            Assert.True(File.Exists(downloaded[item.ItemId]), $"{item.ItemId} did not land on disk");
        }

        // Serial would be four times PerDocument. Halfway between one and four
        // is the threshold: high enough that ordinary listener overhead cannot
        // trip it, low enough that nothing serial can pass it.
        var serial = PerDocument.TotalMilliseconds * DocumentCount;
        Assert.True(
            clock.Elapsed.TotalMilliseconds < serial / 2,
            $"took {clock.ElapsedMilliseconds}ms for {DocumentCount} documents of "
            + $"{PerDocument.TotalMilliseconds}ms each - they were fetched one at a time");
    }

    /// <summary>
    /// The failure the parallel version still has to handle: one document
    /// missing from the server's reply fails the order.
    ///
    /// Asserted on what the server was asked for, not on what is left in the
    /// temp directory - the cleanup on failure removes a partial download
    /// either way, so files on disk cannot tell the two shapes apart. The
    /// difference that matters is that a doomed order now pulls nothing down at
    /// all, instead of fetching its way up to the missing one and discarding
    /// the work.
    /// </summary>
    [Fact(DisplayName = "a document with no URL fails before anything is fetched")]
    public async Task AMissingUrlFetchesNothing()
    {
        var ctx = NewContext("agent-missing.db", "job-missing", "order-2", "AA-000002");

        // doc-99 is not one the listener offers a URL for.
        var detail = new PrintJobDetail(
            "job-missing", "order-2", "AA-000002", PrintJobStatus.CLAIMED,
            new List<PrintJobItem> { Item("item-0", "doc-0"), Item("item-1", "doc-99") });

        await Assert.ThrowsAsync<DocumentValidationError>(
            () => JobPipeline.DownloadAllAsync(ctx, "job-missing", detail, CancellationToken.None));

        // item-0's URL was perfectly good. Fetching it would have been wasted
        // bandwidth on an order that was never going to print.
        Assert.Equal(0, Volatile.Read(ref _documentsRequested));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.pdf"));
    }

    private JobContext NewContext(string dbName, string jobId, string orderId, string orderCode)
    {
        var db = new Database(Path.Combine(_tempDir, dbName));
        var api = new PrintlyApiClient(_origin);
        _disposables.Add(db);
        _disposables.Add(api);

        db.InsertJobReference(jobId, orderId, orderCode, shopId: "shop-1");
        // Where a real job stands when DownloadAllAsync is reached: claimed and
        // checked. The local state machine will not take RECEIVED -> DOWNLOADING.
        db.UpdateJobState(jobId, JobPipeline.VALIDATING);

        return new JobContext(
            Api: api,
            Db: db,
            TempDir: _tempDir,
            HeldDir: Path.Combine(_tempDir, "..", "held"),
            MaxRetryAttempts: 1,
            DownloadTimeoutSeconds: 30,
            JobStallSeconds: 300.0,
            Credential: new AgentCredential("agent-1", "shop-1", "secret"));
    }

    private static PrintJobItem Item(string itemId, string documentId) => new(
        ItemId: itemId,
        DocumentId: documentId,
        FileName: $"{documentId}.pdf",
        PaperSize: PaperSize.A4,
        ColorMode: ColorMode.BLACK_AND_WHITE,
        DuplexMode: DuplexMode.SINGLE_SIDED,
        Orientation: Orientation.PORTRAIT,
        Copies: 1,
        PageRange: null,
        DocumentPageCount: 1);

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return; // stopped
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await RespondAsync(context);
                }
                catch
                {
                    // The client went away; nothing here to report it to.
                }
            });
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;

        if (path.EndsWith("/download-url", StringComparison.Ordinal))
        {
            // doc-99 is deliberately absent, for AMissingUrlFetchesNothing.
            var items = string.Join(",", Enumerable.Range(0, DocumentCount).Select(i =>
                "{" + $"\"documentId\":\"doc-{i}\",\"url\":\"{_origin}/doc/{i}\","
                    + $"\"expiresAt\":\"2099-01-01T00:00:00Z\",\"fileName\":\"doc-{i}.pdf\"" + "}"));
            var body = "{\"items\":[" + items + "]}";
            await WriteAsync(context, 200, "application/json", Encoding.UTF8.GetBytes(body));
            return;
        }

        if (path.StartsWith("/doc/", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _documentsRequested);
            // The whole point of the first test: each document takes real time,
            // so four of them one after another is visibly different from four
            // at once.
            await Task.Delay(PerDocument);
            await WriteAsync(context, 200, "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
            return;
        }

        // Progress pings and anything else. Answered rather than ignored, so a
        // fire-and-forget report cannot leave a connection hanging.
        await WriteAsync(context, 204, "text/plain", Array.Empty<byte>());
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string type, byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        context.Response.ContentLength64 = body.Length;
        if (body.Length > 0) await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already down */ }
        try { _listener.Close(); } catch { /* already down */ }
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
