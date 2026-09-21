namespace Spms.Domain.Scheduling;

/// <summary>Appointment lifecycle, SPECIFICATION_v2.12.md §7.1.</summary>
public enum AppointmentStatus
{
    Draft, Held, Confirmed, CheckedIn, Ready, InService, Completed, Cancelled, NoShow,
}

/// <summary>
/// Which transitions are legal. Enforced in the domain rather than the
/// endpoint so every caller — HTTP, queue consumer, migration — obeys it.
/// </summary>
public static class AppointmentTransitions
{
    private static readonly Dictionary<AppointmentStatus, AppointmentStatus[]> Allowed = new()
    {
        [AppointmentStatus.Draft]     = [AppointmentStatus.Held, AppointmentStatus.Confirmed, AppointmentStatus.Cancelled],
        [AppointmentStatus.Held]      = [AppointmentStatus.Confirmed, AppointmentStatus.Cancelled],
        [AppointmentStatus.Confirmed] = [AppointmentStatus.CheckedIn, AppointmentStatus.Cancelled, AppointmentStatus.NoShow],
        [AppointmentStatus.CheckedIn] = [AppointmentStatus.Ready, AppointmentStatus.Cancelled],
        [AppointmentStatus.Ready]     = [AppointmentStatus.InService, AppointmentStatus.Cancelled],
        [AppointmentStatus.InService] = [AppointmentStatus.Completed],
        [AppointmentStatus.Completed] = [],
        [AppointmentStatus.Cancelled] = [],
        [AppointmentStatus.NoShow]    = [],
    };

    public static bool CanMove(AppointmentStatus from, AppointmentStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static IReadOnlyList<AppointmentStatus> NextFrom(AppointmentStatus from) =>
        Allowed.TryGetValue(from, out var next) ? next : [];
}

public sealed record Appointment
{
    public required Guid AppointmentId { get; init; }
    public required string TenantId { get; init; }
    public required string PropertyId { get; init; }
    public required string GuestAlias { get; init; }
    public required string ServiceCode { get; init; }
    public required string ProviderId { get; init; }
    public required string RoomId { get; init; }

    /// <summary>Always UTC. Local presentation uses the property's IANA zone.</summary>
    public required DateTimeOffset StartUtc { get; init; }
    public required int DurationMinutes { get; init; }

    public DateTimeOffset EndUtc => StartUtc.AddMinutes(DurationMinutes);

    public AppointmentStatus Status { get; init; } = AppointmentStatus.Draft;

    /// <summary>Optimistic concurrency token, surfaced as an ETag.</summary>
    public required int RowVersion { get; init; }

    public string ETag => $"\"v{RowVersion}\"";
}
