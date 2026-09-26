using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spms.Host.Workers;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Scheduling.Operations;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>
/// Batch 3 against the real database: the deferred room exclusion under a
/// bulk swap, undo round trips, the visit and room following the appointment,
/// the visit cascade, the waitlist, and the job runner.
/// </summary>
public class SchedulingOperationsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string T = PostgresWorld.Tenant;
    private const string P = PostgresWorld.Property;
    private static string Id(string name) => TestIds.Of(name);

    private async Task FreshAsync()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(T, P);
    }

    [RequiresPostgres]
    public async Task A_bulk_room_swap_commits_because_the_exclusion_is_deferred_to_the_end()
    {
        await FreshAsync();
        await using var w = new PostgresWorld(fixture);
        await w.AddAsync(PostgresWorld.Appointment("bs1", AppointmentStatus.Confirmed, w.Day.AddHours(9), 60, "prov-lena", "room-a",
            "svc-swedish", "Swedish 60", guestId: "guest-1"));
        await w.AddAsync(PostgresWorld.Appointment("bs2", AppointmentStatus.Confirmed, w.Day.AddHours(9), 60, "prov-marco", "room-b",
            "svc-swedish", "Swedish 60", guestId: "guest-2"));

        var r = await w.Scheduling.BulkMoveAsync(T, P,
        [
            new SchedulingService.BulkItem("bs1", w.Day.AddHours(9), null, "room-b", 1),
            new SchedulingService.BulkItem("bs2", w.Day.AddHours(9), null, "room-a", 1),
        ], null, dryRun: false, "corr");
        Assert.Equal(SchedulingService.BulkOutcome.Committed, r.Outcome);

        await using var fresh = new PostgresWorld(fixture);
        Assert.Equal("room-b", (await fresh.Get("bs1"))!.RoomId);
        Assert.Equal("room-a", (await fresh.Get("bs2"))!.RoomId);
    }

    [RequiresPostgres]
    public async Task A_bulk_move_that_would_double_book_a_room_after_a_concurrent_booking_rolls_back_whole()
    {
        await FreshAsync();
        await using var w = new PostgresWorld(fixture);
        await w.AddAsync(PostgresWorld.Appointment("bc1", AppointmentStatus.Confirmed, w.Day.AddHours(9), 60, "prov-lena", "room-a",
            "svc-swedish", "Swedish 60", guestId: "guest-1"));
        // Already in room-5 at 14:00 — the scan sees it, so this is refused before any write.
        await w.AddAsync(PostgresWorld.Appointment("bc2", AppointmentStatus.Confirmed, w.Day.AddHours(14), 60, "prov-marco", "room-5",
            "svc-swedish", "Swedish 60", guestId: "guest-2"));

        var r = await w.Scheduling.BulkMoveAsync(T, P,
            [new SchedulingService.BulkItem("bc1", w.Day.AddHours(14), null, "room-5", 1)], "x", dryRun: false, "corr");
        Assert.Equal(SchedulingService.BulkOutcome.HardConflict, r.Outcome);
        Assert.Equal("room-a", (await w.Get("bc1"))!.RoomId);
    }

    [RequiresPostgres]
    public async Task Undo_round_trips_through_the_proposal_row_and_happens_once()
    {
        await FreshAsync();
        await using var w = new PostgresWorld(fixture);
        await w.AddAsync(PostgresWorld.Appointment("un1", AppointmentStatus.Confirmed, w.Day.AddHours(9), 90, "prov-lena", "room-1",
            "svc-deep", "Deep tissue 90", guestId: "guest-3"));

        var pf = await w.Preflight((await w.Get("un1"))!, w.Day.AddHours(19), "prov-marco", "room-2");
        var committed = await w.Commit("un1", pf.Token);
        Assert.Equal(SchedulingService.CommitOutcome.Committed, committed.Outcome);
        Assert.NotNull(committed.UndoUntilUtc);

        var undo = await w.Scheduling.UndoMoveAsync(T, P, "un1", pf.Token, "corr");
        Assert.Equal(SchedulingService.UndoOutcome.Undone, undo.Outcome);

        await using var fresh = new PostgresWorld(fixture);
        var a = (await fresh.Get("un1"))!;
        Assert.Equal((w.Day.AddHours(9), "prov-lena", "room-1", 3), (a.StartUtc, a.ProviderId, a.RoomId, a.RowVersion));
        Assert.Equal(SchedulingService.UndoOutcome.WindowClosed,
            (await fresh.Scheduling.UndoMoveAsync(T, P, "un1", pf.Token, "corr")).Outcome);
        Assert.Contains(await fresh.AuditRecentAsync(5), e => e.Action == "appointment.move.undo" && e.SubjectId == "un1");
    }

    /* The effects and the operations services run through DI, with real ids. */

    private static async Task<Appointment> BookAsync(IUnitOfWork uow, SchedulingService s, string id, DateTimeOffset start, string room, string guest, string? visitId = null)
    {
        // Outside a request there is no ambient transaction; the service's
        // reference lookups need one, as they would get from the request.
        await using var tx = await uow.BeginAsync();
        var r = await s.CreateAsync(Id(T), Id(P), new SchedulingService.NewBooking(
            Id(id), Id(guest), "G", Id("svc-swedish"), start, Id("prov-lena"), Id(room), null, "corr", VisitId: visitId), null);
        Assert.Equal(SchedulingService.CreateOutcome.Created, r.Outcome);
        await tx.CommitAsync();
        return r.Appointment!;
    }

    [RequiresPostgres]
    public async Task Check_in_arrives_the_visit_and_completion_leaves_the_room_needing_a_turnover()
    {
        await FreshAsync();
        var clock = new TestClock(new DateTimeOffset(2026, 7, 1, 13, 0, 0, TimeSpan.Zero));
        using var scope = fixture.NewScope(T, [P], P, clock);
        var sp = scope.ServiceProvider;
        var scheduling = sp.GetRequiredService<SchedulingService>();
        var visits = sp.GetRequiredService<VisitService>();
        var turnaround = sp.GetRequiredService<TurnaroundService>();
        var uow = sp.GetRequiredService<IUnitOfWork>();

        var visit = await visits.CreateAsync(TestIds.IdOf("guest-4"), new DateOnly(2026, 7, 1), "DayGuest", null, null, null, default);
        Assert.Equal(VisitService.Outcome.Ok, visit.Outcome);
        var visitId = visit.View!.Visit.VisitId;

        var a = await BookAsync(uow, scheduling, "ef1", clock.UtcNow.AddHours(1), "room-turn", "guest-4", visitId.ToString());
        var id = a.AppointmentId;
        var v = 1;
        foreach (var to in new[] { AppointmentStatus.CheckedIn, AppointmentStatus.Ready, AppointmentStatus.InService, AppointmentStatus.Completed })
        {
            var r = await scheduling.TransitionAsync(Id(T), Id(P), id, to, v, null, "corr");
            Assert.Equal(SchedulingService.TransitionOutcome.Applied, r.Outcome);
            v = r.Appointment!.RowVersion;
            if (to == AppointmentStatus.CheckedIn)
            {
                await using var tx = await uow.BeginAsync();
                var arrived = (await visits.ViewAsync(visitId, default))!.Visit;
                await tx.CommitAsync();
                Assert.Equal(VisitStatuses.Arrived, arrived.Status);
                Assert.Equal(clock.UtcNow, arrived.ActualArrivalAt);
            }
        }

        TurnaroundView task;
        await using (var tx = await uow.BeginAsync())
        {
            Assert.Equal(VisitStatuses.InProgress, (await visits.ViewAsync(visitId, default))!.Visit.Status);
            var (open, _) = await turnaround.ListAsync(true, 0, 10, default);
            task = Assert.Single(open);
            Assert.Contains(TestIds.IdOf("room-turn"), await turnaround.RoomsNotReadyAsync(default));
            await tx.CommitAsync();
        }
        Assert.Equal("Turnover", task.Row.TaskType);
        Assert.Equal(clock.UtcNow.AddMinutes(15), task.Row.DueAt);

        var done = await turnaround.CompleteAsync(task.Row.TurnaroundTaskId, task.Row.Version, "Pass", "TURN-STD", default);
        Assert.Equal(TurnaroundService.Outcome.Ok, done.Outcome);
        await using (var tx = await uow.BeginAsync())
        {
            Assert.DoesNotContain(TestIds.IdOf("room-turn"), await turnaround.RoomsNotReadyAsync(default));
            await tx.CommitAsync();
        }

        var closed = await visits.TransitionAsync(visitId, VisitStatuses.Closed, (await ViewVersion(uow, visits, visitId)), null, default);
        Assert.Equal(VisitService.Outcome.Ok, closed.Outcome);
        Assert.NotNull(closed.View!.Visit.ClosedAt);
    }

    private static async Task<int> ViewVersion(IUnitOfWork uow, VisitService visits, Guid id)
    {
        await using var tx = await uow.BeginAsync();
        var v = (await visits.ViewAsync(id, default))!.Visit.Version;
        await tx.CommitAsync();
        return v;
    }

    [RequiresPostgres]
    public async Task Cancelling_a_visit_cancels_its_bookings_and_is_refused_once_treatment_started()
    {
        await FreshAsync();
        var clock = new TestClock(new DateTimeOffset(2026, 7, 2, 13, 0, 0, TimeSpan.Zero));
        using var scope = fixture.NewScope(T, [P], P, clock);
        var sp = scope.ServiceProvider;
        var scheduling = sp.GetRequiredService<SchedulingService>();
        var visits = sp.GetRequiredService<VisitService>();
        var uow = sp.GetRequiredService<IUnitOfWork>();

        var v1 = (await visits.CreateAsync(TestIds.IdOf("guest-5"), new DateOnly(2026, 7, 2), "Group", null, null, null, default)).View!.Visit.VisitId;
        var b1 = await BookAsync(uow, scheduling, "vc1", clock.UtcNow.AddHours(2), "room-6", "guest-5", v1.ToString());
        var b2 = await BookAsync(uow, scheduling, "vc2", clock.UtcNow.AddHours(4), "room-6", "guest-5", v1.ToString());

        var cancelled = await visits.TransitionAsync(v1, VisitStatuses.Cancelled, 1, "Party called off", default);
        Assert.Equal(VisitService.Outcome.Ok, cancelled.Outcome);
        Assert.All(cancelled.View!.Appointments, a => Assert.Equal(AppointmentStatuses.Cancelled, a.Status));

        var v2 = (await visits.CreateAsync(TestIds.IdOf("guest-6"), new DateOnly(2026, 7, 2), "DayGuest", null, null, null, default)).View!.Visit.VisitId;
        var b3 = await BookAsync(uow, scheduling, "vc3", clock.UtcNow.AddHours(2), "room-7", "guest-6", v2.ToString());
        await scheduling.TransitionAsync(Id(T), Id(P), b3.AppointmentId, AppointmentStatus.CheckedIn, 1, null, "corr");
        var refused = await visits.TransitionAsync(v2, VisitStatuses.Cancelled, await ViewVersion(uow, visits, v2), null, default);
        Assert.Equal(VisitService.Outcome.Blocked, refused.Outcome);
    }

    [RequiresPostgres]
    public async Task The_waitlist_offers_accepts_and_lets_offers_lapse()
    {
        await FreshAsync();
        var clock = new TestClock(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
        using var scope = fixture.NewScope(T, [P], P, clock);
        var sp = scope.ServiceProvider;
        var waitlist = sp.GetRequiredService<WaitlistService>();
        var scheduling = sp.GetRequiredService<SchedulingService>();
        var uow = sp.GetRequiredService<IUnitOfWork>();

        var window = new WaitlistCriteria(clock.UtcNow.AddHours(1), clock.UtcNow.AddHours(6));
        var a = (await waitlist.AddAsync(TestIds.IdOf("guest-7"), null, window, null, default)).View!;
        var b = (await waitlist.AddAsync(TestIds.IdOf("guest-2"), TestIds.IdOf("svc-facial"), window, null, default)).View!;

        await using (var tx = await uow.BeginAsync())
        {
            var fits = await waitlist.CandidatesAsync(clock.UtcNow.AddHours(2), clock.UtcNow.AddHours(3), TestIds.IdOf("svc-swedish"), default);
            Assert.Equal([a.Row.WaitlistId], fits.Select(f => f.Row.WaitlistId));
            await tx.CommitAsync();
        }

        var offered = await waitlist.OfferAsync(a.Row.WaitlistId, 1, 15, default);
        Assert.Equal(WaitlistEntryStatuses.Offered, offered.View!.Row.Status);

        var appt = await BookAsync(uow, scheduling, "wl1", clock.UtcNow.AddHours(2), "room-8", "guest-7");
        var wrong = await waitlist.AcceptAsync(b.Row.WaitlistId, 1, Guid.Parse(appt.AppointmentId), default);
        Assert.Equal(WaitlistService.Outcome.WrongGuest, wrong.Outcome);
        var accepted = await waitlist.AcceptAsync(a.Row.WaitlistId, offered.View.Row.Version, Guid.Parse(appt.AppointmentId), default);
        Assert.Equal(WaitlistEntryStatuses.Accepted, accepted.View!.Row.Status);

        var offeredB = await waitlist.OfferAsync(b.Row.WaitlistId, 1, 15, default);
        clock.Advance(TimeSpan.FromMinutes(16));
        await using (var tx = await uow.BeginAsync())
        {
            Assert.Equal(1, await waitlist.ExpireAsync(default));
            Assert.Equal(WaitlistEntryStatuses.Waiting, (await waitlist.ViewAsync(b.Row.WaitlistId, default))!.Row.Status);
            await tx.CommitAsync();
        }
    }

    [RequiresPostgres]
    public async Task The_job_runner_releases_expired_holds_at_each_property_through_active_properties()
    {
        await FreshAsync();
        // Yesterday, so the hold is long expired by the runner's real clock.
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        var clock = new TestClock(yesterday);
        using (var scope = fixture.NewScope(T, [P], P, clock))
        {
            var scheduling = scope.ServiceProvider.GetRequiredService<SchedulingService>();
            await using var tx = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync();
            var r = await scheduling.CreateAsync(Id(T), Id(P), new SchedulingService.NewBooking(
                Id("hold1"), Id("guest-0"), "G", Id("svc-swedish"), yesterday.AddHours(3), Id("prov-lena"), Id("room-9"), null, "corr",
                BookingSource.Online, HoldFor: TimeSpan.FromMinutes(10)), null);
            Assert.Equal(SchedulingService.CreateOutcome.Created, r.Outcome);
            await tx.CommitAsync();
        }

        var runner = new JobRunner(fixture.Services.GetRequiredService<IServiceScopeFactory>(), fixture.DataSource,
            new OutboxOptions(), new SystemClock(), NullLogger<JobRunner>.Instance);
        Assert.True(await runner.RunDueAsync(force: true, default) >= 1);

        var status = await fixture.ScalarAsync<string>(
            $"SELECT status || '/' || coalesce(cancellation_reason_code, '') FROM scheduling.appointment WHERE appointment_id = '{Id("hold1")}'");
        Assert.Equal("Cancelled/HoldExpired", status);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM core.audit_event WHERE entity_id = '{Id("hold1")}' AND action = 'appointment.transition' AND actor_type = 'System'"));
    }
}
