using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Credentials;
using PrintlyAgent.Net;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// The two SSE clients, the backoff they share, and the timeouts a long-lived
/// stream needs.
///
/// Port of SseClientTimeoutsTest.kt and StreamFailureTest.kt (which holds both
/// StreamFailureTest and ReconnectLoopTest), plus the stream cases the Kotlin
/// suite never needed. OkHttp shipped an EventSource; .NET does not, so the
/// framing is hand-written in SseFrames and the parts the library used to be
/// trusted with - the blank line that dispatches, the comment line that is the
/// backend's ping, a payload that will not parse - are now this port's own
/// behaviour and are tested as such. All of it runs against a local HttpListener
/// rather than the network.
/// </summary>
public class SseClientTests
{
    /// <summary>
    /// `OrderEventBroadcaster.HEARTBEAT_INTERVAL_MS` /
    /// `PrintAgentEventBroadcaster.HEARTBEAT_INTERVAL_MS`.
    /// </summary>
    private const int BackendPingIntervalMs = 15_000;

    // --- timeouts ------------------------------------------------------------
    //
    // Guards the timeouts a long-lived stream needs, because getting them wrong
    // fails silently: the agent reconnects, logs a routine `connection_failed`,
    // and looks healthy while dropping every event that lands in the gap.
    //
    // This is not hypothetical. Both SSE clients originally opened their streams
    // with PrintlyApiClient.Http, whose request-shaped timeout made a stable
    // connection impossible - see PrintlyApiClient.SseHttp for the full
    // reasoning. These assertions are the cheap way to keep that from creeping
    // back in the next time someone reaches for the obvious client.

    [Fact(DisplayName = "sse client never bounds the whole call")]
    public void SseClientNeverBoundsTheWholeCall()
    {
        using var api = new PrintlyApiClient("https://example.invalid");

        // Kotlin asserted OkHttp's callTimeoutMillis == 0. HttpClient.Timeout is
        // the same knob under another name and with another "off" value: it
        // bounds the whole operation, response body included, so anything finite
        // here caps how long a stream may stay open.
        Assert.Equal(Timeout.InfiniteTimeSpan, api.SseHttp.Timeout);
    }

    /// <summary>
    /// The backend pings every 15s. Anything at or under that guarantees a
    /// teardown in the first quiet gap; the margin has to cover a ping being
    /// genuinely late, not just present.
    /// </summary>
    [Fact(DisplayName = "sse read timeout leaves room for the backend's 15s ping")]
    public void SseReadTimeoutLeavesRoomForTheBackendsPing()
    {
        using var api = new PrintlyApiClient("https://example.invalid");

        // SocketsHttpHandler has no per-read timeout to assert on the way OkHttp
        // did, so the equivalent guard is the deadline that does exist: no
        // deadline at all, or one that outlasts more than one backend ping.
        // Someone reaching for the ordinary 30s ceiling fails here, which is the
        // regression this was written for.
        var deadline = api.SseHttp.Timeout;
        Assert.True(
            deadline == Timeout.InfiniteTimeSpan ||
            deadline >= TimeSpan.FromMilliseconds(2 * BackendPingIntervalMs),
            $"SseHttp.Timeout={deadline} must outlast more than one 15s backend ping");
    }

    [Fact(DisplayName = "sse client pings underneath so a half-open socket is still noticed")]
    public void SseClientPingsUnderneath()
    {
        using var api = new PrintlyApiClient("https://example.invalid");
        var handler = HandlerOf(api.SseHttp);

        // With no read deadline above it, the keep-alive ping is the only thing
        // that notices a socket the far end has silently dropped. Unset, the
        // agent would sit on a dead connection indefinitely and report itself
        // connected the whole time.
        Assert.True(
            handler.KeepAlivePingDelay > TimeSpan.Zero && handler.KeepAlivePingDelay != Timeout.InfiniteTimeSpan,
            $"KeepAlivePingDelay={handler.KeepAlivePingDelay} must be set and finite");
        Assert.True(
            handler.KeepAlivePingTimeout > TimeSpan.Zero && handler.KeepAlivePingTimeout != Timeout.InfiniteTimeSpan,
            $"KeepAlivePingTimeout={handler.KeepAlivePingTimeout} must be set and finite");
    }

