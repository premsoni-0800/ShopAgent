package com.printly.agent.net

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * Guards the timeouts a long-lived stream needs, because getting them wrong
 * fails silently: the agent reconnects, logs a routine `connection_failed`,
 * and looks healthy while dropping every event that lands in the gap.
 *
 * This is not hypothetical. Both SSE clients originally opened their streams
 * with [PrintlyApiClient.http], whose request-shaped timeouts made a stable
 * connection impossible - see [PrintlyApiClient.sseHttp] for the full
 * reasoning. These assertions are the cheap way to keep that from creeping
 * back in the next time someone reaches for the obvious client.
 */
class SseClientTimeoutsTest {

    private val api = PrintlyApiClient("https://example.invalid")

    @Test
    fun `sse client never bounds the whole call`() {
        assertEquals(0, api.sseHttp.callTimeoutMillis, "a callTimeout caps how long a stream may stay open")
    }

    /**
     * The backend pings every 15s. Anything at or under that guarantees a
     * teardown in the first quiet gap; the margin has to cover a ping being
     * genuinely late, not just present.
     */
    @Test
    fun `sse read timeout leaves room for the backend's 15s ping`() {
        assertTrue(
            api.sseHttp.readTimeoutMillis >= 2 * BACKEND_PING_INTERVAL_MS,
            "readTimeout=${api.sseHttp.readTimeoutMillis}ms must outlast more than one 15s backend ping",
        )
    }

    @Test
    fun `sse client pings underneath so a half-open socket is still noticed`() {
        assertTrue(api.sseHttp.pingIntervalMillis in 1 until api.sseHttp.readTimeoutMillis)
    }

    /** The request client keeps its bound - an ordinary call must not hang forever. */
    @Test
    fun `request client still bounds the whole call`() {
        assertTrue(api.http.callTimeoutMillis > 0)
    }

    private companion object {
        /** `OrderEventBroadcaster.HEARTBEAT_INTERVAL_MS` / `PrintAgentEventBroadcaster.HEARTBEAT_INTERVAL_MS`. */
        const val BACKEND_PING_INTERVAL_MS = 15_000
    }
}
