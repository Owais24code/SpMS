using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;
using Spms.Infrastructure.InMemory;
using Spms.Infrastructure.Postgres;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Conformance;

/*
 * One set of assertions, run against BOTH the in-memory adapters and the
 * Postgres ones.
 *
 * This exists because the architecture review found that the ports encoded
 * in-memory assumptions and the two implementations would silently diverge —
 * and it was right: the in-memory repository returned independent copies while
 * EF hands back tracked entities, and a test asserting the copy semantics was
 * pinning the wrong contract. Shared cases are the only way to keep the fast
 * suite honest about what the real store does.
 *
 * The cases live in a static class and each adapter declares its own facts
 * over them. That is more lines than inheriting a base class, but xunit 2.9
 * cannot skip an inherited fact, and the Postgres cases must SKIP when no test
 * database is configured rather than silently pass.
 */

public static class RepositoryCases
{
    public static readonly DateTimeOffset Day = new(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

    public delegate Appointment Factory(
        string id, DateTimeOffset start, int minutes, string? provider, string? room,
        string guestId, string? confirmation);

    public static async Task Added_is_readable_within_its_property(IAppointmentRepository repo, Factory f, string tenant, string property)
    {
        Assert.True(await repo.TryAddAsync(f("conf-1", Day.AddHours(10), 30, "prov-priya", "room-1", "guest-0000", null)));

        var read = await repo.GetAsync(tenant, property, "conf-1");
        Assert.NotNull(read);
        Assert.Equal("conf-1", read.AppointmentId);
        Assert.Equal(1, read.RowVersion);
    }

    public static async Task Reads_are_scoped_by_tenant_and_property(IAppointmentRepository repo, Factory f, string tenant, string property)
    {
        await repo.TryAddAsync(f("conf-2", Day.AddHours(10), 30, "prov-priya", "room-1", "guest-0000", null));

        Assert.Null(await repo.GetAsync(tenant, "prop-nowhere", "conf-2"));
        Assert.Null(await repo.GetAsync("tenant-nowhere", property, "conf-2"));
    }

    public static async Task An_unknown_id_reads_as_null(IAppointmentRepository repo, string tenant, string property) =>
        Assert.Null(await repo.GetAsync(tenant, property, "conf-missing"));

    public static async Task A_duplicate_id_is_refused_and_the_original_survives(
        IAppointmentRepository repo, Factory f, string tenant, string property)
    {
        Assert.True(await repo.TryAddAsync(f("conf-3", Day.AddHours(10), 30, "prov-priya", "room-1", "guest-0000", null)));
        Assert.False(await repo.TryAddAsync(f("conf-3", Day.AddHours(14), 30, "prov-priya", "room-2", "guest-0000", null)));

        var read = await repo.GetAsync(tenant, property, "conf-3");
        Assert.Equal("room-1", read!.RoomId);
    }

    public static async Task A_returned_aggregate_may_be_mutated_without_reaching_the_store(
        IAppointmentRepository repo, Factory f, string tenant, string property)
    {
        await repo.TryAddAsync(f("conf-4", Day.AddHours(10), 30, "prov-priya", "room-1", "guest-0000", null));

        var first = (await repo.GetAsync(tenant, property, "conf-4"))!;
        first.ApplyMove(Day.AddHours(15), Assignment.Set(null, "room-2"), Day);

        // The contract both adapters must honour: a read hands back something
        // the caller may mutate freely, and only an explicit write changes the
        // store. Under EF it holds because the repository projects out of the
        // change tracker instead of returning tracked entities — which also
        // keeps the 412 path truthful, since it re-reads to show real state.
        var second = (await repo.GetAsync(tenant, property, "conf-4"))!;
        Assert.Equal(1, second.RowVersion);
        Assert.Equal("room-1", second.RoomId);
    }

    public static async Task TryUpdate_refuses_a_stale_expected_version(
        IAppointmentRepository repo, Factory f, string tenant, string property)
    {
        await repo.TryAddAsync(f("conf-5", Day.AddHours(10), 30, "prov-priya", "room-1", "guest-0000", null));

        var first = (await repo.GetAsync(tenant, property, "conf-5"))!;
        first.ApplyMove(Day.AddHours(15), Assignment.Unchanged, Day);
        Assert.True(await repo.TryUpdateAsync(first, expectedRowVersion: 1));

        var stale = (await repo.GetAsync(tenant, property, "conf-5"))!;
        Assert.Equal(2, stale.RowVersion);
        Assert.False(await repo.TryUpdateAsync(stale, expectedRowVersion: 1));
    }

    public static async Task TryUpdate_on_a_missing_row_is_false_not_an_error(IAppointmentRepository repo, Factory f)
    {
        var ghost = f("conf-ghost", Day.AddHours(10), 30, "prov-priya", "room-1", "guest-0000", null);
        ghost.ApplyMove(Day.AddHours(11), Assignment.Unchanged, Day);
        Assert.False(await repo.TryUpdateAsync(ghost, expectedRowVersion: 1));
    }

    public static async Task The_overlap_query_is_half_open(IAppointmentRepository repo, Factory f, string tenant, string property)
    {
        // 10:00–10:30
        await repo.TryAddAsync(f("conf-6", Day.AddHours(10), 30, "prov-priya", "room-1", "guest-0000", null));

        var after = await repo.ListOverlappingAsync(tenant, property, Day.AddHours(10).AddMinutes(30), Day.AddHours(11));
        var before = await repo.ListOverlappingAsync(tenant, property, Day.AddHours(9), Day.AddHours(10));
        var across = await repo.ListOverlappingAsync(tenant, property, Day.AddHours(10).AddMinutes(15), Day.AddHours(11));

        // Must match the exclusion constraint's '[)' range, or the application
        // and the database disagree about what a conflict is.
        Assert.DoesNotContain(after, a => a.AppointmentId == "conf-6");
        Assert.DoesNotContain(before, a => a.AppointmentId == "conf-6");
        Assert.Contains(across, a => a.AppointmentId == "conf-6");
    }

    public static async Task An_appointment_spanning_midnight_is_found_from_the_next_day(
        IAppointmentRepository repo, Factory f, string tenant, string property)
    {
        await repo.TryAddAsync(f("conf-7", Day.AddHours(23).AddMinutes(45), 30, "prov-priya", "room-2", "guest-0000", null));

        var nextDay = await repo.ListOverlappingAsync(tenant, property, Day.AddDays(1), Day.AddDays(2));
        Assert.Contains(nextDay, a => a.AppointmentId == "conf-7");
    }

    public static async Task Paging_and_counting_agree_with_the_unpaged_query(
        IAppointmentRepository repo, Factory f, string tenant, string property)
    {
        for (var i = 0; i < 5; i++)
            await repo.TryAddAsync(f($"conf-page{i}", Day.AddHours(8).AddMinutes(i * 40), 30, "prov-priya", $"room-{i + 1}", "guest-0000", null));

        var from = Day.AddHours(7);
        var to = Day.AddHours(13);
        var all = await repo.ListOverlappingAsync(tenant, property, from, to);
        var count = await repo.CountOverlappingAsync(tenant, property, from, to);
        var page = await repo.ListPageAsync(tenant, property, from, to, 1, 2);

        Assert.Equal(all.Count, count);
        Assert.Equal(2, page.Count);
        // Deterministic ordering, or paging returns overlapping or missing rows.
        Assert.Equal(all.Skip(1).Take(2).Select(a => a.AppointmentId), page.Select(a => a.AppointmentId));
    }

    public static async Task Confirmation_numbers_are_detectable_within_a_tenant(
        IAppointmentRepository repo, Factory f, string tenant)
    {
        await repo.TryAddAsync(f("conf-8", Day.AddHours(10), 30, "prov-priya", "room-1", "guest-0000", "AARCONF0001"));

        Assert.True(await repo.ConfirmationNumberExistsAsync(tenant, "AARCONF0001"));
        Assert.False(await repo.ConfirmationNumberExistsAsync(tenant, "AARCONF9999"));
        Assert.False(await repo.ConfirmationNumberExistsAsync("tenant-nowhere", "AARCONF0001"));
    }
}

public class InMemoryRepositoryConformance
{
    private const string Tenant = SchedulingWorld.Tenant;
    private const string Property = SchedulingWorld.Property;

