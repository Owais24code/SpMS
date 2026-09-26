using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Resources.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Resources;

public sealed record LocationInput(string? LocationCode, string? LocationName, string? LocationType, Guid? ParentLocationId, string? Status);
public sealed record RoomInput(string? Code, string? Name, string? ResourceType, Guid? LocationId, short? Capacity, string[]? Capabilities, bool? Accessible, string? Status);
public sealed record MaintenanceInput(Guid? ResourceId, string? StartsUtc, string? EndsUtc, string? ReasonCode, string? Note);

/// <summary>
/// The property's places (CON-002). Rooms are what the board books; a room
/// that is out of service or inside a maintenance window refuses bookings
/// (scheduling reads both). Nothing is deleted: a room is retired, because
/// past appointments name it. A closure cannot overlap another closure of
/// the same room (the database's exclusion), and one that would strand
/// bookings is reported with the bookings it affects.
/// </summary>
public sealed class ResourceService(SpmsDbContext db, MasterData master)
{
    public static readonly string[] LocationTypes = ["Facility", "Floor", "Treatment", "Storage", "Laundry", "Retail", "Reception", "Locker", "Relaxation"];
    public static readonly string[] RoomTypes = ["TreatmentRoom", "WetRoom", "CoupleRoom", "Chair"];

    public Task<List<LocationRow>> LocationsAsync(CancellationToken ct) =>
        db.Set<LocationRow>().AsNoTracking().OrderBy(l => l.LocationName).ToListAsync(ct);

    public Task<Edit<LocationRow>> AddLocationAsync(LocationInput i, CancellationToken ct) =>
        master.CreateAsync(new LocationRow
        {
            LocationId = Uuid7.New(), PropertyId = db.Scope.RequireProperty(), LocationCode = i.LocationCode!.Trim(), LocationName = i.LocationName!.Trim(),
            LocationType = i.LocationType!, ParentLocationId = i.ParentLocationId,
        }, "resources.location.create", "location", l => l.LocationId, ct);

    public Task<Edit<LocationRow>> UpdateLocationAsync(Guid id, int version, LocationInput i, CancellationToken ct) =>
        master.ChangeAsync<LocationRow>(l => l.LocationId == id, version, "resources.location.update", "location", l => l.LocationId, l =>
        {
            if (i.ParentLocationId == id) return "A location cannot contain itself.";
            if (i.LocationName is not null) l.LocationName = i.LocationName.Trim();
            if (i.LocationType is not null) l.LocationType = i.LocationType;
            if (i.ParentLocationId is not null) l.ParentLocationId = i.ParentLocationId;
            if (i.Status is not null) l.Status = i.Status;
            return null;
        }, ct);

    public Task<List<ResourceRow>> RoomsAsync(bool includeRetired, CancellationToken ct) =>
        db.Set<ResourceRow>().AsNoTracking().Where(r => includeRetired || r.Status != ResourceStatuses.Retired).OrderBy(r => r.Code).ToListAsync(ct);

    public Task<Edit<ResourceRow>> AddRoomAsync(RoomInput i, CancellationToken ct) =>
        master.CreateAsync(new ResourceRow
        {
            ResourceId = Uuid7.New(), PropertyId = db.Scope.RequireProperty(), Code = i.Code!.Trim(), Name = i.Name!.Trim(), ResourceType = i.ResourceType!,
            LocationId = i.LocationId, Capacity = i.Capacity ?? 1, Capabilities = i.Capabilities ?? [], Accessible = i.Accessible ?? false,
        }, "resources.resource.create", "resource", r => r.ResourceId, ct);

