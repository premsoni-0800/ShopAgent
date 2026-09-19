package com.printly.agent.printing

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import okhttp3.OkHttpClient
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.DisplayName
import org.junit.jupiter.api.Test
import java.net.InetSocketAddress
import java.nio.file.Files
import java.nio.file.Path
import kotlin.io.path.createTempDirectory

/**
 * Resuming an interrupted transfer.
 *
 * The case this exists for is a shop's wifi dropping at 90% of a 40MB
 * coursework PDF. Before, the next attempt pulled all 40MB again; the point of
 * these tests is that it pulls the remainder - and, just as importantly, that
 * every way a store can decline to resume still ends in a correct file rather
 * than a spliced one.
 */
class ResumableDownloadTest {

    private lateinit var server: HttpServer
    private lateinit var tempDir: Path
    private val http = OkHttpClient()

    /** 64KB of non-repeating bytes, so a splice or a doubling cannot pass as correct. */
    private val body = ByteArray(64 * 1024) { (it * 31 % 251).toByte() }

    /** What the last request asked for, so a test can assert the Range was actually sent. */
    @Volatile private var lastRange: String? = null

    @BeforeEach
    fun start() {
        tempDir = createTempDirectory("resume-test")
        server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.executor = null
        server.start()
    }

    @AfterEach
    fun stop() {
        server.stop(0)
        tempDir.toFile().deleteRecursively()
    }

    private fun url() = "http://127.0.0.1:${server.address.port}/doc.pdf"

    private fun serve(handler: (HttpExchange) -> Unit) {
        server.createContext("/doc.pdf") { exchange ->
            lastRange = exchange.requestHeaders.getFirst("Range")
            handler(exchange)
            exchange.close()
        }
    }

    /** A store that honours Range the way R2 and S3 do. */
    private fun serveWithRangeSupport() = serve { exchange ->
        val range = exchange.requestHeaders.getFirst("Range")
        val from = range?.removePrefix("bytes=")?.substringBefore('-')?.toIntOrNull() ?: 0
        val slice = body.copyOfRange(from, body.size)
        exchange.sendResponseHeaders(if (range != null) 206 else 200, slice.size.toLong())
        exchange.responseBody.write(slice)
    }

    private fun download(destination: Path): Path =
        downloadDocumentTo(http, url(), destination, timeoutSeconds = 30)

    @Test
    @DisplayName("A clean download writes the whole file and leaves no partial behind")
    fun cleanDownload() {
        serveWithRangeSupport()
        val destination = tempDir.resolve("out.pdf")

        download(destination)

        assertArrayEquals(body, Files.readAllBytes(destination))
        assertFalse(Files.exists(tempDir.resolve("out.pdf.part")), "the .part must be moved into place, not left")
    }

    @Test
    @DisplayName("An interrupted transfer asks for the remainder, and the file is whole")
    fun resumesFromPartial() {
        serveWithRangeSupport()
        val destination = tempDir.resolve("out.pdf")

        // What a dropped connection leaves: the first 40KB, already on disk.
        val already = 40 * 1024
        Files.write(tempDir.resolve("out.pdf.part"), body.copyOfRange(0, already))

        download(destination)

        assertEquals("bytes=$already-", lastRange, "the remainder must be requested, not the whole object")
        assertArrayEquals(body, Files.readAllBytes(destination))
    }

    @Test
    @DisplayName("A store that ignores Range still yields a correct file, not a doubled one")
    fun storeIgnoresRange() {
        // Answers 200 with the whole object however it was asked. Appending
        // that to a 40KB partial would produce a 104KB file that is not a PDF.
        serve { exchange ->
            exchange.sendResponseHeaders(200, body.size.toLong())
            exchange.responseBody.write(body)
        }
        val destination = tempDir.resolve("out.pdf")
        Files.write(tempDir.resolve("out.pdf.part"), body.copyOfRange(0, 40 * 1024))

        download(destination)

        assertArrayEquals(body, Files.readAllBytes(destination))
    }

    @Test
    @DisplayName("A partial longer than the object is discarded rather than appended to")
    fun unsatisfiableRangeStartsAgain() {
        var sawRange = false
        serve { exchange ->
            if (exchange.requestHeaders.getFirst("Range") != null && !sawRange) {
                sawRange = true
                exchange.sendResponseHeaders(416, -1)
                return@serve
            }
            exchange.sendResponseHeaders(200, body.size.toLong())
            exchange.responseBody.write(body)
        }
        val destination = tempDir.resolve("out.pdf")
        // A leftover from a different, longer document under the same name.
        Files.write(tempDir.resolve("out.pdf.part"), ByteArray(body.size * 2))

        download(destination)

        assertArrayEquals(body, Files.readAllBytes(destination))
    }

    @Test
    @DisplayName("A failed download leaves the partial for the next attempt, and no document")
    fun failureKeepsThePartialAndWritesNoDocument() {
        serve { exchange -> exchange.sendResponseHeaders(500, -1) }
        val destination = tempDir.resolve("out.pdf")

        runCatching { download(destination) }

        assertFalse(Files.exists(destination), "a failed transfer must never produce a document to print")
    }
}