    private static IAppointmentRepository Repo() => new InMemoryAppointmentRepository();

    private static readonly RepositoryCases.Factory Make =
        (id, start, minutes, provider, room, guestId, confirmation) =>
            SchedulingWorld.Appointment(id, AppointmentStatus.Confirmed, start, minutes, provider, room,
                "svc-peel", "Peel 30", guestId, "Guest", confirmation);

    [Fact] public Task Added_is_readable() => RepositoryCases.Added_is_readable_within_its_property(Repo(), Make, Tenant, Property);
    [Fact] public Task Reads_are_scoped() => RepositoryCases.Reads_are_scoped_by_tenant_and_property(Repo(), Make, Tenant, Property);
    [Fact] public Task Unknown_id_is_null() => RepositoryCases.An_unknown_id_reads_as_null(Repo(), Tenant, Property);
    [Fact] public Task Duplicate_id_refused() => RepositoryCases.A_duplicate_id_is_refused_and_the_original_survives(Repo(), Make, Tenant, Property);
    [Fact] public Task Returned_aggregate_is_detached() => RepositoryCases.A_returned_aggregate_may_be_mutated_without_reaching_the_store(Repo(), Make, Tenant, Property);
    [Fact] public Task Stale_update_refused() => RepositoryCases.TryUpdate_refuses_a_stale_expected_version(Repo(), Make, Tenant, Property);
    [Fact] public Task Missing_update_is_false() => RepositoryCases.TryUpdate_on_a_missing_row_is_false_not_an_error(Repo(), Make);
    [Fact] public Task Overlap_is_half_open() => RepositoryCases.The_overlap_query_is_half_open(Repo(), Make, Tenant, Property);
    [Fact] public Task Midnight_span_found() => RepositoryCases.An_appointment_spanning_midnight_is_found_from_the_next_day(Repo(), Make, Tenant, Property);
    [Fact] public Task Paging_agrees_with_count() => RepositoryCases.Paging_and_counting_agree_with_the_unpaged_query(Repo(), Make, Tenant, Property);
    [Fact] public Task Confirmation_numbers_detectable() => RepositoryCases.Confirmation_numbers_are_detectable_within_a_tenant(Repo(), Make, Tenant);
}

public class PostgresRepositoryConformance(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Tenant = PostgresWorld.Tenant;
    private const string Property = PostgresWorld.Property;

    private static readonly RepositoryCases.Factory Make =
        (id, start, minutes, provider, room, guestId, confirmation) =>
            PostgresWorld.Appointment(id, AppointmentStatus.Confirmed, start, minutes, provider, room,
                "svc-peel", "Peel 30", guestId, "Guest", confirmation);

    /// <summary>
    /// A fresh database per case, so ids cannot collide between cases and each
    /// one exercises a schema built exactly the way production's is.
    /// </summary>
    private async Task<IAppointmentRepository> Repo()
    {
        // Cases in one class share a database, and several of them book the
        // same room at the same hour. Without this they refuse each other
        // through the room-overlap constraint and the failure looks like a
        // repository bug.
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(Tenant, Property);
        return new PostgresAppointmentRepository(fixture.NewContext());
    }

    [RequiresPostgres] public async Task Added_is_readable() => await RepositoryCases.Added_is_readable_within_its_property(await Repo(), Make, Tenant, Property);
    [RequiresPostgres] public async Task Reads_are_scoped() => await RepositoryCases.Reads_are_scoped_by_tenant_and_property(await Repo(), Make, Tenant, Property);
    [RequiresPostgres] public async Task Unknown_id_is_null() => await RepositoryCases.An_unknown_id_reads_as_null(await Repo(), Tenant, Property);
    [RequiresPostgres] public async Task Duplicate_id_refused() => await RepositoryCases.A_duplicate_id_is_refused_and_the_original_survives(await Repo(), Make, Tenant, Property);
    [RequiresPostgres] public async Task Returned_aggregate_is_detached() => await RepositoryCases.A_returned_aggregate_may_be_mutated_without_reaching_the_store(await Repo(), Make, Tenant, Property);
    [RequiresPostgres] public async Task Stale_update_refused() => await RepositoryCases.TryUpdate_refuses_a_stale_expected_version(await Repo(), Make, Tenant, Property);
    [RequiresPostgres] public async Task Missing_update_is_false() => await RepositoryCases.TryUpdate_on_a_missing_row_is_false_not_an_error(await Repo(), Make);
    [RequiresPostgres] public async Task Overlap_is_half_open() => await RepositoryCases.The_overlap_query_is_half_open(await Repo(), Make, Tenant, Property);
    [RequiresPostgres] public async Task Midnight_span_found() => await RepositoryCases.An_appointment_spanning_midnight_is_found_from_the_next_day(await Repo(), Make, Tenant, Property);
    [RequiresPostgres] public async Task Paging_agrees_with_count() => await RepositoryCases.Paging_and_counting_agree_with_the_unpaged_query(await Repo(), Make, Tenant, Property);
    [RequiresPostgres] public async Task Confirmation_numbers_detectable() => await RepositoryCases.Confirmation_numbers_are_detectable_within_a_tenant(await Repo(), Make, Tenant);
}

public static class IdempotencyCases
{
    private static IdempotencyScope Key(string key, string tenant = "tenant-test", string property = "prop-test",
        string operation = "op", string route = "/appointments") =>
        new(tenant, property, operation, route, key);

