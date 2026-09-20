using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using PrintlyAgent.Credentials;
using PrintlyAgent.Models;

namespace PrintlyAgent.Net;

/// <summary>
/// The owner-session half of the backend's API.
///
/// <para>
/// Split into its own file rather than grown onto <c>PrintlyApiClient.cs</c>
/// because the two halves are not the same API and the separation is load
/// bearing: everything here carries the owner's own JWT and reaches
/// owner-facing routes, while the device calls in the main file carry the
/// <c>&lt;agentId&gt;.&lt;secret&gt;</c> bearer and reach the print-agent
/// routes. The backend enforces that with two disjoint authentication filters,
/// so a call that takes the wrong credential type does not degrade - it 401s.
/// Every method here takes an <see cref="OwnerSession"/> for exactly that
/// reason, and none of them accepts an <see cref="AgentCredential"/>.
/// </para>
///
/// <para>
/// Ported from the Kotlin controllers in <c>API'S/printly/src/main/kotlin</c>,
/// one method per route. Read the controller before changing a signature here:
/// a path that does not match is a 404 and a body that does not match is a 400,
/// and neither shows up as a compile error.
/// </para>
/// </summary>
public sealed partial class PrintlyApiClient
{
    // --- helpers the owner surface needs beyond the device one ---------------

    private HttpRequestMessage Patch(string url, object? body, AuthenticationHeaderValue? auth) =>
        Build(HttpMethod.Patch, url, body, auth);

    private HttpRequestMessage Delete(string url, AuthenticationHeaderValue? auth) =>
        Build(HttpMethod.Delete, url, null, auth);

