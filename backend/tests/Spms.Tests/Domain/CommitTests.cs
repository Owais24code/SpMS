using Spms.SharedKernel;
using Spms.Modules.Scheduling.Domain;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Domain;

public class PreflightTokenTests
{
    [Fact]
    public async Task Commit_applies_the_move_and_bumps_the_version()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        var r = await w.Commit("a1", pf.Token);
        Assert.Equal(SchedulingService.CommitOutcome.Committed, r.Outcome);
        Assert.Equal(2, r.Appointment!.RowVersion);
        Assert.Equal(w.Day.AddHours(20), r.Appointment.StartUtc);
        Assert.Equal("room-free", r.Appointment.RoomId);
    }

    [Fact]
    public async Task A_token_is_single_use()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        Assert.Equal(SchedulingService.CommitOutcome.Committed, (await w.Commit("a1", pf.Token)).Outcome);
        Assert.Equal(SchedulingService.CommitOutcome.TokenInvalid, (await w.Commit("a1", pf.Token)).Outcome);
    }

    [Fact]
    public async Task Refuse_then_retry_keeps_the_token_usable()
    {
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("busy", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-lena", "room-other"));

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.True(pf.RequiresReason);

        Assert.Equal(SchedulingService.CommitOutcome.ReasonRequired, (await w.Commit("a1", pf.Token)).Outcome);

        // Consuming the token before validating made this second attempt fail
        // with TokenInvalid, so the prompt CON-001 exists to collect was a
        // dead end.
        Assert.Equal(SchedulingService.CommitOutcome.Committed,
            (await w.Commit("a1", pf.Token, reason: "Guest requested the same therapist")).Outcome);
    }

    [Fact]
    public async Task A_hard_conflict_refusal_does_not_burn_the_token_either()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a2"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(9).AddMinutes(30), "prov-priya", "room-1");

        Assert.Equal(SchedulingService.CommitOutcome.HardConflict, (await w.Commit("a2", pf.Token)).Outcome);
        Assert.Equal(SchedulingService.CommitOutcome.HardConflict, (await w.Commit("a2", pf.Token)).Outcome);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        w.Clock.Advance(SchedulingService.PreflightTtl + TimeSpan.FromSeconds(1));
        Assert.Equal(SchedulingService.CommitOutcome.TokenInvalid, (await w.Commit("a1", pf.Token)).Outcome);
    }

    [Fact]
    public async Task A_token_cannot_be_redirected_at_another_appointment()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        // The route id used to be ignored entirely, so a client bug
        // rescheduled whichever appointment the token named rather than the
        // one in the URL.
        Assert.Equal(SchedulingService.CommitOutcome.AppointmentMismatch, (await w.Commit("a2", pf.Token)).Outcome);
        Assert.Equal(1, (await w.Get("a1"))!.RowVersion);
    }

    [Fact]
    public async Task A_token_from_another_tenant_is_not_found()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        var r = await w.Scheduling.CommitMoveAsync(
            "tenant-other", SchedulingWorld.Property, "a1", pf.Token, null, "corr");
        Assert.Equal(SchedulingService.CommitOutcome.TokenInvalid, r.Outcome);

        // And the rightful owner's token survived the attempt.
        Assert.Equal(SchedulingService.CommitOutcome.Committed, (await w.Commit("a1", pf.Token)).Outcome);
    }

    [Fact]
    public async Task A_token_cannot_be_committed_at_another_property()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        // Blocked by a designed check on the token's own PropertyId, not
        // merely incidentally by the repository's property filter.
        var r = await w.Scheduling.CommitMoveAsync(
            SchedulingWorld.Tenant, SchedulingWorld.Elsewhere, "a1", pf.Token, null, "corr");
        Assert.Equal(SchedulingService.CommitOutcome.TokenInvalid, r.Outcome);
    }

    [Fact]
    public async Task Commit_refuses_a_version_that_moved_under_it()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        var interloper = (await w.Get("a1"))!;
        interloper.ApplyTransition(AppointmentStatus.CheckedIn, w.Clock.UtcNow);
        await w.Repo.TryUpdateAsync(interloper, expectedRowVersion: 1);

        Assert.Equal(SchedulingService.CommitOutcome.StaleVersion, (await w.Commit("a1", pf.Token)).Outcome);
    }

    [Fact]
    public async Task Commit_refuses_an_appointment_that_became_terminal()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        var interloper = (await w.Get("a1"))!;
        interloper.ApplyTransition(AppointmentStatus.Cancelled, w.Clock.UtcNow);
        await w.Repo.TryUpdateAsync(interloper, expectedRowVersion: 1);

        Assert.Equal(SchedulingService.CommitOutcome.NotReschedulable, (await w.Commit("a1", pf.Token)).Outcome);
    }

    [Fact]
    public async Task Only_one_of_many_concurrent_commits_succeeds()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => w.Commit("a1", pf.Token))));
        Assert.Single(attempts, r => r.Outcome == SchedulingService.CommitOutcome.Committed);
        Assert.Equal(2, (await w.Get("a1"))!.RowVersion);
    }
}

