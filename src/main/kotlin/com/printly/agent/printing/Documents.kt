package com.printly.agent.printing

import okhttp3.OkHttpClient
import okhttp3.Request
import org.apache.pdfbox.Loader
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.nio.file.StandardOpenOption
import java.time.Duration
import java.util.UUID

class DocumentValidationError(message: String) : RuntimeException(message)

data class ValidatedDocument(val path: Path, val pageCount: Int)

/**
 * Streams a signed R2 URL to a restricted temp file - direct port of the
 * Python agent's `documents.py::download`. The caller is responsible for
 * deleting it once printing has finished, succeeded or not.
 */
fun downloadDocument(
    http: OkHttpClient,
    url: String,
    tempDir: Path,
    timeoutSeconds: Long,
    key: String? = null,
): Path {
    Files.createDirectories(tempDir)
    // Prefixed with this process's id so [sweepOrphanedDocuments] can tell a
    // file another running agent is still printing from one abandoned by an
    // agent that died.
    //
    // [key] is what makes a retry resume instead of starting again. A name
    // invented per call meant the second attempt at a 40MB document shared
    // nothing with the first: a new name, a new part file, and the same bytes
    // pulled over the same bad connection from zero - while the first attempt's
    // partial sat in the directory with nothing left that knew its name. Given
    // the item id, all three attempts are the same file and each one carries on
    // from where the last stopped.
    val pid = ProcessHandle.current().pid()
    val name = key?.let(::sanitiseId) ?: UUID.randomUUID().toString().replace("-", "")
    val destination = tempDir.resolve("$pid-$name.pdf")
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

    // Bytes land here first and are moved into place only once the transfer
    // finishes. Two things fall out of that, and both matter on a shop counter:
    // a half-written file is never mistaken for a document (the held store is
    // read by name, by a process that did not write it, possibly days later),
    // and what survives a dropped connection is a named, resumable remainder
    // rather than rubbish to be deleted.
    val part = destination.resolveSibling("${destination.fileName}.part")

    var have = if (Files.exists(part)) runCatching { Files.size(part) }.getOrDefault(0L) else 0L
    if (have > 0) {
        // Ask for the rest. A signed URL that has since expired, or a store
        // that ignores Range, is handled below rather than here - this is only
        // the request.
        val resumed = attempt(client, url, part, have, resume = true)
        if (resumed) {
            Files.move(part, destination, StandardCopyOption.REPLACE_EXISTING)
            return destination
        }
        // The server would not, or could not, continue where we left off.
        // Starting again is slower and always correct; carrying on from a
        // partial the server did not agree to would splice two different
        // responses into one file.
        Files.deleteIfExists(part)
        have = 0L
    }

    attempt(client, url, part, 0L, resume = false)
    Files.move(part, destination, StandardCopyOption.REPLACE_EXISTING)
    return destination
}

/**
 * One transfer into [part], appending when [resume] and the server agreed to it.
 *
 * @return false when a resume was asked for and refused, which is the caller's
 *   signal to start the file again. Any other failure throws: a download that
 *   cannot happen at all is the retry loop's business, not this function's.
 */
private fun attempt(client: OkHttpClient, url: String, part: Path, from: Long, resume: Boolean): Boolean {
    val request = Request.Builder().url(url)
        .apply { if (resume && from > 0) header("Range", "bytes=$from-") }
        .build()

    client.newCall(request).execute().use { response ->
        // 416 means the range is past the end of the object - the partial is as
        // long as, or longer than, the file the server is offering. That is a
        // stale or mismatched leftover, never something to append to.
        if (resume && response.code == 416) return false

        // 200 to a Range request means the store ignored it and is sending the
        // whole object from byte zero. Appending that would double the file.
        if (resume && response.code != 206) return false

        if (!response.isSuccessful) throw DocumentValidationError("download failed: HTTP ${response.code}")

        val body = response.body ?: throw DocumentValidationError("download failed: empty response")
        val append = resume && response.code == 206
        val options = if (append) {
            arrayOf(StandardOpenOption.WRITE, StandardOpenOption.APPEND)
        } else {
            arrayOf(StandardOpenOption.WRITE, StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING)
        }
        if (append && !Files.exists(part)) throw DocumentValidationError("partial download vanished mid-resume")
        if (!append) Files.deleteIfExists(part)
        Files.newOutputStream(part, *options).use { out -> body.byteStream().copyTo(out) }
        return true
    }
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
        // Both the finished documents and the half-transferred ones. A `.part`
        // is abandoned by exactly the same events - a crash, a kill, the
        // counter PC losing power - and left out of this it is a customer's
        // coursework accumulating in a temp directory for ever, just under a
        // different extension. Held documents are not reached: they live under
        // a sibling directory, and for them a dead owning process is the
        // ordinary case rather than a leak.
        Files.newDirectoryStream(tempDir).use { entries ->
            entries.forEach { entry ->
                val name = entry.fileName.toString()
                if (!name.endsWith(".pdf") && !name.endsWith(".pdf.part")) return@forEach
                val pid = name.substringBefore('-').toLongOrNull() ?: return@forEach
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
