using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Core.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Core.Settings;

public sealed record SettingProposal(string? SettingKey, JsonElement? Value, bool? PropertyOnly, string? DeploymentScope, string? EffectiveFrom, string? Reason);
public sealed record SettingDecision(string? Reason);

/// <summary>
/// Governed configuration (SEC-014). A change is a new row, proposed by one
/// administrator and approved by a different one (the database refuses an
/// author approving their own row). Approval makes it Active from its
/// effective date and closes the value it replaces there; nothing is edited
/// in place, so the history of every policy is its rows.
/// Known keys are shape-checked; anything else must at least be an object.
/// </summary>
public sealed partial class SettingsAdmin(SpmsDbContext db, MasterData master, IUnitOfWork uow, IClock clock)
{
    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$")] private static partial Regex KeyRx();

    public enum Outcome { Ok, NotFound, Invalid, StaleVersion, Illegal, SameApprover }

    /// <summary>Validates the value for keys SpMS reads. Returns a reason when it is refused.</summary>
    public static string? CheckValue(string key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "A setting's value is a JSON object.";
        static bool Num(JsonElement o, string p, double min, double max) =>
            o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() >= min && v.GetDouble() <= max;
        return key switch
        {
            "policy.deposit" => Num(value, "percent", 0, 100) && Num(value, "minimumMinor", 0, 10_000_000) ? null
                : "policy.deposit is {\"percent\": 0–100, \"minimumMinor\": ≥ 0}.",
            "policy.cancellation" => Num(value, "noticeHours", 0, 720) && Num(value, "feePercent", 0, 100) ? null
                : "policy.cancellation is {\"noticeHours\": 0–720, \"feePercent\": 0–100}.",
            "messaging.quiet_hours" => value.TryGetProperty("start", out var s) && value.TryGetProperty("end", out var e)
                                       && TimeOnly.TryParse(s.GetString(), out _) && TimeOnly.TryParse(e.GetString(), out _) ? null
                : "messaging.quiet_hours is {\"start\": \"21:00\", \"end\": \"08:00\"}.",
            "property.operating_mode" => value.TryGetProperty("mode", out var mode) && mode.GetString() is "Standalone" or "MarqueeIntegrated" ? null
                : "property.operating_mode is {\"mode\": \"Standalone\" | \"MarqueeIntegrated\"}.",
            _ when key.StartsWith("retention.", StringComparison.Ordinal) => Num(value, "days", 1, 36_500) ? null
                : "A retention setting is {\"days\": 1–36500}.",
            _ => null,
        };
    }

    public async Task<List<SettingRow>> ListAsync(string? key, CancellationToken ct)
    {
        var property = db.Scope.CurrentPropertyId;
        return await db.Set<SettingRow>().AsNoTracking()
            .Where(s => (key == null || s.SettingKey == key) && (s.PropertyId == null || s.PropertyId == property))
            .OrderBy(s => s.SettingKey).ThenByDescending(s => s.EffectiveFrom).ThenByDescending(s => s.CreatedAt)
            .Take(500).ToListAsync(ct);
    }

    public async Task<Edit<SettingRow>> ProposeAsync(SettingProposal p, DateTimeOffset from, CancellationToken ct)
    {
        if (p.SettingKey is null || !KeyRx().IsMatch(p.SettingKey)) return Edit<SettingRow>.Refused(EditOutcome.Invalid, "settingKey is dotted lowercase (policy.deposit).");
        if (p.Value is not { } value || CheckValue(p.SettingKey, value) is { } bad) return Edit<SettingRow>.Refused(EditOutcome.Invalid, CheckValue(p.SettingKey, p.Value ?? default) ?? "value is required.");
        var scope = p.DeploymentScope ?? "Production";
        if (scope is not ("Training" or "Pilot" or "Production")) return Edit<SettingRow>.Refused(EditOutcome.Invalid, "deploymentScope is Training, Pilot or Production.");
        if (string.IsNullOrWhiteSpace(p.Reason)) return Edit<SettingRow>.Refused(EditOutcome.Invalid, "A change to governed configuration says why (reason).");
        if (p.SettingKey == OperatingMode.Key && p.PropertyOnly != true) return Edit<SettingRow>.Refused(EditOutcome.Invalid, "The operating mode is a property's own (propertyOnly).");
        return await master.CreateAsync(new SettingRow
        {
            SettingId = Uuid7.New(), PropertyId = p.PropertyOnly == true ? db.Scope.RequireProperty() : null, SettingKey = p.SettingKey,
            ValueJson = value.GetRawText(), DeploymentScope = scope, Reason = p.Reason.Trim(), Status = "Proposed", EffectiveFrom = from,
        }, "core.setting.propose", "setting", s => s.SettingId, ct);
    }

