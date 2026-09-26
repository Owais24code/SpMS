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

    /// <summary>
    /// Defers the room exclusion (CON-002) to <see cref="CheckRoomExclusionAsync"/>
    /// for the rest of the transaction, so a bulk move can swap two rooms.
    /// </summary>
    Task DeferRoomExclusionAsync(CancellationToken ct = default);

    /// <summary>Checks the deferred room exclusion now. Throws <see cref="RoomOverlapException"/>.</summary>
    Task CheckRoomExclusionAsync(CancellationToken ct = default);

    /// <summary>Held bookings whose hold ran out, oldest first.</summary>
    Task<IReadOnlyList<Appointment>> ListExpiredHoldsAsync(
        string tenantId, string propertyId, DateTimeOffset nowUtc, int limit, CancellationToken ct = default);
}

/// <summary>Where an appointment sits: start, provider, room. A null means none.</summary>
public sealed record Placement(DateTimeOffset StartUtc, string? ProviderId, string? RoomId);

/// <summary>A committed reassign as the undo path needs it (CON-006).</summary>
public sealed record CommittedMove(
    string AppointmentId, string PropertyId, int CommittedRowVersion, Placement Previous,
    DateTimeOffset? UndoUntilUtc, DateTimeOffset? UndoneUtc);

/// <summary>
/// What else changes when an appointment changes state, in the same
/// transaction: the visit arrives when its first guest checks in, and a
/// completed treatment leaves its room needing a turnover.
/// </summary>
public interface ISchedulingEffects
{
    Task TransitionedAsync(Appointment after, AppointmentStatus from, BufferPolicy buffers, DateTimeOffset nowUtc, CancellationToken ct = default);
}

/// <summary>
/// Another module's reaction to an appointment changing state, in the same
/// transaction — commerce forfeits a deposit on a no-show. Registered by the
/// modules above scheduling; the scheduling effects call every one.
/// </summary>
public interface ISchedulingObserver
{
    Task TransitionedAsync(Appointment after, AppointmentStatus from, DateTimeOffset nowUtc, CancellationToken ct = default);
}

/// <summary>
/// The desk's deposit readiness for its arrivals: NotRequired, Pending or
/// Settled. Commerce owns deposits and implements it; without commerce the
/// answer is NotTracked.
/// </summary>
public interface IDepositStatus
{
    Task<IReadOnlyDictionary<Guid, string>> ForAppointmentsAsync(IReadOnlyCollection<Guid> appointmentIds, CancellationToken ct = default);
}

public sealed class NoSchedulingEffects : ISchedulingEffects
{
    public static readonly NoSchedulingEffects Instance = new();
    public Task TransitionedAsync(Appointment after, AppointmentStatus from, BufferPolicy buffers, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Task.CompletedTask;
}

public sealed class SchedulingOptions
{
    /// <summary>CON-006 undo window for a committed reassign. Zero turns undo off.</summary>
    public int UndoWindowSeconds { get; set; } = 120;
    /// <summary>The most appointments one bulk move may carry.</summary>
    public int BulkMoveLimit { get; set; } = 50;
}

public sealed record GuestBusyInterval(string PropertyId, string AppointmentId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public interface IPreflightStore
{
    Task SaveAsync(string tenantId, PreflightResult result, CancellationToken ct = default);

    /// <summary>Reads without consuming, so a recoverable refusal leaves the token usable.</summary>
    Task<PreflightResult?> FindAsync(string tenantId, string token, CancellationToken ct = default);

    /// <summary>
    /// Consumes the token, recording where the appointment was so the move can
    /// be undone. Returns false if someone else took it first.
    /// </summary>
    Task<bool> TryConsumeAsync(string tenantId, string token, DateTimeOffset nowUtc, DateTimeOffset? undoUntilUtc,
        string? reason, Placement? previous, CancellationToken ct = default);

    /// <summary>A committed token, for undo. Null when unknown or never committed.</summary>
    Task<CommittedMove?> FindCommittedAsync(string tenantId, string token, CancellationToken ct = default);

    /// <summary>Marks the move undone. False if it already was (an undo happens once).</summary>
    Task<bool> TryMarkUndoneAsync(string tenantId, string token, DateTimeOffset nowUtc, CancellationToken ct = default);

    /// <summary>Marks expired tokens Expired and returns how many.</summary>
    Task<int> EvictExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
}
