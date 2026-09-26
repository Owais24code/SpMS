using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Spms.Modules.Commerce.Application;
using Spms.Modules.Commerce.Data;
using Spms.Modules.Commerce.Payments;
using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Scheduling.Endpoints;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Commerce.Endpoints;

public sealed record OrderLineDto(string OrderLineId, int LineNumber, string LineKind, string? AppointmentId, string? ServiceId, string Description,
    decimal Quantity, long UnitPriceMinor, long DiscountMinor, long TaxMinor, long NetMinor);

public sealed record TransactionDto(string PaymentTransactionId, string? PaymentIntentId, string TenderCode, string TransactionType, string Outcome,
    long AmountMinor, string CurrencyCode, string? CardBrand, string? CardLast4, string ProcessedUtc, string? OriginalTransactionId)
{
    public static TransactionDto From(PaymentTransactionRow t) => new(t.PaymentTransactionId.ToString(), t.PaymentIntentId?.ToString(), t.TenderCode,
        t.TransactionType, t.Outcome, t.AmountMinor, t.CurrencyCode, t.CardBrand, t.CardLast4, t.ProcessedAt.ToUniversalTime().ToString("O"),
        t.OriginalTransactionId?.ToString());
}

public sealed record OrderDto(string OrderId, string? OrderNumber, string? ReceiptNumber, string? GuestId, string? VisitId, string OwnerSystem,
    string Status, string CurrencyCode, long SubtotalMinor, long DiscountTotalMinor, long TaxTotalMinor, long TipTotalMinor, long TotalMinor,
    long PaidMinor, long RefundedMinor, long BalanceMinor, IReadOnlyList<OrderLineDto> Lines, IReadOnlyList<TransactionDto> Transactions,
    int RowVersion, string ETag)
{
    public static OrderDto From(OrderView v)
    {
        var o = v.Order;
        return new(o.CommerceOrderId.ToString(), o.OrderNumber, o.ReceiptNumber, o.GuestId?.ToString(), o.VisitId?.ToString(), o.OwnerSystem,
            o.Status, o.CurrencyCode, o.SubtotalMinor, o.DiscountTotalMinor, o.TaxTotalMinor, o.TipTotalMinor, o.TotalMinor,
            v.PaidMinor, v.RefundedMinor, v.BalanceMinor,
            v.Lines.Select(l => new OrderLineDto(l.OrderLineId.ToString(), l.LineNumber, l.LineKind, l.AppointmentId?.ToString(), l.ServiceId?.ToString(),
                l.Description, l.Quantity, l.UnitPriceMinor, l.DiscountMinor, l.TaxMinor, l.NetMinor)).ToList(),
            v.Transactions.Select(TransactionDto.From).ToList(), o.Version, $"\"{o.Version}\"");
    }
}

public sealed record IntentDto(string PaymentIntentId, string Purpose, string Status, long AmountMinor, string CurrencyCode, string? OrderId,
    string? AppointmentId, string? OriginalTransactionId, string? ReasonCode, string? ApprovedBy, string CreatedUtc, string? RequestedBy, int RowVersion, string ETag)
{
    public static IntentDto From(PaymentIntentRow i) => new(i.PaymentIntentId.ToString(), i.Purpose, i.Status, i.AmountMinor, i.CurrencyCode,
        i.CommerceOrderId?.ToString(), i.AppointmentId?.ToString(), i.OriginalTransactionId?.ToString(), i.ReasonCode, i.ApprovedBy?.ToString(),
        i.CreatedAt.ToUniversalTime().ToString("O"), i.CreatedBy?.ToString(), i.Version, $"\"{i.Version}\"");
}

public sealed record PaymentDto(IntentDto Intent, TransactionDto? Transaction, string Outcome, string? Message);

public sealed record CreateOrderRequest(string? GuestId, string? VisitId, List<string>? AppointmentIds);
public sealed record AddLineRequest(string? LineKind, string? AppointmentId, string? ServiceId, string? Description, decimal? Quantity,
    long? UnitPriceMinor, long? DiscountMinor, string? TaxCode);
public sealed record PayRequest(string? TenderCode, long? AmountMinor, string? PaymentMethodToken);
public sealed record VoidRequest(string? Reason);
public sealed record RefundRequest(long? AmountMinor, string? ReasonCode);
public sealed record RejectRequest(string? Reason);

