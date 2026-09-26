namespace Spms.Modules.Scheduling.Domain;

/// <summary>
/// Storage ports for the scheduling rules. The EF adapters over
/// scheduling.appointment satisfy these in the host; in-memory doubles satisfy
/// them in the fast test suite.
///
/// Every read is scoped by tenant AND property. Scoping by tenant alone let an
/// operator at one property read and move another's appointments (ADR 003).
/// The query filters and row-level security scope again underneath, so a
/// wrong argument here fails closed rather than leaking.
/// </summary>
public interface IAppointmentRepository
{
    Task<Appointment?> GetAsync(string tenantId, string propertyId, string appointmentId, CancellationToken ct = default);

    /// <summary>
    /// Everything whose interval overlaps [fromUtc, toUtc) — an interval query,
    /// not a day query, so an appointment running past midnight is seen.
    /// </summary>
    Task<IReadOnlyList<Appointment>> ListOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    /// <summary>One page of the same interval query, paged in the store.</summary>
    Task<IReadOnlyList<Appointment>> ListPageAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int offset, int limit, CancellationToken ct = default);

    Task<int> CountOverlappingAsync(
        string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    /// <summary>
    /// Inserts, or returns false if that id is already taken. Throws
    /// <see cref="RoomOverlapException"/> when the room exclusion refuses it.
    /// </summary>
    Task<bool> TryAddAsync(Appointment appointment, CancellationToken ct = default);

    /// <summary>
    /// Persists only if the stored RowVersion still equals expectedRowVersion,
    /// so the caller can answer 412 rather than overwrite a concurrent edit.
    /// </summary>
    Task<bool> TryUpdateAsync(Appointment appointment, int expectedRowVersion, CancellationToken ct = default);

    /// <summary>Tenant-wide: a confirmation number is quoted at any of a tenant's desks.</summary>
    Task<bool> ConfirmationNumberExistsAsync(string tenantId, string confirmationNumber, CancellationToken ct = default);

    /// <summary>
    /// The guest's busy intervals across EVERY property of the tenant (CON-005
    /// is tenant-wide). Intervals only — no detail of another property's booking.
    /// </summary>
    Task<IReadOnlyList<GuestBusyInterval>> GuestBusyAsync(
        string tenantId, string guestId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);
}

public sealed record GuestBusyInterval(string PropertyId, string AppointmentId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public interface IPreflightStore
{
    Task SaveAsync(string tenantId, PreflightResult result, CancellationToken ct = default);

    /// <summary>Reads without consuming, so a recoverable refusal leaves the token usable.</summary>
    Task<PreflightResult?> FindAsync(string tenantId, string token, CancellationToken ct = default);

    /// <summary>Consumes the token. Returns false if someone else took it first.</summary>
    Task<bool> TryConsumeAsync(string tenantId, string token, DateTimeOffset nowUtc, DateTimeOffset? undoUntilUtc, string? reason, CancellationToken ct = default);

    /// <summary>Marks expired tokens Expired and returns how many.</summary>
    Task<int> EvictExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
}
