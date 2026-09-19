using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Credentials;
using PrintlyAgent.Db;
using PrintlyAgent.Jobs;
using PrintlyAgent.Models;
using PrintlyAgent.Net;
using Xunit;

// The Kotlin tests call the pipeline's top-level declarations by their bare
// names - canTransition(RECEIVED, VALIDATING), resumeInterruptedJobs(ctx, queue).
// `using static` is what keeps those call sites reading identically once the
// top-level functions have become static members of a class.
using static PrintlyAgent.Jobs.JobPipeline;

namespace PrintlyAgent.Tests;

/// <summary>
/// Port of JobPipelineStateMachineTest.kt (10 cases) and
/// ResumeInterruptedJobsTest.kt (2 cases).
///
/// <para>
/// The state-machine half is pure and needs nothing: it is the guard that a job
/// cannot be recorded as printing without first being recorded as about to, and
/// that is worth asserting edge by edge because it is what stops a restart
/// printing somebody's coursework twice.
/// </para>
///
/// <para>
/// The resume half needs a real database and a real queue, and nothing else -
/// no printer, no backend, no app-data directory. The temp directory is per-test
/// and the API client points at a closed loopback port, so the one call that does
/// reach for the network is refused locally and instantly. That is deliberate:
/// the Kotlin test aims at <c>https://example.invalid</c> for the same effect but
/// pays a DNS lookup for it, and the assertion in both is about the rewind, which
/// happens before anything is sent.
/// </para>
/// </summary>
public class JobPipelineTests : IAsyncLifetime
{
    private const string ShopId = "shop-1";

    private readonly string _tempDir;
    private readonly List<PrintQueue> _queues = new();
    private readonly List<IDisposable> _disposables = new();

