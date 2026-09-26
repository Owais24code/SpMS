using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Spms.Modules.Core.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Core.Infrastructure;

/// <summary>
/// core.audit_event, staged on the request's context so the row commits in
/// the same transaction as the change it describes.
/// </summary>
public sealed class EfAuditSink(SpmsDbContext db, IClock clock) : IAuditSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task RecordAsync(AuditEntry e, CancellationToken ct = default)
    {
        var scope = db.Scope;
        db.Add(new AuditEventRow
        {
            AuditId = Uuid7.New(),
            TenantId = scope.RequireTenant(),
            PropertyId = e.TenantWide ? null : e.PropertyId ?? scope.CurrentPropertyId,
            OccurredAt = clock.UtcNow.ToUniversalTime(),
            ActorType = scope.ActorType.ToString(),
            ActorPrincipalId = scope.PrincipalId,
            OnBehalfOfPrincipalId = e.OnBehalfOfPrincipalId,
            Purpose = e.Purpose,
            Action = e.Action,
            EntityType = e.EntityType,
            EntityId = Guid.Parse(e.EntityId),
            EntityVersion = e.EntityVersion,
            FromStatus = e.FromStatus,
            ToStatus = e.ToStatus,
            BeforeHash = e.BeforeHash,
            AfterHash = e.AfterHash,
            BeforeData = e.BeforeData is null ? null : JsonSerializer.Serialize(e.BeforeData, Json),
            AfterData = e.AfterData is null ? null : JsonSerializer.Serialize(e.AfterData, Json),
            ChangedFields = e.ChangedFields?.ToArray(),
            ConflictCodes = e.ConflictCodes?.ToArray() ?? [],
            ReasonCode = e.ReasonCode,
            ReasonText = e.ReasonText,
            AuthorizationDecision = e.Authorization is null ? null : JsonSerializer.Serialize(new
            {
                relation = e.Authorization.Relation, @object = e.Authorization.Object,
                model_id = e.Authorization.ModelId, allowed = e.Authorization.Allowed,
            }),
            CorrelationId = scope.CorrelationId,
        });
        return Task.CompletedTask;
    }
}

/// <summary>core.event_outbox, staged on the request's context (NFR-001).</summary>
public sealed class EfOutbox(SpmsDbContext db, IClock clock) : IOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Enqueue(OutboxEvent e)
    {
        var scope = db.Scope;
        var now = clock.UtcNow.ToUniversalTime();
        db.Add(new EventOutboxRow
        {
            EventId = Uuid7.New(),
            TenantId = scope.RequireTenant(),
            PropertyId = e.TenantWide ? null : e.PropertyId ?? scope.CurrentPropertyId,
            OccurredAt = now,
            EventType = e.EventType,
            SchemaVersion = e.SchemaVersion,
            AggregateType = e.AggregateType,
            AggregateId = e.AggregateId,
            AggregateVersion = e.AggregateVersion,
            Payload = JsonSerializer.Serialize(e.Payload, Json),
            CausationId = e.CausationId,
            NextAttemptAt = now,
            CorrelationId = scope.CorrelationId,
        });
    }
}

