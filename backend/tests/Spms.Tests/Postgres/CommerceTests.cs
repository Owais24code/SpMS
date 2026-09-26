using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Commerce.Application;
using Spms.Modules.Commerce.Data;
using Spms.Modules.Commerce.Payments;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>
/// Batch 5 against the real database: orders with frozen prices and tax,
/// payments through the simulated provider (approve, decline, an unknown
/// outcome resolved by lookup, never by charging again), deposits applied and
/// forfeited, refunds under dual control, and payment ownership.
/// </summary>
public class CommerceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string T = PostgresWorld.Tenant;
    private const string P = PostgresWorld.Property;
    private static string Id(string name) => TestIds.Of(name);

    private async Task FreshAsync()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(T, P);
        await fixture.ExecAsync($"""
            TRUNCATE commerce.commerce_reference, commerce.payment_transaction, commerce.payment_intent,
                     commerce.order_line, commerce.commerce_order CASCADE;
            DELETE FROM core.capability_ownership WHERE property_id = '{Id(P)}';
            UPDATE catalog.service SET base_price_minor = 11000, tax_code = 'SPA', deposit_required = true, version = version + 1
             WHERE service_id = '{Id("svc-swedish")}' AND (base_price_minor <> 11000 OR tax_code IS DISTINCT FROM 'SPA' OR NOT deposit_required);
            INSERT INTO catalog.tax_rule (tax_rule_id, tenant_id, property_id, tax_code, jurisdiction, rate, status, effective_from)
            VALUES ('{Id("tax-spa")}', '{Id(T)}', '{Id(P)}', 'SPA', 'TEST', 0.1, 'Active', '2020-01-01Z') ON CONFLICT DO NOTHING;
            INSERT INTO core.setting (setting_id, tenant_id, setting_key, value_json, status, effective_from, created_by, approved_by, approved_at)
            VALUES ('{Id("setting-deposit")}', '{Id(T)}', 'policy.deposit', '{DepositPolicy}', 'Active', '2020-01-01Z',
                    '{Id("principal-author")}', '{Id("principal-approver")}', '2020-01-01Z') ON CONFLICT DO NOTHING;
            """);
    }

    private const string DepositPolicy = "{\"percent\":20,\"minimumMinor\":0}";

    private static readonly DateTimeOffset Now = new(2026, 7, 1, 13, 0, 0, TimeSpan.Zero);

    private IServiceScope Scope(string principal = "principal-desk", DateTimeOffset? at = null) =>
        fixture.NewScope(T, [P], P, new TestClock(at ?? Now), principal);

    private static async Task<Appointment> BookAsync(IServiceScope s, string id, string room, string guest, int hoursFromNow = 1)
    {
        var uow = s.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var scheduling = s.ServiceProvider.GetRequiredService<SchedulingService>();
        await using var tx = await uow.BeginAsync();
        var r = await scheduling.CreateAsync(Id(T), Id(P), new SchedulingService.NewBooking(
            Id(id), Id(guest), "G", Id("svc-swedish"), Now.AddHours(hoursFromNow), Id("prov-lena"), Id(room), null, "corr"), null);
        Assert.Equal(SchedulingService.CreateOutcome.Created, r.Outcome);
        await tx.CommitAsync();
        return r.Appointment!;
    }

    private static async Task<OrderView> CartAsync(IServiceScope s, string appointment)
    {
        var commerce = s.ServiceProvider.GetRequiredService<CommerceService>();
        var created = await commerce.CreateAsync(null, null, default);
        Assert.Equal(CommerceService.Outcome.Ok, created.Outcome);
        var added = await commerce.AddLineAsync(created.Value!.Order.CommerceOrderId,
            new NewLine("Service", TestIds.IdOf(appointment), null, null, 1, null, 0, null), default);
        Assert.Equal(CommerceService.Outcome.Ok, added.Outcome);
        return added.Value!;
    }

    /// <summary>Reads run in a scoped transaction, as they would inside a request.</summary>
    private static async Task<TResult> InTx<TResult>(IServiceScope scope, Func<Task<TResult>> work)
    {
        await using var tx = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync();
        var r = await work();
        await tx.CommitAsync();
        return r;
    }

    private static string Key() => Guid.NewGuid().ToString("N");

    [RequiresPostgres]
    public async Task An_order_line_takes_the_frozen_booking_price_and_the_property_tax_and_the_order_is_numbered_when_placed()
    {
        await FreshAsync();
        using var s = Scope();
        await BookAsync(s, "co1", "room-1", "guest-1");
        // The catalogue price changes after booking; the order keeps the booked price.
        await fixture.ExecAsync($"UPDATE catalog.service SET base_price_minor = 99000, version = version + 1 WHERE service_id = '{Id("svc-swedish")}'");
        try
        {
            var cart = await CartAsync(s, "co1");
            var line = Assert.Single(cart.Lines);
            Assert.Equal(11000, line.UnitPriceMinor);
            Assert.Equal(1100, line.TaxMinor);
            Assert.Equal(12100, cart.Order.TotalMinor);
            Assert.Equal(TestIds.IdOf("guest-1"), cart.Order.GuestId);
            Assert.Equal("Draft", cart.Order.Status);

            var commerce = s.ServiceProvider.GetRequiredService<CommerceService>();
            var stale = await commerce.PlaceAsync(cart.Order.CommerceOrderId, cart.Order.Version - 1, default);
            Assert.Equal(CommerceService.Outcome.StaleVersion, stale.Outcome);
            var placed = await commerce.PlaceAsync(cart.Order.CommerceOrderId, cart.Order.Version, default);
            Assert.Equal(CommerceService.Outcome.Ok, placed.Outcome);
            Assert.Equal("Open", placed.Value!.Order.Status);
            Assert.StartsWith("ORD", placed.Value.Order.OrderNumber);
            var late = await commerce.AddLineAsync(cart.Order.CommerceOrderId, new NewLine("Fee", null, null, "Late", 1, 500, 0, null), default);
            Assert.Equal(CommerceService.Outcome.Illegal, late.Outcome);
        }
        finally
        {
            await fixture.ExecAsync($"UPDATE catalog.service SET base_price_minor = 11000, version = version + 1 WHERE service_id = '{Id("svc-swedish")}'");
        }
    }

    [RequiresPostgres]
    public async Task A_declined_card_leaves_the_balance_an_approved_one_pays_it_and_a_replayed_key_does_not_charge_twice()
    {
        await FreshAsync();
        using var s = Scope();
        await BookAsync(s, "co2", "room-2", "guest-2");
        var cart = await CartAsync(s, "co2");
        var commerce = s.ServiceProvider.GetRequiredService<CommerceService>();
        var placed = (await commerce.PlaceAsync(cart.Order.CommerceOrderId, cart.Order.Version, default)).Value!;
        var order = placed.Order.CommerceOrderId;

        var declined = await commerce.PayAsync(order, Key(), Tenders.Card, null, "tok_decline", default);
        Assert.Equal(CommerceService.Outcome.Ok, declined.Outcome);
        Assert.Equal(GatewayOutcome.Declined, declined.Value!.Outcome);
        Assert.Equal(12100, (await InTx(s, () => commerce.ViewAsync(order, default)))!.BalanceMinor);

        var key = Key();
        var paid = await commerce.PayAsync(order, key, Tenders.Card, null, "tok_approve", default);
        Assert.Equal(GatewayOutcome.Approved, paid.Value!.Outcome);
        var again = await commerce.PayAsync(order, key, Tenders.Card, null, "tok_approve", default);
        Assert.Equal(paid.Value.Intent.PaymentIntentId, again.Value!.Intent.PaymentIntentId);

        var view = (await InTx(s, () => commerce.ViewAsync(order, default)))!;
        Assert.Equal("Paid", view.Order.Status);
        Assert.StartsWith("RCT", view.Order.ReceiptNumber);
        Assert.Equal(0, view.BalanceMinor);
        Assert.Single(view.Transactions, t => t.Outcome == "Approved");
        var over = await commerce.PayAsync(order, Key(), Tenders.Cash, 100, null, default);
        Assert.NotEqual(CommerceService.Outcome.Ok, over.Outcome);
    }

    [RequiresPostgres]
    public async Task An_unknown_provider_outcome_is_ambiguous_until_a_lookup_settles_it_and_the_job_finds_it()
    {
        await FreshAsync();
        using var s = Scope();
        await BookAsync(s, "co3", "room-3", "guest-3");
        var cart = await CartAsync(s, "co3");
        var commerce = s.ServiceProvider.GetRequiredService<CommerceService>();
        var order = (await commerce.PlaceAsync(cart.Order.CommerceOrderId, cart.Order.Version, default)).Value!.Order.CommerceOrderId;

        var unknown = await commerce.PayAsync(order, Key(), Tenders.Card, null, "tok_timeout", default);
        Assert.Equal(CommerceService.Outcome.Ambiguous, unknown.Outcome);
        Assert.NotNull(unknown.Value);
        Assert.Equal("Open", (await InTx(s, () => commerce.ViewAsync(order, default)))!.Order.Status);

        // The nightly desk sees it; the job asks the provider and settles it (BR-014).
        var recon = s.ServiceProvider.GetRequiredService<ReconciliationService>();
        var day = await InTx(s, () => recon.DayAsync(DateOnly.FromDateTime(Now.UtcDateTime), Now.AddDays(-1), Now.AddDays(1), default));
        Assert.Contains(day.Ambiguous, i => i.PaymentIntentId == unknown.Value!.Intent.PaymentIntentId);

        var job = s.ServiceProvider.GetServices<IPropertyJob>().Single(j => j.Name == "commerce.ambiguous-payments");
        Assert.Equal(1, await InTx(s, () => job.RunAsync(default)));
        var view = (await InTx(s, () => commerce.ViewAsync(order, default)))!;
        Assert.Equal("Paid", view.Order.Status);
        var intent = await InTx(s, () => commerce.IntentAsync(unknown.Value!.Intent.PaymentIntentId, default));
        Assert.Equal("Succeeded", intent!.Status);
        Assert.Equal(0, await InTx(s, () => job.RunAsync(default)));
    }

    [RequiresPostgres]
    public async Task A_deposit_follows_the_policy_is_applied_to_the_order_and_shows_settled_on_arrivals()
    {
        await FreshAsync();
        using var s = Scope();
        var a = await BookAsync(s, "co4", "room-4", "guest-4");
        var status = s.ServiceProvider.GetRequiredService<IDepositStatus>();
        Assert.Equal("Pending", (await InTx(s, () => status.ForAppointmentsAsync([TestIds.IdOf("co4")])))[TestIds.IdOf("co4")]);

        var commerce = s.ServiceProvider.GetRequiredService<CommerceService>();
        var deposit = await commerce.TakeDepositAsync(TestIds.IdOf("co4"), Key(), null, Tenders.Card, "tok_approve", default);
        Assert.Equal(CommerceService.Outcome.Ok, deposit.Outcome);
        Assert.Equal(2200, deposit.Value!.Intent.AmountMinor);  // 20% of the frozen 11000
        Assert.Equal("Settled", (await InTx(s, () => status.ForAppointmentsAsync([TestIds.IdOf("co4")])))[TestIds.IdOf("co4")]);
        var twice = await commerce.TakeDepositAsync(TestIds.IdOf("co4"), Key(), null, Tenders.Card, "tok_approve", default);
        Assert.Equal(CommerceService.Outcome.Illegal, twice.Outcome);

        var cart = await CartAsync(s, "co4");
        var placed = (await commerce.PlaceAsync(cart.Order.CommerceOrderId, cart.Order.Version, default)).Value!;
        Assert.Equal("PartiallyPaid", placed.Order.Status);
        Assert.Equal(2200, placed.PaidMinor);
        Assert.Equal(12100 - 2200, placed.BalanceMinor);
    }

    [RequiresPostgres]
    public async Task A_no_show_forfeits_its_deposit_in_the_same_transaction()
    {
        await FreshAsync();
        Appointment a;
        using (var s = Scope())
        {
            a = await BookAsync(s, "co5", "room-5", "guest-5");
            var commerce = s.ServiceProvider.GetRequiredService<CommerceService>();
            Assert.Equal(CommerceService.Outcome.Ok, (await commerce.TakeDepositAsync(TestIds.IdOf("co5"), Key(), null, Tenders.Cash, null, default)).Outcome);
        }
        using (var later = Scope(at: Now.AddHours(3)))
        {
            var scheduling = later.ServiceProvider.GetRequiredService<SchedulingService>();
            var r = await scheduling.TransitionAsync(Id(T), Id(P), a.AppointmentId, AppointmentStatus.NoShow, a.RowVersion, "did not arrive", "corr");
            Assert.Equal(SchedulingService.TransitionOutcome.Applied, r.Outcome);
        }
        var forfeited = await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM commerce.payment_intent WHERE appointment_id = '{Id("co5")}' AND purpose = 'DepositForfeited' AND status = 'Succeeded'");
        Assert.Equal(1, forfeited);
    }

    [RequiresPostgres]
    public async Task A_refund_needs_a_second_person_and_cannot_exceed_what_was_taken()
    {
        await FreshAsync();
        Guid sale;
        PaymentIntentRow requested;
        using (var desk = Scope("principal-desk"))
        {
            await BookAsync(desk, "co6", "room-6", "guest-6");
            var cart = await CartAsync(desk, "co6");
            var commerce = desk.ServiceProvider.GetRequiredService<CommerceService>();
            var order = (await commerce.PlaceAsync(cart.Order.CommerceOrderId, cart.Order.Version, default)).Value!.Order.CommerceOrderId;
            sale = (await commerce.PayAsync(order, Key(), Tenders.Card, null, "tok_approve", default)).Value!.Transaction!.PaymentTransactionId;

            var refunds = desk.ServiceProvider.GetRequiredService<RefundService>();
            Assert.Equal(RefundService.Outcome.Invalid, (await refunds.RequestAsync(sale, 12101, "ServiceIssue", default)).Outcome);
            var r = await refunds.RequestAsync(sale, 3000, "ServiceIssue", default);
            Assert.Equal(RefundService.Outcome.Ok, r.Outcome);
            requested = r.Intent!;
            Assert.Equal("Requested", requested.Status);
            Assert.Equal(RefundService.Outcome.SameApprover, (await refunds.ApproveAsync(requested.PaymentIntentId, requested.Version, default)).Outcome);
        }
        using (var finance = Scope("principal-finance"))
        {
            var refunds = finance.ServiceProvider.GetRequiredService<RefundService>();
            var approved = await refunds.ApproveAsync(requested.PaymentIntentId, requested.Version, default);
            Assert.Equal(RefundService.Outcome.Ok, approved.Outcome);
            Assert.Equal("Succeeded", approved.Intent!.Status);
            Assert.Equal("Refund", approved.Transaction!.TransactionType);
            Assert.Equal(12100 - 3000, await InTx(finance, () => refunds.RefundableAsync(sale, default)));

            var view = (await InTx(finance, () => finance.ServiceProvider.GetRequiredService<CommerceService>().ViewAsync(approved.Intent.CommerceOrderId!.Value, default)))!;
            Assert.Equal("PartiallyRefunded", view.Order.Status);
            Assert.Equal(3000, view.RefundedMinor);
            Assert.Equal(0, view.BalanceMinor);
        }
    }

    [RequiresPostgres]
    public async Task When_Marquee_owns_payment_the_order_is_delegated_and_SpMS_takes_no_money()
    {
        await FreshAsync();
        await fixture.ExecAsync($"""
            INSERT INTO core.capability_ownership (tenant_id, property_id, capability_code, owner_system, effective_range, status, created_by, approved_by, approved_at)
            VALUES ('{Id(T)}', '{Id(P)}', 'Payment', 'Marquee', tstzrange('2020-01-01Z', NULL), 'Active', '{Id("principal-author")}', '{Id("principal-approver")}', now());
            """);
        using var s = Scope();
        await BookAsync(s, "co7", "room-7", "guest-7");
        var commerce = s.ServiceProvider.GetRequiredService<CommerceService>();
        var created = await commerce.CreateAsync(null, null, default);
        Assert.Equal(CommerceService.Outcome.Ok, created.Outcome);
        Assert.Equal("Delegated", created.Value!.Order.Status);
        Assert.Equal("Marquee", created.Value.Order.OwnerSystem);
        var reference = await fixture.ScalarAsync<string>(
            $"SELECT operation FROM commerce.commerce_reference WHERE commerce_order_id = '{created.Value.Order.CommerceOrderId}'");
        Assert.Equal("CreateCart", reference);
        Assert.Equal(CommerceService.Outcome.Delegated,
            (await commerce.TakeDepositAsync(TestIds.IdOf("co7"), Key(), null, Tenders.Card, "tok_approve", default)).Outcome);
    }

    [RequiresPostgres]
    public async Task A_Marquee_integrated_property_with_no_decision_is_ambiguous_and_refuses_to_guess()
    {
        await FreshAsync();
        await fixture.ExecAsync($"UPDATE core.property SET operating_mode = 'MarqueeIntegrated', version = version + 1 WHERE property_id = '{Id(P)}'");
        try
        {
            using var s = Scope();
            var created = await s.ServiceProvider.GetRequiredService<CommerceService>().CreateAsync(null, null, default);
            Assert.Equal(CommerceService.Outcome.Ambiguous, created.Outcome);
        }
        finally
        {
            await fixture.ExecAsync($"UPDATE core.property SET operating_mode = 'Standalone', version = version + 1 WHERE property_id = '{Id(P)}'");
        }
    }
}
