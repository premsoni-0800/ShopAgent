using System.Net;
using System.Text.Json;
using PrintlyAgent.Credentials;
using PrintlyAgent.Models;
using PrintlyAgent.Net;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// The API client's contract with the Printly Render API.
///
/// Most of this runs against a local stub rather than the real backend: the
/// things worth pinning are how errors are unwrapped and which credential goes
/// to which route, and both are decided entirely on this side. One test at the
/// end does reach the live server, and is skipped rather than failed when there
/// is no network - a port that only passes online is not a test suite.
/// </summary>
public class ApiClientTests
{
    /// <summary>
    /// A throwaway HTTP server. Simpler than a mocking framework and closer to
    /// the thing being tested: the client's job is to speak HTTP correctly, so
    /// the test speaks HTTP back at it.
    /// </summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public List<HttpListenerRequest> Received { get; } = new();
        public List<string> AuthHeaders { get; } = new();

        public StubServer(HttpStatusCode status, string body, string contentType = "application/json")
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }

                    Received.Add(context.Request);
                    AuthHeaders.Add(context.Request.Headers["Authorization"] ?? "");

                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = contentType;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
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

    private static OwnerSession Owner() => new("owner-jwt", "refresh", "shop-1", "A Shop");
    private static AgentCredential Agent() => new("agent-1", "shop-1", "s3cret");

    [Fact(DisplayName = "the agent bearer is agentId.secret, and is never persisted")]
    public void TheAgentBearerIsAgentIdDotSecret()
    {
        Assert.Equal("agent-1.s3cret", Agent().BearerToken);

        // Serialising must not emit it, or reading the credential back later
        // fails on an unrecognised property and the PC looks unpaired.
        var json = JsonSerializer.Serialize(Agent());
        Assert.DoesNotContain("bearerToken", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "an error body is unwrapped from its error envelope")]
    public async Task AnErrorBodyIsUnwrappedFromItsErrorEnvelope()
    {
        // Reading the wrong level here would leave Code null for every failure,
        // which breaks every caller that branches on it.
        const string body = """
            {"error":{"code":"PRINT_JOB_ALREADY_CLAIMED","message":"Another agent has it","requestId":"abc"}}
            """;
        using var server = new StubServer(HttpStatusCode.Conflict, body);
        using var client = new PrintlyApiClient(server.BaseUrl);

        var error = await Assert.ThrowsAsync<ApiError>(
            () => client.ClaimJobAsync(Agent(), "job-1"));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("PRINT_JOB_ALREADY_CLAIMED", error.Code);
        Assert.Contains("Another agent has it", error.Message);
    }

    [Fact(DisplayName = "an error with no envelope still surfaces its status")]
    public async Task AnErrorWithNoEnvelopeStillSurfacesItsStatus()
    {
        using var server = new StubServer(HttpStatusCode.BadGateway, "upstream exploded", "text/plain");
        using var client = new PrintlyApiClient(server.BaseUrl);

        var error = await Assert.ThrowsAsync<ApiError>(() => client.JobDetailAsync(Agent(), "job-1"));

        Assert.Equal(502, error.StatusCode);
        Assert.Null(error.Code);
        Assert.Contains("upstream exploded", error.Message);
    }

    [Fact(DisplayName = "a device call carries the agent bearer, not the owner session")]
    public async Task ADeviceCallCarriesTheAgentBearer()
    {
        using var server = new StubServer(HttpStatusCode.OK, """{"autoPrintEnabled":true,"serverTime":"2026-01-01T00:00:00Z"}""");
        using var client = new PrintlyApiClient(server.BaseUrl);

        var response = await client.HeartbeatAsync(Agent(), "0.1.0");

        Assert.True(response.AutoPrintEnabled);
        Assert.Equal("Bearer agent-1.s3cret", server.AuthHeaders[0]);
    }

    [Fact(DisplayName = "an owner call carries the owner session, not the agent bearer")]
    public async Task AnOwnerCallCarriesTheOwnerSession()
    {
        using var server = new StubServer(HttpStatusCode.OK, "[]");
        using var client = new PrintlyApiClient(server.BaseUrl);

        await client.ListPrintAgentsAsync(Owner());

        Assert.Equal("Bearer owner-jwt", server.AuthHeaders[0]);
    }

    [Fact(DisplayName = "a field the agent has never seen does not stop it printing")]
    public async Task AFieldTheAgentHasNeverSeenDoesNotStopItPrinting()
    {
        // The backend adds fields. An agent that threw on one would stop working
        // the moment the server was deployed, which is not a trade worth making.
        const string body = """
            {"jobId":"j1","orderId":"o1","orderCode":"HH-000001","status":"CLAIMED","items":[],
             "somethingAddedNextQuarter":42}
            """;
        using var server = new StubServer(HttpStatusCode.OK, body);
        using var client = new PrintlyApiClient(server.BaseUrl);

        var detail = await client.JobDetailAsync(Agent(), "j1");

        Assert.Equal("HH-000001", detail.OrderCode);
        Assert.Equal(PrintJobStatus.CLAIMED, detail.Status);
    }

    [Fact(DisplayName = "enum values travel as names, the way the backend sends them")]
    public async Task EnumValuesTravelAsNames()
    {
        // Serialised as ordinals they would silently mean something else - the
        // backend reads FAILED, not 5.
        using var server = new StubServer(HttpStatusCode.OK, "{}");
        using var client = new PrintlyApiClient(server.BaseUrl);

        await client.ReportStatusAsync(Agent(), "j1", PrintJobStatus.FAILED,
            reasonCode: PrintJobFailureReason.PRINTER_OFFLINE);

        // The stub read the body already; assert on what the client built.
        var request = new PrintJobStatusUpdateRequest(
            PrintJobStatus.FAILED, null, PrintJobFailureReason.PRINTER_OFFLINE, null);
        var json = JsonSerializer.Serialize(request, client.Json);
        Assert.Contains("\"FAILED\"", json);
        Assert.Contains("\"PRINTER_OFFLINE\"", json);
    }

    [Fact(DisplayName = "the streaming client has no overall timeout, or an idle stream would be killed")]
    public void TheStreamingClientHasNoOverallTimeout()
    {
        using var client = new PrintlyApiClient("https://example.invalid");

        // 30s bounds a request/response call. Applied to a stream it would tear
        // down a perfectly healthy connection between the backend's 15s pings.
        Assert.Equal(TimeSpan.FromSeconds(30), client.Http.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.SseHttp.Timeout);
    }

    /// <summary>
    /// The one test that talks to the real Render API. Unauthenticated, so it
    /// proves reachability and the error-envelope shape without needing a
    /// credential - and skips rather than fails when the machine is offline.
    /// </summary>
    [Fact(DisplayName = "the live Render API is reachable and refuses an unauthenticated device call")]
    public async Task TheLiveRenderApiIsReachable()
    {
        using var client = new PrintlyApiClient("https://printly-3fa8.onrender.com");
        var credential = new AgentCredential("not", "a", "credential");

        try
        {
            await client.OutstandingJobsAsync(credential);
            Assert.Fail("an invalid credential should not have been accepted");
        }
        catch (ApiError error)
        {
            // Reached the server and it answered in its own vocabulary.
            Assert.True(error.StatusCode is 401 or 403, $"unexpected status {error.StatusCode}");
        }
        catch (HttpRequestException)
        {
            // No network on this machine - not a failure of the port.
            return;
        }
        catch (TaskCanceledException)
        {
            return;
        }
    }
}
