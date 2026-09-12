package com.printly.agent.printing

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

/**
 * `poll_job_outcome`'s decision logic, without touching WinSpool - mirrors
 * the Python agent's `test_print_outcome_polling.py`, which monkeypatches
 * `_job_status` the same way this injects [statusLookup].
 */
class SpoolerOutcomePollerTest {

    private val printedBit = 0x00000080
    private val errorBit = 0x00000002
    private val paperOutBit = 0x00000040
    private val printingBit = 0x00000010 // informational only, never decisive alone

    @Test
    fun `printed bit is completed`() {
        val outcome = SpoolerOutcomePoller.pollJobOutcome("Printer", "job-1", 5.0, 0.0) { _, _ -> printedBit to true }
        assertEquals(PrintOutcome.COMPLETED, outcome)
    }

    @Test
    fun `error bit is failed`() {
        val outcome = SpoolerOutcomePoller.pollJobOutcome("Printer", "job-1", 5.0, 0.0) { _, _ -> errorBit to true }
        assertEquals(PrintOutcome.FAILED, outcome)
    }

    @Test
    fun `paperout bit is failed not a silent success`() {
        val outcome = SpoolerOutcomePoller.pollJobOutcome("Printer", "job-1", 5.0, 0.0) { _, _ -> paperOutBit to true }
        assertEquals(PrintOutcome.FAILED, outcome)
    }

    @Test
    fun `job disappearing from the queue with no prior error is completed`() {
        // The common case: a driver that removes a job from its queue the
        // instant it finishes, rather than leaving a PRINTED bit to observe.
        val outcome = SpoolerOutcomePoller.pollJobOutcome("Printer", "job-1", 5.0, 0.0) { _, _ -> 0 to false }
        assertEquals(PrintOutcome.COMPLETED, outcome)
    }

    @Test
    fun `job disappearing after a seen error is failed not completed`() {
        var call = 0
        val outcome = SpoolerOutcomePoller.pollJobOutcome("Printer", "job-1", 5.0, 0.0) { _, _ ->
            call += 1
            if (call == 1) errorBit to true else 0 to false
        }
        assertEquals(PrintOutcome.FAILED, outcome)
    }

    @Test
    fun `spooler unreachable is unknown never a guess`() {
        val outcome = SpoolerOutcomePoller.pollJobOutcome("Printer", "job-1", 5.0, 0.0) { _, _ -> null to false }
        assertEquals(PrintOutcome.UNKNOWN, outcome)
    }

    @Test
    fun `still printing at the deadline is unknown not completed`() {
        // Never resolves, never errors - just still going when time runs out.
        val outcome = SpoolerOutcomePoller.pollJobOutcome("Printer", "job-1", 0.0, 0.0) { _, _ -> printingBit to true }
        assertEquals(PrintOutcome.UNKNOWN, outcome)
    }
}
