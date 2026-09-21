using Spms.Api.Endpoints;
using Spms.Api.Infrastructure;
using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;
using Spms.Infrastructure.InMemory;
using Spms.Tests;

var runner = new Runner();

/* ============================ state machine ============================ */

runner.Test("Draft can be confirmed", () =>
    Assert.True(AppointmentTransitions.CanMove(AppointmentStatus.Draft, AppointmentStatus.Confirmed)));

runner.Test("Draft cannot jump straight to Completed", () =>
    Assert.False(AppointmentTransitions.CanMove(AppointmentStatus.Draft, AppointmentStatus.Completed)));

runner.Test("Completed is terminal", () =>
    Assert.Equal(0, AppointmentTransitions.NextFrom(AppointmentStatus.Completed).Count));

runner.Test("Cancelled cannot be revived", () =>
    Assert.False(AppointmentTransitions.CanMove(AppointmentStatus.Cancelled, AppointmentStatus.Confirmed)));

runner.Test("Every status has a transition entry", () =>
{
    // A status added to the enum but not the table would silently allow
    // nothing, which reads as a data bug rather than a missing rule.
    foreach (var s in Enum.GetValues<AppointmentStatus>())
        Assert.NotNull(AppointmentTransitions.NextFrom(s), s.ToString());
});

runner.Test("The aggregate refuses an illegal transition itself", () =>
{
    // Enforcement used to be one `if` in the HTTP endpoint, so any second
    // caller could move Draft straight to Completed.
    var a = Fixture.Appointment("t", AppointmentStatus.Draft, DateTimeOffset.UtcNow, 60);
    Assert.Throws<IllegalTransitionException>(() => a.ApplyTransition(AppointmentStatus.Completed, DateTimeOffset.UtcNow));
    Assert.Equal(AppointmentStatus.Draft, a.Status);
    Assert.Equal(1, a.RowVersion);
});

runner.Test("Terminal statuses are not reschedulable", () =>
{
    foreach (var s in new[] { AppointmentStatus.Completed, AppointmentStatus.Cancelled, AppointmentStatus.NoShow })
        Assert.False(Fixture.Appointment("x", s, DateTimeOffset.UtcNow, 60).IsReschedulable, s.ToString());

    foreach (var s in new[] { AppointmentStatus.Draft, AppointmentStatus.Confirmed, AppointmentStatus.InService })
        Assert.True(Fixture.Appointment("x", s, DateTimeOffset.UtcNow, 60).IsReschedulable, s.ToString());
});

runner.Test("The aggregate refuses to move a terminal appointment", () =>
{
    var a = Fixture.Appointment("x", AppointmentStatus.Cancelled, DateTimeOffset.UtcNow, 60);
    Assert.Throws<InvalidOperationException>(() =>
        a.ApplyMove(DateTimeOffset.UtcNow.AddHours(1), Assignment.Unchanged, DateTimeOffset.UtcNow));
});

runner.Test("A non-positive duration is refused at construction", () =>
{
    // A zero duration makes EndUtc <= StartUtc, which turns off overlap
    // detection for that row entirely.
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        Spms.Domain.Scheduling.Appointment.Create(
            "z", "t", "p", "UTC", "g", "G", "svc-peel", "Peel", 0,
            null, null, DateTimeOffset.UtcNow, null, "c", DateTimeOffset.UtcNow));
});

/* =========================== the aggregate =========================== */

runner.Test("An assignment distinguishes unchanged from cleared", () =>
{
    var now = DateTimeOffset.UtcNow;
    var a = Fixture.Appointment("x", AppointmentStatus.Confirmed, now, 60, "prov-lena", "room-1");

    a.ApplyMove(now.AddHours(1), Assignment.Unchanged, now);
    Assert.Equal("prov-lena", a.ProviderId!);
    Assert.Equal("room-1", a.RoomId!);

    // A nullable pair could not express this, so unassigning was impossible
    // through the API with no error to say so.
    a.ApplyMove(now.AddHours(2), new Assignment(ProviderChanged: true, null, RoomChanged: false, null), now);
    Assert.Null(a.ProviderId);
    Assert.Equal("room-1", a.RoomId!);
});

runner.Test("Overlaps is the one definition of an intersection", () =>
{
    var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    var a = Fixture.Appointment("x", AppointmentStatus.Confirmed, now, 60);

    Assert.True(a.Overlaps(now.AddMinutes(30), now.AddMinutes(90)));
    Assert.False(a.Overlaps(now.AddMinutes(60), now.AddMinutes(120)), "touching is not overlapping");
    Assert.False(a.Overlaps(now.AddMinutes(-60), now), "touching on the left is not overlapping");
});

/* ======================= optimistic concurrency ======================= */

await runner.TestAsync("ApplyMove bumps the row version itself", async () =>
{
    var f = new Fixture();
    var a = await f.Get("a1");
    Assert.Equal(1, a!.RowVersion);
    a.ApplyMove(a.StartUtc.AddHours(1), Assignment.Unchanged, f.Clock.UtcNow);
    Assert.Equal(2, a.RowVersion);
});

await runner.TestAsync("TryUpdate rejects a stale row version", async () =>
{
    var f = new Fixture();
    var first = await f.Get("a1");
    first!.ApplyMove(first.StartUtc.AddHours(1), Assignment.Unchanged, f.Clock.UtcNow);
    Assert.True(await f.Repo.TryUpdateAsync(first, expectedRowVersion: 1));

    var stale = await f.Get("a1");
    Assert.Equal(2, stale!.RowVersion);
    Assert.False(await f.Repo.TryUpdateAsync(stale, expectedRowVersion: 1));
});

