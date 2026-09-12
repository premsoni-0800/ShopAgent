package com.printly.agent.printing

import com.sun.jna.Memory
import com.sun.jna.Native
import com.sun.jna.Pointer
import com.sun.jna.Structure
import com.sun.jna.ptr.IntByReference
import com.sun.jna.ptr.PointerByReference
import com.sun.jna.win32.StdCallLibrary
import com.sun.jna.win32.W32APIOptions
import java.util.logging.Logger

enum class PrintOutcome { COMPLETED, FAILED, UNKNOWN }

/**
 * A printer problem that stops a job without ending it.
 *
 * Windows does not throw these jobs away - it holds them in the queue and
 * prints them the moment the condition clears. So none of them is a failure
 * while the job is still sitting there, and treating them as one is how a shop
 * ends up printing a document twice: the agent reports FAILED, the student is
 * told it failed, somebody loads paper, Windows prints it, and then the shop
 * presses "Print again" on an order that already came out.
 */
enum class PrinterCondition(val description: String) {
    OUT_OF_PAPER("the printer is out of paper"),
    NEEDS_ATTENTION("the printer needs attention - check for a jam, an open cover, or a cartridge"),
    OFFLINE("the printer is offline"),
    PAUSED("printing is paused"),
    QUEUE_BLOCKED("the printer's queue is blocked"),
}

/**
 * What the spooler ended up saying, plus the condition it was stuck on if it
 * never got past one. [condition] is only meaningful alongside
 * [PrintOutcome.UNKNOWN]: it is the difference between "nobody knows what
 * happened" and "it is waiting for you to put paper in".
 */
data class SpoolerOutcome(val outcome: PrintOutcome, val condition: PrinterCondition? = null)

/**
 * Watches the Windows spooler's own queue for a submitted job after
 * `javax.print`'s blocking submit call has already returned - direct port of
 * the Python agent's `printing.py::poll_job_outcome`/`_job_status`, same
 * `JOB_STATUS_*` bit constants and same "disappeared from queue with no
 * prior error = completed" heuristic. `javax.print` gives no spooler job id,
 * so a job is matched by the unique `JobName` token [printPdf] tagged it
 * with (`pDocument` in the raw WinSpool API), rather than a returned id.
 *
 * Never raises, and never guesses COMPLETED: anything that leaves real doubt
 * - the printer unreachable to even ask, or the job still sitting there
 * unresolved when the timeout elapses - comes back UNKNOWN.
 */
object SpoolerOutcomePoller {

    private val log = Logger.getLogger(javaClass.name)

    // winspool.h JOB_STATUS_* bit values - same subset used on the Python
    // side, for the same reason: only bits unambiguous enough across drivers
    // to act on. The rest (PAUSED, SPOOLING, RESTART, RETAINED, ...) are
    // informational only and never change the outcome either way.
    private const val JOB_STATUS_PAUSED = 0x00000001
    private const val JOB_STATUS_ERROR = 0x00000002
    private const val JOB_STATUS_OFFLINE = 0x00000020
    private const val JOB_STATUS_PAPEROUT = 0x00000040
    private const val JOB_STATUS_PRINTED = 0x00000080
    private const val JOB_STATUS_DELETED = 0x00000100
    private const val JOB_STATUS_BLOCKED_DEVQ = 0x00000200
    private const val JOB_STATUS_USER_INTERVENTION = 0x00000400
    private const val JOB_STATUS_COMPLETE = 0x00001000

    /**
     * The only bits that end a job badly and for good: the driver reporting an
     * outright error, and somebody cancelling it out of the queue. Everything
     * else that looks like trouble is [RECOVERABLE].
     */
    private const val FAILURE_BITS = JOB_STATUS_ERROR or JOB_STATUS_DELETED

    private const val SUCCESS_BITS = JOB_STATUS_PRINTED or JOB_STATUS_COMPLETE

