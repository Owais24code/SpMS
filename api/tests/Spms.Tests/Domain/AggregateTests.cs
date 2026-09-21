using Spms.Domain.Scheduling;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Domain;

public class StateMachineTests
{
    [Fact]
    public void Draft_can_be_confirmed() =>
        Assert.True(AppointmentTransitions.CanMove(AppointmentStatus.Draft, AppointmentStatus.Confirmed));

    [Fact]
    public void Draft_cannot_jump_straight_to_completed() =>
        Assert.False(AppointmentTransitions.CanMove(AppointmentStatus.Draft, AppointmentStatus.Completed));

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
        // caller could move Draft straight to Completed.
        var a = SchedulingWorld.Appointment("t", AppointmentStatus.Draft, DateTimeOffset.UtcNow, 60);
        Assert.Throws<IllegalTransitionException>(() =>
            a.ApplyTransition(AppointmentStatus.Completed, DateTimeOffset.UtcNow));
        Assert.Equal(AppointmentStatus.Draft, a.Status);
        Assert.Equal(1, a.RowVersion);
    }

    [Theory]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    public void Terminal_statuses_are_not_reschedulable(AppointmentStatus status) =>
        Assert.False(SchedulingWorld.Appointment("x", status, DateTimeOffset.UtcNow, 60).IsReschedulable);

    [Theory]
    [InlineData(AppointmentStatus.Draft)]
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
            Appointment.Create("z", "t", "p", "UTC", "g", "G", "svc-peel", "Peel", 0,
                null, null, DateTimeOffset.UtcNow, null, "c", DateTimeOffset.UtcNow));
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

        var same = Appointment.Rehydrate(
            a.AppointmentId, a.TenantId, a.PropertyId, a.PropertyTimeZone, a.GuestId, a.GuestAlias,
            a.ServiceId, a.ServiceName, a.DurationMinutes, a.ProviderId, a.RoomId,
            a.StartUtc, a.Status, rowVersion: 47, a.ConfirmationNumber, a.CorrelationId, a.CreatedUtc, a.UpdatedUtc);

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
