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
import com.printly.agent.printing.PrinterCondition
import com.printly.agent.printing.SpoolerOutcome
import com.printly.agent.printing.PrintSubmissionError
import com.printly.agent.printing.PrintSubmissionStalled
import com.printly.agent.printing.SpoolerOutcomePoller
import com.printly.agent.printing.discardHeldDocuments
import com.printly.agent.printing.downloadDocument
import com.printly.agent.printing.downloadDocumentTo
import com.printly.agent.printing.heldDocumentPath
import com.printly.agent.printing.storeHeldDocument
import com.printly.agent.printing.printPdf
import com.printly.agent.printing.validatePdf
import kotlinx.coroutines.CancellationException
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
 *                                            DOWNLOADED -> HELD -> DOWNLOADED (student arrived)
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

/**
 * The document is being handed to a printer driver, and may already be coming
 * out of it.
 *
 * This exists to mark where "nothing has printed yet" stops being true.
 * Without it a job read DOWNLOADED for the whole time it was printing -
 * `printPdf` blocks until the driver has finished the entire document, and
 * SUBMITTED was not reached until afterwards - so a job halfway through a
 * 300-page order looked, to [Database.resumableJobs], exactly like one that
 * had downloaded and never started. A restart replayed it, and the shop
 * printed the whole thing again on top of what was already in the tray.
 */
const val SUBMITTING = "SUBMITTING"

/**
 * The documents are on this machine's disk, and nothing is going to print
 * until the student walks in.
 *
 * A shop can accept an order before its student arrives. The backend creates a
 * job for it anyway - marked [com.printly.agent.models.PrintJobDetail.holdForArrival]
 * - precisely so the files can be fetched early, so that the print which
 * follows the counter scan starts from a local copy rather than from a download
 * begun at the worst possible moment, with somebody standing there waiting for
 * it.
 *
 * Deliberately not terminal. A held job has more to do than any other
 * non-printing state: it is waiting on a person, and [processHeldReleases] is
 * what eventually moves it on. It is also deliberately absent from
 * [Database.resumableJobs] - see the long note there, because a held job looks
 * exactly like an interrupted one and replaying it would print into an empty
 * shop.
 *
 * The only way out towards a printer is back through DOWNLOADED, which is not
 * a technicality. It means the release path re-joins the ordinary path at the
 * point the ordinary path starts from, so there is one piece of code that
 * hands documents to a driver and watches what happens, not two.
 */
const val HELD = "HELD"

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
    // No edge to SUBMITTED: everything reaches it through SUBMITTING, so the
    // state machine itself enforces that a job is never recorded as printing
    // without first being recorded as about to.
    DOWNLOADED to setOf(SUBMITTING, HELD, FAILED, CANCELLED),
    // No edge from HELD to SUBMITTING, and that omission is the guard. A held
    // job reaches a printer only by going back to DOWNLOADED first, which is
    // the release path announcing itself in the one place that cannot be
    // bypassed - a future shortcut straight to the printer would have to delete
    // this line to compile, rather than quietly working.
    HELD to setOf(DOWNLOADED, FAILED, CANCELLED),
    SUBMITTING to setOf(SUBMITTED, FAILED, CANCELLED, UNKNOWN),
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
    /** Where documents fetched ahead of a student's arrival live - see [com.printly.agent.core.Settings.heldDir]. */
    val heldDir: Path,
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
fun registerJobReference(
    ctx: JobContext,
    jobId: String,
    orderId: String,
    orderCode: String?,
    scheduledPrintAt: String? = null,
    priority: Boolean = false,
): Boolean {
    // [priority] is written down here rather than only handed to the queue
    // because this call is the only moment the agent is told it. A held-back
    // order is recorded now and enqueued when its slot comes due, by which
    // time the lookup that knew about the counter is long gone.
    val isNew = ctx.db.insertJobReference(jobId, orderId, orderCode, scheduledPrintAt, ctx.credential.shopId, priority)
    if (!isNew) {
        log.fine("duplicate_job_reference_ignored job=$jobId")
        return false
    }
    ctx.onEvent()

    if (scheduledPrintAt != null && scheduledPrintAt > nowIso()) {
        log.info("print_job_scheduled job=$jobId order=$orderId scheduledPrintAt=$scheduledPrintAt")
        return false
    }

    log.info("print_job_received job=$jobId order=$orderId")
    return true
}

