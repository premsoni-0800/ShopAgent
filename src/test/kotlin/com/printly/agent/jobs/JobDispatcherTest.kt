package com.printly.agent.jobs

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
import java.util.concurrent.atomic.AtomicInteger

/**
 * The two properties the dispatcher exists for, and the one it must never
 * lose: jobs overlap, submission never blocks the caller, and the same job
 * never runs twice.
 */
class JobDispatcherTest {

    private val supervisor = SupervisorJob()
    private val scope = CoroutineScope(Dispatchers.Default + supervisor)

    @AfterEach fun tearDown() = supervisor.cancel()

    @Test
    fun `jobs actually run concurrently`(): Unit = runBlocking {
        val dispatcher = JobDispatcher(scope, maxConcurrent = 3)
        val running = AtomicInteger()
        val peak = AtomicInteger()
        val allStarted = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()

        repeat(3) { i ->
            dispatcher.submit("job-$i") {
                val now = running.incrementAndGet()
                peak.updateAndGet { max -> maxOf(max, now) }
                if (now == 3) allStarted.complete(Unit)
                release.await()
                running.decrementAndGet()
            }
        }

        // Serial execution could never get all three into the block at once,
        // so this await is the whole assertion - it simply would not return.
        withTimeout(5_000) { allStarted.await() }
        assertEquals(3, peak.get())
        release.complete(Unit)
    }

    @Test
    fun `concurrency is bounded by maxConcurrent`(): Unit = runBlocking {
        val dispatcher = JobDispatcher(scope, maxConcurrent = 2)
        val running = AtomicInteger()
        val peak = AtomicInteger()

        repeat(8) { i ->
            dispatcher.submit("job-$i") {
                val now = running.incrementAndGet()
                peak.updateAndGet { max -> maxOf(max, now) }
                delay(50)
                running.decrementAndGet()
            }
        }

        withTimeout(10_000) {
            while (dispatcher.activeCount > 0) delay(10)
        }
        assertEquals(2, peak.get(), "more than maxConcurrent jobs were in flight at once")
    }

    /**
     * The reconciliation poll re-lists an outstanding job every 10 seconds for
     * as long as it takes to print, so this is the ordinary case, not an edge
     * one - and the consequence of getting it wrong is a customer's document
     * printed twice.
     */
    @Test
    fun `a job already in flight is never started again`(): Unit = runBlocking {
        val dispatcher = JobDispatcher(scope, maxConcurrent = 4)
        val runs = AtomicInteger()
        val release = CompletableDeferred<Unit>()

        val first = dispatcher.submit("same-job") {
            runs.incrementAndGet()
            release.await()
        }
        withTimeout(5_000) { while (runs.get() == 0) delay(5) }

        val second = dispatcher.submit("same-job") { runs.incrementAndGet() }
        val third = dispatcher.submit("same-job") { runs.incrementAndGet() }

        assertTrue(first, "the first submission should be accepted")
        assertFalse(second, "a redelivery while still running must be dropped")
        assertFalse(third)
        assertEquals(1, runs.get())
        release.complete(Unit)
    }

    /** Once it has finished, the id is free again - a genuine retry must not be blocked forever. */
    @Test
    fun `the same id can be submitted again after it finishes`(): Unit = runBlocking {
        val dispatcher = JobDispatcher(scope, maxConcurrent = 2)
        val runs = AtomicInteger()

        dispatcher.submit("job") { runs.incrementAndGet() }
        withTimeout(5_000) { while (dispatcher.activeCount > 0) delay(5) }

        assertTrue(dispatcher.submit("job") { runs.incrementAndGet() })
        withTimeout(5_000) { while (dispatcher.activeCount > 0) delay(5) }
        assertEquals(2, runs.get())
    }

    /** A thrown job must not poison the dispatcher, and must still free its slot and its id. */
    @Test
    fun `a failing job releases its slot and does not stop later jobs`(): Unit = runBlocking {
        val dispatcher = JobDispatcher(scope, maxConcurrent = 1)
        val completed = AtomicInteger()

        dispatcher.submit("boom") { error("pipeline blew up") }
        dispatcher.submit("fine") { completed.incrementAndGet() }

        withTimeout(5_000) { while (dispatcher.activeCount > 0) delay(10) }
        assertEquals(1, completed.get())
    }
}
