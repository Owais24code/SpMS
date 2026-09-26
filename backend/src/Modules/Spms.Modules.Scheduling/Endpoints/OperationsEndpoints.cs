using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Guest.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Scheduling.Operations;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Scheduling.Endpoints;

/* ------------------------------- wire shapes ------------------------------- */

public sealed record VisitAppointmentDto(string AppointmentId, string ServiceName, string? ProviderId, string? RoomId,
    string StartUtc, string EndUtc, string Status, int RowVersion);

public sealed record VisitDto(
    string VisitId, string VisitType, string GuestAlias, string VisitDate, string OperatingMode, string Status,
    IReadOnlyList<string> AllowedTransitions, string? ScheduledArrivalUtc, string? ActualArrivalUtc, string? ClosedUtc,
    string? Notes, int RowVersion, string ETag, IReadOnlyList<VisitAppointmentDto> Appointments)
{
    public static VisitDto From(VisitView v) => new(
        v.Visit.VisitId.ToString(), v.Visit.VisitType, v.GuestAlias, v.Visit.VisitDate.ToString("yyyy-MM-dd"),
        v.Visit.OperatingMode, v.Visit.Status, VisitService.NextFrom(v.Visit.Status),
        Utc(v.Visit.ScheduledArrivalAt), Utc(v.Visit.ActualArrivalAt), Utc(v.Visit.ClosedAt), v.Visit.Notes,
        v.Visit.Version, $"\"{v.Visit.Version}\"",
        v.Appointments.Select(a => new VisitAppointmentDto(a.AppointmentId.ToString(), a.ServiceName, a.ProviderId?.ToString(),
            a.RoomId?.ToString(), a.StartUtc.ToUniversalTime().ToString("O"), a.EndUtc.ToUniversalTime().ToString("O"), a.Status, a.Version)).ToList());

    internal static string? Utc(DateTimeOffset? t) => t?.ToUniversalTime().ToString("O");
}

public sealed record WaitlistDto(
    string WaitlistId, string GuestAlias, string? ServiceId, string EarliestUtc, string LatestUtc, string? ProviderId,
    string? Notes, string Status, string? ExpiresUtc, string? OfferExpiresUtc, string? AcceptedAppointmentId,
    string CreatedUtc, int RowVersion, string ETag)
{
    public static WaitlistDto From(WaitlistView w) => new(
        w.Row.WaitlistId.ToString(), w.GuestAlias, w.Row.ServiceId?.ToString(),
        w.Criteria.EarliestUtc.ToUniversalTime().ToString("O"), w.Criteria.LatestUtc.ToUniversalTime().ToString("O"),
        w.Criteria.ProviderId?.ToString(), w.Criteria.Notes, w.Row.Status,
        VisitDto.Utc(w.Row.ExpiresAt), VisitDto.Utc(w.Row.OfferExpiresAt), w.Row.AcceptedAppointmentId?.ToString(),
        w.Row.CreatedAt.ToUniversalTime().ToString("O"), w.Row.Version, $"\"{w.Row.Version}\"");
}

public sealed record TurnaroundDto(
    string TurnaroundTaskId, string RoomId, string RoomName, string? AppointmentId, string TaskType, string DueUtc,
    string Status, string? Result, string? ChecklistCode, string? AssignedStaffId, string? CompletedUtc, int RowVersion, string ETag)
{
    public static TurnaroundDto From(TurnaroundView t) => new(
        t.Row.TurnaroundTaskId.ToString(), t.Row.ResourceId.ToString(), t.RoomName, t.Row.AppointmentId?.ToString(),
        t.Row.TaskType, t.Row.DueAt.ToUniversalTime().ToString("O"), t.Row.Status, t.Row.Result, t.Row.ChecklistCode,
        t.Row.AssignedStaffId?.ToString(), VisitDto.Utc(t.Row.CompletedAt), t.Row.Version, $"\"{t.Row.Version}\"");
}

/// <summary>
/// One arrival at the desk: the appointment plus what stands between the guest
/// and the treatment room. Deposit is reported but not yet tracked here; the
/// commerce module owns it.
/// </summary>
public sealed record ArrivalDto(
    string AppointmentId, string GuestAlias, string ServiceName, string StartUtc, string StartLocal,
    string? ProviderId, string? RoomId, string Status, int RowVersion, string ETag, string? VisitId, string? CheckedInUtc,
    bool RoomReady, string Intake, string Deposit);

