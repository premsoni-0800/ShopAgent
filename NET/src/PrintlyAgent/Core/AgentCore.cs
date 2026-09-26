using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
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

    /// <summary>
    /// How long to let order events pile up before sweeping.
    ///
    /// A counter scan moves the order's priority, its queue position, and the
    /// position of everything behind it - so one student arriving produces a
    /// burst of events about a single moment. Waiting briefly turns that burst
    /// into one sweep.
    /// </summary>
    private static readonly TimeSpan OrderEventCoalesceDelay = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan ScheduledJobCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrinterSyncInterval = TimeSpan.FromSeconds(120);

    // The server's column limits for a reported printer (AgentPrinter in the
    // backend's PrintAgentDtos.kt).
    private const int MaxWindowsPrinterName = 260;
    private const int MaxPrinterDisplayName = 120;

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

        // And the files of students who never came.
        //
        // The held store keeps a real copy of somebody's documents from the
        // upload until they walk in, which most of the time is the same day -
        // but some orders are simply abandoned, and on a machine meant to sit on
        // a counter for years those add up to a disk full of strangers'
        // coursework. Swept on startup for the same reason as above: it has to
        // happen whether or not this agent ever manages to sign in.
        var abandoned = SweepAbandonedHeldFiles(settings, Db, _log);
        if (abandoned > 0) _log.LogInformation("abandoned_held_orders_removed count={Count}", abandoned);
    }

    /// <summary>
    /// How long a waiting student's files are kept before the shop PC forgets
    /// them.
    ///
    /// Seven days is well past "they are coming this afternoon" and well short
    /// of a term's worth of uploads. An order swept here is not lost - it is
    /// still the shop's on the backend, and scanning for it downloads the files
    /// again, exactly as it did before any of them were held locally.
    /// </summary>
    internal const int HeldFileRetentionDays = 7;

    internal static int SweepAbandonedHeldFiles(Settings settings, Database db, ILogger log)
    {
        if (string.IsNullOrEmpty(settings.FilesDir)) return 0;

        var cutoff = DateTime.UtcNow.AddDays(-HeldFileRetentionDays)
            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        var removed = 0;
        foreach (var orderId in db.HeldOrdersOlderThan(cutoff))
        {
            try
            {
                JobPipeline.DeleteHeldOrder(db, settings.FilesDir, orderId);
                removed++;
            }
            catch (Exception exc)
            {
                // Next run. A folder still open, or a permission problem, is not
                // a reason to stop sweeping the rest.
                log.LogDebug(exc, "abandoned_held_order_delete_failed order={OrderId}", orderId);
            }
        }
        return removed;
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
        _loops.Add(Task.Run(() => ScanReleaseOnOrderEventLoopAsync(token), token));
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
        // Not disposed here, though it reads naturally enough. Everything below
        // still needs the token: JobContextFor reads _shutdown.Token, and
        // CancellationTokenSource.Token throws once the source is disposed - so
        // disposing it at this point threw straight out of the discard below,
        // into a catch that logs at Debug, which the configured minimum level
        // filters out. The result was silent and permanent: every shutdown left
        // customers' prepared documents on the counter PC, and the log said
        // nothing. The sources are disposed at the very end instead.
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

        // Last, for the reason given above: everything that ran between the
        // cancel and here needed a token off these.
        _shutdown.Dispose();
        _orderEventsShutdown?.Dispose();
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
    /// <param name="takeOver">
    /// The owner chose to make this PC the shop's print agent, revoking the PC
    /// that is online and holding it now.
    /// </param>
    public async Task<OwnerSession> AdoptOwnerSessionAsync(
        string accessToken, string refreshToken, string shopId, string? shopName, bool takeOver = false)
    {
        var session = new OwnerSession(accessToken, refreshToken, shopId, shopName);
        CredentialStore.SaveOwnerSession(session);
        return await AfterSignInAsync(session, takeOver).ConfigureAwait(false);
    }

    private async Task<OwnerSession> AfterSignInAsync(OwnerSession session, bool takeOver = false)
    {
        _ownerSession = session;
        _agentCredential = await _auth.EnsurePairedAsync(Api, session, takeOver: takeOver).ConfigureAwait(false);
        if (_loops.Count == 0)
        {
            Start();
        }
        else
        {
            RestartOrderEventsIfStopped();
            // A new pairing's printers are unknown to the server until the next
            // sweep, which could be two minutes away - and until then the
            // dashboard says "no printers" on a PC that has one plugged in.
            SyncPrintersSoon();
        }
        return session;
    }

    private readonly SemaphoreSlim _printerSyncNow = new(0, 1);
    private string? _lastSyncedPrinters;

    /// <summary>Wakes the printer sweep now rather than at its next interval.</summary>
    private void SyncPrintersSoon()
    {
        try { _printerSyncNow.Release(); }
        catch (SemaphoreFullException) { /* already due */ }
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

    /// <summary>
    /// Orders the student actually placed - matching the backend's own
    /// Order.isVisibleToShop.
    ///
    /// <para>
    /// Keyed on <c>placedAt</c>, falling back to <c>paidAt</c>. There is no
    /// online payment any more: a student uploads from wherever they are and
    /// pays the shop in cash over the counter, so <c>paidAt</c> is null for the
    /// whole of an ordinary order's life and filtering on it - which this did -
    /// hid every single order from the shop.
    /// </para>
    ///
    /// <para>
    /// The fallback is not belt and braces, it is the normal case for months.
    /// This agent is installed on a shop's PC and updates on its own schedule,
    /// so it routinely talks to a backend older than itself - one that has never
    /// heard of <c>placedAt</c> and sends only <c>paidAt</c>. Reading just the
    /// new field would empty the shop's board the moment a counter updated ahead
    /// of the server, which is precisely the wrong way round for a failure to
    /// go. Either field means the same thing here: somebody placed this order.
    /// </para>
    ///
    /// <para>
    /// The null check is still worth keeping rather than dropping the filter:
    /// an order row exists from before it is placed, and one left behind by an
    /// upload somebody walked away from is not work this shop owes anyone.
    /// </para>
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> ListOrdersAsync()
    {
        var result = await OwnerRequestAsync(s =>
            Api.OwnerGetAsync(s, $"/api/v1/shop/{s.ShopId}/orders")).ConfigureAwait(false);

        return AsItemList(result).Where(IsPlaced).ToList();
    }

    /// <summary>
    /// Whether the shop's order list says this order was actually placed.
    ///
    /// Reads whichever of the two fields the backend in front of it sends - see
    /// <see cref="ListOrdersAsync"/> for why both have to work.
    /// </summary>
    internal static bool IsPlaced(IReadOnlyDictionary<string, object?> order) =>
        (order.TryGetValue("placedAt", out var placedAt) && placedAt is not null)
        || (order.TryGetValue("paidAt", out var paidAt) && paidAt is not null);

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

    // --- files waiting for their student ------------------------------------

    /// <summary>
    /// Every order this machine is holding files for, ready for the Files
    /// screen: who it belongs to, what they owe, and where each file is.
    ///
    /// <para>
    /// Two sources, joined here rather than stored together. The files and their
    /// paths are local - only this PC knows where it put them. Everything a
    /// human reads off the card - the name, the mobile, the page count, the
    /// money - comes from the shop's own order list, fetched fresh on every
    /// call, because that is the one place any of it is true. Copying it into
    /// the local table would be copying it into a second place that goes stale
    /// and then gets read out to somebody at a counter.
    /// </para>
    ///
    /// <para>
    /// The files themselves decide what is listed - whatever is in PrintlyFiles
    /// for this shop. They used to be filtered through the order list, which
    /// now leaves out every order until its student scans: the Files page would
    /// have been empty for exactly the orders it exists to show. Another shop's
    /// files are still excluded (held_files carries the shop), and a cancelled
    /// order's are swept with the rest after HeldFileRetentionDays.
    /// </para>
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> HeldOrdersAsync()
    {
        var shopId = RequireSession().ShopId;
        var held = Db.HeldFiles(shopId).Where(row => File.Exists(row.LocalPath)).ToList();
        if (held.Count == 0) return new List<Dictionary<string, object?>>();

        // What is in PrintlyFiles is the list - read from this machine, not
        // filtered through the backend's order list. The counter's order list
        // leaves out an order until its student scans, and these are exactly
        // the orders that have not been scanned yet. The backend only adds the
        // customer's name when it will say it.
        var byUuid = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        try
        {
            foreach (var order in await ListOrdersAsync().ConfigureAwait(false))
            {
                if (order.TryGetValue("id", out var id) && id is not null) byUuid[id.ToString()!] = order;
            }
        }
        catch (Exception exc)
        {
            _log.LogDebug(exc, "held_orders_enrich_failed");
        }

        return held
            .GroupBy(row => row.OrderId, StringComparer.Ordinal)
            // Scanned at the counter: the order has moved to Home, where the
            // owner prints it, so it leaves the waiting list. The files stay in
            // PrintlyFiles until it prints - Print reads them from there.
            .Where(group => !(byUuid.TryGetValue(group.Key, out var o)
                              && o.TryGetValue("inShopPriority", out var scanned) && scanned is true))
            .Select(group =>
            {
                byUuid.TryGetValue(group.Key, out var order);
                var customer = order is not null && order.TryGetValue("customer", out var c)
                    ? c as IReadOnlyDictionary<string, object?>
                    : null;
                var reference = Db.OrderReference(group.Key);
                var first = group.First();

                return new Dictionary<string, object?>
                {
                    ["orderUuid"] = group.Key,
                    ["orderId"] = order?.GetValueOrDefault("orderId") ?? reference.OrderCode,
                    ["jobId"] = reference.JobId,
                    ["customerName"] = customer?.GetValueOrDefault("name"),
                    ["customerPhone"] = customer?.GetValueOrDefault("phone"),
                    ["totalPages"] = order?.GetValueOrDefault("totalPages"),
                    ["status"] = order?.GetValueOrDefault("status"),
                    ["folder"] = Path.GetDirectoryName(first.LocalPath),
                    ["heldAt"] = group.Min(row => row.ReceivedAt),
                    ["files"] = group
                        .OrderBy(row => row.LocalPath, StringComparer.OrdinalIgnoreCase)
                        .Select(row => new Dictionary<string, object?>
                        {
                            ["itemId"] = row.ItemId,
                            ["fileName"] = row.FileName,
                            ["localName"] = Path.GetFileName(row.LocalPath),
                            ["bytes"] = row.Bytes,
                            // Served by this agent's own web server off the local
                            // copy - see WebUiServer.
                            ["previewUrl"] = $"/local/files/{Uri.EscapeDataString(row.OrderId)}/{Uri.EscapeDataString(row.ItemId)}",
                        })
                        .ToList(),
                };
            })
            .OrderByDescending(entry => entry["heldAt"] as string, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Prints everything this shop is holding for one person, now.
    ///
    /// <para>
    /// The manual equivalent of the student scanning at the counter, and it goes
    /// down exactly the same road rather than a second one: the job is marked
    /// priority - which is persisted, so a restart mid-print does not lose it -
    /// and then queued at priority. Anything else would be a second way to start
    /// a printer, and the two would disagree about duplicate protection.
    /// </para>
    ///
    /// <para>
    /// The RECEIVED guard is load-bearing and is the same one the scan path
    /// uses. A job a worker has already taken is not re-queued, because queueing
    /// it again is how one order prints twice.
    /// </para>
    /// </summary>
    /// <summary>
    /// The owner pressed Print on this order: print it now, whether or not the
    /// student has scanned at the counter.
    ///
    /// Scan-at-counter holds every job until the student is standing there, and
    /// the owner's own Print button went through the same gate - it asked the
    /// server for a job, the job arrived, and the agent parked it waiting for a
    /// scan that the person at the printer had just made unnecessary. Nothing
    /// printed and nothing said why. The owner pressing Print is the stronger
    /// signal, so it opens the gate for this order.
    ///
    /// Covers both halves of the race with the job itself: a job already parked
    /// here is started now, and one still on its way (Print has only just asked
    /// the server for it) is let through when it arrives - see OnJobReference.
    /// </summary>
    public int ReleaseOrder(string orderUuid)
    {
        _releasedByOwner[orderUuid] = DateTime.UtcNow;
        var started = PrintOrderNow(orderUuid);
        _log.LogInformation("order_released_by_owner order={OrderId} started_now={Started}", orderUuid, started);

        // A job recorded as held in the instant between the release and the
        // look above would otherwise wait for the rotating recheck.
        _ = Task.Run(async () =>
        {
            if (!await DelayAsync(TimeSpan.FromSeconds(4), _shutdown.Token).ConfigureAwait(false)) return;
            try { PrintOrderNow(orderUuid); }
            catch (Exception exc) { _log.LogWarning(exc, "order_release_retry_failed order={OrderId}", orderUuid); }
        });
        return started;
    }

    /// <summary>How long an owner's Print keeps an order's jobs free of the counter-scan gate.</summary>
    // Long enough for the job Print asked the server for to arrive; short
    // enough that a later job for the same order - a re-issue, a second
    // attempt from another screen - still waits for its own Print.
    private static readonly TimeSpan OwnerReleaseLifetime = TimeSpan.FromMinutes(2);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _releasedByOwner =
        new(StringComparer.Ordinal);

    private bool ReleasedByOwner(string orderId)
    {
        if (!_releasedByOwner.TryGetValue(orderId, out var at)) return false;
        if (DateTime.UtcNow - at < OwnerReleaseLifetime) return true;
        _releasedByOwner.TryRemove(orderId, out _);
        return false;
    }

    public int PrintOrderNow(string orderUuid)
    {
        var credential = _agentCredential ?? throw new InvalidOperationException("This machine is not paired yet.");
        var ctx = JobContextFor(credential);

        var started = 0;
        foreach (var row in Db.WaitingJobsForOrder(credential.ShopId, orderUuid))
        {
            Db.MarkOwnerReleased(row.JobId);
            if (_printQueue.Enqueue(
                    row.JobId, row.OrderCode, priority: true,
                    () => JobPipeline.ProcessJobAsync(ctx, row.JobId, _shutdown.Token, awaitOutcome: false)))
            {
                started++;
                _log.LogInformation(
                    "print_order_now job={JobId} order={OrderId} orderCode={OrderCode}",
                    row.JobId, row.OrderId, row.OrderCode ?? "?");
            }
        }

        if (started > 0) OnJobProgress();
        return started;
    }

    /// <summary>
    /// What happened to each file of a job on this machine - the per-file list
    /// under a job that needs attention.
    /// </summary>
    public List<Dictionary<string, object?>> JobItems(string jobId) =>
        Db.ItemResults(jobId).Select(row => new Dictionary<string, object?>
        {
            ["itemId"] = row.ItemId,
            ["fileName"] = row.FileName,
            ["colorMode"] = row.ColorMode,
            ["status"] = row.Status,
            ["printerName"] = row.PrinterName,
            ["reason"] = row.Reason,
            ["updatedAt"] = row.UpdatedAt,
        }).ToList();

    /// <summary>
    /// Prints one file of a job again, on its own - the Print button beside a
    /// file that did not come out.
    ///
    /// The file is taken from this machine when it is still held (it is, for
    /// any order not fully printed) and fetched otherwise. The printer is the
    /// one asked for, or chosen exactly as the job would have chosen it. Once
    /// every file of a job that was left for a person to check is on paper, the
    /// job is closed as printed, so the order moves on without a second click.
    /// </summary>
    public async Task<Dictionary<string, object?>> PrintItemAsync(string jobId, string itemId, string? printerName)
    {
        var credential = _agentCredential ?? throw new InvalidOperationException("This computer is not connected to the shop yet.");
        var job = Db.GetJob(jobId) ?? throw new InvalidOperationException("This computer has no record of that job.");
        var ctx = JobContextFor(credential);
        var ct = _shutdown.Token;

        var detail = await Api.JobDetailAsync(credential, jobId, ct).ConfigureAwait(false);
        var item = detail.Items.FirstOrDefault(i => i.ItemId == itemId)
            ?? throw new InvalidOperationException("That file is no longer part of the order.");

        var path = Db.HeldFilesForOrder(job.OrderId)
            .FirstOrDefault(row => row.ItemId == itemId && File.Exists(row.LocalPath))?.LocalPath;
        var scratch = false;
        if (path is null)
        {
            var urls = await Api.DownloadUrlsAsync(credential, jobId, ct).ConfigureAwait(false);
            var url = urls.Items.FirstOrDefault(u => u.DocumentId == item.DocumentId)
                ?? throw new InvalidOperationException("The server did not give a download link for that file.");
            path = await Documents.DownloadDocumentAsync(
                Api.Http, url.Url, Settings.TempDir, Settings.DownloadTimeoutSeconds, ct).ConfigureAwait(false);
            scratch = true;
        }

        try
        {
            LocalPrinter? printer;
            var printers = await Task.Run(() => PrinterDiscovery.DiscoverPrinters(_log, askDrivers: true), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(printerName))
            {
                printer = printers.FirstOrDefault(p =>
                    string.Equals(p.WindowsPrinterName, printerName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{printerName} is not connected to this computer.");
            }
            else
            {
                var routing = PrinterRoutingStore.Read(Db);
                printer = PrinterSelector.SelectPrinter(item, printers, routing).Printer;
                if (printer is null)
                {
                    var reason = JobPipeline.NoPrinterReason(item, routing.For(item));
                    Db.UpsertItemResult(jobId, itemId, job.OrderId, item.FileName, item.ColorMode.ToString(),
                        JobPipeline.ITEM_FAILED, null, reason);
                    OnJobProgress();
                    return new Dictionary<string, object?> { ["ok"] = false, ["error"] = reason };
                }
            }

            _log.LogInformation("print_item job={JobId} item={ItemId} printer={Printer}", jobId, itemId, printer.WindowsPrinterName);
            var token = await Task.Run(
                () => PrintSubmission.PrintPdf(printer.WindowsPrinterName, path, JobPipeline.OptionsFor(item), _log), ct)
                .ConfigureAwait(false);
            var outcome = await SpoolerOutcomePoller.PollJobOutcomeAsync(
                printer.WindowsPrinterName, token, Settings.JobStallSeconds, log: _log, cancellation: ct).ConfigureAwait(false);

            var status = outcome.Outcome switch
            {
                PrintOutcome.COMPLETED => JobPipeline.ITEM_PRINTED,
                PrintOutcome.FAILED => JobPipeline.ITEM_FAILED,
                _ => JobPipeline.ITEM_UNKNOWN,
            };
            var note = outcome.Outcome switch
            {
                PrintOutcome.COMPLETED => null,
                PrintOutcome.FAILED => $"{printer.WindowsPrinterName} reported an error after accepting it.",
                _ => outcome.Condition is { } c
                    ? $"still waiting on {printer.WindowsPrinterName}: {c.Description()}"
                    : $"sent to {printer.WindowsPrinterName}, but it never confirmed it finished",
            };
            Db.UpsertItemResult(jobId, itemId, job.OrderId, item.FileName, item.ColorMode.ToString(),
                status, printer.WindowsPrinterName, note);

            // Every file on paper: close the job as printed, so the order moves on.
            var results = Db.ItemResults(jobId);
            var allPrinted = detail.Items.All(i =>
                results.Any(r => r.ItemId == i.ItemId && r.Status == JobPipeline.ITEM_PRINTED));
            if (allPrinted && job.State == JobPipeline.UNKNOWN)
            {
                try
                {
                    await ResolvePrintJobAsync(jobId, success: true, note: "Every file printed from the per-file list.")
                        .ConfigureAwait(false);
                    JobPipeline.ReleaseHeldFiles(ctx, job.OrderId);
                    _log.LogInformation("print_job_completed_by_items job={JobId}", jobId);
                }
                catch (Exception exc)
                {
                    _log.LogWarning(exc, "print_job_item_resolve_failed job={JobId}", jobId);
                }
            }

            OnJobProgress();
            return new Dictionary<string, object?>
            {
                ["ok"] = status == JobPipeline.ITEM_PRINTED,
                ["status"] = status,
                ["printerName"] = printer.WindowsPrinterName,
                ["error"] = note,
                ["jobDone"] = allPrinted,
            };
        }
        catch (PrintSubmissionError exc)
        {
            Db.UpsertItemResult(jobId, itemId, job.OrderId, item.FileName, item.ColorMode.ToString(),
                JobPipeline.ITEM_FAILED, printerName, exc.Message);
            OnJobProgress();
            return new Dictionary<string, object?> { ["ok"] = false, ["error"] = exc.Message };
        }
        finally
        {
            if (scratch)
            {
                try { File.Delete(path); } catch (Exception) { /* swept on next start */ }
            }
        }
    }

    /// <summary>The printers on this PC, as the routing dropdowns need them.</summary>
    public async Task<List<Dictionary<string, object?>>> ListLocalPrintersAsync()
    {
        // askDrivers: this is the Printers page opening, and a printer plugged
        // in a minute ago has to be on it - not two minutes from now when the
        // cached list expires. It costs a fraction of a second, off the print
        // path.
        var printers = await Task.Run(() => PrinterDiscovery.DiscoverPrinters(_log, askDrivers: true)).ConfigureAwait(false);
        return printers.Select(p => new Dictionary<string, object?>
        {
            ["windowsPrinterName"] = p.WindowsPrinterName,
            // Writes a file or sends a fax rather than putting paper in a tray.
            // The page lists real printers only; the selector already passes
            // these over whenever a real one exists.
            ["virtual"] = PrintToFile.IsPrintToFileDriver(p.WindowsPrinterName),
            ["displayName"] = p.DisplayName,
            ["isSystemDefault"] = p.IsSystemDefault,
            ["status"] = p.Status.ToString(),
            ["colorCapable"] = p.ColorCapable,
            ["duplexCapable"] = p.DuplexCapable,
        }).ToList();
    }

    public PrinterRouting GetPrinterRouting() => PrinterRoutingStore.Read(Db);

    public void SetPrinterRouting(string? colour, string? blackAndWhite)
    {
        PrinterRoutingStore.Write(Db, new PrinterRouting(colour, blackAndWhite));
        _log.LogInformation(
            "printer_routing_set colour={Colour} bw={Bw}",
            colour ?? "(auto)", blackAndWhite ?? "(auto)");
    }

    /// <summary>
    /// The local copy of one held file, for the preview.
    ///
    /// Resolved through the database rather than by building a path out of what
    /// the page asked for: the page is not the authority on where this machine
    /// keeps files, and a path assembled from its input is a path it can steer.
    /// Null for anything this shop is not actually holding.
    /// </summary>
    public string? HeldFilePath(string orderUuid, string itemId)
    {
        var shopId = _agentCredential?.ShopId;
        if (shopId is null) return null;

        return Db.HeldFilesForOrder(orderUuid)
            .Where(row => string.Equals(row.ItemId, itemId, StringComparison.Ordinal))
            .Where(row => string.Equals(row.ShopId, shopId, StringComparison.Ordinal))
            .Select(row => row.LocalPath)
            .FirstOrDefault(File.Exists);
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
                    // The server validates the whole list at once - one name over
                    // its limits refuses every printer in the sweep, and the shop
                    // sees none. A long display name is shortened; a Windows name
                    // longer than the server can store cannot be printed to by
                    // that name anyway, so it is left out and said so.
                    var reportable = printers.Where(p =>
                    {
                        if (p.WindowsPrinterName.Length is > 0 and <= MaxWindowsPrinterName) return true;
                        _log.LogWarning("printer_not_reported name_length={Length} name={Name}",
                            p.WindowsPrinterName.Length, p.WindowsPrinterName);
                        return false;
                    }).ToList();

                    var request = new PrinterSyncRequest(reportable.Select(p => new AgentPrinter(
                        p.WindowsPrinterName,
                        p.DisplayName.Length <= MaxPrinterDisplayName
                            ? p.DisplayName
                            : p.DisplayName[..MaxPrinterDisplayName],
                        p.ColorCapable,
                        p.DuplexCapable,
                        p.Sizes.ToList(),
                        p.Status)).ToList());

                    await Api.SyncPrintersAsync(credential, request, ct).ConfigureAwait(false);
                    // Once per change, not per sweep: "which printers did the
                    // server get" is the first question when a shop says none
                    // are showing.
                    var synced = string.Join(", ", request.Printers.Select(p => p.WindowsPrinterName));
                    if (synced != _lastSyncedPrinters)
                    {
                        _log.LogInformation("printers_synced count={Count} names=[{Names}]", request.Printers.Count, synced);
                        _lastSyncedPrinters = synced;
                    }

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

            try
            {
                await _printerSyncNow.WaitAsync(PrinterSyncInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
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
    /// <summary>
    /// Something in the shop's orders moved.
    ///
    /// Two readers, and the second is the one that prints. The dashboard wants
    /// to redraw its list; the sweep wants to know whether one of those changes
    /// was a student scanning at the counter, because under scan-at-counter
    /// that is the event that starts a printer.
    /// </summary>
    private void OnOrdersChanged()
    {
        OnEmit("orders", new Dictionary<string, object?>());
        _orderChangeSignal.Writer.TryWrite(true);
    }

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
            // An order the owner has just pressed Print on is printed whatever
            // the answer, so asking the server about its slot first was a round
            // trip spent between the click and the paper.
            var lookup = ReleasedByOwner(orderId)
                ? ScheduleLookup.Unavailable.Instance
                : await OrderScheduleLookupAsync(orderId, ct).ConfigureAwait(false);

            // Read off the same answer the schedule came from. The order lookup
            // is an HTTP call this path already makes, and OrderResponse has
            // carried inShopPriority all along - so knowing that a student is
            // standing at the counter costs nothing extra.
            // The owner pressed Print on it - see ReleaseOrder.
            var released = ReleasedByOwner(orderId);
            var priority = released || lookup is ScheduleLookup.Known { Priority: true };

            // The SSE push carries only a job id and an order uuid, so for most
            // jobs orderCode arrives null and the queue fell back to showing the
            // job's uuid - which is what the counter then saw on screen in place
            // of the order number. The lookup above has just fetched the order,
            // so the real code is already in hand and costs nothing.
            orderCode ??= (lookup as ScheduleLookup.Known)?.OrderCode;

            // Nothing prints unless the shop owner pressed Print on the order.
            //
            // A job used to go straight to the printer whenever the backend had
            // not marked it held - which is every job on an auto-print shop -
            // and a counter scan, or a scheduled time arriving, released a held
            // one on its own. The counter PC then printed orders nobody at the
            // counter had asked for. The owner's Print (ReleaseOrder, or a file
            // on the Print errors page) is now the only way into the queue;
            // everything else is recorded and its files downloaded, and waits.
            var plan = released ? new SchedulePlan.PrintAt(null) : Scheduling.PlanFor(lookup);
            if (!released && plan is not SchedulePlan.Hold) plan = new SchedulePlan.AwaitCounterScan();

            switch (plan)
            {
                case SchedulePlan.PrintAt printAt:
                    JobPipeline.HandleJobReference(
                        JobContextFor(credential), _printQueue, jobId, orderId, orderCode, printAt.At, priority);
                    break;

                // Paid, downloaded-able, and going nowhere near a printer: the
                // student has not scanned at the counter yet. Recorded so the
                // rotating recheck watches it - see JobPipeline.HoldForCounterScan
                // for why recording and queueing are different things - and
                // released by the scan, through the priority branch of
                // HandleJobReference.
                case SchedulePlan.AwaitCounterScan:
                    JobPipeline.HoldForCounterScan(
                        JobContextFor(credential), jobId, orderId, orderCode);
                    break;

                // Deliberately records nothing. Recording the job means deciding
                // what to do with it, and that is the one thing this path could
                // not find out - so it is left to the ten-second reconciliation
                // poll, which re-lists every outstanding job and brings this one
                // back here to be asked again.
                case SchedulePlan.Hold:
                    _log.LogWarning("scheduled_lookup_deferred job={JobId} order={OrderId}", jobId, orderId);
                    break;
            }
        }, holdsPrinting: work == ReferenceWork.New);
    }

    /// <summary>
    /// Rings when the shop's order stream reports that something changed.
    ///
    /// Capacity one, dropping the oldest: the question it triggers is "has
    /// anyone scanned?", which is the same question however many events
    /// prompted it, so a backlog of them would only ask it repeatedly.
    /// </summary>
    /// <summary>
    /// When this process started, as a round-trip UTC instant.
    ///
    /// The watermark that lets the stranded-at-the-printer sweep tell a
    /// previous run's jobs from this one's - see
    /// JobPipeline.ResolveJobsStrandedAtThePrinterAsync.
    /// </summary>
    private readonly string _startedAtIso =
        DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private readonly Channel<bool> _orderChangeSignal =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private JobContext JobContextFor(AgentCredential credential) => new(
        Api,
        Db,
        Settings.TempDir,
        Settings.FilesDir,
        Settings.MaxRetryAttempts,
        Settings.DownloadTimeoutSeconds,
        Settings.JobStallSeconds,
        credential,
        OnJobProgress,
        _loggerFactory.CreateLogger("PrintlyAgent.Jobs.JobPipeline"),
        // The pipeline's own work is cancelled with the agent, so a job still
        // downloading when the app closes stops rather than finishing into a
        // database that is already shut.
        _shutdown.Token,
        RequireOwnerRelease: true);

    private async Task ScheduledJobsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var credential = _agentCredential;
            if (credential is not null)
            {
                try
                {
                    JobPipeline.ProcessDueScheduledJobs(JobContextFor(credential), _printQueue, ReleasedByOwner);
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
            JobPipeline.ResumeInterruptedJobs(context, _printQueue, ownerStartedOnly: true);
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
            await JobPipeline.ResolveJobsStrandedAtThePrinterAsync(context, _startedAtIso, ct).ConfigureAwait(false);
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
    /// Turns the shop's order stream into the thing that starts a printer.
    ///
    /// The rotating recheck below is the only other way a scan is ever noticed,
    /// and it walks six held orders every ten seconds. That was sized for a
    /// world where holding an order was rare - an order arrived, printed, and
    /// left the pool. Under scan-at-counter every paid order sits in that pool
    /// until its student walks in, so at a shop with thirty orders open the
    /// rotation needs the best part of a minute to come round to the one person
    /// actually standing at the counter. "Scan and it prints" cannot be built
    /// on that.
    ///
    /// The stream already knows. A scan changes the order, and the shop's own
    /// orders stream reports it - the agent has been receiving those events all
    /// along and using them only to redraw a list.
    ///
    /// <para>
    /// Strictly an accelerator. Everything it finds is released through the
    /// ordinary path, and the rotation stays exactly as it was, so a backend
    /// that turns out not to emit on a priority grant loses nothing: the scan
    /// is noticed a little later, as it is today.
    /// </para>
    /// </summary>
    private async Task ScanReleaseOnOrderEventLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _orderChangeSignal.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (!await DelayAsync(OrderEventCoalesceDelay, ct).ConfigureAwait(false)) return;
            while (_orderChangeSignal.Reader.TryRead(out _)) { }

            try
            {
                await ReleaseScannedOrdersAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception exc)
            {
                // The rotation still covers this, so a failure here costs
                // latency rather than correctness.
                _log.LogDebug(exc, "scan_release_sweep_failed");
            }
        }
    }

    /// <summary>
    /// Asks, in one request, which of the held orders have been scanned.
    ///
    /// One request is the whole point. Asking per order is what the rotation
    /// does and why it has to be rationed; the shop's orders list carries
    /// inShopPriority for every order at once, so the cost of this is flat
    /// whether one order is held or fifty.
    ///
    /// What comes back is used only to decide *which* orders are worth asking
    /// about properly. The release itself goes through OnJobReference exactly
    /// as the rotation's does - same lookup, same duplicate guard, same
    /// authority - so the list is a filter and never a source of truth about
    /// whether something may print.
    /// </summary>
    private async Task ReleaseScannedOrdersAsync(CancellationToken ct)
    {
        var credential = _agentCredential;
        if (credential is null) return;

        // Nothing held means nothing a scan could release, and no reason to
        // spend a request finding that out. This is the common case on a quiet
        // counter, where order events still arrive for ordinary traffic.
        var held = Db.HeldForCounterScan(credential.ShopId);
        if (held.Count == 0) return;

        var orders = await ListOrdersAsync().ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        var scanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in orders)
        {
            if (order.TryGetValue("inShopPriority", out var flag) && flag is true &&
                order.TryGetValue("id", out var id) && id is string orderId &&
                !string.IsNullOrEmpty(orderId))
            {
                scanned.Add(orderId);
            }
        }

        if (scanned.Count == 0) return;

        foreach (var row in held)
        {
            if (!scanned.Contains(row.OrderId)) continue;
            _log.LogInformation(
                "counter_scan_seen_on_order_event job={JobId} order={OrderCode}",
                row.JobId, row.OrderCode ?? "?");
            OnJobReference(row.JobId, row.OrderId, row.OrderCode, rechecking: true);
        }
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
