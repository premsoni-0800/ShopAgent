package com.printly.agent.net

import com.printly.agent.credentials.CredentialStore
import kotlinx.coroutines.CompletableDeferred
import okhttp3.Request
import okhttp3.Response
import okhttp3.sse.EventSource
import okhttp3.sse.EventSourceListener
import okhttp3.sse.EventSources
import java.io.IOException
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

    /**
     * Lets the stream run again after it gave up on a signed-out session.
     *
     * [stopped] is set when the refresh token is gone, which is correct while
     * nobody is signed in - but it used to be a one-way latch, so signing back
     * in left the Orders view silently frozen for the rest of the process. The
     * agent went on reporting itself connected, because the heartbeat is a
     * different loop on a different credential, and the only symptom was a
     * list that never changed.
     */
    fun resume() {
        stopped = false
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
                var refreshed = false
                // The access token used to open this connection aged out
                // mid-stream (a live SSE connection easily outlives a
                // short-lived JWT) - refresh once, then let the backoff loop
                // reconnect with it, same principle as a normal request retry.
                if (response?.code == 401 || response?.code == 403) {
                    kotlinx.coroutines.runBlocking {
                        try {
                            refreshSession()
                            refreshed = true
                        } catch (exc: ApiError) {
                            // A refresh token the server has thrown away is not
                            // going to start working. Retrying it reconnected
                            // every couple of seconds for as long as the agent
                            // ran - thirty failures a minute, forever, drowning
                            // the log and hiding anything real. The owner has to
                            // sign in again, and nothing here can do that for
                            // them, so stop asking.
                            if (exc.code == "TOKEN_REVOKED" || exc.code == "TOKEN_INVALID" || exc.statusCode == 401) {
                                log.warning("order_events_stopped_signed_out code=${exc.code}")
                                stopped = true
                            } else {
                                log.log(Level.WARNING, "order_events_session_refresh_failed", exc)
                            }
                        } catch (exc: Exception) {
                            // Anything else - a network blip mid-refresh - is
                            // worth retrying, so the loop is left alone.
                            log.log(Level.WARNING, "order_events_session_refresh_failed", exc)
                        }
                    }
                }
                when (streamFailureAction(refreshed, stopped)) {
                    // A new access token in hand: reconnecting at once is the
                    // point of having refreshed, so this is reported as the
                    // clean disconnect it effectively is and the ladder resets.
                    StreamFailure.RECONNECT_NOW -> done.complete(Unit)
                    // Nobody is signed in. runForever is about to return
                    // anyway; completing cleanly keeps a shutdown quiet.
                    StreamFailure.GIVE_UP -> done.complete(Unit)
                    // Everything else is a failure and has to look like one, or
                    // the backoff never engages and nothing is ever logged.
                    StreamFailure.BACK_OFF ->
                        done.completeExceptionally(t ?: IOException("order events stream rejected: HTTP ${response?.code ?: "?"}"))
                }
            }
        }

        val eventSource = EventSources.createFactory(api.sseHttp).newEventSource(request, listener)
        try {
            done.await()
        } finally {
            eventSource.cancel()
        }
    }
}

/** What a dropped stream should lead to. */
internal enum class StreamFailure { RECONNECT_NOW, BACK_OFF, GIVE_UP }

/**
 * Whether a dropped stream is worth backing off from.
 *
 * The distinction is the whole of why this exists. `onFailure` used to end in
 * an unconditional `done.complete(Unit)`, so [streamOnce] returned *normally*
 * from every failure - and [reconnectLoop] reads a normal return as "the
 * server closed cleanly" and resets its ladder. The delay was therefore pinned
 * at the base value for ever: a ten-minute backend deploy became roughly eight
 * hundred connection attempts against a dead host instead of a dozen, from
 * every agent at once, arriving exactly as the backend came back up. And
 * because nothing was thrown, `reconnectLoop`'s own `connection_failed`
 * warning never fired, so the shop's log for that window was empty.
 *
 * Only two things are not failures: a token that was just refreshed, where
 * reconnecting immediately is the entire point, and a session that is gone,
 * where the loop is about to stop anyway.
 */
internal fun streamFailureAction(refreshed: Boolean, stopped: Boolean): StreamFailure = when {
    stopped -> StreamFailure.GIVE_UP
    refreshed -> StreamFailure.RECONNECT_NOW
    else -> StreamFailure.BACK_OFF
}