    public static async Task A_first_claim_is_reserved_and_a_repeat_replays(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k1"), "hash-a", now)).Outcome);
        await store.CompleteAsync(Key("k1"), 201, "{\"ok\":true}", now);

        var replay = await store.ClaimAsync(Key("k1"), "hash-a", now);
        Assert.Equal(IdempotencyOutcome.Replay, replay.Outcome);
        Assert.Equal(201, replay.StatusCode);
        Assert.Equal("{\"ok\":true}", replay.ResponseJson);
    }

    public static async Task The_same_key_with_a_different_body_is_a_mismatch(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.ClaimAsync(Key("k2"), "hash-a", now);
        Assert.Equal(IdempotencyOutcome.Mismatch, (await store.ClaimAsync(Key("k2"), "hash-b", now)).Outcome);
    }

    public static async Task A_claim_held_by_another_request_reports_in_flight(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.ClaimAsync(Key("k3"), "hash-a", now);
        Assert.Equal(IdempotencyOutcome.InFlight, (await store.ClaimAsync(Key("k3"), "hash-a", now)).Outcome);
    }

    public static async Task An_abandoned_reservation_is_stealable_after_its_lease(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.ClaimAsync(Key("k4"), "hash-a", now);

        // Without a lease, a request killed between claim and complete left
        // the key answering 409 for the full retention with no recovery path.
        Assert.Equal(IdempotencyOutcome.InFlight, (await store.ClaimAsync(Key("k4"), "hash-a", now.AddSeconds(30))).Outcome);
        Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k4"), "hash-a", now.AddMinutes(5))).Outcome);
    }

