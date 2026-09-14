using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrintlyAgent.Credentials;
using PrintlyAgent.Db;
using PrintlyAgent.Jobs;
using PrintlyAgent.Models;
using PrintlyAgent.Net;
using PrintlyAgent.Printers;
using PrintlyAgent.Printing;

namespace PrintlyAgent.Core;

/// <summary>
/// The agent itself: everything that keeps running whether or not anyone is
/// looking at the window. Port of core/AgentCore.kt.
///
/// Seven loops, started once both a signed-in owner and a paired machine exist:
/// heartbeat, printer sync, the print-job stream, the order stream, the
/// scheduled-job check, the reconciliation poll, and a one-shot resume of
/// whatever the last run was interrupted doing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentCore : IAsyncDisposable
{
    /// <summary>
    /// Intake: fetching an order's details and writing it down. Deliberately
    /// separate from printing and allowed to run several at a time - it is
    /// network-bound, and none of it touches a printer. Running it inside the
    /// print slot, as it used to, meant the next order could not be recorded
    /// until the current one had finished printing.
    /// </summary>
    private const int IntakeConcurrency = 4;

    /// <summary>
    /// How many already-known jobs the priority re-check asks about per pass.
    /// Small on purpose: this runs on the reconciliation interval, and asking
    /// about a whole backlog on a timer is what flooded the backend before.
    /// </summary>
    private const int PriorityRecheckBatch = 6;

    private static readonly TimeSpan ScheduledJobCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrinterSyncInterval = TimeSpan.FromSeconds(120);

    private readonly ILogger _log;
    private readonly ILoggerFactory _loggerFactory;

    public Settings Settings { get; }
    public PrintlyApiClient Api { get; }
    public Database Db { get; }

    private readonly Auth _auth;

    // Volatile in Kotlin, and for the same reason here: written by the loops,
    // read by the UI thread whenever the screen asks for status.
    private volatile OwnerSession? _ownerSession;
    private volatile AgentCredential? _agentCredential;
    private volatile bool _autoPrintEnabled;
    private volatile bool _lastHeartbeatOk;

    /// <summary>
    /// Set when the server answers the heartbeat with a rejection rather than
    /// not answering at all.
    ///
    /// The difference matters to whoever is looking at the screen. "Cannot reach
    /// the server" sends them to check the network; a revoked registration needs
    /// them to sign in again, and no amount of waiting or rebooting the router
    /// will fix it. Reporting the second as the first is how someone spends
    /// twenty minutes on the wrong problem.
    /// </summary>
    private volatile bool _credentialRejected;

    public OwnerSession? OwnerSession => _ownerSession;
    public AgentCredential? AgentCredential => _agentCredential;
    public bool AutoPrintEnabled => _autoPrintEnabled;

    /// <summary>
    /// Best-effort UI push - the WebView2 host wires this to
    /// ExecuteScriptAsync(...). No-op headless.
    /// </summary>
    public Action<string, IReadOnlyDictionary<string, object?>> OnEmit { get; set; } = (_, _) => { };

    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// One refresh for however many callers hit the same expired token at once.
    /// Reads the live session each time rather than capturing one, because the
    /// whole question it answers is whether the token has moved on since.
    /// </summary>
    private readonly SharedRefresh _ownerRefresh;
    private readonly List<Task> _loops = new();

    /// <summary>
    /// Held separately from the rest because it is the one loop that can stop on
    /// its own - see OrderEventsClient.Resume - and has to be startable again
    /// without restarting the other six.
    /// </summary>
    private Task? _orderEventsLoop;
    private CancellationTokenSource? _orderEventsShutdown;

    private readonly JobDispatcher _intake;
    private readonly PrintQueue _printQueue;
    private readonly PrintJobSseClient _sse;
    private readonly OrderEventsClient _orderEvents;

    public AgentCore(Settings settings, ILoggerFactory loggerFactory)
    {
        Settings = settings;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<AgentCore>();

        Api = new PrintlyApiClient(settings.BackendBaseUrl);
        Db = new Database(settings.DbPath);
        _auth = new Auth(loggerFactory.CreateLogger<Auth>());

        _ownerSession = CredentialStore.LoadOwnerSession();
        _agentCredential = CredentialStore.LoadAgentCredential();

        _ownerRefresh = new SharedRefresh(() => _ownerSession?.AccessToken, RefreshOwnerSessionAsync);

        _intake = new JobDispatcher(loggerFactory.CreateLogger<JobDispatcher>(), IntakeConcurrency);

        // A job the print queue must wait for is a reference intake has fetched
        // but not yet handed over - HoldsPrintingCount, not ActiveCount. The
        // reconciliation poll keeps intake permanently busy re-examining
        // references the queue already holds, and none of those can change what
        // prints next; waiting on them left the printer idle between sheets for
        // the length of a backlog.
        _printQueue = new PrintQueue(
            loggerFactory.CreateLogger<PrintQueue>(),
            settings.MaxConcurrentPrintJobs,
            intakeBusy: () => _intake.HoldsPrintingCount > 0,
            // Fetch the next couple of documents while this one prints, so the
            // printer is not waiting on a download between orders.
            prepareAhead: jobId =>
            {
                var credential = _agentCredential;
                if (credential is not null) JobPipeline.PrepareAhead(JobContextFor(credential), jobId);
            },
            onDepthChanged: OnJobProgress);

        _sse = new PrintJobSseClient(
            loggerFactory.CreateLogger<PrintJobSseClient>(),
            Api,
            () => _agentCredential,
            (jobId, orderId, orderCode) => OnJobReference(jobId, orderId, orderCode),
            settings.ReconnectBaseDelaySeconds,
            settings.ReconnectMaxDelaySeconds);

        _orderEvents = new OrderEventsClient(
            loggerFactory.CreateLogger<OrderEventsClient>(),
            Api,
            () => _ownerSession,
            OnOrdersChanged,
            _ => RefreshOwnerSessionSharedAsync(),
            settings.ReconnectBaseDelaySeconds,
            settings.ReconnectMaxDelaySeconds);

        // Before anything else, and deliberately not inside Start(): documents
        // abandoned by a crashed agent are there whether or not this one is
        // paired, and an agent that never finishes signing in would otherwise
        // leave them sitting on the counter PC indefinitely.
        var swept = Documents.SweepOrphanedDocuments(settings.TempDir);
        if (swept > 0) _log.LogInformation("orphaned_documents_removed count={Count}", swept);
    }

    // --- lifecycle -----------------------------------------------------------

    public void Start()
    {
        if (_ownerSession is null || _agentCredential is null || _loops.Count > 0) return;

        var token = _shutdown.Token;
        _orderEventsShutdown = CancellationTokenSource.CreateLinkedTokenSource(token);

        _loops.Add(Task.Run(() => HeartbeatLoopAsync(token), token));
        _loops.Add(Task.Run(() => PrinterSyncLoopAsync(token), token));
        _loops.Add(Task.Run(() => _sse.RunForeverAsync(token), token));

        _orderEventsLoop = Task.Run(() => _orderEvents.RunForeverAsync(_orderEventsShutdown.Token), token);
        _loops.Add(_orderEventsLoop);
        _loops.Add(Task.Run(() => ScheduledJobsLoopAsync(token), token));
        _loops.Add(Task.Run(() => JobReconcileLoopAsync(token), token));
        _loops.Add(Task.Run(() => PriorityRecheckLoopAsync(token), token));
        _loops.Add(Task.Run(() => ResumeInterruptedJobsOnceAsync(token), token));
    }

    public async ValueTask DisposeAsync()
    {
        // Asked to stop before being cancelled: the clients close their streams
        // cleanly, which is the difference between a tidy disconnect and the
        // backend holding an emitter open until it times out.
        _sse.Stop();
        _orderEvents.Stop();
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A loop still inside a network call is left to process teardown.
            // Waiting on it forever is how a process fails to exit.
        }
        _shutdown.Dispose();
        _orderEventsShutdown?.Dispose();
        await _printQueue.DisposeAsync().ConfigureAwait(false);

        // After the queue, never before: a worker still mid-submission is using
        // one of these files, and pulling it out from under them would turn an
        // orderly shutdown into a failed print. By here nothing is printing, so
        // anything still fetched is fetched for a job that will not now run.
        var credential = _agentCredential;
        if (credential is not null)
        {
            try { JobPipeline.DiscardPreparedDownloads(JobContextFor(credential)); }
            catch (Exception exc) { _log.LogDebug(exc, "discard_prepared_downloads_failed"); }
        }

        _intake.Dispose();
        Db.Dispose();
        Api.Dispose();
    }

    // --- called from the UI bridge -------------------------------------------

    public async Task<OwnerSession> SignInWithPasswordAsync(string identifier, string password) =>
        await AfterSignInAsync(
            await _auth.LoginWithPasswordAsync(Api, identifier, password).ConfigureAwait(false)).ConfigureAwait(false);

    public async Task<OwnerSession> SignInWithOtpAsync(string widgetAccessToken) =>
        await AfterSignInAsync(
            await _auth.FinishLoginAsync(Api, widgetAccessToken).ConfigureAwait(false)).ConfigureAwait(false);

    public Task SetPasswordAsync(string password) =>
        _auth.SetPasswordAsync(Api, RequireSession(), password);

    /// <summary>
    /// Takes over the session the dashboard just signed in with, and pairs this
    /// machine on the strength of it.
    ///
    /// This is what removes the pairing code. A code exists to carry proof of
    /// ownership from a browser, where the owner is signed in, to a desktop app
    /// that has no way to know who they are - and typing six characters across
    /// that gap was the only reason it was ever asked for. In this app there is
    /// no gap: the dashboard in the window and the agent behind it are one
    /// process, so the session is simply handed over.
    ///
    /// Idempotent, because the page hands it over on every sign-in and on every
    /// reload: pairing an already-paired machine re-uses the registration it
    /// already holds, and Start() is a no-op once the loops are running.
    ///
    /// Still fails loudly if another machine holds this shop's registration -
    /// that is a real conflict a person has to resolve, not something to paper
    /// over by quietly stealing the pairing from the PC that has the printers.
    /// </summary>
    public async Task<OwnerSession> AdoptOwnerSessionAsync(
        string accessToken, string refreshToken, string shopId, string? shopName)
    {
        var session = new OwnerSession(accessToken, refreshToken, shopId, shopName);
        CredentialStore.SaveOwnerSession(session);
        return await AfterSignInAsync(session).ConfigureAwait(false);
    }

    private async Task<OwnerSession> AfterSignInAsync(OwnerSession session)
    {
        _ownerSession = session;
        _agentCredential = await _auth.EnsurePairedAsync(Api, session).ConfigureAwait(false);
        if (_loops.Count == 0) Start(); else RestartOrderEventsIfStopped();
        return session;
    }

    /// <summary>
    /// Brings the Orders stream back after a sign-in, if it had given up.
    ///
    /// Start() only ever runs once, guarded on the loop list being empty, so
    /// without this a stream that stopped on a revoked token stayed stopped for
    /// the life of the process however many times the owner signed back in.
    /// </summary>
    private void RestartOrderEventsIfStopped()
    {
        if (_orderEventsLoop is { IsCompleted: false }) return;

        // Resume() replaces the client's own stop-latch. The loop's token has to
        // be replaced too: a cancelled CancellationTokenSource stays cancelled,
        // so reusing it would rebuild the exact latch this exists to undo.
        _orderEventsShutdown?.Dispose();
        _orderEventsShutdown = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        _orderEvents.Resume();
        _orderEventsLoop = Task.Run(() => _orderEvents.RunForeverAsync(_orderEventsShutdown.Token));
        _loops.Add(_orderEventsLoop);
        _log.LogInformation("order_events_restarted");
    }

    public void SignOut()
    {
        CredentialStore.ClearOwnerSession();
        _ownerSession = null;
    }

    public IReadOnlyDictionary<string, object?> Status() => new Dictionary<string, object?>
    {
        ["signedIn"] = _ownerSession is not null,
        ["paired"] = _agentCredential is not null,
        ["shopId"] = _ownerSession?.ShopId,
        ["autoPrintEnabled"] = _autoPrintEnabled,
        ["connected"] = _lastHeartbeatOk,
        ["credentialRejected"] = _credentialRejected,
        // What the counter needs to answer "where is my order?": how much work
        // is outstanding, and the codes in the order they will actually print.
        ["queueDepth"] = _printQueue.Depth,
        ["printingNow"] = _printQueue.ActiveCount,
        ["queuedOrders"] = _printQueue.Waiting(),
        // Which of those jumped the queue by scanning at the counter, and which
        // are on a printer right now. The agent page colours them from this:
        // green for the one coming out, blue for the people standing there.
        ["priorityOrders"] = _printQueue.WaitingPriority(),
        ["printingOrders"] = _printQueue.Printing(),
        // Anything wrong with a printer right now, named. Present whether or not
        // something is printing, which is the point: a jam does not wait for the
        // next order to become worth knowing about.
        ["printerFaults"] = _deviceConditions
            .Where(entry => entry.Value is not null)
            .Select(entry => new Dictionary<string, object?>
            {
                ["printer"] = entry.Key,
                ["condition"] = entry.Value.ToString(),
                ["detail"] = entry.Value!.Value.Description(),
            })
            .ToList(),
        ["agentVersion"] = Auth.AgentVersion,
        ["computerName"] = Auth.HostName(),
    };

    // --- owner-session calls -------------------------------------------------

    /// <summary>Paid orders only - matches the backend's own OrderStatus.isPaid.</summary>
    public async Task<List<Dictionary<string, object?>>> ListOrdersAsync()
    {
        var result = await OwnerRequestAsync(s =>
            Api.OwnerGetAsync(s, $"/api/v1/shop/{s.ShopId}/orders")).ConfigureAwait(false);

        return AsItemList(result)
            .Where(item => item.TryGetValue("paidAt", out var paidAt) && paidAt is not null)
            .ToList();
    }

    public async Task<List<Dictionary<string, object?>>> ListPrintersAsync()
    {
        var result = await OwnerRequestAsync(s =>
            Api.OwnerGetAsync(s, $"/api/v1/shop/{s.ShopId}/printers")).ConfigureAwait(false);
        return AsItemList(result);
    }

    public async Task SetAutoPrintAsync(bool enabled)
    {
        await OwnerRequestAsync(s => Api.OwnerPutAsync(
            s, $"/api/v1/shop/{s.ShopId}/settings/print",
            new Dictionary<string, object?> { ["autoPrint"] = enabled })).ConfigureAwait(false);

        _autoPrintEnabled = enabled;
        OnEmit("status", Status());
    }

    /// <summary>
    /// The only door out of a local UNKNOWN job - never called automatically,
    /// only from a human in the UI who has physically looked at the printer.
    /// Goes through the owner session, not the agent credential.
    /// </summary>
    public async Task ResolvePrintJobAsync(string jobId, bool success, string? note = null)
    {
        var body = new Dictionary<string, object?> { ["success"] = success };
        if (!string.IsNullOrWhiteSpace(note)) body["note"] = note;

        await OwnerRequestAsync<object?>(async s =>
        {
            await Api.OwnerPostAsync(s, $"/api/v1/shop/{s.ShopId}/print-jobs/{jobId}/resolve", body)
                .ConfigureAwait(false);
            return null;
        }).ConfigureAwait(false);

        Db.UpdateJobState(jobId, success ? "COMPLETED" : "FAILED", lastError: note);
        OnEmit("jobs", new Dictionary<string, object?>());
        OnEmit("orders", new Dictionary<string, object?>());
    }

    public List<Dictionary<string, object?>> UnresolvedJobs()
    {
        // Nothing to show before this machine belongs to a shop, and once it
        // does, another shop's leftovers are not this one's business.
        var shopId = _agentCredential?.ShopId;
        if (shopId is null) return new List<Dictionary<string, object?>>();

        return Db.UnresolvedJobs(shopId).Select(row => new Dictionary<string, object?>
        {
            ["jobId"] = row.JobId,
            ["orderId"] = row.OrderId,
            ["orderCode"] = row.OrderCode,
            ["state"] = row.State,
            ["attemptCount"] = row.AttemptCount,
            ["lastError"] = row.LastError,
            ["updatedAt"] = row.UpdatedAt,
            ["scheduledPrintAt"] = row.ScheduledPrintAt,
        }).ToList();
    }

    /// <summary>
    /// The list endpoints answer either a bare array or a paged envelope, and
    /// which one is not something to depend on - so both are accepted and
    /// anything else is an empty list rather than a crash on the shop's screen.
    /// </summary>
    private static List<Dictionary<string, object?>> AsItemList(JsonElement result)
    {
        var items = result.ValueKind switch
        {
            JsonValueKind.Array => result,
            JsonValueKind.Object when result.TryGetProperty("items", out var nested)
                && nested.ValueKind == JsonValueKind.Array => nested,
            _ => default,
        };

        var list = new List<Dictionary<string, object?>>();
        if (items.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in items.EnumerateArray())
        {
            var map = new Dictionary<string, object?>();
            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in item.EnumerateObject())
                {
                    map[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => property.Value.TryGetInt64(out var n) ? n : property.Value.GetDouble(),
                        _ => property.Value.Clone(),
                    };
                }
            }
            list.Add(map);
        }
        return list;
    }

    private OwnerSession RequireSession() =>
        _ownerSession ?? throw new InvalidOperationException("not signed in");

    /// <summary>
    /// Retries once, after a token refresh, on an expired/invalid owner access
    /// token.
    /// </summary>
    private async Task<T> OwnerRequestAsync<T>(Func<OwnerSession, Task<T>> call)
    {
        var session = RequireSession();
        try
        {
            return await call(session).ConfigureAwait(false);
        }
        catch (ApiError exc) when (exc.Code is "TOKEN_EXPIRED" or "TOKEN_INVALID")
        {
            await RefreshPastAsync(session.AccessToken).ConfigureAwait(false);
            return await call(RequireSession()).ConfigureAwait(false);
        }
    }

    private Task RefreshPastAsync(string expiredAccessToken) =>
        _ownerRefresh.PastAsync(expiredAccessToken);

    /// <summary>
    /// The same coalescing, for the callers that hold a stream rather than a
    /// request. The order-events stream refreshes when its connection is
    /// rejected mid-flight, and that lands at exactly the moment every intake
    /// lookup is being rejected too - which is the stampede RefreshPastAsync
    /// exists to stop.
    /// </summary>
    private Task RefreshOwnerSessionSharedAsync() =>
        RefreshPastAsync(RequireSession().AccessToken);

    public async Task RefreshOwnerSessionAsync()
    {
        var session = RequireSession();
        var refreshed = await Api.RefreshOwnerSessionAsync(session.RefreshToken).ConfigureAwait(false);

        if (!refreshed.TryGetValue("tokens", out var raw) || raw is null)
        {
            throw new InvalidOperationException("the refresh response had no \"tokens\"");
        }
        var tokens = raw is JsonElement element ? element : JsonSerializer.SerializeToElement(raw);

        var updated = new OwnerSession(
            tokens.GetProperty("accessToken").GetString()!,
            tokens.GetProperty("refreshToken").GetString()!,
            session.ShopId,
            session.ShopName);

        _ownerSession = updated;
        CredentialStore.SaveOwnerSession(updated);
    }

    // --- background loops ----------------------------------------------------

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        _log.LogInformation(
            "heartbeat_loop_started interval={Interval}s shop={Shop}",
            Settings.HeartbeatIntervalSeconds, _agentCredential?.ShopId);

        while (!ct.IsCancellationRequested)
        {
            var credential = _agentCredential;
            if (credential is not null)
            {
                var before = ConnectionState();
                try
                {
                    var result = await Api.HeartbeatAsync(credential, Auth.AgentVersion, ct).ConfigureAwait(false);
                    _autoPrintEnabled = result.AutoPrintEnabled;
                    _lastHeartbeatOk = true;
                    _credentialRejected = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exc)
                {
                    _log.LogWarning(exc, "heartbeat_failed");
                    _lastHeartbeatOk = false;
                    // 401/403 is the server saying who this machine is no longer
                    // holds - a different problem from not answering.
                    _credentialRejected = exc is ApiError { StatusCode: 401 or 403 };
                }

                // Push only on an actual flip - not every tick - but on any flip
                // that changes what the shop is being told.
                if (!ConnectionState().Equals(before)) PushStatus();
            }

            if (!await DelayAsync(TimeSpan.FromSeconds(Settings.HeartbeatIntervalSeconds), ct).ConfigureAwait(false)) return;
        }
    }

    private async Task PrinterSyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var credential = _agentCredential;
            if (credential is not null)
            {
                try
                {
                    // askDrivers: this sweep is the one that exists to notice a
                    // printer being plugged in or unplugged, so it is the one
                    // that must not be told what was true two minutes ago. It
                    // also leaves the cache warm for the print path, which is
                    // where the cost would actually be felt.
                    var printers = PrinterDiscovery.DiscoverPrinters(_log, askDrivers: true);
                    var request = new PrinterSyncRequest(printers.Select(p => new AgentPrinter(
                        p.WindowsPrinterName,
                        p.DisplayName,
                        p.ColorCapable,
                        p.DuplexCapable,
                        p.Sizes.ToList(),
                        p.Status)).ToList());

                    await Api.SyncPrintersAsync(credential, request, ct).ConfigureAwait(false);

                    foreach (var p in printers)
                    {
                        Db.UpsertPrinter(
                            p.WindowsPrinterName,
                            p.DisplayName,
                            p.ColorCapable?.ToString(),
                            p.DuplexCapable?.ToString(),
                            string.Join(",", p.Sizes.Select(s => s.ToString()).OrderBy(s => s, StringComparer.Ordinal)),
                            p.Status.ToString(),
                            p.IsSystemDefault);
                    }

                    // Name what is wrong with each device while nothing is
                    // printing.
                    //
                    // The detailed fault check lives in the outcome poll, which
                    // only runs while a job is on a printer - so a jam that
                    // happened after the last job finished was known to the
                    // shop only as "error" until the next print started, and
                    // whoever looked had to go and find out which error. This
                    // is the same mapping, asked on the same 120-second sweep
                    // that already reports the coarse status.
                    NoteDeviceConditions(printers);

                    OnEmit("printers", new Dictionary<string, object?>());
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exc)
                {
                    _log.LogError(exc, "printer_sync_failed");
                }
            }

            if (!await DelayAsync(PrinterSyncInterval, ct).ConfigureAwait(false)) return;
        }
    }

    /// <summary>
    /// Records what is wrong with each printer, and says so once per change.
    ///
    /// Once per change rather than every sweep: a jam nobody has cleared is the
    /// same jam two minutes later, and a line about it every two minutes buries
    /// the log it is meant to be useful in. Clearing is worth a line too - "it
    /// is printing again" is the half of the story a shop otherwise has to infer
    /// from silence.
    /// </summary>
    private void NoteDeviceConditions(IReadOnlyList<LocalPrinter> printers)
    {
        foreach (var printer in printers)
        {
            var condition = PrinterDiscovery.CurrentCondition(printer.WindowsPrinterName);
            var previous = _deviceConditions.TryGetValue(printer.WindowsPrinterName, out var was) ? was : null;
            if (condition == previous) continue;

            _deviceConditions[printer.WindowsPrinterName] = condition;

            if (condition is null)
            {
                _log.LogInformation("printer_recovered printer={Printer}", printer.WindowsPrinterName);
            }
            else
            {
                _log.LogWarning(
                    "printer_fault printer={Printer} condition={Condition} detail={Detail}",
                    printer.WindowsPrinterName, condition, condition.Value.Description());
            }
        }
    }

    /// <summary>What each printer was last seen to be wrong with, so only changes are reported.</summary>
    private readonly Dictionary<string, PrinterCondition?> _deviceConditions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the heartbeat watches for a change worth telling the shop about.
    ///
    /// credentialRejected belongs here and was missing. A 500 followed by a 401
    /// leaves the other two untouched, so nothing was pushed - while the meaning
    /// had changed from "the server is not answering" to "this machine is no
    /// longer paired, re-pair it", which are different things to do next. The
    /// screen kept the first message until the page's own ten-second poll
    /// happened to notice.
    /// </summary>
    private (bool, bool, bool) ConnectionState() => (_lastHeartbeatOk, _autoPrintEnabled, _credentialRejected);

    /// <summary>
    /// Never lets a listener take the loop down with it.
    ///
    /// OnEmit reaches a WebView2, and pushing into one that is on its way out
    /// throws. In the Kotlin this was the one statement in the heartbeat's loop
    /// body outside the try, so that throw ended the loop for the life of the
    /// process - the agent went on printing while its screen quietly stopped
    /// being told anything.
    /// </summary>
    private void PushStatus()
    {
        try { OnEmit("status", Status()); }
        catch (Exception exc) { _log.LogDebug(exc, "status_push_failed"); }
    }

    /// <summary>
    /// An order changed somewhere else - another till, the student's own app,
    /// the shop dashboard in a browser. Nothing local moved, so only the order
    /// list is told.
    /// </summary>
    private void OnOrdersChanged() => OnEmit("orders", new Dictionary<string, object?>());

    private void OnJobProgress()
    {
        OnEmit("jobs", new Dictionary<string, object?>());
        OnEmit("orders", new Dictionary<string, object?>());
        // The queue is in Status() too - its depth, what is printing, and who
        // jumped it by scanning at the counter. Those change as jobs move rather
        // than as the connection flips, so without this the shop's screen showed
        // a queue up to ten seconds out of date and colours that lagged the
        // printer.
        PushStatus();
    }

    /// <summary>
    /// Asks the backend when this order is due, and says plainly when it could
    /// not find out.
    ///
    /// The distinction is the whole point. This used to catch everything and
    /// answer null, and null already meant "no slot, print it now" - so a
    /// momentary 500, a timeout against a cold-starting host, or a token that
    /// had just aged out all read as "this order is not scheduled". A six
    /// o'clock slot discovered at eleven in the morning printed at eleven in the
    /// morning, which is precisely what the scheduling exists to stop.
    ///
    /// Only an answer from the server is treated as an answer. A client error is
    /// not worth retrying - a 404 for an order that is gone will say the same
    /// thing every time - but anything that might succeed later says so.
    /// </summary>
    private async Task<ScheduleLookup> OrderScheduleLookupAsync(string orderId, CancellationToken ct)
    {
        try
        {
            var order = await OwnerRequestAsync(s =>
                Api.OwnerGetAsync(s, $"/api/v1/shop/{s.ShopId}/orders/{orderId}", ct)).ConfigureAwait(false);

            // shopReleaseAt, which the backend works out as scheduledPrintAt
            // minus its own release lead, and which is exactly the moment this
            // shop is meant to receive the order. Taking it whole means the
            // agent has no opinion about the lead time and cannot disagree with
            // the server about it.
            //
            // Null on a Print Now order, which is the same thing as "print it as
            // soon as it is claimed".
            string? releaseAt = null;
            if (order.ValueKind == JsonValueKind.Object
                && order.TryGetProperty("shopReleaseAt", out var release)
                && release.ValueKind == JsonValueKind.String)
            {
                releaseAt = release.GetString();
            }

            var priority = order.ValueKind == JsonValueKind.Object
                && order.TryGetProperty("inShopPriority", out var flag)
                && flag.ValueKind == JsonValueKind.True;

            // The shop-facing order number. The backend calls this field
            // "orderId" on the order itself - the uuid is "id" - and it is what
            // every screen in the shop indexes by.
            string? orderCode = null;
            if (order.ValueKind == JsonValueKind.Object
                && order.TryGetProperty("orderId", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                orderCode = code.GetString();
            }

            return new ScheduleLookup.Known(releaseAt, priority, orderCode);
        }
        catch (OperationCanceledException)
        {
            // Shutting down is not an answer about scheduling, and must not be
            // reported as one.
            throw;
        }
        catch (ApiError exc)
        {
            if (exc.StatusCode is >= 400 and <= 499)
            {
                // OwnerRequestAsync has already refreshed and retried once, so a
                // 401 here means the owner is signed out, not that the token
                // aged out.
                _log.LogWarning(
                    "order_schedule_lookup_refused orderId={OrderId} status={Status} code={Code}",
                    orderId, exc.StatusCode, exc.Code);
                return ScheduleLookup.Refused.Instance;
            }
            _log.LogWarning(exc, "order_schedule_lookup_unavailable orderId={OrderId}", orderId);
            return ScheduleLookup.Unavailable.Instance;
        }
        catch (Exception exc)
        {
            _log.LogWarning(exc, "order_schedule_lookup_unavailable orderId={OrderId}", orderId);
            return ScheduleLookup.Unavailable.Instance;
        }
    }

    /// <summary>
    /// Hands the job to intake and returns at once.
    ///
    /// Returning immediately is the point, not a detail. This is called from the
    /// SSE reader and from the reconciliation loop, and both used to *await* the
    /// whole pipeline - download, print, and up to five minutes of watching the
    /// spooler - before doing anything else. While that ran, the stream read no
    /// further events and the poll loop's interval had not even started counting,
    /// so a second order placed during a print was not merely printed late, it
    /// was not delivered at all until the first one finished.
    ///
    /// The schedule lookup moves inside the dispatched block for the same
    /// reason: it makes its own HTTP call, which has no business happening on a
    /// socket reader thread.
    /// </summary>
    public void OnJobReference(string jobId, string orderId, string? orderCode, bool rechecking = false)
    {
        var credential = _agentCredential;
        if (credential is null) return;

        // What, if anything, asking the backend about this reference could still
        // tell us. Checked *before* the request rather than after it: the
        // duplicate guard downstream is what makes re-delivery safe, but it is
        // reached only once the order lookup has already been paid for, and
        // re-delivery is the common case, not the rare one.
        var work = ReferenceWorkFor(Db.GetJob(jobId));
        if (work == ReferenceWork.None) return;

        // Re-checking a known job for a late counter scan is the rotating
        // batch's job, not the reconciliation pass's - see PriorityRecheckBatch.
        if (work == ReferenceWork.Recheck && !rechecking) return;

        // Only a reference the print queue has never seen can change what prints
        // next, so only that one holds a printer.
        _intake.Submit(jobId, async () =>
        {
            var ct = _shutdown.Token;
            var lookup = await OrderScheduleLookupAsync(orderId, ct).ConfigureAwait(false);

            // Read off the same answer the schedule came from. The order lookup
            // is an HTTP call this path already makes, and OrderResponse has
            // carried inShopPriority all along - so knowing that a student is
            // standing at the counter costs nothing extra.
            var priority = lookup is ScheduleLookup.Known { Priority: true };

            // The SSE push carries only a job id and an order uuid, so for most
            // jobs orderCode arrives null and the queue fell back to showing the
            // job's uuid - which is what the counter then saw on screen in place
            // of the order number. The lookup above has just fetched the order,
            // so the real code is already in hand and costs nothing.
            orderCode ??= (lookup as ScheduleLookup.Known)?.OrderCode;

            switch (Scheduling.PlanFor(lookup, DateTimeOffset.UtcNow))
            {
                case SchedulePlan.PrintAt printAt:
                    JobPipeline.HandleJobReference(
                        JobContextFor(credential), _printQueue, jobId, orderId, orderCode, printAt.At, priority);
                    break;

                // Deliberately records nothing. Recording the job means deciding
                // when to print it, and that is the one thing this path could
                // not find out - so it is left to the ten-second reconciliation
                // poll, which re-lists every outstanding job and brings this one
                // back here to be asked again. Printing it now instead is what
                // sent a six o'clock order out at eleven in the morning, to sit
                // on the counter all day.
                case SchedulePlan.Hold:
                    _log.LogWarning("scheduled_lookup_deferred job={JobId} order={OrderId}", jobId, orderId);
                    break;
            }
        }, holdsPrinting: work == ReferenceWork.New);
    }

    private JobContext JobContextFor(AgentCredential credential) => new(
        Api,
        Db,
        Settings.TempDir,
        Settings.MaxRetryAttempts,
        Settings.DownloadTimeoutSeconds,
        Settings.JobStallSeconds,
        credential,
        OnJobProgress,
        _loggerFactory.CreateLogger("PrintlyAgent.Jobs.JobPipeline"),
        // The pipeline's own work is cancelled with the agent, so a job still
        // downloading when the app closes stops rather than finishing into a
        // database that is already shut.
        _shutdown.Token);

    private async Task ScheduledJobsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var credential = _agentCredential;
            if (credential is not null)
            {
                try
                {
                    JobPipeline.ProcessDueScheduledJobs(JobContextFor(credential), _printQueue);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception exc) { _log.LogError(exc, "scheduled_jobs_check_failed"); }
            }

            if (!await DelayAsync(ScheduledJobCheckInterval, ct).ConfigureAwait(false)) return;
        }
    }

    /// <summary>
    /// Picks up whatever the last run was in the middle of when it stopped.
    ///
    /// A job the agent had already recorded locally is invisible to every other
    /// path: the SSE push and the reconciliation poll both funnel into
    /// HandleJobReference, which drops any id already in the database as a
    /// duplicate. That is correct - it is what stops a document printing twice -
    /// but it means a job interrupted between "recorded" and "printed" is
    /// stranded permanently unless something goes looking for it once, here.
    ///
    /// Waits for the first heartbeat so a resumed job is not attempted while the
    /// backend is still unreachable, which would just burn its retries.
    /// </summary>
    private async Task ResumeInterruptedJobsOnceAsync(CancellationToken ct)
    {
        if (!await DelayAsync(TimeSpan.FromSeconds(Settings.HeartbeatIntervalSeconds), ct).ConfigureAwait(false)) return;

        var credential = _agentCredential;
        if (credential is null) return;

        var context = JobContextFor(credential);

        try
        {
            JobPipeline.ResumeInterruptedJobs(context, _printQueue);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exc) { _log.LogError(exc, "resume_interrupted_jobs_failed"); }

        // The other half of the same restart. ResumeInterruptedJobs picks up
        // what had not yet reached a printer and simply runs it again; this
        // hands over what had, which cannot be re-run and cannot be assumed
        // finished. Done in its own try so that a failure to close those out
        // never costs the resume above, and vice versa.
        try
        {
            await JobPipeline.ResolveJobsStrandedAtThePrinterAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exc) { _log.LogError(exc, "resolve_stranded_jobs_failed"); }
    }

    /// <summary>
    /// Asks the backend outright for outstanding work, independently of the SSE
    /// stream.
    ///
    /// The stream is the fast path and normally delivers a job in under a
    /// second, but it is not something to stake unattended operation on: a
    /// connection can stop delivering without ever closing (so no reconnect, and
    /// no reconnect means no reconcile), a push can be dropped while the backend
    /// restarts mid-deploy, and a job created during a reconnect window belongs
    /// to neither side. This loop is what makes the agent autonomous rather than
    /// merely reactive - the worst case for any job becomes one interval, not
    /// "until something else happens to wake the stream".
    ///
    /// Safe to run as often as we like: HandleJobReference keys off the local
    /// database, so a job id already seen is dropped before any printer is
    /// touched. A pass with nothing new costs one GET.
    /// </summary>
    private async Task JobReconcileLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!await DelayAsync(TimeSpan.FromSeconds(Settings.JobReconcileIntervalSeconds), ct).ConfigureAwait(false)) return;

            var credential = _agentCredential;
            if (credential is null) continue;

            try
            {
                var outstanding = await Api.OutstandingJobsAsync(credential, ct).ConfigureAwait(false);
                foreach (var job in outstanding) OnJobReference(job.JobId, job.OrderId, job.OrderCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception exc)
            {
                // Expected whenever the backend is briefly unreachable; the next
                // pass is seconds away, so this is not worth escalating.
                _log.LogDebug(exc, "job_reconcile_failed");
            }
        }
    }

    /// <summary>
    /// Decides what a re-delivered reference is worth, from the local row alone.
    ///
    /// The reconciliation poll re-lists every job the backend still considers
    /// outstanding, which includes every job waiting in the print queue and
    /// every job sitting in UNKNOWN waiting for a human to resolve it. Each of
    /// those used to cost a full order lookup on every pass, for ever. A
    /// terminal job has nothing left to decide, and one already granted priority
    /// has nothing left to learn; both are answered here, for free, off a row
    /// that is already local.
    /// </summary>
    internal static ReferenceWork ReferenceWorkFor(JobRow? known)
    {
        if (known is null) return ReferenceWork.New;
        if (JobPipeline.TERMINAL.Contains(known.State)) return ReferenceWork.None;
        if (known.Priority) return ReferenceWork.None;
        return ReferenceWork.Recheck;
    }

    /// <summary>
    /// Looks for a counter scan that landed after the order was already taken.
    ///
    /// This is the ordinary way in-shop priority happens - somebody orders ahead
    /// and then walks in - so the grant almost always arrives for a job the
    /// agent already has, and the only way to hear about it is to ask. Asking
    /// about every such job on every pass is what flooded the backend, so this
    /// asks about a rotating handful instead: flat cost whatever the queue looks
    /// like, and a shop with fewer than PriorityRecheckBatch jobs open still has
    /// every one of them checked every pass.
    ///
    /// The trade is that in a long backlog a scan can take a few passes to be
    /// noticed rather than one. That is worth having: the alternative on offer
    /// was an agent that noticed instantly and was too busy asking to print.
    /// </summary>
    private async Task PriorityRecheckLoopAsync(CancellationToken ct)
    {
        var seen = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!await DelayAsync(TimeSpan.FromSeconds(Settings.JobReconcileIntervalSeconds), ct).ConfigureAwait(false))
            {
                return;
            }

            var credential = _agentCredential;
            if (credential is null) continue;

            try
            {
                var batch = Db.PriorityCandidates(credential.ShopId, PriorityRecheckBatch, seen);

                // Round the cursor back when the rotation runs off the end,
                // rather than leaving it stranded past a queue that has since
                // drained and never checking anything again.
                seen = batch.Count == 0 ? 0 : seen + batch.Count;

                foreach (var row in batch)
                {
                    OnJobReference(row.JobId, row.OrderId, row.OrderCode, rechecking: true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception exc)
            {
                _log.LogDebug(exc, "priority_recheck_failed");
            }
        }
    }

    /// <summary>False when shutdown cut the wait short, so the caller can return.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>What asking the backend about a delivered job reference could still tell the agent.</summary>
