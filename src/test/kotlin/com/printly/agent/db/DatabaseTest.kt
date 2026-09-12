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
            assertTrue(db.insertJobReference("job-1", "order-1", "SH001-000001"))
            assertFalse(db.insertJobReference("job-1", "order-1", "SH001-000001"))
            assertFalse(db.insertJobReference("job-1", "order-1", "SH001-000001"))
        }
    }

    @Test
    fun `a completed job redelivered is still a no-op`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-2", "order-2", null)
            db.updateJobState("job-2", "COMPLETED")

            // Same job id arriving again - e.g. a reconnect re-list, or a
            // redelivered SSE push - must not look "new" a second time.
            assertFalse(db.insertJobReference("job-2", "order-2", null))
            assertEquals("COMPLETED", db.getJob("job-2")?.state)
        }
    }

    @Test
    fun `unresolvedJobs excludes terminal states`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-a", "order-a", null)
            db.insertJobReference("job-b", "order-b", null)
            db.updateJobState("job-b", "COMPLETED")

            val unresolved = db.unresolvedJobs().map { it.jobId }.toSet()
            assertEquals(setOf("job-a"), unresolved)
        }
    }

    @Test
    fun `updateJobState increments attempt count`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-c", "order-c", null)
            db.updateJobState("job-c", "FAILED", lastError = "boom", incrementAttempt = true)
            val row = db.getJob("job-c")!!
            assertEquals(1, row.attemptCount)
            assertEquals("boom", row.lastError)
        }
    }

    @Test
    fun `scheduled print at is stored and not due until its time`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-d", "order-d", null, scheduledPrintAt = "2999-01-01T00:00:00Z")
            assertEquals("2999-01-01T00:00:00Z", db.getJob("job-d")?.scheduledPrintAt)

            // Far in the future - not due at any "now" this test will ever run at.
            assertTrue(db.dueScheduledJobs("2026-01-01T00:00:00Z").isEmpty())
        }
    }

    @Test
    fun `dueScheduledJobs returns only received jobs past their time`(@TempDir tempDir: Path) {
        Database(tempDir.resolve("agent.db")).use { db ->
            db.insertJobReference("job-e", "order-e", null, scheduledPrintAt = "2020-01-01T00:00:00Z")
            db.insertJobReference("job-f", "order-f", null, scheduledPrintAt = "2999-01-01T00:00:00Z")
            db.insertJobReference("job-g", "order-g", null) // never scheduled - handled immediately, not by this path
            db.insertJobReference("job-h", "order-h", null, scheduledPrintAt = "2020-01-01T00:00:00Z")
            db.updateJobState("job-h", "COMPLETED") // due, but already printed - must not be picked up again

            val due = db.dueScheduledJobs("2026-01-01T00:00:00Z").map { it.jobId }.toSet()
            assertEquals(setOf("job-e"), due)
        }
    }
}
