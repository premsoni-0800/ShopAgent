namespace PrintlyAgent.Jobs;

/// <summary>
/// Runs work that nobody is waiting for, one piece at a time, in the order it
/// was handed over.
///
/// For the things the agent tells the backend that do not change what the agent
/// does - progress pings, and the like. Awaiting those puts a round trip to a
/// cold-starting host on a path where a printer is sitting idle; loosing them
/// with a bare discard instead lets two land out of order, and a job reported
/// as downloading after it started printing is worse than one reported late.
///
/// So: hand it over and carry on, but it goes out behind whatever was handed
/// over before it.
///
/// Not a queue with a worker, because that is a thread to own and shut down for
/// a few messages a minute. A chain of continuations is the same guarantee with
/// nothing to run: when the chain is idle the tail is a completed task and the
/// next piece starts immediately.
/// </summary>
internal sealed class OrderedBackgroundWork
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>
    /// Queues work behind anything already queued, and returns at once.
    /// </summary>
    /// <remarks>
    /// The work is expected to swallow its own failures - this has no one to
    /// report them to. A faulted task would otherwise poison the chain, so the
    /// continuation deliberately ignores the state of its antecedent and is
    /// registered to run whatever happened to it.
    /// </remarks>
    public void Post(Func<Task> work)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(
                    _ => Swallow(work),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private static async Task Swallow(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch
        {
            // By contract. See Post.
        }
    }

    /// <summary>
    /// Waits for everything queued so far to finish. For shutdown and for
    /// tests; the ordinary path never calls it.
    /// </summary>
    public Task Drain()
    {
        lock (_gate) return _tail;
    }
}
