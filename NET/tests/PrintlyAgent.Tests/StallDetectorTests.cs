using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// When a print submission is considered to have stopped responding.
///
/// Port of StallDetectorTest.kt.
///
/// Both halves of this matter and they pull against each other. Give up too
/// eagerly and a legitimately enormous document gets abandoned halfway; give up
/// never - which is what the code did before - and a wedged driver holds one of
/// the agent's few print slots for good, with the job stuck at DOWNLOADED where
/// nobody can see it.
///
/// Drawn from a real incident: a 3,100-page job printed 2,860 pages and froze,
/// with the spooler still reporting "Printing" and the blocking submit never
/// returning.
/// </summary>
public class StallDetectorTests
{
    private const long Limit = 300L;

    private static long Seconds(long n) => n * 1_000_000_000L;

    /// <summary>
    /// Stands in for SpoolerOutcomePoller.JobProgress. A record so equality is
    /// by value, which is what the detector compares - the real type has the
    /// same shape and the same two fields.
    /// </summary>
    private sealed record Progress(bool Found, int PagesPrinted);

    private static Progress At(int pages, bool found = true) => new(found, pages);

    [Fact(DisplayName = "a job that keeps printing is never called stalled")]
    public void AJobThatKeepsPrintingIsNeverCalledStalled()
    {
        var detector = new StallDetector<Progress>(Limit);
        var pages = 0;
        // An hour of steady progress - far past the limit, but always moving.
        for (var tick = 0; tick <= 240; tick++)
        {
            pages += 10;
            Assert.Null(detector.Sample(At(pages), Seconds(tick * 15L)));
        }
    }

    [Fact(DisplayName = "a job whose page count stops moving is stalled once the limit passes")]
    public void AJobWhosePageCountStopsMovingIsStalledOnceTheLimitPasses()
    {
        var detector = new StallDetector<Progress>(Limit);
        Assert.Null(detector.Sample(At(2860), Seconds(0)));
        Assert.Null(detector.Sample(At(2860), Seconds(120)));
        Assert.Null(detector.Sample(At(2860), Seconds(299)));

        var stalledFor = detector.Sample(At(2860), Seconds(300));
        Assert.Equal(300L, stalledFor);
    }

    /// <summary>The exact shape of the incident: real progress, then a freeze.</summary>
    [Fact(DisplayName = "progress followed by a freeze is caught, and the clock starts at the freeze")]
    public void ProgressFollowedByAFreezeIsCaught()
    {
        var detector = new StallDetector<Progress>(Limit);
        detector.Sample(At(1000), Seconds(0));
        detector.Sample(At(2000), Seconds(100));
        detector.Sample(At(2860), Seconds(200)); // last real movement

        Assert.Null(detector.Sample(At(2860), Seconds(400)));
        Assert.Equal(300L, detector.Sample(At(2860), Seconds(500)));
    }

    /// <summary>
    /// An unreadable spooler is not evidence of anything. Treating it as no
    /// progress would abandon healthy jobs whenever the print service hiccups.
    /// </summary>
    [Fact(DisplayName = "a spooler that cannot be read never counts towards a stall")]
    public void ASpoolerThatCannotBeReadNeverCountsTowardsAStall()
    {
        var detector = new StallDetector<Progress>(Limit);
        detector.Sample(At(500), Seconds(0));
        for (var tick = 1; tick <= 40; tick++) Assert.Null(detector.Sample(null, Seconds(tick * 15L)));
    }

    /// <summary>
    /// A job leaving the queue is a change like any other - it must not read as
    /// a freeze.
    /// </summary>
    [Fact(DisplayName = "a job disappearing from the queue restarts the clock rather than tripping it")]
    public void AJobDisappearingFromTheQueueRestartsTheClock()
    {
        var detector = new StallDetector<Progress>(Limit);
        detector.Sample(At(2860), Seconds(0));
        Assert.Null(detector.Sample(At(0, found: false), Seconds(400)));
        Assert.Null(detector.Sample(At(0, found: false), Seconds(500)));
    }

    /// <summary>
    /// The first observation can never be a stall: there is nothing yet to have
    /// stalled from.
    /// </summary>
    [Fact(DisplayName = "the first sample is never a stall however late it arrives")]
    public void TheFirstSampleIsNeverAStallHoweverLateItArrives()
    {
        Assert.Null(new StallDetector<Progress>(Limit).Sample(At(0), Seconds(100_000)));
    }
}
