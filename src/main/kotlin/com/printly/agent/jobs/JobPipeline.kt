package com.printly.agent.jobs

import com.printly.agent.credentials.CredentialStore
import com.printly.agent.db.Database
import com.printly.agent.models.PrintJobDetail
import com.printly.agent.models.PrintJobFailureReason
import com.printly.agent.models.PrintJobProgressStage
import com.printly.agent.models.PrintJobStatus
import com.printly.agent.net.ApiError
import com.printly.agent.net.PrintlyApiClient
import com.printly.agent.printers.LocalPrinter
import com.printly.agent.printers.discoverPrinters
import com.printly.agent.printers.selectPrinter
import com.printly.agent.printing.DocumentValidationError
import com.printly.agent.printing.PrintOptions
import com.printly.agent.printing.PrintOutcome
import com.printly.agent.printing.PrintSubmissionError
import com.printly.agent.printing.SpoolerOutcomePoller
import com.printly.agent.printing.downloadDocument
import com.printly.agent.printing.printPdf
import com.printly.agent.printing.validatePdf
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import java.nio.file.Path
import java.time.Instant
import java.util.logging.Level
import java.util.logging.Logger
import javax.print.PrintServiceLookup

/**
 * The local job pipeline: one handler shared by SSE pushes and reconciliation
 * polls - direct port of the Python agent's `jobs.py`.
 *
 * State machine (never a shortcut from RECEIVED straight to COMPLETED):
 *
 *   RECEIVED -> VALIDATING -> DOWNLOADING -> DOWNLOADED -> SUBMITTED -> PRINTING -> COMPLETED
 *                   |              | (bounded retry)                           -> FAILED
 *                   v              v                                           -> UNKNOWN
 *                FAILED         FAILED
 *   any non-terminal state -> CANCELLED (backend/reconciliation says the job is gone)
 *
 * Retries are bounded and apply only to DOWNLOADING (a transient network
 * failure) - a corrupt PDF, an incompatible printer, or a spooler error is
 * permanent and is never retried.
 *
 * PRINTING's own three-way branch is decided by [SpoolerOutcomePoller]
 * watching the spooler after `javax.print`'s blocking submit call returns -
 * COMPLETED and FAILED are the spooler's own definitive answer; UNKNOWN means
 * the answer never came before the agent gave up watching. UNKNOWN is
 * terminal *locally* (never retried) even though it is not one of the
 * backend's own terminal statuses: only a human at the shop, via the
 * backend's `PrintJobResolutionService`, can close it out. Reprinting a job
 * whose outcome is merely unknown risks the one thing this whole pipeline
 * exists to prevent - printing the same document twice.
 */
const val RECEIVED = "RECEIVED"
const val VALIDATING = "VALIDATING"
const val DOWNLOADING = "DOWNLOADING"
const val DOWNLOADED = "DOWNLOADED"
const val SUBMITTED = "SUBMITTED"
const val PRINTING = "PRINTING"
const val COMPLETED = "COMPLETED"
const val FAILED = "FAILED"
const val CANCELLED = "CANCELLED"

/** Terminal locally even though the backend's own PrintJobStatus has a matching PRINT_UNKNOWN to resolve it from. */
const val UNKNOWN = "UNKNOWN"

val TERMINAL = setOf(COMPLETED, FAILED, CANCELLED, UNKNOWN)

private val ALLOWED_NEXT: Map<String, Set<String>> = mapOf(
    RECEIVED to setOf(VALIDATING, CANCELLED),
    VALIDATING to setOf(DOWNLOADING, FAILED, CANCELLED),
    DOWNLOADING to setOf(DOWNLOADED, DOWNLOADING, FAILED, CANCELLED), // DOWNLOADING->DOWNLOADING is a bounded retry
    DOWNLOADED to setOf(SUBMITTED, FAILED, CANCELLED),
    SUBMITTED to setOf(PRINTING, FAILED),
    PRINTING to setOf(COMPLETED, FAILED, UNKNOWN),
)

