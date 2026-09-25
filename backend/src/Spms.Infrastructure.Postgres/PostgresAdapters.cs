using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;
using Spms.Infrastructure.Postgres.Rows;

namespace Spms.Infrastructure.Postgres;

/// <summary>
/// Maps between the aggregate and its row. Written by hand on purpose: it is
/// the one place that knows both shapes, and adding a field breaks the build
/// here rather than silently dropping it.
/// </summary>
internal static class AppointmentMap
{
    public static Appointment ToDomain(AppointmentRow r, string propertyTimeZone, string guestAlias, string serviceName) =>
        Appointment.Rehydrate(
            r.AppointmentId, r.TenantId, r.PropertyId, propertyTimeZone,
            r.GuestId, guestAlias, r.ServiceId, serviceName, r.DurationMinutes,
            r.ProviderId, r.RoomId,
            // Postgres timestamptz round-trips as an instant; normalise the
            // offset so a field named StartUtc really is UTC.
            r.StartUtc.ToUniversalTime(),
            Enum.Parse<AppointmentStatus>(r.Status), r.RowVersion,
            r.ConfirmationNumber, r.CorrelationId,
            r.CreatedUtc.ToUniversalTime(), r.UpdatedUtc.ToUniversalTime());

    public static void Apply(AppointmentRow r, Appointment a)
    {
        r.TenantId = a.TenantId;
        r.PropertyId = a.PropertyId;
        r.AppointmentId = a.AppointmentId;
        r.GuestId = a.GuestId;
        r.ServiceId = a.ServiceId;
        r.DurationMinutes = a.DurationMinutes;
        r.ProviderId = a.ProviderId;
        r.RoomId = a.RoomId;
        r.StartUtc = a.StartUtc.ToUniversalTime();
        r.Status = a.Status.ToString();
        r.RowVersion = a.RowVersion;
        r.ConfirmationNumber = a.ConfirmationNumber;
        r.CorrelationId = a.CorrelationId;
        r.CreatedUtc = a.CreatedUtc.ToUniversalTime();
        r.UpdatedUtc = a.UpdatedUtc.ToUniversalTime();
        // EndUtc deliberately not set: the trigger owns it.
    }
}

/// <summary>
/// Translates Postgres error codes into domain outcomes.
///
/// Without this, the exclusion constraint's 23P01 would surface as an
/// unhandled DbUpdateException and the operator would get a 500 for what is
/// actually a conflict with a name, a severity and a set of alternatives.
/// </summary>
internal static class PostgresErrors
{
    public const string ExclusionViolation = "23P01";
    public const string UniqueViolation = "23505";

    public static bool IsRoomOverlap(Exception e, out string? roomId)
    {
        roomId = null;
        var pg = Find(e);
        if (pg is null || pg.SqlState != ExclusionViolation) return false;
        if (pg.ConstraintName is not null && !pg.ConstraintName.Contains("room", StringComparison.Ordinal)) return false;

        // The detail line names the conflicting key. Parsed only to label the
        // conflict; the refusal does not depend on it.
        var detail = pg.Detail ?? string.Empty;
        var m = System.Text.RegularExpressions.Regex.Match(detail, @"=\([^)]*?,\s*([^,)]+),\s*\[");
        if (m.Success) roomId = m.Groups[1].Value.Trim();
        return true;
    }

    public static bool IsUniqueViolation(Exception e, string constraintFragment)
    {
        var pg = Find(e);
        return pg?.SqlState == UniqueViolation
            && (pg.ConstraintName?.Contains(constraintFragment, StringComparison.Ordinal) ?? false);
    }

    private static PostgresException? Find(Exception? e)
    {
        while (e is not null)
        {
            if (e is PostgresException pg) return pg;
            e = e.InnerException;
        }
        return null;
    }
}

/// <summary>
/// A transaction over the shared DbContext connection.
///
/// Disposing without committing rolls back, so an early return in the middle
/// of a commit path cannot leave half the work applied. That is the whole
/// reason the scheduling service can `return` freely after opening one.
/// </summary>
public sealed class PostgresUnitOfWork(SpmsDbContext db) : IUnitOfWork
{
    public async Task<ITransaction> BeginAsync(CancellationToken ct = default)
    {
        // A transaction already in flight on this scoped context means an
        // outer operation owns the boundary; join it rather than nest, since
        // Postgres has no true nested transactions and a savepoint here would
        // silently change the rollback semantics the caller expects.
        if (db.Database.CurrentTransaction is not null)
            return new Joined();

        var tx = await db.Database.BeginTransactionAsync(ct);
        return new Owned(db, tx);
    }

