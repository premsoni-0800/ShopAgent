package com.printly.agent.core

import com.printly.agent.credentials.CredentialStore
import com.printly.agent.db.Database
import com.printly.agent.jobs.JobContext
import com.printly.agent.jobs.handleJobReference
import com.printly.agent.jobs.processDueScheduledJobs
import com.printly.agent.net.ApiError
import com.printly.agent.net.OrderEventsClient
import com.printly.agent.net.PrintJobSseClient
import com.printly.agent.net.PrintlyApiClient
import com.printly.agent.printers.discoverPrinters
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Duration
import java.time.Instant
import java.time.format.DateTimeParseException
import java.util.logging.Level
import java.util.logging.Logger

/**
 * Wires the pieces together: heartbeat, printer sync, and the SSE job
 * pipeline - direct port of the Python agent's `agent_core.py`. Runs its
 * background loops on a supervisor scope; the UI layer (see the `ui`
 * package) never blocks on network I/O because of it.
 */
class AgentCore(val settings: Settings) {

    private val log = Logger.getLogger(javaClass.name)

    val api = PrintlyApiClient(settings.backendBaseUrl)
    val db = Database(settings.dbPath)

    @Volatile var ownerSession: CredentialStore.OwnerSession? = CredentialStore.loadOwnerSession()
        private set

    @Volatile var agentCredential: CredentialStore.AgentCredential? = CredentialStore.loadAgentCredential()
        private set

    @Volatile var autoPrintEnabled: Boolean = false
        private set

    @Volatile var lastHeartbeatOk: Boolean = false
        private set

    /** Best-effort UI push - the JavaFX host wires this to `webEngine.executeScript(...)`. No-op headless. */
    var onEmit: (event: String, payload: Map<String, Any?>) -> Unit = { _, _ -> }

    private val supervisorJob = SupervisorJob()
    private val scope = CoroutineScope(Dispatchers.Default + supervisorJob)
    private var jobs: List<Job> = emptyList()

    private val sse = PrintJobSseClient(api, { agentCredential }, ::onJobReference)
    private val orderEvents = OrderEventsClient(api, { ownerSession }, ::onOrdersChanged, ::refreshOwnerSession)

    // How long before a scheduled order's slot the agent should actually
    // print it - printing right when the job is created would mean paper
    // sitting at the counter well before the student is due.
    private val scheduledPrintLeadTime: Duration = Duration.ofMinutes(10)
    private val scheduledJobCheckInterval: Duration = Duration.ofSeconds(30)

    // --- lifecycle ---

    fun start() {
        if (ownerSession == null || agentCredential == null || jobs.isNotEmpty()) return
        jobs = listOf(
            scope.launch { heartbeatLoop() },
            scope.launch { printerSyncLoop() },
            scope.launch { sse.runForever() },
            scope.launch { orderEvents.runForever() },
            scope.launch { scheduledJobsLoop() },
        )
    }

    fun stop() {
        sse.stop()
        orderEvents.stop()
        supervisorJob.cancel()
        db.close()
    }

    // --- called from the UI bridge ---

    fun signInWithPassword(identifier: String, password: String): CredentialStore.OwnerSession =
        afterSignIn(Auth.loginWithPassword(api, identifier, password))

    fun signInWithOtp(widgetAccessToken: String): CredentialStore.OwnerSession =
        afterSignIn(Auth.finishLogin(api, widgetAccessToken))

    fun setPassword(password: String) {
        Auth.setPassword(api, requireSession(), password)
    }

    private fun afterSignIn(session: CredentialStore.OwnerSession): CredentialStore.OwnerSession {
        ownerSession = session
        agentCredential = Auth.ensurePaired(api, session)
        if (jobs.isEmpty()) start()
        return session
    }

    fun signOut() {
        CredentialStore.clearOwnerSession()
        ownerSession = null
    }

    fun status(): Map<String, Any?> = mapOf(
        "signedIn" to (ownerSession != null),
        "paired" to (agentCredential != null),
        "shopId" to ownerSession?.shopId,
        "autoPrintEnabled" to autoPrintEnabled,
        "connected" to lastHeartbeatOk,
        "agentVersion" to Auth.AGENT_VERSION,
        "computerName" to runCatching { java.net.InetAddress.getLocalHost().hostName }.getOrDefault("-"),
    )

