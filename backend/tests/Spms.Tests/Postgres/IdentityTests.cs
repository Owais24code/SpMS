using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spms.Host.Authorization;
using Spms.Host.Identity;
using Spms.Host.Workers;
using Spms.Modules.Guest.Identity;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

public class IdentityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Issuer = "https://login.example/tenant/v2.0";

    private async Task SeedStaffAsync(string handle, string role, string? atProperty, string principalStatus = "Active")
    {
        var t = TestIds.IdOf(PostgresWorld.Tenant);
        var p = TestIds.IdOf(PostgresWorld.Property);
        var principal = TestIds.IdOf("principal/" + handle);
        var staff = TestIds.IdOf("staff/" + handle);
        var prop = atProperty is null ? "NULL" : $"'{TestIds.IdOf(atProperty)}'";
        await fixture.ExecAsync($"""
            BEGIN; SET LOCAL ROLE spms_owner;
            SELECT core.begin_scope('{t}', ARRAY['{p}', '{TestIds.IdOf("prop-second")}']::uuid[], NULL, 'test');
            INSERT INTO core.principal (principal_id, tenant_id, principal_type, display_name, status)
            VALUES ('{principal}', '{t}', 'Staff', '{handle}', '{principalStatus}') ON CONFLICT DO NOTHING;
            INSERT INTO core.principal_login (tenant_id, principal_id, login_type, idp_issuer, idp_subject)
            VALUES ('{t}', '{principal}', 'EntraUser', '{Issuer}', 'oid-{handle}') ON CONFLICT DO NOTHING;
            INSERT INTO workforce.staff (staff_id, tenant_id, principal_id, home_property_id, preferred_name)
            VALUES ('{staff}', '{t}', '{principal}', '{p}', '{handle}') ON CONFLICT DO NOTHING;
            INSERT INTO workforce.staff_role_assignment (tenant_id, property_id, staff_id, role_code, status, approved_by, approved_at)
            VALUES ('{t}', {prop}, '{staff}', '{role}', 'Active', '{TestIds.IdOf("approver")}', now());
            COMMIT;
            """);
    }

    private PrincipalResolver Resolver() => new(
        fixture.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(), fixture.Services.GetRequiredService<PersistenceOptions>(),
        new MemoryCache(new MemoryCacheOptions()));

    [RequiresPostgres]
    public async Task A_provisioned_subject_resolves_to_its_tenant_roles_and_properties()
    {
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, "prop-second");
        await SeedStaffAsync("desk1", "front_desk", PostgresWorld.Property);

        var id = await Resolver().ResolveAsync(Issuer, "oid-desk1");

        Assert.NotNull(id);
        Assert.Equal(TestIds.IdOf(PostgresWorld.Tenant), id.TenantId);
        Assert.Equal(["front_desk"], id.RoleCodes);
        Assert.Equal([TestIds.IdOf(PostgresWorld.Property)], id.Properties.Select(p => p.PropertyId));
        Assert.Contains(SpaScopes.Write, RoleScopes.For(id.RoleCodes));
    }

    [RequiresPostgres]
    public async Task A_tenant_wide_role_reaches_every_property()
    {
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, "prop-second");
        await SeedStaffAsync("fin1", "finance", null);

        var id = await Resolver().ResolveAsync(Issuer, "oid-fin1");
        Assert.NotNull(id);
        Assert.True(id.Properties.Count >= 2);
    }

    [RequiresPostgres]
    public async Task Unknown_and_disabled_subjects_do_not_resolve()
    {
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await SeedStaffAsync("gone1", "front_desk", PostgresWorld.Property, principalStatus: "Disabled");

        Assert.Null(await Resolver().ResolveAsync(Issuer, "oid-nobody"));
        Assert.Null(await Resolver().ResolveAsync(Issuer, "oid-gone1"));
        // The issuer is part of the identity: the same subject at another IdP is someone else.
        Assert.Null(await Resolver().ResolveAsync("https://elsewhere/v2.0", "oid-desk1"));
    }

    [Fact]
    public void A_token_narrows_role_scopes_and_never_widens_them()
    {
        var roles = new[] { "front_desk" };
        Assert.Contains(SpaScopes.Write, RoleScopes.Effective(roles, []));
        Assert.Equal([SpaScopes.Read], RoleScopes.Effective(roles, [SpaScopes.Read]));
        Assert.DoesNotContain(SpaScopes.Admin, RoleScopes.Effective(roles, [SpaScopes.Admin, SpaScopes.Read]));
        Assert.Empty(RoleScopes.For(["not_a_role"]));
    }

    [Fact]
    public void Contact_values_normalise_once_for_storage_and_search()
    {
        Assert.Equal("ava@example.com", ContactValues.Normalise("Email", "  Ava@Example.COM "));
        Assert.Null(ContactValues.Normalise("Email", "not an email"));
        Assert.Equal("+15550102233", ContactValues.Normalise("Mobile", "+1 (555) 010-2233"));
        Assert.Equal("a***@example.com", ContactValues.Mask("Email", "ava@example.com"));
        Assert.Equal("***2233", ContactValues.Mask("Mobile", "+15550102233"));

        var protector = new AesGcmFieldProtector(StaticKeyRing.Ephemeral());
        var (cipher, hash, _) = ContactValues.Protect(protector, "Email", "ava@example.com");
        Assert.Equal(hash, protector.LookupHash("ava@example.com", ContactValues.LookupPurpose("Email")));
        Assert.Equal("ava@example.com", protector.UnprotectString(cipher.Cipher, cipher.KeyVersion, ContactValues.CipherPurpose));
        // Bound to its purpose: the same cipher cannot be read as something else.
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            protector.UnprotectString(cipher.Cipher, cipher.KeyVersion, "intake.submission.response"));
    }
}

