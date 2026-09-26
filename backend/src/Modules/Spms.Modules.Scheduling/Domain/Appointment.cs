namespace Spms.Modules.Scheduling.Domain;

/// <summary>
/// Everything an appointment is, as one value. Rehydration and snapshots go
/// through this, so adding a field breaks the build at every adapter instead
/// of being silently dropped by one of them.
/// </summary>
public sealed record AppointmentSnapshot(
    string AppointmentId,
    string TenantId,
    string PropertyId,
    string PropertyTimeZone,
    string GuestId,
    string GuestAlias,
    string ServiceId,
    string ServiceName,
    int DurationMinutes,
    string? ProviderId,
    string? RoomId,
    DateTimeOffset StartUtc,
    AppointmentStatus Status,
    int RowVersion,
    string? ConfirmationNumber,
    string CorrelationId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string Source = BookingSource.Desk,
    long PriceMinor = 0,
    string CurrencyCode = "USD",
    string? VisitId = null,
    bool GuestRequestedProvider = false,
    DateTimeOffset? HoldExpiresUtc = null,
    DateTimeOffset? CheckedInUtc = null,
    DateTimeOffset? CancelledUtc = null,
    string? CancellationReasonCode = null,
    DateTimeOffset? CompletedUtc = null,
    string OptionsJson = "[]");

/// <summary>scheduling.appointment.source.</summary>
public static class BookingSource
{
    public const string Desk = "Desk";
    public const string Online = "Online";
    public const string Mobile = "Mobile";
    public const string Phone = "Phone";
    public const string ProviderTablet = "ProviderTablet";
    public const string Marquee = "Marquee";
    public const string Import = "Import";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Desk, Online, Mobile, Phone, ProviderTablet, Marquee, Import };
}

/// <summary>
/// The appointment aggregate.
///
/// Construction goes through <see cref="Create"/> or <see cref="Rehydrate"/>
/// and nothing else, so Status, RowVersion and the resources can only change
/// through the methods that own their rules.
/// </summary>
public sealed class Appointment
{
    private AppointmentSnapshot _s;

    private Appointment(AppointmentSnapshot s) => _s = s;

    public string AppointmentId => _s.AppointmentId;
    public string TenantId => _s.TenantId;
    public string PropertyId => _s.PropertyId;
    public string PropertyTimeZone => _s.PropertyTimeZone;

    /// <summary>Stable identity. Conflict detection must never key on an alias.</summary>
    public string GuestId => _s.GuestId;
    public string GuestAlias => _s.GuestAlias;

    public string ServiceId => _s.ServiceId;
    public string ServiceName => _s.ServiceName;
    public int DurationMinutes => _s.DurationMinutes;

    public string? ProviderId => _s.ProviderId;
    public string? RoomId => _s.RoomId;
    public string? VisitId => _s.VisitId;

    public DateTimeOffset StartUtc => _s.StartUtc;
    public DateTimeOffset EndUtc => _s.StartUtc.AddMinutes(_s.DurationMinutes);

    public AppointmentStatus Status => _s.Status;
    public int RowVersion => _s.RowVersion;

    public string? ConfirmationNumber => _s.ConfirmationNumber;
    public string CorrelationId => _s.CorrelationId;
    public DateTimeOffset CreatedUtc => _s.CreatedUtc;
    public DateTimeOffset UpdatedUtc => _s.UpdatedUtc;

    public string Source => _s.Source;
    public long PriceMinor => _s.PriceMinor;
    public string CurrencyCode => _s.CurrencyCode;
    public bool GuestRequestedProvider => _s.GuestRequestedProvider;
    public DateTimeOffset? HoldExpiresUtc => _s.HoldExpiresUtc;
    public DateTimeOffset? CheckedInUtc => _s.CheckedInUtc;
    public DateTimeOffset? CancelledUtc => _s.CancelledUtc;
    public string? CancellationReasonCode => _s.CancellationReasonCode;
    public DateTimeOffset? CompletedUtc => _s.CompletedUtc;
    public string OptionsJson => _s.OptionsJson;

    /// <summary>Terminal states are not reschedulable: the treatment is history.</summary>
    public bool IsReschedulable =>
        Status is not (AppointmentStatus.Completed or AppointmentStatus.Cancelled or AppointmentStatus.NoShow);

    /// <summary>Whether it still occupies its room and provider (the database's exclusion takes the same view).</summary>
    public bool Occupies => Status is not (AppointmentStatus.Cancelled or AppointmentStatus.NoShow);

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => start < EndUtc && StartUtc < end;

    public AppointmentSnapshot Snapshot() => _s;

    public sealed record NewAppointment(
        string AppointmentId, string TenantId, string PropertyId, string PropertyTimeZone,
        string GuestId, string GuestAlias,
        string ServiceId, string ServiceName, int DurationMinutes,
        string? ProviderId, string? RoomId,
        DateTimeOffset StartUtc, string? ConfirmationNumber,
        string CorrelationId, DateTimeOffset NowUtc,
        AppointmentStatus InitialStatus = AppointmentStatus.Confirmed,
        DateTimeOffset? HoldExpiresUtc = null,
        string Source = BookingSource.Desk,
        long PriceMinor = 0,
        string CurrencyCode = "USD",
        string? VisitId = null,
        bool GuestRequestedProvider = false,
        string OptionsJson = "[]");