/// <summary>
/// Idempotency (API-001) on its OWN connection and its own short transaction,
/// deliberately outside the request transaction: a refused request must not
/// roll back its own replay record, and a completed result must survive a
/// business rollback.
///
/// core.idempotency_record is property-scoped under row-level security like
/// everything else, so each statement opens a transaction scoped to the key's
/// tenant and property. The stored response is envelope-encrypted: it can
/// carry guest-facing data (a confirmation number, an alias).
/// </summary>
public sealed class PostgresIdempotencyStore(NpgsqlDataSource dataSource, PersistenceOptions options, IFieldProtector protector)
    : IIdempotencyStore
{
    private const string Purpose = "core.idempotency_record.response";
    private static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an incomplete reservation is honoured before a later claim may
    /// take it over. Without a lease, a request killed between claim and
    /// complete left the key answering 409 with no recovery path.
    /// </summary>
    private static readonly TimeSpan ReservationLease = TimeSpan.FromSeconds(60);

    public async Task<IdempotencyClaim> ClaimAsync(IdempotencyScope scope, string requestHash, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var hash = requestHash.ToLowerInvariant();
        return await InScope(scope, async (conn, tx) =>
        {
            // One statement: INSERT ... ON CONFLICT DO UPDATE with a guard is
            // atomic, so two concurrent requests holding one key cannot both be
            // reserved. An expired record (a dead reservation, or a completed
            // one past retention) may be taken over.
            await using (var claim = new NpgsqlCommand("""
                INSERT INTO core.idempotency_record
                    (tenant_id, property_id, operation, route, idempotency_key, request_hash, completed, expires_at, created_by, correlation_id)
                VALUES (@t, @p, @o, @r, @k, @h, false, @lease, @who, NULL)
                ON CONFLICT ON CONSTRAINT idempotency_record_key_uq DO UPDATE
                    SET request_hash = EXCLUDED.request_hash, expires_at = EXCLUDED.expires_at,
                        completed = false, response_code = NULL, response_cipher = NULL, key_version = NULL
                    WHERE core.idempotency_record.expires_at < @now
                RETURNING 1;
                """, conn, tx))
            {
                Bind(claim, scope);
                claim.Parameters.AddWithValue("h", hash);
                claim.Parameters.AddWithValue("lease", (nowUtc + ReservationLease).ToUniversalTime());
                claim.Parameters.AddWithValue("now", nowUtc.ToUniversalTime());
                claim.Parameters.AddWithValue("who", (object?)scope.PrincipalId ?? DBNull.Value);
                if (await claim.ExecuteScalarAsync(ct) is not null)
                    return new IdempotencyClaim(IdempotencyOutcome.Reserved);
            }

            await using var read = new NpgsqlCommand("""
                SELECT request_hash, completed, response_code, response_cipher, key_version
                  FROM core.idempotency_record
                 WHERE tenant_id=@t AND property_id=@p AND operation=@o AND route=@r AND idempotency_key=@k;
                """, conn, tx);
            Bind(read, scope);
            await using var rr = await read.ExecuteReaderAsync(ct);
            // Vanished between the two statements: in-flight is the safe answer.
            if (!await rr.ReadAsync(ct)) return new IdempotencyClaim(IdempotencyOutcome.InFlight);

            if (rr.GetString(0).TrimEnd() != hash) return new IdempotencyClaim(IdempotencyOutcome.Mismatch);
            if (!rr.GetBoolean(1)) return new IdempotencyClaim(IdempotencyOutcome.InFlight);

            var code = rr.IsDBNull(2) ? 200 : rr.GetInt32(2);
            string? body = null;
            if (!rr.IsDBNull(3))
                body = protector.UnprotectString((byte[])rr[3], rr.GetString(4), Purpose);
            return new IdempotencyClaim(IdempotencyOutcome.Replay, code, body);
        }, ct);
    }

    public Task CompleteAsync(IdempotencyScope scope, int statusCode, string responseJson, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        InScope(scope, async (conn, tx) =>
        {
            var cipher = protector.ProtectString(responseJson, Purpose);
            await using var cmd = new NpgsqlCommand("""
                UPDATE core.idempotency_record
                   SET completed = true, response_code = @s, response_cipher = @c, key_version = @kv, expires_at = @exp
                 WHERE tenant_id=@t AND property_id=@p AND operation=@o AND route=@r AND idempotency_key=@k;
                """, conn, tx);
            Bind(cmd, scope);
            cmd.Parameters.AddWithValue("s", statusCode);
            cmd.Parameters.AddWithValue("c", cipher.Cipher);
            cmd.Parameters.AddWithValue("kv", cipher.KeyVersion);
            cmd.Parameters.AddWithValue("exp", (nowUtc + CompletedTtl).ToUniversalTime());
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    public Task AbandonAsync(IdempotencyScope scope, CancellationToken ct = default) =>
        InScope(scope, async (conn, tx) =>
        {
            // A completed result is never erased: the replay has to survive.
            await using var cmd = new NpgsqlCommand("""
                DELETE FROM core.idempotency_record
                 WHERE tenant_id=@t AND property_id=@p AND operation=@o AND route=@r AND idempotency_key=@k
                   AND completed = false;
                """, conn, tx);
            Bind(cmd, scope);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    public Task<int> SweepAsync(IdempotencyScope scopeForTenant, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        InScope(scopeForTenant, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM core.idempotency_record WHERE property_id = @p AND expires_at < @now;", conn, tx);
            cmd.Parameters.AddWithValue("p", scopeForTenant.PropertyId);
            cmd.Parameters.AddWithValue("now", nowUtc.ToUniversalTime());
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    private async Task<T> InScope<T>(IdempotencyScope key, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var scope = new ExecutionScope();
        scope.Set(key.TenantId, [key.PropertyId], key.PropertyId, key.PrincipalId, ActorType.System, null);
        await ScopeSql.ApplyAsync(conn, tx, scope, options.RuntimeRole, ct);
        var result = await work(conn, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    private static void Bind(NpgsqlCommand cmd, IdempotencyScope s)
    {
        cmd.Parameters.AddWithValue("t", s.TenantId);
        cmd.Parameters.AddWithValue("p", s.PropertyId);
        cmd.Parameters.AddWithValue("o", s.Operation);
        cmd.Parameters.AddWithValue("r", s.Route);
        cmd.Parameters.AddWithValue("k", s.Key);
    }
}

/// <summary>The read side of the audit trail (spa.admin): the scoped property's recent rows, hashes not values.</summary>
public sealed class AuditQueries(SpmsDbContext db)
{
    public sealed record Row(
        string AtUtc, string ActorType, string? ActorPrincipalId, string Action, string? Purpose,
        string EntityType, string EntityId, int? EntityVersion, string? FromStatus, string? ToStatus,
        string? BeforeHash, string? AfterHash, IReadOnlyList<string> ConflictCodes,
        string? ReasonCode, string? ReasonText, string? CorrelationId);

    public async Task<IReadOnlyList<Row>> RecentAsync(Guid propertyId, int take, string? entityId, CancellationToken ct)
    {
        var q = db.Set<AuditEventRow>().AsNoTracking().Where(a => a.PropertyId == propertyId);
        if (Guid.TryParse(entityId, out var eid)) q = q.Where(a => a.EntityId == eid);
        var rows = await q.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.AuditId).Take(take).ToListAsync(ct);
        return rows.Select(r => new Row(
            r.OccurredAt.ToUniversalTime().ToString("O"), r.ActorType, r.ActorPrincipalId?.ToString(),
            r.Action, r.Purpose, r.EntityType, r.EntityId.ToString(), r.EntityVersion, r.FromStatus, r.ToStatus,
            r.BeforeHash?.Trim(), r.AfterHash?.Trim(), r.ConflictCodes, r.ReasonCode, r.ReasonText, r.CorrelationId)).ToList();
    }
}
