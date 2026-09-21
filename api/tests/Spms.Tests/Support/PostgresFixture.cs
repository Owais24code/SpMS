using Microsoft.EntityFrameworkCore;
using Npgsql;
using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;
using Spms.Infrastructure.Postgres;
using Xunit;

namespace Spms.Tests.Support;

/// <summary>
/// A real PostgreSQL database per test class.
///
/// Set SPMS_TEST_CONNECTION to a server the suite may create databases on.
/// Without it the Postgres tests are SKIPPED rather than silently passing —
/// a green run that quietly proved nothing is worse than a red one.
///
/// Each fixture creates its own database, applies the migrations and drops it
/// afterwards, so classes cannot see each other's rows and the exclusion
/// constraint is being exercised against a schema built the same way
/// production's is.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string SkipReason =
        "Set SPMS_TEST_CONNECTION to a PostgreSQL server to run the integration suite.";

    public static string? AdminConnectionString =>
        Environment.GetEnvironmentVariable("SPMS_TEST_CONNECTION");

    public static bool Available => !string.IsNullOrWhiteSpace(AdminConnectionString);

    private string _databaseName = string.Empty;
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!Available) return;

        _databaseName = "spms_t_" + Guid.NewGuid().ToString("n")[..12];

        var admin = new NpgsqlConnectionStringBuilder(AdminConnectionString);
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\";", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = _databaseName };
        ConnectionString = target.ConnectionString;

        var b = new NpgsqlDataSourceBuilder(ConnectionString);
        DataSource = b.Build();

        var applied = await new MigrationRunner(DataSource, new SilentLog()).RunAsync();
        Assert.Equal(3, applied.Count);

        // If the schema the code needs is not there, every later assertion
        // would fail for the wrong reason.
        var problems = await new SchemaGuard(DataSource).VerifyAsync();
        Assert.Empty(problems);
    }

    public async Task DisposeAsync()
    {
        if (!Available) return;

        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public SpmsDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<SpmsDbContext>()
            .UseNpgsql(DataSource)
            .Options;
        return new SpmsDbContext(options);
    }

    /// <summary>
    /// Clears the transactional tables, leaving reference data in place.
    ///
    /// Called at the start of every Postgres case. A class fixture shares one
    /// database across its cases, and without this the cases contaminate each
    /// other: one test's booking occupied a room another test expected to be
    /// free, so a conflict assertion passed or failed depending on execution
    /// order. Exactly the defect the HTTP sweep hit for the same reason.
    /// </summary>
    public async Task ResetBoardAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        // audit_entry has DO INSTEAD NOTHING rules on DELETE, so TRUNCATE is
        // the only way to clear it — which is correct here and impossible for
        // the application, since it has no DDL rights.
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE appointment, audit_entry, preflight_token, idempotency_key;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Reference data matching <see cref="SchedulingWorld"/>, so the conformance suite can be shared.</summary>
    public async Task SeedReferenceAsync(
        string tenantId, string propertyId, int roomTurnover = 15, int providerTransition = 10)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO tenant (tenant_id, display_name) VALUES (@t, 'Test') ON CONFLICT DO NOTHING;

            INSERT INTO property (tenant_id, property_id, display_name, time_zone_id, open_minute, close_minute)
            VALUES (@t, @p, 'Test', 'UTC', 540, 1020) ON CONFLICT DO NOTHING;

            INSERT INTO service (tenant_id, service_id, display_name, duration_minutes) VALUES
              (@t, 'svc-deep', 'Deep tissue 90', 90), (@t, 'svc-aroma', 'Aromatherapy 60', 60),
              (@t, 'svc-facial', 'Facial 45', 45), (@t, 'svc-hotstone', 'Hot stone 60', 60),
              (@t, 'svc-swedish', 'Swedish 60', 60), (@t, 'svc-peel', 'Peel 30', 30)
            ON CONFLICT DO NOTHING;

            INSERT INTO room (tenant_id, property_id, room_id, display_name)
            SELECT @t, @p, r, r FROM unnest(ARRAY[
              'room-1','room-2','room-3','room-4','room-5','room-6','room-7','room-8','room-9',
              'room-free','room-other','room-busy','room-turn','room-done','room-night','room-spare',
              'room-late','room-shared','room-a','room-b','room-race','room-both','room-tx'
            ]) AS r ON CONFLICT DO NOTHING;

            INSERT INTO staff (tenant_id, property_id, provider_id, display_name, assignable) VALUES
              (@t, @p, 'prov-lena', 'Lena', true), (@t, @p, 'prov-marco', 'Marco', true),
              (@t, @p, 'prov-priya', 'Priya', true), (@t, @p, 'prov-known', 'Known', true)
            ON CONFLICT DO NOTHING;

            -- granted_utc is backdated deliberately. It defaults to now(), and
            -- the cases schedule against a fixed 2026-06-15 clock, so a
            -- default-dated grant is in the FUTURE relative to the appointment
            -- and every provider reads as unqualified — CON-003 on every
            -- preflight, which looks like a rules bug and is a fixture bug.
            INSERT INTO staff_qualification (tenant_id, property_id, provider_id, service_id, granted_utc) VALUES
              (@t, @p, 'prov-lena', 'svc-deep', 'epoch'), (@t, @p, 'prov-lena', 'svc-aroma', 'epoch'),
              (@t, @p, 'prov-lena', 'svc-hotstone', 'epoch'), (@t, @p, 'prov-lena', 'svc-swedish', 'epoch'),
              (@t, @p, 'prov-marco', 'svc-deep', 'epoch'), (@t, @p, 'prov-marco', 'svc-swedish', 'epoch'),
              (@t, @p, 'prov-marco', 'svc-hotstone', 'epoch'),
              (@t, @p, 'prov-priya', 'svc-facial', 'epoch'), (@t, @p, 'prov-priya', 'svc-peel', 'epoch')
            ON CONFLICT DO NOTHING;

            INSERT INTO guest (tenant_id, guest_id, display_alias)
            SELECT @t, g, g FROM unnest(ARRAY[
              'guest-0000','guest-0001','guest-0002','guest-9999','guest-new',
              'guest-0','guest-1','guest-2','guest-3','guest-4','guest-5','guest-6','guest-7'
            ]) AS g ON CONFLICT DO NOTHING;

            DELETE FROM buffer_policy WHERE tenant_id=@t AND property_id=@p AND service_id IS NULL;
            INSERT INTO buffer_policy
              (tenant_id, property_id, service_id, room_turnover_minutes, provider_transition_minutes)
            VALUES (@t, @p, NULL, @rt, @pt);
            """, conn);

        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("p", propertyId);
        cmd.Parameters.AddWithValue("rt", roomTurnover);
        cmd.Parameters.AddWithValue("pt", providerTransition);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class SilentLog : ILoggerLike
    {
        public void Info(string message) { }
    }
}

/// <summary>
/// Skips a fact when no test database is configured, instead of failing.
/// </summary>
public sealed class RequiresPostgresAttribute : FactAttribute
{
    public RequiresPostgresAttribute()
    {
        if (!PostgresFixture.Available) Skip = PostgresFixture.SkipReason;
    }
}

public sealed class RequiresPostgresTheoryAttribute : TheoryAttribute
{
    public RequiresPostgresTheoryAttribute()
    {
        if (!PostgresFixture.Available) Skip = PostgresFixture.SkipReason;
    }
}

/// <summary>
/// The Postgres-backed equivalent of <see cref="SchedulingWorld"/>, wired from
/// the same ports so a shared assertion can run against either.
/// </summary>
public sealed class PostgresWorld : IAsyncDisposable
{
    public const string Tenant = "tenant-test";
    public const string Property = "prop-test";

    private readonly SpmsDbContext _db;

    public IAppointmentRepository Repo { get; }
    public IPreflightStore Preflights { get; }
    public IAuditSink Audit { get; }
    public IIdempotencyStore Idempotency { get; }
    public IUnitOfWork UnitOfWork { get; }
    public SchedulingService Scheduling { get; }
    public TestClock Clock { get; }
    public DateTimeOffset Day { get; }
    public SpmsDbContext Db => _db;

    public PostgresWorld(PostgresFixture fixture, DateTimeOffset? day = null)
    {
        Day = day ?? new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        Clock = new TestClock(Day.AddHours(8));

        _db = fixture.NewContext();
        Repo = new PostgresAppointmentRepository(_db);
        Preflights = new PostgresPreflightStore(_db);
        Audit = new PostgresAuditSink(_db);
        Idempotency = new PostgresIdempotencyStore(fixture.DataSource);
        UnitOfWork = new PostgresUnitOfWork(_db);

        Scheduling = new SchedulingService(
            Repo, Preflights, Audit, UnitOfWork,
            new PostgresServiceCatalog(_db),
            new PostgresQualificationRegister(_db),
            new PostgresPropertyDirectory(_db),
            Clock);
    }

    public Task<Appointment?> Get(string id) => Repo.GetAsync(Tenant, Property, id);

    public Task<SchedulingService.CommitResult> Commit(string routeId, string token, string? reason = null) =>
        Scheduling.CommitMoveAsync(Tenant, Property, routeId, token, reason, "actor-test", "corr-test");

    public Task<PreflightResult> Preflight(Appointment target, DateTimeOffset startUtc, string? provider, string? room) =>
        Scheduling.PreflightAsync(Tenant, Property, target,
            new MoveProposal(target.AppointmentId, startUtc, provider, room, target.RowVersion));

    public async Task AddAsync(Appointment a)
    {
        if (!await Repo.TryAddAsync(a)) throw new InvalidOperationException($"Fixture collided on {a.AppointmentId}.");
    }

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

        foreach (var step in SchedulingWorld.PathTo(status)) a.ApplyTransition(step, startUtc);

        return Spms.Domain.Scheduling.Appointment.Rehydrate(
            a.AppointmentId, a.TenantId, a.PropertyId, a.PropertyTimeZone, a.GuestId, a.GuestAlias,
            a.ServiceId, a.ServiceName, a.DurationMinutes, a.ProviderId, a.RoomId,
            a.StartUtc, a.Status, rowVersion: 1, a.ConfirmationNumber, a.CorrelationId,
            a.CreatedUtc, a.UpdatedUtc);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