public class OutboxDispatcherTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class Recorder(string type, bool fail) : IOutboxHandler
    {
        public List<Guid> Seen { get; } = [];
        public string Name => "recorder";
        public bool Handles(string eventType) => eventType == type;
        public Task HandleAsync(OutboxMessage message, CancellationToken ct)
        {
            Seen.Add(message.EventId);
            return fail ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }

    private async Task<Guid> EnqueueAsync(string type)
    {
        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var db = scope.ServiceProvider.GetRequiredService<SpmsDbContext>();
        var id = Uuid7.New();
        await using var tx = await db.Database.BeginTransactionAsync();
        scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(new OutboxEvent(type, "test", id, 1, new { n = 1 }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return id;
    }

    private OutboxDispatcher Dispatcher(IOutboxHandler h) => new(
        fixture.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(), [h], new OutboxOptions(), NullLogger<OutboxDispatcher>.Instance);

    [RequiresPostgres]
    public async Task A_handled_event_is_published_once()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        var aggregate = await EnqueueAsync("test.ok.v1");
        var h = new Recorder("test.ok.v1", fail: false);

        Assert.Equal(1, await Dispatcher(h).DispatchOnceAsync(default));
        Assert.Equal(0, await Dispatcher(h).DispatchOnceAsync(default));
        Assert.Single(h.Seen);
        Assert.True(await fixture.ScalarAsync<bool>($"SELECT published_at IS NOT NULL FROM core.event_outbox WHERE aggregate_id = '{aggregate}'"));
    }

    [RequiresPostgres]
    public async Task A_failing_handler_backs_the_event_off_and_records_why()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        var aggregate = await EnqueueAsync("test.fail.v1");
        var h = new Recorder("test.fail.v1", fail: true);

        await Dispatcher(h).DispatchOnceAsync(default);
        Assert.Equal(0, await Dispatcher(h).DispatchOnceAsync(default));   // not yet due again
        Assert.Equal(1, await fixture.ScalarAsync<int>($"SELECT attempt_count FROM core.event_outbox WHERE aggregate_id = '{aggregate}'"));
        Assert.Contains("boom", await fixture.ScalarAsync<string>($"SELECT last_error FROM core.event_outbox WHERE aggregate_id = '{aggregate}'"));
        Assert.False(await fixture.ScalarAsync<bool>($"SELECT published_at IS NOT NULL FROM core.event_outbox WHERE aggregate_id = '{aggregate}'"));
    }
}