await runner.TestAsync("Concurrent TryUpdate lets exactly one writer win", async () =>
{
    var f = new Fixture();
    var writers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
    {
        var mine = await f.Get("a1");
        mine!.ApplyMove(mine.StartUtc.AddMinutes(5), Assignment.Unchanged, f.Clock.UtcNow);
        return await f.Repo.TryUpdateAsync(mine, expectedRowVersion: 1);
    }));

    var results = await Task.WhenAll(writers);
    Assert.Equal(1, results.Count(won => won));
});

await runner.TestAsync("Repository hands out copies, not references", async () =>
{
    var f = new Fixture();
    var first = await f.Get("a1");
    first!.ApplyMove(first.StartUtc.AddDays(3), Assignment.Set("prov-marco", "room-9"), f.Clock.UtcNow);
    var second = await f.Get("a1");
    Assert.Equal(1, second!.RowVersion);
    Assert.Equal("room-1", second.RoomId!);
});

await runner.TestAsync("TryAdd refuses a duplicate id", async () =>
{
    var f = new Fixture();
    var dup = Fixture.Appointment("a1", AppointmentStatus.Draft, f.Clock.UtcNow, 60, "prov-lena", "room-8");
    Assert.False(await f.Repo.TryAddAsync(dup));
    Assert.Equal("room-1", (await f.Get("a1"))!.RoomId!);
});

/* ========================= property isolation ========================= */

await runner.TestAsync("Get refuses a record from another property", async () =>
{
    var f = new Fixture();
    Assert.NotNull(await f.Repo.GetAsync(Fixture.Tenant, Fixture.Property, "a1"));
    Assert.Null(await f.Repo.GetAsync(Fixture.Tenant, "prop-elsewhere", "a1"));
});

await runner.TestAsync("Get refuses a record from another tenant", async () =>
{
    var f = new Fixture();
    Assert.Null(await f.Repo.GetAsync("tenant-other", Fixture.Property, "a1"));
});

await runner.TestAsync("Overlap query is scoped to one property", async () =>
{
    var f = new Fixture();
    var here = await f.Repo.ListOverlappingAsync(Fixture.Tenant, Fixture.Property, f.Day, f.Day.AddDays(1));
    var elsewhere = await f.Repo.ListOverlappingAsync(Fixture.Tenant, "prop-elsewhere", f.Day, f.Day.AddDays(1));

    // x1 sits at prop-elsewhere in room-1 at the same hour as a1. Each query
    // must see only its own property's side of that collision.
    Assert.False(here.Any(a => a.AppointmentId == "x1"), "another property's record leaked into the board");
    Assert.True(here.Any(a => a.AppointmentId == "a1"));
    Assert.Equal(1, elsewhere.Count);
    Assert.Equal("x1", elsewhere[0].AppointmentId);
});

await runner.TestAsync("Another property's booking is not a conflict", async () =>
{
    var f = new Fixture();
    // x1 holds room-1 at 09:00 — but at prop-elsewhere.
    var target = (await f.Get("a2"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(9).AddMinutes(30), "prov-priya", "room-1");

    // a1 at this property does conflict; x1 must not add a second one.
    Assert.Equal(1, pf.Conflicts.Count(c => c.Code == "CON-002"));
});

/* ======================= the midnight-span defect ======================= */

await runner.TestAsync("An appointment spanning midnight is found from the next day", async () =>
{
    var f = new Fixture();
    // 23:30 for 90 minutes: starts on day one, ends 01:00 on day two.
    f.Add(Fixture.Appointment("night", AppointmentStatus.Confirmed,
        f.Day.AddHours(23).AddMinutes(30), 90, "prov-lena", "room-night"));

    var nextDay = await f.Repo.ListOverlappingAsync(Fixture.Tenant, Fixture.Property, f.Day.AddDays(1), f.Day.AddDays(2));
    Assert.True(nextDay.Any(a => a.AppointmentId == "night"),
        "a day-bounded query missed it, which is how the board double-booked across midnight");
});

await runner.TestAsync("A move across midnight still sees the other side's conflicts", async () =>
{
    var f = new Fixture();
    f.Add(Fixture.Appointment("night", AppointmentStatus.Confirmed,
        f.Day.AddHours(23).AddMinutes(30), 90, "prov-lena", "room-night"));
    f.Add(Fixture.Appointment("mover", AppointmentStatus.Confirmed,
        f.Day.AddHours(10), 60, "prov-marco", "room-spare"));

    var mover = (await f.Get("mover"))!;
    var pf = await f.Preflight(mover, f.Day.AddDays(1).AddMinutes(15), "prov-marco", "room-night");

    Assert.True(pf.Conflicts.Any(c => c.Code == "CON-002"),
        "the room overlap on the far side of midnight was invisible to the old day query");
});

/* ========================= conflict detection ========================= */

await runner.TestAsync("Room double-booking is a hard conflict", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a2"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(9).AddMinutes(30), "prov-priya", "room-1");

    Assert.True(pf.Conflicts.Any(c => c.Code == "CON-002"));
    Assert.False(pf.CommitAllowed);
});

await runner.TestAsync("Provider overlap is soft and overridable", async () =>
{
    var f = new Fixture();
    // The provider stays qualified for the service, so CON-001 is the only
    // conflict and the soft path is actually reachable.
    f.Add(Fixture.Appointment("busy", AppointmentStatus.Confirmed, f.Day.AddHours(20), 60, "prov-lena", "room-other"));

    var target = (await f.Get("a1"))!;   // svc-deep, prov-lena is qualified
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    Assert.True(pf.Conflicts.Any(c => c.Code == "CON-001"));
    Assert.False(pf.Conflicts.Any(c => !c.Overridable), "expected no hard conflict here");
    Assert.True(pf.CommitAllowed);
    Assert.True(pf.RequiresReason);
});

