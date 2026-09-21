using Spms.Api.Http;
using Spms.Application;
using Spms.Domain.Concurrency;
using Spms.Domain.Errors;
using Spms.Domain.Permissions;
using Spms.Domain.Scheduling;

namespace Spms.Api.Endpoints;

public sealed record PreflightRequest(
    Guid AppointmentId, string ProviderId, string RoomId,
    DateTimeOffset StartUtc, int DurationMinutes, int FromVersion);

public sealed record CommitMoveRequest(string Token, string? OverrideReason);

public static class ScheduleEndpoints
{
    public static void MapSchedule(this IEndpointRouteBuilder app)
    {
        // ---------------------------------------------------------- availability
        app.MapGet("/availability", (HttpContext ctx, AppointmentService svc, DateOnly? day) =>
        {
            var caller = CallerAccessor.From(ctx);
            if (!caller.Has(SpmsScopes.Read))
                return ProblemResults.From(SpmsProblem.AuthorizationDenied, caller.CorrelationId);

            var d = day ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var booked = svc.ForDay(caller.PropertyId, d);

            return Results.Ok(new
            {
                property_id = caller.PropertyId,
                day = d.ToString("yyyy-MM-dd"),
                booked = booked.Select(Dto.Of).ToArray(),
            });
        })
        .WithName("GetAvailability")
        .WithSummary("Booked windows for a property on a day.");

        // ------------------------------------------------------------ preflight
        // Validates a proposed move and quotes a token. Writes nothing —
        // the appointment is unchanged until /schedule/commit redeems it.
        app.MapPost("/schedule/preflight",
            (HttpContext ctx, PreflightService preflight, PreflightRequest req) =>
        {
            var caller = CallerAccessor.From(ctx);
            if (!caller.Has(SpmsScopes.Schedule))
                return ProblemResults.From(SpmsProblem.AuthorizationDenied, caller.CorrelationId);

            if (req.DurationMinutes <= 0)
                return ProblemResults.From(SpmsProblem.ValidationFailed, caller.CorrelationId,
                    "durationMinutes must be greater than zero");

            var token = preflight.Evaluate(
                new MoveProposal(req.AppointmentId, req.ProviderId, req.RoomId,
                                 req.StartUtc, req.DurationMinutes, req.FromVersion),
                caller);

            return Results.Ok(new
            {
                token = token.Token,
                expires_at = token.ExpiresAtUtc,
                proposed_start = token.Proposal.StartUtc,
                proposed_end = token.Proposal.StartUtc.AddMinutes(token.Proposal.DurationMinutes),
                commit_allowed = token.CommitAllowed,
                conflicts = token.Conflicts.Select(c => new
                {
                    code = c.Code,
                    severity = c.Severity.ToString().ToLowerInvariant(),
                    summary = c.Summary,
                    operational_impact = c.OperationalImpact,
                    financial_impact = c.FinancialImpact,
                    overridable = c.Overridable,
                    resolutions = c.Resolutions,
                }).ToArray(),
            });
        })
        .WithName("SchedulePreflight")
        .WithSummary("Validate a proposed move and receive a single-use commit token.");

        // --------------------------------------------------------------- commit
        app.MapPost("/schedule/commit",
            (HttpContext ctx, AppointmentService svc, CommitMoveRequest req) =>
        {
            var caller = CallerAccessor.From(ctx);

            if (!TryIfMatch(ctx, out var version))
                return ProblemResults.From(SpmsProblem.ValidationFailed, caller.CorrelationId,
                    "If-Match with the current ETag is required on a consequential change");

            var outcome = svc.CommitMove(req.Token, version, req.OverrideReason, caller);
            return Respond(ctx, outcome, caller.CorrelationId);
        })
        .WithName("ScheduleCommit")
        .WithSummary("Apply a preflighted move. Requires If-Match and a live token.");
    }

    /// <summary>
    /// API-002: a consequential change must state the version it was based on.
    /// Absent or malformed is a validation failure, not an implicit overwrite.
    /// </summary>
    internal static bool TryIfMatch(HttpContext ctx, out int version)
    {
        version = 0;
        var raw = ctx.Request.Headers.IfMatch.ToString().Trim('"', ' ');
        if (raw.StartsWith('v')) raw = raw[1..];
        return int.TryParse(raw, out version);
    }

    internal static IResult Respond(HttpContext ctx, WriteOutcome<Appointment> outcome, string correlationId) =>
        outcome switch
        {
            WriteOutcome<Appointment>.Committed c => WithETag(ctx, c.Value),

            // 412 carries the current record so the client can show what
            // changed and keep the user's unsaved edit.
            WriteOutcome<Appointment>.Stale s => ProblemResults.From(
                SpmsProblem.StaleVersion, correlationId,
                "This appointment changed while you were editing it",
                new Dictionary<string, object?>
                {
                    ["current_version"] = s.CurrentVersion,
                    ["current"] = Dto.Of(s.Current),
                }),

            WriteOutcome<Appointment>.Conflicted k =>
                ProblemResults.FromConflicts(k.Conflicts, correlationId),

            WriteOutcome<Appointment>.Rejected r =>
                ProblemResults.From(r.Problem, correlationId, r.Detail),

            _ => ProblemResults.From(SpmsProblem.ValidationFailed, correlationId),
        };

    internal static IResult WithETag(HttpContext ctx, Appointment a)
    {
        ctx.Response.Headers.ETag = a.ETag;
        return Results.Ok(Dto.Of(a));
    }
}

/// <summary>
/// Wire shape. Explicit rather than serializing the entity, so a new domain
/// field cannot leak to clients by accident — the restricted-field register
/// makes that a real risk.
/// </summary>
public static class Dto
{
    public static object Of(Appointment a) => new
    {
        appointment_id = a.AppointmentId,
        property_id = a.PropertyId,
        guest_alias = a.GuestAlias,
        service_code = a.ServiceCode,
        provider_id = a.ProviderId,
        room_id = a.RoomId,
        start_utc = a.StartUtc,
        end_utc = a.EndUtc,
        duration_minutes = a.DurationMinutes,
        status = a.Status.ToString(),
        row_version = a.RowVersion,
        etag = a.ETag,
    };
}
