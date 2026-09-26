namespace PrintlyAgent.Printing;

/// <summary>
/// Decides whether a job has stopped moving, kept apart from the threading so
/// the rule can be tested without a printer.
///
/// Port of the StallDetector inside printing/PrintSubmission.kt.
///
/// The rule is deliberately about *change*, not about totals: a job climbing
/// through 3,000 pages is healthy however long it takes, and one whose count has
/// not moved in five minutes is not, whatever it reached. A total time limit
/// would kill large jobs for being large, which is worse than the bug it would
/// be trying to fix.
/// </summary>
/// <typeparam name="TProgress">
/// Whatever the spooler reports for one job. Generic purely so the rule can be
/// exercised without dragging in the Windows spooler - equality is all it needs.
/// </typeparam>
public sealed class StallDetector<TProgress> where TProgress : class
{
    private readonly long _stallSeconds;
    private TProgress? _last;
    private long? _lastChangeAt;

    public StallDetector(long stallSeconds) => _stallSeconds = stallSeconds;

    /// <summary>
    /// Feeds in one observation. Returns how many seconds it has been stuck for
    /// once that passes the limit, or null while it is still moving.
    ///
    /// A null <paramref name="progress"/> means the spooler could not be read at
    /// all, which is not evidence of a stall - an unreadable sample leaves the
    /// clock exactly where it was rather than counting towards giving up on the
    /// job. Treating "I could not see" as "it has stopped" would cancel healthy
    /// jobs whenever the spooler was briefly busy.
    /// </summary>
    /// <param name="nowNanos">
    /// A monotonic reading, not the wall clock: this measures a duration, and a
    /// counter PC resyncing its clock mid-print must not create or erase a stall.
    /// </param>
    public long? Sample(TProgress? progress, long nowNanos)
    {
        if (progress is null) return null;

        if (_lastChangeAt is null || !Equals(progress, _last))
        {
            _last = progress;
            _lastChangeAt = nowNanos;
            return null;
        }

        var stalledFor = (nowNanos - _lastChangeAt.Value) / 1_000_000_000L;
        return stalledFor >= _stallSeconds ? stalledFor : null;
    }
}