fun canTransition(current: String, next: String): Boolean {
    if (current == next) return true // idempotent on repeat, same principle as the backend's own transitions
    return next in (ALLOWED_NEXT[current] ?: emptySet())
}

class NoCompatiblePrinterException(message: String) : RuntimeException(message)

data class JobContext(
    val api: PrintlyApiClient,
    val db: Database,
    val tempDir: Path,
    val maxRetryAttempts: Int,
    val downloadTimeoutSeconds: Long,
    val jobTimeoutSeconds: Double,
    val credential: CredentialStore.AgentCredential,
    /** Best-effort UI push - fired on every local state change. No-op in tests/contexts that don't care about the UI. */
    val onEvent: () -> Unit = {},
)

private val log = Logger.getLogger("com.printly.agent.jobs.JobPipeline")

/**
 * Entry point for both the SSE push and the reconciliation poll.
 * [Database.insertJobReference] is the whole of the duplicate-print
 * protection: a job id already known - whether COMPLETED, FAILED, or simply
 * still in flight from an earlier delivery of the same reference - is
 * dropped here before any printer is ever touched again.
 */
suspend fun handleJobReference(
    ctx: JobContext,
    jobId: String,
    orderId: String,
    orderCode: String?,
    scheduledPrintAt: String? = null,
) {
    val isNew = ctx.db.insertJobReference(jobId, orderId, orderCode, scheduledPrintAt)
    if (!isNew) {
        log.fine("duplicate_job_reference_ignored job=$jobId")
        return
    }
    ctx.onEvent()

    if (scheduledPrintAt != null && scheduledPrintAt > nowIso()) {
        log.info("print_job_scheduled job=$jobId order=$orderId scheduledPrintAt=$scheduledPrintAt")
        return
    }

    log.info("print_job_received job=$jobId order=$orderId")
    processJob(ctx, jobId)
}

/** Prints every job whose held-back time has now arrived - see `handleJobReference`'s [scheduledPrintAt]. */
suspend fun processDueScheduledJobs(ctx: JobContext) {
    for (row in ctx.db.dueScheduledJobs(nowIso())) {
        log.info("scheduled_print_job_due job=${row.jobId} order=${row.orderId}")
        processJob(ctx, row.jobId)
    }
}

private fun nowIso(): String = Instant.now().toString()

suspend fun processJob(ctx: JobContext, jobId: String) {
    val row = ctx.db.getJob(jobId)
    if (row == null || row.state in TERMINAL) return

    try {
        val detail = claim(ctx, jobId) ?: return
        val downloaded = downloadWithRetry(ctx, jobId, detail) ?: return

        val submissions: List<Pair<String, String>>
        try {
            transition(ctx, jobId, DOWNLOADED)
            pingProgress(ctx, jobId, PrintJobProgressStage.PRINTING)
            val printers = withContext(Dispatchers.IO) { discoverPrinters() }
            submissions = withContext(Dispatchers.IO) { selectAndPrint(detail, downloaded, printers) }
        } catch (exc: DocumentValidationError) {
            fail(ctx, jobId, exc.message ?: "invalid document", PrintJobFailureReason.DOCUMENT_INVALID)
            return
        } catch (exc: NoCompatiblePrinterException) {
            fail(ctx, jobId, exc.message ?: "no compatible printer", PrintJobFailureReason.PRINTER_INCOMPATIBLE)
            return
        } catch (exc: PrintSubmissionError) {
            fail(ctx, jobId, exc.message ?: "print submission failed", PrintJobFailureReason.PRINTER_ERROR)
            return
        } finally {
            downloaded.values.forEach { it.toFile().delete() }
        }

        transition(ctx, jobId, SUBMITTED, printerWindowsName = submissions.last().first)
        withContext(Dispatchers.IO) { ctx.api.reportStatus(ctx.credential, jobId, PrintJobStatus.PRINT_SUBMITTED) }

        // javax.print's blocking submit call only proves the driver accepted
        // the job - PRINTING's own outcome (COMPLETED/FAILED/UNKNOWN) is
        // decided by asking the spooler what actually happened, same rule as
        // the module kdoc above.
        transition(ctx, jobId, PRINTING)
        val outcome = withContext(Dispatchers.IO) { pollAllOutcomes(submissions, ctx.jobTimeoutSeconds) }

        when (outcome) {
            PrintOutcome.COMPLETED -> {
                transition(ctx, jobId, COMPLETED)
                withContext(Dispatchers.IO) { ctx.api.reportStatus(ctx.credential, jobId, PrintJobStatus.PRINT_COMPLETED) }
                log.info("print_job_completed job=$jobId")
            }
            PrintOutcome.FAILED ->
                fail(ctx, jobId, "the printer reported an error after accepting the job", PrintJobFailureReason.PRINTER_ERROR)
            PrintOutcome.UNKNOWN ->
                markUnknown(ctx, jobId, "could not confirm the print finished before the spooler check timed out")
        }
    } catch (exc: Exception) { // a bug here must not crash the agent process
        log.log(Level.SEVERE, "print_job_pipeline_error job=$jobId", exc)
        fail(ctx, jobId, "unexpected error: $exc", PrintJobFailureReason.UNKNOWN)
    }
}

