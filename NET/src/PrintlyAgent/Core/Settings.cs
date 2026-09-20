namespace PrintlyAgent.Core;

/// <summary>
/// Where the agent keeps its state and how it is tuned, read once at startup.
///
/// A faithful port of core/Settings.kt. The app-data folder name differs on
/// purpose - "PrintlyAgentNet" rather than "PrintlyAgentKt" - so this build and
/// the Kotlin one can run on the same machine during validation without sharing
/// a database, a credential, or a half-written temp file. That separation is the
/// whole point of having both: they have to be comparable, not entangled.
/// </summary>
public static class AppConstants
{
    public const string AppName = "PrintlyAgentNet";
    public const string DefaultBackendBaseUrl = "https://printly-3fa8.onrender.com";

    // The widget id is not a secret - it ships inside every Printly client (the
    // same value as the dashboard's VITE_MSG91_WIDGET_ID). The MSG91 auth key
    // stays server-side; only the API ever holds it.
    public const string DefaultMsg91WidgetId = "366961726a63303938373737";
}

public sealed record Settings(
    string BackendBaseUrl,
    string Msg91WidgetId,
    string AppDataDir,
    string DbPath,
    string LogDir,
    string TempDir,

    /// <summary>
    /// Where a waiting student's files are kept, one folder per order, from the
    /// moment the order arrives until it prints.
    ///
    /// <para>
    /// Distinct from <see cref="TempDir"/>, which is scratch space cleared as
    /// soon as a job finishes with it. These files exist precisely so that there
    /// is nothing left to fetch when the student finally walks in and scans, so
    /// they have to outlive the job that fetched them - and a restart.
    /// </para>
    /// </summary>
    string FilesDir = "",

    long HeartbeatIntervalSeconds = 10,

    /// <summary>
    /// How often to ask the backend outright for work, independently of the SSE
    /// stream. The stream is the fast path and normally delivers a job in well
    /// under a second; this is what makes the agent autonomous when the stream
    /// is not delivering - a connection that died without the socket closing, a
    /// push dropped mid-deploy, a job created while reconnecting. Reconciling is
    /// free when there is nothing to do: the id is already in the local
    /// database, so it never re-prints anything.
    /// </summary>
    long JobReconcileIntervalSeconds = 10,

    /// <summary>
    /// How many print jobs may be in flight at once. One by default, because a
    /// shop has one printer.
    ///
    /// Concurrency only buys anything when there is somewhere for the extra work
    /// to go. Against a file writer it is a real win - 100 jobs ran at 37/min
    /// four at a time against 17/min one at a time, because the rendering
    /// overlaps. Against a single physical printer there is no such gain: the
    /// driver serialises them anyway, so all four slots do is hold four
    /// downloaded documents in the temp dir and blur which job the spooler is
    /// actually reporting on.
    ///
    /// Raise it to the number of printers the shop really has, via
    /// PRINTLY_MAX_CONCURRENT_PRINT_JOBS. Beyond that it costs and does not pay.
    /// </summary>
    int MaxConcurrentPrintJobs = 1,

    double ReconnectBaseDelaySeconds = 1.0,
    double ReconnectMaxDelaySeconds = 60.0,
    long DownloadTimeoutSeconds = 60,
    /// <summary>
    /// How long a submitted job may make no progress at all before the agent
    /// stops waiting for it and asks a person.
    ///
    /// A stall window, not a total time limit: a job whose page count is still
    /// climbing is healthy however long it takes. It was a total limit, and a
    /// 162-page order - six minutes or so of real printing - was recorded as
    /// UNKNOWN while it was still coming out.
    /// </summary>
    double JobStallSeconds = 300.0,
    int MaxRetryAttempts = 3
);

public static class SettingsLoader
{
    public static Settings Load()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var baseDir = !string.IsNullOrEmpty(localAppData)
            ? localAppData
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "." + AppConstants.AppName.ToLowerInvariant());

        var appDataDir = Path.Combine(baseDir, AppConstants.AppName);
        var logDir = Path.Combine(appDataDir, "logs");
        // Restricted, cleared-on-use scratch space for downloaded documents -
        // never a permanent copy, and never inside a user-browsable folder.
        var tempDir = Path.Combine(appDataDir, "tmp");
        // Where a waiting student's files are kept until they walk in.
        //
        // Separate from tmp deliberately: tmp is cleared on use, and these have
        // to survive from the upload until the scan - minutes or hours later,
        // across a restart. They are fetched the moment the order arrives so
        // that pressing Print at the counter starts a printer rather than a
        // download, which is the whole point of holding them here.
        //
        // Deleted once the order prints, and swept if it never does. See
        // Documents.SweepAbandonedOrders.
        var filesDir = Path.Combine(appDataDir, "files");

        Directory.CreateDirectory(appDataDir);
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(filesDir);

        return new Settings(
            BackendBaseUrl: (Environment.GetEnvironmentVariable("PRINTLY_BACKEND_URL")
                             ?? AppConstants.DefaultBackendBaseUrl).TrimEnd('/'),
            Msg91WidgetId: Environment.GetEnvironmentVariable("PRINTLY_MSG91_WIDGET_ID")
                           ?? AppConstants.DefaultMsg91WidgetId,
            AppDataDir: appDataDir,
            DbPath: Path.Combine(appDataDir, "agent.db"),
            LogDir: logDir,
            TempDir: tempDir,
            FilesDir: filesDir,
            // A shop that genuinely has more than one printer can say so without
            // a rebuild. Nonsense values fall back rather than starting an agent
            // that prints nothing (0) or thrashes the temp dir (300).
            MaxConcurrentPrintJobs: ReadBoundedInt("PRINTLY_MAX_CONCURRENT_PRINT_JOBS", 1, 16, 1)
        );
    }

    private static int ReadBoundedInt(string name, int min, int max, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(raw, out var value) && value >= min && value <= max) return value;
        return fallback;
    }
}
