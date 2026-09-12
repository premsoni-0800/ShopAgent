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
     * More than one because a morning backlog is the normal case, and printing
     * it strictly one at a time means the last student waits for every job
     * ahead of theirs to download, print *and* have its outcome confirmed by
     * the spooler. Bounded because the gain flattens quickly: a shop has a
     * handful of physical printers, and jobs beyond that just queue in the
     * driver while still each holding a downloaded document in the temp dir.
     */
    val maxConcurrentPrintJobs: Int = 4,
    val reconnectBaseDelaySeconds: Double = 1.0,
    val reconnectMaxDelaySeconds: Double = 60.0,
    val downloadTimeoutSeconds: Long = 60,
    val jobTimeoutSeconds: Double = 300.0,
    val maxRetryAttempts: Int = 3,
)

fun loadSettings(): Settings {
    val localAppData = System.getenv("LOCALAPPDATA")
    val base = if (localAppData != null) Paths.get(localAppData) else Paths.get(System.getProperty("user.home"), ".${APP_NAME.lowercase()}")
    val appDataDir = base.resolve(APP_NAME)
    val logDir = appDataDir.resolve("logs")
    // Restricted, cleared-on-use scratch space for downloaded documents -
    // never a permanent copy, and never inside a user-browsable folder.
    val tempDir = appDataDir.resolve("tmp")

    Files.createDirectories(appDataDir)
    Files.createDirectories(logDir)
    Files.createDirectories(tempDir)

    return Settings(
        backendBaseUrl = (System.getenv("PRINTLY_BACKEND_URL") ?: DEFAULT_BACKEND_BASE_URL).trimEnd('/'),
        msg91WidgetId = System.getenv("PRINTLY_MSG91_WIDGET_ID") ?: DEFAULT_MSG91_WIDGET_ID,
        appDataDir = appDataDir,
        dbPath = appDataDir.resolve("agent.db"),
        logDir = logDir,
        tempDir = tempDir,
    )
}
