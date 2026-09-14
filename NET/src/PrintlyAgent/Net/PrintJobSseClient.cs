using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrintlyAgent.Credentials;

namespace PrintlyAgent.Net;

/// <summary>
/// Deliberately not an async delegate. A handler here is called from the loop
/// that is reading the stream, and anything it waits for is time that
/// connection spends reading nothing - so the contract is that a handler queues
/// the work and returns, never that it does the work. See
/// <see cref="PrintlyAgent.Jobs.JobDispatcher"/>.
///
/// <para>
/// Port of Kotlin's <c>typealias JobReferenceHandler</c>. Kotlin said "not a
/// suspend function"; the same statement in C# is a <c>void</c> delegate rather
/// than one returning Task, and for the same reason - the type is what stops
/// somebody awaiting real work here.
/// </para>
/// </summary>
public delegate void JobReferenceHandler(string jobId, string orderId, string? orderCode);

/// <summary>One dispatched server-sent event: the fields the spec lets a frame carry.</summary>
internal readonly record struct SseFrame(string? Id, string? EventType, string Data);

/// <summary>
/// The server-sent-events wire format, parsed by hand.
///
/// OkHttp's <c>EventSource</c> did this on the Kotlin side and .NET ships no
/// equivalent, so the framing lives here and both SSE clients share it. Only the
/// part of the spec this backend uses is implemented - <c>event:</c>,
/// <c>data:</c>, <c>id:</c>, the blank line that dispatches, and comment lines -
/// but the two easy-to-miss rules are honoured, because getting either wrong
/// shows up as events that quietly never arrive:
///
///  - a line starting with <c>:</c> is a comment, and that is exactly what the
///    backend's 15s keep-alive ping is. Treating it as an event would fire a
///    refetch four times a minute for ever; treating a ping as data would hand
///    the job handler an unparseable payload on every heartbeat.
///  - one leading space after the colon belongs to the framing, not to the
///    value. <c>data: {"jobId":...}</c> carries <c>{"jobId":...}</c>, and a JSON
///    parser handed the extra space would be fine - but an <c>event:</c> name
///    compared with the space still attached would never match.
/// </summary>
internal static class SseFrames
{
    /// <summary>
    /// Frames off <paramref name="stream"/> until the server closes it, which
    /// ends the sequence normally - a clean disconnect, the thing
    /// <see cref="ReconnectLoop"/> reads as "reset the ladder".
    /// </summary>
    public static async IAsyncEnumerable<SseFrame> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        // leaveOpen: the caller owns the response body and disposes it.
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: -1, leaveOpen: true);

        string? id = null;
        string? eventType = null;
        var data = new StringBuilder();
        var hasData = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);

            // End of stream. The server closed cleanly; so do we.
            if (line is null) yield break;

            if (line.Length == 0)
            {
                // The blank line is the dispatch. A frame with no data line at
                // all is not an event - that is what a bare comment leaves
                // behind - so it resets the fields and nothing more.
                if (hasData)
                {
                    yield return new SseFrame(id, eventType, data.ToString());
                }
                id = null;
                eventType = null;
                data.Clear();
                hasData = false;
                continue;
            }

            // A comment, and the backend's keep-alive ping is one.
            if (line[0] == ':') continue;

            var colon = line.IndexOf(':');
            string field;
            string value;
            if (colon < 0)
            {
                // A bare field name with no colon is legal and carries an empty value.
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (value.StartsWith(' ')) value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventType = value;
                    break;
                case "data":
                    // Multiple data lines in one frame join with newlines.
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                    break;
                case "id":
                    id = value;
                    break;
                default:
                    // "retry", and anything the backend adds later. Ignored on
                    // purpose: an unknown field must never stop the stream.
                    break;
            }
        }
    }
}

/// <summary>
/// SSE client for <c>/api/v1/print-agent/events</c>, with reconnect and
/// reconciliation - port of net/PrintJobSseClient.kt, itself a direct port of
/// the Python agent's <c>sse_client.py</c>.
///
/// A missed push must never mean a missed job: every (re)connect, including the
/// very first, lists outstanding jobs *before* subscribing to the stream, so a
/// job created while the agent was offline is picked up by the list call rather
/// than depending on the stream having no gaps.
///
/// <para>
/// Porting notes. OkHttp's EventSource plus a CompletableDeferred becomes one
/// <see cref="HttpClient"/> request opened with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> and a read loop over
/// <see cref="SseFrames"/>: the loop returning is the old <c>onClosed</c>, and
/// the loop throwing is the old <c>onFailure</c>, which is the distinction
/// <see cref="ReconnectLoop"/> is built on. The request MUST go through
/// <see cref="PrintlyApiClient.SseHttp"/> - the ordinary client's 30s ceiling
/// bounds the whole call, body included, and would tear a healthy stream down
/// between the backend's 15s pings.
/// </para>
/// </summary>
public sealed class PrintJobSseClient
{
    private readonly ILogger _log;
    private readonly PrintlyApiClient _api;
    private readonly Func<AgentCredential?> _credentialProvider;
    private readonly JobReferenceHandler _onJobReference;
    private readonly double _baseDelaySeconds;
    private readonly double _maxDelaySeconds;