    /// <summary>The request client keeps its bound - an ordinary call must not hang forever.</summary>
    [Fact(DisplayName = "request client still bounds the whole call")]
    public void RequestClientStillBoundsTheWholeCall()
    {
        using var api = new PrintlyApiClient("https://example.invalid");
        Assert.True(api.Http.Timeout > TimeSpan.Zero);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, api.Http.Timeout);
    }

    /// <summary>
    /// SocketsHttpHandler's settings are not readable back through HttpClient,
    /// so this digs out the handler the way the runtime stores it. Ugly, and
    /// worth it: the alternative is not checking the one setting that notices a
    /// half-open socket, and that setting is exactly the sort of thing a later
    /// edit drops without anyone seeing a symptom for weeks.
    /// </summary>
    private static SocketsHttpHandler HandlerOf(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var handler = field!.GetValue(client);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler;

        var sockets = handler as SocketsHttpHandler;
        Assert.NotNull(sockets);
        return sockets!;
    }

    // --- what a dropped stream leads to --------------------------------------
    //
    // ReconnectLoop decides how long to wait by watching how connectOnce
    // returns: normally means the server closed cleanly and the ladder resets,
    // throwing means something went wrong and the delay grows. The order-events
    // client used to return normally from *every* failure, which pinned the
    // delay at its base value for ever and silenced the loop's own logging with
    // it.

    [Fact(DisplayName = "an ordinary failure backs off")]
    public void AnOrdinaryFailureBacksOff()
    {
        Assert.Equal(StreamFailure.BackOff, StreamFailures.ActionFor(refreshed: false, stopped: false));
    }

    /// <summary>
    /// A token was just refreshed, so there is a new one to reconnect with and
    /// no reason to wait - that is the entire point of having refreshed.
    /// </summary>
    [Fact(DisplayName = "a refreshed token reconnects at once")]
    public void ARefreshedTokenReconnectsAtOnce()
    {
        Assert.Equal(StreamFailure.ReconnectNow, StreamFailures.ActionFor(refreshed: true, stopped: false));
    }

    /// <summary>Nobody is signed in; the loop is about to end, and should end quietly.</summary>
    [Fact(DisplayName = "a signed-out session gives up")]
    public void ASignedOutSessionGivesUp()
    {
        Assert.Equal(StreamFailure.GiveUp, StreamFailures.ActionFor(refreshed: false, stopped: true));
        Assert.Equal(
            StreamFailure.GiveUp,
            StreamFailures.ActionFor(refreshed: true, stopped: true));
        // A refresh that ended in a revoked token is still a session that is gone.
    }

    // --- the backoff itself --------------------------------------------------
    //
    // The bug above was only harmful because of what this does with a normal
    // return, so the contract is worth pinning: a throw grows the wait, a clean
    // return resets it.

    /// <summary>
    /// Measured against each other rather than against a stopwatch: the delays
    /// are jittered on purpose, and a first-run process spends longer starting
    /// tasks than nine base delays take. What is not in doubt is the gap - a
    /// climbing ladder is orders of magnitude slower than a flat one.
    /// </summary>
    [Fact(DisplayName = "failures back off, clean disconnects do not")]
    public async Task FailuresBackOffCleanDisconnectsDoNot()
    {
        var cleanMs = await NineRoundsAsync(() => Task.Delay(1));
        var failingMs = await NineRoundsAsync(() => Task.FromException(new IOException("stream died")));

        Assert.True(
            failingMs > cleanMs * 3,
            $"nine failures should cost far more than nine clean reconnects (failing={failingMs}ms clean={cleanMs}ms)");
    }

    /// <summary>Nine rounds of <paramref name="body"/>, bounded so a climbing ladder cannot hang the suite.</summary>
    private static async Task<long> NineRoundsAsync(Func<Task> body)
    {
        var attempts = 0;
        var elapsed = Stopwatch.StartNew();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await ReconnectLoop.RunAsync(
            NullLogger.Instance,
            baseDelaySeconds: 0.01,
            maxDelaySeconds: 60.0,
            isStopped: () => Volatile.Read(ref attempts) >= 9,
            connectOnce: _ =>
            {
                Interlocked.Increment(ref attempts);
                return body();
            },
            cancellation: cancel.Token);

        return elapsed.ElapsedMilliseconds;
    }

    [Fact(DisplayName = "stopping ends the loop")]
    public async Task StoppingEndsTheLoop()
    {
        var attempts = 0;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await ReconnectLoop.RunAsync(
            NullLogger.Instance,
            baseDelaySeconds: 0.01,
            maxDelaySeconds: 60.0,
            isStopped: () => Volatile.Read(ref attempts) >= 1,
            connectOnce: _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.CompletedTask;
            },
            cancellation: cancel.Token);

        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    // --- the stream itself ---------------------------------------------------

    [Fact(DisplayName = "an sse frame is dispatched on the blank line that ends it")]
    public async Task AnSseFrameIsDispatchedOnTheBlankLineThatEndsIt()
    {
        var hold = Gate();
        var received = new ConcurrentQueue<string>();

        using var server = new StubServer(async (context, _) =>
        {
            if (IsJobsList(context)) { await SendJsonAsync(context, "[]"); return; }
            await SendSseAsync(context, Frame(
                "event: PRINT_JOB_AVAILABLE",
                """data: {"jobId":"j1","orderId":"o1","shopId":"shop-1"}"""));
            await hold.Task;
        });

        using var api = new PrintlyApiClient(server.BaseUrl);
        var client = JobClient(api, NullLogger.Instance, received);
        using var cancel = new CancellationTokenSource();
        var run = client.RunForeverAsync(cancel.Token);

        await UntilAsync(() => !received.IsEmpty);
        Assert.Equal("j1/o1/-", received.First());
        // The event carries no order code; only the reconcile list does.

        hold.TrySetResult();
        await StopAndAwaitAsync(client.Stop, cancel, run);
    }

    /// <summary>
    /// The backend's keep-alive is a comment line, and it arrives every 15s for
    /// as long as the stream is open. Dispatched as an event it would fire the
    /// job handler four times a minute on an idle shop, with an unparseable
    /// payload every time.
    /// </summary>
    [Fact(DisplayName = "a keep-alive comment is not an event")]
    public async Task AKeepAliveCommentIsNotAnEvent()
    {
        var hold = Gate();
        var received = new ConcurrentQueue<string>();

        using var server = new StubServer(async (context, _) =>
        {
            if (IsJobsList(context)) { await SendJsonAsync(context, "[]"); return; }
            await SendSseAsync(context,
                ": keep-alive\n\n" +
                ": keep-alive\n\n" +
                Frame("""data: {"jobId":"j1","orderId":"o1"}"""));
            await hold.Task;
        });

        using var api = new PrintlyApiClient(server.BaseUrl);
        var client = JobClient(api, NullLogger.Instance, received);
        using var cancel = new CancellationTokenSource();
        var run = client.RunForeverAsync(cancel.Token);

        await UntilAsync(() => !received.IsEmpty);
        await Task.Delay(100); // the connection is held open, so nothing else can arrive
        Assert.Single(received);

        hold.TrySetResult();
        await StopAndAwaitAsync(client.Stop, cancel, run);
    }

    /// <summary>
    /// One unreadable frame must not end the subscription. Dropping the
    /// connection over it would cost every event until the reconnect lands, and
    /// the backend is the only thing that decides what turns up next.
    /// </summary>
    [Fact(DisplayName = "a malformed event does not kill the stream")]
    public async Task AMalformedEventDoesNotKillTheStream()
    {
        var hold = Gate();
        var received = new ConcurrentQueue<string>();

        using var server = new StubServer(async (context, _) =>
        {
            if (IsJobsList(context)) { await SendJsonAsync(context, "[]"); return; }
            await SendSseAsync(context,
                Frame("data: not json at all") +
                Frame("""data: {"orderId":"o2"}""") +      // no jobId: skipped, not fatal
                Frame("""data: {"jobId":"j3","orderId":"o3"}"""));
            await hold.Task;
        });

        using var api = new PrintlyApiClient(server.BaseUrl);
        var client = JobClient(api, NullLogger.Instance, received);
        using var cancel = new CancellationTokenSource();
        var run = client.RunForeverAsync(cancel.Token);

        await UntilAsync(() => !received.IsEmpty);
        Assert.Equal("j3/o3/-", received.First());

        hold.TrySetResult();
        await StopAndAwaitAsync(client.Stop, cancel, run);
    }

    /// <summary>
    /// A missed push must never mean a missed job. Every connect lists what is
    /// outstanding *before* subscribing, so a job created while the agent was
    /// offline is picked up by the list call rather than depending on the stream
    /// having no gaps.
    /// </summary>
    [Fact(DisplayName = "outstanding jobs are listed before the stream is subscribed to")]
    public async Task OutstandingJobsAreListedBeforeTheStreamIsSubscribedTo()
    {
        var hold = Gate();
        var received = new ConcurrentQueue<string>();

        using var server = new StubServer(async (context, _) =>
        {
            if (IsJobsList(context))
            {
                await SendJsonAsync(context, """
                    [{"jobId":"j9","orderId":"o9","orderCode":"HH-000009","status":"CREATED",
                      "createdAt":"2026-01-01T00:00:00Z"}]
                    """);
                return;
            }
            // A comment and nothing else: the subscription is live, but no
            // event will ever come down it. Everything received here must
            // therefore have come from the list call above.
            await SendSseAsync(context, ": subscribed\n\n");
            await hold.Task;
        });

        using var api = new PrintlyApiClient(server.BaseUrl);
        var client = JobClient(api, NullLogger.Instance, received);
        using var cancel = new CancellationTokenSource();
        var run = client.RunForeverAsync(cancel.Token);

        await UntilAsync(() => server.Paths.Count >= 2);

        var paths = server.Paths.ToList();
        Assert.EndsWith("/api/v1/print-agent/jobs", paths[0], StringComparison.Ordinal);
        Assert.EndsWith("/api/v1/print-agent/events", paths[1], StringComparison.Ordinal);

        // The list call is where an order code comes from, which is what the
        // queue sorts on - the push carries ids only.
        Assert.Equal("j9/o9/HH-000009", received.First());
        Assert.Equal("Bearer agent-1.s3cret", server.AuthHeaders.First());

        hold.TrySetResult();
        await StopAndAwaitAsync(client.Stop, cancel, run);
    }

    /// <summary>
    /// A per-route 503 or 429 on /events used to arrive as a clean close, which
    /// reset the ladder: a silent ~1.3/s connect-reject loop, with jobs still
    /// printing off the ten-second reconcile poll so the only symptom was every
    /// order arriving late. A rejected subscription has to throw, because a
    /// throw is the only thing the backoff and the log ever see.
    /// </summary>
    [Fact(DisplayName = "a rejected subscription is a failure, not a clean close")]
    public async Task ARejectedSubscriptionIsAFailureNotACleanClose()
    {
        using var server = new StubServer(async (context, _) =>
        {
            if (IsJobsList(context)) { await SendJsonAsync(context, "[]"); return; }
            await SendJsonAsync(
                context,
                """{"error":{"code":"RATE_LIMITED","message":"slow down"}}""",
                HttpStatusCode.ServiceUnavailable);
        });

        var log = new RecordingLogger();
        using var api = new PrintlyApiClient(server.BaseUrl);
        var client = JobClient(api, log, new ConcurrentQueue<string>());
        using var cancel = new CancellationTokenSource();
        var run = client.RunForeverAsync(cancel.Token);

        await UntilAsync(() => log.Saw("connection_failed"));

        await StopAndAwaitAsync(client.Stop, cancel, run);
    }

    /// <summary>Every event, regardless of payload, just means "go refetch the orders list."</summary>
    [Fact(DisplayName = "every order event just means refetch")]
    public async Task EveryOrderEventJustMeansRefetch()
    {
        var hold = Gate();
        var refetches = 0;

        using var server = new StubServer(async (context, _) =>
        {
            await SendSseAsync(context,
                Frame("data: {}") +
                Frame("event: ORDER_UPDATED", "data: anything at all"));
            await hold.Task;
        });

        using var api = new PrintlyApiClient(server.BaseUrl);
        var client = new OrderEventsClient(
            NullLogger.Instance, api, Owner,
            onOrdersChanged: () => Interlocked.Increment(ref refetches),
            refreshSession: _ => Task.CompletedTask,
            baseDelaySeconds: 0.01);
        using var cancel = new CancellationTokenSource();
        var run = client.RunForeverAsync(cancel.Token);

        await UntilAsync(() => Volatile.Read(ref refetches) >= 2);
        Assert.EndsWith("/api/v1/shop/shop-1/orders/events", server.Paths.First(), StringComparison.Ordinal);
        Assert.Equal("Bearer owner-jwt", server.AuthHeaders.First());

        hold.TrySetResult();
        await StopAndAwaitAsync(client.Stop, cancel, run);
    }

    /// <summary>
    /// The access token used to open a connection ages out mid-stream - a live
    /// SSE connection easily outlives a short-lived JWT. Refresh once, then
    /// reconnect with it; and because there is a new token in hand, that
    /// reconnect is reported as the clean disconnect it effectively is, so the
    /// ladder resets and nothing is logged as a failure.
    /// </summary>
    [Fact(DisplayName = "a stale access token is refreshed once and the stream reconnects")]
    public async Task AStaleAccessTokenIsRefreshedOnceAndTheStreamReconnects()
    {
        var hold = Gate();
        var refetches = 0;
        var refreshes = 0;

        using var server = new StubServer(async (context, n) =>
        {
            if (n == 0)
            {
                await SendJsonAsync(
                    context,
                    """{"error":{"code":"TOKEN_EXPIRED","message":"expired"}}""",
                    HttpStatusCode.Unauthorized);
                return;
            }
            await SendSseAsync(context, Frame("data: {}"));
            await hold.Task;
        });

        var log = new RecordingLogger();
        using var api = new PrintlyApiClient(server.BaseUrl);
        var client = new OrderEventsClient(
            log, api, Owner,
            onOrdersChanged: () => Interlocked.Increment(ref refetches),
            refreshSession: _ => { Interlocked.Increment(ref refreshes); return Task.CompletedTask; },
            baseDelaySeconds: 0.01);
        using var cancel = new CancellationTokenSource();
        var run = client.RunForeverAsync(cancel.Token);

        await UntilAsync(() => Volatile.Read(ref refetches) >= 1);

        Assert.Equal(1, Volatile.Read(ref refreshes));
        Assert.False(log.Saw("connection_failed"), "a refreshed token reconnects at once, it does not back off");

        hold.TrySetResult();
        await StopAndAwaitAsync(client.Stop, cancel, run);
    }

    /// <summary>
    /// A refresh token the server has thrown away is not going to start working.
    /// Retrying it reconnected every couple of seconds for as long as the agent
    /// ran - thirty failures a minute, forever, drowning the log and hiding
    /// anything real.
    /// </summary>
    [Fact(DisplayName = "a revoked refresh token stops the loop instead of retrying for ever")]
    public async Task ARevokedRefreshTokenStopsTheLoop()
    {
        using var server = new StubServer(async (context, _) =>
            await SendJsonAsync(
                context,
                """{"error":{"code":"TOKEN_REVOKED","message":"gone"}}""",
                HttpStatusCode.Unauthorized));

        var log = new RecordingLogger();
        using var api = new PrintlyApiClient(server.BaseUrl);
        var client = new OrderEventsClient(
            log, api, Owner,
            onOrdersChanged: () => { },
            refreshSession: _ => throw new ApiError(401, "TOKEN_REVOKED", "gone"),
            baseDelaySeconds: 0.01);

        // No Stop() from the test: the loop has to end on its own, and the
        // timeout is only here so a regression reports a failure rather than
        // hanging the suite.
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.RunForeverAsync(cancel.Token);

        Assert.False(cancel.IsCancellationRequested, "the loop should have ended by itself");
        Assert.True(log.Saw("order_events_stopped_signed_out"));
    }

    /// <summary>
    /// Stopping on a signed-out session used to be a one-way latch, so signing
    /// back in left the Orders view silently frozen for the rest of the process.
    /// The agent went on reporting itself connected, because the heartbeat is a
    /// different loop on a different credential, and the only symptom was a list
    /// that never changed.
    /// </summary>
    [Fact(DisplayName = "a signed-out stream can be resumed when the owner signs back in")]
    public async Task ASignedOutStreamCanBeResumed()
    {
        var hold = Gate();
        var refetches = 0;

        using var server = new StubServer(async (context, _) =>
        {
            await SendSseAsync(context, Frame("data: {}"));
            await hold.Task;
        });

        using var api = new PrintlyApiClient(server.BaseUrl);
        var client = new OrderEventsClient(
            NullLogger.Instance, api, Owner,
            onOrdersChanged: () => Interlocked.Increment(ref refetches),
            refreshSession: _ => Task.CompletedTask,
            baseDelaySeconds: 0.01);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        client.Stop();
        await client.RunForeverAsync(cancel.Token);
        Assert.Empty(server.Paths);

        client.Resume();
        var run = client.RunForeverAsync(cancel.Token);
        await UntilAsync(() => Volatile.Read(ref refetches) >= 1);

        hold.TrySetResult();
        await StopAndAwaitAsync(client.Stop, cancel, run);
    }

    // --- helpers -------------------------------------------------------------

    private static AgentCredential Agent() => new("agent-1", "shop-1", "s3cret");

    private static OwnerSession Owner() => new("owner-jwt", "refresh", "shop-1", "A Shop");

    private static PrintJobSseClient JobClient(PrintlyApiClient api, ILogger log, ConcurrentQueue<string> received) =>
        new(log, api, Agent,
            (jobId, orderId, orderCode) => received.Enqueue($"{jobId}/{orderId}/{orderCode ?? "-"}"),
            baseDelaySeconds: 0.01);

    /// <summary>A TaskCompletionSource used as Kotlin's CompletableDeferred&lt;Unit&gt;.</summary>
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsJobsList(HttpListenerContext context) =>
        (context.Request.Url?.AbsolutePath ?? "").EndsWith("/print-agent/jobs", StringComparison.Ordinal);

    /// <summary>One SSE frame: the given lines, then the blank line that dispatches it.</summary>
    private static string Frame(params string[] lines) => string.Join("\n", lines) + "\n\n";

    /// <summary>
    /// Stops the client, cancels its token, and waits for the loop to return.
    ///
    /// Awaiting the task is half the assertion: a stop must end the loop
    /// promptly and must not throw out of it, so anything escaping here fails
    /// the test that called it.
    /// </summary>
    private static async Task StopAndAwaitAsync(Action stop, CancellationTokenSource cancel, Task run)
    {
        stop();
        cancel.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task UntilAsync(Func<bool> condition, int timeoutMillis = 10_000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.ElapsedMilliseconds > timeoutMillis) throw new TimeoutException("condition never became true");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Captures formatted log lines, because "was this reported as a failure?"
    /// is the only externally visible difference between a backoff and a clean
    /// reconnect - and it is precisely the distinction these clients get wrong
    /// when they get anything wrong.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue(formatter(state, exception));

        public bool Saw(string fragment) => _lines.Any(line => line.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// A throwaway HTTP server that can hold a response open, which an SSE test
    /// needs and the one in ApiClientTests does not: the point of most of these
    /// is what happens on a single connection, and a stub that closed after
    /// writing would have the client reconnecting mid-assertion.
    ///
    /// The handler is given the 0-based request number so a test can answer the
    /// first connection differently from the rest - that is how an expired token
    /// is staged.
    /// </summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<HttpListenerContext, int, Task> _handle;
        private int _served;

        public string BaseUrl { get; }
        public ConcurrentQueue<string> Paths { get; } = new();
        public ConcurrentQueue<string> AuthHeaders { get; } = new();

        public StubServer(Func<HttpListenerContext, int, Task> handle)
        {
            _handle = handle;
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                Paths.Enqueue(context.Request.Url?.AbsolutePath ?? "");
                AuthHeaders.Enqueue(context.Request.Headers["Authorization"] ?? "");
                var served = Interlocked.Increment(ref _served) - 1;

                // Each connection is handled on its own task. A held-open SSE
                // response would otherwise block the accept loop, and the next
                // request is often the thing a test is waiting for.
                _ = Task.Run(async () =>
                {
                    try { await _handle(context, served); }
                    catch { /* the client hung up, or the listener stopped */ }
                    finally { try { context.Response.Close(); } catch { /* already gone */ } }
                });
            }
        }

        private static int FreePort()
        {
            var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Stop();
            ((IDisposable)_listener).Dispose();
        }
    }

    /// <summary>
    /// Writes an SSE body and leaves the response open, so the caller decides
    /// when the stream ends.
    /// </summary>
    private static async Task SendSseAsync(HttpListenerContext context, string body)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        // Chunked, because a stream has no length to declare and a declared
        // length would make the client wait for bytes that never come.
        context.Response.SendChunked = true;
        if (body.Length > 0)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            await context.Response.OutputStream.WriteAsync(bytes);
        }
        await context.Response.OutputStream.FlushAsync();
    }

    private static async Task SendJsonAsync(
        HttpListenerContext context, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }
}
