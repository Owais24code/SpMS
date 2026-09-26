using Spms.Modules.Scheduling.Domain;
using Spms.Tests.Support;
using Xunit;
using static Spms.Tests.Support.SchedulingWorld;

namespace Spms.Tests.Domain;

/// <summary>CON-006 undo, bulk move and hold expiry, against the in-memory adapters.</summary>
public class UndoTests
{
    private static async Task<(SchedulingWorld W, string Token)> MovedA1(SchedulingWorld w)
    {
        var a1 = (await w.Get("a1"))!;
        var pf = await w.Preflight(a1, w.Day.AddHours(15), "prov-marco", "room-3");
        var r = await w.Commit("a1", pf.Token);
        Assert.Equal(SchedulingService.CommitOutcome.Committed, r.Outcome);
        Assert.Equal(w.Clock.UtcNow.Add(w.Scheduling.UndoWindow), r.UndoUntilUtc);
        return (w, pf.Token);
    }

    [Fact]
    public async Task Undo_puts_the_appointment_back_exactly_once()
    {
        var (w, token) = await MovedA1(new SchedulingWorld());

        var undo = await w.Scheduling.UndoMoveAsync(Tenant, Property, "a1", token, "corr");
        Assert.Equal(SchedulingService.UndoOutcome.Undone, undo.Outcome);

        var a1 = (await w.Get("a1"))!;
        Assert.Equal(w.Day.AddHours(9), a1.StartUtc);
        Assert.Equal("prov-lena", a1.ProviderId);
        Assert.Equal("room-1", a1.RoomId);
        Assert.Equal(3, a1.RowVersion);
        Assert.Contains(w.Audit.Entries, e => e.Action == "appointment.move.undo");

        var again = await w.Scheduling.UndoMoveAsync(Tenant, Property, "a1", token, "corr");
        Assert.Equal(SchedulingService.UndoOutcome.WindowClosed, again.Outcome);
    }

    [Fact]
    public async Task Undo_is_refused_once_the_window_has_closed()
    {
        var (w, token) = await MovedA1(new SchedulingWorld());
        w.Clock.Advance(w.Scheduling.UndoWindow);
        var undo = await w.Scheduling.UndoMoveAsync(Tenant, Property, "a1", token, "corr");
        Assert.Equal(SchedulingService.UndoOutcome.WindowClosed, undo.Outcome);
        Assert.Equal(w.Day.AddHours(15), (await w.Get("a1"))!.StartUtc);
    }

    [Fact]
    public async Task Undo_never_overwrites_a_later_change()
    {
        var (w, token) = await MovedA1(new SchedulingWorld());
        var a1 = (await w.Get("a1"))!;
        await w.Scheduling.TransitionAsync(Tenant, Property, "a1", AppointmentStatus.CheckedIn, a1.RowVersion, null, "corr");

        var undo = await w.Scheduling.UndoMoveAsync(Tenant, Property, "a1", token, "corr");
        Assert.Equal(SchedulingService.UndoOutcome.StaleVersion, undo.Outcome);
    }

    [Fact]
    public async Task Undo_is_refused_when_the_original_room_was_taken_meanwhile()
    {
        var (w, token) = await MovedA1(new SchedulingWorld());
        w.Add(Appointment("thief", AppointmentStatus.Confirmed, w.Day.AddHours(9), 60, "prov-priya", "room-1",
            serviceId: "svc-facial", serviceName: "Facial 45", guestId: "guest-0007"));

        var undo = await w.Scheduling.UndoMoveAsync(Tenant, Property, "a1", token, "corr");
        Assert.Equal(SchedulingService.UndoOutcome.HardConflict, undo.Outcome);
        Assert.Contains(undo.Conflicts, c => c.Code == "CON-002");
    }

    [Fact]
    public async Task Undo_needs_the_token_that_committed_this_appointment()
    {
        var (w, token) = await MovedA1(new SchedulingWorld());
        Assert.Equal(SchedulingService.UndoOutcome.AppointmentMismatch,
            (await w.Scheduling.UndoMoveAsync(Tenant, Property, "a2", token, "corr")).Outcome);
        Assert.Equal(SchedulingService.UndoOutcome.TokenInvalid,
            (await w.Scheduling.UndoMoveAsync(Tenant, Property, "a1", "pf_unknown", "corr")).Outcome);
        Assert.Equal(SchedulingService.UndoOutcome.TokenInvalid,
            (await w.Scheduling.UndoMoveAsync(Tenant, Elsewhere, "a1", token, "corr")).Outcome);
    }
}

public class BulkMoveTests
{
    private static SchedulingWorld WithNeighbour()
    {
        var w = new SchedulingWorld();
        // a3: same hour as a1, next room, another provider and guest.
        w.Add(Appointment("a3", AppointmentStatus.Confirmed, w.Day.AddHours(9), 90, "prov-marco", "room-2",
            serviceId: "svc-deep", serviceName: "Deep tissue 90", guestId: "guest-0003"));
        return w;
    }

    [Fact]
    public async Task Two_appointments_can_trade_rooms_because_each_is_judged_where_the_other_is_going()
    {
        var w = WithNeighbour();
        var r = await w.Scheduling.BulkMoveAsync(Tenant, Property,
        [
            new SchedulingService.BulkItem("a1", w.Day.AddHours(9), null, "room-2", 1),
            new SchedulingService.BulkItem("a3", w.Day.AddHours(9), null, "room-1", 1),
        ], null, dryRun: false, "corr");

        Assert.Equal(SchedulingService.BulkOutcome.Committed, r.Outcome);
        Assert.All(r.Items, i => Assert.Empty(i.Conflicts));
        Assert.Equal("room-2", (await w.Get("a1"))!.RoomId);
        Assert.Equal("room-1", (await w.Get("a3"))!.RoomId);
        Assert.Equal(2, w.Audit.Entries.Count(e => e.Action == "appointment.move"));
    }

