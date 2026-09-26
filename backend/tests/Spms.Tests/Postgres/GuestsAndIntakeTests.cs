using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Spms.Modules.Core.Data;
using Spms.Modules.Guest.Data;
using Spms.Modules.Guest.Profiles;
using Spms.Modules.Intake.Application;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

/// <summary>Batch 4: guest profiles, merge and split, delegation, consent, privacy and intake, against the real database.</summary>
public class GuestsAndIntakeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string T = PostgresWorld.Tenant;
    private const string P = PostgresWorld.Property;

    private async Task FreshAsync()
    {
        await fixture.ResetBoardAsync();
        await fixture.SeedReferenceAsync(T, P);
    }

    private IServiceScope Scope(string principal = "principal-test", TestClock? clock = null) =>
        fixture.NewScope(T, [P], P, clock ?? new TestClock(DateTimeOffset.UtcNow), principal);

    private static async Task<TResult> InTx<TResult>(IServiceScope scope, Func<Task<TResult>> work)
    {
        await using var tx = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync();
        var r = await work();
        await tx.CommitAsync();
        return r;
    }

    private static string Unique(string stem) => $"{stem}.{Guid.NewGuid():N}"[..(stem.Length + 9)];

    private static async Task<GuestRow> NewGuestAsync(IServiceScope scope, string first, string last, string? email = null, DateOnly? birth = null)
    {
        var r = await scope.ServiceProvider.GetRequiredService<GuestProfileService>()
            .CreateAsync(new NewGuest(first, last, null, null, "en", birth, email, null, ContactsVerifiedInPerson: true), default);
        Assert.Equal(GuestProfileService.Outcome.Ok, r.Outcome);
        return r.View!.Guest;
    }

    /* -------------------------------- profiles ------------------------------- */

    [RequiresPostgres]
    public async Task A_guest_is_found_by_email_hash_and_name_and_a_second_record_with_that_email_becomes_a_merge_candidate()
    {
        await FreshAsync();
        using var s = Scope();
        var guests = s.ServiceProvider.GetRequiredService<GuestProfileService>();
        var email = $"{Unique("ava")}@Example.com";
        var ava = await NewGuestAsync(s, "Ava", "Reyes", email);
        Assert.Equal("Ava R.", ava.DisplayAlias);
        Assert.Matches("^G[A-Z0-9]{5}$", ava.PublicQueueId!);

        var byEmail = await InTx(s, () => guests.SearchAsync("  " + email.ToUpperInvariant() + " ", 10, default));
        Assert.Equal([ava.GuestId], byEmail.Select(g => g.Guest.GuestId));
        Assert.StartsWith("a***@", byEmail[0].Contacts.Single().DisplayHint);
        var byName = await InTx(s, () => guests.SearchAsync("reyes ava", 10, default));
        Assert.Contains(byName, g => g.Guest.GuestId == ava.GuestId);

        // The contact value is stored encrypted: the plaintext is nowhere in the row.
        var stored = await fixture.ScalarAsync<byte[]>($"SELECT contact_cipher FROM guest.guest_contact_point WHERE guest_id = '{ava.GuestId}'");
        Assert.DoesNotContain(email.ToLowerInvariant(), System.Text.Encoding.UTF8.GetString(stored));

        var second = await s.ServiceProvider.GetRequiredService<GuestProfileService>()
            .CreateAsync(new NewGuest("Ava", "Reyes-Smith", null, null, null, null, email, null, false), default);
        Assert.Equal([ava.GuestId], second.PossibleDuplicates);
        var candidates = await InTx(s, () => s.ServiceProvider.GetRequiredService<GuestMergeService>().ListAsync(GuestMergeCaseStatuses.Candidate, default));
        Assert.Contains(candidates, m => m.SurvivingGuestId == ava.GuestId && m.DuplicateGuestId == second.View!.Guest.GuestId);
    }

    [RequiresPostgres]
    public async Task Preferences_are_operational_only_and_a_stale_update_is_refused()
    {
        await FreshAsync();
        using var s = Scope();
        var guests = s.ServiceProvider.GetRequiredService<GuestProfileService>();
        var g = await NewGuestAsync(s, "Ben", "Ode");

        var health = await guests.UpdateAsync(g.GuestId, g.Version, new GuestChanges(null, null, null, null, null, null, false,
            new Dictionary<string, string> { ["allergies"] = "nuts" }), default);
        Assert.Equal(GuestProfileService.Outcome.Invalid, health.Outcome);

        var ok = await guests.UpdateAsync(g.GuestId, g.Version, new GuestChanges(null, null, "Benny", null, null, null, false,
            new Dictionary<string, string> { ["pressure"] = "Firm", ["music"] = "Nature" }), default);
        Assert.Equal(GuestProfileService.Outcome.Ok, ok.Outcome);
        Assert.Equal("Benny", ok.View!.Guest.PreferredName);
        Assert.Equal(g.Version + 1, ok.View.Guest.Version);

        var stale = await guests.UpdateAsync(g.GuestId, g.Version, new GuestChanges(null, null, "B", null, null, null, false, null), default);
        Assert.Equal(GuestProfileService.Outcome.StaleVersion, stale.Outcome);
    }

    /* ---------------------------------- merge -------------------------------- */

    [RequiresPostgres]
    public async Task A_merge_needs_a_second_person_moves_contacts_and_a_split_moves_exactly_them_back()
    {
        await FreshAsync();
        GuestRow keep, dup;
        GuestMergeCaseRow proposed;
        using (var desk = Scope("principal-desk"))
        {
            keep = await NewGuestAsync(desk, "Cara", "Lind");
            dup = await NewGuestAsync(desk, "Kara", "Lind", $"{Unique("kara")}@example.com");
            var merges = desk.ServiceProvider.GetRequiredService<GuestMergeService>();
            proposed = (await merges.ProposeAsync(keep.GuestId, dup.GuestId, "same person, typo", default)).Row!;
            var self = await merges.DecideAsync(proposed.GuestMergeCaseId, proposed.Version, approve: true, null, default);
            Assert.Equal(GovOutcome.SameReviewer, self.Outcome);
        }

        using var mgr = Scope("principal-manager");
        var m = mgr.ServiceProvider.GetRequiredService<GuestMergeService>();
        var approved = await m.DecideAsync(proposed.GuestMergeCaseId, proposed.Version, approve: true, "checked the booking", default);
        Assert.Equal(GovOutcome.Ok, approved.Outcome);
        var merged = await m.ExecuteAsync(proposed.GuestMergeCaseId, approved.Row!.Version, default);
        Assert.Equal(GovOutcome.Ok, merged.Outcome);

        Assert.Equal("Merged", await fixture.ScalarAsync<string>($"SELECT status FROM guest.guest WHERE guest_id = '{dup.GuestId}'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM guest.guest_contact_point WHERE guest_id = '{keep.GuestId}'"));
        var hits = await InTx(mgr, () => mgr.ServiceProvider.GetRequiredService<GuestProfileService>().SearchAsync("Kara", 10, default));
        Assert.DoesNotContain(hits, h => h.Guest.GuestId == dup.GuestId);

        var split = await m.SplitAsync(proposed.GuestMergeCaseId, merged.Row!.Version, "two different people after all", default);
        Assert.Equal(GovOutcome.Ok, split.Outcome);
        Assert.Equal("Active", await fixture.ScalarAsync<string>($"SELECT status FROM guest.guest WHERE guest_id = '{dup.GuestId}'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM guest.guest_contact_point WHERE guest_id = '{keep.GuestId}'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM guest.guest_contact_point WHERE guest_id = '{dup.GuestId}'"));
    }

    /* -------------------------------- delegation ------------------------------ */

    [RequiresPostgres]
    public async Task A_delegation_gives_the_delegate_a_principal_and_publishes_the_tuple_change_and_revocation()
    {
        await FreshAsync();
        using var s = Scope();
        var parent = await NewGuestAsync(s, "Dora", "Vale");
        var assistant = await NewGuestAsync(s, "Eli", "Vale");
        var d = s.ServiceProvider.GetRequiredService<DelegationService>();

        var bad = await d.GrantAsync(parent.GuestId, new NewDelegation(assistant.GuestId, ["Book", "ReadNotes"], null, null, null,
            "ItineraryOnly", "signed form", DateTimeOffset.UtcNow.AddDays(30)), default);
        Assert.Equal(GovOutcome.Invalid, bad.Outcome);

        var granted = await d.GrantAsync(parent.GuestId, new NewDelegation(assistant.GuestId, ["Book", "ViewItinerary"], null, 50000, "USD",
            "ItineraryOnly", "signed form at desk", DateTimeOffset.UtcNow.AddDays(30)), default);
        Assert.Equal(GovOutcome.Ok, granted.Outcome);

        var delegatePrincipal = await fixture.ScalarAsync<Guid>($"SELECT principal_id FROM guest.guest WHERE guest_id = '{assistant.GuestId}'");
        var payload = await fixture.ScalarAsync<string>(
            $"SELECT payload::text FROM core.event_outbox WHERE event_type = '{EventTypes.DelegationChanged}' AND aggregate_id = '{granted.Row!.DelegatedAuthorityId}' ORDER BY occurred_at LIMIT 1");
        using (var doc = JsonDocument.Parse(payload))
        {
            Assert.Equal(delegatePrincipal, doc.RootElement.GetProperty("delegatePrincipalId").GetGuid());
            Assert.Equal("Active", doc.RootElement.GetProperty("status").GetString());
        }

        var revoked = await d.RevokeAsync(granted.Row.DelegatedAuthorityId, granted.Row.Version, "guest asked", default);
        Assert.Equal(GovOutcome.Ok, revoked.Outcome);
        Assert.Equal(2L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM core.event_outbox WHERE event_type = '{EventTypes.DelegationChanged}' AND aggregate_id = '{granted.Row.DelegatedAuthorityId}'"));
    }

    /* ---------------------------------- consent ------------------------------- */

    [RequiresPostgres]
    public async Task Consent_evidence_is_immutable_and_revocation_is_the_only_change()
    {
        await FreshAsync();
        using var s = Scope();
        var g = await NewGuestAsync(s, "Fay", "Moss");
        var consents = s.ServiceProvider.GetRequiredService<ConsentService>();
        var c = await consents.RecordAsync(g.GuestId, "Marketing", "Email", "mkt-email", 3, "Ticked the box on the desk tablet", null, null, default);
        Assert.Equal(GovOutcome.Ok, c.Outcome);
        Assert.True(await InTx(s, () => consents.HasActiveAsync(g.GuestId, "Marketing", "Email", default)));

        // Even the application role cannot rewrite what the guest agreed to.
        var e = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecAsync($"""
            BEGIN; SET LOCAL ROLE spms_app; SELECT core.begin_scope('{TestIds.Of(T)}', ARRAY['{TestIds.Of(P)}']::uuid[], NULL, 't');
            UPDATE guest.consent_record SET template_version = 4, version = version + 1 WHERE consent_id = '{c.Row!.ConsentId}'; COMMIT;
            """));
        Assert.Equal("42501", e.SqlState);

        var revoked = await consents.RevokeAsync(c.Row!.ConsentId, c.Row.Version, default);
        Assert.Equal(GovOutcome.Ok, revoked.Outcome);
        Assert.False(await InTx(s, () => consents.HasActiveAsync(g.GuestId, "Marketing", "Email", default)));
    }

    /* ---------------------------------- privacy ------------------------------- */

    [RequiresPostgres]
    public async Task A_fulfilled_deletion_request_erases_the_profile_destroys_contact_values_and_redacts_audit_payloads()
    {
        await FreshAsync();
        using var s = Scope();
        var g = await NewGuestAsync(s, "Gil", "Nash", $"{Unique("gil")}@example.com", new DateOnly(1990, 4, 1));
        var privacy = s.ServiceProvider.GetRequiredService<PrivacyService>();
        var req = (await privacy.OpenAsync(g.GuestId, "Deletion", "GDPR", default)).Row!;

        var early = await privacy.TransitionAsync(req.PrivacyRequestId, req.Version, "Fulfilled", null, default);
        Assert.Equal(GovOutcome.Illegal, early.Outcome);
        var verified = await privacy.TransitionAsync(req.PrivacyRequestId, req.Version, "Verified", null, default);
        var done = await privacy.TransitionAsync(req.PrivacyRequestId, verified.Row!.Version, "Fulfilled", null, default);
        Assert.Equal(GovOutcome.Ok, done.Outcome);

        Assert.Equal("Erased|Erased guest||", await fixture.ScalarAsync<string>(
            $"SELECT status || '|' || display_alias || '|' || coalesce(legal_last_name, '') || '|' || coalesce(birth_date::text, '') FROM guest.guest WHERE guest_id = '{g.GuestId}'"));
        Assert.Equal(0, await fixture.ScalarAsync<int>($"SELECT max(octet_length(contact_cipher)) FROM guest.guest_contact_point WHERE guest_id = '{g.GuestId}'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM core.audit_event WHERE entity_type = 'guest' AND entity_id = '{g.GuestId}' AND (after_data IS NOT NULL OR before_data IS NOT NULL)"));
        Assert.True(await fixture.ScalarAsync<long>($"SELECT count(*) FROM core.audit_event WHERE entity_type = 'guest' AND entity_id = '{g.GuestId}'") > 0,
            "the audit rows themselves are kept");
    }

    /* ---------------------------------- intake -------------------------------- */

    private async Task SeedIntakeAsync()
    {
        await fixture.ExecAsync($$"""
            BEGIN; SET LOCAL ROLE spms_owner; SELECT core.begin_scope('{{TestIds.Of(T)}}', ARRAY['{{TestIds.Of(P)}}']::uuid[], NULL, 'test-seed');
            UPDATE catalog.service SET requires_intake = true, version = version + 1 WHERE service_id = '{{TestIds.Of("svc-deep")}}' AND NOT requires_intake;
            INSERT INTO intake.form_definition (form_definition_id, tenant_id, form_code, version_number, title, purpose, schema_json, status, published_at, published_by, effective_from)
            VALUES ('{{TestIds.Of("form-health")}}', '{{TestIds.Of(T)}}', 'health-intake', 1, 'Health', 'HealthIntake',
              '{"fields":[{"key":"pregnant","label":"Pregnant?","type":"boolean","required":true,"summary":true,"review":true},
                          {"key":"allergies","label":"Allergies","type":"text","summary":true},
                          {"key":"medications","label":"Medications","type":"text"},
                          {"key":"accurate","label":"Accurate","type":"boolean","required":true,"mustBeTrue":true}]}',
              'Published', now(), '{{TestIds.Of("principal-test")}}', now())
            ON CONFLICT DO NOTHING;
            COMMIT;
            """);
    }

    [RequiresPostgres]
    public async Task The_api_role_cannot_read_intake_tables_at_all()
    {
        await FreshAsync();
        var e = await Assert.ThrowsAsync<PostgresException>(() => fixture.ScalarAsync<long>($"""
            BEGIN; SET LOCAL ROLE spms_app; SELECT core.begin_scope('{TestIds.Of(T)}', ARRAY['{TestIds.Of(P)}']::uuid[], NULL, 't');
            SELECT count(*) FROM intake.intake_submission;
            """));
        Assert.Equal("42501", e.SqlState);
    }

    [RequiresPostgres]
    public async Task A_guest_submits_intake_the_desk_sees_status_only_and_the_provider_sees_the_summary_then_it_locks()
    {
        await FreshAsync();
        await SeedIntakeAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var s = Scope(clock: clock);
        var sp = s.ServiceProvider;
        var guest = await NewGuestAsync(s, "Hal", "Ives");

        var booked = await InTx(s, () => sp.GetRequiredService<SchedulingService>().CreateAsync(TestIds.Of(T), TestIds.Of(P),
            new SchedulingService.NewBooking(Uuid7.New().ToString(), guest.GuestId.ToString(), "Hal I.", TestIds.Of("svc-deep"),
                clock.UtcNow.AddDays(1), TestIds.Of("prov-lena"), TestIds.Of("room-1"), null, "corr"), null));
        Assert.Equal(SchedulingService.CreateOutcome.Created, booked.Outcome);
        var appointmentId = Guid.Parse(booked.Appointment!.AppointmentId);

        var intake = sp.GetRequiredService<IntakeService>();
        var forms = await InTx(s, () => intake.ForGuestAsync(guest.GuestId, default));
        var form = Assert.Single(forms);
        Assert.Equal("Assigned", form.Status);
        Assert.True(form.Editable);

        var answers = new Dictionary<string, JsonElement>
        {
            ["pregnant"] = JsonDocument.Parse("true").RootElement,
            ["allergies"] = JsonDocument.Parse("\"almond oil\"").RootElement,
            ["medications"] = JsonDocument.Parse("\"secret-med-x\"").RootElement,
        };
        var missing = await intake.SaveAsync(guest.GuestId, form.SubmissionId, form.Version, answers, submit: true, default);
        Assert.Equal(IntakeService.Outcome.Invalid, missing.Outcome);
        Assert.Contains(missing.Violations!, v => v.StartsWith("accurate"));

        answers["accurate"] = JsonDocument.Parse("true").RootElement;
        var submitted = await intake.SaveAsync(guest.GuestId, form.SubmissionId, form.Version, answers, submit: true, default);
        Assert.Equal(IntakeService.Outcome.Ok, submitted.Outcome);
        Assert.Equal("Submitted", submitted.Value!.Status);

        // Ciphertext only: the answers are nowhere in the stored bytes.
        var raw = await fixture.ScalarAsync<byte[]>($"SELECT response_cipher FROM intake.intake_submission WHERE submission_id = '{form.SubmissionId}'");
        Assert.DoesNotContain("secret-med-x", System.Text.Encoding.UTF8.GetString(raw));

        // The desk: status, through the definer function.
        var status = await InTx(s, () => intake.StatusAsync(appointmentId, default));
        Assert.Equal(("Submitted", true), (status!.Status, status.RequiresReview));

        // The provider: the summary fields, never the rest.
        var summary = await InTx(s, () => intake.SummaryAsync(appointmentId, default));
        Assert.Equal(IntakeService.Outcome.Ok, summary.Outcome);
        Assert.Equal(["pregnant", "allergies"], summary.Value!.Items.Select(i => i.Field.Key));
        Assert.DoesNotContain(summary.Value.Items, i => i.Answer.ToString().Contains("secret-med-x"));

        var ack = await intake.AcknowledgeAsync(appointmentId, TestIds.IdOf("prov-lena"), default);
        Assert.Equal(IntakeService.Outcome.Ok, ack.Outcome);
        Assert.Equal("Locked", ack.Value!.Status);
        Assert.NotNull(await fixture.ScalarAsync<DateTime?>($"SELECT intake_acknowledged_at FROM scheduling.appointment WHERE appointment_id = '{appointmentId}'"));

        var after = await intake.SaveAsync(guest.GuestId, form.SubmissionId, submitted.Value.Version, answers, submit: true, default);
        Assert.Equal(IntakeService.Outcome.Locked, after.Outcome);
    }

    [RequiresPostgres]
    public async Task Treatment_notes_are_encrypted_amended_only_by_their_author_and_only_once()
    {
        await FreshAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var s = Scope(clock: clock);
        var sp = s.ServiceProvider;
        var booked = await InTx(s, () => sp.GetRequiredService<SchedulingService>().CreateAsync(TestIds.Of(T), TestIds.Of(P),
            new SchedulingService.NewBooking(Uuid7.New().ToString(), TestIds.Of("guest-1"), "G", TestIds.Of("svc-swedish"),
                clock.UtcNow.AddHours(2), TestIds.Of("prov-lena"), TestIds.Of("room-2"), null, "corr"), null));
        var appointmentId = Guid.Parse(booked.Appointment!.AppointmentId);
        var notes = sp.GetRequiredService<TreatmentNoteService>();
        var lena = TestIds.IdOf("prov-lena");

        var (_, first) = await notes.AddAsync(appointmentId, lena, "Tight left shoulder; worked lightly.", "SOAP", null, default);
        var raw = await fixture.ScalarAsync<byte[]>($"SELECT content_cipher FROM intake.treatment_note WHERE note_id = '{first!.NoteId}'");
        Assert.DoesNotContain("shoulder", System.Text.Encoding.UTF8.GetString(raw));

        Assert.Equal(TreatmentNoteService.Outcome.NotAuthor,
            (await notes.AmendAsync(first.NoteId, TestIds.IdOf("prov-marco"), "x", "y", default)).Item1);
        var (ok, amended) = await notes.AmendAsync(first.NoteId, lena, "Tight RIGHT shoulder; worked lightly.", "wrong side", default);
        Assert.Equal(TreatmentNoteService.Outcome.Ok, ok);
        Assert.Equal(TreatmentNoteService.Outcome.AlreadyAmended,
            (await notes.AmendAsync(first.NoteId, lena, "again", "again", default)).Item1);

        var list = await InTx(s, () => notes.ListAsync(appointmentId, default));
        Assert.Equal(2, list.Count);
        Assert.True(list.Single(n => n.NoteId == first.NoteId).Superseded);
        Assert.Equal(first.NoteId, list.Single(n => n.NoteId == amended!.NoteId).SupersedesNoteId);
    }
}
