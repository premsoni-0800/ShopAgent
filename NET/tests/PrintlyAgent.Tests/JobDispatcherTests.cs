using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Jobs;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Port of JobDispatcherTest.kt.
///
/// The two properties the dispatcher exists for, and the one it must never
/// lose: jobs overlap, submission never blocks the caller, and the same job
/// never runs twice.
/// </summary>
public class JobDispatcherTests
{
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<bool> condition, int timeoutMillis = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMillis) throw new TimeoutException("condition never became true");
            await Task.Delay(5);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen) return;
        } while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    [Fact(DisplayName = "jobs actually run concurrently")]
    public async Task JobsActuallyRunConcurrently()
    {
        using var dispatcher = new JobDispatcher(NullLogger.Instance, maxConcurrent: 3);
        var running = 0;
        var peak = 0;
        var allStarted = Gate();
        var release = Gate();

        for (var i = 0; i < 3; i++)
        {
            dispatcher.Submit($"job-{i}", async () =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                if (now == 3) allStarted.TrySetResult();
                await release.Task;
                Interlocked.Decrement(ref running);
            });
        }

        // Serial execution could never get all three into the block at once, so
        // this await is the whole assertion - it simply would not return.
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, Volatile.Read(ref peak));
        release.SetResult();
    }

    [Fact(DisplayName = "concurrency is bounded by maxConcurrent")]
    public async Task ConcurrencyIsBoundedByMaxConcurrent()
    {
        using var dispatcher = new JobDispatcher(NullLogger.Instance, maxConcurrent: 2);
        var running = 0;
        var peak = 0;

        for (var i = 0; i < 8; i++)
        {
            dispatcher.Submit($"job-{i}", async () =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                await Task.Delay(50);
                Interlocked.Decrement(ref running);
            });
        }

        await UntilAsync(() => dispatcher.ActiveCount == 0);
        Assert.Equal(2, Volatile.Read(ref peak));
    }

    /// <summary>
    /// The reconciliation poll re-lists an outstanding job every 10 seconds for
    /// as long as it takes to print, so this is the ordinary case, not an edge
    /// one - and the consequence of getting it wrong is a customer's document
    /// printed twice.
    /// </summary>
    [Fact(DisplayName = "a job already in flight is never started again")]
    public async Task AJobAlreadyInFlightIsNeverStartedAgain()
    {
        using var dispatcher = new JobDispatcher(NullLogger.Instance, maxConcurrent: 4);
        var runs = 0;
        var release = Gate();

        var first = dispatcher.Submit("same-job", async () =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
        });
        await UntilAsync(() => Volatile.Read(ref runs) > 0, 5_000);

        var second = dispatcher.Submit("same-job", () => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
        var third = dispatcher.Submit("same-job", () => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        Assert.True(first, "the first submission should be accepted");
        Assert.False(second, "a redelivery while still running must be dropped");
        Assert.False(third);
        Assert.Equal(1, Volatile.Read(ref runs));
        release.SetResult();
    }

    /// <summary>
    /// Once it has finished, the id is free again - a genuine retry must not be
    /// blocked forever.
    /// </summary>
    [Fact(DisplayName = "the same id can be submitted again after it finishes")]
    public async Task TheSameIdCanBeSubmittedAgainAfterItFinishes()
    {
        using var dispatcher = new JobDispatcher(NullLogger.Instance, maxConcurrent: 2);
        var runs = 0;

        dispatcher.Submit("job", () => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
        await UntilAsync(() => dispatcher.ActiveCount == 0, 5_000);

        Assert.True(dispatcher.Submit("job", () => { Interlocked.Increment(ref runs); return Task.CompletedTask; }));
        await UntilAsync(() => dispatcher.ActiveCount == 0, 5_000);
        Assert.Equal(2, Volatile.Read(ref runs));
    }

    /// <summary>
    /// A thrown job must not poison the dispatcher, and must still free its slot
    /// and its id.
    /// </summary>
    [Fact(DisplayName = "a failing job releases its slot and does not stop later jobs")]
    public async Task AFailingJobReleasesItsSlotAndDoesNotStopLaterJobs()
    {
        using var dispatcher = new JobDispatcher(NullLogger.Instance, maxConcurrent: 1);
        var completed = 0;

        dispatcher.Submit("boom", () => throw new InvalidOperationException("pipeline blew up"));
        dispatcher.Submit("fine", () => { Interlocked.Increment(ref completed); return Task.CompletedTask; });

        await UntilAsync(() => dispatcher.ActiveCount == 0, 5_000);
        Assert.Equal(1, Volatile.Read(ref completed));
    }
    /// <summary>
    /// What the print queue waits for. It used to wait on ActiveCount - any
    /// intake at all - and the reconciliation poll keeps intake busy almost
    /// continuously by re-examining references that are already in the print
    /// queue. Those cannot sort ahead of anything, so waiting for them just left
    /// the printer idle between sheets, over and over, for the length of a
    /// backlog.
    /// </summary>
    [Fact(DisplayName = "only work marked as holding printing is counted against the printer")]
    public async Task OnlyWorkMarkedAsHoldingPrintingIsCountedAgainstThePrinter()
    {
        using var dispatcher = new JobDispatcher(NullLogger.Instance, maxConcurrent: 4);
        var started = Gate();
        var release = Gate();

        dispatcher.Submit("recheck-1", async () => await release.Task);
        dispatcher.Submit("recheck-2", async () => await release.Task);
        await WaitUntil(() => dispatcher.ActiveCount >= 2);

        Assert.Equal(2, dispatcher.ActiveCount);
        Assert.Equal(0, dispatcher.HoldsPrintingCount);

        dispatcher.Submit("brand-new", async () =>
        {
            started.TrySetResult();
            await release.Task;
        }, holdsPrinting: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, dispatcher.HoldsPrintingCount);

        release.TrySetResult();
        await WaitUntil(() => dispatcher.ActiveCount == 0);
        Assert.Equal(0, dispatcher.HoldsPrintingCount);
    }

    /// <summary>
    /// A count left behind here holds the printer for ever - a worse failure
    /// than the one it exists to prevent - so it has to survive every way a
    /// submission can end.
    /// </summary>
    [Fact(DisplayName = "the printer hold is released however the work ends")]
    public async Task ThePrinterHoldIsReleasedHoweverTheWorkEnds()
    {
        using var dispatcher = new JobDispatcher(NullLogger.Instance, maxConcurrent: 4);

        dispatcher.Submit("explodes", () => throw new InvalidOperationException("lookup blew up"), holdsPrinting: true);
        dispatcher.Submit("returns", () => Task.CompletedTask, holdsPrinting: true);

        // A redelivery of an id already in flight is refused, and must not leave
        // a hold behind on its way out either.
        var running = Gate();
        var release = Gate();
        Assert.True(dispatcher.Submit("busy", async () =>
        {
            running.TrySetResult();
            await release.Task;
        }, holdsPrinting: true));
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(dispatcher.Submit("busy", () => Task.CompletedTask, holdsPrinting: true));
        release.TrySetResult();

        await WaitUntil(() => dispatcher.ActiveCount == 0);
        Assert.Equal(0, dispatcher.HoldsPrintingCount);
    }

    /// <summary>Polls rather than sleeping a fixed time, so a slow machine does not fail it.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.ElapsedMilliseconds < 5_000, "the dispatcher never reached the expected state");
            await Task.Delay(5);
        }
    }

}
