using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Spms.Modules.Core.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Core.Integrations;

public sealed record DeviceInput(string? DeviceKind, string? DeviceName, string? PublicKeySpki);
public sealed record MappingInput(string? EntityType, Guid? LocalId, string? SourceSystem, string? SourceKey, string? ExternalPropertyReference);
public sealed record InboundInput(Guid? EventId, string? EventType, JsonElement? Payload);
public sealed record OwnershipInput(string? CapabilityCode, string? OwnerSystem, string? EffectiveFrom);

/// <summary>
/// Devices (SEC-013): a provider tablet, kiosk or desk terminal is its own
/// principal, registered at one property and activated by a manager; OpenFGA
/// learns of it through the outbox, and revocation is immediate at the next
/// sign-in resolution. The device's public key protects its offline queue.
/// </summary>
public sealed class DeviceService(SpmsDbContext db, MasterData master, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    public const string Issuer = "spms-device";

    public Task<List<DeviceRegistrationRow>> ListAsync(CancellationToken ct) =>
        db.Set<DeviceRegistrationRow>().AsNoTracking().OrderBy(d => d.DeviceName).ToListAsync(ct);

    public async Task<Edit<DeviceRegistrationRow>> RegisterAsync(string kind, string name, byte[] key, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var principal = new PrincipalRow { PrincipalId = Uuid7.New(), PrincipalType = "Device", DisplayName = name };
        db.Add(principal);
        var id = Uuid7.New();
        db.Add(new PrincipalLoginRow
        {
            PrincipalLoginId = Uuid7.New(), PrincipalId = principal.PrincipalId, LoginType = "EntraApplication", IdpIssuer = Issuer,
            IdpSubject = id.ToString(), MfaRequired = false,
        });
        var r = await master.CreateAsync(new DeviceRegistrationRow
        {
            DeviceRegistrationId = id, PropertyId = db.Scope.RequireProperty(), PrincipalId = principal.PrincipalId, DeviceKind = kind,
            DeviceName = name, PublicKeySpki = key, Status = "Pending",
        }, "core.device.register", "device_registration", d => d.DeviceRegistrationId, ct, after: d => new { d.DeviceKind, d.DeviceName, d.PropertyId });
        if (r.Outcome == EditOutcome.Ok) await tx.CommitAsync(ct);
        return r;
    }

    public async Task<Edit<DeviceRegistrationRow>> DecideAsync(Guid id, int version, bool activate, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var r = await master.ChangeAsync<DeviceRegistrationRow>(d => d.DeviceRegistrationId == id, version, activate ? "core.device.activate" : "core.device.revoke",
            "device_registration", d => d.DeviceRegistrationId, d =>
            {
                if (activate && d.Status != "Pending") return $"A {d.Status} device cannot be activated.";
                if (!activate && d.Status == "Revoked") return "Already revoked.";
                d.Status = activate ? "Active" : "Revoked";
                if (!activate) { d.RevokedAt = clock.UtcNow; d.RevokedBy = db.Scope.PrincipalId; }
                return null;
            }, ct, after: d => new { d.DeviceKind, d.DeviceName, d.Status });
        if (r.Outcome != EditOutcome.Ok) return r;
        var d = r.Row!;
        outbox.Enqueue(new OutboxEvent(EventTypes.DeviceRegistrationChanged, "device_registration", d.DeviceRegistrationId, d.Version,
            new { deviceId = d.DeviceRegistrationId, propertyId = d.PropertyId, principalId = d.PrincipalId, status = d.Status, kind = d.DeviceKind }));
        if (!activate)
            await db.Set<PrincipalRow>().Where(p => p.PrincipalId == d.PrincipalId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, "Revoked").SetProperty(p => p.Version, p => p.Version + 1), ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return r;
    }

    /// <summary>A device reports in: last seen now. Only the device itself calls this.</summary>
    public async Task<bool> HeartbeatAsync(Guid principalId, CancellationToken ct) =>
        await db.Set<DeviceRegistrationRow>().Where(d => d.PrincipalId == principalId && d.Status == "Active")
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.LastSeenAt, clock.UtcNow).SetProperty(d => d.Version, d => d.Version + 1), ct) == 1;
}

