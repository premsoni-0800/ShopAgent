package com.printly.agent.core

import com.printly.agent.credentials.CredentialStore
import com.printly.agent.db.Database
import com.printly.agent.jobs.JobContext
import com.printly.agent.jobs.JobDispatcher
import com.printly.agent.jobs.PrintQueue
import com.printly.agent.jobs.handleJobReference
import com.printly.agent.jobs.processDueScheduledJobs
import com.printly.agent.jobs.resumeInterruptedJobs
import com.printly.agent.net.ApiError
import com.printly.agent.net.OrderEventsClient
import com.printly.agent.net.PrintJobSseClient
import com.printly.agent.net.PrintlyApiClient
import com.printly.agent.printers.discoverPrinters
import com.printly.agent.printing.sweepOrphanedDocuments
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
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
 * How many orders may be looked up at once.
 *
 * Intake is network-bound and touches no printer, so it is not the thing that
 * needs limiting - but it is still somebody's backend, and a hundred orders
 * arriving at once should not become a hundred simultaneous requests. Four
 * keeps a backlog moving into the queue quickly while the printer works
 * through what is already there.
 */
private const val INTAKE_CONCURRENCY = 4

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

    /**
     * Set when the server answers the heartbeat with a rejection rather than
     * not answering at all.
     *
     * The difference matters to whoever is looking at the screen. "Cannot
     * reach the server" sends them to check the network; a revoked
     * registration needs them to sign in again, and no amount of waiting or
     * rebooting the router will fix it. Reporting the second as the first is
     * how someone spends twenty minutes on the wrong problem.
     */
    @Volatile var credentialRejected: Boolean = false
        private set

    /** Best-effort UI push - the JavaFX host wires this to `webEngine.executeScript(...)`. No-op headless. */
    var onEmit: (event: String, payload: Map<String, Any?>) -> Unit = { _, _ -> }

    private val supervisorJob = SupervisorJob()
    private val scope = CoroutineScope(Dispatchers.Default + supervisorJob)
    private var jobs: List<Job> = emptyList()

    /**
     * Intake: fetching an order's details and writing it down. Deliberately
     * separate from printing and allowed to run several at a time - it is
     * network-bound, and none of it touches a printer. Running it inside the
     * print slot, as it used to, meant the next order could not be recorded
     * until the current one had finished printing.
     */
    private val intake = JobDispatcher(scope, maxConcurrent = INTAKE_CONCURRENCY)

    /** Printing: one order at a time, lowest order number first. */
    private val printQueue = PrintQueue(scope, settings.maxConcurrentPrintJobs) { onJobProgress() }

    private val sse = PrintJobSseClient(api, { agentCredential }, ::onJobReference)
    private val orderEvents = OrderEventsClient(api, { ownerSession }, ::onOrdersChanged, ::refreshOwnerSession)

    private val scheduledJobCheckInterval: Duration = Duration.ofSeconds(30)

    // --- lifecycle ---

    init {
        // Before anything else, and deliberately not inside start(): documents
        // abandoned by a crashed agent are there whether or not this one is
        // paired, and an agent that never finishes signing in would otherwise
        // leave them sitting on the counter PC indefinitely.
        val swept = sweepOrphanedDocuments(settings.tempDir)
        if (swept > 0) log.info("orphaned_documents_removed count=$swept")
    }

    fun start() {
        if (ownerSession == null || agentCredential == null || jobs.isNotEmpty()) return
        jobs = listOf(
            scope.launch { heartbeatLoop() },
            scope.launch { printerSyncLoop() },
            scope.launch { sse.runForever() },
            scope.launch { orderEvents.runForever() },
            scope.launch { scheduledJobsLoop() },
            scope.launch { jobReconcileLoop() },
            scope.launch { resumeInterruptedJobsOnce() },
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

    /**
     * Takes over the session the dashboard just signed in with, and pairs this
     * machine on the strength of it.
     *
     * This is what removes the pairing code. A code exists to carry proof of
     * ownership from a browser, where the owner is signed in, to a desktop app
     * that has no way to know who they are - and typing six characters across
     * that gap was the only reason it was ever asked for. In this app there is
     * no gap: the dashboard in the window and the agent behind it are one
     * process, so the session is simply handed over.
     *
     * Idempotent, because the page hands it over on every sign-in and on every
     * reload: pairing an already-paired machine re-uses the registration it
     * already holds (see [Auth.ensurePaired]), and [start] is a no-op once the
     * loops are running.
     *
     * Still fails loudly if another machine holds this shop's registration -
     * that is a real conflict a person has to resolve, not something to paper
     * over by quietly stealing the pairing from the PC that has the printers.
     */
    fun adoptOwnerSession(accessToken: String, refreshToken: String, shopId: String, shopName: String?): CredentialStore.OwnerSession {
        val session = CredentialStore.OwnerSession(accessToken, refreshToken, shopId, shopName)
        CredentialStore.saveOwnerSession(session)
        return afterSignIn(session)
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
        "credentialRejected" to credentialRejected,
        // What the counter needs to answer "where is my order?": how much work
        // is outstanding, and the codes in the order they will actually print.
        "queueDepth" to printQueue.depth,
        "printingNow" to printQueue.activeCount,
        "queuedOrders" to printQueue.waiting(),
        // Which of those jumped the queue by scanning at the counter, and which
        // are on a printer right now. The agent page colours them from this:
        // green for the one coming out, blue for the people standing there.
        "priorityOrders" to printQueue.waitingPriority(),
        "printingOrders" to printQueue.printing(),
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

    fun unresolvedJobs(): List<Map<String, Any?>> {
        // Nothing to show before this machine belongs to a shop, and once it
        // does, another shop's leftovers are not this one's business.
        val shopId = agentCredential?.shopId ?: return emptyList()
        return db.unresolvedJobs(shopId).map { row ->
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
        log.info("heartbeat_loop_started interval=${settings.heartbeatIntervalSeconds}s shop=${agentCredential?.shopId}")
        while (true) {
            val credential = agentCredential
            if (credential != null) {
                val before = lastHeartbeatOk to autoPrintEnabled
                try {
                    val result = withContext(Dispatchers.IO) { api.heartbeat(credential, Auth.AGENT_VERSION) }
                    autoPrintEnabled = result.autoPrintEnabled
                    lastHeartbeatOk = true
                    credentialRejected = false
                } catch (exc: Exception) {
                    log.log(Level.WARNING, "heartbeat_failed", exc)
                    lastHeartbeatOk = false
                    // 401/403 is the server saying who this machine is no
                    // longer holds - a different problem from not answering.
                    credentialRejected = (exc as? ApiError)?.statusCode in setOf(401, 403)
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

    /**
     * Hands the job to [dispatcher] and returns at once.
     *
     * Returning immediately is the point, not a detail. This is called from
     * OkHttp's SSE reader thread and from the reconciliation loop, and both
     * used to *await* the whole pipeline - download, print, and up to five
     * minutes of watching the spooler - before doing anything else. While that
     * ran, the stream read no further events and the poll loop's interval had
     * not even started counting, so a second order placed during a print was
     * not merely printed late, it was not delivered at all until the first one
     * finished.
     *
     * [computeScheduledPrintAt] moves inside the dispatched block for the same
     * reason: it makes its own HTTP call, which has no business happening on a
     * socket reader thread.
     */
    private fun onJobReference(jobId: String, orderId: String, orderCode: String?) {
        val credential = agentCredential ?: return
        intake.submit(jobId) {
            val lookup = orderScheduledSlotStart(orderId)
            // Read off the same answer the schedule came from. The order lookup
            // is an HTTP call this path already makes, and OrderResponse has
            // carried inShopPriority all along - so knowing that a student is
            // standing at the counter costs nothing extra.
            val priority = (lookup as? ScheduleLookup.Known)?.priority == true
            when (val plan = schedulePlanFor(lookup, Instant.now())) {
                is SchedulePlan.PrintAt ->
                    handleJobReference(jobContext(credential), printQueue, jobId, orderId, orderCode, plan.at, priority)
                // Deliberately records nothing. Recording the job means deciding
                // when to print it, and that is the one thing this path could
                // not find out - so it is left to the ten-second reconciliation
                // poll, which re-lists every outstanding job and brings this one
                // back here to be asked again. Printing it now instead is what
                // sent a six o'clock order out at eleven in the morning, to sit
                // on the counter all day.
                SchedulePlan.Hold ->
                    log.warning("scheduled_lookup_deferred job=$jobId order=$orderId")
            }
        }
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

    /**
     * Asks the backend when this order is due, and says plainly when it could
     * not find out.
     *
     * The distinction is the whole point. This used to catch everything and
     * answer null, and null already meant "no slot, print it now" - so a
     * momentary 500, a timeout against a cold-starting host, or a token that
     * had just aged out all read as "this order is not scheduled". A six
     * o'clock slot discovered at eleven in the morning printed at eleven in
     * the morning, which is precisely what the scheduling exists to stop.
     *
     * Only an answer from the server is treated as an answer. A client error
     * is not worth retrying - a 404 for an order that is gone will say the
     * same thing every time - but anything that might succeed later says so.
     */
    @Suppress("UNCHECKED_CAST")
    private suspend fun orderScheduledSlotStart(orderId: String): ScheduleLookup = try {
        // On Dispatchers.IO because ownerGet blocks, and this runs on the
        // shared Default dispatcher the print queue's own workers live on.
        val order = withContext(Dispatchers.IO) {
            ownerRequest { s -> api.ownerGet(s, "/api/v1/shop/${s.shopId}/orders/$orderId") }
        }
        val fields = order as? Map<String, Any?>
        ScheduleLookup.Known(
            // shopReleaseAt, which the backend works out as scheduledPrintAt
            // minus its own release lead, and which is exactly the moment this
            // shop is meant to receive the order. Taking it whole means the
            // agent has no opinion about the lead time and cannot disagree
            // with the server about it - see [schedulePlanFor].
            //
            // Null on a Print Now order, which is the same thing as "print it
            // as soon as it is claimed".
            releaseAt = fields?.get("shopReleaseAt") as? String,
            priority = fields?.get("inShopPriority") == true,
        )
    } catch (exc: CancellationException) {
        throw exc
    } catch (exc: ApiError) {
        if (exc.statusCode in 400..499) {
            // ownerRequest has already refreshed and retried once, so a 401
            // here means the owner is signed out, not that the token aged out.
            log.warning("order_schedule_lookup_refused orderId=$orderId status=${exc.statusCode} code=${exc.code}")
            ScheduleLookup.Refused
        } else {
            log.log(Level.WARNING, "order_schedule_lookup_unavailable orderId=$orderId", exc)
            ScheduleLookup.Unavailable
        }
    } catch (exc: Exception) {
        log.log(Level.WARNING, "order_schedule_lookup_unavailable orderId=$orderId", exc)
        ScheduleLookup.Unavailable
    }

    private suspend fun scheduledJobsLoop() {
        while (true) {
            val credential = agentCredential
            if (credential != null) {
                try {
                    processDueScheduledJobs(jobContext(credential), printQueue)
                } catch (exc: Exception) {
                    log.log(Level.SEVERE, "scheduled_jobs_check_failed", exc)
                }
            }
            delay(scheduledJobCheckInterval.toMillis())
        }
    }

    /**
     * Picks up whatever the last run was in the middle of when it stopped.
     *
     * A job the agent had already recorded locally is invisible to every other
     * path: the SSE push and the reconciliation poll both funnel into
     * [handleJobReference], which drops any id already in the database as a
     * duplicate. That is correct - it is what stops a document printing twice -
     * but it means a job interrupted between "recorded" and "printed" is
     * stranded permanently unless something goes looking for it once, here.
     *
     * Waits for the first heartbeat so a resumed job is not attempted while
     * the backend is still unreachable, which would just burn its retries.
     */
    private suspend fun resumeInterruptedJobsOnce() {
        delay(settings.heartbeatIntervalSeconds * 1000)
        val credential = agentCredential ?: return
        try {
            resumeInterruptedJobs(jobContext(credential), printQueue)
        } catch (exc: Exception) {
            log.log(Level.SEVERE, "resume_interrupted_jobs_failed", exc)
        }
    }

    /**
     * Asks the backend outright for outstanding work, independently of the SSE
     * stream.
     *
     * The stream is the fast path and normally delivers a job in under a
     * second, but it is not something to stake unattended operation on: a
     * connection can stop delivering without ever closing (so no reconnect,
     * and no reconnect means no reconcile), a push can be dropped while the
     * backend restarts mid-deploy, and a job created during a reconnect window
     * belongs to neither side. This loop is what makes the agent autonomous
     * rather than merely reactive - the worst case for any job becomes one
     * interval, not "until something else happens to wake the stream".
     *
     * Safe to run as often as we like: [handleJobReference] keys off the local
     * database, so a job id already seen is dropped before any printer is
     * touched. A pass with nothing new costs one GET.
     */
    private suspend fun jobReconcileLoop() {
        while (true) {
            delay(settings.jobReconcileIntervalSeconds * 1000)
            val credential = agentCredential ?: continue
            try {
                val outstanding = withContext(Dispatchers.IO) { api.outstandingJobs(credential) }
                for (job in outstanding) {
                    onJobReference(job.jobId, job.orderId, job.orderCode)
                }
            } catch (exc: Exception) {
                // Expected whenever the backend is briefly unreachable; the
                // next pass is seconds away, so this is not worth escalating.
                log.log(Level.FINE, "job_reconcile_failed", exc)
            }
        }
    }
}

/** What the backend could tell the agent about an order's slot. */
internal sealed interface ScheduleLookup {
    /**
     * The server answered. [releaseAt] is when this shop is meant to receive
     * the order - null on a Print Now order - and [priority] is true when the
     * student has scanned the shop's QR at the counter and the backend has
     * agreed to serve them next.
     */
    data class Known(val releaseAt: String?, val priority: Boolean = false) : ScheduleLookup

    /** It could not be asked, and asking again shortly might work - a 5xx, a timeout, a dropped connection. */
    object Unavailable : ScheduleLookup

    /** It refused, and will refuse again - a 404 for an order that is gone, or a 401 once the owner is signed out. */
    object Refused : ScheduleLookup
}

/** What to do about it. */
internal sealed interface SchedulePlan {
    /** Record the job and print it at [at]; null means as soon as it is claimed. */
    data class PrintAt(val at: String?) : SchedulePlan

    /** Establish nothing and record nothing, so the reconciliation poll asks again. */
    object Hold : SchedulePlan
}

/**
 * Turns what the lookup found into what the agent should do.
 *
 * Printing early and never printing at all are both real harms, so they are
 * weighed rather than one being picked outright. A failure that might clear
 * holds the job: the reconciliation poll re-lists it within ten seconds, and a
 * scheduled order is by definition one with time to spare. A failure that will
 * not clear prints it now, because the alternative there is an order that
 * never comes out at all.
 *
 * There is deliberately no lead time here. The backend already subtracted its
 * own from the student's chosen time and sent the answer as `shopReleaseAt`,
 * so the agent takes that whole rather than keeping a number of its own. A
 * copy would be a fourth: the backend has `printly.scheduled-print
 * .release-lead`, the student app has SHOP_RELEASE_LEAD, and the dashboard has
 * PRINT_LEAD_MINUTES. That shape has already failed once - the student app sat
 * at twenty minutes while the server released at five, so a student asking for
 * 7:00 was told 6:40 and the shop got it at 6:55. An agent that derives
 * nothing cannot drift.
 *
 * A release time already upon us is not a schedule, it is a job to print now -
 * which is also what an unscheduled order, or one with an unreadable time,
 * deserves.
 */
internal fun schedulePlanFor(lookup: ScheduleLookup, now: Instant): SchedulePlan = when (lookup) {
    ScheduleLookup.Unavailable -> SchedulePlan.Hold
    ScheduleLookup.Refused -> SchedulePlan.PrintAt(null)
    is ScheduleLookup.Known -> {
        val releaseAt = lookup.releaseAt?.let {
            try {
                Instant.parse(it)
            } catch (exc: DateTimeParseException) {
                null
            }
        }
        if (releaseAt == null || !releaseAt.isAfter(now)) {
            SchedulePlan.PrintAt(null)
        } else {
            SchedulePlan.PrintAt(releaseAt.toString())
        }
    }
}
