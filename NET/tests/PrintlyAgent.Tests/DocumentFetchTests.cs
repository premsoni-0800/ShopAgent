using System.Net;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// What happens when the document is not there to fetch.
///
/// This is the shape of a real incident. An order was placed whose upload never
/// finished, so the backend had an item but no file behind it. The agent claimed
/// the job, asked for the document, and got a refusal - and then treated that
/// refusal as a blip and asked again, three times, sixty seconds of download
/// timeout apiece with backoff in between. A shop runs one print worker by
/// default, so for those three minutes nothing else in the shop printed: the
/// queue was held by an order that was never going to print at all.
///
/// So the distinction these tests pin is not cosmetic. "The server said no" and
/// "the server did not answer" have to be handled differently, or a permanently
/// missing document becomes an outage for every other order behind it.
/// </summary>
public class DocumentFetchTests : IDisposable
{
    private readonly HttpListener _server = new();
    private readonly string _baseUrl;
    private readonly string _tempDir;
    private int _status = 200;
    private byte[] _body = Array.Empty<byte>();

    public int Requests { get; private set; }

    public DocumentFetchTests()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _baseUrl = $"http://127.0.0.1:{port}/";
        _server.Prefixes.Add(_baseUrl);
        _server.Start();
        _ = Task.Run(async () =>
        {
            while (_server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _server.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; }

                Requests++;
                ctx.Response.StatusCode = _status;
                ctx.Response.ContentLength64 = _body.Length;
                if (_body.Length > 0) await ctx.Response.OutputStream.WriteAsync(_body).ConfigureAwait(false);
                ctx.Response.Close();
            }
        });

        _tempDir = Path.Combine(Path.GetTempPath(), "printly-fetch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // Awaited inside, not returned unawaited: with `using` and a bare `return`
    // the HttpClient is disposed the instant the task is handed back, and every
    // one of these fails as a cancellation instead of what it was testing.
    private async Task<string> FetchAsync()
    {
        using var http = new HttpClient();
        return await Documents
            .DownloadDocumentAsync(http, _baseUrl + "doc.pdf", _tempDir, timeoutSeconds: 5)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The incident itself: the item exists, the file behind it does not.
    /// </summary>
    [Fact(DisplayName = "a document that was never uploaded is refused, and says so")]
    public async Task AMissingDocumentIsRefused()
    {
        _status = 404;

        var error = await Assert.ThrowsAsync<DocumentFetchError>(FetchAsync);

        Assert.Equal(404, error.StatusCode);
        Assert.False(error.WorthRetrying, "a 404 will still be a 404 in two seconds");
    }

    /// <summary>A signed URL that has aged out is equally settled.</summary>
    [Fact(DisplayName = "an expired signature is not worth asking about again")]
    public async Task AnExpiredSignatureIsNotRetried()
    {
        _status = 403;

        var error = await Assert.ThrowsAsync<DocumentFetchError>(FetchAsync);

        Assert.Equal(403, error.StatusCode);
        Assert.False(error.WorthRetrying);
    }

    /// <summary>
    /// The other half, and the reason this is a classification rather than a
    /// blanket "never retry": the backend is on a host that cold-starts, so a
    /// 5xx really is a blip and really does clear.
    /// </summary>
    [Fact(DisplayName = "a server having a bad moment is still worth asking again")]
    public async Task AServerErrorIsStillRetried()
    {
        _status = 503;

        var error = await Assert.ThrowsAsync<DocumentFetchError>(FetchAsync);

        Assert.Equal(503, error.StatusCode);
        Assert.True(error.WorthRetrying, "a cold-starting host answers on the next attempt");
    }

    [Fact(DisplayName = "being asked to slow down is worth asking again")]
    public void BackpressureIsRetried()
    {
        Assert.True(new DocumentFetchError(429, "too many requests").WorthRetrying);
        Assert.True(new DocumentFetchError(408, "request timeout").WorthRetrying);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Close();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* the temp dir is disposable either way */ }
        GC.SuppressFinalize(this);
    }
}
