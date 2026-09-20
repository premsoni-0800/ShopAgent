using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrintlyAgent.Core;
using PrintlyAgent.Net;
using PrintlyAgent.Printing;

namespace PrintlyAgent.Ui;

/// <summary>
/// The JS-callable bridge. Port of ui/JsBridge.kt.
///
/// Exposed to the page and wrapped by <see cref="ShimScript.Source"/> into the
/// same Promise-returning <c>window.pywebview.api.*</c> shape the vendored
/// bundles already expect, so neither bundle needed a single change.
///
/// Every call is dispatched off the UI thread - a bridge call may do blocking
/// network I/O, and the webview invokes it from script context, so running it
/// inline would freeze the window for the call's duration.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JsBridge
{
    /// <summary>
    /// The methods the page may call.
    ///
    /// This list is the bridge's actual surface and it exists in two places -
    /// here, and in the shim that builds window.pywebview.api. A method present
    /// in one and missing from the other is not a compile error in either
    /// language: it is `undefined` on the page, the call throws a TypeError, and
    /// the page's own wrapper catches it and returns null - which every caller
    /// reads as "the desktop could not answer". The feature simply does nothing
    /// and says nothing.
    ///
    /// So the two lists are generated from this one array rather than typed
    /// twice, and a test asserts the shim and the dispatcher agree.
    /// </summary>
    public static readonly IReadOnlyList<string> Methods = new[]
    {
        "adopt_session",
        "sign_in_password",
        "sign_in_otp",
        "set_password",
        "sign_out",
        "status",
        "get_config",
        "unresolved_jobs",
        "resolve_print_job",
        "list_orders",
        "list_printers",
        "set_auto_print",
        "open_external",
        "print_document",
        "list_held_files",
        "print_order_now",
        "list_local_printers",
        "get_printer_routing",
        "set_printer_routing",
    };

    private readonly ILogger _log;
    private readonly AgentCore _core;

    public JsBridge(ILogger log, AgentCore core)
    {
        _log = log;
        _core = core;
    }

    /// <summary>
    /// Runs one call. <paramref name="argsJson"/> is the JSON array the shim
    /// sent; the result is serialised back to the page.
    /// </summary>
    public async Task<object?> InvokeAsync(string method, string argsJson)
    {
        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "[]" : argsJson);
        var args = parsed.RootElement.EnumerateArray().ToList();

        string Str(int i) => args[i].GetString() ?? "";
        string? StrOrNull(int i) => i < args.Count && args[i].ValueKind == JsonValueKind.String ? args[i].GetString() : null;
        bool Bool(int i) => args[i].ValueKind == JsonValueKind.True;

        return method switch
        {
            "adopt_session" => await AdoptSessionAsync(Str(0), Str(1), Str(2), StrOrNull(3)).ConfigureAwait(false),
            "sign_in_password" => await SignInPasswordAsync(Str(0), Str(1)).ConfigureAwait(false),
            "sign_in_otp" => await SignInOtpAsync(Str(0)).ConfigureAwait(false),
            "set_password" => await OkAsync(() => _core.SetPasswordAsync(Str(0))).ConfigureAwait(false),
            "sign_out" => SignOut(),
            "status" => _core.Status(),
            "get_config" => new Dictionary<string, object?> { ["msg91WidgetId"] = _core.Settings.Msg91WidgetId },
            "unresolved_jobs" => _core.UnresolvedJobs(),
            "resolve_print_job" => await ResolvePrintJobAsync(Str(0), Bool(1), StrOrNull(2)).ConfigureAwait(false),
            "list_orders" => await OkListAsync("orders", () => _core.ListOrdersAsync()).ConfigureAwait(false),
            "list_printers" => await OkListAsync("printers", () => _core.ListPrintersAsync()).ConfigureAwait(false),
            "set_auto_print" => await OkAsync(() => _core.SetAutoPrintAsync(Bool(0))).ConfigureAwait(false),
            "open_external" => OpenExternal(Str(0)),
            "print_document" => await ManualPrint.PrintWithDialogAsync(Str(0), StrOrNull(1), _log).ConfigureAwait(false),
            "list_held_files" => await ListHeldFilesAsync().ConfigureAwait(false),
            "print_order_now" => await PrintOrderNowAsync(Str(0)).ConfigureAwait(false),
            "list_local_printers" => ListLocalPrinters(),
            "get_printer_routing" => GetPrinterRouting(),
            "set_printer_routing" => SetPrinterRouting(StrOrNull(0), StrOrNull(1)),
            _ => new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"unknown bridge method: {method}" },
        };
    }

    private object SignOut()
    {
        _core.SignOut();
        return new Dictionary<string, object?> { ["ok"] = true };
    }

    /// <summary>
    /// The orders this machine is holding files for, for the Files screen.
    /// </summary>
    private Task<object> ListHeldFilesAsync() =>
        OkListAsync("orders", () => _core.HeldOrdersAsync());

    /// <summary>
    /// Prints everything held for one person, now - the Print button on a Files
    /// card.
    ///
    /// Reports how many jobs it actually started rather than a bare success, so
    /// the page can say "nothing to print" instead of appearing to work and then
    /// leaving the counter waiting on a printer that was never going to run.
    /// </summary>
    private async Task<object> PrintOrderNowAsync(string orderUuid)
    {
        try
        {
            var started = await Task.Run(() => _core.PrintOrderNow(orderUuid)).ConfigureAwait(false);
            return new Dictionary<string, object?> { ["ok"] = true, ["started"] = started };
        }
        catch (Exception exc)
        {
            return Failure((exc as ApiError)?.Code, exc.Message);
        }
    }

    /// <summary>
    /// This PC's own printers, by the names Windows knows them by.
    ///
    /// Distinct from <c>list_printers</c>, which asks the backend what this shop
    /// has registered. The routing settings have to offer the devices actually
    /// attached to this machine, because those are the names the routing is
    /// stored against and the ones the spooler will be handed.
    /// </summary>
    private Task<object> ListLocalPrinters() =>
        OkListAsync("printers", () => _core.ListLocalPrintersAsync());

    private object GetPrinterRouting()
    {
        var routing = _core.GetPrinterRouting();
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["colour"] = routing.Colour,
            ["bw"] = routing.BlackAndWhite,
            // Whether the agent chose these or the shop did, so the screen can
            // say "chosen automatically" rather than presenting the agent's own
            // answer back as though somebody had set it. Additive: a dashboard
            // bundle that does not read them behaves exactly as before.
            ["colourAuto"] = routing.ColourAuto,
            ["bwAuto"] = routing.BlackAndWhiteAuto,
        };
    }

    private object SetPrinterRouting(string? colour, string? blackAndWhite)
    {
        try
        {
            _core.SetPrinterRouting(colour, blackAndWhite);
            return new Dictionary<string, object?> { ["ok"] = true };
        }
        catch (Exception exc)
        {
            return Failure(null, exc.Message);
        }
    }

    /// <summary>
    /// The dashboard handing over the session it just signed in with, so this
    /// machine pairs itself without anyone typing a code.
    ///
    /// Reports AnotherMachinePairedError by its code rather than as a generic
    /// failure: it is the one outcome the page can act on, by telling the owner
    /// which PC currently holds the registration.
    /// </summary>
    private async Task<object> AdoptSessionAsync(
        string accessToken, string refreshToken, string shopId, string? shopName)
    {
        try
        {
            await _core.AdoptOwnerSessionAsync(accessToken, refreshToken, shopId, shopName).ConfigureAwait(false);
            return new Dictionary<string, object?> { ["ok"] = true, ["status"] = _core.Status() };
        }
        catch (AnotherMachinePairedError exc)
        {
            _log.LogWarning("adopt_session_failed reason=ANOTHER_MACHINE_PAIRED {Message}", exc.Message);
            return Failure("ANOTHER_MACHINE_PAIRED", exc.Message);
        }
        catch (ApiError exc)
        {
            _log.LogWarning(exc, "adopt_session_failed code={Code}", exc.Code);
            return Failure(exc.Code, exc.Message);
        }
        catch (Exception exc)
        {
            _log.LogWarning(exc, "adopt_session_failed");
            return Failure(null, exc.Message);
        }
    }

    private async Task<object> SignInPasswordAsync(string identifier, string password)
    {
        try
        {
            await _core.SignInWithPasswordAsync(identifier, password).ConfigureAwait(false);
            return new Dictionary<string, object?> { ["ok"] = true };
        }
        catch (PasswordNotSetError exc) { return Failure("PASSWORD_NOT_SET", exc.Message); }
        catch (NoOwnedShopError exc) { return Failure(null, exc.Message); }
        catch (AnotherMachinePairedError exc) { return Failure("ANOTHER_MACHINE_PAIRED", exc.Message); }
        catch (ApiError exc) { return Failure(exc.Code, exc.Message); }
        catch (Exception exc) { return Failure(null, exc.Message); }
    }

    private async Task<object> SignInOtpAsync(string widgetAccessToken)
    {
        try
        {
            await _core.SignInWithOtpAsync(widgetAccessToken).ConfigureAwait(false);
            return new Dictionary<string, object?> { ["ok"] = true };
        }
        catch (NoOwnedShopError exc) { return Failure(null, exc.Message); }
        catch (Exception exc) { return Failure(null, exc.Message); }
    }

    private async Task<object> ResolvePrintJobAsync(string jobId, bool success, string? note)
    {
        try
        {
            await _core.ResolvePrintJobAsync(jobId, success, note).ConfigureAwait(false);
            return new Dictionary<string, object?> { ["ok"] = true };
        }
        catch (ApiError exc) { return Failure(exc.Code, exc.Message); }
        catch (Exception exc) { return Failure(null, exc.Message); }
    }

    /// <summary>
    /// Hands a document to whatever the shop's PC already opens it with.
    ///
    /// Restricted to http and https on purpose. The page hands over a link it
    /// got from the API, but this method can be reached by anything running in
    /// the webview, and opening a file: or a custom scheme is a way to launch
    /// things on this machine rather than to open a document.
    /// </summary>
    private object OpenExternal(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                return Failure(null, "Only web links can be opened");
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
            _log.LogInformation("opened_document_externally host={Host}", uri.Host);
            return new Dictionary<string, object?> { ["ok"] = true };
        }
        catch (Exception exc)
        {
            _log.LogWarning(exc, "open_external_failed");
            return Failure(null, exc.Message);
        }
    }

    private static Dictionary<string, object?> Failure(string? code, string message)
    {
        var result = new Dictionary<string, object?> { ["ok"] = false, ["error"] = message };
        if (code is not null) result["code"] = code;
        return result;
    }

    private static async Task<object> OkAsync(Func<Task> block)
    {
        try
        {
            await block().ConfigureAwait(false);
            return new Dictionary<string, object?> { ["ok"] = true };
        }
        catch (Exception exc)
        {
            return Failure((exc as ApiError)?.Code, exc.Message);
        }
    }

    private static async Task<object> OkListAsync<T>(string key, Func<Task<T>> block)
    {
        try
        {
            return new Dictionary<string, object?> { ["ok"] = true, [key] = await block().ConfigureAwait(false) };
        }
        catch (Exception exc)
        {
            return Failure((exc as ApiError)?.Code, exc.Message);
        }
    }
}
