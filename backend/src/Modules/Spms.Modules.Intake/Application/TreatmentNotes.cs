using Microsoft.EntityFrameworkCore;
using Spms.Modules.Intake.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Intake.Application;

public sealed record NoteView(Guid NoteId, Guid AppointmentId, Guid ProviderStaffId, string? TemplateCode, string Content,
    DateTimeOffset AuthoredUtc, Guid? SupersedesNoteId, string? AmendmentReason, bool Superseded);

/// <summary>
/// Treatment notes (SEC-008): written by the assigned provider, envelope
/// encrypted, never edited. An amendment is a new note that supersedes the old
/// one, with a reason, and only its author may amend it.
/// </summary>
public sealed class TreatmentNoteService(SpmsDbContext db, IntakeRole role, EnvelopeCipher envelope, IAuditSink audit, IUnitOfWork uow, IClock clock)
{
    public enum Outcome { Ok, NotFound, NotAuthor, AlreadyAmended }

    public async Task<IReadOnlyList<NoteView>> ListAsync(Guid appointmentId, CancellationToken ct)
    {
        List<TreatmentNoteRow> rows;
        await using (await role.EnterAsync(ct))
            rows = await db.Set<TreatmentNoteRow>().AsNoTracking().Where(n => n.AppointmentId == appointmentId)
                .OrderBy(n => n.AuthoredAt).ToListAsync(ct);
        var superseded = rows.Where(r => r.SupersedesNoteId is not null).Select(r => r.SupersedesNoteId!.Value).ToHashSet();
        var views = rows.Select(r => new NoteView(r.NoteId, r.AppointmentId, r.ProviderStaffId, r.TemplateCode,
            envelope.Open(r.ContentCipher, r.KeyVersion, IntakePurposes.Note, r.NoteId), r.AuthoredAt, r.SupersedesNoteId,
            r.AmendmentReason, superseded.Contains(r.NoteId))).ToList();
        await audit.RecordAsync(new AuditEntry("treatment_note.read", "appointment", appointmentId.ToString(),
            Purpose: "treatment", AfterData: new { notes = views.Count }), ct);
        await db.SaveChangesAsync(ct);
        return views;
    }

    public async Task<(Outcome, NoteView?)> AddAsync(Guid appointmentId, Guid providerStaffId, string content, string? templateCode,
        DateTimeOffset? authoredUtc, CancellationToken ct) =>
        await WriteAsync(appointmentId, providerStaffId, content, templateCode, authoredUtc, null, null, ct);

    public async Task<(Outcome, NoteView?)> AmendAsync(Guid noteId, Guid providerStaffId, string content, string reason, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        TreatmentNoteRow? old;
        bool amended;
        await using (await role.EnterAsync(ct))
        {
            old = await db.Set<TreatmentNoteRow>().AsNoTracking().SingleOrDefaultAsync(n => n.NoteId == noteId, ct);
            amended = old is not null && await db.Set<TreatmentNoteRow>().AnyAsync(n => n.SupersedesNoteId == noteId, ct);
        }
        if (old is null) return (Outcome.NotFound, null);
        if (old.ProviderStaffId != providerStaffId) return (Outcome.NotAuthor, null);
        if (amended) return (Outcome.AlreadyAmended, null);
        var written = await WriteAsync(old.AppointmentId, providerStaffId, content, old.TemplateCode, null, noteId, reason, ct);
        await tx.CommitAsync(ct);
        return written;
    }

    public async Task<Guid?> AppointmentOfAsync(Guid noteId, CancellationToken ct)
    {
        await using (await role.EnterAsync(ct))
            return await db.Set<TreatmentNoteRow>().AsNoTracking().Where(n => n.NoteId == noteId).Select(n => (Guid?)n.AppointmentId).SingleOrDefaultAsync(ct);
    }