    private sealed class Owned(SpmsDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx) : ITransaction
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                await tx.RollbackAsync();
                // Pending tracked changes belong to the rolled-back attempt.
                // Left in place they would be flushed by the next SaveChanges
                // on this scoped context and resurrect the abandoned write.
                db.ChangeTracker.Clear();
            }
            await tx.DisposeAsync();
        }
    }

    private sealed class Joined : ITransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class PostgresAppointmentRepository(SpmsDbContext db) : IAppointmentRepository
{
    public async Task<Appointment?> GetAsync(
        string tenantId, string propertyId, string appointmentId, CancellationToken ct = default)
    {
        // Property scoping is a filter, not a decoration: without it an
        // operator at one property can read and move another's records.
        var q = from a in db.Appointments.AsNoTracking()
                where a.TenantId == tenantId && a.PropertyId == propertyId && a.AppointmentId == appointmentId
                join p in db.Properties on new { a.TenantId, a.PropertyId } equals new { p.TenantId, p.PropertyId }
                join g in db.Guests on new { a.TenantId, a.GuestId } equals new { g.TenantId, g.GuestId }
                join s in db.Services on new { a.TenantId, a.ServiceId } equals new { s.TenantId, s.ServiceId }
                select new { Row = a, p.TimeZoneId, g.DisplayAlias, s.DisplayName };

        var hit = await q.SingleOrDefaultAsync(ct);
        return hit is null ? null : AppointmentMap.ToDomain(hit.Row, hit.TimeZoneId, hit.DisplayAlias, hit.DisplayName);
    }

    public async Task<IReadOnlyList<Appointment>> ListOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        // Half-open, matching the exclusion constraint: an appointment that
        // ends exactly when the window starts does not overlap it.
        var q = from a in db.Appointments.AsNoTracking()
                where a.TenantId == tenantId && a.PropertyId == propertyId
                      && a.StartUtc < toUtc && fromUtc < a.EndUtc
                join p in db.Properties on new { a.TenantId, a.PropertyId } equals new { p.TenantId, p.PropertyId }
                join g in db.Guests on new { a.TenantId, a.GuestId } equals new { g.TenantId, g.GuestId }
                join s in db.Services on new { a.TenantId, a.ServiceId } equals new { s.TenantId, s.ServiceId }
                orderby a.StartUtc
                select new { Row = a, p.TimeZoneId, g.DisplayAlias, s.DisplayName };