    public async Task<(Outcome Outcome, SettingRow? Row, string? Detail)> DecideAsync(Guid id, int version, bool approve, string? reason, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var row = await db.Set<SettingRow>().SingleOrDefaultAsync(s => s.SettingId == id, ct);
        if (row is null) return (Outcome.NotFound, null, null);
        if (row.Version != version) { db.ChangeTracker.Clear(); return (Outcome.StaleVersion, row, null); }
        if (row.Status != "Proposed") { db.ChangeTracker.Clear(); return (Outcome.Illegal, row, $"A {row.Status} setting has been decided."); }
        if (row.CreatedBy is { } author && author == db.Scope.PrincipalId)
        { db.ChangeTracker.Clear(); return (Outcome.SameApprover, row, "The author of a configuration change cannot approve it (SEC-014)."); }

        var now = clock.UtcNow;
        if (!approve)
        {
            row.Status = "Rejected";
        }
        else
        {
            if (row.EffectiveFrom < now) row.EffectiveFrom = now;
            // The value it replaces ends where this one starts.
            var current = await db.Set<SettingRow>()
                .Where(s => s.SettingId != id && s.SettingKey == row.SettingKey && s.DeploymentScope == row.DeploymentScope && s.PropertyId == row.PropertyId
                            && (s.Status == "Active" || s.Status == "Approved") && (s.EffectiveTo == null || s.EffectiveTo > row.EffectiveFrom))
                .ToListAsync(ct);
            foreach (var c in current)
            {
                if (c.EffectiveFrom >= row.EffectiveFrom) { c.Status = "Superseded"; continue; }
                c.EffectiveTo = row.EffectiveFrom;
                if (row.EffectiveFrom <= now) c.Status = "Superseded";
            }
            await db.SaveChangesAsync(ct);
            row.Status = row.EffectiveFrom <= now ? "Active" : "Approved";
            row.ApprovedBy = db.Scope.PrincipalId;
            row.ApprovedAt = now;
            if (row.Status == "Active") await OperatingMode.ApplyAsync(db, row, ct);
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException p && MasterData.Map(p) is { } m)
        { db.ChangeTracker.Clear(); return (m.Outcome == EditOutcome.Invalid ? Outcome.Invalid : Outcome.Illegal, null, m.Detail); }
        await master.AuditAsync(new AuditEntry(approve ? "core.setting.approve" : "core.setting.reject", "setting", id.ToString(), row.Version,
            FromStatus: "Proposed", ToStatus: row.Status, ReasonText: reason, AfterData: new { row.SettingKey, row.PropertyId, row.DeploymentScope, row.ValueJson }), ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return (Outcome.Ok, row, null);
    }
}

/// <summary>
/// The property's operating mode (§53.2; DEC-011) is governed like any policy:
/// proposed as property.operating_mode, approved by a second person, and
/// written to core.property when it takes effect. The mode is the default;
/// who owns each capability is still decided per capability (DEC-001), and a
/// Marquee-integrated property with no payment decision refuses to guess.
/// </summary>
public static class OperatingMode
{
    public const string Key = "property.operating_mode";