public sealed record CreateVisitRequest(string? GuestId, string? VisitDate, string? VisitType, string? ScheduledArrivalUtc, string? Notes, string? PmsStayReference);
public sealed record VisitTransitionRequest(string? To, string? Reason);
public sealed record CreateWaitlistRequest(string? GuestId, string? ServiceId, string? EarliestUtc, string? LatestUtc, string? ProviderId, string? Notes, string? ExpiresUtc);
public sealed record WaitlistOfferRequest(int? Minutes);
public sealed record WaitlistAcceptRequest(string? AppointmentId);
public sealed record CreateTurnaroundRequest(string? RoomId, string? TaskType, string? DueUtc, string? ChecklistCode);
public sealed record CompleteTurnaroundRequest(string? Result, string? ChecklistCode);
public sealed record SkipTurnaroundRequest(string? Reason);

/// <summary>Visits, the waitlist, room turnover and the desk's arrivals list.</summary>
public static class OperationsEndpoints
{
    public static void MapOperations(this IEndpointRouteBuilder app)
    {
        app.MapGet("/visits", ListVisits);
        app.MapGet("/visits/{id:guid}", GetVisit);
        app.MapPost("/visits", CreateVisit);
        app.MapPost("/visits/{id:guid}/transitions", TransitionVisit);

        app.MapGet("/waitlist", ListWaitlist);
        app.MapGet("/waitlist/candidates", WaitlistCandidates);
        app.MapPost("/waitlist", AddWaitlist);
        app.MapPost("/waitlist/{id:guid}/offer", OfferWaitlist);
        app.MapPost("/waitlist/{id:guid}/accept", AcceptWaitlist);
        app.MapPost("/waitlist/{id:guid}/cancel", CancelWaitlist);

        app.MapGet("/turnaround", ListTurnaround);
        app.MapPost("/turnaround", AddTurnaround);
        app.MapPost("/turnaround/{id:guid}/start", StartTurnaround);
        app.MapPost("/turnaround/{id:guid}/complete", CompleteTurnaround);
        app.MapPost("/turnaround/{id:guid}/skip", SkipTurnaround);

        app.MapGet("/front-desk/arrivals", Arrivals);
    }

