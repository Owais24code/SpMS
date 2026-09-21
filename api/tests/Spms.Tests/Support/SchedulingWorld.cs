using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;
using Spms.Infrastructure.InMemory;

namespace Spms.Tests.Support;

/// <summary>
/// A scheduling world backed by the in-memory adapters.
///
/// Two appointments on a known day: a1 is a 90-minute deep tissue with
/// prov-lena in room-1 at 09:00, a2 is a 45-minute facial with prov-priya in
/// room-2 at 13:30. A third, x1, sits at another property in room-1 at the
/// same hour as a1, so scoping is proven rather than assumed.
///
/// These cases run without a database on purpose: they are the fast suite, and
/// they exercise the rules. What they cannot prove — that the audit row and
/// the appointment write land together, and that CON-002 holds under real
/// concurrency — is proven by the Postgres suite instead, against the same
/// ports.
/// </summary>
public sealed class SchedulingWorld
{
    public const string Tenant = "tenant-test";
    public const string Property = "prop-test";
    public const string Elsewhere = "prop-elsewhere";
    public const string GuestOfA1 = "guest-0001";

    public InMemoryAppointmentRepository Repo { get; } = new();
    public InMemoryPreflightStore Preflights { get; } = new();
    public InMemoryAuditSink Audit { get; } = new();
    public InMemoryServiceCatalog Services { get; } = new();
    public InMemoryQualificationRegister Qualifications { get; } = new();
    public InMemoryPropertyDirectory Properties { get; } = new();
    public TestClock Clock { get; }
    public SchedulingService Scheduling { get; }
    public DateTimeOffset Day { get; }

    public SchedulingWorld(int roomTurnoverMinutes = 15, int providerTransitionMinutes = 10)
    {
        Day = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        Clock = new TestClock(Day.AddHours(8));

        Services.Add(Tenant,
            new CatalogService("svc-deep", "Deep tissue 90", 90),
            new CatalogService("svc-aroma", "Aromatherapy 60", 60),
            new CatalogService("svc-facial", "Facial 45", 45),
            new CatalogService("svc-hotstone", "Hot stone 60", 60),
            new CatalogService("svc-swedish", "Swedish 60", 60),
            new CatalogService("svc-peel", "Peel 30", 30));

        var buffers = new BufferPolicy(roomTurnoverMinutes, providerTransitionMinutes);
        Properties.Add(Tenant, new PropertyProfile(Property, "UTC", 540, 1020, buffers, new Dictionary<string, BufferPolicy>()));
        Properties.Add(Tenant, new PropertyProfile(Elsewhere, "UTC", 540, 1020, buffers, new Dictionary<string, BufferPolicy>()));

        Qualifications.Grant(Tenant, Property, "prov-lena", "svc-deep");
        Qualifications.Grant(Tenant, Property, "prov-lena", "svc-aroma");
        Qualifications.Grant(Tenant, Property, "prov-lena", "svc-hotstone");
        Qualifications.Grant(Tenant, Property, "prov-lena", "svc-swedish");
        Qualifications.Grant(Tenant, Property, "prov-marco", "svc-deep");
        Qualifications.Grant(Tenant, Property, "prov-marco", "svc-swedish");
        Qualifications.Grant(Tenant, Property, "prov-marco", "svc-hotstone");
        Qualifications.Grant(Tenant, Property, "prov-priya", "svc-facial");
        Qualifications.Grant(Tenant, Property, "prov-priya", "svc-peel");

        Scheduling = new SchedulingService(
            Repo, Preflights, Audit, new NullUnitOfWork(),
            Services, Qualifications, Properties, Clock);

        Add(Appointment("a1", AppointmentStatus.Confirmed, Day.AddHours(9), 90, "prov-lena", "room-1",
            serviceId: "svc-deep", serviceName: "Deep tissue 90",
            guestId: GuestOfA1, alias: "Guest A", confirmation: "AAR000001"));

        Add(Appointment("a2", AppointmentStatus.Confirmed, Day.AddHours(13).AddMinutes(30), 45, "prov-priya", "room-2",
            serviceId: "svc-facial", serviceName: "Facial 45",
            guestId: "guest-0002", alias: "Guest B", confirmation: "AAR000002"));

        Add(Appointment("x1", AppointmentStatus.Confirmed, Day.AddHours(9), 60, "prov-lena", "room-1",
            guestId: "guest-9999", alias: "Guest X").CopyToProperty(Elsewhere));
    }

    public void Add(Appointment a)
    {
        if (!Repo.TryAddAsync(a).GetAwaiter().GetResult())
            throw new InvalidOperationException($"Fixture collided on {a.AppointmentId}.");
    }

    public Task<Appointment?> Get(string id) => Repo.GetAsync(Tenant, Property, id);

    public Task<SchedulingService.CommitResult> Commit(string routeId, string token, string? reason = null) =>
        Scheduling.CommitMoveAsync(Tenant, Property, routeId, token, reason, "actor-test", "corr-test");

    public Task<PreflightResult> Preflight(Appointment target, DateTimeOffset startUtc, string? provider, string? room) =>
        Scheduling.PreflightAsync(Tenant, Property, target,
            new MoveProposal(target.AppointmentId, startUtc, provider, room, target.RowVersion));

    /// <summary>
    /// Builds an appointment at an arbitrary status by walking the state
    /// machine, then resets the version to 1 — so a fixture cannot produce a
    /// state the API could not reach, and an ETag does not depend on how many
    /// transitions the status happened to need.
    /// </summary>
    public static Appointment Appointment(
        string id, AppointmentStatus status, DateTimeOffset startUtc, int minutes,
        string? provider = null, string? room = null,
        string serviceId = "svc-swedish", string serviceName = "Swedish 60",
        string guestId = "guest-0000", string alias = "Guest", string? confirmation = null)
    {
        var a = Spms.Domain.Scheduling.Appointment.Create(
            appointmentId: id, tenantId: Tenant, propertyId: Property, propertyTimeZone: "UTC",
            guestId: guestId, guestAlias: alias,
            serviceId: serviceId, serviceName: serviceName, durationMinutes: minutes,
            providerId: provider, roomId: room,
            startUtc: startUtc, confirmationNumber: confirmation,
            correlationId: "fixture", nowUtc: startUtc);

        foreach (var step in PathTo(status)) a.ApplyTransition(step, startUtc);

        return Spms.Domain.Scheduling.Appointment.Rehydrate(
            a.AppointmentId, a.TenantId, a.PropertyId, a.PropertyTimeZone, a.GuestId, a.GuestAlias,
            a.ServiceId, a.ServiceName, a.DurationMinutes, a.ProviderId, a.RoomId,
            a.StartUtc, a.Status, rowVersion: 1, a.ConfirmationNumber, a.CorrelationId,
            a.CreatedUtc, a.UpdatedUtc);
    }

    public static IEnumerable<AppointmentStatus> PathTo(AppointmentStatus target) => target switch
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

    public static AuditEntry AuditFor(string tenantId, string propertyId = Property) => new(
        AtUtc: DateTimeOffset.UtcNow, TenantId: tenantId, PropertyId: propertyId, Actor: "a",
        Action: "test", Purpose: "test", SubjectType: "appointment", SubjectId: "a1",
        SubjectVersion: 1, BeforeHash: null, AfterHash: null,
        ConflictCodes: [], SelectedResolution: null, TargetStatus: null, Reason: null, CorrelationId: "c");
}

public sealed class TestClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;
    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
