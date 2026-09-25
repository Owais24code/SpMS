using Spms.Domain.Scheduling;

namespace Spms.Domain.Abstractions;

/// <summary>
/// Storage ports. In-memory adapters satisfy these today; EF Core against
/// PostgreSQL replaces them.
///
/// Every read is scoped by tenant AND property. Scoping by tenant alone let an
/// operator at one property read and move another's appointments (ADR 003).
/// </summary>
public interface IAppointmentRepository
{
    Task<Appointment?> GetAsync(string tenantId, string propertyId, string appointmentId, CancellationToken ct = default);

    /// <summary>
    /// Everything whose interval overlaps [fromUtc, toUtc).
    ///
    /// Deliberately an interval query, not a day query. A day query missed any
    /// appointment that started before midnight and ran past it, so a move
    /// across the boundary saw an empty board and double-booked silently.
    /// </summary>
    Task<IReadOnlyList<Appointment>> ListOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    /// <summary>
    /// One page of the same interval query, paged in the store rather than in
    /// memory. Fetching a whole 62-day window and slicing it afterwards paid
    /// the full server-side cost the paging exists to avoid.
    /// </summary>
    Task<IReadOnlyList<Appointment>> ListPageAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int offset, int limit, CancellationToken ct = default);

    Task<int> CountOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    /// <summary>
    /// Inserts, or returns false if that id is already taken.
    ///
    /// Throws <see cref="RoomOverlapException"/> when the store's own
    /// room-overlap constraint refuses the row — CON-002 is enforced in the
    /// database, so the adapter has to hand that refusal back as a domain
    /// outcome rather than let a driver exception escape.
    /// </summary>
    Task<bool> TryAddAsync(Appointment appointment, CancellationToken ct = default);

    /// <summary>
    /// Persists only if the stored RowVersion still equals expectedRowVersion,
    /// so the caller can answer 412 rather than overwrite a concurrent edit.
    /// </summary>
    Task<bool> TryUpdateAsync(Appointment appointment, int expectedRowVersion, CancellationToken ct = default);

    /// <summary>
    /// Tenant-wide by design: a confirmation number is quoted at any of a
    /// tenant's desks, so uniqueness cannot be per property.
    /// </summary>
    Task<bool> ConfirmationNumberExistsAsync(string tenantId, string confirmationNumber, CancellationToken ct = default);
}

public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims the key. Returns Reserved on a first arrival,
    /// Replay with the stored response on a repeat of the same body,
    /// Mismatch when the body differs, InFlight when another request holds it.
    ///
    /// A read-then-write check let two concurrent requests with one key both
    /// proceed, which is the exact case the key exists to prevent.
    /// </summary>
    Task<IdempotencyClaim> ClaimAsync(IdempotencyScope scope, string requestHash, DateTimeOffset nowUtc, CancellationToken ct = default);

    Task CompleteAsync(IdempotencyScope scope, int statusCode, string responseJson, DateTimeOffset nowUtc, CancellationToken ct = default);

    /// <summary>Releases a reservation whose request failed, so a retry is possible.</summary>
    Task AbandonAsync(IdempotencyScope scope, CancellationToken ct = default);

    /// <summary>Drops completed records past their retention. Returns how many.</summary>
    Task<int> SweepAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
}

/// <summary>
/// What an idempotency key is scoped to.
///
/// PropertyId is part of it: scoped to tenant and operation alone, the same
/// client-chosen key arriving at two properties replayed the first property's
/// response — id, guest alias, room and confirmation number — to the second,
/// which then owned nothing. Route is part of it because the route id is
/// load-bearing for a reassign, so two reassigns of different appointments
/// with one key and identical bodies replayed each other.
/// </summary>
public sealed record IdempotencyScope(
    string TenantId, string PropertyId, string Operation, string Route, string Key);

public enum IdempotencyOutcome { Reserved, Replay, Mismatch, InFlight }

public sealed record IdempotencyClaim(IdempotencyOutcome Outcome, int StatusCode = 0, string? ResponseJson = null);

public interface IPreflightStore
{
    Task SaveAsync(string tenantId, PreflightResult result, CancellationToken ct = default);

    /// <summary>Reads without consuming, so a recoverable refusal leaves the token usable.</summary>
    Task<PreflightResult?> FindAsync(string tenantId, string token, CancellationToken ct = default);

    /// <summary>Consumes the token. Returns false if someone else took it first.</summary>
    Task<bool> TryConsumeAsync(string tenantId, string token, CancellationToken ct = default);

    /// <summary>
    /// Drops expired tokens and returns how many. Called opportunistically;
    /// without it the store grows forever. Async because against a database it
    /// is a DELETE, and a synchronous one would block a request thread.
    /// </summary>
    Task<int> EvictExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
}

public interface IAuditSink
{
    Task RecordAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Scoped by tenant AND property, like every other read. Tenant-only
    /// scoping let an admin at one property read every other property's
    /// actor names, subject ids and correlation ids.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> RecentAsync(
        string tenantId, string propertyId, int count, CancellationToken ct = default);
}

/// <summary>
/// Append-only audit record.
///
/// BeforeHash/AfterHash are SHA-256 over the serialized state rather than the
/// values themselves, so the trail cannot become a second copy of restricted
/// data while still proving what changed (§54.3).
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset AtUtc,
    string TenantId,
    string PropertyId,
    string Actor,
    string Action,
    string Purpose,
    string SubjectType,
    string SubjectId,
    int SubjectVersion,
    string? BeforeHash,
    string? AfterHash,
    IReadOnlyList<string> ConflictCodes,
    /// <summary>Which of the offered conflict alternatives the operator took.</summary>
    string? SelectedResolution,
    /// <summary>
    /// The status a transition moved to. Its own field, because writing it
    /// into SelectedResolution made the trail claim a resolution was chosen
    /// where none was offered.
    /// </summary>
    string? TargetStatus,
    string? Reason,
    string CorrelationId);

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
