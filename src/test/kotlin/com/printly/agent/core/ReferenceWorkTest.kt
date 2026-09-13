package com.printly.agent.core

import com.printly.agent.db.Database
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

/**
 * What a re-delivered job reference is worth spending a request on.
 *
 * The reconciliation poll re-lists everything the backend still considers
 * outstanding, every ten seconds, for ever. Each of those used to cost a full
 * order lookup *before* anything checked whether there was anything to find
 * out - the duplicate guard sits downstream of the request, not in front of
 * it. A shop with a backlog paid that for every waiting job on every pass; a
 * shop with a few UNKNOWN jobs waiting on somebody to walk over and resolve
 * them paid it for those for ever, because a job waiting on a human is
 * outstanding for as long as the human takes. Everything else the agent
 * needed the backend for queued behind it, including the shop's own screen.
 */
class ReferenceWorkTest {

    private fun row(state: String, priority: Boolean = false) = Database.JobRow(
        jobId = "job-1",
        orderId = "order-1",
        orderCode = "HH-000001",
        state = state,
        printerWindowsName = null,
        attemptCount = 0,
        lastError = null,
        receivedAt = "2026-01-01T00:00:00Z",
        updatedAt = "2026-01-01T00:00:00Z",
        scheduledPrintAt = null,
        priority = priority,
    )

    @Test
    fun `a reference never seen here is new work`() {
        assertEquals(ReferenceWork.NEW, referenceWorkFor(null))
    }

    /**
     * And only this one holds the printer: a reference already in the queue
     * cannot sort ahead of anything, because it is already there.
     */
    @Test
    fun `a job still working its way through is only worth re-asking about`() {
        listOf("RECEIVED", "VALIDATING", "DOWNLOADING", "DOWNLOADED", "SUBMITTING", "SUBMITTED", "PRINTING")
            .forEach { state ->
                assertEquals(ReferenceWork.RECHECK, referenceWorkFor(row(state)), "state $state")
            }
    }

    /** Nothing left to decide. */
    @Test
    fun `a finished job is not worth a request`() {
        listOf("COMPLETED", "FAILED", "CANCELLED").forEach { state ->
            assertEquals(ReferenceWork.NONE, referenceWorkFor(row(state)), "state $state")
        }
    }

    /**
     * The expensive one. UNKNOWN stays outstanding on the backend until a
     * human resolves it, so it is re-listed on every pass indefinitely - and
     * locally it is terminal, so there was never anything to do with it.
     */
    @Test
    fun `a job waiting on a human is not re-asked about for ever`() {
        assertEquals(ReferenceWork.NONE, referenceWorkFor(row("UNKNOWN")))
    }

    /** Already at the counter - the one thing a re-check could discover is already known. */
    @Test
    fun `a job that already has priority has nothing left to learn`() {
        assertEquals(ReferenceWork.NONE, referenceWorkFor(row("RECEIVED", priority = true)))
        assertEquals(ReferenceWork.NONE, referenceWorkFor(row("DOWNLOADING", priority = true)))
    }
}
