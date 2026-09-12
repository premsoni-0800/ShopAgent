package com.printly.agent.net

import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.atomic.AtomicInteger
import java.util.logging.Logger

/**
 * What a dropped stream leads to, and why the difference is not cosmetic.
 *
 * [reconnectLoop] decides how long to wait by watching how `connectOnce`
 * returns: normally means the server closed cleanly and the ladder resets,
 * throwing means something went wrong and the delay grows. The order-events
 * client used to return normally from *every* failure, which pinned the delay
 * at its base value for ever and silenced the loop's own logging with it.
 */
class StreamFailureTest {

    @Test
    fun `an ordinary failure backs off`() {
        assertEquals(StreamFailure.BACK_OFF, streamFailureAction(refreshed = false, stopped = false))
    }

    /**
     * A token was just refreshed, so there is a new one to reconnect with and
     * no reason to wait - that is the entire point of having refreshed.
     */
    @Test
    fun `a refreshed token reconnects at once`() {
        assertEquals(StreamFailure.RECONNECT_NOW, streamFailureAction(refreshed = true, stopped = false))
    }

    /** Nobody is signed in; the loop is about to end, and should end quietly. */
    @Test
    fun `a signed-out session gives up`() {
        assertEquals(StreamFailure.GIVE_UP, streamFailureAction(refreshed = false, stopped = true))
        assertEquals(
            StreamFailure.GIVE_UP,
            streamFailureAction(refreshed = true, stopped = true),
            "a refresh that ended in a revoked token is still a session that is gone",
        )
    }
}

/**
 * The backoff itself, which both SSE clients lean on and nothing covered.
 *
 * The bug above was only harmful because of what this does with a normal
 * return, so the contract is worth pinning: a throw grows the wait, a clean
 * return resets it.
 */
class ReconnectLoopTest {

    private val log = Logger.getLogger("ReconnectLoopTest")

    /**
     * Measured against each other rather than against a stopwatch: the delays
     * are jittered on purpose, and a first-run JVM spends longer starting
     * coroutines than nine base delays take. What is not in doubt is the gap -
     * a climbing ladder is orders of magnitude slower than a flat one.
     */
    @Test
    fun `failures back off, clean disconnects do not`() {
        val cleanMs = nineRounds { delay(1) }
        val failingMs = nineRounds { throw java.io.IOException("stream died") }

        assertTrue(
            failingMs > cleanMs * 3,
            "nine failures should cost far more than nine clean reconnects (failing=${failingMs}ms clean=${cleanMs}ms)",
        )
    }

    /** Nine rounds of [body], bounded so a climbing ladder cannot hang the suite. */
    private fun nineRounds(body: suspend () -> Unit): Long {
        val attempts = AtomicInteger()
        return kotlin.system.measureTimeMillis {
            runBlocking {
                runCatching {
                    withTimeout(3_000) {
                        reconnectLoop(
                            log,
                            baseDelaySeconds = 0.01,
                            maxDelaySeconds = 60.0,
                            isStopped = { attempts.get() >= 9 },
                        ) {
                            attempts.incrementAndGet()
                            body()
                        }
                    }
                }
            }
        }
    }

    @Test
    fun `stopping ends the loop`() {
        val attempts = AtomicInteger()
        runBlocking {
            withTimeout(3_000) {
                reconnectLoop(log, baseDelaySeconds = 0.01, maxDelaySeconds = 60.0, isStopped = { attempts.get() >= 1 }) {
                    attempts.incrementAndGet()
                }
            }
        }
        assertEquals(1, attempts.get())
    }
}