    public JobPipelineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "printly-pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Queues first: a worker still inside a job would otherwise be writing to
        // a database that has already been closed underneath it.
        foreach (var queue in _queues) await queue.DisposeAsync();
        foreach (var disposable in _disposables) disposable.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* a file still held; the temp dir is disposable either way */ }
    }

    // --- state machine -------------------------------------------------------

    [Fact(DisplayName = "happy path is allowed step by step")]
    public void HappyPathIsAllowedStepByStep()
    {
        var path = new[] { RECEIVED, VALIDATING, DOWNLOADING, DOWNLOADED, SUBMITTING, SUBMITTED, PRINTING, COMPLETED };
        for (var i = 0; i < path.Length - 1; i++)
        {
            Assert.True(CanTransition(path[i], path[i + 1]), $"{path[i]} -> {path[i + 1]} should be allowed");
        }
    }

    /// <summary>
    /// The state machine is what enforces that a job cannot be recorded as
    /// printing without first being recorded as about to print. That matters
    /// because SUBMITTING is the marker <see cref="Database.ResumableJobs"/> uses
    /// to decide a job is no longer safe to replay - if anything could reach
    /// SUBMITTED around it, a job could be printing while still looking
    /// replayable, and a restart would print it twice.
    /// </summary>
    [Fact(DisplayName = "nothing reaches submitted without passing through submitting")]
    public void NothingReachesSubmittedWithoutPassingThroughSubmitting()
    {
        Assert.True(CanTransition(DOWNLOADED, SUBMITTING));
        Assert.True(CanTransition(SUBMITTING, SUBMITTED));
        Assert.False(CanTransition(DOWNLOADED, SUBMITTED), "DOWNLOADED is still replayable; SUBMITTED is not");
        Assert.False(CanTransition(DOWNLOADING, SUBMITTED));
        Assert.False(CanTransition(VALIDATING, SUBMITTING));
    }

    /// <summary>A job that never got as far as the driver is still an honest failure.</summary>
    [Fact(DisplayName = "submitting can still fail or be cancelled")]
    public void SubmittingCanStillFailOrBeCancelled()
    {
        Assert.True(CanTransition(SUBMITTING, FAILED));
        Assert.True(CanTransition(SUBMITTING, CANCELLED));
        Assert.True(CanTransition(SUBMITTING, UNKNOWN));
    }

    [Fact(DisplayName = "cannot skip straight from received to completed")]
    public void CannotSkipStraightFromReceivedToCompleted()
    {
        Assert.False(CanTransition(RECEIVED, COMPLETED));
    }

    [Fact(DisplayName = "cannot skip download steps")]
    public void CannotSkipDownloadSteps()
    {
        Assert.False(CanTransition(RECEIVED, DOWNLOADED));
        Assert.False(CanTransition(VALIDATING, SUBMITTED));
    }

    [Fact(DisplayName = "repeating the same state is idempotent")]
    public void RepeatingTheSameStateIsIdempotent()
    {
        Assert.True(CanTransition(DOWNLOADING, DOWNLOADING));
        Assert.True(CanTransition(COMPLETED, COMPLETED));
    }

    [Fact(DisplayName = "terminal states accept no further transition except repeat")]
    public void TerminalStatesAcceptNoFurtherTransitionExceptRepeat()
    {
        foreach (var terminal in new[] { COMPLETED, FAILED, CANCELLED, UNKNOWN })
        {
            Assert.False(CanTransition(terminal, RECEIVED));
            Assert.False(CanTransition(terminal, PRINTING));
        }
    }

    [Fact(DisplayName = "cancellation allowed early but not after submission")]
    public void CancellationAllowedEarlyButNotAfterSubmission()
    {
        Assert.True(CanTransition(RECEIVED, CANCELLED));
        Assert.True(CanTransition(DOWNLOADED, CANCELLED));
        Assert.False(CanTransition(SUBMITTED, CANCELLED));
        Assert.False(CanTransition(PRINTING, CANCELLED));
    }

    [Fact(DisplayName = "printing can resolve to unknown when the spooler never confirms")]
    public void PrintingCanResolveToUnknownWhenTheSpoolerNeverConfirms()
    {
        Assert.True(CanTransition(PRINTING, UNKNOWN));
    }

    [Fact(DisplayName = "unknown is terminal and never auto-retried")]
    public void UnknownIsTerminalAndNeverAutoRetried()
    {
        // Only a human resolving the matching PRINT_UNKNOWN on the backend
        // moves this on - see PrintJobResolutionService. Nothing local ever does.
        Assert.False(CanTransition(UNKNOWN, PRINTING));
        Assert.False(CanTransition(UNKNOWN, RECEIVED));
        Assert.True(CanTransition(UNKNOWN, UNKNOWN));
    }

    // --- resuming interrupted jobs -------------------------------------------

    /*
     * The restart sweep must not disturb a job that is already running.
     *
     * It rewinds a stranded job to RECEIVED so ProcessJobAsync can replay it from
     * the top. Doing that *before* submitting meant a job the sweep found
     * mid-flight had its state pulled back underneath it - the queue then dropped
     * the duplicate submission, so nothing replayed it, and the job still running
     * reached DOWNLOADING from RECEIVED, tripped the transition guard, and was
     * reported as "download failed after 3 attempts" having downloaded fine.
     *
     * Startup is exactly when both happen at once: the sweep runs while the
     * stream is delivering the day's jobs.
     */

    [Fact(DisplayName = "a job already in flight is left exactly as it is")]
    public async Task AJobAlreadyInFlightIsLeftExactlyAsItIs()
    {
        var (ctx, db) = NewContext();
        const string jobId = "job-in-flight";
        db.InsertJobReference(jobId, "order-1", "AA-000001", shopId: ShopId);
        db.UpdateJobState(jobId, DOWNLOADING);

        // Occupy the queue with this id, exactly as a job mid-download does.
        var queue = NewQueue(workers: 2);
        var started = Gate();
        var release = Gate();
        queue.Enqueue(jobId, "AA-000001", false, async () =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ResumeInterruptedJobs(ctx, queue);

        Assert.Equal(DOWNLOADING, db.GetJob(jobId)?.State);
        // ^ "the sweep rewound a running job, which is what made it fail for the
        //   wrong reason"

        release.SetResult();
        await DrainAsync(queue);
    }

    /// <summary>
    /// The other half: a job the sweep is genuinely responsible for must still be
    /// rewound, or a restart strands it for good - no later redelivery gets past
    /// the duplicate guard.
    /// </summary>
    [Fact(DisplayName = "a stranded job is still rewound and replayed")]
    public async Task AStrandedJobIsStillRewoundAndReplayed()
    {
        var (ctx, db) = NewContext();
        const string jobId = "job-stranded";
        db.InsertJobReference(jobId, "order-2", "AA-000002", shopId: ShopId);
        db.UpdateJobState(jobId, DOWNLOADING);

        // workers 0 would deadlock; instead let it run and observe the
        // rewind, which happens first thing inside the submitted work.
        var queue = NewQueue(workers: 1);
        ResumeInterruptedJobs(ctx, queue);

        await UntilAsync(() => db.GetJob(jobId)?.State != DOWNLOADING);

        // ProcessJobAsync then runs against an unreachable API and fails the job,
        // which is fine - what matters is that the rewind happened at all.
        Assert.NotEqual(DOWNLOADING, db.GetJob(jobId)?.State);
        // ^ "a stranded job must be replayed from the top, or it is stuck for good"

        await DrainAsync(queue);
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// Kotlin's <c>context(temp)</c>: a fresh database and a JobContext around it.
    ///
    /// <para>
    /// The base URL is a closed loopback port rather than a hostname, so the one
    /// HTTP call the resume path makes is refused by the local stack without a
    /// DNS lookup or a packet leaving the machine. Both tests assert on state the
    /// pipeline writes before it ever gets that far.
    /// </para>
    /// </summary>
    private (JobContext Ctx, Database Db) NewContext()
    {
        var db = new Database(Path.Combine(_tempDir, $"agent-{Guid.NewGuid():N}.db"));
        var api = new PrintlyApiClient("http://127.0.0.1:1");
        _disposables.Add(db);
        _disposables.Add(api);

        var ctx = new JobContext(
            Api: api,
            Db: db,
            TempDir: _tempDir,
            HeldDir: Path.Combine(_tempDir, "..", "held"),
            MaxRetryAttempts: 3,
            DownloadTimeoutSeconds: 30,
            JobStallSeconds: 300.0,
            Credential: new AgentCredential("agent-1", ShopId, "secret"));
        return (ctx, db);
    }

    private PrintQueue NewQueue(int workers)
    {
        var queue = new PrintQueue(NullLogger.Instance, workers);
        _queues.Add(queue);
        return queue;
    }

    /// <summary>A TaskCompletionSource used as Kotlin's CompletableDeferred&lt;Unit&gt;.</summary>
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<bool> condition, int timeoutMillis = 5_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMillis) throw new TimeoutException("condition never became true");
            await Task.Delay(20);
        }
    }

    private static async Task DrainAsync(PrintQueue queue, int timeoutMillis = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (queue.Depth > 0)
        {
            if (sw.ElapsedMilliseconds > timeoutMillis) throw new TimeoutException("queue did not drain");
            await Task.Delay(10);
        }
    }
    /// <summary>
    /// The restart case the other sweep deliberately will not touch.
    ///
    /// A job killed between "sent to the printer" and "confirmed" cannot be
    /// replayed - paper may already be out - and cannot be called done. Left as
    /// it was, it told nobody anything: not FAILED, so it never reached the
    /// errors list; not COMPLETED, so the agent screen went on showing it as
    /// active and "Sent to the printer" for ever, from an agent that had
    /// restarted and forgotten it.
    /// </summary>
    [Fact(DisplayName = "a job the printer already had is handed to a person, not replayed")]
    public async Task AJobThePrinterAlreadyHadIsHandedToAPerson()
    {
        var (ctx, db) = NewContext();

        foreach (var (jobId, state) in new[]
                 {
                     ("job-submitting", SUBMITTING),
                     ("job-submitted", SUBMITTED),
                     ("job-printing", PRINTING),
                 })
        {
            db.InsertJobReference(jobId, $"order-{jobId}", "AA-000009", shopId: ShopId);
            // Walked through the real states: the transition guard refuses a jump.
            db.UpdateJobState(jobId, VALIDATING);
            db.UpdateJobState(jobId, DOWNLOADING);
            db.UpdateJobState(jobId, DOWNLOADED);
            db.UpdateJobState(jobId, SUBMITTING);
            if (state != SUBMITTING) db.UpdateJobState(jobId, SUBMITTED);
            if (state == PRINTING) db.UpdateJobState(jobId, PRINTING);
        }

        await ResolveJobsStrandedAtThePrinterAsync(ctx, CancellationToken.None);

        foreach (var jobId in new[] { "job-submitting", "job-submitted", "job-printing" })
        {
            var row = db.GetJob(jobId);
            Assert.Equal(UNKNOWN, row?.State);
            // The shop is told why it is being asked, not just that it is.
            Assert.Contains("restarted", row?.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// And it must not reach past its own half. A job that never got to a
    /// printer is the resume sweep's to replay; marking it UNKNOWN here would
    /// park a perfectly printable order behind a question nobody needed to
    /// answer.
    /// </summary>
    [Fact(DisplayName = "a job that never reached a printer is left for the resume sweep")]
    public async Task AJobThatNeverReachedAPrinterIsLeftAlone()
    {
        var (ctx, db) = NewContext();

        db.InsertJobReference("job-early", "order-early", "AA-000010", shopId: ShopId);
        db.UpdateJobState("job-early", VALIDATING);
        db.UpdateJobState("job-early", DOWNLOADING);

        db.InsertJobReference("job-fresh", "order-fresh", "AA-000011", shopId: ShopId);

        await ResolveJobsStrandedAtThePrinterAsync(ctx, CancellationToken.None);

        Assert.Equal(DOWNLOADING, db.GetJob("job-early")?.State);
        Assert.Equal(RECEIVED, db.GetJob("job-fresh")?.State);
    }

    /// <summary>
    /// A finished job is finished. Re-opening one as UNKNOWN would put an order
    /// that printed perfectly well back in front of the shop as a question.
    /// </summary>
    [Fact(DisplayName = "a job that already finished is not re-opened")]
    public async Task AFinishedJobIsNotReOpened()
    {
        var (ctx, db) = NewContext();

        db.InsertJobReference("job-done", "order-done", "AA-000012", shopId: ShopId);
        db.UpdateJobState("job-done", VALIDATING);
        db.UpdateJobState("job-done", DOWNLOADING);
        db.UpdateJobState("job-done", DOWNLOADED);
        db.UpdateJobState("job-done", SUBMITTING);
        db.UpdateJobState("job-done", SUBMITTED);
        db.UpdateJobState("job-done", PRINTING);
        db.UpdateJobState("job-done", COMPLETED);

        await ResolveJobsStrandedAtThePrinterAsync(ctx, CancellationToken.None);

        Assert.Equal(COMPLETED, db.GetJob("job-done")?.State);
    }

    /// <summary>
    /// Closing the agent takes the customer's documents with it.
    ///
    /// Preparing ahead means a claimed, downloaded job can be sitting waiting
    /// its turn when the window is closed. The ordinary delete runs once a job
    /// is handed to a driver, so those never reach it, and their documents
    /// would otherwise stay on the counter PC until the next launch swept them.
    /// </summary>
    [Fact(DisplayName = "documents fetched for jobs that never printed are deleted on shutdown")]
    public async Task PreparedDocumentsAreDeletedOnShutdown()
    {
        var (ctx, _) = NewContext();
        var document = Path.Combine(_tempDir, "prepared-but-never-printed.pdf");
        await File.WriteAllTextAsync(document, "a customer's coursework");

        SeedPreparation("job-waiting-its-turn", document);
        Assert.True(File.Exists(document));

        DiscardPreparedDownloads(ctx);

        Assert.False(File.Exists(document), "the document outlived the session that fetched it");
    }

    /// <summary>
    /// A preparation still downloading is mid-write to its own file. That
    /// half-written file belongs to the orphan sweep on the next start, which
    /// can tell a dead process's leftovers from a live one's work - reaching
    /// into it here would mean deleting a file something is still writing.
    /// </summary>
    [Fact(DisplayName = "a download still in flight is left to the orphan sweep")]
    public void AnUnfinishedPreparationIsLeftAlone()
    {
        var (ctx, _) = NewContext();
        var pending = new TaskCompletionSource<(PrintJobDetail, Dictionary<string, string>)?>();
        SeedPreparation("job-still-downloading", pending.Task);

        DiscardPreparedDownloads(ctx);

        // Nothing to assert about a file - the point is that this neither threw
        // nor blocked waiting for a download that may never finish.
        pending.TrySetCanceled();
    }

    /// <summary>Stands a finished preparation up, as the prefetch would leave one.</summary>
    private static void SeedPreparation(string jobId, string documentPath) =>
        SeedPreparation(jobId, Task.FromResult<(PrintJobDetail, Dictionary<string, string>)?>(
            (null!, new Dictionary<string, string> { ["item-1"] = documentPath })));

    private static void SeedPreparation(
        string jobId, Task<(PrintJobDetail, Dictionary<string, string>)?> preparation) =>
        JobPipeline.Preparations[jobId] = preparation!;

    /// <summary>
    /// A refused claim must not be remembered.
    ///
    /// PRINT_JOB_ALREADY_CLAIMED comes back for reasons that pass - a
    /// redelivery racing the reconciliation poll, or a job this agent itself
    /// still held from a previous run. Preparing ahead meant that answer was
    /// reached before the job's turn and then cached, so every later delivery
    /// read the cached "no" and returned without asking again: the job was
    /// skipped for the life of the process. Before preparing ahead existed the
    /// claim simply failed and the next delivery tried again.
    /// </summary>
    [Fact(DisplayName = "a claim that came back refused is not remembered as refused")]
    public async Task ARefusedClaimIsNotRemembered()
    {
        const string jobId = "job-contested";

        // Seeded exactly as PrepareAsync would, then asked to settle - because
        // the assertion is that the entry is taken back out again. Removing
        // from a memo that never held anything would pass whether or not the
        // code removes anything, which is no test at all.
        SeedPreparation(jobId, Task.FromResult<(PrintJobDetail, Dictionary<string, string>)?>(null));
        var refused = await SettlePreparation(jobId);

        Assert.Null(refused);
        Assert.False(
            JobPipeline.Preparations.ContainsKey(jobId),
            "the refusal was remembered, so this job would never be tried again");
    }

    /// <summary>
    /// And a preparation that threw - a dropped connection mid-download, say -
    /// is no more permanent than one that was refused.
    /// </summary>
    [Fact(DisplayName = "a preparation that threw is not remembered either")]
    public async Task AThrownPreparationIsNotRemembered()
    {
        const string jobId = "job-that-threw";

        SeedPreparation(jobId, Task.FromException<(PrintJobDetail, Dictionary<string, string>)?>(
            new InvalidOperationException("the connection dropped")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SettlePreparation(jobId));

        Assert.False(JobPipeline.Preparations.ContainsKey(jobId));
    }

    /// <summary>
    /// And the other half: a preparation that worked is kept, because holding
    /// it is the entire point of doing it early.
    /// </summary>
    [Fact(DisplayName = "a preparation that worked is kept for the job to collect")]
    public void ASuccessfulPreparationIsKept()
    {
        SeedPreparation("job-ready", Path.Combine(_tempDir, "ready.pdf"));

        Assert.True(JobPipeline.Preparations.ContainsKey("job-ready"));
    }

    /// <summary>
    /// Runs the bookkeeping PrepareAsync does once a preparation settles, on an
    /// entry already in the memo - which is the state a prepared-ahead job is
    /// in when its turn comes.
    /// </summary>
    private static Task<(PrintJobDetail Detail, Dictionary<string, string> Downloaded)?> SettlePreparation(
        string jobId) =>
        // The context is never touched: the entry is already in the memo, so
        // GetOrAdd returns it rather than calling the factory that would need
        // one. What runs is the bookkeeping that follows.
        JobPipeline.PrepareAsync(null!, jobId, CancellationToken.None);

}