/// <summary>
/// Integration operations (§26; MCI-004; DEC-001/002). External id mappings;
/// inbound events consumed exactly once; the outbox's health and a replay for
/// what failed; and who owns each capability at a property — the switch that
/// makes a property Marquee-integrated for payment — proposed by one
/// administrator and approved by another.
/// </summary>
public sealed class IntegrationService(SpmsDbContext db, MasterData master, IEnumerable<IInboundHandler> handlers, IUnitOfWork uow, IClock clock)
{
    public static readonly string[] Capabilities = ["Payment", "Catalog", "Guest", "Inventory", "Folio"];
    public static readonly string[] Owners = ["Spa", "Marquee", "Pms", "Pos", "External"];

    public Task<List<ExternalMappingRow>> MappingsAsync(string? entityType, CancellationToken ct) =>
        db.Set<ExternalMappingRow>().AsNoTracking().Where(m => entityType == null || m.EntityType == entityType)
            .OrderBy(m => m.EntityType).ThenBy(m => m.SourceKey).Take(500).ToListAsync(ct);

    public Task<Edit<ExternalMappingRow>> MapAsync(MappingInput i, CancellationToken ct) =>
        master.CreateAsync(new ExternalMappingRow
        {
            ExternalMappingId = Uuid7.New(), PropertyId = db.Scope.CurrentPropertyId, EntityType = i.EntityType!, LocalId = i.LocalId!.Value,
            SourceSystem = i.SourceSystem!, SourceKey = i.SourceKey!, ExternalPropertyReference = i.ExternalPropertyReference, EffectiveFrom = clock.UtcNow,
        }, "core.mapping.create", "external_mapping", m => m.ExternalMappingId, ct);

    public enum InboundOutcome { Processed, Duplicate, NoHandler, Rejected }