await runner.TestAsync("An unqualified provider is a hard conflict", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;   // svc-deep
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-priya", "room-free");

    Assert.True(pf.Conflicts.Any(c => c.Rule == "provider-not-qualified"));
    Assert.False(pf.CommitAllowed);
});

await runner.TestAsync("An unknown provider fails closed, not open", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-newhire", "room-free");

    // The previous version treated an absent qualification record as
    // qualified, which inverted a licensing rule for every new hire and typo.
    Assert.True(pf.Conflicts.Any(c => c.Rule == "provider-unknown"), "an unknown provider was silently allowed");
    Assert.False(pf.CommitAllowed);
});

await runner.TestAsync("Guest overlap keys on identity, not the display alias", async () =>
{
    var f = new Fixture();
    // Same guest id, different alias text — the alias must not be what matches.
    f.Add(Fixture.Appointment("twin", AppointmentStatus.Confirmed, f.Day.AddHours(9), 60, "prov-marco", "room-7",
        guestId: Fixture.GuestOfA1, alias: "A totally different label"));

    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(9).AddMinutes(15), "prov-lena", "room-free");

    Assert.True(pf.Conflicts.Any(c => c.Code == "CON-005"));
});

await runner.TestAsync("A clean slot produces no conflicts", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    Assert.Equal(0, pf.Conflicts.Count);
    Assert.True(pf.CommitAllowed);
    Assert.False(pf.RequiresReason);
});

await runner.TestAsync("Cancelled and no-show neighbours do not block", async () =>
{
    var f = new Fixture();
    f.Add(Fixture.Appointment("gone", AppointmentStatus.Cancelled, f.Day.AddHours(20), 60, "prov-lena", "room-free"));
    f.Add(Fixture.Appointment("noshow", AppointmentStatus.NoShow, f.Day.AddHours(20), 60, "prov-lena", "room-free"));

    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");
    Assert.Equal(0, pf.Conflicts.Count);
});

await runner.TestAsync("A completed neighbour still occupied the room", async () =>
{
    var f = new Fixture();
    f.Add(Fixture.Appointment("done", AppointmentStatus.Completed, f.Day.AddHours(20), 60, "prov-marco", "room-done"));

    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-done");
    Assert.True(pf.Conflicts.Any(c => c.Code == "CON-002"), "a finished treatment still used the room");
});

await runner.TestAsync("Conflicts of the same rule are deduplicated", async () =>
{
    var f = new Fixture();
    f.Add(Fixture.Appointment("n1", AppointmentStatus.Confirmed, f.Day.AddHours(20), 30, "prov-marco", "room-busy"));
    f.Add(Fixture.Appointment("n2", AppointmentStatus.Confirmed, f.Day.AddHours(20).AddMinutes(30), 30, "prov-marco", "room-busy"));

    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-busy");
    Assert.Equal(1, pf.Conflicts.Count(c => c.Code == "CON-002"));
});

/* ===================== CON-004 buffer boundaries ===================== */

await runner.TestAsync("Room turnover after a neighbour: 0, 14 and 15 minute gaps", async () =>
{
    // Baseline is 15 minutes, so 0 and 14 breach and 15 does not.
    foreach (var (gapMinutes, expectConflict) in new[] { (0, true), (14, true), (15, false) })
    {
        var f = new Fixture();
        var neighbour = Fixture.Appointment("nb", AppointmentStatus.Confirmed, f.Day.AddHours(20), 60, "prov-marco", "room-turn");
        f.Add(neighbour);

        var target = (await f.Get("a1"))!;
        var pf = await f.Preflight(target, neighbour.EndUtc.AddMinutes(gapMinutes), "prov-lena", "room-turn");

        Assert.Equal(expectConflict, pf.Conflicts.Any(c => c.Rule == "room-turnover"), $"gap {gapMinutes}m");
    }
});

await runner.TestAsync("Room turnover BEFORE a neighbour: 0, 14 and 15 minute gaps", async () =>
{
    // The before-side branch of the gap calculation was never exercised at a
    // boundary; only the after side was.
    foreach (var (gapMinutes, expectConflict) in new[] { (0, true), (14, true), (15, false) })
    {
        var f = new Fixture();
        var neighbour = Fixture.Appointment("nb", AppointmentStatus.Confirmed, f.Day.AddHours(20), 60, "prov-marco", "room-turn");
        f.Add(neighbour);

        var target = (await f.Get("a1"))!;   // 90 minutes
        var start = neighbour.StartUtc.AddMinutes(-gapMinutes).AddMinutes(-target.DurationMinutes);
        var pf = await f.Preflight(target, start, "prov-lena", "room-turn");

        Assert.Equal(expectConflict, pf.Conflicts.Any(c => c.Rule == "room-turnover"), $"gap {gapMinutes}m before");
    }
});

await runner.TestAsync("Provider transition: 0, 9 and 10 minute gaps", async () =>
{
    // Baseline is 10 minutes. This buffer was previously never evaluated.
    foreach (var (gapMinutes, expectConflict) in new[] { (0, true), (9, true), (10, false) })
    {
        var f = new Fixture();
        var neighbour = Fixture.Appointment("nb", AppointmentStatus.Confirmed, f.Day.AddHours(20), 60, "prov-lena", "room-other");
        f.Add(neighbour);

        var target = (await f.Get("a1"))!;
        var pf = await f.Preflight(target, neighbour.EndUtc.AddMinutes(gapMinutes), "prov-lena", "room-free");

        Assert.Equal(expectConflict, pf.Conflicts.Any(c => c.Rule == "provider-transition"), $"gap {gapMinutes}m");
    }
});