private suspend fun claim(ctx: JobContext, jobId: String): PrintJobDetail? {
    transition(ctx, jobId, VALIDATING)
    return try {
        withContext(Dispatchers.IO) { ctx.api.claimJob(ctx.credential, jobId) }
    } catch (exc: ApiError) {
        if (exc.code == "PRINT_JOB_ALREADY_CLAIMED") {
            // Another delivery of the same reference beat this one to it - not
            // an error, just nothing left for this call to do.
            ctx.db.updateJobState(jobId, CANCELLED, lastError = "already claimed elsewhere")
            null
        } else {
            throw exc
        }
    }
}

private suspend fun downloadWithRetry(ctx: JobContext, jobId: String, detail: PrintJobDetail): Map<String, Path>? {
    for (attempt in 1..ctx.maxRetryAttempts) {
        try {
            return downloadAll(ctx, jobId, detail)
        } catch (exc: Exception) {
            ctx.db.recordEvent(jobId, "DOWNLOAD_FAILED", "attempt $attempt: $exc")
            if (attempt == ctx.maxRetryAttempts) {
                fail(ctx, jobId, "download failed after $attempt attempts: $exc", PrintJobFailureReason.DOWNLOAD_FAILED)
                return null
            }
            delay(minOf(30, 1 shl attempt) * 1000L)
        }
    }
    return null
}

private suspend fun downloadAll(ctx: JobContext, jobId: String, detail: PrintJobDetail): Map<String, Path> {
    transition(ctx, jobId, DOWNLOADING)
    pingProgress(ctx, jobId, PrintJobProgressStage.DOWNLOADING)
    val urls = withContext(Dispatchers.IO) { ctx.api.downloadUrls(ctx.credential, jobId) }
    val byDocumentId = urls.items.associateBy { it.documentId }

    val downloaded = mutableMapOf<String, Path>()
    for (item in detail.items) {
        val url = byDocumentId[item.documentId]?.url
            ?: throw DocumentValidationError("no download URL returned for document ${item.documentId}")
        downloaded[item.itemId] = withContext(Dispatchers.IO) {
            downloadDocument(ctx.api.http, url, ctx.tempDir, ctx.downloadTimeoutSeconds)
        }
    }
    return downloaded
}

