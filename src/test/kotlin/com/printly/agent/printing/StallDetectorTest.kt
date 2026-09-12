package com.printly.agent.printing

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

/**
 * When a print submission is considered to have stopped responding.
 *
 * Both halves of this matter and they pull against each other. Give up too
 * eagerly and a legitimately enormous document gets abandoned halfway; give up
 * never - which is what the code did before - and a wedged driver holds one of
 * the agent's few print slots for good, with the job stuck at DOWNLOADED where
 * nobody can see it.
 *
 * Drawn from a real incident: a 3,100-page job printed 2,860 pages and froze,
 * with the spooler still reporting "Printing" and `PrinterJob.print()` never
 * returning.
 */
class StallDetectorTest {

    private val limit = 300L
    private fun seconds(n: Long) = n * 1_000_000_000L
    private fun at(pages: Int, found: Boolean = true) = SpoolerOutcomePoller.JobProgress(found, pages)

    @Test
    fun `a job that keeps printing is never called stalled`() {
        val detector = StallDetector(limit)
        var pages = 0
        // An hour of steady progress - far past the limit, but always moving.
        for (tick in 0..240) {
            pages += 10
            assertNull(detector.sample(at(pages), seconds(tick * 15L)))
        }
    }

    @Test
    fun `a job whose page count stops moving is stalled once the limit passes`() {
        val detector = StallDetector(limit)
        assertNull(detector.sample(at(2860), seconds(0)))
        assertNull(detector.sample(at(2860), seconds(120)), "two minutes of nothing is not yet a stall")
        assertNull(detector.sample(at(2860), seconds(299)))

        val stalledFor = detector.sample(at(2860), seconds(300))
        assertEquals(300L, stalledFor)
    }

    /** The exact shape of the incident: real progress, then a freeze. */
    @Test
    fun `progress followed by a freeze is caught, and the clock starts at the freeze`() {
        val detector = StallDetector(limit)
        detector.sample(at(1000), seconds(0))
        detector.sample(at(2000), seconds(100))
        detector.sample(at(2860), seconds(200)) // last real movement

        assertNull(detector.sample(at(2860), seconds(400)), "200s since the freeze, not 400 since the start")
        assertEquals(300L, detector.sample(at(2860), seconds(500)))
    }

    /**
     * An unreadable spooler is not evidence of anything. Treating it as no
     * progress would abandon healthy jobs whenever the print service hiccups.
     */
    @Test
    fun `a spooler that cannot be read never counts towards a stall`() {
        val detector = StallDetector(limit)
        detector.sample(at(500), seconds(0))
        for (tick in 1..40) assertNull(detector.sample(null, seconds(tick * 15L)))
    }

    /** A job leaving the queue is a change like any other - it must not read as a freeze. */
    @Test
    fun `a job disappearing from the queue restarts the clock rather than tripping it`() {
        val detector = StallDetector(limit)
        detector.sample(at(2860), seconds(0))
        assertNull(detector.sample(at(0, found = false), seconds(400)))
        assertNull(detector.sample(at(0, found = false), seconds(500)))
    }

    /** The first observation can never be a stall: there is nothing yet to have stalled from. */
    @Test
    fun `the first sample is never a stall however late it arrives`() {
        assertNull(StallDetector(limit).sample(at(0), seconds(100_000)))
    }
}
