using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PrintlyAgent.Jobs;

/// <summary>
/// The shop's print queue: one order at a time, lowest order number first.
///
/// Port of jobs/PrintQueue.kt.
///
/// What this replaces mattered on a busy counter. Printing was dispatched by
/// JobDispatcher, which bounds how many run at once but says nothing about
/// *which* runs next - waiters take the slot in the order they happened to
/// arrive. Arrival order is the order the server's stream delivered them, or the
/// order a reconciliation query returned, neither of which is the order the
/// customers are standing in. With a backlog of large orders the shop printed
/// them in an order nobody could predict or explain, and the person holding
/// order 31 watched 47 come out.
///
/// Ordering is by the number in the order code - <c>HH-000032</c> sorts as 32 -
/// so the queue matches what is written on the receipt. Ahead of all of it sits
/// in-shop priority: a student who has walked to the counter and scanned the
/// shop's QR is standing there waiting, and the backend has already agreed to
/// serve them next.
///
/// Priority jobs are ordered among themselves by when they arrived here, not by
/// their number. Two people at the counter are a queue of two people, and the
/// one who scanned first is standing at the front of it - their order number
/// says only when they placed the order, which may have been yesterday.
///
/// Discovery is deliberately *not* done here. Fetching an order's details is an
/// HTTP call, and doing that inside the print slot meant the next order could
/// not even be recorded until the current one had finished printing.
///
/// <para>
/// Porting note: Kotlin's PriorityBlockingQueue plus a Channel of permits
/// becomes a plain priority list guarded by a lock, plus a SemaphoreSlim used
/// the same way - one permit released per enqueue, workers waiting on it rather
/// than spinning. .NET's PriorityQueue is neither thread-safe nor enumerable in
/// order, and this class has to answer Waiting() in queue order for the UI, so
/// it is not a drop-in.
/// </para>
/// </summary>
public sealed class PrintQueue : IAsyncDisposable
{
    /// <summary>
    /// Never hold the printer longer than this for intake. Generous, because it
    /// is a backstop against intake wedging rather than a routine limit - a
    /// backlog of a hundred references is handed over in well under a second.
    /// </summary>
    public const long MaxIntakeWaitMillisDefault = 20_000L;

    /// <summary>How often to re-ask whether intake is still holding something.</summary>
    private const int IntakePollMillis = 25;

    /// <summary>
    /// How far ahead documents are fetched. Two is enough to keep a printer fed
    /// - one printing, one ready, one being fetched - without holding a claim
    /// and a customer's file for every order in a backlog.
    /// </summary>
    private const int PrepareLookahead = 2;

    private readonly ILogger _log;
    private readonly Func<bool> _intakeBusy;

    /// <summary>
    /// Told about jobs whose turn is coming, so their document can be fetched
    /// while the printer is still busy with the one before.
    /// </summary>
    private readonly Action<string> _prepareAhead;

    /// <summary>Jobs already handed to <see cref="_prepareAhead"/>, so each is offered once.</summary>
    private readonly HashSet<string> _preparing = new(StringComparer.Ordinal);
    private readonly long _maxIntakeWaitMillis;
    private readonly Action _onDepthChanged;

    private readonly object _gate = new();
    private readonly List<Entry> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _running = new();

    /// <summary>
    /// Every id that is queued or running. The duplicate guard, and the reason
    /// the ten-second reconciliation poll is safe: it re-lists the same job on
    /// every pass while that job waits its turn, and every pass after the first
    /// is dropped here rather than printing a customer's document twice.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _known = new();

    /// <summary>One permit per queued job. Workers wait on this rather than spinning.</summary>
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>Ticks once per enqueue, so priority jobs can be ordered by when they got here.</summary>
    private long _arrivals;

    /// <summary>Order code against job id, for everything printing right now - for the UI.</summary>
    private readonly ConcurrentDictionary<string, string> _runningOrders = new();

    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = new();