/// <summary>Against a live OpenFGA (SPMS_TEST_OPENFGA=http://host:port), skipped otherwise.</summary>
public class OpenFgaTests
{
    public static string? Url => Environment.GetEnvironmentVariable("SPMS_TEST_OPENFGA");

    public sealed class RequiresOpenFgaAttribute : FactAttribute
    {
        public RequiresOpenFgaAttribute() { if (string.IsNullOrWhiteSpace(Url)) Skip = "Set SPMS_TEST_OPENFGA to run the OpenFGA cases."; }
    }

    private static async Task<(OpenFgaAccessDecider Decider, FgaTupleWriter Writer)> StoreAsync()
    {
        var holder = new FgaClientHolder(new OpenFgaOptions { ApiUrl = Url, StoreName = "spms-test-" + Guid.NewGuid().ToString("n")[..8] });
        await holder.BootstrapAsync(NullLogger.Instance);
        return (new OpenFgaAccessDecider(holder, NullLogger<OpenFgaAccessDecider>.Instance), new FgaTupleWriter(holder));
    }

    [RequiresOpenFga]
    public async Task Stored_roles_and_contextual_row_tuples_decide_appointment_access()
    {
        var (decider, writer) = await StoreAsync();
        var tenant = Guid.NewGuid();
        var property = Guid.NewGuid();
        var desk = Guid.NewGuid();
        var provider = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var appointment = Guid.NewGuid();
        var guest = Guid.NewGuid();

        await writer.WriteAsync([
            new FgaTuple(Fga.Tenant(tenant), "tenant", Fga.Property(property)),
            new FgaTuple(Fga.User(desk), "front_desk", Fga.Property(property)),
        ], default);
        // Idempotent: writing the same tuples again is not an error.
        await writer.WriteAsync([new FgaTuple(Fga.User(desk), "front_desk", Fga.Property(property))], default);

        IReadOnlyList<FgaTuple> row =
        [
            new(Fga.Property(property), "property", Fga.Appointment(appointment)),
            new(Fga.Guest(guest), "guest", Fga.Appointment(appointment)),
            new(Fga.User(provider), "assigned_provider", Fga.Appointment(appointment)),
        ];

        async Task<bool> Can(Guid who, string relation) =>
            (await decider.CheckAsync(new AccessCheck(Fga.User(who), relation, Fga.Appointment(appointment), row,
                new Dictionary<string, object> { ["current_time"] = DateTimeOffset.UtcNow.ToString("O"), ["property_id"] = property.ToString() }))).Allowed;

        Assert.True(await Can(desk, "can_read"));
        Assert.True(await Can(provider, "can_read_intake_summary"));
        Assert.False(await Can(desk, "can_read_intake_summary"));
        Assert.False(await Can(stranger, "can_read"));
        Assert.False(await Can(desk, "can_reassign"));   // front desk books; scheduler/manager reassign
    }

    [Fact]
    public async Task An_unreachable_server_is_a_refusal_not_an_allow()
    {
        var holder = new FgaClientHolder(new OpenFgaOptions { ApiUrl = "http://127.0.0.1:9", StoreId = "01HZZZZZZZZZZZZZZZZZZZZZZZ" });
        var decider = new OpenFgaAccessDecider(holder, NullLogger<OpenFgaAccessDecider>.Instance);
        await Assert.ThrowsAsync<AccessUnavailableException>(() =>
            decider.CheckAsync(new AccessCheck("user:x", "can_read", "appointment:y")));
    }
}
