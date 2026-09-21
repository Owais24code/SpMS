using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Spms.Api.Infrastructure;
using Spms.Domain.Abstractions;
using Spms.Domain.Errors;
using Spms.Domain.Permissions;
using Spms.Domain.Scheduling;

namespace Spms.Api.Endpoints;

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
        HttpContext http, IAppointmentRepository repo, string id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;

        var a = await repo.GetAsync(ctx.TenantId, ctx.PropertyId, id, ct);
        if (a is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId);

        var dto = AppointmentDto.From(a);
        // The ETag is how the client obtains the version it must send back.
        http.Response.Headers.ETag = dto.ETag;
        return Results.Json(dto, Json.Options);
    }

    /* -------------------------------- list --------------------------------- */

    private static async Task<IResult> List(
        HttpContext http, IAppointmentRepository repo,
        string? date, string? from, string? to, int? offset, int? limit, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;

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
            windowFrom = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero);
            windowTo = windowFrom.AddDays(1);
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

        var rows = await repo.ListOverlappingAsync(ctx.TenantId, ctx.PropertyId, windowFrom, windowTo, ct);

        var (pageOffset, pageLimit) = Guard.Page(offset, limit);

        // Paged BEFORE projecting. Mapping every row first — each with a
        // time-zone conversion — paid the full server-side cost the paging was
        // introduced to avoid, and only trimmed the response.
        var items = rows.Skip(pageOffset).Take(pageLimit).Select(AppointmentDto.From).ToList();
        return Results.Json(new Page<AppointmentDto>(items, rows.Count, pageOffset, pageLimit), Json.Options);
    }

    /* -------------------------------- create ------------------------------- */

    private static async Task<IResult> Create(
        HttpContext http, IAppointmentRepository repo, IIdempotencyStore idem,
        SchedulingService scheduling, IClock clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Write) is { } denied) return denied;

        var (body, readFailure) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return readFailure!;

        var logger = loggers.CreateLogger(typeof(AppointmentEndpoints));

        return await Idempotency.RunAsync(
            http, idem, ctx, logger, "appointments.create", body, clock.UtcNow,
            () => CreateCore(http, repo, scheduling, clock, ctx, body, ct), ct);
    }

    private static async Task<Idempotency.Outcome> CreateCore(
        HttpContext http, IAppointmentRepository repo, SchedulingService scheduling,
        IClock clock, RequestContext ctx, string body, CancellationToken ct)
    {
        if (!Guard.TryParse<CreateAppointmentRequest>(body, ctx, out var req, out var parseFailure))
            return Idempotency.Refused(parseFailure!);

        var violations = new List<object>();
        if (string.IsNullOrWhiteSpace(req!.GuestId)) violations.Add(new { field = "guestId", rule = "required" });
        if (string.IsNullOrWhiteSpace(req.GuestAlias)) violations.Add(new { field = "guestAlias", rule = "required" });
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

        var catalog = ServiceCatalog.Find(req.ServiceId!);
        if (catalog is null)
            return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "Unknown serviceId.",
                extensions: Problem.Ext(
                    "field_violations", new[] { new { field = "serviceId", rule = "unknown_service" } },
                    "known_services", ServiceCatalog.Services.Select(s => s.ServiceId).ToArray())));

        var confirmation = await NextConfirmationNumber(repo, ctx.TenantId, ct);
        if (confirmation is null)
            // Our own defect, not a dependency being slow. Labelling it
            // DEPENDENCY_TIMEOUT sent every investigation to the wrong team and
            // made the 503 retry advice wrong.
            return Idempotency.Refused(Problem.From(ApiError.InternalError, ctx.CorrelationId,
                "Could not allocate a confirmation number."));

        var profile = PropertyDirectory.For(ctx.PropertyId);

        var candidate = Appointment.Create(
            appointmentId: "appt-" + Guid.NewGuid().ToString("n")[..10],
            tenantId: ctx.TenantId, propertyId: ctx.PropertyId, propertyTimeZone: profile.TimeZoneId,
            guestId: req.GuestId!, guestAlias: req.GuestAlias!,
            serviceId: catalog.ServiceId, serviceName: catalog.Name, durationMinutes: catalog.DurationMinutes,
            providerId: req.ProviderId, roomId: req.RoomId,
            startUtc: start, confirmationNumber: confirmation,
            correlationId: ctx.CorrelationId, nowUtc: clock.UtcNow);

        // Create runs the same conflict rules as a reassign, and evaluates and
        // inserts under one gate. It previously did neither, so a booking a
        // reassign would have refused could be typed straight into the board,
        // and two concurrent creates into one room both succeeded.
        var result = await scheduling.CreateAsync(
            ctx.TenantId, ctx.PropertyId, candidate, req.Reason, ctx.Actor, ctx.CorrelationId, ct);

        switch (result.Outcome)
        {
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

            case SchedulingService.CreateOutcome.Created:
                var dto = AppointmentDto.From(result.Appointment!);
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
        HttpContext http, SchedulingService scheduling, string id, CancellationToken ct)
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

        var result = await scheduling.TransitionAsync(
            ctx.TenantId, ctx.PropertyId, id, to, assertedVersion,
            req.Reason, ctx.Actor, ctx.CorrelationId, ct);

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
