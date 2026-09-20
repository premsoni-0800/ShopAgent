using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintlyAgent.Credentials;
using PrintlyAgent.Models;

namespace PrintlyAgent.Net;

public sealed class ApiError : Exception
{
    public int StatusCode { get; }
    public string? Code { get; }

    public ApiError(int statusCode, string? code, string message)
        : base($"{statusCode} {code}: {message}")
    {
        StatusCode = statusCode;
        Code = code;
    }
}

/// <summary>
/// HTTP calls to the Printly backend - port of net/PrintlyApiClient.kt.
///
/// Two distinct credential shapes, matching the backend's two disjoint
/// authentication filters: owner-session calls carry the owner's own JWT and
/// only ever reach owner-facing routes; agent calls carry the
/// <c>&lt;agentId&gt;.&lt;secret&gt;</c> bearer and only ever reach the
/// print-agent device routes. Keeping them apart is not tidiness - sending an
/// agent bearer at an owner route is a 401, and the reverse is worse.
/// </summary>
public sealed partial class PrintlyApiClient : IDisposable
{
    public string BaseUrl { get; }

    /// <summary>
    /// For request/response calls. A 30 second ceiling on the whole call, which
    /// is right for an API call and fatal for a stream - see <see cref="SseHttp"/>.
    /// </summary>
    public HttpClient Http { get; }

    /// <summary>
    /// The client every SSE stream must use - <see cref="Http"/>'s timeouts are
    /// correct for a request/response call and fatal for a long-lived one.
    ///
    /// HttpClient.Timeout bounds the *whole* operation, response body included,
    /// so a stream opened with the ordinary client is killed 30 seconds in no
    /// matter how healthy it is. The backend's broadcasters ping every 15s
    /// (OrderEventBroadcaster.HEARTBEAT_INTERVAL_MS), so a well-behaved idle
    /// stream would be torn down between keepalives, the reconnect loop would
    /// reopen it, and the same thing would happen again - a permanent flap that
    /// looks like a flaky network and quietly costs every event landing in the
    /// gap.
    ///
    /// So: no timeout at all here (the read loop and the cancellation token own
    /// the lifetime instead), and KeepAlive pings underneath to notice a
    /// half-open socket without waiting for one.
    /// </summary>
    public HttpClient SseHttp { get; }

