using PrintlyAgent.Core;
using PrintlyAgent.Db;
using PrintlyAgent.Printing;
using Xunit;

// Same reason as JobPipelineTests: the Kotlin original calls canTransition and
// the state constants by their bare names, and `using static` keeps the ported
// call sites reading identically.
using static PrintlyAgent.Jobs.JobPipeline;

namespace PrintlyAgent.Tests;

/// <summary>
/// Port of HeldForArrivalTest.kt - the rules that keep an order accepted before
/// its student arrives from printing into an empty shop.
///
/// <para>
/// Every assertion here is about the same failure, approached from a different
/// direction: a held job has claimed and downloaded and not printed, which is
/// the exact shape of a job a restart interrupted, and the sweep that replays
/// interrupted jobs would happily replay this one. The difference is that
/// nothing interrupted it - it is waiting on a person - and the shop cannot
/// un-print what a mistake here would produce, hours early, with nobody there.
/// </para>
/// </summary>
public class HeldForArrivalTests : IDisposable
{
    private const string Shop = "11111111-1111-1111-1111-111111111111";
    private const string OtherShop = "22222222-2222-2222-2222-222222222222";

    private readonly string _tempDir;

    public HeldForArrivalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "printly-held-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private Database NewDatabase() => new(Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".db"));

    // --- the state machine ---------------------------------------------------

    /// <summary>
    /// Only a job whose documents are already down can be held, because holding
    /// is a statement about files on this disk. The earlier states have nothing
    /// to hold: RECEIVED has not been claimed, VALIDATING has not been told what
    /// the items are, and DOWNLOADING is by definition not finished.
    /// </summary>
    [Fact(DisplayName = "only a downloaded job can be held")]
    public void OnlyADownloadedJobCanBeHeld()
    {
        Assert.True(CanTransition(DOWNLOADED, HELD));
        Assert.False(CanTransition(RECEIVED, HELD));
        Assert.False(CanTransition(VALIDATING, HELD));
        Assert.False(CanTransition(DOWNLOADING, HELD));
    }

    /// <summary>
    /// The walk the hold path actually takes, from the state a finished download
    /// really leaves the job in.
    ///
    /// <para>
    /// This is not the same assertion as the one above, and the difference cost
    /// a bug: DownloadAllAsync leaves the job DOWNLOADING, so a hold that moved
    /// straight to HELD from there tripped the transition guard and failed an
    /// order whose documents had downloaded perfectly. Asserting the rule
    /// (DOWNLOADING -> HELD is refused) is what makes the rule safe; asserting
    /// the route is what makes the code that obeys it correct.
    /// </para>
    /// </summary>
    [Fact(DisplayName = "holding walks from downloading through downloaded")]
    public void HoldingWalksThroughDownloaded()
    {
        Assert.True(CanTransition(DOWNLOADING, DOWNLOADED));
        Assert.True(CanTransition(DOWNLOADED, HELD));
        Assert.False(CanTransition(DOWNLOADING, HELD));
    }

    /// <summary>
    /// SUBMITTING is the marker that says "paper may be moving", and a held job
    /// must not be able to reach it directly. The release path goes back through
    /// DOWNLOADED first, which is what puts the release in one identifiable
    /// place instead of letting any future caller print a held job by accident.
    /// </summary>
    [Fact(DisplayName = "a held job cannot reach the printer without being released first")]
    public void AHeldJobCannotReachThePrinterDirectly()
    {
        Assert.False(CanTransition(HELD, SUBMITTING));
        Assert.False(CanTransition(HELD, SUBMITTED));
        Assert.False(CanTransition(HELD, PRINTING));
        Assert.True(CanTransition(HELD, DOWNLOADED));
        Assert.True(CanTransition(DOWNLOADED, SUBMITTING));
    }

