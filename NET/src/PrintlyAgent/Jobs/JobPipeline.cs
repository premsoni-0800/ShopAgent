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
    /// <summary>Where documents fetched ahead of a student's arrival live - see <see cref="Settings.HeldDir"/>.</summary>
    string HeldDir,
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

    /// <summary>
    /// The documents are on this machine's disk, and nothing is going to print
    /// until the student walks in.
    ///
    /// <para>
    /// A shop can accept an order before its student arrives. The backend
    /// creates a job for it anyway - marked
    /// <see cref="PrintJobDetail.HoldForArrival"/> - precisely so the files can
    /// be fetched early, so that the print which follows the counter scan starts
    /// from a local copy rather than from a download begun at the worst possible
    /// moment, with somebody standing there waiting for it.
    /// </para>
    ///
    /// <para>
    /// Deliberately not terminal. A held job has more to do than any other
    /// non-printing state: it is waiting on a person, and
    /// <see cref="ProcessHeldReleasesAsync"/> is what eventually moves it on. It
    /// is also deliberately absent from <see cref="Database.ResumableJobs"/> -
    /// see the long note there, because a held job looks exactly like an
    /// interrupted one and replaying it would print into an empty shop.
    /// </para>
    ///
    /// <para>
    /// The only way out towards a printer is back through DOWNLOADED, which is
    /// not a technicality. It means the release path re-joins the ordinary path
    /// at the point the ordinary path starts from, so there is one piece of code
    /// that hands documents to a driver and watches what happens, not two.
    /// </para>
    /// </summary>
    public const string HELD = "HELD";

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
            [DOWNLOADED] = Set(SUBMITTING, HELD, FAILED, CANCELLED),
            // No edge from HELD to SUBMITTING, and that omission is the guard. A
            // held job reaches a printer only by going back to DOWNLOADED first,
            // which is the release path announcing itself in the one place that
            // cannot be bypassed - a future shortcut straight to the printer
            // would have to delete this line to compile, rather than quietly
            // working.
            [HELD] = Set(DOWNLOADED, FAILED, CANCELLED),
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
            queue.Enqueue(jobId, orderCode, priority, () => ProcessJobAsync(ctx, jobId, ctx.Cancellation));
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
        }
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
            if (queue.Enqueue(row.JobId, row.OrderCode, false, () => ProcessJobAsync(ctx, row.JobId, ctx.Cancellation)))
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
            var accepted = queue.Enqueue(row.JobId, row.OrderCode, false, () =>
            {
                ctx.Db.UpdateJobState(row.JobId, RECEIVED);
                return ProcessJobAsync(ctx, row.JobId, ctx.Cancellation);
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
    public static async Task ResolveJobsStrandedAtThePrinterAsync(
        JobContext ctx, CancellationToken cancellation)
    {
        foreach (var row in ctx.Db.JobsStrandedAtThePrinter(ctx.Credential.ShopId))
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

    public static async Task ProcessJobAsync(JobContext ctx, string jobId, CancellationToken cancellation = default)
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
            var prepared = await PrepareAsync(ctx, jobId, cancellation).ConfigureAwait(false);
            if (prepared is null) return;

            var (detail, downloaded) = prepared.Value;

            if (detail.HoldForArrival)
            {
                // The claim and the download have happened; the printing must
                // not. Everything below this point assumes a student is waiting
                // for these pages, and for a held order nobody is.
                await HoldForArrivalAsync(ctx, jobId, detail, downloaded, cancellation).ConfigureAwait(false);
                return;
            }

            List<(string PrinterName, string JobNameToken)> submissions;
            try
            {
                Transition(ctx, jobId, DOWNLOADED);
                ReportProgress(ctx, jobId, PrintJobProgressStage.PRINTING, cancellation);
                var printers = await Task.Run(() => PrinterDiscovery.DiscoverPrinters(ctx.Logger), cancellation)
                    .ConfigureAwait(false);
                submissions = await Task.Run(
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
            // There is deliberately no DocumentValidationError case here any
            // more. It existed to catch the document inspection that ran in this
            // block; with that removed, nothing in here can raise one, and a
            // DOCUMENT_INVALID failure path that can never fire would only
            // suggest to the next reader that documents are still being checked.
            // A file that cannot be opened now surfaces as a PrintSubmissionError
            // below, from the renderer.
            catch (NoCompatiblePrinterException exc)
            {
                await FailAsync(ctx, jobId, Reason(exc, "no compatible printer"), PrintJobFailureReason.PRINTER_INCOMPATIBLE, cancellation)
                    .ConfigureAwait(false);
                return;
            }
            catch (PrintSubmissionError exc)
            {
                await FailAsync(ctx, jobId, Reason(exc, "print submission failed"), PrintJobFailureReason.PRINTER_ERROR, cancellation)
                    .ConfigureAwait(false);
                return;
            }
            catch (PrintSubmissionStalled exc)
            {
                // Deliberately not a failure. The driver took the job and stopped
                // responding partway through, so pages may well be in the tray
                // already - reporting it failed is what would have the shop print
                // the whole thing again on top of what came out. Same rule as the
                // spooler's own UNKNOWN: only somebody who can look at the printer
                // can say what happened.
                await MarkUnknownAsync(ctx, jobId, Reason(exc, "the printer stopped responding"), cancellation)
                    .ConfigureAwait(false);
                return;
            }
            finally
            {
                DeleteDownloads(ctx, downloaded);
                // A held order's files live in their own directory rather than
                // in the temp dir, so deleting the files alone would leave the
                // directory behind for every order a shop ever accepted early.
                // A no-op for a job that was never held.
                Documents.DiscardHeldDocuments(ctx.HeldDir, jobId);
                ForgetPreparation(jobId);
            }

            // The printer is recorded locally and reported at the same moment, so
            // the shop's own job list can say which machine took the order - and,
            // when something goes wrong later, which one to go and look at.
            var printerUsed = submissions[^1].PrinterName;
            Transition(ctx, jobId, SUBMITTED, printerWindowsName: printerUsed);
            // Best-effort, deliberately. The driver has the document either way and
            // the outcome report that follows carries the real answer. Letting a
            // failure here escape would hand a printed order to the catch-all
            // below - and the backend is a cold-starting host, so a timeout on
            // this call is an ordinary event, not evidence anything went wrong.
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

            // The blocking submit call only proves the driver accepted the job -
            // PRINTING's own outcome (COMPLETED/FAILED/UNKNOWN) is decided by
            // asking the spooler what actually happened, same rule as the class
            // doc above.
            Transition(ctx, jobId, PRINTING);
            var result = await PollAllOutcomesAsync(
                submissions,
                ctx.JobStallSeconds,
                condition =>
                {
                    // Recorded the moment it happens, so the shop's job list can
                    // say "out of paper" while the job is still waiting rather
                    // than only once it has timed out.
                    if (condition is not null)
                    {
                        ctx.Logger.LogWarning(
                            "print_job_blocked job={JobId} condition={Condition}", jobId, condition.Value);
                        ctx.Db.UpdateJobState(jobId, PRINTING, lastError: $"waiting: {condition.Value.Description()}");
                        ctx.Db.RecordEvent(jobId, "BLOCKED", condition.Value.Description());
                        ctx.RaiseEvent();
                    }
                },
                ctx.Logger,
                cancellation).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case PrintOutcome.COMPLETED:
                    Transition(ctx, jobId, COMPLETED);
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
                        // The pages printed; the only thing that failed was saying
                        // so. Leaving it COMPLETED strands the order on the backend
                        // as forever-printing, with nothing left locally to move it
                        // on - COMPLETED is terminal here, so no sweep looks at it
                        // again. UNKNOWN is the state that means a person has to
                        // look, which is exactly what is wanted, and it is
                        // emphatically not FAILED.
                        await MarkUnknownAsync(
                            ctx, jobId, $"the pages printed, but the server could not be told: {exc}", cancellation)
                            .ConfigureAwait(false);
                    }
                    break;

                case PrintOutcome.FAILED:
                    await FailAsync(
                        ctx, jobId, "the printer reported an error after accepting the job",
                        PrintJobFailureReason.PRINTER_ERROR, cancellation).ConfigureAwait(false);
                    break;

                case PrintOutcome.UNKNOWN:
                default:
                    // A stuck job is still in the queue and may yet print, so this
                    // is never reported as failed - that is what would let the
                    // shop reprint a page that then comes out anyway. Naming the
                    // condition turns "go and look at the printer" into something
                    // actionable.
                    await MarkUnknownAsync(
                        ctx,
                        jobId,
                        result.Condition is { } stuckOn
                            ? $"still waiting: {stuckOn.Description()}. The job is queued and prints once this is fixed."
                            : "could not confirm the print finished before the spooler check timed out",
                        cancellation).ConfigureAwait(false);
                    break;
            }
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

    private static void DeleteDownloads(JobContext ctx, IReadOnlyDictionary<string, string> downloaded)
    {
        foreach (var path in downloaded.Values)
        {
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

    /// <summary>
    /// Puts a held order's documents somewhere durable, records that they are
    /// there, and stops.
    ///
    /// <para>
    /// The order of the three steps is the whole of the design. The files move
    /// first, because everything afterwards is a claim that they exist. The
    /// local state is written second, because that is what survives a restart
    /// and what <see cref="ProcessHeldReleasesAsync"/> reads. The report to the
    /// backend goes last and is allowed to fail, because it is the only one of
    /// the three that somebody else owns.
    /// </para>
    ///
    /// <para>
    /// That last point deserves its reason in full: <c>/cached</c> is what
    /// lights "Order accepted" on the student's timeline. A report that does not
    /// land leaves them looking at step one for longer than they should, which
    /// is a worse timeline and not a worse outcome - the pages are on this disk
    /// either way and print the moment they scan. The release loop re-sends it
    /// on every pass while the hold stands, so a lost report costs one interval
    /// rather than being lost for good, and the backend takes the first report
    /// and ignores the rest.
    /// </para>
    ///
    /// <para>
    /// The memoised preparation is released here as it is anywhere else a job
    /// stops being in flight. The release path seeds a fresh one from the local
    /// row, which is what it has to do anyway - the process that eventually
    /// prints this is very often not this one.
    /// </para>
    ///
    /// <para>Nothing here deletes anything. That is the point of the state.</para>
    /// </summary>
    private static async Task HoldForArrivalAsync(
        JobContext ctx,
        string jobId,
        PrintJobDetail detail,
        Dictionary<string, string> downloaded,
        CancellationToken cancellation)
    {
        try
        {
            foreach (var item in detail.Items)
            {
                if (!downloaded.TryGetValue(item.ItemId, out var source)) continue;
                Documents.StoreHeldDocument(source, ctx.HeldDir, jobId, item.ItemId);
            }
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            // The files could not be put somewhere they will survive a restart,
            // so there is nothing to hold. Failing here is honest and safe -
            // nothing has printed - and the shop is told why rather than being
            // left with an order that claims to be ready and is not.
            await FailAsync(
                ctx, jobId, $"could not store the documents for collection: {exc.Message}",
                PrintJobFailureReason.DOWNLOAD_FAILED, cancellation).ConfigureAwait(false);
            ForgetPreparation(jobId);
            return;
        }

        // Through DOWNLOADED rather than straight to HELD, because the download
        // did finish and because HELD is deliberately reachable from nowhere
        // else - the one edge into it is the same edge the ordinary path takes
        // out of downloading, so a held job and a printable one are the same job
        // until the moment this line runs. Going direct would need an edge from
        // DOWNLOADING, and an edge into HELD from a state where the files are
        // not yet on disk is exactly the thing that must not exist.
        Transition(ctx, jobId, DOWNLOADED);
        Transition(ctx, jobId, HELD);
        ForgetPreparation(jobId);
        ctx.Logger.LogInformation(
            "print_job_held_for_arrival job={JobId} order={OrderId} items={Items}",
            jobId, detail.OrderId, detail.Items.Count);
        await ReportCachedAsync(ctx, jobId, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases every held job whose student has since arrived, and re-reports
    /// the ones still waiting.
    ///
    /// <para>
    /// A poll rather than a push, and that is a decision rather than an
    /// omission. The backend clears the hold when the student scans at the
    /// counter and sends nothing to say so - but even if it did, this agent
    /// could not afford to depend on it. An SSE connection can stop delivering
    /// without closing, and a push dropped in that window would be an order that
    /// never prints at all until somebody notices and restarts something, with
    /// the student standing at the counter the entire time. Asking costs one
    /// request per held job per interval and cannot be lost. It also covers the
    /// case no push can: an agent that was switched off for the whole of the
    /// scan picks the release up when it comes back, because the answer is a
    /// fact about the job rather than an event that happened while nobody was
    /// listening.
    /// </para>
    ///
    /// <para>
    /// A job still held gets <c>/cached</c> sent again. That is not noise - it
    /// is how a student whose first report was lost to a flaky connection still
    /// reaches step two, and the backend's own idempotence is what makes
    /// repeating it free.
    /// </para>
    /// </summary>
    public static async Task ProcessHeldReleasesAsync(
        JobContext ctx, PrintQueue queue, CancellationToken cancellation = default)
    {
        foreach (var row in ctx.Db.HeldJobs(ctx.Credential.ShopId))
        {
            PrintJobDetail detail;
            try
            {
                detail = await ctx.Api.JobDetailAsync(ctx.Credential, row.JobId, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                // Held jobs are asked about again on the next pass, so an
                // unreachable backend costs a delay and never a state change.
                // The documents are already on disk; nothing about them expires.
                ctx.Logger.LogDebug(exc, "held_job_detail_failed job={JobId}", row.JobId);
                continue;
            }

            if (detail.HoldForArrival)
            {
                await ReportCachedAsync(ctx, row.JobId, cancellation).ConfigureAwait(false);
                continue;
            }

            // The student has scanned. Priority is not read from the row here:
            // by definition this order's student is at the counter right now,
            // which is the exact condition in-shop priority exists for, and the
            // row may not have been told yet - the grant arrives through a
            // separate lookup that this release has just overtaken.
            var captured = detail;
            var accepted = queue.Enqueue(
                row.JobId, row.OrderCode, priority: true,
                work: () => ReleaseHeldJobAsync(ctx, row.JobId, captured, cancellation));
            if (accepted)
            {
                ctx.Logger.LogInformation("held_job_released job={JobId} order={OrderId}", row.JobId, row.OrderId);
            }
        }
    }

    /// <summary>
    /// Prints a job whose hold has lifted, from the copy taken when the shop
    /// accepted it.
    ///
    /// <para>
    /// Seeds <see cref="Preparations"/> with the detail already fetched and the
    /// files already on disk, then puts the job back to DOWNLOADED and hands it
    /// to <see cref="ProcessJobAsync"/>. That is the whole release: the ordinary
    /// path then runs with its claim already done and its documents already
    /// present, and there is no second copy of the spooling and outcome-polling
    /// code for the two paths to drift apart on. Printing the same document
    /// twice is the failure this pipeline is built around, and a duplicated
    /// print path is how that starts.
    /// </para>
    ///
    /// <para>
    /// Being replayable again from this moment is correct rather than a
    /// regression: the job is DOWNLOADED, nothing has reached a printer, and
    /// somebody is standing at the counter waiting for it - a restart now should
    /// pick it straight back up, which is exactly what
    /// <see cref="Database.ResumableJobs"/> will do.
    /// </para>
    /// </summary>
    private static async Task ReleaseHeldJobAsync(
        JobContext ctx, string jobId, PrintJobDetail detail, CancellationToken cancellation)
    {
        var row = ctx.Db.GetJob(jobId);
        if (row is null || TERMINAL.Contains(row.State)) return;

        Dictionary<string, string> files;
        try
        {
            files = await EnsureHeldDocumentsAsync(ctx, jobId, detail, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exc)
        {
            await FailAsync(
                ctx, jobId, $"held documents could not be recovered: {exc.Message}",
                PrintJobFailureReason.DOWNLOAD_FAILED, cancellation).ConfigureAwait(false);
            return;
        }

        Transition(ctx, jobId, DOWNLOADED);
        Preparations[jobId] = Task.FromResult<(PrintJobDetail Detail, Dictionary<string, string> Downloaded)?>((detail, files));
        await ProcessJobAsync(ctx, jobId, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The held files for a job, fetching again any that are no longer there.
    ///
    /// <para>
    /// A held document can go missing between the shop accepting the order and
    /// the student arriving, and none of the ways are exotic: a disk cleaner, a
    /// policy that empties app data, a machine rebuilt over the weekend,
    /// somebody tidying a folder. Failing the order in that moment would be the
    /// worst possible answer - there is a person at the counter who has already
    /// paid, and the file is still one signed URL away. A lost cache should cost
    /// them the download they would have paid for anyway, not their order.
    /// </para>
    ///
    /// <para>
    /// Deliberately does no state transitions. This runs with the job in HELD,
    /// on its way to DOWNLOADED, and the download states belong to the first
    /// fetch. Moving through them again here would say something untrue about a
    /// job that is being recovered rather than downloaded for the first time.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<string, string>> EnsureHeldDocumentsAsync(
        JobContext ctx, string jobId, PrintJobDetail detail, CancellationToken cancellation)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<PrintJobItem>();
        foreach (var item in detail.Items)
        {
            var path = Documents.HeldDocumentPath(ctx.HeldDir, jobId, item.ItemId);
            if (File.Exists(path) && new FileInfo(path).Length > 0) files[item.ItemId] = path;
            else missing.Add(item);
        }

        if (missing.Count == 0) return files;

        ctx.Logger.LogWarning(
            "held_documents_missing job={JobId} count={Count} - fetching them again", jobId, missing.Count);
        var urls = await ctx.Api.DownloadUrlsAsync(ctx.Credential, jobId, cancellation).ConfigureAwait(false);
        var byDocumentId = urls.Items.ToDictionary(item => item.DocumentId, StringComparer.Ordinal);
        foreach (var item in missing)
        {
            if (!byDocumentId.TryGetValue(item.DocumentId, out var url))
            {
                throw new InvalidOperationException($"no download URL returned for document {item.DocumentId}");
            }

            files[item.ItemId] = await Documents.DownloadDocumentToAsync(
                ctx.Api.Http,
                url.Url,
                Documents.HeldDocumentPath(ctx.HeldDir, jobId, item.ItemId),
                ctx.DownloadTimeoutSeconds,
                cancellation).ConfigureAwait(false);
        }

        return files;
    }

    /// <summary>Best-effort by design - see <see cref="HoldForArrivalAsync"/> for why a lost report is a worse timeline and not a worse outcome.</summary>
    private static async Task ReportCachedAsync(JobContext ctx, string jobId, CancellationToken cancellation)
    {
        try
        {
            await ctx.Api.ReportCachedAsync(ctx.Credential, jobId, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exc)
        {
            ctx.Logger.LogDebug(exc, "print_job_cached_report_failed job={JobId}", jobId);
        }
    }

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

    internal static async Task<Dictionary<string, string>> DownloadAllAsync(
        JobContext ctx, string jobId, PrintJobDetail detail, CancellationToken cancellation)
    {
        Transition(ctx, jobId, DOWNLOADING);
        ReportProgress(ctx, jobId, PrintJobProgressStage.DOWNLOADING, cancellation);
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
        var sources = new List<(string ItemId, string Url)>(detail.Items.Count);
        foreach (var item in detail.Items)
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
                    .DownloadDocumentAsync(
                        ctx.Api.Http, source.Url, ctx.TempDir, ctx.DownloadTimeoutSeconds, cancellation, key: source.ItemId)
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

        return new Dictionary<string, string>(downloaded, StringComparer.Ordinal);
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
    private static List<(string PrinterName, string JobNameToken)> SelectAndPrint(
        JobContext ctx,
        PrintJobDetail detail,
        IReadOnlyDictionary<string, string> downloaded,
        IReadOnlyList<LocalPrinter> printers,
        Action<string> onAboutToPrint)
    {
        var submissions = new List<(string PrinterName, string JobNameToken)>();
        // Fully qualified: System.Drawing.Printing carries a PaperSize of its own
        // that means something different from the order's, and importing the
        // namespace here to reach one static property would put that name in
        // scope alongside PrintlyAgent.Models.PaperSize. PrintSubmission.cs keeps
        // the two apart with an explicit alias for the same reason.
        var installed = new HashSet<string>(
            System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>(),
            StringComparer.OrdinalIgnoreCase);

        // Two passes, and the split is the point.
        //
        // Every printer is chosen and checked for *every* item before anything
        // goes to a driver. Interleaved - choose for item, print item, choose
        // for the next - a two-item order whose second item had no printer that
        // could take it printed the first and then reported the job FAILED,
        // which the shop reads as "print it again". Pages were already in the
        // tray.
        //
        // The document itself is deliberately not examined in either pass: it
        // is sent to the printer exactly as downloaded.
        var planned = new List<(PrintJobItem Item, string DocumentPath, LocalPrinter Printer)>();
        foreach (var item in detail.Items)
        {
            // Taken as it arrived. The document is not opened, measured or
            // checked for being a readable PDF before it goes to the driver -
            // whatever was downloaded is what gets printed.
            var documentPath = downloaded[item.ItemId];
            var selection = PrinterSelector.SelectPrinter(item, printers);
            var printer = selection.Printer
                ?? throw new NoCompatiblePrinterException(
                    $"no compatible printer for item {item.ItemId}: {selection.Reason}");
            if (!installed.Contains(printer.WindowsPrinterName))
            {
                throw new NoCompatiblePrinterException(
                    $"printer {printer.WindowsPrinterName} not found in the Windows printer registry");
            }
            planned.Add((item, documentPath, printer));
        }

        // Checked here rather than after the submit loop, where it used to be:
        // an empty job is not something to discover once printing is over.
        if (planned.Count == 0) throw new NoCompatiblePrinterException("job had no items");

        // From here paper can move, and nothing below may be reported as FAILED.
        foreach (var (item, documentPath, printer) in planned)
        {
            var options = new PrintOptions(
                item.ColorMode, item.DuplexMode, item.PaperSize, item.Copies, item.PageRange);
            if (submissions.Count == 0) onAboutToPrint(printer.WindowsPrinterName);
            var jobNameToken = PrintSubmission.PrintPdf(
                printer.WindowsPrinterName, documentPath, options, ctx.Logger);
            submissions.Add((printer.WindowsPrinterName, jobNameToken));
        }

        return submissions;
    }

    /// <summary>
    /// Any FAILED short-circuits immediately; COMPLETED only if every item
    /// resolved COMPLETED; UNKNOWN if any item's outcome could not be confirmed.
    ///
    /// <para>
    /// <paramref name="onCondition"/> fires while a job is stuck on something a
    /// person can fix, so the shop is told "out of paper" as it happens rather
    /// than after the timeout.
    /// </para>
    /// </summary>
    private static async Task<SpoolerOutcome> PollAllOutcomesAsync(
        IReadOnlyList<(string PrinterName, string JobNameToken)> submissions,
        double stallSeconds,
        Action<PrinterCondition?> onCondition,
        ILogger log,
        CancellationToken cancellation)
    {
        var worst = new SpoolerOutcome(PrintOutcome.COMPLETED);
        foreach (var (printerName, jobNameToken) in submissions)
        {
            var result = await SpoolerOutcomePoller.PollJobOutcomeAsync(
                printerName,
                jobNameToken,
                stallSeconds,
                onCondition: onCondition,
                log: log,
                cancellation: cancellation).ConfigureAwait(false);
            if (result.Outcome == PrintOutcome.FAILED) return result;
            // Keep the condition with the UNKNOWN it belongs to - it is the only
            // thing that tells a human what to go and fix.
            if (result.Outcome == PrintOutcome.UNKNOWN) worst = result;
        }
        return worst;
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
