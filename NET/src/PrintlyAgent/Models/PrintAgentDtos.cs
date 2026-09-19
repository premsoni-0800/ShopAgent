namespace PrintlyAgent.Models;

/// <summary>
/// Mirrors <c>com.printly.printagents.PrintAgentDtos</c> on the backend
/// field-for-field (same camelCase names on the wire) - see that file before
/// changing anything here. A mismatch fails silently as a 4xx, not a type error.
///
/// Port of models/PrintAgentDtos.kt.
/// </summary>
public enum PrintJobStatus
{
    CREATED,
    CLAIMED,
    PRINT_SUBMITTED,

    /// <summary>
    /// The agent submitted the job but could not confirm it printed before
    /// giving up watching. Never guessed.
    /// </summary>
    PRINT_UNKNOWN,

    PRINT_COMPLETED,
    FAILED,
    CANCELLED
}

public enum ColorMode { BLACK_AND_WHITE, COLOR }

public enum DuplexMode { SINGLE_SIDED, DOUBLE_SIDED }

public enum PaperSize { A4, A3, LETTER, LEGAL }

public enum Orientation { PORTRAIT, LANDSCAPE }

public enum PrinterReportedStatus { UNKNOWN, READY, OFFLINE, ERROR }

/// <summary>
/// A small, fixed vocabulary - mirrors the backend's own enum exactly; the
/// backend turns this into the safe, friendly message a student sees.
/// </summary>
public enum PrintJobFailureReason
{
    PRINTER_OFFLINE,
    PRINTER_INCOMPATIBLE,
    DOCUMENT_INVALID,
    DOWNLOAD_FAILED,
    PRINTER_ERROR,
    UNKNOWN
}

/// <summary>
/// Ephemeral, never-persisted live-tracing pings - see
/// <c>PrintAgentDeviceService.reportProgress</c> on the backend.
/// </summary>
public enum PrintJobProgressStage { DOWNLOADING, PRINTING }

public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);

/// <summary>One of a shop's registered agents, as the owner-facing list returns it.</summary>
public sealed record PrintAgentSummary(
    string Id,
    string Name,
    string Status,
    bool Online,
    DateTimeOffset? LastSeenAt,
    string? AgentVersion,
    DateTimeOffset CreatedAt);

public sealed record PrintAgentExchangeRequest(
    string Code,
    string AgentName = "Print Agent",
    string? MachineFingerprint = null,
    string? AgentVersion = null);

public sealed record PrintAgentExchangeResponse(string AgentId, string ShopId, string Secret);

public sealed record PrintAgentHeartbeatRequest(string? AgentVersion = null);

public sealed record PrintAgentHeartbeatResponse(bool AutoPrintEnabled, DateTimeOffset ServerTime);

/// <summary>
/// One item on a print job: a document plus exactly how it is to be printed.
///
/// Field-for-field with the Kotlin data class, in the same order, including the
/// two that are easy to get wrong: PageRange is a nullable string rather than a
/// parsed structure because that is what the backend sends and because an empty
/// or unparseable range has to stay distinguishable from "all pages", and
/// DocumentPageCount is what an unparseable range falls back to.
/// </summary>
public sealed record PrintJobItem(
    string ItemId,
    string DocumentId,
    string FileName,
    PaperSize PaperSize,
    ColorMode ColorMode,
    DuplexMode DuplexMode,
    Orientation Orientation,
    int Copies,
    string? PageRange,
    int DocumentPageCount);

public sealed record PrintJobDetail(
    string JobId,
    string OrderId,
    string OrderCode,
    PrintJobStatus Status,
    List<PrintJobItem> Items,
    /// <summary>
    /// The shop accepted this order before its student walked in: fetch the
    /// documents, say they are fetched, and print nothing.
    ///
    /// <para>
    /// Optional with a false default rather than required, and that default is
    /// what lets this agent talk to a backend that predates the field. A
    /// constructor parameter the JSON does not mention keeps its default, so an
    /// older server - which sends no such field and creates no such job -
    /// produces exactly the behaviour this agent had before hold-for-arrival
    /// existed, with no version check anywhere.
    /// </para>
    ///
    /// <para>
    /// The backend clears it when the student scans at the counter, and there
    /// is no push for that; see <see cref="JobPipeline.ProcessHeldReleasesAsync"/>
    /// for why the agent goes and asks instead.
    /// </para>
    /// </summary>
    bool HoldForArrival = false);

public sealed record PrintJobSummary(
    string JobId,
    string OrderId,
    string OrderCode,
    PrintJobStatus Status,
    DateTimeOffset CreatedAt,
    /// <summary>See <see cref="PrintJobDetail.HoldForArrival"/> - same flag, carried on the list form.</summary>
    bool HoldForArrival = false);

public sealed record DownloadUrl(string DocumentId, string Url, DateTimeOffset ExpiresAt, string FileName);

public sealed record PrintJobDownloadUrls(List<DownloadUrl> Items);

public sealed record PrintJobStatusUpdateRequest(
    PrintJobStatus Status,
    string? Error = null,
    PrintJobFailureReason? ReasonCode = null,
    /// <summary>
    /// The Windows printer this job went to. Sent with PRINT_SUBMITTED so the
    /// shop's job list can name it.
    /// </summary>
    string? PrinterName = null);

public sealed record PrintJobProgressRequest(PrintJobProgressStage Stage);

public sealed record PrintAgentSseEvent(
    string JobId,
    string OrderId,
    string ShopId,
    string Event = "PRINT_JOB_AVAILABLE");

public sealed record AgentPrinter(
    string WindowsPrinterName,
    string DisplayName,
    /// <summary>Null when the driver would not say reliably - never guessed.</summary>
    bool? ColorCapable,
    bool? DuplexCapable,
    IReadOnlyCollection<PaperSize> PaperSizes,
    PrinterReportedStatus Status = PrinterReportedStatus.UNKNOWN);

public sealed record PrinterSyncRequest(IReadOnlyList<AgentPrinter> Printers);