    private async Task<(Outcome, NoteView?)> WriteAsync(Guid appointmentId, Guid staffId, string content, string? templateCode,
        DateTimeOffset? authoredUtc, Guid? supersedes, string? reason, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var id = Uuid7.New();
        var sealedContent = envelope.Seal(content, IntakePurposes.Note, id);
        var now = clock.UtcNow;
        var row = new TreatmentNoteRow
        {
            NoteId = id, PropertyId = db.Scope.RequireProperty(), AppointmentId = appointmentId, ProviderStaffId = staffId,
            TemplateCode = templateCode, ContentCipher = sealedContent.Cipher, KeyVersion = sealedContent.KeyVersion,
            // Captured offline on the tablet, the note keeps the time it was written, never later than now.
            AuthoredAt = authoredUtc is { } a && a <= now ? a.ToUniversalTime() : now,
            SupersedesNoteId = supersedes, AmendmentReason = reason,
        };
        await using (await role.EnterAsync(ct))
        {
            db.Add(row);
            await db.SaveChangesAsync(ct);
        }
        db.ChangeTracker.Clear();
        await audit.RecordAsync(new AuditEntry(supersedes is null ? "treatment_note.write" : "treatment_note.amend", "treatment_note",
            id.ToString(), Purpose: "treatment", ReasonText: reason, AfterData: new { appointmentId, supersedes }), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (Outcome.Ok, new NoteView(id, appointmentId, staffId, templateCode, content, row.AuthoredAt, supersedes, reason, false));
    }
}

/// <summary>Versioned forms: a draft is edited, a published version is frozen and answered, the previous one retires.</summary>
public sealed class FormService(SpmsDbContext db, IntakeRole role, IAuditSink audit, IUnitOfWork uow, IClock clock)
{
    public async Task<List<FormDefinitionRow>> ListAsync(bool publishedOnly, CancellationToken ct)
    {
        await using (await role.EnterAsync(ct))
        {
            var q = db.Set<FormDefinitionRow>().AsNoTracking();
            if (publishedOnly) q = q.Where(f => f.Status == FormDefinitionStatuses.Published);
            return await q.OrderBy(f => f.FormCode).ThenByDescending(f => f.VersionNumber).ToListAsync(ct);
        }
    }

    public async Task<(bool Ok, FormDefinitionRow? Row, string? Problem)> DraftAsync(string code, string title, string purpose, FormSchema schema, CancellationToken ct)
    {
        if (schema.Problem() is { } p) return (false, null, p);
        await using var tx = await uow.BeginAsync(ct);
        FormDefinitionRow row;
        await using (await role.EnterAsync(ct))
        {
            var next = (await db.Set<FormDefinitionRow>().Where(f => f.FormCode == code).MaxAsync(f => (int?)f.VersionNumber, ct) ?? 0) + 1;
            row = new FormDefinitionRow
            {
                FormCode = code, VersionNumber = next, Title = title, Purpose = purpose, SchemaJson = schema.ToJson(),
                Status = FormDefinitionStatuses.Draft, EffectiveFrom = clock.UtcNow,
            };
            db.Add(row);
            await db.SaveChangesAsync(ct);
        }
        db.ChangeTracker.Clear();
        await audit.RecordAsync(new AuditEntry("intake.form.draft", "form_definition", row.FormDefinitionId.ToString(), row.Version,
            AfterData: new { code, version = row.VersionNumber }, TenantWide: true), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (true, row, null);
    }

    public async Task<(bool Ok, FormDefinitionRow? Row, string? Problem)> PublishAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        FormDefinitionRow? row;
        await using (await role.EnterAsync(ct))
        {
            row = await db.Set<FormDefinitionRow>().SingleOrDefaultAsync(f => f.FormDefinitionId == id, ct);
            if (row is null) return (false, null, null);
            if (row.Status != FormDefinitionStatuses.Draft) { db.ChangeTracker.Clear(); return (false, row, $"A {row.Status} form cannot be published."); }
            var now = clock.UtcNow;
            var previous = await db.Set<FormDefinitionRow>()
                .Where(f => f.FormCode == row.FormCode && f.Status == FormDefinitionStatuses.Published).ToListAsync(ct);
            foreach (var p in previous) { p.Status = FormDefinitionStatuses.Retired; p.EffectiveTo = now; }
            row.Status = FormDefinitionStatuses.Published;
            row.PublishedAt = now;
            row.PublishedBy = db.Scope.PrincipalId;
            row.EffectiveFrom = now;
            await db.SaveChangesAsync(ct);
        }
        db.ChangeTracker.Clear();
        await audit.RecordAsync(new AuditEntry("intake.form.publish", "form_definition", id.ToString(), row.Version,
            ToStatus: FormDefinitionStatuses.Published, AfterData: new { code = row.FormCode, version = row.VersionNumber }, TenantWide: true), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (true, row, null);
    }
}

/// <summary>Intake's part of a privacy request. Answers are exported to the guest; erasure keeps health records under their retention rule.</summary>
public sealed class IntakeGuestData(IntakeService intake) : IGuestDataContributor
{
    public string Section => "intake";
    public async Task<object?> ExportAsync(Guid guestId, CancellationToken ct) => await intake.ExportAsync(guestId, ct);
    public Task<int> EraseAsync(Guid guestId, CancellationToken ct) => Task.FromResult(0);
}
