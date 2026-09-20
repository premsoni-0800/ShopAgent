using PrintlyAgent.Core;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// What starts a printer, and what does not.
///
/// Printly is a scan-at-counter service: the student uploads and pays from
/// wherever they are, walks into the shop, and scans the QR on the counter. The
/// scan is the trigger. Every case below is really the same assertion from a
/// different angle - no scan, no paper.
///
/// This file used to test the opposite, and that history is worth keeping
/// because it is what the rule now defends against. An order with no release
/// time meant "print it the moment it is claimed", so a student ordering from
/// their room had a printout on the counter minutes later: going cold, paid for
/// in the shop's own paper and toner, and collected only if the student
/// actually turned up. Two earlier bugs made it worse by making every order
/// look like that one - a lookup that answered null on any failure, and a read
/// of the wrong field - so both are still pinned below, in the form they take
/// now: a failure must never be the thing that starts a printer.
/// </summary>
public class SchedulePlanTests
{
    private static void AssertPrintsNow(SchedulePlan plan)
    {
        var printAt = Assert.IsType<SchedulePlan.PrintAt>(plan);
        Assert.Null(printAt.At);
    }

    private static void AssertWaitsForScan(SchedulePlan plan) =>
        Assert.IsType<SchedulePlan.AwaitCounterScan>(plan);

    /// <summary>
    /// The change this whole flow exists for. A paid order that has just landed
    /// is not work to do, it is work to be ready for.
    /// </summary>
    [Fact(DisplayName = "an order nobody has scanned for does not print")]
    public void AnOrderNobodyHasScannedForDoesNotPrint()
    {
        AssertWaitsForScan(Scheduling.PlanFor(new ScheduleLookup.Known(null)));
    }

    [Fact(DisplayName = "the counter scan is what starts the printer")]
    public void TheCounterScanIsWhatStartsThePrinter()
    {
        AssertPrintsNow(Scheduling.PlanFor(new ScheduleLookup.Known(null, Priority: true)));
    }

    /// <summary>
    /// The release time was the old trigger and is no longer consulted at all -
    /// neither to start a print nor to delay one. A student at the counter is at
    /// the counter whatever a slot once said, and one who has not scanned gets
    /// nothing however overdue that slot is.
    /// </summary>
    [Fact(DisplayName = "a release time decides nothing on its own")]
    public void AReleaseTimeDecidesNothingOnItsOwn()
    {
        // Long past, which under the old rule printed immediately.
        AssertWaitsForScan(Scheduling.PlanFor(new ScheduleLookup.Known("2026-09-12T09:00:00Z")));
        // Hours away, which under the old rule was held back for the slot.
        AssertWaitsForScan(Scheduling.PlanFor(new ScheduleLookup.Known("2026-09-12T17:55:00Z")));
        // With a scan, that same order prints now rather than waiting for it.
        AssertPrintsNow(Scheduling.PlanFor(new ScheduleLookup.Known("2026-09-12T17:55:00Z", Priority: true)));
    }

    [Fact(DisplayName = "an unreadable time is not a reason to print")]
    public void AnUnreadableTimeIsNotAReasonToPrint()
    {
        AssertWaitsForScan(Scheduling.PlanFor(new ScheduleLookup.Known("tomorrow-ish")));
        AssertWaitsForScan(Scheduling.PlanFor(new ScheduleLookup.Known("")));
    }

    /// <summary>
    /// A failure that might clear is not an answer about anything. Held without
    /// being recorded, so the ten-second reconciliation poll brings it back to
    /// be asked again.
    /// </summary>
    [Fact(DisplayName = "a lookup that might succeed later is held unrecorded")]
    public void ALookupThatMightSucceedLaterIsHeldUnrecorded()
    {
        Assert.IsType<SchedulePlan.Hold>(Scheduling.PlanFor(ScheduleLookup.Unavailable.Instance));
    }

    /// <summary>
    /// The reversal. A refusal that will not clear - the order is gone, or the
    /// owner is signed out - used to print, on the reasoning that never printing
    /// at all is a real harm too. Under scan-at-counter it is not: printing here
    /// puts paper in the tray for a student the agent cannot confirm ever asked
    /// for it. Waiting costs nothing, and a scan still resolves it.
    /// </summary>
    [Fact(DisplayName = "a refusal waits for the scan rather than printing")]
    public void ARefusalWaitsForTheScanRatherThanPrinting()
    {
        AssertWaitsForScan(Scheduling.PlanFor(ScheduleLookup.Refused.Instance));
    }
}
