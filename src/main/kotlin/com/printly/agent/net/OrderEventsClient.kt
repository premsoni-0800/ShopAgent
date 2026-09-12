package com.printly.agent.net

import com.printly.agent.credentials.CredentialStore
import kotlinx.coroutines.CompletableDeferred
import okhttp3.Request
import okhttp3.Response
import okhttp3.sse.EventSource
import okhttp3.sse.EventSourceListener
import okhttp3.sse.EventSources
import java.util.logging.Level
import java.util.logging.Logger

/**
 * Live order-change notifications for the owner-facing Orders view - direct
 * port of the Python agent's `order_events_client.py`. Reuses the shop
 * dashboard's own `GET /api/v1/shop/{shopId}/orders/events` stream; every
 * event, regardless of payload, just means "go refetch the orders list."
 */
class OrderEventsClient(
    private val api: PrintlyApiClient,
    private val sessionProvider: () -> CredentialStore.OwnerSession?,
    private val onOrdersChanged: () -> Unit,
    private val refreshSession: suspend () -> Unit,
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
            val session = sessionProvider()
            if (session == null) {
                kotlinx.coroutines.delay((baseDelaySeconds * 1000).toLong())
                return@reconnectLoop
            }
            streamOnce(session)
        }
    }

    private suspend fun streamOnce(session: CredentialStore.OwnerSession) {
        val done = CompletableDeferred<Unit>()
        val request = Request.Builder()
            .url(api.baseUrl.trimEnd('/') + "/api/v1/shop/${session.shopId}/orders/events")
            .header("Authorization", "Bearer ${session.accessToken}")
            .header("Accept", "text/event-stream")
            .build()

        val listener = object : EventSourceListener() {
            override fun onOpen(eventSource: EventSource, response: Response) {
                log.info("order_events_connected")
            }

            override fun onEvent(eventSource: EventSource, id: String?, type: String?, data: String) {
                onOrdersChanged()
            }

            override fun onClosed(eventSource: EventSource) {
                if (!done.isCompleted) done.complete(Unit)
            }

            override fun onFailure(eventSource: EventSource, t: Throwable?, response: Response?) {
                if (done.isCompleted) return
                // The access token used to open this connection aged out
                // mid-stream (a live SSE connection easily outlives a
                // short-lived JWT) - refresh once, then let the backoff loop
                // reconnect with it, same principle as a normal request retry.
                if (response?.code == 401 || response?.code == 403) {
                    kotlinx.coroutines.runBlocking {
                        try {
                            refreshSession()
                        } catch (exc: Exception) {
                            log.log(Level.WARNING, "order_events_session_refresh_failed", exc)
                        }
                    }
                }
                done.complete(Unit)
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
