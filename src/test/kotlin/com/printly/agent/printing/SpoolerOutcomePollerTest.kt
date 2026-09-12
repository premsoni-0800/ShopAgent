package com.printly.agent.printing

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * `pollJobOutcome`'s decision logic, without touching WinSpool - mirrors
 * the Python agent's `test_print_outcome_polling.py`, which monkeypatches
 * `_job_status` the same way this injects [statusLookup].
 */
class SpoolerOutcomePollerTest {

    private val pausedBit = 0x00000001
    private val errorBit = 0x00000002
    private val printingBit = 0x00000010 // informational only, never decisive alone
    private val offlineBit = 0x00000020
    private val paperOutBit = 0x00000040
    private val printedBit = 0x00000080
    private val deletedBit = 0x00000100
    private val userInterventionBit = 0x00000400

    private fun poll(
        timeoutSeconds: Double = 5.0,
        onCondition: (PrinterCondition?) -> Unit = {},
        statusLookup: (String, String) -> Pair<Int?, Boolean>,
    ) = SpoolerOutcomePoller.pollJobOutcome(
        "Printer", "job-1", timeoutSeconds, 0.0, statusLookup, onCondition,
    )

    @Test
    fun `printed bit is completed`() {
        assertEquals(PrintOutcome.COMPLETED, poll { _, _ -> printedBit to true }.outcome)
    }

    @Test
    fun `error bit is failed`() {
        assertEquals(PrintOutcome.FAILED, poll { _, _ -> errorBit to true }.outcome)
    }

    @Test
    fun `a job cancelled out of the queue is failed`() {
        assertEquals(PrintOutcome.FAILED, poll { _, _ -> deletedBit to true }.outcome)
    }

    @Test
    fun `job disappearing from the queue with no prior error is completed`() {
        // The common case: a driver that removes a job from its queue the
        // instant it finishes, rather than leaving a PRINTED bit to observe.
        assertEquals(PrintOutcome.COMPLETED, poll { _, _ -> 0 to false }.outcome)
    }

    @Test
    fun `job disappearing after a seen error is failed not completed`() {
        var call = 0
        val result = poll { _, _ ->
            call += 1
            if (call == 1) errorBit to true else 0 to false
        }
        assertEquals(PrintOutcome.FAILED, result.outcome)
    }

    @Test
    fun `spooler unreachable is unknown never a guess`() {
        assertEquals(PrintOutcome.UNKNOWN, poll { _, _ -> null to false }.outcome)
    }

    @Test
    fun `still printing at the deadline is unknown not completed`() {
        // Never resolves, never errors - just still going when time runs out.
        assertEquals(PrintOutcome.UNKNOWN, poll(timeoutSeconds = 0.0) { _, _ -> printingBit to true }.outcome)
    }

    // -----------------------------------------------------------------------
    // Printer problems a person can fix. None of these ends the job: Windows
    // holds it in the queue and prints it once the condition clears, so
    // reporting a failure here is how a shop prints a document twice.
    // -----------------------------------------------------------------------

    @Test
    fun `out of paper is never reported as a failure`() {
        val result = poll(timeoutSeconds = 0.0) { _, _ -> paperOutBit to true }

        assertEquals(PrintOutcome.UNKNOWN, result.outcome)
        assertEquals(PrinterCondition.OUT_OF_PAPER, result.condition)
    }

    @Test
    fun `a printer needing attention is never reported as a failure`() {
        val result = poll(timeoutSeconds = 0.0) { _, _ -> userInterventionBit to true }

        assertEquals(PrintOutcome.UNKNOWN, result.outcome)
        assertEquals(PrinterCondition.NEEDS_ATTENTION, result.condition)
    }

    @Test
    fun `an offline or paused printer is never reported as a failure`() {
        assertEquals(PrinterCondition.OFFLINE, poll(timeoutSeconds = 0.0) { _, _ -> offlineBit to true }.condition)
        assertEquals(PrinterCondition.PAUSED, poll(timeoutSeconds = 0.0) { _, _ -> pausedBit to true }.condition)
    }

    /** The whole point of waiting rather than failing: somebody loads paper and the job prints itself. */
    @Test
    fun `a job that was out of paper and then prints is completed`() {
        var call = 0
        val result = poll {
            _, _ ->
            call += 1
            if (call <= 3) paperOutBit to true else printedBit to true
        }

        assertEquals(PrintOutcome.COMPLETED, result.outcome)
        assertNull(result.condition, "a job that printed has nothing outstanding to fix")
    }

    @Test
    fun `the condition is reported as it happens, not only at the end`() {
        val seen = mutableListOf<PrinterCondition?>()
        poll(timeoutSeconds = 0.0, onCondition = { seen.add(it) }) { _, _ -> paperOutBit to true }

        assertEquals(listOf(PrinterCondition.OUT_OF_PAPER), seen)
    }

    /** Only on a change - a job stuck for five minutes must not emit a notification per poll. */
    @Test
    fun `an unchanged condition is reported once`() {
        val seen = mutableListOf<PrinterCondition?>()
        var call = 0
        poll(timeoutSeconds = 0.02, onCondition = { seen.add(it) }) { _, _ ->
            call += 1
            paperOutBit to true
        }

        assertTrue(call >= 1)
        assertEquals(listOf(PrinterCondition.OUT_OF_PAPER), seen)
    }

    /** Paper is what the person at the printer sees first, whatever else the queue also reports. */
    @Test
    fun `out of paper wins over a blocked queue`() {
        val blockedDevQ = 0x00000200
        val result = poll(timeoutSeconds = 0.0) { _, _ -> (paperOutBit or blockedDevQ) to true }

        assertEquals(PrinterCondition.OUT_OF_PAPER, result.condition)
    }

    /** A real driver error still ends the job, even alongside a recoverable-looking bit. */
    @Test
    fun `a driver error beats a recoverable condition`() {
        assertEquals(PrintOutcome.FAILED, poll { _, _ -> (errorBit or paperOutBit) to true }.outcome)
    }
}
