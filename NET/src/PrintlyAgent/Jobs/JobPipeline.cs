using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Credentials;
using PrintlyAgent.Db;
using PrintlyAgent.Models;
using PrintlyAgent.Net;
using PrintlyAgent.Printers;
using PrintlyAgent.Printing;

namespace PrintlyAgent.Jobs;

/// <summary>
/// Port of Kotlin's <c>class NoCompatiblePrinterException : RuntimeException</c>.
/// Named for what it is rather than reshaped to .NET conventions, so the two
/// ports read the same at the call site that catches it.
/// </summary>
public sealed class NoCompatiblePrinterException : Exception
{
    public NoCompatiblePrinterException(string message) : base(message)
    {
    }
}

/// <summary>
/// Everything one run of the pipeline needs. Port of the Kotlin
/// <c>data class JobContext</c>.
///
/// <para>
/// Porting note: two members have no Kotlin counterpart and both exist because
/// the coroutine machinery that supplied them implicitly is gone.
/// <c>Log</c> stands in for the module-level <c>java.util.logging.Logger</c> -
/// this port injects ILogger everywhere else (see PrintQueue, JobDispatcher) and
/// a static logger here would be the one exception. <c>Cancellation</c> stands in
/// for the CoroutineScope's Job: in Kotlin every <c>suspend</c> frame carries the
/// scope's cancellation with it for free, so cancelling the scope on shutdown
/// reached every in-flight job. .NET has no ambient equivalent, so the token is
/// carried explicitly - and it has to be carried, because "a job cancelled on
/// shutdown is never reported as a print failure" is a rule this pipeline exists
/// to keep.
/// </para>
/// </summary>
public sealed record JobContext(
    PrintlyApiClient Api,
    Database Db,
    string TempDir,
    /// <summary>
    /// Where a waiting student's files are kept until they arrive - see
    /// Settings.FilesDir. Empty in tests that never prefetch, which is why
    /// every use checks it first.
    /// </summary>
    string FilesDir,
    int MaxRetryAttempts,
    long DownloadTimeoutSeconds,
    double JobStallSeconds,
    AgentCredential Credential,
    /// <summary>Best-effort UI push - fired on every local state change. No-op in tests/contexts that don't care about the UI.</summary>
    Action? OnEvent = null,
    ILogger? Log = null,
    CancellationToken Cancellation = default)
{
    internal ILogger Logger => Log ?? NullLogger.Instance;

    internal void RaiseEvent() => OnEvent?.Invoke();
}

/// <summary>
/// The local job pipeline: one handler shared by SSE pushes and reconciliation
/// polls - direct port of jobs/JobPipeline.kt, itself a port of the Python
/// agent's <c>jobs.py</c>.
///
/// State machine (never a shortcut from RECEIVED straight to COMPLETED):
///
/// <code>
///   RECEIVED -> VALIDATING -> DOWNLOADING -> DOWNLOADED -> SUBMITTED -> PRINTING -> COMPLETED
///                   |              | (bounded retry)                           -> FAILED
///                   v              v                                           -> UNKNOWN
///                FAILED         FAILED
///   any non-terminal state -> CANCELLED (backend/reconciliation says the job is gone)
/// </code>
///
/// Retries are bounded and apply only to DOWNLOADING (a transient network
/// failure) - a corrupt PDF, an incompatible printer, or a spooler error is
/// permanent and is never retried.
///
/// PRINTING's own three-way branch is decided by <see cref="SpoolerOutcomePoller"/>
/// watching the spooler after the blocking submit call returns - COMPLETED and
/// FAILED are the spooler's own definitive answer; UNKNOWN means the answer never
/// came before the agent gave up watching. UNKNOWN is terminal *locally* (never
/// retried) even though it is not one of the backend's own terminal statuses:
/// only a human at the shop, via the backend's <c>PrintJobResolutionService</c>,
/// can close it out. Reprinting a job whose outcome is merely unknown risks the
/// one thing this whole pipeline exists to prevent - printing the same document
/// twice.
///
/// <para>
/// Porting note: Kotlin's top-level functions and <c>const val</c>s become static
/// members of one class, which is as close as C# gets. Everything else about the
/// shape is unchanged; <c>suspend</c> becomes <c>async Task</c> and the
/// <c>withContext(Dispatchers.IO)</c> wrappers become either a natively async
/// call or a Task.Run, whichever the ported dependency offers.
/// </para>
/// </summary>
public static class JobPipeline
{
    public const string RECEIVED = "RECEIVED";
    public const string VALIDATING = "VALIDATING";
    public const string DOWNLOADING = "DOWNLOADING";
    public const string DOWNLOADED = "DOWNLOADED";

    /// <summary>
    /// The document is being handed to a printer driver, and may already be
    /// coming out of it.
    ///
    /// <para>
    /// This exists to mark where "nothing has printed yet" stops being true.
    /// Without it a job read DOWNLOADED for the whole time it was printing - the
    /// submit call blocks until the driver has finished the entire document, and
    /// SUBMITTED was not reached until afterwards - so a job halfway through a
    /// 300-page order looked, to <see cref="Database.ResumableJobs"/>, exactly
    /// like one that had downloaded and never started. A restart replayed it, and
    /// the shop printed the whole thing again on top of what was already in the
    /// tray.
    /// </para>
    /// </summary>
    public const string SUBMITTING = "SUBMITTING";

    public const string SUBMITTED = "SUBMITTED";
    public const string PRINTING = "PRINTING";
    public const string COMPLETED = "COMPLETED";
    public const string FAILED = "FAILED";
    public const string CANCELLED = "CANCELLED";

    /// <summary>Terminal locally even though the backend's own PrintJobStatus has a matching PRINT_UNKNOWN to resolve it from.</summary>
    public const string UNKNOWN = "UNKNOWN";