    public static async Task A_completed_result_is_not_stolen_by_a_later_claim(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.ClaimAsync(Key("k5"), "hash-a", now);
        await store.CompleteAsync(Key("k5"), 201, "{}", now);

        Assert.Equal(IdempotencyOutcome.Replay, (await store.ClaimAsync(Key("k5"), "hash-a", now.AddHours(2))).Outcome);
    }

    public static async Task Exactly_one_of_many_concurrent_claims_is_reserved(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        // The single case the key exists to prevent. A read-then-write check
        // let two requests holding one key both proceed.
        var claims = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.ClaimAsync(Key("k6"), "hash-a", now))));

        Assert.Single(claims, c => c.Outcome == IdempotencyOutcome.Reserved);
    }

    public static async Task Abandon_frees_the_key_for_a_corrected_retry(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.ClaimAsync(Key("k7"), "hash-a", now);
        await store.AbandonAsync(Key("k7"));
        Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k7"), "hash-b", now)).Outcome);
    }

    public static async Task Abandon_cannot_erase_a_completed_result(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.ClaimAsync(Key("k8"), "hash-a", now);
        await store.CompleteAsync(Key("k8"), 201, "{}", now);
        await store.AbandonAsync(Key("k8"));
        Assert.Equal(IdempotencyOutcome.Replay, (await store.ClaimAsync(Key("k8"), "hash-a", now)).Outcome);
    }

    public static async Task Keys_are_scoped_by_tenant_property_operation_and_route(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.ClaimAsync(Key("k9"), "hash-a", now);

        Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k9", tenant: "t2"), "hash-a", now)).Outcome);
        // Without the property axis, one client-chosen key arriving at a
        // second property replayed the first property's record — id, guest
        // alias, room, confirmation number — to a property that owned nothing.
        Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k9", property: "p2"), "hash-a", now)).Outcome);
        Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k9", operation: "other"), "hash-a", now)).Outcome);
        // The route id is load-bearing for a reassign, so two reassigns of
        // different appointments with one key must not replay each other.
        Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k9", route: "/a/2"), "hash-a", now)).Outcome);
    }

    public static async Task The_sweep_drops_completed_records_past_retention(IIdempotencyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.ClaimAsync(Key("k10"), "hash-a", now);
        await store.CompleteAsync(Key("k10"), 201, "{}", now);

        Assert.Equal(0, await store.SweepAsync(now));
        Assert.True(await store.SweepAsync(now.AddDays(2)) >= 1);
        Assert.Equal(IdempotencyOutcome.Reserved, (await store.ClaimAsync(Key("k10"), "hash-a", now.AddDays(2))).Outcome);
    }
}

