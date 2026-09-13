package com.printly.agent.jobs

import com.printly.agent.credentials.CredentialStore
import com.printly.agent.db.Database
import com.printly.agent.net.PrintlyApiClient
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.nio.file.Path

/**
 * Where "nothing has printed yet" stops being true, and what the pipeline is
 * allowed to say on either side of that line.
 *
 * The shop reads FAILED as "print it again". So every error path has to know
 * whether the document has reached a driver, because past that point the pages
 * may be in the tray and the only honest answers are COMPLETED or UNKNOWN.
 */
class InkOnPaperBoundaryTest {

    private val shopId = "11111111-1111-1111-1111-111111111111"

    private fun context(temp: Path, api: PrintlyApiClient, db: Database) = JobContext(
        api = api,
        db = db,
        tempDir = temp,
        maxRetryAttempts = 1,
        downloadTimeoutSeconds = 5,
        jobTimeoutSeconds = 10.0,
        credential = CredentialStore.AgentCredential("agent-1", shopId, "secret"),
    )

    /**
     * Closing the app mid-job must not be recorded as the job failing.
     *
     * `CancellationException` is an `IllegalStateException`, so a plain
     * `catch (Exception)` swallows it: cancelling the scope on shutdown ran
     * the pipeline's failure handler, wrote FAILED for a job that was printing
     * perfectly well, and had the shop reprint it the next morning.
     */
    @Test
    fun `cancellation is not a print failure`(@TempDir temp: Path) {
        val api = mockk<PrintlyApiClient>()
        every { api.claimJob(any(), any()) } throws CancellationException("the agent is shutting down")

        Database(temp.resolve("agent.db")).use { db ->
            db.insertJobReference("job-1", "order-1", "HH-000001", shopId = shopId)
            val ctx = context(temp, api, db)

            assertThrows(CancellationException::class.java) {
                runBlocking { processJob(ctx, "job-1") }
            }

            assertNotEquals(
                FAILED,
                db.getJob("job-1")?.state,
                "shutting down is not the job failing - marking it FAILED is what makes the shop reprint it",
            )
            assertEquals(VALIDATING, db.getJob("job-1")?.state)
        }
    }

    /**
     * The other side of the same line: an error before anything reached a
     * driver is a real failure and must still be reported as one. Weakening
     * that would leave genuinely failed orders sitting in the shop's list with
     * nobody told.
     */
    @Test
    fun `a failure before the printer is still a failure`(@TempDir temp: Path) {
        // An unresolvable host, so the download cannot succeed and nothing can
        // possibly have been printed.
        val api = PrintlyApiClient("https://printly-agent-test.invalid")

        Database(temp.resolve("agent.db")).use { db ->
            db.insertJobReference("job-2", "order-2", "HH-000002", shopId = shopId)
            val ctx = context(temp, api, db)

            runBlocking { processJob(ctx, "job-2") }

            assertEquals(FAILED, db.getJob("job-2")?.state)
        }
    }
}
