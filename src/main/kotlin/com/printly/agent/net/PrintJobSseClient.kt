package com.printly.agent.net

import com.printly.agent.credentials.CredentialStore
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import okhttp3.Request
import okhttp3.Response
import okhttp3.sse.EventSource
import okhttp3.sse.EventSourceListener
import okhttp3.sse.EventSources
import java.util.logging.Logger

typealias JobReferenceHandler = suspend (jobId: String, orderId: String, orderCode: String?) -> Unit

/**
 * SSE client for `/api/v1/print-agent/events`, with reconnect and
 * reconciliation - direct port of the Python agent's `sse_client.py`.
 *
 * A missed push must never mean a missed job: every (re)connect, including
 * the very first, lists outstanding jobs *before* subscribing to the stream,
 * so a job created while the agent was offline is picked up by the list call
 * rather than depending on the stream having no gaps.
 */
class PrintJobSseClient(
    private val api: PrintlyApiClient,
    private val credentialProvider: () -> CredentialStore.AgentCredential?,
    private val onJobReference: JobReferenceHandler,
    private val baseDelaySeconds: Double = 1.0,
    private val maxDelaySeconds: Double = 60.0,
) {
    private val log = Logger.getLogger(javaClass.name)

    @Volatile private var stopped = false
    fun stop() {
        stopped = true
    }

    suspend fun runForever() {
        reconnectLoop(log, baseDelaySeconds, maxDelaySeconds, { stopped }) {
            val credential = credentialProvider()
            if (credential == null) {
                kotlinx.coroutines.delay((baseDelaySeconds * 1000).toLong())
                return@reconnectLoop
            }
            reconcile(credential)
            streamOnce(credential)
        }
    }

    private suspend fun reconcile(credential: CredentialStore.AgentCredential) {
        val jobs = withContext(Dispatchers.IO) { api.outstandingJobs(credential) }
        for (job in jobs) onJobReference(job.jobId, job.orderId, job.orderCode)
    }

    private suspend fun streamOnce(credential: CredentialStore.AgentCredential) {
        val done = CompletableDeferred<Unit>()
        val request = Request.Builder()
            .url(api.baseUrl.trimEnd('/') + "/api/v1/print-agent/events")
            .header("Authorization", "Bearer ${credential.bearerToken}")
            .header("Accept", "text/event-stream")
            .build()

        val listener = object : EventSourceListener() {
            override fun onOpen(eventSource: EventSource, response: Response) {
                log.info("sse_connected")
            }

            override fun onEvent(eventSource: EventSource, id: String?, type: String?, data: String) {
                try {
                    val payload: Map<String, Any?> = api.mapper.readValue(data, Map::class.java) as Map<String, Any?>
                    val jobId = payload["jobId"] as? String ?: return
                    val orderId = payload["orderId"] as? String ?: return
                    runBlocking { onJobReference(jobId, orderId, null) }
                } catch (exc: Exception) {
                    log.warning("sse_malformed_event: $exc")
                }
            }

            override fun onClosed(eventSource: EventSource) {
                if (!done.isCompleted) done.complete(Unit)
            }

            override fun onFailure(eventSource: EventSource, t: Throwable?, response: Response?) {
                if (!done.isCompleted) {
                    if (t != null) done.completeExceptionally(t) else done.complete(Unit)
                }
            }
        }

        val eventSource = EventSources.createFactory(api.http).newEventSource(request, listener)
        try {
            done.await()
        } finally {
            eventSource.cancel()
        }
    }
}
