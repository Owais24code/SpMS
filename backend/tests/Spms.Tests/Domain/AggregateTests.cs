using Spms.Modules.Scheduling.Domain;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Domain;

public class StateMachineTests
{
    [Fact]
    public void A_hold_can_be_confirmed() =>
        Assert.True(AppointmentTransitions.CanMove(AppointmentStatus.Held, AppointmentStatus.Confirmed));

    [Fact]
    public void A_hold_cannot_jump_straight_to_completed() =>
        Assert.False(AppointmentTransitions.CanMove(AppointmentStatus.Held, AppointmentStatus.Completed));

    [Fact]
    public void There_is_no_draft_status()
    {
        // The R1 schema has no Draft: an online slot hold is Held (with an
        // expiry) and a desk booking starts Confirmed. The enum must be exactly
        // the values the scheduling.appointment CHECK allows.
        Assert.Equal(
            ["Held", "Confirmed", "CheckedIn", "Ready", "InService", "Completed", "Cancelled", "NoShow"],
            Enum.GetNames<AppointmentStatus>());
    }

    [Fact]
    public void Completed_is_terminal() =>
        Assert.Empty(AppointmentTransitions.NextFrom(AppointmentStatus.Completed));

    [Fact]
    public void Cancelled_cannot_be_revived() =>
        Assert.False(AppointmentTransitions.CanMove(AppointmentStatus.Cancelled, AppointmentStatus.Confirmed));

    [Fact]
    public void Every_status_has_a_transition_entry()
    {
        // A status added to the enum but not the table would silently allow
        // nothing, which reads as a data bug rather than a missing rule.
        foreach (var s in Enum.GetValues<AppointmentStatus>())
            Assert.NotNull(AppointmentTransitions.NextFrom(s));
    }

    [Fact]
    public void The_aggregate_refuses_an_illegal_transition_itself()
    {
        // Enforcement used to be one `if` in the HTTP endpoint, so any second
        // caller could move a hold straight to Completed.
        var a = SchedulingWorld.Appointment("t", AppointmentStatus.Held, DateTimeOffset.UtcNow, 60);
        Assert.Throws<IllegalTransitionException>(() =>
            a.ApplyTransition(AppointmentStatus.Completed, DateTimeOffset.UtcNow));
        Assert.Equal(AppointmentStatus.Held, a.Status);
        Assert.Equal(1, a.RowVersion);
    }

    [Theory]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    public void Terminal_statuses_are_not_reschedulable(AppointmentStatus status) =>
        Assert.False(SchedulingWorld.Appointment("x", status, DateTimeOffset.UtcNow, 60).IsReschedulable);

    [Theory]
    [InlineData(AppointmentStatus.Held)]
    [InlineData(AppointmentStatus.Confirmed)]
    [InlineData(AppointmentStatus.InService)]
    public void Live_statuses_are_reschedulable(AppointmentStatus status) =>
        Assert.True(SchedulingWorld.Appointment("x", status, DateTimeOffset.UtcNow, 60).IsReschedulable);

    [Fact]
    public void The_aggregate_refuses_to_move_a_terminal_appointment()
    {
        var a = SchedulingWorld.Appointment("x", AppointmentStatus.Cancelled, DateTimeOffset.UtcNow, 60);
        Assert.Throws<InvalidOperationException>(() =>
            a.ApplyMove(DateTimeOffset.UtcNow.AddHours(1), Assignment.Unchanged, DateTimeOffset.UtcNow));
    }
}

