package com.printly.agent.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import java.time.Instant

/**
 * When a scheduled order actually prints, and what happens when the agent
 * cannot find out.
 *
 * Two things went wrong here before, and both are pinned below.
 *
 * The lookup used to catch every exception and answer null, and null already
 * meant "not scheduled, print it now" - so a momentary 500 or a timed-out
 * token read as "this order is not scheduled" and a six o'clock order printed
 * at eleven in the morning.
 *
 * And it read the wrong field: scheduledSlotStart, a booked collection slot
 * that nothing in this system sets, rather than the Schedule Print time the
 * student actually chose. Every scheduled order looked unscheduled.
 */
class SchedulePlanTest {

    private val now: Instant = Instant.parse("2026-09-12T11:00:00Z")

    /**
     * The whole of the arithmetic, and the agent does none of it. The backend
     * subtracts its own release lead from the student's chosen time and sends
     * the answer; taking it whole is what stops a fourth copy of "five
     * minutes" drifting away from the other three.
     */
    @Test
    fun `the agent prints when the backend says the shop receives it`() {
        val plan = schedulePlanFor(ScheduleLookup.Known(releaseAt = "2026-09-12T17:55:00Z"), now)
        assertEquals(SchedulePlan.PrintAt("2026-09-12T17:55:00Z"), plan)
    }

    @Test
    fun `a Print Now order prints as soon as it is claimed`() {
        assertEquals(SchedulePlan.PrintAt(null), schedulePlanFor(ScheduleLookup.Known(releaseAt = null), now))
    }

    /** The shop was due this already. Holding it back would be the opposite of the point. */
    @Test
    fun `a release time already upon us prints now`() {
        assertEquals(
            SchedulePlan.PrintAt(null),
            schedulePlanFor(ScheduleLookup.Known("2026-09-12T11:00:00Z"), now),
            "due exactly now is due",
        )
        assertEquals(
            SchedulePlan.PrintAt(null),
            schedulePlanFor(ScheduleLookup.Known("2026-09-12T09:00:00Z"), now),
            "a release time in the past is a late order, not a future one",
        )
    }

    @Test
    fun `an unreadable time is treated as unscheduled rather than guessed at`() {
        assertEquals(SchedulePlan.PrintAt(null), schedulePlanFor(ScheduleLookup.Known("tomorrow-ish"), now))
        assertEquals(SchedulePlan.PrintAt(null), schedulePlanFor(ScheduleLookup.Known(""), now))
    }

    /**
     * A failure that might clear must not be allowed to look like "not
     * scheduled" - the job is held, unrecorded, and the ten-second
     * reconciliation poll brings it back to be asked again.
     */
    @Test
    fun `a lookup that might succeed later holds the job instead of printing it`() {
        assertEquals(
            SchedulePlan.Hold,
            schedulePlanFor(ScheduleLookup.Unavailable, now),
            "a 500 or a timeout is not evidence that an order is unscheduled",
        )
    }

    /**
     * The other side of it. Never printing at all is a real harm too, so a
     * refusal that will not clear - the order is gone, or the owner is signed
     * out - prints rather than holding for ever.
     */
    @Test
    fun `a refusal that will not clear prints rather than holding for ever`() {
        assertEquals(SchedulePlan.PrintAt(null), schedulePlanFor(ScheduleLookup.Refused, now))
    }

    /** Priority rides along on the same lookup, and does not disturb the timing. */
    @Test
    fun `priority is independent of when it prints`() {
        assertEquals(
            SchedulePlan.PrintAt("2026-09-12T17:55:00Z"),
            schedulePlanFor(ScheduleLookup.Known("2026-09-12T17:55:00Z", priority = true), now),
        )
    }
}