    /// <summary>Consumes an event once: the inbox row and the handler's work commit together or not at all.</summary>
    public async Task<(InboundOutcome Outcome, string? Detail)> ConsumeAsync(string system, Guid eventId, string type, JsonElement payload, CancellationToken ct)
    {
        var consumer = $"{system}:{type}";
        await using var tx = await uow.BeginAsync(ct);
        if (await db.Set<EventInboxRow>().AsNoTracking().AnyAsync(e => e.ConsumerName == consumer && e.EventId == eventId, ct))
            return (InboundOutcome.Duplicate, null);
        var handler = handlers.FirstOrDefault(h => h.EventTypes.Contains(type));
        string outcome;
        string? detail = null;
        if (handler is null) { outcome = "Skipped"; detail = "No consumer for that event type."; }
        else
        {
            try { detail = await handler.HandleAsync(system, type, payload, ct); outcome = "Processed"; }
            catch (InboundRejected r) { return (InboundOutcome.Rejected, r.Message); }
        }
        db.Add(new EventInboxRow { EventInboxId = Uuid7.New(), ConsumerName = consumer, EventId = eventId, Outcome = outcome, ProcessedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);
        await master.AuditAsync(new AuditEntry("core.inbound.consume", "event_inbox", eventId.ToString(), ReasonCode: outcome, ReasonText: detail,
            AfterData: new { system, type }), ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return (handler is null ? InboundOutcome.NoHandler : InboundOutcome.Processed, detail);
    }

    public sealed record OutboxHealth(int Pending, int Failing, DateTimeOffset? OldestPendingUtc, IReadOnlyList<EventOutboxRow> Problems);

    public async Task<OutboxHealth> OutboxAsync(CancellationToken ct)
    {
        var pending = db.Set<EventOutboxRow>().AsNoTracking().Where(e => e.PublishedAt == null);
        var count = await pending.CountAsync(ct);
        var failing = await pending.CountAsync(e => e.AttemptCount > 0, ct);
        var oldest = await pending.MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct);
        var problems = await pending.Where(e => e.AttemptCount > 0).OrderBy(e => e.OccurredAt).Take(50).ToListAsync(ct);
        return new OutboxHealth(count, failing, oldest, problems);
    }

    /// <summary>A failed event goes back to the front of the queue, as it was: the same id, so consumers still see it once.</summary>
    public async Task<bool> ReplayAsync(Guid eventId, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var n = await db.Set<EventOutboxRow>().Where(e => e.EventId == eventId && e.PublishedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.NextAttemptAt, clock.UtcNow).SetProperty(e => e.AttemptCount, 0).SetProperty(e => e.LastError, (string?)null), ct);
        if (n == 0) return false;
        await master.AuditAsync(new AuditEntry("core.outbox.replay", "event_outbox", eventId.ToString()), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /* ---------------------------- capability ownership ---------------------------- */

    public Task<List<CapabilityOwnershipRow>> OwnershipAsync(CancellationToken ct)
    {
        var property = db.Scope.RequireProperty();
        return db.Set<CapabilityOwnershipRow>().AsNoTracking().Where(o => o.PropertyId == property)
            .OrderBy(o => o.CapabilityCode).ThenByDescending(o => o.CreatedAt).ToListAsync(ct);
    }

    public Task<Edit<CapabilityOwnershipRow>> ProposeOwnershipAsync(string capability, string owner, DateTimeOffset from, CancellationToken ct) =>
        master.CreateAsync(new CapabilityOwnershipRow
        {
            OwnershipId = Uuid7.New(), PropertyId = db.Scope.RequireProperty(), CapabilityCode = capability, OwnerSystem = owner,
            EffectiveRange = new NpgsqlRange<DateTime>(from.UtcDateTime, true, false, default, false, true), Status = "Proposed",
        }, "core.ownership.propose", "capability_ownership", o => o.OwnershipId, ct);

    public enum OwnershipOutcome { Ok, NotFound, StaleVersion, Illegal, SameApprover }

    /// <summary>Approval makes it the single authority from its start; the one it replaces ends there.</summary>
    public async Task<(OwnershipOutcome Outcome, CapabilityOwnershipRow? Row, string? Detail)> DecideOwnershipAsync(Guid id, int version, bool approve, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var o = await db.Set<CapabilityOwnershipRow>().SingleOrDefaultAsync(x => x.OwnershipId == id, ct);
        if (o is null) return (OwnershipOutcome.NotFound, null, null);
        if (o.Version != version) { db.ChangeTracker.Clear(); return (OwnershipOutcome.StaleVersion, o, null); }
        if (o.Status != "Proposed") { db.ChangeTracker.Clear(); return (OwnershipOutcome.Illegal, o, $"A {o.Status} decision has been made."); }
        if (o.CreatedBy is { } author && author == db.Scope.PrincipalId)
        { db.ChangeTracker.Clear(); return (OwnershipOutcome.SameApprover, o, "The person who proposed the change cannot approve it."); }
        if (!approve) o.Status = "Rejected";
        else
        {
            var now = clock.UtcNow.UtcDateTime;
            var start = o.EffectiveRange.LowerBound < now ? now : o.EffectiveRange.LowerBound;
            o.EffectiveRange = new NpgsqlRange<DateTime>(start, true, false, default, false, true);
            var current = await db.Set<CapabilityOwnershipRow>().Where(x => x.OwnershipId != id && x.PropertyId == o.PropertyId && x.CapabilityCode == o.CapabilityCode
                                                                           && (x.Status == "Active" || x.Status == "Approved")).ToListAsync(ct);
            foreach (var c in current)
            {
                if (c.EffectiveRange.LowerBound >= start) { c.Status = "Superseded"; continue; }
                c.EffectiveRange = new NpgsqlRange<DateTime>(c.EffectiveRange.LowerBound, true, start, false);
                c.Status = "Superseded";
            }
            await db.SaveChangesAsync(ct);
            o.Status = "Active";
            o.ApprovedBy = db.Scope.PrincipalId;
            o.ApprovedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await master.AuditAsync(new AuditEntry(approve ? "core.ownership.approve" : "core.ownership.reject", "capability_ownership", id.ToString(), o.Version,
            FromStatus: "Proposed", ToStatus: o.Status, AfterData: new { o.CapabilityCode, o.OwnerSystem }), ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return (OwnershipOutcome.Ok, o, null);
    }
}

public static class IntegrationEndpoints
{
    private static object Device(DeviceRegistrationRow d) => new
    {
        deviceId = d.DeviceRegistrationId, d.PrincipalId, d.DeviceKind, d.DeviceName, d.Status, registeredBy = d.CreatedBy,
        lastSeenUtc = d.LastSeenAt?.ToUniversalTime().ToString("O"), revokedUtc = d.RevokedAt?.ToUniversalTime().ToString("O"),
        keyFingerprint = d.PublicKeySpki.Length == 0 ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(d.PublicKeySpki))[..16],
        rowVersion = d.Version, eTag = $"\"{d.Version}\"",
    };

    private static object Mapping(ExternalMappingRow m) => new
    {
        mappingId = m.ExternalMappingId, m.EntityType, m.LocalId, m.SourceSystem, m.SourceKey, m.ExternalPropertyReference, m.Status,
        rowVersion = m.Version, eTag = $"\"{m.Version}\"",
    };

    private static object Ownership(CapabilityOwnershipRow o) => new
    {
        ownershipId = o.OwnershipId, o.CapabilityCode, o.OwnerSystem, o.Status, proposedBy = o.CreatedBy, o.ApprovedBy,
        effectiveFromUtc = DateTime.SpecifyKind(o.EffectiveRange.LowerBound, DateTimeKind.Utc).ToString("O"),
        effectiveToUtc = o.EffectiveRange.UpperBoundInfinite ? null : DateTime.SpecifyKind(o.EffectiveRange.UpperBound, DateTimeKind.Utc).ToString("O"),
        rowVersion = o.Version, eTag = $"\"{o.Version}\"",
    };

    private static Task<IResult?> Manage(RequestContext ctx, IAccessDecider access, CancellationToken ct) =>
        WebApi.GateAsync(ctx, access, SpaScopes.Device, "can_manage_devices", Fga.Property(ctx.PropertyId), ct);

    public static IEndpointRouteBuilder MapIntegrations(this IEndpointRouteBuilder app)
    {
        /* devices */

        app.MapGet("/devices", async (HttpContext http, DeviceService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Device, "can_operate_devices_lockers", Fga.Property(ctx.PropertyId), ct) is { } refused
                && await Manage(ctx, access, ct) is { } alsoRefused) return alsoRefused;
            return Results.Json((await svc.ListAsync(ct)).Select(Device), Json.Options);
        });

        app.MapPost("/devices", async (HttpContext http, DeviceService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Manage(ctx, access, ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<DeviceInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.DeviceKind is not ("ProviderTablet" or "Kiosk" or "FrontDesk") || string.IsNullOrWhiteSpace(i.DeviceName) || i.DeviceName.Length > 80)
                return WebApi.Invalid(ctx, "deviceKind (ProviderTablet, Kiosk, FrontDesk) and a deviceName are required.");
            byte[] key;
            try { key = Convert.FromBase64String(i.PublicKeySpki ?? ""); }
            catch (FormatException) { return WebApi.Invalid(ctx, "publicKeySpki is the device's public key, base64 SPKI."); }
            if (key.Length is < 32 or > 1024) return WebApi.Invalid(ctx, "publicKeySpki is the device's public key, base64 SPKI.");
            return (await svc.RegisterAsync(i.DeviceKind, i.DeviceName.Trim(), key, ct)).ToHttp(http, ctx, Device, 201);
        });

        foreach (var (path, activate) in new[] { ("activate", true), ("revoke", false) })
            app.MapPost($"/devices/{{id:guid}}/{path}", async (HttpContext http, DeviceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
            {
                var ctx = RequestContext.From(http);
                if (await Manage(ctx, access, ct) is { } refused) return refused;
                if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
                return (await svc.DecideAsync(id, version, activate, ct)).ToHttp(http, ctx, Device);
            });

        app.MapPost("/devices/heartbeat", async (HttpContext http, DeviceService svc, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Device) is { } denied) return denied;
            if (ctx.ActorType != ActorType.Device || ctx.PrincipalId is not { } p) return Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "Only a device reports in.");
            return await svc.HeartbeatAsync(p, ct) ? Results.NoContent() : Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, "This device is not active.");
        });

        /* mappings */

        app.MapGet("/integrations/mappings", async (HttpContext http, IntegrationService svc, IAccessDecider access, string? entityType, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_propose_configuration", Fga.Tenant(ctx.TenantId), ct) is { } refused) return refused;
            return Results.Json((await svc.MappingsAsync(entityType, ct)).Select(Mapping), Json.Options);
        });

        app.MapPost("/integrations/mappings", async (HttpContext http, IntegrationService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Write, "can_integrate", Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<MappingInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.EntityType) || i.LocalId is null || i.SourceSystem is not ("Marquee" or "Pms" or "Pos" or "External") || string.IsNullOrWhiteSpace(i.SourceKey))
                return WebApi.Invalid(ctx, "entityType, localId, sourceSystem (Marquee, Pms, Pos, External) and sourceKey are required.");
            return (await svc.MapAsync(i, ct)).ToHttp(http, ctx, Mapping, 201);
        });

        /* inbound */

        app.MapPost("/integrations/inbound/{system}", async (HttpContext http, IntegrationService svc, IAccessDecider access, string system, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Write, "can_integrate", Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
            if (system is not ("marquee" or "pms" or "pos")) return WebApi.NotFound(ctx);
            var (i, fail) = await WebApi.BodyAsync<InboundInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.EventId is not { } eventId || string.IsNullOrWhiteSpace(i.EventType) || i.Payload is not { ValueKind: JsonValueKind.Object } payload)
                return WebApi.Invalid(ctx, "eventId, eventType and a payload object are required.");
            var (outcome, detail) = await svc.ConsumeAsync(system, eventId, i.EventType, payload, ct);
            return outcome switch
            {
                IntegrationService.InboundOutcome.Rejected => WebApi.Invalid(ctx, detail ?? "The event was refused."),
                _ => Results.Json(new { outcome = outcome.ToString(), detail }, Json.Options, statusCode: outcome == IntegrationService.InboundOutcome.Processed ? 201 : 200),
            };
        });

        /* outbox */

        app.MapGet("/integrations/outbox", async (HttpContext http, IntegrationService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Read, "can_replay_integration", Fga.Property(ctx.PropertyId), ct) is { } refused
                && await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_propose_configuration", Fga.Tenant(ctx.TenantId), ct) is { } alsoRefused) return alsoRefused;
            var h = await svc.OutboxAsync(ct);
            return Results.Json(new
            {
                h.Pending, h.Failing, oldestPendingUtc = h.OldestPendingUtc?.ToUniversalTime().ToString("O"),
                problems = h.Problems.Select(e => new { e.EventId, e.EventType, e.AggregateType, e.AggregateId, e.AttemptCount, e.LastError,
                    occurredUtc = e.OccurredAt.ToUniversalTime().ToString("O"), nextAttemptUtc = e.NextAttemptAt.ToUniversalTime().ToString("O") }),
            }, Json.Options);
        });

        app.MapPost("/integrations/outbox/{id:guid}/replay", async (HttpContext http, IntegrationService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Read, "can_replay_integration", Fga.Property(ctx.PropertyId), ct) is { } refused
                && await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_propose_configuration", Fga.Tenant(ctx.TenantId), ct) is { } alsoRefused) return alsoRefused;
            return await svc.ReplayAsync(id, ct) ? Results.Json(new { replayed = true }, Json.Options) : WebApi.NotFound(ctx);
        });

        /* the property itself: its zone, currency and operating mode, and who owns what now */

        app.MapGet("/properties/current", async (HttpContext http, SpmsDbContext db, IClock clock, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            var p = await db.Set<PropertyRow>().AsNoTracking().SingleOrDefaultAsync(x => x.PropertyId == ctx.PropertyId, ct);
            if (p is null) return WebApi.NotFound(ctx);
            var now = clock.UtcNow.UtcDateTime;
            var owners = (await db.Set<CapabilityOwnershipRow>().AsNoTracking().Where(o => o.PropertyId == p.PropertyId && o.Status == "Active").ToListAsync(ct))
                .Where(o => o.EffectiveRange.LowerBound <= now && (o.EffectiveRange.UpperBoundInfinite || o.EffectiveRange.UpperBound > now))
                .ToDictionary(o => o.CapabilityCode, o => o.OwnerSystem);
            // With no explicit decision a standalone property owns its own; an integrated one has no owner until someone decides.
            var payment = owners.GetValueOrDefault("Payment") ?? (p.OperatingMode == "Standalone" ? "Spa" : null);
            return Results.Json(new
            {
                propertyId = p.PropertyId, p.Code, p.Name, timeZone = p.Timezone, currencyCode = p.CurrencyCode.Trim(), p.Locale, p.OperatingMode,
                owners, paymentOwner = payment, paymentAmbiguous = payment is null,
            }, Json.Options);
        });

        /* ownership (Marquee mode) */

        app.MapGet("/integrations/ownership", async (HttpContext http, IntegrationService svc, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await svc.OwnershipAsync(ct)).Select(Ownership), Json.Options);
        });

        app.MapPost("/integrations/ownership", async (HttpContext http, IntegrationService svc, IAccessDecider access, IClock clock, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_propose_configuration", Fga.Tenant(ctx.TenantId), ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<OwnershipInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (!IntegrationService.Capabilities.Contains(i.CapabilityCode) || !IntegrationService.Owners.Contains(i.OwnerSystem))
                return WebApi.Invalid(ctx, $"capabilityCode ({string.Join(", ", IntegrationService.Capabilities)}) and ownerSystem ({string.Join(", ", IntegrationService.Owners)}) are required.");
            var from = clock.UtcNow;
            if (i.EffectiveFrom is not null && !Guard.TryParseInstant(i.EffectiveFrom, out from)) return WebApi.Invalid(ctx, "effectiveFrom is ISO 8601.");
            return (await svc.ProposeOwnershipAsync(i.CapabilityCode!, i.OwnerSystem!, from, ct)).ToHttp(http, ctx, Ownership, 201);
        });

        foreach (var (path, approve) in new[] { ("approve", true), ("reject", false) })
            app.MapPost($"/integrations/ownership/{{id:guid}}/{path}", async (HttpContext http, IntegrationService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
            {
                var ctx = RequestContext.From(http);
                if (await WebApi.GateAsync(ctx, access, SpaScopes.Admin, "can_approve_configuration", Fga.Tenant(ctx.TenantId), ct) is { } refused) return refused;
                if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
                var r = await svc.DecideOwnershipAsync(id, version, approve, ct);
                return r.Outcome switch
                {
                    IntegrationService.OwnershipOutcome.Ok => EditResults.Ok(http, r.Row!, Ownership),
                    IntegrationService.OwnershipOutcome.NotFound => WebApi.NotFound(ctx),
                    IntegrationService.OwnershipOutcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, extensions: Problem.Ext("current", Ownership(r.Row!))),
                    IntegrationService.OwnershipOutcome.SameApprover => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, r.Detail),
                    _ => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
                };
            });

        return app;
    }
}
