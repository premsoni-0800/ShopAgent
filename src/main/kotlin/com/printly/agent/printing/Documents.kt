package com.printly.agent.printing

import okhttp3.OkHttpClient
import okhttp3.Request
import org.apache.pdfbox.Loader
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
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
    return downloadDocumentTo(http, url, destination, timeoutSeconds)
}

/**
 * The same download, to a caller-chosen path.
 *
 * Split out for the held-document store, whose filenames have to be worked out
 * rather than invented: the process that eventually prints a held order is very
 * often not the one that fetched it, so there is nobody left to remember a
 * random name. Kept as the one implementation both paths call rather than a
 * second copy, because "downloads a customer's document" is not a thing worth
 * having two of.
 */
fun downloadDocumentTo(http: OkHttpClient, url: String, destination: Path, timeoutSeconds: Long): Path {
    destination.parent?.let { Files.createDirectories(it) }
    val client = http.newBuilder().callTimeout(Duration.ofSeconds(timeoutSeconds)).build()
    val request = Request.Builder().url(url).build()
    client.newCall(request).execute().use { response ->
        if (!response.isSuccessful) throw DocumentValidationError("download failed: HTTP ${response.code}")
        Files.newOutputStream(destination).use { out -> response.body?.byteStream()?.copyTo(out) }
    }
    return destination
}

/**
 * Where a held order's document lives: one directory per job, one file per
 * item, both named after ids the backend already gave us.
 *
 * Deterministic on purpose, and it is the whole reason this is a function
 * rather than a name chosen at download time. A held order is fetched in the
 * morning and printed after lunch, very often by a different process - the
 * shop closes the app, Windows updates, the counter PC is restarted. Anything
 * remembered only in memory is gone by then; anything random needs an index to
 * find it again, which is one more thing that can disagree with the disk. The
 * job id and the item id are both already in the local row and in the job
 * detail, so the path can simply be recomputed whenever it is wanted.
 */
fun heldDocumentPath(heldDir: Path, jobId: String, itemId: String): Path =
    heldDir.resolve(sanitiseId(jobId)).resolve("${sanitiseId(itemId)}.pdf")

/**
 * Moves a freshly downloaded document into the held store.
 *
 * A move rather than a copy, so there is never a window in which the same
 * customer document exists twice on a shop's disk, and so the temp copy cannot
 * be left behind for the orphan sweep to find. ATOMIC_MOVE is not requested:
 * both directories are under the same app-data root in every real install, but
 * a move that cannot be atomic must still succeed rather than throw, and the
 * replace is what makes re-fetching a missing file idempotent.
 */
fun storeHeldDocument(source: Path, heldDir: Path, jobId: String, itemId: String): Path {
    val destination = heldDocumentPath(heldDir, jobId, itemId)
    Files.createDirectories(destination.parent)
    Files.move(source, destination, StandardCopyOption.REPLACE_EXISTING)
    return destination
}

/**
 * Deletes a job's held directory once its pages have gone to a printer.
 *
 * Best-effort and silent: the documents have printed by the time this runs, so
 * a file that cannot be removed is a housekeeping problem, not something to
 * fail an order over. A job that was never held has no directory here, which is
 * why the normal print path can call this unconditionally.
 */
fun discardHeldDocuments(heldDir: Path, jobId: String) {
    val dir = heldDir.resolve(sanitiseId(jobId))
    runCatching {
        if (!Files.isDirectory(dir)) return
        Files.newDirectoryStream(dir).use { entries -> entries.forEach { runCatching { Files.deleteIfExists(it) } } }
        Files.deleteIfExists(dir)
    }
}

/**
 * Keeps an id to the characters that are safe in a path segment.
 *
 * These ids are server-generated and have never been anything but hex and
 * dashes, so in practice this changes nothing - it is here because they are
 * used to build a filesystem path, and a path built from a value this process
 * did not choose is worth being uninteresting about. A stray separator would
 * otherwise write a customer's document somewhere other than the held store.
 */
private fun sanitiseId(id: String): String = id.map { if (it.isLetterOrDigit() || it == '-' || it == '_') it else '_' }.joinToString("")

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
 *
 * Reaches only [tempDir] itself, and not one level down. That is what keeps
 * held documents safe without this function needing to know they exist: they
 * live under `Settings.heldDir`, a sibling directory, and for them a dead
 * owning process means the agent was restarted between the shop accepting an
 * order and its student arriving - which is the ordinary case, not a leak.
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
