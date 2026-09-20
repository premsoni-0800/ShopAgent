using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using PrintlyAgent.Core;
using PrintlyAgent.Ui;

namespace PrintlyAgent;

[SupportedOSPlatform("windows")]
internal static class Program
{
    // DllImport rather than the newer LibraryImport: the source generator that
    // backs LibraryImport emits unsafe code, and turning AllowUnsafeBlocks on
    // for the whole project to bring a window to the front is a poor trade.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>SW_RESTORE - un-minimises without disturbing a window that is already open.</summary>
    private const int ShowRestore = 9;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // One agent per machine, enforced before anything else starts.
        //
        // There was nothing stopping a second copy, and a second copy is not
        // harmless: the first one owns port 17384, so the second falls back to
        // an ephemeral port - and the session cookie the dashboard signed in
        // with is scoped to the origin, which means the new window comes up
        // signed out while a perfectly good session is still running behind it.
        // Worse, both then poll the backend and claim print jobs, so a job can
        // be claimed by one and printed by whichever got there first.
        //
        // Launching again is how somebody asks for the window they already have,
        // so that is what it does: focus the running one and leave. The mutex is
        // session-scoped rather than Global\ because the agent is per-user, and
        // two different users signed into the same counter PC are two different
        // shops as far as the credential store is concerned.
        using var single = new Mutex(initiallyOwned: true, AppConstants.AppName, out var isOnlyInstance);
        if (!isOnlyInstance)
        {
            FocusRunningInstance();
            return;
        }

        // Settings first: the log directory comes from it, and a log that only
        // starts once everything else has succeeded is no use for the failures
        // worth recording.
        var settings = SettingsLoader.Load();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            // Both sinks on purpose. The console one is silent in the shipped
            // WinExe (there is no console) but is how the app is read during
            // development; the file one is the only record that survives on a
            // shop counter.
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.AddProvider(new FileLoggerProvider(settings.LogDir));
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var log = loggerFactory.CreateLogger("PrintlyAgent");
        log.LogInformation(
            "agent_starting version={Version} backend={Backend} data={Data}",
            Auth.AgentVersion, settings.BackendBaseUrl, settings.AppDataDir);

        // Nothing above this point can crash unrecorded, and nothing below it
        // should either. A WinExe that dies takes its reason with it unless
        // somebody writes it down first - and "it just closed" is the single
        // least actionable bug report there is.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.LogCritical(e.ExceptionObject as Exception, "unhandled_exception terminating={Terminating}", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "unobserved_task_exception");
            e.SetObserved();
        };
        Application.ThreadException += (_, e) => log.LogError(e.Exception, "ui_thread_exception");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var core = new AgentCore(settings, loggerFactory);

        // Attempts to resume the background loops immediately if this PC was
        // already signed in and paired from a previous run - Start() is itself a
        // no-op until both are true, so calling it unconditionally is correct.
        core.Start();

        // Signed in but never paired is a real state and it is silent: the app
        // serves the dashboard, the owner sees their orders, and no job is ever
        // claimed because there is no device credential. Repaired here, from
        // the session already on disk, rather than waiting for the dashboard to
        // ask again. Fire-and-forget so a slow or unreachable backend cannot
        // hold up the window; it logs its own outcome either way.
        _ = core.EnsurePairedIfSignedInAsync();

        // The bundles live next to the executable. Served over http://127.0.0.1
        // rather than from a file: URL because webviews are unreliable about
        // executing a dynamically inserted third-party script (the OTP widget)
        // from a non-http origin.
        var contentRoot = Path.Combine(AppContext.BaseDirectory, "webcontent");

        using var server = new WebUiServer(
            loggerFactory.CreateLogger<WebUiServer>(), settings.BackendBaseUrl, contentRoot);

        // How the Files screen previews a document this machine is already
        // holding, without going back to the network for a signed URL to a file
        // that is sitting on this disk. The agent resolves it from its own
        // table, so the page never gets to name a path.
        server.HeldFileResolver = core.HeldFilePath;

        var bridge = new JsBridge(loggerFactory.CreateLogger<JsBridge>(), core);

        using var form = new MainForm(loggerFactory.CreateLogger<MainForm>(), core, server, bridge);

        Application.Run(form);

        // Stopped rather than left to the process exit, so the database is
        // closed cleanly and the backend is not left holding an SSE emitter open
        // until it times out.
        core.DisposeAsync().AsTask().GetAwaiter().GetResult();
        log.LogInformation("agent_stopped");
    }

    /// <summary>
    /// Brings the agent that is already running to the front.
    ///
    /// Best-effort by design: if the window cannot be found - it is still
    /// starting, or the process is running without one - the new instance still
    /// exits rather than starting a second agent. A missing window is a worse
    /// reason to end up with two of them than it is to end up with none.
    /// </summary>
    private static void FocusRunningInstance()
    {
        try
        {
            var self = Environment.ProcessId;
            foreach (var other in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                using (other)
                {
                    if (other.Id == self || other.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(other.MainWindowHandle, ShowRestore);
                    SetForegroundWindow(other.MainWindowHandle);
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Nothing to report to and nowhere to report it: the logger belongs
            // to the instance that is actually running. Exiting quietly is the
            // whole contract of this path.
        }
    }
}