    /// <summary>
    /// Shared, and configured to match Jackson on the Kotlin side: camelCase on
    /// the wire, enums as their names rather than as ordinals, and unknown
    /// properties ignored. That last one is not laziness - the backend adds
    /// fields, and an agent that threw on one it had never seen would stop
    /// printing the moment the server was deployed.
    /// </summary>
    public JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public PrintlyApiClient(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');

        Http = new HttpClient(new SocketsHttpHandler
        {
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
            Timeout = TimeSpan.FromSeconds(30),
        };

        SseHttp = new HttpClient(new SocketsHttpHandler
        {
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private string Url(string path) => BaseUrl + path;

    // --- owner-session calls -------------------------------------------------

    public Task<Dictionary<string, object?>> VerifyWidgetAsync(string accessToken, CancellationToken ct = default) =>
        PostJsonAsync(Url("/api/v1/auth/verify-widget"), new { accessToken }, ct);

    /// <summary>`identifier` is a phone number or an email address - the backend branches on `@`.</summary>
    public Task<Dictionary<string, object?>> LoginWithPasswordAsync(string identifier, string password, CancellationToken ct = default) =>
        PostJsonAsync(Url("/api/v1/auth/login-password"), new { identifier, password }, ct);

    public Task SetPasswordAsync(OwnerSession session, string password, CancellationToken ct = default) =>
        SendAsync(Post(Url("/api/v1/auth/set-password"), new { password }, OwnerAuth(session)), ct);

    public Task<Dictionary<string, object?>> RefreshOwnerSessionAsync(string refreshToken, CancellationToken ct = default) =>
        PostJsonAsync(Url("/api/v1/auth/refresh"), new { refreshToken }, ct);

    public Task<PairingCodeResponse> PairAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<PairingCodeResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/print-agents/pair"), null, OwnerAuth(session)), ct);

    public Task<List<PrintAgentSummary>> ListPrintAgentsAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<List<PrintAgentSummary>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/print-agents"), OwnerAuth(session)), ct);

    public Task RevokePrintAgentAsync(OwnerSession session, string agentId, CancellationToken ct = default) =>
        SendAsync(
            Post(Url($"/api/v1/shop/{session.ShopId}/print-agents/{agentId}/revoke"), null, OwnerAuth(session)), ct);

    public Task<JsonElement> OwnerGetAsync(OwnerSession session, string path, CancellationToken ct = default) =>
        SendAsync<JsonElement>(Get(Url(path), OwnerAuth(session)), ct);

    public Task<JsonElement> OwnerPutAsync(OwnerSession session, string path, object body, CancellationToken ct = default) =>
        SendAsync<JsonElement>(Put(Url(path), body, OwnerAuth(session)), ct);

    public Task OwnerPostAsync(OwnerSession session, string path, object body, CancellationToken ct = default) =>
        SendAsync(Post(Url(path), body, OwnerAuth(session)), ct);

    // --- device (agent-credential) calls -------------------------------------

    public Task<PrintAgentExchangeResponse> ExchangeAsync(
        string code, string agentName, string? machineFingerprint, string? agentVersion, CancellationToken ct = default) =>
        SendAsync<PrintAgentExchangeResponse>(
            Post(Url("/api/v1/print-agents/exchange"),
                new PrintAgentExchangeRequest(code, agentName, machineFingerprint, agentVersion), null), ct);

    public Task<PrintAgentHeartbeatResponse> HeartbeatAsync(
        AgentCredential credential, string agentVersion, CancellationToken ct = default) =>
        SendAsync<PrintAgentHeartbeatResponse>(
            Post(Url("/api/v1/print-agent/heartbeat"),
                new PrintAgentHeartbeatRequest(agentVersion), AgentAuth(credential)), ct);

    public Task SyncPrintersAsync(AgentCredential credential, object request, CancellationToken ct = default) =>
        SendAsync(Put(Url("/api/v1/print-agent/printers"), request, AgentAuth(credential)), ct);

    public Task<List<PrintJobSummary>> OutstandingJobsAsync(AgentCredential credential, CancellationToken ct = default) =>
        SendAsync<List<PrintJobSummary>>(Get(Url("/api/v1/print-agent/jobs"), AgentAuth(credential)), ct);

    public Task<PrintJobDetail> JobDetailAsync(AgentCredential credential, string jobId, CancellationToken ct = default) =>
        SendAsync<PrintJobDetail>(Get(Url($"/api/v1/print-agent/jobs/{jobId}"), AgentAuth(credential)), ct);

    public Task<PrintJobDetail> ClaimJobAsync(AgentCredential credential, string jobId, CancellationToken ct = default) =>
        SendAsync<PrintJobDetail>(
            Post(Url($"/api/v1/print-agent/jobs/{jobId}/claim"), null, AgentAuth(credential)), ct);

    public Task<PrintJobDownloadUrls> DownloadUrlsAsync(AgentCredential credential, string jobId, CancellationToken ct = default) =>
        SendAsync<PrintJobDownloadUrls>(
            Post(Url($"/api/v1/print-agent/jobs/{jobId}/download-url"), null, AgentAuth(credential)), ct);

    public Task ReportStatusAsync(
        AgentCredential credential,
        string jobId,
        PrintJobStatus status,
        string? error = null,
        PrintJobFailureReason? reasonCode = null,
        string? printerName = null,
        CancellationToken ct = default) =>
        SendAsync(
            Post(Url($"/api/v1/print-agent/jobs/{jobId}/status"),
                new PrintJobStatusUpdateRequest(status, error, reasonCode, printerName),
                AgentAuth(credential)), ct);

    public Task ReportProgressAsync(
        AgentCredential credential, string jobId, PrintJobProgressStage stage, CancellationToken ct = default) =>
        SendAsync(
            Post(Url($"/api/v1/print-agent/jobs/{jobId}/progress"),
                new PrintJobProgressRequest(stage), AgentAuth(credential)), ct);

    /// <summary>
    /// The documents for this job are now on this agent's own disk.
    ///
    /// Durable, unlike <see cref="ReportProgressAsync"/>, which is a live trace
    /// the backend never persists. This is what lights "Order accepted" on the
    /// student's timeline for an order accepted before they arrived, so it must
    /// be sent only once the files are written and validated - not when the
    /// download starts. Idempotent: the first report wins and later ones change
    /// nothing, which is what makes it safe to send again after a restart.
    /// </summary>
    public Task<PrintJobDetail> ReportCachedAsync(
        AgentCredential credential, string jobId, CancellationToken ct = default) =>
        SendAsync<PrintJobDetail>(
            Post(Url($"/api/v1/print-agent/jobs/{jobId}/cached"), null, AgentAuth(credential)), ct);

    // -------------------------------------------------------------------------

    private static AuthenticationHeaderValue OwnerAuth(OwnerSession session) =>
        new("Bearer", session.AccessToken);

    private static AuthenticationHeaderValue AgentAuth(AgentCredential credential) =>
        new("Bearer", credential.BearerToken);

    private HttpRequestMessage Get(string url, AuthenticationHeaderValue? auth) =>
        Build(HttpMethod.Get, url, null, auth);

    private HttpRequestMessage Post(string url, object? body, AuthenticationHeaderValue? auth) =>
        Build(HttpMethod.Post, url, body, auth);

    private HttpRequestMessage Put(string url, object? body, AuthenticationHeaderValue? auth) =>
        Build(HttpMethod.Put, url, body, auth);

    private HttpRequestMessage Build(HttpMethod method, string url, object? body, AuthenticationHeaderValue? auth)
    {
        var request = new HttpRequestMessage(method, url);
        if (auth is not null) request.Headers.Authorization = auth;
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }
        return request;
    }

    private async Task<Dictionary<string, object?>> PostJsonAsync(string url, object body, CancellationToken ct)
    {
        var result = await SendAsync<Dictionary<string, object?>>(Post(url, body, null), ct).ConfigureAwait(false);
        return result ?? new Dictionary<string, object?>();
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        await ReadAsync(request, ct).ConfigureAwait(false);

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await ReadAsync(request, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return default!;
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    /// <summary>
    /// Every error this backend returns is wrapped as
    /// <c>{"error": {"code": ..., "message": ...}}</c> - reading the wrong level
    /// here would silently leave <see cref="ApiError.Code"/> null for every
    /// failure, which breaks every caller that branches on it (owner-session
    /// refresh-on-expiry, PASSWORD_NOT_SET detection, PRINT_JOB_ALREADY_CLAIMED
    /// race handling).
    /// </summary>
    private async Task<string> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return body;

        string? code = null;
        var message = body;
        try
        {
            using var parsed = JsonDocument.Parse(body);
            var root = parsed.RootElement;
            var error = root.TryGetProperty("error", out var nested) ? nested : root;
            if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                code = c.GetString();
            if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                message = m.GetString() ?? body;
        }
        catch (JsonException)
        {
            // Non-JSON error body - fall back to the raw text.
        }

        throw new ApiError((int)response.StatusCode, code, message);
    }

    public void Dispose()
    {
        Http.Dispose();
        SseHttp.Dispose();
    }
}
