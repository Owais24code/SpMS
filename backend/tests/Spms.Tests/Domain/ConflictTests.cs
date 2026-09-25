using Spms.Domain.Scheduling;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Domain;

public class ConflictDetectionTests
{
    [Fact]
    public async Task Room_double_booking_is_a_hard_conflict()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a2"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(9).AddMinutes(30), "prov-priya", "room-1");

        Assert.Contains(pf.Conflicts, c => c.Code == "CON-002");
        Assert.False(pf.CommitAllowed);
    }

    [Fact]
    public async Task Provider_overlap_is_soft_and_overridable()
    {
        var w = new SchedulingWorld();
        // The provider stays qualified for the service, so CON-001 is the only
        // conflict and the soft path is actually reachable. Pairing an overlap
        // with an unqualified provider tests CON-003, not this.
        w.Add(SchedulingWorld.Appointment("busy", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-lena", "room-other"));

        var target = (await w.Get("a1"))!;   // svc-deep, prov-lena is qualified
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        Assert.Contains(pf.Conflicts, c => c.Code == "CON-001");
        Assert.DoesNotContain(pf.Conflicts, c => !c.Overridable);
        Assert.True(pf.CommitAllowed);
        Assert.True(pf.RequiresReason);
    }

    [Fact]
    public async Task An_unqualified_provider_is_a_hard_conflict()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;   // svc-deep
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-priya", "room-free");

        Assert.Contains(pf.Conflicts, c => c.Rule == "provider-not-qualified");
        Assert.False(pf.CommitAllowed);
    }

    [Fact]
    public async Task An_unknown_provider_fails_closed_not_open()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-newhire", "room-free");

        // The previous version treated an absent qualification record as
        // qualified, which inverted a licensing rule for every new hire and typo.
        Assert.Contains(pf.Conflicts, c => c.Rule == "provider-unknown");
        Assert.False(pf.CommitAllowed);
    }

    [Fact]
    public async Task A_suspended_provider_is_refused_even_with_a_live_credential()
    {
        var w = new SchedulingWorld();
        w.Qualifications.Grant(SchedulingWorld.Tenant, SchedulingWorld.Property, "prov-gone", "svc-deep");
        w.Qualifications.Suspend(SchedulingWorld.Tenant, SchedulingWorld.Property, "prov-gone");

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-gone", "room-free");

        // Terminated staff keep their history but must not take new work.
        Assert.Contains(pf.Conflicts, c => c.Code == "CON-003");
        Assert.False(pf.CommitAllowed);
    }

    [Fact]
    public async Task An_expired_qualification_is_refused()
    {
        var w = new SchedulingWorld();
        w.Qualifications.Grant(SchedulingWorld.Tenant, SchedulingWorld.Property, "prov-lapsed", "svc-deep",
            expiresUtc: w.Day.AddHours(-1));

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lapsed", "room-free");

        Assert.Contains(pf.Conflicts, c => c.Code == "CON-003");
    }

    [Fact]
    public async Task Guest_overlap_keys_on_identity_not_the_display_alias()
    {
        var w = new SchedulingWorld();
        // Same guest id, different alias text — the alias must not be what matches.
        w.Add(SchedulingWorld.Appointment("twin", AppointmentStatus.Confirmed, w.Day.AddHours(9), 60, "prov-marco", "room-7",
            guestId: SchedulingWorld.GuestOfA1, alias: "A totally different label"));

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(9).AddMinutes(15), "prov-lena", "room-free");

        Assert.Contains(pf.Conflicts, c => c.Code == "CON-005");
    }

    [Fact]
    public async Task A_clean_slot_produces_no_conflicts()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");

        Assert.Empty(pf.Conflicts);
        Assert.True(pf.CommitAllowed);
        Assert.False(pf.RequiresReason);
    }

    [Theory]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    public async Task A_dead_neighbour_does_not_block(AppointmentStatus status)
    {
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("gone", status, w.Day.AddHours(20), 60, "prov-lena", "room-free"));

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-free");
        Assert.Empty(pf.Conflicts);
    }

    [Fact]
    public async Task A_completed_neighbour_still_occupied_the_room()
    {
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("done", AppointmentStatus.Completed, w.Day.AddHours(20), 60, "prov-marco", "room-done"));

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-done");

        // The database's exclusion constraint takes the same view, so the
        // application must not disagree with it.
        Assert.Contains(pf.Conflicts, c => c.Code == "CON-002");
    }

    [Fact]
    public async Task Conflicts_of_the_same_rule_are_deduplicated()
    {
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("n1", AppointmentStatus.Confirmed, w.Day.AddHours(20), 30, "prov-marco", "room-busy"));
        w.Add(SchedulingWorld.Appointment("n2", AppointmentStatus.Confirmed, w.Day.AddHours(20).AddMinutes(30), 30, "prov-marco", "room-busy"));

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(20), "prov-lena", "room-busy");
        Assert.Single(pf.Conflicts, c => c.Code == "CON-002");
    }

    [Fact]
    public async Task A_hard_conflict_suppresses_the_reason_prompt()
    {
        var w = new SchedulingWorld();
        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(9).AddMinutes(30), "prov-priya", "room-1");

        Assert.False(pf.CommitAllowed);
        // Reporting RequiresReason alongside CommitAllowed=false showed the
        // operator a reason box that could never succeed.
        Assert.False(pf.RequiresReason);
    }
}

