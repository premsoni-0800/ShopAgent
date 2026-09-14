using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using PrintlyAgent.Credentials;

namespace PrintlyAgent.Net;

/// <summary>
/// Live order-change notifications for the owner-facing Orders view - port of
/// net/OrderEventsClient.kt, itself a direct port of the Python agent's
/// <c>order_events_client.py</c>. Reuses the shop dashboard's own
/// <c>GET /api/v1/shop/{shopId}/orders/events</c> stream; every event,
/// regardless of payload, just means "go refetch the orders list."
///
/// <para>
/// Porting notes. The OkHttp EventSource listener becomes a read loop over
/// <see cref="SseFrames"/> (defined next to <see cref="PrintJobSseClient"/> and
/// shared with it), opened on <see cref="PrintlyApiClient.SseHttp"/> because the
/// ordinary client's 30s call ceiling would kill a healthy idle stream between
/// the backend's 15s pings. Kotlin had to reach for <c>runBlocking</c> to
/// refresh a token from inside the listener callback, since the callback was not
/// a suspend function; here the loop is already async, so the refresh is a plain
/// await.
/// </para>
/// </summary>
public sealed class OrderEventsClient
{
    private readonly ILogger _log;
    private readonly PrintlyApiClient _api;
    private readonly Func<OwnerSession?> _sessionProvider;
    private readonly Action _onOrdersChanged;
    private readonly Func<CancellationToken, Task> _refreshSession;
    private readonly double _baseDelaySeconds;
    private readonly double _maxDelaySeconds;

    private volatile bool _stopped;

    /// <summary>
    /// Cancelled by <see cref="Stop"/> so a stop lands promptly rather than at
    /// the next thing the backend says, and replaced by <see cref="Resume"/> so
    /// signing back in is not blocked by the token that ended the last run. See
    /// the same field on <see cref="PrintJobSseClient"/>.
    /// </summary>
    private volatile CancellationTokenSource _stopSignal = new();

    public OrderEventsClient(
        ILogger log,
        PrintlyApiClient api,
        Func<OwnerSession?> sessionProvider,
        Action onOrdersChanged,
        Func<CancellationToken, Task> refreshSession,
        double baseDelaySeconds = 1.0,
        double maxDelaySeconds = 60.0)
    {
        _log = log;
        _api = api;
        _sessionProvider = sessionProvider;
        _onOrdersChanged = onOrdersChanged;
        _refreshSession = refreshSession;
        _baseDelaySeconds = baseDelaySeconds;
        _maxDelaySeconds = maxDelaySeconds;
    }

    public void Stop()
    {
        _stopped = true;
        try
        {
            _stopSignal.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped once. Nothing left to interrupt.
        }
    }

    /// <summary>
    /// Lets the stream run again after it gave up on a signed-out session.
    ///
    /// <see cref="Stop"/> is called when the refresh token is gone, which is
    /// correct while nobody is signed in - but it used to be a one-way latch, so
    /// signing back in left the Orders view silently frozen for the rest of the
    /// process. The agent went on reporting itself connected, because the
    /// heartbeat is a different loop on a different credential, and the only
    /// symptom was a list that never changed.
    ///
    /// The stop token is replaced rather than reset, because a cancelled
    /// CancellationTokenSource stays cancelled - leaving the old one in place
    /// would rebuild the very latch this exists to undo.
    /// </summary>
    public void Resume()
    {
        _stopSignal = new CancellationTokenSource();
        _stopped = false;
    }