    public Task<Edit<ResourceRow>> UpdateRoomAsync(Guid id, int version, RoomInput i, CancellationToken ct) =>
        master.ChangeAsync<ResourceRow>(r => r.ResourceId == id, version, "resources.resource.update", "resource", r => r.ResourceId, r =>
        {
            if (r.Status == ResourceStatuses.Retired) return "A retired room is not changed.";
            if (i.Name is not null) r.Name = i.Name.Trim();
            if (i.ResourceType is not null) r.ResourceType = i.ResourceType;
            if (i.LocationId is not null) r.LocationId = i.LocationId;
            if (i.Capacity is { } c) r.Capacity = c;
            if (i.Capabilities is not null) r.Capabilities = i.Capabilities;
            if (i.Accessible is { } a) r.Accessible = a;
            if (i.Status is not null) r.Status = i.Status;
            return null;
        }, ct);

    public Task<List<MaintenanceWindowRow>> ClosuresAsync(Guid? roomId, DateTimeOffset fromUtc, CancellationToken ct) =>
        db.Set<MaintenanceWindowRow>().AsNoTracking()
            .Where(m => (roomId == null || m.ResourceId == roomId) && m.EndsAt > fromUtc)
            .OrderBy(m => m.StartsAt).ToListAsync(ct);

    public Task<Edit<MaintenanceWindowRow>> CloseAsync(Guid roomId, DateTimeOffset starts, DateTimeOffset ends, string reason, string? note, CancellationToken ct) =>
        master.CreateAsync(new MaintenanceWindowRow
        {
            MaintenanceWindowId = Uuid7.New(), PropertyId = db.Scope.RequireProperty(), ResourceId = roomId, StartsAt = starts, EndsAt = ends,
            ReasonCode = reason, Note = note, Status = MaintenanceWindowStatuses.Planned,
        }, "resources.maintenance.create", "maintenance_window", m => m.MaintenanceWindowId, ct,
            async () => await db.Set<ResourceRow>().AnyAsync(r => r.ResourceId == roomId && r.Status != ResourceStatuses.Retired, ct)
                ? null : (string?)"No such room at this property.");

    public Task<Edit<MaintenanceWindowRow>> CancelClosureAsync(Guid id, int version, CancellationToken ct) =>
        master.ChangeAsync<MaintenanceWindowRow>(m => m.MaintenanceWindowId == id, version, "resources.maintenance.cancel", "maintenance_window",
            m => m.MaintenanceWindowId, m =>
            {
                if (m.Status is not (MaintenanceWindowStatuses.Planned or MaintenanceWindowStatuses.Active)) return $"A {m.Status} closure is already over.";
                m.Status = MaintenanceWindowStatuses.Cancelled;
                return null;
            }, ct);
}

public static class ResourceEndpoints
{
    private static object Location(LocationRow l) => new
    {
        locationId = l.LocationId, l.LocationCode, l.LocationName, l.LocationType, l.ParentLocationId, l.Status, rowVersion = l.Version, eTag = $"\"{l.Version}\"",
    };

    private static object Room(ResourceRow r) => new
    {
        roomId = r.ResourceId, r.Code, r.Name, r.ResourceType, r.LocationId, r.Capacity, r.Capabilities, r.Accessible, r.Status,
        rowVersion = r.Version, eTag = $"\"{r.Version}\"",
    };

    private static object Closure(MaintenanceWindowRow m) => new
    {
        maintenanceWindowId = m.MaintenanceWindowId, roomId = m.ResourceId, startsUtc = m.StartsAt.ToUniversalTime().ToString("O"),
        endsUtc = m.EndsAt.ToUniversalTime().ToString("O"), m.ReasonCode, m.Note, m.Status, rowVersion = m.Version, eTag = $"\"{m.Version}\"",
    };

    private static Task<IResult?> Manage(RequestContext ctx, IAccessDecider access, CancellationToken ct) =>
        WebApi.GateAsync(ctx, access, SpaScopes.Write, "can_manage_offering", Fga.Property(ctx.PropertyId), ct);

