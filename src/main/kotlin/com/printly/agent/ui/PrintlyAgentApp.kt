package com.printly.agent.ui

import com.printly.agent.core.AgentCore
import com.printly.agent.core.loadSettings
import com.sun.net.httpserver.HttpServer
import javafx.application.Application
import javafx.application.Platform
import javafx.concurrent.Worker
import javafx.scene.Scene
import javafx.scene.web.WebView
import javafx.stage.Stage
import netscape.javascript.JSObject
import java.net.InetSocketAddress
import java.util.logging.Level
import java.util.logging.Logger

/**
 * The desktop shell - direct equivalent of the Python agent's `main.py` +
 * pywebview window, except the page is hosted in a JavaFX `WebView` instead
 * of a native OS webview. The existing React `webui/` (bundled unchanged
 * under `src/main/resources/webui/`) runs inside it exactly as built for the
 * Python agent - see [JsBridge] and [SHIM_SCRIPT] for how `window.pywebview`
 * is reproduced so `webui/src/lib/bridge.js` needed zero changes.
 */
class PrintlyAgentApp : Application() {

    private val log = Logger.getLogger(javaClass.name)
    private lateinit var core: AgentCore
    private lateinit var webUiServer: HttpServer

    override fun start(primaryStage: Stage) {
        core = AgentCore(loadSettings())
        // Attempts to resume the background loops immediately if this PC was
        // already signed in and paired from a previous run - AgentCore.start
        // itself is a no-op until both are true, same as the Python agent's
        // main.py calling this unconditionally at launch.
        core.start()

        webUiServer = startWebUiServer()

        val webView = WebView()
        val engine = webView.engine
        engine.isJavaScriptEnabled = true

        val bridge = JsBridge(core) { script -> engine.executeScript(script) }

        engine.loadWorker.stateProperty().addListener { _, _, state ->
            if (state == Worker.State.SUCCEEDED) {
                try {
                    val window = engine.executeScript("window") as JSObject
                    window.setMember("javaBridge", bridge)
                    engine.executeScript(SHIM_SCRIPT)
                } catch (exc: Exception) {
                    log.log(Level.SEVERE, "bridge_injection_failed", exc)
                }
            }
        }

        // Best-effort UI push - same "push/SSE is a hint, always refetch
        // authoritative state" pattern the rest of Printly's clients follow
        // (see PRINTLY_ARCHITECTURE.md); webui's own `usePushData` hook
        // always refetches on this event rather than trusting a payload.
        core.onEmit = { event, payload ->
            Platform.runLater {
                try {
                    val payloadJson = core.api.mapper.writeValueAsString(payload)
                    engine.executeScript("window.dispatchAgentEvent && window.dispatchAgentEvent(${jsString(event)}, $payloadJson)")
                } catch (exc: Exception) {
                    log.log(Level.WARNING, "emit_failed event=$event", exc)
                }
            }
        }

        val port = webUiServer.address.port
        engine.load("http://127.0.0.1:$port/index.html")

        primaryStage.title = "Printly Print Agent"
        primaryStage.scene = Scene(webView, 1100.0, 720.0)
        primaryStage.setOnCloseRequest {
            core.stop()
            webUiServer.stop(0)
        }
        primaryStage.show()
    }

    override fun stop() {
        core.stop()
        if (::webUiServer.isInitialized) webUiServer.stop(0)
    }

    private fun jsString(s: String) = "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\""

    /**
     * Serves the bundled `webui/` over `http://127.0.0.1` instead of loading
     * it as a `jar:`/classpath resource URL directly - direct port of the
     * Python agent's `main.py::_start_webui_server` and its reasoning:
     * Chromium-based webviews are unreliable about executing a dynamically
     * inserted third-party `<script src="https://...">` (the MSG91 OTP
     * widget - see `webui/src/lib/msg91.js`) from a non-http origin. A real
     * `http://` origin, even a local one, sidesteps that entirely.
     */
    private fun startWebUiServer(): HttpServer {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            try {
                val path = exchange.requestURI.path.let { if (it == "/") "/index.html" else it }
                val resource = javaClass.getResourceAsStream("/webui$path")
                if (resource == null) {
                    exchange.sendResponseHeaders(404, -1)
                } else {
                    val bytes = resource.use { it.readBytes() }
                    exchange.responseHeaders.set("Content-Type", contentTypeFor(path))
                    exchange.sendResponseHeaders(200, bytes.size.toLong())
                    exchange.responseBody.use { it.write(bytes) }
                }
            } catch (exc: Exception) {
                log.log(Level.WARNING, "webui_server_request_failed", exc)
                runCatching { exchange.sendResponseHeaders(500, -1) }
            } finally {
                exchange.close()
            }
        }
        server.start()
        return server
    }

    private fun contentTypeFor(path: String): String = when {
        path.endsWith(".html") -> "text/html; charset=utf-8"
        path.endsWith(".js") -> "application/javascript; charset=utf-8"
        path.endsWith(".css") -> "text/css; charset=utf-8"
        path.endsWith(".svg") -> "image/svg+xml"
        path.endsWith(".png") -> "image/png"
        path.endsWith(".json") -> "application/json; charset=utf-8"
        path.endsWith(".woff2") -> "font/woff2"
        else -> "application/octet-stream"
    }

    companion object {
        /**
         * Reproduces `window.pywebview.api` as a Promise-returning proxy over
         * [JsBridge] - pywebview's own JS binding always returns a Promise
         * from every `window.pywebview.api.xxx()` call (resolved once the
         * Python call finishes, off the UI thread); this replicates that
         * exact contract so `bridge.js` runs completely unmodified. Injected
         * once, right after the page finishes loading.
         */
        private val SHIM_SCRIPT = """
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
                  window.javaBridge.invoke(method, requestId, JSON.stringify(callArgs));
                });
              }
              var methods = ['sign_in_password','sign_in_otp','set_password','sign_out','status','get_config',
                'unresolved_jobs','resolve_print_job','list_orders','list_printers','set_auto_print'];
              var api = {};
              methods.forEach(function(m) {
                api[m] = function() { return call.apply(null, [m].concat(Array.prototype.slice.call(arguments))); };
              });
              window.pywebview = { api: api };
              window.dispatchEvent(new Event('pywebviewready'));
            })();
        """.trimIndent()
    }
}

fun main(args: Array<String>) {
    Application.launch(PrintlyAgentApp::class.java, *args)
}
