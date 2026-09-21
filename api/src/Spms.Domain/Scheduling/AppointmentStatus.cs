namespace Spms.Domain.Scheduling;

/// <summary>Appointment lifecycle from SPECIFICATION_v2.12.md §7.1.</summary>
public enum AppointmentStatus
{
    Draft, Held, Confirmed, CheckedIn, Ready, InService, Completed, Cancelled, NoShow,
}

public static class AppointmentTransitions
{
    /// <summary>
    /// The only legal moves. Anything absent here is rejected rather than
    /// warned about — an appointment that jumps from Draft to Completed has
    /// skipped intake, payment and arrival, and the audit trail would lie.
    /// </summary>
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