/// <summary>
/// Orders, payments, deposits, refunds and reconciliation.
///
/// Money endpoints require an Idempotency-Key: a retried payment is the same
/// payment. An ambiguous provider outcome answers 202 PAYMENT_OUTCOME_AMBIGUOUS
/// with the intent to query (BR-014), never a prompt to pay again.
/// </summary>
public static class CommerceEndpoints
{
    public static IEndpointRouteBuilder MapCommerce(this IEndpointRouteBuilder app)
    {
        app.MapGet("/orders", List);
        app.MapPost("/orders", Create);
        app.MapGet("/orders/{id:guid}", Get);
        app.MapPost("/orders/{id:guid}/lines", AddLine);
        app.MapDelete("/orders/{id:guid}/lines/{lineId:guid}", RemoveLine);
        app.MapPost("/orders/{id:guid}/place", Place);
        app.MapPost("/orders/{id:guid}/void", Void);
        app.MapPost("/orders/{id:guid}/payments", Pay);
        app.MapGet("/orders/{id:guid}/references", References);
        app.MapPost("/appointments/{id:guid}/deposit", Deposit);
        app.MapGet("/payment-intents/{id:guid}", GetIntent);
        app.MapPost("/payment-intents/{id:guid}/resolve", Resolve);
        app.MapPost("/payment-transactions/{id:guid}/refunds", RequestRefund);
        app.MapPost("/payment-intents/{id:guid}/approve", ApproveRefund);
        app.MapPost("/payment-intents/{id:guid}/reject", RejectRefund);
        app.MapGet("/reconciliation", Reconcile);
        app.MapPost("/reconciliation/resolve-ambiguous", ResolveAll);
        return app;
    }

    private static Task<IResult?> Can(RequestContext ctx, IAccessDecider access, string relation, CancellationToken ct) =>
        Guard.RequireAccessAsync(ctx, access, relation, Fga.Property(ctx.PropertyId), ct: ct);