    /// <summary>
    /// A student can cancel an order they never came to collect, and a held job
    /// can still fail - most plainly when the documents cannot be recovered.
    /// Neither is possible if HELD is treated as an end state.
    /// </summary>
    [Fact(DisplayName = "a held job can still be cancelled or failed and is not terminal")]
    public void AHeldJobIsNotTerminal()
    {
        Assert.True(CanTransition(HELD, CANCELLED));
        Assert.True(CanTransition(HELD, FAILED));
        Assert.DoesNotContain(HELD, TERMINAL);
    }

    // --- the queries ---------------------------------------------------------

    /// <summary>
    /// The pair, in one test, because the pair is the point.
    ///
    /// <para>
    /// Asserting only that a held job is skipped would pass just as well against
    /// a resume sweep that had stopped working altogether - and that failure
    /// mode is invisible until the day a shop restarts mid-order and nothing
    /// comes back. Asserting only that an interrupted job resumes would pass
    /// against a sweep that replays held jobs too. The behaviour worth pinning
    /// is that the sweep can tell these two apart, and both rows are
    /// DOWNLOADED-shaped by construction here so that it has to.
    /// </para>
    /// </summary>
    [Fact(DisplayName = "the resume sweep replays an interrupted job and leaves a held one alone")]
    public void ResumeSweepTellsInterruptedFromHeld()
    {
        using var db = NewDatabase();

        db.InsertJobReference("job-interrupted", "order-1", "A-1", shopId: Shop);
        db.UpdateJobState("job-interrupted", DOWNLOADED);

        db.InsertJobReference("job-held", "order-2", "A-2", shopId: Shop);
        db.UpdateJobState("job-held", DOWNLOADED);
        db.UpdateJobState("job-held", HELD);

        var resumable = db.ResumableJobs(Shop).Select(row => row.JobId).ToList();

        Assert.Contains("job-interrupted", resumable);
        Assert.DoesNotContain("job-held", resumable);
    }

    /// <summary>
    /// Oldest first, so a shop that took a morning's worth of orders releases
    /// them in the order it took them, and so successive passes walk the list
    /// the same way rather than rotating.
    /// </summary>
    [Fact(DisplayName = "held jobs come back oldest first")]
    public void HeldJobsAreOldestFirst()
    {
        using var db = NewDatabase();

        // received_at is written by InsertJobReference from the clock, so the
        // rows are aged apart rather than inserted in one instant - otherwise
        // this would be asserting the tie-break and not the ordering it is
        // named for.
        foreach (var id in new[] { "job-a", "job-b", "job-c" })
        {
            db.InsertJobReference(id, "order-" + id, id.ToUpperInvariant(), shopId: Shop);
            db.UpdateJobState(id, DOWNLOADED);
            db.UpdateJobState(id, HELD);
            Thread.Sleep(5);
        }

        Assert.Equal(
            new[] { "job-a", "job-b", "job-c" },
            db.HeldJobs(Shop).Select(row => row.JobId).ToArray());
    }

    /// <summary>
    /// One machine serves different shops over its life - a shop signs out and
    /// another signs in with its own credentials. A held order left by the
    /// previous shop is not this one's to print, and the credential this agent
    /// now holds could not even ask the backend about it.
    /// </summary>
    [Fact(DisplayName = "held jobs are scoped to one shop")]
    public void HeldJobsAreScopedToOneShop()
    {
        using var db = NewDatabase();

        db.InsertJobReference("ours", "order-1", "A-1", shopId: Shop);
        db.UpdateJobState("ours", DOWNLOADED);
        db.UpdateJobState("ours", HELD);

        db.InsertJobReference("theirs", "order-2", "B-2", shopId: OtherShop);
        db.UpdateJobState("theirs", DOWNLOADED);
        db.UpdateJobState("theirs", HELD);

        Assert.Equal(new[] { "ours" }, db.HeldJobs(Shop).Select(row => row.JobId).ToArray());
    }

    // --- where the files live ------------------------------------------------

