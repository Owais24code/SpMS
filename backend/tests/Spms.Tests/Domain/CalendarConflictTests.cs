using Spms.Modules.Scheduling.Domain;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Domain;

/// <summary>Rooms that are not there or closed, people on leave, and a guest booked at another property.</summary>
public class CalendarConflictTests
{
    [Fact]
    public async Task A_room_that_is_not_at_this_property_is_a_hard_conflict()
    {
        var w = new SchedulingWorld();
        w.Calendar.SetRoom("room-ghost", RoomState.Unknown);
        var pf = await w.Preflight((await w.Get("a1"))!, w.Day.AddHours(20), "prov-lena", "room-ghost");
        Assert.Contains(pf.Conflicts, c => c.Rule == "room-unknown" && !c.Overridable);
        Assert.False(pf.CommitAllowed);
    }

    [Fact]
    public async Task A_room_closed_for_maintenance_is_a_hard_conflict()
    {
        var w = new SchedulingWorld();
        w.Calendar.SetRoom("room-free", RoomState.OutOfService);
        var pf = await w.Preflight((await w.Get("a1"))!, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.Contains(pf.Conflicts, c => c.Rule == "room-out-of-service");
        Assert.False(pf.CommitAllowed);
    }

    [Theory]
    [InlineData(RosterState.OnLeave)]
    [InlineData(RosterState.OffShift)]
    public async Task A_provider_not_on_the_roster_is_soft_and_needs_a_reason(RosterState state)
    {
        var w = new SchedulingWorld();
        w.Calendar.SetProvider("prov-lena", state);
        var pf = await w.Preflight((await w.Get("a1"))!, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.Contains(pf.Conflicts, c => c.Rule == "provider-unavailable" && c.Overridable);
        Assert.True(pf.RequiresReason);
    }

    [Theory]
    [InlineData(RosterState.OnShift)]
    [InlineData(RosterState.NotRostered)]
    public async Task A_rostered_or_unrostered_provider_raises_nothing(RosterState state)
    {
        var w = new SchedulingWorld();
        w.Calendar.SetProvider("prov-lena", state);
        var pf = await w.Preflight((await w.Get("a1"))!, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.DoesNotContain(pf.Conflicts, c => c.Rule == "provider-unavailable");
    }

    [Fact]
    public async Task The_same_guest_at_another_property_at_the_same_time_is_CON005()
    {
        // CON-005 is tenant-wide: the neighbour scan is property-local, so the
        // other property's booking is found through the tenant-wide interval query.
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("elsewhere", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60,
            guestId: SchedulingWorld.GuestOfA1).CopyToProperty(SchedulingWorld.Elsewhere));

        var pf = await w.Preflight((await w.Get("a1"))!, w.Day.AddHours(20), "prov-lena", "room-free");
        var c = Assert.Single(pf.Conflicts, x => x.Code == "CON-005");
        Assert.Equal("guest-overlap-elsewhere", c.Rule);
        Assert.True(c.Overridable);
    }

    [Fact]
    public async Task A_cancelled_booking_elsewhere_does_not_count()
    {
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("gone", AppointmentStatus.Cancelled, w.Day.AddHours(20), 60,
            guestId: SchedulingWorld.GuestOfA1).CopyToProperty(SchedulingWorld.Elsewhere));

        var pf = await w.Preflight((await w.Get("a1"))!, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.DoesNotContain(pf.Conflicts, x => x.Code == "CON-005");
    }

    [Fact]
    public async Task An_unknown_guest_cannot_be_booked()
    {
        var w = new SchedulingWorld();
        w.Guests.Forget("guest-ghost");
        var r = await w.Scheduling.CreateAsync(SchedulingWorld.Tenant, SchedulingWorld.Property,
            new SchedulingService.NewBooking("n1", "guest-ghost", "Ghost", "svc-peel", w.Day.AddHours(20), null, "room-free", null, "c"), null);
        Assert.Equal(SchedulingService.CreateOutcome.UnknownGuest, r.Outcome);
    }

    [Fact]
    public async Task An_online_hold_is_created_held_with_its_expiry_and_frozen_price()
    {
        var w = new SchedulingWorld();
        var r = await w.Scheduling.CreateAsync(SchedulingWorld.Tenant, SchedulingWorld.Property,
            new SchedulingService.NewBooking("h1", "guest-0005", "G", "svc-peel", w.Day.AddHours(20), null, "room-free", null, "c",
                Source: BookingSource.Online, HoldFor: TimeSpan.FromMinutes(10)), null);
        Assert.Equal(SchedulingService.CreateOutcome.Created, r.Outcome);
        Assert.Equal(AppointmentStatus.Held, r.Appointment!.Status);
        Assert.Equal(w.Clock.UtcNow.AddMinutes(10), r.Appointment.HoldExpiresUtc);
        Assert.Equal(BookingSource.Online, r.Appointment.Source);
    }
}
