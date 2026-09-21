using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;

namespace Spms.Infrastructure.InMemory;

/// <summary>
/// In-memory adapters, swappable for EF Core.
///
/// One Dictionary behind one lock, taken by every method. The previous version
/// mixed a ConcurrentDictionary with a separate lock, so an add racing an
/// update could clobber it and the concurrency safety was accidental.
/// </summary>
public sealed class InMemoryAppointmentRepository : IAppointmentRepository
{
    private readonly Dictionary<string, Appointment> _byId = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<Appointment?> GetAsync(string tenantId, string propertyId, string appointmentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(Key(tenantId, appointmentId), out var a)) return Task.FromResult<Appointment?>(null);
            // Property scoping is a filter, not a decoration: without it an
            // operator at one property can read and move another's records.
            if (!string.Equals(a.PropertyId, propertyId, StringComparison.Ordinal)) return Task.FromResult<Appointment?>(null);
            return Task.FromResult<Appointment?>(a.Copy());
        }
    }

    public Task<IReadOnlyList<Appointment>> ListOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var rows = _byId.Values
                .Where(a => a.TenantId == tenantId
                         && a.PropertyId == propertyId
                         && a.Overlaps(fromUtc, toUtc))
                .OrderBy(a => a.StartUtc)
                .Select(a => a.Copy())
                .ToList();
            return Task.FromResult<IReadOnlyList<Appointment>>(rows);
        }
    }

    public Task<IReadOnlyList<Appointment>> ListPageAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int offset, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var rows = _byId.Values
                .Where(a => a.TenantId == tenantId && a.PropertyId == propertyId && a.Overlaps(fromUtc, toUtc))
                .OrderBy(a => a.StartUtc).ThenBy(a => a.AppointmentId, StringComparer.Ordinal)
                .Skip(offset).Take(limit)
                .Select(a => a.Copy())
                .ToList();
            return Task.FromResult<IReadOnlyList<Appointment>>(rows);
        }
    }

    public Task<int> CountOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_byId.Values.Count(
                a => a.TenantId == tenantId && a.PropertyId == propertyId && a.Overlaps(fromUtc, toUtc)));
    }

    public Task<bool> TryAddAsync(Appointment appointment, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_byId.TryAdd(Key(appointment.TenantId, appointment.AppointmentId), appointment.Copy()));
    }

    public Task<bool> TryUpdateAsync(Appointment appointment, int expectedRowVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(appointment.TenantId, appointment.AppointmentId);
            if (!_byId.TryGetValue(key, out var stored)) return Task.FromResult(false);
            if (stored.RowVersion != expectedRowVersion) return Task.FromResult(false);
            _byId[key] = appointment.Copy();
            return Task.FromResult(true);
        }
    }

    public Task<bool> ConfirmationNumberExistsAsync(string tenantId, string confirmationNumber, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_byId.Values.Any(a => a.TenantId == tenantId && a.ConfirmationNumber == confirmationNumber));
    }

    private static string Key(string tenantId, string id) => $"{tenantId}/{id}";
}

