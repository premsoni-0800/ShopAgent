using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PrintlyAgent.Ui;

/// <summary>
/// The local origin both bundles and the API proxy are served from.
///
/// Port of the web-server half of ui/PrintlyAgentApp.kt. Kotlin uses
/// com.sun.net.httpserver; this uses HttpListener, which is the same shape - a
/// prefix, a handler, and a response stream you write yourself.
///
/// Served over http://127.0.0.1 rather than from a file: or resource URL for a
/// reason inherited from the original: webviews are unreliable about executing a
/// dynamically inserted third-party script (the MSG91 OTP widget) from a
/// non-http origin. A real http:// origin, even a local one, sidesteps that
/// entirely.
/// </summary>
public sealed class WebUiServer : IDisposable
{
    /// <summary>
    /// The port the local UI binds to, and so half of the origin the shop's
    /// saved session is filed under. Changing it signs every existing install
    /// out once, because their session is stored against the old one - so it is
    /// a constant rather than anything derived, and there is no reason to ever
    /// move it.
    ///
    /// 17384 is below the 49152+ range Windows draws ephemeral outbound ports
    /// from, which is the point: a port in that range can be taken by an
    /// unrelated outgoing connection between two launches.
    /// </summary>
    public const int DefaultPort = 17384;

    private readonly ILogger _log;
    private readonly string _backendBaseUrl;
    private readonly string _contentRoot;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _upstream;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public WebUiServer(ILogger log, string backendBaseUrl, string contentRoot, int? preferredPort = null)
    {
        _log = log;
        _backendBaseUrl = backendBaseUrl.TrimEnd('/');
        _contentRoot = contentRoot;

        // No timeout at all: this same client carries the dashboard's SSE
        // streams, and any ceiling here would tear down a healthy one. The
        // per-request deadline is the caller's cancellation token instead.
        _upstream = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            // The .NET half of Kotlin's relaxDnsFailureCaching.
            //
            // The JVM caches DNS *failures* process-wide, which is what that
            // line turns down; .NET has no such cache, so the port dropped it -
            // but .NET has the mirror-image problem instead. A pooled
            // connection is reused indefinitely by default and never re-resolves
            // the name behind it, so when the backend moves - and it is on a
            // host that reassigns addresses on every redeploy - the agent can go
            // on posting to an address that has stopped being the backend, with
            // no failure that a reconnect would clear. Capping the lifetime
            // forces a fresh lookup periodically. It never interrupts a request
            // in flight: an expired connection is retired once its current
            // request finishes, which is what makes it safe for the SSE streams.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        Port = Bind(preferredPort ?? PortFromEnvironment() ?? DefaultPort);
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    private static int? PortFromEnvironment() =>
        int.TryParse(Environment.GetEnvironmentVariable("PRINTLY_WEBUI_PORT"), out var port) ? port : null;

    /// <summary>
    /// Binds the same port every launch, falling back to any free one only if
    /// that port is taken.
    ///
    /// The port is part of the origin, and the browser engine keys local storage
    /// by origin. Binding an arbitrary port therefore handed the dashboard a
    /// brand new, empty storage area on every single launch, and the session it
    /// had carefully saved under the last port was simply not there to be found.
    /// The shop was asked to sign in again every time the app started.
    ///
    /// If it is taken anyway - a second copy of the agent, or something else
    /// squatting on it - binding any free port is much better than refusing to
    /// start, and costs only that one sign-in.
    /// </summary>
    private int Bind(int preferred)
    {
        if (IsFree(preferred)) return preferred;

        _log.LogWarning(
            "webui_port_unavailable port={Port} - binding an ephemeral port instead, so this session starts signed out",
            preferred);

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // listener stopped
            }

            // Deliberately not awaited: one request must never hold up the next,
            // and an SSE stream holds its own for as long as the page is open.
            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (!IsAddressedLocally(context.Request))
            {
                context.Response.StatusCode = 421;
                context.Response.Close();
                return;
            }

            if (path.StartsWith("/doc/", StringComparison.Ordinal))
            {
                await ProxyDocumentAsync(context, path, ct).ConfigureAwait(false);
            }
            else if (path.StartsWith("/api", StringComparison.Ordinal)
                || path.StartsWith("/actuator", StringComparison.Ordinal))
            {
                await ProxyToBackendAsync(context, ct).ConfigureAwait(false);
            }
            else
            {
                ServeStatic(context, path);
            }
        }
        catch (Exception exc)
        {
            _log.LogWarning(exc, "webui_server_request_failed");
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Refuses anything that did not address this server by its own loopback
    /// name and port.
    ///
    /// A page on the internet cannot read a response from here, but it can make
    /// the request - and DNS rebinding lets it resolve its own hostname to
    /// 127.0.0.1 and talk to this server as same-origin. The Host header is what
    /// separates that from the real UI, so it is checked rather than trusted.
    /// </summary>
    private bool IsAddressedLocally(HttpListenerRequest request)
    {
        var host = request.Headers["Host"];
        if (string.IsNullOrEmpty(host)) return false;
        return host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase);
    }