public class AggregateTests
{
    [Fact]
    public void A_non_positive_duration_is_refused_at_construction()
    {
        // A zero duration makes EndUtc <= StartUtc, which turns off overlap
        // detection for that row entirely — and the database rejects it too.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Appointment.Create(new Appointment.NewAppointment("z", "t", "p", "UTC", "g", "G", "svc-peel", "Peel", 0,
                null, null, DateTimeOffset.UtcNow, null, "c", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void A_hold_needs_an_expiry_and_nothing_else_has_one()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => Appointment.Create(new Appointment.NewAppointment(
            "z", "t", "p", "UTC", "g", "G", "s", "S", 30, null, null, now, null, "c", now, InitialStatus: AppointmentStatus.Held)));
        Assert.Throws<ArgumentException>(() => Appointment.Create(new Appointment.NewAppointment(
            "z", "t", "p", "UTC", "g", "G", "s", "S", 30, null, null, now, null, "c", now, HoldExpiresUtc: now.AddMinutes(5))));
    }

    [Fact]
    public void Leaving_a_hold_clears_its_expiry_and_status_timestamps_follow_the_status()
    {
        // The schema's CHECKs tie hold_expires_at, checked_in_at, cancelled_at
        // and completed_at to the status; the aggregate moves them together.
        var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var a = SchedulingWorld.Appointment("x", AppointmentStatus.Held, now, 60);
        Assert.NotNull(a.HoldExpiresUtc);

        a.ApplyTransition(AppointmentStatus.Confirmed, now);
        Assert.Null(a.HoldExpiresUtc);

        a.ApplyTransition(AppointmentStatus.CheckedIn, now.AddMinutes(1));
        Assert.Equal(now.AddMinutes(1), a.CheckedInUtc);

        a.ApplyTransition(AppointmentStatus.Cancelled, now.AddMinutes(2), "GuestRequest");
        Assert.Equal(now.AddMinutes(2), a.CancelledUtc);
        Assert.Equal("GuestRequest", a.CancellationReasonCode);
    }

    [Fact]
    public void An_assignment_distinguishes_unchanged_from_cleared()
    {
        var now = DateTimeOffset.UtcNow;
        var a = SchedulingWorld.Appointment("x", AppointmentStatus.Confirmed, now, 60, "prov-lena", "room-1");

        a.ApplyMove(now.AddHours(1), Assignment.Unchanged, now);
        Assert.Equal("prov-lena", a.ProviderId);
        Assert.Equal("room-1", a.RoomId);

        // A nullable pair could not express this, so unassigning was
        // impossible through the API with no error to say so.
        a.ApplyMove(now.AddHours(2), new Assignment(ProviderChanged: true, null, RoomChanged: false, null), now);
        Assert.Null(a.ProviderId);
        Assert.Equal("room-1", a.RoomId);
    }

    [Fact]
    public void Overlaps_is_the_one_definition_of_an_intersection()
    {
        var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var a = SchedulingWorld.Appointment("x", AppointmentStatus.Confirmed, now, 60);

        Assert.True(a.Overlaps(now.AddMinutes(30), now.AddMinutes(90)));
        Assert.False(a.Overlaps(now.AddMinutes(60), now.AddMinutes(120)));   // touching is not overlapping
        Assert.False(a.Overlaps(now.AddMinutes(-60), now));                  // touching on the left either
    }

    [Fact]
    public void ApplyMove_bumps_the_row_version_itself()
    {
        var now = DateTimeOffset.UtcNow;
        var a = SchedulingWorld.Appointment("x", AppointmentStatus.Confirmed, now, 60, "prov-lena", "room-1");
        Assert.Equal(1, a.RowVersion);
        a.ApplyMove(now.AddHours(1), Assignment.Unchanged, now);
        Assert.Equal(2, a.RowVersion);
    }

    [Fact]
    public void State_hashes_ignore_the_row_version()
    {
        var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var a = SchedulingWorld.Appointment("h", AppointmentStatus.Confirmed, now, 60, "prov-lena", "room-1");

        var same = Appointment.Rehydrate(a.Snapshot() with { RowVersion = 47 });

        // RowVersion used to be inside the canonical string, so the two hashes
        // always differed and could not prove anything substantive had changed.
        Assert.Equal(StateHash.Of(a), StateHash.Of(same));
    }

    [Fact]
    public void State_hashes_change_when_the_state_does()
    {
        var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var a = SchedulingWorld.Appointment("h", AppointmentStatus.Confirmed, now, 60, "prov-lena", "room-1");
        var before = StateHash.Of(a);

        a.ApplyMove(now.AddMinutes(30), Assignment.Unchanged, now);
        Assert.NotEqual(before, StateHash.Of(a));
    }
}
