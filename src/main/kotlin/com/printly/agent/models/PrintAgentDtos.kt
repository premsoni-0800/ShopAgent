package com.printly.agent.models

import java.time.Instant

/**
 * Mirrors `com.printly.printagents.PrintAgentDtos` on the backend field-for-
 * field (same camelCase names, since both sides use Jackson) - see that file
 * before changing anything here. A mismatch fails silently as a 4xx, not a
 * type error, same risk the Python agent's own `models.py` calls out.
 */
enum class PrintJobStatus {
    CREATED, CLAIMED, PRINT_SUBMITTED,

    /** The agent submitted the job but could not confirm it printed before giving up watching. Never guessed. */
    PRINT_UNKNOWN,

    PRINT_COMPLETED, FAILED, CANCELLED
}

enum class ColorMode { BLACK_AND_WHITE, COLOR }
enum class DuplexMode { SINGLE_SIDED, DOUBLE_SIDED }
enum class PaperSize { A4, A3, LETTER, LEGAL }
enum class Orientation { PORTRAIT, LANDSCAPE }
enum class PrinterReportedStatus { UNKNOWN, READY, OFFLINE, ERROR }

/** A small, fixed vocabulary - mirrors the backend's own enum exactly; the backend turns this into the safe, friendly message a student sees. */
enum class PrintJobFailureReason { PRINTER_OFFLINE, PRINTER_INCOMPATIBLE, DOCUMENT_INVALID, DOWNLOAD_FAILED, PRINTER_ERROR, UNKNOWN }

/** Ephemeral, never-persisted live-tracing pings - see `PrintAgentDeviceService.reportProgress` on the backend. */
enum class PrintJobProgressStage { DOWNLOADING, PRINTING }

data class PairingCodeResponse(val code: String, val expiresAt: Instant)

/** One of a shop's registered agents, as the owner-facing list returns it. */
data class PrintAgentSummary(
    val id: String,
    val name: String,
    val status: String,
    val online: Boolean,
    val lastSeenAt: Instant?,
    val agentVersion: String?,
    val createdAt: Instant,
)

data class PrintAgentExchangeRequest(
    val code: String,
    val agentName: String = "Print Agent",
    val machineFingerprint: String? = null,
    val agentVersion: String? = null,
)

data class PrintAgentExchangeResponse(val agentId: String, val shopId: String, val secret: String)

data class PrintAgentHeartbeatRequest(val agentVersion: String? = null)

data class PrintAgentHeartbeatResponse(val autoPrintEnabled: Boolean, val serverTime: Instant)

data class AgentPrinter(
    val windowsPrinterName: String,
    val displayName: String,
    /** Null when the driver would not say reliably - never guessed. */
    val colorCapable: Boolean?,
    val duplexCapable: Boolean?,
    val paperSizes: Set<PaperSize> = emptySet(),
    val status: PrinterReportedStatus = PrinterReportedStatus.UNKNOWN,
)

data class PrinterSyncRequest(val printers: List<AgentPrinter> = emptyList())

data class PrintJobItem(
    val itemId: String,
    val documentId: String,
    val fileName: String,
    val paperSize: PaperSize,
    val colorMode: ColorMode,
    val duplexMode: DuplexMode,
    val orientation: Orientation,
    val copies: Int,
    val pageRange: String?,
    val documentPageCount: Int,
)

data class PrintJobDetail(
    val jobId: String,
    val orderId: String,
    val orderCode: String,
    val status: PrintJobStatus,
    val items: List<PrintJobItem>,
    /**
     * The shop accepted this order before its student walked in: fetch the
     * documents, say they are fetched, and print nothing.
     *
     * Defaulted to false rather than required, and that default is what lets
     * this agent talk to a backend that predates the field. Jackson leaves an
     * absent property at its default, so an older server - which sends no such
     * field and creates no such job - produces exactly the behaviour this agent
     * had before hold-for-arrival existed, with no version check anywhere.
     *
     * Implemented server-side as of backend main@a109e21, which is deployed.
     * The flag is on both job responses, `POST /print-agent/jobs/{jobId}/cached`
     * is served, and a shop accepting an order before its student arrives now
     * gets a job created with this set - so the hold branch in the pipeline is
     * live, where for its first several releases it was unreachable and never
     * once ran. A backend older than that still sends no such field, and the
     * false default still makes this agent behave exactly as it did then, with
     * no version check anywhere.
     *
     * The release is discovered by ASKING, and has to be - see
     * `processHeldReleases`. The agent SSE stream carries one event type,
     * print-job-available, and nothing about a student arriving reaches it.
     * The backend clears the flag when the student scans and pushes nothing;
     * the next poll of this job is what finds it cleared.
     */
    val holdForArrival: Boolean = false,
)

data class PrintJobSummary(
    val jobId: String,
    val orderId: String,
    val orderCode: String,
    val status: PrintJobStatus,
    val createdAt: Instant,
    /** See [PrintJobDetail.holdForArrival] - same flag, carried on the list form. */
    val holdForArrival: Boolean = false,
)

data class DownloadUrl(val documentId: String, val url: String, val expiresAt: Instant, val fileName: String)

data class PrintJobDownloadUrls(val items: List<DownloadUrl>)

data class PrintJobStatusUpdateRequest(
    val status: PrintJobStatus,
    val error: String? = null,
    val reasonCode: PrintJobFailureReason? = null,
    /** The Windows printer this job went to. Sent with PRINT_SUBMITTED so the shop's job list can name it. */
    val printerName: String? = null,
)

data class PrintJobProgressRequest(val stage: PrintJobProgressStage)

data class PrintAgentSseEvent(
    val event: String = "PRINT_JOB_AVAILABLE",
    val jobId: String,
    val orderId: String,
    val shopId: String,
)
