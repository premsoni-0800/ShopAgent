package com.printly.agent.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import java.util.concurrent.CyclicBarrier
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

/**
 * An expired owner token does not expire for one caller and not the others.
 * Intake is looking several orders up at once, the shop's screen calls in on
 * its own thread, and the order-events stream is rejected mid-flight - so they
 * all discover it in the same instant and all used to refresh.
 *
 * A backend that rotates refresh tokens honours the first of those and rejects
 * the rest, which can take the session down with it. The agent then goes on
 * heartbeating on its own separate credential - reporting itself connected -
 * while every order lookup fails, every failed lookup is read as "ask again
 * later", and nothing is ever picked up.
 */
class SharedRefreshTest {

    @Test
    fun `callers that hit the same expired token together refresh once`() {
        val token = AtomicReference("old")
        val refreshes = AtomicInteger()
        val shared = SharedRefresh({ token.get() }) {
            refreshes.incrementAndGet()
            Thread.sleep(20) // a refresh is a network call; the others pile up behind it
            token.set("new")
        }

        val callers = 8
        val together = CyclicBarrier(callers)
        val threads = (1..callers).map {
            Thread {
                together.await()
                shared.past("old")
            }.apply { start() }
        }
        threads.forEach { it.join() }

        assertEquals(1, refreshes.get(), "one refresh for the whole stampede")
        assertEquals("new", token.get())
    }

    /** A caller arriving later finds the work done and spends nothing. */
    @Test
    fun `a token already refreshed past is not refreshed again`() {
        val token = AtomicReference("new")
        val refreshes = AtomicInteger()
        val shared = SharedRefresh({ token.get() }) { refreshes.incrementAndGet() }

        shared.past("old")

        assertEquals(0, refreshes.get())
    }

    /** But a genuinely new expiry still refreshes - this must not latch. */
    @Test
    fun `a later expiry is refreshed on its own account`() {
        val token = AtomicReference("first")
        val refreshes = AtomicInteger()
        val shared = SharedRefresh({ token.get() }) {
            token.set("token-${refreshes.incrementAndGet()}")
        }

        shared.past("first")
        assertEquals(1, refreshes.get())

        shared.past(token.get())
        assertEquals(2, refreshes.get(), "the second token expired too, and that is a separate refresh")
    }
}
