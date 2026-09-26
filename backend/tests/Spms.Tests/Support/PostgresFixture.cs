using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Spms.Host;
using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Scheduling.Infrastructure;
using Spms.Persistence;
using Spms.SharedKernel;
using Xunit;

namespace Spms.Tests.Support;

/// <summary>
/// A real PostgreSQL database per test class, built exactly the way a
/// deployment builds it: roles, then the EF migrations (the R1 baseline is
/// the reviewed SQL) as spms_owner.
///
/// Set SPMS_TEST_CONNECTION to a server the suite may create databases on.
/// Without it the Postgres tests are SKIPPED rather than silently passing.
///
/// The application side runs through the real container: persistence with its
/// tenancy interceptors, every module, and SET LOCAL ROLE spms_app — so these
/// tests exercise row-level security even though the test connection is a
/// superuser.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string SkipReason =
        "Set SPMS_TEST_CONNECTION to a PostgreSQL server to run the integration suite.";

    public static string? AdminConnectionString => Environment.GetEnvironmentVariable("SPMS_TEST_CONNECTION");
    public static bool Available => !string.IsNullOrWhiteSpace(AdminConnectionString);

    /// <summary>Roles are cluster-level; parallel fixtures must not race creating them.</summary>
    private static readonly SemaphoreSlim RoleGate = new(1, 1);

    private string _databaseName = string.Empty;
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string ConnectionString { get; private set; } = string.Empty;
    public ServiceProvider Services { get; private set; } = null!;
    public IReadOnlyList<string> AppliedMigrations { get; private set; } = [];

    public async Task InitializeAsync()
    {
        if (!Available) return;

        _databaseName = "spms_t_" + Guid.NewGuid().ToString("n")[..12];
        await using (var conn = new NpgsqlConnection(AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\";", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = _databaseName }.ConnectionString;
        DataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();

        await RoleGate.WaitAsync();
        try
        {
            await DatabaseBootstrapper.EnsureRolesAsync(ConnectionString);
        }
        finally
        {
            RoleGate.Release();
        }
        AppliedMigrations = await DatabaseBootstrapper.MigrateAsync(ConnectionString, SpmsModules.Contributors(), "spms_owner");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSpmsPersistence(ConnectionString, o => o.RuntimeRole = "spms_app");
        services.AddScoped<ClockHolder>();
        services.AddScoped<IClock>(sp => sp.GetRequiredService<ClockHolder>().Clock);
        services.AddSingleton<IKeyRing>(StaticKeyRing.Ephemeral());
        services.AddSingleton<IFieldProtector, AesGcmFieldProtector>();
        services.AddSpmsModules();
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public async Task DisposeAsync()
    {
        if (!Available) return;
        await Services.DisposeAsync();
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>An application scope: one "request", with the execution scope set.</summary>
    public IServiceScope NewScope(string tenant, IEnumerable<string> properties, string? current, IClock? clock = null,
                                  string principal = "principal-test")
    {
        var scope = Services.CreateScope();
        var props = properties.Select(TestIds.IdOf).ToArray();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().Set(
            TestIds.IdOf(tenant), props, current is null ? null : TestIds.IdOf(current),
            TestIds.IdOf(principal), ActorType.Staff, "corr-test");
        if (clock is not null) scope.ServiceProvider.GetRequiredService<ClockHolder>().Clock = clock;
        return scope;
    }

    /// <summary>Clears the transactional tables, leaving reference data in place.</summary>
    public async Task ResetBoardAsync()
    {
        // TRUNCATE is outside the application's powers (no DDL rights); the
        // superuser test connection does it, so each case starts from an empty
        // board without the append-only triggers standing in the way.
        await ExecAsync("""
            TRUNCATE scheduling.schedule_change_proposal, scheduling.visit_exception, scheduling.turnaround_task,
                     scheduling.waitlist_entry, scheduling.appointment, scheduling.visit,
                     core.audit_event, core.event_outbox, core.idempotency_record CASCADE;
            """);
    }

    public async Task ExecAsync(string sql, Action<NpgsqlCommand>? bind = null)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind?.Invoke(cmd);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, Action<NpgsqlCommand>? bind = null)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind?.Invoke(cmd);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public static readonly string[] Rooms =
    [
        "room-1", "room-2", "room-3", "room-4", "room-5", "room-6", "room-7", "room-8", "room-9",
        "room-free", "room-other", "room-busy", "room-turn", "room-turnover", "room-done", "room-night", "room-spare",
        "room-late", "room-shared", "room-a", "room-b", "room-race", "room-both", "room-tx",
    ];

    public static readonly (string Id, string Name, int Minutes)[] ServiceList =
    [
        ("svc-deep", "Deep tissue 90", 90), ("svc-aroma", "Aromatherapy 60", 60), ("svc-facial", "Facial 45", 45),
        ("svc-hotstone", "Hot stone 60", 60), ("svc-swedish", "Swedish 60", 60), ("svc-peel", "Peel 30", 30),
    ];

    public static readonly string[] Providers = ["prov-lena", "prov-marco", "prov-priya", "prov-known"];

    public static readonly (string Staff, string Service)[] Qualifications =
    [
        ("prov-lena", "svc-deep"), ("prov-lena", "svc-aroma"), ("prov-lena", "svc-hotstone"), ("prov-lena", "svc-swedish"),
        ("prov-marco", "svc-deep"), ("prov-marco", "svc-swedish"), ("prov-marco", "svc-hotstone"),
        ("prov-priya", "svc-facial"), ("prov-priya", "svc-peel"),
    ];

    public static readonly string[] Guests =
    [
        "guest-0000", "guest-0001", "guest-0002", "guest-9999", "guest-new",
        "guest-0", "guest-1", "guest-2", "guest-3", "guest-4", "guest-5", "guest-6", "guest-7",
    ];

    /// <summary>
    /// Reference data matching <see cref="SchedulingWorld"/>, keyed through
    /// <see cref="TestIds"/>, so the conformance suite can be shared. Runs as
    /// spms_owner inside a tenant scope: FORCE RLS binds the owner too.
    /// </summary>
    public async Task SeedReferenceAsync(string tenant, string property, int roomTurnover = 15, int providerTransition = 10)
    {
        string Q(string name) => $"'{TestIds.Of(name)}'";
        // The main test tenant's reference rows are named plainly ("room-1"), so
        // the shared cases resolve them. Any other tenant's are qualified by the
        // tenant: a uuid names one row in the whole database.
        string R(string name) => Q(tenant == PostgresWorld.Tenant ? name : tenant + "/" + name);
        // Rooms belong to one property, so any property but the main one qualifies them too.
        string Room(string name) => property == PostgresWorld.Property ? R(name) : Q(property + "/" + name);
        var t = Q(tenant);
        var p = Q(property);
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("BEGIN; SET LOCAL ROLE spms_owner;");
        sql.AppendLine($"SELECT core.begin_scope({t}, ARRAY[{p}]::uuid[], NULL, 'test-seed');");
        sql.AppendLine($"INSERT INTO core.tenant (tenant_id, code, name, data_region) VALUES ({t}, 't-{TestIds.IdOf(tenant).ToString()[..8]}', 'Test', 'test') ON CONFLICT DO NOTHING;");
        sql.AppendLine($"""
            INSERT INTO core.property (property_id, tenant_id, code, name, timezone, currency_code, opening_hours, room_turnover_minutes, provider_transition_minutes)
            VALUES ({p}, {t}, 'p-{TestIds.IdOf(property).ToString()[..8]}', 'Test', 'UTC', 'USD', '[]', {roomTurnover}, {providerTransition})
            ON CONFLICT (property_id) DO UPDATE SET room_turnover_minutes = EXCLUDED.room_turnover_minutes,
                provider_transition_minutes = EXCLUDED.provider_transition_minutes, version = core.property.version + 1;
            """);
        foreach (var (id, name, minutes) in ServiceList)
        {
            sql.AppendLine($"INSERT INTO catalog.service (service_id, tenant_id, code, name, duration_minutes, currency_code, status) VALUES ({R(id)}, {t}, '{id}', '{name}', {minutes}, 'USD', 'Active') ON CONFLICT DO NOTHING;");
            sql.AppendLine($"INSERT INTO catalog.property_service (tenant_id, property_id, service_id) VALUES ({t}, {p}, {R(id)}) ON CONFLICT DO NOTHING;");
        }
        foreach (var r in Rooms)
            sql.AppendLine($"INSERT INTO resources.resource (resource_id, tenant_id, property_id, resource_type, code, name) VALUES ({Room(r)}, {t}, {p}, 'TreatmentRoom', '{r}', '{r}') ON CONFLICT DO NOTHING;");
        foreach (var s in Providers)
            sql.AppendLine($"INSERT INTO workforce.staff (staff_id, tenant_id, home_property_id, preferred_name) VALUES ({R(s)}, {t}, {p}, '{s}') ON CONFLICT DO NOTHING;");
        foreach (var (s, svc) in Qualifications)
            sql.AppendLine($"""
                INSERT INTO workforce.staff_qualification (tenant_id, staff_id, service_id, effective_range)
                SELECT {t}, {R(s)}, {R(svc)}, tstzrange('-infinity', 'infinity')
                 WHERE NOT EXISTS (SELECT 1 FROM workforce.staff_qualification WHERE staff_id = {R(s)} AND service_id = {R(svc)});
                """);
        foreach (var g in Guests)
            sql.AppendLine($"INSERT INTO guest.guest (guest_id, tenant_id, display_alias) VALUES ({R(g)}, {t}, '{g}') ON CONFLICT DO NOTHING;");
        sql.AppendLine("COMMIT;");
        await ExecAsync(sql.ToString());
    }
}

/// <summary>The scope's clock, replaceable per world.</summary>
public sealed class ClockHolder
{
    public IClock Clock { get; set; } = new SystemClock();
}

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
