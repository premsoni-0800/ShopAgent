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
import java.util.concurrent.atomic.AtomicBoolean
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

    /**
     * The bug this was reported for. A backlog does not arrive all at once -
     * intake fetches each order in turn - so the first reference registered
     * used to be on the printer before the rest had been looked up. On the
     * counter, HH-000108 came out of a backlog that started at HH-000025.
     */
    @Test
    fun `a backlog still arriving is not printed until intake has handed it over`(): Unit = runBlocking {
        val intakeBusy = AtomicBoolean(true)
        val queue = PrintQueue(scope, workers = 1, intakeBusy = { intakeBusy.get() })
        val printed = Collections.synchronizedList(mutableListOf<String>())

        // Intake is working through the backlog; the high number happens to
        // be looked up first.
        queue.enqueue("job-108", order(108)) { printed.add(order(108)) }
        delay(100)
        queue.enqueue("job-25", order(25)) { printed.add(order(25)) }
        queue.enqueue("job-26", order(26)) { printed.add(order(26)) }
        intakeBusy.set(false)

        withTimeout(5_000) { while (queue.depth > 0) delay(10) }

        assertEquals(
            listOf(order(25), order(26), order(108)),
            printed,
            "nothing should print while intake is still holding orders that might sort ahead of it",
        )
    }

    /**
     * The same backlog, arriving while the printer is busy - which is when a
     * reconciliation sweep usually finds one. The queue being non-empty says
     * nothing about whether intake has finished; only intake does.
     */
    @Test
    fun `a backlog that lands while the printer is busy still prints in order`(): Unit = runBlocking {
        val intakeBusy = AtomicBoolean(false)
        val queue = PrintQueue(scope, workers = 1, intakeBusy = { intakeBusy.get() })
        val printed = Collections.synchronizedList(mutableListOf<String>())
        val running = CompletableDeferred<Unit>()
        val gate = CompletableDeferred<Unit>()

        queue.enqueue("blocker", order(1)) { running.complete(Unit); gate.await() }
        withTimeout(5_000) { running.await() }

        intakeBusy.set(true)
        queue.enqueue("job-108", order(108)) { printed.add(order(108)) }
        gate.complete(Unit)
        delay(150) // the rest of the backlog is still being fetched
        queue.enqueue("job-25", order(25)) { printed.add(order(25)) }
        intakeBusy.set(false)

        withTimeout(5_000) { while (queue.depth > 0) delay(10) }

        assertEquals(listOf(order(25), order(108)), printed)
    }

    /**
     * Orders arriving one at a time, each fetched and handed over before the
     * next turns up. Intake is idle in between, so there is nothing to wait
     * for and the printer must not pause at all.
     */
    @Test
    fun `a trickle of orders is never held up`(): Unit = runBlocking {
        val intakeBusy = AtomicBoolean(false)
        val queue = PrintQueue(scope, workers = 1, intakeBusy = { intakeBusy.get() }, maxIntakeWaitMillis = 10_000)
        val printed = AtomicInteger()

        val elapsed = kotlin.system.measureTimeMillis {
            repeat(5) { n ->
                intakeBusy.set(true)
                queue.enqueue("job-$n", order(n)) { printed.incrementAndGet() }
                intakeBusy.set(false)
                delay(100)
            }
            withTimeout(5_000) { while (printed.get() < 5) delay(10) }
        }

        assertTrue(elapsed < 2_000, "a trickle must not be made to wait out the cap each time (took ${elapsed}ms)")
    }

    /** Intake that somehow never finishes must not hold the printer for ever. */
    @Test
    fun `intake that never finishes does not hold the printer past the cap`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1, intakeBusy = { true }, maxIntakeWaitMillis = 300)
        val done = CompletableDeferred<Unit>()

        queue.enqueue("job-1", order(1)) { done.complete(Unit) }

        withTimeout(3_000) { done.await() }
    }

    /**
     * And once it has given up waiting, it keeps printing. Waiting again
     * before every sheet would turn a busy intake into a stalled printer,
     * which is worse than not waiting at all.
     */
    @Test
    fun `once the wait is capped the queue keeps printing`(): Unit = runBlocking {
        val queue = PrintQueue(scope, workers = 1, intakeBusy = { true }, maxIntakeWaitMillis = 400)
        val printed = AtomicInteger()

        repeat(5) { n -> queue.enqueue("job-$n", order(n)) { printed.incrementAndGet() } }

        val elapsed = kotlin.system.measureTimeMillis {
            withTimeout(5_000) { while (printed.get() < 5) delay(10) }
        }

        assertTrue(elapsed < 1_200, "the cap should have been paid once, not once per sheet (took ${elapsed}ms)")
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
