using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

using PrintlyAgent.Printers;

namespace PrintlyAgent.Printing;

public enum PrintOutcome { COMPLETED, FAILED, UNKNOWN }

/// <summary>
/// A printer problem that stops a job without ending it.
///
/// Windows does not throw these jobs away - it holds them in the queue and
/// prints them the moment the condition clears. So none of them is a failure
/// while the job is still sitting there, and treating them as one is how a shop
/// ends up printing a document twice: the agent reports FAILED, the student is
/// told it failed, somebody loads paper, Windows prints it, and then the shop
/// presses "Print again" on an order that already came out.
/// </summary>
public enum PrinterCondition
{
    // From the job's own status. These are what the spooler says about this
    // document, and they are all the agent used to be able to see.
    OUT_OF_PAPER,
    NEEDS_ATTENTION,
    OFFLINE,
    PAUSED,
    QUEUE_BLOCKED,

    // From the device's status, which is a different call and carries the
    // detail the job's status throws away. Windows knows the difference between
    // a jam, an open cover and an empty toner cartridge; the shop was being
    // told "needs attention" for all three and left to go and find out which.
    PAPER_JAM,
    DOOR_OPEN,
    OUT_OF_TONER,
    OUTPUT_BIN_FULL,
    PAPER_PROBLEM,
    MANUAL_FEED_REQUIRED,
    OUT_OF_MEMORY,

    /// <summary>
    /// The printer cannot be reached at all - switched off, unplugged, or the
    /// machine sharing it is gone. Distinct from OFFLINE, which is the spooler's
    /// own "work offline" flag that a person sets deliberately.
    /// </summary>
    NOT_REACHABLE,
}

/// <summary>
/// The human-readable half of <see cref="PrinterCondition"/>.
///
/// Kotlin's enum carries a <c>description</c> constructor argument; a C# enum
/// cannot hold data, so the same strings live here verbatim. They are what the
/// shop actually reads on screen, so they are part of the behaviour, not a
/// comment - losing them would leave the counter staring at a bare enum name.
/// </summary>
public static class PrinterConditionDescriptions
{
    public static string Description(this PrinterCondition condition) => condition switch
    {
        PrinterCondition.OUT_OF_PAPER => "the printer is out of paper",
        PrinterCondition.NEEDS_ATTENTION =>
            "the printer needs attention - check for a jam, an open cover, or a cartridge",
        PrinterCondition.OFFLINE => "the printer is offline",
        PrinterCondition.PAUSED => "printing is paused",
        PrinterCondition.QUEUE_BLOCKED => "the printer's queue is blocked",

        // Named to the thing somebody has to walk over and do. "Needs
        // attention" sends a person to the printer to work out what is wrong;
        // these send them with the answer.
        PrinterCondition.PAPER_JAM => "there is a paper jam - clear it and printing will carry on",
        PrinterCondition.DOOR_OPEN => "a cover or door is open on the printer",
        PrinterCondition.OUT_OF_TONER => "the printer is out of toner or ink",
        PrinterCondition.OUTPUT_BIN_FULL => "the output tray is full - take the paper out",
        PrinterCondition.PAPER_PROBLEM => "there is a problem with the paper - check the tray and the size loaded",
        PrinterCondition.MANUAL_FEED_REQUIRED => "the printer is waiting for paper to be fed by hand",
        PrinterCondition.OUT_OF_MEMORY => "the printer ran out of memory for this job",
        PrinterCondition.NOT_REACHABLE => "the printer cannot be reached - check it is switched on and connected",
        _ => condition.ToString(),
    };
}

/// <summary>
/// What the spooler ended up saying, plus the condition it was stuck on if it
/// never got past one. <c>Condition</c> is only meaningful alongside
/// <see cref="PrintOutcome.UNKNOWN"/>: it is the difference between "nobody
/// knows what happened" and "it is waiting for you to put paper in".
/// </summary>
public sealed record SpoolerOutcome(PrintOutcome Outcome, PrinterCondition? Condition = null);

