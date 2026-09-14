using System.Globalization;

namespace PrintlyAgent.Core;

/// <summary>What the backend could tell the agent about an order's slot.</summary>
public abstract record ScheduleLookup
{
    /// <summary>
    /// The server answered. <see cref="Known.ReleaseAt"/> is when this shop is
    /// meant to receive the order - null on a Print Now order - and
    /// <see cref="Known.Priority"/> is true when the student has scanned the
    /// shop's QR at the counter and the backend has agreed to serve them next.
    ///
    /// <see cref="Known.OrderCode"/> is the shop-facing order number -
    /// PPP01-000079, the thing on the customer's receipt - read off the same
    /// response as the other two. It is here because the SSE push that starts
    /// most jobs carries only a job id and an order uuid, so without it the
    /// queue has no name to show for the job it is about to print.
    /// </para>
    /// </summary>
    public sealed record Known(string? ReleaseAt, bool Priority = false, string? OrderCode = null) : ScheduleLookup;

    /// <summary>
    /// It could not be asked, and asking again shortly might work - a 5xx, a
    /// timeout, a dropped connection.
    /// </summary>
    public sealed record Unavailable : ScheduleLookup
    {
        public static readonly Unavailable Instance = new();
    }

    /// <summary>
    /// It refused, and will refuse again - a 404 for an order that is gone, or
    /// a 401 once the owner is signed out.
    /// </summary>
    public sealed record Refused : ScheduleLookup
    {
        public static readonly Refused Instance = new();
    }
}

/// <summary>What to do about it.</summary>
public abstract record SchedulePlan
{
    /// <summary>
    /// Record the job and print it at <see cref="PrintAt.At"/>; null means as
    /// soon as it is claimed.
    /// </summary>
    public sealed record PrintAt(string? At) : SchedulePlan;

    /// <summary>
    /// Establish nothing and record nothing, so the reconciliation poll asks
    /// again.
    /// </summary>
    public sealed record Hold : SchedulePlan
    {
        public static readonly Hold Instance = new();
    }
}

public static class Scheduling
{
    /// <summary>
    /// Turns what the lookup found into what the agent should do.
    ///
    /// Printing early and never printing at all are both real harms, so they are
    /// weighed rather than one being picked outright. A failure that might clear
    /// holds the job: the reconciliation poll re-lists it within ten seconds,
    /// and a scheduled order is by definition one with time to spare. A failure
    /// that will not clear prints it now, because the alternative there is an
    /// order that never comes out at all.
    ///
    /// There is deliberately no lead time here. The backend already subtracted
    /// its own from the student's chosen time and sent the answer as
    /// `shopReleaseAt`, so the agent takes that whole rather than keeping a
    /// number of its own. A copy would be a fourth: the backend has
    /// printly.scheduled-print.release-lead, the student app has
    /// SHOP_RELEASE_LEAD, and the dashboard has PRINT_LEAD_MINUTES. That shape
    /// has already failed once - the student app sat at twenty minutes while the
    /// server released at five, so a student asking for 7:00 was told 6:40 and
    /// the shop got it at 6:55. An agent that derives nothing cannot drift.
    ///
    /// A release time already upon us is not a schedule, it is a job to print
    /// now - which is also what an unscheduled order, or one with an unreadable
    /// time, deserves.
    /// </summary>
    public static SchedulePlan PlanFor(ScheduleLookup lookup, DateTimeOffset now) => lookup switch
    {
        ScheduleLookup.Unavailable => SchedulePlan.Hold.Instance,
        ScheduleLookup.Refused => new SchedulePlan.PrintAt(null),
        ScheduleLookup.Known known => PlanForKnown(known, now),
        _ => new SchedulePlan.PrintAt(null),
    };

    private static SchedulePlan PlanForKnown(ScheduleLookup.Known known, DateTimeOffset now)
    {
        if (known.ReleaseAt is null) return new SchedulePlan.PrintAt(null);

        // Round-trip parsing only. An unreadable instant is treated exactly as
        // "not scheduled" rather than as a failure, because the alternative -
        // holding the job - would strand an order on a typo the shop cannot see
        // or fix.
        if (!DateTimeOffset.TryParse(
                known.ReleaseAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal,
                out var releaseAt))
        {
            return new SchedulePlan.PrintAt(null);
        }

        return releaseAt > now
            ? new SchedulePlan.PrintAt(releaseAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            : new SchedulePlan.PrintAt(null);
    }
}