/**
 * Records the reference and, if it is due, puts it in the print queue.
 *
 * The recording half and the printing half are deliberately separate calls.
 * They used to be one, run inside the print slot, so an order could not even
 * be written down until the previous one had finished printing - which is why
 * the queue was never ordered: there was never more than one thing in it to
 * sort. Intake now runs ahead of printing, and [PrintQueue] decides what
 * prints next by order number.
 *
 * A repeat delivery is not always nothing to do. In-shop priority is normally
 * granted *after* the order reaches the agent - the student ordered ahead and
 * has now walked in and scanned - so by the time the backend says so, the job
 * is already in the queue and the reference is a duplicate. That path dropped
 * the grant on the floor: the duplicate guard returned, and the person at the
 * counter went on waiting behind every lower number. The reconciliation poll
 * re-lists outstanding jobs every few seconds and looks the order up again,
 * so it is that pass which brings the scan here to be acted on.
 */
suspend fun handleJobReference(
    ctx: JobContext,
    queue: PrintQueue,
    jobId: String,
    orderId: String,
    orderCode: String?,
    scheduledPrintAt: String? = null,
    priority: Boolean = false,
) {
    if (registerJobReference(ctx, jobId, orderId, orderCode, scheduledPrintAt, priority)) {
        queue.enqueue(jobId, orderCode, priority) { processJob(ctx, jobId) }
        return
    }
    if (!priority) return

    // Already known, and the student has since scanned. Written down first so
    // that a job still held back for a later slot, or one a restart has yet to
    // resume, keeps the grant it is too early to act on - and only then moved
    // in the queue, if it is in one.
    val row = ctx.db.getJob(jobId) ?: return
    if (row.priority || row.state in TERMINAL) return
    ctx.db.markPriority(jobId)
    ctx.onEvent()
    if (queue.promote(jobId)) {
        log.info("in_shop_priority_granted job=$jobId order=$orderId")
    }
}

/**
 * Prints every job whose held-back time has now arrived - see
 * `handleJobReference`'s [scheduledPrintAt].
 *
 * Dispatches rather than awaiting: several scheduled slots commonly come due
 * in the same tick, and printing them one after another would make the last
 * one late by however long all the others took.
 *
 * [Database.JobRow.priority] is carried through, and that is the whole reason
 * it is a column. This path enqueued with the default - no priority - so an
 * order whose student had scanned at the counter arrived in the queue as an
 * ordinary one and went behind every lower number waiting. The scan had been
 * read correctly half an hour earlier and then thrown away at the only point
 * that could still act on it.
 */
fun processDueScheduledJobs(ctx: JobContext, queue: PrintQueue) {
    for (row in ctx.db.dueScheduledJobs(nowIso(), ctx.credential.shopId)) {
        if (queue.enqueue(row.jobId, row.orderCode, row.priority) { processJob(ctx, row.jobId) }) {
            log.info("scheduled_print_job_due job=${row.jobId} order=${row.orderId}")
        }
    }
}

/**
 * Re-runs the jobs a restart left stranded partway through.
 *
 * Without this they are stuck for good: the local row exists, so
 * [Database.insertJobReference] drops every later redelivery of that id as a
 * duplicate, and nothing else ever looks at it again. See
 * [Database.resumableJobs] for why only the pre-printing states qualify.
 */
