using PrintlyAgent.Core;
using PrintlyAgent.Db;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// What a re-delivered job reference is worth spending a request on.
///
/// Port of core/ReferenceWorkTest.kt.
///
/// The reconciliation poll re-lists everything the backend still considers
/// outstanding, every ten seconds, for ever. Each of those used to cost a full
/// order lookup *before* anything checked whether there was anything to find out
/// - the duplicate guard sits downstream of the request, not in front of it. A
/// shop with a backlog paid that for every waiting job on every pass; a shop
/// with a few UNKNOWN jobs waiting on somebody to walk over and resolve them
/// paid it for those for ever, because a job waiting on a human is outstanding
/// for as long as the human takes. Everything else the agent needed the backend
/// for queued behind it, including the shop's own screen.
/// </summary>
public class ReferenceWorkTests
{
    private static JobRow Row(string state, bool priority = false) => new(
        JobId: "job-1",
        OrderId: "order-1",
        OrderCode: "HH-000001",
        State: state,
        PrinterWindowsName: null,
        AttemptCount: 0,
        LastError: null,
        ReceivedAt: "2026-01-01T00:00:00Z",
        UpdatedAt: "2026-01-01T00:00:00Z",
        ScheduledPrintAt: null,
        Priority: priority);

    [Fact(DisplayName = "a reference never seen here is new work")]
    public void AReferenceNeverSeenHereIsNewWork() =>
        Assert.Equal(ReferenceWork.New, AgentCore.ReferenceWorkFor(null));

    /// <summary>
    /// And only this one holds the printer: a reference already in the queue
    /// cannot sort ahead of anything, because it is already there.
    /// </summary>
    [Fact(DisplayName = "a job still working its way through is only worth re-asking about")]
    public void AJobStillWorkingItsWayThroughIsOnlyWorthReAskingAbout()
    {
        foreach (var state in new[]
                 { "RECEIVED", "VALIDATING", "DOWNLOADING", "DOWNLOADED", "SUBMITTING", "SUBMITTED", "PRINTING" })
        {
            Assert.Equal(ReferenceWork.Recheck, AgentCore.ReferenceWorkFor(Row(state)));
        }
    }

    /// <summary>Nothing left to decide.</summary>
    [Fact(DisplayName = "a finished job is not worth a request")]
    public void AFinishedJobIsNotWorthARequest()
    {
        foreach (var state in new[] { "COMPLETED", "FAILED", "CANCELLED" })
        {
            Assert.Equal(ReferenceWork.None, AgentCore.ReferenceWorkFor(Row(state)));
        }
    }

    /// <summary>
    /// The expensive one. UNKNOWN stays outstanding on the backend until a human
    /// resolves it, so it is re-listed on every pass indefinitely - and locally
    /// it is terminal, so there was never anything to do with it.
    /// </summary>
    [Fact(DisplayName = "a job waiting on a human is not re-asked about for ever")]
    public void AJobWaitingOnAHumanIsNotReAskedAboutForEver() =>
        Assert.Equal(ReferenceWork.None, AgentCore.ReferenceWorkFor(Row("UNKNOWN")));

    /// <summary>Already at the counter - the one thing a re-check could discover is already known.</summary>
    [Fact(DisplayName = "a job that already has priority has nothing left to learn")]
    public void AJobThatAlreadyHasPriorityHasNothingLeftToLearn()
    {
        Assert.Equal(ReferenceWork.None, AgentCore.ReferenceWorkFor(Row("RECEIVED", priority: true)));
        Assert.Equal(ReferenceWork.None, AgentCore.ReferenceWorkFor(Row("DOWNLOADING", priority: true)));
    }
}
