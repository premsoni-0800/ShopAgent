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
     * The student is standing at the counter, having scanned the shop's QR,
     * and the backend has already agreed to serve them next. Sorting the whole
     * queue by order number put them straight back behind everything else -
     * the agent quietly overruling the thing the scan exists to do.
     */
    @Test
    fun `a priority order prints before every number waiting`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val printed = Collections.synchronizedList(mutableListOf<String>())
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("blocker", order(1)) { gate.await() }
        listOf(8, 12, 31).forEach { n ->
            queue.enqueue("job-$n", order(n)) { printed.add(order(n)) }
        }
        // Arrives last, with the highest number, and still goes first.
        queue.enqueue("job-99", order(99), priority = true) { printed.add(order(99)) }

        assertEquals(
            listOf(99, 8, 12, 31).map(::order),
            queue.waiting(),
            "priority outranks the number on the receipt, however low",
        )

        gate.complete(Unit)
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }

        assertEquals(listOf(99, 8, 12, 31).map(::order), printed)
    }

    /**
     * Two people at the counter are a queue of two people, and the one who
     * scanned first is at the front of it. Their order numbers say only when
     * they placed the orders, which may have been yesterday - 40 scanning
     * before 20 must still print first.
     */
    @Test
    fun `priority orders print in the order they were scanned`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val printed = Collections.synchronizedList(mutableListOf<String>())
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("blocker", order(1)) { gate.await() }
        // 40 scans first, then 20 - the higher number is at the front.
        queue.enqueue("job-40", order(40), priority = true) { printed.add(order(40)) }
        queue.enqueue("job-20", order(20), priority = true) { printed.add(order(20)) }
        queue.enqueue("job-5", order(5)) { printed.add(order(5)) }

        assertEquals(
            listOf(40, 20, 5).map(::order),
            queue.waiting(),
            "scan order decides between two people at the counter, not the receipt",
        )

        gate.complete(Unit)
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }
        assertEquals(listOf(40, 20, 5).map(::order), printed)
    }

    /** Everything not at the counter is still the queue the receipts describe. */
    @Test
    fun `orders that did not scan keep their number order`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("blocker", order(1)) { gate.await() }
        queue.enqueue("job-47", order(47)) {}
        queue.enqueue("job-12", order(12)) {}
        queue.enqueue("job-99", order(99), priority = true) {}

        assertEquals(listOf(99, 12, 47).map(::order), queue.waiting())
        gate.complete(Unit)
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }
    }

    /** What the shop's own screen colours: green for printing, blue for the counter. */
    @Test
    fun `the queue says what is printing and what jumped it`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val running = CompletableDeferred<Unit>()
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("blocker", order(1)) { running.complete(Unit); gate.await() }
        withTimeout(5_000) { running.await() }

        queue.enqueue("job-30", order(30), priority = true) {}
        queue.enqueue("job-8", order(8)) {}

        assertEquals(listOf(order(1)), queue.printing(), "the one on the printer")
        assertEquals(listOf(order(30)), queue.waitingPriority(), "the one at the counter")

        gate.complete(Unit)
        withTimeout(5_000) { while (queue.depth > 0) delay(10) }
        assertEquals(emptyList<String>(), queue.printing(), "nothing left on the printer")
    }

    /** Nothing asked for priority, so nothing gets it. */
    @Test
    fun `an ordinary queue is unchanged`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1)
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("blocker", order(1)) { gate.await() }
        listOf(47, 12, 31).forEach { n -> queue.enqueue("job-$n", order(n)) {} }

        assertEquals(listOf(12, 31, 47).map(::order), queue.waiting())
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