fun resumeInterruptedJobs(ctx: JobContext, queue: PrintQueue) {
    for (row in ctx.db.resumableJobs(ctx.credential.shopId)) {
        // Rewind to the start rather than continuing from where it stopped.
        // [processJob] always begins by moving to VALIDATING, and the state
        // machine has no edge back to it from DOWNLOADING or DOWNLOADED - so
        // resuming in place would trip the illegal-transition guard and mark a
        // job FAILED that has not even been attempted. Replaying from the top
        // is safe precisely because none of these states has reached a
        // printer; the download is simply done again.
        //
        // The rewind belongs *inside* the submitted work, not before it. A job
        // this sweep finds mid-flight is dropped by [JobDispatcher.submit] as a
        // duplicate - but rewinding first happened anyway, pulling the running
        // job's state back to RECEIVED underneath it. That job then reached
        // DOWNLOADING from RECEIVED, tripped the guard, and was retried three
        // times and failed as "download failed after 3 attempts" - a job that
        // downloaded perfectly well and was never given the chance to print.
        // Startup is exactly when both happen at once: this sweep runs while
        // the stream is delivering today's jobs.
        // Priority survives the restart with the row, for the same reason
        // the scheduled path carries it: the order lookup that knew somebody
        // was at the counter ran in the process that died.
        val accepted = queue.enqueue(row.jobId, row.orderCode, row.priority) {
            ctx.db.updateJobState(row.jobId, RECEIVED)
            processJob(ctx, row.jobId)
        }
        if (accepted) log.info("resuming_interrupted_job job=${row.jobId} state=${row.state}")
    }
}

private fun nowIso(): String = Instant.now().toString()

suspend fun processJob(ctx: JobContext, jobId: String) {
    val row = ctx.db.getJob(jobId)
    if (row == null || row.state in TERMINAL) return

    val detail = try {
        claim(ctx, jobId) ?: return
    } catch (exc: CancellationException) {
        throw exc
    } catch (exc: Exception) {
        // Nothing has reached a printer yet, so the catch-all that used to sit
        // around the whole of this function still applies here unchanged.
        log.log(Level.SEVERE, "print_job_pipeline_error job=$jobId", exc)
        fail(ctx, jobId, "unexpected error: $exc", PrintJobFailureReason.UNKNOWN)
        return
    }
    val downloaded = downloadWithRetry(ctx, jobId, detail) ?: return

    if (detail.holdForArrival) {
        holdForArrival(ctx, jobId, detail, downloaded)
        return
    }

    printDownloaded(ctx, jobId, detail, downloaded)
}

/**
 * Everything from "the documents are on disk" to "the shop has been told what
 * came out of the printer".
 *
 * Split from [processJob] so that the release of a held order can run it
 * without a second copy existing. There is exactly one piece of code in this
 * agent that hands a document to a driver and then works out what happened, and
 * that is not tidiness: the duplicated version of it is how the same coursework
 * gets printed twice, because two copies drift and the one that drifts is
 * always the one nobody is testing. The held path arrives here having already
 * claimed and already downloaded, which is precisely the state the ordinary
 * path is in by the time it calls this.
 *
 * Both callers have their job in DOWNLOADED - the ordinary one having just
 * finished downloading, the release having just come back from HELD - so the
 * transition below is a no-op for one of them and the state machine's own
 * idempotence covers it.
 */
