namespace PrintlyAgent.Models;

/// <summary>
/// The owner-facing half of the backend's API, mirrored field-for-field the way
/// <see cref="PrintJobDetail"/> and its neighbours mirror the device half.
///
/// <para>
/// These are the routes an owner's own session reaches - the shop, its orders,
/// its printers, its settings, its batches and its reports - as opposed to the
/// <c>/api/v1/print-agent/*</c> routes the agent process calls with its own
/// bearer. The two sets never mix: see the note on
/// <see cref="Net.PrintlyApiClient"/> about why sending the wrong credential at
/// the wrong route is a 401 in the good case.
/// </para>
///
/// <para>
/// Ported from the Kotlin backend in <c>API'S/printly/src/main/kotlin</c>. The
/// mapping is mechanical and worth stating once, because getting any of it
/// wrong fails as a silent null rather than a type error:
/// </para>
/// <list type="bullet">
///   <item>Kotlin <c>UUID</c> becomes <c>string</c>, matching the ids already
///   carried by the device DTOs - the agent never does arithmetic on one.</item>
///   <item><c>Instant</c> becomes <c>DateTimeOffset</c>, <c>LocalDate</c>
///   becomes <c>DateOnly</c> and <c>LocalTime</c> becomes <c>TimeOnly</c>; all
///   three round-trip as the ISO strings Jackson writes.</item>
///   <item><c>BigDecimal</c> becomes <c>decimal</c> and never <c>double</c>.
///   These are rupee amounts that get summed and reconciled against
///   settlements, and binary floating point would drift.</item>
///   <item><c>URI</c> becomes <c>string</c>, left unparsed - a presigned URL is
///   something to hand to the downloader, not to inspect.</item>
///   <item>A nullable Kotlin field stays nullable here. Several of them carry a
///   real third answer rather than a missing one, the way
///   <see cref="AgentPrinter.ColorCapable"/> does.</item>
/// </list>
/// </summary>
public enum OrderStatus
{
    DRAFT,
    PAYMENT_PENDING,
    PAID,
    SHOP_RECEIVED,
    SCHEDULED,
    PRINTING,
    PRINTED,
    READY,
    COLLECTED,
    COMPLETED,
    DELAYED,
    CANCELLED,
    FAILED,
    REFUND_PENDING,
    REFUNDED
}

/// <summary>
/// The customer-visible progress of an order, which is deliberately not
/// <see cref="OrderStatus"/>: several statuses collapse into one stage, and the
/// backend owns that mapping. Read it, never derive it.
/// </summary>
public enum OrderStage
{
    ORDER_PLACED,
    PAYMENT_CONFIRMED,
    QUEUED,
    ACCEPTED,
    QR_SCANNED,
    PRINTING,
    READY_FOR_PICKUP,
    COLLECTED,
    COMPLETED,
    CANCELLED,
    FAILED,
    REFUNDED
}

public enum PaymentMethod { WALLET, GATEWAY }

public enum PrintQuality { DRAFT, NORMAL, HIGH }

public enum PageRangeMode { ALL, CUSTOM }

public enum RecordStatus { ACTIVE, INACTIVE, SUSPENDED, ARCHIVED }

public enum FinishingService
{
    NONE,
    SPIRAL_BINDING,
    SOFT_BINDING,
    HARD_BINDING,
    STAPLING,
    LAMINATION,
    HOLE_PUNCH
}

public enum ChargeUnit { PER_ORDER, PER_PAGE, PER_COPY }

public enum BatchStatus
{
    CREATED,
    QUEUED,
    GENERATING,
    READY,
    PRINTING,
    PRINTED,
    COMPLETED,
    FAILED,
    CANCELLED
}

public enum SettlementStatus { PENDING, PROCESSING, SETTLED, FAILED, ON_HOLD }

public enum ContentReportReason { PORNOGRAPHIC, ABUSIVE, ILLEGAL, COPYRIGHT, SPAM, OTHER }

public enum ContentReportStatus { OPEN, UNDER_REVIEW, RESOLVED, DISMISSED }

public enum ContentReportResolution { NO_ACTION, CUSTOMER_WARNED, CUSTOMER_SUSPENDED, ORDER_CANCELLED }

public enum ReportPeriod
{
    TODAY,
    YESTERDAY,
    LAST_7_DAYS,
    THIS_WEEK,
    LAST_WEEK,
    THIS_MONTH,
    LAST_MONTH,

    /// <summary>Requires both `from` and `to`; the backend rejects it without them.</summary>
    CUSTOM
}

public enum ReportType { SALES, DAILY, SHOP, COMMISSION, SETTLEMENTS, BATCHES }

