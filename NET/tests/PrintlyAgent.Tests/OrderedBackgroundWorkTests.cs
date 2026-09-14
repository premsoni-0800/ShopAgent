using System.Collections.Concurrent;
using PrintlyAgent.Jobs;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// The two properties that make it safe to stop waiting for a progress ping.
/// </summary>
public class OrderedBackgroundWorkTests
{
    [Fact(DisplayName = "posting does not wait for the work")]
    public async Task PostingReturnsBeforeTheWorkDoes()
    {
        var work = new OrderedBackgroundWork();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        work.Post(async () =>
        {
            started.SetResult();
            await release.Task;
        });

        // The whole point: this line is reached while the posted work is still
        // sitting there. If Post waited, the test would hang here instead.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        release.SetResult();
        await work.Drain().WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Order is the reason this exists rather than a bare discard. A job's
    /// DOWNLOADING and PRINTING pings can be posted moments apart, and if the
    /// second overtakes the first the shop is told the job went back to
    /// downloading after it started printing.
    /// </summary>
    [Fact(DisplayName = "work runs in the order it was posted, however slow the earlier piece is")]
    public async Task WorkRunsInOrder()
    {
        var work = new OrderedBackgroundWork();
        var finished = new ConcurrentQueue<int>();
        var holdUpTheFirst = new TaskCompletionSource();

        // The first piece cannot complete until told to, so anything that ran
        // concurrently would finish ahead of it and be caught here.
        work.Post(async () =>
        {
            await holdUpTheFirst.Task;
            finished.Enqueue(1);
        });
        for (var i = 2; i <= 5; i++)
        {
            var n = i;
            work.Post(() =>
            {
                finished.Enqueue(n);
                return Task.CompletedTask;
            });
        }

        Assert.Empty(finished);
        holdUpTheFirst.SetResult();
        await work.Drain().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, finished.ToArray());
    }

    /// <summary>
    /// A ping that throws must not take the rest of the chain with it. These
    /// are fire-and-forget by design, so a poisoned chain would silently stop
    /// every later report with nobody holding the exception to notice.
    /// </summary>
    [Fact(DisplayName = "work that throws does not stop what was posted after it")]
    public async Task AFailureDoesNotPoisonTheChain()
    {
        var work = new OrderedBackgroundWork();
        var ran = false;

        work.Post(() => throw new InvalidOperationException("the backend was unreachable"));
        work.Post(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await work.Drain().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ran, "a failed report stopped every later one");
    }
}