private suspend fun printDownloaded(
    ctx: JobContext,
    jobId: String,
    detail: PrintJobDetail,
    downloaded: Map<String, Path>,
) {
    // Whether anything has been handed to a printer driver yet. Past that
    // point no error may be reported as FAILED, however it arrives: the shop
    // reads FAILED as "print it again", and the pages are already out.
    var reachedPrinter = false

    try {
        val submissions: List<Pair<String, String>>
        try {
            transition(ctx, jobId, DOWNLOADED)
            pingProgress(ctx, jobId, PrintJobProgressStage.PRINTING)
            val printers = withContext(Dispatchers.IO) { discoverPrinters() }
            submissions = withContext(Dispatchers.IO) {
                selectAndPrint(detail, downloaded, printers) { printerName ->
                    // Fired once, immediately before the first page is handed
                    // to a driver. Everything after this point has to assume
                    // paper may already be moving.
                    reachedPrinter = true
                    transition(ctx, jobId, SUBMITTING, printerWindowsName = printerName)
                }
            }
        } catch (exc: DocumentValidationError) {
            fail(ctx, jobId, exc.message ?: "invalid document", PrintJobFailureReason.DOCUMENT_INVALID)
            return
        } catch (exc: NoCompatiblePrinterException) {
            fail(ctx, jobId, exc.message ?: "no compatible printer", PrintJobFailureReason.PRINTER_INCOMPATIBLE)
            return
        } catch (exc: PrintSubmissionError) {
            fail(ctx, jobId, exc.message ?: "print submission failed", PrintJobFailureReason.PRINTER_ERROR)
            return
        } catch (exc: PrintSubmissionStalled) {
            // Deliberately not a failure. The driver took the job and stopped
            // responding partway through, so pages may well be in the tray
            // already - reporting it failed is what would have the shop print
            // the whole thing again on top of what came out. Same rule as the
            // spooler's own UNKNOWN: only somebody who can look at the printer
            // can say what happened.
            markUnknown(ctx, jobId, exc.message ?: "the printer stopped responding")
            return
        } finally {
            downloaded.values.forEach { it.toFile().delete() }
            // A held order's files live in their own directory rather than in
            // the temp dir, so deleting the files alone would leave the
            // directory behind for every order a shop ever accepted early.
            // A no-op for a job that was never held.
            discardHeldDocuments(ctx.heldDir, jobId)
        }

        // The printer is recorded locally and reported at the same moment, so
        // the shop's own job list can say which machine took the order - and,
        // when something goes wrong later, which one to go and look at.
        val printerUsed = submissions.last().first
        transition(ctx, jobId, SUBMITTED, printerWindowsName = printerUsed)
        // Best-effort, deliberately. The driver has the document either way and
        // the outcome report that follows carries the real answer. Letting a
        // failure here escape would hand a printed order to the catch-all
        // below - and the backend is a cold-starting host, so a timeout on
        // this call is an ordinary event, not evidence anything went wrong.
        try {
            withContext(Dispatchers.IO) {
                ctx.api.reportStatus(ctx.credential, jobId, PrintJobStatus.PRINT_SUBMITTED, printerName = printerUsed)
            }
        } catch (exc: CancellationException) {
            throw exc
        } catch (exc: Exception) {
            log.log(Level.WARNING, "print_job_submitted_report_failed job=$jobId", exc)
        }

        // javax.print's blocking submit call only proves the driver accepted
        // the job - PRINTING's own outcome (COMPLETED/FAILED/UNKNOWN) is
        // decided by asking the spooler what actually happened, same rule as
        // the module kdoc above.
        transition(ctx, jobId, PRINTING)
        val result = withContext(Dispatchers.IO) {
            pollAllOutcomes(submissions, ctx.jobTimeoutSeconds) { condition ->
                // Recorded the moment it happens, so the shop's job list can
                // say "out of paper" while the job is still waiting rather
                // than only once it has timed out.
                if (condition != null) {
                    log.warning("print_job_blocked job=$jobId condition=${condition.name}")
                    ctx.db.updateJobState(jobId, PRINTING, lastError = "waiting: ${condition.description}")
                    ctx.db.recordEvent(jobId, "BLOCKED", condition.description)
                    ctx.onEvent()
                }
            }
        }

        when (result.outcome) {
            PrintOutcome.COMPLETED -> {
                transition(ctx, jobId, COMPLETED)
                try {
                    withContext(Dispatchers.IO) { ctx.api.reportStatus(ctx.credential, jobId, PrintJobStatus.PRINT_COMPLETED) }
                    log.info("print_job_completed job=$jobId")
                } catch (exc: CancellationException) {
                    throw exc
                } catch (exc: Exception) {
                    // The pages printed; the only thing that failed was saying
                    // so. Leaving it COMPLETED strands the order on the backend
                    // as forever-printing, with nothing left locally to move it
                    // on - COMPLETED is terminal here, so no sweep looks at it
                    // again. UNKNOWN is the state that means a person has to
                    // look, which is exactly what is wanted, and it is
                    // emphatically not FAILED.
                    markUnknown(ctx, jobId, "the pages printed, but the server could not be told: $exc")
                }
            }
            PrintOutcome.FAILED ->
                fail(ctx, jobId, "the printer reported an error after accepting the job", PrintJobFailureReason.PRINTER_ERROR)
            PrintOutcome.UNKNOWN ->
                // A stuck job is still in the queue and may yet print, so this
                // is never reported as failed - that is what would let the
                // shop reprint a page that then comes out anyway. Naming the
                // condition turns "go and look at the printer" into something
                // actionable.
                markUnknown(
                    ctx,
                    jobId,
                    result.condition
                        ?.let { "still waiting: ${it.description}. The job is queued and prints once this is fixed." }
                        ?: "could not confirm the print finished before the spooler check timed out",
                )
        }
    } catch (exc: CancellationException) {
        // Shutting down is not a print failure. CancellationException is an
        // IllegalStateException, so without this it lands in the catch-all
        // below and the agent marks a job FAILED on its way out of the door -
        // closing the app mid-print then has the shop reprint, in the morning,
        // a document that was already in the tray.
        throw exc
    } catch (exc: Exception) { // a bug here must not crash the agent process
        log.log(Level.SEVERE, "print_job_pipeline_error job=$jobId", exc)
        if (reachedPrinter) {
            markUnknown(ctx, jobId, "unexpected error after the document reached the printer: $exc")
        } else {
            fail(ctx, jobId, "unexpected error: $exc", PrintJobFailureReason.UNKNOWN)
        }
    }
}