public class BufferBoundaryTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]
    public async Task Room_turnover_after_a_neighbour(int gapMinutes, bool expectConflict)
    {
        var w = new SchedulingWorld();
        var neighbour = SchedulingWorld.Appointment("nb", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-marco", "room-turn");
        w.Add(neighbour);

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, neighbour.EndUtc.AddMinutes(gapMinutes), "prov-lena", "room-turn");

        Assert.Equal(expectConflict, pf.Conflicts.Any(c => c.Rule == "room-turnover"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]
    public async Task Room_turnover_before_a_neighbour(int gapMinutes, bool expectConflict)
    {
        // The before-side branch of the gap calculation was never exercised at
        // a boundary; only the after side was.
        var w = new SchedulingWorld();
        var neighbour = SchedulingWorld.Appointment("nb", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-marco", "room-turn");
        w.Add(neighbour);

        var target = (await w.Get("a1"))!;   // 90 minutes
        var start = neighbour.StartUtc.AddMinutes(-gapMinutes).AddMinutes(-target.DurationMinutes);
        var pf = await w.Preflight(target, start, "prov-lena", "room-turn");

        Assert.Equal(expectConflict, pf.Conflicts.Any(c => c.Rule == "room-turnover"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public async Task Provider_transition(int gapMinutes, bool expectConflict)
    {
        // This buffer was previously never evaluated at all.
        var w = new SchedulingWorld();
        var neighbour = SchedulingWorld.Appointment("nb", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-lena", "room-other");
        w.Add(neighbour);

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, neighbour.EndUtc.AddMinutes(gapMinutes), "prov-lena", "room-free");

        Assert.Equal(expectConflict, pf.Conflicts.Any(c => c.Rule == "provider-transition"));
    }

    [Fact]
    public async Task Both_CON004_breaches_are_reported_not_just_the_first()
    {
        var w = new SchedulingWorld();
        // One neighbour sharing BOTH the room and the provider, 5 minutes
        // away: under the 15-minute room buffer and the 10-minute provider one.
        var neighbour = SchedulingWorld.Appointment("nb", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-lena", "room-both");
        w.Add(neighbour);

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, neighbour.EndUtc.AddMinutes(5), "prov-lena", "room-both");

        // Deduplicating by code collapsed these two different rules into one,
        // so the operator was told housekeeping was tight and never told the
        // therapist had no transition time — and the offered resolutions did
        // not fix the breach they were not shown.
        Assert.Contains(pf.Conflicts, c => c.Rule == "room-turnover");
        Assert.Contains(pf.Conflicts, c => c.Rule == "provider-transition");
        Assert.Equal(2, pf.Conflicts.Count(c => c.Code == "CON-004"));
    }

    [Fact]
    public async Task Buffers_come_from_configuration_not_from_a_constant()
    {
        // The same geometry that breaches a 15-minute buffer passes a 5-minute
        // one. Baked into the code, a property could not change its turnover
        // without a deployment.
        var w = new SchedulingWorld(roomTurnoverMinutes: 5, providerTransitionMinutes: 5);
        var neighbour = SchedulingWorld.Appointment("nb", AppointmentStatus.Confirmed, w.Day.AddHours(20), 60, "prov-marco", "room-turn");
        w.Add(neighbour);

        var target = (await w.Get("a1"))!;
        var pf = await w.Preflight(target, neighbour.EndUtc.AddMinutes(10), "prov-lena", "room-turn");

        Assert.DoesNotContain(pf.Conflicts, c => c.Rule == "room-turnover");
    }
}

public class MidnightSpanTests
{
    [Fact]
    public async Task An_appointment_spanning_midnight_is_found_from_the_next_day()
    {
        var w = new SchedulingWorld();
        // 23:30 for 90 minutes: starts on day one, ends 01:00 on day two.
        w.Add(SchedulingWorld.Appointment("night", AppointmentStatus.Confirmed,
            w.Day.AddHours(23).AddMinutes(30), 90, "prov-lena", "room-night"));

        var nextDay = await w.Repo.ListOverlappingAsync(
            SchedulingWorld.Tenant, SchedulingWorld.Property, w.Day.AddDays(1), w.Day.AddDays(2));

        // A day-bounded query missed it, which is how the board double-booked
        // across midnight.
        Assert.Contains(nextDay, a => a.AppointmentId == "night");
    }

    [Fact]
    public async Task A_move_across_midnight_still_sees_the_other_sides_conflicts()
    {
        var w = new SchedulingWorld();
        w.Add(SchedulingWorld.Appointment("night", AppointmentStatus.Confirmed,
            w.Day.AddHours(23).AddMinutes(30), 90, "prov-lena", "room-night"));
        w.Add(SchedulingWorld.Appointment("mover", AppointmentStatus.Confirmed,
            w.Day.AddHours(10), 60, "prov-marco", "room-spare"));

        var mover = (await w.Get("mover"))!;
        var pf = await w.Preflight(mover, w.Day.AddDays(1).AddMinutes(15), "prov-marco", "room-night");

        Assert.Contains(pf.Conflicts, c => c.Code == "CON-002");
    }
}

public class IsolationTests
{
    [Fact]
    public async Task Get_refuses_a_record_from_another_property()
    {
        var w = new SchedulingWorld();
        Assert.NotNull(await w.Repo.GetAsync(SchedulingWorld.Tenant, SchedulingWorld.Property, "a1"));
        Assert.Null(await w.Repo.GetAsync(SchedulingWorld.Tenant, SchedulingWorld.Elsewhere, "a1"));
    }

    [Fact]
    public async Task Get_refuses_a_record_from_another_tenant()
    {
        var w = new SchedulingWorld();
        Assert.Null(await w.Repo.GetAsync("tenant-other", SchedulingWorld.Property, "a1"));
    }

    [Fact]
    public async Task Overlap_query_is_scoped_to_one_property()
    {
        var w = new SchedulingWorld();
        var here = await w.Repo.ListOverlappingAsync(SchedulingWorld.Tenant, SchedulingWorld.Property, w.Day, w.Day.AddDays(1));
        var elsewhere = await w.Repo.ListOverlappingAsync(SchedulingWorld.Tenant, SchedulingWorld.Elsewhere, w.Day, w.Day.AddDays(1));

        // x1 sits at prop-elsewhere in room-1 at the same hour as a1. Each
        // query must see only its own property's side of that collision.
        Assert.DoesNotContain(here, a => a.AppointmentId == "x1");
        Assert.Contains(here, a => a.AppointmentId == "a1");
        Assert.Single(elsewhere);
        Assert.Equal("x1", elsewhere[0].AppointmentId);
    }

    [Fact]
    public async Task Another_propertys_booking_is_not_a_conflict()
    {
        var w = new SchedulingWorld();
        // x1 holds room-1 at 09:00 — but at prop-elsewhere.
        var target = (await w.Get("a2"))!;
        var pf = await w.Preflight(target, w.Day.AddHours(9).AddMinutes(30), "prov-priya", "room-1");

        // a1 at this property does conflict; x1 must not add a second one.
        Assert.Single(pf.Conflicts, c => c.Code == "CON-002");
    }
}
