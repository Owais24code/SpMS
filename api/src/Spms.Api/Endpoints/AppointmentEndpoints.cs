using Spms.Api.Http;
using Spms.Application;
using Spms.Domain.Concurrency;
using Spms.Domain.Errors;
using Spms.Domain.Permissions;
using Spms.Domain.Scheduling;

namespace Spms.Api.Endpoints;

public sealed record CreateAppointmentRequest(
    string GuestAlias, string ServiceCode, string ProviderId,
    string RoomId, DateTimeOffset StartUtc, int DurationMinutes);

public sealed record TransitionRequest(string To);

public static class AppointmentEndpoints
{
    public static void MapAppointments(this IEndpointRouteBuilder app)
    {
        // ------------------------------------------------------------- create
        app.MapPost("/appointments",
            (HttpContext ctx, AppointmentService svc, IIdempotencyStore idem,
             CreateAppointmentRequest req) =>
        {
            var caller = CallerAccessor.From(ctx);
            var key = ctx.Request.Headers[Idempotency.Header].ToString();

            // API-001: same key + same body replays the original result and
            // does NOT create a second appointment. Same key + different body
            // is a client bug, and is refused rather than guessed at.
            var check = Idempotency.Inspect(idem, caller.TenantId, "appointments.create", key, req);
            if (check.Mismatch)
                return ProblemResults.From(SpmsProblem.IdempotencyMismatch, caller.CorrelationId,
                    "This Idempotency-Key was already used with a different request body");

            if (check is { IsReplay: true, Record: not null })
                return Results.Content(check.Record.ResponseJson, "application/json",
                    statusCode: check.Record.StatusCode);

            var outcome = svc.Create(
                new CreateAppointment(req.GuestAlias, req.ServiceCode, req.ProviderId,
                                      req.RoomId, req.StartUtc, req.DurationMinutes),
                caller);

            if (outcome is WriteOutcome<Appointment>.Committed ok)
            {
                var dto = Dto.Of(ok.Value);
                Idempotency.Remember(idem, caller.TenantId, "appointments.create", key, req, 201, dto);
                ctx.Response.Headers.ETag = ok.Value.ETag;
                return Results.Created($"/appointments/{ok.Value.AppointmentId}", dto);
            }

            return ScheduleEndpoints.Respond(ctx, outcome, caller.CorrelationId);
        })
        .WithName("CreateAppointment")
        .WithSummary("Create an appointment. Idempotency-Key is honoured.");

        // ---------------------------------------------------------------- get
        app.MapGet("/appointments/{id:guid}",
            (HttpContext ctx, AppointmentService svc, Guid id) =>
        {
            var caller = CallerAccessor.From(ctx);
            if (!caller.Has(SpmsScopes.Read))
                return ProblemResults.From(SpmsProblem.AuthorizationDenied, caller.CorrelationId);

            var found = svc.Get(id);
            if (found is null)
                return ProblemResults.From(SpmsProblem.NotFound, caller.CorrelationId);

            ctx.Response.Headers.ETag = found.ETag;
            return Results.Ok(Dto.Of(found));
        })
        .WithName("GetAppointment");

        // --------------------------------------------------------- transition
        app.MapPost("/appointments/{id:guid}/transitions",
            (HttpContext ctx, AppointmentService svc, Guid id, TransitionRequest req) =>
        {
            var caller = CallerAccessor.From(ctx);

            if (!Enum.TryParse<AppointmentStatus>(req.To, ignoreCase: true, out var to))
                return ProblemResults.From(SpmsProblem.ValidationFailed, caller.CorrelationId,
                    $"'{req.To}' is not an appointment status");

            if (!ScheduleEndpoints.TryIfMatch(ctx, out var version))
                return ProblemResults.From(SpmsProblem.ValidationFailed, caller.CorrelationId,
                    "If-Match with the current ETag is required on a consequential change");

            return ScheduleEndpoints.Respond(ctx, svc.Transition(id, to, version, caller), caller.CorrelationId);
        })
        .WithName("TransitionAppointment")
        .WithSummary("Move an appointment through its lifecycle. Illegal transitions are refused.");
    }
}