/**
 * Puts a held order's documents somewhere durable, records that they are
 * there, and stops.
 *
 * The order of the three steps is the whole of the design. The files move
 * first, because everything afterwards is a claim that they exist. The local
 * state is written second, because that is what survives a restart and what
 * [processHeldReleases] reads. The report to the backend goes last and is
 * allowed to fail, because it is the only one of the three that somebody else
 * owns.
 *
 * That last point deserves its reason in full: `/cached` is what lights "Order
 * accepted" on the student's timeline. A report that does not land leaves them
 * looking at step one for longer than they should, which is a worse timeline
 * and not a worse outcome - the pages are on this disk either way and print
 * the moment they scan. The release loop re-sends it on every pass while the
 * hold stands, so a lost report costs one interval rather than being lost for
 * good, and the backend takes the first report and ignores the rest.
 *
 * Nothing here deletes anything. That is the point of the state.
 */
private suspend fun holdForArrival(
    ctx: JobContext,
    jobId: String,
    detail: PrintJobDetail,
    downloaded: Map<String, Path>,
) {
    try {
        withContext(Dispatchers.IO) {
            for (item in detail.items) {
                val source = downloaded[item.itemId] ?: continue
                storeHeldDocument(source, ctx.heldDir, jobId, item.itemId)
            }
        }
    } catch (exc: CancellationException) {
        throw exc
    } catch (exc: Exception) {
        // The files could not be put somewhere they will survive a restart, so
        // there is nothing to hold. Failing here is honest and safe - nothing
        // has printed - and the shop is told why rather than being left with an
        // order that claims to be ready and is not.
        fail(ctx, jobId, "could not store the documents for collection: $exc", PrintJobFailureReason.DOWNLOAD_FAILED)
        return
    }

    // Through DOWNLOADED rather than straight to HELD, because the download did
    // finish and because HELD is deliberately reachable from nowhere else - the
    // one edge into it is the same edge the ordinary path takes out of
    // downloading, so a held job and a printable one are the same job until the
    // moment this line runs. Going direct would need an edge from DOWNLOADING,
    // and an edge into HELD from a state where the files are not yet on disk is
    // exactly the thing that must not exist.
    transition(ctx, jobId, DOWNLOADED)
    transition(ctx, jobId, HELD)
    log.info("print_job_held_for_arrival job=$jobId order=${detail.orderId} items=${detail.items.size}")
    reportCached(ctx, jobId)
}

/**
 * Releases every held job whose student has since arrived, and re-reports the
 * ones still waiting.
 *
 * A poll rather than a push, and that is a decision rather than an omission.
 * The backend clears the hold when the student scans at the counter and sends
 * nothing to say so - but even if it did, this agent could not afford to
 * depend on it. An SSE connection can stop delivering without closing, and a
 * push dropped in that window would be an order that never prints at all until
 * somebody notices and restarts something, with the student standing at the
 * counter the entire time. Asking costs one request per held job per interval
 * and cannot be lost. It also covers the case no push can: an agent that was
 * switched off for the whole of the scan picks the release up when it comes
 * back, because the answer is a fact about the job rather than an event that
 * happened while nobody was listening.
 *
 * A job still held gets `/cached` sent again. That is not noise - it is how a
 * student whose first report was lost to a flaky connection still reaches step
 * two, and the backend's own idempotence is what makes repeating it free.
 */