/// <summary>
/// Claim-then-complete idempotency. A reservation is inserted atomically, so
/// two concurrent requests with one key cannot both proceed.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private sealed record Slot(string RequestHash, bool Completed, int StatusCode, string? ResponseJson, DateTimeOffset AtUtc);

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an incomplete reservation is honoured before a later claim may
    /// steal it. Without a lease, a request killed between Claim and
    /// Complete — a process crash, or a cancellation on a path the finally
    /// could not reach — left the key answering 409 for the full 24 hours with
    /// no recovery path.
    /// </summary>
    private static readonly TimeSpan ReservationLease = TimeSpan.FromSeconds(60);

    public Task<IdempotencyClaim> ClaimAsync(
        IdempotencyScope scope, string requestHash, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Evict(nowUtc);
            var key = Scope(scope);

            if (!_slots.TryGetValue(key, out var slot))
            {
                _slots[key] = new Slot(requestHash, Completed: false, 0, null, nowUtc);
                return Task.FromResult(new IdempotencyClaim(IdempotencyOutcome.Reserved));
            }

            // An expired reservation is taken over rather than reported busy.
            if (!slot.Completed && nowUtc - slot.AtUtc > ReservationLease)
            {
                _slots[key] = new Slot(requestHash, Completed: false, 0, null, nowUtc);
                return Task.FromResult(new IdempotencyClaim(IdempotencyOutcome.Reserved));
            }

            if (slot.RequestHash != requestHash)
                return Task.FromResult(new IdempotencyClaim(IdempotencyOutcome.Mismatch));

            return Task.FromResult(slot.Completed
                ? new IdempotencyClaim(IdempotencyOutcome.Replay, slot.StatusCode, slot.ResponseJson)
                : new IdempotencyClaim(IdempotencyOutcome.InFlight));
        }
    }

    public Task CompleteAsync(
        IdempotencyScope scope, int statusCode, string responseJson, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Scope(scope);
            if (_slots.TryGetValue(key, out var slot))
                _slots[key] = slot with { Completed = true, StatusCode = statusCode, ResponseJson = responseJson, AtUtc = nowUtc };
        }
        return Task.CompletedTask;
    }

    public Task AbandonAsync(IdempotencyScope scope, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Scope(scope);
            // A completed result is never erased: the replay must survive.
            if (_slots.TryGetValue(key, out var slot) && !slot.Completed) _slots.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task<int> SweepAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var stale = _slots.Where(kv => kv.Value.Completed && nowUtc - kv.Value.AtUtc > CompletedTtl)
                              .Select(kv => kv.Key).ToList();
            foreach (var k in stale) _slots.Remove(k);
            return Task.FromResult(stale.Count);
        }
    }

    private void Evict(DateTimeOffset nowUtc)
    {
        foreach (var stale in _slots.Where(kv => kv.Value.Completed && nowUtc - kv.Value.AtUtc > CompletedTtl)
                                    .Select(kv => kv.Key).ToList())
            _slots.Remove(stale);
    }

    private static string Scope(IdempotencyScope s) =>
        $"{s.TenantId}/{s.PropertyId}/{s.Operation}/{s.Route}/{s.Key}";
}

/// <summary>Preflight tokens, scoped to their issuing tenant.</summary>
public sealed class InMemoryPreflightStore : IPreflightStore
{
    private readonly Dictionary<string, PreflightResult> _tokens = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task SaveAsync(string tenantId, PreflightResult result, CancellationToken ct = default)
    {
        lock (_gate) _tokens[Scope(tenantId, result.Token)] = result;
        return Task.CompletedTask;
    }

    public Task<PreflightResult?> FindAsync(string tenantId, string token, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_tokens.TryGetValue(Scope(tenantId, token), out var r) ? r : null);
    }

    public Task<bool> TryConsumeAsync(string tenantId, string token, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_tokens.Remove(Scope(tenantId, token)));
    }

    public Task<int> EvictExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var dead = _tokens.Where(kv => kv.Value.IsExpired(nowUtc)).Select(kv => kv.Key).ToList();
            foreach (var k in dead) _tokens.Remove(k);
            return Task.FromResult(dead.Count);
        }
    }

    /// <summary>
    /// Tenant-scoped so a token from another tenant is simply not found —
    /// previously any caller could consume, and therefore destroy, someone
    /// else's in-flight token. The property is checked by the scheduling
    /// service against the token's own PropertyId.
    /// </summary>
    private static string Scope(string tenantId, string token) => $"{tenantId}/{token}";
}

public sealed class InMemoryAuditSink : IAuditSink
{
    private readonly List<AuditEntry> _entries = [];
    private readonly object _gate = new();

    /// <summary>
    /// A ring in memory only. Real deployments never drop audit rows; this cap
    /// exists so a demo process does not grow without bound, which is why the
    /// interface is documented as append-only and this implementation is not
    /// a candidate for production.
    /// </summary>
    private const int Cap = 5000;

    public Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Cap) _entries.RemoveRange(0, _entries.Count - Cap);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> RecentAsync(
        string tenantId, string propertyId, int count, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // Walks backwards rather than materialising and reversing the whole
            // list under the lock on every call.
            var found = new List<AuditEntry>(count);
            for (var i = _entries.Count - 1; i >= 0 && found.Count < count; i--)
            {
                var e = _entries[i];
                if (e.TenantId == tenantId && e.PropertyId == propertyId) found.Add(e);
            }
            return Task.FromResult<IReadOnlyList<AuditEntry>>(found);
        }
    }
}
