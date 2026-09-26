using PrintlyAgent.Db;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Port of DatabaseTest.kt, all 11 cases.
///
/// JUnit's @TempDir becomes an IDisposable fixture that makes a directory per
/// test and removes it afterwards - the tests share nothing, which is the point
/// of @TempDir and matters more here than usual: several of these assert on what
/// a *fresh* database does.
/// </summary>
public class DatabaseTests : IDisposable
{
    private readonly string _tempDir;

    private const string ShopA = "11111111-1111-1111-1111-111111111111";
    private const string ShopB = "22222222-2222-2222-2222-222222222222";

    public DatabaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "printly-db-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* a file still held; the temp dir is disposable either way */ }
    }

    private Database NewDatabase() => new(Path.Combine(_tempDir, $"agent-{Guid.NewGuid():N}.db"));

    [Fact(DisplayName = "insertJobReference is true only the first time")]
    public void InsertJobReferenceIsTrueOnlyTheFirstTime()
    {
        using var db = NewDatabase();
        Assert.True(db.InsertJobReference("job-1", "order-1", "SH001-000001", shopId: ShopA));
        Assert.False(db.InsertJobReference("job-1", "order-1", "SH001-000001", shopId: ShopA));
        Assert.False(db.InsertJobReference("job-1", "order-1", "SH001-000001", shopId: ShopA));
    }

    [Fact(DisplayName = "a completed job redelivered is still a no-op")]
    public void ACompletedJobRedeliveredIsStillANoOp()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-2", "order-2", null, shopId: ShopA);
        db.UpdateJobState("job-2", "COMPLETED");

        // Same job id arriving again - e.g. a reconnect re-list, or a
        // redelivered SSE push - must not look "new" a second time.
        Assert.False(db.InsertJobReference("job-2", "order-2", null, shopId: ShopA));
        Assert.Equal("COMPLETED", db.GetJob("job-2")?.State);
    }

    /// <summary>
    /// A job that is being handed to a driver may already be printing, so it is
    /// not safe to replay on restart - that is the whole reason SUBMITTING
    /// exists. DOWNLOADED is safe, and has to stay safe, or an ordinary crash
    /// between downloading and printing would need a human every time.
    /// </summary>
    [Fact(DisplayName = "a job on its way to the printer is never resumed")]
    public void AJobOnItsWayToThePrinterIsNeverResumed()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-downloaded", "order-1", "HH-000001", shopId: ShopA);
        db.InsertJobReference("job-submitting", "order-2", "HH-000002", shopId: ShopA);
        db.UpdateJobState("job-downloaded", "DOWNLOADED");
        db.UpdateJobState("job-submitting", "SUBMITTING");

        var resumable = db.ResumableJobs(ShopA).Select(r => r.JobId).ToList();

        Assert.Contains("job-downloaded", resumable);
        Assert.DoesNotContain("job-submitting", resumable);
    }

    /// <summary>
    /// The restart that would have undone scan-at-counter entirely.
    ///
    /// An order waiting for its student to walk in is RECEIVED, unscheduled and
    /// unscanned - byte for byte what an interrupted job looked like before the
    /// scan existed. So the resume sweep claimed them, and every agent restart
    /// printed the lot in a batch with nobody at the counter: the exact waste
    /// the feature is there to stop, made worse by arriving all at once.
    /// </summary>
    [Fact(DisplayName = "orders waiting for a counter scan are not resumed on restart")]
    public void OrdersWaitingForACounterScanAreNotResumed()
    {
        using var db = NewDatabase();

        // Two students who have paid and are still walking over.
        db.InsertJobReference("job-awaiting-1", "order-1", "HH-000001", shopId: ShopA);
        db.InsertJobReference("job-awaiting-2", "order-2", "HH-000002", shopId: ShopA);

        // One who scanned, and whose job the restart caught before it started.
        db.InsertJobReference("job-scanned", "order-3", "HH-000003", shopId: ShopA);
        db.MarkPriority("job-scanned");

        var resumable = db.ResumableJobs(ShopA).Select(r => r.JobId).ToList();

        Assert.Equal(new[] { "job-scanned" }, resumable);
    }

    /// <summary>
    /// The other half, so the exclusion cannot be widened into stranding real
    /// work. Past RECEIVED a job has already been taken off the print queue,
    /// which under scan-at-counter means it was scanned - but a row left
    /// mid-download by an agent built before any of this carries no priority
    /// flag, and must still be picked up rather than stuck for ever.
    /// </summary>
    [Fact(DisplayName = "a job interrupted mid-download is resumed whatever its priority flag says")]
    public void AJobInterruptedMidDownloadIsResumedRegardlessOfPriority()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-downloading", "order-1", "HH-000001", shopId: ShopA);
        db.UpdateJobState("job-downloading", "VALIDATING");
        db.UpdateJobState("job-downloading", "DOWNLOADING");

        Assert.False(db.GetJob("job-downloading")!.Priority);
        Assert.Contains("job-downloading", db.ResumableJobs(ShopA).Select(r => r.JobId));
    }

    [Fact(DisplayName = "unresolvedJobs excludes terminal states")]
    public void UnresolvedJobsExcludesTerminalStates()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-a", "order-a", null, shopId: ShopA);
        db.InsertJobReference("job-b", "order-b", null, shopId: ShopA);
        db.UpdateJobState("job-b", "COMPLETED");

        var unresolved = db.UnresolvedJobs(ShopA).Select(r => r.JobId).ToHashSet();
        Assert.Equal(new HashSet<string> { "job-a" }, unresolved);
    }

    [Fact(DisplayName = "updateJobState increments attempt count")]
    public void UpdateJobStateIncrementsAttemptCount()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-c", "order-c", null, shopId: ShopA);
        db.UpdateJobState("job-c", "FAILED", lastError: "boom", incrementAttempt: true);
        var row = db.GetJob("job-c")!;
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal("boom", row.LastError);
    }

    [Fact(DisplayName = "scheduled print at is stored and not due until its time")]
    public void ScheduledPrintAtIsStoredAndNotDueUntilItsTime()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-d", "order-d", null, scheduledPrintAt: "2999-01-01T00:00:00Z", shopId: ShopA);
        Assert.Equal("2999-01-01T00:00:00Z", db.GetJob("job-d")?.ScheduledPrintAt);

        // Far in the future - not due at any "now" this test will ever run at.
        Assert.Empty(db.DueScheduledJobs("2026-01-01T00:00:00Z", ShopA));
    }

    [Fact(DisplayName = "dueScheduledJobs returns only received jobs past their time")]
    public void DueScheduledJobsReturnsOnlyReceivedJobsPastTheirTime()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-e", "order-e", null, scheduledPrintAt: "2020-01-01T00:00:00Z", shopId: ShopA);
        db.InsertJobReference("job-f", "order-f", null, scheduledPrintAt: "2999-01-01T00:00:00Z", shopId: ShopA);
        // Never scheduled - handled immediately, not by this path.
        db.InsertJobReference("job-g", "order-g", null, shopId: ShopA);
        db.InsertJobReference("job-h", "order-h", null, scheduledPrintAt: "2020-01-01T00:00:00Z", shopId: ShopA);
        // Due, but already printed - must not be picked up again.
        db.UpdateJobState("job-h", "COMPLETED");

        var due = db.DueScheduledJobs("2026-01-01T00:00:00Z", ShopA).Select(r => r.JobId).ToHashSet();
        Assert.Equal(new HashSet<string> { "job-e" }, due);
    }

    // -------------------------------------------------------------------------
    // One machine, several shops over its life. A shop signs in with its own
    // credentials and the agent re-pairs to them, so the jobs already on disk
    // belong to whoever had it before - and are invisible to the new shop's
    // credential, which is exactly why they must not be picked up.
    // -------------------------------------------------------------------------

    [Fact(DisplayName = "a resumable job belongs only to the shop that received it")]
    public void AResumableJobBelongsOnlyToTheShopThatReceivedIt()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-a", "order-a", "AAA-1", shopId: ShopA);
        db.InsertJobReference("job-b", "order-b", "BBB-1", shopId: ShopB);
        // Scanned, so both are genuinely resumable and the assertion below is
        // about whose job it is rather than about whether it may run at all -
        // an unscanned job is excluded from the sweep for its own reasons, and
        // testing shop scoping through one would pass no matter what.
        db.MarkPriority("job-a");
        db.MarkPriority("job-b");

        Assert.Equal(new[] { "job-a" }, db.ResumableJobs(ShopA).Select(r => r.JobId));
        Assert.Equal(new[] { "job-b" }, db.ResumableJobs(ShopB).Select(r => r.JobId));
    }

    [Fact(DisplayName = "unresolved jobs are never another shop's")]
    public void UnresolvedJobsAreNeverAnotherShops()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-a", "order-a", null, shopId: ShopA);
        db.InsertJobReference("job-b", "order-b", null, shopId: ShopB);
        db.UpdateJobState("job-a", "UNKNOWN", lastError: "check the printer");

        Assert.Equal(new[] { "job-a" }, db.UnresolvedJobs(ShopA).Select(r => r.JobId));
        Assert.Equal(new[] { "job-b" }, db.UnresolvedJobs(ShopB).Select(r => r.JobId));
    }

    [Fact(DisplayName = "a scheduled job comes due only for its own shop")]
    public void AScheduledJobComesDueOnlyForItsOwnShop()
    {
        using var db = NewDatabase();
        db.InsertJobReference("job-a", "order-a", null, scheduledPrintAt: "2020-01-01T00:00:00Z", shopId: ShopA);

        Assert.Equal(new[] { "job-a" }, db.DueScheduledJobs("2026-01-01T00:00:00Z", ShopA).Select(r => r.JobId));
        Assert.Empty(db.DueScheduledJobs("2026-01-01T00:00:00Z", ShopB));
    }

    /// <summary>
    /// Duplicate-print protection is global on purpose. Job ids are unique
    /// across the estate, so a redelivery must be refused whoever is asking -
    /// scoping this by shop would turn one re-pair into a second copy of a
    /// customer's document.
    /// </summary>
    [Fact(DisplayName = "the duplicate guard is not weakened by shop scoping")]
    public void TheDuplicateGuardIsNotWeakenedByShopScoping()
    {
        using var db = NewDatabase();
        Assert.True(db.InsertJobReference("job-x", "order-x", null, shopId: ShopA));
        Assert.False(db.InsertJobReference("job-x", "order-x", null, shopId: ShopB));
    }
    /// <summary>
    /// The rotating batch that looks for a counter scan landing late. It has to
    /// skip everything a scan could no longer change, or the rotation spends its
    /// whole budget re-asking about jobs that finished days ago.
    /// </summary>
    [Fact(DisplayName = "priorityCandidates offers only the jobs a scan could still move")]
    public void PriorityCandidatesOffersOnlyTheJobsAScanCouldStillMove()
    {
        using var db = NewDatabase();

        db.InsertJobReference("job-open", "order-1", "HH-000001", shopId: ShopA);
        db.InsertJobReference("job-printing", "order-2", "HH-000002", shopId: ShopA);
        db.UpdateJobState("job-printing", "PRINTING");
        db.InsertJobReference("job-done", "order-3", "HH-000003", shopId: ShopA);
        db.UpdateJobState("job-done", "COMPLETED");

        // Terminal locally, outstanding on the backend for as long as a human
        // takes to resolve it - the one that used to be re-asked about for ever.
        db.InsertJobReference("job-unknown", "order-4", "HH-000004", shopId: ShopA);
        db.UpdateJobState("job-unknown", "UNKNOWN");

        db.InsertJobReference("job-at-counter", "order-5", "HH-000005", shopId: ShopA, priority: true);
        db.InsertJobReference("job-other-shop", "order-6", "HH-000006", shopId: ShopB);

        Assert.Equal(
            new[] { "job-open", "job-printing" },
            db.PriorityCandidates(ShopA, 10, 0).Select(r => r.JobId));
    }

    /// <summary>Paged, so a long queue costs the backend exactly what a short one does.</summary>
    [Fact(DisplayName = "priorityCandidates rotates through a backlog a page at a time")]
    public void PriorityCandidatesRotatesThroughABacklogAPageAtATime()
    {
        using var db = NewDatabase();
        for (var n = 0; n < 10; n++)
        {
            db.InsertJobReference($"job-{n}", $"order-{n}", $"HH-{n:D6}", shopId: ShopA);
        }

        var first = db.PriorityCandidates(ShopA, 4, 0).Select(r => r.JobId).ToList();
        var second = db.PriorityCandidates(ShopA, 4, 4).Select(r => r.JobId).ToList();
        var third = db.PriorityCandidates(ShopA, 4, 8).Select(r => r.JobId).ToList();

        Assert.Equal(4, first.Count);
        Assert.Equal(4, second.Count);
        Assert.Equal(2, third.Count);
        Assert.Equal(10, first.Concat(second).Concat(third).Distinct().Count());
        Assert.Empty(db.PriorityCandidates(ShopA, 4, 10));
    }

}
