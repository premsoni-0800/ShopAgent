package com.printly.agent.ui

import com.printly.agent.core.AgentCore
import com.printly.agent.core.loadSettings
import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import javafx.application.Application
import javafx.application.Platform
import javafx.concurrent.Worker
import javafx.scene.Scene
import javafx.scene.control.Alert
import javafx.scene.control.ButtonType
import javafx.scene.web.WebEngine
import javafx.scene.web.WebView
import javafx.stage.Stage
import netscape.javascript.JSObject
import java.awt.Desktop
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.URI
import java.nio.file.Files
import java.util.concurrent.Executors
import java.util.logging.Level
import java.util.logging.Logger

/**
 * The desktop shell: one Windows app that is both the shop dashboard and the
 * thing that drives the printers.
 *
 * The shopkeeper installs a single MSI and gets a single icon. Behind the
 * window, [AgentCore]'s loops run exactly as before - claiming jobs, printing
 * them, reporting outcomes - whether or not anyone is looking at the UI, which
 * is what makes unattended printing unattended.
 *
 * Two bundles share the one local origin:
 *
 *  - `/` - the full shop dashboard (orders, print jobs, products, reports,
 *    settings), built from the `printlypartner` repo and vendored under
 *    `src/main/resources/dashboard/`. It talks to the real backend over HTTP
 *    like any browser would; [proxyToBackend] is what lets it.
 *    The setup this machine needs - signing this PC in, pairing it, and storing
 *    the credential in Windows Credential Manager - is a view inside that same
 *    bundle, reached from the left nav. It used to be a second bundle served at
 *    `/agent` from `src/main/resources/webui/`; the dashboard absorbed it, that
 *    route went unvisited, and both have now been removed. See [JsBridge] and
 *    [SHIM_SCRIPT], which reproduce `window.pywebview` so the bundle runs
 *    unmodified.
 *
 * Served over `http://127.0.0.1` rather than as a `jar:`/classpath URL for a
 * reason inherited from the Python agent: Chromium-based webviews are
 * unreliable about executing a dynamically inserted third-party
 * `<script src="https://...">` (the MSG91 OTP widget) from a non-http origin.
 * A real `http://` origin, even a local one, sidesteps that entirely.
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

        // Without a user data directory JavaFX keeps local storage in memory
        // only, and the dashboard keeps its whole session there
        // (printlyAccessToken_v1 and friends). Left unset, the shopkeeper
        // would be signed out every single time they close the app - on the
        // one machine that is meant to sit logged in at the counter all day.
        // Deliberately alongside the agent's other state rather than in a
        // temp dir, so it survives for exactly as long as the install does.
        runCatching {
            val webViewData = core.settings.appDataDir.resolve("webview")
            Files.createDirectories(webViewData)
            engine.userDataDirectory = webViewData.toFile()
        }.onFailure { log.log(Level.WARNING, "webview_user_data_dir_failed", it) }

        // A WebEngine with no handlers set does not merely skip these dialogs -
        // it answers them. alert() is dropped on the floor and confirm()
        // returns *false*, so every button in the dashboard guarded by
        // `if (!confirm(...)) return` silently does nothing here while working
        // perfectly in a browser. That was four dead buttons, logout included,
        // and an invisible "could not send that to the printer" message.
        //
        // Modal against the app window, because the caller is a synchronous
        // JavaScript confirm(): it is already blocking the FX thread and needs
        // an answer before it can return.
        engine.setOnAlert { event ->
            Alert(Alert.AlertType.INFORMATION, event.data ?: "", ButtonType.OK).apply {
                initOwner(primaryStage)
                headerText = null
                title = "Printly Partner"
            }.showAndWait()
        }
        engine.setConfirmHandler { message ->
            Alert(Alert.AlertType.CONFIRMATION, message ?: "", ButtonType.OK, ButtonType.CANCEL).apply {
                initOwner(primaryStage)
                headerText = null
                title = "Printly Partner"
            }.showAndWait().orElse(ButtonType.CANCEL) == ButtonType.OK
        }

        val bridge = JsBridge(core) { script -> engine.executeScript(script) }
        val uiPort = webUiServer.address.port

        // The bridge is the whole agent: signInPassword, adoptSession, print
        // control. It used to be handed to whatever finished loading, which is
        // only safe while nothing but our own bundles can ever load - and that
        // was not true. The dashboard previews customer-uploaded documents in
        // an iframe, and a document that can reach `top.location` navigates
        // this window anywhere it likes; the next SUCCEEDED would then have
        // handed that page the bridge.
        //
        // Now the origin is checked first, so even a successful navigation
        // away gets an ordinary browser window with no agent in it.
        engine.loadWorker.stateProperty().addListener { _, _, state ->
            if (state == Worker.State.SUCCEEDED) {
                if (!isLocalUi(engine.location, uiPort)) {
                    log.warning("bridge_withheld location=${engine.location}")
                    return@addListener
                }
                try {
                    val window = engine.executeScript("window") as JSObject
                    window.setMember("javaBridge", bridge)
                    engine.executeScript(SHIM_SCRIPT)
                } catch (exc: Exception) {
                    log.log(Level.SEVERE, "bridge_injection_failed", exc)
                }
            }
        }

        // Belt to that brace: refuse the navigation itself rather than only
        // withholding the bridge afterwards. A page that cannot load cannot
        // phish the shopkeeper for the password they are used to typing here
        // either, which withholding the bridge alone would not prevent.
        engine.locationProperty().addListener { _, previous, next ->
            if (isLocalUi(next, uiPort)) return@addListener
            log.warning("external_navigation_blocked url=$next")
            val back = previous?.takeIf { isLocalUi(it, uiPort) } ?: "http://127.0.0.1:$uiPort/"
            Platform.runLater {
                engine.loadWorker.cancel()
                engine.load(back)
                openInSystemBrowser(next)
            }
        }

        // target="_blank" and window.open returned null here, so every such
        // link was simply dead - an invoice or a report that opened nothing.
        // They now go to the real browser, which is also where anything
        // outside this app belongs.
        engine.setCreatePopupHandler { _ ->
            val popup = WebEngine()
            popup.locationProperty().addListener { _, _, url ->
                if (!url.isNullOrBlank() && url != "about:blank") {
                    openInSystemBrowser(url)
                    Platform.runLater { popup.load(null) }
                }
            }
            popup
        }

        // Best-effort UI push - same "push/SSE is a hint, always refetch
        // authoritative state" pattern the rest of Printly's clients follow
        // (see PRINTLY_ARCHITECTURE.md); the dashboard's own `usePushData` hook
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
        // Logged because the port is chosen by the OS at each launch: without
        // this there is no way to reach the UI from outside the window, which
        // is exactly what support and diagnostics need.
        log.info("webui_ready url=http://127.0.0.1:$port/ dashboard=/ agentSetup=/agent")
        engine.load("http://127.0.0.1:$port/")

        primaryStage.title = "Printly Partner"
        // The icon on the running window and in the taskbar, which is separate
        // from the one jpackage stamps on the installed shortcut - a shop PC
        // shows this one all day, and without it the window wears the stock
        // Java mark. Best-effort: a missing or unreadable resource is not a
        // reason to refuse to open the app.
        runCatching {
            PrintlyAgentApp::class.java.getResourceAsStream("/printly-icon.png")?.use { stream ->
                primaryStage.icons.add(javafx.scene.image.Image(stream))
            }
        }.onFailure { log.fine("window_icon_unavailable: $it") }
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

    /** The local origin both bundles and the API proxy are served from - see the class doc for why it is http rather than a classpath URL. */
    private fun startWebUiServer(): HttpServer {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        // Executor with real threads: the default runs handlers on the single
        // dispatch thread, which an SSE stream would occupy forever - the
        // orders feed alone would wedge the whole UI.
        server.executor = Executors.newCachedThreadPool { runnable ->
            Thread(runnable, "printly-webui").apply { isDaemon = true }
        }
        val port = server.address.port
        server.createContext("/api") { exchange -> ifAddressedLocally(exchange, port) { proxyToBackend(exchange) } }
        server.createContext("/actuator") { exchange -> ifAddressedLocally(exchange, port) { proxyToBackend(exchange) } }
        server.createContext("/") { exchange -> ifAddressedLocally(exchange, port) { serveStatic(exchange) } }
        server.start()
        return server
    }

    /**
     * Rejects anything not addressed to this server by name.
     *
     * Binding to 127.0.0.1 keeps other machines out, but not other *pages*: a
     * site the shopkeeper happens to visit can point its own hostname at
     * 127.0.0.1 (DNS rebinding) and then talk to this server as same-origin,
     * reading the replies - which here means the dashboard and a proxy that
     * forwards to the real backend. Such a request still carries the attacker's
     * hostname in Host, so checking it is what closes the door.
     *
     * A missing Host is allowed through: HTTP/1.0 clients and some local tools
     * omit it, and no browser ever does.
     */
    private fun ifAddressedLocally(exchange: HttpExchange, port: Int, handler: () -> Unit) {
        val host = exchange.requestHeaders.getFirst("Host")
        if (host != null && host != "127.0.0.1:$port" && host != "localhost:$port") {
            log.warning("rejected_foreign_host host=$host path=${exchange.requestURI.path}")
            runCatching {
                exchange.sendResponseHeaders(403, -1)
                exchange.close()
            }
            return
        }
        handler()
    }

    /**
     * Serves two bundles from one origin: the shop dashboard at `/`, and this
     * agent's own setup UI at `/agent`.
     *
     * They can share `/assets` safely because both are Vite builds with
     * content-hashed filenames, so a name in one can never collide with a name
     * in the other - the fallback lookup is unambiguous rather than lucky.
     *
     * Anything unrecognised falls back to the dashboard's `index.html` rather
     * than 404ing, the standard single-page-app rule: a client-side route is a
     * path this server has never heard of and must not treat as missing.
     */
    private fun serveStatic(exchange: HttpExchange) {
        try {
            val path = exchange.requestURI.path
            val resolved = when {
                path == "/" || path.isEmpty() -> "/dashboard/index.html"
                else -> null
            }

            val bytes = resolved?.let { readResource(it) }
                ?: readResource("/dashboard$path")
                // SPA fallback - but never for a file request, where a 404 is
                // the honest answer and handing back HTML would surface as a
                // baffling "unexpected token <" in the console instead.
                ?: if (path.substringAfterLast('/').contains('.')) null else readResource("/dashboard/index.html")

            if (bytes == null) {
                exchange.sendResponseHeaders(404, -1)
                return
            }

            val typePath = resolved ?: path
            exchange.responseHeaders.set("Content-Type", contentTypeFor(typePath))
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        } catch (exc: Exception) {
            log.log(Level.WARNING, "webui_server_request_failed", exc)
            runCatching { exchange.sendResponseHeaders(500, -1) }
        } finally {
            exchange.close()
        }
    }

    /**
     * Reads a bundled file. Refuses any path with a `..` segment in it.
     *
     * The name here is built from the request URI, and the classloader does not
     * normalise the result: packaged as a jar the lookup simply misses, but run
     * from a directory - `./gradlew run`, and so every developer machine - the
     * traversal resolves and serves whatever it lands on.
     */
    private fun readResource(path: String): ByteArray? {
        if (path.split('/').any { it == ".." }) {
            log.warning("rejected_traversal path=$path")
            return null
        }
        return javaClass.getResourceAsStream(path)?.use { it.readBytes() }
    }

    /**
     * Whether a URL is this app's own UI, compared by parsed scheme/host/port
     * rather than by prefix: `http://127.0.0.1:59249@evil.com/` and
     * `http://127.0.0.1:59249.evil.com/` both start with the local origin as
     * text, and neither is it.
     *
     * Blank and `about:blank` count as local - they are what the engine reports
     * before the first load, and treating the startup state as foreign would
     * withhold the bridge from the real UI.
     */
    private fun isLocalUi(location: String?, port: Int): Boolean {
        if (location.isNullOrBlank() || location == "about:blank") return true
        return runCatching {
            val uri = URI(location)
            uri.scheme == "http" && uri.host == "127.0.0.1" && uri.port == port
        }.getOrDefault(false)
    }

    /**
     * Hands a URL to the system browser. Restricted to http(s) on purpose:
     * `browse()` on a `file:` or a registered custom scheme asks Windows to
     * launch whatever is associated with it, and the URLs reaching here come
     * from pages this app has just decided it does not trust.
     */
    private fun openInSystemBrowser(url: String?) {
        val target = url?.takeIf { it.startsWith("http://") || it.startsWith("https://") } ?: return
        runCatching {
            if (Desktop.isDesktopSupported() && Desktop.getDesktop().isSupported(Desktop.Action.BROWSE)) {
                Desktop.getDesktop().browse(URI(target))
            }
        }.onFailure { log.log(Level.WARNING, "open_external_failed url=$target", it) }
    }

    /**
     * Forwards `/api` and `/actuator` to the real backend.
     *
     * The dashboard is built to call the API same-origin, and here that origin
     * is `http://127.0.0.1:<random port>`. Calling the backend directly from
     * the page would therefore be a cross-origin request from an origin that
     * changes every launch, and the backend's CORS_ALLOWED_ORIGINS is an
     * explicit list that refuses `*` - so it could never be allow-listed.
     *
     * Proxying sidesteps it the same way the dashboard's own Vite dev server
     * does, and for the same reason: with the Origin header dropped this is a
     * plain server-to-server call, which the backend does not CORS-check at
     * all.
     *
     * Streams both ways rather than buffering. The orders feed is
     * `text/event-stream` and never ends, so reading it into a byte array
     * before responding would hang that request forever and deliver nothing.
     */
    private fun proxyToBackend(exchange: HttpExchange) {
        try {
            val target = URI(core.settings.backendBaseUrl + exchange.requestURI.rawPath +
                (exchange.requestURI.rawQuery?.let { "?$it" } ?: ""))
            val connection = (target.toURL().openConnection() as HttpURLConnection).apply {
                requestMethod = exchange.requestMethod
                instanceFollowRedirects = false
                connectTimeout = 30_000
                // No read timeout: an idle SSE stream is healthy, not stalled.
                readTimeout = 0
                doInput = true
            }

            val forwarded = mutableListOf<String>()
            exchange.requestHeaders.forEach { (name, values) ->
                // Origin and Host belong to the local server, not the backend;
                // forwarding Origin is exactly what would re-introduce the CORS
                // rejection this proxy exists to avoid.
                if (!name.equals("Origin", true) && !name.equals("Host", true) &&
                    !name.equals("Connection", true) && !name.equals("Content-Length", true)
                ) {
                    forwarded += name
                    values.forEach { connection.addRequestProperty(name, it) }
                }
            }

            if (exchange.requestMethod !in setOf("GET", "HEAD")) {
                // Buffered and sent with a known length rather than streamed
                // chunked. An API request body is a few hundred bytes, so there
                // is nothing to gain by streaming it - and streaming costs the
                // ability to replay it. HttpURLConnection cannot re-send a
                // streamed body, so it answers anything that needs the request
                // repeated - an authentication challenge above all - by
                // throwing HttpRetryException instead of returning the status.
                // That landed in the catch below, which replied 502 with an
                // empty body, and an empty body is exactly the case the
                // dashboard can say nothing useful about: "Mark as Collected"
                // came back as "Something went wrong", only in the desktop app,
                // because only the desktop app has this proxy in front of it.
                val body = exchange.requestBody.use { it.readBytes() }
                connection.doOutput = true
                // No streaming mode at all. In streaming mode - fixed length or
                // chunked - HttpURLConnection has already committed the request
                // by the time the response arrives, and it cannot then produce
                // an error stream: a 4xx comes back with the status intact and
                // getErrorStream() empty. Measured against the live backend, the
                // same 401 returned 165 bytes of JSON through a GET and nothing
                // at all through a POST. The dashboard shows the server's own
                // sentence when there is one, so an emptied error body is
                // exactly what surfaced as "Something went wrong", and only in
                // the desktop app, because only it proxies.
                //
                // Letting the connection buffer instead costs one copy of a few
                // hundred bytes and gets the body back.
                connection.outputStream.use { it.write(body) }
            }

            val status = connection.responseCode
            // A refusal is not an exception, so it used to pass through without
            // a trace - and a shop reporting "the button does nothing" left
            // nothing behind to look at. Reads are noisy and uninteresting;
            // a write the server refused is the thing worth recording.
            if (status >= 400 && !exchange.requestURI.path.endsWith("/events")) {
                // Header NAMES only - never the values. Whether Authorization
                // survived the hop is the whole question; what it contains is
                // not something to write to a log file on a shop counter.
                // Gathered while the headers were being set: asking the
                // connection afterwards throws "Already connected".
                val sent = forwarded.sorted().joinToString(",")
                val came = exchange.requestHeaders.keys.sorted().joinToString(",")
                log.warning(
                    "api_proxy_refused method=${exchange.requestMethod} " +
                        "path=${exchange.requestURI.path} status=$status " +
                        "requestId=${connection.getHeaderField("x-request-id") ?: "-"} " +
                        "from_page=[$came] forwarded=[$sent]",
                )
            }
            connection.headerFields.forEach { (name, values) ->
                if (name != null && !name.equals("Content-Length", true) &&
                    !name.equals("Transfer-Encoding", true) && !name.equals("Connection", true)
                ) {
                    values.forEach { exchange.responseHeaders.add(name, it) }
                }
            }

            // 0 means "chunked, length unknown" - required for a stream whose
            // length genuinely is unknown.
            exchange.sendResponseHeaders(status, 0)
            val source = if (status >= 400) connection.errorStream else connection.inputStream
            source?.use { input ->
                exchange.responseBody.use { output ->
                    val buffer = ByteArray(8 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read == -1) break
                        output.write(buffer, 0, read)
                        // Flush per chunk or an SSE event sits in the buffer
                        // until enough bytes accumulate to justify a write.
                        output.flush()
                    }
                }
            }
        } catch (exc: Exception) {
            // A page closing an SSE stream lands here routinely and is not worth
            // shouting about. Anything else is a request the shop made and did
            // not get an answer to, and hiding those at FINE is how a broken
            // button stayed unexplained: the log said nothing and the reply had
            // no body to explain itself either.
            val streaming = exchange.requestURI.path.endsWith("/events")
            if (streaming) {
                log.log(Level.FINE, "api_proxy_stream_closed path=${exchange.requestURI.path}", exc)
            } else {
                log.log(Level.WARNING, "api_proxy_failed path=${exchange.requestURI.path}", exc)
            }

            // With a reason in it. The dashboard shows the server's own sentence
            // when there is one, and a bodyless 502 leaves it nothing to show
            // but a shrug.
            runCatching {
                val reason = (exc.message ?: exc.javaClass.simpleName).take(300)
                val payload = core.api.mapper.writeValueAsBytes(
                    mapOf(
                        "error" to mapOf(
                            "code" to "AGENT_PROXY_FAILED",
                            "message" to "The desktop app could not reach the Printly server: $reason",
                        ),
                    ),
                )
                exchange.responseHeaders.add("Content-Type", "application/json; charset=utf-8")
                exchange.sendResponseHeaders(502, payload.size.toLong())
                exchange.responseBody.use { it.write(payload) }
            }
        } finally {
            exchange.close()
        }
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
              var methods = ['adopt_session','sign_in_password','sign_in_otp','set_password','sign_out','status',
                'get_config','unresolved_jobs','resolve_print_job','list_orders','list_printers','set_auto_print'];
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
