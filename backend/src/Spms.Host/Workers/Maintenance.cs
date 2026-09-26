using Npgsql;
using Spms.Persistence;

namespace Spms.Host.Workers;

public sealed class MaintenanceOptions
{
    /// <summary>Months of partitions kept ready ahead of now for each partitioned table.</summary>
    public int MonthsAhead { get; set; } = 3;
    /// <summary>A month of the outbox is dropped once every event in it is published and it is older than this.</summary>
    public int OutboxRetentionDays { get; set; } = 30;
    /// <summary>Development only: run in-process this often. Deployed environments run `Spms.Host --maintain` as a scheduled job.</summary>
    public int InProcessHours { get; set; } = 24;
}

/// <summary>
/// Database housekeeping that needs the owner's rights, so it never runs as
/// the API role: monthly partitions are created ahead of the rows that will
/// need them (a row that lands in a DEFAULT partition blocks that month's
/// partition later), and outbox months that are wholly published and past
/// retention are detached and dropped. Audit and the stock ledger are never
/// dropped here: their retention is a legal decision and they are archived,
/// not deleted.
/// </summary>
public static class DatabaseMaintenance
{
    private static readonly string[] Partitioned = ["core.audit_event", "core.event_outbox", "inventory.inventory_ledger_entry"];

    public sealed record Report(IReadOnlyList<string> Created, IReadOnlyList<string> Dropped, IReadOnlyList<string> Warnings);

    public static async Task<Report> RunAsync(string ownerConnection, string ownerRole, MaintenanceOptions options, ILogger logger, CancellationToken ct = default)
    {
        var created = new List<string>();
        var dropped = new List<string>();
        var warnings = new List<string>();
        await using var conn = new NpgsqlConnection(ownerConnection);
        await conn.OpenAsync(ct);

        async Task<T?> Scalar<T>(string sql, NpgsqlTransaction? tx = null, params (string, object)[] ps)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            var r = await cmd.ExecuteScalarAsync(ct);
            return r is null or DBNull ? default : (T)r;
        }

        var start = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        foreach (var parent in Partitioned)
        {
            var (schema, table) = (parent.Split('.')[0], parent.Split('.')[1]);
            var timeCol = await Scalar<string>("""
                SELECT a.attname FROM pg_partitioned_table p JOIN pg_attribute a ON a.attrelid = p.partrelid AND a.attnum = p.partattrs[0]
                 WHERE p.partrelid = @t::regclass
                """, null, ("t", parent));
            if (timeCol is null) { warnings.Add($"{parent} is not partitioned"); continue; }
            for (var i = 0; i <= options.MonthsAhead; i++)
            {
                var month = start.AddMonths(i);
                var name = $"{table}_{month:yyyyMM}";
                if (await Scalar<bool>("SELECT to_regclass(@n) IS NOT NULL", null, ("n", $"{schema}.{name}"))) continue;
                var from = month.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var to = month.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                // A DEFAULT partition holding rows of that month would make the CREATE fail; report it instead.
                var stray = await Scalar<long>($"SELECT count(*) FROM {schema}.{table}_default WHERE {timeCol} >= @f AND {timeCol} < @t", null, ("f", from), ("t", to));
                if (stray > 0) { warnings.Add($"{schema}.{table}_default holds {stray} row(s) of {month:yyyy-MM}; that month's partition needs a manual move"); continue; }
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var role = new NpgsqlCommand($"SET LOCAL ROLE \"{ownerRole.Replace("\"", "\"\"")}\"", conn, tx)) await role.ExecuteNonQueryAsync(ct);
                await using (var cmd = new NpgsqlCommand("SELECT core.ensure_monthly_partitions(@p::regclass, @m, 1)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("p", parent);
                    cmd.Parameters.AddWithValue("m", month);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                created.Add($"{schema}.{name}");
            }
        }

        // Outbox months: detach and drop when entirely published and past retention.
        var cutoff = DateTime.UtcNow.AddDays(-options.OutboxRetentionDays);
        var months = new List<(string Name, DateTime Upper)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.relname, pg_get_expr(c.relpartbound, c.oid)
              FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
             WHERE i.inhparent = 'core.event_outbox'::regclass AND c.relname <> 'event_outbox_default'
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var bound = r.GetString(1);
                var upper = bound.Split("TO ('")[^1].Split("'")[0];
                if (DateTime.TryParse(upper, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var u))
                    months.Add((r.GetString(0), u));
            }
        foreach (var (name, upper) in months.Where(m => m.Upper <= cutoff))
        {
            var unpublished = await Scalar<long>($"SELECT count(*) FROM core.\"{name}\" WHERE published_at IS NULL");
            if (unpublished > 0) { warnings.Add($"core.{name} still has {unpublished} unpublished event(s)"); continue; }
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var role = new NpgsqlCommand($"SET LOCAL ROLE \"{ownerRole.Replace("\"", "\"\"")}\"", conn, tx)) await role.ExecuteNonQueryAsync(ct);
            await using (var detach = new NpgsqlCommand($"ALTER TABLE core.event_outbox DETACH PARTITION core.\"{name}\"; DROP TABLE core.\"{name}\";", conn, tx))
                await detach.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            dropped.Add($"core.{name}");
        }

        foreach (var c in created) logger.LogInformation("Maintenance: created partition {Partition}", c);
        foreach (var d in dropped) logger.LogInformation("Maintenance: dropped outbox partition {Partition}", d);
        foreach (var w in warnings) logger.LogWarning("Maintenance: {Warning}", w);
        return new Report(created, dropped, warnings);
    }
}

/// <summary>Development: the maintenance pass in-process, at start and then every InProcessHours.</summary>
public sealed class MaintenanceWorker(IConfiguration config, MaintenanceOptions options, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var owner = config.GetConnectionString("SpmsOwner") ?? config.GetConnectionString("Spms") ?? "";
        var role = config["Database:MigrationRole"] ?? "spms_owner";
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DatabaseMaintenance.RunAsync(owner, role, options, logger, stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Maintenance pass failed"); }
            await Task.Delay(TimeSpan.FromHours(Math.Max(1, options.InProcessHours)), stoppingToken);
        }
    }
}
