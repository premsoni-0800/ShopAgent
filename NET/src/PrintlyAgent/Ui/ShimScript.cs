using System.Runtime.Versioning;

namespace PrintlyAgent.Ui;

/// <summary>
/// Reproduces <c>window.pywebview.api</c> as a Promise-returning proxy over
/// <see cref="JsBridge"/>.
///
/// Port of SHIM_SCRIPT in ui/PrintlyAgentApp.kt. Both vendored bundles were
/// written against pywebview's own JS binding, where every
/// <c>window.pywebview.api.xxx()</c> call returns a Promise resolved once the
/// native call finishes off the UI thread. Reproducing that exact contract is
/// what let both bundles run here completely unmodified.
///
/// <para>
/// The method list is generated from <see cref="JsBridge.Methods"/> rather than
/// written out again. In the Kotlin these were two hand-maintained lists, and
/// they drifted: a method was added to the dispatcher and not to the shim, so it
/// was `undefined` on the page, the call threw a TypeError, the page's wrapper
/// swallowed it and returned null, and the feature did nothing at all - silently,
/// in both directions. Generating one from the other removes the class of bug
/// rather than testing for it, though the test exists too.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShimScript
{
    public static string Source =>
        """
        (function() {
          if (window.pywebview) return;
          var pending = {};
          window.__printlyResolve = function(requestId, resultJson, isError) {
            var entry = pending[requestId];
            if (!entry) return;
            delete pending[requestId];
            if (isError) { entry.reject(new Error(JSON.parse(resultJson))); }
            else { entry.resolve(JSON.parse(resultJson)); }
          };
          function call(method) {
            var callArgs = Array.prototype.slice.call(arguments, 1);
            return new Promise(function(resolve, reject) {
              var requestId = 'r' + Date.now() + Math.random().toString(36).slice(2);
              pending[requestId] = { resolve: resolve, reject: reject };
              window.chrome.webview.postMessage(
                JSON.stringify({ method: method, requestId: requestId, args: callArgs }));
            });
          }
          var methods = __METHODS__;
          var api = {};
          methods.forEach(function(m) {
            api[m] = function() { return call.apply(null, [m].concat(Array.prototype.slice.call(arguments))); };
          });
          window.pywebview = { api: api };
          window.dispatchEvent(new Event('pywebviewready'));
        })();
        """.Replace("__METHODS__", MethodsLiteral(), StringComparison.Ordinal);

    /// <summary>The bridge's method list as a JavaScript array literal.</summary>
    internal static string MethodsLiteral() =>
        "[" + string.Join(",", JsBridge.Methods.Select(m => $"'{m}'")) + "]";
}
