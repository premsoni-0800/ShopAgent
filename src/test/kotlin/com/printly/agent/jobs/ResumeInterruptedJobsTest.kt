package com.printly.agent.jobs

import com.printly.agent.credentials.CredentialStore
import com.printly.agent.db.Database
import com.printly.agent.net.PrintlyApiClient
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.nio.file.Path

/**
 * The restart sweep must not disturb a job that is already running.
 *
 * It rewinds a stranded job to RECEIVED so [processJob] can replay it from the
 * top. Doing that *before* submitting meant a job the sweep found mid-flight
 * had its state pulled back underneath it - the dispatcher then dropped the
 * duplicate submission, so nothing replayed it, and the job still running
 * reached DOWNLOADING from RECEIVED, tripped the transition guard, and was
 * reported as "download failed after 3 attempts" having downloaded fine.
 *
 * Startup is exactly when both happen at once: the sweep runs while the stream
 * is delivering the day's jobs.
 */
class ResumeInterruptedJobsTest {

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

    @Test
    fun `a job already in flight is left exactly as it is`(@TempDir temp: Path): Unit = runBlocking {
        val (ctx, db) = context(temp)
        val jobId = "job-in-flight"
        db.insertJobReference(jobId, "order-1", "AA-000001", null, shopId)
        db.updateJobState(jobId, DOWNLOADING)

        // Occupy the dispatcher with this id, exactly as a job mid-download does.
        val queue = PrintQueue(scope, workers = 2)
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        queue.enqueue(jobId, "AA-000001") {
            started.complete(Unit)
            release.await()
        }
        withTimeout(5_000) { started.await() }

        resumeInterruptedJobs(ctx, queue)

        assertEquals(
            DOWNLOADING,
            db.getJob(jobId)?.state,
            "the sweep rewound a running job, which is what made it fail for the wrong reason",
        )
        release.complete(Unit)
        db.close()
    }

    /**
     * The other half: a job the sweep is genuinely responsible for must still
     * be rewound, or a restart strands it for good - no later redelivery gets
     * past the duplicate guard.
     */
    @Test
    fun `a stranded job is still rewound and replayed`(@TempDir temp: Path): Unit = runBlocking {
        val (ctx, db) = context(temp)
        val jobId = "job-stranded"
        db.insertJobReference(jobId, "order-2", "AA-000002", null, shopId)
        db.updateJobState(jobId, DOWNLOADING)

        // workers 0 would deadlock; instead let it run and observe the
        // rewind, which happens first thing inside the submitted work.
        val queue = PrintQueue(scope, workers = 1)
        resumeInterruptedJobs(ctx, queue)

        withTimeout(5_000) {
            while (db.getJob(jobId)?.state == DOWNLOADING) kotlinx.coroutines.delay(20)
        }
        // processJob then runs against an unreachable API and fails the job,
        // which is fine - what matters is that the rewind happened at all.
        assertEquals(
            true,
            db.getJob(jobId)?.state != DOWNLOADING,
            "a stranded job must be replayed from the top, or it is stuck for good",
        )
        db.close()
    }
}
