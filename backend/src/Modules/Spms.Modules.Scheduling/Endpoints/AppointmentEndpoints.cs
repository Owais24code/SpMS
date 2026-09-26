using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Spms.Web;
using Spms.SharedKernel;
using Spms.Modules.Scheduling.Domain;

namespace Spms.Modules.Scheduling.Endpoints;

/// <summary>
/// Appointment reads, creation and lifecycle transitions. Reassignment lives
/// in <see cref="SchedulingEndpoints"/> because it goes through preflight and
/// shares nothing with these but the aggregate.
/// </summary>
public static class AppointmentEndpoints
{
    public static void MapAppointments(this IEndpointRouteBuilder app)
    {
        app.MapGet("/appointments/{id}", GetOne);
        app.MapGet("/appointments", List);
        app.MapPost("/appointments", Create);
        app.MapPost("/appointments/{id}/transitions", Transition);
    }

    /* ------------------------------- read one ------------------------------ */

    private static async Task<IResult> GetOne(
        HttpContext http, IAppointmentRepository repo, AppointmentAccess access, string id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;

        // Found first, then authorised: a record the caller's scope cannot see
        // is 404 whether or not they would have been allowed to read it.
        var a = await repo.GetAsync(ctx.Tenant(), ctx.Property(), id, ct);
        if (a is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId);
        if (await access.AppointmentAsync(ctx, a, "can_read", ct) is { } refused) return refused;

        var dto = AppointmentDto.From(a);
        // The ETag is how the client obtains the version it must send back.
        http.Response.Headers.ETag = dto.ETag;
        return Results.Json(dto, Json.Options);
    }

    /* -------------------------------- list --------------------------------- */