await runner.TestAsync("Both CON-004 breaches are reported, not just the first", async () =>
{
    var f = new Fixture();
    // One neighbour sharing BOTH the room and the provider, 5 minutes away:
    // under the 15-minute room buffer and under the 10-minute provider buffer.
    var neighbour = Fixture.Appointment("nb", AppointmentStatus.Confirmed, f.Day.AddHours(20), 60, "prov-lena", "room-both");
    f.Add(neighbour);

    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, neighbour.EndUtc.AddMinutes(5), "prov-lena", "room-both");

    // Deduplicating by code collapsed these two different rules into one, so
    // the operator was told housekeeping was tight and never told the
    // therapist had no transition time — and the offered resolutions did not
    // fix the breach they were not shown.
    Assert.True(pf.Conflicts.Any(c => c.Rule == "room-turnover"), "room turnover missing");
    Assert.True(pf.Conflicts.Any(c => c.Rule == "provider-transition"), "provider transition was dropped");
    Assert.Equal(2, pf.Conflicts.Count(c => c.Code == "CON-004"));
});

/* ===================== hard plus soft interaction ===================== */

await runner.TestAsync("A hard conflict suppresses the reason prompt", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(9).AddMinutes(30), "prov-priya", "room-1");

    Assert.False(pf.CommitAllowed);
    Assert.False(pf.RequiresReason, "RequiresReason must be false when CommitAllowed is false");
});

/* ======================== the preflight token ======================== */

await runner.TestAsync("Commit applies the move and bumps the version", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    var r = await f.Commit("a1", pf.Token);
    Assert.Equal(SchedulingService.CommitOutcome.Committed, r.Outcome);
    Assert.Equal(2, r.Appointment!.RowVersion);
    Assert.Equal(f.Day.AddHours(20), r.Appointment.StartUtc);
    Assert.Equal("room-free", r.Appointment.RoomId!);
});

await runner.TestAsync("A token is single use", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    Assert.Equal(SchedulingService.CommitOutcome.Committed, (await f.Commit("a1", pf.Token)).Outcome);
    Assert.Equal(SchedulingService.CommitOutcome.TokenInvalid, (await f.Commit("a1", pf.Token)).Outcome);
});

await runner.TestAsync("Refuse-then-retry keeps the token usable", async () =>
{
    var f = new Fixture();
    f.Add(Fixture.Appointment("busy", AppointmentStatus.Confirmed, f.Day.AddHours(20), 60, "prov-lena", "room-other"));

    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");
    Assert.True(pf.RequiresReason);

    Assert.Equal(SchedulingService.CommitOutcome.ReasonRequired, (await f.Commit("a1", pf.Token)).Outcome);

    // Consuming the token before validating made this second attempt fail
    // with TokenInvalid, so the prompt CON-001 exists to collect was a dead end.
    Assert.Equal(SchedulingService.CommitOutcome.Committed,
        (await f.Commit("a1", pf.Token, reason: "Guest requested the same therapist")).Outcome);
});

await runner.TestAsync("A hard conflict refusal does not burn the token either", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a2"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(9).AddMinutes(30), "prov-priya", "room-1");

    Assert.Equal(SchedulingService.CommitOutcome.HardConflict, (await f.Commit("a2", pf.Token)).Outcome);
    Assert.Equal(SchedulingService.CommitOutcome.HardConflict, (await f.Commit("a2", pf.Token)).Outcome);
});

await runner.TestAsync("An expired token is refused", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    f.Clock.Advance(SchedulingService.PreflightTtl + TimeSpan.FromSeconds(1));
    Assert.Equal(SchedulingService.CommitOutcome.TokenInvalid, (await f.Commit("a1", pf.Token)).Outcome);
});

await runner.TestAsync("A token cannot be redirected at another appointment", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    // The route id used to be ignored entirely, so a client bug rescheduled
    // whichever appointment the token named rather than the one in the URL.
    Assert.Equal(SchedulingService.CommitOutcome.AppointmentMismatch, (await f.Commit("a2", pf.Token)).Outcome);
    Assert.Equal(1, (await f.Get("a1"))!.RowVersion);
});

await runner.TestAsync("A token from another tenant is not found", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    var r = await f.Scheduling.CommitMoveAsync(
        "tenant-other", Fixture.Property, "a1", pf.Token, null, "actor", "corr");
    Assert.Equal(SchedulingService.CommitOutcome.TokenInvalid, r.Outcome);

    // And the rightful owner's token survived the attempt.
    Assert.Equal(SchedulingService.CommitOutcome.Committed, (await f.Commit("a1", pf.Token)).Outcome);
});

await runner.TestAsync("A token cannot be committed at another property", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    // Blocked by a designed check on the token's own PropertyId, not merely
    // incidentally by the repository's property filter.
    var r = await f.Scheduling.CommitMoveAsync(
        Fixture.Tenant, "prop-elsewhere", "a1", pf.Token, null, "actor", "corr");
    Assert.Equal(SchedulingService.CommitOutcome.TokenInvalid, r.Outcome);
});

await runner.TestAsync("Commit refuses a version that moved under it", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    var interloper = (await f.Get("a1"))!;
    interloper.ApplyTransition(AppointmentStatus.CheckedIn, f.Clock.UtcNow);
    await f.Repo.TryUpdateAsync(interloper, expectedRowVersion: 1);

    Assert.Equal(SchedulingService.CommitOutcome.StaleVersion, (await f.Commit("a1", pf.Token)).Outcome);
});

