using System.Diagnostics;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Port of SpoolerOutcomePollerTest.kt, all 15 cases.
///
/// PollJobOutcomeAsync's decision logic, without touching WinSpool - mirrors the
/// Python agent's <c>test_print_outcome_polling.py</c>, which monkeypatches
/// <c>_job_status</c> the same way this injects <c>statusLookup</c>. No printer
/// is involved at any point, on purpose: this is the part that decides whether a
/// student is told their document printed, and it has to be testable on a laptop
/// with nothing plugged in.
/// </summary>
public class SpoolerOutcomePollerTests
{
    private const int PausedBit = 0x00000001;
    private const int ErrorBit = 0x00000002;
    private const int PrintingBit = 0x00000010; // informational only, never decisive alone
    private const int OfflineBit = 0x00000020;
    private const int PaperOutBit = 0x00000040;
    private const int PrintedBit = 0x00000080;
    private const int DeletedBit = 0x00000100;
    private const int UserInterventionBit = 0x00000400;

    /// <summary>The job is in the queue with these status bits set.</summary>
    private static SpoolerOutcomePoller.JobStatus InQueue(int bits) => new(bits, true);

    /// <summary>The job is not in the queue at all.</summary>
    private static SpoolerOutcomePoller.JobStatus NotInQueue() => new(0, false);

    /// <summary>The spooler could not be asked - the null that must never become 0.</summary>
    private static SpoolerOutcomePoller.JobStatus Unreachable() => new(null, false);

    private static Task<SpoolerOutcome> PollAsync(
        Func<string, string, SpoolerOutcomePoller.JobStatus> statusLookup,
        double timeoutSeconds = 5.0,
        Action<PrinterCondition?>? onCondition = null) =>
        SpoolerOutcomePoller.PollJobOutcomeAsync(
            "Printer", "job-1", timeoutSeconds, 0.0, statusLookup,
            // No real device behind these: the tests drive the job's status
            // directly, so the printer has nothing to add.
            deviceConditionLookup: _ => null,
            onCondition: onCondition);

    [Fact(DisplayName = "a job stuck on an unreachable printer is withdrawn and reported not printed")]
    public async Task AJobStuckOnAnUnreachablePrinterIsWithdrawn()
    {
        var withdrawn = 0;
        var result = await SpoolerOutcomePoller.PollJobOutcomeAsync(
            "Printer", "job-1", stallSeconds: 60, pollIntervalSeconds: 0.0,
            statusLookup: (_, _) => InQueue(PrintingBit),
            deviceConditionLookup: _ => PrinterCondition.NOT_REACHABLE,
            unreachableSeconds: 0.05,
            cancelJob: (_, _) => { withdrawn++; return true; });

        Assert.Equal(PrintOutcome.FAILED, result.Outcome);
        Assert.Equal(PrinterCondition.NOT_REACHABLE, result.Condition);
        Assert.Equal(1, withdrawn);
    }

    [Fact(DisplayName = "an unreachable job that cannot be withdrawn stays unknown")]
    public async Task AnUnreachableJobThatCannotBeWithdrawnStaysUnknown()
    {
        // Still queued, so it may yet print - never FAILED on a guess.
        var result = await SpoolerOutcomePoller.PollJobOutcomeAsync(
            "Printer", "job-1", stallSeconds: 60, pollIntervalSeconds: 0.0,
            statusLookup: (_, _) => InQueue(OfflineBit),
            deviceConditionLookup: _ => null,
            unreachableSeconds: 0.05,
            cancelJob: (_, _) => false);

        Assert.Equal(PrintOutcome.UNKNOWN, result.Outcome);
        Assert.Equal(PrinterCondition.OFFLINE, result.Condition);
    }

    [Fact(DisplayName = "an unreachable printer that has already printed pages is not withdrawn")]
    public async Task AnUnreachablePrinterMidJobIsNotWithdrawn()
    {
        var withdrawn = 0;
        var result = await SpoolerOutcomePoller.PollJobOutcomeAsync(
            "Printer", "job-1", stallSeconds: 0.3, pollIntervalSeconds: 0.0,
            statusLookup: (_, _) => new SpoolerOutcomePoller.JobStatus(OfflineBit, true, PagesPrinted: 3),
            deviceConditionLookup: _ => null,
            unreachableSeconds: 0.05,
            cancelJob: (_, _) => { withdrawn++; return true; });

        Assert.Equal(PrintOutcome.UNKNOWN, result.Outcome);
        Assert.Equal(0, withdrawn);
    }

    [Fact(DisplayName = "printed bit is completed")]
    public async Task PrintedBitIsCompleted()
    {
        var result = await PollAsync((_, _) => InQueue(PrintedBit));
        Assert.Equal(PrintOutcome.COMPLETED, result.Outcome);
    }