    /// <summary>A new booking: Confirmed from the desk, or Held (with an expiry) for an online slot hold.</summary>
    public static Appointment Create(NewAppointment n)
    {
        // A zero or negative duration makes EndUtc <= StartUtc, which turns
        // off overlap detection for this row entirely. Refuse it at the door.
        if (n.DurationMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(n), "A service must have a positive duration.");
        if (n.InitialStatus is not (AppointmentStatus.Confirmed or AppointmentStatus.Held))
            throw new ArgumentOutOfRangeException(nameof(n), "A booking starts Confirmed or Held.");
        if ((n.InitialStatus == AppointmentStatus.Held) != (n.HoldExpiresUtc is not null))
            throw new ArgumentException("A Held booking needs an expiry, and only a Held booking has one.", nameof(n));
        if (!BookingSource.All.Contains(n.Source))
            throw new ArgumentOutOfRangeException(nameof(n), $"Unknown booking source {n.Source}.");

        return new Appointment(new AppointmentSnapshot(
            n.AppointmentId, n.TenantId, n.PropertyId, n.PropertyTimeZone, n.GuestId, n.GuestAlias,
            n.ServiceId, n.ServiceName, n.DurationMinutes, n.ProviderId, n.RoomId,
            n.StartUtc.ToUniversalTime(), n.InitialStatus, RowVersion: 1,
            n.ConfirmationNumber, n.CorrelationId, n.NowUtc, n.NowUtc,
            n.Source, n.PriceMinor, n.CurrencyCode, n.VisitId, n.GuestRequestedProvider,
            n.HoldExpiresUtc?.ToUniversalTime(), OptionsJson: n.OptionsJson));
    }

    /// <summary>Rebuilds a stored aggregate from every field.</summary>
    public static Appointment Rehydrate(AppointmentSnapshot s) => new(s);

    /// <summary>A copy, so callers cannot mutate stored state.</summary>
    public Appointment Copy() => new(_s);

    /// <summary>A copy at a different property. Test and fixture use only.</summary>
    public Appointment CopyToProperty(string propertyId) => new(_s with { PropertyId = propertyId });

    public void ApplyMove(DateTimeOffset startUtc, Assignment assignment, DateTimeOffset nowUtc)
    {
        if (!IsReschedulable)
            throw new InvalidOperationException($"A {Status} appointment cannot be rescheduled.");

        _s = _s with
        {
            StartUtc = startUtc.ToUniversalTime(),
            ProviderId = assignment.ProviderChanged ? assignment.ProviderId : _s.ProviderId,
            RoomId = assignment.RoomChanged ? assignment.RoomId : _s.RoomId,
            RowVersion = _s.RowVersion + 1,
            UpdatedUtc = nowUtc,
        };
    }

    /// <summary>
    /// The state machine is enforced here, not in the endpoint, and the
    /// timestamps the schema ties to a status (checked_in_at, cancelled_at,
    /// completed_at, hold_expires_at) move with it — the database's CHECKs
    /// refuse a Cancelled row without cancelled_at, so the two cannot disagree.
    /// </summary>
    public void ApplyTransition(AppointmentStatus to, DateTimeOffset nowUtc, string? reasonCode = null)
    {
        if (!AppointmentTransitions.CanMove(Status, to))
            throw new IllegalTransitionException(Status, to);

        _s = _s with
        {
            Status = to,
            RowVersion = _s.RowVersion + 1,
            UpdatedUtc = nowUtc,
            HoldExpiresUtc = to == AppointmentStatus.Held ? _s.HoldExpiresUtc : null,
            CheckedInUtc = to == AppointmentStatus.CheckedIn ? nowUtc : _s.CheckedInUtc,
            CancelledUtc = to == AppointmentStatus.Cancelled ? nowUtc : _s.CancelledUtc,
            CancellationReasonCode = to == AppointmentStatus.Cancelled ? reasonCode : _s.CancellationReasonCode,
            CompletedUtc = to == AppointmentStatus.Completed ? nowUtc : _s.CompletedUtc,
        };
    }

    /// <summary>Attaches the booking to a visit (the day plan).</summary>
    public void AttachToVisit(string visitId, DateTimeOffset nowUtc)
    {
        _s = _s with { VisitId = visitId, RowVersion = _s.RowVersion + 1, UpdatedUtc = nowUtc };
    }
}

/// <summary>
/// A resource change with tri-state semantics. A plain nullable pair could not
/// distinguish "leave the room alone" from "unassign the room".
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

/// <summary>
/// Raised when a write violated the database's own room-overlap constraint
/// (CON-002, SQLSTATE 23P01), so the domain answers with a conflict rather
/// than a driver exception reaching the 500 handler.
/// </summary>
public sealed class RoomOverlapException(string? roomId, Exception? inner = null)
    : Exception($"The database refused an overlapping booking for room {roomId ?? "(none)"}.", inner)
{
    public string? RoomId { get; } = roomId;
}