    private volatile bool _stopped;

    /// <summary>
    /// Cancelled by <see cref="Stop"/>, and linked into the token the read loop
    /// uses.
    ///
    /// Kotlin's <c>stop()</c> set a flag and nothing else, so a stop that landed
    /// while a stream was open was not noticed until the backend next said
    /// something - which on an idle stream is up to 15 seconds of a shutdown
    /// that looks hung. A token costs nothing and makes the stop prompt; the
    /// flag is still what <see cref="ReconnectLoop"/> reads, so the loop ends
    /// quietly rather than by throwing.
    /// </summary>
    private readonly CancellationTokenSource _stopSignal = new();

    public PrintJobSseClient(
        ILogger log,
        PrintlyApiClient api,
        Func<AgentCredential?> credentialProvider,
        JobReferenceHandler onJobReference,
        double baseDelaySeconds = 1.0,
        double maxDelaySeconds = 60.0)
    {
        _log = log;
        _api = api;
        _credentialProvider = credentialProvider;
        _onJobReference = onJobReference;
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

    public async Task RunForeverAsync(CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopSignal.Token);
        await ReconnectLoop.RunAsync(
            _log, _baseDelaySeconds, _maxDelaySeconds, () => _stopped, ConnectOnceAsync, linked.Token)
            .ConfigureAwait(false);
    }

    private async Task ConnectOnceAsync(CancellationToken cancellation)
    {
        var credential = _credentialProvider();
        if (credential is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_baseDelaySeconds * 1000), cancellation).ConfigureAwait(false);
            return;
        }

        await ReconcileAsync(credential, cancellation).ConfigureAwait(false);
        await StreamOnceAsync(credential, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists what the backend still considers outstanding, before subscribing.
    ///
    /// Kotlin wrapped this in <c>withContext(Dispatchers.IO)</c>; the .NET call
    /// is already asynchronous down to the socket, so there is nothing to move
    /// off - the dispatcher hop has no equivalent and needs none.
    /// </summary>
    private async Task ReconcileAsync(AgentCredential credential, CancellationToken cancellation)
    {
        var jobs = await _api.OutstandingJobsAsync(credential, cancellation).ConfigureAwait(false);
        foreach (var job in jobs) _onJobReference(job.JobId, job.OrderId, job.OrderCode);
    }

    private async Task StreamOnceAsync(AgentCredential credential, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, _api.BaseUrl.TrimEnd('/') + "/api/v1/print-agent/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _api.SseHttp
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .ConfigureAwait(false);

        // A rejected subscription is a failure and has to look like one. This is
        // the case OkHttp used to deliver as an onFailure with a *null*
        // throwable, and reading that as a clean close meant a per-route 503 or
        // 429 on /events became a silent ~1.3/s connect-reject loop: jobs kept
        // printing off the ten-second reconcile poll, so the only symptom was
        // every order arriving late. Returning normally here would do exactly
        // the same thing, because a normal return is what resets the ladder.
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"print job stream rejected: HTTP {(int)response.StatusCode}");
        }

        _log.LogInformation("sse_connected");

        await using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await foreach (var frame in SseFrames.ReadAsync(body, cancellation).ConfigureAwait(false))
        {
            HandleEvent(frame.Data);
        }

        // Falling out of the loop is the server having closed the stream: a
        // clean disconnect, which returns normally so the backoff ladder resets.
    }

    /// <summary>
    /// Reads the two ids off an event payload, and skips anything that does not
    /// carry both.
    ///
    /// Parsed loosely rather than into <see cref="Models.PrintAgentSseEvent"/>
    /// on purpose, which is what Kotlin did too (it read a raw Map and pulled
    /// the fields with <c>as? String</c>). A payload missing a field is a frame
    /// to skip, not a crash - and deserialising into the record would hand the
    /// handler nulls in fields the type swears are non-null, which is worse than
    /// either.
    /// </summary>
    private void HandleEvent(string data)
    {
        try
        {
            using var payload = JsonDocument.Parse(data);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (!root.TryGetProperty("jobId", out var jobId) || jobId.ValueKind != JsonValueKind.String) return;
            if (!root.TryGetProperty("orderId", out var orderId) || orderId.ValueKind != JsonValueKind.String) return;

            _onJobReference(jobId.GetString()!, orderId.GetString()!, null);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            // One unreadable frame must not end the subscription - the next one
            // is probably fine, and dropping the connection over it would cost
            // every event until the reconnect lands.
            _log.LogWarning(exc, "sse_malformed_event");
        }
    }
}
