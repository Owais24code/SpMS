using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Intake.Application;
using Spms.Modules.Intake.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Scheduling.Endpoints;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Intake.Endpoints;

public sealed record IntakeFieldDto(string Key, string Label, string Type, bool Required, bool MustBeTrue, IReadOnlyList<string>? Options, int? MaxLength);

public sealed record GuestIntakeDto(string SubmissionId, string? AppointmentId, string ServiceName, string? AppointmentStartUtc, string FormTitle,
    IReadOnlyList<IntakeFieldDto> Fields, string Status, Dictionary<string, JsonElement>? Answers, string? SubmittedUtc, bool Editable, int RowVersion, string ETag)
{
    public static GuestIntakeDto From(IntakeForGuest i) => new(i.SubmissionId.ToString(), i.AppointmentId?.ToString(), i.ServiceName,
        i.AppointmentStartUtc?.ToUniversalTime().ToString("O"), i.FormTitle,
        // The guest sees their own form; which answers the provider will see is not the guest's concern here.
        i.Schema.Fields.Select(f => new IntakeFieldDto(f.Key, f.Label, f.Type, f.Required, f.MustBeTrue, f.Options, f.MaxLength)).ToList(),
        i.Status, i.Answers, i.SubmittedUtc?.ToUniversalTime().ToString("O"), i.Editable, i.Version, $"\"{i.Version}\"");
}

public sealed record SummaryItemDto(string Key, string Label, JsonElement Answer, bool Review);

public sealed record IntakeSummaryDto(string SubmissionId, string FormTitle, string Status, bool RequiresReview, IReadOnlyList<SummaryItemDto> Items,
    string? SubmittedUtc, string? AcknowledgedUtc)
{
    public static IntakeSummaryDto From(IntakeSummary s) => new(s.SubmissionId.ToString(), s.FormTitle, s.Status, s.RequiresReview,
        s.Items.Select(i => new SummaryItemDto(i.Field.Key, i.Field.Label, i.Answer, i.Field.Review)).ToList(),
        s.SubmittedUtc?.ToUniversalTime().ToString("O"), s.AcknowledgedUtc?.ToUniversalTime().ToString("O"));
}

public sealed record NoteDto(string NoteId, string AppointmentId, string ProviderStaffId, string? TemplateCode, string Content,
    string AuthoredUtc, string? SupersedesNoteId, string? AmendmentReason, bool Superseded)
{
    public static NoteDto From(NoteView n) => new(n.NoteId.ToString(), n.AppointmentId.ToString(), n.ProviderStaffId.ToString(), n.TemplateCode,
        n.Content, n.AuthoredUtc.ToUniversalTime().ToString("O"), n.SupersedesNoteId?.ToString(), n.AmendmentReason, n.Superseded);
}

public sealed record FormDto(string FormDefinitionId, string FormCode, int VersionNumber, string Title, string Purpose, string Status,
    JsonElement Schema, string? PublishedUtc)
{
    public static FormDto From(FormDefinitionRow f) => new(f.FormDefinitionId.ToString(), f.FormCode, f.VersionNumber, f.Title, f.Purpose,
        f.Status, JsonDocument.Parse(f.SchemaJson).RootElement.Clone(), f.PublishedAt?.ToUniversalTime().ToString("O"));
}

public sealed record SaveIntakeRequest(Dictionary<string, JsonElement>? Answers, bool? Submit);
public sealed record WriteNoteRequest(string? Content, string? TemplateCode, string? AuthoredUtc);
public sealed record AmendNoteRequest(string? Content, string? Reason);
public sealed record DraftFormRequest(string? FormCode, string? Title, string? Purpose, FormSchema? Schema);

/// <summary>
/// Intake and treatment notes (SEC-008).
///
///   GET  /guest/intake                         the signed-in guest's forms and their own answers
///   PUT  /guest/intake/{submissionId}          save a draft or submit (If-Match)
///   GET  /appointments/{id}/intake/status      the desk: status only
///   GET  /appointments/{id}/intake             the assigned provider: the minimum-necessary summary
///   POST /appointments/{id}/intake/acknowledge the assigned provider has read it; the form locks
///   GET  /appointments/{id}/notes              treatment notes (assigned provider)
///   POST /appointments/{id}/notes              write a note
///   POST /treatment-notes/{id}/amend           the author supersedes their note, with a reason
///   GET/POST /intake/forms, POST /intake/forms/{id}/publish
/// </summary>
public static class IntakeEndpoints
{
    public static IEndpointRouteBuilder MapIntake(this IEndpointRouteBuilder app)
    {
        app.MapGet("/guest/intake", GuestForms);
        app.MapPut("/guest/intake/{submissionId:guid}", GuestSave);
        app.MapGet("/appointments/{id:guid}/intake/status", Status);
        app.MapGet("/appointments/{id:guid}/intake", Summary);
        app.MapPost("/appointments/{id:guid}/intake/acknowledge", Acknowledge);
        app.MapGet("/appointments/{id:guid}/notes", ListNotes);
        app.MapPost("/appointments/{id:guid}/notes", WriteNote);
        app.MapPost("/treatment-notes/{id:guid}/amend", AmendNote);
        app.MapGet("/intake/forms", Forms);
        app.MapPost("/intake/forms", DraftForm);
        app.MapPost("/intake/forms/{id:guid}/publish", PublishForm);
        return app;
    }

