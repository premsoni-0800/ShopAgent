package com.printly.agent.jobs

import com.printly.agent.core.loadSettings
import com.printly.agent.db.Database
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.nio.file.Path

/**
 * The rules that keep an order accepted before its student arrives from
 * printing into an empty shop.
 *
 * Every assertion here is about the same failure, approached from a different
 * direction: a held job has claimed and downloaded and not printed, which is
 * the exact shape of a job a restart interrupted, and the sweep that replays
 * interrupted jobs would happily replay this one. The difference is that
 * nothing interrupted it - it is waiting on a person - and the shop cannot
 * un-print what a mistake here would produce, hours early, with nobody there.
 */
class HeldForArrivalTest {

    // --- the state machine -------------------------------------------------

    /**
     * Only a job whose documents are already down can be held, because holding
     * is a statement about files on this disk. The earlier states have nothing
     * to hold: RECEIVED has not been claimed, VALIDATING has not been told what
     * the items are, and DOWNLOADING is by definition not finished.
     */
    @Test
    fun `only a downloaded job can be held`() {
        assertTrue(canTransition(DOWNLOADED, HELD))
        assertFalse(canTransition(RECEIVED, HELD), "nothing has been claimed yet")
        assertFalse(canTransition(VALIDATING, HELD), "the items are not known yet")
        assertFalse(canTransition(DOWNLOADING, HELD), "the documents are not on disk yet")
    }

    /**
     * The walk the hold path actually takes, from the state a finished download
     * really leaves the job in.
     *
     * This is not the same assertion as the one above, and the difference cost a
     * bug: `downloadAll` leaves the job DOWNLOADING, so a hold that moved
     * straight to HELD from there tripped the transition guard and failed an
     * order whose documents had downloaded perfectly. Asserting the rule
     * (DOWNLOADING -> HELD is refused) is what makes the rule safe; asserting
     * the route is what makes the code that obeys it correct.
     */
    @Test
    fun `holding walks from downloading through downloaded`() {
        assertTrue(canTransition(DOWNLOADING, DOWNLOADED), "the download did finish")
        assertTrue(canTransition(DOWNLOADED, HELD), "and only then is there something to hold")
        assertFalse(
            canTransition(DOWNLOADING, HELD),
            "holding a job whose files are not on disk yet is the thing this edge must never allow",
        )
    }

    /**
     * SUBMITTING is the marker that says "paper may be moving", and a held job
     * must not be able to reach it directly. The release path goes back through
     * DOWNLOADED first, which is what puts the release in one identifiable
     * place instead of letting any future caller print a held job by accident.
     */
    @Test
    fun `a held job cannot reach the printer without being released first`() {
        assertFalse(canTransition(HELD, SUBMITTING), "the release must go back through DOWNLOADED")
        assertFalse(canTransition(HELD, SUBMITTED))
        assertFalse(canTransition(HELD, PRINTING))
        assertTrue(canTransition(HELD, DOWNLOADED), "this is the release")
        assertTrue(canTransition(DOWNLOADED, SUBMITTING), "and from there the ordinary path runs")
    }

    /**
     * A student can cancel an order they never came to collect, and a held job
     * can still fail - most plainly when the documents cannot be recovered.
     * Neither is possible if HELD is treated as an end state.
     */
    @Test
    fun `a held job can still be cancelled or failed and is not terminal`() {
        assertTrue(canTransition(HELD, CANCELLED))
        assertTrue(canTransition(HELD, FAILED))
        assertFalse(TERMINAL.contains(HELD), "a held job has somebody still to serve")
    }

    // --- the queries -------------------------------------------------------

    /**
     * The pair, in one test, because the pair is the point.
     *
     * Asserting only that a held job is skipped would pass just as well against
     * a resume sweep that had stopped working altogether - and that failure
     * mode is invisible until the day a shop restarts mid-order and nothing
     * comes back. Asserting only that an interrupted job resumes would pass
     * against a sweep that replays held jobs too. The behaviour worth pinning
     * is that the sweep can tell these two apart, and both rows are DOWNLOADED-
     * shaped by construction here so that it has to.
     */
    @Test
    fun `the resume sweep replays an interrupted job and leaves a held one alone`(@TempDir temp: Path) {
        Database(temp.resolve("agent.db")).use { db ->
            db.insertJobReference("job-interrupted", "order-1", "A-1", shopId = SHOP)
            db.updateJobState("job-interrupted", DOWNLOADED)

            db.insertJobReference("job-held", "order-2", "A-2", shopId = SHOP)
            db.updateJobState("job-held", DOWNLOADED)
            db.updateJobState("job-held", HELD)

            val resumable = db.resumableJobs(SHOP).map { it.jobId }
            assertTrue(
                resumable.contains("job-interrupted"),
                "a genuinely interrupted DOWNLOADED job must still be replayed, or a restart strands it for good",
            )
            assertFalse(
                resumable.contains("job-held"),
                "replaying a held job prints somebody's coursework into an empty shop",
            )
        }
    }