    [Fact(DisplayName = "error bit is failed")]
    public async Task ErrorBitIsFailed()
    {
        var result = await PollAsync((_, _) => InQueue(ErrorBit));
        Assert.Equal(PrintOutcome.FAILED, result.Outcome);
    }

    [Fact(DisplayName = "a job cancelled out of the queue is failed")]
    public async Task AJobCancelledOutOfTheQueueIsFailed()
    {
        var result = await PollAsync((_, _) => InQueue(DeletedBit));
        Assert.Equal(PrintOutcome.FAILED, result.Outcome);
    }

    [Fact(DisplayName = "job disappearing from the queue with no prior error is completed")]
    public async Task JobDisappearingFromTheQueueWithNoPriorErrorIsCompleted()
    {
        // The common case: a driver that removes a job from its queue the
        // instant it finishes, rather than leaving a PRINTED bit to observe.
        var result = await PollAsync((_, _) => NotInQueue());
        Assert.Equal(PrintOutcome.COMPLETED, result.Outcome);
    }

    [Fact(DisplayName = "job disappearing after a seen error is failed not completed")]
    public async Task JobDisappearingAfterASeenErrorIsFailedNotCompleted()
    {
        var call = 0;
        var result = await PollAsync((_, _) =>
        {
            call += 1;
            return call == 1 ? InQueue(ErrorBit) : NotInQueue();
        });

        Assert.Equal(PrintOutcome.FAILED, result.Outcome);
    }

    [Fact(DisplayName = "spooler unreachable is unknown never a guess")]
    public async Task SpoolerUnreachableIsUnknownNeverAGuess()
    {
        // Bounded by the stall window rather than answered on the first
        // sample: a spooler that is briefly unreadable must not abandon a job,
        // but one that never becomes readable must not be polled for ever.
        var result = await PollAsync((_, _) => Unreachable(), timeoutSeconds: 0.0);
        Assert.Equal(PrintOutcome.UNKNOWN, result.Outcome);
    }

    [Fact(DisplayName = "still printing at the deadline is unknown not completed")]
    public async Task StillPrintingAtTheDeadlineIsUnknownNotCompleted()
    {
        // Never resolves, never errors - just still going when time runs out.
        var result = await PollAsync((_, _) => InQueue(PrintingBit), timeoutSeconds: 0.0);
        Assert.Equal(PrintOutcome.UNKNOWN, result.Outcome);
    }

