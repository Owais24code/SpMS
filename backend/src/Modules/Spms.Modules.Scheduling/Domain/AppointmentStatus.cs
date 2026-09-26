namespace Spms.Modules.Scheduling.Domain;

/// <summary>
/// Appointment lifecycle (SPECIFICATION_v2.12.md §7.1), exactly the values of
/// scheduling.appointment.status. There is no Draft: an online slot hold is
/// Held (with an expiry) and a desk booking starts Confirmed.
/// </summary>
public enum AppointmentStatus
{
    Held, Confirmed, CheckedIn, Ready, InService, Completed, Cancelled, NoShow,
}

public static class AppointmentTransitions
{
    /// <summary>
    /// The only legal moves. Anything absent here is rejected rather than
    /// warned about — an appointment that jumps from Held to Completed has
    /// skipped intake, payment and arrival, and the audit trail would lie.
    /// </summary>
    private static readonly Dictionary<AppointmentStatus, AppointmentStatus[]> Allowed = new()
    {
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

    public static bool IsTerminal(AppointmentStatus s) => NextFrom(s).Count == 0;
}