await runner.TestAsync("Commit refuses an appointment that became terminal", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    var interloper = (await f.Get("a1"))!;
    interloper.ApplyTransition(AppointmentStatus.Cancelled, f.Clock.UtcNow);
    await f.Repo.TryUpdateAsync(interloper, expectedRowVersion: 1);

    Assert.Equal(SchedulingService.CommitOutcome.NotReschedulable, (await f.Commit("a1", pf.Token)).Outcome);
});

await runner.TestAsync("Only one of many concurrent commits succeeds", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.Commit("a1", pf.Token))));
    Assert.Equal(1, attempts.Count(r => r.Outcome == SchedulingService.CommitOutcome.Committed));
    Assert.Equal(2, (await f.Get("a1"))!.RowVersion);
});

/* ================= commit-time re-validation (the blocker) ================= */

await runner.TestAsync("A room taken after the token was minted refuses the commit", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a2"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-priya", "room-late");
    Assert.True(pf.CommitAllowed);
    Assert.Equal(0, pf.Conflicts.Count);

    // Somebody else books that room inside the 90-second window. Nothing
    // bumps a2's row version, so the optimistic check cannot see it: the
    // invariant is over the SET of appointments sharing the room.
    f.Add(Fixture.Appointment("interloper", AppointmentStatus.Confirmed,
        f.Day.AddHours(20), 60, "prov-marco", "room-late"));

    var r = await f.Commit("a2", pf.Token);
    Assert.Equal(SchedulingService.CommitOutcome.BoardChanged, r.Outcome);
    Assert.True(r.Conflicts.Any(c => c.Code == "CON-002"), "the new hard conflict is not reported back");

    // And the move did not land.
    Assert.Equal(1, (await f.Get("a2"))!.RowVersion);
});

await runner.TestAsync("Two clean tokens for the same slot cannot both commit", async () =>
{
    var f = new Fixture();
    f.Add(Fixture.Appointment("m1", AppointmentStatus.Confirmed, f.Day.AddHours(10), 60, "prov-marco", "room-a"));
    f.Add(Fixture.Appointment("m2", AppointmentStatus.Confirmed, f.Day.AddHours(11), 60, "prov-marco", "room-b"));

    // Both preflights are issued before either commits, so each legitimately
    // sees the shared room free.
    var pf1 = await f.Preflight((await f.Get("m1"))!, f.Day.AddHours(20), "prov-marco", "room-shared");
    var pf2 = await f.Preflight((await f.Get("m2"))!, f.Day.AddHours(20), "prov-marco", "room-shared");
    Assert.True(pf1.CommitAllowed);
    Assert.True(pf2.CommitAllowed);

    Assert.Equal(SchedulingService.CommitOutcome.Committed, (await f.Commit("m1", pf1.Token)).Outcome);

    // Trusting the snapshot let this land too, putting two treatments in one
    // room — the conflict the register calls physically impossible.
    var second = await f.Commit("m2", pf2.Token);
    Assert.Equal(SchedulingService.CommitOutcome.BoardChanged, second.Outcome);
    Assert.Equal("room-b", (await f.Get("m2"))!.RoomId!);
});

await runner.TestAsync("A soft conflict appearing after the decision is re-surfaced", async () =>
{
    var f = new Fixture();
    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");
    Assert.False(pf.RequiresReason);

    // A provider overlap the operator was never shown.
    f.Add(Fixture.Appointment("busy", AppointmentStatus.Confirmed, f.Day.AddHours(20), 60, "prov-lena", "room-other"));

    var r = await f.Commit("a1", pf.Token, reason: "a reason for different facts");
    Assert.Equal(SchedulingService.CommitOutcome.BoardChanged, r.Outcome);
    Assert.True(r.Conflicts.Any(c => c.Code == "CON-001"));
});

await runner.TestAsync("A board that did not change still commits", async () =>
{
    var f = new Fixture();
    f.Add(Fixture.Appointment("busy", AppointmentStatus.Confirmed, f.Day.AddHours(20), 60, "prov-lena", "room-other"));

    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");
    Assert.True(pf.RequiresReason);

    // Re-validation must not turn a conflict the operator DID acknowledge
    // into a refusal.
    var r = await f.Commit("a1", pf.Token, reason: "Guest asked for Lena");
    Assert.Equal(SchedulingService.CommitOutcome.Committed, r.Outcome);
});

/* ============================= create ============================= */

await runner.TestAsync("Create evaluates the conflict rules", async () =>
{
    var f = new Fixture();
    // Straight into the room a1 already occupies. Create used to bypass the
    // conflict rules entirely, so this was bookable.
    var candidate = Fixture.Appointment("new", AppointmentStatus.Draft,
        f.Day.AddHours(9).AddMinutes(30), 60, "prov-marco", "room-1");

    var r = await f.Scheduling.CreateAsync(Fixture.Tenant, Fixture.Property, candidate, null, "actor", "corr");
    Assert.Equal(SchedulingService.CreateOutcome.HardConflict, r.Outcome);
    Assert.True(r.Conflicts.Any(c => c.Code == "CON-002"));
    Assert.Null(await f.Get("new"));
});

await runner.TestAsync("Create demands a reason for a soft conflict", async () =>
{
    var f = new Fixture();
    var candidate = Fixture.Appointment("new", AppointmentStatus.Draft,
        f.Day.AddHours(9).AddMinutes(30), 60, "prov-lena", "room-free",
        serviceId: "svc-swedish", serviceName: "Swedish 60");

    var refused = await f.Scheduling.CreateAsync(Fixture.Tenant, Fixture.Property, candidate, null, "actor", "corr");
    Assert.Equal(SchedulingService.CreateOutcome.ReasonRequired, refused.Outcome);

    var accepted = await f.Scheduling.CreateAsync(
        Fixture.Tenant, Fixture.Property, candidate, "Guest asked for Lena", "actor", "corr");
    Assert.Equal(SchedulingService.CreateOutcome.Created, accepted.Outcome);
});