    public static readonly IReadOnlySet<string> TERMINAL =
        new HashSet<string>(StringComparer.Ordinal) { COMPLETED, FAILED, CANCELLED, UNKNOWN };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedNext =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [RECEIVED] = Set(VALIDATING, CANCELLED),
            [VALIDATING] = Set(DOWNLOADING, FAILED, CANCELLED),
            // DOWNLOADING->DOWNLOADING is a bounded retry
            [DOWNLOADING] = Set(DOWNLOADED, DOWNLOADING, FAILED, CANCELLED),
            // No edge to SUBMITTED: everything reaches it through SUBMITTING, so
            // the state machine itself enforces that a job is never recorded as
            // printing without first being recorded as about to.
            [DOWNLOADED] = Set(SUBMITTING, FAILED, CANCELLED),
            [SUBMITTING] = Set(SUBMITTED, FAILED, CANCELLED, UNKNOWN),
            [SUBMITTED] = Set(PRINTING, FAILED),
            [PRINTING] = Set(COMPLETED, FAILED, UNKNOWN),
        };

    private static IReadOnlySet<string> Set(params string[] states) =>
        new HashSet<string>(states, StringComparer.Ordinal);

    public static bool CanTransition(string current, string next)
    {
        // idempotent on repeat, same principle as the backend's own transitions
        if (string.Equals(current, next, StringComparison.Ordinal)) return true;
        return AllowedNext.TryGetValue(current, out var allowed) && allowed.Contains(next);
    }

    /// <summary>
    /// Entry point for both the SSE push and the reconciliation poll.
    /// <see cref="Database.InsertJobReference"/> is the whole of the
    /// duplicate-print protection: a job id already known - whether COMPLETED,
    /// FAILED, or simply still in flight from an earlier delivery of the same
    /// reference - is dropped here before any printer is ever touched again.
    /// </summary>
    public static bool RegisterJobReference(
        JobContext ctx,
        string jobId,
        string orderId,
        string? orderCode,
        string? scheduledPrintAt = null,
        bool priority = false)
    {
        var isNew = ctx.Db.InsertJobReference(
            jobId, orderId, orderCode, scheduledPrintAt, ctx.Credential.ShopId, priority);
        if (!isNew)
        {
            ctx.Logger.LogDebug("duplicate_job_reference_ignored job={JobId}", jobId);
            return false;
        }
        ctx.RaiseEvent();

        // Kotlin compares the two ISO strings with `>`, which is String.compareTo
        // - an ordinal comparison. C#'s String.Compare is culture-sensitive by
        // default, and a culture-aware comparison of timestamps is a bug waiting
        // for a machine with the wrong locale, so the ordinal one is spelled out.
        if (scheduledPrintAt != null && string.CompareOrdinal(scheduledPrintAt, NowIso()) > 0)
        {
            ctx.Logger.LogInformation(
                "print_job_scheduled job={JobId} order={OrderId} scheduledPrintAt={ScheduledPrintAt}",
                jobId, orderId, scheduledPrintAt);
            return false;
        }

        ctx.Logger.LogInformation("print_job_received job={JobId} order={OrderId}", jobId, orderId);
        return true;
    }

    /// <summary>
    /// Records the reference and, if it is due, puts it in the print queue.
    ///
    /// <para>
    /// The recording half and the printing half are deliberately separate calls.
    /// They used to be one, run inside the print slot, so an order could not even
    /// be written down until the previous one had finished printing - which is
    /// why the queue was never ordered: there was never more than one thing in it
    /// to sort. Intake now runs ahead of printing, and <see cref="PrintQueue"/>
    /// decides what prints next by order number.
    /// </para>
    ///
    /// <para>
    /// Porting note: <c>suspend</c> in Kotlin, but nothing it calls suspends -
    /// <c>enqueue</c> hands the work over and returns. In C# an <c>async</c>
    /// method with no <c>await</c> is a compiler warning, and this build treats
    /// warnings as errors, so it is plainly synchronous. The work itself still
    /// runs on a queue worker exactly as before.
    /// </para>
    /// </summary>
    public static void HandleJobReference(
        JobContext ctx,
        PrintQueue queue,
        string jobId,
        string orderId,
        string? orderCode,
        string? scheduledPrintAt = null,
        bool priority = false)
    {
        if (RegisterJobReference(ctx, jobId, orderId, orderCode, scheduledPrintAt, priority))
        {
            queue.Enqueue(jobId, orderCode, priority, () => ProcessJobAsync(ctx, jobId, ctx.Cancellation, awaitOutcome: false));
            return;
        }

        if (!priority) return;

        // Already known, and the student has since scanned.
        //
        // This is the ordinary shape of in-shop priority, not an edge case:
        // somebody orders ahead and then walks in, so the grant almost always
        // arrives after the reference did. The insert above is a no-op by then -
        // that is what stops the document printing twice - so without this the
        // scan would be silently dropped.
        //
        // Written down first, so that a job still held back for a later slot, or
        // one a restart has yet to resume, keeps a grant it is too early to act
        // on. Only then moved in the queue, if it is in one.
        var row = ctx.Db.GetJob(jobId);
        if (row is null || row.Priority || TERMINAL.Contains(row.State)) return;

        ctx.Db.MarkPriority(jobId);
        ctx.RaiseEvent();
        if (queue.Promote(jobId))
        {
            ctx.Logger.LogInformation(
                "in_shop_priority_granted job={JobId} order={OrderId}", jobId, orderId);
            return;
        }

        // Not in the queue to be promoted. Under scan-at-counter that is the
        // ordinary case, not a failure: the job was recorded by
        // HoldForCounterScan and deliberately never queued, because this scan is
        // the event that was being waited for. So it goes in now.
        //
        // Guarded on RECEIVED, and that guard is load-bearing. Promote also
        // returns false for a job a worker has already taken, and re-queueing
        // one of those would print the customer's document a second time. A job
        // past RECEIVED has been taken; one still at RECEIVED has not. Enqueue
        // is idempotent on top of that - a job genuinely still waiting in the
        // queue is dropped by its own id guard - so the two together leave no
        // reading of this that prints twice.
        if (!string.Equals(row.State, RECEIVED, StringComparison.Ordinal)) return;

        if (queue.Enqueue(jobId, orderCode ?? row.OrderCode, true, () => ProcessJobAsync(ctx, jobId, ctx.Cancellation, awaitOutcome: false)))
        {
            ctx.Logger.LogInformation(
                "counter_scan_released_job job={JobId} order={OrderId}", jobId, orderId);
        }
    }

    /// <summary>
    /// Writes the job down and prints nothing, because the student has not
    /// scanned yet.
    ///
    /// <para>
    /// The recording is the entire job of this method, and it is not
    /// bookkeeping. A recorded row is what <see cref="Database.PriorityCandidates"/>
    /// returns, which is what the agent's rotating recheck asks the backend
    /// about, which is how the scan is ever noticed. A job left unrecorded would
    /// instead be re-looked-up by the reconciliation poll every ten seconds for
    /// as long as the student took to walk over, and would hold a printer slot
    /// each time it did.
    /// </para>
    ///
    /// <para>
    /// It deliberately does not go into the print queue. The queue is the list
    /// of things that are going to print, and the shop's counter screen reads it
    /// as exactly that; putting a job there that nobody has scanned for would
    /// promise a printout that is not coming.
    /// </para>
    /// </summary>
    public static void HoldForCounterScan(
        JobContext ctx,
        string jobId,
        string orderId,
        string? orderCode)
    {
        var isNew = ctx.Db.InsertJobReference(
            jobId, orderId, orderCode, null, ctx.Credential.ShopId, priority: false);

        if (!isNew)
        {
            ctx.Logger.LogDebug("awaiting_counter_scan_still job={JobId}", jobId);
            // Held by an earlier run, or by a version that never reported it:
            // the files are here, so the timeline should say so.
            if (ctx.Db.HasHeldFiles(orderId)) _ = Task.Run(() => ReportCachedAsync(ctx, jobId, orderId, ctx.Cancellation));
            return;
        }

        ctx.RaiseEvent();
        ctx.Logger.LogInformation(
            "print_job_awaiting_counter_scan job={JobId} order={OrderId} orderCode={OrderCode}",
            jobId, orderId, orderCode ?? "?");

        // Fetch the files now, while nobody is waiting for them.
        //
        // Not awaited, and inside the `isNew` branch: this runs once per order,
        // on the one call that recorded it. Awaiting would hold up the dispatcher
        // that called us for the length of a download, and there is nothing to
        // wait for - the order is not going to print until somebody scans, which
        // is the whole reason there is time to do this at all.
        _ = Task.Run(() => PrefetchOrderFilesAsync(ctx, jobId, orderId, ctx.Cancellation));
    }

    /// <summary>
    /// Prints every job whose held-back time has now arrived - see
    /// <see cref="HandleJobReference"/>'s <c>scheduledPrintAt</c>.
    ///
    /// <para>
    /// Dispatches rather than awaiting: several scheduled slots commonly come due
    /// in the same tick, and printing them one after another would make the last
    /// one late by however long all the others took.
    /// </para>
    /// </summary>
    public static void ProcessDueScheduledJobs(JobContext ctx, PrintQueue queue)
    {
        foreach (var row in ctx.Db.DueScheduledJobs(NowIso(), ctx.Credential.ShopId))
        {
            // row.Priority, not false. MarkPriority persists a counter scan
            // precisely so it survives a restart, and hard-coding false here
            // threw that away at the one moment it mattered.
            if (queue.Enqueue(row.JobId, row.OrderCode, row.Priority, () => ProcessJobAsync(ctx, row.JobId, ctx.Cancellation, awaitOutcome: false)))
            {
                ctx.Logger.LogInformation(
                    "scheduled_print_job_due job={JobId} order={OrderId}", row.JobId, row.OrderId);
            }
        }
    }

    /// <summary>
    /// Re-runs the jobs a restart left stranded partway through.
    ///
    /// <para>
    /// Without this they are stuck for good: the local row exists, so
    /// <see cref="Database.InsertJobReference"/> drops every later redelivery of
    /// that id as a duplicate, and nothing else ever looks at it again. See
    /// <see cref="Database.ResumableJobs"/> for why only the pre-printing states
    /// qualify.
    /// </para>
    /// </summary>
    public static void ResumeInterruptedJobs(JobContext ctx, PrintQueue queue)
    {
        foreach (var row in ctx.Db.ResumableJobs(ctx.Credential.ShopId))
        {
            // Rewind to the start rather than continuing from where it stopped.
            // [ProcessJobAsync] always begins by moving to VALIDATING, and the
            // state machine has no edge back to it from DOWNLOADING or DOWNLOADED
            // - so resuming in place would trip the illegal-transition guard and
            // mark a job FAILED that has not even been attempted. Replaying from
            // the top is safe precisely because none of these states has reached a
            // printer; the download is simply done again.
            //
            // The rewind belongs *inside* the submitted work, not before it. A job
            // this sweep finds mid-flight is dropped by the queue as a duplicate -
            // but rewinding first happened anyway, pulling the running job's state
            // back to RECEIVED underneath it. That job then reached DOWNLOADING
            // from RECEIVED, tripped the guard, and was retried three times and
            // failed as "download failed after 3 attempts" - a job that downloaded
            // perfectly well and was never given the chance to print. Startup is
            // exactly when both happen at once: this sweep runs while the stream
            // is delivering today's jobs.
            // Carries the grant across the restart. Queued as an ordinary entry
            // instead, a scanned student's job sorted back behind the whole
            // backlog - and unrecoverably so, because a priority=1 row is
            // excluded from both PriorityCandidates and ReferenceWorkFor, so
            // nothing would ever re-grant it. They stood at the counter while
            // the queue printed everyone else.
            var accepted = queue.Enqueue(row.JobId, row.OrderCode, row.Priority, () =>
            {
                ctx.Db.UpdateJobState(row.JobId, RECEIVED);
                return ProcessJobAsync(ctx, row.JobId, ctx.Cancellation, awaitOutcome: false);
            });
            if (accepted)
            {
                ctx.Logger.LogInformation(
                    "resuming_interrupted_job job={JobId} state={State}", row.JobId, row.State);
            }
        }
    }

    /// <summary>
    /// Closes out jobs a previous run abandoned after the printer had the
    /// document, by handing each one to a person.
    ///
    /// A crash, a kill, or the counter PC losing power between "submitted" and
    /// "confirmed" leaves a job that nothing will ever move again. It is not
    /// FAILED, so the errors list never showed it; it is not COMPLETED, so the
    /// agent screen kept listing it as active and "Sent to the printer"
    /// indefinitely - from an agent that had restarted and forgotten it. The
    /// shop was left reading a live-looking row about a job nobody was doing.
    ///
    /// Marked UNKNOWN rather than completed or failed, which is the same rule
    /// the spooler's own UNKNOWN outcome follows: calling it done could quietly
    /// swallow an order that never printed, and calling it failed invites a
    /// reprint on top of pages that may already be in the tray. UNKNOWN is the
    /// state that means "someone has to look", and it is the one that reaches
    /// the errors list with a "Did it print?" on it.
    /// </summary>
    /// <param name="strandedBefore">
    /// Only jobs last touched before this instant are a previous run's. Without
    /// it the sweep could not tell them from this run's: it fires ten seconds
    /// after start, and under scan-at-counter a student who scanned before the
    /// agent came up has their job released the moment the stream connects - so
    /// a document physically coming out of the printer was being marked UNKNOWN
    /// and reported to the backend as "did it print?". The real outcome then
    /// could not be recorded (COMPLETED is not reachable from UNKNOWN), leaving
    /// a perfectly good print on the errors list inviting a reprint.
    /// </param>
    public static async Task ResolveJobsStrandedAtThePrinterAsync(
        JobContext ctx, string strandedBefore, CancellationToken cancellation)
    {
        foreach (var row in ctx.Db.JobsStrandedAtThePrinter(ctx.Credential.ShopId, strandedBefore))
        {
            ctx.Logger.LogWarning(
                "job_stranded_at_printer job={JobId} state={State} - asking the shop to confirm",
                row.JobId, row.State);
            await MarkUnknownAsync(
                ctx, row.JobId,
                "the agent restarted after this was sent to the printer, so nobody can say whether it came out",
                cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Kotlin's <c>Instant.now().toString()</c>. A UTC DateTime formatted "O"
    /// gives the same shape - ISO-8601, fractional seconds, trailing <c>Z</c> -
    /// which matters because these strings are compared and sorted as text, both
    /// here and in <see cref="Database.DueScheduledJobs"/>'s SQL. A local
    /// DateTimeOffset would render "+05:30" instead of "Z" and quietly break both.
    /// </summary>
    private static string NowIso() => DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <param name="awaitOutcome">
    /// Wait for the spooler's final answer before returning. The print queue
    /// passes false, so its slot frees the moment every file is with Windows;
    /// tests pass the default, to read the outcome when this returns.
    /// </param>
    public static async Task ProcessJobAsync(
        JobContext ctx, string jobId, CancellationToken cancellation = default, bool awaitOutcome = true)
    {
        var row = ctx.Db.GetJob(jobId);
        if (row is null || TERMINAL.Contains(row.State)) return;

        // Whether anything has been handed to a printer driver yet. Past that
        // point no error may be reported as FAILED, however it arrives: the shop
        // reads FAILED as "print it again", and the pages are already out.
        var reachedPrinter = false;

        try
        {
            // Claimed and fetched, possibly some time ago: PrepareAsync is
            // memoised, so if the queue ran it ahead while the previous job was
            // still on the printer, this returns the finished work rather than
            // doing it again. That is the whole point - the gap between one
            // print ending and the next starting is this phase, and on a run of
            // small orders it was most of each order's life.
            var preparing = System.Diagnostics.Stopwatch.StartNew();
            var prepared = await PrepareAsync(ctx, jobId, cancellation).ConfigureAwait(false);
            if (prepared is null) return;
            var preparedMs = preparing.ElapsedMilliseconds;

            var (detail, downloaded) = prepared.Value;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            PrintAttempt attempt;
            try
            {
                Transition(ctx, jobId, DOWNLOADED);
                ReportProgress(ctx, jobId, PrintJobProgressStage.PRINTING, cancellation);
                var printers = await Task.Run(() => PrinterDiscovery.DiscoverPrinters(ctx.Logger), cancellation)
                    .ConfigureAwait(false);
                attempt = await Task.Run(
                    () => SelectAndPrint(ctx, detail, downloaded, printers, printerName =>
                    {
                        // Fired once, immediately before the first page is handed
                        // to a driver. Everything after this point has to assume
                        // paper may already be moving.
                        reachedPrinter = true;
                        Transition(ctx, jobId, SUBMITTING, printerWindowsName: printerName);
                    }),
                    cancellation).ConfigureAwait(false);
            }
            finally
            {
                DeleteDownloads(ctx, downloaded);
                ForgetPreparation(jobId);
            }

            ctx.Logger.LogInformation(
                "print_job_timing job={JobId} prepared_ms={Prepared} spooled_ms={Spooled} files={Files} sent={Sent}",
                jobId, preparedMs, timer.ElapsedMilliseconds, detail.Items.Count, attempt.Submitted.Count);

            foreach (var failure in attempt.Failed)
            {
                RecordItem(ctx, detail, failure.Item, failure.MayHavePrinted ? ITEM_UNKNOWN : ITEM_FAILED,
                    failure.PrinterName, failure.Reason);
            }

            // Nothing reached a printer: the order failed as a whole, which the
            // shop reads as "print it again" - correct, since no paper exists.
            if (attempt.Submitted.Count == 0 && attempt.Failed.All(f => !f.MayHavePrinted))
            {
                var allUnroutable = attempt.Failed.All(f => f.PrinterName is null);
                await FailAsync(
                    ctx, jobId, Summary(detail, attempt.Failed.Select(f => (f.Item, f.Reason)).ToList(), printed: 0),
                    allUnroutable ? PrintJobFailureReason.PRINTER_INCOMPATIBLE : PrintJobFailureReason.PRINTER_ERROR,
                    cancellation).ConfigureAwait(false);
                return;
            }

            if (attempt.Submitted.Count == 0)
            {
                // Only stalled sends: pages may be out, so never FAILED.
                await MarkUnknownAsync(
                    ctx, jobId, Summary(detail, attempt.Failed.Select(f => (f.Item, f.Reason)).ToList(), printed: 0),
                    cancellation).ConfigureAwait(false);
                return;
            }

            // The printer is recorded locally and reported at the same moment, so
            // the shop's own job list can say which machine took the order.
            var printerUsed = attempt.Submitted[^1].PrinterName;
            Transition(ctx, jobId, SUBMITTED, printerWindowsName: printerUsed);
            // Best-effort, deliberately: the outcome report that follows carries
            // the real answer, and the backend is a cold-starting host.
            try
            {
                await ctx.Api.ReportStatusAsync(
                    ctx.Credential, jobId, PrintJobStatus.PRINT_SUBMITTED, printerName: printerUsed, ct: cancellation)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                ctx.Logger.LogWarning(exc, "print_job_submitted_report_failed job={JobId}", jobId);
            }

            // The blocking submit call only proves the driver accepted each file;
            // what actually happened is asked of the spooler, file by file.
            Transition(ctx, jobId, PRINTING);
            async Task FinishAsync()
            {
                PrinterCondition? stuckOn = null;
                var outcomes = await PollEachOutcomeAsync(
                    attempt.Submitted,
                    ctx.JobStallSeconds,
                    condition =>
                    {
                        // Recorded the moment it happens, so the shop's job list can
                        // say "out of paper" while the job is still waiting.
                        if (condition is not null)
                        {
                            stuckOn = condition;
                            ctx.Logger.LogWarning(
                                "print_job_blocked job={JobId} condition={Condition}", jobId, condition.Value);
                            ctx.Db.UpdateJobState(jobId, PRINTING, lastError: $"waiting: {condition.Value.Description()}");
                            ctx.Db.RecordEvent(jobId, "BLOCKED", condition.Value.Description());
                            ctx.RaiseEvent();
                        }
                    },
                    ctx.Logger,
                    cancellation).ConfigureAwait(false);

                var notPrinted = new List<(PrintJobItem Item, string Reason)>();
                var printedCount = 0;
                var anyUnconfirmed = attempt.Failed.Any(f => f.MayHavePrinted);
                for (var i = 0; i < attempt.Submitted.Count; i++)
                {
                    var sent = attempt.Submitted[i];
                    var outcome = outcomes[i];
                    switch (outcome.Outcome)
                    {
                        case PrintOutcome.COMPLETED:
                            printedCount++;
                            RecordItem(ctx, detail, sent.Item, ITEM_PRINTED, sent.PrinterName, null);
                            break;
                        case PrintOutcome.FAILED when outcome.Condition is { } gone:
                            // Withdrawn from a printer that is not there - see
                            // SpoolerOutcomePoller's unreachable rule. Nothing came out.
                            notPrinted.Add((sent.Item, $"{sent.PrinterName}: {gone.Description()}"));
                            RecordItem(ctx, detail, sent.Item, ITEM_FAILED, sent.PrinterName,
                                $"Not printed - {gone.Description()} ({sent.PrinterName}). " +
                                "Print this file once the printer is back.");
                            break;
                        case PrintOutcome.FAILED:
                            notPrinted.Add((sent.Item, $"{sent.PrinterName} reported an error"));
                            RecordItem(ctx, detail, sent.Item, ITEM_FAILED, sent.PrinterName,
                                $"{sent.PrinterName} reported an error after accepting it.");
                            break;
                        default:
                            anyUnconfirmed = true;
                            var why = outcome.Condition is { } c
                                ? $"still waiting on {sent.PrinterName}: {c.Description()}"
                                : $"sent to {sent.PrinterName}, but it never confirmed it finished";
                            notPrinted.Add((sent.Item, why));
                            RecordItem(ctx, detail, sent.Item, ITEM_UNKNOWN, sent.PrinterName, why);
                            break;
                    }
                }
                notPrinted.AddRange(attempt.Failed.Select(f => (f.Item, f.Reason)));

                ctx.Logger.LogInformation(
                    "print_job_outcome job={JobId} printed={Printed} of={Total} total_ms={Total_ms}",
                    jobId, printedCount, detail.Items.Count, preparedMs + timer.ElapsedMilliseconds);

                if (notPrinted.Count == 0)
                {
                    Transition(ctx, jobId, COMPLETED);
                    // The paper exists, so the copies held for this order have done
                    // their job. Not released for anything less than a full print:
                    // the files not printed are what the shop prints next.
                    ReleaseHeldFiles(ctx, detail.OrderId);
                    try
                    {
                        await ctx.Api.ReportStatusAsync(ctx.Credential, jobId, PrintJobStatus.PRINT_COMPLETED, ct: cancellation)
                            .ConfigureAwait(false);
                        ctx.Logger.LogInformation("print_job_completed job={JobId}", jobId);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exc)
                    {
                        // The pages printed; only saying so failed. UNKNOWN is the
                        // state that means a person has to look, and it is
                        // emphatically not FAILED.
                        await MarkUnknownAsync(
                            ctx, jobId, $"the pages printed, but the server could not be told: {exc}", cancellation)
                            .ConfigureAwait(false);
                    }
                }
                else if (printedCount == 0 && !anyUnconfirmed)
                {
                    // The spooler rejected everything that was sent: no paper.
                    await FailAsync(
                        ctx, jobId, Summary(detail, notPrinted, printed: 0),
                        PrintJobFailureReason.PRINTER_ERROR, cancellation).ConfigureAwait(false);
                }
                else
                {
                    // Some of it is on paper. Never FAILED - that has the shop reprint
                    // the whole order on top of what came out. UNKNOWN is "a person
                    // needs to look", and the per-file list says exactly at what.
                    await MarkUnknownAsync(
                        ctx, jobId,
                        stuckOn is { } blocked && printedCount == 0
                            ? $"still waiting: {blocked.Description()}. The job is queued and prints once this is fixed."
                            : Summary(detail, notPrinted, printedCount),
                        cancellation).ConfigureAwait(false);
                }
            }

            if (awaitOutcome)
            {
                await FinishAsync().ConfigureAwait(false);
                return;
            }

            // Everything is with the spooler now, which queues it for the
            // printer by itself. Waiting here for the paper to come out held the
            // agent's one print slot for as long as the printer took - a
            // 40-page order kept every other order's Print button waiting
            // minutes. The outcome is still watched and reported, just not with
            // the next order stuck behind it.
            _ = Task.Run(async () =>
            {
                try
                {
                    await FinishAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // Shutting down; the stranded-at-the-printer sweep settles it next start.
                }
                catch (Exception exc)
                {
                    ctx.Logger.LogError(exc, "print_job_outcome_error job={JobId}", jobId);
                    await MarkUnknownAsync(
                        ctx, jobId, $"unexpected error after the document reached the printer: {exc}", cancellation)
                        .ConfigureAwait(false);
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Shutting down is not a print failure. Kotlin needs this guard
            // because CancellationException is an IllegalStateException and would
            // otherwise land in the catch-all below, marking a job FAILED on the
            // agent's way out of the door - closing the app mid-print then has the
            // shop reprint, in the morning, a document that was already in the
            // tray. .NET has the same trap with a different shape:
            // OperationCanceledException is the equivalent, and it is what every
            // cancelled await throws.
            //
            // The `when` clause is load-bearing and is *not* in the Kotlin.
            // HttpClient reports its own request timeout as TaskCanceledException,
            // which derives from OperationCanceledException - so a bare catch here
            // would silently reclassify an ordinary backend timeout as "we are
            // shutting down" and leave the job stuck in whatever state it was in,
            // reported to nobody. Only a cancellation the caller actually asked
            // for is a cancellation; everything else is a fault and belongs below.
            throw;
        }
        catch (Exception exc) // a bug here must not crash the agent process
        {
            ctx.Logger.LogError(exc, "print_job_pipeline_error job={JobId}", jobId);
            if (reachedPrinter)
            {
                await MarkUnknownAsync(
                    ctx, jobId, $"unexpected error after the document reached the printer: {exc}", cancellation)
                    .ConfigureAwait(false);
            }
            else
            {
                await FailAsync(ctx, jobId, $"unexpected error: {exc}", PrintJobFailureReason.UNKNOWN, cancellation)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Kotlin's <c>exc.message ?: fallback</c>. .NET never leaves Message null,
    /// but it does synthesise "Exception of type X was thrown." for one
    /// constructed without a message - which is the same nothing, and the
    /// fallback says something a person can act on.
    /// </summary>
    private static string Reason(Exception exc, string fallback) =>
        string.IsNullOrWhiteSpace(exc.Message) ? fallback : exc.Message;

    // Per-file outcomes, as the shop's per-file list shows them.
    internal const string ITEM_PRINTED = "PRINTED";
    internal const string ITEM_FAILED = "FAILED";
    internal const string ITEM_UNKNOWN = "UNKNOWN";

    private static void RecordItem(
        JobContext ctx, PrintJobDetail detail, PrintJobItem item, string status, string? printerName, string? reason)
    {
        try
        {
            ctx.Db.UpsertItemResult(
                detail.JobId, item.ItemId, detail.OrderId, item.FileName, item.ColorMode.ToString(),
                status, printerName, reason);
        }
        catch (Exception exc)
        {
            // Bookkeeping for the screen; never a reason to change the outcome.
            ctx.Logger.LogWarning(exc, "item_result_record_failed job={JobId} item={ItemId}", detail.JobId, item.ItemId);
        }
    }

    /// <summary>"Printed 2 of 3 files. Not printed: x.pdf - why." Short enough for the job list.</summary>
    internal static string Summary(PrintJobDetail detail, IReadOnlyList<(PrintJobItem Item, string Reason)> notPrinted, int printed)
    {
        var names = string.Join("; ", notPrinted.Select(n => $"{n.Item.FileName} - {n.Reason}"));
        return printed == 0
            ? $"Nothing printed. {names}"
            : $"Printed {printed} of {detail.Items.Count} files. Not printed: {names}";
    }

    /// <summary>
    /// Kotlin's <c>downloaded.values.forEach { it.toFile().delete() }</c>.
    /// <c>File.delete()</c> returns false rather than throwing; .NET's throws, and
    /// a failure to tidy up must never become the reason a job that printed
    /// perfectly well is reported as broken - Documents.SweepOrphanedDocuments
    /// picks up whatever is left on the next start.
    /// </summary>
    /// <summary>
    /// Deletes documents fetched for jobs that never reached a printer.
    ///
    /// Preparing ahead means a claimed, downloaded job can be sitting waiting
    /// its turn when the agent is closed. The ordinary delete runs once a job is
    /// handed to a driver, so those never reach it - and without this their
    /// documents would stay on the counter PC until the next launch swept them.
    /// Bounded and recoverable either way, but a customer's coursework should
    /// not outlive the session that fetched it when closing the window can say
    /// so plainly.
    ///
    /// Only preparations that finished are touched. One still downloading is
    /// mid-write to its own file, and that half-written file belongs to
    /// SweepOrphanedDocuments on the next start, which can tell a dead process's
    /// leftovers from a live one's work.
    ///
    /// Must run after the print queue has stopped. Deleting a file a worker is
    /// about to submit would turn an orderly shutdown into a failed print.
    /// </summary>
    public static void DiscardPreparedDownloads(JobContext ctx)
    {
        foreach (var jobId in Preparations.Keys.ToArray())
        {
            if (!Preparations.TryRemove(jobId, out var preparation)) continue;
            if (!preparation.IsCompletedSuccessfully) continue;

            var prepared = preparation.Result;
            if (prepared is null) continue;

            ctx.Logger.LogInformation("prepared_job_discarded_on_shutdown job={JobId}", jobId);
            DeleteDownloads(ctx, prepared.Value.Downloaded);
        }
    }

    /// <summary>
    /// Deletes the scratch copies a job pulled down.
    ///
    /// <para>
    /// Only the ones under <see cref="JobContext.TempDir"/>. A held file - one
    /// prefetched when the order arrived - lives in the files store, is shared
    /// with the Files screen, and belongs to the order rather than to this
    /// attempt at printing it. Deleting it here would mean a print that failed
    /// and was retried had to fetch the whole order again, with the student
    /// still standing at the counter. <see cref="ReleaseHeldFiles"/> clears those
    /// once the paper actually exists.
    /// </para>
    /// </summary>
    private static void DeleteDownloads(JobContext ctx, IReadOnlyDictionary<string, string> downloaded)
    {
        foreach (var path in downloaded.Values)
        {
            if (!IsUnder(path, ctx.TempDir)) continue;
            try
            {
                File.Delete(path);
            }
            catch (Exception exc)
            {
                ctx.Logger.LogDebug(exc, "print_job_temp_file_delete_failed path={Path}", path);
            }
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;
        try
        {
            var root = Path.GetFullPath(directory);
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // An unreadable path is not one to start deleting on a guess.
            return false;
        }
    }

    /// <summary>
    /// Everything a job needs before a printer can take it: the claim, and the
    /// document on disk.
    ///
    /// Kept apart from printing so it can happen while a printer is busy with
    /// something else. Printing is strictly one at a time - there is one
    /// printer - but claiming and downloading are network waits, and doing them
    /// only once the previous sheet has landed left the printer idle for the
    /// length of a download on every single order. On a run of small jobs that
    /// was most of the wall clock.
    ///
    /// Memoised per job id, and that is what makes it safe to call from two
    /// places at once: the queue runs it ahead for what is coming next, the
    /// worker asks for it when its turn arrives, and whichever is second gets
    /// the first one's result rather than claiming or downloading twice.
    /// Printing the same document twice is the failure this whole pipeline is
    /// built to avoid, so "twice" must not be reachable even by accident.
    ///
    /// The entry is dropped once the job has been processed - see
    /// <see cref="ForgetPreparation"/> - so a long-running agent does not
    /// accumulate one per order it has ever seen.
    /// </summary>
    internal static Task<(PrintJobDetail Detail, Dictionary<string, string> Downloaded)?> PrepareAsync(
        JobContext ctx, string jobId, CancellationToken cancellation)
    {
        var preparation = Preparations.GetOrAdd(jobId, id => PrepareOnceAsync(ctx, id, cancellation));

        // Memoise a success and nothing else.
        //
        // Caching a failure was a real bug and a bad one. A claim can come back
        // PRINT_JOB_ALREADY_CLAIMED for reasons that pass - a redelivery racing
        // the reconciliation poll, a job this agent itself still held from a
        // previous run - and preparing ahead meant that answer was reached
        // before the job's turn and then remembered. Every later delivery read
        // the cached "no" and returned without asking again, so the job was
        // skipped for the life of the process.
        //
        // Cleared here rather than inside the preparation, and the difference
        // matters: a task that completes synchronously would run its own body
        // before GetOrAdd had stored anything, so the removal would find nothing
        // to remove and the entry would be written immediately afterwards - the
        // failure cached by the very code meant to stop it. Attaching this after
        // the entry is definitely in place cannot be raced that way.
        //
        // A success stays until the job is done with it, which is the whole
        // point of preparing early; ForgetPreparation releases it.
        _ = preparation.ContinueWith(
            finished =>
            {
                if (finished.IsFaulted || finished.IsCanceled || finished.Result is null)
                {
                    ForgetPreparation(jobId);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return preparation;
    }

    private static async Task<(PrintJobDetail, Dictionary<string, string>)?> PrepareOnceAsync(
        JobContext ctx, string jobId, CancellationToken cancellation)
    {
        var detail = await ClaimAsync(ctx, jobId, cancellation).ConfigureAwait(false);
        if (detail is null) return null;

        var downloaded = await DownloadWithRetryAsync(ctx, jobId, detail, cancellation).ConfigureAwait(false);
        if (downloaded is null) return null;

        return (detail, downloaded);
    }

    /// <summary>
    /// Starts the claim and download for a job that is still waiting, so the
    /// printer has nothing to wait for when it reaches it.
    ///
    /// Never throws and never blocks the caller: this is speculative work on
    /// behalf of a job whose turn has not come, and a failure here must be
    /// discovered by that job when it runs - reported against it, with its own
    /// retries - rather than escaping into whatever was printing at the time.
    /// </summary>
    public static void PrepareAhead(JobContext ctx, string jobId)
    {
        try
        {
            _ = PrepareAsync(ctx, jobId, ctx.Cancellation)
                .ContinueWith(
                    task => ctx.Logger.LogDebug(
                        task.Exception, "prepare_ahead_failed job={JobId} - the job will retry when its turn comes", jobId),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }
        catch (Exception exc)
        {
            ctx.Logger.LogDebug(exc, "prepare_ahead_refused job={JobId}", jobId);
        }
    }

    /// <summary>Releases a finished job's memoised preparation.</summary>
    private static void ForgetPreparation(string jobId) => Preparations.TryRemove(jobId, out _);

    /// <summary>
    /// Internal rather than private so a test can stand a prepared job up
    /// without a backend to claim from or a document to fetch. Nothing outside
    /// this class writes to it in earnest.
    /// </summary>
    internal static readonly ConcurrentDictionary<
        string, Task<(PrintJobDetail Detail, Dictionary<string, string> Downloaded)?>> Preparations = new();

    private static async Task<PrintJobDetail?> ClaimAsync(JobContext ctx, string jobId, CancellationToken cancellation)
    {
        Transition(ctx, jobId, VALIDATING);
        try
        {
            return await ctx.Api.ClaimJobAsync(ctx.Credential, jobId, cancellation).ConfigureAwait(false);
        }
        catch (ApiError exc) when (exc.Code == "PRINT_JOB_ALREADY_CLAIMED")
        {
            // Another delivery of the same reference beat this one to it - not
            // an error, just nothing left for this call to do.
            ctx.Db.UpdateJobState(jobId, CANCELLED, lastError: "already claimed elsewhere");
            return null;
        }
    }

    private static async Task<Dictionary<string, string>?> DownloadWithRetryAsync(
        JobContext ctx, string jobId, PrintJobDetail detail, CancellationToken cancellation)
    {
        for (var attempt = 1; attempt <= ctx.MaxRetryAttempts; attempt++)
        {
            try
            {
                return await DownloadAllAsync(ctx, jobId, detail, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Kotlin needs this because CancellationException is also an
                // IllegalStateException, so without it a perfectly healthy
                // download that was merely cancelled got reported as "job state no
                // longer allows downloading: Job was cancelled". The .NET shapes
                // differ - OperationCanceledException is not an
                // InvalidOperationException - but the guard stays, because the
                // `when` is what separates a real shutdown from the
                // TaskCanceledException an HttpClient timeout raises. Only the
                // former may escape; the latter must fall through to the retry
                // below, which is exactly what a transient network failure wants.
                throw;
            }
            catch (InvalidOperationException exc)
            {
                // The state machine refused the move. Retrying cannot help - the
                // state will be the same next time - and doing so anyway spent
                // seven seconds of backoff before reporting the honest cause under
                // the wrong headline, "download failed after 3 attempts", for a
                // download that never began. Fail once, saying what happened.
                await FailAsync(
                    ctx, jobId, $"job state no longer allows downloading: {exc.Message}",
                    PrintJobFailureReason.UNKNOWN, cancellation).ConfigureAwait(false);
                return null;
            }
            catch (DocumentFetchError exc) when (!exc.WorthRetrying)
            {
                // The server answered, and the answer was no: a 404 for a
                // document whose upload never finished, a 403 for a signature
                // that has expired. Asking again cannot change it.
                //
                // This used to fall into the retry below and cost the shop its
                // only print worker for three minutes - sixty seconds of
                // download timeout, three times, with backoff - while every
                // other order waited behind an order that was never going to
                // print. Now it goes straight to the errors list, with the
                // reason on it, and the queue moves on.
                ctx.Db.RecordEvent(jobId, "DOWNLOAD_FAILED", exc.Message);
                await FailAsync(
                    ctx, jobId, exc.Message, PrintJobFailureReason.DOWNLOAD_FAILED, cancellation)
                    .ConfigureAwait(false);
                return null;
            }
            catch (DocumentValidationError exc)
            {
                // Nothing about the document is going to improve on a second
                // look either - no download URL was issued for it at all, which
                // is what the backend says about an order whose upload is still
                // incomplete.
                ctx.Db.RecordEvent(jobId, "DOWNLOAD_FAILED", exc.Message);
                await FailAsync(
                    ctx, jobId, exc.Message, PrintJobFailureReason.DOCUMENT_INVALID, cancellation)
                    .ConfigureAwait(false);
                return null;
            }
            catch (Exception exc)
            {
                ctx.Db.RecordEvent(jobId, "DOWNLOAD_FAILED", $"attempt {attempt}: {exc}");
                if (attempt == ctx.MaxRetryAttempts)
                {
                    await FailAsync(
                        ctx, jobId, $"download failed after {attempt} attempts: {exc}",
                        PrintJobFailureReason.DOWNLOAD_FAILED, cancellation).ConfigureAwait(false);
                    return null;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)), cancellation).ConfigureAwait(false);
            }
        }
        return null;
    }

    /// <summary>
    /// Fetches a waiting order's files now, so that nothing has to be fetched
    /// when the student finally walks in.
    ///
    /// <para>
    /// This is what makes the counter instant. Under scan-at-counter an order
    /// sits untouched from the moment it arrives until somebody scans, which can
    /// be hours - and the old behaviour was to start downloading at that point,
    /// with the student standing there watching a progress bar on a shop's
    /// domestic uplink. Fetching on arrival spends that time while nobody is
    /// waiting.
    /// </para>
    ///
    /// <para>
    /// Deliberately does not claim the job, does not move its state and does not
    /// report anything to the backend. The order is still waiting for its scan;
    /// only the bytes have moved. <c>POST /print-agent/jobs/{id}/download-url</c>
    /// needs no claim - it checks that the job belongs to this agent and that the
    /// order is not a scheduled one still held back, and nothing else.
    /// </para>
    ///
    /// <para>
    /// Best-effort, and silent about it. A failure here costs the head start and
    /// nothing else: the print path downloads whatever is missing, exactly as it
    /// did before any of this existed.
    /// </para>
    /// </summary>
    internal static async Task PrefetchOrderFilesAsync(
        JobContext ctx, string jobId, string orderId, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(ctx.FilesDir)) return;
        if (ctx.Db.HasHeldFiles(orderId)) return;

        try
        {
            var detail = await ctx.Api.JobDetailAsync(ctx.Credential, jobId, cancellation).ConfigureAwait(false);
            var urls = await ctx.Api.DownloadUrlsAsync(ctx.Credential, jobId, cancellation).ConfigureAwait(false);

            var byDocumentId = new Dictionary<string, DownloadUrl>(StringComparer.Ordinal);
            foreach (var entry in urls.Items) byDocumentId[entry.DocumentId] = entry;

            // Named by the order's number, so the folder in PrintlyFiles reads
            // the way the counter talks about the order.
            var folder = OrderFolder(ctx, orderId, detail.OrderCode);
            Directory.CreateDirectory(folder);
            var position = detail.Items
                .Select((item, index) => (item.ItemId, index))
                .ToDictionary(pair => pair.ItemId, pair => pair.index + 1, StringComparer.Ordinal);

            using var slots = new SemaphoreSlim(ParallelDownloads);
            var fetches = detail.Items
                .Where(item => byDocumentId.ContainsKey(item.DocumentId))
                .Select(async item =>
                {
                    await slots.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        // Downloaded into the scratch directory and then moved,
                        // so a fetch interrupted half way through never leaves a
                        // truncated file in the held store looking complete.
                        var scratch = await Documents.DownloadDocumentAsync(
                            ctx.Api.Http, byDocumentId[item.DocumentId].Url,
                            ctx.TempDir, ctx.DownloadTimeoutSeconds, cancellation).ConfigureAwait(false);

                        // "1 - thesis.pdf": the student's own file name, numbered
                        // so two files with one name cannot overwrite each other.
                        // Always .pdf - the backend converts every upload to PDF.
                        var destination = Path.Combine(
                            folder, $"{position[item.ItemId]} - {ReadableFileStem(item.FileName, item.ItemId)}.pdf");
                        File.Move(scratch, destination, overwrite: true);

                        ctx.Db.UpsertHeldFile(
                            orderId, item.ItemId, ctx.Credential.ShopId, item.FileName,
                            destination, new FileInfo(destination).Length);
                    }
                    finally
                    {
                        slots.Release();
                    }
                })
                .ToList();

            await Task.WhenAll(fetches).ConfigureAwait(false);

            ctx.RaiseEvent();
            ctx.Logger.LogInformation(
                "order_files_held order={OrderId} files={Count}", orderId, fetches.Count);

            // Every file is on disk: tell the backend. This is the report that
            // moves the student's timeline from "Order placed" to "Order
            // accepted" for an order accepted ahead of them - and it was never
            // sent, so the timeline never moved.
            if (fetches.Count > 0 && fetches.Count == detail.Items.Count)
            {
                await ReportCachedAsync(ctx, jobId, orderId, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception exc)
        {
            // Not a failure of anything. The print path will fetch what is
            // missing, and the only thing lost is the head start.
            ctx.Logger.LogWarning(exc, "order_files_prefetch_failed order={OrderId}", orderId);
        }
    }

    /// <summary>
    /// This order's folder inside PrintlyFiles: the order's number, with the
    /// start of its id alongside so two orders that share a number can never
    /// share a folder.
    /// </summary>
    internal static string OrderFolder(JobContext ctx, string orderId, string? orderCode = null) =>
        Path.Combine(ctx.FilesDir, string.IsNullOrWhiteSpace(orderCode)
            ? SafeFileStem(orderId)
            : $"{SafeFileStem(orderCode)} ({SafeFileStem(orderId)[..Math.Min(8, SafeFileStem(orderId).Length)]})");

    /// <summary>A student's file name, without its extension, made safe for Windows.</summary>
    internal static string ReadableFileStem(string? fileName, string fallback)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length > 80) cleaned = cleaned[..80];
        return cleaned.Length == 0 ? SafeFileStem(fallback) : cleaned;
    }

    /// <summary>Jobs whose files this process has already reported cached.</summary>
    private static readonly ConcurrentDictionary<string, bool> CachedReported = new(StringComparer.Ordinal);

    /// <summary>
    /// Tells the backend the order's files are on this disk, once per job per
    /// run. Best effort: a failure is retried the next time the job is seen.
    /// </summary>
    internal static async Task ReportCachedAsync(JobContext ctx, string jobId, string orderId, CancellationToken cancellation)
    {
        if (!CachedReported.TryAdd(jobId, true)) return;
        try
        {
            await ctx.Api.ReportCachedAsync(ctx.Credential, jobId, cancellation).ConfigureAwait(false);
            ctx.Logger.LogInformation("order_files_cached_reported job={JobId} order={OrderId}", jobId, orderId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CachedReported.TryRemove(jobId, out _);
        }
        catch (Exception exc)
        {
            CachedReported.TryRemove(jobId, out _);
            ctx.Logger.LogWarning(exc, "order_files_cached_report_failed job={JobId}", jobId);
        }
    }

    /// <summary>
    /// An id reduced to something safe to put in a path.
    ///
    /// Order and item ids come from the backend, but they end up as directory
    /// and file names on this machine, and a name is not the place to find out
    /// that one of them contained a separator.
    /// </summary>
    internal static string SafeFileStem(string value)
    {
        var cleaned = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    /// <summary>
    /// Deletes an order's held files, from the disk and from the table.
    ///
    /// Called once the paper exists. Keeping them would grow without bound on a
    /// machine that is meant to sit on a counter for years.
    /// </summary>
    internal static void ReleaseHeldFiles(JobContext ctx, string orderId)
    {
        if (string.IsNullOrEmpty(ctx.FilesDir)) return;
        try
        {
            DeleteHeldOrder(ctx.Db, ctx.FilesDir, orderId);
        }
        catch (Exception exc)
        {
            ctx.Logger.LogWarning(exc, "held_files_release_failed order={OrderId}", orderId);
        }
    }

    /// <summary>
    /// Removes an order's held files: every folder its rows point into (named
    /// by order number now, by id before PrintlyFiles), then the rows.
    /// </summary>
    internal static void DeleteHeldOrder(Database db, string filesDir, string orderId)
    {
        var folders = db.HeldFilesForOrder(orderId)
            .Select(row => Path.GetDirectoryName(row.LocalPath))
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Append(Path.Combine(filesDir, SafeFileStem(orderId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        db.DeleteHeldFiles(orderId);
        foreach (var folder in folders)
        {
            // Only ever a folder inside a held-files store, never whatever a row
            // happened to point at.
            var parent = Path.GetDirectoryName(folder!);
            if (Directory.Exists(folder)
                && (string.Equals(parent, Path.GetFullPath(filesDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(parent) is "files"))
            {
                Directory.Delete(folder!, recursive: true);
            }
        }
    }

    internal static async Task<Dictionary<string, string>> DownloadAllAsync(
        JobContext ctx, string jobId, PrintJobDetail detail, CancellationToken cancellation)
    {
        Transition(ctx, jobId, DOWNLOADING);
        ReportProgress(ctx, jobId, PrintJobProgressStage.DOWNLOADING, cancellation);

        // What is already on this disk, from the prefetch when the order
        // arrived. In the ordinary case this is the whole order, the network is
        // never touched, and the printer starts on the click.
        var held = string.IsNullOrEmpty(ctx.FilesDir)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ctx.Db.HeldFilesForOrder(detail.OrderId)
                .Where(row => File.Exists(row.LocalPath))
                .ToDictionary(row => row.ItemId, row => row.LocalPath, StringComparer.Ordinal);

        var missing = detail.Items.Where(item => !held.ContainsKey(item.ItemId)).ToList();
        if (missing.Count == 0)
        {
            ctx.Logger.LogInformation(
                "order_printed_from_held_files order={OrderId} files={Count}", detail.OrderId, held.Count);
            return new Dictionary<string, string>(held, StringComparer.Ordinal);
        }

        var urls = await ctx.Api.DownloadUrlsAsync(ctx.Credential, jobId, cancellation).ConfigureAwait(false);

        // Built by assignment rather than ToDictionary so a repeated documentId
        // overwrites instead of throwing - that is what Kotlin's associateBy does,
        // and a server that ever sent one twice must not fail the whole job here.
        var byDocumentId = new Dictionary<string, DownloadUrl>(StringComparer.Ordinal);
        foreach (var entry in urls.Items) byDocumentId[entry.DocumentId] = entry;

        // Every URL is checked before any file is fetched. It used to be checked
        // one at a time inside the fetch loop, which on a missing URL meant the
        // order had already pulled down whatever came before it - work thrown
        // away, and a temp file nobody deleted.
        //
        // Only the items not already on this disk. A part-held order - the
        // prefetch was interrupted, or one file arrived after it ran - fetches
        // the remainder and nothing else.
        var sources = new List<(string ItemId, string Url)>(missing.Count);
        foreach (var item in missing)
        {
            if (!byDocumentId.TryGetValue(item.DocumentId, out var entry))
            {
                throw new DocumentValidationError($"no download URL returned for document {item.DocumentId}");
            }
            sources.Add((item.ItemId, entry.Url));
        }

        // Fetched together rather than one after another.
        //
        // These are independent GETs to blob storage, so a four-document order
        // was paying the sum of four round trips where it only ever needed the
        // longest one - and that time is spent between the shop being told the
        // order is being prepared and anything reaching a printer.
        //
        // Bounded, because "all of them at once" is not faster on a counter's
        // uplink - a hundred-item order would put a hundred connections through
        // one domestic line and make every document slower, including the first
        // one the printer is waiting for.
        var downloaded = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        using var slots = new SemaphoreSlim(ParallelDownloads);
        var fetches = sources.Select(async source =>
        {
            await slots.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                downloaded[source.ItemId] = await Documents
                    .DownloadDocumentAsync(ctx.Api.Http, source.Url, ctx.TempDir, ctx.DownloadTimeoutSeconds, cancellation)
                    .ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        }).ToList();

        try
        {
            // WhenAll waits for all of them before it reports the first failure,
            // which is what makes the cleanup below complete: by the time it
            // runs, nothing is still writing into the temp directory.
            await Task.WhenAll(fetches).ConfigureAwait(false);
        }
        catch
        {
            DeleteDownloads(ctx, downloaded);
            throw;
        }

        // What was already held, plus what was just fetched. The held paths are
        // outside the temp directory and are deliberately left there: they are
        // cleaned up by ReleaseHeldFiles once the paper exists, not by the temp
        // sweep, so a failed print can be retried without fetching again.
        var all = new Dictionary<string, string>(held, StringComparer.Ordinal);
        foreach (var entry in downloaded) all[entry.Key] = entry.Value;
        return all;
    }

    /// <summary>
    /// How many of an order's documents are fetched at once. See DownloadAllAsync
    /// for why this is a small number and not "all of them".
    /// </summary>
    private const int ParallelDownloads = 4;

    /// <summary>
    /// Returns <c>(windowsPrinterName, jobNameToken)</c> per item, in print order
    /// - every one is polled before deciding the job's own outcome.
    ///
    /// <para>
    /// <paramref name="onAboutToPrint"/> is called exactly once, with the printer
    /// chosen for the first item, in the moment between "nothing has been sent"
    /// and "something has". Everything before it - validation, discovery,
    /// selection - can still be replayed safely; nothing after it can.
    /// </para>
    ///
    /// <para>
    /// Porting note: Kotlin looks the chosen printer up in the javax.print
    /// registry and hands <c>printPdf</c> the PrintService object. The .NET
    /// submit path is named rather than object-based, so the equivalent check is
    /// against PrinterSettings.InstalledPrinters - the same question ("does the
    /// print subsystem know this name?") asked of the same spooler. It is kept
    /// rather than dropped because it is the one thing standing between a stale
    /// discovery result and a submission to a printer that is no longer there.
    /// Compared case-insensitively, because Windows printer names are.
    /// </para>
    ///
    /// <para>
    /// Also no longer <c>suspend</c>: the only suspending thing in the Kotlin was
    /// <c>onAboutToPrint</c>, whose .NET counterpart is a synchronous local
    /// database write. The whole method runs on a Task.Run worker, which is what
    /// the enclosing <c>withContext(Dispatchers.IO)</c> was for.
    /// </para>
    /// </summary>
    private static PrintAttempt SelectAndPrint(
        JobContext ctx,
        PrintJobDetail detail,
        IReadOnlyDictionary<string, string> downloaded,
        IReadOnlyList<LocalPrinter> printers,
        Action<string> onAboutToPrint)
    {
        var attempt = new PrintAttempt();
        // Fully qualified: System.Drawing.Printing carries a PaperSize of its own
        // that means something different from the order's.
        var installed = new HashSet<string>(
            System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>(),
            StringComparer.OrdinalIgnoreCase);
        var routing = PrinterRoutingStore.Read(ctx.Db);

        // Two passes, still: every file is matched to a printer before anything
        // goes to a driver, so what cannot print is known up front.
        //
        // What changed is what happens to a file that has no printer. It used to
        // fail the whole order before anything printed - one colour file with
        // the colour printer switched off meant the black-and-white pages stayed
        // unprinted too, with the student at the counter. Now every file that
        // can print does, and the ones that cannot are recorded with the reason,
        // so the shop prints just those once the printer is back.
        var planned = new List<(PrintJobItem Item, string DocumentPath, LocalPrinter Printer)>();
        foreach (var item in detail.Items)
        {
            if (!downloaded.TryGetValue(item.ItemId, out var documentPath))
            {
                attempt.Failed.Add(new ItemFailure(item, "The file could not be downloaded.", null, MayHavePrinted: false));
                continue;
            }
            var selection = PrinterSelector.SelectPrinter(item, printers, routing);
            if (selection.Printer is not { } printer)
            {
                attempt.Failed.Add(new ItemFailure(item, NoPrinterReason(item, routing.For(item)), null, MayHavePrinted: false));
                continue;
            }
            if (!installed.Contains(printer.WindowsPrinterName))
            {
                attempt.Failed.Add(new ItemFailure(
                    item, $"{printer.WindowsPrinterName} is no longer installed on this computer.",
                    printer.WindowsPrinterName, MayHavePrinted: false));
                continue;
            }
            planned.Add((item, documentPath, printer));
        }

        foreach (var (item, documentPath, printer) in planned)
        {
            if (attempt.Submitted.Count == 0) onAboutToPrint(printer.WindowsPrinterName);
            try
            {
                var jobNameToken = PrintSubmission.PrintPdf(
                    printer.WindowsPrinterName, documentPath, OptionsFor(item), ctx.Logger);
                attempt.Submitted.Add(new ItemSubmission(item, printer.WindowsPrinterName, jobNameToken));
            }
            catch (PrintSubmissionError exc)
            {
                ctx.Logger.LogWarning(exc, "print_item_failed item={ItemId} printer={Printer}", item.ItemId, printer.WindowsPrinterName);
                attempt.Failed.Add(new ItemFailure(
                    item, Reason(exc, "the printer refused it"), printer.WindowsPrinterName, MayHavePrinted: false));
            }
            catch (PrintSubmissionStalled exc)
            {
                // The driver took it and stopped partway: pages may be out.
                attempt.Failed.Add(new ItemFailure(
                    item, Reason(exc, "the printer stopped responding"), printer.WindowsPrinterName, MayHavePrinted: true));
            }
        }

        return attempt;
    }

    /// <summary>The print settings a file was ordered with.</summary>
    internal static PrintOptions OptionsFor(PrintJobItem item) =>
        new(item.ColorMode, item.DuplexMode, item.PaperSize, item.Copies, item.PageRange, item.Orientation);

    /// <summary>Why no printer could take this file, in words the counter can act on.</summary>
    internal static string NoPrinterReason(PrintJobItem item, string? namedPrinter = null)
    {
        var what = item.ColorMode == ColorMode.COLOR ? "colour printer" : "printer";
        var extra = item.DuplexMode == DuplexMode.DOUBLE_SIDED ? ", double-sided" : "";
        // The shop chose a machine for this kind of file, so name it: "switch
        // on the Canon" is something to do, "no printer" is not.
        if (namedPrinter is not null)
        {
            var mode = item.ColorMode == ColorMode.COLOR ? "colour" : "black-and-white";
            return $"The {mode} printer ({namedPrinter}) is not connected or not ready. " +
                   "Switch it on, then print this file.";
        }
        return $"No {what} is connected and ready for {item.PaperSize}{extra}. " +
               "Switch it on or set one on the Printers page, then print this file.";
    }

    internal sealed record ItemSubmission(PrintJobItem Item, string PrinterName, string JobNameToken);

    internal sealed record ItemFailure(PrintJobItem Item, string Reason, string? PrinterName, bool MayHavePrinted);

    internal sealed class PrintAttempt
    {
        public List<ItemSubmission> Submitted { get; } = new();
        public List<ItemFailure> Failed { get; } = new();
    }

    /// <summary>
    /// The spooler's answer for each file that was sent, in the order sent.
    ///
    /// Every file is asked about, even after one has failed: each answer is
    /// what that file's row on the shop's screen says, and stopping at the first
    /// failure left the rest of an order unaccounted for.
    ///
    /// <paramref name="onCondition"/> fires while a job is stuck on something a
    /// person can fix, so the shop is told "out of paper" as it happens rather
    /// than after the timeout.
    /// </summary>
    private static async Task<List<SpoolerOutcome>> PollEachOutcomeAsync(
        IReadOnlyList<ItemSubmission> submissions,
        double stallSeconds,
        Action<PrinterCondition?> onCondition,
        ILogger log,
        CancellationToken cancellation)
    {
        var results = new List<SpoolerOutcome>(submissions.Count);
        foreach (var submission in submissions)
        {
            results.Add(await SpoolerOutcomePoller.PollJobOutcomeAsync(
                submission.PrinterName,
                submission.JobNameToken,
                stallSeconds,
                onCondition: onCondition,
                log: log,
                cancellation: cancellation).ConfigureAwait(false));
        }
        return results;
    }

    private static void Transition(JobContext ctx, string jobId, string next, string? printerWindowsName = null)
    {
        var row = ctx.Db.GetJob(jobId);
        // A missing row used to be treated as RECEIVED and written anyway. The
        // write is a plain UPDATE, so it silently did nothing, and every later
        // transition then reasoned from a state that was never stored - the kind
        // of desync that surfaces somewhere else entirely, as a job that failed
        // for a reason unrelated to what went wrong.
        if (row is null)
        {
            throw new InvalidOperationException($"no local row for job {jobId} - cannot move it to {next}");
        }
        var current = row.State;
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"illegal local transition {current} -> {next} for job {jobId}");
        }
        ctx.Db.UpdateJobState(jobId, next, printerWindowsName: printerWindowsName);
        ctx.Db.RecordEvent(jobId, next);
        ctx.RaiseEvent();
    }

    private static async Task FailAsync(
        JobContext ctx, string jobId, string error, PrintJobFailureReason reasonCode, CancellationToken cancellation)
    {
        var trimmed = Take(error, 500);
        ctx.Db.UpdateJobState(jobId, FAILED, lastError: trimmed, incrementAttempt: true);
        ctx.Db.RecordEvent(jobId, FAILED, trimmed);
        ctx.RaiseEvent();
        ctx.Logger.LogWarning("print_job_failed job={JobId}", jobId);
        try
        {
            // `error` is free text for shop/ops diagnostics only; `reasonCode`
            // is the only part of this report that ever reaches the student.
            await ctx.Api.ReportStatusAsync(
                ctx.Credential, jobId, PrintJobStatus.FAILED, error: trimmed, reasonCode: reasonCode, ct: cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            // Kotlin catches Exception here, CancellationException included, and
            // so does this: the local row already says FAILED and the only thing
            // left to do was tell the server. Letting a shutdown throw out of here
            // would abandon that with nothing gained.
            ctx.Logger.LogError(exc, "print_job_failure_report_failed job={JobId}", jobId);
        }
    }

    /// <summary>
    /// Terminal locally - never retried, never reprocessed on the next reconnect.
    /// Only the backend's PrintJobResolutionService, driven by a human, moves this
    /// job on.
    /// </summary>
    private static async Task MarkUnknownAsync(
        JobContext ctx, string jobId, string note, CancellationToken cancellation)
    {
        var trimmed = Take(note, 500);
        ctx.Db.UpdateJobState(jobId, UNKNOWN, lastError: trimmed);
        ctx.Db.RecordEvent(jobId, UNKNOWN, trimmed);
        ctx.RaiseEvent();
        ctx.Logger.LogWarning("print_job_unknown job={JobId}", jobId);
        try
        {
            await ctx.Api.ReportStatusAsync(
                ctx.Credential, jobId, PrintJobStatus.PRINT_UNKNOWN, error: trimmed, ct: cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            ctx.Logger.LogError(exc, "print_job_unknown_report_failed job={JobId}", jobId);
        }
    }

    /// <summary>Best-effort live-tracing signal - never part of the pipeline's own error handling.</summary>
    /// <summary>
    /// Tells the shop what this job is doing - without making the job wait to
    /// find out whether the telling worked.
    ///
    /// Nothing here changes what the agent does. The result was already thrown
    /// away into a debug line, so the only thing awaiting it ever bought was a
    /// round trip to a cold-starting host, taken at the two worst moments there
    /// are: once between a job being selected and the first page reaching a
    /// driver, and once before a download begins. The first of those is idle
    /// printer, on every single order.
    ///
    /// Fired in order rather than simply loosed, because the two stages are the
    /// whole content of the message. A job's DOWNLOADING and PRINTING can be
    /// close together - prefetch can have finished the download moments before
    /// the printer frees up - and two pings in flight at once can arrive in
    /// either order, which would leave the shop being told a job had gone back
    /// to downloading after it started printing. Appending to one chain costs
    /// nothing on a path where nobody is waiting, and makes that impossible.
    /// </summary>
    internal static readonly OrderedBackgroundWork ProgressReports = new();

    private static void ReportProgress(
        JobContext ctx, string jobId, PrintJobProgressStage stage, CancellationToken cancellation)
        => ProgressReports.Post(() => PingProgressAsync(ctx, jobId, stage, cancellation));

    private static async Task PingProgressAsync(
        JobContext ctx, string jobId, PrintJobProgressStage stage, CancellationToken cancellation)
    {
        try
        {
            await ctx.Api.ReportProgressAsync(ctx.Credential, jobId, stage, cancellation).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            // Swallowed on purpose, and the reason the call above need not be
            // waited for: a progress ping that does not arrive costs the shop a
            // line on a screen, not a print.
            ctx.Logger.LogDebug("print_job_progress_report_failed job={JobId} stage={Stage}: {Error}", jobId, stage, exc);
        }
    }

    /// <summary>Kotlin's <c>String.take(n)</c>: the first n characters, or all of them.</summary>
    private static string Take(string value, int count) => value.Length <= count ? value : value[..count];
}
