package com.printly.agent.jobs

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.PriorityBlockingQueue
import java.util.logging.Level
import java.util.logging.Logger

/**
 * The shop's print queue: one order at a time, lowest order number first.
 *
 * What this replaces mattered on a busy counter. Printing was dispatched by
 * [JobDispatcher], which bounds how many run at once but says nothing about
 * *which* runs next - waiters take the slot in the order they happened to
 * arrive. Arrival order is the order the server's stream delivered them, or
 * the order a reconciliation query returned, neither of which is the order the
 * customers are standing in. With a backlog of large orders the shop printed
 * them in an order nobody could predict or explain, and the person holding
 * order 31 watched 47 come out.
 *
 * Ordering is by the number in the order code - `HH-000032` sorts as 32 - so
 * the queue matches what is written on the receipt. Ahead of all of it sits
 * in-shop priority: a student who has walked to the counter and scanned the
 * shop's QR is standing there waiting, and the backend has already agreed to
 * serve them next. Without this the agent quietly overruled that - the backend
 * hands out priority jobs first, and sorting the whole queue by order number
 * put them straight back behind everything else.
 *
 * Priority jobs are ordered among themselves by when they arrived here, not by
 * their number. Two people at the counter are a queue of two people, and the
 * one who scanned first is the one standing at the front of it - their order
 * number says only when they placed the order, which may have been yesterday.
 * Arrival is the closest thing the agent has to scan order: the backend serves
 * priority jobs in the order it granted them, so that is the order they reach
 * this queue in. Anything with no readable
 * number sorts last rather than first: unknown should wait behind known, never
 * jump the counter. Ties break on job id so the order is total and stable.
 *
 * Discovery is deliberately *not* done here. Fetching an order's details is an
 * HTTP call, and doing that inside the print slot meant the next order could
 * not even be recorded until the current one had finished printing - which is
 * both slower than it needs to be and the reason the queue could not be
 * ordered in the first place: nothing was ever waiting in it to sort. Intake
 * runs on its own small dispatcher and hands finished, recorded jobs here.
 *
 * [workers] is normally 1. Above 1 the ordering still decides what starts
 * next, but jobs then overlap and finish out of order - see
 * `Settings.maxConcurrentPrintJobs` for why one printer wants one.
 */
class PrintQueue(
    scope: CoroutineScope,
    workers: Int,
    private val onDepthChanged: () -> Unit = {},
) {
    private val log = Logger.getLogger(javaClass.name)

    private val pending = PriorityBlockingQueue<Entry>()
    private val running = ConcurrentHashMap.newKeySet<String>()

    /**
     * Every id that is queued or running. The duplicate guard, and the reason
     * the ten-second reconciliation poll is safe: it re-lists the same job on
     * every pass while that job waits its turn, and every pass after the first
     * is dropped here rather than printing a customer's document twice.
     */
    private val known = ConcurrentHashMap.newKeySet<String>()

    /** One permit per queued job. Workers wait on this rather than spinning. */
    private val signal = Channel<Unit>(Channel.UNLIMITED)

    /** Ticks once per enqueue, so priority jobs can be ordered by when they got here. */
    private val arrivals = java.util.concurrent.atomic.AtomicLong()

    /** Order code against job id, for everything printing right now - for the UI. */
    private val runningOrders = ConcurrentHashMap<String, String>()

    init {
        repeat(workers.coerceAtLeast(1)) {
            scope.launch {
                while (true) {
                    signal.receive()
                    val entry = pending.poll() ?: continue
                    running.add(entry.jobId)
                    runningOrders[entry.jobId] = entry.orderCode ?: entry.jobId
                    onDepthChanged()
                    try {
                        entry.work()
                    } catch (exc: Exception) {
                        // processJob reports its own failures; reaching here
                        // means something outside it broke. One bad job must
                        // not stop the queue draining.
                        log.log(Level.SEVERE, "print_queue_job_failed job=${entry.jobId}", exc)
                    } finally {
                        running.remove(entry.jobId)
                        runningOrders.remove(entry.jobId)
                        known.remove(entry.jobId)
                        onDepthChanged()
                    }
                }
            }
        }
    }

    /**
     * Adds [jobId] to the queue unless it is already queued or printing, and
     * returns immediately either way. Never throws: callers are event
     * listeners and polling loops with nowhere to put an exception.
     */
    fun enqueue(jobId: String, orderCode: String?, priority: Boolean = false, work: suspend () -> Unit): Boolean {
        if (!known.add(jobId)) {
            log.fine("job_already_queued job=$jobId")
            return false
        }
        pending.add(Entry(orderSequence(orderCode), arrivals.incrementAndGet(), jobId, orderCode, priority, work))
        signal.trySend(Unit)
        log.info("print_job_queued job=$jobId order=${orderCode ?: "?"} priority=$priority depth=${known.size}")
        onDepthChanged()
        return true
    }

    /** Queued or printing - what the UI shows as outstanding work. */
    val depth: Int get() = known.size

    /** Printing right now. */
    val activeCount: Int get() = running.size

    /** Waiting order codes, in the order they will print. For the UI and for tests. */
    fun waiting(): List<String> = pending.sorted().map { it.orderCode ?: it.jobId }

    /** The waiting order codes that jumped the queue by scanning at the counter. */
    fun waitingPriority(): List<String> =
        pending.sorted().filter { it.priority }.map { it.orderCode ?: it.jobId }

    /** Order codes on a printer right now - what the shop's screen shows in green. */
    fun printing(): List<String> = runningOrders.values.sorted()

    private class Entry(
        val sequence: Long,
        val arrival: Long,
        val jobId: String,
        val orderCode: String?,
        val priority: Boolean,
        val work: suspend () -> Unit,
    ) : Comparable<Entry> {
        /**
         * Priority first. Then, within each group, the key that actually
         * describes the queue those jobs are in.
         *
         * Priority is deliberately the outermost key rather than a bonus
         * applied to the number: the point of it is that somebody is standing
         * at the counter, and that outranks every order not yet collected,
         * however low its number.
         *
         * Among priority jobs the key is arrival, not the number. Two people at
         * the counter are a queue of two people, and the one who scanned first
         * is at the front of it; their order number says only when they placed
         * the order, which may have been yesterday. Among everything else the
         * key is still the number, because that is the queue the receipts
         * describe.
         *
         * Job id breaks a tie either way, so the order is total and stable -
         * which a PriorityBlockingQueue requires.
         */
        override fun compareTo(other: Entry): Int {
            if (priority != other.priority) return if (priority) -1 else 1
            return if (priority) {
                compareValuesBy(this, other, { it.arrival }, { it.jobId })
            } else {
                compareValuesBy(this, other, { it.sequence }, { it.jobId })
            }
        }
    }
}

/**
 * The number in an order code, used to sort the queue: `HH-000032` is 32,
 * `PPP01-000063` is 63.
 *
 * Codes carry a per-shop prefix and an agent serves one shop, so comparing the
 * numeric tail alone is safe here. Anything unreadable sorts last - a job with
 * no number should wait behind every job that has one, not jump ahead of them.
 */
internal fun orderSequence(orderCode: String?): Long {
    val digits = orderCode?.takeLastWhile { it.isDigit() }.orEmpty()
    return digits.toLongOrNull() ?: Long.MAX_VALUE
}
