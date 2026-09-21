using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;
using Spms.Infrastructure.InMemory;

namespace Spms.Tests;

/*
 * Zero-dependency test harness.
 *
 * NuGet is unreachable in this environment, so xunit is not an option. This is
 * a plain console runner with the guarantees that matter: named cases, an
 * isolated fixture per case, and a non-zero exit on failure. Swap for xunit
 * when package restore is available — the assertions port directly.
 *
 * Split out of the test file, which had grown to 800 lines of cases plus the
 * runner plus the assertion library plus the fixture.
 */

public sealed class TestClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;
    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// One fixture per test. Two appointments on a known day: a1 is a 90-minute
/// deep tissue with prov-lena in room-1 at 09:00, a2 is a 45-minute facial
/// with prov-priya in room-2 at 13:30. A third, x1, sits at another property
/// in room-1 at the same hour as a1, so scoping is proven rather than assumed.
/// </summary>
public sealed class Fixture
{
    public const string Tenant = "tenant-test";
    public const string Property = "prop-test";
    public const string GuestOfA1 = "guest-0001";

    public InMemoryAppointmentRepository Repo { get; } = new();
    public InMemoryPreflightStore Preflights { get; } = new();
    public InMemoryAuditSink Audit { get; } = new();
    public TestClock Clock { get; }
    public SchedulingService Scheduling { get; }
    public DateTimeOffset Day { get; }

    public Fixture()
    {
        Day = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        Clock = new TestClock(Day.AddHours(8));
        Scheduling = new SchedulingService(Repo, Preflights, Audit, Clock);

        Add(Appointment("a1", AppointmentStatus.Confirmed, Day.AddHours(9), 90, "prov-lena", "room-1",
            serviceId: "svc-deep", serviceName: "Deep tissue 90",
            guestId: GuestOfA1, alias: "Guest A", confirmation: "AAR000001"));

        Add(Appointment("a2", AppointmentStatus.Confirmed, Day.AddHours(13).AddMinutes(30), 45, "prov-priya", "room-2",
            serviceId: "svc-facial", serviceName: "Facial 45",
            guestId: "guest-0002", alias: "Guest B", confirmation: "AAR000002"));

        Add(Appointment("x1", AppointmentStatus.Confirmed, Day.AddHours(9), 60, "prov-lena", "room-1",
            guestId: "guest-9999", alias: "Guest X").CopyToProperty("prop-elsewhere"));
    }

    public void Add(Spms.Domain.Scheduling.Appointment a)
    {
        if (!Repo.TryAddAsync(a).GetAwaiter().GetResult())
            throw new InvalidOperationException($"Fixture collided on {a.AppointmentId}.");
    }

    public Task<Spms.Domain.Scheduling.Appointment?> Get(string id) => Repo.GetAsync(Tenant, Property, id);

    public Task<SchedulingService.CommitResult> Commit(string routeId, string token, string? reason = null) =>
        Scheduling.CommitMoveAsync(Tenant, Property, routeId, token, reason, "actor-test", "corr-test");

    public Task<PreflightResult> Preflight(
        Spms.Domain.Scheduling.Appointment target, DateTimeOffset startUtc, string? provider, string? room) =>
        Scheduling.PreflightAsync(Tenant, Property, target,
            new MoveProposal(target.AppointmentId, startUtc, provider, room, target.RowVersion));

    /// <summary>
    /// Builds an appointment at an arbitrary status by walking the state
    /// machine, so a fixture cannot produce a state the API could not reach.
    /// </summary>
    public static Spms.Domain.Scheduling.Appointment Appointment(
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

        // Reset the version so fixtures start at 1 regardless of how many
        // transitions it took to reach the status.
        return Spms.Domain.Scheduling.Appointment.Rehydrate(
            a.AppointmentId, a.TenantId, a.PropertyId, a.PropertyTimeZone, a.GuestId, a.GuestAlias,
            a.ServiceId, a.ServiceName, a.DurationMinutes, a.ProviderId, a.RoomId,
            a.StartUtc, a.Status, rowVersion: 1, a.ConfirmationNumber, a.CorrelationId,
            a.CreatedUtc, a.UpdatedUtc);
    }

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

    public static AuditEntry AuditFor(string tenantId, string propertyId = Property) => new(
        AtUtc: DateTimeOffset.UtcNow, TenantId: tenantId, PropertyId: propertyId, Actor: "a",
        Action: "test", Purpose: "test", SubjectType: "appointment", SubjectId: "a1",
        SubjectVersion: 1, BeforeHash: null, AfterHash: null,
        ConflictCodes: [], SelectedResolution: null, TargetStatus: null, Reason: null, CorrelationId: "c");
}

public sealed class Runner
{
    private int _passed;
    private readonly List<string> _failures = [];

    public void Test(string name, Action body) =>
        Record(name, () => { body(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    public Task TestAsync(string name, Func<Task> body) => Record(name, body);

    private async Task Record(string name, Func<Task> body)
    {
        try
        {
            await body();
            _passed++;
            Console.WriteLine($"  pass  {name}");
        }
        catch (Exception ex)
        {
            _failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.Message}");
        }
    }

    public int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failures.Count} failed");
        foreach (var f in _failures) Console.WriteLine($"  - {f}");
        return _failures.Count == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void True(bool condition, string? because = null)
    {
        if (!condition) throw new Exception(because is null ? "expected true" : $"expected true: {because}");
    }

    public static void False(bool condition, string? because = null)
    {
        if (condition) throw new Exception(because is null ? "expected false" : $"expected false: {because}");
    }

    public static void Equal<T>(T expected, T actual, string? because = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected {expected}, got {actual}{(because is null ? "" : $" ({because})")}");
    }

    public static void NotEqual<T>(T unexpected, T actual, string? because = null)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new Exception($"expected something other than {unexpected}{(because is null ? "" : $" ({because})")}");
    }

    public static void NotNull(object? value, string? because = null)
    {
        if (value is null) throw new Exception(because is null ? "expected non-null" : $"expected non-null: {because}");
    }

    public static void Null(object? value, string? because = null)
    {
        if (value is not null) throw new Exception(because is null ? $"expected null, got {value}" : $"expected null: {because}");
    }

    public static void Throws<TException>(Action body, string? because = null) where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Exception($"expected {typeof(TException).Name}, got {ex.GetType().Name}");
        }
        throw new Exception(because is null
            ? $"expected {typeof(TException).Name}, nothing was thrown"
            : $"expected {typeof(TException).Name}: {because}");
    }
}