    public static async Task ApplyAsync(SpmsDbContext db, SettingRow s, CancellationToken ct)
    {
        if (s.SettingKey != Key || s.PropertyId is not { } property) return;
        var mode = JsonDocument.Parse(s.ValueJson).RootElement.GetProperty("mode").GetString()!;
        await db.Set<PropertyRow>().Where(p => p.PropertyId == property && p.OperatingMode != mode)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.OperatingMode, mode).SetProperty(p => p.Version, p => p.Version + 1), ct);
    }
}

/// <summary>Moves an approved future setting to Active, and the one it replaces to Superseded, when its day comes.</summary>
public sealed class SettingActivationJob(SpmsDbContext db, IClock clock) : IPropertyJob
{
    public string Name => "core.setting-activation";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await db.Set<SettingRow>().Where(s => s.Status == "Approved" && s.EffectiveFrom <= now).ToListAsync(ct);
        var ended = await db.Set<SettingRow>().Where(s => s.Status == "Active" && s.EffectiveTo != null && s.EffectiveTo <= now).ToListAsync(ct);
        foreach (var s in due) { s.Status = "Active"; await OperatingMode.ApplyAsync(db, s, ct); }
        foreach (var s in ended) s.Status = "Superseded";
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return due.Count + ended.Count;
    }
}

public static class SettingsEndpoints
{
    private static object Dto(SettingRow s) => new
    {
        settingId = s.SettingId, s.SettingKey, value = JsonDocument.Parse(s.ValueJson).RootElement.Clone(), s.PropertyId, propertyOnly = s.PropertyId != null,
        s.DeploymentScope, s.Reason, s.Status, proposedBy = s.CreatedBy, s.ApprovedBy, effectiveFrom = s.EffectiveFrom.ToUniversalTime().ToString("O"),
        effectiveTo = s.EffectiveTo?.ToUniversalTime().ToString("O"), rowVersion = s.Version, eTag = $"\"{s.Version}\"",
    };

    public static IEndpointRouteBuilder MapSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet("/settings", async (HttpContext http, SettingsAdmin admin, string? key, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await admin.ListAsync(key, ct)).Select(Dto), Json.Options);
        });

        app.MapPost("/settings", async (HttpContext http, SettingsAdmin admin, IAccessDecider access, IClock clock, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_propose_configuration", Fga.Tenant(ctx.TenantId), ct) is { } refused) return refused;
            var (p, fail) = await WebApi.BodyAsync<SettingProposal>(http, ctx, ct);
            if (p is null) return fail!;
            var from = clock.UtcNow;
            if (p.EffectiveFrom is not null && !Guard.TryParseInstant(p.EffectiveFrom, out from)) return WebApi.Invalid(ctx, "effectiveFrom is ISO 8601.");
            return (await admin.ProposeAsync(p, from, ct)).ToHttp(http, ctx, Dto, 201);
        });

        foreach (var (path, approve) in new[] { ("approve", true), ("reject", false) })
            app.MapPost($"/settings/{{id:guid}}/{path}", async (HttpContext http, SettingsAdmin admin, IAccessDecider access, Guid id, CancellationToken ct) =>
            {
                var ctx = RequestContext.From(http);
                if (await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_approve_configuration", Fga.Tenant(ctx.TenantId), ct) is { } refused) return refused;
                if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
                var (d, fail) = await WebApi.BodyAsync<SettingDecision>(http, ctx, ct);
                if (d is null) return fail!;
                if (!approve && string.IsNullOrWhiteSpace(d.Reason)) return WebApi.Invalid(ctx, "A rejection says why.");
                var r = await admin.DecideAsync(id, version, approve, d.Reason, ct);
                return r.Outcome switch
                {
                    SettingsAdmin.Outcome.Ok => EditResults.Ok(http, r.Row!, Dto),
                    SettingsAdmin.Outcome.NotFound => WebApi.NotFound(ctx),
                    SettingsAdmin.Outcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, extensions: Problem.Ext("current", Dto(r.Row!))),
                    SettingsAdmin.Outcome.SameApprover => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, r.Detail),
                    SettingsAdmin.Outcome.Invalid => WebApi.Invalid(ctx, r.Detail ?? "Refused."),
                    _ => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
                };
            });

        return app;
    }
}