suspend fun processHeldReleases(ctx: JobContext, queue: PrintQueue) {
    for (row in ctx.db.heldJobs(ctx.credential.shopId)) {
        val detail = try {
            withContext(Dispatchers.IO) { ctx.api.jobDetail(ctx.credential, row.jobId) }
        } catch (exc: CancellationException) {
            throw exc
        } catch (exc: Exception) {
            // Held jobs are asked about again on the next pass, so an
            // unreachable backend costs a delay and never a state change. The
            // documents are already on disk; nothing about them expires.
            log.fine("held_job_detail_failed job=${row.jobId}: $exc")
            continue
        }

        if (detail.holdForArrival) {
            reportCached(ctx, row.jobId)
            continue
        }

        // The student has scanned. Priority is not read from the row here: by
        // definition this order's student is at the counter right now, which is
        // the exact condition in-shop priority exists for, and the row may not
        // have been told yet - the grant arrives through a separate lookup that
        // this release has just overtaken.
        val accepted = queue.enqueue(row.jobId, row.orderCode, priority = true) {
            releaseHeldJob(ctx, row.jobId, detail)
        }
        if (accepted) log.info("held_job_released job=${row.jobId} order=${row.orderId}")
    }
}

/**
 * Prints a job whose hold has lifted, from the copy taken when the shop
 * accepted it.
 *
 * Rejoins the ordinary path at DOWNLOADED rather than running a print of its
 * own - see [printDownloaded]. The claim was done when the order was accepted
 * and must not be repeated; the documents are already here.
 */
private suspend fun releaseHeldJob(ctx: JobContext, jobId: String, detail: PrintJobDetail) {
    val row = ctx.db.getJob(jobId)
    if (row == null || row.state in TERMINAL) return

    val files = try {
        ensureHeldDocuments(ctx, jobId, detail)
    } catch (exc: CancellationException) {
        throw exc
    } catch (exc: Exception) {
        fail(ctx, jobId, "held documents could not be recovered: $exc", PrintJobFailureReason.DOWNLOAD_FAILED)
        return
    }

    transition(ctx, jobId, DOWNLOADED)
    printDownloaded(ctx, jobId, detail, files)
}

/**
 * The held files for a job, fetching again any that are no longer there.
 *
 * A held document can go missing between the shop accepting the order and the
 * student arriving, and none of the ways are exotic: a disk cleaner, a policy
 * that empties app data, a machine rebuilt over the weekend, somebody tidying
 * a folder. Failing the order in that moment would be the worst possible
 * answer - there is a person at the counter who has already paid, and the file
 * is still one signed URL away. A lost cache should cost them the download
 * they would have paid for anyway, not their order.
 *
 * Deliberately does no state transitions. This runs with the job in HELD, on
 * its way to DOWNLOADED, and the download states belong to the first fetch.
 * Moving through them again here would say something untrue about a job that
 * is being recovered rather than downloaded for the first time.
 */
private suspend fun ensureHeldDocuments(
    ctx: JobContext,
    jobId: String,
    detail: PrintJobDetail,
): Map<String, Path> {
    val existing = mutableMapOf<String, Path>()
    val missing = mutableListOf<com.printly.agent.models.PrintJobItem>()
    for (item in detail.items) {
        val path = heldDocumentPath(ctx.heldDir, jobId, item.itemId)
        if (java.nio.file.Files.isRegularFile(path) && path.toFile().length() > 0) existing[item.itemId] = path
        else missing.add(item)
    }
    if (missing.isEmpty()) return existing

    log.warning("held_documents_missing job=$jobId count=${missing.size} - fetching them again")
    val urls = withContext(Dispatchers.IO) { ctx.api.downloadUrls(ctx.credential, jobId) }
    val byDocumentId = urls.items.associateBy { it.documentId }
    for (item in missing) {
        val url = byDocumentId[item.documentId]?.url
            ?: throw DocumentValidationError("no download URL returned for document ${item.documentId}")
        existing[item.itemId] = withContext(Dispatchers.IO) {
            downloadDocumentTo(
                ctx.api.http,
                url,
                heldDocumentPath(ctx.heldDir, jobId, item.itemId),
                ctx.downloadTimeoutSeconds,
            )
        }
    }
    return existing
}

