using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;

namespace PrintlyAgent.Printers;

/// <summary>
/// Tells the agent the moment this machine's set of printers changes, instead of
/// leaving it to find out on the next sweep.
///
/// <para>
/// No Kotlin counterpart - the JVM has no binding for this and the Kotlin build
/// polls. <c>AgentCore.PrinterSyncLoopAsync</c> re-reads the machine every 120
/// seconds, which is the right cost for a background reconcile and the wrong
/// answer for somebody standing at the counter: they plug a printer in, it is
/// not in the list, and nothing on screen says whether to wait or to go looking
/// for the fault. Two minutes of that is long enough to start unplugging things.
/// </para>
///
/// <para>
/// The spooler already knows. <c>FindFirstPrinterChangeNotification</c> against
/// the local print server hands back a waitable object that Windows signals when
/// a printer is added or removed, so the agent can be told rather than asking.
/// The sweep is kept exactly as it was - this only wakes it early. A notification
/// that never arrives, because the handle was refused or the spooler service was
/// restarted underneath it, therefore costs the 120 seconds it always cost and
/// nothing more.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrinterChangeWatcher
{
    private readonly ILogger _log;
    private readonly Action _onChanged;

    /// <summary>
    /// How long to block in one wait before looping to re-check cancellation.
    ///
    /// The wait is woken by the cancellation token's own handle, so this is not
    /// how shutdown is noticed - it is a backstop for the case where the
    /// notification object is signalled but <c>FindNextPrinterChangeNotification</c>
    /// reports nothing, which would otherwise spin.
    /// </summary>
    private static readonly TimeSpan WaitSlice = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What is worth waking the sweep for.
    ///
    /// <para>
    /// Deliberately not PRINTER_CHANGE_SET_PRINTER. That bit fires for any
    /// change to a printer's own state, including the status transitions a
    /// printer makes while it works, so subscribing to it would sync the whole
    /// printer list to the backend several times per order to report things the
    /// backend is already being told about. Status is not what goes stale here:
    /// <see cref="PrinterDiscovery.DiscoverPrinters"/> re-reads every printer's
    /// status on every single call and only caches the capabilities, so the
    /// print path always has the current one. What it cannot discover is a
    /// printer that was not there when it last asked - which is exactly these
    /// three bits.
    /// </para>
    /// </summary>
    private const uint ChangeFilter =
        PrinterChangeAddPrinter | PrinterChangeDeletePrinter | PrinterChangeFailedConnectionPrinter;

    public PrinterChangeWatcher(ILogger log, Action onChanged)
    {
        _log = log;
        _onChanged = onChanged;
    }

    /// <summary>
    /// Watches until cancelled, calling the change callback each time the set of
    /// printers changes.
    ///
    /// <para>
    /// On its own thread rather than the thread pool, because the wait is a
    /// blocking OS wait that is idle for hours at a time: parking a pool thread
    /// on it would take a worker out of circulation for the life of the agent,
    /// and the pool grows to compensate by injecting threads a second at a time.
    /// LongRunning asks the scheduler for a thread of its own instead, which is
    /// what this actually is.
    /// </para>
    /// </summary>
    public Task RunForeverAsync(CancellationToken ct) =>
        Task.Factory.StartNew(
            () => Watch(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Watch(CancellationToken ct)
    {
        // Opened once and held. Re-opening per wait would be a spooler round
        // trip for every notification, and the notification object is tied to
        // this handle's lifetime anyway.
        if (!OpenPrinterW(null, out var server, IntPtr.Zero))
        {
            // Not fatal and not retried. The only reasons this fails are the
            // spooler being stopped or the handle being refused, neither of
            // which changes while the agent runs - and the sweep still covers
            // the machine either way, so an agent that cannot watch is slower,
            // not broken.
            _log.LogWarning(
                "printer_watch_unavailable error={Error} reason=OPEN_PRINT_SERVER_FAILED",
                Marshal.GetLastWin32Error());
            return;
        }

        try
        {
            var change = FindFirstPrinterChangeNotification(server, ChangeFilter, 0, IntPtr.Zero);
            if (change == IntPtr.Zero || change == InvalidHandle)
            {
                _log.LogWarning(
                    "printer_watch_unavailable error={Error} reason=SUBSCRIBE_FAILED",
                    Marshal.GetLastWin32Error());
                return;
            }

            try
            {
                _log.LogInformation("printer_watch_started filter=ADD|DELETE|FAILED_CONNECTION");
                WaitLoop(change, ct);
            }
            finally
            {
                FindClosePrinterChangeNotification(change);
                _log.LogInformation("printer_watch_stopped");
            }
        }
        finally
        {
            ClosePrinter(server);
        }
    }

    private void WaitLoop(IntPtr change, CancellationToken ct)
    {
        // The notification object is a kernel event, so it can be waited on
        // alongside the cancellation token's own handle. That is what makes
        // shutdown immediate: without it the agent would sit in a blocking wait
        // until a printer changed or the slice expired, and DisposeAsync gives
        // its loops five seconds before giving up on them.
        using var signalled = new ManualResetEvent(false);
        signalled.SafeWaitHandle = new SafeWaitHandle(change, ownsHandle: false);

        var waits = new[] { signalled, ct.WaitHandle };

        while (!ct.IsCancellationRequested)
        {
            var woke = WaitHandle.WaitAny(waits, WaitSlice);
            if (woke != 0) continue; // cancelled, or the slice expired

            // Re-arms the subscription as well as reporting what happened, and
            // must be called even when the result is ignored: until it is, the
            // object stays signalled and the next wait returns instantly.
            if (!FindNextPrinterChangeNotification(change, out var what, IntPtr.Zero, IntPtr.Zero))
            {
                // The spooler service restarted, most likely, taking the
                // subscription with it. The handle cannot be re-armed, so the
                // watch ends here and the 120-second sweep carries on alone.
                _log.LogWarning(
                    "printer_watch_lost error={Error}", Marshal.GetLastWin32Error());
                return;
            }

            if ((what & ChangeFilter) == 0) continue;

            _log.LogInformation("printers_changed added={Added} removed={Removed} failed={Failed}",
                (what & PrinterChangeAddPrinter) != 0,
                (what & PrinterChangeDeletePrinter) != 0,
                (what & PrinterChangeFailedConnectionPrinter) != 0);

            try
            {
                _onChanged();
            }
            catch (Exception exc)
            {
                // The callback is the agent's, and a fault in it is not a reason
                // to stop watching - the next change would then go unnoticed
                // too, turning one failed sweep into a permanently deaf agent.
                _log.LogError(exc, "printer_change_handler_failed");
            }
        }
    }

    // ------------------------------------------------------------------------
    // winspool.drv interop. Named and laid out as in winspool.h, for the same
    // reason as PrinterDiscovery's block: so it can be audited against the
    // header rather than against this file.
    // ------------------------------------------------------------------------

    /// <summary>INVALID_HANDLE_VALUE - what FindFirstPrinterChangeNotification returns on failure.</summary>
    private static readonly IntPtr InvalidHandle = new(-1);

    // winspool.h - PRINTER_CHANGE_*
    private const uint PrinterChangeAddPrinter = 0x00000001;
    private const uint PrinterChangeDeletePrinter = 0x00000004;
    private const uint PrinterChangeFailedConnectionPrinter = 0x00000008;

    /// <summary>
    /// A null printer name opens the local print server, which is the only
    /// handle that is told about printers being added and removed - a handle to
    /// one printer is only ever told about that printer.
    /// </summary>
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string? pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern IntPtr FindFirstPrinterChangeNotification(
        IntPtr hPrinter, uint fdwFilter, uint fdwOptions, IntPtr pPrinterNotifyOptions);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool FindNextPrinterChangeNotification(
        IntPtr hChange, out uint pdwChange, IntPtr pvReserved, IntPtr ppPrinterNotifyInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool FindClosePrinterChangeNotification(IntPtr hChange);
}
