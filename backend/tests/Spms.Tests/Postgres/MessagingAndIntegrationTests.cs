using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Core.Integrations;
using Spms.Modules.Guest.Profiles;
using Spms.Modules.Messaging;
using Spms.Modules.Reporting;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>
/// Batch 7 against the real database: reminders scheduled once per template
/// and booking and cancelled with it, quiet hours, dispatch with retry and
/// expiry, inbound events consumed exactly once, and report results that can
/// prove they were not altered.
/// </summary>
public class MessagingAndIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string T = PostgresWorld.Tenant;
    private const string P = PostgresWorld.Property;
    private static string Id(string name) => TestIds.Of(name);
    // 10:00 UTC; the test property is in UTC, so the default quiet hours (21:00–08:00) are UTC too.
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);

    private async Task FreshAsync()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(T, P);
        await fixture.ExecAsync($"""
            TRUNCATE messaging.scheduled_message, core.event_inbox CASCADE;
            DELETE FROM messaging.message_template WHERE tenant_id = '{Id(T)}';
            DELETE FROM core.external_mapping WHERE tenant_id = '{Id(T)}';
            INSERT INTO messaging.message_template (message_template_id, tenant_id, template_code, version_number, channel, locale, purpose, subject, body_template,
                                                    trigger_event, offset_minutes, status, created_by, approved_by, approved_at)
            VALUES ('{Id("tpl-reminder")}', '{Id(T)}', 'reminder', 1, 'Email', 'en-US', 'Reminder', 'Soon', '{ReminderBody}',
                    'BeforeStart', -600, 'Active', '{Id("principal-author")}', '{Id("principal-approver")}', now());
            """);
    }

    private const string ReminderBody = "Your {{serviceName}} at {{startLocal}}";

    private IServiceScope Scope(DateTimeOffset? at = null, string principal = "principal-desk") =>
        fixture.NewScope(T, [P], P, new TestClock(at ?? Now), principal);

    private static async Task<TResult> InTx<TResult>(IServiceScope scope, Func<Task<TResult>> work)
    {
        await using var tx = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync();
        var r = await work();
        await tx.CommitAsync();
        return r;
    }

    private static async Task<Guid> GuestAsync(IServiceScope s, string email)
    {
        var r = await s.ServiceProvider.GetRequiredService<GuestProfileService>()
            .CreateAsync(new NewGuest("Test", "Guest", null, null, "en", null, email, null, ContactsVerifiedInPerson: true), default);
        Assert.Equal(GuestProfileService.Outcome.Ok, r.Outcome);
        return r.View!.Guest.GuestId;
    }

    private static async Task<Appointment> BookAsync(IServiceScope s, string id, Guid guest, DateTimeOffset start, string room, string provider = "prov-lena")
    {
        var uow = s.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var tx = await uow.BeginAsync();
        var r = await s.ServiceProvider.GetRequiredService<SchedulingService>().CreateAsync(Id(T), Id(P), new SchedulingService.NewBooking(
            Id(id), guest.ToString(), "G", Id("svc-swedish"), start, Id(provider), Id(room), null, "corr"), null);
        Assert.Equal(SchedulingService.CreateOutcome.Created, r.Outcome);
        await tx.CommitAsync();
        return r.Appointment!;
    }

    [RequiresPostgres]
    public async Task A_reminder_is_scheduled_once_out_of_quiet_hours_and_cancelled_with_its_booking()
    {
        await FreshAsync();
        using var s = Scope();
        var guest = await GuestAsync(s, $"r{Guid.NewGuid():N}"[..12] + "@example.test");
        // Starts at 15:00 tomorrow; ten hours before is 05:00, inside quiet hours, so it moves to 08:00.
        var a = await BookAsync(s, "rem1", guest, Now.Date.AddDays(1).AddHours(15), "room-1");
        var messaging = s.ServiceProvider.GetRequiredService<MessagingService>();
        Assert.Equal(1, await InTx(s, () => messaging.ScheduleDueAsync(default)));
        Assert.Equal(0, await InTx(s, () => messaging.ScheduleDueAsync(default)));
        var sendAfter = await fixture.ScalarAsync<DateTime>($"SELECT send_after FROM messaging.scheduled_message WHERE appointment_id = '{a.AppointmentId}'");
        Assert.Equal(new DateTime(2026, 7, 2, 8, 0, 0, DateTimeKind.Utc), DateTime.SpecifyKind(sendAfter, DateTimeKind.Utc));
        var stored = await fixture.ScalarAsync<byte[]>($"SELECT recipient_address_cipher FROM messaging.scheduled_message WHERE appointment_id = '{a.AppointmentId}'");
        Assert.DoesNotContain("@example.test", System.Text.Encoding.UTF8.GetString(stored));

        var cancelled = await s.ServiceProvider.GetRequiredService<SchedulingService>()
            .TransitionAsync(Id(T), Id(P), a.AppointmentId, AppointmentStatus.Cancelled, a.RowVersion, "guest called", "corr", reasonCode: "GuestRequest");
        Assert.Equal(SchedulingService.TransitionOutcome.Applied, cancelled.Outcome);
        await InTx(s, () => messaging.ScheduleDueAsync(default));
        Assert.Equal("Cancelled", await fixture.ScalarAsync<string>($"SELECT status FROM messaging.scheduled_message WHERE appointment_id = '{a.AppointmentId}'"));
    }

    [RequiresPostgres]
    public async Task Dispatch_sends_retries_a_transient_failure_and_expires_what_is_too_late()
    {
        await FreshAsync();
        Guid ok, flaky;
        using (var s = Scope())
        {
            var g1 = await GuestAsync(s, $"ok{Guid.NewGuid():N}"[..10] + "@example.test");
            var g2 = await GuestAsync(s, $"fail{Guid.NewGuid():N}"[..10] + "@example.test");
            var a1 = await BookAsync(s, "dsp1", g1, Now.AddHours(11), "room-2");
            var a2 = await BookAsync(s, "dsp2", g2, Now.AddHours(11), "room-3", "prov-marco");
            await InTx(s, () => s.ServiceProvider.GetRequiredService<MessagingService>().ScheduleDueAsync(default));
            ok = await fixture.ScalarAsync<Guid>($"SELECT scheduled_message_id FROM messaging.scheduled_message WHERE appointment_id = '{a1.AppointmentId}'");
            flaky = await fixture.ScalarAsync<Guid>($"SELECT scheduled_message_id FROM messaging.scheduled_message WHERE appointment_id = '{a2.AppointmentId}'");
        }
        using (var s = Scope(Now.AddHours(1).AddMinutes(1)))
        {
            Assert.Equal(2, await InTx(s, () => s.ServiceProvider.GetRequiredService<MessagingService>().DispatchDueAsync(default)));
        }
        Assert.Equal("Sent", await fixture.ScalarAsync<string>($"SELECT status FROM messaging.scheduled_message WHERE scheduled_message_id = '{ok}'"));
        Assert.Equal("Scheduled", await fixture.ScalarAsync<string>($"SELECT status FROM messaging.scheduled_message WHERE scheduled_message_id = '{flaky}'"));
        Assert.Equal((short)1, await fixture.ScalarAsync<short>($"SELECT attempt_count FROM messaging.scheduled_message WHERE scheduled_message_id = '{flaky}'"));
        // Retried until the treatment begins; after that it expires rather than going out late.
        using (var late = Scope(Now.AddHours(12)))
        {
            await InTx(late, () => late.ServiceProvider.GetRequiredService<MessagingService>().DispatchDueAsync(default));
        }
        Assert.Equal("Expired", await fixture.ScalarAsync<string>($"SELECT status FROM messaging.scheduled_message WHERE scheduled_message_id = '{flaky}'"));
    }

    [RequiresPostgres]
    public async Task An_inbound_event_is_consumed_exactly_once_and_its_guest_mapped_once()
    {
        await FreshAsync();
        using var s = Scope(principal: "principal-connector");
        var integrations = s.ServiceProvider.GetRequiredService<IntegrationService>();
        var key = $"mq-{Guid.NewGuid():N}"[..12];
        var eventId = Guid.NewGuid();
        var payload = JsonDocument.Parse($"{{\"guestKey\":\"{key}\",\"firstName\":\"Ines\",\"lastName\":\"Park\"}}").RootElement;
        var first = await integrations.ConsumeAsync("marquee", eventId, "marquee.guest.upserted", payload, default);
        Assert.Equal(IntegrationService.InboundOutcome.Processed, first.Outcome);
        var second = await integrations.ConsumeAsync("marquee", eventId, "marquee.guest.upserted", payload, default);
        Assert.Equal(IntegrationService.InboundOutcome.Duplicate, second.Outcome);
        Assert.Equal(1L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM core.external_mapping WHERE source_key = '{key}'"));
        var rejected = await integrations.ConsumeAsync("marquee", Guid.NewGuid(), "marquee.guest.upserted", JsonDocument.Parse("{}").RootElement, default);
        Assert.Equal(IntegrationService.InboundOutcome.Rejected, rejected.Outcome);
    }

    [RequiresPostgres]
    public async Task A_report_run_keeps_a_hash_that_exposes_any_later_change_to_its_result()
    {
        await FreshAsync();
        using var s = Scope();
        var guest = await GuestAsync(s, $"rep{Guid.NewGuid():N}"[..11] + "@example.test");
        await BookAsync(s, "rep1", guest, Now.AddHours(2), "room-4");
        var reports = s.ServiceProvider.GetRequiredService<ReportService>();
        var d = ReportService.Find("operations.daily")!;
        var day = DateOnly.FromDateTime(Now.UtcDateTime);
        var run = await reports.RunAsync(d, day, "UTC", ReportService.DayStartUtc(day, "UTC"), ReportService.DayStartUtc(day.AddDays(1), "UTC"), default);
        Assert.Equal("Succeeded", run.Status);
        Assert.Contains("\"Confirmed\"", run.ResultJson);
        Assert.Equal((true, true), ReportService.Verify(run));
        await fixture.ExecAsync($"UPDATE reporting.report_run SET result_json = jsonb_set(result_json, '{{date}}', '\"1999-01-01\"'), version = version + 1 WHERE report_run_id = '{run.ReportRunId}'");
        var tampered = (await InTx(s, () => reports.RunAsync(run.ReportRunId, default)))!;
        Assert.False(ReportService.Verify(tampered).ResultIntact);
    }
}