    public PrintQueue(
        ILogger log,
        int workers,
        Func<bool>? intakeBusy = null,
        Action<string>? prepareAhead = null,
        long maxIntakeWaitMillis = MaxIntakeWaitMillisDefault,
        Action? onDepthChanged = null)
    {
        _log = log;
        _intakeBusy = intakeBusy ?? (() => false);
        _prepareAhead = prepareAhead ?? (_ => { });
        _maxIntakeWaitMillis = maxIntakeWaitMillis;
        _onDepthChanged = onDepthChanged ?? (() => { });

        for (var i = 0; i < Math.Max(1, workers); i++)
        {
            _workers.Add(Task.Run(() => WorkerLoopAsync(_shutdown.Token)));
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellation)
    {
        // Set once maxIntakeWaitMillis has cut a wait short, and cleared when
        // intake is next seen idle. Without it, intake that stays busy would
        // cost a full wait before every single sheet rather than once.
        var gaveUpOnIntake = false;

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_intakeBusy())
            {
                gaveUpOnIntake = false;
            }
            else if (PriorityWaiting())
            {
                // Somebody is at the counter and their job is already in hand -
                // see PriorityWaiting for why waiting for intake here buys
                // nothing and costs them the wait.
                _log.LogDebug(
                    "print_queue_intake_wait_skipped_for_priority depth={Depth}", _known.Count);
            }
            else if (!gaveUpOnIntake)
            {
                gaveUpOnIntake = !await AwaitIntakeDrainedAsync(cancellation).ConfigureAwait(false);
            }

            var entry = Poll();
            if (entry is null) continue;

            // Start fetching what comes next, now, while this one is printing.
            // Printing stays strictly one at a time - there is one printer - but
            // the claim and the download in front of it are network waits, and
            // paying them only once the previous sheet has landed left the
            // printer idle for the length of a download on every order. On a run
            // of small jobs that was most of the wall clock.
            //
            // A lookahead of a few rather than the whole queue: each prepared
            // job holds a claim and a customer's document on disk until it
            // prints, and a backlog of two hundred would mean two hundred of
            // both.
            PrepareUpcoming();

            _running[entry.JobId] = 0;
            _runningOrders[entry.JobId] = entry.OrderCode ?? entry.JobId;
            _onDepthChanged();
            try
            {
                await entry.Work().ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                // ProcessJob reports its own failures; reaching here means
                // something outside it broke. One bad job must not stop the
                // queue draining.
                _log.LogError(exc, "print_queue_job_failed job={JobId}", entry.JobId);
            }
            finally
            {
                _running.TryRemove(entry.JobId, out _);
                _runningOrders.TryRemove(entry.JobId, out _);
                _known.TryRemove(entry.JobId, out _);
                // Alongside the others, and for the same reason they are here.
                // This only exists to offer each job for preparation once;
                // keeping the id after the job is done turns a set that should
                // be a handful deep into one entry for every order the agent has
                // ever seen, for as long as it runs.
                lock (_gate) _preparing.Remove(entry.JobId);
                _onDepthChanged();
            }
        }
    }

    private Entry? Poll()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return null;
            var best = 0;
            for (var i = 1; i < _pending.Count; i++)
            {
                if (_pending[i].CompareTo(_pending[best]) < 0) best = i;
            }
            var entry = _pending[best];
            _pending.RemoveAt(best);
            return entry;
        }
    }

    /// <summary>
    /// Adds <paramref name="jobId"/> to the queue unless it is already queued or
    /// printing, and returns immediately either way. Never throws: callers are
    /// event listeners and polling loops with nowhere to put an exception.
    /// </summary>
    public bool Enqueue(string jobId, string? orderCode, bool priority, Func<Task> work)
    {
        if (!_known.TryAdd(jobId, 0))
        {
            _log.LogDebug("job_already_queued job={JobId}", jobId);
            return false;
        }

        var entry = new Entry(
            OrderSequence(orderCode),
            Interlocked.Increment(ref _arrivals),
            jobId,
            orderCode,
            priority,
            work);

        lock (_gate) _pending.Add(entry);
        _signal.Release();

        _log.LogInformation(
            "print_job_queued job={JobId} order={Order} priority={Priority} depth={Depth}",
            jobId, orderCode ?? "?", priority, _known.Count);
        _onDepthChanged();

        // Also here, not only when a worker takes a job. Orders arriving during
        // a long print are exactly the ones worth fetching early - waiting for
        // the next poll would mean fetching them only once the printer was free,
        // which is the wait this exists to remove. A job that turns out to be
        // next anyway loses nothing: preparation is memoised, so the worker gets
        // the same result rather than doing it again.
        PrepareUpcoming();
        return true;
    }

    /// <summary>
    /// Moves a job already waiting into the priority group, because the student
    /// has since walked to the counter and scanned.
    ///
    /// Re-inserted rather than mutated: priority is a sort key, and changing one
    /// in place inside an ordered collection corrupts the ordering. Both halves
    /// happen under the same lock so no worker can see the queue one entry short
    /// and spend a permit on nothing.
    ///
    /// It takes a fresh arrival number on the way in, which is what puts it
    /// behind anyone already at the counter rather than ahead of them - the
    /// scan that just happened is the latest one, not the earliest.
    ///
    /// Does nothing for a job that is already printing (there is nothing ahead
    /// of it left to move it past), already priority, or not here at all.
    /// </summary>
    public bool Promote(string jobId)
    {
        string? orderCode;
        lock (_gate)
        {
            var at = _pending.FindIndex(e => e.JobId == jobId && !e.Priority);
            // Not waiting: either already printing, already priority, or gone -
            // and a worker having just taken it is the outcome promotion was
            // after anyway.
            if (at < 0) return false;

            var entry = _pending[at];
            _pending.RemoveAt(at);
            _pending.Add(new Entry(
                entry.Sequence, Interlocked.Increment(ref _arrivals),
                entry.JobId, entry.OrderCode, true, entry.Work));
            orderCode = entry.OrderCode;
        }

        // Outside the lock: this reaches the shop's screen, and the screen is
        // not something to hold a queue lock across.
        _log.LogInformation(
            "print_job_promoted job={JobId} order={Order} depth={Depth}",
            jobId, orderCode ?? "?", _known.Count);
        _onDepthChanged();
        return true;
    }

    /// <summary>Queued or printing - what the UI shows as outstanding work.</summary>
    public int Depth => _known.Count;

    /// <summary>Printing right now.</summary>
    public int ActiveCount => _running.Count;

    private List<Entry> SortedPending()
    {
        lock (_gate)
        {
            var copy = new List<Entry>(_pending);
            copy.Sort();
            return copy;
        }
    }

    /// <summary>Waiting order codes, in the order they will print. For the UI and for tests.</summary>
    public IReadOnlyList<string> Waiting() =>
        SortedPending().Select(e => e.OrderCode ?? e.JobId).ToList();

    /// <summary>The waiting order codes that jumped the queue by scanning at the counter.</summary>
    public IReadOnlyList<string> WaitingPriority() =>
        SortedPending().Where(e => e.Priority).Select(e => e.OrderCode ?? e.JobId).ToList();

    /// <summary>Order codes on a printer right now - what the shop's screen shows in green.</summary>
    public IReadOnlyList<string> Printing() =>
        _runningOrders.Values.OrderBy(v => v, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Whether somebody who scanned at the counter is waiting for the printer.
    ///
    /// Cheap and exact: priority is the outermost key in Entry.CompareTo, so the
    /// head of the queue is a priority entry if and only if one is waiting at
    /// all.
    ///
    /// This is what lets a scan take the next sheet straight away. The intake
    /// wait below exists so that sorting *by order number* sees every candidate
    /// - a number still being fetched might belong ahead of the one in hand. A
    /// priority entry is not sorted by number: it outranks every ordinary job
    /// whatever intake is still holding, and priority entries are ordered among
    /// themselves by when they reached this queue, which is already settled for
    /// one that is here. So the wait cannot change what prints next, and paying
    /// it only leaves the student standing at the counter for up to
    /// maxIntakeWaitMillis - twenty seconds, in front of the person who just
    /// served them - while the agent thinks about orders nobody is waiting on.
    ///
    /// The one thing it can cost is a second scan that is mid-intake going
    /// second instead of first; two people at the counter sorting by a hair is
    /// worth far less than neither of them being served.
    /// </summary>
    /// <summary>
    /// Offers the next few waiting jobs for preparation, each exactly once.
    ///
    /// Read off the same sorted order the queue will actually print in, so the
    /// work goes to the jobs that are genuinely next - preparing by arrival
    /// order would fetch for a job that a counter scan is about to overtake.
    /// </summary>
    private void PrepareUpcoming()
    {
        List<string> upcoming;
        lock (_gate)
        {
            upcoming = SortedPending().Take(PrepareLookahead)
                .Select(e => e.JobId)
                .Where(id => _preparing.Add(id))
                .ToList();
        }

        foreach (var jobId in upcoming)
        {
            _log.LogInformation("print_job_preparing_ahead job={JobId}", jobId);
            _prepareAhead(jobId);
        }
    }

    private bool PriorityWaiting()
    {
        // "Is any entry priority?" rather than "is the sorted head priority?".
        // The two are the same question - priority is the outermost sort key -
        // and this one answers it without sorting the queue. Kotlin gets the
        // head off a heap for nothing; here the equivalent would be an O(n log
        // n) sort on a path walked before every sheet.
        lock (_gate)
        {
            foreach (var entry in _pending)
            {
                if (entry.Priority) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Holds the printer while intake still has orders it has not handed over.
    ///
    /// Sorting only ever orders what is already in the queue, and intake runs
    /// while printing does. A backlog does not arrive all at once - intake
    /// fetches each order in turn - so without this the first reference to be
    /// registered goes on the printer before the rest have been looked up, and
    /// comes out ahead of lower numbers still on their way. Observed exactly
    /// that: HH-000108 printed first out of a backlog starting at HH-000025.
    ///
    /// The question being asked is "does intake still hold something?", and
    /// intakeBusy answers it directly - JobDispatcher.ActiveCount, the count of
    /// references fetched but not yet enqueued. An earlier cut inferred the
    /// answer from arrival timing instead, waiting for a lull, and timing turns
    /// out to answer a different question badly: a shop taking orders steadily
    /// never falls quiet, so every sheet waited out the whole cap, while a
    /// backlog landing mid-print was never waited for at all because the queue
    /// was not empty when it arrived.
    ///
    /// Returns true if intake drained, false if the cap cut the wait short -
    /// because intake that somehow never finishes must not hold the printer for
    /// ever.
    /// </summary>
    private async Task<bool> AwaitIntakeDrainedAsync(CancellationToken cancellation)
    {
        // A stopwatch, not the wall clock: this is a duration, and a counter PC
        // resyncing its clock mid-wait must not extend or cut it.
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (_intakeBusy())
        {
            // A scan landing mid-wait ends the wait, rather than making the
            // person who just scanned stand there for the rest of it. Returns
            // true because nothing is wrong with intake - the wait is simply no
            // longer the right thing to be doing - so the next ordinary sheet
            // waits for it again as usual.
            if (PriorityWaiting())
            {
                _log.LogInformation(
                    "print_queue_intake_wait_cut_for_priority waited={Waited}ms depth={Depth}",
                    started.ElapsedMilliseconds, _known.Count);
                return true;
            }

            if (started.ElapsedMilliseconds >= _maxIntakeWaitMillis)
            {
                _log.LogWarning(
                    "print_queue_intake_wait_capped waited={Waited}ms depth={Depth}",
                    _maxIntakeWaitMillis, _known.Count);
                return false;
            }
            try
            {
                await Task.Delay(IntakePollMillis, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        var waitedMillis = started.ElapsedMilliseconds;
        if (waitedMillis > 0)
        {
            _log.LogInformation(
                "print_queue_waited_for_intake waited={Waited}ms depth={Depth}", waitedMillis, _known.Count);
        }
        return true;
    }

    /// <summary>
    /// Stops the workers, but never waits for one for ever.
    ///
    /// Cancelling releases a worker that is idle on the signal; it does NOT
    /// release one that is inside a job, because the job is somebody else's code
    /// and may be blocked on anything - a spooler that never answers, a
    /// download that never finishes. Waiting unconditionally on those turned a
    /// single stuck job into a process that would not exit: a test whose
    /// assertion failed before it released its gate left a worker parked for
    /// ever, and the whole suite hung with no output rather than reporting the
    /// one failure.
    ///
    /// So shutdown is bounded. A worker still holding a job is left to the
    /// process teardown, which is the honest outcome - the alternative is
    /// hanging, and hanging tells nobody anything.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("print_queue_shutdown_timed_out active={Active}", _running.Count);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        _shutdown.Dispose();
        _signal.Dispose();
    }

    /// <summary>
    /// The number in an order code, used to sort the queue: <c>HH-000032</c> is
    /// 32, <c>PPP01-000063</c> is 63.
    ///
    /// Codes carry a per-shop prefix and an agent serves one shop, so comparing
    /// the numeric tail alone is safe here. Anything unreadable sorts last - a
    /// job with no number should wait behind every job that has one, not jump
    /// ahead of them.
    /// </summary>
    internal static long OrderSequence(string? orderCode)
    {
        if (string.IsNullOrEmpty(orderCode)) return long.MaxValue;

        var end = orderCode.Length;
        var start = end;
        while (start > 0 && char.IsDigit(orderCode[start - 1])) start--;
        if (start == end) return long.MaxValue;

        return long.TryParse(orderCode.AsSpan(start, end - start), out var value) ? value : long.MaxValue;
    }

    private sealed class Entry : IComparable<Entry>
    {
        public long Sequence { get; }
        public long Arrival { get; }
        public string JobId { get; }
        public string? OrderCode { get; }
        public bool Priority { get; }
        public Func<Task> Work { get; }

        public Entry(long sequence, long arrival, string jobId, string? orderCode, bool priority, Func<Task> work)
        {
            Sequence = sequence;
            Arrival = arrival;
            JobId = jobId;
            OrderCode = orderCode;
            Priority = priority;
            Work = work;
        }

        /// <summary>
        /// Priority first. Then, within each group, the key that actually
        /// describes the queue those jobs are in.
        ///
        /// Priority is deliberately the outermost key rather than a bonus
        /// applied to the number: the point of it is that somebody is standing
        /// at the counter, and that outranks every order not yet collected,
        /// however low its number.
        ///
        /// Among priority jobs the key is arrival, not the number. Two people at
        /// the counter are a queue of two people, and the one who scanned first
        /// is at the front of it; their order number says only when they placed
        /// the order, which may have been yesterday. Among everything else the
        /// key is still the number, because that is the queue the receipts
        /// describe.
        ///
        /// Job id breaks a tie either way, so the order is total and stable.
        /// </summary>
        public int CompareTo(Entry? other)
        {
            if (other is null) return -1;
            if (Priority != other.Priority) return Priority ? -1 : 1;

            if (Priority)
            {
                var byArrival = Arrival.CompareTo(other.Arrival);
                if (byArrival != 0) return byArrival;
            }
            else
            {
                var bySequence = Sequence.CompareTo(other.Sequence);
                if (bySequence != 0) return bySequence;
            }

            return string.CompareOrdinal(JobId, other.JobId);
        }
    }
}
