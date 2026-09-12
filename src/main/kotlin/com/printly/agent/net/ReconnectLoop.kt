package com.printly.agent.net

import kotlinx.coroutines.delay
import java.util.logging.Level
import java.util.logging.Logger
import kotlin.math.min
import kotlin.math.pow
import kotlin.random.Random

/**
 * Shared reconnect-with-backoff shape used by both SSE clients (print jobs
 * and order-change events) - exponential backoff with jitter so a downed
 * backend is never hammered by a tight retry loop, and a clean disconnect
 * resets the attempt counter. Port of the identical logic duplicated across
 * the Python agent's `sse_client.py` and `order_events_client.py`.
 */
suspend fun reconnectLoop(
    log: Logger,
    baseDelaySeconds: Double,
    maxDelaySeconds: Double,
    isStopped: () -> Boolean,
    connectOnce: suspend () -> Unit,
) {
    var attempt = 0
    while (!isStopped()) {
        try {
            connectOnce()
            attempt = 0 // a clean disconnect (server-initiated) resets backoff
        } catch (exc: Exception) {
            log.log(Level.WARNING, "connection_failed attempt=$attempt", exc)
        }

        if (isStopped()) return

        attempt += 1
        val delaySeconds = min(maxDelaySeconds, baseDelaySeconds * 2.0.pow(attempt - 1))
        val jittered = delaySeconds * (0.5 + Random.nextDouble() / 2) // 50-100% of the computed delay
        delay((jittered * 1000).toLong())
    }
}
