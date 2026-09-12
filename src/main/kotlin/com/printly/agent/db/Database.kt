package com.printly.agent.db

import java.nio.file.Path
import java.sql.Connection
import java.sql.DriverManager
import java.time.Instant

/**
 * Local SQLite state - the agent's source of truth for "have I already
 * handled this job", direct port of the Python agent's `db.py`. Every write
 * is a plain committed statement, never buffered in memory only, so it
 * survives a crash, a Windows restart, or a network outage.
 *
 * Every public method holds [lock], because more than one print job now runs
 * at a time (see [com.printly.agent.jobs.JobDispatcher]) and they all share
 * the one [connection]. A JDBC `Connection` is not safe to drive from several
 * threads at once, and the specific thing that would break is the one that
 * matters most: [insertJobReference] is the *entire* duplicate-print
 * protection, and it is only a guard while "check and insert" stays
 * indivisible. Serialising here costs nothing worth measuring - SQLite
 * serialises writes internally regardless, and these critical sections are
 * microseconds of local I/O - whereas the failure it prevents is printing a
 * customer's document twice.
 */
class Database(dbPath: Path) : AutoCloseable {

    private val lock = Any()

    private val connection: Connection = DriverManager.getConnection("jdbc:sqlite:$dbPath").also {
        it.autoCommit = true
        it.createStatement().use { st -> st.execute("PRAGMA journal_mode=WAL") }
    }

