using System.Drawing;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PrintlyAgent.Core;

namespace PrintlyAgent.Ui;

/// <summary>
/// The desktop shell: one Windows app that is both the shop dashboard and the
/// thing that drives the printers.
///
/// Port of the window half of ui/PrintlyAgentApp.kt, with JavaFX's WebView
/// replaced by WebView2.
///
/// The shopkeeper installs one thing and gets one icon. Behind the window,
/// <see cref="AgentCore"/>'s loops run exactly as before - claiming jobs,
/// printing them, reporting outcomes - whether or not anyone is looking at the
/// UI, which is what makes unattended printing unattended.
///
/// Two bundles share the one local origin: <c>/</c> is the full shop dashboard,
/// and <c>/agent</c> is this machine's own setup screen, which does the things
/// only the native side can - signing this PC in, pairing it, and storing the
/// credential in Windows Credential Manager.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MainForm : Form
{
    private readonly ILogger _log;
    private readonly AgentCore _core;
    private readonly WebUiServer _server;
    private readonly JsBridge _bridge;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };

    /// <summary>
    /// The window the shop gets on a screen with room for it, in device-
    /// independent pixels - the size the dashboard's widest layout was drawn
    /// for. It is a preference, not a promise: <see cref="FitToScreen"/> takes
    /// whichever is smaller, this or the screen.
    /// </summary>
    private const int PreferredWidthDip = 1280;
    private const int PreferredHeightDip = 860;

    /// <summary>
    /// The floor on dragging the window smaller - about not being able to lose
    /// the window by shrinking it to a stub, rather than about the layout, which
    /// <see cref="FitDashboardToWindow"/> handles on its own.
    /// </summary>
    private const int MinimumWidthDip = 720;
    private const int MinimumHeightDip = 520;

    /// <summary>
    /// The width the vendored dashboard is actually drawn for.
    ///
    /// Below it the bundle has no layout that holds together: the stat row stays
    /// five columns however narrow it gets, and the Details/Print buttons in a
    /// queue row keep their full width and ride up over the filename and the
    /// IN-SHOP PRIORITY badge beside them. It is visibly wrong by 1100px and
    /// unusable well before the window reaches its minimum.
    ///
    /// That belongs in the dashboard's own repo (premsoni-0800/printlypartner,
    /// built from ../printlypartner-web) and cannot be fixed from this side -
    /// what this side can do is never ask the page to lay out narrower than the
    /// one width it is known to be right at. See FitDashboardToWindow.
    /// </summary>
    private const int DashboardDesignWidthDip = 1280;

    /// <summary>
    /// How far down the page is allowed to be scaled before legibility loses to
    /// layout. Only reachable on a window near its minimum.
    /// </summary>
    private const double MinimumZoom = 0.5;

    /// <summary>
    /// The brand mark, for the title bar and the taskbar button.
    ///
    /// Read from the embedded copy rather than the .exe, so every size in the
    /// file is available and Windows picks the one it wants instead of scaling
    /// a 32px image up. Failure is not fatal: a window with the stock icon is a
    /// working window, and refusing to open one over a missing picture would be
    /// a poor trade.
    /// </summary>
    private static Icon? LoadAppIcon(ILogger log)
    {
        try
        {
            using var stream = typeof(MainForm).Assembly
                .GetManifestResourceStream("PrintlyAgent.printly.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception exc)
        {
            log.LogDebug(exc, "app_icon_unavailable");
            return null;
        }
    }

    public MainForm(ILogger log, AgentCore core, WebUiServer server, JsBridge bridge)
    {
        _log = log;
        _core = core;
        _server = server;
        _bridge = bridge;

        Text = "Printly Partner";
        Icon = LoadAppIcon(log);

        // Placed by FitToScreen once the handle exists and the screen and its
        // scaling are known. CenterScreen would centre the size asked for here,
        // which is not necessarily the size the window ends up with.
        StartPosition = FormStartPosition.Manual;

        Controls.Add(_webView);
        _ = StartWebViewAsync();
    }

    /// <summary>
    /// The scaling of the screen this window is on, asked of Windows directly.
    ///
    /// Not <see cref="Control.DeviceDpi"/>, which still reads 96 this early in
    /// the window's life however aware the process is - and a 96 here is not an
    /// error that announces itself. It is simply a window that comes out at a
    /// quarter of its intended area on a 200% screen, which is how the dashboard
    /// ended up laying itself out for a 640px viewport inside what looked like a
    /// 1280px window: text and toggles spilling out of cards built for a width
    /// the page never had.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Falls back to the value WinForms has if the call fails, which is better
    /// than dividing by zero over a window size.
    /// </summary>
    private int CurrentDpi
    {
        get
        {
            if (!IsHandleCreated) return DeviceDpi;
            try
            {
                var dpi = GetDpiForWindow(Handle);
                return dpi > 0 ? (int)dpi : DeviceDpi;
            }
            catch (Exception exc)
            {
                _log.LogDebug(exc, "window_dpi_unavailable");
                return DeviceDpi;
            }
        }
    }

    /// <summary>
    /// Device-independent pixels to real ones, at this window's current scaling.
    ///
    /// Every size in this class is written at 96dpi and put through here,
    /// because a shop counter is as likely to be a 4K screen at 200% as a
    /// 1366x768 laptop at 100%, and a literal 1280 means two very different
    /// windows on those two machines.
    /// </summary>
    private static int Scale(int dip, int dpi) => (int)Math.Round(dip * dpi / 96.0);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var dpi = CurrentDpi;
        ApplyMinimumSize(dpi);
        FitToScreen(dpi);
    }

    /// <summary>
    /// Opens the window at the size the dashboard wants, or at the size the
    /// screen actually has, whichever is smaller - then centres what is left.
    ///
    /// The old fixed 1280x860 was bigger than the whole working area of a
    /// 1366x768 counter PC, so the window opened with its bottom edge - and the
    /// buttons along it - underneath the taskbar, on a machine nobody was going
    /// to reach for a mouse and resize.
    /// </summary>
    private void FitToScreen(int dpi)
    {
        var work = Screen.FromHandle(Handle).WorkingArea;

        var width = Math.Min(Scale(PreferredWidthDip, dpi), work.Width);
        var height = Math.Min(Scale(PreferredHeightDip, dpi), work.Height);

        Bounds = new Rectangle(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height);

        _log.LogInformation(
            "window_sized dpi={Dpi} size={Width}x{Height} work={WorkWidth}x{WorkHeight}",
            dpi, width, height, work.Width, work.Height);
    }

    /// <summary>
    /// Clamped to the working area as well as scaled, because a minimum larger
    /// than the screen is a window Windows cannot show in one piece.
    /// </summary>
    private void ApplyMinimumSize(int dpi)
    {
        var work = Screen.FromHandle(Handle).WorkingArea;

        MinimumSize = new Size(
            Math.Min(Scale(MinimumWidthDip, dpi), work.Width),
            Math.Min(Scale(MinimumHeightDip, dpi), work.Height));
    }

    /// <summary>
    /// Scales the page so the dashboard is always laid out at the width it was
    /// designed for, whatever size the window happens to be.
    ///
    /// A narrower window used to mean a narrower page, and a narrower page is
    /// where the vendored bundle comes apart - see
    /// <see cref="DashboardDesignWidthDip"/>. Zooming out instead keeps the
    /// viewport at 1280 and makes everything in it smaller, so a half-screen
    /// window shows the whole dashboard, just smaller: the toggles stay in their
    /// cards and the Print button stays beside the filename rather than on top
    /// of it.
    ///
    /// The zoom is expressed against the window's own scaling, not its pixels,
    /// which is what keeps this right on a 200% screen - where the same window
    /// has twice the pixels and needs exactly the same zoom.
    ///
    /// Above the design width it stays at 1 and the dashboard simply gets the
    /// extra room, which is what the wide layout is for.
    /// </summary>
    private void FitDashboardToWindow()
    {
        if (_webView.CoreWebView2 is null) return;
        if (WindowState == FormWindowState.Minimized) return;
        if (ClientSize.Width <= 0) return;

        var logicalWidth = ClientSize.Width * 96.0 / CurrentDpi;
        var zoom = Math.Clamp(logicalWidth / DashboardDesignWidthDip, MinimumZoom, 1.0);

        try
        {
            // Compared before assigning because this runs on every resize step,
            // and every assignment is a relayout of the whole page - dragging an
            // edge would otherwise be one relayout per mouse move.
            if (Math.Abs(_webView.ZoomFactor - zoom) > 0.005) _webView.ZoomFactor = zoom;
        }
        catch (Exception exc)
        {
            // A webview on its way out, most likely. A window at the wrong zoom
            // is not worth taking the app down for.
            _log.LogDebug(exc, "zoom_failed");
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitDashboardToWindow();
    }

    /// <summary>
    /// Follows the window onto a screen with different scaling.
    ///
    /// WinForms resizes the window itself under PerMonitorV2 and WebView2
    /// re-renders at the new scale on its own; what neither does is revisit a
    /// MinimumSize measured in the old screen's pixels, which would otherwise
    /// act as a 150%-sized floor on a 100% screen.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyMinimumSize(e.DeviceDpiNew);
        FitDashboardToWindow();
    }

    /// <summary>
    /// Brings the webview up, and says so plainly if it cannot.
    ///
    /// The interface is entirely inside that control, so a failure here is a
    /// window with nothing in it. Unwrapped, the exception went to the
    /// process-wide unobserved-task handler and the shop got a blank white
    /// rectangle - technically logged, but nobody standing at a counter reads
    /// a log to find out why the app they just installed looks broken.
    ///
    /// The case worth naming is a machine without the WebView2 runtime. It is
    /// present on Windows 11 and on any Windows 10 that has had Edge updated,
    /// which is nearly all of them - but "nearly" is doing real work there, and
    /// the failure is one a shop can actually fix once it is told what it is.
    /// </summary>
    private async Task StartWebViewAsync()
    {
        try
        {
            await InitialiseAsync().ConfigureAwait(true);
        }
        catch (WebView2RuntimeNotFoundException exc)
        {
            _log.LogCritical(exc, "webview2_runtime_missing");
            ShowStartupFailure(
                "Microsoft Edge WebView2 is required",
                "Printly Partner displays its screens using Microsoft Edge WebView2, "
                + "which is not installed on this computer.\n\n"
                + "Install the Microsoft Edge WebView2 Runtime, then start Printly "
                + "Partner again. It is a free download from Microsoft:\n\n"
                + "https://developer.microsoft.com/microsoft-edge/webview2/");
        }
        catch (Exception exc)
        {
            _log.LogCritical(exc, "webui_failed_to_start");
            ShowStartupFailure(
                "Printly Partner could not start",
                "The application window could not be prepared.\n\n"
                + exc.Message
                + "\n\nThe log file has the details: "
                + _core.Settings.LogDir);
        }
    }

    /// <summary>
    /// Replaces the empty webview with the reason it is empty, and closes when
    /// dismissed - there is nothing this window can do without it.
    /// </summary>
    private void ShowStartupFailure(string caption, string detail)
    {
        if (IsDisposed) return;

        void Show()
        {
            Controls.Remove(_webView);
            MessageBox.Show(this, detail, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }

        if (InvokeRequired) BeginInvoke(Show); else Show();
    }

    private async Task InitialiseAsync()
    {
        // The user data folder is where the engine keeps local storage, and the
        // dashboard keeps its whole session there. Left to the default it lands
        // beside the executable - unwritable under Program Files - so the
        // shopkeeper would be signed out every single time they close the app,
        // on the one machine that is meant to sit logged in at the counter all
        // day. Kept alongside the agent's other state instead.
        var userData = Path.Combine(_core.Settings.AppDataDir, "webview");
        Directory.CreateDirectory(userData);

        var environment = await CoreWebView2Environment
            .CreateAsync(userDataFolder: userData).ConfigureAwait(true);
        await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var web = _webView.CoreWebView2;

        // The dashboard previews customer-uploaded documents. Nothing about that
        // should be able to open a second window, and a page that navigates away
        // must not keep the bridge - see the handlers below.
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.Settings.IsStatusBarEnabled = false;

        // Injected before any page script runs, and re-injected on every
        // navigation, which is what AddScriptToExecuteOnDocumentCreatedAsync is
        // for. The shim guards against installing itself twice.
        await web.AddScriptToExecuteOnDocumentCreatedAsync(ShimScript.Source).ConfigureAwait(true);

        web.WebMessageReceived += OnWebMessage;
        web.NewWindowRequested += OnNewWindowRequested;
        web.NavigationStarting += OnNavigationStarting;

        // Best-effort UI push. Marshalled onto the UI thread because it is
        // called from the agent's background loops, and never allowed to throw:
        // pushing into a webview that is on its way out does, and in the Kotlin
        // that throw once killed the heartbeat loop for the life of the process.
        _core.OnEmit = (evt, payload) =>
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                var json = JsonSerializer.Serialize(payload);
                BeginInvoke(() =>
                {
                    try
                    {
                        _webView.CoreWebView2?.ExecuteScriptAsync(
                            $"window.dispatchAgentEvent && window.dispatchAgentEvent({Quote(evt)}, {json})");
                    }
                    catch (Exception exc) { _log.LogDebug(exc, "emit_failed event={Event}", evt); }
                });
            }
            catch (Exception exc) { _log.LogDebug(exc, "emit_failed event={Event}", evt); }
        };

        // The first one: OnResize has been firing since before there was a
        // CoreWebView2 to set a zoom on, so without this the window opens at
        // whatever zoom the control defaults to until it is next resized.
        FitDashboardToWindow();

        _webView.Source = new Uri(_server.BaseUrl);
        _log.LogInformation("webui_ready url={Url} dashboard=/", _server.BaseUrl);
    }

    /// <summary>
    /// One call from the page: <c>{ method, requestId, args }</c>, answered by
    /// resolving the Promise the shim is holding.
    ///
    /// Dispatched off the UI thread, because a bridge call may do blocking
    /// network I/O and the message arrives on the UI thread - running it inline
    /// would freeze the window for the call's duration.
    /// </summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string raw;
        try { raw = args.TryGetWebMessageAsString(); }
        catch (Exception) { return; }

        _ = Task.Run(async () =>
        {
            string requestId = "";
            try
            {
                using var message = JsonDocument.Parse(raw);
                var root = message.RootElement;
                requestId = root.GetProperty("requestId").GetString() ?? "";
                var method = root.GetProperty("method").GetString() ?? "";
                var argsJson = root.TryGetProperty("args", out var a) ? a.GetRawText() : "[]";

                var result = await _bridge.InvokeAsync(method, argsJson).ConfigureAwait(false);
                Resolve(requestId, JsonSerializer.Serialize(result), isError: false);
            }
            catch (Exception exc)
            {
                _log.LogWarning(exc, "bridge_call_failed");
                if (requestId.Length > 0)
                {
                    Resolve(requestId, JsonSerializer.Serialize(exc.Message), isError: true);
                }
            }
        });
    }

    private void Resolve(string requestId, string resultJson, bool isError)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    _webView.CoreWebView2?.ExecuteScriptAsync(
                        $"window.__printlyResolve({Quote(requestId)}, {Quote(resultJson)}, {(isError ? "true" : "false")})");
                }
                catch (Exception exc) { _log.LogDebug(exc, "bridge_resolve_failed"); }
            });
        }
        catch (Exception exc) { _log.LogDebug(exc, "bridge_resolve_failed"); }
    }

    /// <summary>
    /// target="_blank" and window.open would otherwise open nothing at all - an
    /// invoice or a report that silently did nothing when clicked. Handed to the
    /// system browser instead, which is where an external link belongs.
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        OpenInSystemBrowser(args.Uri);
    }

    /// <summary>
    /// Restricted to http(s) on purpose: asking Windows to open a file: or a
    /// registered custom scheme is a way to launch whatever is associated with
    /// it, and the URLs reaching here come from pages this app has just decided
    /// it does not trust.
    /// </summary>
    private void OpenInSystemBrowser(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme is not ("http" or "https")) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = parsed.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception exc) { _log.LogWarning(exc, "open_external_failed"); }
    }

    /// <summary>
    /// Refuses to navigate anywhere but the local UI.
    ///
    /// The dashboard previews customer-uploaded documents, and a document that
    /// can reach top.location navigates this window anywhere it likes. The shim
    /// is injected on every document, so a page that got here would be handed
    /// the bridge - which is the agent: sign-in, pairing, printing. Blocking the
    /// navigation itself is the belt to that brace, and it also means such a
    /// page cannot phish the shopkeeper for the password they are used to
    /// typing in this window.
    /// </summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme == "http"
            && uri.Host == "127.0.0.1"
            && uri.Port == _server.Port)
        {
            return;
        }

        args.Cancel = true;
        _log.LogWarning("external_navigation_blocked url={Url}", args.Uri);
        OpenInSystemBrowser(args.Uri);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
}
