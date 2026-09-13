package com.printly.agent.db

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.io.TempDir
import org.junit.jupiter.api.Test
import java.nio.file.Path

class DatabaseTest {

    @Test
    fun `insertJobReference is true only the first time`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            assertTrue(db.insertJobReference("job-1", "order-1", "SH001-000001", shopId = "11111111-1111-1111-1111-111111111111"))
            assertFalse(db.insertJobReference("job-1", "order-1", "SH001-000001", shopId = "11111111-1111-1111-1111-111111111111"))
            assertFalse(db.insertJobReference("job-1", "order-1", "SH001-000001", shopId = "11111111-1111-1111-1111-111111111111"))
        }
    }

    @Test
    fun `a completed job redelivered is still a no-op`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-2", "order-2", null, shopId = "11111111-1111-1111-1111-111111111111")
            db.updateJobState("job-2", "COMPLETED")

            // Same job id arriving again - e.g. a reconnect re-list, or a
            // redelivered SSE push - must not look "new" a second time.
            assertFalse(db.insertJobReference("job-2", "order-2", null, shopId = "11111111-1111-1111-1111-111111111111"))
            assertEquals("COMPLETED", db.getJob("job-2")?.state)
        }
    }

    /**
     * A job that is being handed to a driver may already be printing, so it is
     * not safe to replay on restart - that is the whole reason SUBMITTING
     * exists. DOWNLOADED is safe, and has to stay safe, or an ordinary crash
     * between downloading and printing would need a human every time.
     */
    @Test
    fun `a job on its way to the printer is never resumed`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            val shop = "11111111-1111-1111-1111-111111111111"
            db.insertJobReference("job-downloaded", "order-1", "HH-000001", shopId = shop)
            db.insertJobReference("job-submitting", "order-2", "HH-000002", shopId = shop)
            db.updateJobState("job-downloaded", "DOWNLOADED")
            db.updateJobState("job-submitting", "SUBMITTING")

            val resumable = db.resumableJobs(shop).map { it.jobId }

            assertTrue("job-downloaded" in resumable, "nothing has reached a printer, so replaying is safe")
            assertFalse("job-submitting" in resumable, "this one may be 150 pages into a 300-page order")
        }
    }

    @Test
    fun `unresolvedJobs excludes terminal states`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-a", "order-a", null, shopId = "11111111-1111-1111-1111-111111111111")
            db.insertJobReference("job-b", "order-b", null, shopId = "11111111-1111-1111-1111-111111111111")
            db.updateJobState("job-b", "COMPLETED")

            val unresolved = db.unresolvedJobs("11111111-1111-1111-1111-111111111111").map { it.jobId }.toSet()
            assertEquals(setOf("job-a"), unresolved)
        }
    }

    @Test
    fun `updateJobState increments attempt count`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-c", "order-c", null, shopId = "11111111-1111-1111-1111-111111111111")
            db.updateJobState("job-c", "FAILED", lastError = "boom", incrementAttempt = true)
            val row = db.getJob("job-c")!!
            assertEquals(1, row.attemptCount)
            assertEquals("boom", row.lastError)
        }
    }

    @Test
    fun `scheduled print at is stored and not due until its time`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-d", "order-d", null, scheduledPrintAt = "2999-01-01T00:00:00Z", shopId = "11111111-1111-1111-1111-111111111111")
            assertEquals("2999-01-01T00:00:00Z", db.getJob("job-d")?.scheduledPrintAt)

            // Far in the future - not due at any "now" this test will ever run at.
            assertTrue(db.dueScheduledJobs("2026-01-01T00:00:00Z", "11111111-1111-1111-1111-111111111111").isEmpty())
        }
    }

    @Test
    fun `dueScheduledJobs returns only received jobs past their time`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-e", "order-e", null, scheduledPrintAt = "2020-01-01T00:00:00Z", shopId = "11111111-1111-1111-1111-111111111111")
            db.insertJobReference("job-f", "order-f", null, scheduledPrintAt = "2999-01-01T00:00:00Z", shopId = "11111111-1111-1111-1111-111111111111")
            db.insertJobReference("job-g", "order-g", null, shopId = "11111111-1111-1111-1111-111111111111") // never scheduled - handled immediately, not by this path
            db.insertJobReference("job-h", "order-h", null, scheduledPrintAt = "2020-01-01T00:00:00Z", shopId = "11111111-1111-1111-1111-111111111111")
            db.updateJobState("job-h", "COMPLETED") // due, but already printed - must not be picked up again

            val due = db.dueScheduledJobs("2026-01-01T00:00:00Z", "11111111-1111-1111-1111-111111111111").map { it.jobId }.toSet()
            assertEquals(setOf("job-e"), due)
        }
    }

    // -----------------------------------------------------------------------
    // One machine, several shops over its life. A shop signs in with its own
    // credentials and the agent re-pairs to them, so the jobs already on disk
    // belong to whoever had it before - and are invisible to the new shop's
    // credential, which is exactly why they must not be picked up.
    // -----------------------------------------------------------------------

    private val shopA = "11111111-1111-1111-1111-111111111111"
    private val shopB = "22222222-2222-2222-2222-222222222222"

    @Test
    fun `a resumable job belongs only to the shop that received it`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-a", "order-a", "AAA-1", shopId = shopA)
            db.insertJobReference("job-b", "order-b", "BBB-1", shopId = shopB)

            assertEquals(listOf("job-a"), db.resumableJobs(shopA).map { it.jobId })
            assertEquals(listOf("job-b"), db.resumableJobs(shopB).map { it.jobId })
        }
    }

    @Test
    fun `unresolved jobs are never another shop's`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-a", "order-a", null, shopId = shopA)
            db.insertJobReference("job-b", "order-b", null, shopId = shopB)
            db.updateJobState("job-a", "UNKNOWN", lastError = "check the printer")

            assertEquals(listOf("job-a"), db.unresolvedJobs(shopA).map { it.jobId })
            assertEquals(listOf("job-b"), db.unresolvedJobs(shopB).map { it.jobId })
        }
    }

    @Test
    fun `a scheduled job comes due only for its own shop`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-a", "order-a", null, scheduledPrintAt = "2020-01-01T00:00:00Z", shopId = shopA)

            assertEquals(listOf("job-a"), db.dueScheduledJobs("2026-01-01T00:00:00Z", shopA).map { it.jobId })
            assertEquals(emptyList<String>(), db.dueScheduledJobs("2026-01-01T00:00:00Z", shopB).map { it.jobId })
        }
    }

    /**
     * Duplicate-print protection is global on purpose. Job ids are unique
     * across the estate, so a redelivery must be refused whoever is asking -
     * scoping this by shop would turn one re-pair into a second copy of a
     * customer's document.
     */
    @Test
    fun `the duplicate guard is not weakened by shop scoping`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            assertTrue(db.insertJobReference("job-x", "order-x", null, shopId = shopA))
            assertFalse(db.insertJobReference("job-x", "order-x", null, shopId = shopB))
        }
    }

    /**
     * The rotating batch that looks for a counter scan landing late. It has to
     * skip everything a scan could no longer change, or the rotation spends
     * its whole budget re-asking about jobs that finished days ago.
     */
    @Test
    fun `priorityCandidates offers only the jobs a scan could still move`(@TempDir tempDir: Path) {
        val shop = "11111111-1111-1111-1111-111111111111"
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-open", "order-1", "HH-000001", shopId = shop)
            db.insertJobReference("job-printing", "order-2", "HH-000002", shopId = shop)
            db.updateJobState("job-printing", "PRINTING")
            db.insertJobReference("job-done", "order-3", "HH-000003", shopId = shop)
            db.updateJobState("job-done", "COMPLETED")
            // Terminal locally, outstanding on the backend for as long as a
            // human takes to resolve it - the one that used to be re-asked
            // about for ever.
            db.insertJobReference("job-unknown", "order-4", "HH-000004", shopId = shop)
            db.updateJobState("job-unknown", "UNKNOWN")
            db.insertJobReference("job-at-counter", "order-5", "HH-000005", shopId = shop, priority = true)
            db.insertJobReference("job-other-shop", "order-6", "HH-000006", shopId = "22222222-2222-2222-2222-222222222222")

            assertEquals(
                listOf("job-open", "job-printing"),
                db.priorityCandidates(shop, 10, 0).map { it.jobId },
            )
        }
    }

    /** Paged, so a long queue costs the backend exactly what a short one does. */
    @Test
    fun `priorityCandidates rotates through a backlog a page at a time`(@TempDir tempDir: Path) {
        val shop = "11111111-1111-1111-1111-111111111111"
        Database(tempDir.resolve("agent.db")).use { db ->
            repeat(10) { n -> db.insertJobReference("job-$n", "order-$n", "HH-%06d".format(n), shopId = shop) }

            val first = db.priorityCandidates(shop, 4, 0).map { it.jobId }
            val second = db.priorityCandidates(shop, 4, 4).map { it.jobId }
            val third = db.priorityCandidates(shop, 4, 8).map { it.jobId }

            assertEquals(4, first.size)
            assertEquals(4, second.size)
            assertEquals(2, third.size, "the last page is short, and tells the cursor to start over")
            assertEquals(10, (first + second + third).toSet().size, "the rotation must cover every job exactly once")
            assertTrue(db.priorityCandidates(shop, 4, 10).isEmpty())
        }
    }
}
