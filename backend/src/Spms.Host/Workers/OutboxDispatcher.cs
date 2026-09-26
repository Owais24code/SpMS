using Npgsql;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Host.Workers;

public sealed class OutboxOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>The role that may read the outbox across tenants (BYPASSRLS on that table only).</summary>
    public string Role { get; set; } = "spms_outbox";
    public int BatchSize { get; set; } = 50;
    public int IdleDelayMs { get; set; } = 1000;
    public int MaxAttempts { get; set; } = 25;
}

/// <summary>
/// The outbox publisher (NFR-001): reads committed events from
/// core.event_outbox and hands each to every handler that wants it — OpenFGA
/// tuple sync, message scheduling, integrations.
///
/// At least once: an event is marked published only after every handler
/// succeeded, so handlers are idempotent. FOR UPDATE SKIP LOCKED lets several
/// instances run without handing one event to two of them. A failing handler
/// backs the event off exponentially and records the error; after
/// MaxAttempts it is parked (next attempt a day out) and logged as critical,
/// never silently dropped.
/// </summary>
public sealed class OutboxDispatcher(
    NpgsqlDataSource dataSource, IEnumerable<IOutboxHandler> handlers, OutboxOptions options, ILogger<OutboxDispatcher> logger)
{
    private readonly IReadOnlyList<IOutboxHandler> _handlers = handlers.ToList();

    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var role = new NpgsqlCommand($"SET LOCAL ROLE \"{options.Role.Replace("\"", "\"\"")}\"", conn, tx))
            await role.ExecuteNonQueryAsync(ct);

        var batch = new List<OutboxMessage>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT event_id, occurred_at, tenant_id, property_id, event_type, aggregate_type, aggregate_id,
                   aggregate_version, payload::text, correlation_id, attempt_count
              FROM core.event_outbox
             WHERE published_at IS NULL AND next_attempt_at <= now()
             ORDER BY occurred_at
             LIMIT @n
               FOR UPDATE SKIP LOCKED
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("n", options.BatchSize);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                batch.Add(new OutboxMessage(
                    r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3),
                    r.GetString(4), r.GetString(5), r.GetGuid(6), r.IsDBNull(7) ? null : r.GetInt32(7),
                    r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10)));
            }
        }

        foreach (var m in batch)
        {
            string? error = null;
            foreach (var h in _handlers.Where(h => h.Handles(m.EventType)))
            {
                try
                {
                    await h.HandleAsync(m, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    error = $"{h.Name}: {e.GetType().Name}: {e.Message}";
                    logger.LogWarning(e, "Outbox handler {Handler} failed on {EventType} {EventId}", h.Name, m.EventType, m.EventId);
                    break;
                }
            }

            await using var update = error is null
                ? new NpgsqlCommand("UPDATE core.event_outbox SET published_at = now() WHERE event_id = @id AND occurred_at = @at", conn, tx)
                : new NpgsqlCommand("""
                    UPDATE core.event_outbox
                       SET attempt_count = attempt_count + 1, last_error = left(@e, 2000),
                           next_attempt_at = now() + CASE WHEN attempt_count + 1 >= @max THEN interval '1 day'
                                                          ELSE make_interval(secs => least(3600, power(2, attempt_count + 1)::int)) END
                     WHERE event_id = @id AND occurred_at = @at
                    """, conn, tx);
            update.Parameters.AddWithValue("id", m.EventId);
            update.Parameters.AddWithValue("at", m.OccurredAt);
            if (error is not null)
            {
                update.Parameters.AddWithValue("e", error);
                update.Parameters.AddWithValue("max", options.MaxAttempts);
                if (m.AttemptCount + 1 >= options.MaxAttempts)
                    logger.LogCritical("Outbox event {EventId} ({EventType}) parked after {Attempts} attempts: {Error}",
                        m.EventId, m.EventType, m.AttemptCount + 1, error);
            }
            await update.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return batch.Count;
    }
}

public sealed class OutboxWorker(OutboxDispatcher dispatcher, OutboxOptions options, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                handled = await dispatcher.DispatchOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Outbox dispatch failed; retrying");
            }
            if (handled == 0)
            {
                try { await Task.Delay(options.IdleDelayMs, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
