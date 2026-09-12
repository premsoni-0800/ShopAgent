package com.printly.agent.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant

/**
 * When a scheduled order actually prints, and what happens when the agent
 * cannot find out.
 *
 * The lookup used to catch every exception and answer null, and null already
 * meant "no slot, print it now" - so a momentary 500 or a timed-out token read
 * as "this order is not scheduled" and a six o'clock slot discovered at eleven
 * in the morning printed at eleven in the morning. The distinction between
 * "there is no slot" and "I could not ask" is the whole of this.
 */
class SchedulePlanTest {

    private val leadTime: Duration = Duration.ofMinutes(10)
    private val now: Instant = Instant.parse("2026-09-12T11:00:00Z")

    @Test
    fun `a future slot prints one lead time before it`() {
        val plan = schedulePlanFor(ScheduleLookup.Known("2026-09-12T18:00:00Z"), leadTime, now)
        assertEquals(SchedulePlan.PrintAt("2026-09-12T17:50:00Z"), plan)
    }

    @Test
    fun `an unscheduled order prints as soon as it is claimed`() {
        assertEquals(SchedulePlan.PrintAt(null), schedulePlanFor(ScheduleLookup.Known(null), leadTime, now))
    }

    /** The student is already due. Holding it back would be the opposite of the point. */
    @Test
    fun `a time already upon us prints now`() {
        assertEquals(
            SchedulePlan.PrintAt(null),
            schedulePlanFor(ScheduleLookup.Known("2026-09-12T11:05:00Z"), leadTime, now),
            "the lead time has already elapsed, so this is a job to print, not to schedule",
        )
        assertEquals(
            SchedulePlan.PrintAt(null),
            schedulePlanFor(ScheduleLookup.Known("2026-09-12T09:00:00Z"), leadTime, now),
            "a slot in the past is a late order, not a future one",
        )
    }

    @Test
    fun `an unreadable time is treated as unscheduled rather than guessed at`() {
        assertEquals(SchedulePlan.PrintAt(null), schedulePlanFor(ScheduleLookup.Known("tomorrow-ish"), leadTime, now))
        assertEquals(SchedulePlan.PrintAt(null), schedulePlanFor(ScheduleLookup.Known(""), leadTime, now))
    }

    /**
     * The field the time is read from, pinned because reading the wrong one is
     * exactly what broke this. OrderResponse carries both scheduledPrintAt and
     * scheduledSlotStart; only Schedule Print is ever populated, and the agent
     * spent its life reading the empty one - so every scheduled order looked
     * unscheduled and printed the moment it was paid for.
     */
    @Test
    fun `the print time comes from Schedule Print`() {
        val plan = schedulePlanFor(ScheduleLookup.Known(printAt = "2026-09-12T18:00:00Z"), leadTime, now)
        assertEquals(SchedulePlan.PrintAt("2026-09-12T17:50:00Z"), plan)
    }

    /**
     * The bug this exists for. A failure that might clear must not be allowed
     * to look like "not scheduled" - the job is held, unrecorded, and the
     * ten-second reconciliation poll brings it back to be asked again.
     */
    @Test
    fun `a lookup that might succeed later holds the job instead of printing it`() {
        assertEquals(
            SchedulePlan.Hold,
            schedulePlanFor(ScheduleLookup.Unavailable, leadTime, now),
            "a 500 or a timeout is not evidence that an order has no slot",
        )
    }

    /**
     * The other side of it. Never printing at all is a real harm too, so a
     * refusal that will not clear - the order is gone, or the owner is signed
     * out - prints rather than holding for ever.
     */
    @Test
    fun `a refusal that will not clear prints rather than holding for ever`() {
        assertEquals(SchedulePlan.PrintAt(null), schedulePlanFor(ScheduleLookup.Refused, leadTime, now))
    }

    /** The lead time is the agent's to choose; the arithmetic must follow it. */
    @Test
    fun `the lead time is honoured, whatever it is set to`() {
        assertEquals(
            SchedulePlan.PrintAt("2026-09-12T17:55:00Z"),
            schedulePlanFor(ScheduleLookup.Known("2026-09-12T18:00:00Z"), Duration.ofMinutes(5), now),
        )
        assertEquals(
            SchedulePlan.PrintAt("2026-09-12T17:00:00Z"),
            schedulePlanFor(ScheduleLookup.Known("2026-09-12T18:00:00Z"), Duration.ofHours(1), now),
        )
    }
}
