using System.Globalization;
using PrintlyAgent.Core;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// When a scheduled order actually prints, and what happens when the agent
/// cannot find out. Port of SchedulePlanTest.kt.
///
/// Two things went wrong here before, and both are pinned below.
///
/// The lookup used to catch every exception and answer null, and null already
/// meant "not scheduled, print it now" - so a momentary 500 or a timed-out token
/// read as "this order is not scheduled" and a six o'clock order printed at
/// eleven in the morning.
///
/// And it read the wrong field: scheduledSlotStart, a booked collection slot
/// that nothing in this system sets, rather than the Schedule Print time the
/// student actually chose. Every scheduled order looked unscheduled.
/// </summary>
public class SchedulePlanTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-09-12T11:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// The instant the Kotlin compares against, spelled the way .NET round-trips
    /// it. Kotlin's Instant.toString() emits "2026-09-12T17:55:00Z"; .NET's "O"
    /// format emits full sub-second precision. Both are the same instant, so the
    /// assertion is on the parsed value rather than on the spelling - comparing
    /// strings here would be testing the formatter, not the rule.
    /// </summary>
    private static void AssertPrintsAt(string expectedIso, SchedulePlan plan)
    {
        var printAt = Assert.IsType<SchedulePlan.PrintAt>(plan);
        Assert.NotNull(printAt.At);
        Assert.Equal(
            DateTimeOffset.Parse(expectedIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(printAt.At!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static void AssertPrintsNow(SchedulePlan plan)
    {
        var printAt = Assert.IsType<SchedulePlan.PrintAt>(plan);
        Assert.Null(printAt.At);
    }

    /// <summary>
    /// The whole of the arithmetic, and the agent does none of it. The backend
    /// subtracts its own release lead from the student's chosen time and sends
    /// the answer; taking it whole is what stops a fourth copy of "five minutes"
    /// drifting away from the other three.
    /// </summary>
    [Fact(DisplayName = "the agent prints when the backend says the shop receives it")]
    public void TheAgentPrintsWhenTheBackendSaysTheShopReceivesIt()
    {
        var plan = Scheduling.PlanFor(new ScheduleLookup.Known("2026-09-12T17:55:00Z"), Now);
        AssertPrintsAt("2026-09-12T17:55:00Z", plan);
    }

    [Fact(DisplayName = "a Print Now order prints as soon as it is claimed")]
    public void APrintNowOrderPrintsAsSoonAsItIsClaimed()
    {
        AssertPrintsNow(Scheduling.PlanFor(new ScheduleLookup.Known(null), Now));
    }

    /// <summary>
    /// The shop was due this already. Holding it back would be the opposite of
    /// the point.
    /// </summary>
    [Fact(DisplayName = "a release time already upon us prints now")]
    public void AReleaseTimeAlreadyUponUsPrintsNow()
    {
        AssertPrintsNow(Scheduling.PlanFor(new ScheduleLookup.Known("2026-09-12T11:00:00Z"), Now));
        AssertPrintsNow(Scheduling.PlanFor(new ScheduleLookup.Known("2026-09-12T09:00:00Z"), Now));
    }

    [Fact(DisplayName = "an unreadable time is treated as unscheduled rather than guessed at")]
    public void AnUnreadableTimeIsTreatedAsUnscheduled()
    {
        AssertPrintsNow(Scheduling.PlanFor(new ScheduleLookup.Known("tomorrow-ish"), Now));
        AssertPrintsNow(Scheduling.PlanFor(new ScheduleLookup.Known(""), Now));
    }

    /// <summary>
    /// A failure that might clear must not be allowed to look like "not
    /// scheduled" - the job is held, unrecorded, and the ten-second
    /// reconciliation poll brings it back to be asked again.
    /// </summary>
    [Fact(DisplayName = "a lookup that might succeed later holds the job instead of printing it")]
    public void ALookupThatMightSucceedLaterHoldsTheJob()
    {
        Assert.IsType<SchedulePlan.Hold>(Scheduling.PlanFor(ScheduleLookup.Unavailable.Instance, Now));
    }

    /// <summary>
    /// The other side of it. Never printing at all is a real harm too, so a
    /// refusal that will not clear - the order is gone, or the owner is signed
    /// out - prints rather than holding for ever.
    /// </summary>
    [Fact(DisplayName = "a refusal that will not clear prints rather than holding for ever")]
    public void ARefusalThatWillNotClearPrintsRatherThanHolding()
    {
        AssertPrintsNow(Scheduling.PlanFor(ScheduleLookup.Refused.Instance, Now));
    }

    /// <summary>Priority rides along on the same lookup, and does not disturb the timing.</summary>
    [Fact(DisplayName = "priority is independent of when it prints")]
    public void PriorityIsIndependentOfWhenItPrints()
    {
        AssertPrintsAt(
            "2026-09-12T17:55:00Z",
            Scheduling.PlanFor(new ScheduleLookup.Known("2026-09-12T17:55:00Z", Priority: true), Now));
    }
}
