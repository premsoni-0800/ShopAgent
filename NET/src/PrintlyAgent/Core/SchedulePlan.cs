namespace PrintlyAgent.Core;

/// <summary>What the backend could tell the agent about an order's slot.</summary>
public abstract record ScheduleLookup
{
    /// <summary>
    /// The server answered. <see cref="Known.Priority"/> is the one that decides
    /// anything: true when the student has scanned the shop's QR at the counter
    /// and the backend has agreed to serve them next. Nothing prints without it.
    ///
    /// <see cref="Known.ReleaseAt"/> is when this shop was due to receive the
    /// order, from the days of scheduled printing. Still read off the response,
    /// still worth having in a log, no longer consulted by
    /// <see cref="Scheduling.PlanFor"/>.
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

    /// <summary>
    /// Record the job, and print nothing until the student scans at the counter.
    ///
    /// Distinct from <see cref="Hold"/> in the one way that matters: this one
    /// <em>is</em> written down. Holding without recording is for a question the
    /// agent could not get an answer to, and it works because the reconciliation
    /// poll re-asks within ten seconds. Waiting for a scan is not a question at
    /// all - the answer is known and it is "not yet" - and it can last an hour
    /// while the student walks over. Re-asking the backend about it every ten
    /// seconds for that whole time, per order, is exactly the flood
    /// PriorityCandidates exists to avoid.
    ///
    /// So the row goes in as RECEIVED with priority 0, which is the state the
    /// rotating recheck already looks for, and the scan is noticed there.
    /// </summary>
    public sealed record AwaitCounterScan : SchedulePlan
    {
        public static readonly AwaitCounterScan Instance = new();
    }
}

public static class Scheduling
{
    /// <summary>
    /// Turns what the lookup found into what the agent should do.
    ///
    /// One rule now: nothing prints until the student scans the shop's QR at the
    /// counter. Printly is a scan-at-counter service - the student uploads, pays,
    /// walks in, and scans - so a paid order arriving here is not a job to do, it
    /// is a job to be ready for. The scan is the whole trigger.
    ///
    /// This reverses what used to be here, and the reversal is the point. An
    /// order with no release time used to mean "print it the moment it is
    /// claimed", so a student who ordered from their room had a printout sitting
    /// on the counter minutes later - going cold, paid for by the shop in paper
    /// and toner, and possibly never collected at all. That is the exact waste
    /// the shop owner is being shown a fix for.
    ///
    /// <para>
    /// The release time is no longer consulted. It answered a question nobody
    /// asks any more ("when is this shop due to receive it?"), and a scanned
    /// student is at the counter <em>now</em> whatever a slot once said. The
    /// backend still sends it and <see cref="ScheduleLookup.Known.ReleaseAt"/>
    /// still carries it, because rows written by an older agent before an
    /// upgrade are drained by the scheduled path on the way through - but no new
    /// decision is made from it.
    /// </para>
    /// </summary>
    public static SchedulePlan PlanFor(ScheduleLookup lookup) => lookup switch
    {
        // Might clear. Record nothing and let the reconciliation poll ask again -
        // an agent that cannot reach its backend has not learned that a student
        // has not scanned, it has learned nothing.
        ScheduleLookup.Unavailable => SchedulePlan.Hold.Instance,

        // Will not clear - the order is gone, or the owner is signed out. This
        // used to print, on the reasoning that never printing at all is a real
        // harm too. Under scan-at-counter it is not: printing here puts paper in
        // a tray for a student the agent cannot confirm ever asked for it, and
        // the job is visible in the shop's own queue either way. Waiting wastes
        // nothing and can be resolved by the student simply scanning.
        ScheduleLookup.Refused => SchedulePlan.AwaitCounterScan.Instance,

        // The student is standing at the counter. This is the only thing that
        // starts a printer.
        ScheduleLookup.Known { Priority: true } => new SchedulePlan.PrintAt(null),

        ScheduleLookup.Known => SchedulePlan.AwaitCounterScan.Instance,

        _ => SchedulePlan.AwaitCounterScan.Instance,
    };
}
