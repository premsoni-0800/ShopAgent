package com.printly.agent.jobs

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
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
            listOf(1, 8, 12, 31, 47, 99).map(::order),
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
     * The bug this was reported for: a backlog does not arrive all at once.
     * Intake fetches each order in turn, so by the time the second one is
     * recorded the first is already on the printer - and HH-000108 came out
     * ahead of a backlog that started at HH-000025.
     */
    @Test
    fun `a burst is allowed to land before the first sheet comes out`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1, settleMillis = 400, maxSettleMillis = 5_000)
        val printed = Collections.synchronizedList(mutableListOf<String>())

        queue.enqueue("job-108", order(108)) { printed.add(order(108)) }
        delay(100)
        queue.enqueue("job-25", order(25)) { printed.add(order(25)) }
        delay(100)
        queue.enqueue("job-26", order(26)) { printed.add(order(26)) }

        withTimeout(5_000) { while (queue.depth > 0) delay(10) }

        assertEquals(
            listOf(order(25), order(26), order(108)),
            printed,
            "the order registered first must not print ahead of lower numbers still arriving",
        )
    }

    /**
     * Waiting for a lull is a courtesy, not a licence to sit idle. A shop
     * taking orders steadily never goes quiet, and the printer must still run.
     */
    @Test
    fun `a steady trickle does not hold the printer past the cap`(): Unit = runBlocking {
        // Intake never goes quiet for this long, so only the cap can release it.
        val queue = PrintQueue(scope, workers = 1, settleMillis = 60_000, maxSettleMillis = 300)
        val first = CompletableDeferred<Unit>()

        queue.enqueue("job-1", order(1)) { first.complete(Unit) }
        val intake = scope.launch {
            var n = 2
            while (true) {
                delay(50)
                queue.enqueue("job-$n", order(n)) {}
                n++
            }
        }

        try {
            withTimeout(3_000) { first.await() }
        } finally {
            intake.cancel()
        }
    }

    /**
     * Only the *first* job of a burst waits. Settling again before every sheet
     * would mean a shop with orders coming in steadily stalls for the full cap
     * between each one - the queue is already sorted and already moving.
     */
    @Test
    fun `a draining queue is never held up again`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1, settleMillis = 500, maxSettleMillis = 10_000)
        val printed = AtomicInteger()
        val started = CompletableDeferred<Unit>()
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("blocker", order(1)) { started.complete(Unit); gate.await() }
        repeat(5) { n -> queue.enqueue("job-$n", order(n + 2)) { printed.incrementAndGet() } }

        // The burst's own wait belongs to the first job; the rest is the drain.
        withTimeout(5_000) { started.await() }

        // Intake keeps ticking over, so the queue never sees a lull again.
        val intake = scope.launch {
            var n = 100
            while (true) {
                delay(100)
                queue.enqueue("late-$n", order(n)) {}
                n++
            }
        }

        // Let intake tick at least once, so the queue is demonstrably mid-burst
        // and not merely quiet, before the drain begins.
        delay(150)
        gate.complete(Unit)
        val elapsed = kotlin.system.measureTimeMillis {
            withTimeout(4_000) { while (printed.get() < 5) delay(10) }
        }
        intake.cancel()

        assertTrue(elapsed < 2_000, "a sorted, draining queue must keep printing (took ${elapsed}ms)")
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