    public static IEndpointRouteBuilder MapResources(this IEndpointRouteBuilder app)
    {
        app.MapGet("/locations", async (HttpContext http, ResourceService svc, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await svc.LocationsAsync(ct)).Select(Location), Json.Options);
        });

        app.MapPost("/locations", async (HttpContext http, ResourceService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Manage(ctx, access, ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<LocationInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.LocationCode) || string.IsNullOrWhiteSpace(i.LocationName) || !ResourceService.LocationTypes.Contains(i.LocationType))
                return WebApi.Invalid(ctx, $"locationCode, locationName and locationType ({string.Join(", ", ResourceService.LocationTypes)}) are required.");
            return (await svc.AddLocationAsync(i, ct)).ToHttp(http, ctx, Location, 201);
        });

        app.MapPatch("/locations/{id:guid}", async (HttpContext http, ResourceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Manage(ctx, access, ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<LocationInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.LocationType is not null && !ResourceService.LocationTypes.Contains(i.LocationType)) return WebApi.Invalid(ctx, "Unknown locationType.");
            if (i.Status is not (null or "Active" or "Retired")) return WebApi.Invalid(ctx, "status is Active or Retired.");
            return (await svc.UpdateLocationAsync(id, version, i, ct)).ToHttp(http, ctx, Location);
        });

        app.MapGet("/rooms", async (HttpContext http, ResourceService svc, bool? includeRetired, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await svc.RoomsAsync(includeRetired == true, ct)).Select(Room), Json.Options);
        });

        app.MapPost("/rooms", async (HttpContext http, ResourceService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Manage(ctx, access, ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<RoomInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.Code) || string.IsNullOrWhiteSpace(i.Name) || !ResourceService.RoomTypes.Contains(i.ResourceType))
                return WebApi.Invalid(ctx, $"code, name and resourceType ({string.Join(", ", ResourceService.RoomTypes)}) are required.");
            if (i.Capacity is < 1 or > 20) return WebApi.Invalid(ctx, "capacity is 1–20.");
            return (await svc.AddRoomAsync(i, ct)).ToHttp(http, ctx, Room, 201);
        });

        app.MapPatch("/rooms/{id:guid}", async (HttpContext http, ResourceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Manage(ctx, access, ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<RoomInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.ResourceType is not null && !ResourceService.RoomTypes.Contains(i.ResourceType)) return WebApi.Invalid(ctx, "Unknown resourceType.");
            if (i.Status is not (null or "Active" or "OutOfService" or "Retired")) return WebApi.Invalid(ctx, "status is Active, OutOfService or Retired.");
            if (i.Capacity is < 1 or > 20) return WebApi.Invalid(ctx, "capacity is 1–20.");
            return (await svc.UpdateRoomAsync(id, version, i, ct)).ToHttp(http, ctx, Room);
        });

        app.MapGet("/rooms/closures", async (HttpContext http, ResourceService svc, IClock clock, Guid? roomId, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await svc.ClosuresAsync(roomId, clock.UtcNow.AddDays(-1), ct)).Select(Closure), Json.Options);
        });

        app.MapPost("/rooms/closures", async (HttpContext http, ResourceService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Manage(ctx, access, ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<MaintenanceInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.ResourceId is not { } room || string.IsNullOrWhiteSpace(i.ReasonCode)) return WebApi.Invalid(ctx, "resourceId and reasonCode are required.");
            if (!Guard.TryParseInstant(i.StartsUtc, out var starts) || !Guard.TryParseInstant(i.EndsUtc, out var ends) || ends <= starts)
                return WebApi.Invalid(ctx, "startsUtc and endsUtc are ISO 8601 instants, end after start.");
            return (await svc.CloseAsync(room, starts, ends, i.ReasonCode!.Trim(), i.Note, ct)).ToHttp(http, ctx, Closure, 201);
        });

        app.MapPost("/rooms/closures/{id:guid}/cancel", async (HttpContext http, ResourceService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Manage(ctx, access, ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            return (await svc.CancelClosureAsync(id, version, ct)).ToHttp(http, ctx, Closure);
        });

        return app;
    }
}