    private static IResult Invalid(RequestContext ctx, string? detail) => Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, detail);

    private static IResult OrderResult(HttpContext http, RequestContext ctx, CommerceService.Result<OrderView> r, int created = 200) => r.Outcome switch
    {
        CommerceService.Outcome.Ok => Json(http, OrderDto.From(r.Value!), created),
        CommerceService.Outcome.NotFound => Problem.From(ApiError.NotFound, ctx.CorrelationId, r.Detail),
        CommerceService.Outcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, "The order changed since you read it.",
            extensions: r.Value is null ? null : Problem.Ext("current", OrderDto.From(r.Value))),
        CommerceService.Outcome.Ambiguous => Problem.From(ApiError.OwnershipAmbiguous, ctx.CorrelationId, r.Detail),
        CommerceService.Outcome.Delegated => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
        CommerceService.Outcome.Illegal => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
        _ => Invalid(ctx, r.Detail),
    };

    private static IResult Json(HttpContext http, OrderDto dto, int status = 200)
    {
        http.Response.Headers.ETag = dto.ETag;
        return Results.Json(dto, Spms.Web.Json.Options, statusCode: status);
    }

    /* --------------------------------- orders -------------------------------- */

    private static async Task<IResult> List(HttpContext http, CommerceService commerce, IAccessDecider access, string? date, string? status, string? guestId, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (await Can(ctx, access, "can_read_payment_status", ct) is { } refused) return refused;
        DateOnly? day = DateOnly.TryParse(date ?? "", out var d) ? d : null;
        Guid? guest = Guid.TryParse(guestId, out var g) ? g : null;
        var rows = await commerce.ListAsync(day, status, guest, ct);
        return Results.Json(rows.Select(o => new { orderId = o.CommerceOrderId, o.OrderNumber, o.ReceiptNumber, o.Status, o.OwnerSystem, o.TotalMinor,
            o.CurrencyCode, guestId = o.GuestId, createdUtc = o.CreatedAt }).ToList(), Spms.Web.Json.Options);
    }

    private static async Task<IResult> Create(HttpContext http, CommerceService commerce, IAccessDecider access, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (await Can(ctx, access, "can_take_payment", ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<CreateOrderRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        Guid? guest = Guid.TryParse(req!.GuestId, out var g) ? g : null;
        Guid? visit = Guid.TryParse(req.VisitId, out var v) ? v : null;
        var r = await commerce.CreateAsync(guest, visit, ct);
        // Convenience: a cart opened with its appointments' service lines already on it.
        if (r.Outcome == CommerceService.Outcome.Ok && r.Value!.Order.Status == "Draft")
            foreach (var a in req.AppointmentIds ?? [])
                if (Guid.TryParse(a, out var aid))
                {
                    var added = await commerce.AddLineAsync(r.Value!.Order.CommerceOrderId, new NewLine("Service", aid, null, null, 1, null, 0, null), ct);
                    if (added.Outcome != CommerceService.Outcome.Ok) return OrderResult(http, ctx, added);
                    r = added;
                }
        if (r.Outcome == CommerceService.Outcome.Ok) http.Response.Headers.Location = $"/orders/{r.Value!.Order.CommerceOrderId}";
        return OrderResult(http, ctx, r, 201);
    }

    private static async Task<IResult> Get(HttpContext http, CommerceService commerce, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        var v = await commerce.ViewAsync(id, ct);
        if (v is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId);
        if (await Can(ctx, access, "can_read_payment_status", ct) is { } refused) return refused;
        return Json(http, OrderDto.From(v));
    }

    private static async Task<IResult> AddLine(HttpContext http, CommerceService commerce, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<AddLineRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        var kind = req!.LineKind ?? "Service";
        if (!LineKinds.All.Contains(kind) || kind == "Deposit") return Invalid(ctx, "lineKind must be Service, Option, Retail, Fee, Discount or Tip.");
        // A discount is a comp: the manager's right (can_approve_comp), not the till's.
        if (await Can(ctx, access, kind == "Discount" ? "can_approve_comp" : "can_take_payment", ct) is { } refused) return refused;
        var quantity = req.Quantity ?? 1;
        if (quantity <= 0 || quantity > 100) return Invalid(ctx, "quantity must be between 0 and 100.");
        if (req.UnitPriceMinor is < 0 || req.DiscountMinor is < 0) return Invalid(ctx, "Prices and discounts are positive amounts; a Discount line subtracts.");
        Guid? appointment = Guid.TryParse(req.AppointmentId, out var a) ? a : null;
        Guid? service = Guid.TryParse(req.ServiceId, out var s) ? s : null;
        return OrderResult(http, ctx, await commerce.AddLineAsync(id, new NewLine(kind, appointment, service, req.Description, quantity,
            req.UnitPriceMinor, req.DiscountMinor ?? 0, req.TaxCode), ct));
    }

    private static async Task<IResult> RemoveLine(HttpContext http, CommerceService commerce, IAccessDecider access, Guid id, Guid lineId, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (await Can(ctx, access, "can_take_payment", ct) is { } refused) return refused;
        return OrderResult(http, ctx, await commerce.RemoveLineAsync(id, lineId, ct));
    }

    private static async Task<IResult> Place(HttpContext http, CommerceService commerce, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Can(ctx, access, "can_take_payment", ct) is { } refused) return refused;
        return OrderResult(http, ctx, await commerce.PlaceAsync(id, version, ct));
    }

    private static async Task<IResult> Void(HttpContext http, CommerceService commerce, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<VoidRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (string.IsNullOrWhiteSpace(req!.Reason)) return Invalid(ctx, "A void needs a reason.");
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Can(ctx, access, "can_take_payment", ct) is { } refused) return refused;
        return OrderResult(http, ctx, await commerce.VoidAsync(id, version, req.Reason!, ct));
    }

    /// <summary>The calls SpMS owes (or made to) the system that owns commerce for this order (DEC-002).</summary>
    private static async Task<IResult> References(HttpContext http, SpmsDbContext db, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (await Can(ctx, access, "can_read_payment_status", ct) is { } refused) return refused;
        var rows = await db.Set<CommerceReferenceRow>().AsNoTracking().Where(r => r.CommerceOrderId == id).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        return Results.Json(rows.Select(r => new { referenceId = r.ReferenceId, r.OwnerSystem, r.Operation, r.ExternalId, r.Status, r.LastError,
            createdUtc = r.CreatedAt.ToUniversalTime().ToString("O") }), Spms.Web.Json.Options);
    }

    /* -------------------------------- payments ------------------------------- */

    private static IResult PaymentResultOf(HttpContext http, RequestContext ctx, CommerceService.Result<PaymentResult> r)
    {
        switch (r.Outcome)
        {
            case CommerceService.Outcome.Ok:
                return Results.Json(new PaymentDto(IntentDto.From(r.Value!.Intent), r.Value.Transaction is null ? null : TransactionDto.From(r.Value.Transaction),
                    r.Value.Outcome.ToString(), r.Value.Message), Spms.Web.Json.Options, statusCode: r.Value.Intent.Status == "Succeeded" ? 201 : 200);
            case CommerceService.Outcome.Ambiguous when r.Value is not null:
                http.Response.Headers.Location = $"/payment-intents/{r.Value.Intent.PaymentIntentId}";
                return Problem.From(ApiError.PaymentOutcomeAmbiguous, ctx.CorrelationId,
                    "The provider has not confirmed. Query this payment; do not take it again.",
                    extensions: Problem.Ext("payment_intent_id", r.Value.Intent.PaymentIntentId.ToString()));
            case CommerceService.Outcome.Ambiguous:
                return Problem.From(ApiError.OwnershipAmbiguous, ctx.CorrelationId, r.Detail);
            case CommerceService.Outcome.NotFound:
                return Problem.From(ApiError.NotFound, ctx.CorrelationId, r.Detail);
            case CommerceService.Outcome.Delegated or CommerceService.Outcome.Illegal:
                return Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail);
            default:
                return Invalid(ctx, r.Detail);
        }
    }

    private static string? RequireKey(HttpContext http) =>
        http.Request.Headers["Idempotency-Key"].FirstOrDefault() is { Length: >= 8 and <= 200 } k ? k : null;

    private static async Task<IResult> Pay(HttpContext http, CommerceService commerce, IAccessDecider access, IIdempotencyStore idem, IClock clock,
        ILoggerFactory loggers, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (await Can(ctx, access, "can_take_payment", ct) is { } refused) return refused;
        if (RequireKey(http) is not { } key) return Invalid(ctx, "Idempotency-Key (8–200 characters) is required on a payment.");
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        return await Idempotency.RunAsync(http, idem, ctx, loggers.CreateLogger(typeof(CommerceEndpoints)), "orders.pay", body, clock.UtcNow, async () =>
        {
            if (!Guard.TryParse<PayRequest>(body, ctx, out var req, out var parseFail)) return Idempotency.Refused(parseFail!);
            var r = await commerce.PayAsync(id, key, req!.TenderCode ?? "", req.AmountMinor, req.PaymentMethodToken, ct);
            return Outcome(http, ctx, r);
        }, ct);
    }

    private static async Task<IResult> Deposit(HttpContext http, CommerceService commerce,
        IAccessDecider access, IIdempotencyStore idem, IClock clock, ILoggerFactory loggers, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (await Can(ctx, access, "can_take_payment", ct) is { } refused) return refused;
        if (RequireKey(http) is not { } key) return Invalid(ctx, "Idempotency-Key (8–200 characters) is required on a deposit.");
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        return await Idempotency.RunAsync(http, idem, ctx, loggers.CreateLogger(typeof(CommerceEndpoints)), "appointments.deposit", body, clock.UtcNow, async () =>
        {
            if (!Guard.TryParse<PayRequest>(body, ctx, out var req, out var parseFail)) return Idempotency.Refused(parseFail!);
            var r = await commerce.TakeDepositAsync(id, key, req!.AmountMinor, req.TenderCode ?? Tenders.Card, req.PaymentMethodToken, ct);
            return Outcome(http, ctx, r);
        }, ct);
    }

    private static Idempotency.Outcome Outcome(HttpContext http, RequestContext ctx, CommerceService.Result<PaymentResult> r)
    {
        Telemetry.Payments.Add(1, new KeyValuePair<string, object?>("outcome", r.Value?.Outcome.ToString() ?? r.Outcome.ToString()));
        var result = PaymentResultOf(http, ctx, r);
        if (r.Outcome == CommerceService.Outcome.Ok)
        {
            var dto = new PaymentDto(IntentDto.From(r.Value!.Intent), r.Value.Transaction is null ? null : TransactionDto.From(r.Value.Transaction),
                r.Value.Outcome.ToString(), r.Value.Message);
            var status = r.Value.Intent.Status == "Succeeded" ? 201 : 200;
            var json = JsonSerializer.Serialize(dto, Spms.Web.Json.Options);
            // A settled payment (approved or declined) is remembered for replay; an ambiguous one is not — its answer changes.
            return new Idempotency.Outcome(status, json, Durable: true, Results.Content(json, "application/json", statusCode: status));
        }
        return Idempotency.Refused(result);
    }

    private static async Task<IResult> GetIntent(HttpContext http, CommerceService commerce, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (await Can(ctx, access, "can_read_payment_status", ct) is { } refused) return refused;
        var i = await commerce.IntentAsync(id, ct);
        return i is null ? Problem.From(ApiError.NotFound, ctx.CorrelationId) : Results.Json(IntentDto.From(i), Spms.Web.Json.Options);
    }

    private static async Task<IResult> Resolve(HttpContext http, CommerceService commerce, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (await Can(ctx, access, "can_read_payment_status", ct) is { } refused) return refused;
        return PaymentResultOf(http, ctx, await commerce.ResolveAsync(id, ct));
    }

    /* --------------------------------- refunds ------------------------------- */

    private static IResult RefundResult(RequestContext ctx, RefundService.Result r, int ok = 200) => r.Outcome switch
    {
        RefundService.Outcome.Ok => Results.Json(new { intent = IntentDto.From(r.Intent!), transaction = r.Transaction is null ? null : TransactionDto.From(r.Transaction) },
            Spms.Web.Json.Options, statusCode: ok),
        RefundService.Outcome.NotFound => Problem.From(ApiError.NotFound, ctx.CorrelationId),
        RefundService.Outcome.SameApprover => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, r.Detail),
        RefundService.Outcome.Illegal => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
        RefundService.Outcome.Failed => Problem.From(ApiError.DependencyTimeout, ctx.CorrelationId, "The provider did not complete the refund."),
        _ => Invalid(ctx, r.Detail),
    };

    private static async Task<IResult> RequestRefund(HttpContext http, RefundService refunds, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (await Can(ctx, access, "can_take_payment", ct) is not null && await Can(ctx, access, "can_refund", ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<RefundRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (req!.AmountMinor is not > 0 || string.IsNullOrWhiteSpace(req.ReasonCode)) return Invalid(ctx, "amountMinor and a reasonCode are required.");
        return RefundResult(ctx, await refunds.RequestAsync(id, req.AmountMinor.Value, req.ReasonCode!.Trim(), ct), 201);
    }

    private static async Task<IResult> ApproveRefund(HttpContext http, RefundService refunds, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Can(ctx, access, "can_refund", ct) is { } refused) return refused;
        return RefundResult(ctx, await refunds.ApproveAsync(id, version, ct));
    }

    private static async Task<IResult> RejectRefund(HttpContext http, RefundService refunds, IAccessDecider access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Commerce) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<RejectRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (string.IsNullOrWhiteSpace(req!.Reason)) return Invalid(ctx, "A rejection needs a reason.");
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await Can(ctx, access, "can_refund", ct) is { } refused) return refused;
        return RefundResult(ctx, await refunds.RejectAsync(id, version, req.Reason!, ct));
    }

    /* ------------------------------ reconciliation ---------------------------- */

    private static async Task<IResult> Reconcile(HttpContext http, ReconciliationService reconciliation, IPropertyDirectory properties, IAccessDecider access,
        string? date, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Reconcile) is { } denied) return denied;
        if (await Can(ctx, access, "can_reconcile", ct) is { } refused) return refused;
        if (!DateOnly.TryParse(date ?? "", out var day)) return Invalid(ctx, "date is required as yyyy-MM-dd.");
        var profile = await properties.FindAsync(ctx.Tenant(), ctx.Property(), ct);
        if (profile is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId);
        var from = LocalClock.DayStartUtc(day, profile.TimeZoneId);
        var r = await reconciliation.DayAsync(day, from, LocalClock.DayStartUtc(day.AddDays(1), profile.TimeZoneId), ct);
        return Results.Json(new
        {
            date = day.ToString("yyyy-MM-dd"), timeZone = profile.TimeZoneId, netMinor = r.NetMinor,
            totals = r.Totals, ambiguous = r.Ambiguous.Select(IntentDto.From), refundsAwaitingApproval = r.RefundsAwaitingApproval.Select(IntentDto.From),
            transactions = r.Transactions.Select(TransactionDto.From),
        }, Spms.Web.Json.Options);
    }

    private static async Task<IResult> ResolveAll(HttpContext http, ReconciliationService reconciliation, IAccessDecider access, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Reconcile) is { } denied) return denied;
        if (await Can(ctx, access, "can_reconcile", ct) is { } refused) return refused;
        return Results.Json(new { settled = await reconciliation.ResolveAllAsync(ct) }, Spms.Web.Json.Options);
    }
}