    /**
     * Conditions that stop a job without ending it, most specific first - a
     * job that is both out of paper and blocked is, to the person standing at
     * the printer, out of paper.
     *
     * These used to be treated as immediate failures. They are not: the job
     * stays queued and prints itself once the condition clears, so failing
     * here reported a document as lost seconds before Windows went and printed
     * it - and then offered the shop a "Print again" for a page already in the
     * tray.
     */
    private val RECOVERABLE = listOf(
        JOB_STATUS_PAPEROUT to PrinterCondition.OUT_OF_PAPER,
        JOB_STATUS_USER_INTERVENTION to PrinterCondition.NEEDS_ATTENTION,
        JOB_STATUS_OFFLINE to PrinterCondition.OFFLINE,
        JOB_STATUS_PAUSED to PrinterCondition.PAUSED,
        JOB_STATUS_BLOCKED_DEVQ to PrinterCondition.QUEUE_BLOCKED,
    )

    private fun conditionOf(status: Int): PrinterCondition? =
        RECOVERABLE.firstOrNull { (bit, _) -> status and bit != 0 }?.second

    /**
     * [statusLookup] defaults to the real WinSpool-backed [jobStatus] - tests
     * inject a fake one instead, the same principle as the Python test suite
     * monkeypatching `_job_status`, so this runs with no real printer.
     */
    fun pollJobOutcome(
        printerName: String,
        jobNameToken: String,
        timeoutSeconds: Double,
        pollIntervalSeconds: Double = 2.0,
        statusLookup: (String, String) -> Pair<Int?, Boolean> = ::jobStatus,
        onCondition: (PrinterCondition?) -> Unit = {},
    ): SpoolerOutcome {
        val deadline = System.nanoTime() + (timeoutSeconds * 1_000_000_000L).toLong()
        var lastSeenStatus = 0
        var reportedCondition: PrinterCondition? = null

        while (true) {
            val (status, found) = statusLookup(printerName, jobNameToken)
            if (status == null) return SpoolerOutcome(PrintOutcome.UNKNOWN) // could not even ask the spooler

            if (found) {
                lastSeenStatus = status
                if (status and FAILURE_BITS != 0) return SpoolerOutcome(PrintOutcome.FAILED)
                if (status and SUCCESS_BITS != 0) return SpoolerOutcome(PrintOutcome.COMPLETED)

                // Stuck but not finished. Keep waiting - the job is still in
                // the queue and Windows prints it the moment somebody fixes
                // the printer - and say what is wrong so the shop can go and
                // fix it rather than watching a silent spinner.
                val condition = conditionOf(status)
                if (condition != reportedCondition) {
                    reportedCondition = condition
                    onCondition(condition)
                }
            } else {
                // No longer in the queue - printed and removed (the common
                // case; "keep printed documents" is off by default) unless
                // the last status observed for it was already a clear failure.
                val outcome = if (lastSeenStatus and FAILURE_BITS != 0) PrintOutcome.FAILED else PrintOutcome.COMPLETED
                return SpoolerOutcome(outcome)
            }

            if (System.nanoTime() >= deadline) {
                // Out of time. UNKNOWN either way, never FAILED: the job is
                // still sitting in the queue, so it may yet print, and calling
                // it failed is what would let the shop reprint a document that
                // then comes out anyway. The condition rides along so a human
                // is told what to fix rather than just asked to go and look.
                return SpoolerOutcome(PrintOutcome.UNKNOWN, reportedCondition)
            }
            Thread.sleep((pollIntervalSeconds * 1000).toLong())
        }
    }

    /**
     * How far along a job is, for watching whether it is still moving.
     *
     * [pagesPrinted] is the spooler's own count and is what distinguishes a
     * long job from a stuck one: a 3,000-page document legitimately takes a
     * while, but it climbs while it does. One that stops climbing has stopped.
     */
    data class JobProgress(val found: Boolean, val pagesPrinted: Int)

    /** Null when the spooler could not be asked at all - which is not the same as "no progress". */
    fun jobProgress(printerName: String, jobNameToken: String): JobProgress? {
        val printerHandle = PointerByReference()
        if (!WinSpool.INSTANCE.OpenPrinterW(printerName, printerHandle, null)) return null
        try {
            val needed = IntByReference()
            val returned = IntByReference()
            WinSpool.INSTANCE.EnumJobsW(printerHandle.value, 0, 999, 1, null, 0, needed, returned)
            if (needed.value <= 0) return JobProgress(found = false, pagesPrinted = 0)

            val buffer = Memory(needed.value.toLong())
            if (!WinSpool.INSTANCE.EnumJobsW(printerHandle.value, 0, 999, 1, buffer, needed.value, needed, returned)) {
                return null
            }

            @Suppress("UNCHECKED_CAST")
            val jobs = JOB_INFO_1(buffer).toArray(returned.value) as Array<JOB_INFO_1>
            val match = jobs.firstOrNull { it.pDocument?.getWideString(0) == jobNameToken }
                ?: return JobProgress(found = false, pagesPrinted = 0)
            return JobProgress(found = true, pagesPrinted = match.pagesPrinted)
        } catch (exc: Exception) {
            log.fine("job_progress_read_failed printer=$printerName: $exc")
            return null
        } finally {
            WinSpool.INSTANCE.ClosePrinter(printerHandle.value)
        }
    }