    /** Paid orders only - matches the backend's own `OrderStatus.isPaid`. */
    @Suppress("UNCHECKED_CAST")
    fun listOrders(): List<Map<String, Any?>> {
        val result = ownerRequest { s -> api.ownerGet(s, "/api/v1/shop/${s.shopId}/orders") }
        val items = asItemList(result)
        return items.filter { it["paidAt"] != null }
    }

    @Suppress("UNCHECKED_CAST")
    fun listPrinters(): List<Map<String, Any?>> {
        val result = ownerRequest { s -> api.ownerGet(s, "/api/v1/shop/${s.shopId}/printers") }
        return asItemList(result)
    }

    fun setAutoPrint(enabled: Boolean) {
        ownerRequest { s -> api.ownerPut(s, "/api/v1/shop/${s.shopId}/settings/print", mapOf("autoPrint" to enabled)) }
        autoPrintEnabled = enabled
        onEmit("status", status())
    }

    /**
     * The only door out of a local UNKNOWN job - never called automatically,
     * only from a human in the UI who has physically looked at the printer.
     * Goes through the owner session, not the agent credential.
     */
    fun resolvePrintJob(jobId: String, success: Boolean, note: String? = null) {
        val body = mutableMapOf<String, Any?>("success" to success)
        if (!note.isNullOrBlank()) body["note"] = note
        ownerRequest { s -> api.ownerPost(s, "/api/v1/shop/${s.shopId}/print-jobs/$jobId/resolve", body) }
        db.updateJobState(jobId, if (success) "COMPLETED" else "FAILED", lastError = note)
        onEmit("jobs", emptyMap())
        onEmit("orders", emptyMap())
    }

    fun unresolvedJobs(): List<Map<String, Any?>> = db.unresolvedJobs().map { row ->
        mapOf(
            "jobId" to row.jobId,
            "orderId" to row.orderId,
            "orderCode" to row.orderCode,
            "state" to row.state,
            "attemptCount" to row.attemptCount,
            "lastError" to row.lastError,
            "updatedAt" to row.updatedAt,
            "scheduledPrintAt" to row.scheduledPrintAt,
        )
    }

    private fun requireSession(): CredentialStore.OwnerSession = ownerSession ?: error("not signed in")

    @Suppress("UNCHECKED_CAST")
    private fun asItemList(result: Any): List<Map<String, Any?>> = when (result) {
        is List<*> -> result as List<Map<String, Any?>>
        is Map<*, *> -> (result["items"] as? List<Map<String, Any?>>).orEmpty()
        else -> emptyList()
    }

    /** Retries once, after a token refresh, on an expired/invalid owner access token. */
    private fun <T> ownerRequest(call: (CredentialStore.OwnerSession) -> T): T {
        val session = requireSession()
        return try {
            call(session)
        } catch (exc: ApiError) {
            if (exc.code != "TOKEN_EXPIRED" && exc.code != "TOKEN_INVALID") throw exc
            runBlockingRefresh()
            call(requireSession())
        }
    }

    private fun runBlockingRefresh() = kotlinx.coroutines.runBlocking { refreshOwnerSession() }

    private suspend fun refreshOwnerSession() {
        val session = requireSession()
        val refreshed = withContext(Dispatchers.IO) { api.refreshOwnerSession(session.refreshToken) }
        @Suppress("UNCHECKED_CAST")
        val tokens = refreshed["tokens"] as Map<String, Any?>
        val updated = CredentialStore.OwnerSession(
            accessToken = tokens["accessToken"] as String,
            refreshToken = tokens["refreshToken"] as String,
            shopId = session.shopId,
            shopName = session.shopName,
        )
        ownerSession = updated
        CredentialStore.saveOwnerSession(updated)
    }

    // --- background loops ---