    public async Task RunForeverAsync(CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopSignal.Token);
        await ReconnectLoop.RunAsync(
            _log, _baseDelaySeconds, _maxDelaySeconds, () => _stopped, ConnectOnceAsync, linked.Token)
            .ConfigureAwait(false);
    }

    private async Task ConnectOnceAsync(CancellationToken cancellation)
    {
        var session = _sessionProvider();
        if (session is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_baseDelaySeconds * 1000), cancellation).ConfigureAwait(false);
            return;
        }

        await StreamOnceAsync(session, cancellation).ConfigureAwait(false);
    }

    private async Task StreamOnceAsync(OwnerSession session, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, _api.BaseUrl.TrimEnd('/') + $"/api/v1/shop/{session.ShopId}/orders/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // A connect-level failure - DNS, a refused socket, a reset - throws
        // straight out of here, which is the BACK_OFF arm of OnFailureAsync
        // reached without needing to ask: there is no status to inspect and no
        // token to refresh, so there is nothing for the decision to do.
        using var response = await _api.SseHttp
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            await OnFailureAsync((int)response.StatusCode, null, cancellation).ConfigureAwait(false);
            return;
        }

        _log.LogInformation("order_events_connected");

        await using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Every event, whatever it carries, means the same thing.
            await foreach (var _ in SseFrames.ReadAsync(body, cancellation).ConfigureAwait(false))
            {
                _onOrdersChanged();
            }
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            // Dropped mid-stream. There is no HTTP status at this point, so no
            // refresh is possible - but the decision still has to be taken
            // through the same door, or a drop while signed out would be logged
            // and backed off from on the way to a shutdown.
            await OnFailureAsync(null, exc, cancellation).ConfigureAwait(false);
        }

        // Falling out cleanly is the server closing the stream, which returns
        // normally and resets the ladder.
    }

    /// <summary>
    /// What to do about a stream that did not survive - refresh, reconnect, back
    /// off or stop.
    /// </summary>
    private async Task OnFailureAsync(int? statusCode, Exception? cause, CancellationToken cancellation)
    {
        var refreshed = false;

        // The access token used to open this connection aged out mid-stream (a
        // live SSE connection easily outlives a short-lived JWT) - refresh once,
        // then let the backoff loop reconnect with it, same principle as a
        // normal request retry.
        if (statusCode is 401 or 403)
        {
            try
            {
                await _refreshSession(cancellation).ConfigureAwait(false);
                refreshed = true;
            }
            catch (ApiError exc)
            {
                // A refresh token the server has thrown away is not going to
                // start working. Retrying it reconnected every couple of seconds
                // for as long as the agent ran - thirty failures a minute,
                // forever, drowning the log and hiding anything real. The owner
                // has to sign in again, and nothing here can do that for them,
                // so stop asking.
                if (exc.Code == "TOKEN_REVOKED" || exc.Code == "TOKEN_INVALID" || exc.StatusCode == 401)
                {
                    _log.LogWarning("order_events_stopped_signed_out code={Code}", exc.Code);
                    Stop();
                }
                else
                {
                    _log.LogWarning(exc, "order_events_session_refresh_failed");
                }
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                // Anything else - a network blip mid-refresh - is worth
                // retrying, so the loop is left alone.
                _log.LogWarning(exc, "order_events_session_refresh_failed");
            }
        }

        switch (StreamFailures.ActionFor(refreshed, _stopped))
        {
            // A new access token in hand: reconnecting at once is the point of
            // having refreshed, so this is reported as the clean disconnect it
            // effectively is and the ladder resets.
            case StreamFailure.ReconnectNow:
                return;
            // Nobody is signed in. RunForeverAsync is about to return anyway;
            // returning cleanly keeps a shutdown quiet.
            case StreamFailure.GiveUp:
                return;
            // Everything else is a failure and has to look like one, or the
            // backoff never engages and nothing is ever logged.
            case StreamFailure.BackOff:
            default:
                var status = statusCode?.ToString() ?? "?";
                throw cause ?? new IOException($"order events stream rejected: HTTP {status}");
        }
    }
}

/// <summary>What a dropped stream should lead to.</summary>
internal enum StreamFailure
{
    ReconnectNow,
    BackOff,
    GiveUp,
}

internal static class StreamFailures
{
    /// <summary>
    /// Whether a dropped stream is worth backing off from.
    ///
    /// The distinction is the whole of why this exists. The failure path used to
    /// end in an unconditional "complete normally", so the stream call returned
    /// *normally* from every failure - and <see cref="ReconnectLoop"/> reads a
    /// normal return as "the server closed cleanly" and resets its ladder. The
    /// delay was therefore pinned at the base value for ever: a ten-minute
    /// backend deploy became roughly eight hundred connection attempts against a
    /// dead host instead of a dozen, from every agent at once, arriving exactly
    /// as the backend came back up. And because nothing was thrown,
    /// <see cref="ReconnectLoop"/>'s own <c>connection_failed</c> warning never
    /// fired, so the shop's log for that window was empty.
    ///
    /// Only two things are not failures: a token that was just refreshed, where
    /// reconnecting immediately is the entire point, and a session that is gone,
    /// where the loop is about to stop anyway.
    /// </summary>
    public static StreamFailure ActionFor(bool refreshed, bool stopped) =>
        stopped ? StreamFailure.GiveUp
        : refreshed ? StreamFailure.ReconnectNow
        : StreamFailure.BackOff;
}
