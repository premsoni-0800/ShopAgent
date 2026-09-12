package com.printly.agent.ui

import com.fasterxml.jackson.module.kotlin.jacksonObjectMapper
import com.fasterxml.jackson.module.kotlin.readValue
import com.printly.agent.core.AgentCore
import com.printly.agent.core.Auth
import com.printly.agent.net.ApiError
import javafx.application.Platform
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.util.logging.Level
import java.util.logging.Logger

/**
 * The JS-callable bridge object - equivalent of the Python agent's
 * `bridge.py` `js_api`. Exposed to the page as `window.javaBridge`; the shim
 * script [PrintlyAgentApp] injects wraps every call into the same Promise-
 * returning `window.pywebview.api.*` shape `webui/`'s own `bridge.js`
 * already expects, so that file needed zero changes.
 *
 * Every call is dispatched onto [Dispatchers.IO] - a bridge call may do
 * blocking network I/O (see `AgentCore`), and the JavaFX WebView invokes
 * `window.javaBridge.invoke(...)` synchronously from script context, so
 * running it inline here would freeze the UI for the call's duration.
 */
class JsBridge(private val core: AgentCore, private val executeScript: (String) -> Unit) {

    private val log = Logger.getLogger(javaClass.name)
    private val mapper = jacksonObjectMapper()
    private val scope = CoroutineScope(Dispatchers.IO)

    /** Called from JS via the shim: `window.javaBridge.invoke(method, requestId, argsJson)`. */
    fun invoke(method: String, requestId: String, argsJson: String) {
        scope.launch {
            try {
                val args: List<Any?> = if (argsJson.isBlank()) emptyList() else mapper.readValue(argsJson)
                val result = dispatch(method, args)
                resolve(requestId, mapper.writeValueAsString(result), isError = false)
            } catch (exc: Exception) {
                log.log(Level.WARNING, "bridge_call_failed method=$method", exc)
                resolve(requestId, mapper.writeValueAsString(exc.message ?: "Something went wrong"), isError = true)
            }
        }
    }

    private fun resolve(requestId: String, resultJson: String, isError: Boolean) {
        // Re-encoding as a JSON string literal is also valid JS string
        // literal syntax - the simplest safe way to embed arbitrary content
        // (quotes, newlines, unicode) into an executeScript() call.
        val jsLiteral = mapper.writeValueAsString(resultJson)
        Platform.runLater {
            executeScript("window.__printlyResolve('$requestId', $jsLiteral, $isError)")
        }
    }

    @Suppress("UNCHECKED_CAST")
    private fun dispatch(method: String, args: List<Any?>): Any? = when (method) {
        "adopt_session" -> adoptSession(
            args[0] as String, args[1] as String, args[2] as String, args.getOrNull(3) as? String,
        )
        "sign_in_password" -> signInPassword(args[0] as String, args[1] as String)
        "sign_in_otp" -> signInOtp(args[0] as String)
        "set_password" -> ok { core.setPassword(args[0] as String) }
        "sign_out" -> { core.signOut(); mapOf("ok" to true) }
        "status" -> core.status()
        "get_config" -> mapOf("msg91WidgetId" to core.settings.msg91WidgetId)
        "unresolved_jobs" -> core.unresolvedJobs()
        "resolve_print_job" -> resolvePrintJob(args[0] as String, args[1] as Boolean, args.getOrNull(2) as? String)
        "list_orders" -> okList("orders") { core.listOrders() }
        "list_printers" -> okList("printers") { core.listPrinters() }
        "set_auto_print" -> ok { core.setAutoPrint(args[0] as Boolean) }
        else -> mapOf("ok" to false, "error" to "unknown bridge method: $method")
    }

    /**
     * The dashboard handing over the session it just signed in with, so this
     * machine pairs itself without anyone typing a code.
     *
     * Reports [Auth.AnotherMachinePairedError] by its code rather than as a
     * generic failure: it is the one outcome the page can act on, by telling
     * the owner which PC currently holds the registration.
     */
    private fun adoptSession(accessToken: String, refreshToken: String, shopId: String, shopName: String?): Map<String, Any?> = try {
        core.adoptOwnerSession(accessToken, refreshToken, shopId, shopName)
        mapOf("ok" to true, "status" to core.status())
    } catch (exc: Auth.AnotherMachinePairedError) {
        mapOf("ok" to false, "code" to "ANOTHER_MACHINE_PAIRED", "error" to exc.message)
    } catch (exc: ApiError) {
        mapOf("ok" to false, "code" to exc.code, "error" to exc.message)
    } catch (exc: Exception) {
        mapOf("ok" to false, "error" to (exc.message ?: "Something went wrong"))
    }

    private fun signInPassword(identifier: String, password: String): Map<String, Any?> = try {
        core.signInWithPassword(identifier, password)
        mapOf("ok" to true)
    } catch (exc: Auth.PasswordNotSetError) {
        mapOf("ok" to false, "code" to "PASSWORD_NOT_SET", "error" to exc.message)
    } catch (exc: Auth.NoOwnedShopError) {
        mapOf("ok" to false, "error" to exc.message)
    } catch (exc: Auth.AnotherMachinePairedError) {
        mapOf("ok" to false, "code" to "ANOTHER_MACHINE_PAIRED", "error" to exc.message)
    } catch (exc: ApiError) {
        mapOf("ok" to false, "code" to exc.code, "error" to exc.message)
    } catch (exc: Exception) {
        mapOf("ok" to false, "error" to (exc.message ?: "Something went wrong"))
    }

    private fun signInOtp(widgetAccessToken: String): Map<String, Any?> = try {
        core.signInWithOtp(widgetAccessToken)
        mapOf("ok" to true)
    } catch (exc: Auth.NoOwnedShopError) {
        mapOf("ok" to false, "error" to exc.message)
    } catch (exc: Exception) {
        mapOf("ok" to false, "error" to (exc.message ?: "Something went wrong"))
    }

    private fun resolvePrintJob(jobId: String, success: Boolean, note: String?): Map<String, Any?> = try {
        core.resolvePrintJob(jobId, success, note)
        mapOf("ok" to true)
    } catch (exc: ApiError) {
        mapOf("ok" to false, "code" to exc.code, "error" to exc.message)
    } catch (exc: Exception) {
        mapOf("ok" to false, "error" to (exc.message ?: "Something went wrong"))
    }

    private fun ok(block: () -> Unit): Map<String, Any?> = try {
        block(); mapOf("ok" to true)
    } catch (exc: Exception) {
        mapOf("ok" to false, "error" to (exc.message ?: "Something went wrong"))
    }

    private fun okList(key: String, block: () -> List<Map<String, Any?>>): Map<String, Any?> = try {
        mapOf("ok" to true, key to block())
    } catch (exc: Exception) {
        mapOf("ok" to false, "error" to (exc.message ?: "Something went wrong"))
    }
}
