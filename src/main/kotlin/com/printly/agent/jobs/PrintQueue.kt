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
 * the queue matches what is written on the receipt. Ahead of all of it sits
 * in-shop priority: a student who has walked to the counter and scanned the
 * shop's QR is standing there waiting, and the backend has already agreed to
 * serve them next. Without this the agent quietly overruled that - the backend
 * hands out priority jobs first, and sorting the whole queue by order number
 * put them straight back behind everything else.
 *
 * Priority is not only granted on the way in. Most people who scan at a
 * counter ordered ahead, so the job is usually already waiting here as an
 * ordinary one by the time the backend grants it - [promote] is how that
 * grant reaches a job already in the queue, and a scan never waits for intake
 * (see [priorityWaiting]).
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

    /** Ticks once per enqueue, so priority jobs can be ordered by when they got here. */
    private val arrivals = java.util.concurrent.atomic.AtomicLong()

    /**
     * Held across taking a job off the queue, and across [promote]'s
     * take-out-and-put-back.
     *
     * Workers are woken by counted permits on [signal], one per queued job, so
     * the count of permits and the count of entries have to stay equal.
     * Promotion cannot edit an entry in place - [Entry.priority] is a sort
     * key, and changing one inside a heap corrupts it - so it removes and
     * re-inserts, and a worker that polled the gap between the two would find
     * nothing, spend its permit, and leave the re-inserted job with no permit
     * left to wake anybody for it. That job would then sit in the queue until
     * some unrelated order happened to arrive. The lock is held for a heap
     * operation and nothing else: no I/O, no callback, nothing that suspends.
     */
    private val pollLock = Any()

    /** Order code against job id, for everything printing right now - for the UI. */
    private val runningOrders = ConcurrentHashMap<String, String>()

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
                    } else if (priorityWaiting()) {
                        // Somebody is at the counter and their job is already
                        // in hand - see [priorityWaiting] for why waiting for
                        // intake here buys nothing and costs them the wait.
                        log.fine("print_queue_intake_wait_skipped_for_priority depth=${known.size}")
                    } else if (!gaveUpOnIntake) {
                        gaveUpOnIntake = !awaitIntakeDrained()
                    }
                    val entry = synchronized(pollLock) { pending.poll() } ?: continue
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

    /**
     * Moves a job already waiting to the front, because its student has since
     * walked in and scanned at the counter. Returns true if it moved.
     *
     * This is the ordinary way in-shop priority is granted, and the queue
     * could not act on it. Most people who scan at a counter ordered earlier
     * - that is the point of ordering ahead - so by the time the backend
     * grants priority the job is usually already sitting here as an ordinary
     * one, and [enqueue] refuses it as a duplicate. The scan was read
     * correctly, and then went nowhere: the student stood at the counter
     * while the queue worked through every lower number ahead of them.
     *
     * A fresh arrival stamp, not the original one, because the queue among
     * priority jobs is the queue of people standing at the counter, and this
     * person has only just joined it - keeping the old stamp would put them
     * ahead of somebody who scanned while they were still walking over.
     *
     * Re-inserting rather than editing in place is forced: [Entry.priority] is
     * a sort key, and changing one inside a heap corrupts it. Both halves
     * happen under [pollLock] so no worker can see the queue one entry short
     * and spend a permit on nothing.
     *
     * Does nothing for a job that is already printing (there is nothing ahead
     * of it left to move it past), already priority, or not here at all.
     */
    fun promote(jobId: String): Boolean {
        val orderCode = synchronized(pollLock) {
            val entry = pending.firstOrNull { it.jobId == jobId && !it.priority } ?: return false
            // Lost the race to a worker that has just taken it - which is the
            // outcome promotion was after anyway.
            if (!pending.remove(entry)) return false
            pending.add(Entry(entry.sequence, arrivals.incrementAndGet(), entry.jobId, entry.orderCode, true, entry.work))
            entry.orderCode
        }
        // Outside the lock: this reaches the shop's screen, and the screen is
        // not something to hold a queue lock across.
        log.info("print_job_promoted job=$jobId order=${orderCode ?: "?"} depth=${known.size}")
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

    /**
     * Whether somebody who scanned at the counter is waiting for the printer.
     *
     * Cheap and exact: priority is the outermost key in [Entry.compareTo], so
     * the head of the queue is a priority entry if and only if one is waiting
     * at all.
     *
     * This is what lets a scan take the next sheet straight away. The intake
     * wait below exists so that sorting *by order number* sees every candidate
     * - a number still being fetched might belong ahead of the one in hand. A
     * priority entry is not sorted by number: it outranks every ordinary job
     * whatever intake is still holding, and priority entries are ordered
     * among themselves by when they reached this queue, which is already
     * settled for one that is here. So the wait cannot change what prints
     * next, and paying it only leaves the student standing at the counter for
     * up to [maxIntakeWaitMillis] - twenty seconds, in front of the person who
     * just served them - while the agent thinks about orders nobody is waiting
     * on. The one thing it can cost is a second scan that is mid-intake going
     * second instead of first; two people at the counter sorting by a hair is
     * worth far less than neither of them being served.
     */
    private fun priorityWaiting(): Boolean = pending.peek()?.priority == true

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
     * The question being asked is "does intake still hold something that could
     * belong in front of what is waiting?", and [intakeBusy] answers it
     * directly - [JobDispatcher.holdsPrintingCount], the references intake is
     * looking up that this queue has never seen. Not every reference intake
     * handles: the reconciliation poll re-examines everything already waiting
     * here, over and over, and none of that can sort ahead of a queue it is
     * already in. Waiting for it left the printer idle between sheets for the
     * length of a backlog. An earlier cut of this
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
            // A scan landing mid-wait ends the wait, rather than making the
            // person who just scanned stand there for the rest of it. Returns
            // true because nothing is wrong with intake - the wait is simply
            // no longer the right thing to be doing - so the next ordinary
            // sheet waits for it again as usual.
            if (priorityWaiting()) {
                val waited = (System.nanoTime() - startedAt) / 1_000_000L
                log.info("print_queue_intake_wait_cut_for_priority waited=${waited}ms depth=${known.size}")
                return true
            }
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
