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
        Width = 1280;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;

        Controls.Add(_webView);
        _ = StartWebViewAsync();
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
