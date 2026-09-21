using Npgsql;
using Spms.Infrastructure.Postgres;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>
/// The migrations and the structures the application cannot enforce itself.
/// </summary>
public class SchemaTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [RequiresPostgres]
    public async Task All_three_migrations_are_recorded_with_a_checksum()
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT version, file_name, checksum FROM schema_migration ORDER BY version;", conn);
        await using var r = await cmd.ExecuteReaderAsync();

        var versions = new List<string>();
        while (await r.ReadAsync())
        {
            versions.Add(r.GetString(0));
            Assert.NotEmpty(r.GetString(1));
            // TST-023: a migration edited after it was applied has to be
            // detectable rather than silently divergent.
            Assert.Equal(32, r.GetString(2).Length);
        }

        Assert.Equal(["V001", "V002", "V003"], versions);
    }

    [RequiresPostgres]
    public async Task Running_the_migrations_again_is_a_no_op()
    {
        var applied = await new MigrationRunner(fixture.DataSource, new Quiet()).RunAsync();
        Assert.All(applied, a => Assert.True(a.AlreadyPresent));
    }

    [RequiresPostgres]
    public async Task The_schema_guard_passes_against_a_migrated_database()
    {
        Assert.Empty(await new SchemaGuard(fixture.DataSource).VerifyAsync());
    }

    [RequiresPostgres]
    public async Task The_schema_guard_fails_when_the_room_constraint_is_dropped()
    {
        // The whole point of the guard: an instance that starts without the
        // exclusion constraint would go on evaluating conflicts and be
        // systematically wrong about the one it cannot enforce itself.
        await Exec("ALTER TABLE appointment DROP CONSTRAINT appointment_room_no_overlap;");
        try
        {
            var problems = await new SchemaGuard(fixture.DataSource).VerifyAsync();
            Assert.Contains(problems, p => p.Contains("appointment_room_no_overlap"));
        }
        finally
        {
            await Exec("""
                ALTER TABLE appointment ADD CONSTRAINT appointment_room_no_overlap
                EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, room_id WITH =,
                                    tstzrange(start_utc, end_utc, '[)') WITH &&)
                WHERE (room_id IS NOT NULL AND status NOT IN ('Cancelled','NoShow'));
                """);
        }
    }

    [RequiresPostgres]
    public async Task End_utc_is_derived_by_the_database_not_by_the_caller()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);

        // A caller supplying a wrong end_utc must not be able to corrupt what
        // the exclusion constraint indexes.
        await Exec("""
            INSERT INTO appointment
              (tenant_id, property_id, appointment_id, guest_id, service_id, duration_minutes,
               provider_id, room_id, start_utc, end_utc, status, row_version, correlation_id, created_utc, updated_utc)
            VALUES (@t, @p, 'derive', 'guest-0000', 'svc-peel', 30, NULL, 'room-tx',
                    '2026-06-15T10:00:00Z', '2030-01-01T00:00:00Z', 'Confirmed', 1, 'c', now(), now());
            """);

        var end = await Scalar<DateTime>("SELECT end_utc FROM appointment WHERE appointment_id = 'derive';");
        Assert.Equal(new DateTime(2026, 6, 15, 10, 30, 0, DateTimeKind.Utc), end.ToUniversalTime());
    }

    [RequiresPostgres]
    public async Task The_audit_trail_cannot_be_updated_or_deleted()
    {
        await Exec("""
            INSERT INTO audit_entry
              (at_utc, tenant_id, property_id, actor, action, purpose, subject_type, subject_id,
               subject_version, correlation_id)
            VALUES (now(), 'tenant-append', 'p', 'me', 'test', 'test', 'appointment', 'a1', 1, 'c');
            """);

        await Exec("UPDATE audit_entry SET actor = 'tampered' WHERE tenant_id = 'tenant-append';");
        await Exec("DELETE FROM audit_entry WHERE tenant_id = 'tenant-append';");

        // Append-only is enforced by database rules, not by a comment on an
        // interface. A trail the application can rewrite is not evidence.
        var actor = await Scalar<string>("SELECT actor FROM audit_entry WHERE tenant_id = 'tenant-append';");
        Assert.Equal("me", actor);
    }

    [RequiresPostgres]
    public async Task A_zero_duration_service_is_refused_by_the_database()
    {
        await Assert.ThrowsAsync<PostgresException>(() => Exec(
            "INSERT INTO service (tenant_id, service_id, display_name, duration_minutes) " +
            "VALUES ('tenant-test', 'svc-zero', 'Zero', 0);"));
    }

    private async Task Exec(string sql)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (sql.Contains("@t", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("t", PostgresWorld.Tenant);
        if (sql.Contains("@p", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("p", PostgresWorld.Property);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed class Quiet : ILoggerLike
    {
        public void Info(string message) { }
    }
}
