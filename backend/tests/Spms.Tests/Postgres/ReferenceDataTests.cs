using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Core.Settings;
using Spms.Modules.Inventory;
using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Workforce;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>
/// Batch 6 against the real database: stock posting (row-locked, never
/// negative, idempotent), counts approved by someone else, consumption on
/// completion, credential expiry taking qualifications with it, roles under
/// two-person approval reaching the outbox, and settings superseded by date.
/// </summary>
public class ReferenceDataTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string T = PostgresWorld.Tenant;
    private const string P = PostgresWorld.Property;
    private static string Id(string name) => TestIds.Of(name);
    private static Guid G(string name) => TestIds.IdOf(name);
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 13, 0, 0, TimeSpan.Zero);

    private async Task FreshAsync()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(T, P);
        await fixture.ExecAsync($"""
            TRUNCATE inventory.inventory_ledger_entry, inventory.inventory_location_balance, inventory.stock_count,
                     inventory.laundry_batch, inventory.service_supply CASCADE;
            INSERT INTO resources.location (location_id, tenant_id, property_id, location_code, location_name, location_type)
            VALUES ('{Id("loc-store")}', '{Id(T)}', '{Id(P)}', 'STORE', 'Store', 'Storage'),
                   ('{Id("loc-shelf")}', '{Id(T)}', '{Id(P)}', 'SHELF', 'Shelf', 'Retail') ON CONFLICT DO NOTHING;
            INSERT INTO inventory.inventory_item (inventory_item_id, tenant_id, item_code, item_name, item_kind)
            VALUES ('{Id("item-oil")}', '{Id(T)}', 'oil', 'Oil', 'Professional'), ('{Id("item-towel")}', '{Id(T)}', 'towel', 'Towel', 'Linen') ON CONFLICT DO NOTHING;
            INSERT INTO inventory.inventory_item_variant (inventory_item_variant_id, tenant_id, inventory_item_id, variant_code)
            VALUES ('{Id("var-oil")}', '{Id(T)}', '{Id("item-oil")}', 'OIL'), ('{Id("var-towel")}', '{Id(T)}', '{Id("item-towel")}', 'TOWEL') ON CONFLICT DO NOTHING;
            """);
    }

    private IServiceScope Scope(string principal = "principal-desk", DateTimeOffset? at = null) =>
        fixture.NewScope(T, [P], P, new TestClock(at ?? Now), principal);

    private static async Task<TResult> InTx<TResult>(IServiceScope scope, Func<Task<TResult>> work)
    {
        await using var tx = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync();
        var r = await work();
        await tx.CommitAsync();
        return r;
    }

    private static Task<InventoryService.Posted> ReceiveAsync(IServiceScope s, string variant, string location, decimal qty, string state = "Saleable", string? key = null) =>
        s.ServiceProvider.GetRequiredService<InventoryService>().PostAsync(
            [new Movement(G(variant), G(location), state, "Receipt", qty, UnitCostMinor: 1000)], key ?? Guid.NewGuid().ToString("N"), default);

    private async Task<decimal> OnHandAsync(string variant, string location, string state = "Saleable") =>
        await fixture.ScalarAsync<decimal?>($"""
            SELECT on_hand FROM inventory.inventory_location_balance
             WHERE inventory_item_variant_id = '{Id(variant)}' AND location_id = '{Id(location)}' AND stock_state = '{state}'
            """) ?? 0;

    [RequiresPostgres]
    public async Task A_movement_updates_the_balance_in_its_transaction_replays_on_its_key_and_never_goes_below_zero()
    {
        await FreshAsync();
        using var s = Scope();
        var inventory = s.ServiceProvider.GetRequiredService<InventoryService>();
        var first = await ReceiveAsync(s, "var-oil", "loc-store", 10, key: "rcpt-1");
        Assert.Equal(InventoryService.Outcome.Ok, first.Outcome);
        var replay = await ReceiveAsync(s, "var-oil", "loc-store", 10, key: "rcpt-1");
        Assert.Equal(first.Entries.Single().EntryId, replay.Entries.Single().EntryId);
        Assert.Equal(10m, await OnHandAsync("var-oil", "loc-store"));

        var over = await inventory.PostAsync([new Movement(G("var-oil"), G("loc-store"), "Saleable", "Issue", -11)], "issue-1", default);
        Assert.Equal(InventoryService.Outcome.Insufficient, over.Outcome);
        Assert.Equal(10m, await OnHandAsync("var-oil", "loc-store"));

        var moved = await inventory.TransferAsync(new TransferInput(G("var-oil"), G("loc-store"), G("loc-shelf"), null, 4), "tr-1", default);
        Assert.Equal(InventoryService.Outcome.Ok, moved.Outcome);
        Assert.Equal(6m, await OnHandAsync("var-oil", "loc-store"));
        Assert.Equal(4m, await OnHandAsync("var-oil", "loc-shelf"));
        Assert.Equal(await fixture.ScalarAsync<decimal>($"SELECT sum(quantity) FROM inventory.inventory_ledger_entry WHERE inventory_item_variant_id = '{Id("var-oil")}'"),
            await fixture.ScalarAsync<decimal>($"SELECT sum(on_hand) FROM inventory.inventory_location_balance WHERE inventory_item_variant_id = '{Id("var-oil")}'"));
    }

    [RequiresPostgres]
    public async Task A_count_posts_its_variance_only_when_someone_other_than_the_counter_approves_it()
    {
        await FreshAsync();
        StockCountRowRef count;
        using (var counter = Scope("principal-counter"))
        {
            await ReceiveAsync(counter, "var-oil", "loc-shelf", 8);
            var inventory = counter.ServiceProvider.GetRequiredService<InventoryService>();
            var opened = await inventory.OpenCountAsync(new CountInput(G("var-oil"), G("loc-shelf"), null), default);
            Assert.Equal(EditOutcome.Ok, opened.Outcome);
            Assert.Null(opened.Row!.VarianceQuantity);
            Assert.Equal(8m, opened.Row.ExpectedQuantity);
            var recorded = await inventory.RecordCountAsync(opened.Row.StockCountId, opened.Row.Version, 5, false, "Breakage", default);
            Assert.Equal(EditOutcome.Ok, recorded.Outcome);
            var own = await inventory.ApproveCountAsync(opened.Row.StockCountId, recorded.Row!.Version, default);
            Assert.Equal(InventoryService.Outcome.SameCounter, own.Outcome);
            count = new(opened.Row.StockCountId, recorded.Row.Version);
        }
        using (var approver = Scope("principal-approver"))
        {
            // Two more arrive between the count and its approval: they are not written off.
            await ReceiveAsync(approver, "var-oil", "loc-shelf", 2);
            var r = await approver.ServiceProvider.GetRequiredService<InventoryService>().ApproveCountAsync(count.Id, count.Version, default);
            Assert.Equal(InventoryService.Outcome.Ok, r.Outcome);
            Assert.Equal("Posted", r.Row!.Status);
        }
        Assert.Equal(5m, await OnHandAsync("var-oil", "loc-shelf"));
        Assert.Equal(-5m, await fixture.ScalarAsync<decimal>(
            $"SELECT quantity FROM inventory.inventory_ledger_entry WHERE movement_type = 'CountVariance' AND source_document_id = '{count.Id}'"));
    }

    private sealed record StockCountRowRef(Guid Id, int Version);

    [RequiresPostgres]
    public async Task A_completed_treatment_consumes_its_supplies_and_soils_its_linen_once()
    {
        await FreshAsync();
        using var s = Scope();
        await ReceiveAsync(s, "var-oil", "loc-store", 5);
        await ReceiveAsync(s, "var-towel", "loc-store", 10, "Clean");
        var inventory = s.ServiceProvider.GetRequiredService<InventoryService>();
        Assert.Equal(EditOutcome.Ok, (await InTx(s, () => inventory.AddSupplyAsync(new SupplyInput(G("svc-swedish"), G("var-oil"), 0.5m, 0.1m, false), default))).Outcome);
        Assert.Equal(EditOutcome.Ok, (await InTx(s, () => inventory.AddSupplyAsync(new SupplyInput(G("svc-swedish"), G("var-towel"), 2, 0, true), default))).Outcome);

        var scheduling = s.ServiceProvider.GetRequiredService<SchedulingService>();
        var uow = s.ServiceProvider.GetRequiredService<IUnitOfWork>();
        Appointment a;
        await using (var tx = await uow.BeginAsync())
        {
            var r = await scheduling.CreateAsync(Id(T), Id(P), new SchedulingService.NewBooking(Id("use1"), Id("guest-1"), "G", Id("svc-swedish"),
                Now.AddHours(1), Id("prov-lena"), Id("room-1"), null, "corr"), null);
            Assert.Equal(SchedulingService.CreateOutcome.Created, r.Outcome);
            a = r.Appointment!;
            await tx.CommitAsync();
        }
        var v = a.RowVersion;
        foreach (var to in new[] { AppointmentStatus.CheckedIn, AppointmentStatus.Ready, AppointmentStatus.InService, AppointmentStatus.Completed })
        {
            var r = await scheduling.TransitionAsync(Id(T), Id(P), a.AppointmentId, to, v, null, "corr");
            Assert.Equal(SchedulingService.TransitionOutcome.Applied, r.Outcome);
            v = r.Appointment!.RowVersion;
        }
        Assert.Equal(4.45m, await OnHandAsync("var-oil", "loc-store"));           // 0.5 plus 10% waste
        Assert.Equal(8m, await OnHandAsync("var-towel", "loc-store", "Clean"));
        Assert.Equal(2m, await OnHandAsync("var-towel", "loc-store", "Soiled"));
        Assert.Equal(3L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM inventory.inventory_ledger_entry WHERE source_document_type = 'Appointment' AND source_document_id = '{a.AppointmentId}'"));
    }

    [RequiresPostgres]
    public async Task An_expired_credential_stops_qualifying_and_its_qualifications_expire_with_it()
    {
        await FreshAsync();
        await fixture.ExecAsync($"DELETE FROM workforce.staff_qualification WHERE staff_id = '{Id("prov-known")}'");
        Guid credential, qualification;
        using (var hr = Scope("principal-hr"))
        {
            var workforce = hr.ServiceProvider.GetRequiredService<WorkforceService>();
            var added = await workforce.AddCredentialAsync(G("prov-known"), new CredentialInput("License", "LMT", "US-NY", null, "LIC-99887766", null, null, null),
                null, new DateOnly(2026, 7, 10), default);
            Assert.Equal(EditOutcome.Ok, added.Outcome);
            Assert.Equal("7766", added.Row!.NumberLast4);
            Assert.DoesNotContain("99887766", System.Text.Encoding.UTF8.GetString(added.Row.NumberCipher!));
            var verified = await workforce.VerifyCredentialAsync(added.Row.CredentialId, added.Row.Version, true, null, default);
            Assert.Equal(EditOutcome.Ok, verified.Outcome);
            credential = added.Row.CredentialId;
            var granted = await workforce.GrantAsync(G("prov-known"), G("svc-aroma"), credential, default);
            Assert.Equal(EditOutcome.Ok, granted.Outcome);
            qualification = granted.Row!.QualificationId;
        }
        using (var later = Scope("principal-system", Now.AddDays(12)))
        {
            var job = later.ServiceProvider.GetServices<IPropertyJob>().Single(j => j.Name == "workforce.credential-expiry");
            Assert.Equal(1, await InTx(later, () => job.RunAsync(default)));
        }
        Assert.Equal("Expired", await fixture.ScalarAsync<string>($"SELECT status FROM workforce.credential WHERE credential_id = '{credential}'"));
        Assert.Equal("Expired", await fixture.ScalarAsync<string>($"SELECT status FROM workforce.staff_qualification WHERE qualification_id = '{qualification}'"));
    }

    [RequiresPostgres]
    public async Task A_role_needs_a_second_person_and_its_approval_is_an_outbox_event_for_OpenFGA()
    {
        await FreshAsync();
        await fixture.ExecAsync($"""
            INSERT INTO core.principal (principal_id, tenant_id, principal_type, display_name) VALUES ('{Id("principal-marco")}', '{Id(T)}', 'Staff', 'Marco') ON CONFLICT DO NOTHING;
            UPDATE workforce.staff SET principal_id = '{Id("principal-marco")}', version = version + 1 WHERE staff_id = '{Id("prov-marco")}' AND principal_id IS NULL;
            DELETE FROM workforce.staff_role_assignment WHERE staff_id = '{Id("prov-marco")}';
            """);
        Guid assignment;
        int version;
        using (var proposer = Scope("principal-admin-1"))
        {
            var workforce = proposer.ServiceProvider.GetRequiredService<WorkforceService>();
            var p = await workforce.ProposeRoleAsync(G("prov-marco"), "scheduler", G(P), default);
            Assert.Equal(EditOutcome.Ok, p.Outcome);
            assignment = p.Row!.StaffRoleAssignmentId;
            version = p.Row.Version;
            Assert.Equal(WorkforceService.RoleOutcome.SameApprover, (await workforce.DecideRoleAsync(assignment, version, "Active", null, default)).Outcome);
        }
        using (var approver = Scope("principal-admin-2"))
        {
            var r = await approver.ServiceProvider.GetRequiredService<WorkforceService>().DecideRoleAsync(assignment, version, "Active", null, default);
            Assert.Equal(WorkforceService.RoleOutcome.Ok, r.Outcome);
            Assert.Equal("Active", r.Row!.Status);
        }
        var payload = await fixture.ScalarAsync<string>(
            $"SELECT payload::text FROM core.event_outbox WHERE event_type = '{EventTypes.RoleAssignmentChanged}' AND aggregate_id = '{assignment}'");
        Assert.Contains("\"roleCode\": \"scheduler\"", payload);
        Assert.Contains(Id("principal-marco"), payload);
    }

    [RequiresPostgres]
    public async Task A_setting_approved_for_a_later_date_waits_and_the_value_it_replaces_ends_there()
    {
        await FreshAsync();
        await fixture.ExecAsync($"DELETE FROM core.setting WHERE tenant_id = '{Id(T)}' AND setting_key = 'policy.cancellation'");
        Guid first, second;
        using (var author = Scope("principal-author"))
        {
            var admin = author.ServiceProvider.GetRequiredService<SettingsAdmin>();
            var v = System.Text.Json.JsonDocument.Parse("{\"noticeHours\":24,\"feePercent\":50}").RootElement;
            first = (await InTx(author, () => admin.ProposeAsync(new SettingProposal("policy.cancellation", v, false, null, null, "launch"), Now, default))).Row!.SettingId;
            var v2 = System.Text.Json.JsonDocument.Parse("{\"noticeHours\":12,\"feePercent\":25}").RootElement;
            second = (await InTx(author, () => admin.ProposeAsync(new SettingProposal("policy.cancellation", v2, false, null, null, "softer"), Now.AddDays(10), default))).Row!.SettingId;
            Assert.Equal(SettingsAdmin.Outcome.SameApprover, (await admin.DecideAsync(first, 1, true, null, default)).Outcome);
        }
        using (var approver = Scope("principal-approver"))
        {
            var admin = approver.ServiceProvider.GetRequiredService<SettingsAdmin>();
            Assert.Equal("Active", (await admin.DecideAsync(first, 1, true, null, default)).Row!.Status);
            Assert.Equal("Approved", (await admin.DecideAsync(second, 1, true, null, default)).Row!.Status);
            var reader = approver.ServiceProvider.GetRequiredService<Spms.Modules.Core.Infrastructure.SettingsReader>();
            var now = await InTx(approver, () => reader.GetAsync("policy.cancellation", default));
            Assert.Equal(24, now!.Value.GetProperty("noticeHours").GetInt32());
        }
        using (var later = Scope("principal-system", Now.AddDays(11)))
        {
            var job = later.ServiceProvider.GetServices<IPropertyJob>().Single(j => j.Name == "core.setting-activation");
            Assert.Equal(2, await InTx(later, () => job.RunAsync(default)));
            var reader = later.ServiceProvider.GetRequiredService<Spms.Modules.Core.Infrastructure.SettingsReader>();
            Assert.Equal(12, (await InTx(later, () => reader.GetAsync("policy.cancellation", default)))!.Value.GetProperty("noticeHours").GetInt32());
        }
        Assert.Equal("Superseded", await fixture.ScalarAsync<string>($"SELECT status FROM core.setting WHERE setting_id = '{first}'"));
    }
}
