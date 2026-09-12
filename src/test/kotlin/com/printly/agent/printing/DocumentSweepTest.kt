package com.printly.agent.printing

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.nio.file.Files
import java.nio.file.Path

/**
 * Customer documents must not outlive the job that printed them. The pipeline
 * deletes each one in a `finally`, which covers every way a job can end but
 * none of the ways the process can - so this is the half that survives a crash
 * or a power cut on the counter PC.
 *
 * The risk in a sweep is the opposite mistake: deleting the file another agent
 * is printing from right now. Both directions are covered here.
 */
class DocumentSweepTest {

    /** No process has this id: pid 0 is not a real process handle on any supported OS. */
    private val deadPid = 0L

    @Test
    fun `a document left by a dead agent is removed`(@TempDir temp: Path) {
        val orphan = temp.resolve("$deadPid-abc123.pdf")
        Files.writeString(orphan, "%PDF-1.4")

        assertEquals(1, sweepOrphanedDocuments(temp))
        assertFalse(Files.exists(orphan), "a document whose agent is gone must not survive a restart")
    }

    @Test
    fun `a document this running agent owns is left alone`(@TempDir temp: Path) {
        val live = temp.resolve("${ProcessHandle.current().pid()}-live.pdf")
        Files.writeString(live, "%PDF-1.4")

        assertEquals(0, sweepOrphanedDocuments(temp))
        assertTrue(Files.exists(live), "deleting a document mid-print would fail the job it belongs to")
    }

    /**
     * Files written before the pid naming existed, and anything else that finds
     * its way in here, have no owner this can check. Left alone on purpose:
     * guessing wrong deletes a live job's document, and the alternative costs
     * only that one stale file stays until it is cleaned up by hand.
     */
    @Test
    fun `a file with no readable owner is left alone`(@TempDir temp: Path) {
        val legacy = temp.resolve("deadbeefcafe.pdf")
        Files.writeString(legacy, "%PDF-1.4")

        assertEquals(0, sweepOrphanedDocuments(temp))
        assertTrue(Files.exists(legacy))
    }

    @Test
    fun `non-pdf files in the temp directory are never touched`(@TempDir temp: Path) {
        val other = temp.resolve("$deadPid-notes.txt")
        Files.writeString(other, "not a customer document")

        assertEquals(0, sweepOrphanedDocuments(temp))
        assertTrue(Files.exists(other))
    }

    /** Called before the first job on a fresh install, when the directory may not exist yet. */
    @Test
    fun `a missing temp directory is not an error`(@TempDir temp: Path) {
        assertEquals(0, sweepOrphanedDocuments(temp.resolve("never-created")))
    }
}
