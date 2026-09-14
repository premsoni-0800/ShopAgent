using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Ui;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// What the proxy actually puts on the wire for a POST with nothing to send.
///
/// Every interesting order transition is one of those - accept, start-printing,
/// mark-printed, mark-ready, collect - and getting this request wrong once
/// already broke the whole set: an empty body attached as content made the HTTP
/// client invent a Content-Type of application/x-www-form-urlencoded, the
/// backend read a form submission where it expected none, and the answer came
/// back 500 INTERNAL_ERROR - which the dashboard shows as "Something went
/// wrong". The proxy therefore attaches no content at all in that case, and
/// these tests exist to stop anyone "tidying" that back.
///
/// The obvious worry about attaching nothing is that the Content-Length goes
/// with it and the backend answers 411 Length Required instead. It does not:
/// HttpClient still frames a body-bearing method with Content-Length: 0 of its
/// own accord. That is an implementation detail of somebody else's library
/// rather than a promise, which is exactly why it is asserted here rather than
/// assumed.
///
/// The stub is a bare socket rather than an HttpListener on purpose. All of
/// this is about which header bytes leave the machine, and HttpListener hides
/// precisely that - it reports a missing Content-Length as 0, so a listener
/// based stub cannot tell the two apart. This was written that way first, and
/// it could not.
/// </summary>
[SupportedOSPlatform("windows")]
public class ApiProxyTests : IDisposable
{
    private readonly TcpListener _backend;
    private readonly string _backendUrl;
    private readonly TaskCompletionSource<string> _request = new();

    public ApiProxyTests()
    {
        _backend = new TcpListener(IPAddress.Loopback, 0);
        _backend.Start();
        _backendUrl = $"http://127.0.0.1:{((IPEndPoint)_backend.LocalEndpoint).Port}/";

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await _backend.AcceptTcpClientAsync().ConfigureAwait(false);
                using var stream = client.GetStream();

                // Read until the blank line that ends the headers, then whatever
                // body the declared length promised.
                var raw = new MemoryStream();
                var buffer = new byte[1];
                var seen = new StringBuilder();
                while (await stream.ReadAsync(buffer).ConfigureAwait(false) == 1)
                {
                    raw.WriteByte(buffer[0]);
                    seen.Append((char)buffer[0]);
                    if (seen.Length >= 4 && seen.ToString(seen.Length - 4, 4) == "\r\n\r\n") break;
                }

                var head = Encoding.ASCII.GetString(raw.ToArray());
                var declared = 0;
                foreach (var line in head.Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line[15..].Trim(), out declared);
                }

                var body = new byte[declared];
                var read = 0;
                while (read < declared)
                {
                    var n = await stream.ReadAsync(body.AsMemory(read)).ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }

                _request.TrySetResult(head + Encoding.UTF8.GetString(body, 0, read));

                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _request.TrySetException(e);
            }
        });
    }

    /// <summary>The literal bytes the proxy sent upstream.</summary>
    private async Task<string> SendAsync(Func<HttpClient, string, Task> call)
    {
        using var proxy = new WebUiServer(
            NullLogger.Instance,
            _backendUrl,
            Path.Combine(Path.GetTempPath(), "printly-proxy-tests-no-content"),
            FreePort());
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        await call(client, proxy.BaseUrl);
        return await _request.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact(DisplayName = "a POST with nothing to send still declares a length the backend can read")]
    public async Task ABodylessPostDeclaresAZeroLength()
    {
        var sent = await SendAsync((c, b) => c.PostAsync(b + "api/v1/orders/x/collect", null));

        Assert.StartsWith("POST /api/v1/orders/x/collect", sent);

        // Were this ever missing, the backend would answer 411 Length Required
        // and the button that sent it would report a failure the shop cannot
        // act on.
        Assert.Contains("Content-Length: 0", sent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "a POST with nothing to send does not claim to be a form submission")]
    public async Task ABodylessPostInventsNoContentType()
    {
        var sent = await SendAsync((c, b) => c.PostAsync(b + "api/v1/orders/x/mark-printed", null));

        // This is the one that produced 500 INTERNAL_ERROR, and so "Something
        // went wrong", for every order button in the Kotlin agent.
        Assert.DoesNotContain("x-www-form-urlencoded", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content-Type:", sent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "a POST that does have a body keeps both the body and its type")]
    public async Task ABodiedPostIsForwardedIntact()
    {
        var sent = await SendAsync((c, b) => c.PostAsync(
            b + "api/v1/print-agents/register",
            new StringContent("""{"printerName":"Counter"}""", Encoding.UTF8, "application/json")));

        Assert.Contains("""{"printerName":"Counter"}""", sent);
        Assert.Contains("Content-Type: application/json", sent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Length: 25", sent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half: a GET must not pick up a body on the way through, since
    /// a GET carrying content is treated as malformed by some servers.
    /// </summary>
    [Fact(DisplayName = "a GET is not given a body on its way through")]
    public async Task AGetStaysBodyless()
    {
        var sent = await SendAsync((c, b) => c.GetAsync(b + "api/v1/orders"));

        Assert.StartsWith("GET /api/v1/orders", sent);
        Assert.DoesNotContain("Content-Length:", sent, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("\r\n\r\n", sent);
    }

    public void Dispose()
    {
        _backend.Stop();
        GC.SuppressFinalize(this);
    }
}
