using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Intake.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Intake.Application;

public static class IntakePurposes
{
    public const string Response = "intake.response";
    public const string Summary = "intake.summary";
    public const string Note = "intake.treatment_note";
    public const string DefaultForm = "health-intake";
}

public sealed record IntakeForGuest(
    Guid SubmissionId, Guid? AppointmentId, string ServiceName, DateTimeOffset? AppointmentStartUtc, string FormTitle,
    FormSchema Schema, string Status, Dictionary<string, JsonElement>? Answers, DateTimeOffset? SubmittedUtc, int Version, bool Editable);

public sealed record IntakeSummary(
    Guid SubmissionId, string FormTitle, string Status, bool RequiresReview, IReadOnlyList<(FormField Field, JsonElement Answer)> Items,
    DateTimeOffset? SubmittedUtc, DateTimeOffset? AcknowledgedUtc, Guid? AcknowledgedByStaffId);

public sealed record IntakeStatus(string Status, bool RequiresReview);

/// <summary>
/// Intake (SEC-008, DEC-004). The guest completes their own form before the
/// appointment; the full answers and the minimum-necessary summary are each
/// envelope-encrypted and stored only as ciphertext. The desk sees status,
/// the assigned provider sees the summary and acknowledges it, and nobody
/// else sees either. Every statement against the intake tables runs as
/// spms_intake (<see cref="IntakeRole"/>).
/// </summary>
public sealed class IntakeService(SpmsDbContext db, IntakeRole role, EnvelopeCipher envelope, IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Editable = [IntakeSubmissionStatuses.Assigned, IntakeSubmissionStatuses.Draft];
    private static readonly string[] Answered = [IntakeSubmissionStatuses.Submitted, IntakeSubmissionStatuses.Locked, IntakeSubmissionStatuses.Reviewed];

    public enum Outcome { Ok, NotFound, Locked, Invalid, StaleVersion, NotAnswered }

    public sealed record Result<T>(Outcome Outcome, T? Value = default, IReadOnlyList<string>? Violations = null, string? Detail = null);

    // A class with init members, not a positional record: EF translates a Where after this projection.
    private sealed class Booking
    {
        public Guid AppointmentId { get; init; }
        public Guid PropertyId { get; init; }
        public Guid GuestId { get; init; }
        public required string Status { get; init; }
        public DateTimeOffset StartAt { get; init; }
        public required string ServiceName { get; init; }
        public bool RequiresIntake { get; init; }
    }

    private IQueryable<Booking> Bookings() =>
        from a in db.Set<AppointmentRow>().AsNoTracking()
        join s in db.Set<ServiceRow>() on a.ServiceId equals s.ServiceId
        select new Booking
        {
            AppointmentId = a.AppointmentId, PropertyId = a.PropertyId, GuestId = a.GuestId, Status = a.Status,
            StartAt = a.StartAt, ServiceName = s.Name, RequiresIntake = s.RequiresIntake,
        };

    /* ------------------------------ assignment ------------------------------ */

    /// <summary>
    /// A booking whose service needs intake gets its form the first time
    /// anyone asks — the guest opening their page, the desk's status check —
    /// against the latest published version of the health intake form.
    /// </summary>
    private async Task<List<IntakeSubmissionRow>> EnsureAssignedAsync(IReadOnlyList<Booking> bookings, CancellationToken ct)
    {
        var needing = bookings.Where(b => b.RequiresIntake && b.Status is AppointmentStatuses.Held or AppointmentStatuses.Confirmed or AppointmentStatuses.CheckedIn).ToList();
        var ids = bookings.Select(b => b.AppointmentId).ToList();
        await using var _ = await role.EnterAsync(ct);
        var existing = await db.Set<IntakeSubmissionRow>()
            .Where(s => s.AppointmentId != null && ids.Contains(s.AppointmentId.Value) && s.Status != IntakeSubmissionStatuses.Superseded)
            .ToListAsync(ct);
        var missing = needing.Where(b => existing.All(e => e.AppointmentId != b.AppointmentId)).ToList();
        if (missing.Count > 0)
        {
            var form = await db.Set<FormDefinitionRow>().AsNoTracking()
                .Where(f => f.FormCode == IntakePurposes.DefaultForm && f.Status == FormDefinitionStatuses.Published)
                .OrderByDescending(f => f.VersionNumber).FirstOrDefaultAsync(ct);
            if (form is not null)
            {
                foreach (var b in missing)
                {
                    var row = new IntakeSubmissionRow
                    {
                        SubmissionId = Uuid7.New(), PropertyId = b.PropertyId, FormDefinitionId = form.FormDefinitionId,
                        AppointmentId = b.AppointmentId, GuestId = b.GuestId, DueAt = b.StartAt, Status = IntakeSubmissionStatuses.Assigned,
                    };
                    db.Add(row);
                    existing.Add(row);
                }
                await db.SaveChangesAsync(ct);
            }
        }
        return existing;
    }

    /* --------------------------------- guest -------------------------------- */

    /// <summary>The signed-in guest's forms for their upcoming bookings, with their own answers so far.</summary>
    public async Task<IReadOnlyList<IntakeForGuest>> ForGuestAsync(Guid guestId, CancellationToken ct)
    {
        var since = clock.UtcNow.AddHours(-12);
        var bookings = await Bookings().Where(b => b.GuestId == guestId && b.StartAt >= since).OrderBy(b => b.StartAt).ToListAsync(ct);
        var subs = await EnsureAssignedAsync(bookings, ct);
        var result = new List<IntakeForGuest>();
        await using (await role.EnterAsync(ct))
        {
            var formIds = subs.Select(s => s.FormDefinitionId).Distinct().ToList();
            var forms = await db.Set<FormDefinitionRow>().AsNoTracking().Where(f => formIds.Contains(f.FormDefinitionId)).ToDictionaryAsync(f => f.FormDefinitionId, ct);
            foreach (var s in subs.OrderBy(s => s.DueAt))
            {
                var b = bookings.FirstOrDefault(x => x.AppointmentId == s.AppointmentId);
                var form = forms[s.FormDefinitionId];
                result.Add(new IntakeForGuest(s.SubmissionId, s.AppointmentId, b?.ServiceName ?? "", b?.StartAt, form.Title,
                    FormSchema.Parse(form.SchemaJson), s.Status, Answers(s), s.SubmittedAt, s.Version,
                    Editable.Contains(s.Status) && b?.Status is AppointmentStatuses.Held or AppointmentStatuses.Confirmed));
            }
        }
        db.ChangeTracker.Clear();
        await audit.RecordAsync(new AuditEntry("intake.guest.read", "guest", guestId.ToString(), Purpose: "intake.self",
            AfterData: new { forms = result.Count }, TenantWide: true), ct);
        return result;
    }

    /// <summary>
    /// The guest saves (draft) or submits their answers. Refused once the form
    /// is locked (the provider acknowledged it) or the guest has arrived:
    /// intake:update:own:before_lock.
    /// </summary>
    public async Task<Result<IntakeForGuest>> SaveAsync(Guid guestId, Guid submissionId, int expectedVersion,
        Dictionary<string, JsonElement> answers, bool submit, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        IntakeSubmissionRow? s;
        FormDefinitionRow? form;
        await using (await role.EnterAsync(ct))
        {
            s = await db.Set<IntakeSubmissionRow>().AsNoTracking().SingleOrDefaultAsync(x => x.SubmissionId == submissionId && x.GuestId == guestId, ct);
            form = s is null ? null : await db.Set<FormDefinitionRow>().AsNoTracking().SingleAsync(f => f.FormDefinitionId == s.FormDefinitionId, ct);
        }
        if (s is null || form is null) return new(Outcome.NotFound);
        var booking = s.AppointmentId is { } aid ? await Bookings().SingleOrDefaultAsync(b => b.AppointmentId == aid, ct) : null;
        if (!Editable.Contains(s.Status) || booking?.Status is not (null or AppointmentStatuses.Held or AppointmentStatuses.Confirmed))
            return new(Outcome.Locked, Detail: "This form can no longer be changed. Tell the front desk if something is different.");
        if (s.Version != expectedVersion) return new(Outcome.StaleVersion);

        var schema = FormSchema.Parse(form.SchemaJson);
        var violations = schema.Violations(answers, submit);
        if (violations.Count > 0) return new(Outcome.Invalid, Violations: violations);

        var now = clock.UtcNow;
        var response = envelope.Seal(JsonSerializer.Serialize(answers, Json), IntakePurposes.Response, submissionId);
        var summary = envelope.Seal(JsonSerializer.Serialize(schema.Summary(answers), Json), IntakePurposes.Summary, submissionId);
        var review = schema.NeedsReview(answers);
        var status = submit ? IntakeSubmissionStatuses.Submitted : IntakeSubmissionStatuses.Draft;

        int n;
        await using (await role.EnterAsync(ct))
        {
            n = await db.Set<IntakeSubmissionRow>()
                .Where(x => x.SubmissionId == submissionId && x.Version == expectedVersion && Editable.Contains(x.Status))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.ResponseCipher, response.Cipher)
                    .SetProperty(x => x.SummaryCipher, summary.Cipher)
                    .SetProperty(x => x.KeyVersion, response.KeyVersion)
                    .SetProperty(x => x.RequiresReview, review)
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.SubmittedAt, submit ? now : (DateTimeOffset?)null)
                    .SetProperty(x => x.SubmittedByGuestId, guestId)
                    .SetProperty(x => x.Version, x => x.Version + 1)
                    .SetProperty(x => x.UpdatedBy, db.Scope.PrincipalId), ct);
        }
        if (n == 0) return new(Outcome.StaleVersion);

        // Never the answers: which form, and that it moved.
        await audit.RecordAsync(new AuditEntry(submit ? "intake.submit" : "intake.save", "intake_submission", submissionId.ToString(),
            expectedVersion + 1, Purpose: "intake.self", FromStatus: s.Status, ToStatus: status,
            AfterData: new { formCode = form.FormCode, formVersion = form.VersionNumber, requiresReview = review }, PropertyId: s.PropertyId), ct);
        if (submit)
            outbox.Enqueue(new OutboxEvent(EventTypes.IntakeChanged, "intake_submission", submissionId, expectedVersion + 1,
                new { submissionId, appointmentId = s.AppointmentId, status, requiresReview = review }, PropertyId: s.PropertyId));
        await db.SaveChangesAsync(ct);

        var view = (await ForGuestAsync(guestId, ct)).Single(x => x.SubmissionId == submissionId);
        await tx.CommitAsync(ct);
        return new(Outcome.Ok, view);
    }

    /* -------------------------------- provider ------------------------------- */

    /// <summary>
    /// The assigned provider's minimum-necessary summary (DEC-004). The caller
    /// has already been checked for can_read_intake_summary on the appointment;
    /// the read is audited as a sensitive read with its purpose.
    /// </summary>
    public async Task<Result<IntakeSummary>> SummaryAsync(Guid appointmentId, CancellationToken ct)
    {
        var booking = await Bookings().SingleOrDefaultAsync(b => b.AppointmentId == appointmentId, ct);
        if (booking is null) return new(Outcome.NotFound);
        var subs = await EnsureAssignedAsync([booking], ct);
        var s = subs.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (s is null) return new(Outcome.NotFound, Detail: "This service does not use an intake form.");

        IntakeSummary summary;
        await using (await role.EnterAsync(ct))
        {
            var form = await db.Set<FormDefinitionRow>().AsNoTracking().SingleAsync(f => f.FormDefinitionId == s.FormDefinitionId, ct);
            if (!Answered.Contains(s.Status) || s.SummaryCipher is null)
                return new(Outcome.NotAnswered, Value: new IntakeSummary(s.SubmissionId, form.Title, s.Status, s.RequiresReview, [], null, null, null));
            var schema = FormSchema.Parse(form.SchemaJson);
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                envelope.Open(s.SummaryCipher, s.KeyVersion!, IntakePurposes.Summary, s.SubmissionId), Json) ?? [];
            summary = new IntakeSummary(s.SubmissionId, form.Title, s.Status, s.RequiresReview,
                schema.Fields.Where(f => f.Summary && values.ContainsKey(f.Key)).Select(f => (f, values[f.Key])).ToList(),
                s.SubmittedAt, s.AcknowledgedAt, s.AcknowledgedByStaffId);
        }
        db.ChangeTracker.Clear();
        await audit.RecordAsync(new AuditEntry("intake.summary.read", "intake_submission", s.SubmissionId.ToString(), s.Version,
            Purpose: "treatment", AfterData: new { appointmentId }, PropertyId: s.PropertyId), ct);
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, summary);
    }

    /// <summary>The provider has read the summary: the form locks and the appointment records the acknowledgement.</summary>
    public async Task<Result<IntakeSummary>> AcknowledgeAsync(Guid appointmentId, Guid staffId, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var now = clock.UtcNow;
        IntakeSubmissionRow? s;
        await using (await role.EnterAsync(ct))
        {
            s = await db.Set<IntakeSubmissionRow>()
                .Where(x => x.AppointmentId == appointmentId && x.Status != IntakeSubmissionStatuses.Superseded)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (s is null) return new(Outcome.NotFound);
            if (!Answered.Contains(s.Status)) { db.ChangeTracker.Clear(); return new(Outcome.NotAnswered, Detail: "The guest has not submitted the form yet."); }
            if (s.AcknowledgedAt is null)
            {
                s.AcknowledgedByStaffId = staffId;
                s.AcknowledgedAt = now;
                if (s.Status == IntakeSubmissionStatuses.Submitted) { s.Status = IntakeSubmissionStatuses.Locked; s.LockedAt = now; }
                await db.SaveChangesAsync(ct);
            }
        }
        var submissionId = s.SubmissionId;
        var version = s.Version;
        var propertyId = s.PropertyId;
        db.ChangeTracker.Clear();

        await db.Set<AppointmentRow>().Where(a => a.AppointmentId == appointmentId && a.IntakeAcknowledgedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.IntakeAcknowledgedAt, now).SetProperty(a => a.Version, a => a.Version + 1)
                .SetProperty(a => a.UpdatedBy, db.Scope.PrincipalId), ct);
        await audit.RecordAsync(new AuditEntry("intake.acknowledge", "intake_submission", submissionId.ToString(), version,
            Purpose: "treatment", ToStatus: IntakeSubmissionStatuses.Locked, AfterData: new { appointmentId, staffId }, PropertyId: propertyId), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.IntakeChanged, "intake_submission", submissionId, version,
            new { submissionId, appointmentId, status = IntakeSubmissionStatuses.Locked, acknowledged = true }, PropertyId: propertyId));
        await db.SaveChangesAsync(ct);
        var summary = await SummaryAsync(appointmentId, ct);
        await tx.CommitAsync(ct);
        return summary;
    }

    /* ---------------------------------- desk --------------------------------- */

    /// <summary>Status only, for the desk (can_read_intake_status). Assigns the form if it is due.</summary>
    public async Task<IntakeStatus?> StatusAsync(Guid appointmentId, CancellationToken ct)
    {
        var booking = await Bookings().SingleOrDefaultAsync(b => b.AppointmentId == appointmentId, ct);
        if (booking is null) return null;
        if (!booking.RequiresIntake) return new IntakeStatus("NotRequired", false);
        var subs = await EnsureAssignedAsync([booking], ct);
        db.ChangeTracker.Clear();
        var s = subs.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        return s is null ? new IntakeStatus("NoForm", false) : new IntakeStatus(s.Status, s.RequiresReview);
    }

    private Dictionary<string, JsonElement>? Answers(IntakeSubmissionRow s) =>
        s.ResponseCipher is null ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(envelope.Open(s.ResponseCipher, s.KeyVersion!, IntakePurposes.Response, s.SubmissionId), Json);

    /* ------------------------------- guest data ------------------------------ */

    /// <summary>The guest's own intake for an access request: forms, dates and their answers.</summary>
    public async Task<object> ExportAsync(Guid guestId, CancellationToken ct)
    {
        await using (await role.EnterAsync(ct))
        {
            var subs = await db.Set<IntakeSubmissionRow>().AsNoTracking().Where(s => s.GuestId == guestId).ToListAsync(ct);
            var formIds = subs.Select(s => s.FormDefinitionId).Distinct().ToList();
            var forms = await db.Set<FormDefinitionRow>().AsNoTracking().Where(f => formIds.Contains(f.FormDefinitionId)).ToDictionaryAsync(f => f.FormDefinitionId, ct);
            return subs.Select(s => new
            {
                form = forms[s.FormDefinitionId].Title, s.Status, s.SubmittedAt, s.AppointmentId, answers = Answers(s),
            }).ToList();
        }
    }
}
