package com.printly.agent.printing

import okhttp3.OkHttpClient
import okhttp3.Request
import org.apache.pdfbox.Loader
import java.nio.file.Files
import java.nio.file.Path
import java.time.Duration
import java.util.UUID

class DocumentValidationError(message: String) : RuntimeException(message)

data class ValidatedDocument(val path: Path, val pageCount: Int)

/**
 * Streams a signed R2 URL to a restricted temp file - direct port of the
 * Python agent's `documents.py::download`. The caller is responsible for
 * deleting it once printing has finished, succeeded or not.
 */
fun downloadDocument(http: OkHttpClient, url: String, tempDir: Path, timeoutSeconds: Long): Path {
    Files.createDirectories(tempDir)
    // Prefixed with this process's id so [sweepOrphanedDocuments] can tell a
    // file another running agent is still printing from one abandoned by an
    // agent that died.
    val pid = ProcessHandle.current().pid()
    val destination = tempDir.resolve("$pid-${UUID.randomUUID().toString().replace("-", "")}.pdf")

    val client = http.newBuilder().callTimeout(Duration.ofSeconds(timeoutSeconds)).build()
    val request = Request.Builder().url(url).build()
    client.newCall(request).execute().use { response ->
        if (!response.isSuccessful) throw DocumentValidationError("download failed: HTTP ${response.code}")
        Files.newOutputStream(destination).use { out -> response.body?.byteStream()?.copyTo(out) }
    }
    return destination
}

/**
 * Deletes customer documents left behind by an agent that did not shut down.
 *
 * The pipeline deletes each file in a `finally`, which covers every way a job
 * can end - but not the ways the *process* can end. A crash, a kill from Task
 * Manager, or the counter PC losing power leaves somebody's coursework sitting
 * in the temp directory indefinitely, on a machine in a shop. The rule for
 * these files is download, print, delete; this is what makes it true across a
 * restart as well as across a job.
 *
 * Only sweeps files whose owning process is gone, so a second agent - or a
 * long job still spooling in another instance - is never robbed of the
 * document it is printing. A file whose name predates this scheme, or whose
 * pid has since been recycled onto a live process, is left for the next run
 * rather than risked.
 */
fun sweepOrphanedDocuments(tempDir: Path): Int {
    if (!Files.isDirectory(tempDir)) return 0
    var removed = 0
    runCatching {
        Files.newDirectoryStream(tempDir, "*.pdf").use { entries ->
            entries.forEach { entry ->
                val pid = entry.fileName.toString().substringBefore('-').toLongOrNull() ?: return@forEach
                val ownerAlive = ProcessHandle.of(pid).map { it.isAlive }.orElse(false)
                if (!ownerAlive && runCatching { Files.deleteIfExists(entry) }.getOrDefault(false)) removed++
            }
        }
    }
    return removed
}

/**
 * Verifies the file is a real, unencrypted, non-empty PDF before it is ever
 * handed to a printer - direct port of `documents.py::validate_pdf`. Never
 * trusts the page count the order recorded; [printPdf] needs a count it can
 * rely on to resolve a page range safely.
 */
fun validatePdf(path: Path, maxBytes: Long = 200_000_000): ValidatedDocument {
    val file = path.toFile()
    if (!file.exists()) throw DocumentValidationError("downloaded file missing: $path")

    val size = file.length()
    if (size == 0L) throw DocumentValidationError("downloaded file is empty")
    if (size > maxBytes) throw DocumentValidationError("downloaded file is unreasonably large: $size bytes")

    val document = try {
        Loader.loadPDF(file)
    } catch (exc: Exception) {
        throw DocumentValidationError("not a readable PDF: $exc")
    }

    document.use { doc ->
        if (doc.isEncrypted) throw DocumentValidationError("document is password-protected")
        val pageCount = doc.numberOfPages
        if (pageCount < 1) throw DocumentValidationError("document has no pages")
        return ValidatedDocument(path = path, pageCount = pageCount)
    }
}
