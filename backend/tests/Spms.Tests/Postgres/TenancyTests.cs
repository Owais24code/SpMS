using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Spms.Modules.Core.Data;
using Spms.Modules.Guest.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>
/// The three tenancy layers, from the application's side: EF query filters,
/// the save interceptor, and row-level security underneath both — plus the
/// transaction discipline that makes RLS meaningful at all.
/// </summary>
public class TenancyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Other = "tenant-other";
    private const string Elsewhere = "prop-elsewhere";

    private async Task SeedAsync()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, Elsewhere);
        await fixture.SeedReferenceAsync(Other, "prop-other");
    }

    private async Task InsertRawAsync(string tenant, string property, string id, string guest, string room)
    {
        var t = TestIds.IdOf(tenant);
        var p = TestIds.IdOf(property);
        var svc = TestIds.IdOf(tenant == PostgresWorld.Tenant ? "svc-peel" : tenant + "/svc-peel");
        await fixture.ExecAsync($"""
            BEGIN; SELECT core.begin_scope('{t}', ARRAY['{p}']::uuid[], NULL, 'test');
            INSERT INTO scheduling.appointment (appointment_id, tenant_id, property_id, guest_id, service_id, room_id, duration_minutes,
                start_at, end_at, entered_timezone, source, currency_code, status)
            VALUES ('{TestIds.IdOf(id)}', '{t}', '{p}', '{TestIds.IdOf(guest)}', '{svc}', '{TestIds.IdOf(room)}', 30,
                    '2026-06-15T12:00:00Z', 'epoch', 'UTC', 'Desk', 'USD', 'Confirmed');
            COMMIT;
            """);
    }

    private async Task<T> InTx<T>(IServiceScope scope, Func<SpmsDbContext, Task<T>> work)
    {
        var db = scope.ServiceProvider.GetRequiredService<SpmsDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var r = await work(db);
        await tx.CommitAsync();
        return r;
    }

    [RequiresPostgres]
    public async Task Rls_hides_another_tenant_even_when_the_query_filter_is_bypassed()
    {
        await SeedAsync();
        await InsertRawAsync(PostgresWorld.Tenant, PostgresWorld.Property, "mine", "guest-0000", "room-1");
        await InsertRawAsync(Other, "prop-other", "theirs", Other + "/guest-0000", "prop-other/room-1");

        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var ids = await InTx(scope, db => db.Set<AppointmentRow>().IgnoreQueryFilters().Select(a => a.AppointmentId).ToListAsync());

        Assert.Contains(TestIds.IdOf("mine"), ids);
        Assert.DoesNotContain(TestIds.IdOf("theirs"), ids);
    }

    [RequiresPostgres]
    public async Task A_property_scope_hides_the_same_tenants_other_property()
    {
        await SeedAsync();
        await InsertRawAsync(PostgresWorld.Tenant, PostgresWorld.Property, "here", "guest-0000", "room-1");
        await InsertRawAsync(PostgresWorld.Tenant, Elsewhere, "there", "guest-0001", Elsewhere + "/room-2");

        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var filtered = await InTx(scope, db => db.Set<AppointmentRow>().Select(a => a.AppointmentId).ToListAsync());
        var raw = await InTx(scope, db => db.Set<AppointmentRow>().IgnoreQueryFilters().Select(a => a.AppointmentId).ToListAsync());

        Assert.Equal([TestIds.IdOf("here")], filtered);
        Assert.Equal([TestIds.IdOf("here")], raw);

        // Tenant-wide rows (a guest) are visible at either property.
        var guests = await InTx(scope, db => db.Set<GuestRow>().CountAsync());
        Assert.True(guests >= PostgresFixture.Guests.Length);
    }

    [RequiresPostgres]
    public async Task The_save_interceptor_refuses_a_row_for_another_tenant()
    {
        await SeedAsync();
        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var db = scope.ServiceProvider.GetRequiredService<SpmsDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Add(new GuestRow { TenantId = TestIds.IdOf(Other), DisplayAlias = "sneaky" });
        await Assert.ThrowsAsync<CrossScopeWriteException>(() => db.SaveChangesAsync());
    }

    [RequiresPostgres]
    public async Task Rows_are_stamped_from_the_scope()
    {
        await SeedAsync();
        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var guest = new GuestRow { DisplayAlias = "Stamped" };
        await InTx(scope, async db => { db.Add(guest); await db.SaveChangesAsync(); return 0; });

        Assert.Equal(TestIds.IdOf(PostgresWorld.Tenant), guest.TenantId);
        Assert.True(Uuid7.IsVersion7(guest.GuestId));
        var createdBy = await fixture.ScalarAsync<Guid>($"SELECT created_by FROM guest.guest WHERE guest_id = '{guest.GuestId}'");
        Assert.Equal(TestIds.IdOf("principal-test"), createdBy);
    }

    [RequiresPostgres]
    public async Task A_statement_outside_a_transaction_is_refused_rather_than_answered_empty()
    {
        await SeedAsync();
        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var db = scope.ServiceProvider.GetRequiredService<SpmsDbContext>();
        await Assert.ThrowsAsync<UnscopedCommandException>(() => db.Set<GuestRow>().CountAsync());
    }

    [RequiresPostgres]
    public async Task An_unset_scope_sees_nothing()
    {
        await SeedAsync();
        using var scope = fixture.Services.CreateScope();   // ExecutionScope never set
        var n = await InTx(scope, db => db.Set<GuestRow>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, n);
    }

    [RequiresPostgres]
    public async Task A_tracked_update_advances_the_version_by_exactly_one()
    {
        await SeedAsync();
        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var id = TestIds.IdOf("guest-0001");
        var version = await InTx(scope, async db =>
        {
            var g = await db.Set<GuestRow>().SingleAsync(x => x.GuestId == id);
            var before = g.Version;
            g.PreferredName = "Renamed " + Guid.NewGuid().ToString("n")[..4];
            await db.SaveChangesAsync();
            return (before, g.Version);
        });
        Assert.Equal(version.before + 1, version.Item2);
    }

    [RequiresPostgres]
    public async Task The_database_refuses_an_update_that_does_not_advance_the_version()
    {
        await SeedAsync();
        var t = TestIds.IdOf(PostgresWorld.Tenant);
        var e = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecAsync($"""
            BEGIN; SET LOCAL ROLE spms_app; SELECT core.begin_scope('{t}', ARRAY[]::uuid[], NULL, 'test');
            UPDATE guest.guest SET preferred_name = 'x' WHERE guest_id = '{TestIds.IdOf("guest-0002")}';
            COMMIT;
            """));
        Assert.Equal(PostgresErrors.SerializationFailure, e.SqlState);
    }

    [RequiresPostgres]
    public async Task A_nested_unit_of_work_rolls_back_to_its_savepoint_only()
    {
        await SeedAsync();
        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var db = scope.ServiceProvider.GetRequiredService<SpmsDbContext>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var kept = new GuestRow { DisplayAlias = "kept-" + Guid.NewGuid().ToString("n")[..6] };
        var dropped = new GuestRow { DisplayAlias = "dropped-" + Guid.NewGuid().ToString("n")[..6] };

        await using (var outer = await uow.BeginAsync())
        {
            db.Add(kept);
            await using (await uow.BeginAsync())
            {
                db.Add(dropped);
                await db.SaveChangesAsync();
                // disposed without commit: rolls back to the savepoint
            }
            await outer.CommitAsync();
        }

        Assert.Equal(1L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM guest.guest WHERE display_alias = '{kept.DisplayAlias}'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM guest.guest WHERE display_alias = '{dropped.DisplayAlias}'"));
    }

    [RequiresPostgres]
    public async Task Runtime_role_cannot_read_intake_without_switching_role()
    {
        await SeedAsync();
        var t = TestIds.IdOf(PostgresWorld.Tenant);
        var e = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecAsync($"""
            BEGIN; SET LOCAL ROLE spms_app; SELECT core.begin_scope('{t}', ARRAY[]::uuid[], NULL, 'test');
            SELECT count(*) FROM intake.treatment_note;
            COMMIT;
            """));
        Assert.Equal(PostgresErrors.InsufficientPrivilege, e.SqlState);
    }

    [RequiresPostgres]
    public async Task Audit_rows_carry_the_acting_principal_and_correlation()
    {
        await SeedAsync();
        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var sink = scope.ServiceProvider.GetRequiredService<IAuditSink>();
        var entity = Uuid7.New();
        await InTx(scope, async db =>
        {
            await sink.RecordAsync(new AuditEntry("test.action", "guest", entity.ToString(), ReasonText: "because"));
            await db.SaveChangesAsync();
            return 0;
        });

        var actor = await fixture.ScalarAsync<Guid>($"SELECT actor_principal_id FROM core.audit_event WHERE entity_id = '{entity}'");
        var corr = await fixture.ScalarAsync<string>($"SELECT correlation_id FROM core.audit_event WHERE entity_id = '{entity}'");
        Assert.Equal(TestIds.IdOf("principal-test"), actor);
        Assert.Equal("corr-test", corr);
    }

    [RequiresPostgres]
    public async Task Outbox_events_commit_with_the_change_or_not_at_all()
    {
        await SeedAsync();
        using var scope = fixture.NewScope(PostgresWorld.Tenant, [PostgresWorld.Property], PostgresWorld.Property);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var db = scope.ServiceProvider.GetRequiredService<SpmsDbContext>();
        var id = Uuid7.New();

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            outbox.Enqueue(new OutboxEvent("test.rolled_back.v1", "test", id, 1, new { x = 1 }));
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal(0L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM core.event_outbox WHERE aggregate_id = '{id}'"));

        await InTx(scope, async d =>
        {
            outbox.Enqueue(new OutboxEvent("test.committed.v1", "test", id, 1, new { x = 1 }));
            await d.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(1L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM core.event_outbox WHERE aggregate_id = '{id}'"));
    }
}
