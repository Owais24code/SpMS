namespace Spms.Domain.Scheduling;

/// <summary>
/// The appointment aggregate for the R1 scheduling slice.
///
/// Construction goes through <see cref="Create"/> or <see cref="Rehydrate"/>
/// and nothing else. The previous shape — a public object initialiser plus
/// public Seed() and AssignResources() — advertised private setters while
/// leaving Status, RowVersion and the resources writable by anyone: Seed could
/// reset RowVersion downwards, which makes a stale writer win, and correct
/// rehydration was a three-call incantation hand-copied in four places.
/// Adding a field to this class now breaks the build instead of the data.
/// </summary>
public sealed class Appointment
{
    private Appointment() { }

    public string AppointmentId { get; private init; } = string.Empty;
    public string TenantId { get; private init; } = string.Empty;
    public string PropertyId { get; private init; } = string.Empty;
    public string PropertyTimeZone { get; private init; } = string.Empty;

    /// <summary>Stable identity. Conflict detection must never key on an alias.</summary>
    public string GuestId { get; private init; } = string.Empty;
    public string GuestAlias { get; private init; } = string.Empty;

    public string ServiceId { get; private init; } = string.Empty;
    public string ServiceName { get; private init; } = string.Empty;
    public int DurationMinutes { get; private init; }

    public string? ProviderId { get; private set; }
    public string? RoomId { get; private set; }

    public DateTimeOffset StartUtc { get; private set; }
    public DateTimeOffset EndUtc => StartUtc.AddMinutes(DurationMinutes);

    public AppointmentStatus Status { get; private set; } = AppointmentStatus.Draft;

    public int RowVersion { get; private set; } = 1;

    public string? ConfirmationNumber { get; private init; }
    public string CorrelationId { get; private init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private init; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    /// <summary>Terminal states are not reschedulable: the treatment is history.</summary>
    public bool IsReschedulable =>
        Status is not (AppointmentStatus.Completed or AppointmentStatus.Cancelled or AppointmentStatus.NoShow);

    /// <summary>
    /// Whether this appointment's interval intersects [start, end). The
    /// definition the whole conflict register turns on, so it lives here
    /// rather than being rewritten at each call site.
    /// </summary>
    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => start < EndUtc && StartUtc < end;

    /// <summary>A new booking. Always starts at Draft, version 1.</summary>
    public static Appointment Create(
        string appointmentId, string tenantId, string propertyId, string propertyTimeZone,
        string guestId, string guestAlias,
        string serviceId, string serviceName, int durationMinutes,
        string? providerId, string? roomId,
        DateTimeOffset startUtc, string? confirmationNumber,
        string correlationId, DateTimeOffset nowUtc)
    {
        // A zero or negative duration makes EndUtc <= StartUtc, which turns
        // off overlap detection for this row entirely. Refuse it at the door.
        if (durationMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationMinutes), "A service must have a positive duration.");

        return new Appointment
        {
            AppointmentId = appointmentId, TenantId = tenantId, PropertyId = propertyId,
            PropertyTimeZone = propertyTimeZone, GuestId = guestId, GuestAlias = guestAlias,
            ServiceId = serviceId, ServiceName = serviceName, DurationMinutes = durationMinutes,
            ProviderId = providerId, RoomId = roomId,
            StartUtc = startUtc.ToUniversalTime(), ConfirmationNumber = confirmationNumber,
            CorrelationId = correlationId, CreatedUtc = nowUtc, UpdatedUtc = nowUtc,
            Status = AppointmentStatus.Draft, RowVersion = 1,
        };
    }

    /// <summary>
    /// Rebuilds a stored aggregate. Takes every field, including the ones
    /// Create derives, so a storage adapter cannot silently drop one.
    /// </summary>
    public static Appointment Rehydrate(
        string appointmentId, string tenantId, string propertyId, string propertyTimeZone,
        string guestId, string guestAlias,
        string serviceId, string serviceName, int durationMinutes,
        string? providerId, string? roomId,
        DateTimeOffset startUtc, AppointmentStatus status, int rowVersion,
        string? confirmationNumber, string correlationId,
        DateTimeOffset createdUtc, DateTimeOffset updatedUtc) =>
        new()
        {
            AppointmentId = appointmentId, TenantId = tenantId, PropertyId = propertyId,
            PropertyTimeZone = propertyTimeZone, GuestId = guestId, GuestAlias = guestAlias,
            ServiceId = serviceId, ServiceName = serviceName, DurationMinutes = durationMinutes,
            ProviderId = providerId, RoomId = roomId,
            StartUtc = startUtc, Status = status, RowVersion = rowVersion,
            ConfirmationNumber = confirmationNumber, CorrelationId = correlationId,
            CreatedUtc = createdUtc, UpdatedUtc = updatedUtc,
        };

    /// <summary>A field-for-field copy, so callers cannot mutate stored state.</summary>
    public Appointment Copy() => Rehydrate(
        AppointmentId, TenantId, PropertyId, PropertyTimeZone, GuestId, GuestAlias,
        ServiceId, ServiceName, DurationMinutes, ProviderId, RoomId,
        StartUtc, Status, RowVersion, ConfirmationNumber, CorrelationId, CreatedUtc, UpdatedUtc);

    /// <summary>A copy at a different property. Test and fixture use only.</summary>
    public Appointment CopyToProperty(string propertyId) => Rehydrate(
        AppointmentId, TenantId, propertyId, PropertyTimeZone, GuestId, GuestAlias,
        ServiceId, ServiceName, DurationMinutes, ProviderId, RoomId,
        StartUtc, Status, RowVersion, ConfirmationNumber, CorrelationId, CreatedUtc, UpdatedUtc);

    public void ApplyMove(DateTimeOffset startUtc, Assignment assignment, DateTimeOffset nowUtc)
    {
        if (!IsReschedulable)
            throw new InvalidOperationException($"A {Status} appointment cannot be rescheduled.");

        StartUtc = startUtc.ToUniversalTime();
        if (assignment.ProviderChanged) ProviderId = assignment.ProviderId;
        if (assignment.RoomChanged) RoomId = assignment.RoomId;
        RowVersion += 1;
        UpdatedUtc = nowUtc;
    }

    /// <summary>
    /// The state machine is enforced here, not in the endpoint. It was
    /// previously one `if` in the HTTP layer, so any second caller — the
    /// command bus, a batch import — could move Draft straight to Completed.
    /// </summary>
    public void ApplyTransition(AppointmentStatus to, DateTimeOffset nowUtc)
    {
        if (!AppointmentTransitions.CanMove(Status, to))
            throw new IllegalTransitionException(Status, to);

        Status = to;
        RowVersion += 1;
        UpdatedUtc = nowUtc;
    }
}

/// <summary>
/// A resource change with tri-state semantics. A plain nullable pair could not
/// distinguish "leave the room alone" from "unassign the room", so unassigning
/// was impossible through the API with no error to say so.
/// </summary>
public sealed record Assignment(bool ProviderChanged, string? ProviderId, bool RoomChanged, string? RoomId)
{
    public static readonly Assignment Unchanged = new(false, null, false, null);

    /// <summary>Sets whichever of the two was supplied; a null means "leave it".</summary>
    public static Assignment Set(string? providerId, string? roomId) =>
        new(providerId is not null, providerId, roomId is not null, roomId);
}

public sealed class IllegalTransitionException(AppointmentStatus from, AppointmentStatus to)
    : InvalidOperationException($"{from} cannot move to {to}.")
{
    public AppointmentStatus From { get; } = from;
    public AppointmentStatus To { get; } = to;
}
