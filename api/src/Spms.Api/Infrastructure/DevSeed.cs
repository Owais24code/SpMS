using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;

namespace Spms.Api.Infrastructure;

/// <summary>
/// A day of demo data so the board has something in it. Development only, and
/// kept out of both the repository implementations (so swapping to EF Core
/// does not drag it along) and Program.cs (which is not where 50 lines of
/// fixture data belong).
/// </summary>
public static class DevSeed
{
    private const string Tenant = "tenant-demo";
    private const string Property = "prop-riverside";

    public static async Task ApplyAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var repo = services.GetRequiredService<IAppointmentRepository>();
        var clock = services.GetRequiredService<IClock>();
        var profile = PropertyDirectory.For(Property);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        Appointment Make(
            string id, string guestId, string alias, string svcId,
            string? provider, string? room, int hour, int minute, AppointmentStatus status)
        {
            var service = ServiceCatalog.Find(svcId)
                ?? throw new InvalidOperationException($"Seed references unknown service {svcId}.");

            var a = Appointment.Create(
                appointmentId: id, tenantId: Tenant, propertyId: Property,
                propertyTimeZone: profile.TimeZoneId,
                guestId: guestId, guestAlias: alias,
                serviceId: service.ServiceId, serviceName: service.Name,
                durationMinutes: service.DurationMinutes,
                providerId: provider, roomId: room,
                startUtc: new DateTimeOffset(today.Year, today.Month, today.Day, hour, minute, 0, TimeSpan.Zero),
                confirmationNumber: "AAR" + id[^6..],
                correlationId: "seed", nowUtc: clock.UtcNow);

            // Walk the state machine rather than assigning the status, so the
            // seed cannot produce a state the API itself could not reach.
            foreach (var step in PathTo(status)) a.ApplyTransition(step, clock.UtcNow);

            // Then present it as a freshly stored row at version 1. Leaving the
            // version at whatever the walk produced made a seeded appointment's
            // ETag depend on how many transitions its status happened to need,
            // which is an implementation detail no client should see.
            return Appointment.Rehydrate(
                a.AppointmentId, a.TenantId, a.PropertyId, a.PropertyTimeZone, a.GuestId, a.GuestAlias,
                a.ServiceId, a.ServiceName, a.DurationMinutes, a.ProviderId, a.RoomId,
                a.StartUtc, a.Status, rowVersion: 1, a.ConfirmationNumber, a.CorrelationId,
                a.CreatedUtc, a.UpdatedUtc);
        }

        // Seeded rows are deliberately conflict-free against each other, in
        // distinct rooms with qualified providers, so the board opens clean and
        // a demo conflict is something the operator creates.
        var seed = new[]
        {
            Make("appt-seed00001", "guest-4821", "Guest 4821", "svc-deep",     "prov-lena",  "room-suite3", 13, 0,  AppointmentStatus.InService),
            Make("appt-seed00002", "guest-4822", "Guest 4822", "svc-facial",   "prov-priya", "room-2",      13, 30, AppointmentStatus.Confirmed),
            Make("appt-seed00003", "guest-4823", "Guest 4823", "svc-aroma",    null,         "room-suite1", 15, 0,  AppointmentStatus.Confirmed),
            Make("appt-seed00004", "guest-4824", "Guest 4824", "svc-hotstone", "prov-marco", "room-4",      14, 15, AppointmentStatus.Confirmed),
            Make("appt-seed00005", "guest-4825", "Guest 4825", "svc-swedish",  "prov-marco", "room-5",      16, 0,  AppointmentStatus.Draft),
        };

        foreach (var a in seed)
        {
            if (!await repo.TryAddAsync(a, ct))
                throw new InvalidOperationException($"Seed collided on {a.AppointmentId}.");
        }
    }

    /// <summary>The legal transition path from Draft to a target status.</summary>
    private static IEnumerable<AppointmentStatus> PathTo(AppointmentStatus target) => target switch
    {
        AppointmentStatus.Draft => [],
        AppointmentStatus.Held => [AppointmentStatus.Held],
        AppointmentStatus.Confirmed => [AppointmentStatus.Confirmed],
        AppointmentStatus.CheckedIn => [AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn],
        AppointmentStatus.Ready => [AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn, AppointmentStatus.Ready],
        AppointmentStatus.InService =>
            [AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn, AppointmentStatus.Ready, AppointmentStatus.InService],
        AppointmentStatus.Completed =>
            [AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn, AppointmentStatus.Ready,
             AppointmentStatus.InService, AppointmentStatus.Completed],
        AppointmentStatus.Cancelled => [AppointmentStatus.Cancelled],
        AppointmentStatus.NoShow => [AppointmentStatus.Confirmed, AppointmentStatus.NoShow],
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
}
