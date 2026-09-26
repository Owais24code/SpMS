using Npgsql;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Host.Workers;

public sealed class JobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>How often the runner wakes to see which jobs are due.</summary>
    public int TickSeconds { get; set; } = 15;
}

/// <summary>
/// Runs every <see cref="IPropertyJob"/> at every active property on its own
/// interval. One scope and one transaction per job per property: a failure at
/// one property is logged and retried next time, never stops the others.
/// The property list comes from core.active_properties() under the publisher
/// role (ids only); the work itself runs as spms_app inside that property's
/// scope, exactly like a request.
/// </summary>
public sealed class JobRunner(
    IServiceScopeFactory scopes, NpgsqlDataSource dataSource, OutboxOptions outbox, IClock clock, ILogger<JobRunner> logger)
{
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new(StringComparer.Ordinal);

    public async Task<int> RunDueAsync(bool force, CancellationToken ct)
    {
        List<string> due;
        await using (var probe = scopes.CreateAsyncScope())
        {
            var now = clock.UtcNow;
            due = probe.ServiceProvider.GetServices<IPropertyJob>()
                .Where(j => force || !_lastRun.TryGetValue(j.Name, out var last) || now - last >= j.Interval)
                .Select(j => j.Name).ToList();
        }
        if (due.Count == 0) return 0;

        var sites = await ActivePropertiesAsync(ct);
        var total = 0;
        foreach (var name in due)
        {
            foreach (var (tenant, property) in sites)
            {
                try
                {
                    var changed = await RunOneAsync(name, tenant, property, ct);
                    if (changed > 0) Telemetry.JobChanges.Add(changed, new KeyValuePair<string, object?>("job", name));
                    total += changed;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Telemetry.JobFailures.Add(1, new KeyValuePair<string, object?>("job", name));
                    logger.LogError(e, "Job {Job} failed at property {Property}", name, property);
                }
            }
            _lastRun[name] = clock.UtcNow;
        }
        return total;
    }

    private async Task<int> RunOneAsync(string name, Guid tenant, Guid property, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ExecutionScope>().Set(tenant, [property], property, null, ActorType.System, $"job:{name}");
        var job = sp.GetServices<IPropertyJob>().Single(j => j.Name == name);
        var uow = sp.GetRequiredService<IUnitOfWork>();

        await using var tx = await uow.BeginAsync(ct);
        var changed = await job.RunAsync(ct);
        await tx.CommitAsync(ct);
        if (changed > 0) logger.LogInformation("Job {Job} changed {Count} record(s) at property {Property}", name, changed, property);
        return changed;
    }

    private async Task<List<(Guid Tenant, Guid Property)>> ActivePropertiesAsync(CancellationToken ct)
    {
        var list = new List<(Guid, Guid)>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var role = new NpgsqlCommand($"SET LOCAL ROLE \"{outbox.Role.Replace("\"", "\"\"")}\"", conn, tx))
            await role.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT tenant_id, property_id FROM core.active_properties()", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetGuid(0), r.GetGuid(1)));
        return list;
    }
}

public sealed class JobWorker(JobRunner runner, JobOptions options, ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunDueAsync(force: false, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Job runner tick failed; retrying");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.TickSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
