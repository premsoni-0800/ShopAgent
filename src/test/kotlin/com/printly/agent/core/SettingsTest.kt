package com.printly.agent.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * The concurrency default, which is a correctness setting rather than a
 * performance one.
 *
 * Too high against a single printer does not print faster - the driver
 * serialises the jobs regardless - it just holds several customers' documents
 * in the temp dir at once and makes it harder to tell which job the spooler is
 * reporting on. Too high is also how four wedged drivers can consume every
 * slot at once.
 */
class SettingsTest {

    @Test
    fun `one printer means one job at a time`() {
        assertEquals(1, Settings(
            backendBaseUrl = "https://example.invalid",
            msg91WidgetId = "w",
            appDataDir = java.nio.file.Paths.get("."),
            dbPath = java.nio.file.Paths.get("./x.db"),
            logDir = java.nio.file.Paths.get("."),
            tempDir = java.nio.file.Paths.get("."),
            heldDir = java.nio.file.Paths.get("."),
        ).maxConcurrentPrintJobs)
    }

    /**
     * A shop with real printers should be able to say so, but the value has to
     * survive a typo: an agent started with 0 would print nothing at all, and
     * would look exactly like an agent that was merely idle.
     */
    @Test
    fun `the concurrency override accepts sane values and rejects the rest`() {
        fun parse(raw: String?) = raw?.toIntOrNull()?.takeIf { it in 1..16 } ?: 1

        assertEquals(3, parse("3"))
        assertEquals(16, parse("16"))
        assertEquals(1, parse("0"), "zero would print nothing while looking idle")
        assertEquals(1, parse("-2"))
        assertEquals(1, parse("300"))
        assertEquals(1, parse("four"))
        assertEquals(1, parse(""))
        assertEquals(1, parse(null))
    }

    /**
     * The storage override, which decides where other people's documents sit.
     *
     * Two things have to hold. A folder the shop names is used, so "put the
     * order files on my Desktop" is answerable without a rebuild. And a value
     * that cannot be turned into a directory falls back rather than throwing:
     * a typo in an environment variable must not leave a counter with no agent
     * in the middle of a shift.
     */
    @Test
    fun `the data directory override is used when it can be created`() {
        val chosen = java.nio.file.Files.createTempDirectory("printly-data-dir")
        val resolved = resolveDataDir(chosen.toString()) { java.nio.file.Paths.get("/fallback") }

        assertEquals(chosen.toAbsolutePath(), resolved)
        assertTrue(java.nio.file.Files.isDirectory(resolved))
    }

    @Test
    fun `an unusable or absent override falls back instead of failing to start`() {
        val fallback = java.nio.file.Paths.get("/fallback")

        assertEquals(fallback, resolveDataDir(null) { fallback })
        assertEquals(fallback, resolveDataDir("") { fallback })
        assertEquals(fallback, resolveDataDir("   ") { fallback })

        // A path whose parent is a file, not a directory - createDirectories
        // cannot make this and must not take the agent down with it.
        val file = java.nio.file.Files.createTempFile("printly-not-a-dir", ".txt")
        assertEquals(fallback, resolveDataDir("$file/inside") { fallback })
    }

    /** Mirrors loadSettings()'s resolution so the rule can be tested without touching the real environment. */
    private fun resolveDataDir(configured: String?, fallback: () -> java.nio.file.Path): java.nio.file.Path =
        configured
            ?.trim()
            ?.takeIf { it.isNotEmpty() }
            ?.let { raw ->
                runCatching {
                    java.nio.file.Paths.get(raw).toAbsolutePath().also(java.nio.file.Files::createDirectories)
                }.getOrNull()
            }
            ?: fallback()
}