public enum ExportJobStatus { QUEUED, RUNNING, READY, FAILED, EXPIRED }

public enum UserRole { STUDENT, SHOP_OWNER, SHOP_STAFF, ADMIN, SUPER_ADMIN }

// --- paging ------------------------------------------------------------------

/// <summary>
/// The backend's one paging envelope, used by every list route that has one.
///
/// Generic rather than a per-type copy because the shape really is identical
/// across orders, batches, products, settlements and reports - and because
/// HasNext is the field to loop on. TotalPages is derived from a count query
/// that some of these routes deliberately approximate.
/// </summary>
public sealed record PageResponse<T>(
    List<T> Items,
    int Page,
    int Size,
    long TotalItems,
    int TotalPages,
    bool HasNext);

// --- orders ------------------------------------------------------------------

public sealed record OrderItemResponse(
    string Id,
    string DocumentId,
    string FileName,
    PaperSize PaperSize,
    ColorMode ColorMode,
    DuplexMode DuplexMode,
    Orientation Orientation,
    int Copies,
    string? PageRange,
    int DocumentPageCount,
    int PagesSelected,
    int PrintedPages,
    int Sheets,
    decimal PricePerPage,
    decimal SetupFee,
    decimal LineAmount);

public sealed record OrderProductResponse(
    string Id,
    string ProductId,
    string Name,
    decimal UnitPrice,
    int Quantity,
    decimal LineAmount);

public sealed record QuoteServiceResponse(
    FinishingService Service,
    string DisplayName,
    string ChargeUnit,
    decimal UnitPrice,
    int Quantity,
    decimal Amount);

/// <summary>What the platform kept, split the way the settlement report splits it.</summary>
public sealed record PlatformFeeBreakdown(
    decimal BlackAndWhite = 0m,
    decimal Color = 0m,
    decimal Product = 0m);

/// <summary>
/// Only populated on shop and admin reads - a student reading their own order
/// gets null here, which is why every field is nullable rather than the record
/// itself carrying a guarantee it cannot make.
/// </summary>
public sealed record OrderCustomerResponse(
    string Id,
    string? Name,
    string? Phone,
    string? Email,
    string? CampusId,
    string? CollegeUid = null);

/// <summary>
/// One order, in full, as the shop sees it.
///
/// <para>
/// Two identifier fields that are not interchangeable: <c>Id</c> is the UUID
/// that goes in URLs and is never shown, and <c>OrderId</c> is the display code
/// - "SH001-000127" - that every client prints verbatim rather than formatting
/// itself. <c>CounterCode</c> is neither: it is the last four digits of the
/// customer's mobile, for calling an order out across a counter, and it is not
/// unique. Never look an order up by it.
/// </para>
///
/// <para>
/// <c>Version</c> is bumped on every status change, and <c>ServerTime</c> is
/// the backend's clock at the moment it answered - the pair is what lets a
/// countdown on a shop screen stay honest without trusting the local clock.
/// </para>
/// </summary>
public sealed record OrderResponse(
    string Id,
    string OrderId,
    long OrderNumber,
    string? CounterCode,
    string ShopId,
    string? ShopCode,
    string? ShopName,
    string UserId,
    string? UserName,
    OrderStatus Status,
    string StatusDisplay,
    int Version,
    List<OrderItemResponse> Items,
    List<QuoteServiceResponse> Services,
    List<OrderProductResponse> Products,
    int TotalPages,
    int TotalSheets,
    int BlackAndWhitePages,
    int ColourPages,
    decimal PrintAmount,
    decimal ServiceAmount,
    decimal ProductAmount,
    decimal ShopCollectsAmount,
    decimal PlatformFee,
    PlatformFeeBreakdown? PlatformFeeBreakdown,
    decimal TaxAmount,
    decimal DiscountAmount,
    string? CouponCode,
    decimal TotalAmount,
    string Currency,
    PaymentMethod? PaymentMethod,
    DateTimeOffset? ScheduledSlotStart,
    DateTimeOffset? ScheduledSlotEnd,
    DateTimeOffset? ScheduledPrintAt,
    bool InShopPriority,
    DateTimeOffset? PriorityGrantedAt,
    DateTimeOffset? ShopReleaseAt,
    bool HeldFromShop,
    DateTimeOffset? ReleasedToShopAt,
    string? CustomerNote,
    DateTimeOffset? PaidAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? CancelledAt,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    OrderStage Stage,
    string StageDisplay,
    int? QueuePosition,
    int? OrdersAhead,
    DateTimeOffset? EstimatedStartAt,
    DateTimeOffset? EstimatedReadyAt,
    DateTimeOffset? PrintingStartedAt,
    DateTimeOffset? PrintingExpectedAt,
    DateTimeOffset ServerTime,
    OrderCustomerResponse? Customer);

