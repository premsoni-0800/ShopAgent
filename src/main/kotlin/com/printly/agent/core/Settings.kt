package com.printly.agent.core

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths

// Deliberately distinct from the Python agent's own "PrintlyAgent" app-data
// folder name - the two can coexist on the same dev machine during
// validation without sharing (or clobbering) local state.
const val APP_NAME = "PrintlyAgentKt"
const val DEFAULT_BACKEND_BASE_URL = "https://printly-3fa8.onrender.com"

// The widget id is not a secret - it ships inside every Printly client (same
// value as PrintlyShopDashboard's VITE_MSG91_WIDGET_ID and Printlypartner's
// MSG91_WIDGET_ID, and the Python agent's own DEFAULT_MSG91_WIDGET_ID). The
// MSG91 auth key stays server-side; only the API ever holds it.
const val DEFAULT_MSG91_WIDGET_ID = "366961726a63303938373737"

data class Settings(
    val backendBaseUrl: String,
    val msg91WidgetId: String,
    val appDataDir: Path,
    val dbPath: Path,
    val logDir: Path,
    val tempDir: Path,
    /**
     * Where documents fetched ahead of a student's arrival are kept.
     *
     * A sibling of [tempDir] rather than a folder inside it, because the two
     * have opposite lifetimes and one sweep runs over the other. [tempDir] is
     * scratch: download, print, delete, and `sweepOrphanedDocuments` enforces
     * that across a crash by deleting any file whose owning process is gone.
     * A held document is the one case where an absent process means a restart
     * rather than a leak - the shop accepted in the morning, the student walks
     * in after lunch, and the agent may well have been restarted in between -
     * so keeping these anywhere that sweep looks would delete exactly the files
     * the feature exists to keep.
     */
    val heldDir: Path,
    val heartbeatIntervalSeconds: Long = 10,
    /**
     * How often to ask the backend outright for work, independently of the
     * SSE stream. The stream is the fast path and normally delivers a job in
     * well under a second; this is what makes the agent autonomous when the
     * stream is not delivering - a connection that died without the socket
     * closing, a push dropped mid-deploy, a job created while reconnecting.
     * Reconciling is free when there is nothing to do: the id is already in
     * the local database, so it never re-prints anything.
     */
    val jobReconcileIntervalSeconds: Long = 10,
    /**
     * How many print jobs may be in flight at once.
     *
     * One by default, because a shop has one printer.
     *
     * Concurrency here only buys anything when there is somewhere for the
     * extra work to go. Against a file writer it is a real win - 100 jobs ran
     * at 37/min four at a time against 17/min one at a time, because the
     * rendering overlaps. Against a single physical printer there is no such
     * gain: the driver serialises them anyway, so all four slots do is hold
     * four downloaded documents in the temp dir and blur which job the spooler
     * is actually reporting on.
     *
     * Raise it to the number of printers the shop really has, via
     * PRINTLY_MAX_CONCURRENT_PRINT_JOBS. Beyond that it costs and does not pay.
     */
    val maxConcurrentPrintJobs: Int = 1,
    val reconnectBaseDelaySeconds: Double = 1.0,
    val reconnectMaxDelaySeconds: Double = 60.0,
    val downloadTimeoutSeconds: Long = 60,
    val jobTimeoutSeconds: Double = 300.0,
    val maxRetryAttempts: Int = 3,
)

fun loadSettings(): Settings {
    val localAppData = System.getenv("LOCALAPPDATA")
    val base = if (localAppData != null) Paths.get(localAppData) else Paths.get(System.getProperty("user.home"), ".${APP_NAME.lowercase()}")

    // PRINTLY_DATA_DIR moves everything this agent keeps on disk - the database,
    // the logs, the scratch space and the held documents - to a folder the shop
    // chooses. Set it to something like C:\Users\you\Desktop\order files and the
    // documents land where they can be watched arriving.
    //
    // The default is deliberately NOT such a folder. These are other people's
    // coursework: the scratch copies are deleted the moment they have printed,
    // and %LOCALAPPDATA% keeps them out of a directory that gets opened, backed
    // up, screen-shared or synced to a cloud drive while they are there.
    // Pointing this at the Desktop is a reasonable thing for a shop to want and
    // an entirely different exposure, so it is a deliberate choice rather than
    // the default.
    //
    // A path that cannot be created falls back rather than refusing to start: a
    // typo in an environment variable must not leave a counter with no agent
    // mid-shift. The fallback is logged loudly, because an agent silently
    // writing somewhere other than where it was told is worse than either
    // outcome.
    val appDataDir = System.getenv("PRINTLY_DATA_DIR")
        ?.trim()
        ?.takeIf { it.isNotEmpty() }
        ?.let { configured ->
            runCatching { Paths.get(configured).toAbsolutePath().also(Files::createDirectories) }
                .onFailure { System.err.println("PRINTLY_DATA_DIR '$configured' could not be used ($it); falling back") }
                .getOrNull()
        }
        ?: base.resolve(APP_NAME)
    val logDir = appDataDir.resolve("logs")
    // Restricted, cleared-on-use scratch space for downloaded documents -
    // never a permanent copy, and never inside a user-browsable folder.
    val tempDir = appDataDir.resolve("tmp")
    // Deliberately not under tempDir - see Settings.heldDir for why the orphan
    // sweep must never be able to reach these.
    val heldDir = appDataDir.resolve("held")

    Files.createDirectories(appDataDir)
    Files.createDirectories(logDir)
    Files.createDirectories(tempDir)
    Files.createDirectories(heldDir)

    return Settings(
        backendBaseUrl = (System.getenv("PRINTLY_BACKEND_URL") ?: DEFAULT_BACKEND_BASE_URL).trimEnd('/'),
        msg91WidgetId = System.getenv("PRINTLY_MSG91_WIDGET_ID") ?: DEFAULT_MSG91_WIDGET_ID,
        // A shop that genuinely has more than one printer can say so without a
        // rebuild. Nonsense values fall back rather than starting an agent
        // that prints nothing (0) or thrashes the temp dir (300).
        maxConcurrentPrintJobs = System.getenv("PRINTLY_MAX_CONCURRENT_PRINT_JOBS")
            ?.toIntOrNull()
            ?.takeIf { it in 1..16 }
            ?: 1,
        appDataDir = appDataDir,
        dbPath = appDataDir.resolve("agent.db"),
        logDir = logDir,
        tempDir = tempDir,
        heldDir = heldDir,
    )
}
