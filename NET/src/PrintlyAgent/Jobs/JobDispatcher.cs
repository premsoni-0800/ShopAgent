using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PrintlyAgent.Jobs;

/// <summary>
/// Runs print jobs concurrently, and - just as importantly - gets them off the
/// thread that discovered them.
///
/// Port of jobs/JobDispatcher.kt.
///
/// Every job used to be processed inline by whoever found it. The SSE listener
/// blocked *on the HTTP reader thread*, so for as long as a job took to
/// download, print and have its outcome confirmed by the spooler - up to
/// jobTimeoutSeconds, five minutes - that connection read nothing. A second
/// order placed during a print was therefore not merely printed late, it was not
/// *delivered* until the first job finished, and the stream itself could time
/// out waiting. The reconciliation loop had the matching flaw: it awaited each
/// job in turn, so its interval only started counting after the whole backlog
/// had drained one at a time.
///
/// So dispatch is fire-and-forget. Discovering work is instant, and the work
/// itself runs on the thread pool, maxConcurrent at a time.
///
/// Two independent guards stop the same job running twice, which matters more
/// here than anywhere else in the agent - the failure mode is a customer's
/// document printed twice, on paper, at their expense:
///
///  - <c>_inFlight</c> rejects a second submission of an id that is still
///    running. This is what makes the 10-second reconciliation poll safe: it
///    re-lists the same outstanding job on every pass while that job is
///    mid-print, and every one of those passes after the first is dropped here.
///  - Database.InsertJobReference rejects an id ever seen before, in a single
///    atomic statement. That is the durable guard, and it outlives restarts.
///
/// The bound exists because concurrency stops helping well before it stops
/// costing: each job holds a downloaded PDF and a spooler poller, and a shop has
/// few enough physical printers that a dozen at once would just queue in the
/// driver anyway.
/// </summary>
public sealed class JobDispatcher : IDisposable
{
    private readonly ILogger _log;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    /// <summary>
    /// The subset of in-flight work the print queue must not start a sheet
    /// ahead of - references it has never seen.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _holding = new();

    public JobDispatcher(ILogger log, int maxConcurrent)
    {
        _log = log;
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrent));
    }

    /// <summary>Visible for the UI and tests - how many jobs are running or queued right now.</summary>
    public int ActiveCount => _inFlight.Count;

    /// <summary>
    /// How much in-flight work the print queue must wait for before it starts a
    /// sheet - see PrintQueue.AwaitIntakeDrainedAsync.
    ///
    /// Deliberately not <see cref="ActiveCount"/>, which is what the queue used
    /// to wait on. Intake re-examines references the queue has *already* got,
    /// over and over, for as long as the backend keeps listing them; none of
    /// that can change what prints next, and waiting for it left the printer
    /// standing idle for seconds at a time between sheets, all the way through
    /// a backlog.
    /// </summary>
    public int HoldsPrintingCount => _holding.Count;

    /// <summary>
    /// Queues <paramref name="work"/> for <paramref name="jobId"/> unless that
    /// id is already running, and returns immediately either way. Never throws:
    /// a caller is an event listener or a polling loop, and neither has anywhere
    /// sensible to put an exception.
    ///
    /// <paramref name="holdsPrinting"/> marks work the print queue must not
    /// start a sheet ahead of - a reference it has never seen, which might still
    /// belong in front of what is waiting. Everything else runs without holding
    /// anything up.
    /// </summary>
    public bool Submit(string jobId, Func<Task> work, bool holdsPrinting = false)
    {
        if (!_inFlight.TryAdd(jobId, 0))
        {
            _log.LogDebug("job_already_in_flight job={JobId}", jobId);
            return false;
        }

        if (holdsPrinting) _holding.TryAdd(jobId, 0);

        // Deliberately not awaited - that is the whole point of the class. The
        // discarded task cannot throw out of here because everything inside is
        // caught below.
        _ = Task.Run(async () =>
        {
            try
            {
                await _slots.WaitAsync().ConfigureAwait(false);
                try
                {
                    await work().ConfigureAwait(false);
                }
                finally
                {
                    _slots.Release();
                }
            }
            catch (Exception exc)
            {
                // ProcessJob already reports its own failures to the backend;
                // reaching here means something outside it broke. Swallowing is
                // deliberate - one bad job must not take the dispatcher down.
                _log.LogError(exc, "job_dispatch_failed job={JobId}", jobId);
            }
            finally
            {
                _inFlight.TryRemove(jobId, out _);

                // However the work ended. A count left behind here holds the
                // printer for ever, which is a worse failure than the one the
                // hold exists to prevent.
                _holding.TryRemove(jobId, out _);
            }
        });

        return true;
    }

    public void Dispose() => _slots.Dispose();
}