    private suspend fun heartbeatLoop() {
        while (true) {
            val credential = agentCredential
            if (credential != null) {
                val before = lastHeartbeatOk to autoPrintEnabled
                try {
                    val result = withContext(Dispatchers.IO) { api.heartbeat(credential, Auth.AGENT_VERSION) }
                    autoPrintEnabled = result.autoPrintEnabled
                    lastHeartbeatOk = true
                } catch (exc: Exception) {
                    log.log(Level.WARNING, "heartbeat_failed", exc)
                    lastHeartbeatOk = false
                }
                // Push only on an actual flip - not every tick.
                if ((lastHeartbeatOk to autoPrintEnabled) != before) onEmit("status", status())
            }
            delay(settings.heartbeatIntervalSeconds * 1000)
        }
    }

    private suspend fun printerSyncLoop() {
        while (true) {
            val credential = agentCredential
            if (credential != null) {
                try {
                    val printers = withContext(Dispatchers.IO) { discoverPrinters() }
                    val request = com.printly.agent.models.PrinterSyncRequest(
                        printers = printers.map {
                            com.printly.agent.models.AgentPrinter(
                                windowsPrinterName = it.windowsPrinterName,
                                displayName = it.displayName,
                                colorCapable = it.colorCapable,
                                duplexCapable = it.duplexCapable,
                                paperSizes = it.paperSizes,
                                status = it.status,
                            )
                        },
                    )
                    withContext(Dispatchers.IO) { api.syncPrinters(credential, request) }
                    for (p in printers) {
                        db.upsertPrinter(
                            p.windowsPrinterName, p.displayName,
                            p.colorCapable?.toString(), p.duplexCapable?.toString(),
                            p.paperSizes.map { it.name }.sorted().joinToString(","),
                            p.status.name, p.isSystemDefault,
                        )
                    }
                    onEmit("printers", emptyMap())
                } catch (exc: Exception) {
                    log.log(Level.SEVERE, "printer_sync_failed", exc)
                }
            }
            delay(120_000)
        }
    }

    private suspend fun onJobReference(jobId: String, orderId: String, orderCode: String?) {
        val credential = agentCredential ?: return
        val scheduledPrintAt = computeScheduledPrintAt(orderId)
        handleJobReference(jobContext(credential), jobId, orderId, orderCode, scheduledPrintAt)
    }

    private fun onJobProgress() {
        onEmit("jobs", emptyMap())
        onEmit("orders", emptyMap())
    }

    private fun onOrdersChanged() {
        onEmit("orders", emptyMap())
    }

    private fun jobContext(credential: CredentialStore.AgentCredential) = JobContext(
        api = api,
        db = db,
        tempDir = settings.tempDir,
        maxRetryAttempts = settings.maxRetryAttempts,
        downloadTimeoutSeconds = settings.downloadTimeoutSeconds,
        jobTimeoutSeconds = settings.jobTimeoutSeconds,
        credential = credential,
        onEvent = ::onJobProgress,
    )

    /** None means "print as soon as claimed" - no scheduled slot, or it (or its lead-time offset) is already in the past. */
    private suspend fun computeScheduledPrintAt(orderId: String): String? {
        val slotStartRaw = orderScheduledSlotStart(orderId) ?: return null
        val slotStart = try {
            Instant.parse(slotStartRaw)
        } catch (exc: DateTimeParseException) {
            log.warning("unparseable_scheduled_slot_start orderId=$orderId value=$slotStartRaw")
            return null
        }
        val printAt = slotStart.minus(scheduledPrintLeadTime)
        if (!printAt.isAfter(Instant.now())) return null
        return printAt.toString()
    }

    @Suppress("UNCHECKED_CAST")
    private fun orderScheduledSlotStart(orderId: String): String? = try {
        val order = ownerRequest { s -> api.ownerGet(s, "/api/v1/shop/${s.shopId}/orders/$orderId") }
        (order as? Map<String, Any?>)?.get("scheduledSlotStart") as? String
    } catch (exc: Exception) {
        log.log(Level.SEVERE, "order_lookup_for_schedule_failed orderId=$orderId", exc)
        null
    }

    private suspend fun scheduledJobsLoop() {
        while (true) {
            val credential = agentCredential
            if (credential != null) {
                try {
                    processDueScheduledJobs(jobContext(credential))
                } catch (exc: Exception) {
                    log.log(Level.SEVERE, "scheduled_jobs_check_failed", exc)
                }
            }
            delay(scheduledJobCheckInterval.toMillis())
        }
    }
}
