package com.printly.agent.jobs

import com.printly.agent.credentials.CredentialStore
import com.printly.agent.db.Database
import com.printly.agent.net.PrintlyApiClient
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.nio.file.Path

/**
 * In-shop priority only means anything if it survives the trip from the order
 * lookup to the print queue - and on the paths that matter most it did not.
 *
 * A scan says a student is standing at the counter and the backend has agreed
 * to serve them next. The agent read that correctly and then lost it twice
 * over: the grant lived only in the argument list of the call that enqueued
 * the job, so an order held back for a later slot, or one a restart had to
 * resume, was re-enqueued from its stored row with no priority at all. And
 * because people normally scan *after* ordering ahead, the grant usually
 * arrived for a job already in the queue, where the duplicate guard dropped
 * the whole delivery on the floor.
 */
class InShopPriorityTest {

    private val supervisor = SupervisorJob()
    private val scope = CoroutineScope(Dispatchers.Default + supervisor)

    @AfterEach fun tearDown() = supervisor.cancel()

    private val shopId = "shop-1"

    private fun context(temp: Path): Pair<JobContext, Database> {
        val db = Database(temp.resolve("agent.db"))
        val ctx = JobContext(
            api = PrintlyApiClient("https://example.invalid"),
            db = db,
            tempDir = temp,
            maxRetryAttempts = 3,
            downloadTimeoutSeconds = 30,
            jobTimeoutSeconds = 300.0,
            credential = CredentialStore.AgentCredential("agent-1", shopId, "secret"),
        )
        return ctx to db
    }

    /**
     * Holds the single worker so nothing is actually printed, and the queue
     * can be inspected as the shop's screen would see it.
     */
    private suspend fun blockedQueue(): Pair<PrintQueue, CompletableDeferred<Unit>> {
        val queue = PrintQueue(scope, workers = 1)
        val running = CompletableDeferred<Unit>()
        val gate = CompletableDeferred<Unit>()
        queue.enqueue("blocker", "HH-000001") { running.complete(Unit); gate.await() }
        withTimeout(5_000) { running.await() }
        return queue to gate
    }

    @Test
    fun `the row remembers that a student scanned`(@TempDir temp: Path) {
        Database(temp.resolve("agent.db")).use { db ->
            db.insertJobReference("job-plain", "order-1", "HH-000001", shopId = shopId)
            db.insertJobReference("job-scanned", "order-2", "HH-000002", shopId = shopId, priority = true)

            assertFalse(db.getJob("job-plain")!!.priority, "nothing asked for priority")
            assertTrue(db.getJob("job-scanned")!!.priority)

            // Granted later, which is the normal case - the student ordered
            // ahead and has now walked in.
            db.markPriority("job-plain")
            assertTrue(db.getJob("job-plain")!!.priority)
        }
    }

    /**
     * The order was scheduled for a later slot, so it was written down now and
     * enqueued half an hour later from its row. That path enqueued with the
     * default - no priority - and the scan read at intake was gone.
     */
    @Test
    fun `a held-back order keeps the priority its scan granted`(@TempDir temp: Path): Unit = runBlocking {
        val (ctx, db) = context(temp)
        val (queue, gate) = blockedQueue()

        db.insertJobReference("job-due", "order-due", "HH-000090", "2020-01-01T00:00:00Z", shopId, priority = true)
        db.insertJobReference("job-ordinary", "order-ord", "HH-000008", "2020-01-01T00:00:00Z", shopId)

        processDueScheduledJobs(ctx, queue)

        assertEquals(listOf("HH-000090"), queue.waitingPriority(), "the slot came due; the scan must still count")
        assertEquals(listOf("HH-000090", "HH-000008"), queue.waiting(), "and it goes ahead of the lower number")

        gate.complete(Unit)
        db.close()
    }

    /** Same again across a restart: the lookup that knew ran in the process that died. */
    @Test
    fun `a resumed order keeps the priority its scan granted`(@TempDir temp: Path): Unit = runBlocking {
        val (ctx, db) = context(temp)
        val (queue, gate) = blockedQueue()

        db.insertJobReference("job-resume", "order-r", "HH-000090", null, shopId, priority = true)
        db.updateJobState("job-resume", DOWNLOADING)
        db.insertJobReference("job-other", "order-o", "HH-000008", null, shopId)
        db.updateJobState("job-other", DOWNLOADING)

        resumeInterruptedJobs(ctx, queue)

        assertEquals(listOf("HH-000090"), queue.waitingPriority())
        assertEquals(listOf("HH-000090", "HH-000008"), queue.waiting())

        gate.complete(Unit)
        db.close()
    }

    /**
     * The common case, and the one that was silently dropped: the student
     * ordered ahead, the job has been waiting here for twenty minutes, and
     * they have just scanned at the counter. The reconciliation poll re-lists
     * the job and looks the order up again, so this is how the grant arrives.
     */
    @Test
    fun `a scan after the order is queued moves it to the front`(@TempDir temp: Path): Unit = runBlocking {
        val (ctx, db) = context(temp)
        val (queue, gate) = blockedQueue()

        // Delivered earlier, with no priority, and now waiting behind others.
        handleJobReference(ctx, queue, "job-90", "order-90", "HH-000090")
        handleJobReference(ctx, queue, "job-8", "order-8", "HH-000008")
        assertEquals(listOf("HH-000008", "HH-000090"), queue.waiting())

        // The same reference again, the backend now saying the student is here.
        handleJobReference(ctx, queue, "job-90", "order-90", "HH-000090", priority = true)

        assertEquals(listOf("HH-000090"), queue.waitingPriority())
        assertEquals(listOf("HH-000090", "HH-000008"), queue.waiting(), "the counter goes next, not after")
        assertTrue(db.getJob("job-90")!!.priority, "and it is written down, so a restart keeps it")

        gate.complete(Unit)
        db.close()
    }

    /** A repeat delivery must still never print the document a second time. */
    @Test
    fun `a repeat delivery with priority does not queue the job twice`(@TempDir temp: Path): Unit = runBlocking {
        val (ctx, db) = context(temp)
        val (queue, gate) = blockedQueue()

        handleJobReference(ctx, queue, "job-90", "order-90", "HH-000090")
        repeat(3) { handleJobReference(ctx, queue, "job-90", "order-90", "HH-000090", priority = true) }

        assertEquals(listOf("HH-000090"), queue.waiting(), "one entry, however many times it is delivered")
        assertEquals(2, queue.depth, "the blocker plus the one order")

        gate.complete(Unit)
        db.close()
    }

    /** A job already finished is not dragged back into the queue by a late scan. */
    @Test
    fun `a scan for a job that already printed changes nothing`(@TempDir temp: Path): Unit = runBlocking {
        val (ctx, db) = context(temp)
        val (queue, gate) = blockedQueue()

        db.insertJobReference("job-done", "order-done", "HH-000090", null, shopId)
        db.updateJobState("job-done", COMPLETED)

        handleJobReference(ctx, queue, "job-done", "order-done", "HH-000090", priority = true)

        assertEquals(emptyList<String>(), queue.waiting())
        assertFalse(db.getJob("job-done")!!.priority, "nothing left to prioritise")

        gate.complete(Unit)
        delay(50)
        db.close()
    }
}