/// <summary>
/// Watches the Windows spooler's own queue for a submitted job after the
/// blocking submit call has already returned - port of
/// printing/SpoolerOutcomePoller.kt, itself a port of the Python agent's
/// <c>printing.py::poll_job_outcome</c>/<c>_job_status</c>, with the same
/// <c>JOB_STATUS_*</c> bit constants and the same "disappeared from queue with
/// no prior error = completed" heuristic. The submit path gives back no spooler
/// job id, so a job is matched by the unique job-name token it was tagged with
/// (<c>pDocument</c> in the raw WinSpool API), rather than by a returned id.
///
/// <para>
/// Never raises, and never guesses COMPLETED: anything that leaves real doubt -
/// the printer unreachable to even ask, or the job still sitting there
/// unresolved when the timeout elapses - comes back UNKNOWN.
/// </para>
///
/// <para>
/// Spooler access is a direct P/Invoke to <c>winspool.drv</c>'s EnumJobsW,
/// mirroring the Kotlin JNA binding one call at a time, rather than
/// System.Printing's PrintQueue/PrintSystemJobInfo. Two reasons, both about
/// keeping behaviour identical. First, the decisions below are made on the raw
/// <c>JOB_STATUS_*</c> bitmask; System.Printing exposes a re-derived
/// PrintJobStatus flags enum plus a set of IsPaperOut/IsOffline booleans, and
/// the "most specific condition first" ordering and the exact PAPEROUT-beats-
/// BLOCKED_DEVQ precedence would have to be reconstructed from a different
/// vocabulary and hope it maps. Second, System.Printing lives in
/// ReachFramework, which is a WPF assembly and would need a project reference
/// this port does not otherwise carry. The P/Invoke reads the same bytes the
/// Kotlin agent reads.
/// </para>
/// </summary>
public static class SpoolerOutcomePoller
{
    // winspool.h JOB_STATUS_* bit values - the same subset used on the Python
    // and Kotlin sides, for the same reason: only bits unambiguous enough
    // across drivers to act on. The rest (SPOOLING, RESTART, RETAINED, ...) are
    // informational only and never change the outcome either way.
    private const int JOB_STATUS_PAUSED = 0x00000001;
    private const int JOB_STATUS_ERROR = 0x00000002;
    private const int JOB_STATUS_OFFLINE = 0x00000020;
    private const int JOB_STATUS_PAPEROUT = 0x00000040;
    private const int JOB_STATUS_PRINTED = 0x00000080;
    private const int JOB_STATUS_DELETING = 0x00000004;
    private const int JOB_STATUS_DELETED = 0x00000100;
    private const int JOB_STATUS_BLOCKED_DEVQ = 0x00000200;
    private const int JOB_STATUS_USER_INTERVENTION = 0x00000400;
    private const int JOB_STATUS_COMPLETE = 0x00001000;

    /// <summary>
    /// The only bits that end a job badly and for good: the driver reporting an
    /// outright error, and somebody cancelling it out of the queue. Everything
    /// else that looks like trouble is <see cref="Recoverable"/>.
    ///
    /// DELETING counts as well as DELETED, and leaving it out was a real hole
    /// rather than a fine distinction. A job cancelled by hand from the Windows
    /// queue shows DELETING and is then removed, often without a two-second
    /// poll ever catching DELETED - so the last status seen carried no failure
    /// bit, the job was simply gone next tick, and the branch below read that
    /// absence as "printed and removed". Shop staff clearing a jammed job were
    /// telling the student it had printed.
    /// </summary>
    private const int FailureBits = JOB_STATUS_ERROR | JOB_STATUS_DELETED | JOB_STATUS_DELETING;

    private const int SuccessBits = JOB_STATUS_PRINTED | JOB_STATUS_COMPLETE;