        var rows = await q.ToListAsync(ct);
        return rows.Select(h => AppointmentMap.ToDomain(h.Row, h.TimeZoneId, h.DisplayAlias, h.DisplayName)).ToList();
    }

    public async Task<IReadOnlyList<Appointment>> ListPageAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int offset, int limit, CancellationToken ct = default)
    {
        // Paged in SQL. Fetching the whole window and paging in memory paid
        // the full server-side cost the paging was introduced to avoid.
        var q = from a in db.Appointments.AsNoTracking()
                where a.TenantId == tenantId && a.PropertyId == propertyId
                      && a.StartUtc < toUtc && fromUtc < a.EndUtc
                join p in db.Properties on new { a.TenantId, a.PropertyId } equals new { p.TenantId, p.PropertyId }
                join g in db.Guests on new { a.TenantId, a.GuestId } equals new { g.TenantId, g.GuestId }
                join s in db.Services on new { a.TenantId, a.ServiceId } equals new { s.TenantId, s.ServiceId }
                orderby a.StartUtc, a.AppointmentId
                select new { Row = a, p.TimeZoneId, g.DisplayAlias, s.DisplayName };

        var rows = await q.Skip(offset).Take(limit).ToListAsync(ct);
        return rows.Select(h => AppointmentMap.ToDomain(h.Row, h.TimeZoneId, h.DisplayAlias, h.DisplayName)).ToList();
    }

    public async Task<int> CountOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        await db.Appointments.AsNoTracking().CountAsync(
            a => a.TenantId == tenantId && a.PropertyId == propertyId
                 && a.StartUtc < toUtc && fromUtc < a.EndUtc, ct);

    /// <summary>
    /// Inserts without touching the change tracker.
    ///
    /// The tracked version could not report a duplicate id at all: the first
    /// Add left the row in the identity map, so a second Add with the same key
    /// threw InvalidOperationException from EF before any SQL ran — the
    /// database never got the chance to refuse it. ON CONFLICT DO NOTHING puts
    /// the decision where the constraint is, and the statement joins whatever
    /// transaction the unit of work has open.
    ///
    /// ON CONFLICT covers the primary key only; an exclusion constraint is not
    /// a conflict target, so a room overlap still raises 23P01 and is
    /// translated into a domain outcome below.
    /// </summary>
    public async Task<bool> TryAddAsync(Appointment a, CancellationToken ct = default)
    {
        try
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO appointment
                    (tenant_id, property_id, appointment_id, guest_id, service_id, duration_minutes,
                     provider_id, room_id, start_utc, end_utc, status, row_version,
                     confirmation_number, correlation_id, created_utc, updated_utc)
                VALUES ({a.TenantId}, {a.PropertyId}, {a.AppointmentId}, {a.GuestId}, {a.ServiceId},
                        {a.DurationMinutes}, {a.ProviderId}, {a.RoomId},
                        {a.StartUtc.ToUniversalTime()}, 'epoch', {a.Status.ToString()}, {a.RowVersion},
                        {a.ConfirmationNumber}, {a.CorrelationId},
                        {a.CreatedUtc.ToUniversalTime()}, {a.UpdatedUtc.ToUniversalTime()})
                ON CONFLICT (tenant_id, property_id, appointment_id) DO NOTHING
                """, ct);

            return inserted == 1;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrors.ExclusionViolation)
        {
            PostgresErrors.IsRoomOverlap(e, out var roomId);
            throw new RoomOverlapException(roomId ?? a.RoomId, e);
        }
        catch (DbUpdateException e) when (PostgresErrors.IsRoomOverlap(e, out var roomId))
        {
            throw new RoomOverlapException(roomId ?? a.RoomId, e);
        }
    }

    public async Task<bool> TryUpdateAsync(Appointment appointment, int expectedRowVersion, CancellationToken ct = default)
    {
        // A conditional UPDATE, not read-then-write: the version check and the
        // write are one statement, so two writers cannot both pass the check.
        // ExecuteUpdate bypasses the change tracker, which is what we want —
        // nothing here should be flushed by a later SaveChanges.
        try
        {
            var affected = await db.Appointments
                .Where(a => a.TenantId == appointment.TenantId
                         && a.PropertyId == appointment.PropertyId
                         && a.AppointmentId == appointment.AppointmentId
                         && a.RowVersion == expectedRowVersion)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.StartUtc, appointment.StartUtc.ToUniversalTime())
                    .SetProperty(a => a.ProviderId, appointment.ProviderId)
                    .SetProperty(a => a.RoomId, appointment.RoomId)
                    .SetProperty(a => a.Status, appointment.Status.ToString())
                    .SetProperty(a => a.RowVersion, appointment.RowVersion)
                    .SetProperty(a => a.UpdatedUtc, appointment.UpdatedUtc.ToUniversalTime()), ct);

            return affected == 1;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrors.ExclusionViolation)
        {
            throw new RoomOverlapException(appointment.RoomId, e);
        }
        catch (DbUpdateException e) when (PostgresErrors.IsRoomOverlap(e, out var roomId))
        {
            throw new RoomOverlapException(roomId ?? appointment.RoomId, e);
        }
    }

    /// <summary>
    /// Tenant-wide by design: a confirmation number is quoted at any of a
    /// tenant's desks, so uniqueness cannot be per property. The unique index
    /// is the real guarantee; this only lets the allocator avoid a collision
    /// before spending a round trip on the insert.
    /// </summary>
    public Task<bool> ConfirmationNumberExistsAsync(
        string tenantId, string confirmationNumber, CancellationToken ct = default) =>
        db.Appointments.AsNoTracking()
          .AnyAsync(a => a.TenantId == tenantId && a.ConfirmationNumber == confirmationNumber, ct);
}

public sealed class PostgresAuditSink(SpmsDbContext db) : IAuditSink
{
    /// <summary>
    /// Staged on the context, not written immediately: the audit row must
    /// commit in the same transaction as the change it describes, or a failure
    /// between the two leaves the appointment moved with no trail.
    /// </summary>
    public Task RecordAsync(AuditEntry e, CancellationToken ct = default)
    {
        db.AuditEntries.Add(new AuditRow
        {
            AtUtc = e.AtUtc.ToUniversalTime(),
            TenantId = e.TenantId,
            PropertyId = e.PropertyId,
            Actor = e.Actor,
            Action = e.Action,
            Purpose = e.Purpose,
            SubjectType = e.SubjectType,
            SubjectId = e.SubjectId,
            SubjectVersion = e.SubjectVersion,
            BeforeHash = e.BeforeHash,
            AfterHash = e.AfterHash,
            ConflictCodes = e.ConflictCodes.ToArray(),
            SelectedResolution = e.SelectedResolution,
            TargetStatus = e.TargetStatus,
            Reason = e.Reason,
            CorrelationId = e.CorrelationId,
        });
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AuditEntry>> RecentAsync(
        string tenantId, string propertyId, int count, CancellationToken ct = default)
    {
        var rows = await db.AuditEntries.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.PropertyId == propertyId)
            .OrderByDescending(a => a.AuditId)
            .Take(count)
            .ToListAsync(ct);

        return rows.Select(r => new AuditEntry(
            r.AtUtc.ToUniversalTime(), r.TenantId, r.PropertyId, r.Actor, r.Action, r.Purpose,
            r.SubjectType, r.SubjectId, r.SubjectVersion, r.BeforeHash, r.AfterHash,
            r.ConflictCodes, r.SelectedResolution, r.TargetStatus, r.Reason, r.CorrelationId)).ToList();
    }
}

/// <summary>
/// Idempotency on its OWN connection, deliberately outside any transaction the
/// unit of work opened.
///
/// If a claim were enrolled in the business transaction, a refused request
/// would roll the reservation back too — and the retry would re-run the work
/// instead of replaying the refusal. Conversely a completed result must
/// survive a business rollback, or the client is told nothing happened when
/// something did.
/// </summary>
public sealed class PostgresIdempotencyStore(NpgsqlDataSource dataSource) : IIdempotencyStore
{
    private static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an incomplete reservation is honoured before a later claim may
    /// steal it. Without a lease, a request killed between claim and complete
    /// left the key answering 409 for the full TTL with no recovery path.
    /// </summary>
    private static readonly TimeSpan ReservationLease = TimeSpan.FromSeconds(60);

    public async Task<IdempotencyClaim> ClaimAsync(
        IdempotencyScope scope, string requestHash, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // One statement. An INSERT ... ON CONFLICT DO UPDATE with a guard is
        // atomic, so two concurrent requests holding one key cannot both be
        // reserved — which a read-then-write check allowed, and is the single
        // case the key exists to prevent.
        const string sql = """
            INSERT INTO idempotency_key
                (tenant_id, property_id, operation, route, key, request_hash, completed, status_code, response_json, at_utc)
            VALUES (@t, @p, @o, @r, @k, @h, false, 0, NULL, @now)
            ON CONFLICT (tenant_id, property_id, operation, route, key) DO UPDATE
                SET request_hash = EXCLUDED.request_hash,
                    at_utc       = EXCLUDED.at_utc,
                    completed    = false,
                    status_code  = 0,
                    response_json = NULL
                WHERE idempotency_key.completed = false
                  AND idempotency_key.at_utc < @leaseCutoff
            RETURNING 'reserved' AS outcome, 0 AS status_code, NULL::text AS response_json;
            """;

        await using (var claim = new NpgsqlCommand(sql, conn))
        {
            claim.Parameters.AddWithValue("t", scope.TenantId);
            claim.Parameters.AddWithValue("p", scope.PropertyId);
            claim.Parameters.AddWithValue("o", scope.Operation);
            claim.Parameters.AddWithValue("r", scope.Route);
            claim.Parameters.AddWithValue("k", scope.Key);
            claim.Parameters.AddWithValue("h", requestHash);
            claim.Parameters.AddWithValue("now", nowUtc);
            claim.Parameters.AddWithValue("leaseCutoff", nowUtc - ReservationLease);

            await using var reader = await claim.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return new IdempotencyClaim(IdempotencyOutcome.Reserved);
        }

        // The upsert did nothing, so a row exists that we may not take over.
        const string read = """
            SELECT request_hash, completed, status_code, response_json
            FROM idempotency_key
            WHERE tenant_id=@t AND property_id=@p AND operation=@o AND route=@r AND key=@k;
            """;

        await using var q = new NpgsqlCommand(read, conn);
        q.Parameters.AddWithValue("t", scope.TenantId);
        q.Parameters.AddWithValue("p", scope.PropertyId);
        q.Parameters.AddWithValue("o", scope.Operation);
        q.Parameters.AddWithValue("r", scope.Route);
        q.Parameters.AddWithValue("k", scope.Key);

        await using var rr = await q.ExecuteReaderAsync(ct);
        if (!await rr.ReadAsync(ct))
            // Vanished between the two statements. Treating it as in-flight is
            // the safe read: the caller retries rather than double-executing.
            return new IdempotencyClaim(IdempotencyOutcome.InFlight);

        var storedHash = rr.GetString(0);
        var completed = rr.GetBoolean(1);

        if (storedHash != requestHash) return new IdempotencyClaim(IdempotencyOutcome.Mismatch);
        if (!completed) return new IdempotencyClaim(IdempotencyOutcome.InFlight);

        return new IdempotencyClaim(IdempotencyOutcome.Replay, rr.GetInt32(2), rr.IsDBNull(3) ? null : rr.GetString(3));
    }

    public async Task CompleteAsync(
        IdempotencyScope scope, int statusCode, string responseJson, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE idempotency_key
            SET completed = true, status_code = @s, response_json = @j, at_utc = @now
            WHERE tenant_id=@t AND property_id=@p AND operation=@o AND route=@r AND key=@k;
            """, conn);
        cmd.Parameters.AddWithValue("t", scope.TenantId);
        cmd.Parameters.AddWithValue("p", scope.PropertyId);
        cmd.Parameters.AddWithValue("o", scope.Operation);
        cmd.Parameters.AddWithValue("r", scope.Route);
        cmd.Parameters.AddWithValue("k", scope.Key);
        cmd.Parameters.AddWithValue("s", statusCode);
        cmd.Parameters.AddWithValue("j", responseJson);
        cmd.Parameters.AddWithValue("now", nowUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AbandonAsync(IdempotencyScope scope, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // A completed result is never erased: the replay has to survive.
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM idempotency_key
            WHERE tenant_id=@t AND property_id=@p AND operation=@o AND route=@r AND key=@k
              AND completed = false;
            """, conn);
        cmd.Parameters.AddWithValue("t", scope.TenantId);
        cmd.Parameters.AddWithValue("p", scope.PropertyId);
        cmd.Parameters.AddWithValue("o", scope.Operation);
        cmd.Parameters.AddWithValue("r", scope.Route);
        cmd.Parameters.AddWithValue("k", scope.Key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> SweepAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM idempotency_key WHERE completed AND at_utc < @cutoff;", conn);
        cmd.Parameters.AddWithValue("cutoff", nowUtc - CompletedTtl);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class PostgresPreflightStore(SpmsDbContext db) : IPreflightStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(string tenantId, PreflightResult r, CancellationToken ct = default)
    {
        db.PreflightTokens.Add(new PreflightRow
        {
            TenantId = tenantId,
            Token = r.Token,
            PropertyId = r.PropertyId,
            AppointmentId = r.Proposal.AppointmentId,
            ProposedStartUtc = r.ProposedStartUtc.ToUniversalTime(),
            ProposedEndUtc = r.ProposedEndUtc.ToUniversalTime(),
            ProposedProviderId = r.Proposal.ProviderId,
            ProposedRoomId = r.Proposal.RoomId,
            FromRowVersion = r.Proposal.FromRowVersion,
            ConflictsJson = JsonSerializer.Serialize(r.Conflicts, Json),
            IssuedUtc = (r.ExpiresUtc - SchedulingService.PreflightTtl).ToUniversalTime(),
            ExpiresUtc = r.ExpiresUtc.ToUniversalTime(),
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<PreflightResult?> FindAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var row = await db.PreflightTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TenantId == tenantId && t.Token == token, ct);
        return row is null ? null : ToDomain(row);
    }

    /// <summary>
    /// Consumes the token. A conditional DELETE, so in a race exactly one
    /// caller sees a row removed and the loser is told the token is invalid
    /// rather than both applying the same move.
    /// </summary>
    public async Task<bool> TryConsumeAsync(string tenantId, string token, CancellationToken ct = default)
    {
        var deleted = await db.PreflightTokens
            .Where(t => t.TenantId == tenantId && t.Token == token)
            .ExecuteDeleteAsync(ct);
        return deleted == 1;
    }

    public Task<int> EvictExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        db.PreflightTokens.Where(t => t.ExpiresUtc < nowUtc).ExecuteDeleteAsync(ct);

    private static PreflightResult ToDomain(PreflightRow r) => new(
        r.Token, r.ExpiresUtc.ToUniversalTime(),
        new MoveProposal(r.AppointmentId, r.ProposedStartUtc.ToUniversalTime(),
            r.ProposedProviderId, r.ProposedRoomId, r.FromRowVersion),
        r.PropertyId,
        r.ProposedStartUtc.ToUniversalTime(), r.ProposedEndUtc.ToUniversalTime(),
        JsonSerializer.Deserialize<List<Conflict>>(r.ConflictsJson, Json) ?? []);
}

public sealed class PostgresServiceCatalog(SpmsDbContext db) : IServiceCatalog
{
    public async Task<CatalogService?> FindAsync(string tenantId, string serviceId, CancellationToken ct = default)
    {
        var r = await db.Services.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.ServiceId == serviceId, ct);
        return r is null ? null : new CatalogService(r.ServiceId, r.DisplayName, r.DurationMinutes);
    }

    public async Task<IReadOnlyList<CatalogService>> ListAsync(string tenantId, CancellationToken ct = default) =>
        await db.Services.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.ServiceId)
            .Select(s => new CatalogService(s.ServiceId, s.DisplayName, s.DurationMinutes))
            .ToListAsync(ct);
}

public sealed class PostgresQualificationRegister(SpmsDbContext db) : IQualificationRegister
{
    /// <summary>
    /// Fails closed in two ways: no row means not qualified, and an expired
    /// row means not qualified. The staff member must also still be
    /// assignable, so a terminated provider cannot take new work even with a
    /// live credential on file.
    /// </summary>
    public Task<bool> IsQualifiedAsync(
        string tenantId, string propertyId, string providerId, string serviceId,
        DateTimeOffset asOfUtc, CancellationToken ct = default) =>
        (from q in db.StaffQualifications.AsNoTracking()
         where q.TenantId == tenantId && q.PropertyId == propertyId
               && q.ProviderId == providerId && q.ServiceId == serviceId
               && q.GrantedUtc <= asOfUtc
               && (q.ExpiresUtc == null || q.ExpiresUtc > asOfUtc)
         join s in db.Staff on new { q.TenantId, q.PropertyId, q.ProviderId }
                        equals new { s.TenantId, s.PropertyId, s.ProviderId }
         where s.Assignable
         select q).AnyAsync(ct);

    public Task<bool> IsKnownAsync(
        string tenantId, string propertyId, string providerId, CancellationToken ct = default) =>
        db.Staff.AsNoTracking()
          .AnyAsync(s => s.TenantId == tenantId && s.PropertyId == propertyId && s.ProviderId == providerId, ct);
}

public sealed class PostgresPropertyDirectory(SpmsDbContext db) : IPropertyDirectory
{
    public async Task<PropertyProfile?> FindAsync(
        string tenantId, string propertyId, CancellationToken ct = default)
    {
        var p = await db.Properties.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.PropertyId == propertyId, ct);
        if (p is null) return null;

        var policies = await db.BufferPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PropertyId == propertyId)
            .ToListAsync(ct);

        var fallback = policies.SingleOrDefault(x => x.ServiceId == null);
        var byService = policies
            .Where(x => x.ServiceId != null)
            .ToDictionary(x => x.ServiceId!, x => new BufferPolicy(x.RoomTurnoverMinutes, x.ProviderTransitionMinutes),
                          StringComparer.Ordinal);

        return new PropertyProfile(
            p.PropertyId, p.TimeZoneId, p.OpenMinute, p.CloseMinute,
            fallback is null
                ? BufferPolicy.Fallback
                : new BufferPolicy(fallback.RoomTurnoverMinutes, fallback.ProviderTransitionMinutes),
            byService);
    }
}
