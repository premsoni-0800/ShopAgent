using Microsoft.Data.Sqlite;

namespace PrintlyAgent.Db;

/// <summary>
/// One row of print_jobs. Port of Database.JobRow.
/// </summary>
public sealed record JobRow(
    string JobId,
    string OrderId,
    string? OrderCode,
    string State,
    string? PrinterWindowsName,
    int AttemptCount,
    string? LastError,
    string ReceivedAt,
    string UpdatedAt,
    string? ScheduledPrintAt,
    /// <summary>The order jumped the queue at the counter - see the `priority` column.</summary>
    bool Priority = false);

/// <summary>
/// Local SQLite state - the agent's source of truth for "have I already handled
/// this job". Port of db/Database.kt.
///
/// Every write is a plain committed statement, never buffered in memory only, so
/// it survives a crash, a Windows restart, or a network outage.
///
/// Every public method holds <c>_lock</c>, because more than one print job can
/// run at a time (see JobDispatcher) and they all share the one connection. A
/// connection is not safe to drive from several threads at once, and the
/// specific thing that would break is the one that matters most:
/// <see cref="InsertJobReference"/> is the *entire* duplicate-print protection,
/// and it is only a guard while "check and insert" stays indivisible.
/// Serialising here costs nothing worth measuring - SQLite serialises writes
/// internally regardless, and these critical sections are microseconds of local
/// I/O - whereas the failure it prevents is printing a customer's document
/// twice.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;

    public Database(string dbPath)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL");

        Execute("""
            CREATE TABLE IF NOT EXISTS agent_state (
                key   TEXT PRIMARY KEY,
                value TEXT
            )
            """);

        Execute("""
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
            """);

        Execute("""
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
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS job_events (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id  TEXT NOT NULL,
                at      TEXT NOT NULL,
                event   TEXT NOT NULL,
                detail  TEXT
            )
            """);

        // One machine can serve different shops over its life: a shop signs in
        // with its own credentials and the agent re-pairs to them. The jobs
        // already here belong to whoever had the machine before, and are
        // invisible to the new shop's credential - so they must not be resumed,
        // which means knowing whose they were.
        //
        // Added separately rather than in the CREATE above because installs
        // predating it exist, and CREATE TABLE IF NOT EXISTS leaves those
        // untouched. Already-present is the normal case on every run after the
        // first, so the duplicate-column error is expected, not a fault.
        try { Execute("ALTER TABLE print_jobs ADD COLUMN shop_id TEXT"); }
        catch (SqliteException) { /* column already there */ }

        // Whether the student walked to the counter and scanned. Same migration
        // shape, same reason: already-present is the normal case on every run
        // after the first.
        try { Execute("ALTER TABLE print_jobs ADD COLUMN priority INTEGER NOT NULL DEFAULT 0"); }
        catch (SqliteException) { /* column already there */ }
    }

    private void Execute(string sql)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static string NowIso() =>
        DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        lock (_lock) _connection.Dispose();
    }

    // --- agent_state ---------------------------------------------------------

    public string? GetState(string key)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM agent_state WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            var result = command.ExecuteScalar();
            return result is DBNull or null ? null : (string)result;
        }
    }

    public void SetState(string key, string value)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO agent_state (key, value) VALUES ($key, $value) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    // --- print_jobs ----------------------------------------------------------

    /// <summary>
    /// Records that a job id has been seen. Returns true only the first time - a
    /// redelivered SSE push or a reconciliation re-list for a job already known
    /// is therefore a guaranteed no-op for the caller, which is the entire
    /// duplicate-print protection: nothing downstream decides whether to print
    /// twice, this insert already decided it.
    /// </summary>
    public bool InsertJobReference(
        string jobId,
        string orderId,
        string? orderCode,
        string? scheduledPrintAt = null,
        string? shopId = null,
        bool priority = false)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT OR IGNORE INTO print_jobs " +
                "(job_id, order_id, order_code, state, attempt_count, received_at, updated_at, scheduled_print_at, shop_id, priority) " +
                "VALUES ($jobId, $orderId, $orderCode, 'RECEIVED', 0, $now, $now, $scheduled, $shopId, $priority)";
            var now = NowIso();
            command.Parameters.AddWithValue("$jobId", jobId);
            command.Parameters.AddWithValue("$orderId", orderId);
            command.Parameters.AddWithValue("$orderCode", (object?)orderCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$scheduled", (object?)scheduledPrintAt ?? DBNull.Value);
            command.Parameters.AddWithValue("$shopId", (object?)shopId ?? DBNull.Value);
            command.Parameters.AddWithValue("$priority", priority ? 1 : 0);
            return command.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// Records that this job's student has since scanned at the counter.
    ///
    /// Separate from <see cref="InsertJobReference"/> because priority usually
    /// arrives *after* the reference does: somebody orders ahead, then walks in.
    /// The insert is a no-op by then - that is what stops the document printing
    /// twice - so without this the grant would only ever live in the running
    /// queue and be lost on the next restart.
    /// </summary>
    public void MarkPriority(string jobId)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE print_jobs SET priority = 1 WHERE job_id = $jobId";
            command.Parameters.AddWithValue("$jobId", jobId);
            command.ExecuteNonQuery();
        }
    }

    public JobRow? GetJob(string jobId)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM print_jobs WHERE job_id = $jobId";
            command.Parameters.AddWithValue("$jobId", jobId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ToJobRow(reader) : null;
        }
    }

    public void UpdateJobState(
        string jobId,
        string state,
        string? printerWindowsName = null,
        string? lastError = null,
        bool incrementAttempt = false)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "UPDATE print_jobs SET state = $state, " +
                "printer_windows_name = COALESCE($printer, printer_windows_name), " +
                "last_error = $error, attempt_count = attempt_count + $bump, updated_at = $now " +
                "WHERE job_id = $jobId";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$printer", (object?)printerWindowsName ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
            command.Parameters.AddWithValue("$bump", incrementAttempt ? 1 : 0);
            command.Parameters.AddWithValue("$now", NowIso());
            command.Parameters.AddWithValue("$jobId", jobId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Whether <paramref name="jobId"/> has a local row at all.
    ///
    /// <see cref="UpdateJobState"/> is a plain UPDATE, so writing the state of a
    /// job that was never inserted changes nothing and says nothing - the state
    /// machine then runs on an assumed state that is not the one on disk.
    /// Callers that cannot tolerate that ask first.
    /// </summary>
    public bool HasJob(string jobId)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM print_jobs WHERE job_id = $jobId";
            command.Parameters.AddWithValue("$jobId", jobId);
            using var reader = command.ExecuteReader();
            return reader.Read();
        }
    }

    public List<JobRow> UnresolvedJobs(string shopId) =>
        Query(
            "SELECT * FROM print_jobs WHERE state NOT IN ('COMPLETED', 'FAILED', 'CANCELLED') AND shop_id = $shopId",
            command => command.Parameters.AddWithValue("$shopId", shopId));

    /// <summary>
    /// Jobs a restart interrupted that it is *safe* to simply run again.
    ///
    /// Only the states before anything reached a printer qualify. From
    /// SUBMITTING onwards the driver may already have put ink on paper, and
    /// nothing readable afterwards distinguishes "died before printing" from
    /// "died after printing" - so those are deliberately left alone for a human
    /// to resolve via <see cref="UnresolvedJobs"/> rather than reprinted on a
    /// guess. Same rule as the pipeline's UNKNOWN outcome, and for the same
    /// reason.
    ///
    /// SUBMITTING is what makes DOWNLOADED safe to include here. Printing is a
    /// blocking call, so before that state existed a job stayed DOWNLOADED for
    /// the whole time it was printing, and this query could not tell a job that
    /// had merely finished downloading from one that was 150 pages into a
    /// 300-page order.
    ///
    /// Excludes anything still held back for a future slot; that is
    /// <see cref="DueScheduledJobs"/>' job, and running it now would print it
    /// early.
    ///
    /// Scoped to one shop because a machine can serve several over its life -
    /// each shop signs in with its own credentials and the agent re-pairs. A job
    /// left behind by the previous shop is not this one's to resume, and
    /// claiming it would be done with credentials that cannot even see it.
    /// </summary>
    public List<JobRow> ResumableJobs(string shopId) =>
        Query(
            "SELECT * FROM print_jobs WHERE state IN ('RECEIVED', 'VALIDATING', 'DOWNLOADING', 'DOWNLOADED') " +
            "AND scheduled_print_at IS NULL AND shop_id = $shopId",
            command => command.Parameters.AddWithValue("$shopId", shopId));

    /// <summary>
    /// Jobs a previous run left after the document had reached a printer.
    ///
    /// The mirror image of <see cref="ResumableJobs"/>. Those are safe to run
    /// again because nothing was printed; these are not - the driver had the
    /// document, so paper may well have come out, and nothing readable
    /// afterwards distinguishes "died before printing" from "died after". They
    /// cannot be reprinted and they cannot be called done, so the only honest
    /// answer is to hand them to a person.
    ///
    /// Left alone, a job like this told nobody anything. It is not FAILED, so it
    /// never reached the errors list; it is not COMPLETED, so the shop's agent
    /// screen went on showing it as "Sent to the printer" - for ever, from an
    /// agent that had long since restarted and was never going to mention it
    /// again.
    /// </summary>
    public List<JobRow> JobsStrandedAtThePrinter(string shopId) =>
        Query(
            "SELECT * FROM print_jobs WHERE state IN ('SUBMITTING', 'SUBMITTED', 'PRINTING') AND shop_id = $shopId",
            command => command.Parameters.AddWithValue("$shopId", shopId));

    /// <summary>RECEIVED jobs held back for a future print time whose time has now arrived.</summary>
    public List<JobRow> DueScheduledJobs(string nowIso, string shopId) =>
        Query(
            "SELECT * FROM print_jobs WHERE state = 'RECEIVED' " +
            "AND scheduled_print_at IS NOT NULL AND scheduled_print_at <= $now AND shop_id = $shopId",
            command =>
            {
                command.Parameters.AddWithValue("$now", nowIso);
                command.Parameters.AddWithValue("$shopId", shopId);
            });

    /// <summary>
    /// Open jobs that have not been granted in-shop priority - the only ones a
    /// counter scan could still change - oldest first, a page at a time.
    ///
    /// Paged because the caller re-asks the backend about each one, and doing
    /// that for a whole backlog on a timer is what made the agent flood its own
    /// server. Oldest first so the rotation is stable: a page taken now and a
    /// page taken later walk the same list in the same direction.
    /// </summary>
    public List<JobRow> PriorityCandidates(string shopId, int limit, int offset) =>
        Query(
            "SELECT * FROM print_jobs WHERE state NOT IN ('COMPLETED', 'FAILED', 'CANCELLED', 'UNKNOWN') " +
            "AND priority = 0 AND shop_id = $shopId ORDER BY received_at, job_id LIMIT $limit OFFSET $offset",
            command =>
            {
                command.Parameters.AddWithValue("$shopId", shopId);
                command.Parameters.AddWithValue("$limit", limit);
                command.Parameters.AddWithValue("$offset", offset);
            });

    private List<JobRow> Query(string sql, Action<SqliteCommand> bind)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            bind(command);
            using var reader = command.ExecuteReader();
            var rows = new List<JobRow>();
            while (reader.Read()) rows.Add(ToJobRow(reader));
            return rows;
        }
    }

    // --- job_events ----------------------------------------------------------

    public void RecordEvent(string jobId, string @event, string? detail = null)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO job_events (job_id, at, event, detail) VALUES ($jobId, $at, $event, $detail)";
            command.Parameters.AddWithValue("$jobId", jobId);
            command.Parameters.AddWithValue("$at", NowIso());
            command.Parameters.AddWithValue("$event", @event);
            command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    // --- printers ------------------------------------------------------------

    public void UpsertPrinter(
        string windowsPrinterName,
        string displayName,
        string? colorCapable,
        string? duplexCapable,
        string paperSizes,
        string status,
        bool isSystemDefault)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO printers (windows_printer_name, display_name, color_capable, duplex_capable, paper_sizes, " +
                "status, is_system_default, last_synced_at) " +
                "VALUES ($name, $display, $color, $duplex, $sizes, $status, $default, $synced) " +
                "ON CONFLICT(windows_printer_name) DO UPDATE SET display_name = excluded.display_name, " +
                "color_capable = excluded.color_capable, duplex_capable = excluded.duplex_capable, " +
                "paper_sizes = excluded.paper_sizes, status = excluded.status, " +
                "is_system_default = excluded.is_system_default, last_synced_at = excluded.last_synced_at";
            command.Parameters.AddWithValue("$name", windowsPrinterName);
            command.Parameters.AddWithValue("$display", displayName);
            command.Parameters.AddWithValue("$color", (object?)colorCapable ?? DBNull.Value);
            command.Parameters.AddWithValue("$duplex", (object?)duplexCapable ?? DBNull.Value);
            command.Parameters.AddWithValue("$sizes", paperSizes);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$default", isSystemDefault ? 1 : 0);
            command.Parameters.AddWithValue("$synced", NowIso());
            command.ExecuteNonQuery();
        }
    }

    private static JobRow ToJobRow(SqliteDataReader reader) => new(
        JobId: reader.GetString(reader.GetOrdinal("job_id")),
        OrderId: reader.GetString(reader.GetOrdinal("order_id")),
        OrderCode: GetNullableString(reader, "order_code"),
        State: reader.GetString(reader.GetOrdinal("state")),
        PrinterWindowsName: GetNullableString(reader, "printer_windows_name"),
        AttemptCount: reader.GetInt32(reader.GetOrdinal("attempt_count")),
        LastError: GetNullableString(reader, "last_error"),
        ReceivedAt: reader.GetString(reader.GetOrdinal("received_at")),
        UpdatedAt: reader.GetString(reader.GetOrdinal("updated_at")),
        ScheduledPrintAt: GetNullableString(reader, "scheduled_print_at"),
        Priority: GetBool(reader, "priority"));

    private static bool GetBool(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return !reader.IsDBNull(ordinal) && reader.GetInt64(ordinal) != 0;
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