public sealed record OrderSummaryResponse(
    string Id,
    string OrderId,
    long OrderNumber,
    string ShopId,
    string? ShopName,
    string UserId,
    OrderStatus Status,
    string StatusDisplay,
    OrderStage Stage,
    string StageDisplay,
    List<OrderItemResponse> Items,
    int TotalPages,
    decimal TotalAmount,
    string Currency,
    decimal PlatformFee,
    PlatformFeeBreakdown? PlatformFeeBreakdown,
    PaymentMethod? PaymentMethod,
    DateTimeOffset? ScheduledSlotStart,
    bool InShopPriority,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? ReadyAt,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? EstimatedReadyAt,
    int? QueuePosition,
    int? OrdersAhead);

/// <summary>
/// The optional note on accept / start-printing / mark-printed / mark-ready /
/// collect / delay.
///
/// The body is optional on every one of those routes, and the proxy in
/// <c>WebUiServer</c> has a long comment about why sending an empty one is
/// worse than sending none. Pass null and the client omits the body entirely.
/// </summary>
public sealed record ShopOrderActionRequest(string? Note = null);

/// <summary>The four-to-eight character code the student shows at the counter.</summary>
public sealed record CollectionVerificationRequest(string Code);

/// <summary>
/// <c>CodeAvailable</c> false means the order has no collection code to check
/// against at all - which is not the same as a code that did not match, and the
/// counter has to say something different in each case.
/// </summary>
public sealed record CollectionVerificationResponse(bool Verified, bool CodeAvailable = true);

/// <summary>Only the three fields the shop may override; everything else is the student's choice.</summary>
public sealed record ItemPrintSettings(
    string ItemId,
    PaperSize? PaperSize = null,
    DuplexMode? DuplexMode = null,
    Orientation? Orientation = null);

public sealed record UpdateOrderPrintSettingsRequest(IReadOnlyList<ItemPrintSettings> Items);