    /**
     * Oldest first, so a shop that took a morning's worth of orders releases
     * them in the order it took them, and so successive passes walk the list
     * the same way rather than rotating.
     */
    @Test
    fun `held jobs come back oldest first`(@TempDir temp: Path) {
        Database(temp.resolve("agent.db")).use { db ->
            // received_at is written by insertJobReference from the clock, so
            // the rows are aged apart rather than inserted in one instant -
            // otherwise this test would be asserting the tie-break and not the
            // ordering it is named for.
            for (id in listOf("job-a", "job-b", "job-c")) {
                db.insertJobReference(id, "order-$id", id.uppercase(), shopId = SHOP)
                db.updateJobState(id, DOWNLOADED)
                db.updateJobState(id, HELD)
                Thread.sleep(5)
            }

            assertEquals(listOf("job-a", "job-b", "job-c"), db.heldJobs(SHOP).map { it.jobId })
        }
    }

    /**
     * One machine serves different shops over its life - a shop signs out and
     * another signs in with its own credentials. A held order left by the
     * previous shop is not this one's to print, and the credential this agent
     * now holds could not even ask the backend about it.
     */
    @Test
    fun `held jobs are scoped to one shop`(@TempDir temp: Path) {
        Database(temp.resolve("agent.db")).use { db ->
            db.insertJobReference("ours", "order-1", "A-1", shopId = SHOP)
            db.updateJobState("ours", DOWNLOADED)
            db.updateJobState("ours", HELD)

            db.insertJobReference("theirs", "order-2", "B-2", shopId = "another-shop")
            db.updateJobState("theirs", DOWNLOADED)
            db.updateJobState("theirs", HELD)

            assertEquals(listOf("ours"), db.heldJobs(SHOP).map { it.jobId })
        }
    }

    // --- where the files live ----------------------------------------------

    /**
     * The orphan sweep deletes any document whose owning process is gone. For
     * a held document that is exactly the wrong reading: the shop accepted in
     * the morning, the student arrives after lunch, and the agent may well have
     * been restarted in between - an absent process means a restart, not a
     * leak.
     *
     * Pinned as a fact about the two directories rather than by running the
     * sweep, because the sweep's own safety here rests on it looking in one
     * directory and not below it. Making the held store a sibling means that
     * safety does not depend on anyone remembering the distinction later.
     */
    @Test
    fun `the held store is not where the orphan sweep looks`() {
        val settings = loadSettings()
        assertFalse(
            settings.heldDir.startsWith(settings.tempDir),
            "held documents under the temp dir would be deleted by the orphan sweep after any restart",
        )
        assertEquals(
            settings.tempDir.parent,
            settings.heldDir.parent,
            "siblings under the app data dir - the held store is not somewhere else entirely",
        )
    }

    /**
     * The process that prints a held order is very often not the one that
     * fetched it, so the path has to be recomputable from ids both processes
     * have rather than remembered.
     */
    @Test
    fun `a held document's path is derived from the job and item ids`(@TempDir temp: Path) {
        val first = com.printly.agent.printing.heldDocumentPath(temp, "job-1", "item-1")
        val again = com.printly.agent.printing.heldDocumentPath(temp, "job-1", "item-1")
        assertEquals(first, again, "the same job and item must always name the same file")

        assertEquals("item-1.pdf", first.fileName.toString())
        assertEquals("job-1", first.parent.fileName.toString(), "one directory per job")

        val otherItem = com.printly.agent.printing.heldDocumentPath(temp, "job-1", "item-2")
        assertEquals(first.parent, otherItem.parent, "items of one job share its directory")
        assertFalse(first == otherItem)
    }

    private companion object {
        const val SHOP = "shop-1"
    }
}