    /// <summary>
    /// The orphan sweep deletes any document whose owning process is gone. For a
    /// held document that is exactly the wrong reading: the shop accepted in the
    /// morning, the student arrives after lunch, and the agent may well have
    /// been restarted in between - an absent process means a restart, not a
    /// leak.
    ///
    /// <para>
    /// Pinned as a fact about the two directories rather than by running the
    /// sweep, because the sweep's own safety here rests on it looking in one
    /// directory and not below it. Making the held store a sibling means that
    /// safety does not depend on anyone remembering the distinction later.
    /// </para>
    /// </summary>
    [Fact(DisplayName = "the held store is not where the orphan sweep looks")]
    public void HeldStoreIsOutsideTheSweep()
    {
        var settings = SettingsLoader.Load();

        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.TempDir));
        var held = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.HeldDir));

        Assert.False(
            held.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "held documents under the temp dir would be deleted by the orphan sweep after any restart");
        Assert.Equal(Path.GetDirectoryName(temp), Path.GetDirectoryName(held));
    }

    /// <summary>
    /// The sweep, run for real over a temp directory with a held store beside
    /// it, leaves the held file alone. The directory layout test above says why
    /// this holds; this one says that it does.
    /// </summary>
    [Fact(DisplayName = "the orphan sweep does not touch a held document")]
    public void SweepLeavesHeldDocumentsAlone()
    {
        var appData = Path.Combine(_tempDir, "app");
        var temp = Path.Combine(appData, "tmp");
        var held = Path.Combine(appData, "held");
        Directory.CreateDirectory(temp);

        // A pid that is not running, which is what marks a temp file as
        // abandoned. 0 is never a live user process on Windows.
        File.WriteAllText(Path.Combine(temp, "0-abandoned.pdf"), "x");
        var heldFile = Documents.StoreHeldDocument(
            WriteTempFile("0-tobeheld.pdf"), held, "job-1", "item-1");

        var removed = Documents.SweepOrphanedDocuments(temp);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(heldFile), "the held document must survive a sweep it is not the subject of");
    }

    /// <summary>
    /// The process that prints a held order is very often not the one that
    /// fetched it, so the path has to be recomputable from ids both processes
    /// have rather than remembered.
    /// </summary>
    [Fact(DisplayName = "a held document's path is derived from the job and item ids")]
    public void HeldPathIsDeterministic()
    {
        var first = Documents.HeldDocumentPath(_tempDir, "job-1", "item-1");
        var again = Documents.HeldDocumentPath(_tempDir, "job-1", "item-1");
        Assert.Equal(first, again);

        Assert.Equal("item-1.pdf", Path.GetFileName(first));
        Assert.Equal("job-1", Path.GetFileName(Path.GetDirectoryName(first)));

        var otherItem = Documents.HeldDocumentPath(_tempDir, "job-1", "item-2");
        Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(otherItem));
        Assert.NotEqual(first, otherItem);
    }

    /// <summary>
    /// Once the pages have printed the copy goes, directory and all. A job that
    /// was never held has nothing here, which is why the ordinary print path can
    /// call this without asking.
    /// </summary>
    [Fact(DisplayName = "discarding a held job removes its whole directory, and is a no-op otherwise")]
    public void DiscardRemovesTheJobDirectory()
    {
        var held = Path.Combine(_tempDir, "held");
        Documents.StoreHeldDocument(WriteTempFile("src.pdf"), held, "job-1", "item-1");
        Assert.True(Directory.Exists(Path.Combine(held, "job-1")));

        Documents.DiscardHeldDocuments(held, "job-1");
        Assert.False(Directory.Exists(Path.Combine(held, "job-1")));

        // The normal print path calls this for every job, held or not.
        Documents.DiscardHeldDocuments(held, "never-held");
    }

    private string WriteTempFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "%PDF-1.4 test");
        return path;
    }
}