/// <summary>
/// The blocker three independent reviews converged on: the commit path trusted
/// a conflict snapshot up to 90 seconds old and never looked again.
/// </summary>
public class CommitRevalidationTests
{
    [Fact]
    public async Task A_room_taken_after_the_token_was_minted_refuses_the_commit()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a2"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-priya", "room-late");
        Assert.True(pf.CommitAllowed);
        Assert.Empty(pf.Conflicts);

        // Somebody else books that room inside the window. Nothing bumps a2's
        // row version, so the optimistic check cannot see it: the invariant is
        // over the SET of appointments sharing the room.
        w.Add(SchedulingWorld.Appointment("interloper", AppointmentStatus.Confirmed,
            w.Day.AddHours(20), 60, "prov-marco", "room-late"));

        var r = await w.Commit("a2", pf.Token);
        Assert.Equal(SchedulingService.CommitOutcome.BoardChanged, r.Outcome);
        Assert.Contains(r.Conflicts, c => c.Code == "CON-002");
        Assert.Equal(1, (await w.Get("a2"))!.RowVersion);
    }

    [Fact]
    public async Task Two_clean_tokens_for_the_same_slot_cannot_both_commit()
    {
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("m1", AppointmentStatus.Confirmed, w.Day.AddHours(10), 60, "prov-marco", "room-a"));
        w.Add(SchedulingWorld.Appointment("m2", AppointmentStatus.Confirmed, w.Day.AddHours(11), 60, "prov-marco", "room-b"));

        // Both preflights are issued before either commits, so each
        // legitimately sees the shared room free.
        var pf1 = await w.Preflight((await w.Get("m1"))!, w.Day.AddHours(20), "prov-marco", "room-shared");
        var pf2 = await w.Preflight((await w.Get("m2"))!, w.Day.AddHours(20), "prov-marco", "room-shared");
        Assert.True(pf1.CommitAllowed);
        Assert.True(pf2.CommitAllowed);

        Assert.Equal(SchedulingService.CommitOutcome.Committed, (await w.Commit("m1", pf1.Token)).Outcome);

        // Trusting the snapshot let this land too, putting two treatments in
        // one room — the conflict the register calls physically impossible.
        Assert.Equal(SchedulingService.CommitOutcome.BoardChanged, (await w.Commit("m2", pf2.Token)).Outcome);
        Assert.Equal("room-b", (await w.Get("m2"))!.RoomId);
    }

    [Fact]
    public async Task A_soft_conflict_appearing_after_the_decision_is_resurfaced()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.False(pf.RequiresReason);

        // A provider overlap the operator was never shown.
        w.Add(SchedulingWorld.Appointment("busy", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-lena", "room-other"));

        var r = await w.Commit("a1", pf.Token, reason: "a reason for different facts");
        Assert.Equal(SchedulingService.CommitOutcome.BoardChanged, r.Outcome);
        Assert.Contains(r.Conflicts, c => c.Code == "CON-001");
    }

    [Fact]
    public async Task A_board_that_did_not_change_still_commits()
    {
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("busy", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-lena", "room-other"));

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.True(pf.RequiresReason);

        // Re-validation must not turn a conflict the operator DID acknowledge
        // into a refusal.
        var r = await w.Commit("a1", pf.Token, reason: "Guest asked for Lena");
        Assert.Equal(SchedulingService.CommitOutcome.Committed, r.Outcome);
    }
}

public class CreateTests
{
    private static SchedulingService.NewBooking Booking(
        string id, string serviceId, DateTimeOffset start, string? provider, string? room,
        string guestId = "guest-new") =>
        new(id, guestId, "Guest New", serviceId, start, provider, room, $"AAR{id}", "corr-test");

    [Fact]
    public async Task Create_evaluates_the_conflict_rules()
    {
        var w = new SchedulingWorld();
        // Straight into the room a1 already occupies. Create used to bypass
        // the conflict rules entirely, so this was bookable.
        var r = await w.Scheduling.CreateAsync(SchedulingWorld.Tenant, SchedulingWorld.Property,
            Booking("new", "svc-swedish", w.Day.AddHours(9).AddMinutes(30), "prov-marco", "room-1"),
            null);

        Assert.Equal(SchedulingService.CreateOutcome.HardConflict, r.Outcome);
        Assert.Contains(r.Conflicts, c => c.Code == "CON-002");
        Assert.Null(await w.Get("new"));
    }

    [Fact]
    public async Task Create_demands_a_reason_for_a_soft_conflict()
    {
        var w = new SchedulingWorld();
        var booking = Booking("new", "svc-swedish", w.Day.AddHours(9).AddMinutes(30), "prov-lena", "room-free");

        var refused = await w.Scheduling.CreateAsync(SchedulingWorld.Tenant, SchedulingWorld.Property, booking, null);
        Assert.Equal(SchedulingService.CreateOutcome.ReasonRequired, refused.Outcome);

        var accepted = await w.Scheduling.CreateAsync(
            SchedulingWorld.Tenant, SchedulingWorld.Property, booking, "Guest asked for Lena");
        Assert.Equal(SchedulingService.CreateOutcome.Created, accepted.Outcome);
    }

    [Fact]
    public async Task Create_refuses_an_unknown_service()
    {
        var w = new SchedulingWorld();
        var r = await w.Scheduling.CreateAsync(SchedulingWorld.Tenant, SchedulingWorld.Property,
            Booking("new", "svc-nonexistent", w.Day.AddHours(20), null, "room-free"), null);

        Assert.Equal(SchedulingService.CreateOutcome.UnknownService, r.Outcome);
    }

    [Fact]
    public async Task Create_refuses_an_unknown_property()
    {
        var w = new SchedulingWorld();
        var r = await w.Scheduling.CreateAsync(SchedulingWorld.Tenant, "prop-nowhere",
            Booking("new", "svc-peel", w.Day.AddHours(20), null, "room-free"), null);

        Assert.Equal(SchedulingService.CreateOutcome.UnknownProperty, r.Outcome);
    }

    [Fact]
    public async Task Create_takes_the_services_duration_not_the_callers()
    {
        var w = new SchedulingWorld();
        var r = await w.Scheduling.CreateAsync(SchedulingWorld.Tenant, SchedulingWorld.Property,
            Booking("new", "svc-peel", w.Day.AddHours(20), "prov-priya", "room-free"), null);

        Assert.Equal(SchedulingService.CreateOutcome.Created, r.Outcome);
        Assert.Equal(30, r.Appointment!.DurationMinutes);
        Assert.Equal("Peel 30", r.Appointment.ServiceName);
    }

    [Fact]
    public async Task Concurrent_creates_into_one_room_admit_exactly_one()
    {
        var w = new SchedulingWorld();
        var slot = w.Day.AddHours(20);

        // Evaluation and insertion were two separate operations, so every one
        // of these saw the room free and several succeeded. Note this passes
        // here because the in-memory repository serialises writes; the real
        // guarantee is the database's exclusion constraint, proven in the
        // Postgres suite.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            w.Scheduling.CreateAsync(SchedulingWorld.Tenant, SchedulingWorld.Property,
                Booking($"race{i}", "svc-hotstone", slot, "prov-marco", "room-race", $"guest-{i}"),
                null))));

        Assert.Single(attempts, r => r.Outcome == SchedulingService.CreateOutcome.Created);

        var inRoom = await w.Repo.ListOverlappingAsync(SchedulingWorld.Tenant, SchedulingWorld.Property, slot, slot.AddHours(1));
        Assert.Single(inRoom, a => a.RoomId == "room-race");
    }
}

