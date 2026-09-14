using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Jobs;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Port of PrintQueueTest.kt, all 19 cases.
///
/// The queue exists so a shop prints in the order its customers are standing in.
/// Before it, printing was bounded but unordered - whichever job reached the
/// semaphore first went next, which is the order the server's stream delivered
/// them, not the order on the receipts.
///
/// Kotlin's CompletableDeferred becomes TaskCompletionSource; runBlocking
/// becomes an async test. Everything else is assertion-for-assertion.
/// </summary>
public class PrintQueueTests : IAsyncLifetime
{
    private readonly List<PrintQueue> _queues = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var queue in _queues) await queue.DisposeAsync();
    }

    private PrintQueue NewQueue(
        int workers = 1,
        Func<bool>? intakeBusy = null,
        Action<string>? prepareAhead = null,
        long maxIntakeWaitMillis = PrintQueue.MaxIntakeWaitMillisDefault,
        Action? onDepthChanged = null)
    {
        var queue = new PrintQueue(
            NullLogger.Instance, workers, intakeBusy, prepareAhead, maxIntakeWaitMillis, onDepthChanged);
        _queues.Add(queue);
        return queue;
    }

    private static string Order(int n) => $"HH-{n:D6}";

    /// <summary>A TaskCompletionSource used as Kotlin's CompletableDeferred&lt;Unit&gt;.</summary>
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task DrainAsync(PrintQueue queue, int timeoutMillis = 5_000)
    {
        var sw = Stopwatch.StartNew();
        while (queue.Depth > 0)
        {
            if (sw.ElapsedMilliseconds > timeoutMillis) throw new TimeoutException("queue did not drain");
            await Task.Delay(10);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, int timeoutMillis = 5_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMillis) throw new TimeoutException("condition never became true");
            await Task.Delay(5);
        }
    }

    [Fact(DisplayName = "orders print lowest number first, whatever order they arrive in")]
    public async Task OrdersPrintLowestNumberFirst()
    {
        var queue = NewQueue();
        var printed = new ConcurrentQueue<string>();
        var gate = Gate();
        var running = Gate();

        // Hold the single worker so everything below queues up behind it,
        // which is the situation this is all about: a backlog. Waited on so the
        // blocker is provably OFF the pending list before Waiting() is read -
        // otherwise this asserts on a queue that still contains it.
        queue.Enqueue("blocker", Order(1), false, () => { running.TrySetResult(); return gate.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        foreach (var n in new[] { 47, 12, 31, 8, 99 })
        {
            var code = Order(n);
            queue.Enqueue($"job-{n}", code, false, () => { printed.Enqueue(code); return Task.CompletedTask; });
        }

        Assert.Equal(
            new[] { 8, 12, 31, 47, 99 }.Select(Order).ToList(),
            queue.Waiting());

        gate.SetResult();
        await DrainAsync(queue);

        Assert.Equal(new[] { 8, 12, 31, 47, 99 }.Select(Order).ToList(), printed.ToList());
    }

    /// <summary>
    /// The point of the separation: an order discovered while a big job is
    /// printing must still get into the queue, and must take its rightful place
    /// rather than the back.
    /// </summary>
    [Fact(DisplayName = "an order that arrives mid-print still takes its place by number")]
    public async Task AnOrderThatArrivesMidPrintStillTakesItsPlaceByNumber()
    {
        var queue = NewQueue();
        var printed = new ConcurrentQueue<string>();
        var gate = Gate();

        queue.Enqueue("big", Order(50), false, () => gate.Task);
        queue.Enqueue("job-80", Order(80), false, () => { printed.Enqueue(Order(80)); return Task.CompletedTask; });
        // Arrives last, belongs first.
        queue.Enqueue("job-60", Order(60), false, () => { printed.Enqueue(Order(60)); return Task.CompletedTask; });

        gate.SetResult();
        await DrainAsync(queue);

        Assert.Equal(new[] { Order(60), Order(80) }, printed.ToList());
    }

    [Fact(DisplayName = "one order at a time")]
    public async Task OneOrderAtATime()
    {
        var queue = NewQueue();
        var running = 0;
        var peak = 0;

        for (var n = 0; n < 6; n++)
        {
            queue.Enqueue($"job-{n}", Order(n), false, async () =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref running);
            });
        }

        await DrainAsync(queue);
        Assert.Equal(1, peak);
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

    [Fact(DisplayName = "the same job is never queued twice")]
    public async Task TheSameJobIsNeverQueuedTwice()
    {
        var queue = NewQueue();
        var runs = 0;
        var gate = Gate();

        Assert.True(queue.Enqueue("job-1", Order(5), false, async () =>
        {
            await gate.Task;
            Interlocked.Increment(ref runs);
        }));
        Assert.False(
            queue.Enqueue("job-1", Order(5), false, () => { Interlocked.Increment(ref runs); return Task.CompletedTask; }),
            "queued twice while waiting");

        gate.SetResult();
        await DrainAsync(queue);

        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact(DisplayName = "enqueue does not block the caller")]
    public async Task EnqueueDoesNotBlockTheCaller()
    {
        var queue = NewQueue();
        var gate = Gate();
        queue.Enqueue("slow", Order(1), false, () => gate.Task);

        var sw = Stopwatch.StartNew();
        for (var n = 0; n < 50; n++) queue.Enqueue($"job-{n}", Order(n + 2), false, () => Task.CompletedTask);
        var elapsed = sw.ElapsedMilliseconds;

        gate.SetResult();
        await DrainAsync(queue);

        Assert.True(elapsed < 1_000,
            $"submission is on a stream reader thread; it must return at once (took {elapsed}ms)");
    }

    /// <summary>One bad job must not stop the queue draining - the shop has other orders.</summary>
    [Fact(DisplayName = "a job that throws does not stall the queue")]
    public async Task AJobThatThrowsDoesNotStallTheQueue()
    {
        var queue = NewQueue();
        var printed = new ConcurrentQueue<string>();

        queue.Enqueue("bad", Order(1), false, () => throw new InvalidOperationException("driver exploded"));
        queue.Enqueue("good", Order(2), false, () => { printed.Enqueue(Order(2)); return Task.CompletedTask; });

        await DrainAsync(queue);
        Assert.Equal(new[] { Order(2) }, printed.ToList());
    }

    /// <summary>
    /// The student is standing at the counter, having scanned the shop's QR, and
    /// the backend has already agreed to serve them next. Sorting the whole
    /// queue by order number put them straight back behind everything else - the
    /// agent quietly overruling the thing the scan exists to do.
    /// </summary>
    [Fact(DisplayName = "a priority order prints before every number waiting")]
    public async Task APriorityOrderPrintsBeforeEveryNumberWaiting()
    {
        var queue = NewQueue();
        var printed = new ConcurrentQueue<string>();
        var gate = Gate();
        var running = Gate();

        queue.Enqueue("blocker", Order(1), false, () => { running.TrySetResult(); return gate.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var n in new[] { 8, 12, 31 })
        {
            var code = Order(n);
            queue.Enqueue($"job-{n}", code, false, () => { printed.Enqueue(code); return Task.CompletedTask; });
        }
        // Arrives last, with the highest number, and still goes first.
        queue.Enqueue("job-99", Order(99), true, () => { printed.Enqueue(Order(99)); return Task.CompletedTask; });

        Assert.Equal(
            new[] { 99, 8, 12, 31 }.Select(Order).ToList(),
            queue.Waiting());

        gate.SetResult();
        await DrainAsync(queue);

        Assert.Equal(new[] { 99, 8, 12, 31 }.Select(Order).ToList(), printed.ToList());
    }

    /// <summary>
    /// Two people at the counter are a queue of two people, and the one who
    /// scanned first is at the front of it. Their order numbers say only when
    /// they placed the orders, which may have been yesterday - 40 scanning
    /// before 20 must still print first.
    /// </summary>
    [Fact(DisplayName = "priority orders print in the order they were scanned")]
    public async Task PriorityOrdersPrintInTheOrderTheyWereScanned()
    {
        var queue = NewQueue();
        var printed = new ConcurrentQueue<string>();
        var gate = Gate();
        var running = Gate();

        queue.Enqueue("blocker", Order(1), false, () => { running.TrySetResult(); return gate.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 40 scans first, then 20 - the higher number is at the front.
        queue.Enqueue("job-40", Order(40), true, () => { printed.Enqueue(Order(40)); return Task.CompletedTask; });
        queue.Enqueue("job-20", Order(20), true, () => { printed.Enqueue(Order(20)); return Task.CompletedTask; });
        queue.Enqueue("job-5", Order(5), false, () => { printed.Enqueue(Order(5)); return Task.CompletedTask; });

        Assert.Equal(new[] { 40, 20, 5 }.Select(Order).ToList(), queue.Waiting());

        gate.SetResult();
        await DrainAsync(queue);
        Assert.Equal(new[] { 40, 20, 5 }.Select(Order).ToList(), printed.ToList());
    }

    /// <summary>Everything not at the counter is still the queue the receipts describe.</summary>
    [Fact(DisplayName = "orders that did not scan keep their number order")]
    public async Task OrdersThatDidNotScanKeepTheirNumberOrder()
    {
        var queue = NewQueue();
        var gate = Gate();
        var running = Gate();

        queue.Enqueue("blocker", Order(1), false, () => { running.TrySetResult(); return gate.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Enqueue("job-47", Order(47), false, () => Task.CompletedTask);
        queue.Enqueue("job-12", Order(12), false, () => Task.CompletedTask);
        queue.Enqueue("job-99", Order(99), true, () => Task.CompletedTask);

        Assert.Equal(new[] { 99, 12, 47 }.Select(Order).ToList(), queue.Waiting());
        gate.SetResult();
        await DrainAsync(queue);
    }

    /// <summary>What the shop's own screen colours: green for printing, blue for the counter.</summary>
    [Fact(DisplayName = "the queue says what is printing and what jumped it")]
    public async Task TheQueueSaysWhatIsPrintingAndWhatJumpedIt()
    {
        var queue = NewQueue();
        var running = Gate();
        var gate = Gate();

        queue.Enqueue("blocker", Order(1), false, () => { running.TrySetResult(); return gate.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        queue.Enqueue("job-30", Order(30), true, () => Task.CompletedTask);
        queue.Enqueue("job-8", Order(8), false, () => Task.CompletedTask);

        Assert.Equal(new[] { Order(1) }, queue.Printing());
        Assert.Equal(new[] { Order(30) }, queue.WaitingPriority());

        gate.SetResult();
        await DrainAsync(queue);
        Assert.Empty(queue.Printing());
    }

    /// <summary>Nothing asked for priority, so nothing gets it.</summary>
    [Fact(DisplayName = "an ordinary queue is unchanged")]
    public async Task AnOrdinaryQueueIsUnchanged()
    {
        var queue = NewQueue();
        var gate = Gate();
        var running = Gate();

        queue.Enqueue("blocker", Order(1), false, () => { running.TrySetResult(); return gate.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var n in new[] { 47, 12, 31 }) queue.Enqueue($"job-{n}", Order(n), false, () => Task.CompletedTask);

        Assert.Equal(new[] { 12, 31, 47 }.Select(Order).ToList(), queue.Waiting());
        gate.SetResult();
        await DrainAsync(queue);
    }

    /// <summary>
    /// The bug this was reported for. A backlog does not arrive all at once -
    /// intake fetches each order in turn - so the first reference registered
    /// used to be on the printer before the rest had been looked up. On the
    /// counter, HH-000108 came out of a backlog that started at HH-000025.
    /// </summary>
    [Fact(DisplayName = "a backlog still arriving is not printed until intake has handed it over")]
    public async Task ABacklogStillArrivingIsNotPrintedUntilIntakeHasHandedItOver()
    {
        var intakeBusy = true;
        var queue = NewQueue(intakeBusy: () => Volatile.Read(ref intakeBusy));
        var printed = new ConcurrentQueue<string>();

        // Intake is working through the backlog; the high number happens to be
        // looked up first.
        queue.Enqueue("job-108", Order(108), false, () => { printed.Enqueue(Order(108)); return Task.CompletedTask; });
        await Task.Delay(100);
        queue.Enqueue("job-25", Order(25), false, () => { printed.Enqueue(Order(25)); return Task.CompletedTask; });
        queue.Enqueue("job-26", Order(26), false, () => { printed.Enqueue(Order(26)); return Task.CompletedTask; });
        Volatile.Write(ref intakeBusy, false);

        await DrainAsync(queue);

        Assert.Equal(
            new[] { Order(25), Order(26), Order(108) },
            printed.ToList());
    }

    /// <summary>
    /// The same backlog, arriving while the printer is busy - which is when a
    /// reconciliation sweep usually finds one. The queue being non-empty says
    /// nothing about whether intake has finished; only intake does.
    /// </summary>
    [Fact(DisplayName = "a backlog that lands while the printer is busy still prints in order")]
    public async Task ABacklogThatLandsWhileThePrinterIsBusyStillPrintsInOrder()
    {
        var intakeBusy = false;
        var queue = NewQueue(intakeBusy: () => Volatile.Read(ref intakeBusy));
        var printed = new ConcurrentQueue<string>();
        var running = Gate();
        var gate = Gate();

        queue.Enqueue("blocker", Order(1), false, () => { running.TrySetResult(); return gate.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Write(ref intakeBusy, true);
        queue.Enqueue("job-108", Order(108), false, () => { printed.Enqueue(Order(108)); return Task.CompletedTask; });
        gate.SetResult();
        await Task.Delay(150); // the rest of the backlog is still being fetched
        queue.Enqueue("job-25", Order(25), false, () => { printed.Enqueue(Order(25)); return Task.CompletedTask; });
        Volatile.Write(ref intakeBusy, false);

        await DrainAsync(queue);

        Assert.Equal(new[] { Order(25), Order(108) }, printed.ToList());
    }

    /// <summary>
    /// Orders arriving one at a time, each fetched and handed over before the
    /// next turns up. Intake is idle in between, so there is nothing to wait for
    /// and the printer must not pause at all.
    /// </summary>
    [Fact(DisplayName = "a trickle of orders is never held up")]
    public async Task ATrickleOfOrdersIsNeverHeldUp()
    {
        var intakeBusy = false;
        var queue = NewQueue(intakeBusy: () => Volatile.Read(ref intakeBusy), maxIntakeWaitMillis: 10_000);
        var printed = 0;

        var sw = Stopwatch.StartNew();
        for (var n = 0; n < 5; n++)
        {
            Volatile.Write(ref intakeBusy, true);
            queue.Enqueue($"job-{n}", Order(n), false, () => { Interlocked.Increment(ref printed); return Task.CompletedTask; });
            Volatile.Write(ref intakeBusy, false);
            await Task.Delay(100);
        }
        await UntilAsync(() => Volatile.Read(ref printed) >= 5);
        var elapsed = sw.ElapsedMilliseconds;

        Assert.True(elapsed < 2_000, $"a trickle must not be made to wait out the cap each time (took {elapsed}ms)");
    }

    /// <summary>Intake that somehow never finishes must not hold the printer for ever.</summary>
    [Fact(DisplayName = "intake that never finishes does not hold the printer past the cap")]
    public async Task IntakeThatNeverFinishesDoesNotHoldThePrinterPastTheCap()
    {
        var queue = NewQueue(intakeBusy: () => true, maxIntakeWaitMillis: 300);
        var done = Gate();

        queue.Enqueue("job-1", Order(1), false, () => { done.TrySetResult(); return Task.CompletedTask; });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// And once it has given up waiting, it keeps printing. Waiting again before
    /// every sheet would turn a busy intake into a stalled printer, which is
    /// worse than not waiting at all.
    /// </summary>
    [Fact(DisplayName = "once the wait is capped the queue keeps printing")]
    public async Task OnceTheWaitIsCappedTheQueueKeepsPrinting()
    {
        var queue = NewQueue(intakeBusy: () => true, maxIntakeWaitMillis: 400);
        var printed = 0;

        for (var n = 0; n < 5; n++)
        {
            queue.Enqueue($"job-{n}", Order(n), false, () => { Interlocked.Increment(ref printed); return Task.CompletedTask; });
        }

        var sw = Stopwatch.StartNew();
        await UntilAsync(() => Volatile.Read(ref printed) >= 5);
        var elapsed = sw.ElapsedMilliseconds;

        Assert.True(elapsed < 1_200, $"the cap should have been paid once, not once per sheet (took {elapsed}ms)");
    }

    /// <summary>
    /// The shop's screen is told when the queue changes shape, not on a timer.
    ///
    /// AgentCore hangs the status push off this callback, so a queue that
    /// changed quietly would leave the counter looking at a stale list - and
    /// with it the colours that say what is printing and who is waiting.
    /// </summary>
    [Fact(DisplayName = "the queue says when its shape changes")]
    public async Task TheQueueSaysWhenItsShapeChanges()
    {
        var notifications = 0;
        var queue = NewQueue(onDepthChanged: () => Interlocked.Increment(ref notifications));
        var running = Gate();
        var gate = Gate();

        queue.Enqueue("job-1", Order(1), false, () => { running.TrySetResult(); return gate.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Counted across the whole job rather than between the steps: the worker
        // can start - and notify - before the enqueueing thread has read the
        // count back, so the ordering is not something to assert on.
        gate.SetResult();
        await DrainAsync(queue);

        Assert.True(Volatile.Read(ref notifications) >= 3,
            $"queued, started and finished are three changes worth telling the screen about (saw {notifications})");
    }

    /// <summary>A duplicate is not a change, and must not make the screen redraw for nothing.</summary>
    [Fact(DisplayName = "a job already queued raises no change")]
    public async Task AJobAlreadyQueuedRaisesNoChange()
    {
        var notifications = 0;
        var queue = NewQueue(onDepthChanged: () => Interlocked.Increment(ref notifications));
        var gate = Gate();

        queue.Enqueue("job-1", Order(1), false, () => gate.Task);
        await UntilAsync(() => Volatile.Read(ref notifications) > 0);
        var settled = Volatile.Read(ref notifications);

        Assert.False(queue.Enqueue("job-1", Order(1), false, () => Task.CompletedTask),
            "the duplicate guard should refuse it");
        Assert.Equal(settled, Volatile.Read(ref notifications));

        gate.SetResult();
        await DrainAsync(queue);
    }

    [Fact(DisplayName = "an unreadable order code waits behind every readable one")]
    public void AnUnreadableOrderCodeWaitsBehindEveryReadableOne()
    {
        Assert.Equal(32L, PrintQueue.OrderSequence("HH-000032"));
        Assert.Equal(63L, PrintQueue.OrderSequence("PPP01-000063"));
        Assert.Equal(7L, PrintQueue.OrderSequence("7"));
        Assert.Equal(long.MaxValue, PrintQueue.OrderSequence(null));
        Assert.Equal(long.MaxValue, PrintQueue.OrderSequence(""));
        Assert.Equal(long.MaxValue, PrintQueue.OrderSequence("NO-DIGITS"));
    }
    /// <summary>
    /// The student has scanned and is standing there. Intake is busy because the
    /// shop is busy, and the wait exists to get *numbers* in order - which
    /// cannot change what goes next when a scan is already in hand. Paying it
    /// anyway left them at the counter for up to twenty seconds.
    /// </summary>
    [Fact(DisplayName = "a scan at the counter does not wait out the intake drain")]
    public async Task AScanAtTheCounterDoesNotWaitOutTheIntakeDrain()
    {
        // Intake never falls quiet: a shop taking orders steadily.
        var queue = NewQueue(workers: 1, intakeBusy: () => true, maxIntakeWaitMillis: 10_000);
        var printed = Gate();

        var started = System.Diagnostics.Stopwatch.StartNew();
        queue.Enqueue("scan", Order(90), priority: true, () =>
        {
            printed.TrySetResult();
            return Task.CompletedTask;
        });
        await printed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var elapsed = started.ElapsedMilliseconds;

        Assert.True(
            elapsed < 1_000,
            $"a person at the counter must not wait for orders nobody is waiting on (took {elapsed}ms)");
    }

    /// <summary>And a scan that lands while the wait is already running ends it.</summary>
    [Fact(DisplayName = "a scan landing mid-wait ends the wait")]
    public async Task AScanLandingMidWaitEndsTheWait()
    {
        var queue = NewQueue(workers: 1, intakeBusy: () => true, maxIntakeWaitMillis: 10_000);
        var printed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var scanned = Gate();

        queue.Enqueue("ordinary", Order(5), priority: false, () =>
        {
            printed.Enqueue(Order(5));
            return Task.CompletedTask;
        });
        await Task.Delay(150); // the worker is now sitting in the intake wait

        var started = System.Diagnostics.Stopwatch.StartNew();
        queue.Enqueue("scan", Order(90), priority: true, () =>
        {
            printed.Enqueue(Order(90));
            scanned.TrySetResult();
            return Task.CompletedTask;
        });
        await scanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var elapsed = started.ElapsedMilliseconds;

        Assert.True(printed.TryPeek(out var first));
        Assert.Equal(Order(90), first);
        Assert.True(elapsed < 1_000, $"the wait should have ended the moment the scan arrived (took {elapsed}ms)");
    }

    // -----------------------------------------------------------------------
    // Fetching the next document while this one prints. Printing stays one at
    // a time - there is one printer - but the claim and download in front of it
    // are network waits, and paying them only once the previous sheet landed
    // left the printer idle on every order. On a run of small jobs that was
    // most of the wall clock.
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "the next jobs are prepared while one is printing")]
    public async Task TheNextJobsArePreparedWhileOneIsPrinting()
    {
        var prepared = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var queue = NewQueue(workers: 1, prepareAhead: prepared.Enqueue);

        var printing = Gate();
        var release = Gate();
        queue.Enqueue("job-1", Order(1), false, async () => { printing.TrySetResult(); await release.Task; });
        queue.Enqueue("job-2", Order(2), false, () => Task.CompletedTask);
        queue.Enqueue("job-3", Order(3), false, () => Task.CompletedTask);

        await printing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While job-1 holds the printer, the ones behind it are already being
        // fetched - that is the whole point.
        await WaitUntil(() => prepared.Count >= 2);
        Assert.Contains("job-2", prepared);

        release.TrySetResult();
        await DrainAsync(queue);
    }

    /// <summary>
    /// Each job is offered once however many times the queue turns over.
    /// Offering twice would mean claiming and downloading twice, and printing
    /// the same document twice is the failure this pipeline exists to avoid.
    /// </summary>
    [Fact(DisplayName = "a job is never offered for preparation twice")]
    public async Task AJobIsNeverOfferedForPreparationTwice()
    {
        var prepared = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var queue = NewQueue(workers: 1, prepareAhead: prepared.Enqueue);

        for (var i = 1; i <= 5; i++)
        {
            var code = Order(i);
            queue.Enqueue($"job-{i}", code, false, () => Task.CompletedTask);
        }

        await DrainAsync(queue);

        Assert.Equal(prepared.Distinct().Count(), prepared.Count);
    }

    /// <summary>
    /// A counter scan is fetched even when the lookahead is already full.
    ///
    /// The window is deliberately small - two - so a backlog does not mean a
    /// claim and a customer's file held for every order in it. That makes the
    /// choice of which two matter: they are taken in the order the queue will
    /// actually print, so an order that jumps the queue is fetched at once
    /// rather than queueing behind preparation done for orders it has passed.
    /// </summary>
    [Fact(DisplayName = "a counter scan is prepared even when the lookahead is full")]
    public async Task ACounterScanIsPreparedEvenWhenTheLookaheadIsFull()
    {
        var prepared = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var queue = NewQueue(workers: 1, prepareAhead: prepared.Enqueue);

        var printing = Gate();
        var release = Gate();
        queue.Enqueue("printing-now", Order(1), false, async () => { printing.TrySetResult(); await release.Task; });
        await printing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fill the queue so the two-deep window is spoken for.
        for (var i = 2; i <= 6; i++)
        {
            queue.Enqueue($"ordinary-{i}", Order(i * 10), false, () => Task.CompletedTask);
        }
        await WaitUntil(() => prepared.Count >= 2);
        var beforeTheScan = prepared.Count;

        queue.Enqueue("scanned-at-the-counter", Order(90), true, () => Task.CompletedTask);

        await WaitUntil(() => prepared.Contains("scanned-at-the-counter"));
        Assert.True(
            prepared.Count > beforeTheScan,
            "the scan should have been fetched on arrival, not left behind a full window");

        release.TrySetResult();
        await DrainAsync(queue);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.ElapsedMilliseconds < 5_000, "the queue never reached the expected state");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// The point of the whole thing, measured rather than asserted about.
    ///
    /// Five small orders, each needing a fetch before it can print. Serially
    /// that is five fetches and five prints end to end; overlapped, only the
    /// first fetch is ever waited for, because every later one happens while
    /// the printer is busy with the order before it.
    ///
    /// Modelled exactly as the real pipeline is: preparation is memoised per
    /// job, the queue starts it early, and the work awaits it when its turn
    /// comes - so a job whose fetch was never started early still fetches on
    /// demand and simply waits, which is what makes the comparison fair.
    /// </summary>
    [Fact(DisplayName = "a run of small orders does not wait for a download between each")]
    public async Task ARunOfSmallOrdersDoesNotWaitForADownloadBetweenEach()
    {
        const int orders = 5;
        var fetchMillis = TimeSpan.FromMilliseconds(120);
        var printMillis = TimeSpan.FromMilliseconds(80);

        var fetches = new System.Collections.Concurrent.ConcurrentDictionary<string, Task>();
        Task Fetch(string jobId) => fetches.GetOrAdd(jobId, _ => Task.Delay(fetchMillis));

        var queue = NewQueue(workers: 1, prepareAhead: id => Fetch(id));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 1; i <= orders; i++)
        {
            var jobId = $"job-{i}";
            queue.Enqueue(jobId, Order(i), false, async () =>
            {
                await Fetch(jobId);          // already running, if it was prepared ahead
                await Task.Delay(printMillis);
            });
        }
        await DrainAsync(queue);
        var elapsed = clock.Elapsed;

        // Serial would be orders x (fetch + print). Overlapped, the fetches
        // after the first are paid during the print before them, so the floor
        // is one fetch plus all the prints.
        var serial = (fetchMillis + printMillis) * orders;
        var overlapped = fetchMillis + printMillis * orders;

        Assert.True(
            elapsed < serial * 0.85,
            $"took {elapsed.TotalMilliseconds:F0}ms, which is no better than fetching one at a time " +
            $"({serial.TotalMilliseconds:F0}ms serial, {overlapped.TotalMilliseconds:F0}ms overlapped)");
    }

}