/** Returns `(windowsPrinterName, jobNameToken)` per item, in print order - every one is polled before deciding the job's own outcome. */
private fun selectAndPrint(detail: PrintJobDetail, downloaded: Map<String, Path>, printers: List<LocalPrinter>): List<Pair<String, String>> {
    val submissions = mutableListOf<Pair<String, String>>()
    val services = PrintServiceLookup.lookupPrintServices(null, null).associateBy { it.name }

    for (item in detail.items) {
        val validated = validatePdf(downloaded.getValue(item.itemId))
        val selection = selectPrinter(item, printers)
        val printer = selection.printer
            ?: throw NoCompatiblePrinterException("no compatible printer for item ${item.itemId}: ${selection.reason}")
        val service = services[printer.windowsPrinterName]
            ?: throw NoCompatiblePrinterException("printer ${printer.windowsPrinterName} not found in javax.print registry")

        val options = PrintOptions(item.colorMode, item.duplexMode, item.paperSize, item.copies, item.pageRange)
        val jobNameToken = printPdf(service, validated.path, options, validated.pageCount)
        submissions.add(printer.windowsPrinterName to jobNameToken)
    }

    if (submissions.isEmpty()) throw NoCompatiblePrinterException("job had no items")
    return submissions
}

/** Any FAILED short-circuits immediately; COMPLETED only if every item resolved COMPLETED; UNKNOWN if any item's outcome could not be confirmed. */
private fun pollAllOutcomes(submissions: List<Pair<String, String>>, timeoutSeconds: Double): PrintOutcome {
    var worst = PrintOutcome.COMPLETED
    for ((printerName, jobNameToken) in submissions) {
        val outcome = SpoolerOutcomePoller.pollJobOutcome(printerName, jobNameToken, timeoutSeconds)
        if (outcome == PrintOutcome.FAILED) return PrintOutcome.FAILED
        if (outcome == PrintOutcome.UNKNOWN) worst = PrintOutcome.UNKNOWN
    }
    return worst
}

private fun transition(ctx: JobContext, jobId: String, next: String, printerWindowsName: String? = null) {
    val row = ctx.db.getJob(jobId)
    val current = row?.state ?: RECEIVED
    if (!canTransition(current, next)) throw IllegalStateException("illegal local transition $current -> $next for job $jobId")
    ctx.db.updateJobState(jobId, next, printerWindowsName = printerWindowsName)
    ctx.db.recordEvent(jobId, next)
    ctx.onEvent()
}

private suspend fun fail(ctx: JobContext, jobId: String, error: String, reasonCode: PrintJobFailureReason) {
    val trimmed = error.take(500)
    ctx.db.updateJobState(jobId, FAILED, lastError = trimmed, incrementAttempt = true)
    ctx.db.recordEvent(jobId, FAILED, trimmed)
    ctx.onEvent()
    log.warning("print_job_failed job=$jobId")
    try {
        withContext(Dispatchers.IO) {
            // `error` is free text for shop/ops diagnostics only; `reasonCode`
            // is the only part of this report that ever reaches the student.
            ctx.api.reportStatus(ctx.credential, jobId, PrintJobStatus.FAILED, error = trimmed, reasonCode = reasonCode)
        }
    } catch (exc: Exception) {
        log.log(Level.SEVERE, "print_job_failure_report_failed job=$jobId", exc)
    }
}

/** Terminal locally - never retried, never reprocessed on the next reconnect. Only the backend's PrintJobResolutionService, driven by a human, moves this job on. */
private suspend fun markUnknown(ctx: JobContext, jobId: String, note: String) {
    val trimmed = note.take(500)
    ctx.db.updateJobState(jobId, UNKNOWN, lastError = trimmed)
    ctx.db.recordEvent(jobId, UNKNOWN, trimmed)
    ctx.onEvent()
    log.warning("print_job_unknown job=$jobId")
    try {
        withContext(Dispatchers.IO) { ctx.api.reportStatus(ctx.credential, jobId, PrintJobStatus.PRINT_UNKNOWN, error = trimmed) }
    } catch (exc: Exception) {
        log.log(Level.SEVERE, "print_job_unknown_report_failed job=$jobId", exc)
    }
}

/** Best-effort live-tracing signal - never part of the pipeline's own error handling. */
private suspend fun pingProgress(ctx: JobContext, jobId: String, stage: PrintJobProgressStage) {
    try {
        withContext(Dispatchers.IO) { ctx.api.reportProgress(ctx.credential, jobId, stage) }
    } catch (exc: Exception) {
        log.fine("print_job_progress_report_failed job=$jobId: $exc")
    }
}