/** Best-effort by design - see [holdForArrival] for why a lost report is a worse timeline and not a worse outcome. */
private suspend fun reportCached(ctx: JobContext, jobId: String) {
    try {
        withContext(Dispatchers.IO) { ctx.api.reportCached(ctx.credential, jobId) }
    } catch (exc: CancellationException) {
        throw exc
    } catch (exc: Exception) {
        log.fine("print_job_cached_report_failed job=$jobId: $exc")
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
        } catch (exc: CancellationException) {
            // Also an IllegalStateException, so without this it is reported as
            // "job state no longer allows downloading: Job was cancelled" and
            // fails a download that was going perfectly well.
            throw exc
        } catch (exc: IllegalStateException) {
            // The state machine refused the move. Retrying cannot help - the
            // state will be the same next time - and doing so anyway spent
            // seven seconds of backoff before reporting the honest cause under
            // the wrong headline, "download failed after 3 attempts", for a
            // download that never began. Fail once, saying what happened.
            fail(ctx, jobId, "job state no longer allows downloading: ${exc.message}", PrintJobFailureReason.UNKNOWN)
            return null
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

/**
 * Returns `(windowsPrinterName, jobNameToken)` per item, in print order -
 * every one is polled before deciding the job's own outcome.
 *
 * [onAboutToPrint] is called exactly once, with the printer chosen for the
 * first item, in the moment between "nothing has been sent" and "something
 * has". Everything before it - validation, discovery, selection - can still be
 * replayed safely; nothing after it can.
 */
private suspend fun selectAndPrint(
    detail: PrintJobDetail,
    downloaded: Map<String, Path>,
    printers: List<LocalPrinter>,
    onAboutToPrint: suspend (printerName: String) -> Unit,
): List<Pair<String, String>> {
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
        if (submissions.isEmpty()) onAboutToPrint(printer.windowsPrinterName)
        val jobNameToken = printPdf(service, validated.path, options, validated.pageCount)
        submissions.add(printer.windowsPrinterName to jobNameToken)
    }

    if (submissions.isEmpty()) throw NoCompatiblePrinterException("job had no items")
    return submissions
}

/**
 * Any FAILED short-circuits immediately; COMPLETED only if every item resolved
 * COMPLETED; UNKNOWN if any item's outcome could not be confirmed.
 *
 * [onCondition] fires while a job is stuck on something a person can fix, so
 * the shop is told "out of paper" as it happens rather than after the timeout.
 */
private fun pollAllOutcomes(
    submissions: List<Pair<String, String>>,
    timeoutSeconds: Double,
    onCondition: (PrinterCondition?) -> Unit,
): SpoolerOutcome {
    var worst = SpoolerOutcome(PrintOutcome.COMPLETED)
    for ((printerName, jobNameToken) in submissions) {
        val result = SpoolerOutcomePoller.pollJobOutcome(
            printerName, jobNameToken, timeoutSeconds, onCondition = onCondition,
        )
        if (result.outcome == PrintOutcome.FAILED) return result
        // Keep the condition with the UNKNOWN it belongs to - it is the only
        // thing that tells a human what to go and fix.
        if (result.outcome == PrintOutcome.UNKNOWN) worst = result
    }
    return worst
}

private fun transition(ctx: JobContext, jobId: String, next: String, printerWindowsName: String? = null) {
    val row = ctx.db.getJob(jobId)
    // A missing row used to be treated as RECEIVED and written anyway. The
    // write is a plain UPDATE, so it silently did nothing, and every later
    // transition then reasoned from a state that was never stored - the kind
    // of desync that surfaces somewhere else entirely, as a job that failed
    // for a reason unrelated to what went wrong.
    if (row == null) throw IllegalStateException("no local row for job $jobId - cannot move it to $next")
    val current = row.state
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