    /** `(statusBits, found)`; null statusBits means the spooler itself could not be asked. */
    private fun jobStatus(printerName: String, jobNameToken: String): Pair<Int?, Boolean> {
        val printerHandle = PointerByReference()
        if (!WinSpool.INSTANCE.OpenPrinterW(printerName, printerHandle, null)) {
            log.warning("open_printer_failed printer=$printerName")
            return null to false
        }
        try {
            val needed = IntByReference()
            val returned = IntByReference()
            WinSpool.INSTANCE.EnumJobsW(printerHandle.value, 0, 999, 1, null, 0, needed, returned)
            if (needed.value <= 0) return 0 to false // no jobs at all in the queue

            val buffer = Memory(needed.value.toLong())
            val ok = WinSpool.INSTANCE.EnumJobsW(printerHandle.value, 0, 999, 1, buffer, needed.value, needed, returned)
            if (!ok) return null to false

            val prototype = JOB_INFO_1(buffer)
            @Suppress("UNCHECKED_CAST")
            val jobs = prototype.toArray(returned.value) as Array<JOB_INFO_1>

            val match = jobs.firstOrNull { it.pDocument?.getWideString(0) == jobNameToken }
            return if (match != null) match.status to true else 0 to false
        } finally {
            WinSpool.INSTANCE.ClosePrinter(printerHandle.value)
        }
    }

    interface WinSpool : StdCallLibrary {
        fun OpenPrinterW(pPrinterName: String, phPrinter: PointerByReference, pDefault: Pointer?): Boolean
        fun ClosePrinter(hPrinter: Pointer): Boolean
        fun EnumJobsW(
            hPrinter: Pointer, firstJob: Int, noJobs: Int, level: Int,
            pJob: Pointer?, cbBuf: Int, pcbNeeded: IntByReference, pcReturned: IntByReference,
        ): Boolean

        companion object {
            val INSTANCE: WinSpool = Native.load("winspool.drv", WinSpool::class.java, W32APIOptions.UNICODE_OPTIONS)
        }
    }

    @Structure.FieldOrder(
        "jobId", "pPrinterName", "pMachineName", "pUserName", "pDocument", "pDatatype",
        "pStatus", "status", "priority", "position", "totalPages", "pagesPrinted", "submitted",
    )
    class JOB_INFO_1() : Structure() {
        constructor(p: Pointer) : this() {
            useMemory(p)
            read()
        }

        @JvmField var jobId: Int = 0
        @JvmField var pPrinterName: Pointer? = null
        @JvmField var pMachineName: Pointer? = null
        @JvmField var pUserName: Pointer? = null
        @JvmField var pDocument: Pointer? = null
        @JvmField var pDatatype: Pointer? = null
        @JvmField var pStatus: Pointer? = null
        @JvmField var status: Int = 0
        @JvmField var priority: Int = 0
        @JvmField var position: Int = 0
        @JvmField var totalPages: Int = 0
        @JvmField var pagesPrinted: Int = 0
        @JvmField var submitted: SystemTime = SystemTime()
    }

    /** Win32 `SYSTEMTIME` - 8 WORDs. Only present because it's part of `JOB_INFO_1`'s layout; never read. */
    @Structure.FieldOrder("wYear", "wMonth", "wDayOfWeek", "wDay", "wHour", "wMinute", "wSecond", "wMilliseconds")
    class SystemTime : Structure() {
        @JvmField var wYear: Short = 0
        @JvmField var wMonth: Short = 0
        @JvmField var wDayOfWeek: Short = 0
        @JvmField var wDay: Short = 0
        @JvmField var wHour: Short = 0
        @JvmField var wMinute: Short = 0
        @JvmField var wSecond: Short = 0
        @JvmField var wMilliseconds: Short = 0
    }
}