    // -----------------------------------------------------------------------
    // Printer problems a person can fix. None of these ends the job: Windows
    // holds it in the queue and prints it once the condition clears, so
    // reporting a failure here is how a shop prints a document twice.
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "out of paper is never reported as a failure")]
    public async Task OutOfPaperIsNeverReportedAsAFailure()
    {
        var result = await PollAsync((_, _) => InQueue(PaperOutBit), timeoutSeconds: 0.0);

        Assert.Equal(PrintOutcome.UNKNOWN, result.Outcome);
        Assert.Equal(PrinterCondition.OUT_OF_PAPER, result.Condition);
    }

    [Fact(DisplayName = "a printer needing attention is never reported as a failure")]
    public async Task APrinterNeedingAttentionIsNeverReportedAsAFailure()
    {
        var result = await PollAsync((_, _) => InQueue(UserInterventionBit), timeoutSeconds: 0.0);

        Assert.Equal(PrintOutcome.UNKNOWN, result.Outcome);
        Assert.Equal(PrinterCondition.NEEDS_ATTENTION, result.Condition);
    }

    [Fact(DisplayName = "an offline or paused printer is never reported as a failure")]
    public async Task AnOfflineOrPausedPrinterIsNeverReportedAsAFailure()
    {
        var offline = await PollAsync((_, _) => InQueue(OfflineBit), timeoutSeconds: 0.0);
        Assert.Equal(PrinterCondition.OFFLINE, offline.Condition);

        var paused = await PollAsync((_, _) => InQueue(PausedBit), timeoutSeconds: 0.0);
        Assert.Equal(PrinterCondition.PAUSED, paused.Condition);
    }

    /// <summary>
    /// The whole point of waiting rather than failing: somebody loads paper and
    /// the job prints itself.
    /// </summary>
    [Fact(DisplayName = "a job that was out of paper and then prints is completed")]
    public async Task AJobThatWasOutOfPaperAndThenPrintsIsCompleted()
    {
        var call = 0;
        var result = await PollAsync((_, _) =>
        {
            call += 1;
            return call <= 3 ? InQueue(PaperOutBit) : InQueue(PrintedBit);
        });

        Assert.Equal(PrintOutcome.COMPLETED, result.Outcome);
        // A job that printed has nothing outstanding to fix.
        Assert.Null(result.Condition);
    }

    [Fact(DisplayName = "the condition is reported as it happens, not only at the end")]
    public async Task TheConditionIsReportedAsItHappensNotOnlyAtTheEnd()
    {
        var seen = new List<PrinterCondition?>();
        await PollAsync(
            (_, _) => InQueue(PaperOutBit),
            timeoutSeconds: 0.0,
            onCondition: condition => seen.Add(condition));

        Assert.Equal(new List<PrinterCondition?> { PrinterCondition.OUT_OF_PAPER }, seen);
    }

    /// <summary>
    /// Only on a change - a job stuck for five minutes must not emit a
    /// notification per poll.
    /// </summary>
    [Fact(DisplayName = "an unchanged condition is reported once")]
    public async Task AnUnchangedConditionIsReportedOnce()
    {
        var seen = new List<PrinterCondition?>();
        var call = 0;
        await PollAsync(
            (_, _) =>
            {
                call += 1;
                return InQueue(PaperOutBit);
            },
            timeoutSeconds: 0.02,
            onCondition: condition => seen.Add(condition));

        Assert.True(call >= 1);
        Assert.Equal(new List<PrinterCondition?> { PrinterCondition.OUT_OF_PAPER }, seen);
    }

    /// <summary>
    /// Paper is what the person at the printer sees first, whatever else the
    /// queue also reports.
    /// </summary>
    [Fact(DisplayName = "out of paper wins over a blocked queue")]
    public async Task OutOfPaperWinsOverABlockedQueue()
    {
        const int blockedDevQ = 0x00000200;
        var result = await PollAsync((_, _) => InQueue(PaperOutBit | blockedDevQ), timeoutSeconds: 0.0);

        Assert.Equal(PrinterCondition.OUT_OF_PAPER, result.Condition);
    }

    /// <summary>
    /// A real driver error still ends the job, even alongside a
    /// recoverable-looking bit.
    /// </summary>
    [Fact(DisplayName = "a driver error beats a recoverable condition")]
    public async Task ADriverErrorBeatsARecoverableCondition()
    {
        var result = await PollAsync((_, _) => InQueue(ErrorBit | PaperOutBit));
        Assert.Equal(PrintOutcome.FAILED, result.Outcome);
    }
    // -----------------------------------------------------------------------
    // Long jobs. The rule is about movement, not elapsed time: this used to be
    // a flat deadline, and a 162-page order - six minutes or so of real
    // printing - was recorded UNKNOWN while its pages were still coming out,
    // then put in front of the shop as "did it print?".
    // -----------------------------------------------------------------------

    /// <summary>A job in the queue whose spooler page count has reached <paramref name="pages"/>.</summary>
    private static SpoolerOutcomePoller.JobStatus Printing(int pages) =>
        new(PrintingBit, true, pages);

    [Fact(DisplayName = "a long job is never given up on while its pages keep climbing")]
    public async Task ALongJobIsNeverGivenUpOnWhileItsPagesKeepClimbing()
    {
        // The window is deliberately far shorter than the job. Under the old
        // rule - give up this long after starting, whatever is happening - this
        // is UNKNOWN several times over. Under a stall rule it is a healthy job.
        const double stallWindowSeconds = 0.5;
        var clock = Stopwatch.StartNew();
        var pages = 0;

        var result = await PollAsync(
            (_, _) =>
            {
                // Keeps printing for well past the window, then finishes.
                if (clock.Elapsed.TotalSeconds < stallWindowSeconds * 4) return Printing(++pages);
                return NotInQueue();
            },
            timeoutSeconds: stallWindowSeconds);

        Assert.Equal(PrintOutcome.COMPLETED, result.Outcome);
        Assert.True(
            clock.Elapsed.TotalSeconds > stallWindowSeconds,
            "the test did not actually run past the window, so it proves nothing");
        Assert.True(pages > 1, "the page count never climbed, so no progress was ever observed");
    }

    [Fact(DisplayName = "a job whose page count stops climbing is given up on")]
    public async Task AJobWhosePageCountStopsClimbingIsGivenUpOn()
    {
        // Same shape as above, and the only difference is that the count sticks.
        // That difference alone has to decide the outcome.
        var result = await PollAsync((_, _) => Printing(42), timeoutSeconds: 0.25);

        Assert.Equal(PrintOutcome.UNKNOWN, result.Outcome);
    }

    /// <summary>
    /// Over a twenty-minute print the spooler is asked hundreds of times. One
    /// unreadable answer in the middle of that is a blip, not a verdict - and it
    /// must never be read as "the job has left the queue", which is the branch
    /// that means it printed.
    /// </summary>
    [Fact(DisplayName = "one unreadable sample does not abandon, or complete, a healthy job")]
    public async Task OneUnreadableSampleDoesNotAbandonAHealthyJob()
    {
        var samples = 0;
        var pages = 0;

        var result = await PollAsync(
            (_, _) =>
            {
                samples++;
                if (samples == 2) return Unreachable();
                if (samples < 6) return Printing(++pages);
                return NotInQueue();
            },
            timeoutSeconds: 5.0);

        Assert.Equal(PrintOutcome.COMPLETED, result.Outcome);
    }

}