    /// <summary>
    /// Conditions that stop a job without ending it, most specific first - a job
    /// that is both out of paper and blocked is, to the person standing at the
    /// printer, out of paper.
    ///
    /// <para>
    /// These used to be treated as immediate failures. They are not: the job
    /// stays queued and prints itself once the condition clears, so failing here
    /// reported a document as lost seconds before Windows went and printed it -
    /// and then offered the shop a "Print again" for a page already in the tray.
    /// </para>
    /// </summary>
    private static readonly (int Bit, PrinterCondition Condition)[] Recoverable =
    {
        (JOB_STATUS_PAPEROUT, PrinterCondition.OUT_OF_PAPER),
        (JOB_STATUS_USER_INTERVENTION, PrinterCondition.NEEDS_ATTENTION),
        (JOB_STATUS_OFFLINE, PrinterCondition.OFFLINE),
        (JOB_STATUS_PAUSED, PrinterCondition.PAUSED),
        (JOB_STATUS_BLOCKED_DEVQ, PrinterCondition.QUEUE_BLOCKED),
    };

    private static PrinterCondition? ConditionOf(int status)
    {
        foreach (var (bit, condition) in Recoverable)
        {
            if ((status & bit) != 0) return condition;
        }
        return null;
    }

    /// <summary>
    /// One reading of the spooler for one job.
    ///
    /// <c>StatusBits</c> is nullable and the null is load-bearing: it means the
    /// spooler could not be asked at all, which is not the same as a job with no
    /// status bits set. Collapsing it to 0 would turn "we could not reach the
    /// printer" into "the job is not in the queue", and this class reads the
    /// latter as COMPLETED. That is Kotlin's <c>Pair&lt;Int?, Boolean&gt;</c>,
    /// named rather than positional so the two halves cannot be swapped.
    /// </summary>
    /// <summary>
    /// One observation of a job in the spooler queue.
    ///
    /// <c>PagesPrinted</c> is carried alongside the status bits rather than
    /// fetched separately because it is what tells a long job from a stuck one:
    /// a 162-page document legitimately takes several minutes, but it climbs
    /// while it does, and one that has stopped climbing has stopped. Both come
    /// from the same EnumJobs call, so knowing it costs nothing.
    /// </summary>
    public readonly record struct JobStatus(int? StatusBits, bool Found, int PagesPrinted = 0);

