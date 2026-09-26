using Npgsql;
using Spms.Host;
using Spms.Persistence;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>The schema as the migrations build it, and the EF model's agreement with it.</summary>
public class SchemaTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [RequiresPostgres]
    public void The_baseline_is_applied_first_and_every_migration_after_it()
    {
        Assert.EndsWith("_R1Baseline", fixture.AppliedMigrations[0], StringComparison.Ordinal);
        Assert.Contains(fixture.AppliedMigrations, m => m.EndsWith("_IdentityLookups", StringComparison.Ordinal));
    }

    [RequiresPostgres]
    public async Task Migrating_again_is_a_no_op()
    {
        var applied = await DatabaseBootstrapper.MigrateAsync(fixture.ConnectionString, SpmsModules.Contributors(), "spms_owner");
        Assert.Empty(applied);
    }

    [RequiresPostgres]
    public async Task The_migrated_database_has_all_61_tables_and_forced_rls_on_every_tenant_table()
    {
        Assert.Equal(61L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind IN ('r','p') AND NOT c.relispartition
               AND n.nspname IN ('core','catalog','resources','workforce','guest','scheduling','intake','inventory','commerce','messaging','reporting')
            """));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id'
             WHERE c.relkind IN ('r','p') AND NOT c.relispartition
               AND n.nspname NOT IN ('pg_catalog','information_schema','spms_migrations')
               AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """));
    }

    [RequiresPostgres]
    public async Task The_ef_model_matches_the_live_schema_column_for_column()
    {
        await using var db = DatabaseBootstrapper.CreateMigrationContext(fixture.ConnectionString, SpmsModules.Contributors(), null);
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        var problems = await SchemaDrift.CompareAsync(db, conn);
        Assert.Empty(problems);
    }

    [RequiresPostgres]
    public async Task Drift_is_detected_when_a_column_appears_that_nothing_maps()
    {
        await fixture.ExecAsync("ALTER TABLE reporting.report_run ADD COLUMN hotfix text;");
        try
        {
            await using var db = DatabaseBootstrapper.CreateMigrationContext(fixture.ConnectionString, SpmsModules.Contributors(), null);
            await using var conn = await fixture.DataSource.OpenConnectionAsync();
            var problems = await SchemaDrift.CompareAsync(db, conn);
            Assert.Contains(problems, p => p.Contains("reporting.report_run.hotfix", StringComparison.Ordinal));
        }
        finally
        {
            await fixture.ExecAsync("ALTER TABLE reporting.report_run DROP COLUMN hotfix;");
        }
    }

    [RequiresPostgres]
    public async Task End_at_is_derived_by_the_database_not_by_the_caller()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        var t = TestIds.IdOf(PostgresWorld.Tenant);
        var p = TestIds.IdOf(PostgresWorld.Property);

        await fixture.ExecAsync($"""
            BEGIN; SELECT core.begin_scope('{t}', ARRAY['{p}']::uuid[], NULL, 'test');
            INSERT INTO scheduling.appointment (appointment_id, tenant_id, property_id, guest_id, service_id, duration_minutes,
                start_at, end_at, entered_timezone, source, currency_code, status)
            VALUES ('{TestIds.IdOf("derive")}', '{t}', '{p}', '{TestIds.IdOf("guest-0000")}', '{TestIds.IdOf("svc-peel")}', 30,
                    '2026-06-15T10:00:00Z', '2030-01-01T00:00:00Z', 'UTC', 'Desk', 'USD', 'Confirmed');
            COMMIT;
            """);

        var end = await fixture.ScalarAsync<DateTime>($"SELECT end_at FROM scheduling.appointment WHERE appointment_id = '{TestIds.IdOf("derive")}'");
        Assert.Equal(new DateTime(2026, 6, 15, 10, 30, 0, DateTimeKind.Utc), end.ToUniversalTime());
    }

    [RequiresPostgres]
    public async Task The_audit_trail_refuses_update_and_delete_even_from_the_superuser()
    {
        var t = TestIds.IdOf(PostgresWorld.Tenant);
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await fixture.ExecAsync($"""
            INSERT INTO core.audit_event (tenant_id, actor_type, action, entity_type, entity_id)
            VALUES ('{t}', 'System', 'test', 'appointment', '{TestIds.IdOf("a1")}');
            """);

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            fixture.ExecAsync("UPDATE core.audit_event SET action = 'tampered' WHERE action = 'test';"));
        Assert.Equal(PostgresErrors.InsufficientPrivilege, update.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecAsync("DELETE FROM core.audit_event WHERE action = 'test';"));
    }

    [RequiresPostgres]
    public async Task A_zero_duration_service_is_refused_by_the_database()
    {
        var e = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecAsync($"""
            INSERT INTO catalog.service (tenant_id, code, name, duration_minutes, currency_code)
            VALUES ('{TestIds.IdOf(PostgresWorld.Tenant)}', 'zero', 'Zero', 0, 'USD');
            """));
        Assert.Equal(PostgresErrors.CheckViolation, e.SqlState);
    }

    [RequiresPostgres]
    public async Task The_room_exclusion_constraint_exists()
    {
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'appointment_room_no_overlap' AND contype = 'x'"));
    }
}
