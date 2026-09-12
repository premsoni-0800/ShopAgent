package com.printly.agent.jobs

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
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
 * the queue matches what is written on the receipt. Anything with no readable
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
 * The two being separate is also why the queue has to ask whether intake is
 * still holding anything before it starts a sheet - see [awaitIntakeDrained].
 * A backlog is only in order once all of it has arrived.
 *
 * [workers] is normally 1. Above 1 the ordering still decides what starts
 * next, but jobs then overlap and finish out of order - see
 * `Settings.maxConcurrentPrintJobs` for why one printer wants one.
 */
class PrintQueue(
    scope: CoroutineScope,
    workers: Int,
    private val intakeBusy: () -> Boolean = { false },
    private val maxIntakeWaitMillis: Long = MAX_INTAKE_WAIT_MILLIS,
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

    init {
        repeat(workers.coerceAtLeast(1)) {
            scope.launch {
                // Set once [maxIntakeWaitMillis] has cut a wait short, and
                // cleared when intake is next seen idle. Without it, intake
                // that stays busy would cost a full wait before every single
                // sheet rather than once.
                var gaveUpOnIntake = false
                while (true) {
                    signal.receive()
                    if (!intakeBusy()) {
                        gaveUpOnIntake = false
                    } else if (!gaveUpOnIntake) {
                        gaveUpOnIntake = !awaitIntakeDrained()
                    }
                    val entry = pending.poll() ?: continue
                    running.add(entry.jobId)
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
    fun enqueue(jobId: String, orderCode: String?, work: suspend () -> Unit): Boolean {
        if (!known.add(jobId)) {
            log.fine("job_already_queued job=$jobId")
            return false
        }
        pending.add(Entry(orderSequence(orderCode), jobId, orderCode, work))
        signal.trySend(Unit)
        log.info("print_job_queued job=$jobId order=${orderCode ?: "?"} depth=${known.size}")
        onDepthChanged()
        return true
    }

    /** Queued or printing - what the UI shows as outstanding work. */
    val depth: Int get() = known.size

    /** Printing right now. */
    val activeCount: Int get() = running.size

    /** Waiting order codes, in the order they will print. For the UI and for tests. */
    fun waiting(): List<String> = pending.sorted().map { it.orderCode ?: it.jobId }

    /**
     * Holds the printer while intake still has orders it has not handed over.
     *
     * Sorting only ever orders what is already in the queue, and intake runs
     * while printing does. A backlog does not arrive all at once - intake
     * fetches each order in turn - so without this the first reference to be
     * registered goes on the printer before the rest have been looked up, and
     * comes out ahead of lower numbers still on their way. Observed exactly
     * that: HH-000108 printed first out of a backlog starting at HH-000025.
     *
     * The question being asked is "does intake still hold something?", and
     * [intakeBusy] answers it directly - `JobDispatcher.activeCount`, the
     * count of references fetched but not yet enqueued. An earlier cut of this
     * inferred the answer from arrival timing instead, waiting for a lull, and
     * timing turns out to answer a different question badly: a shop taking
     * orders steadily never falls quiet, so every sheet waited out the whole
     * cap, while a backlog landing mid-print was never waited for at all
     * because the queue was not empty when it arrived.
     *
     * Returns true if intake drained, false if [maxIntakeWaitMillis] cut the
     * wait short - because intake that somehow never finishes must not hold
     * the printer for ever. Past the cap the queue prints what it has, and
     * whatever arrives later takes its place in what remains.
     */
    private suspend fun awaitIntakeDrained(): Boolean {
        val startedAt = System.nanoTime()
        val deadline = startedAt + maxIntakeWaitMillis * 1_000_000L
        while (intakeBusy()) {
            // nanoTime, not the wall clock: this is a duration, and a counter
            // PC resyncing its clock mid-wait must not extend or cut it.
            if (System.nanoTime() - deadline >= 0) {
                log.warning("print_queue_intake_wait_capped waited=${maxIntakeWaitMillis}ms depth=${known.size}")
                return false
            }
            delay(INTAKE_POLL_MILLIS)
        }
        val waitedMillis = (System.nanoTime() - startedAt) / 1_000_000L
        if (waitedMillis > 0) {
            log.info("print_queue_waited_for_intake waited=${waitedMillis}ms depth=${known.size}")
        }
        return true
    }

    private companion object {
        /**
         * Never hold the printer longer than this for intake. Generous,
         * because it is a backstop against intake wedging rather than a
         * routine limit - a backlog of a hundred references is handed over in
         * well under a second of this.
         */
        const val MAX_INTAKE_WAIT_MILLIS = 20_000L

        /** How often to re-ask whether intake is still holding something. */
        const val INTAKE_POLL_MILLIS = 25L
    }

    private class Entry(
        val sequence: Long,
        val jobId: String,
        val orderCode: String?,
        val work: suspend () -> Unit,
    ) : Comparable<Entry> {
        override fun compareTo(other: Entry): Int =
            compareValuesBy(this, other, { it.sequence }, { it.jobId })
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
