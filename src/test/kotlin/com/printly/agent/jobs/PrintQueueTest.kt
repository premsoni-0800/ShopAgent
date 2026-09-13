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
import java.util.Collections
import java.util.concurrent.atomic.AtomicInteger

/**
 * The queue exists so a shop prints in the order its customers are standing
 * in. Before it, printing was bounded but unordered - whichever job's
 * coroutine happened to reach the semaphore first went next, which is the
 * order the server's stream delivered them, not the order on the receipts.
 */
class PrintQueueTest {

    private val supervisor = SupervisorJob()
    private val scope = CoroutineScope(Dispatchers.Default + supervisor)

    @AfterEach fun tearDown() = supervisor.cancel()

    private fun order(n: Int) = "HH-%06d".format(n)

    @Test
    fun `orders print lowest number first, whatever order they arrive in`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val printed = Collections.synchronizedList(mutableListOf<String>())
        val gate = CompletableDeferred<Unit>()

        // Hold the single worker so everything below queues up behind it,
        // which is the situation this is all about: a backlog.
        queue.enqueue("blocker", order(1)) { gate.await() }

        listOf(47, 12, 31, 8, 99).forEach { n ->
            queue.enqueue("job-$n", order(n)) { printed.add(order(n)) }
        }

        assertEquals(
            listOf(8, 12, 31, 47, 99).map(::order),
            queue.waiting(),
            "the queue must be ordered before anything is printed, not after",
        )

        gate.complete(Unit)
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }

        assertEquals(listOf(8, 12, 31, 47, 99).map(::order), printed)
    }

    /**
     * The point of the separation: an order discovered while a big job is
     * printing must still get into the queue, and must take its rightful
     * place rather than the back.
     */
    @Test
    fun `an order that arrives mid-print still takes its place by number`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val printed = Collections.synchronizedList(mutableListOf<String>())
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("big", order(50)) { gate.await() }
        queue.enqueue("job-80", order(80)) { printed.add(order(80)) }
        // Arrives last, belongs first.
        queue.enqueue("job-60", order(60)) { printed.add(order(60)) }

        gate.complete(Unit)
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }

        assertEquals(listOf(order(60), order(80)), printed)
    }

    @Test
    fun `one order at a time`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val running = AtomicInteger()
        val peak = AtomicInteger()

        repeat(6) { n ->
            queue.enqueue("job-$n", order(n)) {
                val now = running.incrementAndGet()
                peak.updateAndGet { max -> maxOf(max, now) }
                delay(20)
                running.decrementAndGet()
            }
        }
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }

        assertEquals(1, peak.get(), "a single printer must never be handed two jobs at once")
    }

    /**
     * The duplicate guard. The reconciliation poll re-lists the same job every
     * ten seconds while it sits in the queue, and printing a customer's
     * document twice costs them paper and money.
     */
    @Test
    fun `the same job is never queued twice`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val runs = AtomicInteger()
        val gate = CompletableDeferred<Unit>()

        assertTrue(queue.enqueue("job-1", order(5)) { gate.await(); runs.incrementAndGet() })
        assertFalse(queue.enqueue("job-1", order(5)) { runs.incrementAndGet() }, "queued twice while waiting")

        gate.complete(Unit)
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }

        assertEquals(1, runs.get())
    }

    @Test
    fun `enqueue does not block the caller`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val gate = CompletableDeferred<Unit>()
        queue.enqueue("slow", order(1)) { gate.await() }

        val elapsed = kotlin.system.measureTimeMillis {
            repeat(50) { n -> queue.enqueue("job-$n", order(n + 2)) {} }
        }
        gate.complete(Unit)

        assertTrue(elapsed < 1_000, "submission is on a stream reader thread; it must return at once (took ${elapsed}ms)")
    }

    /** One bad job must not stop the queue draining - the shop has other orders. */
    @Test
    fun `a job that throws does not stall the queue`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val printed = Collections.synchronizedList(mutableListOf<String>())

        queue.enqueue("bad", order(1)) { error("driver exploded") }
        queue.enqueue("good", order(2)) { printed.add(order(2)) }

        withTimeout(5_000) { while (queue.depth > 0) delay(10) }
        assertEquals(listOf(order(2)), printed)
    }

    /**
     * The shop's screen is told when the queue changes shape, not on a timer.
     *
     * AgentCore hangs the status push off this callback, so a queue that
     * changed quietly would leave the counter looking at a stale list - and
     * with it the colours that say what is printing and who is waiting.
     */
    @Test
    fun `the queue says when its shape changes`(): Unit = runBlocking {
        val notifications = AtomicInteger()
        val queue = PrintQueue(scope, workers = 1, onDepthChanged = { notifications.incrementAndGet() })
        val running = CompletableDeferred<Unit>()
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("job-1", order(1)) { running.complete(Unit); gate.await() }
        withTimeout(5_000) { running.await() }

        // Counted across the whole job rather than between the steps: the
        // worker can start - and notify - before the enqueueing thread has
        // read the count back, so the ordering is not something to assert on.
        gate.complete(Unit)
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }

        assertTrue(
            notifications.get() >= 3,
            "queued, started and finished are three changes worth telling the screen about (saw ${notifications.get()})",
        )
    }

    /** A duplicate is not a change, and must not make the screen redraw for nothing. */
    @Test
    fun `a job already queued raises no change`(): Unit = runBlocking {
        val notifications = AtomicInteger()
        val queue = PrintQueue(scope, workers = 1, onDepthChanged = { notifications.incrementAndGet() })
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("job-1", order(1)) { gate.await() }
        withTimeout(5_000) { while (notifications.get() == 0) delay(5) }
        val settled = notifications.get()

        assertFalse(queue.enqueue("job-1", order(1)) {}, "the duplicate guard should refuse it")
        assertEquals(settled, notifications.get(), "a refused duplicate changed nothing")

        gate.complete(Unit)
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }
    }

    @Test
    fun `an unreadable order code waits behind every readable one`() {
        assertEquals(32L, orderSequence("HH-000032"))
        assertEquals(63L, orderSequence("PPP01-000063"))
        assertEquals(7L, orderSequence("7"))
        assertEquals(Long.MAX_VALUE, orderSequence(null), "unknown must not jump the counter")
        assertEquals(Long.MAX_VALUE, orderSequence(""))
        assertEquals(Long.MAX_VALUE, orderSequence("NO-DIGITS"))
    }
}
