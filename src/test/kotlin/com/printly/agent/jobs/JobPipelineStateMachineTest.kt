package com.printly.agent.jobs

import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class JobPipelineStateMachineTest {

    @Test
    fun `happy path is allowed step by step`() {
        val path = listOf(RECEIVED, VALIDATING, DOWNLOADING, DOWNLOADED, SUBMITTING, SUBMITTED, PRINTING, COMPLETED)
        for (i in 0 until path.size - 1) {
            assertTrue(canTransition(path[i], path[i + 1]), "${path[i]} -> ${path[i + 1]} should be allowed")
        }
    }

    /**
     * The state machine is what enforces that a job cannot be recorded as
     * printing without first being recorded as about to print. That matters
     * because SUBMITTING is the marker [com.printly.agent.db.Database
     * .resumableJobs] uses to decide a job is no longer safe to replay - if
     * anything could reach SUBMITTED around it, a job could be printing while
     * still looking replayable, and a restart would print it twice.
     */
    @Test
    fun `nothing reaches submitted without passing through submitting`() {
        assertTrue(canTransition(DOWNLOADED, SUBMITTING))
        assertTrue(canTransition(SUBMITTING, SUBMITTED))
        assertFalse(canTransition(DOWNLOADED, SUBMITTED), "DOWNLOADED is still replayable; SUBMITTED is not")
        assertFalse(canTransition(DOWNLOADING, SUBMITTED))
        assertFalse(canTransition(VALIDATING, SUBMITTING))
    }

    /** A job that never got as far as the driver is still an honest failure. */
    @Test
    fun `submitting can still fail or be cancelled`() {
        assertTrue(canTransition(SUBMITTING, FAILED))
        assertTrue(canTransition(SUBMITTING, CANCELLED))
        assertTrue(canTransition(SUBMITTING, UNKNOWN))
    }

    @Test
    fun `cannot skip straight from received to completed`() {
        assertFalse(canTransition(RECEIVED, COMPLETED))
    }

    @Test
    fun `cannot skip download steps`() {
        assertFalse(canTransition(RECEIVED, DOWNLOADED))
        assertFalse(canTransition(VALIDATING, SUBMITTED))
    }

    @Test
    fun `repeating the same state is idempotent`() {
        assertTrue(canTransition(DOWNLOADING, DOWNLOADING))
        assertTrue(canTransition(COMPLETED, COMPLETED))
    }

    @Test
    fun `terminal states accept no further transition except repeat`() {
        for (terminal in listOf(COMPLETED, FAILED, CANCELLED, UNKNOWN)) {
            assertFalse(canTransition(terminal, RECEIVED))
            assertFalse(canTransition(terminal, PRINTING))
        }
    }

    @Test
    fun `cancellation allowed early but not after submission`() {
        assertTrue(canTransition(RECEIVED, CANCELLED))
        assertTrue(canTransition(DOWNLOADED, CANCELLED))
        assertFalse(canTransition(SUBMITTED, CANCELLED))
        assertFalse(canTransition(PRINTING, CANCELLED))
    }

    @Test
    fun `printing can resolve to unknown when the spooler never confirms`() {
        assertTrue(canTransition(PRINTING, UNKNOWN))
    }

    @Test
    fun `unknown is terminal and never auto-retried`() {
        // Only a human resolving the matching PRINT_UNKNOWN on the backend
        // moves this on - see PrintJobResolutionService. Nothing local ever does.
        assertFalse(canTransition(UNKNOWN, PRINTING))
        assertFalse(canTransition(UNKNOWN, RECEIVED))
        assertTrue(canTransition(UNKNOWN, UNKNOWN))
    }
}