    private static IResult Violations(RequestContext ctx, List<object> v) =>
        Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "One or more fields were not accepted.",
            extensions: Problem.Ext("field_violations", v));

    private static IResult Json<T>(HttpContext http, T dto, string etag, int status = 200)
    {
        http.Response.Headers.ETag = etag;
        return Results.Json(dto, Spms.Web.Json.Options, statusCode: status);
    }

    /* --------------------------------- visits -------------------------------- */

    private static async Task<IResult> ListVisits(HttpContext http, VisitService visits, AppointmentAccess access,
        string? date, string? status, int? offset, int? limit, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_view_board", ct) is { } refused) return refused;
        if (!DateOnly.TryParse(date ?? "", out var day)) return Violations(ctx, [new { field = "date", rule = "iso_date_required" }]);
        if (status is not null && !VisitStatuses.All.Contains(status)) return Violations(ctx, [new { field = "status", rule = "unknown_status" }]);
        var (o, l) = Guard.Page(offset, limit);
        var (items, total) = await visits.ListAsync(day, status, o, l, ct);
        return Results.Json(new Page<VisitDto>(items.Select(VisitDto.From).ToList(), total, o, l), Spms.Web.Json.Options);
    }

    private static async Task<IResult> GetVisit(HttpContext http, VisitService visits, AppointmentAccess access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        var v = await visits.ViewAsync(id, ct);
        if (v is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId);
        if (await access.PropertyAsync(ctx, "can_view_board", ct) is { } refused) return refused;
        var dto = VisitDto.From(v);
        return Json(http, dto, dto.ETag);
    }

    private static async Task<IResult> CreateVisit(HttpContext http, VisitService visits, AppointmentAccess access, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Write) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_book", ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<CreateVisitRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;

        var v = new List<object>();
        if (!Guid.TryParse(req!.GuestId, out var guestId)) v.Add(new { field = "guestId", rule = "uuid_required" });
        if (!DateOnly.TryParse(req.VisitDate ?? "", out var date)) v.Add(new { field = "visitDate", rule = "iso_date_required" });
        var type = req.VisitType ?? "DayGuest";
        if (!VisitTypes.All.Contains(type)) v.Add(new { field = "visitType", rule = "unknown_visit_type" });
        DateTimeOffset? arrival = null;
        if (req.ScheduledArrivalUtc is not null)
        {
            if (Guard.TryParseInstant(req.ScheduledArrivalUtc, out var a) && Guard.IsSaneInstant(a)) arrival = a;
            else v.Add(new { field = "scheduledArrivalUtc", rule = "iso8601_required" });
        }
        if (req.Notes is { Length: > 2000 }) v.Add(new { field = "notes", rule = "max_length_2000" });
        if (v.Count > 0) return Violations(ctx, v);

        var r = await visits.CreateAsync(guestId, date, type, arrival, req.Notes, req.PmsStayReference, ct);
        if (r.Outcome == VisitService.Outcome.UnknownGuest) return Violations(ctx, [new { field = "guestId", rule = "unknown_guest" }]);
        var dto = VisitDto.From(r.View!);
        http.Response.Headers.Location = $"/visits/{dto.VisitId}";
        return Json(http, dto, dto.ETag, 201);
    }

    private static async Task<IResult> TransitionVisit(HttpContext http, VisitService visits, AppointmentAccess access, Guid id, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Write) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<VisitTransitionRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (req!.To is null || !VisitStatuses.All.Contains(req.To))
            return Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, "to must be a visit status.",
                extensions: Problem.Ext("allowed", VisitStatuses.All));
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await access.PropertyAsync(ctx, "can_check_in", ct) is { } refused) return refused;

        var r = await visits.TransitionAsync(id, req.To, version, req.Reason, ct);
        return r.Outcome switch
        {
            VisitService.Outcome.NotFound => Problem.From(ApiError.NotFound, ctx.CorrelationId),
            VisitService.Outcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, r.Detail ?? "The visit changed since you read it.",
                extensions: r.View is null ? null : Problem.Ext("current", VisitDto.From(r.View))),
            VisitService.Outcome.Illegal => Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, r.Detail,
                extensions: Problem.Ext("allowed", VisitService.NextFrom(r.View!.Visit.Status))),
            VisitService.Outcome.Blocked => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
            _ => Json(http, VisitDto.From(r.View!), $"\"{r.View!.Visit.Version}\""),
        };
    }

    /* -------------------------------- waitlist ------------------------------- */

    private static async Task<IResult> ListWaitlist(HttpContext http, WaitlistService waitlist, AppointmentAccess access,
        string? status, int? offset, int? limit, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_view_board", ct) is { } refused) return refused;
        if (status is not null && !WaitlistEntryStatuses.All.Contains(status)) return Violations(ctx, [new { field = "status", rule = "unknown_status" }]);
        var (o, l) = Guard.Page(offset, limit);
        var (items, total) = await waitlist.ListAsync(status, o, l, ct);
        return Results.Json(new Page<WaitlistDto>(items.Select(WaitlistDto.From).ToList(), total, o, l), Spms.Web.Json.Options);
    }

    private static async Task<IResult> WaitlistCandidates(HttpContext http, WaitlistService waitlist, AppointmentAccess access,
        string? startUtc, string? endUtc, string? serviceId, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_view_board", ct) is { } refused) return refused;
        if (!Guard.TryParseInstant(startUtc, out var start) || !Guard.TryParseInstant(endUtc, out var end) || end <= start)
            return Violations(ctx, [new { field = "startUtc", rule = "iso8601_window_required" }]);
        Guid? service = Guid.TryParse(serviceId, out var s) ? s : null;
        var items = await waitlist.CandidatesAsync(start, end, service, ct);
        return Results.Json(items.Select(WaitlistDto.From).ToList(), Spms.Web.Json.Options);
    }

    private static async Task<IResult> AddWaitlist(HttpContext http, WaitlistService waitlist, AppointmentAccess access, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Write) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_book", ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<CreateWaitlistRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;

        var v = new List<object>();
        if (!Guid.TryParse(req!.GuestId, out var guestId)) v.Add(new { field = "guestId", rule = "uuid_required" });
        Guid? serviceId = null;
        if (req.ServiceId is not null) { if (Guid.TryParse(req.ServiceId, out var sid)) serviceId = sid; else v.Add(new { field = "serviceId", rule = "uuid_required" }); }
        Guid? providerId = null;
        if (req.ProviderId is not null) { if (Guid.TryParse(req.ProviderId, out var pid)) providerId = pid; else v.Add(new { field = "providerId", rule = "uuid_required" }); }
        if (!Guard.TryParseInstant(req.EarliestUtc, out var earliest) || !Guard.IsSaneInstant(earliest)) v.Add(new { field = "earliestUtc", rule = "iso8601_required" });
        if (!Guard.TryParseInstant(req.LatestUtc, out var latest) || !Guard.IsSaneInstant(latest)) v.Add(new { field = "latestUtc", rule = "iso8601_required" });
        else if (latest <= earliest) v.Add(new { field = "latestUtc", rule = "after_earliest" });
        DateTimeOffset? expires = null;
        if (req.ExpiresUtc is not null) { if (Guard.TryParseInstant(req.ExpiresUtc, out var e)) expires = e; else v.Add(new { field = "expiresUtc", rule = "iso8601_required" }); }
        if (req.Notes is { Length: > 500 }) v.Add(new { field = "notes", rule = "max_length_500" });
        if (v.Count > 0) return Violations(ctx, v);

        var r = await waitlist.AddAsync(guestId, serviceId, new WaitlistCriteria(earliest, latest, providerId, req.Notes), expires, ct);
        if (r.Outcome == WaitlistService.Outcome.UnknownGuest) return Violations(ctx, [new { field = "guestId", rule = "unknown_guest" }]);
        var dto = WaitlistDto.From(r.View!);
        return Json(http, dto, dto.ETag, 201);
    }

    private static Task<IResult> OfferWaitlist(HttpContext http, WaitlistService waitlist, AppointmentAccess access, Guid id, CancellationToken ct) =>
        WaitlistChange<WaitlistOfferRequest>(http, access, ct, (req, v) => waitlist.OfferAsync(id, v, req.Minutes ?? 30, ct));

    private static Task<IResult> AcceptWaitlist(HttpContext http, WaitlistService waitlist, AppointmentAccess access, Guid id, CancellationToken ct) =>
        WaitlistChange<WaitlistAcceptRequest>(http, access, ct, (req, v) =>
            Guid.TryParse(req.AppointmentId, out var a)
                ? waitlist.AcceptAsync(id, v, a, ct)
                : Task.FromResult(new WaitlistService.Result(WaitlistService.Outcome.NotFound, null, "appointmentId is required.")));

    private static Task<IResult> CancelWaitlist(HttpContext http, WaitlistService waitlist, AppointmentAccess access, Guid id, CancellationToken ct) =>
        WaitlistChange<object>(http, access, ct, (_, v) => waitlist.CancelAsync(id, v, ct));

    private static async Task<IResult> WaitlistChange<TReq>(HttpContext http, AppointmentAccess access, CancellationToken ct,
        Func<TReq, int, Task<WaitlistService.Result>> change) where TReq : class
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Write) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<TReq>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await access.PropertyAsync(ctx, "can_book", ct) is { } refused) return refused;

        var r = await change(req!, version);
        return r.Outcome switch
        {
            WaitlistService.Outcome.NotFound => Problem.From(ApiError.NotFound, ctx.CorrelationId, r.Detail),
            WaitlistService.Outcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, "The entry changed since you read it.",
                extensions: r.View is null ? null : Problem.Ext("current", WaitlistDto.From(r.View))),
            WaitlistService.Outcome.Illegal or WaitlistService.Outcome.WrongGuest => Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, r.Detail),
            _ => Json(http, WaitlistDto.From(r.View!), $"\"{r.View!.Row.Version}\""),
        };
    }

    /* ------------------------------- turnaround ------------------------------ */

    private static async Task<IResult> ListTurnaround(HttpContext http, TurnaroundService turnaround, IAccessDecider decider,
        bool? open, int? offset, int? limit, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await RoomsAccessAsync(ctx, decider, write: false, ct) is { } refused) return refused;
        var (o, l) = Guard.Page(offset, limit);
        var (items, total) = await turnaround.ListAsync(open ?? true, o, l, ct);
        return Results.Json(new Page<TurnaroundDto>(items.Select(TurnaroundDto.From).ToList(), total, o, l), Spms.Web.Json.Options);
    }

    private static async Task<IResult> AddTurnaround(HttpContext http, TurnaroundService turnaround, IAccessDecider decider, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Inventory) is { } denied) return denied;
        if (await RoomsAccessAsync(ctx, decider, write: true, ct) is { } refused) return refused;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<CreateTurnaroundRequest>(body, ctx, out var req, out var parseFail)) return parseFail!;
        var v = new List<object>();
        if (!Guid.TryParse(req!.RoomId, out var room)) v.Add(new { field = "roomId", rule = "uuid_required" });
        var type = req.TaskType ?? "DeepClean";
        if (!TurnaroundService.TaskTypes.Contains(type)) v.Add(new { field = "taskType", rule = "unknown_task_type" });
        var due = DateTimeOffset.UtcNow;
        if (req.DueUtc is not null && (!Guard.TryParseInstant(req.DueUtc, out due) || !Guard.IsSaneInstant(due))) v.Add(new { field = "dueUtc", rule = "iso8601_required" });
        if (v.Count > 0) return Violations(ctx, v);
        var r = await turnaround.AddAsync(room, type, due, req.ChecklistCode, ct);
        if (r.Outcome == TurnaroundService.Outcome.UnknownRoom) return Violations(ctx, [new { field = "roomId", rule = "unknown_room" }]);
        var dto = TurnaroundDto.From(r.View!);
        return Json(http, dto, dto.ETag, 201);
    }

    private static Task<IResult> StartTurnaround(HttpContext http, TurnaroundService t, IAccessDecider d, Guid id, CancellationToken ct) =>
        TurnaroundChange<object>(http, d, ct, (_, v) => Task.FromResult<(TurnaroundService.Result?, IResult?)>((null, null)), (_, v) => t.StartAsync(id, v, ct));

    private static Task<IResult> CompleteTurnaround(HttpContext http, TurnaroundService t, IAccessDecider d, Guid id, CancellationToken ct) =>
        TurnaroundChange<CompleteTurnaroundRequest>(http, d, ct,
            (req, _) => Task.FromResult<(TurnaroundService.Result?, IResult?)>(
                req.Result is not null && TurnaroundService.Results.Contains(req.Result) ? (null, null)
                    : (null, Problem.From(ApiError.ValidationFailed, RequestContext.From(http).CorrelationId, "result must be Pass, Fail or NeedsAttention.",
                        extensions: Problem.Ext("field_violations", new[] { new { field = "result", rule = "unknown_result" } })))),
            (req, v) => t.CompleteAsync(id, v, req.Result!, req.ChecklistCode, ct));

    private static Task<IResult> SkipTurnaround(HttpContext http, TurnaroundService t, IAccessDecider d, Guid id, CancellationToken ct) =>
        TurnaroundChange<SkipTurnaroundRequest>(http, d, ct,
            (req, _) => Task.FromResult<(TurnaroundService.Result?, IResult?)>(
                string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500
                    ? (null, Problem.From(ApiError.ValidationFailed, RequestContext.From(http).CorrelationId, "A reason (up to 500 characters) is required to skip a task."))
                    : (null, null)),
            (req, v) => t.SkipAsync(id, v, req.Reason!, ct));

    private static async Task<IResult> TurnaroundChange<TReq>(HttpContext http, IAccessDecider decider, CancellationToken ct,
        Func<TReq, int, Task<(TurnaroundService.Result?, IResult?)>> validate,
        Func<TReq, int, Task<TurnaroundService.Result>> change) where TReq : class
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Inventory) is { } denied) return denied;
        var (body, fail) = await Guard.ReadBodyAsync(http, ctx, ct);
        if (body is null) return fail!;
        if (!Guard.TryParse<TReq>(body, ctx, out var req, out var parseFail)) return parseFail!;
        if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
        if (await RoomsAccessAsync(ctx, decider, write: true, ct) is { } refused) return refused;
        if ((await validate(req!, version)).Item2 is { } invalid) return invalid;

        var r = await change(req!, version);
        return r.Outcome switch
        {
            TurnaroundService.Outcome.NotFound => Problem.From(ApiError.NotFound, ctx.CorrelationId),
            TurnaroundService.Outcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, "The task changed since you read it.",
                extensions: r.View is null ? null : Problem.Ext("current", TurnaroundDto.From(r.View))),
            TurnaroundService.Outcome.Illegal => Problem.From(ApiError.ValidationFailed, ctx.CorrelationId, r.Detail),
            _ => Json(http, TurnaroundDto.From(r.View!), $"\"{r.View!.Row.Version}\""),
        };
    }

    /// <summary>Room readiness is housekeeping's (can_update_room_readiness); the desk and the manager may read it.</summary>
    private static async Task<IResult?> RoomsAccessAsync(RequestContext ctx, IAccessDecider decider, bool write, CancellationToken ct)
    {
        if (await Guard.RequireAccessAsync(ctx, decider, "can_update_room_readiness", Fga.Property(ctx.PropertyId), ct: ct) is not { } refused)
            return null;
        if (write) return refused;
        return await Guard.RequireAccessAsync(ctx, decider, "can_view_board", Fga.Property(ctx.PropertyId), ct: ct);
    }

    /* -------------------------------- arrivals ------------------------------- */

    private static async Task<IResult> Arrivals(HttpContext http, SpmsDbContext db, IAppointmentRepository repo, IPropertyDirectory properties,
        TurnaroundService turnaround, AppointmentAccess access, string? date, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        if (await access.PropertyAsync(ctx, "can_view_board", ct) is { } refused) return refused;
        if (!DateOnly.TryParse(date ?? "", out var day)) return Violations(ctx, [new { field = "date", rule = "iso_date_required" }]);
        var profile = await properties.FindAsync(ctx.Tenant(), ctx.Property(), ct);
        if (profile is null) return Problem.From(ApiError.NotFound, ctx.CorrelationId, "This tenant has no such property.");

        var from = LocalClock.DayStartUtc(day, profile.TimeZoneId);
        var to = LocalClock.DayStartUtc(day.AddDays(1), profile.TimeZoneId);
        var rows = (await repo.ListOverlappingAsync(ctx.Tenant(), ctx.Property(), from, to, ct))
            .Where(a => a.StartUtc >= from && a.Status is AppointmentStatus.Held or AppointmentStatus.Confirmed
                or AppointmentStatus.CheckedIn or AppointmentStatus.Ready or AppointmentStatus.InService)
            .ToList();

        var notReady = await turnaround.RoomsNotReadyAsync(ct);
        var ids = rows.Select(a => Guid.Parse(a.AppointmentId)).ToList();
        var intake = await (from a in db.Set<AppointmentRow>().AsNoTracking()
                            where ids.Contains(a.AppointmentId)
                            join s in db.Set<ServiceRow>() on a.ServiceId equals s.ServiceId
                            select new { a.AppointmentId, s.RequiresIntake, a.IntakeAcknowledgedAt })
            .ToDictionaryAsync(x => x.AppointmentId, ct);

        var items = rows.Select(a =>
        {
            var i = intake.GetValueOrDefault(Guid.Parse(a.AppointmentId));
            var dto = AppointmentDto.From(a);
            return new ArrivalDto(a.AppointmentId, a.GuestAlias, a.ServiceName, dto.StartUtc, dto.StartLocal,
                a.ProviderId, a.RoomId, a.Status.ToString(), a.RowVersion, dto.ETag, a.VisitId, dto.CheckedInUtc,
                RoomReady: a.RoomId is null || !notReady.Contains(Guid.Parse(a.RoomId)),
                Intake: i is null || !i.RequiresIntake ? "NotRequired" : i.IntakeAcknowledgedAt is null ? "Pending" : "Complete",
                Deposit: "NotTracked");
        }).ToList();
        return Results.Json(new { date = day.ToString("yyyy-MM-dd"), timeZone = profile.TimeZoneId, items }, Spms.Web.Json.Options);
    }
}