internal enum ReferenceWork
{
    /// <summary>Never seen here. Look it up, and hold the printer until it is in the queue.</summary>
    New,

    /// <summary>Known and still open. Nothing is new except, possibly, a counter scan.</summary>
    Recheck,

    /// <summary>Nothing left to learn - do not spend a request on it.</summary>
    None,
}

/// <summary>
/// Runs a token refresh once for however many callers hit the same expired
/// token together.
///
/// They do arrive together: intake looks orders up several at a time, the shop's
/// screen calls in on its own thread, the order-events stream is rejected
/// mid-flight, and a token does not expire for one of them and not the others.
/// Unsynchronised, each ran its own refresh with the same refresh token - and a
/// backend that rotates refresh tokens honours the first and rejects the rest,
/// which can take the whole session down with it. From there every order lookup
/// fails, a failed lookup is read as "ask again later", and the agent goes on
/// heartbeating happily while picking up nothing at all: whoever is looking at
/// the screen sees an agent that says it is connected and is not printing.
///
/// Comparing the current token against the one the failed call actually used is
/// what makes waiting cheap. A caller queued behind the refresh finds the work
/// already done and simply retries, rather than spending a second one.
/// </summary>
internal sealed class SharedRefresh
{
    private readonly Func<string?> _current;
    private readonly Func<Task> _refresh;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SharedRefresh(Func<string?> current, Func<Task> refresh)
    {
        _current = current;
        _refresh = refresh;
    }

    /// <summary>Refreshes unless <paramref name="staleToken"/> has already been refreshed past.</summary>
    public async Task PastAsync(string staleToken)
    {
        // A semaphore rather than a lock: the refresh is asynchronous, and
        // holding a monitor across an await is how a deadlock gets written.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_current() != staleToken) return;
            await _refresh().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