    private static IResult Invalid(RequestContext ctx, string detail, object? violations = null) =>
        Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, detail,
            extensions: violations is null ? null : Problem.Ext("field_violations", violations));

    /* --------------------------------- guest -------------------------------- */

    private static async Task<IResult> GuestForms(HttpContext http, IntakeService intake, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestSelf) is { } denied) return denied;
        if (!ctx.IsGuest) return Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "A guest session is required.");
        var forms = await intake.ForGuestAsync(ctx.GuestId!.Value, ct);
        return Results.Json(forms.Select(GuestIntakeDto.From).ToList(), Json.Options);
    }

    private static async Task<IResult> GuestSave(HttpContext http, IntakeService intake, Guid submissionId, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.GuestSelf) is { } denied) return denied;
        if (!ctx.IsGuest) return Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "A guest session is required.");
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<SaveIntakeRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;

        var r = await intake.SaveAsync(ctx.GuestId!.Value, submissionId, version, req!.Answers ?? [], req.Submit ?? false, ct);
        return r.Outcome switch
        {
            IntakeService.Outcome.NotFound => Problem.From(ApiError.NotFound, ctx.CorrelationId),
            IntakeService.Outcome.Locked => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
            IntakeService.Outcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, "The form changed since you opened it. Reload it."),
            IntakeService.Outcome.Invalid => Invalid(ctx, "Some answers need attention.", r.Violations!.Select(v => new { field = v.Split(':')[0], rule = v }).ToList()),
            _ => Results.Json(GuestIntakeDto.From(r.Value!), Json.Options),
        };
    }

    /* ----------------------------- desk / provider ---------------------------- */

    private static async Task<(Appointment? A, IResult? Fail)> LoadAsync(RequestContext ctx, IAppointmentRepository repo, Guid id, CancellationToken ct)
    {
        var a = await repo.GetAsync(ctx.Tenant(), ctx.Property(), id.ToString(), ct);
        return a is null ? (null, Problem.From(ApiError.NotFound, ctx.CorrelationId)) : (a, null);
    }

    private static async Task<IResult> Status(HttpContext http, IntakeService intake, IAppointmentRepository repo, AppointmentAccess access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        var (a, fail) = await LoadAsync(ctx, repo, id, ct);
        if (fail is not null) return fail;
        if (await access.AppointmentAsync(ctx, a!, "can_read_intake_status", ct) is { } refused) return refused;
        var s = await intake.StatusAsync(id, ct);
        return s is null ? Problem.From(ApiError.NotFound, ctx.CorrelationId)
            : Results.Json(new { appointmentId = id, status = s.Status, requiresReview = s.RequiresReview }, Json.Options);
    }

    private static async Task<IResult> Summary(HttpContext http, IntakeService intake, IAppointmentRepository repo, AppointmentAccess access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.HealthRestricted) is { } denied) return denied;
        var (a, fail) = await LoadAsync(ctx, repo, id, ct);
        if (fail is not null) return fail;
        if (await access.AppointmentAsync(ctx, a!, "can_read_intake_summary", ct, strong: true) is { } refused) return refused;
        var r = await intake.SummaryAsync(id, ct);
        return r.Outcome switch
        {
            IntakeService.Outcome.Ok => Results.Json(IntakeSummaryDto.From(r.Value!), Json.Options),
            IntakeService.Outcome.NotAnswered => Results.Json(IntakeSummaryDto.From(r.Value!), Json.Options),
            _ => Problem.From(ApiError.NotFound, ctx.CorrelationId, r.Detail),
        };
    }

    private static async Task<Guid?> CallerStaffAsync(SpmsDbContext db, RequestContext ctx, CancellationToken ct) =>
        await db.Set<StaffRow>().AsNoTracking().Where(s => s.PrincipalId == ctx.PrincipalId).Select(s => (Guid?)s.StaffId).FirstOrDefaultAsync(ct);

    private static async Task<IResult> Acknowledge(HttpContext http, IntakeService intake, IAppointmentRepository repo, AppointmentAccess access,
        SpmsDbContext db, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.HealthRestricted) is { } denied) return denied;
        var (a, fail) = await LoadAsync(ctx, repo, id, ct);
        if (fail is not null) return fail;
        if (await access.AppointmentAsync(ctx, a!, "can_read_intake_summary", ct, strong: true) is { } refused) return refused;
        if (await CallerStaffAsync(db, ctx, ct) is not { } staff)
            return Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "Only a member of staff can acknowledge intake.");
        var r = await intake.AcknowledgeAsync(id, staff, ct);
        return r.Outcome switch
        {
            IntakeService.Outcome.Ok => Results.Json(IntakeSummaryDto.From(r.Value!), Json.Options),
            IntakeService.Outcome.NotAnswered => Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, r.Detail),
            _ => Problem.From(ApiError.NotFound, ctx.CorrelationId, r.Detail),
        };
    }

    private static async Task<IResult> ListNotes(HttpContext http, TreatmentNoteService notes, IAppointmentRepository repo, AppointmentAccess access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.HealthRestricted) is { } denied) return denied;
        var (a, fail) = await LoadAsync(ctx, repo, id, ct);
        if (fail is not null) return fail;
        if (await access.AppointmentAsync(ctx, a!, "can_write_treatment_note", ct, strong: true) is { } refused) return refused;
        return Results.Json((await notes.ListAsync(id, ct)).Select(NoteDto.From).ToList(), Json.Options);
    }

    private static async Task<IResult> WriteNote(HttpContext http, TreatmentNoteService notes, IAppointmentRepository repo, AppointmentAccess access,
        SpmsDbContext db, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.HealthRestricted) is { } denied) return denied;
        var (body, bodyFail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return bodyFail!;
        if (!Guard.TryParse<WriteNoteRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (string.IsNullOrWhiteSpace(req!.Content) || req.Content.Length > 8000) return Invalid(ctx, "content is required, up to 8000 characters.");
        var (a, fail) = await LoadAsync(ctx, repo, id, ct);
        if (fail is not null) return fail;
        if (await access.AppointmentAsync(ctx, a!, "can_write_treatment_note", ct, strong: true) is { } refused) return refused;
        if (await CallerStaffAsync(db, ctx, ct) is not { } staff)
            return Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "Only a member of staff can write a treatment note.");
        DateTimeOffset? authored = Guard.TryParseInstant(req.AuthoredUtc, out var t) ? t : null;
        var (_, note) = await notes.AddAsync(id, staff, req.Content, req.TemplateCode, authored, ct);
        return Results.Json(NoteDto.From(note!), Json.Options, statusCode: 201);
    }

    private static async Task<IResult> AmendNote(HttpContext http, TreatmentNoteService notes, IAppointmentRepository repo, AppointmentAccess access,
        SpmsDbContext db, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.HealthRestricted) is { } denied) return denied;
        var (body, bodyFail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return bodyFail!;
        if (!Guard.TryParse<AmendNoteRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (string.IsNullOrWhiteSpace(req!.Content) || req.Content.Length > 8000) return Invalid(ctx, "content is required, up to 8000 characters.");
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500) return Invalid(ctx, "An amendment needs a reason, up to 500 characters.");
        if (await notes.AppointmentOfAsync(id, ct) is not { } appointmentId) return Problem.From(ApiError.NotFound, ctx.CorrelationId);
        var (a, fail) = await LoadAsync(ctx, repo, appointmentId, ct);
        if (fail is not null) return fail;
        if (await access.AppointmentAsync(ctx, a!, "can_write_treatment_note", ct, strong: true) is { } refused) return refused;
        if (await CallerStaffAsync(db, ctx, ct) is not { } staff)
            return Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "Only a member of staff can amend a treatment note.");
        var (outcome, note) = await notes.AmendAsync(id, staff, req.Content, req.Reason, ct);
        return outcome switch
        {
            TreatmentNoteService.Outcome.Ok => Results.Json(NoteDto.From(note!), Json.Options, statusCode: 201),
            TreatmentNoteService.Outcome.NotAuthor => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "Only the author may amend a note."),
            TreatmentNoteService.Outcome.AlreadyAmended => Problem.From(ApiError.HardConflict, ctx.CorrelationId, "This note was already amended; amend the latest one."),
            _ => Problem.From(ApiError.NotFound, ctx.CorrelationId),
        };
    }

    /* ---------------------------------- forms -------------------------------- */

    private static async Task<IResult> Forms(HttpContext http, FormService forms, bool? all, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        var everything = all == true && ctx.Scopes.Contains(SpaScopes.Admin);
        return Results.Json((await forms.ListAsync(!everything, ct)).Select(FormDto.From).ToList(), Json.Options);
    }

    private static async Task<IResult> DraftForm(HttpContext http, FormService forms, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Admin) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<DraftFormRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (string.IsNullOrWhiteSpace(req!.FormCode) || string.IsNullOrWhiteSpace(req.Title) || req.Schema is null)
            return Invalid(ctx, "formCode, title and schema are required.");
        if (req.Purpose is not ("HealthIntake" or "Consent" or "Waiver" or "Feedback")) return Invalid(ctx, "purpose must be HealthIntake, Consent, Waiver or Feedback.");
        var (ok, row, problem) = await forms.DraftAsync(req.FormCode.Trim(), req.Title.Trim(), req.Purpose, req.Schema, ct);
        return ok ? Results.Json(FormDto.From(row!), Json.Options, statusCode: 201) : Invalid(ctx, problem!);
    }

    private static async Task<IResult> PublishForm(HttpContext http, FormService forms, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Admin) is { } denied) return denied;
        var (ok, row, problem) = await forms.PublishAsync(id, ct);
        if (ok) return Results.Json(FormDto.From(row!), Json.Options);
        return row is null ? Problem.From(ApiError.NotFound, ctx.CorrelationId) : Invalid(ctx, problem!);
    }
}
