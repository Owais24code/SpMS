using Npgsql;
using Spms.SharedKernel;
using Spms.Modules.Scheduling.Domain;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>
/// The two guarantees the in-memory adapter cannot give, and which were the
/// top two findings of the architecture review.
/// </summary>
public class UnitOfWorkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [RequiresPostgres]
    public async Task The_audit_row_and_the_appointment_write_commit_together()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await using var w = new PostgresWorld(fixture);

        await w.AddAsync(PostgresWorld.Appointment("uow1", AppointmentStatus.Confirmed,
            w.Day.AddHours(9), 90, "prov-lena", "room-1", "svc-deep", "Deep tissue 90"));

        var target = (await w.Get("uow1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");
        var r = await w.Commit("uow1", pf.Token);
        Assert.Equal(SchedulingService.CommitOutcome.Committed, r.Outcome);

        // Both sides of the transaction are visible to a NEW connection, which
        // is the only way to know they were actually committed rather than
        // merely tracked.
        await using var fresh = new PostgresWorld(fixture);
        Assert.Equal(2, (await fresh.Get("uow1"))!.RowVersion);

        var audit = await fresh.AuditRecentAsync(10);
        Assert.Contains(audit, a => a.Action == "appointment.move" && a.SubjectId == "uow1");
    }

    [RequiresPostgres]
    public async Task A_refused_commit_leaves_no_audit_row_and_no_change()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await using var w = new PostgresWorld(fixture);

        await w.AddAsync(PostgresWorld.Appointment("uow2", AppointmentStatus.Confirmed,
            w.Day.AddHours(9), 90, "prov-lena", "room-2", "svc-deep", "Deep tissue 90"));
        await w.AddAsync(PostgresWorld.Appointment("uow2blocker", AppointmentStatus.Confirmed,
            w.Day.AddHours(20), 60, "prov-marco", "room-3", "svc-hotstone", "Hot stone 60",
            guestId: "guest-0002"));

        var target = (await w.Get("uow2"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.True(pf.CommitAllowed);

        // Take the room after the token was minted, from a separate connection.
        await using (var other = new PostgresWorld(fixture))
        {
            await other.AddAsync(PostgresWorld.Appointment("uow2thief", AppointmentStatus.Confirmed,
                w.Day.AddHours(20), 60, "prov-marco", "room-free", "svc-hotstone", "Hot stone 60",
                guestId: "guest-9999"));
        }

        var r = await w.Commit("uow2", pf.Token);
        Assert.Equal(SchedulingService.CommitOutcome.BoardChanged, r.Outcome);

        await using var fresh = new PostgresWorld(fixture);
        Assert.Equal(1, (await fresh.Get("uow2"))!.RowVersion);
        Assert.Equal("room-2", (await fresh.Get("uow2"))!.RoomId);

        var audit = await fresh.AuditRecentAsync(50);
        Assert.DoesNotContain(audit, a => a.SubjectId == "uow2");
    }

    [RequiresPostgres]
    public async Task A_refused_commit_returns_the_token_rather_than_burning_it()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await using var w = new PostgresWorld(fixture);

        await w.AddAsync(PostgresWorld.Appointment("tok1", AppointmentStatus.Confirmed,
            w.Day.AddHours(9), 90, "prov-lena", "room-4", "svc-deep", "Deep tissue 90"));
        await w.AddAsync(PostgresWorld.Appointment("tok1busy", AppointmentStatus.Confirmed,
            w.Day.AddHours(20), 60, "prov-lena", "room-other", "svc-swedish", "Swedish 60",
            guestId: "guest-0002"));

        var target = (await w.Get("tok1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.True(pf.RequiresReason);

        Assert.Equal(SchedulingService.CommitOutcome.ReasonRequired, (await w.Commit("tok1", pf.Token)).Outcome);

        // Token consumption now happens inside the transaction, so a refusal
        // rolls it back and the reason prompt CON-001 exists to collect is not
        // a dead end.
        await using var fresh = new PostgresWorld(fixture);
        Assert.NotNull(await fresh.Preflights.FindAsync(PostgresWorld.Tenant, pf.Token));

        Assert.Equal(SchedulingService.CommitOutcome.Committed,
            (await w.Commit("tok1", pf.Token, reason: "Guest asked for Lena")).Outcome);
    }
}

/// <summary>
/// CON-002 under real concurrency. These are the cases the in-process gate
/// could never honestly claim, because a lock on one node says nothing about
/// a second instance.
/// </summary>
public class RoomOverlapConstraintTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [RequiresPostgres]
    public async Task Concurrent_creates_into_one_room_admit_exactly_one()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        var slot = new DateTimeOffset(2026, 6, 15, 20, 0, 0, TimeSpan.Zero);

        // Each attempt gets its OWN DbContext and connection, so these are
        // genuinely concurrent transactions rather than serialised calls
        // through one in-process lock.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var w = new PostgresWorld(fixture);
            return await w.Scheduling.CreateAsync(PostgresWorld.Tenant, PostgresWorld.Property,
                new SchedulingService.NewBooking(
                    $"race{i}", $"guest-{i}", $"Guest {i}", "svc-hotstone", slot,
                    "prov-marco", "room-race", $"AARRACE{i}", "corr"),
                null);
        }));

        var created = attempts.Count(a => a.Outcome == SchedulingService.CreateOutcome.Created);
        var refused = attempts.Count(a => a.Outcome == SchedulingService.CreateOutcome.HardConflict);

        Assert.Equal(1, created);
        Assert.Equal(7, refused);

        // And the board really holds one.
        await using var fresh = new PostgresWorld(fixture);
        var inRoom = await fresh.Repo.ListOverlappingAsync(
            PostgresWorld.Tenant, PostgresWorld.Property, slot, slot.AddHours(2));
        Assert.Single(inRoom, a => a.RoomId == "room-race");
    }

    [RequiresPostgres]
    public async Task The_refusal_is_reported_as_CON002_not_as_a_driver_error()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await using var w = new PostgresWorld(fixture);

        var slot = w.Day.AddHours(21);
        await w.AddAsync(PostgresWorld.Appointment("holder", AppointmentStatus.Confirmed,
            slot, 60, "prov-marco", "room-shared", "svc-hotstone", "Hot stone 60"));

        // Insert straight through the repository, bypassing the conflict scan,
        // so only the constraint can refuse it.
        var clash = PostgresWorld.Appointment("clash", AppointmentStatus.Confirmed,
            slot.AddMinutes(30), 60, "prov-lena", "room-shared", "svc-swedish", "Swedish 60",
            guestId: "guest-0002");

        var e = await Assert.ThrowsAsync<RoomOverlapException>(() => w.Repo.TryAddAsync(clash));
        // SQLSTATE 23P01 translated into a domain outcome, so the operator sees
        // a named conflict instead of a 500.
        Assert.Equal("room-shared", e.RoomId);
    }

    [RequiresPostgres]
    public async Task A_provider_double_booking_is_still_committable()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await using var w = new PostgresWorld(fixture);

        var slot = w.Day.AddHours(22);
        await w.AddAsync(PostgresWorld.Appointment("p1", AppointmentStatus.Confirmed,
            slot, 60, "prov-marco", "room-8", "svc-hotstone", "Hot stone 60"));

        // Same provider, same hour, different room. CON-001 is soft and must
        // remain committable — making this a constraint too would be the easy
        // mistake, and would remove the override the specification requires.
        await w.AddAsync(PostgresWorld.Appointment("p2", AppointmentStatus.Confirmed,
            slot, 60, "prov-marco", "room-9", "svc-hotstone", "Hot stone 60", guestId: "guest-0002"));

        var both = await w.Repo.ListOverlappingAsync(
            PostgresWorld.Tenant, PostgresWorld.Property, slot, slot.AddHours(1));
        Assert.Equal(2, both.Count(a => a.ProviderId == "prov-marco"));
    }

    [RequiresPostgres]
    public async Task Cancelling_frees_the_room()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await using var w = new PostgresWorld(fixture);

        var slot = w.Day.AddHours(23);
        await w.AddAsync(PostgresWorld.Appointment("c1", AppointmentStatus.Confirmed,
            slot, 30, "prov-priya", "room-6", "svc-peel", "Peel 30"));

        var held = (await w.Get("c1"))!;
        held.ApplyTransition(AppointmentStatus.Cancelled, w.Clock.UtcNow);
        Assert.True(await w.Repo.TryUpdateAsync(held, expectedRowVersion: 1));

        // The constraint's WHERE clause excludes Cancelled, so the slot is
        // genuinely reusable rather than blocked by a dead row.
        await w.AddAsync(PostgresWorld.Appointment("c2", AppointmentStatus.Confirmed,
            slot, 30, "prov-priya", "room-6", "svc-peel", "Peel 30", guestId: "guest-0002"));
    }

    [RequiresPostgres]
    public async Task Touching_intervals_are_allowed()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await using var w = new PostgresWorld(fixture);

        var slot = w.Day.AddHours(11);
        await w.AddAsync(PostgresWorld.Appointment("t1", AppointmentStatus.Confirmed,
            slot, 30, "prov-priya", "room-7", "svc-peel", "Peel 30"));

        // Half-open range: an appointment starting exactly when another ends
        // does not overlap it. A closed range would refuse back-to-back
        // bookings, which is normal operation.
        await w.AddAsync(PostgresWorld.Appointment("t2", AppointmentStatus.Confirmed,
            slot.AddMinutes(30), 30, "prov-priya", "room-7", "svc-peel", "Peel 30", guestId: "guest-0002"));
    }

    [RequiresPostgres]
    public async Task Another_tenant_may_book_its_own_rooms_at_the_same_time()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(PostgresWorld.Tenant, PostgresWorld.Property);
        await fixture.SeedReferenceAsync("tenant-other", "prop-other");
        await using var w = new PostgresWorld(fixture);

        var slot = w.Day.AddHours(12);
        await w.AddAsync(PostgresWorld.Appointment("iso1", AppointmentStatus.Confirmed,
            slot, 30, "prov-priya", "room-5", "svc-peel", "Peel 30"));

        // The other tenant's scope, its own room, the same instant: no conflict,
        // because the exclusion is per tenant and property, and nothing of ours
        // is visible to them.
        await using var other = new PostgresWorld(fixture, tenant: "tenant-other", property: "prop-other");
        var theirs = Spms.Modules.Scheduling.Domain.Appointment.Rehydrate(PostgresWorld.Appointment("iso2", AppointmentStatus.Confirmed,
            slot, 30, null, "prop-other/room-5", "tenant-other/svc-peel", "Peel 30", guestId: "tenant-other/guest-0000")
            .Snapshot() with { TenantId = "tenant-other", PropertyId = "prop-other" });
        Assert.True(await other.Repo.TryAddAsync(theirs));
        Assert.Null(await other.Repo.GetAsync("tenant-other", "prop-other", "iso1"));
    }
}