    // --- static files --------------------------------------------------------

    /// <summary>
    /// How long the webview may keep a file, and why index.html is never kept.
    ///
    /// Left to the engine's own heuristics, a stale index.html is the whole
    /// problem in one file: it names the hashed bundle to load, so holding on to
    /// yesterday's copy pins the whole dashboard to yesterday's build. The files
    /// on disk are correct, the app is launched fresh, and the window still shows
    /// the old screen - with nothing anywhere to say why.
    ///
    /// Everything under assets/ is safe to keep forever precisely because
    /// index.html is not: the bundler puts a content hash in each of those names,
    /// so a changed file is a different URL and can never be served stale.
    /// </summary>
    internal static string CacheControlFor(string path) =>
        path.Contains("/assets/", StringComparison.Ordinal)
            ? "public, max-age=31536000, immutable"
            : "no-store";

    internal static string ContentTypeFor(string path) => path switch
    {
        _ when path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) => "text/html; charset=utf-8",
        // .mjs alongside .js, and not as a nicety: the dashboard's PDF viewer
        // starts its renderer with `new Worker(url, { type: "module" })`, and a
        // module worker is refused outright unless the response is a JavaScript
        // MIME type. Served as application/octet-stream it failed silently -
        // the page simply reported that it could not preview the document, with
        // nothing to connect that to a content type.
        _ when path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            => "application/javascript; charset=utf-8",
        _ when path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) => "text/css; charset=utf-8",
        _ when path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => "application/json; charset=utf-8",
        _ when path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) => "image/svg+xml",
        _ when path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) => "image/png",
        _ when path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) => "image/x-icon",
        _ when path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) => "font/woff2",
        _ => "application/octet-stream",
    };

    private void ServeStatic(HttpListenerContext context, string path)
    {
        var resolved = path is "/" or "" ? "/dashboard/index.html" : null;

        var bytes = ReadContent(resolved)
            ?? ReadContent("/dashboard" + path)
            // SPA fallback - but never for a file request, where a 404 is the
            // honest answer and handing back HTML would surface as a baffling
            // "unexpected token <" in the console instead.
            ?? (path.Split('/').Last().Contains('.') ? null : ReadContent("/dashboard/index.html"));

        if (bytes is null)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        var typePath = resolved ?? path;
        context.Response.ContentType = ContentTypeFor(typePath);
        context.Response.Headers["Cache-Control"] = CacheControlFor(typePath);
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private byte[]? ReadContent(string? relative)
    {
        if (relative is null) return null;
        var full = Path.GetFullPath(Path.Combine(_contentRoot, relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        // Never serve outside the content root, whatever the request said.
        if (!full.StartsWith(Path.GetFullPath(_contentRoot), StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    // --- the API proxy -------------------------------------------------------

    private static bool IsSafeToRetry(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase)
        || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Serves one of an order's documents from this origin: GET
    /// /doc/{shopId}/{orderId}/{itemId}.
    ///
    /// The page could already ask the backend for a link and open it - that is
    /// what the preview does - but a link is a signed URL on the storage host,
    /// and anything that has to *read* the bytes from script rather than hand
    /// them to an &lt;iframe&gt; needs that host to allow this origin by CORS. It
    /// does not, and a shop counter is the wrong place to discover it: page
    /// thumbnails came up as "preview unavailable" with nothing to say why.
    ///
    /// So the bytes come back through here instead, which is same-origin and
    /// needs no permission from anyone. The two hops are the ones the page would
    /// have made itself - ask for the link, then fetch it - just made from this
    /// side of the window.
    ///
    /// Note what is NOT proxied: a URL supplied by the page. The only address
    /// fetched is the one this agent's own backend just returned, so a document
    /// route cannot be talked into fetching something else. The ids are checked
    /// as GUIDs before they are put into a path for the same reason.
    /// </summary>
    private async Task ProxyDocumentAsync(HttpListenerContext context, string path, CancellationToken ct)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !Guid.TryParse(parts[1], out var shopId)
            || !Guid.TryParse(parts[2], out var orderId)
            || !Guid.TryParse(parts[3], out var itemId))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        try
        {
            var linkUrl =
                $"{_backendBaseUrl}/api/v1/shop/{shopId}/orders/{orderId}/items/{itemId}/download-url";

            using var linkRequest = new HttpRequestMessage(HttpMethod.Get, linkUrl);
            // The page's own credentials, not the agent's. This route reaches
            // exactly the documents the signed-in shop could already reach.
            var authorization = context.Request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authorization))
            {
                linkRequest.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            using var linkResponse = await _upstream.SendAsync(linkRequest, ct).ConfigureAwait(false);
            if (!linkResponse.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "doc_proxy_link_refused status={Status} order={Order}",
                    (int)linkResponse.StatusCode, orderId);
                context.Response.StatusCode = (int)linkResponse.StatusCode;
                context.Response.Close();
                return;
            }

            var payload = await linkResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? signed;
            using (var document = JsonDocument.Parse(payload))
            {
                signed = document.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
            }

            // Absolute http(s) only. The backend is ours, but a malformed answer
            // should fail here rather than be handed to HttpClient.
            if (!Uri.TryCreate(signed, UriKind.Absolute, out var target)
                || target.Scheme is not ("http" or "https"))
            {
                _log.LogWarning("doc_proxy_bad_link order={Order}", orderId);
                context.Response.StatusCode = 502;
                context.Response.Close();
                return;
            }

            using var fileRequest = new HttpRequestMessage(HttpMethod.Get, target);
            using var fileResponse = await _upstream
                .SendAsync(fileRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            context.Response.StatusCode = (int)fileResponse.StatusCode;
            context.Response.ContentType =
                fileResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            // A customer's document, on a shared counter machine: held only for
            // as long as the page is looking at it.
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.SendChunked = true;

            await using var source = await fileResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await source.CopyToAsync(context.Response.OutputStream, ct).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception exc)
        {
            _log.LogWarning(exc, "doc_proxy_failed order={Order}", orderId);
            try { context.Response.StatusCode = 502; context.Response.Close(); } catch (Exception) { }
        }
    }

    private async Task ProxyToBackendAsync(HttpListenerContext context, CancellationToken ct, bool retried = false)
    {
        var request = context.Request;
        var target = _backendBaseUrl + request.Url!.PathAndQuery;

        try
        {
            using var upstream = new HttpRequestMessage(new HttpMethod(request.HttpMethod), target);

            byte[]? body = null;
            if (!IsSafeToRetry(request.HttpMethod))
            {
                using var buffer = new MemoryStream();
                await request.InputStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                body = buffer.ToArray();
            }

            // Only when there IS a body.
            //
            // Attaching an empty one makes the client supply a Content-Type of
            // its own - application/x-www-form-urlencoded - so every bodyless
            // POST arrives at the backend claiming to be a form submission and
            // comes back 500 INTERNAL_ERROR, whose message is the literal string
            // "Something went wrong". That is most of the order workflow:
            // accept, start-printing, mark-printed, mark-ready and collect are
            // all transitions with nothing to send.
            //
            // Dropping the content does not cost the Content-Length: HttpClient
            // still sends "Content-Length: 0" for a body-bearing method, so the
            // backend gets the framing it needs without the type it does not.
            // ApiProxyTests reads the bytes off a socket to keep it that way.
            if (body is { Length: > 0 })
            {
                upstream.Content = new ByteArrayContent(body);
            }

            foreach (var name in request.Headers.AllKeys)
            {
                if (name is null) continue;
                // Origin and Host belong to the local server, not the backend;
                // forwarding Origin is exactly what would re-introduce the CORS
                // rejection this proxy exists to avoid.
                if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = request.Headers[name];
                if (value is null) continue;
                if (!upstream.Headers.TryAddWithoutValidation(name, value))
                {
                    upstream.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await _upstream
                .SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status >= 400 && !request.Url.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
            {
                // A refusal is not an exception, so it used to pass through
                // without a trace - and a shop reporting "the button does
                // nothing" left nothing behind to look at. Header NAMES only,
                // never the values: whether Authorization survived the hop is
                // the whole question; what it contains is not something to write
                // to a log file on a shop counter.
                _log.LogWarning(
                    "api_proxy_refused method={Method} path={Path} status={Status} from_page=[{Headers}]",
                    request.HttpMethod, request.Url.AbsolutePath, status,
                    string.Join(",", request.Headers.AllKeys.Where(k => k is not null).OrderBy(k => k, StringComparer.Ordinal)));
            }

            context.Response.StatusCode = status;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (var value in header.Value) context.Response.Headers.Add(header.Key, value);
            }

            // Length unknown - required for a stream whose length genuinely is.
            context.Response.SendChunked = true;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var chunk = new byte[8 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0) break;
                await context.Response.OutputStream.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
                // Flushed per chunk, or an SSE event sits in the buffer until
                // enough bytes accumulate to justify a write.
                await context.Response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception exc)
        {
            // One retry for a read that could not find the server at all. This
            // is the shop's wifi blinking, and it surfaced as an error naming a
            // host and explaining nothing, for a connection that was working
            // again by the time anyone read it.
            //
            // Restricted to reads: re-sending a POST that did arrive would print
            // an order twice, or take a payment twice.
            var dns = exc is HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.HostNotFound } };
            if (dns && IsSafeToRetry(request.HttpMethod) && !retried)
            {
                _log.LogInformation("api_proxy_retry_after_dns_failure path={Path}", request.Url?.AbsolutePath);
                await ProxyToBackendAsync(context, ct, retried: true).ConfigureAwait(false);
                return;
            }

            var streaming = request.Url?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true;
            if (streaming) _log.LogDebug(exc, "api_proxy_stream_closed");
            else _log.LogWarning(exc, "api_proxy_failed path={Path}", request.Url?.AbsolutePath);

            // With a reason in it. The dashboard shows the server's own sentence
            // when there is one, and a bodyless 502 leaves it nothing to show
            // but a shrug.
            try
            {
                var message = dns
                    ? "This computer is not connected to the internet, or cannot look up the Printly server. Check the connection and try again."
                    : "The desktop app could not reach the Printly server: " + exc.Message;
                var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                {
                    error = new { code = "AGENT_PROXY_FAILED", message },
                });
                context.Response.StatusCode = 502;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = payload.Length;
                context.Response.OutputStream.Write(payload, 0, payload.Length);
            }
            catch (Exception) { /* the page has already gone */ }
        }
        finally
        {
            try { context.Response.Close(); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
        ((IDisposable)_listener).Dispose();
        _shutdown.Dispose();
        _upstream.Dispose();
    }
}
