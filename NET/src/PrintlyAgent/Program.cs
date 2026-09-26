using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using PrintlyAgent.Core;
using PrintlyAgent.Ui;

namespace PrintlyAgent;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string InstanceMutexName = @"Local\PrintlyAgentNet.Instance";
    private const string ShowWindowEventName = @"Local\PrintlyAgentNet.ShowWindow";

    [STAThread]
    private static void Main()
    {
        // One agent per signed-in Windows user. A second copy cannot bind the
        // UI port, starts signed out on a random one, and - once signed in -
        // claims the same jobs as the first. Opening the app again (the desktop
        // shortcut while it is already running at login) now brings the running
        // window forward instead.
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var firstInstance);
        if (!firstInstance)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowWindowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The running copy is still starting up; it will show itself.
            }
            return;
        }
        using var showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);

        ApplicationConfiguration.Initialize();

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

        var showRequests = ThreadPool.RegisterWaitForSingleObject(showWindow, (_, _) =>
        {
            if (!form.IsHandleCreated || form.IsDisposed) return;
            form.BeginInvoke(() =>
            {
                if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                form.Show();
                form.Activate();
            });
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        Application.Run(form);
        showRequests.Unregister(null);

        // Stopped rather than left to the process exit, so the database is
        // closed cleanly and the backend is not left holding an SSE emitter open
        // until it times out.
        core.DisposeAsync().AsTask().GetAwaiter().GetResult();
        log.LogInformation("agent_stopped");
    }
}