await runner.TestAsync("Concurrent creates into one room admit exactly one", async () =>
{
    var f = new Fixture();
    var slot = f.Day.AddHours(20);

    // Evaluation and insertion were two separate operations, so every one of
    // these saw the room free and every one of them succeeded.
    var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        f.Scheduling.CreateAsync(
            Fixture.Tenant, Fixture.Property,
            Fixture.Appointment($"race{i}", AppointmentStatus.Draft, slot, 60, "prov-marco", "room-race"),
            null, "actor", "corr"))));

    Assert.Equal(1, attempts.Count(r => r.Outcome == SchedulingService.CreateOutcome.Created));

    var inRoom = await f.Repo.ListOverlappingAsync(Fixture.Tenant, Fixture.Property, slot, slot.AddHours(1));
    Assert.Equal(1, inRoom.Count(a => a.RoomId == "room-race"));
});

await runner.TestAsync("Confirmation numbers are detectably taken", async () =>
{
    var f = new Fixture();
    Assert.True(await f.Repo.ConfirmationNumberExistsAsync(Fixture.Tenant, "AAR000001"));
    Assert.False(await f.Repo.ConfirmationNumberExistsAsync(Fixture.Tenant, "AAR999999"));
    // Scoped per tenant: two tenants may legitimately hold the same number.
    Assert.False(await f.Repo.ConfirmationNumberExistsAsync("tenant-other", "AAR000001"));
});

/* ============================ audit trail ============================ */

await runner.TestAsync("A commit records hashes, conflict codes and the reason", async () =>
{
    var f = new Fixture();
    f.Add(Fixture.Appointment("busy", AppointmentStatus.Confirmed, f.Day.AddHours(20), 60, "prov-lena", "room-other"));

    var target = (await f.Get("a1"))!;
    var pf = await f.Preflight(target, f.Day.AddHours(20), "prov-lena", "room-free");

    Assert.Equal(SchedulingService.CommitOutcome.Committed,
        (await f.Commit("a1", pf.Token, reason: "Guest asked for Lena")).Outcome);

    var entry = (await f.Audit.RecentAsync(Fixture.Tenant, Fixture.Property, 10)).First();
    Assert.Equal("appointment.move", entry.Action);
    Assert.Equal("Guest asked for Lena", entry.Reason!);
    Assert.Equal(Fixture.Property, entry.PropertyId);
    Assert.True(entry.ConflictCodes.Contains("CON-001"));
    Assert.NotNull(entry.BeforeHash);
    Assert.NotNull(entry.AfterHash);
    Assert.NotEqual(entry.BeforeHash!, entry.AfterHash!);
    Assert.Equal("corr-test", entry.CorrelationId);
});

await runner.TestAsync("A transition records the target status, not a resolution", async () =>
{
    var f = new Fixture();
    var r = await f.Scheduling.TransitionAsync(
        Fixture.Tenant, Fixture.Property, "a1", AppointmentStatus.CheckedIn, 1, null, "actor", "corr");
    Assert.Equal(SchedulingService.TransitionOutcome.Applied, r.Outcome);

    var entry = (await f.Audit.RecentAsync(Fixture.Tenant, Fixture.Property, 10)).First();
    // The target status used to be written into SelectedResolution, which
    // means "which alternative the operator picked" — so the trail claimed a
    // resolution was chosen where none had been offered.
    Assert.Equal("CheckedIn", entry.TargetStatus!);
    Assert.Null(entry.SelectedResolution);
});

await runner.TestAsync("Audit reads are scoped by tenant AND property", async () =>
{
    var f = new Fixture();
    await f.Audit.RecordAsync(Fixture.AuditFor("tenant-other"));
    await f.Audit.RecordAsync(Fixture.AuditFor(Fixture.Tenant, "prop-elsewhere"));
    await f.Audit.RecordAsync(Fixture.AuditFor(Fixture.Tenant));

    Assert.Equal(1, (await f.Audit.RecentAsync(Fixture.Tenant, Fixture.Property, 50)).Count);
    // Tenant-only scoping let an admin at one property read every other
    // property's actor names and subject ids.
    Assert.Equal(1, (await f.Audit.RecentAsync(Fixture.Tenant, "prop-elsewhere", 50)).Count);
    Assert.Equal(1, (await f.Audit.RecentAsync("tenant-other", Fixture.Property, 50)).Count);
});

runner.Test("State hashes ignore the row version", () =>
{
    var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    var a = Fixture.Appointment("h", AppointmentStatus.Confirmed, now, 60, "prov-lena", "room-1");

    var same = Spms.Domain.Scheduling.Appointment.Rehydrate(
        a.AppointmentId, a.TenantId, a.PropertyId, a.PropertyTimeZone, a.GuestId, a.GuestAlias,
        a.ServiceId, a.ServiceName, a.DurationMinutes, a.ProviderId, a.RoomId,
        a.StartUtc, a.Status, rowVersion: 47, a.ConfirmationNumber, a.CorrelationId, a.CreatedUtc, a.UpdatedUtc);

    // RowVersion used to be inside the canonical string, so the two hashes
    // always differed and could not prove anything substantive had changed.
    Assert.Equal(StateHash.Of(a), StateHash.Of(same));
});