    private static async Task<IResult> List(
        HttpContext http, IAppointmentRepository repo, IPropertyDirectory properties, AppointmentAccess access,
        string? date, string? from, string? to, int? offset, int? limit, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_view_board", ct) is { } refused) return refused;

        DateTimeOffset windowFrom, windowTo;

        if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
        {
            if (!Guard.TryParseInstant(from, out windowFrom) || !Guard.TryParseInstant(to, out windowTo))
                return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                    "from and to must both be ISO-8601 instants.",
                    extensions: Problem.Ext("field_violations",
                        new[] { new { field = "from", rule = "iso8601_required" } }));
        }
        else if (DateOnly.TryParse(date ?? "", out var day))
        {
            // A calendar day is still served, but as an explicit interval so an
            // appointment running across midnight is returned by the day it
            // overlaps rather than only the day it starts on.
            //
            // The interval is the PROPERTY's day, not UTC's. It used to be UTC
            // midnight to UTC midnight while /availability built its grid in
            // the property's zone, so the two endpoints answered about
            // different days and a property at a large positive offset lost
            // its morning from the board while the grid still showed it.
            var profile = await properties.FindAsync(ctx.Tenant(), ctx.Property(), ct);
            if (profile is null)
                return Problem.From(ApiError.NotFound, ctx.CorrelationId, "This tenant has no such property.");

            windowFrom = LocalClock.DayStartUtc(day, profile.TimeZoneId);
            windowTo = LocalClock.DayStartUtc(day.AddDays(1), profile.TimeZoneId);
        }
        else
        {
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "Supply date=yyyy-MM-dd, or from and to as ISO-8601 instants.",
                extensions: Problem.Ext("field_violations",
                    new[] { new { field = "date", rule = "iso_date_required" } }));
        }

        if (windowTo <= windowFrom)
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "to must be after from.");

        if (windowTo - windowFrom > TimeSpan.FromDays(62))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "The window may not exceed 62 days. Page through shorter ranges.");

        if (!Guard.IsSaneInstant(windowFrom) || !Guard.IsSaneInstant(windowTo))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "Dates must fall between 2000 and 2100.");

        var (pageOffset, pageLimit) = Guard.Page(offset, limit);

        // Paged in the STORE, not in memory. Fetching the whole window and
        // slicing it afterwards paid the full server-side cost the paging was
        // introduced to avoid and only trimmed the response.
        var rows = await repo.ListPageAsync(
            ctx.Tenant(), ctx.Property(), windowFrom, windowTo, pageOffset, pageLimit, ct);
        var total = await repo.CountOverlappingAsync(ctx.Tenant(), ctx.Property(), windowFrom, windowTo, ct);

        var items = rows.Select(AppointmentDto.From).ToList();
        return Results.Json(new Page<AppointmentDto>(items, total, pageOffset, pageLimit), Json.Options);
    }

    /* -------------------------------- create ------------------------------- */

    private static async Task<IResult> Create(
        HttpContext http, IAppointmentRepository repo, IIdempotencyStore idem,
        SchedulingService scheduling, IServiceCatalog services, IClock clock, AppointmentAccess access,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Write) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_book", ct) is { } refused) return refused;

        var (body, readFailure) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return readFailure!;

        var logger = loggers.CreateLogger(typeof(AppointmentEndpoints));

        return await Idempotency.RunAsync(
            http, idem, ctx, logger, "appointments.create", body, clock.UtcNow,
            () => CreateCore(http, repo, scheduling, services, access, ctx, body, ct), ct);
    }

    private static async Task<Idempotency.Outcome> CreateCore(
        HttpContext http, IAppointmentRepository repo, SchedulingService scheduling,
        IServiceCatalog services, AppointmentAccess access, RequestContext ctx, string body, CancellationToken ct)
    {
        if (!Guard.TryParse<CreateAppointmentRequest>(body, ctx, out var req, out var parseFailure))
            return Idempotency.Refused(parseFailure!);

        var violations = new List<object>();
        if (string.IsNullOrWhiteSpace(req!.GuestId)) violations.Add(new { field = "guestId", rule = "required" });
        if (!Guid.TryParse(req.GuestId, out _)) violations.Add(new { field = "guestId", rule = "uuid_required" });
        if (req.Source is not null && !BookingSource.All.Contains(req.Source))
            violations.Add(new { field = "source", rule = "unknown_source" });
        if (req.HoldMinutes is < 1 or > 60) violations.Add(new { field = "holdMinutes", rule = "between_1_and_60" });
        if (req.VisitId is not null && !Guid.TryParse(req.VisitId, out _)) violations.Add(new { field = "visitId", rule = "uuid_required" });
        if (string.IsNullOrWhiteSpace(req.ServiceId)) violations.Add(new { field = "serviceId", rule = "required" });

        if (!Guard.TryParseInstant(req.StartUtc, out var start))
            violations.Add(new { field = "startUtc", rule = "iso8601_required" });
        else if (!Guard.IsSaneInstant(start))
            violations.Add(new { field = "startUtc", rule = "out_of_range" });

        if (req.Reason is { Length: > 1000 })
            violations.Add(new { field = "reason", rule = "max_length_1000" });

        if (violations.Count > 0)
            return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "One or more fields were not accepted.",
                extensions: Problem.Ext("field_violations", violations)));

        var confirmation = await NextConfirmationNumber(repo, ctx.Tenant(), ct);
        if (confirmation is null)
            // Our own defect, not a dependency being slow. Labelling it
            // DEPENDENCY_TIMEOUT sent every investigation to the wrong team and
            // made the 503 retry advice wrong.
            return Idempotency.Refused(Problem.From(ApiError.InternalError, ctx.CorrelationId,
                "Could not allocate a confirmation number."));

        // The service's duration and the property's time zone are resolved by
        // the domain service, which owns the reference data. An HTTP handler
        // that looks them up to assemble an aggregate is a second place where
        // the booking rules live.
        var booking = new SchedulingService.NewBooking(
            AppointmentId: Uuid7.New().ToString(),
            GuestId: req.GuestId!,
            // The alias shown on the board is the guest record's (IDN-002), read
            // back below; this is only the label for conflicts raised during create.
            GuestAlias: string.IsNullOrWhiteSpace(req.GuestAlias) ? "Guest" : req.GuestAlias!,
            ServiceId: req.ServiceId!,
            StartUtc: start,
            ProviderId: req.ProviderId,
            RoomId: req.RoomId,
            ConfirmationNumber: confirmation,
            CorrelationId: ctx.CorrelationId,
            Source: req.Source ?? BookingSource.Desk,
            HoldFor: req.HoldMinutes is { } hold ? TimeSpan.FromMinutes(hold) : null,
            VisitId: req.VisitId,
            GuestRequestedProvider: req.GuestRequestedProvider ?? false);

        // Create runs the same conflict rules as a reassign, inside one
        // transaction. It previously did neither, so a booking a reassign
        // would have refused could be typed straight into the board, and two
        // concurrent creates into one room both succeeded.
        // A reason is an override of a soft conflict, which is its own right
        // (spa_manager): the front desk can book, but not talk past a conflict.
        if (!string.IsNullOrWhiteSpace(req.Reason)
            && await access.PropertyAsync(ctx, "can_override_soft_conflict", ct) is { } noOverride)
            return Idempotency.Refused(noOverride);

        var result = await scheduling.CreateAsync(
            ctx.Tenant(), ctx.Property(), booking, req.Reason, ct);

        switch (result.Outcome)
        {
            case SchedulingService.CreateOutcome.UnknownService:
                return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                    "Unknown serviceId.",
                    extensions: Problem.Ext(
                        "field_violations", new[] { new { field = "serviceId", rule = "unknown_service" } },
                        "known_services", (await services.ListAsync(ctx.Tenant(), ct)).Select(s => s.ServiceId).ToArray())));

            case SchedulingService.CreateOutcome.UnknownProperty:
                // The token named a property this tenant does not have. Not a
                // 404 on the appointment: nothing was looked up yet.
                return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                    "This tenant has no such property."));

            case SchedulingService.CreateOutcome.HardConflict:
                return Idempotency.Refused(Problem.From(ApiError.HardConflict, ctx.CorrelationId,
                    "This booking cannot be created as specified.",
                    extensions: Problem.Ext("conflicts", ConflictDto.FromAll(result.Conflicts))));

            case SchedulingService.CreateOutcome.ReasonRequired:
                return Idempotency.Refused(Problem.From(ApiError.SoftConflictApproval, ctx.CorrelationId,
                    "This booking crosses a soft conflict. Supply a reason; it is audited.",
                    extensions: Problem.Ext("conflicts", ConflictDto.FromAll(result.Conflicts))));

            case SchedulingService.CreateOutcome.IdCollision:
                // A generated id collided, which should be impossible.
                // Surfacing it beats the silent overwrite the indexer performed.
                return Idempotency.Refused(Problem.From(ApiError.InternalError, ctx.CorrelationId,
                    "Could not allocate an appointment id. Retry."));

            case SchedulingService.CreateOutcome.UnknownGuest:
                return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                    "Unknown guestId.",
                    extensions: Problem.Ext("field_violations", new[] { new { field = "guestId", rule = "unknown_guest" } })));

            case SchedulingService.CreateOutcome.Created:
                var stored = await repo.GetAsync(ctx.Tenant(), ctx.Property(), result.Appointment!.AppointmentId, ct)
                             ?? result.Appointment!;
                var dto = AppointmentDto.From(stored);
                var json = JsonSerializer.Serialize(dto, Json.Options);
                http.Response.Headers.ETag = dto.ETag;
                http.Response.Headers.Location = $"/appointments/{dto.AppointmentId}";
                return new Idempotency.Outcome(201, json, Durable: true,
                    Results.Content(json, "application/json", statusCode: 201));

            default:
                throw new InvalidOperationException($"Unhandled create outcome {result.Outcome}.");
        }
    }

    /// <summary>
    /// Confirmation numbers are guest-facing and must not repeat: two guests
    /// quoting the same number at the desk is unresolvable. Uses a
    /// cryptographic RNG rather than Random.Shared, because a guest-facing
    /// number from a predictable PRNG is enumerable, and retries on the
    /// unlikely collision.
    /// </summary>
    private static async Task<string?> NextConfirmationNumber(
        IAppointmentRepository repo, string tenantId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = "AAR" + System.Security.Cryptography.RandomNumberGenerator
                .GetInt32(100_000_000, 1_000_000_000).ToString("D9");
            if (!await repo.ConfirmationNumberExistsAsync(tenantId, candidate, ct)) return candidate;
        }
        return null;
    }

    /* ------------------------------ transitions ---------------------------- */

    private static async Task<IResult> Transition(
        HttpContext http, SchedulingService scheduling, IAppointmentRepository repo, AppointmentAccess access,
        string id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Write) is { } denied) return denied;

        var (body, readFailure) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return readFailure!;

        if (!Guard.TryParse<TransitionRequest>(body, ctx, out var req, out var parseFailure))
            return parseFailure!;

        if (!Enum.TryParse<AppointmentStatus>(req!.To ?? "", ignoreCase: true, out var to))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "to must be a known appointment status.",
                extensions: Problem.Ext("allowed", Enum.GetNames<AppointmentStatus>()));

        if (req.Reason is { Length: > 1000 })
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "reason may not exceed 1000 characters.");

        // If-Match is required on a consequential change (API-002), and is read
        // before the record so a caller who omitted it is told so whether or
        // not the id exists.
        var ifMatch = http.Request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ifMatch))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "If-Match is required. Send the ETag you read.",
                extensions: Problem.Ext("field_violations",
                    new[] { new { field = "If-Match", rule = "required" } }));

        // A bare "*" is refused rather than honoured: it satisfies the header's
        // letter while defeating its point, and a client that always sent it
        // got last-write-wins with 412 unreachable.
        if (!Guard.TryParseIfMatchVersion(ifMatch, out var assertedVersion))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "If-Match must carry the ETag you read, such as \"3\". A wildcard is not accepted on this operation.",
                extensions: Problem.Ext("field_violations",
                    new[] { new { field = "If-Match", rule = "explicit_etag_required" } }));

        var current = await repo.GetAsync(ctx.Tenant(), ctx.Property(), id, ct);
        if (current is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId);
        if (await access.AppointmentAsync(ctx, current, to == AppointmentStatus.Cancelled ? "can_cancel" : "can_transition", ct, strong: true)
            is { } refused) return refused;

        var result = await scheduling.TransitionAsync(
            ctx.Tenant(), ctx.Property(), id, to, assertedVersion,
            req.Reason, ctx.CorrelationId, ct, req.ReasonCode);

        switch (result.Outcome)
        {
            case SchedulingService.TransitionOutcome.NotFound:
                return Problem.From(ApiError.NotFound, ctx.CorrelationId);

            case SchedulingService.TransitionOutcome.StaleVersion:
                return Problem.From(ApiError.StaleVersion, ctx.CorrelationId,
                    "The appointment changed since you read it.",
                    extensions: result.Appointment is null
                        ? null
                        : Problem.Ext("current", AppointmentDto.From(result.Appointment)));

            case SchedulingService.TransitionOutcome.Illegal:
                return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                    $"{result.Appointment!.Status} cannot move to {to}.",
                    extensions: Problem.Ext(
                        "from", result.Appointment.Status.ToString(),
                        "allowed", result.Allowed.Select(s => s.ToString()).ToArray()));

            case SchedulingService.TransitionOutcome.Applied:
                var dto = AppointmentDto.From(result.Appointment!);
                http.Response.Headers.ETag = dto.ETag;
                return Results.Json(dto, Json.Options);

            default:
                throw new InvalidOperationException($"Unhandled transition outcome {result.Outcome}.");
        }
    }
}