public class TransitionAndAuditTests
{
    [Fact]
    public async Task A_commit_records_hashes_conflict_codes_and_the_reason()
    {
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("busy", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-lena", "room-other"));

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        Assert.Equal(SchedulingService.CommitOutcome.Committed,
            (await w.Commit("a1", pf.Token, reason: "Guest asked for Lena")).Outcome);

        var entry = w.Audit.Recent(10).First();
        Assert.Equal("appointment.move", entry.Action);
        Assert.Equal("Guest asked for Lena", entry.ReasonText);
        Assert.Equal("a1", entry.EntityId);
        Assert.Contains("CON-001", entry.ConflictCodes!);
        Assert.NotNull(entry.BeforeHash);
        Assert.NotNull(entry.AfterHash);
        Assert.Equal(64, entry.AfterHash!.Length);
        Assert.NotEqual(entry.BeforeHash, entry.AfterHash);

        // The move is announced in the same unit of work.
        Assert.Contains(w.Outbox.Events, e => e.EventType == EventTypes.AppointmentRescheduled);
    }

    [Fact]
    public async Task A_transition_records_the_target_status_not_a_resolution()
    {
        var w = new SchedulingWorld();
        var r = await w.Scheduling.TransitionAsync(
            SchedulingWorld.Tenant, SchedulingWorld.Property, "a1", AppointmentStatus.CheckedIn, 1, null, "corr");
        Assert.Equal(SchedulingService.TransitionOutcome.Applied, r.Outcome);

        var entry = w.Audit.Recent(10).First();
        // Both ends of the transition are recorded, in their own fields.
        Assert.Equal("Confirmed", entry.FromStatus);
        Assert.Equal("CheckedIn", entry.ToStatus);
        Assert.Null(entry.ConflictCodes);
    }

    [Fact]
    public async Task A_transition_refuses_a_stale_version()
    {
        var w = new SchedulingWorld();
        var r = await w.Scheduling.TransitionAsync(
            SchedulingWorld.Tenant, SchedulingWorld.Property, "a1", AppointmentStatus.CheckedIn, 99, null, "corr");
        Assert.Equal(SchedulingService.TransitionOutcome.StaleVersion, r.Outcome);
    }

    [Fact]
    public async Task A_transition_refuses_an_illegal_target_and_says_what_is_allowed()
    {
        var w = new SchedulingWorld();
        var r = await w.Scheduling.TransitionAsync(
            SchedulingWorld.Tenant, SchedulingWorld.Property, "a1", AppointmentStatus.Completed, 1, null, "corr");
        Assert.Equal(SchedulingService.TransitionOutcome.Illegal, r.Outcome);
        Assert.NotEmpty(r.Allowed);
    }
}