    [Fact]
    public async Task Two_moves_into_one_room_at_one_time_are_a_hard_conflict_and_nothing_moves()
    {
        var w = WithNeighbour();
        var r = await w.Scheduling.BulkMoveAsync(Tenant, Property,
        [
            new SchedulingService.BulkItem("a1", w.Day.AddHours(11), null, "room-5", 1),
            new SchedulingService.BulkItem("a3", w.Day.AddHours(11), null, "room-5", 1),
        ], "busy day", dryRun: false, "corr");

        Assert.Equal(SchedulingService.BulkOutcome.HardConflict, r.Outcome);
        Assert.All(r.Items, i => Assert.Contains(i.Conflicts, c => c.Code == "CON-002"));
        Assert.Equal("room-1", (await w.Get("a1"))!.RoomId);
        Assert.Equal(1, (await w.Get("a3"))!.RowVersion);
    }

    [Fact]
    public async Task A_stale_item_refuses_the_whole_batch()
    {
        var w = WithNeighbour();
        var r = await w.Scheduling.BulkMoveAsync(Tenant, Property,
        [
            new SchedulingService.BulkItem("a1", w.Day.AddHours(11), null, null, 1),
            new SchedulingService.BulkItem("a3", w.Day.AddHours(12), null, null, 7),
        ], null, dryRun: false, "corr");

        Assert.Equal(SchedulingService.BulkOutcome.StaleVersion, r.Outcome);
        Assert.Equal(w.Day.AddHours(9), (await w.Get("a1"))!.StartUtc);
    }

    [Fact]
    public async Task Unknown_or_repeated_items_are_invalid()
    {
        var w = WithNeighbour();
        var r = await w.Scheduling.BulkMoveAsync(Tenant, Property,
        [
            new SchedulingService.BulkItem("a1", w.Day.AddHours(11), null, null, 1),
            new SchedulingService.BulkItem("a1", w.Day.AddHours(12), null, null, 1),
            new SchedulingService.BulkItem("nope", w.Day.AddHours(12), null, null, 1),
        ], null, dryRun: false, "corr");
        Assert.Equal(SchedulingService.BulkOutcome.Invalid, r.Outcome);
        Assert.Equal(SchedulingService.BulkItemState.Duplicate, r.Items[1].State);
        Assert.Equal(SchedulingService.BulkItemState.NotFound, r.Items[2].State);
    }

    [Fact]
    public async Task A_dry_run_reports_and_writes_nothing_and_a_soft_conflict_needs_a_reason()
    {
        var w = WithNeighbour();
        // Both with prov-lena at 11:00: CON-001 on each, soft.
        SchedulingService.BulkItem[] items =
        [
            new("a1", w.Day.AddHours(11), "prov-lena", "room-5", 1),
            new("a3", w.Day.AddHours(11), "prov-lena", "room-6", 1),
        ];

        var dry = await w.Scheduling.BulkMoveAsync(Tenant, Property, items, null, dryRun: true, "corr");
        Assert.Equal(SchedulingService.BulkOutcome.Evaluated, dry.Outcome);
        Assert.True(dry.HasSoft);
        Assert.False(dry.HasHard);
        Assert.Equal(1, (await w.Get("a1"))!.RowVersion);

        var noReason = await w.Scheduling.BulkMoveAsync(Tenant, Property, items, null, dryRun: false, "corr");
        Assert.Equal(SchedulingService.BulkOutcome.ReasonRequired, noReason.Outcome);

        var withReason = await w.Scheduling.BulkMoveAsync(Tenant, Property, items, "Lena covers both, agreed", dryRun: false, "corr");
        Assert.Equal(SchedulingService.BulkOutcome.Committed, withReason.Outcome);
        Assert.Equal("prov-lena", (await w.Get("a3"))!.ProviderId);
    }
}

public class HoldExpiryTests
{
    [Fact]
    public async Task An_expired_hold_is_released_and_a_live_one_is_kept()
    {
        var w = new SchedulingWorld();
        var held = await w.Scheduling.CreateAsync(Tenant, Property, new SchedulingService.NewBooking(
            "h1", "guest-0004", "Guest H", "svc-facial", w.Day.AddHours(16), "prov-priya", "room-4", "AAR100001", "corr",
            BookingSource.Online, HoldFor: TimeSpan.FromMinutes(10)), null);
        Assert.Equal(SchedulingService.CreateOutcome.Created, held.Outcome);
        await w.Scheduling.CreateAsync(Tenant, Property, new SchedulingService.NewBooking(
            "h2", "guest-0005", "Guest I", "svc-facial", w.Day.AddHours(17), "prov-priya", "room-4", "AAR100002", "corr",
            BookingSource.Online, HoldFor: TimeSpan.FromMinutes(30)), null);

        w.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await w.Scheduling.ReleaseExpiredHoldsAsync(Tenant, Property));

        var h1 = (await w.Get("h1"))!;
        Assert.Equal(AppointmentStatus.Cancelled, h1.Status);
        Assert.Equal(SchedulingService.HoldExpiredReason, h1.CancellationReasonCode);
        Assert.Equal(AppointmentStatus.Held, (await w.Get("h2"))!.Status);
    }
}