public class InMemoryIdempotencyConformance
{
    private static IIdempotencyStore Store() => new InMemoryIdempotencyStore();

    [Fact] public Task First_claim_then_replay() => IdempotencyCases.A_first_claim_is_reserved_and_a_repeat_replays(Store());
    [Fact] public Task Different_body_is_mismatch() => IdempotencyCases.The_same_key_with_a_different_body_is_a_mismatch(Store());
    [Fact] public Task Held_claim_is_in_flight() => IdempotencyCases.A_claim_held_by_another_request_reports_in_flight(Store());
    [Fact] public Task Lease_allows_takeover() => IdempotencyCases.An_abandoned_reservation_is_stealable_after_its_lease(Store());
    [Fact] public Task Completed_survives() => IdempotencyCases.A_completed_result_is_not_stolen_by_a_later_claim(Store());
    [Fact] public Task One_concurrent_winner() => IdempotencyCases.Exactly_one_of_many_concurrent_claims_is_reserved(Store());
    [Fact] public Task Abandon_frees_key() => IdempotencyCases.Abandon_frees_the_key_for_a_corrected_retry(Store());
    [Fact] public Task Abandon_spares_completed() => IdempotencyCases.Abandon_cannot_erase_a_completed_result(Store());
    [Fact] public Task Scoped_four_ways() => IdempotencyCases.Keys_are_scoped_by_tenant_property_operation_and_route(Store());
    [Fact] public Task Sweep_drops_old() => IdempotencyCases.The_sweep_drops_completed_records_past_retention(Store());
}

public class PostgresIdempotencyConformance(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private IIdempotencyStore Store()
    {
        fixture.ResetBoardAsync().GetAwaiter().GetResult();
        return new PostgresIdempotencyStore(fixture.DataSource);
    }

    [RequiresPostgres] public Task First_claim_then_replay() => IdempotencyCases.A_first_claim_is_reserved_and_a_repeat_replays(Store());
    [RequiresPostgres] public Task Different_body_is_mismatch() => IdempotencyCases.The_same_key_with_a_different_body_is_a_mismatch(Store());
    [RequiresPostgres] public Task Held_claim_is_in_flight() => IdempotencyCases.A_claim_held_by_another_request_reports_in_flight(Store());
    [RequiresPostgres] public Task Lease_allows_takeover() => IdempotencyCases.An_abandoned_reservation_is_stealable_after_its_lease(Store());
    [RequiresPostgres] public Task Completed_survives() => IdempotencyCases.A_completed_result_is_not_stolen_by_a_later_claim(Store());
    [RequiresPostgres] public Task One_concurrent_winner() => IdempotencyCases.Exactly_one_of_many_concurrent_claims_is_reserved(Store());
    [RequiresPostgres] public Task Abandon_frees_key() => IdempotencyCases.Abandon_frees_the_key_for_a_corrected_retry(Store());
    [RequiresPostgres] public Task Abandon_spares_completed() => IdempotencyCases.Abandon_cannot_erase_a_completed_result(Store());
    [RequiresPostgres] public Task Scoped_four_ways() => IdempotencyCases.Keys_are_scoped_by_tenant_property_operation_and_route(Store());
    [RequiresPostgres] public Task Sweep_drops_old() => IdempotencyCases.The_sweep_drops_completed_records_past_retention(Store());
}
