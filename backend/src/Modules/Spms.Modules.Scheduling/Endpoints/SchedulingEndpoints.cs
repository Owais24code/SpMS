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
/// The SCH-020 / GUI-003 preflight flow: propose, receive a token and the
/// conflicts, then commit that token. The two halves are separate requests
/// because the operator decides in between, and the decision needs the
/// alternatives CON-001 requires to be shown.
/// </summary>
public static class SchedulingEndpoints
{
    public static void MapScheduling(this IEndpointRouteBuilder app)
    {
        app.MapGet("/services", Services);
        app.MapGet("/availability", Availability);
        app.MapPost("/schedule/preflight", Preflight);

        // "reassign", not "move": the operation changes start, provider and
        // room together, and half the callers read "move" as time-only. The
        // domain still says Move internally, deliberately — renaming
        // MoveProposal and appointment.move would break the stored audit
        // action and the client contract for no behavioural gain.
        app.MapPost("/appointments/{id}/reassign", Reassign);
        app.MapPost("/appointments/{id}/undo-reassign", UndoReassign);
        app.MapPost("/schedule/bulk-move", BulkMove);
    }

    /* ------------------------------- services ------------------------------ */

    /// <summary>[spa.read] The services this property offers, at its prices. A bare array: the catalogue is small.</summary>
    private static async Task<IResult> Services(HttpContext http, IServiceCatalog services, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        return Results.Json(await services.ListAsync(ctx.Tenant(), ct), Json.Options);
    }

    /* ----------------------------- availability ---------------------------- */

    private static async Task<IResult> Availability(
        HttpContext http, IAppointmentRepository repo, IServiceCatalog services,
        IPropertyDirectory properties, AppointmentAccess access, string? date, string? serviceId, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_view_board", ct) is { } refused) return refused;

        if (!DateOnly.TryParse(date ?? "", out var day))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "date is required as yyyy-MM-dd.",
                extensions: Problem.Ext("field_violations",
                    new[] { new { field = "date", rule = "iso_date_required" } }));

        // Treats whitespace as absent, consistently with the lookup below —
        // the two disagreed, so `?serviceId=` answered 422 "Unknown serviceId".
        var wanted = string.IsNullOrWhiteSpace(serviceId) ? null : serviceId.Trim();

