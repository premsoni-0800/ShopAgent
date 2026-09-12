package com.printly.agent.jobs

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.util.concurrent.ConcurrentHashMap
import java.util.logging.Level
import java.util.logging.Logger

/**
 * Runs print jobs concurrently, and - just as importantly - gets them off the
 * thread that discovered them.
 *
 * Every job used to be processed inline by whoever found it. The SSE listener
 * did `runBlocking { handleJobReference(...) }` *on OkHttp's reader thread*,
 * so for as long as a job took to download, print and have its outcome
 * confirmed by the spooler - up to `jobTimeoutSeconds`, five minutes - that
 * connection read nothing. A second order placed during a print was therefore
 * not merely printed late, it was not *delivered* until the first job
 * finished, and the stream itself could time out waiting. The reconciliation
 * loop had the matching flaw: it awaited each job in turn, so its interval
 * only started counting after the whole backlog had drained one at a time.
 *
 * So dispatch is now fire-and-forget. Discovering work is instant, and the
 * work itself runs on the shared scope, [maxConcurrent] at a time.
 *
 * Two independent guards stop the same job running twice, which matters more
 * here than anywhere else in the agent - the failure mode is a customer's
 * document printed twice, on paper, at their expense:
 *
 *  - [inFlight] rejects a second submission of an id that is still running.
 *    This is what makes the 10-second reconciliation poll safe: it re-lists
 *    the same outstanding job on every pass while that job is mid-print, and
 *    every one of those passes after the first is dropped here.
 *  - [Database.insertJobReference] rejects an id ever seen before, in a single
 *    atomic statement. That is the durable guard, and it outlives restarts.
 *
 * The bound exists because concurrency stops helping well before it stops
 * costing: each job holds a downloaded PDF and a spooler poller, and a shop
 * has few enough physical printers that a dozen at once would just queue in
 * the driver anyway.
 */
class JobDispatcher(
    private val scope: CoroutineScope,
    maxConcurrent: Int,
) {
    private val log = Logger.getLogger(javaClass.name)

    private val slots = Semaphore(maxConcurrent)
    private val inFlight: MutableSet<String> = ConcurrentHashMap.newKeySet()

    /** Visible for the UI and tests - how many jobs are running or queued right now. */
    val activeCount: Int get() = inFlight.size

    /**
     * Queues [work] for [jobId] unless that id is already running, and returns
     * immediately either way. Never throws: a caller is an event listener or a
     * polling loop, and neither has anywhere sensible to put an exception.
     */
    fun submit(jobId: String, work: suspend () -> Unit): Boolean {
        if (!inFlight.add(jobId)) {
            log.fine("job_already_in_flight job=$jobId")
            return false
        }
        scope.launch {
            try {
                slots.withPermit { work() }
            } catch (exc: Exception) {
                // processJob already reports its own failures to the backend;
                // reaching here means something outside it broke. Swallowing is
                // deliberate - one bad job must not take the dispatcher down.
                log.log(Level.SEVERE, "job_dispatch_failed job=$jobId", exc)
            } finally {
                inFlight.remove(jobId)
            }
        }
        return true
    }
}
