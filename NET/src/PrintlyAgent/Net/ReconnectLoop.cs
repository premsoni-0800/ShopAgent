using Microsoft.Extensions.Logging;

namespace PrintlyAgent.Net;

/// <summary>
/// Shared reconnect-with-backoff shape used by both SSE clients (print jobs and
/// order-change events) - exponential backoff with jitter so a downed backend is
/// never hammered by a tight retry loop, and a clean disconnect resets the
/// attempt counter.
///
/// Port of net/ReconnectLoop.kt. The Kotlin version is a suspend function taking
/// a suspend lambda; the shape here is the same with Task and CancellationToken,
/// which is what the rest of this port uses in place of coroutines.
/// </summary>
public static class ReconnectLoop
{
    public static async Task RunAsync(
        ILogger log,
        double baseDelaySeconds,
        double maxDelaySeconds,
        Func<bool> isStopped,
        Func<CancellationToken, Task> connectOnce,
        CancellationToken cancellation = default)
    {
        var attempt = 0;
        while (!isStopped() && !cancellation.IsCancellationRequested)
        {
            try
            {
                await connectOnce(cancellation).ConfigureAwait(false);
                attempt = 0; // a clean disconnect (server-initiated) resets backoff
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Shutting down is not a connection failure and must not be
                // retried or logged as one.
                return;
            }
            catch (Exception exc)
            {
                log.LogWarning(exc, "connection_failed attempt={Attempt}", attempt);
            }

            if (isStopped() || cancellation.IsCancellationRequested) return;

            attempt += 1;
            var delaySeconds = Math.Min(maxDelaySeconds, baseDelaySeconds * Math.Pow(2.0, attempt - 1));
            // 50-100% of the computed delay, so a fleet of agents that all lost
            // the same backend do not return in lockstep.
            var jittered = delaySeconds * (0.5 + Random.Shared.NextDouble() / 2);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(jittered * 1000), cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