        CatalogService? service = null;
        if (wanted is not null)
        {
            service = await services.FindAsync(ctx.Tenant(), wanted, ct);
            if (service is null)
                return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "Unknown serviceId.",
                    extensions: Problem.Ext("known_services",
                        (await services.ListAsync(ctx.Tenant(), ct)).Select(s => s.ServiceId).ToArray()));
        }

        var duration = service?.DurationMinutes ?? 60;

        var profile = await properties.FindAsync(ctx.Tenant(), ctx.Property(), ct);
        if (profile is null)
            return Problem.From(ApiError.NotFound, ctx.CorrelationId, "This tenant has no such property.");

        // The business day is built in the PROPERTY's zone, by the same
        // function /appointments?date= uses, so the two cannot disagree about
        // which day they are describing.
        var dayStartUtc = LocalClock.DayStartUtc(day, profile.TimeZoneId);

        if (!Guard.IsSaneInstant(dayStartUtc))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "date must fall between 2000 and 2100.");

        // Padded either side: an appointment that began the previous evening
        // still occupies this morning, and a day-bounded read reported it free.
        var live = (await repo.ListOverlappingAsync(
                ctx.Tenant(), ctx.Property(), dayStartUtc.AddHours(-12), dayStartUtc.AddDays(1).AddHours(12), ct))
            .Where(a => a.Status is not (AppointmentStatus.Cancelled or AppointmentStatus.NoShow))
            .ToList();

        var slots = new List<AvailabilitySlot>();

        // Half-hourly, and a slot is only offered if the whole treatment fits
        // before close. The bound was inclusive, so the last slot STARTED at
        // closing time and a 90-minute service ran to 18:30.
        // The property's own hours for that weekday; a closed day has no slots.
        var hours = profile.HoursOn(day) ?? (0, 0);
        for (var minutes = hours.Open; minutes + duration <= hours.Close; minutes += 30)
        {
            var start = dayStartUtc.AddMinutes(minutes);
            var end = start.AddMinutes(duration);

            var clashing = live.Where(a => a.Overlaps(start, end)).ToList();

            // Per-resource, not spa-wide. The old check asked "is the entire
            // property idle", so five bookings in five different rooms closed
            // five slots for everybody.
            var busyRooms = clashing.Select(a => a.RoomId).OfType<string>().Distinct().OrderBy(r => r).ToList();
            var busyProviders = clashing.Select(a => a.ProviderId).OfType<string>().Distinct().OrderBy(p => p).ToList();

            slots.Add(new AvailabilitySlot(
                start.ToString("O"),
                LocalClock.Label(start, profile.TimeZoneId),
                // Open unless every known room is taken; with no room model
                // yet, a slot with no clashes is open and one with clashes is
                // open-with-caveats, which the busy lists carry.
                Open: clashing.Count == 0,
                BusyRooms: busyRooms,
                BusyProviders: busyProviders));
        }

        return Results.Json(new
        {
            date = day.ToString("yyyy-MM-dd"),
            serviceId = service?.ServiceId,
            durationMinutes = duration,
            timeZone = profile.TimeZoneId,
            slots,
        }, Json.Options);
    }

    /* ------------------------------- preflight ----------------------------- */

    private static async Task<IResult> Preflight(
        HttpContext http, SchedulingService scheduling, IAppointmentRepository repo, IClock clock,
        AppointmentAccess access, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Schedule) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_preflight", ct) is { } refused) return refused;

        var (body, readFailure) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return readFailure!;

        if (!Guard.TryParse<PreflightRequest>(body, ctx, out var req, out var parseFailure))
            return parseFailure!;

        var violations = new List<object>();
        if (string.IsNullOrWhiteSpace(req!.AppointmentId)) violations.Add(new { field = "appointmentId", rule = "required" });

        if (!Guard.TryParseInstant(req.StartUtc, out var start))
            violations.Add(new { field = "startUtc", rule = "iso8601_required" });
        else if (!Guard.IsSaneInstant(start))
            violations.Add(new { field = "startUtc", rule = "out_of_range" });

        // Required, not defaulted. Defaulting it from the row we had just read
        // made the optimistic check assert the server's own value, so a
        // concurrent edit between read and commit was undetectable.
        if (req.FromRowVersion is null or < 1)
            violations.Add(new { field = "fromRowVersion", rule = "required" });

        if (violations.Count > 0)
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "One or more fields were not accepted.",
                extensions: Problem.Ext("field_violations", violations));

        var current = await repo.GetAsync(ctx.Tenant(), ctx.Property(), req.AppointmentId!, ct);
        if (current is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId);

        if (!current.IsReschedulable)
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                $"A {current.Status} appointment cannot be rescheduled.",
                extensions: Problem.Ext("appointment_status", current.Status.ToString()));

        // Refused here rather than at commit: minting a token that can only
        // fail wastes the operator's decision and the TTL.
        if (current.RowVersion != req.FromRowVersion)
            return Problem.From(ApiError.StaleVersion, ctx.CorrelationId,
                "The appointment changed since you read it. Re-read and preflight again.",
                extensions: Problem.Ext("current", AppointmentDto.From(current)));

        var proposal = new MoveProposal(
            req.AppointmentId!, start, req.ProviderId, req.RoomId, req.FromRowVersion!.Value);

        var result = await scheduling.PreflightAsync(ctx.Tenant(), ctx.Property(), current, proposal, ct);

        // Preflight always answers 200: conflicts are information, not a failed
        // request. Refusing here would deny the operator the alternatives
        // CON-002 requires them to be shown.
        return Results.Json(PreflightResponse.From(result, clock.UtcNow), Json.Options);
    }

    /* -------------------------------- commit ------------------------------- */

    private static async Task<IResult> Reassign(
        HttpContext http, SchedulingService scheduling, IIdempotencyStore idem, IAppointmentRepository repo,
        AppointmentAccess access, IClock clock, ILoggerFactory loggers, string id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Schedule) is { } denied) return denied;

        var (body, readFailure) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return readFailure!;

        var logger = loggers.CreateLogger(typeof(SchedulingEndpoints));

        return await Idempotency.RunAsync(
            http, idem, ctx, logger, "appointments.reassign", body, clock.UtcNow,
            () => ReassignCore(http, scheduling, repo, access, ctx, id, body, ct), ct);
    }

    private static async Task<Idempotency.Outcome> ReassignCore(
        HttpContext http, SchedulingService scheduling, IAppointmentRepository repo, AppointmentAccess access,
        RequestContext ctx, string id, string body, CancellationToken ct)
    {
        if (!Guard.TryParse<ReassignRequest>(body, ctx, out var req, out var parseFailure))
            return Idempotency.Refused(parseFailure!);

        if (string.IsNullOrWhiteSpace(req!.Token))
            return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "token is required — run /schedule/preflight first.",
                extensions: Problem.Ext("field_violations",
                    new[] { new { field = "token", rule = "required" } })));

        if (req.Reason is { Length: > 1000 })
            return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                "reason may not exceed 1000 characters."));

        var target = await repo.GetAsync(ctx.Tenant(), ctx.Property(), id, ct);
        if (target is not null)
        {
            if (await access.AppointmentAsync(ctx, target, "can_reassign", ct, strong: true) is { } refused)
                return Idempotency.Refused(refused);
            if (!string.IsNullOrWhiteSpace(req.Reason)
                && await access.AppointmentAsync(ctx, target, "can_override_soft_conflict", ct) is { } noOverride)
                return Idempotency.Refused(noOverride);
        }

        // The route id is passed down and checked against the token's own
        // proposal. It was previously ignored, so a client bug could
        // reschedule a different guest than the URL named.
        var outcome = await scheduling.CommitMoveAsync(
            ctx.Tenant(), ctx.Property(), id, req.Token!, req.Reason, ctx.CorrelationId, ct);

        switch (outcome.Outcome)
        {
            case SchedulingService.CommitOutcome.TokenInvalid:
                return Idempotency.Refused(Problem.From(ApiError.PreflightExpired, ctx.CorrelationId,
                    "That preflight token is unknown, already used, or expired. Re-run preflight against the current board.",
                    retryable: true));

            case SchedulingService.CommitOutcome.AppointmentMismatch:
                return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                    "This token was issued for a different appointment than the one in the URL.",
                    extensions: Problem.Ext(
                        "field_violations", new[] { new { field = "token", rule = "appointment_mismatch" } },
                        "token_appointment_id", outcome.Preflight!.Proposal.AppointmentId,
                        "url_appointment_id", id)));

            case SchedulingService.CommitOutcome.HardConflict:
                return Idempotency.Refused(Problem.From(ApiError.HardConflict, ctx.CorrelationId,
                    "A hard conflict cannot be overridden by any role.",
                    extensions: Problem.Ext("conflicts", ConflictDto.FromAll(outcome.Conflicts))));

            case SchedulingService.CommitOutcome.BoardChanged:
                // The board moved between the proposal and the commit. This is
                // the case trusting the preflight snapshot silently committed:
                // a room booked by someone else inside the 90-second window was
                // invisible, and CON-002 landed with a 200.
                return Idempotency.Refused(Problem.From(ApiError.HardConflict, ctx.CorrelationId,
                    "The board changed since you were shown these conflicts. Re-run preflight and decide again.",
                    retryable: true,
                    extensions: Problem.Ext(
                        "conflicts", ConflictDto.FromAll(outcome.Conflicts),
                        "conflicts_when_shown", ConflictDto.FromAll(outcome.Preflight!.Conflicts))));

            case SchedulingService.CommitOutcome.ReasonRequired:
                // The token survives this refusal, so the reason prompt the
                // client now shows can actually succeed. Consuming the token
                // first made it a dead end.
                return Idempotency.Refused(Problem.From(ApiError.SoftConflictApproval, ctx.CorrelationId,
                    "This move crosses a soft conflict. Supply a reason; it is audited. The token remains valid until it expires.",
                    extensions: Problem.Ext(
                        "conflicts", ConflictDto.FromAll(outcome.Conflicts),
                        "token", req.Token,
                        "expires_utc", outcome.Preflight!.ExpiresUtc.ToUniversalTime().ToString("O"))));

            case SchedulingService.CommitOutcome.NotReschedulable:
                return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                    $"A {outcome.Appointment!.Status} appointment cannot be rescheduled.",
                    extensions: Problem.Ext("appointment_status", outcome.Appointment.Status.ToString())));

            case SchedulingService.CommitOutcome.StaleVersion:
                return Idempotency.Refused(Problem.From(ApiError.StaleVersion, ctx.CorrelationId,
                    "The appointment changed while you were deciding. Re-read it and preflight again.",
                    extensions: outcome.Appointment is null
                        ? null
                        : Problem.Ext("current", AppointmentDto.From(outcome.Appointment))));

            case SchedulingService.CommitOutcome.NotFound:
                return Idempotency.Refused(Problem.From(ApiError.NotFound, ctx.CorrelationId));

            case SchedulingService.CommitOutcome.Committed:
                // CON-006: the same token undoes the move until UndoUntilUtc.
                var dto = AppointmentDto.From(outcome.Appointment!) with
                {
                    UndoUntilUtc = outcome.UndoUntilUtc?.ToUniversalTime().ToString("O"),
                };
                var json = JsonSerializer.Serialize(dto, Json.Options);
                http.Response.Headers.ETag = dto.ETag;
                return new Idempotency.Outcome(200, json, Durable: true,
                    Results.Content(json, "application/json", statusCode: 200));

            default:
                // An outcome added to the enum without a case here is a defect,
                // not a client error. Fail loudly in the 500 path rather than
                // quietly answering 200 as the old default did.
                throw new InvalidOperationException($"Unhandled commit outcome {outcome.Outcome}.");
        }
    }

    /* --------------------------------- undo -------------------------------- */

    private static async Task<IResult> UndoReassign(
        HttpContext http, SchedulingService scheduling, IIdempotencyStore idem, IAppointmentRepository repo,
        AppointmentAccess access, IClock clock, ILoggerFactory loggers, string id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Schedule) is { } denied) return denied;

        var (body, readFailure) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return readFailure!;

        return await Idempotency.RunAsync(
            http, idem, ctx, loggers.CreateLogger(typeof(SchedulingEndpoints)), "appointments.undo-reassign", body, clock.UtcNow,
            async () =>
            {
                if (!Guard.TryParse<UndoRequest>(body, ctx, out var req, out var parseFailure)) return Idempotency.Refused(parseFailure!);
                if (string.IsNullOrWhiteSpace(req!.Token))
                    return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                        "token is required: the one the reassign was committed with.",
                        extensions: Problem.Ext("field_violations", new[] { new { field = "token", rule = "required" } })));

                var target = await repo.GetAsync(ctx.Tenant(), ctx.Property(), id, ct);
                if (target is not null && await access.AppointmentAsync(ctx, target, "can_reassign", ct, strong: true) is { } refused)
                    return Idempotency.Refused(refused);

                var r = await scheduling.UndoMoveAsync(ctx.Tenant(), ctx.Property(), id, req.Token!, ctx.CorrelationId, ct);
                switch (r.Outcome)
                {
                    case SchedulingService.UndoOutcome.TokenInvalid:
                        return Idempotency.Refused(Problem.From(ApiError.NotFound, ctx.CorrelationId, "No committed move matches that token."));
                    case SchedulingService.UndoOutcome.AppointmentMismatch:
                        return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                            "This token committed a different appointment than the one in the URL."));
                    case SchedulingService.UndoOutcome.WindowClosed:
                        return Idempotency.Refused(Problem.From(ApiError.PreflightExpired, ctx.CorrelationId,
                            "The undo window has closed, or this move was already undone. Reschedule it explicitly instead."));
                    case SchedulingService.UndoOutcome.NotFound:
                        return Idempotency.Refused(Problem.From(ApiError.NotFound, ctx.CorrelationId));
                    case SchedulingService.UndoOutcome.StaleVersion:
                        return Idempotency.Refused(Problem.From(ApiError.StaleVersion, ctx.CorrelationId,
                            "The appointment changed after the move; undoing it now would overwrite that change.",
                            extensions: r.Appointment is null ? null : Problem.Ext("current", AppointmentDto.From(r.Appointment))));
                    case SchedulingService.UndoOutcome.NotReschedulable:
                        return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                            $"A {r.Appointment!.Status} appointment cannot be moved back."));
                    case SchedulingService.UndoOutcome.HardConflict:
                        return Idempotency.Refused(Problem.From(ApiError.HardConflict, ctx.CorrelationId,
                            "The original slot is no longer free.",
                            extensions: Problem.Ext("conflicts", ConflictDto.FromAll(r.Conflicts))));
                    case SchedulingService.UndoOutcome.Undone:
                        var dto = AppointmentDto.From(r.Appointment!);
                        var json = JsonSerializer.Serialize(dto, Json.Options);
                        http.Response.Headers.ETag = dto.ETag;
                        return new Idempotency.Outcome(200, json, Durable: true, Results.Content(json, "application/json", statusCode: 200));
                    default:
                        throw new InvalidOperationException($"Unhandled undo outcome {r.Outcome}.");
                }
            }, ct);
    }

    /* ------------------------------- bulk move ----------------------------- */

    private static async Task<IResult> BulkMove(
        HttpContext http, SchedulingService scheduling, IIdempotencyStore idem, AppointmentAccess access,
        SchedulingOptions options, IClock clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Schedule) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_bulk_move", ct) is { } refused) return refused;

        var (body, readFailure) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return readFailure!;

        return await Idempotency.RunAsync(
            http, idem, ctx, loggers.CreateLogger(typeof(SchedulingEndpoints)), "schedule.bulk-move", body, clock.UtcNow,
            async () =>
            {
                if (!Guard.TryParse<BulkMoveRequest>(body, ctx, out var req, out var parseFailure)) return Idempotency.Refused(parseFailure!);

                var moves = req!.Moves ?? [];
                var violations = new List<object>();
                if (moves.Count == 0) violations.Add(new { field = "moves", rule = "required" });
                if (moves.Count > options.BulkMoveLimit) violations.Add(new { field = "moves", rule = $"at_most_{options.BulkMoveLimit}" });
                if (req.Reason is { Length: > 1000 }) violations.Add(new { field = "reason", rule = "max_length_1000" });

                var items = new List<SchedulingService.BulkItem>();
                for (var i = 0; i < moves.Count; i++)
                {
                    var m = moves[i];
                    if (string.IsNullOrWhiteSpace(m.AppointmentId)) violations.Add(new { field = $"moves[{i}].appointmentId", rule = "required" });
                    if (!Guard.TryParseInstant(m.StartUtc, out var start) || !Guard.IsSaneInstant(start))
                        violations.Add(new { field = $"moves[{i}].startUtc", rule = "iso8601_required" });
                    if (m.FromRowVersion is null or < 1) violations.Add(new { field = $"moves[{i}].fromRowVersion", rule = "required" });
                    items.Add(new SchedulingService.BulkItem(m.AppointmentId ?? "", start, m.ProviderId, m.RoomId, m.FromRowVersion ?? 0));
                }
                if (violations.Count > 0)
                    return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                        "One or more fields were not accepted.", extensions: Problem.Ext("field_violations", violations)));

                if (!string.IsNullOrWhiteSpace(req.Reason) && await access.PropertyAsync(ctx, "can_override_soft_conflict", ct) is { } noOverride)
                    return Idempotency.Refused(noOverride);

                var dryRun = req.DryRun ?? false;
                var r = await scheduling.BulkMoveAsync(ctx.Tenant(), ctx.Property(), items, req.Reason, dryRun, ctx.CorrelationId, ct);
                var response = BulkMoveResponse.From(r);
                var ext = Problem.Ext("items", response.Items);
                switch (r.Outcome)
                {
                    case SchedulingService.BulkOutcome.Evaluated:
                        return new Idempotency.Outcome(200, "", Durable: false, Results.Json(response, Json.Options));
                    case SchedulingService.BulkOutcome.Committed:
                        var json = JsonSerializer.Serialize(response, Json.Options);
                        return new Idempotency.Outcome(200, json, Durable: true, Results.Content(json, "application/json", statusCode: 200));
                    case SchedulingService.BulkOutcome.Invalid:
                        return Idempotency.Refused(Problem.From(ApiError.ValidationFailed, ctx.CorrelationId,
                            "Some appointments cannot be moved (unknown, finished or listed twice). Nothing moved.", extensions: ext));
                    case SchedulingService.BulkOutcome.StaleVersion:
                        return Idempotency.Refused(Problem.From(ApiError.StaleVersion, ctx.CorrelationId,
                            "Some appointments changed since you read them. Nothing moved.", extensions: ext));
                    case SchedulingService.BulkOutcome.HardConflict:
                        return Idempotency.Refused(Problem.From(ApiError.HardConflict, ctx.CorrelationId,
                            "At least one move crosses a hard conflict. Nothing moved.", extensions: ext));
                    case SchedulingService.BulkOutcome.ReasonRequired:
                        return Idempotency.Refused(Problem.From(ApiError.SoftConflictApproval, ctx.CorrelationId,
                            "These moves cross soft conflicts. Supply a reason; it is audited. Nothing moved yet.", extensions: ext));
                    case SchedulingService.BulkOutcome.BoardChanged:
                        return Idempotency.Refused(Problem.From(ApiError.HardConflict, ctx.CorrelationId,
                            "The board changed while the moves were applied. Nothing moved; evaluate again.", retryable: true, extensions: ext));
                    default:
                        throw new InvalidOperationException($"Unhandled bulk outcome {r.Outcome}.");
                }
            }, ct);
    }
}
