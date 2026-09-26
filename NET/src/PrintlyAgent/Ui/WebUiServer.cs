using System.Net;
using System.Net.Sockets;
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
            catch (Exception exc)
            {
                // Only a stop is a reason to leave. This used to return on any
                // exception at all, which meant a single aborted connection or
                // http.sys hiccup silently ended the accept loop for the life
                // of the process: the window kept its dead page, every call
                // hung, nothing was logged, and the only cure was restarting
                // the app - on the machine specifically meant to sit unattended
                // all day.
                if (_shutdown.IsCancellationRequested || !_listener.IsListening) return;

                _log.LogWarning(exc, "webui_accept_failed");
                // A listener failing instantly and repeatedly must not become a
                // spin; a single aborted connection costs nothing here.
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            // Deliberately not awaited: one request must never hold up the next,
            // and an SSE stream holds its own for as long as the page is open.
            //
            // The token is not passed as Task.Run's creation token on purpose:
            // that cancels the work before it starts once shutdown begins, and
            // the context is then never closed, leaving that client hanging on
            // a request nobody will ever answer.
            _ = Task.Run(() => HandleAsync(context, ct));
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

            if (path.StartsWith(LocalFilesPrefix, StringComparison.Ordinal))
            {
                ServeHeldFile(context, path);
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

    // --- held files ----------------------------------------------------------

    /// <summary>
    /// Where the Files screen asks for a preview of a document this machine is
    /// already holding: <c>/local/files/{orderUuid}/{itemId}</c>.
    /// </summary>
    internal const string LocalFilesPrefix = "/local/files/";

    /// <summary>
    /// Resolves a held file for the page. Set by the host once the agent exists;
    /// null in tests and before pairing, which serves a 404 - the honest answer
    /// when this machine is holding nothing for anyone.
    /// </summary>
    public Func<string, string, string?>? HeldFileResolver { get; set; }

    /// <summary>
    /// Serves one held document off this disk.
    ///
    /// <para>
    /// The path is <em>not</em> turned into a filename. The two segments are
    /// handed to the agent, which looks them up in its own table and returns the
    /// path it recorded when it fetched the file - so what is served is always
    /// something this shop is genuinely holding, and never something the page
    /// steered a path towards. A page inside this webview is not a trusted
    /// caller: it is the one place a bad document could get script running.
    /// </para>
    ///
    /// <para>
    /// Inline rather than an attachment, because the whole point is that the
    /// owner can see what they are about to print without downloading it first.
    /// </para>
    /// </summary>
    private void ServeHeldFile(HttpListenerContext context, string path)
    {
        var segments = path[LocalFilesPrefix.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        string? resolved = null;
        if (segments.Length >= 2 && HeldFileResolver is { } resolve)
        {
            resolved = resolve(Uri.UnescapeDataString(segments[0]), Uri.UnescapeDataString(segments[1]));
        }

        if (resolved is null || !File.Exists(resolved))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        // .../pages and .../page/{n}: the file as sheet images, so the
        // dashboard shows each page as the paper it will print on - no PDF
        // viewer, no toolbar, nothing to zoom.
        if (segments.Length == 3 && segments[2] == "pages")
        {
            ServePageList(context, resolved);
            return;
        }
        if (segments.Length == 4 && segments[2] == "page" && int.TryParse(segments[3], out var pageNumber))
        {
            ServePageImage(context, resolved, pageNumber, context.Request.QueryString["w"]);
            return;
        }
        if (segments.Length != 2)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        try
        {
            using var file = File.OpenRead(resolved);
            context.Response.ContentType = "application/pdf";
            // Never cached. These are somebody's documents, they are deleted the
            // moment the order prints, and a stale one shown against the next
            // student's name is the worst outcome this screen has.
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Content-Disposition"] = "inline";
            context.Response.ContentLength64 = file.Length;
            file.CopyTo(context.Response.OutputStream);
        }
        catch (IOException exc)
        {
            // Being deleted underneath us by a print that just finished.
            _log.LogDebug(exc, "held_file_read_failed path={Path}", path);
            try { context.Response.StatusCode = 404; } catch (Exception) { }
        }
        finally
        {
            try { context.Response.Close(); } catch (Exception) { }
        }
    }

    /// <summary>{"pages":[{"width":pt,"height":pt}]} - each page's size, for its sheet's shape.</summary>
    private void ServePageList(HttpListenerContext context, string pdfPath)
    {
        try
        {
            // 72 dpi renders a page at its size in points, which is all this needs.
            using var renderer = new Printing.PdfPageRenderer(pdfPath, 72);
            var pages = new List<object>();
            for (var i = 0; i < renderer.PageCount; i++)
            {
                var page = renderer.RenderPage(i);
                pages.Add(new { width = page.Width, height = page.Height });
            }
            WriteJson(context, new { pages });
        }
        catch (Exception exc)
        {
            _log.LogDebug(exc, "held_file_pages_failed");
            try { context.Response.StatusCode = 422; context.Response.Close(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// One page as a PNG, rendered for a sheet <paramref name="widthParam"/>
    /// pixels wide (the dashboard asks for its on-screen size), 150 dpi if not
    /// given, never above 200 dpi.
    /// </summary>
    private void ServePageImage(HttpListenerContext context, string pdfPath, int pageNumber, string? widthParam)
    {
        try
        {
            using var probe = new Printing.PdfPageRenderer(pdfPath, 72);
            if (pageNumber < 1 || pageNumber > probe.PageCount)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }
            var widthPt = probe.RenderPage(pageNumber - 1).Width;
            var dpi = 150;
            if (int.TryParse(widthParam, out var wanted) && wanted > 0 && widthPt > 0)
            {
                dpi = Math.Clamp((int)Math.Ceiling(wanted * 72.0 / widthPt), 36, 200);
            }

            using var renderer = new Printing.PdfPageRenderer(pdfPath, dpi);
            var bitmap = renderer.RenderPage(pageNumber - 1);
            using var buffer = new MemoryStream();
            bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
            context.Response.ContentType = "image/png";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength64 = buffer.Length;
            buffer.Position = 0;
            buffer.CopyTo(context.Response.OutputStream);
        }
        catch (Exception exc)
        {
            _log.LogDebug(exc, "held_file_page_render_failed");
            try { context.Response.StatusCode = 422; } catch (Exception) { }
        }
        finally
        {
            try { context.Response.Close(); } catch (Exception) { }
        }
    }

    private static void WriteJson(HttpListenerContext context, object value)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
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
        _ when path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) => "application/javascript; charset=utf-8",
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
        var resolved = path is "/" or "" ? "/dashboard/index.html"
            : path is "/agent" or "/agent/" ? "/webui/index.html"
            : null;

        var bytes = ReadContent(resolved)
            ?? ReadContent("/dashboard" + path)
            ?? ReadContent("/webui" + path)
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
