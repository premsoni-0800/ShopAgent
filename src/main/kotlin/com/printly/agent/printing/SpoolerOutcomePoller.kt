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
    private const val JOB_STATUS_ERROR = 0x00000002
    private const val JOB_STATUS_PAPEROUT = 0x00000040
    private const val JOB_STATUS_PRINTED = 0x00000080
    private const val JOB_STATUS_BLOCKED_DEVQ = 0x00000200
    private const val JOB_STATUS_USER_INTERVENTION = 0x00000400
    private const val JOB_STATUS_COMPLETE = 0x00001000

    private const val FAILURE_BITS = JOB_STATUS_ERROR or JOB_STATUS_PAPEROUT or JOB_STATUS_BLOCKED_DEVQ or JOB_STATUS_USER_INTERVENTION
    private const val SUCCESS_BITS = JOB_STATUS_PRINTED or JOB_STATUS_COMPLETE

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
    ): PrintOutcome {
        val deadline = System.nanoTime() + (timeoutSeconds * 1_000_000_000L).toLong()
        var lastSeenStatus = 0

        while (true) {
            val (status, found) = statusLookup(printerName, jobNameToken)
            if (status == null) return PrintOutcome.UNKNOWN // could not even ask the spooler

            if (found) {
                lastSeenStatus = status
                if (status and FAILURE_BITS != 0) return PrintOutcome.FAILED
                if (status and SUCCESS_BITS != 0) return PrintOutcome.COMPLETED
            } else {
                // No longer in the queue - printed and removed (the common
                // case; "keep printed documents" is off by default) unless
                // the last status observed for it was already a clear failure.
                return if (lastSeenStatus and FAILURE_BITS != 0) PrintOutcome.FAILED else PrintOutcome.COMPLETED
            }

            if (System.nanoTime() >= deadline) return PrintOutcome.UNKNOWN // still printing - genuinely unclear, never guessed
            Thread.sleep((pollIntervalSeconds * 1000).toLong())
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