    init {
        connection.createStatement().use { st ->
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS agent_state (
                    key   TEXT PRIMARY KEY,
                    value TEXT
                )
                """.trimIndent(),
            )
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS printers (
                    windows_printer_name TEXT PRIMARY KEY,
                    display_name         TEXT NOT NULL,
                    color_capable        TEXT,
                    duplex_capable       TEXT,
                    paper_sizes          TEXT,
                    status               TEXT NOT NULL DEFAULT 'UNKNOWN',
                    is_system_default    INTEGER NOT NULL DEFAULT 0,
                    last_synced_at       TEXT
                )
                """.trimIndent(),
            )
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS print_jobs (
                    job_id               TEXT PRIMARY KEY,
                    order_id             TEXT NOT NULL,
                    order_code           TEXT,
                    state                TEXT NOT NULL,
                    printer_windows_name TEXT,
                    attempt_count        INTEGER NOT NULL DEFAULT 0,
                    last_error           TEXT,
                    received_at          TEXT NOT NULL,
                    updated_at           TEXT NOT NULL,
                    scheduled_print_at   TEXT
                )
                """.trimIndent(),
            )
            st.executeUpdate(
                """
                CREATE TABLE IF NOT EXISTS job_events (
                    id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    job_id  TEXT NOT NULL,
                    at      TEXT NOT NULL,
                    event   TEXT NOT NULL,
                    detail  TEXT
                )
                """.trimIndent(),
            )
        }
    }

    override fun close() = synchronized(lock) { connection.close() }

    // --- agent_state ---

    fun getState(key: String): String? = synchronized(lock) {
        connection.prepareStatement("SELECT value FROM agent_state WHERE key = ?").use { ps ->
            ps.setString(1, key)
            ps.executeQuery().use { rs -> return if (rs.next()) rs.getString("value") else null }
        }
    }

    fun setState(key: String, value: String) = synchronized(lock) {
        connection.prepareStatement(
            "INSERT INTO agent_state (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
        ).use { ps ->
            ps.setString(1, key)
            ps.setString(2, value)
            ps.executeUpdate()
        }
    }

    // --- print_jobs ---

    data class JobRow(
        val jobId: String,
        val orderId: String,
        val orderCode: String?,
        val state: String,
        val printerWindowsName: String?,
        val attemptCount: Int,
        val lastError: String?,
        val receivedAt: String,
        val updatedAt: String,
        val scheduledPrintAt: String?,
    )

    /**
     * Records that a job id has been seen. Returns true only the first time -
     * a redelivered SSE push or a reconciliation re-list for a job already
     * known is therefore a guaranteed no-op for the caller, which is the
     * entire duplicate-print protection: nothing downstream decides whether
     * to print twice, this insert already decided it.
     */
    fun insertJobReference(jobId: String, orderId: String, orderCode: String?, scheduledPrintAt: String? = null): Boolean = synchronized(lock) {
        connection.prepareStatement(
            "INSERT OR IGNORE INTO print_jobs " +
                "(job_id, order_id, order_code, state, attempt_count, received_at, updated_at, scheduled_print_at) " +
                "VALUES (?, ?, ?, 'RECEIVED', 0, ?, ?, ?)",
        ).use { ps ->
            val now = Instant.now().toString()
            ps.setString(1, jobId)
            ps.setString(2, orderId)
            ps.setString(3, orderCode)
            ps.setString(4, now)
            ps.setString(5, now)
            ps.setString(6, scheduledPrintAt)
            return ps.executeUpdate() == 1
        }
    }

    fun getJob(jobId: String): JobRow? = synchronized(lock) {
        connection.prepareStatement("SELECT * FROM print_jobs WHERE job_id = ?").use { ps ->
            ps.setString(1, jobId)
            ps.executeQuery().use { rs -> return if (rs.next()) rs.toJobRow() else null }
        }
    }

    fun updateJobState(
        jobId: String,
        state: String,
        printerWindowsName: String? = null,
        lastError: String? = null,
        incrementAttempt: Boolean = false,
    ) = synchronized(lock) {
        connection.prepareStatement(
            "UPDATE print_jobs SET state = ?, printer_windows_name = COALESCE(?, printer_windows_name), " +
                "last_error = ?, attempt_count = attempt_count + ?, updated_at = ? WHERE job_id = ?",
        ).use { ps ->
            ps.setString(1, state)
            ps.setString(2, printerWindowsName)
            ps.setString(3, lastError)
            ps.setInt(4, if (incrementAttempt) 1 else 0)
            ps.setString(5, Instant.now().toString())
            ps.setString(6, jobId)
            ps.executeUpdate()
        }
    }

    fun unresolvedJobs(): List<JobRow> = synchronized(lock) {
        connection.prepareStatement(
            "SELECT * FROM print_jobs WHERE state NOT IN ('COMPLETED', 'FAILED', 'CANCELLED')",
        ).use { ps ->
            ps.executeQuery().use { rs ->
                val rows = mutableListOf<JobRow>()
                while (rs.next()) rows.add(rs.toJobRow())
                return rows
            }
        }
    }

    /**
     * Jobs a restart interrupted that it is *safe* to simply run again.
     *
     * Only the states before anything reached a printer qualify. Once a job is
     * SUBMITTED or PRINTING the spooler may already have put ink on paper, and
     * nothing readable afterwards distinguishes "died before printing" from
     * "died after printing" - so those are deliberately left alone for a human
     * to resolve via [unresolvedJobs] rather than reprinted on a guess. Same
     * rule as the pipeline's UNKNOWN outcome, and for the same reason.
     *
     * Excludes anything still held back for a future slot; that is
     * [dueScheduledJobs]' job, and running it now would print it early.
     */
    fun resumableJobs(): List<JobRow> = synchronized(lock) {
        connection.prepareStatement(
            "SELECT * FROM print_jobs WHERE state IN ('RECEIVED', 'VALIDATING', 'DOWNLOADING', 'DOWNLOADED') " +
                "AND scheduled_print_at IS NULL",
        ).use { ps ->
            ps.executeQuery().use { rs ->
                val rows = mutableListOf<JobRow>()
                while (rs.next()) rows.add(rs.toJobRow())
                return rows
            }
        }
    }

    /** RECEIVED jobs held back for a future print time whose time has now arrived. */
    fun dueScheduledJobs(nowIso: String): List<JobRow> = synchronized(lock) {
        connection.prepareStatement(
            "SELECT * FROM print_jobs WHERE state = 'RECEIVED' " +
                "AND scheduled_print_at IS NOT NULL AND scheduled_print_at <= ?",
        ).use { ps ->
            ps.setString(1, nowIso)
            ps.executeQuery().use { rs ->
                val rows = mutableListOf<JobRow>()
                while (rs.next()) rows.add(rs.toJobRow())
                return rows
            }
        }
    }

    // --- job_events ---

    fun recordEvent(jobId: String, event: String, detail: String? = null) = synchronized(lock) {
        connection.prepareStatement("INSERT INTO job_events (job_id, at, event, detail) VALUES (?, ?, ?, ?)").use { ps ->
            ps.setString(1, jobId)
            ps.setString(2, Instant.now().toString())
            ps.setString(3, event)
            ps.setString(4, detail)
            ps.executeUpdate()
        }
    }

    // --- printers ---

    fun upsertPrinter(
        windowsPrinterName: String,
        displayName: String,
        colorCapable: String?,
        duplexCapable: String?,
        paperSizes: String,
        status: String,
        isSystemDefault: Boolean,
    ) = synchronized(lock) {
        connection.prepareStatement(
            "INSERT INTO printers (windows_printer_name, display_name, color_capable, duplex_capable, paper_sizes, " +
                "status, is_system_default, last_synced_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?) " +
                "ON CONFLICT(windows_printer_name) DO UPDATE SET display_name = excluded.display_name, " +
                "color_capable = excluded.color_capable, duplex_capable = excluded.duplex_capable, " +
                "paper_sizes = excluded.paper_sizes, status = excluded.status, " +
                "is_system_default = excluded.is_system_default, last_synced_at = excluded.last_synced_at",
        ).use { ps ->
            ps.setString(1, windowsPrinterName)
            ps.setString(2, displayName)
            ps.setString(3, colorCapable)
            ps.setString(4, duplexCapable)
            ps.setString(5, paperSizes)
            ps.setString(6, status)
            ps.setInt(7, if (isSystemDefault) 1 else 0)
            ps.setString(8, Instant.now().toString())
            ps.executeUpdate()
        }
    }

    private fun java.sql.ResultSet.toJobRow() = JobRow(
        jobId = getString("job_id"),
        orderId = getString("order_id"),
        orderCode = getString("order_code"),
        state = getString("state"),
        printerWindowsName = getString("printer_windows_name"),
        attemptCount = getInt("attempt_count"),
        lastError = getString("last_error"),
        receivedAt = getString("received_at"),
        updatedAt = getString("updated_at"),
        scheduledPrintAt = getString("scheduled_print_at"),
    )
}