runner.Test("State hashes change when the state does", () =>
{
    var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    var a = Fixture.Appointment("h", AppointmentStatus.Confirmed, now, 60, "prov-lena", "room-1");
    var before = StateHash.Of(a);

    a.ApplyMove(now.AddMinutes(30), Assignment.Unchanged, now);
    Assert.NotEqual(before, StateHash.Of(a));
});

/* ========================== idempotency ========================== */

static IdempotencyScope Key(string key, string tenant = "t", string property = "p",
    string operation = "op", string route = "/appointments") =>
    new(tenant, property, operation, route, key);

await runner.TestAsync("A first claim is reserved and a repeat replays", async () =>
{
    var store = new InMemoryIdempotencyStore();
    var now = DateTimeOffset.UtcNow;

    Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k1"), "hash-a", now)).Outcome);
    await store.CompleteAsync(Key("k1"), 201, "{\"ok\":true}", now);

    var replay = await store.ClaimAsync(Key("k1"), "hash-a", now);
    Assert.Equal(IdempotencyOutcome.Replay, replay.Outcome);
    Assert.Equal(201, replay.StatusCode);
    Assert.Equal("{\"ok\":true}", replay.ResponseJson!);
});

await runner.TestAsync("The same key with a different body is a mismatch", async () =>
{
    var store = new InMemoryIdempotencyStore();
    var now = DateTimeOffset.UtcNow;
    await store.ClaimAsync(Key("k1"), "hash-a", now);
    Assert.Equal(IdempotencyOutcome.Mismatch, (await store.ClaimAsync(Key("k1"), "hash-b", now)).Outcome);
});

await runner.TestAsync("A claim held by another request reports InFlight", async () =>
{
    var store = new InMemoryIdempotencyStore();
    var now = DateTimeOffset.UtcNow;
    await store.ClaimAsync(Key("k1"), "hash-a", now);
    Assert.Equal(IdempotencyOutcome.InFlight, (await store.ClaimAsync(Key("k1"), "hash-a", now)).Outcome);
});

await runner.TestAsync("An abandoned reservation is stealable after its lease", async () =>
{
    var store = new InMemoryIdempotencyStore();
    var now = DateTimeOffset.UtcNow;
    await store.ClaimAsync(Key("k1"), "hash-a", now);

    // Without a lease, a request killed between Claim and Complete left the
    // key answering 409 for a full day with no recovery path.
    Assert.Equal(IdempotencyOutcome.InFlight,
        (await store.ClaimAsync(Key("k1"), "hash-a", now.AddSeconds(30))).Outcome);
    Assert.Equal(IdempotencyOutcome.Reserved,
        (await store.ClaimAsync(Key("k1"), "hash-a", now.AddMinutes(5))).Outcome);
});

await runner.TestAsync("A completed result is not stolen when its lease would have expired", async () =>
{
    var store = new InMemoryIdempotencyStore();
    var now = DateTimeOffset.UtcNow;
    await store.ClaimAsync(Key("k1"), "hash-a", now);
    await store.CompleteAsync(Key("k1"), 201, "{}", now);

    Assert.Equal(IdempotencyOutcome.Replay,
        (await store.ClaimAsync(Key("k1"), "hash-a", now.AddHours(2))).Outcome);
});

await runner.TestAsync("Exactly one of many concurrent claims is reserved", async () =>
{
    var store = new InMemoryIdempotencyStore();
    var now = DateTimeOffset.UtcNow;
    var claims = await Task.WhenAll(Enumerable.Range(0, 32)
        .Select(_ => Task.Run(() => store.ClaimAsync(Key("k1"), "hash-a", now))));

    Assert.Equal(1, claims.Count(c => c.Outcome == IdempotencyOutcome.Reserved));
});

await runner.TestAsync("Abandon frees the key for a corrected retry", async () =>
{
    var store = new InMemoryIdempotencyStore();
    var now = DateTimeOffset.UtcNow;
    await store.ClaimAsync(Key("k1"), "hash-a", now);
    await store.AbandonAsync(Key("k1"));
    Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k1"), "hash-b", now)).Outcome);
});

await runner.TestAsync("Abandon cannot erase a completed result", async () =>
{
    var store = new InMemoryIdempotencyStore();
    var now = DateTimeOffset.UtcNow;
    await store.ClaimAsync(Key("k1"), "hash-a", now);
    await store.CompleteAsync(Key("k1"), 201, "{}", now);
    await store.AbandonAsync(Key("k1"));
    Assert.Equal(IdempotencyOutcome.Replay, (await store.ClaimAsync(Key("k1"), "hash-a", now)).Outcome);
});

await runner.TestAsync("Keys are scoped by tenant, property, operation and route", async () =>
{
    var store = new InMemoryIdempotencyStore();
    var now = DateTimeOffset.UtcNow;
    await store.ClaimAsync(Key("k1"), "hash-a", now);

    Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k1", tenant: "t2"), "hash-a", now)).Outcome);
    // Without the property axis, one client-chosen key arriving at two
    // properties replayed the first property's record — id, guest alias, room
    // and confirmation number — to the second, which then owned nothing.
    Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k1", property: "p2"), "hash-a", now)).Outcome);
    Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k1", operation: "other"), "hash-a", now)).Outcome);
    // The route id is load-bearing for a reassign, so two reassigns of
    // different appointments with one key replayed each other.
    Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k1", route: "/a/2"), "hash-a", now)).Outcome);
});

/* ======================== preflight token store ======================== */

static PreflightResult Token(string token, DateTimeOffset expires, DateTimeOffset now) =>
    new(token, expires, new MoveProposal("a1", now, null, null, 1), "p", now, now.AddHours(1), []);

