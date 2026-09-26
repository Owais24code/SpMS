using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Core.Data;
using Spms.Modules.Guest.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Scheduling.Endpoints;

public sealed record KioskLookup(string? ConfirmationNumber, string? LastName);
public sealed record KioskCheckIn(Guid? AppointmentId, string? ConfirmationNumber, string? LastName);

/// <summary>
/// The lobby kiosk (a registered device, SEC-013). A guest finds today's
/// booking with its confirmation number and their last name, and checks
/// themselves in. The kiosk sees only what the guest typed can reach: one
/// booking, its service and time, never a list, a name or a health detail.
/// The same pair is asked again at check-in, so a kiosk left on someone
/// else's booking cannot check in a different one.
/// </summary>
public static class KioskEndpoints
{
    private sealed class Hit
    {
        public Guid AppointmentId { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset StartAt { get; set; }
        public string ServiceName { get; set; } = "";
        public int Version { get; set; }
        public string? Alias { get; set; }
    }

    private static async Task<Hit?> FindAsync(SpmsDbContext db, RequestContext ctx, string confirmation, string lastName, CancellationToken ct)
    {
        var zone = await db.Set<PropertyRow>().AsNoTracking().Where(p => p.PropertyId == ctx.PropertyId).Select(p => p.Timezone).SingleAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime);
        var dayStart = LocalClock.DayStartUtc(today, zone);
        var dayEnd = LocalClock.DayStartUtc(today.AddDays(1), zone);
        var last = lastName.Trim().ToLowerInvariant();
        var number = confirmation.Trim().ToUpperInvariant();
        return await (from a in db.Set<AppointmentRow>().AsNoTracking()
                      where a.ConfirmationNumber == number && a.StartAt >= dayStart && a.StartAt < dayEnd
                      join g in db.Set<GuestRow>() on a.GuestId equals g.GuestId
                      where g.LegalLastName != null && g.LegalLastName.ToLower() == last
                      join s in db.Set<ServiceRow>() on a.ServiceId equals s.ServiceId
                      select new Hit { AppointmentId = a.AppointmentId, Status = a.Status, StartAt = a.StartAt, ServiceName = s.Name, Version = a.Version, Alias = g.DisplayAlias })
            .SingleOrDefaultAsync(ct);
    }

    private static IResult? RequireKiosk(RequestContext ctx) =>
        Guard.RequireScope(ctx, SpaScopes.Device) ?? (ctx.ActorType == ActorType.Device ? null
            : Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "The kiosk runs on a registered device."));

    public static IEndpointRouteBuilder MapKiosk(this IEndpointRouteBuilder app)
    {
        app.MapPost("/kiosk/lookup", async (HttpContext http, SpmsDbContext db, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (RequireKiosk(ctx) is { } denied) return denied;
            var (i, fail) = await WebApi.BodyAsync<KioskLookup>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.ConfirmationNumber) || string.IsNullOrWhiteSpace(i.LastName)) return WebApi.Invalid(ctx, "Your confirmation number and last name.");
            var hit = await FindAsync(db, ctx, i.ConfirmationNumber, i.LastName, ct);
            // Not found and not today read the same: the kiosk never confirms that a booking exists.
            if (hit is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId, "We could not find a booking for today with those details. Please see the front desk.");
            return Results.Json(new { appointmentId = hit.AppointmentId, serviceName = hit.ServiceName, startUtc = hit.StartAt.ToUniversalTime().ToString("O"),
                status = hit.Status, greeting = hit.Alias, canCheckIn = hit.Status == "Confirmed" }, Json.Options);
        });

        app.MapPost("/kiosk/check-in", async (HttpContext http, SpmsDbContext db, SchedulingService scheduling, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (RequireKiosk(ctx) is { } denied) return denied;
            var (i, fail) = await WebApi.BodyAsync<KioskCheckIn>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.AppointmentId is null || string.IsNullOrWhiteSpace(i.ConfirmationNumber) || string.IsNullOrWhiteSpace(i.LastName))
                return WebApi.Invalid(ctx, "appointmentId, confirmation number and last name.");
            var hit = await FindAsync(db, ctx, i.ConfirmationNumber, i.LastName, ct);
            if (hit is null || hit.AppointmentId != i.AppointmentId) return Problem.From(ApiError.NotFound, ctx.CorrelationId, "Please see the front desk.");
            if (hit.Status != "Confirmed") return Problem.From(ApiError.HardConflict, ctx.CorrelationId,
                hit.Status == "CheckedIn" ? "You are already checked in." : "This booking cannot be checked in here. Please see the front desk.");
            var r = await scheduling.TransitionAsync(ctx.Tenant(), ctx.Property(), hit.AppointmentId.ToString(), AppointmentStatus.CheckedIn, hit.Version,
                "Self check-in at the kiosk", ctx.CorrelationId, ct);
            return r.Outcome == SchedulingService.TransitionOutcome.Applied
                ? Results.Json(new { status = "CheckedIn", message = "You're checked in. Please take a seat; we'll come for you." }, Json.Options)
                : Problem.From(ApiError.HardConflict, ctx.CorrelationId, "Please see the front desk.");
        });

        return app;
    }
}
