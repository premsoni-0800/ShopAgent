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
    /// Where documents fetched ahead of a student's arrival are kept.
    ///
    /// <para>
    /// A sibling of <see cref="TempDir"/> rather than a folder inside it,
    /// because the two have opposite lifetimes and one sweep runs over the
    /// other. TempDir is scratch: download, print, delete, and
    /// <c>Documents.SweepOrphanedDocuments</c> enforces that across a crash by
    /// deleting any file whose owning process is gone. A held document is the
    /// one case where an absent process means a restart rather than a leak -
    /// the shop accepted in the morning, the student walks in after lunch, and
    /// the agent may well have been restarted in between - so keeping these
    /// anywhere that sweep looks would delete exactly the files the feature
    /// exists to keep.
    /// </para>
    /// </summary>
    string HeldDir,
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

        // PRINTLY_DATA_DIR moves everything this agent keeps on disk - the
        // database, the logs, the scratch space and the held documents - to a
        // folder the shop chooses. Set it to something like
        // C:\Users\you\Desktop\order files and the documents land where they
        // can be watched arriving.
        //
        // The default is deliberately NOT such a folder. These are other
        // people's coursework: the scratch copies are deleted the moment they
        // have printed, and %LOCALAPPDATA% keeps them out of a directory that
        // gets opened, backed up, screen-shared or synced to a cloud drive
        // while they are there. Pointing this at the Desktop is a reasonable
        // thing for a shop to want and an entirely different exposure, so it is
        // a deliberate choice rather than the default.
        //
        // A path that cannot be created falls back rather than refusing to
        // start: a typo in an environment variable must not leave a counter with
        // no agent mid-shift. The fallback is logged loudly, because an agent
        // silently writing somewhere other than where it was told is worse than
        // either outcome.
        var configured = Environment.GetEnvironmentVariable("PRINTLY_DATA_DIR");
        var appDataDir = Path.Combine(baseDir, AppConstants.AppName);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var resolved = Path.GetFullPath(configured.Trim());
                Directory.CreateDirectory(resolved);
                appDataDir = resolved;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine(
                    $"PRINTLY_DATA_DIR '{configured}' could not be used ({exc.Message}); falling back to {appDataDir}");
            }
        }
        var logDir = Path.Combine(appDataDir, "logs");
        // Restricted, cleared-on-use scratch space for downloaded documents -
        // never a permanent copy, and never inside a user-browsable folder.
        var tempDir = Path.Combine(appDataDir, "tmp");
        // Deliberately not under tempDir - see Settings.HeldDir for why the
        // orphan sweep must never be able to reach these.
        var heldDir = Path.Combine(appDataDir, "held");

        Directory.CreateDirectory(appDataDir);
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(heldDir);

        return new Settings(
            BackendBaseUrl: (Environment.GetEnvironmentVariable("PRINTLY_BACKEND_URL")
                             ?? AppConstants.DefaultBackendBaseUrl).TrimEnd('/'),
            Msg91WidgetId: Environment.GetEnvironmentVariable("PRINTLY_MSG91_WIDGET_ID")
                           ?? AppConstants.DefaultMsg91WidgetId,
            AppDataDir: appDataDir,
            DbPath: Path.Combine(appDataDir, "agent.db"),
            LogDir: logDir,
            TempDir: tempDir,
            HeldDir: heldDir,
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