await runner.TestAsync("Expired tokens are evicted", async () =>
{
    var store = new InMemoryPreflightStore();
    var now = DateTimeOffset.UtcNow;
    await store.SaveAsync("t", Token("pf_x", now.AddSeconds(30), now));

    Assert.Equal(0, store.EvictExpired(now));
    Assert.Equal(1, store.EvictExpired(now.AddMinutes(5)));
    Assert.Null(await store.FindAsync("t", "pf_x"));
});

await runner.TestAsync("Find does not consume", async () =>
{
    var store = new InMemoryPreflightStore();
    var now = DateTimeOffset.UtcNow;
    await store.SaveAsync("t", Token("pf_x", now.AddMinutes(1), now));

    Assert.NotNull(await store.FindAsync("t", "pf_x"));
    Assert.NotNull(await store.FindAsync("t", "pf_x"));
    Assert.True(await store.TryConsumeAsync("t", "pf_x"));
    Assert.False(await store.TryConsumeAsync("t", "pf_x"));
});

/* ========================= API layer helpers ========================= */

runner.Test("If-Match accepts a strong and a weak ETag", () =>
{
    Assert.True(Guard.ETagMatches("\"3\"", "\"3\""));
    Assert.True(Guard.ETagMatches("W/\"3\"", "\"3\""));
    Assert.True(Guard.ETagMatches("\"1\", W/\"3\"", "\"3\""), "a list of candidates");
    Assert.False(Guard.ETagMatches("\"2\"", "\"3\""));
});

runner.Test("If-Match refuses a bare wildcard on a consequential write", () =>
{
    // RFC 7232 reads "*" as "if the resource exists", but API-002 requires the
    // caller to assert the version they read. Honouring it made 412
    // unreachable for any client that always sent it.
    Assert.False(Guard.ETagMatches("*", "\"3\""));
    Assert.False(Guard.TryParseIfMatchVersion("*", out _));
    Assert.True(Guard.TryParseIfMatchVersion("W/\"7\"", out var v));
    Assert.Equal(7, v);
});

runner.Test("An offset-less timestamp is read as UTC, not as server-local", () =>
{
    // A plain TryParse assumes the SERVER's zone, so the same request landed on
    // different instants depending on where the process ran — in a field whose
    // own violation rule says iso8601_required.
    Assert.True(Guard.TryParseInstant("2026-06-15T10:00:00", out var naive));
    Assert.Equal(TimeSpan.Zero, naive.Offset);
    Assert.Equal(10, naive.Hour);

    Assert.True(Guard.TryParseInstant("2026-06-15T08:00:00+05:30", out var offset));
    Assert.Equal(TimeSpan.Zero, offset.Offset);
    Assert.Equal(2, offset.Hour);
    Assert.Equal(30, offset.Minute);
});

runner.Test("The sane-instant window includes its lower bound", () =>
{
    // Every caller's message says "between 2000 and 2100", and a strict
    // comparison rejected 2000-01-01T00:00:00Z itself.
    Assert.True(Guard.IsSaneInstant(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    Assert.True(Guard.IsSaneInstant(new DateTimeOffset(2099, 12, 31, 23, 59, 0, TimeSpan.Zero)));
    Assert.False(Guard.IsSaneInstant(new DateTimeOffset(1999, 12, 31, 23, 59, 0, TimeSpan.Zero)));
    Assert.False(Guard.IsSaneInstant(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)));
});

runner.Test("Paging clamps rather than honouring an absurd request", () =>
{
    Assert.Equal((0, PageLimits.Default), Guard.Page(null, null));
    Assert.Equal((0, PageLimits.Max), Guard.Page(-5, 99999));
    Assert.Equal((10, 1), Guard.Page(10, 0));
});

runner.Test("A problem extension cannot shadow a reserved member", () =>
{
    // Extensions were copied straight into the body, so a call site passing
    // `status` replaced the HTTP status with an appointment status string.
    Assert.Throws<ArgumentException>(() => Problem.From(
        Spms.Domain.Errors.ApiError.ValidationFailed, "corr",
        extensions: Problem.Ext("status", "Cancelled")));

    Assert.Throws<ArgumentException>(() => Problem.From(
        Spms.Domain.Errors.ApiError.ValidationFailed, "corr",
        extensions: Problem.Ext("correlation_id", "spoofed")));
});

runner.Test("An unresolvable time zone renders a marked fallback, not a crash", () =>
{
    var instant = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);
    Assert.Null(LocalClock.Resolve("Nowhere/Invented"));
    Assert.True(LocalClock.Label(instant, "Nowhere/Invented").EndsWith('Z'),
        "the reader must be able to tell UTC is not being presented as local");
});

runner.Test("Property-local rendering uses the property's zone", () =>
{
    var instant = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);
    var tz = LocalClock.Resolve("America/New_York");
    if (tz is null) return;   // zone data absent on this host; nothing to assert

    Assert.Equal("2026-06-15 09:00", LocalClock.Label(instant, "America/New_York"));
});

/* ========================== reference data ========================== */

runner.Test("Every catalogue service has a positive duration", () =>
{
    foreach (var s in ServiceCatalog.Services)
        Assert.True(s.DurationMinutes > 0, s.ServiceId);
});

runner.Test("An unknown service is not found", () =>
    Assert.Null(ServiceCatalog.Find("svc-nonexistent")));

runner.Test("An unknown property falls back to UTC rather than throwing", () =>
{
    Assert.Equal("UTC", PropertyDirectory.For("prop-never-heard-of").TimeZoneId);
    Assert.Equal("America/New_York", PropertyDirectory.For("prop-riverside").TimeZoneId);
});

return runner.Finish();