/// <summary>
/// How a manual print went, reported by whoever drove it.
///
/// <para>
/// <c>Success</c> is deliberately not nullable, though the Kotlin field is.
/// There it is <c>Boolean?</c> carrying <c>@NotNull</c> - nullable only so that
/// an omitted key fails validation instead of binding to the JVM primitive
/// default, because guessing false would park a printed order as DELAYED. The
/// contract is still that a value must be sent, so modelling it as
/// <c>bool?</c> here would let a caller build a request that is always a 400 -
/// and worse, the client's WhenWritingNull policy would drop the key rather
/// than send the null, which is the exact omission the backend is guarding
/// against.
/// </para>
/// </summary>
public sealed record OrderPrintOutcomeRequest(
    bool Success,
    string? PrinterInfo = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// One transition in an order's history. <c>ActorType</c> says who caused it -
/// the student, the shop, an admin or the system itself - which is why a
/// timeline can show a cancellation without implying the shop did it.
/// </summary>
public sealed record OrderTimelineEntry(
    OrderStatus Status,
    string StatusDisplay,
    OrderStatus? PreviousStatus,
    string ActorType,
    string? Reason,
    DateTimeOffset At);

/// <summary>A presigned link to one document, good until <c>ExpiresAt</c>.</summary>
public sealed record DownloadUrlResponse(
    string DocumentId,
    string Url,
    DateTimeOffset ExpiresAt,
    string FileName);

/// <summary>
/// A bookable window. <c>UnavailableReason</c> is the sentence to show when
/// <c>Available</c> is false - a full slot and a closed shop are both
/// unavailable and the student needs to be told which.
/// </summary>
public sealed record SlotResponse(
    DateTimeOffset SlotStart,
    DateTimeOffset SlotEnd,
    bool Available,
    int RemainingOrders,
    int RemainingPages,
    string? UnavailableReason);

// --- print jobs, owner side --------------------------------------------------

/// <summary>
/// A print job as the shop's own screen lists it - the counterpart to
/// <see cref="PrintJobSummary"/>, which is what the agent itself sees.
///
/// <c>NeedsAttention</c> is the whole point of the route: it is what the
/// backend has already decided a human must look at, so a client shows that
/// flag rather than re-deriving it from Status and RetryCount.
/// </summary>
public sealed record PrintJobOwnerResponse(
    string JobId,
    string OrderId,
    string OrderCode,
    PrintJobStatus Status,
    bool NeedsAttention,
    PrintJobFailureReason? ReasonCode,
    string? LastError,
    int RetryCount,
    string? AgentName,
    string? PrinterName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Closing out a job a person had to deal with. <c>Success</c> is required and
/// non-nullable for the same reason it is on
/// <see cref="OrderPrintOutcomeRequest"/>.
/// </summary>
public sealed record ResolvePrintJobRequest(bool Success, string? Note = null);

// --- shop settings -----------------------------------------------------------

public sealed record NotificationSettingsResponse(
    bool NewOrders,
    bool ScheduledDue,
    bool PaymentConfirmed,
    bool OrderUpdates,
    bool Announcements);

/// <summary>Every field optional: the backend treats null as "leave this one alone".</summary>
public sealed record UpdateNotificationSettingsRequest(
    bool? NewOrders = null,
    bool? ScheduledDue = null,
    bool? PaymentConfirmed = null,
    bool? OrderUpdates = null,
    bool? Announcements = null);

/// <summary>
/// The shop's print defaults - including <c>AutoPrint</c>, which is the same
/// flag the agent already receives on every heartbeat as
/// <see cref="PrintAgentHeartbeatResponse.AutoPrintEnabled"/>. This route is
/// how it gets changed; the heartbeat is how the agent finds out.
/// </summary>
public sealed record PrintSettingsResponse(
    ColorMode DefaultColorMode,
    PaperSize DefaultPaperSize,
    DuplexMode DefaultDuplexMode,
    PrintQuality Quality,
    PageRangeMode PageRangeMode,
    string? PageRangeCustom,
    bool AutoAccept,
    bool AutoPrint,
    bool LowPaperAlert,
    bool PrintFailureAlert,
    string? DefaultPrinterId,
    int LargeOrderPageThreshold,
    int? LargeOrderPageThresholdOverride,
    bool BatchEnabled,
    int BatchTargetOrders,
    int BatchMinOrders,
    int BatchMaxOrders);

/// <summary>
/// Null leaves a field alone, which is why clearing the large-order threshold
/// needs <c>ClearLargeOrderPageThreshold</c> rather than a null: the two
/// meanings are not the same and one flag is how the backend tells them apart.
/// </summary>
public sealed record UpdatePrintSettingsRequest(
    ColorMode? DefaultColorMode = null,
    PaperSize? DefaultPaperSize = null,
    DuplexMode? DefaultDuplexMode = null,
    PrintQuality? Quality = null,
    PageRangeMode? PageRangeMode = null,
    string? PageRangeCustom = null,
    bool? AutoAccept = null,
    bool? AutoPrint = null,
    bool? LowPaperAlert = null,
    bool? PrintFailureAlert = null,
    string? DefaultPrinterId = null,
    int? LargeOrderPageThreshold = null,
    bool ClearLargeOrderPageThreshold = false,
    bool? BatchEnabled = null,
    int? BatchTargetOrders = null);

public sealed record ShopSettingsResponse(
    string ShopId,
    NotificationSettingsResponse Notifications,
    PrintSettingsResponse Print,
    DateTimeOffset UpdatedAt);

/// <summary>The account number comes back masked and never in full - do not log it either way.</summary>
public sealed record BankDetailsResponse(
    string AccountHolder,
    string BankName,
    string AccountNumberMasked,
    string Ifsc,
    string? UpiId,
    bool Verified,
    DateTimeOffset UpdatedAt);

public sealed record SaveBankDetailsRequest(
    string AccountHolder,
    string BankName,
    string AccountNumber,
    string Ifsc,
    string? UpiId = null);

// --- printers, as the shop registers them ------------------------------------

/// <summary>
/// A printer on the shop's record - not the same thing as
/// <see cref="AgentPrinter"/>, which is what this machine's spooler reports.
///
/// <c>Source</c> says which of the two a row came from, and <c>PrintAgentId</c>
/// is set when an agent registered it. <c>ColorCapable</c> is a plain bool here
/// rather than the nullable one on <see cref="AgentPrinter"/>, because by this
/// point the shop has committed to an answer the driver would not give.
/// </summary>
public sealed record PrinterResponse(
    string Id,
    string Name,
    string? Model,
    string? ConnectionHint,
    bool ColorCapable,
    bool DuplexCapable,
    List<PaperSize> PaperSizes,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    string Source,
    string ReportedStatus,
    string? PrintAgentId);

public sealed record CreatePrinterRequest(
    string Name,
    string? Model = null,
    string? ConnectionHint = null,
    bool ColorCapable = true,
    bool DuplexCapable = true,
    IReadOnlyList<PaperSize>? PaperSizes = null,
    bool MakeDefault = false);

public sealed record UpdatePrinterRequest(
    string? Name = null,
    string? Model = null,
    string? ConnectionHint = null,
    bool? ColorCapable = null,
    bool? DuplexCapable = null,
    IReadOnlyList<PaperSize>? PaperSizes = null);

// --- the shop itself ---------------------------------------------------------

public sealed record ShopHoursResponse(int DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt);

public sealed record HoursEntry(int DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt);

public sealed record UpdateShopHoursRequest(IReadOnlyList<HoursEntry> Hours);

/// <summary>
/// A closure, or a short day. Both opening times null is the full-day closure
/// that <c>FullDayClosure</c> reports back.
/// </summary>
public sealed record ShopHolidayResponse(
    DateOnly Date,
    string? Reason,
    TimeOnly? OpensAt,
    TimeOnly? ClosesAt,
    bool FullDayClosure);

public sealed record UpsertShopHolidayRequest(
    DateOnly Date,
    string? Reason = null,
    TimeOnly? OpensAt = null,
    TimeOnly? ClosesAt = null);

public sealed record ShopPricingResponse(
    string Id,
    PaperSize PaperSize,
    ColorMode ColorMode,
    DuplexMode DuplexMode,
    int MinPages,
    int? MaxPages,
    decimal PricePerPage,
    decimal SetupFee);

/// <summary>A null <c>MaxPages</c> is the open-ended top band, not a missing value.</summary>
public sealed record PricingEntry(
    PaperSize PaperSize,
    ColorMode ColorMode,
    DuplexMode DuplexMode,
    decimal PricePerPage,
    int MinPages = 1,
    int? MaxPages = null,
    decimal SetupFee = 0m);

public sealed record UpdateShopPricingRequest(IReadOnlyList<PricingEntry> Pricing);

public sealed record ShopServiceResponse(
    string Id,
    FinishingService Service,
    string DisplayName,
    ChargeUnit ChargeUnit,
    decimal Price,
    string? Description);

public sealed record ServiceEntry(
    FinishingService Service,
    ChargeUnit ChargeUnit,
    decimal Price,
    string? Description = null);

public sealed record UpdateShopServicesRequest(IReadOnlyList<ServiceEntry> Services);

/// <summary>
/// <c>CanAcceptOrders</c> is the one to branch on: it already folds in
/// <c>AcceptingOrders</c>, <c>OpenNow</c>, holidays and capacity, and
/// <c>CanAcceptOrdersReason</c> is the sentence to show when it is false.
/// </summary>
public sealed record ShopDetailResponse(
    string Id,
    string Name,
    string Code,
    string CampusId,
    string? Description,
    string? ContactPhone,
    string? ContactEmail,
    string? AddressLine,
    string? Landmark,
    decimal? Latitude,
    decimal? Longitude,
    string? GstNumber,
    string? LogoKey,
    string? LogoUrl,
    RecordStatus Status,
    bool AcceptingOrders,
    bool OpenNow,
    bool CanAcceptOrders,
    string? CanAcceptOrdersReason,
    int SlotDurationMinutes,
    int MinLeadTimeMinutes,
    int MaxAdvanceDays,
    List<ShopHoursResponse> Hours,
    List<ShopHolidayResponse> UpcomingHolidays,
    List<ShopPricingResponse> Pricing,
    List<ShopServiceResponse> Services);

public sealed record ShopSummaryResponse(
    string Id,
    string Name,
    string Code,
    string CampusId,
    string? AddressLine,
    string? Landmark,
    string? LogoKey,
    string? LogoUrl,
    bool AcceptingOrders,
    bool OpenNow,
    bool CanAcceptOrders,
    string? CanAcceptOrdersReason,
    decimal? StartingPricePerPage,
    decimal? Latitude,
    decimal? Longitude,
    double? DistanceMetres);

public sealed record CreateShopRequest(
    string Name,
    string CampusId,
    string? Description = null,
    string? ContactPhone = null,
    string? ContactEmail = null,
    string? AddressLine = null,
    string? Landmark = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string? GstNumber = null);

public sealed record UpdateShopRequest(
    string? Name = null,
    string? Description = null,
    string? ContactPhone = null,
    string? ContactEmail = null,
    string? AddressLine = null,
    string? Landmark = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string? LogoKey = null,
    string? GstNumber = null,
    bool? AcceptingOrders = null);

public sealed record UpdateShopCapacityRequest(
    int? MaxConcurrentOrders = null,
    int? DefaultSlotOrderCapacity = null,
    int? DefaultSlotPageCapacity = null,
    int? SlotDurationMinutes = null,
    int? MaxAdvanceDays = null,
    int? MinLeadTimeMinutes = null);

public sealed record ShopStaffResponse(
    string Id,
    string UserId,
    string? Name,
    string? Phone,
    UserRole Role,
    List<string> Capabilities,
    bool Active);

public sealed record AddShopStaffRequest(
    string Phone,
    string? DisplayName = null,
    IReadOnlyList<string>? Capabilities = null);

// --- shop media --------------------------------------------------------------

/// <summary>
/// A presigned PUT. <c>RequiredHeaders</c> must be sent on the upload exactly
/// as given - the signature covers them, so dropping one is a 403 from storage
/// rather than anything the backend will explain.
/// </summary>
public sealed record ImageUploadUrlResponse(
    string ImageKey,
    string UploadUrl,
    string Method,
    Dictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAt);

public sealed record ImageUploadUrlRequest(string FileName, string ContentType, long SizeBytes);

/// <summary>Commits an upload: the key from <see cref="ImageUploadUrlResponse"/>, once the PUT succeeded.</summary>
public sealed record SetImageRequest(string ImageKey);

// --- batches -----------------------------------------------------------------

public sealed record BatchOrderResponse(
    string OrderId,
    string OrderNumber,
    int SequenceNumber,
    int DocumentPageCount,
    bool SeparatorBefore,
    int? StartPage);

/// <summary>
/// Several orders merged into one PDF so they come off the printer together.
///
/// <c>Version</c> matters more here than elsewhere: adding, removing or
/// resequencing an order invalidates a generated PDF, and the print routes
/// report the version they actually printed so a stale one is detectable.
/// </summary>
public sealed record BatchResponse(
    string Id,
    string BatchNumber,
    string ShopId,
    string? Name,
    int Version,
    BatchStatus Status,
    string StatusDisplay,
    int OrderCount,
    int DocumentPageCount,
    int SeparatorPageCount,
    int TotalPageCount,
    PaperSize PaperSize,
    ColorMode ColorMode,
    DuplexMode DuplexMode,
    Orientation Orientation,
    List<BatchOrderResponse> Orders,
    string? GenerationError,
    DateTimeOffset? PrintedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// The poll target while a batch PDF is being assembled. <c>Ready</c> is the
/// flag to wait on; <c>GenerationError</c> is why it never will be.
/// </summary>
public sealed record BatchStatusResponse(
    string BatchId,
    BatchStatus Status,
    string StatusDisplay,
    int Version,
    bool Ready,
    int OrderCount,
    int DocumentPageCount,
    int SeparatorPageCount,
    int TotalPageCount,
    DateTimeOffset? GenerationStartedAt,
    DateTimeOffset? GenerationCompletedAt,
    string? GenerationError);

public sealed record BatchPdfLinkResponse(
    string Url,
    DateTimeOffset ExpiresAt,
    int Version,
    int TotalPageCount);

/// <summary>
/// <c>AlreadyInitiated</c> true means the idempotency key matched an attempt
/// that was already running - the caller double-tapped, and nothing new
/// started. The outcome still has to be reported against <c>AttemptId</c>.
/// </summary>
public sealed record PrintInitiatedResponse(
    string AttemptId,
    int BatchVersion,
    string PrintUrl,
    DateTimeOffset ExpiresAt,
    bool AlreadyInitiated,
    int DocumentPageCount,
    int SeparatorPageCount,
    int TotalPageCount,
    string? Note);

/// <summary>Batch counterpart of <see cref="OrderPrintOutcomeRequest"/>; <c>Success</c> is required for the same reason.</summary>
public sealed record PrintOutcomeRequest(
    string AttemptId,
    bool Success,
    string? PrinterInfo = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record PrintAttemptResponse(
    string Id,
    int Version,
    string Status,
    bool IsReprint,
    string? PrinterInfo,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record BatchEventResponse(
    string EventType,
    int BatchVersion,
    string? ActorUserId,
    Dictionary<string, object?>? Metadata,
    DateTimeOffset At);

public sealed record CreateBatchRequest(IReadOnlyList<string> OrderIds, string? Name = null);

/// <summary>Either the UUID or the display code - the backend accepts one of the two.</summary>
public sealed record AddBatchOrderRequest(string? OrderUuid = null, string? OrderId = null);

public sealed record ResequenceBatchRequest(IReadOnlyList<string> OrderIds);

public sealed record CancelBatchRequest(string? Reason = null);

// --- large orders and bundles ------------------------------------------------

public sealed record LargeOrderDocument(
    string DocumentId,
    string FileName,
    string Url,
    DateTimeOffset ExpiresAt,
    int Pages,
    int Copies,
    string? PageRange,
    string PaperSize,
    string ColorMode,
    string DuplexMode);

public sealed record LargeOrderPrintResponse(
    string OrderId,
    string OrderNumber,
    int TotalPages,
    int TotalSheets,
    List<LargeOrderDocument> Documents);

public sealed record BundleMemberResponse(
    string OrderId,
    string OrderCode,
    int Sequence,
    int TotalPages,
    decimal TotalAmount,
    int FileCount);

/// <summary>
/// Orders a student merged so they print as one. The agent already honours
/// these: see <c>PrintAgentDeviceService.ordersToPrint</c> on the backend, which
/// hands the whole bundle to the job that owns it, in <c>Sequence</c> order.
/// </summary>
public sealed record ShopBundleResponse(
    string BundleId,
    string PrimaryOrderId,
    int OrderCount,
    int TotalPages,
    decimal TotalAmount,
    string Currency,
    int FileCount,
    List<BundleMemberResponse> Members);

// --- products ----------------------------------------------------------------

public sealed record ProductResponse(
    string Id,
    string ShopId,
    string Name,
    string? Description,
    decimal Price,
    string Currency,
    bool InStock,
    string? ImageUrl,
    int SortOrder,
    RecordStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateProductRequest(
    string Name,
    decimal Price,
    string? Description = null,
    bool InStock = true,
    int SortOrder = 0);

public sealed record UpdateProductRequest(
    string? Name = null,
    string? Description = null,
    decimal? Price = null,
    bool? InStock = null,
    int? SortOrder = null);

/// <summary>Required, not nullable: the backend refuses an omitted key rather than guessing "out of stock".</summary>
public sealed record SetStockRequest(bool InStock);

// --- priority and queue speed ------------------------------------------------

public sealed record PriorityModeResponse(string ShopId, bool Enabled);

public sealed record SetPriorityModeRequest(bool Enabled);

/// <summary>
/// The rotating token behind the counter's QR. It expires - re-read it rather
/// than caching the image, and use <c>ExpiresInSeconds</c> to decide when.
/// </summary>
public sealed record PriorityQrResponse(
    string Token,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    long ExpiresInSeconds);

/// <summary>
/// What the backend assumes this shop's printer does, which is what every
/// "ready by" estimate is built on. <c>Overridden</c> false means these are
/// platform defaults rather than anything this shop measured.
/// </summary>
public sealed record PrintSpeedResponse(
    decimal SecondsPerPageBw,
    decimal SecondsPerPageColor,
    int OperationalBufferPercent,
    bool Overridden);

/// <summary>
/// <c>Fields</c> is what makes clearing possible: name a field there and send
/// it null to reset it to the platform default, rather than leaving it alone.
/// </summary>
public sealed record UpdatePrintSpeedRequest(
    decimal? SecondsPerPageBw = null,
    decimal? SecondsPerPageColor = null,
    int? OperationalBufferPercent = null,
    IReadOnlyList<string>? Fields = null);

// --- settlements -------------------------------------------------------------

public sealed record SettlementResponse(
    string Id,
    string SettlementNumber,
    string ShopId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int OrderCount,
    decimal GrossSales,
    decimal RefundAmount,
    decimal NetSales,
    decimal CommissionAmount,
    decimal PaymentFees,
    decimal AdjustmentAmount,
    string? AdjustmentNote,
    decimal ShopPayable,
    string Currency,
    SettlementStatus Status,
    string? HoldReason,
    string? PayoutReference,
    DateTimeOffset? SettledAt,
    DateTimeOffset CreatedAt);

public sealed record SettlementItemResponse(
    string OrderId,
    string OrderNumber,
    DateOnly SaleDate,
    decimal GrossAmount,
    decimal RefundAmount,
    decimal NetSales,
    decimal Commission,
    decimal PaymentFee,
    decimal ShopPayable);

public sealed record SettlementDetailResponse(
    SettlementResponse Settlement,
    List<SettlementItemResponse> Items);

/// <summary>What has been earned but not yet paid out - the figure the owner asks about.</summary>
public sealed record UnsettledBalanceResponse(
    string ShopId,
    decimal UnsettledPayable,
    string Currency);

// --- content reports ---------------------------------------------------------

/// <summary>
/// A shop flagging what a student sent it. <c>HoldOrder</c> on the request
/// stops the order rather than only recording the complaint.
/// </summary>
public sealed record CreateContentReportRequest(
    ContentReportReason Reason,
    string? ClientReportId = null,
    string? ShopId = null,
    string? OrderId = null,
    string? OrderNumber = null,
    string? FileName = null,
    string? CustomerId = null,
    string? CustomerName = null,
    string? CustomerPhone = null,
    string? CustomerUid = null,
    int? Pages = null,
    int? Copies = null,
    string? PaperSize = null,
    string? ColorMode = null,
    string? Note = null,
    DateTimeOffset? ReportedAt = null,
    bool HoldOrder = false);

public sealed record ContentReportResponse(
    string Id,
    string ShopId,
    string? ShopName,
    string? OrderId,
    string? OrderNumber,
    string? ClientReportId,
    string ReportedBy,
    string? FileName,
    string? CustomerUserId,
    string? CustomerName,
    string? CustomerPhone,
    string? CustomerUid,
    int? Pages,
    int? Copies,
    PaperSize? PaperSize,
    ColorMode? ColorMode,
    ContentReportReason Reason,
    string ReasonDisplay,
    string? Note,
    ContentReportStatus Status,
    ContentReportResolution? Resolution,
    string? ResolutionNote,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset ReportedAt,
    DateTimeOffset SyncedAt);

// --- dashboard, analytics and reports ----------------------------------------

public sealed record QueueCounts(
    long AwaitingAcceptance,
    long Queued,
    long Printing,
    long Printed,
    long Ready,
    long Delayed,
    long Total);

public sealed record TodayStats(
    DateOnly Date,
    long OrdersReceived,
    long OrdersCompleted,
    long PagesSold,
    decimal GrossSales,
    decimal NetSales,
    decimal ShopPayable);

/// <summary>
/// One read for the whole counter screen. <c>GeneratedAt</c> is the backend's
/// clock, not this machine's - show staleness against that.
/// </summary>
public sealed record ShopDashboardResponse(
    string ShopId,
    string ShopName,
    string Timezone,
    bool AcceptingOrders,
    bool OpenNow,
    QueueCounts Queue,
    TodayStats Today,
    long ActiveBatches,
    long LargeOrdersWaiting,
    int LargeOrderPageThreshold,
    long OpenContentReports,
    decimal UnsettledPayable,
    string Currency,
    DateTimeOffset GeneratedAt);

public sealed record AnalyticsKpis(
    long OrderCount,
    long PageCount,
    decimal GrossSales,
    decimal Refunds,
    decimal NetSales,
    decimal Commission,
    decimal PaymentFees,
    decimal ShopPayable,
    decimal AverageOrderValue,
    long ColorOrders,
    long BlackAndWhiteOrders,
    long ColorPages,
    long BlackAndWhitePages);

public sealed record AnalyticsDay(
    DateOnly Date,
    int Orders,
    int Pages,
    decimal GrossSales,
    decimal NetSales,
    decimal ShopPayable);

public sealed record ShopAnalyticsResponse(
    string ShopId,
    ReportPeriod Period,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Timezone,
    string Currency,
    AnalyticsKpis Kpis,
    List<AnalyticsDay> Daily,
    Dictionary<OrderStatus, long> OrdersByStatus);

public sealed record PeakHourCell(int DayOfWeek, int Hour, int Orders);

/// <summary>
/// <c>Cells</c> is the sparse grid; <c>ByHour</c> and <c>ByDayOfWeek</c> are its
/// margins, already summed, so a client charts them without re-aggregating.
/// </summary>
public sealed record PeakHoursResponse(
    string ShopId,
    ReportPeriod Period,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Timezone,
    int TotalOrders,
    List<PeakHourCell> Cells,
    int? BusiestDayOfWeek,
    int? BusiestHour,
    List<int> ByHour,
    List<int> ByDayOfWeek);

public sealed record FinanceSummary(
    string? ShopId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal GrossSales,
    decimal Refunds,
    decimal NetSales,
    decimal Commission,
    decimal PaymentFees,
    decimal ShopPayable,
    long OrderCount,
    long PageCount);

public sealed record DailyRow(
    DateOnly Date,
    int OrderCount,
    int Pages,
    decimal GrossSales,
    decimal Refunds,
    decimal NetSales,
    decimal PaymentFees,
    decimal Commission,
    decimal ShopPayable);

/// <summary>
/// An export is a job, not a file: the route that starts one returns this with
/// <c>Ready</c> false, and the download only works once it flips.
/// </summary>
public sealed record ExportJobResponse(
    string JobId,
    ReportType ReportType,
    ExportJobStatus Status,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    int? RowCount,
    long? FileSizeBytes,
    string? FailureReason,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    bool Ready);