    /// <summary>
    /// Polls the spooler until the job resolves, or until it has stopped moving
    /// for <paramref name="stallSeconds"/>.
    ///
    /// <para>
    /// Deliberately a stall window and not a total time limit. This used to give
    /// up a flat five minutes after the driver accepted the document, whatever
    /// the document was - so a 162-page order, which at ordinary printer speeds
    /// takes six or more minutes to physically come out, was recorded UNKNOWN
    /// while it was still printing perfectly well. The shop was then asked "did
    /// it print?" about a job whose pages were still arriving, and the only
    /// thing wrong with it was that it was long.
    /// </para>
    ///
    /// <para>
    /// The submit half of printing already worked this way - see
    /// <see cref="StallDetector{T}"/>, whose whole point is that "a job climbing
    /// through 3,000 pages is healthy however long it takes". The two halves
    /// disagreed; now they do not.
    /// </para>
    ///
    /// <paramref name="statusLookup"/> defaults to the real WinSpool-backed
    /// <see cref="QueryJobStatus"/> - tests inject a fake one instead, the same
    /// principle as the Python test suite monkeypatching <c>_job_status</c>, so
    /// this runs with no real printer.
    ///
    /// <para>
    /// Kotlin's blocking Thread.sleep loop becomes an async one, so a poll for a
    /// 3,000-page job does not park a thread for its whole life. Cancellation is
    /// handled by returning UNKNOWN (with whatever condition was last seen)
    /// rather than by throwing: the contract of this method is that it never
    /// raises and never guesses, and a cancelled poll leaves exactly the doubt a
    /// timed-out one does - the job is still in the queue and may yet print.
    /// </para>
    /// </summary>
    public static async Task<SpoolerOutcome> PollJobOutcomeAsync(
        string printerName,
        string jobNameToken,
        double stallSeconds,
        double pollIntervalSeconds = 2.0,
        Func<string, string, JobStatus>? statusLookup = null,
        Func<string, PrinterCondition?>? deviceConditionLookup = null,
        Action<PrinterCondition?>? onCondition = null,
        ILogger? log = null,
        CancellationToken cancellation = default,
        double unreachableSeconds = DefaultUnreachableSeconds,
        Func<string, string, bool>? cancelJob = null)
    {
        var lookup = statusLookup ?? ((printer, token) => QueryJobStatus(printer, token, log));
        var cancelQueued = cancelJob ?? ((printer, token) => CancelJob(printer, token, log));
        var deviceCondition = deviceConditionLookup ?? PrinterDiscovery.CurrentCondition;
        var report = onCondition ?? (_ => { });

        // Nanoseconds off a monotonic stopwatch, matching Kotlin's
        // System.nanoTime(): this measures a duration, and a counter PC
        // resyncing its clock mid-job must not create or erase a stall.
        var nanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        var detector = new StallDetector<object>((long)stallSeconds);
        var stallNanos = (long)(stallSeconds * 1_000_000_000L);
        long? unreadableSince = null;
        long? unreachableSince = null;
        var unreachableNanos = (long)(unreachableSeconds * 1_000_000_000L);
        var lastSeenStatus = 0;
        PrinterCondition? reportedCondition = null;

        while (true)
        {
            if (cancellation.IsCancellationRequested) return new SpoolerOutcome(PrintOutcome.UNKNOWN, reportedCondition);

            var observation = lookup(printerName, jobNameToken);
            var (status, found) = (observation.StatusBits, observation.Found);

            // A null status means the spooler could not be asked at all. That is
            // emphatically NOT the same as "the job is no longer in the queue",
            // which the else-branch below reads as a finished print - so an
            // unreadable spooler must never reach it, or a printer that was
            // briefly unavailable would be reported as a successful print of a
            // document nobody has seen. It falls through to the stall check,
            // which treats it as no evidence either way.
            if (status is not null)
            {
                if (found)
                {
                    lastSeenStatus = status.Value;
                    if ((status.Value & FailureBits) != 0)
                    {
                        // Reported as not printed, so it must not print later
                        // either: a job left in an errored queue comes out the
                        // moment the printer is fixed, on top of the reprint the
                        // shop was just invited to make from the Errors page.
                        cancelQueued(printerName, jobNameToken);
                        return new SpoolerOutcome(PrintOutcome.FAILED);
                    }
                    if ((status.Value & SuccessBits) != 0) return new SpoolerOutcome(PrintOutcome.COMPLETED);

                    // Stuck but not finished. Keep waiting - the job is still in the
                    // queue and Windows prints it the moment somebody fixes the
                    // printer - and say what is wrong so the shop can go and fix it
                    // rather than watching a silent spinner.
                    // The device first, because it knows more. A job's status
                    // can only say "user intervention"; the printer's says
                    // whether that means a jam, an open cover or an empty
                    // cartridge - which is the difference between sending
                    // somebody to look and sending them to fix it. Falls back to
                    // the job's own reading when the device has nothing to add
                    // or cannot be asked.
                    var condition = deviceCondition(printerName) ?? ConditionOf(status.Value);
                    if (condition != reportedCondition)
                    {
                        reportedCondition = condition;
                        report(condition);
                    }

                    // A printer that is not there at all is different from one
                    // that is out of paper. Somebody fixes paper in a minute; an
                    // unplugged or switched-off machine can stay that way all
                    // day, and waiting on it held the order on "Printing…" with
                    // its other files already on paper and nothing telling the
                    // counter to look. Nothing has come out, so the job is taken
                    // back out of the queue and the file goes to the Errors page
                    // as not printed, to be printed from there once it is back.
                    var nowUnreachable = (condition is PrinterCondition.OFFLINE or PrinterCondition.NOT_REACHABLE)
                                         && observation.PagesPrinted == 0;
                    if (!nowUnreachable)
                    {
                        unreachableSince = null;
                    }
                    else
                    {
                        var at = (long)(Stopwatch.GetTimestamp() * nanosPerTick);
                        unreachableSince ??= at;
                        if (at - unreachableSince.Value >= unreachableNanos)
                        {
                            var withdrawn = cancelQueued(printerName, jobNameToken);
                            if (log is not null)
                            {
                                log.LogWarning(
                                    "spooler_printer_unreachable printer={Printer} condition={Condition} withdrawn={Withdrawn}",
                                    printerName, condition, withdrawn);
                            }
                            // Withdrawn: certainly not printed. Not withdrawn: it
                            // is still queued and may yet come out - unknown.
                            return new SpoolerOutcome(withdrawn ? PrintOutcome.FAILED : PrintOutcome.UNKNOWN, condition);
                        }
                    }
                }
                else
                {
                    // No longer in the queue - printed and removed (the common case;
                    // "keep printed documents" is off by default) unless the last
                    // status observed for it was already a clear failure.
                    var outcome = (lastSeenStatus & FailureBits) != 0 ? PrintOutcome.FAILED : PrintOutcome.COMPLETED;
                    return new SpoolerOutcome(outcome);
                }
            }

            var nowNanos = (long)(Stopwatch.GetTimestamp() * nanosPerTick);
            long? stalledFor = null;

            if (status is null)
            {
                // The spooler could not be read. Tolerated, because over a long
                // print this is sampled hundreds of times and one unreadable
                // moment must not abandon a healthy job - but bounded, because a
                // spooler that is never readable again would otherwise be polled
                // for ever, holding one of the agent's few print slots open on a
                // job nobody will ever hear about.
                unreadableSince ??= nowNanos;
                if (nowNanos - unreadableSince.Value >= stallNanos) stalledFor = (nowNanos - unreadableSince.Value) / 1_000_000_000L;
            }
            else
            {
                unreadableSince = null;
                // The pair (status, pages) is the progress: a climbing page count
                // is the normal signal of a healthy job, and a status change
                // counts too, so one that pauses and resumes is not written off
                // for the pause.
                stalledFor = detector.Sample(new { Status = status.Value, observation.PagesPrinted }, nowNanos);
            }

            if (stalledFor is not null)
            {
                // Stopped moving. UNKNOWN either way, never FAILED: the job is
                // still sitting in the queue, so it may yet print, and calling it
                // failed is what would let the shop reprint a document that then
                // comes out anyway. The condition rides along so a human is told
                // what to fix rather than just asked to go and look.
                if (log is not null)
                {
                    log.LogWarning(
                        "spooler_job_stalled printer={Printer} stalledFor={Seconds}s pages={Pages}",
                        printerName, stalledFor, observation.PagesPrinted);
                }
                return new SpoolerOutcome(PrintOutcome.UNKNOWN, reportedCondition);
            }

            try
            {
                // Truncated to whole milliseconds, as Kotlin's
                // Thread.sleep((pollIntervalSeconds * 1000).toLong()) is, so a
                // fractional interval cannot round up into a longer wait.
                var intervalMillis = (long)(pollIntervalSeconds * 1000);
                await Task.Delay(TimeSpan.FromMilliseconds(intervalMillis), cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new SpoolerOutcome(PrintOutcome.UNKNOWN, reportedCondition);
            }
        }
    }

    /// <summary>
    /// How far along a job is, for watching whether it is still moving.
    ///
    /// <c>PagesPrinted</c> is the spooler's own count and is what distinguishes a
    /// long job from a stuck one: a 3,000-page document legitimately takes a
    /// while, but it climbs while it does. One that stops climbing has stopped.
    /// </summary>
    /// <summary>
    /// How long a job may sit, with nothing printed, on a printer Windows says is
    /// offline or unreachable before the file is handed back to the shop.
    /// Long enough for a printer that is just waking up; short enough that the
    /// counter hears about it while the student is still standing there.
    /// </summary>
    public const double DefaultUnreachableSeconds = 45.0;

    private const int JOB_CONTROL_DELETE = 5;

    /// <summary>
    /// Takes this agent's job back out of the printer's queue. True only when
    /// the spooler confirmed it, or the job is already gone.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool CancelJob(string printerName, string jobNameToken, ILogger? log = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!OpenPrinterW(printerName, out var printerHandle, IntPtr.Zero)) return false;
            try
            {
                var size = JobBufferSize(printerHandle);
                if (size is null) return false;
                if (size == 0) return true;

                var needed = size.Value;
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!EnumJobsW(printerHandle, 0, 999, 1, buffer, needed, out needed, out var returned)) return false;
                    var match = FindJob(buffer, returned, jobNameToken);
                    if (match is null) return true;
                    return SetJobW(printerHandle, match.Value.JobId, 0, IntPtr.Zero, JOB_CONTROL_DELETE);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                ClosePrinter(printerHandle);
            }
        }
        catch (Exception exc)
        {
            if (log is not null) log.LogWarning(exc, "spooler_job_cancel_failed printer={Printer}", printerName);
            return false;
        }
    }

    public sealed record JobProgress(bool Found, int PagesPrinted);

    /// <summary>
    /// This job's progress, or null when the spooler could not be asked at all -
    /// which is not the same as "no progress", and callers watching for a stall
    /// must not treat it as one.
    ///
    /// <para>
    /// Named GetJobProgress rather than JobProgress only because C# will not let
    /// a method share a name with the nested type it returns; the Kotlin is
    /// <c>SpoolerOutcomePoller.jobProgress(printerName, jobNameToken)</c>.
    /// </para>
    /// </summary>
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// Bytes needed for the job buffer, or null when the spooler could not be
    /// read at all.
    ///
    /// The distinction is the whole point, and both callers used to lose it.
    /// They discarded the probe's return value and treated pcbNeeded == 0 as
    /// "no jobs in the queue" - but EnumJobs leaves pcbNeeded at 0 on *any*
    /// failure that is not ERROR_INSUFFICIENT_BUFFER. An access-denied, an
    /// invalid handle, or an RPC fault against a network print server all read
    /// as an empty queue, and an empty queue is how this file decides a job
    /// finished printing. A remote spooler restarting mid-job was therefore
    /// reported as PRINT_COMPLETED, and the student was sent to collect pages
    /// that had never come out.
    ///
    /// Null means "no answer", which every caller already knows how to carry.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int? JobBufferSize(IntPtr printerHandle)
    {
        var ok = EnumJobsW(printerHandle, 0, 999, 1, IntPtr.Zero, 0, out var needed, out _);
        // Read immediately: any interop in between would overwrite it.
        var error = Marshal.GetLastWin32Error();

        // Succeeded outright - a genuine answer, and 0 genuinely means empty.
        if (ok) return needed;
        // The expected "your buffer is too small, here is the size" reply.
        if (error == ERROR_INSUFFICIENT_BUFFER) return needed;
        return null;
    }

    [SupportedOSPlatform("windows")]
    public static JobProgress? GetJobProgress(string printerName, string jobNameToken, ILogger? log = null)
    {
        if (!OpenPrinterW(printerName, out var printerHandle, IntPtr.Zero)) return null;
        try
        {
            // Probe for the buffer size first; this call is expected to fail with
            // ERROR_INSUFFICIENT_BUFFER, and only pcbNeeded is meaningful.
            var size = JobBufferSize(printerHandle);
            // Unreadable, not empty. Null is what StallDetector reads as "no
            // evidence either way", which is the honest answer here.
            if (size is null) return null;
            if (size == 0) return new JobProgress(false, 0);

            var needed = size.Value;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumJobsW(printerHandle, 0, 999, 1, buffer, needed, out needed, out var returned))
                {
                    return null;
                }

                var match = FindJob(buffer, returned, jobNameToken);
                return match is null ? new JobProgress(false, 0) : new JobProgress(true, match.Value.PagesPrinted);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception exc)
        {
            // ILogger's Log* helpers are extension methods, which `?.` cannot
            // invoke, hence the explicit null check here and below.
            if (log is not null) log.LogDebug(exc, "job_progress_read_failed printer={Printer}", printerName);
            return null;
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    /// <summary>
    /// The job's status bits and whether it is still in the queue. A null
    /// <c>StatusBits</c> means the spooler itself could not be asked.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static JobStatus QueryJobStatus(string printerName, string jobNameToken, ILogger? log = null)
    {
        if (!OpenPrinterW(printerName, out var printerHandle, IntPtr.Zero))
        {
            if (log is not null) log.LogWarning("open_printer_failed printer={Printer}", printerName);
            return new JobStatus(null, false);
        }
        try
        {
            var size = JobBufferSize(printerHandle);
            // Unreadable. Emphatically not "the job is gone, so it printed".
            if (size is null) return new JobStatus(null, false);
            if (size == 0) return new JobStatus(0, false); // no jobs at all in the queue

            var needed = size.Value;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumJobsW(printerHandle, 0, 999, 1, buffer, needed, out needed, out var returned))
                {
                    return new JobStatus(null, false);
                }

                var match = FindJob(buffer, returned, jobNameToken);
                return match is null
                    ? new JobStatus(0, false)
                    : new JobStatus(match.Value.Status, true, match.Value.PagesPrinted);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    /// <summary>
    /// The job in the EnumJobs buffer whose document name is exactly
    /// <paramref name="jobNameToken"/>, or null if it is not there.
    ///
    /// <para>
    /// The strings are read with PtrToStringUni rather than declared as
    /// <c>string</c> fields on the struct, because EnumJobs packs them into the
    /// tail of the very buffer being freed here: letting the marshaller treat
    /// them as owned strings would have it try to free pointers it does not own.
    /// This is the same thing JNA's raw <c>Pointer.getWideString(0)</c> does on
    /// the Kotlin side.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static JOB_INFO_1? FindJob(IntPtr buffer, int count, string jobNameToken)
    {
        var stride = Marshal.SizeOf<JOB_INFO_1>();
        for (var i = 0; i < count; i++)
        {
            var job = Marshal.PtrToStructure<JOB_INFO_1>(IntPtr.Add(buffer, i * stride));
            if (job.pDocument != IntPtr.Zero && Marshal.PtrToStringUni(job.pDocument) == jobNameToken)
            {
                return job;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------------
    // winspool.drv interop. Laid out field for field against winspool.h so the
    // layout can be audited against the header rather than against this port.
    // ------------------------------------------------------------------------

    // The marshaller fills these in; nothing in managed code ever assigns them,
    // and most are never read - they exist only to give the struct the byte
    // layout the spooler writes.
#pragma warning disable 0649, 0169

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOB_INFO_1
    {
        public int JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pDatatype;
        public IntPtr pStatus;
        public int Status;
        public int Priority;
        public int Position;
        public int TotalPages;
        public int PagesPrinted;
        public SYSTEMTIME Submitted;
    }

    /// <summary>
    /// Win32 <c>SYSTEMTIME</c> - 8 WORDs. Only present because it is part of
    /// JOB_INFO_1's layout; never read.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

#pragma warning restore 0649, 0169

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetJobW(IntPtr hPrinter, int jobId, int level, IntPtr pJob, int command);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumJobsW(
        IntPtr hPrinter, int firstJob, int noJobs, int level,
        IntPtr pJob, int cbBuf, out int pcbNeeded, out int pcReturned);
}