    /// <summary>
    /// Builds a query string from name/value pairs, dropping every null.
    ///
    /// Dropping rather than sending an empty value is the point. Spring binds a
    /// missing parameter to the handler's declared default - "LAST_7_DAYS",
    /// page 0, size 20 - but binds an empty one to a conversion failure, so
    /// <c>?status=</c> is a 400 where sending nothing at all is correct. A
    /// repeated value (the same name more than once) is how the list-valued
    /// parameters arrive, which is what the order list's `status` expects.
    /// </summary>
    private static string Query(params (string Name, object? Value)[] parameters)
    {
        var builder = new StringBuilder();

        void Append(string name, object value)
        {
            builder.Append(builder.Length == 0 ? '?' : '&')
                .Append(Uri.EscapeDataString(name))
                .Append('=')
                .Append(Uri.EscapeDataString(Format(value)));
        }

        foreach (var (name, value) in parameters)
        {
            switch (value)
            {
                case null:
                    continue;
                // Before the IEnumerable arm: a string is one, and splitting it
                // into characters would be a long and baffling query string.
                case string text:
                    Append(name, text);
                    break;
                case System.Collections.IEnumerable many:
                    foreach (var item in many)
                    {
                        if (item is not null) Append(name, item);
                    }
                    break;
                default:
                    Append(name, value);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The wire form of a single query value.
    ///
    /// Invariant culture throughout, and never the machine's own: a shop in a
    /// locale that formats dates as dd-MM-yyyy or decimals with a comma would
    /// otherwise send exactly that, and the backend parses neither. Booleans go
    /// lowercase for the same reason - .NET's own "True" is not what Spring
    /// reads as true.
    /// </summary>
    private static string Format(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        // "Z", never "+00:00". Spring binds an Instant parameter with
        // Instant.parse, whose canonical form ends in Z; the offset spelling is
        // only accepted by some JDK versions, and round-trip "o" emits exactly
        // that. Milliseconds because nothing here is finer-grained and the
        // seven-digit tick fraction is needless on the wire.
        DateTimeOffset instant => instant.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        Enum member => member.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    // --- shop discovery ------------------------------------------------------

    /// <summary>
    /// Every shop the caller could order from. <c>lat</c> and <c>lng</c> go
    /// together - the backend rejects one without the other - and only then is
    /// <see cref="ShopSummaryResponse.DistanceMetres"/> populated.
    /// </summary>
    public Task<PageResponse<ShopSummaryResponse>> BrowseShopsAsync(
        OwnerSession session,
        string? campusId = null,
        string? search = null,
        double? lat = null,
        double? lng = null,
        int page = 0,
        int size = 20,
        CancellationToken ct = default) =>
        SendAsync<PageResponse<ShopSummaryResponse>>(
            Get(Url("/api/v1/shops" + Query(
                ("campusId", campusId), ("search", search), ("lat", lat), ("lng", lng),
                ("page", page), ("size", size))), OwnerAuth(session)), ct);

    public Task<ShopDetailResponse> PublicShopAsync(OwnerSession session, string shopId, CancellationToken ct = default) =>
        SendAsync<ShopDetailResponse>(Get(Url($"/api/v1/shops/{shopId}"), OwnerAuth(session)), ct);

    public Task<PageResponse<ProductResponse>> BrowseShopProductsAsync(
        OwnerSession session, string shopId, string? search = null, int page = 0, int size = 50,
        CancellationToken ct = default) =>
        SendAsync<PageResponse<ProductResponse>>(
            Get(Url($"/api/v1/shops/{shopId}/products" + Query(("search", search), ("page", page), ("size", size))),
                OwnerAuth(session)), ct);

    /// <summary>
    /// The bookable windows on one date. <c>pages</c> is the size of the order
    /// being placed, because a slot with room for another order may still not
    /// have room for another two hundred pages.
    /// </summary>
    public Task<List<SlotResponse>> ShopSlotsAsync(
        OwnerSession session, string shopId, DateOnly date, int pages = 0, CancellationToken ct = default) =>
        SendAsync<List<SlotResponse>>(
            Get(Url($"/api/v1/shops/{shopId}/slots" + Query(("date", date), ("pages", pages))),
                OwnerAuth(session)), ct);

    // --- the owner's own shops ----------------------------------------------

    public Task<List<ShopSummaryResponse>> MyShopsAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<List<ShopSummaryResponse>>(Get(Url("/api/v1/shop"), OwnerAuth(session)), ct);

    public Task<ShopDetailResponse> CreateShopAsync(
        OwnerSession session, CreateShopRequest request, CancellationToken ct = default) =>
        SendAsync<ShopDetailResponse>(Post(Url("/api/v1/shop"), request, OwnerAuth(session)), ct);

    public Task<ShopDetailResponse> ShopAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<ShopDetailResponse>(Get(Url($"/api/v1/shop/{session.ShopId}"), OwnerAuth(session)), ct);

    public Task<ShopDetailResponse> UpdateShopAsync(
        OwnerSession session, UpdateShopRequest request, CancellationToken ct = default) =>
        SendAsync<ShopDetailResponse>(
            Patch(Url($"/api/v1/shop/{session.ShopId}"), request, OwnerAuth(session)), ct);

    /// <summary>Replaces the whole week - send every open day, not just the changed one.</summary>
    public Task<List<ShopHoursResponse>> UpdateShopHoursAsync(
        OwnerSession session, UpdateShopHoursRequest request, CancellationToken ct = default) =>
        SendAsync<List<ShopHoursResponse>>(
            Patch(Url($"/api/v1/shop/{session.ShopId}/hours"), request, OwnerAuth(session)), ct);

    public Task<ShopHolidayResponse> UpsertShopHolidayAsync(
        OwnerSession session, UpsertShopHolidayRequest request, CancellationToken ct = default) =>
        SendAsync<ShopHolidayResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/holidays"), request, OwnerAuth(session)), ct);

    public Task DeleteShopHolidayAsync(OwnerSession session, DateOnly date, CancellationToken ct = default) =>
        SendAsync(
            Delete(Url($"/api/v1/shop/{session.ShopId}/holidays/{Format(date)}"), OwnerAuth(session)), ct);

    /// <summary>Replaces the whole price list, the way hours are replaced whole.</summary>
    public Task<List<ShopPricingResponse>> UpdateShopPricingAsync(
        OwnerSession session, UpdateShopPricingRequest request, CancellationToken ct = default) =>
        SendAsync<List<ShopPricingResponse>>(
            Patch(Url($"/api/v1/shop/{session.ShopId}/pricing"), request, OwnerAuth(session)), ct);

    public Task<List<ShopServiceResponse>> UpdateShopServicesAsync(
        OwnerSession session, UpdateShopServicesRequest request, CancellationToken ct = default) =>
        SendAsync<List<ShopServiceResponse>>(
            Patch(Url($"/api/v1/shop/{session.ShopId}/services"), request, OwnerAuth(session)), ct);

    public Task<ShopDetailResponse> UpdateShopCapacityAsync(
        OwnerSession session, UpdateShopCapacityRequest request, CancellationToken ct = default) =>
        SendAsync<ShopDetailResponse>(
            Patch(Url($"/api/v1/shop/{session.ShopId}/capacity"), request, OwnerAuth(session)), ct);

    public Task<List<ShopStaffResponse>> ListStaffAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<List<ShopStaffResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/staff"), OwnerAuth(session)), ct);

    public Task<ShopStaffResponse> AddStaffAsync(
        OwnerSession session, AddShopStaffRequest request, CancellationToken ct = default) =>
        SendAsync<ShopStaffResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/staff"), request, OwnerAuth(session)), ct);

    public Task RemoveStaffAsync(OwnerSession session, string userId, CancellationToken ct = default) =>
        SendAsync(Delete(Url($"/api/v1/shop/{session.ShopId}/staff/{userId}"), OwnerAuth(session)), ct);

    // --- shop media ----------------------------------------------------------

    public Task<ImageUploadUrlResponse> ShopLogoUploadUrlAsync(
        OwnerSession session, ImageUploadUrlRequest request, CancellationToken ct = default) =>
        SendAsync<ImageUploadUrlResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/logo/upload-url"), request, OwnerAuth(session)), ct);

    /// <summary>Call only after the presigned PUT succeeded - this commits the key, it does not upload.</summary>
    public Task<ShopDetailResponse> SetShopLogoAsync(
        OwnerSession session, string imageKey, CancellationToken ct = default) =>
        SendAsync<ShopDetailResponse>(
            Put(Url($"/api/v1/shop/{session.ShopId}/logo"), new SetImageRequest(imageKey), OwnerAuth(session)), ct);

    public Task RemoveShopLogoAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync(Delete(Url($"/api/v1/shop/{session.ShopId}/logo"), OwnerAuth(session)), ct);

    // --- settings ------------------------------------------------------------

    public Task<ShopSettingsResponse> SettingsAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<ShopSettingsResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/settings"), OwnerAuth(session)), ct);

    public Task<NotificationSettingsResponse> NotificationSettingsAsync(
        OwnerSession session, CancellationToken ct = default) =>
        SendAsync<NotificationSettingsResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/settings/notifications"), OwnerAuth(session)), ct);

    public Task<NotificationSettingsResponse> UpdateNotificationSettingsAsync(
        OwnerSession session, UpdateNotificationSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<NotificationSettingsResponse>(
            Put(Url($"/api/v1/shop/{session.ShopId}/settings/notifications"), request, OwnerAuth(session)), ct);

    /// <summary>
    /// The shop's print defaults, <c>AutoPrint</c> among them - the same flag
    /// the agent learns from every heartbeat. Reading it here is how a screen
    /// shows the current value; the heartbeat is how the agent acts on it.
    /// </summary>
    public Task<PrintSettingsResponse> PrintSettingsAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<PrintSettingsResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/settings/print"), OwnerAuth(session)), ct);

    public Task<PrintSettingsResponse> UpdatePrintSettingsAsync(
        OwnerSession session, UpdatePrintSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<PrintSettingsResponse>(
            Put(Url($"/api/v1/shop/{session.ShopId}/settings/print"), request, OwnerAuth(session)), ct);

    public Task<BankDetailsResponse> BankDetailsAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<BankDetailsResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/bank-details"), OwnerAuth(session)), ct);

    public Task<BankDetailsResponse> SaveBankDetailsAsync(
        OwnerSession session, SaveBankDetailsRequest request, CancellationToken ct = default) =>
        SendAsync<BankDetailsResponse>(
            Put(Url($"/api/v1/shop/{session.ShopId}/bank-details"), request, OwnerAuth(session)), ct);

    public Task DeleteBankDetailsAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync(Delete(Url($"/api/v1/shop/{session.ShopId}/bank-details"), OwnerAuth(session)), ct);

    // --- printers on the shop's record ---------------------------------------

    /// <summary>
    /// The shop's registered printers - what the backend routes jobs to. Not
    /// what this machine's spooler currently offers: that is
    /// <c>PrinterDiscovery</c>, and <see cref="SyncPrintersAsync"/> is what
    /// carries it here.
    /// </summary>
    public Task<List<PrinterResponse>> ListShopPrintersAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<List<PrinterResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/printers"), OwnerAuth(session)), ct);

    public Task<PrinterResponse> AddShopPrinterAsync(
        OwnerSession session, CreatePrinterRequest request, CancellationToken ct = default) =>
        SendAsync<PrinterResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/printers"), request, OwnerAuth(session)), ct);

    public Task<PrinterResponse> UpdateShopPrinterAsync(
        OwnerSession session, string printerId, UpdatePrinterRequest request, CancellationToken ct = default) =>
        SendAsync<PrinterResponse>(
            Patch(Url($"/api/v1/shop/{session.ShopId}/printers/{printerId}"), request, OwnerAuth(session)), ct);

    public Task<PrinterResponse> MakePrinterDefaultAsync(
        OwnerSession session, string printerId, CancellationToken ct = default) =>
        SendAsync<PrinterResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/printers/{printerId}/make-default"), null, OwnerAuth(session)), ct);

    public Task RemoveShopPrinterAsync(OwnerSession session, string printerId, CancellationToken ct = default) =>
        SendAsync(
            Delete(Url($"/api/v1/shop/{session.ShopId}/printers/{printerId}"), OwnerAuth(session)), ct);

    // --- orders --------------------------------------------------------------

    /// <summary>
    /// The shop's order list. <c>status</c> repeats on the wire, so several may
    /// be passed and the backend ORs them.
    /// </summary>
    public Task<PageResponse<OrderResponse>> ListOrdersAsync(
        OwnerSession session,
        IReadOnlyList<OrderStatus>? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? search = null,
        int page = 0,
        int size = 20,
        CancellationToken ct = default) =>
        SendAsync<PageResponse<OrderResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/orders" + Query(
                ("status", status), ("from", from), ("to", to), ("search", search),
                ("page", page), ("size", size))), OwnerAuth(session)), ct);

    /// <summary>
    /// Finds one order by the code a customer reads out - "SH001-000127", or
    /// just "127". Distinct from <see cref="OrderAsync"/>, which takes the UUID.
    /// </summary>
    public Task<OrderResponse> LookupOrderAsync(
        OwnerSession session, string orderCode, CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/orders/lookup" + Query(("orderId", orderCode))),
                OwnerAuth(session)), ct);

    public Task<OrderResponse> OrderAsync(OwnerSession session, string orderId, CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}"), OwnerAuth(session)), ct);

    public Task<DownloadUrlResponse> OrderItemDownloadUrlAsync(
        OwnerSession session, string orderId, string itemId, CancellationToken ct = default) =>
        SendAsync<DownloadUrlResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/items/{itemId}/download-url"),
                OwnerAuth(session)), ct);

    public Task<OrderResponse> AcceptOrderAsync(
        OwnerSession session, string orderId, string? note = null, CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/accept"), Note(note), OwnerAuth(session)), ct);

    public Task<OrderResponse> StartPrintingOrderAsync(
        OwnerSession session, string orderId, string? note = null, CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/start-printing"), Note(note),
                OwnerAuth(session)), ct);

    public Task<OrderResponse> MarkOrderPrintedAsync(
        OwnerSession session, string orderId, string? note = null, CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/mark-printed"), Note(note),
                OwnerAuth(session)), ct);

    public Task<OrderResponse> MarkOrderReadyAsync(
        OwnerSession session, string orderId, string? note = null, CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/mark-ready"), Note(note),
                OwnerAuth(session)), ct);

    public Task<OrderResponse> CollectOrderAsync(
        OwnerSession session, string orderId, string? note = null, CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/collect"), Note(note), OwnerAuth(session)), ct);

    public Task<OrderResponse> DelayOrderAsync(
        OwnerSession session, string orderId, string? note = null, CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/delay"), Note(note), OwnerAuth(session)), ct);

    /// <summary>
    /// Checks the code the student shows before handing the work over. Read
    /// <see cref="CollectionVerificationResponse.CodeAvailable"/> as well as
    /// <c>Verified</c> - an order with no code at all is not a failed check.
    /// </summary>
    public Task<CollectionVerificationResponse> VerifyCollectionAsync(
        OwnerSession session, string orderId, string code, CancellationToken ct = default) =>
        SendAsync<CollectionVerificationResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/verify-collection"),
                new CollectionVerificationRequest(code), OwnerAuth(session)), ct);

    /// <summary>The three settings a shop may override on the student's behalf, per item.</summary>
    public Task<OrderResponse> UpdateOrderPrintSettingsAsync(
        OwnerSession session, string orderId, IReadOnlyList<ItemPrintSettings> items,
        CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Patch(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/print-settings"),
                new UpdateOrderPrintSettingsRequest(items), OwnerAuth(session)), ct);

    public Task<OrderResponse> ReportOrderPrintOutcomeAsync(
        OwnerSession session, string orderId, OrderPrintOutcomeRequest request, CancellationToken ct = default) =>
        SendAsync<OrderResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/print-outcome"), request,
                OwnerAuth(session)), ct);

    /// <summary>
    /// Queues a fresh print job for an order that already has one. The owner's
    /// way back from a job this agent could not finish - the device side never
    /// retries itself past <c>MaxRetryAttempts</c>.
    /// </summary>
    public Task RetryOrderPrintAsync(OwnerSession session, string orderId, CancellationToken ct = default) =>
        SendAsync(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/retry-print"), null, OwnerAuth(session)), ct);

    /// <summary>
    /// Orders too big to batch, which the shop prints on their own.
    /// <see cref="LargeOrderThresholdAsync"/> is where the cutoff comes from.
    /// </summary>
    public Task<PageResponse<OrderResponse>> ListLargeOrdersAsync(
        OwnerSession session, int page = 0, int size = 20, CancellationToken ct = default) =>
        SendAsync<PageResponse<OrderResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/orders/large" + Query(("page", page), ("size", size))),
                OwnerAuth(session)), ct);

    public Task<Dictionary<string, int>> LargeOrderThresholdAsync(
        OwnerSession session, CancellationToken ct = default) =>
        SendAsync<Dictionary<string, int>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/orders/large/threshold"), OwnerAuth(session)), ct);

    /// <summary>Hands back presigned links to a large order's documents, to print directly.</summary>
    public Task<LargeOrderPrintResponse> PrintLargeOrderAsync(
        OwnerSession session, string orderId, CancellationToken ct = default) =>
        SendAsync<LargeOrderPrintResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/orders/{orderId}/print"), null, OwnerAuth(session)), ct);

    // --- print jobs, owner side ----------------------------------------------

    /// <summary>
    /// <c>needingAttention</c> is the one worth polling: it is the backend's own
    /// judgement about which jobs a person has to deal with, already accounting
    /// for retries and for <see cref="PrintJobStatus.PRINT_UNKNOWN"/>.
    /// </summary>
    public Task<List<PrintJobOwnerResponse>> ListPrintJobsAsync(
        OwnerSession session, bool needingAttention = false, int? limit = null, CancellationToken ct = default) =>
        SendAsync<List<PrintJobOwnerResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/print-jobs" + Query(
                ("needingAttention", needingAttention), ("limit", limit))), OwnerAuth(session)), ct);

    public Task ResolvePrintJobAsync(
        OwnerSession session, string jobId, bool success, string? note = null, CancellationToken ct = default) =>
        SendAsync(
            Post(Url($"/api/v1/shop/{session.ShopId}/print-jobs/{jobId}/resolve"),
                new ResolvePrintJobRequest(success, note), OwnerAuth(session)), ct);

    // --- batches -------------------------------------------------------------

    public Task<PageResponse<BatchResponse>> ListBatchesAsync(
        OwnerSession session, BatchStatus? status = null, int page = 0, int size = 20,
        CancellationToken ct = default) =>
        SendAsync<PageResponse<BatchResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/batches" + Query(
                ("status", status), ("page", page), ("size", size))), OwnerAuth(session)), ct);

    public Task<BatchResponse> CreateBatchAsync(
        OwnerSession session, IReadOnlyList<string> orderIds, string? name = null, CancellationToken ct = default) =>
        SendAsync<BatchResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/batches"), new CreateBatchRequest(orderIds, name),
                OwnerAuth(session)), ct);

    public Task<BatchResponse> BatchAsync(OwnerSession session, string batchId, CancellationToken ct = default) =>
        SendAsync<BatchResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}"), OwnerAuth(session)), ct);

    public Task<BatchResponse> AddBatchOrderAsync(
        OwnerSession session, string batchId, AddBatchOrderRequest request, CancellationToken ct = default) =>
        SendAsync<BatchResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/orders"), request, OwnerAuth(session)), ct);

    public Task<BatchResponse> RemoveBatchOrderAsync(
        OwnerSession session, string batchId, string orderId, CancellationToken ct = default) =>
        SendAsync<BatchResponse>(
            Delete(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/orders/{orderId}"), OwnerAuth(session)), ct);

    /// <summary>Sets the order the documents come off the printer in - and invalidates any generated PDF.</summary>
    public Task<BatchResponse> ResequenceBatchAsync(
        OwnerSession session, string batchId, IReadOnlyList<string> orderIds, CancellationToken ct = default) =>
        SendAsync<BatchResponse>(
            Patch(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/order-sequence"),
                new ResequenceBatchRequest(orderIds), OwnerAuth(session)), ct);

    /// <summary>
    /// Starts assembling the merged PDF. Returns immediately with the job's
    /// state - poll <see cref="BatchStatusAsync"/> until <c>Ready</c>.
    /// </summary>
    public Task<BatchStatusResponse> GenerateBatchAsync(
        OwnerSession session, string batchId, CancellationToken ct = default) =>
        SendAsync<BatchStatusResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/generate"), null, OwnerAuth(session)), ct);

    public Task<BatchStatusResponse> BatchStatusAsync(
        OwnerSession session, string batchId, CancellationToken ct = default) =>
        SendAsync<BatchStatusResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/status"), OwnerAuth(session)), ct);

    public Task<BatchPdfLinkResponse> BatchPreviewUrlAsync(
        OwnerSession session, string batchId, CancellationToken ct = default) =>
        SendAsync<BatchPdfLinkResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/preview-url"), OwnerAuth(session)), ct);

    /// <summary>
    /// <paramref name="idempotencyKey"/> is what stops a double tap printing
    /// twice: repeat the same key on a retry and the backend returns the
    /// original attempt with <c>AlreadyInitiated</c> set rather than starting a
    /// second one. Generate it once per user intent, not per call.
    /// </summary>
    public Task<PrintInitiatedResponse> PrintBatchAsync(
        OwnerSession session, string batchId, string? idempotencyKey = null, CancellationToken ct = default) =>
        SendAsync<PrintInitiatedResponse>(
            Idempotent(Post(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/print"), null, OwnerAuth(session)),
                idempotencyKey), ct);

    public Task<PrintInitiatedResponse> ReprintBatchAsync(
        OwnerSession session, string batchId, string? idempotencyKey = null, CancellationToken ct = default) =>
        SendAsync<PrintInitiatedResponse>(
            Idempotent(Post(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/reprint"), null, OwnerAuth(session)),
                idempotencyKey), ct);

    public Task<PrintAttemptResponse> ReportBatchPrintOutcomeAsync(
        OwnerSession session, string batchId, PrintOutcomeRequest request, CancellationToken ct = default) =>
        SendAsync<PrintAttemptResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/print-outcome"), request,
                OwnerAuth(session)), ct);

    public Task<List<PrintAttemptResponse>> BatchAttemptsAsync(
        OwnerSession session, string batchId, CancellationToken ct = default) =>
        SendAsync<List<PrintAttemptResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/attempts"), OwnerAuth(session)), ct);

    public Task<List<BatchEventResponse>> BatchEventsAsync(
        OwnerSession session, string batchId, CancellationToken ct = default) =>
        SendAsync<List<BatchEventResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/events"), OwnerAuth(session)), ct);

    public Task<BatchResponse> CompleteBatchAsync(
        OwnerSession session, string batchId, CancellationToken ct = default) =>
        SendAsync<BatchResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/complete"), null, OwnerAuth(session)), ct);

    public Task<BatchResponse> CancelBatchAsync(
        OwnerSession session, string batchId, string? reason = null, CancellationToken ct = default) =>
        SendAsync<BatchResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/batches/{batchId}/cancel"),
                reason is null ? null : new CancelBatchRequest(reason), OwnerAuth(session)), ct);

    /// <summary>Orders a student merged, which the agent prints together in <c>Sequence</c> order.</summary>
    public Task<List<ShopBundleResponse>> ListBundlesAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<List<ShopBundleResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/bundles"), OwnerAuth(session)), ct);

    // --- products ------------------------------------------------------------

    public Task<PageResponse<ProductResponse>> ListProductsAsync(
        OwnerSession session,
        bool includeArchived = false,
        bool? inStock = null,
        string? search = null,
        int page = 0,
        int size = 50,
        CancellationToken ct = default) =>
        SendAsync<PageResponse<ProductResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/products" + Query(
                ("includeArchived", includeArchived), ("inStock", inStock), ("search", search),
                ("page", page), ("size", size))), OwnerAuth(session)), ct);

    public Task<ProductResponse> CreateProductAsync(
        OwnerSession session, CreateProductRequest request, CancellationToken ct = default) =>
        SendAsync<ProductResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/products"), request, OwnerAuth(session)), ct);

    public Task<ProductResponse> ProductAsync(
        OwnerSession session, string productId, CancellationToken ct = default) =>
        SendAsync<ProductResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/products/{productId}"), OwnerAuth(session)), ct);

    public Task<ProductResponse> UpdateProductAsync(
        OwnerSession session, string productId, UpdateProductRequest request, CancellationToken ct = default) =>
        SendAsync<ProductResponse>(
            Patch(Url($"/api/v1/shop/{session.ShopId}/products/{productId}"), request, OwnerAuth(session)), ct);

    public Task<ProductResponse> SetProductStockAsync(
        OwnerSession session, string productId, bool inStock, CancellationToken ct = default) =>
        SendAsync<ProductResponse>(
            Put(Url($"/api/v1/shop/{session.ShopId}/products/{productId}/stock"),
                new SetStockRequest(inStock), OwnerAuth(session)), ct);

    /// <summary>Archives rather than deletes - past orders still reference the product.</summary>
    public Task ArchiveProductAsync(OwnerSession session, string productId, CancellationToken ct = default) =>
        SendAsync(
            Delete(Url($"/api/v1/shop/{session.ShopId}/products/{productId}"), OwnerAuth(session)), ct);

    public Task<ImageUploadUrlResponse> ProductImageUploadUrlAsync(
        OwnerSession session, string productId, ImageUploadUrlRequest request, CancellationToken ct = default) =>
        SendAsync<ImageUploadUrlResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/products/{productId}/image/upload-url"), request,
                OwnerAuth(session)), ct);

    public Task<ProductResponse> SetProductImageAsync(
        OwnerSession session, string productId, string imageKey, CancellationToken ct = default) =>
        SendAsync<ProductResponse>(
            Put(Url($"/api/v1/shop/{session.ShopId}/products/{productId}/image"),
                new SetImageRequest(imageKey), OwnerAuth(session)), ct);

    public Task<ProductResponse> RemoveProductImageAsync(
        OwnerSession session, string productId, CancellationToken ct = default) =>
        SendAsync<ProductResponse>(
            Delete(Url($"/api/v1/shop/{session.ShopId}/products/{productId}/image"), OwnerAuth(session)), ct);

    // --- priority and queue speed --------------------------------------------

    public Task<PriorityModeResponse> PriorityModeAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<PriorityModeResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/priority"), OwnerAuth(session)), ct);

    public Task<PriorityModeResponse> SetPriorityModeAsync(
        OwnerSession session, bool enabled, CancellationToken ct = default) =>
        SendAsync<PriorityModeResponse>(
            Put(Url($"/api/v1/shop/{session.ShopId}/priority"), new SetPriorityModeRequest(enabled),
                OwnerAuth(session)), ct);

    /// <summary>
    /// The counter QR's current token, which rotates. Re-read it on
    /// <see cref="PriorityQrResponse.ExpiresInSeconds"/> rather than caching the
    /// rendered image, or the code on screen stops scanning.
    /// </summary>
    public Task<PriorityQrResponse> PriorityQrAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<PriorityQrResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/priority/qr"), OwnerAuth(session)), ct);

    /// <summary>What the backend believes this shop's printer does - every "ready by" rests on it.</summary>
    public Task<PrintSpeedResponse> PrintSpeedAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<PrintSpeedResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/print-speed"), OwnerAuth(session)), ct);

    public Task<PrintSpeedResponse> UpdatePrintSpeedAsync(
        OwnerSession session, UpdatePrintSpeedRequest request, CancellationToken ct = default) =>
        SendAsync<PrintSpeedResponse>(
            Patch(Url($"/api/v1/shop/{session.ShopId}/print-speed"), request, OwnerAuth(session)), ct);

    // --- settlements ---------------------------------------------------------

    public Task<PageResponse<SettlementResponse>> ListSettlementsAsync(
        OwnerSession session, SettlementStatus? status = null, int page = 0, int size = 20,
        CancellationToken ct = default) =>
        SendAsync<PageResponse<SettlementResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/settlements" + Query(
                ("status", status), ("page", page), ("size", size))), OwnerAuth(session)), ct);

    public Task<SettlementDetailResponse> SettlementAsync(
        OwnerSession session, string settlementId, CancellationToken ct = default) =>
        SendAsync<SettlementDetailResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/settlements/{settlementId}"), OwnerAuth(session)), ct);

    public Task<UnsettledBalanceResponse> UnsettledBalanceAsync(
        OwnerSession session, CancellationToken ct = default) =>
        SendAsync<UnsettledBalanceResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/settlements/unsettled"), OwnerAuth(session)), ct);

    // --- content reports -----------------------------------------------------

    public Task<ContentReportResponse> CreateContentReportAsync(
        OwnerSession session, CreateContentReportRequest request, CancellationToken ct = default) =>
        SendAsync<ContentReportResponse>(
            Post(Url($"/api/v1/shop/{session.ShopId}/content-reports"), request, OwnerAuth(session)), ct);

    /// <summary>
    /// The shop-less variant of the same thing, where the shop is named in the
    /// body rather than the path.
    ///
    /// Both routes exist because a report can be raised before the client knows
    /// which shop it belongs to - a device syncing a queue it recorded offline,
    /// which is also what <see cref="CreateContentReportRequest.ClientReportId"/>
    /// deduplicates. Prefer <see cref="CreateContentReportAsync"/> when the shop
    /// is known.
    /// </summary>
    public Task<ContentReportResponse> SubmitContentReportAsync(
        OwnerSession session, CreateContentReportRequest request, CancellationToken ct = default) =>
        SendAsync<ContentReportResponse>(
            Post(Url("/api/v1/shop/content-reports"), request, OwnerAuth(session)), ct);

    public Task<PageResponse<ContentReportResponse>> ListContentReportsAsync(
        OwnerSession session, ContentReportStatus? status = null, int page = 0, int size = 20,
        CancellationToken ct = default) =>
        SendAsync<PageResponse<ContentReportResponse>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/content-reports" + Query(
                ("status", status), ("page", page), ("size", size))), OwnerAuth(session)), ct);

    public Task<ContentReportResponse> ContentReportAsync(
        OwnerSession session, string reportId, CancellationToken ct = default) =>
        SendAsync<ContentReportResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/content-reports/{reportId}"), OwnerAuth(session)), ct);

    // --- dashboard, analytics and reports ------------------------------------

    /// <summary>One read for the whole counter screen - queue counts, today's takings and what needs attention.</summary>
    public Task<ShopDashboardResponse> DashboardAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<ShopDashboardResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/dashboard"), OwnerAuth(session)), ct);

    /// <summary><paramref name="from"/> and <paramref name="to"/> are required when the period is CUSTOM and ignored otherwise.</summary>
    public Task<ShopAnalyticsResponse> AnalyticsAsync(
        OwnerSession session, ReportPeriod period = ReportPeriod.LAST_7_DAYS,
        DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default) =>
        SendAsync<ShopAnalyticsResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/analytics" + Query(
                ("period", period), ("from", from), ("to", to))), OwnerAuth(session)), ct);

    public Task<PeakHoursResponse> PeakHoursAsync(
        OwnerSession session, ReportPeriod period = ReportPeriod.LAST_MONTH,
        DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default) =>
        SendAsync<PeakHoursResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/analytics/peak-hours" + Query(
                ("period", period), ("from", from), ("to", to))), OwnerAuth(session)), ct);

    public Task<FinanceSummary> ReportSummaryAsync(
        OwnerSession session, ReportPeriod period = ReportPeriod.THIS_MONTH,
        DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default) =>
        SendAsync<FinanceSummary>(
            Get(Url($"/api/v1/shop/{session.ShopId}/reports/summary" + Query(
                ("period", period), ("from", from), ("to", to))), OwnerAuth(session)), ct);

    public Task<List<DailyRow>> ReportDailyAsync(
        OwnerSession session, ReportPeriod period = ReportPeriod.THIS_MONTH,
        DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default) =>
        SendAsync<List<DailyRow>>(
            Get(Url($"/api/v1/shop/{session.ShopId}/reports/daily" + Query(
                ("period", period), ("from", from), ("to", to))), OwnerAuth(session)), ct);

    /// <summary>
    /// Starts an export and returns the job, not the file: the response comes
    /// back with <c>Ready</c> false and has to be polled before it can be
    /// downloaded.
    /// </summary>
    public Task<ExportJobResponse> ExportSalesAsync(
        OwnerSession session, ReportPeriod period = ReportPeriod.THIS_MONTH,
        DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default) =>
        SendAsync<ExportJobResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/reports/sales/export" + Query(
                ("period", period), ("from", from), ("to", to))), OwnerAuth(session)), ct);

    public Task<ExportJobResponse> ExportBatchesAsync(OwnerSession session, CancellationToken ct = default) =>
        SendAsync<ExportJobResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/reports/batches/export"), OwnerAuth(session)), ct);

    public Task<ExportJobResponse> ExportSettlementsAsync(
        OwnerSession session, ReportPeriod period = ReportPeriod.LAST_MONTH,
        DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default) =>
        SendAsync<ExportJobResponse>(
            Get(Url($"/api/v1/shop/{session.ShopId}/reports/settlements/export" + Query(
                ("period", period), ("from", from), ("to", to))), OwnerAuth(session)), ct);

    // -------------------------------------------------------------------------

    /// <summary>
    /// The optional note body those order transitions take.
    ///
    /// Null rather than an empty <see cref="ShopOrderActionRequest"/> when there
    /// is nothing to say, so no body is attached at all - the proxy in
    /// <c>WebUiServer</c> carries the long version of why an empty body on these
    /// exact routes comes back as a 500.
    /// </summary>
    private static ShopOrderActionRequest? Note(string? note) =>
        note is null ? null : new ShopOrderActionRequest(note);

    private static HttpRequestMessage Idempotent(HttpRequestMessage request, string? key)
    {
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return request;
    }
}
