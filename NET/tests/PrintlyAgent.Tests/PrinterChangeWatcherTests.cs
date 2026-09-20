using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Printers;
using Xunit;
using Xunit.Abstractions;

namespace PrintlyAgent.Tests;

/// <summary>
/// The spooler watch, against the real spooler on this machine.
///
/// <para>
/// No Kotlin counterpart - the watch has none. What these hold to is the
/// contract the rest of the agent depends on: it must not stop the process
/// exiting, and it must not report a change that did not happen. The second
/// matters more than it looks. A watch that fires spuriously syncs the whole
/// printer list to the backend each time, and does it from a callback that runs
/// however often the spooler is chatty - so a false positive is not a wasted
/// sweep, it is a loop.
/// </para>
///
/// <para>
/// A printer genuinely being added is not tested here: installing one needs
/// administrator rights, and a suite that quietly passes when it cannot get them
/// is worse than one that does not claim to cover it. That path was verified by
/// hand against a running agent - see the log line <c>printers_changed</c>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class PrinterChangeWatcherTests
{
    private readonly ITestOutputHelper _output;

    public PrinterChangeWatcherTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "stops promptly when cancelled, rather than holding up shutdown")]
    public async Task StopsPromptlyWhenCancelled()
    {
        var watcher = new PrinterChangeWatcher(NullLogger.Instance, () => { });
        using var shutdown = new CancellationTokenSource();

        var watching = watcher.RunForeverAsync(shutdown.Token);

        // Long enough to be inside the blocking wait rather than still starting
        // up, which is the state the cancellation has to be able to interrupt.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var clock = Stopwatch.StartNew();
        shutdown.Cancel();
        await watching.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Stop();

        _output.WriteLine($"stopped in {clock.ElapsedMilliseconds}ms");

        // AgentCore.DisposeAsync gives every loop five seconds together before
        // abandoning them to process teardown. A watch that took seconds of
        // that on its own would spend the budget the network loops need to
        // close their streams cleanly.
        Assert.True(
            clock.ElapsedMilliseconds < 1000,
            $"cancellation took {clock.ElapsedMilliseconds}ms; it waits on the token's own handle, so it should be immediate");
    }

    [Fact(DisplayName = "reports no change while the set of printers is left alone")]
    public async Task ReportsNoChangeWhileNothingHappens()
    {
        var changes = 0;
        var watcher = new PrinterChangeWatcher(NullLogger.Instance, () => Interlocked.Increment(ref changes));
        using var shutdown = new CancellationTokenSource();

        var watching = watcher.RunForeverAsync(shutdown.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));

        shutdown.Cancel();
        await watching.WaitAsync(TimeSpan.FromSeconds(5));

        // Subscribed to ADD, DELETE and FAILED_CONNECTION only. Printers on this
        // machine are busy things - they change status, they take jobs from
        // other applications - and none of that is a printer appearing or
        // disappearing. If this fails, the filter is picking up SET_PRINTER
        // traffic and every status flip is about to become a backend sync.
        Assert.Equal(0, Volatile.Read(ref changes));
    }

    [Fact(DisplayName = "two watchers can subscribe at once, so one does not lock the spooler out")]
    public async Task TwoWatchersCanSubscribeAtOnce()
    {
        // The agent runs one, but the installed build and a development build
        // are routinely running side by side on the same counter PC - which is
        // the whole reason the .NET assembly is named apart from the Kotlin one.
        // A subscription that were exclusive would leave the second agent
        // silently blind rather than failing outright.
        using var shutdown = new CancellationTokenSource();

        var first = new PrinterChangeWatcher(NullLogger.Instance, () => { }).RunForeverAsync(shutdown.Token);
        var second = new PrinterChangeWatcher(NullLogger.Instance, () => { }).RunForeverAsync(shutdown.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Neither may have failed: a watch that cannot subscribe returns
        // straight away rather than throwing, so completion here is the failure.
        Assert.False(first.IsCompleted, "the first watch ended on its own - it could not subscribe to the spooler");
        Assert.False(second.IsCompleted, "the second watch ended on its own - the subscription is exclusive");

        shutdown.Cancel();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
